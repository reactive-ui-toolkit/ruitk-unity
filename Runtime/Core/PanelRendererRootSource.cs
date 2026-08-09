#if UNITY_6000_5_OR_NEWER
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Core
{
    /// <summary>
    /// The Unity 6.5 <see cref="PanelRenderer"/>-backed root source.
    ///
    /// <para><b>Push model.</b> <c>PanelRenderer.rootVisualElement</c> is
    /// internal - the UI reload callback is the only way to the root. The
    /// callback fires immediately at registration when the panel is already
    /// live, and again on every panel rebuild.</para>
    ///
    /// <para><b>Sub-root.</b> The fiber tree never mounts on Unity's root:
    /// Unity front-inserts nested-renderer roots into it and rewrites its
    /// layout/transform styles every frame. One child (<c>__ruitk_root</c>)
    /// is created per mount generation and IS this source's
    /// <see cref="CurrentRoot"/>; host-level <c>V.Host</c> props land on it,
    /// and full-screen behaviour is its <c>flexGrow</c>, not Unity's root
    /// class list.</para>
    ///
    /// <para><b>Three-way callback dispatch</b> (plan §5.2), keyed on the
    /// sub-root's own state - never on the callback's version counter, which
    /// resets on domain reload:</para>
    /// <list type="bullet">
    /// <item><b>Reuse in place</b> - the sub-root is still parented to the
    /// delivered root (disable/enable, double-fired callbacks). Nothing to
    /// do; this branch is also the idempotence guard.</item>
    /// <item><b>Retarget</b> - the sub-root is orphaned but not released.
    /// Re-adding it to the new root carries the whole live tree across;
    /// the fiber tree never notices because its container is the sub-root
    /// itself.</item>
    /// <item><b>Remount</b> - the sub-root is released
    /// (<c>resourcesReleased</c>). The old tree is dropped WITHOUT touching
    /// it, retention sites are swept, and a fresh sub-root triggers the
    /// mount's deferred-replay path.</item>
    /// </list>
    /// </summary>
    internal sealed class PanelRendererRootSource : IRootSource
    {
        private const string SubRootName = "__ruitk_root";

        // WA1/WA2 escalation ladder timing: each rung waits this long for the
        // symptom (no callback) to clear before trying the next lever.
        private const float WatchdogRungSeconds = 1.0f;

        // WA4: how long a released sub-root may wait for Unity's own follow-up
        // callback (which the three-way remount would handle) before the
        // nested-renderer repair concludes the callback is never coming.
        // 3s, not 1s: a .uxml save goes through asset reimport before the
        // callback fires, and an editor sweep showed that path can exceed a
        // second - repairing early is worse than waiting, since the normal
        // remount preserves the renderer component.
        private const float RepairAfterSeconds = 3.0f;

        private PanelRenderer renderer;
        private VisualElement subRoot;
        private Action onRootChanged;
        private Action tickUnsubscribe;

        private bool callbackEverArrived;
        private double watchStartTime;
        private int watchdogRung;
        private double lastRungTime;
        private double releaseObservedTime;
        private bool repairAttempted;
        private bool warnedVisualTreeAsset;
        private bool warnedNested;

        public PanelRendererRootSource(PanelRenderer renderer)
        {
            this.renderer = renderer;
        }

        public object CurrentRoot => subRoot;

        public void Start(Action rootChanged)
        {
            Stop();
            if (renderer == null)
            {
                return;
            }
            onRootChanged = rootChanged;
            WarnOnceAboutRiskyConfigurations();
            watchStartTime = Now();
            watchdogRung = 0;
            lastRungTime = watchStartTime;
            // Fires immediately iff the panel is already live, so a mount onto
            // a running renderer gets its sub-root synchronously.
            renderer.RegisterUIReloadCallback(OnUIReload);
            tickUnsubscribe = Ruitk.Core.Animation.AnimationTicker.Subscribe(Tick);
        }

        public void Stop()
        {
            if (renderer != null)
            {
                renderer.UnregisterUIReloadCallback(OnUIReload);
            }
            tickUnsubscribe?.Invoke();
            tickUnsubscribe = null;
            onRootChanged = null;
        }

        private void OnUIReload(PanelRenderer sender, VisualElement newRoot)
        {
            callbackEverArrived = true;
            releaseObservedTime = 0;
            if (newRoot == null)
            {
                return;
            }

            if (subRoot != null && subRoot.parent == newRoot)
            {
                // REUSE IN PLACE. Measured: disable/enable leaves the tree
                // parented and intact; a blind rebuild here visibly stacks
                // duplicate trees. Also the double-fire guard.
                return;
            }

            if (subRoot != null && !subRoot.resourcesReleased)
            {
                // RETARGET. The sub-root is orphaned but alive; moving it
                // carries the entire mounted tree - fiber, hooks, refs,
                // animations - and the fiber tree's container (the sub-root)
                // never changes, so no reconciler involvement is needed.
                newRoot.Add(subRoot);
                return;
            }

            // REMOUNT. Either first mount (no sub-root yet) or the old
            // sub-root was released - it is poison and is never touched
            // again. Sweep the style-tracking retention site (per-element
            // OnHostRemoved never ran for a wholesale release), then hand the
            // mount a fresh sub-root; the deferred-mount path replays the UI.
            if (subRoot != null)
            {
                Ruitk.Props.PropsApplier.EvictReleasedElements();
            }
            subRoot = new VisualElement { name = SubRootName };
            subRoot.style.flexGrow = 1f;
            // The sub-root is structural, and Unity layers whole panels over
            // each other (a PanelRenderer root above a UIDocument root, both
            // full-surface): with default picking this element would swallow
            // every click aimed at UI beneath it. Content inside still picks;
            // V.Host props can override.
            subRoot.pickingMode = PickingMode.Ignore;
            newRoot.Add(subRoot);
            // A fresh, healthy mount generation: the one-shot repair guard
            // re-arms for the next release, if any.
            repairAttempted = false;
            onRootChanged?.Invoke();
        }

        private static double Now()
        {
            try
            {
                return Time.realtimeSinceStartupAsDouble;
            }
            catch
            {
                return Time.realtimeSinceStartup;
            }
        }

        private void Tick()
        {
            if (renderer == null)
            {
                Stop();
                return;
            }
            TickMountWatchdog();
            TickNestedRepair();
        }

        // WORKAROUND(WA1+WA2, case IN-150082 + UUM-147875): panelSettings round-trip
        // (escalating to PerformUpdate / PerformValidation / component toggle) when
        // an enabled, configured PanelRenderer never delivers its UI reload callback.
        // AFFECTS:    Unity 6000.5.x editor (IN-150082, nested mount) and
        //             6000.5.0-6000.5.6 (UUM-147875, disabled-in-Awake; fix ships in 6000.5.7f1).
        // GATED BY:   the symptom - no callback N seconds after Start with panelSettings set.
        //             On a fixed editor the callback arrives and this never runs.
        //             Opt-out: config.json "mount_watchdog".
        // REMOVE WHEN: both are fixed upstream AND package.json's "unity" floor is past the fixes.
        // EVIDENCE:   Plans~/archive/UNITY_6_5_SUPPORT_PLAN.md 5.7.1 (verified end-to-end on 6000.5.6f1)
        //             and 5.9.2 (registry rows WA1/WA2 - one mechanism, one flag).
        // NOTE:       the panelSettings self-assignment is the MECHANISM (one of the four
        //             attach/release paths), not dead code. Success is judged by OUTCOME
        //             (callbackEverArrived flips), never by whether a call threw.
        private void TickMountWatchdog()
        {
            if (callbackEverArrived || !BuildDefinesConfig.ResolveMountWatchdog())
            {
                return;
            }
            if (
                !renderer.enabled
                || !renderer.gameObject.activeInHierarchy
                || renderer.panelSettings == null
            )
            {
                // Not a symptom: an inactive or unconfigured renderer is
                // legitimately callback-less. Restart the clock.
                watchStartTime = Now();
                watchdogRung = 0;
                lastRungTime = watchStartTime;
                return;
            }
            double now = Now();
            if (now - lastRungTime < WatchdogRungSeconds || watchdogRung >= 4)
            {
                return;
            }
            watchdogRung++;
            lastRungTime = now;
            var nested = DisableNestedChildRenderers();
            try
            {
                switch (watchdogRung)
                {
                    case 1:
                        var ps = renderer.panelSettings;
                        renderer.panelSettings = null;
                        renderer.panelSettings = ps;
                        break;
                    case 2:
                        ((IPanelComponent)renderer).PerformUpdate();
                        break;
                    case 3:
                        ((IPanelComponent)renderer).PerformValidation(true);
                        break;
                    case 4:
                        renderer.enabled = false;
                        renderer.enabled = true;
                        break;
                }
            }
            finally
            {
                ReEnableNestedChildRenderers(nested);
            }
        }

        // WORKAROUND(WA3, UUM-148452): disable nested child PanelRenderers around
        // rebuilds this library itself triggers, so the parent's release cascade
        // cannot poison them (the measured N2 prevention - the only lever that
        // destroys nothing).
        // AFFECTS:    all Unity 6000.5.x, editor AND player. Open upstream as of 2026-08-01.
        // GATED BY:   none - prevention acts before the damage, so there is no symptom
        //             to observe; it costs one frame of the child being disabled and only
        //             runs around our own rebuild levers. Opt-out: config.json "nested_prevention".
        // REMOVE WHEN: UUM-148452 is fixed AND package.json's "unity" floor is past the fix.
        // EVIDENCE:   Plans~/archive/UNITY_6_5_SUPPORT_PLAN.md 5.8.7 (recovery ladder N1-N6) and
        //             5.8.8 T3 (prevention holds in a built player).
        private List<PanelRenderer> DisableNestedChildRenderers()
        {
            if (!BuildDefinesConfig.ResolveNestedPrevention())
            {
                return null;
            }
            List<PanelRenderer> disabled = null;
            var children = renderer.GetComponentsInChildren<PanelRenderer>(includeInactive: false);
            foreach (var child in children)
            {
                if (child == renderer || child.parentUI != renderer || !child.enabled)
                {
                    continue;
                }
                child.enabled = false;
                (disabled ??= new List<PanelRenderer>()).Add(child);
            }
            return disabled;
        }

        private static void ReEnableNestedChildRenderers(List<PanelRenderer> disabled)
        {
            if (disabled == null)
            {
                return;
            }
            foreach (var child in disabled)
            {
                if (child != null)
                {
                    child.enabled = true;
                }
            }
        }

        // WORKAROUND(WA4, UUM-148452): destroy + re-add THIS nested renderer when a
        // parent rebuild released its tree and no follow-up callback arrives. Measured
        // (N6): the stuck state lives in the child's renderer component and must be
        // REMOVED, not supplemented - re-adding a fresh component is the minimal repair.
        // AFFECTS:    all Unity 6000.5.x, editor AND player. Open upstream as of 2026-08-01.
        // GATED BY:   subRoot.resourcesReleased persisting with no callback for
        //             RepairAfterSeconds - inert when the bug is absent (a callback after
        //             release is the normal three-way remount instead). Runs only for
        //             NESTED renderers (parentUI != null); a top-level release always
        //             gets its callback. Opt-out: config.json "nested_repair". One attempt
        //             per mount generation; judged by outcome (the fresh component's
        //             callback re-enters the normal mount path).
        // REMOVE WHEN: UUM-148452 is fixed AND package.json's "unity" floor is past the fix.
        // EVIDENCE:   Plans~/archive/UNITY_6_5_SUPPORT_PLAN.md 5.8.7 (N6 measured, decisions D1-D5)
        //             and 5.8.8 T5 (repair works in a built player).
        // NOTE:       every copyable setting is carried over (visualTreeAsset, sortingOrder,
        //             position, pivot, pivotReferenceSize, worldSpaceSizeMode, worldSpaceSize;
        //             panelSettings is parent-owned on a nested child). In edit mode the
        //             copy is a full serialized copy and the destroy/add go through Undo,
        //             so the repair is one Ctrl+Z, not a silent scene rewrite. Serialized
        //             references TO the old component cannot survive - Unity has no
        //             replace-in-place API; that residual case is what the opt-out is for.
        private void TickNestedRepair()
        {
            if (
                repairAttempted
                || subRoot == null
                || !subRoot.resourcesReleased
                || renderer.parentUI == null
                || !BuildDefinesConfig.ResolveNestedRepair()
            )
            {
                return;
            }
            double now = Now();
            if (releaseObservedTime == 0)
            {
                releaseObservedTime = now;
                return;
            }
            if (now - releaseObservedTime < RepairAfterSeconds)
            {
                return;
            }
            repairAttempted = true;

            var go = renderer.gameObject;
            var old = renderer;
            renderer.UnregisterUIReloadCallback(OnUIReload);

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditorInternal.ComponentUtility.CopyComponent(old);
                try
                {
                    UnityEditor.Undo.DestroyObjectImmediate(old);
                }
                catch (InvalidOperationException)
                {
                    // Unity's own OnPanelRendererCleanup throws when the tree
                    // is already released (measured, plan f18/f23) - the very
                    // condition this repair exists for. Success is judged by
                    // the outcome below, never by whether this call threw.
                }
                if (old != null)
                {
                    // The destroy did not complete - the component survived.
                    // Adding a second renderer would nest two; keep observing
                    // the original instead.
                    AdoptRepairedRenderer(old);
                    return;
                }
                var freshEditor = UnityEditor.Undo.AddComponent<PanelRenderer>(go);
                UnityEditorInternal.ComponentUtility.PasteComponentValues(freshEditor);
                AdoptRepairedRenderer(freshEditor);
                return;
            }
#endif
            var visualTreeAsset = old.visualTreeAsset;
            var sortingOrder = old.sortingOrder;
            var position = old.position;
            var pivot = old.pivot;
            var pivotReferenceSize = old.pivotReferenceSize;
            var worldSpaceSizeMode = old.worldSpaceSizeMode;
            var worldSpaceSize = old.worldSpaceSize;

            try
            {
                UnityEngine.Object.DestroyImmediate(old);
            }
            catch (InvalidOperationException)
            {
                // Same as the edit-mode branch: Unity's cleanup throws on a
                // released tree by the nature of the bug being repaired; the
                // settings copy below must still run (aborting here is what
                // used to leave the repaired child unconfigured).
            }
            if (old != null)
            {
                AdoptRepairedRenderer(old);
                return;
            }
            var fresh = go.AddComponent<PanelRenderer>();
            fresh.visualTreeAsset = visualTreeAsset;
            fresh.sortingOrder = sortingOrder;
            fresh.position = position;
            fresh.pivot = pivot;
            fresh.pivotReferenceSize = pivotReferenceSize;
            fresh.worldSpaceSizeMode = worldSpaceSizeMode;
            fresh.worldSpaceSize = worldSpaceSize;
            AdoptRepairedRenderer(fresh);
        }

        private void AdoptRepairedRenderer(PanelRenderer fresh)
        {
            renderer = fresh;
            callbackEverArrived = false;
            releaseObservedTime = 0;
            watchStartTime = Now();
            watchdogRung = 0;
            lastRungTime = watchStartTime;
            // The fresh component's callback re-enters OnUIReload; the released
            // sub-root fails the reuse and retarget probes, so the normal
            // three-way dispatch remounts. repairAttempted resets only when a
            // healthy mount generation is established.
            fresh.RegisterUIReloadCallback(OnUIReload);
        }

        private void WarnOnceAboutRiskyConfigurations()
        {
            if (!warnedVisualTreeAsset && renderer.visualTreeAsset != null)
            {
                warnedVisualTreeAsset = true;
                Debug.LogWarning(
                    "[Ruitk] This PanelRenderer has a Source Asset (visualTreeAsset). Saving "
                        + "that .uxml releases and rebuilds the panel: the mounted UI remounts "
                        + "and transient state (hooks, scroll positions, focus) is dropped. "
                        + "For a fully code-driven UI leave the Source Asset empty; the "
                        + "frequent editor triggers then never release the tree.",
                    renderer
                );
            }
            if (!warnedNested && renderer.parentUI != null)
            {
                warnedNested = true;
                Debug.LogWarning(
                    "[Ruitk] This PanelRenderer is NESTED under another PanelRenderer "
                        + "(parentUI is set). Unity 6000.5.x has open issues with nested "
                        + "renderers (case IN-150082: child may never mount in the editor; "
                        + "UUM-148452: a parent rebuild releases the child's tree). The "
                        + "library ships symptom-gated workarounds (mount_watchdog, "
                        + "nested_prevention, nested_repair in config.json); see the "
                        + "Unity 6.5 known-issues docs page.",
                    renderer
                );
            }
        }
    }
}
#endif
