using System;
using UnityEngine.UIElements;

namespace Ruitk.Core
{
    /// <summary>
    /// The root-source seam (6.5 plan A3): where a mount's root element comes
    /// from and how its replacement is observed. Implementations are internal
    /// by design - hosts surface through the public
    /// <see cref="RootRenderer.Initialize(VisualElement, Action{HostContext})"/>
    /// overloads, one per host kind.
    /// </summary>
    internal interface IRootSource
    {
        /// <summary>
        /// The host's current root element, or null while the host has not
        /// built its panel (yet, or mid-rebuild).
        /// </summary>
        object CurrentRoot { get; }

        /// <summary>
        /// Begin observing the host. <paramref name="rootChanged"/> fires when
        /// <see cref="CurrentRoot"/> no longer matches what the mount last saw.
        /// </summary>
        void Start(Action rootChanged);

        void Stop();
    }

    /// <summary>A fixed element supplied by the caller; never changes.</summary>
    internal sealed class StaticRootSource : IRootSource
    {
        private readonly VisualElement root;

        public StaticRootSource(VisualElement root)
        {
            this.root = root;
        }

        public object CurrentRoot => root;

        public void Start(Action rootChanged) { }

        public void Stop() { }
    }

    /// <summary>
    /// A <see cref="UIDocument"/>-backed root. In the editor Unity silently
    /// replaces <c>rootVisualElement</c> on undo, asset swap, disable/enable,
    /// HMR, and the 6.3 InspectorWindow selection storm (UUM-127851) - hookless
    /// mutations with no callback to observe - so the source polls once per
    /// frame on <see cref="Ruitk.Core.Animation.AnimationTicker"/> (one
    /// ReferenceEquals per frame). Built players have no hookless swaps, so
    /// the poll is compiled out and the source is effectively static.
    /// </summary>
    internal sealed class UIDocumentRootSource : IRootSource
    {
        private readonly UIDocument document;
#if UNITY_EDITOR
        private Action tickUnsubscribe;
        private VisualElement lastSeenRoot;
        private Action onRootChanged;
#endif

        public UIDocumentRootSource(UIDocument document)
        {
            this.document = document;
        }

        public object CurrentRoot => document != null ? document.rootVisualElement : null;

        public void Start(Action rootChanged)
        {
#if UNITY_EDITOR
            Stop();
            if (document == null)
            {
                return;
            }
            onRootChanged = rootChanged;
            lastSeenRoot = document.rootVisualElement;
            tickUnsubscribe = Ruitk.Core.Animation.AnimationTicker.Subscribe(Poll);
#endif
        }

        public void Stop()
        {
#if UNITY_EDITOR
            tickUnsubscribe?.Invoke();
            tickUnsubscribe = null;
            onRootChanged = null;
#endif
        }

#if UNITY_EDITOR
        private void Poll()
        {
            if (document == null)
            {
                Stop();
                return;
            }
            var next = document.rootVisualElement;
            if (ReferenceEquals(next, lastSeenRoot))
            {
                return;
            }
            lastSeenRoot = next;
            onRootChanged?.Invoke();
        }
#endif
    }
}
