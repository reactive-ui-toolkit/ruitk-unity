namespace Ruitk.Core
{
    // The UI Toolkit-flavoured half of Hooks: members that name Unity asset or
    // component types (Animator tracks over VisualElement styles, UIDocument,
    // AudioClip/AudioMixerGroup via MediaHost, AnimationTicker whose player-build
    // pump rides MediaHost). The engine half in Hooks.cs stays host-agnostic so
    // the CI test suite can link it against the UnityEngine shim; this file is
    // compiled only where the real UnityEngine assemblies exist.
    public static partial class Hooks
    {
        public static void UseAnimate(
            System.Collections.Generic.IReadOnlyList<Ruitk.Core.Animation.AnimateTrack> tracks,
            bool autoplay = true,
            params object[] dependencies
        )
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return;
            }
            RecordHook(metadata, state, HookIdAnimate);

            state.HookStates ??= new System.Collections.Generic.List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(null);
            }
            int index = state.HookIndex;
            state.HookIndex++;
            SyncState(metadata, state);

            UseEffect(
                () =>
                {
                    var prev =
                        state.HookStates[index]
                        as System.Collections.Generic.List<Ruitk.Core.Animation.AnimationHandle>;
                    if (prev != null)
                    {
                        foreach (var h in prev)
                        {
                            try
                            {
                                h?.Stop();
                            }
                            catch { }
                        }
                    }

                    var target = ResolveAnimationTarget(metadata, state);
                    System.Collections.Generic.List<Ruitk.Core.Animation.AnimationHandle> handles =
                        null;
                    if (autoplay && tracks != null && tracks.Count > 0 && target != null)
                    {
                        handles = Ruitk.Core.Animation.Animator.PlayTracks(target, tracks);
                    }
                    state.HookStates[index] = handles;
                    SyncState(metadata, state);
                    return () =>
                    {
                        var hs =
                            state.HookStates[index]
                            as System.Collections.Generic.List<Ruitk.Core.Animation.AnimationHandle>;
                        if (hs != null)
                        {
                            foreach (var h in hs)
                            {
                                try
                                {
                                    h?.Stop();
                                }
                                catch { }
                            }
                            state.HookStates[index] = null;
                            SyncState(metadata, state);
                        }
                    };
                },
                dependencies
            );
        }

        // ═══════════════════════════════════════════════════════════════════
        //  UseUiDocumentRoot — reactive UIDocument.rootVisualElement tracking
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the current <c>rootVisualElement</c> of the supplied
        /// <see cref="UnityEngine.UIElements.UIDocument"/>, or null if the
        /// document is null or has not yet built its panel. The hook
        /// re-renders the calling component whenever the
        /// <c>rootVisualElement</c> reference changes — Unity rebuilds the
        /// panel (and therefore replaces the root) on undo, asset swap,
        /// disable/enable, and the editor playmode selection storm. This
        /// hook gives consumer components a stable handle that follows the
        /// rebuilds without requiring any manual subscription.
        ///
        /// Designed for portal targeting: pair with a non-null guard at the
        /// call site, e.g.
        /// <c>target != null ? &lt;Portal target={target}&gt;...&lt;/Portal&gt; : null</c>,
        /// so the portal is unrendered while the panel is between rebuilds.
        ///
        /// In the editor, detection uses a per-frame ReferenceEquals poll on
        /// the panel-independent <see cref="Ruitk.Core.Animation.AnimationTicker"/>
        /// because <c>UIDocument</c> exposes no public event for panel
        /// rebuilds. Those rebuilds are editor-only hookless mutations, so in
        /// player builds the poll is compiled out: the hook returns the root
        /// captured at mount (plus one effect-time resync) and does not track
        /// later runtime rebuilds. A build that intentionally rebuilds a
        /// target panel at runtime should drive that change through its own
        /// reactive state rather than relying on this hook.
        /// </summary>
        public static UnityEngine.UIElements.VisualElement UseUiDocumentRoot(
            UnityEngine.UIElements.UIDocument doc
        )
        {
            var (current, setCurrent) = UseState<UnityEngine.UIElements.VisualElement>(
                doc != null ? doc.rootVisualElement : null
            );

            UseEffect(
                () =>
                {
                    if (doc == null)
                    {
                        return null;
                    }
                    // Sync once on effect-run in case the rootVisualElement
                    // changed between hook-call (UseState init) and the
                    // commit phase that runs effects.
                    var initial = doc.rootVisualElement;
                    if (!ReferenceEquals(initial, current))
                    {
                        setCurrent(initial);
                    }

#if UNITY_EDITOR
                    // Editor-only: Unity silently replaces rootVisualElement on
                    // undo, asset swap, disable/enable, HMR, and the 6.3
                    // InspectorWindow selection storm — hookless mutations with
                    // no callback. Poll once per frame to follow them. Built
                    // players have no hookless swaps, so this is compiled out
                    // and the root synced above is returned as a stable value.
                    System.Action unsubscribe = null;
                    unsubscribe = Ruitk.Core.Animation.AnimationTicker.Subscribe(() =>
                    {
                        if (doc == null)
                        {
                            unsubscribe?.Invoke();
                            unsubscribe = null;
                            return;
                        }
                        var next = doc.rootVisualElement;
                        System.Func<
                            UnityEngine.UIElements.VisualElement,
                            UnityEngine.UIElements.VisualElement
                        > updater = prev => ReferenceEquals(prev, next) ? prev : next;
                        setCurrent(updater);
                    });

                    return () =>
                    {
                        unsubscribe?.Invoke();
                        unsubscribe = null;
                    };
#else
                    return null;
#endif
                },
                doc
            );

            return current;
        }

        /// <summary>
        /// Resolves a <see cref="UnityEngine.UIElements.UIDocument"/> via
        /// <see cref="UseContext{T}"/> using <paramref name="contextKey"/>
        /// and forwards to <see cref="UseUiDocumentRoot(UnityEngine.UIElements.UIDocument)"/>.
        /// Returns null if the key is empty or the context value is not a
        /// UIDocument.
        /// </summary>
        public static UnityEngine.UIElements.VisualElement UseUiDocumentRoot(string contextKey)
        {
            if (string.IsNullOrEmpty(contextKey))
            {
                return null;
            }
            var doc = UseContext<UnityEngine.UIElements.UIDocument>(contextKey);
            return UseUiDocumentRoot(doc);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  UseSfx — fire-and-forget one-shot audio
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns a stable <see cref="System.Action{AudioClip, float}"/> that
        /// plays a one-shot <see cref="UnityEngine.AudioClip"/> on the shared
        /// <c>MediaHost.Instance.SfxSource</c>. Call the returned delegate
        /// from event handlers (button clicks, hover, etc.) — it never
        /// allocates and returns the same delegate reference across renders
        /// for stable identity in <c>UseEffect</c> dependency lists.
        ///
        /// <para>
        /// The <paramref name="mixer"/> argument is captured at hook-call time
        /// and re-applied each invocation. Changing it between renders
        /// rebuilds the cached delegate.
        /// </para>
        ///
        /// <para>
        /// Hook-order signature: this hook reports as <c>UseSfx</c> in the
        /// generated <c>HookSignatureAttribute</c>; it MUST appear in the
        /// emitter regex whitelists at <c>CSharpEmitter.cs</c> and
        /// <c>HmrCSharpEmitter.cs</c> for HMR rude-edit detection to work.
        /// </para>
        /// </summary>
        public static System.Action<UnityEngine.AudioClip, float> UseSfx(
            UnityEngine.Audio.AudioMixerGroup mixer = null
        )
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return static (_, __) => { };
            }
            RecordHook(metadata, state, HookIdSfx);

            state.HookStates ??= new System.Collections.Generic.List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(null);
            }

            var entry =
                state.HookStates[state.HookIndex]
                as System.Tuple<
                    UnityEngine.Audio.AudioMixerGroup,
                    System.Action<UnityEngine.AudioClip, float>
                >;
            if (entry == null || !ReferenceEquals(entry.Item1, mixer))
            {
                var capturedMixer = mixer;
                System.Action<UnityEngine.AudioClip, float> action = (clip, volumeScale) =>
                {
                    if (clip == null)
                        return;
                    var src = Ruitk.Core.Media.MediaHost.Instance.SfxSource;
                    if (capturedMixer != null)
                        src.outputAudioMixerGroup = capturedMixer;
                    src.PlayOneShot(clip, UnityEngine.Mathf.Clamp01(volumeScale));
                };
                entry = System.Tuple.Create(capturedMixer, action);
                state.HookStates[state.HookIndex] = entry;
            }

            state.HookIndex++;
            SyncState(metadata, state);
            return entry.Item2;
        }

        public static void UseTweenFloat(
            float from,
            float to,
            float duration,
            Ruitk.Core.Animation.Ease ease,
            float delay,
            System.Action<float> onUpdate,
            System.Action onComplete,
            params object[] dependencies
        )
        {
            NodeMetadata metadata = HookContext.Current?.Owner;
            var state = EnsureState(metadata);
            if (state == null)
            {
                return;
            }
            RecordHook(metadata, state, HookIdTween);
            state.HookStates ??= new System.Collections.Generic.List<object>();
            if (state.HookIndex >= state.HookStates.Count)
            {
                state.HookStates.Add(null);
            }
            int index = state.HookIndex;
            state.HookIndex++;
            SyncState(metadata, state);

            UseEffect(
                () =>
                {
                    System.Action unsubscribe = null;
                    double start = 0;
                    bool started = false;
                    bool completed = false;
                    var target = ResolveAnimationTarget(metadata, state);
                    if (target == null)
                    {
                        return null;
                    }
                    unsubscribe = Ruitk.Core.Animation.AnimationTicker.Subscribe(() =>
                    {
                        if (completed)
                        {
                            return;
                        }
                        double now;
                        try
                        {
                            now = UnityEngine.Time.realtimeSinceStartupAsDouble;
                        }
                        catch
                        {
                            now = (double)UnityEngine.Time.realtimeSinceStartup;
                        }
                        if (!started)
                        {
                            start = now + delay;
                            started = true;
                        }
                        if (now < start)
                        {
                            return;
                        }
                        float t =
                            duration <= 0f
                                ? 1f
                                : UnityEngine.Mathf.Clamp01((float)((now - start) / duration));
                        float eased = Ruitk.Core.Animation.Easing.Evaluate(ease, t);
                        float v = UnityEngine.Mathf.Lerp(from, to, eased);
                        // onUpdate is the consumer's writeback; the consumer is
                        // responsible for any panel-presence gating it needs.
                        try
                        {
                            onUpdate?.Invoke(v);
                        }
                        catch { }
                        if (t >= 1f)
                        {
                            completed = true;
                            try
                            {
                                onComplete?.Invoke();
                            }
                            catch { }
                            try
                            {
                                unsubscribe?.Invoke();
                            }
                            catch { }
                            unsubscribe = null;
                        }
                    });

                    return () =>
                    {
                        try
                        {
                            unsubscribe?.Invoke();
                        }
                        catch { }
                        unsubscribe = null;
                    };
                },
                dependencies
            );
        }

    }
}
