using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ruitk.Diagnostics
{
    /// <summary>
    /// Why a component rendered, as flags. A render can have more than one reason -- a state
    /// update that arrives in the same pass as changed props sets both.
    /// </summary>
    [Flags]
    public enum RenderReason
    {
        /// <summary>The bailout fired: nothing changed and the component was not re-run.</summary>
        None = 0,

        /// <summary>No previous typed props, so this fiber had never rendered.</summary>
        FirstMount = 1 << 0,

        /// <summary>A hook state update was queued on this component.</summary>
        StateUpdate = 1 << 1,

        /// <summary>The props object compared unequal to the one from the last render.</summary>
        PropsChanged = 1 << 2,

        /// <summary>A context this component reads was written by an ancestor.</summary>
        ContextChanged = 1 << 3,

        /// <summary>The parent passed a different children list.</summary>
        ChildrenChanged = 1 << 4,
    }

    /// <summary>
    /// Per-component render diagnostics: which component rendered, and why.
    ///
    /// <para>The reconciler already computes every input for this -- the bailout in
    /// <c>FiberFunctionComponent.RenderFunctionComponent</c> decides on exactly these four
    /// facts -- and used to discard them. <c>FiberReconciler.MetricsEmitted</c> reports
    /// per-commit totals with no component names, which is the gap: it tells you that 40
    /// components rendered, never which one is rendering on every frame.</para>
    ///
    /// <para><b>Cost when nobody is listening:</b> the reconciler tests one static bool per
    /// function-component render and does nothing else. Component names are resolved inside
    /// <see cref="Report"/>, so the reflection that resolves them never runs while the
    /// feature is off, and the result is cached per component function rather than per
    /// fiber -- one entry for a component that renders ten thousand times.</para>
    ///
    /// <para>Both sinks are live at once: <see cref="Enabled"/> writes to the Unity console,
    /// <see cref="Rendered"/> hands the same facts to a subscriber that wants to count,
    /// filter or draw them.</para>
    /// </summary>
    public static class WhyDidYouRender
    {
        /// <summary>Write every render reason to the Unity console.</summary>
        public static bool Enabled = false;

        /// <summary>
        /// Raised once per function-component render pass, with the component's name and why
        /// it rendered. <see cref="RenderReason.None"/> means the bailout fired and the
        /// component's body did NOT run -- which is the interesting case when you are checking
        /// that memoisation works.
        /// </summary>
        public static event Action<string, RenderReason> Rendered;

        /// <summary>
        /// Whether anything is listening. The reconciler reads this and skips the whole
        /// diagnostic when it is false, so an unsubscribed build pays one bool test.
        /// </summary>
        public static bool Active => Enabled || Rendered != null;

        /// <summary>
        /// Report one render pass. Safe to call only when <see cref="Active"/>; callers check
        /// it so that <paramref name="componentName"/> is never even resolved while off.
        /// </summary>
        public static void Report(string componentName, RenderReason reason)
        {
            if (Enabled)
            {
                Debug.Log(
                    reason == RenderReason.None
                        ? $"[WDYR] {componentName} bailed out - nothing changed."
                        : $"[WDYR] {componentName} rendered: {reason}."
                );
            }

            try
            {
                Rendered?.Invoke(componentName, reason);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        /// <summary>
        /// Compares two prop dictionaries and logs the first difference. Predates
        /// <see cref="Report"/> and is kept for callers that hold dictionary props; the
        /// reconciler's function-component path is typed and uses <see cref="Report"/>.
        /// </summary>
        public static void Log(
            string componentName,
            IReadOnlyDictionary<string, object> prev,
            IReadOnlyDictionary<string, object> next,
            bool forced
        )
        {
            if (!Enabled)
            {
                return;
            }
            if (forced)
            {
                Debug.Log($"[WDYR] {componentName} forced render");
                return;
            }
            if (ReferenceEquals(prev, next))
            {
                Debug.Log(
                    $"[WDYR] {componentName} rendered with identical props (reference equal)."
                );
                return;
            }
            if (prev == null || next == null)
            {
                Debug.Log($"[WDYR] {componentName} rendered; prev or next props were null.");
                return;
            }
            if (prev.Count != next.Count)
            {
                Debug.Log(
                    $"[WDYR] {componentName} rendered; prop count changed {prev.Count} -> {next.Count}."
                );
                return;
            }
            foreach (var kv in prev)
            {
                if (!next.TryGetValue(kv.Key, out var v) || !Equals(v, kv.Value))
                {
                    Debug.Log($"[WDYR] {componentName} prop changed: {kv.Key}");
                    return;
                }
            }
            Debug.Log($"[WDYR] {componentName} rendered but props shallow-equal. Consider memo.");
        }
    }
}
