using System;
using Ruitk.Elements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Core
{
    public sealed class RootRenderer : MonoBehaviour
    {
        /// <summary>
        /// The first-created renderer, kept for backward compatibility. A
        /// scene may host any number of RootRenderers - one per mount (e.g. a
        /// UIDocument screen plus a world-space PanelRenderer); each owns its
        /// own tree.
        /// </summary>
        public static RootRenderer Instance { get; private set; }
        private HostContext sharedHostContext;
        private ElementRegistry elementRegistry;
        private IRootSource rootSource;
        private VisualElement rootElement;
        private VNodeHostRenderer vnodeHostRenderer;

        // Deferred mount: a Render() that arrives before the host has built
        // its panel is held here and replayed when the root source produces a
        // root. Previously the vnode was silently discarded, which forced
        // callers to sequence their first Render after Unity's panel build.
        private VirtualNode pendingRootNode;

        // The last vnode actually rendered, for the 6.5 remount path: when the
        // host releases the mounted subtree wholesale, the fresh sub-root
        // replays this. Safe to retain - the reconciler itself keeps the root
        // vnode alive for state-driven re-renders (FiberRoot.RootVNode).
        private VirtualNode lastRootNode;

        private void EnsureSetup()
        {
            if (elementRegistry == null)
            {
                elementRegistry = ElementRegistryProvider.GetDefaultRegistry();
            }
            if (sharedHostContext == null)
            {
                if (RenderScheduler.Instance == null)
                {
                    var go = new GameObject("RenderScheduler");
                    go.hideFlags = HideFlags.DontSave;
                    go.AddComponent<RenderScheduler>();
                }
                sharedHostContext = RuitkBootstrap.CreateHostContext(
                    elementRegistry,
                    hostConfig: null,
                    scheduler: RenderScheduler.Instance,
                    isEditor: false
                );
            }
        }

        private void Awake()
        {
            // First one wins the legacy Instance slot; additional renderers
            // are independent mounts. (This used to destroy the second
            // instance's whole GameObject, which made every multi-mount scene
            // - mixed hosts, nested renderers - silently self-delete.)
            if (Instance == null)
            {
                Instance = this;
            }
            EnsureSetup();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            Unmount();
        }

        /// <summary>
        /// Configures the root VisualElement and optionally seeds named environment slots
        /// (portal targets, feature flags, etc.) into the shared <see cref="HostContext"/>.
        /// Must be called before the first <see cref="Render"/> call.
        /// </summary>
        /// <param name="uiRootElement">The VisualElement that acts as the React root.</param>
        /// <param name="env">
        /// Optional callback invoked with the <see cref="HostContext"/> after built-in keys
        /// (scheduler, env, etc.) are set.  Use this to seed named portal target slots:
        /// <code>
        /// rootRenderer.Initialize(uiDoc.rootVisualElement,
        ///     env: ctx => ctx.Environment[PortalContextKeys.ModalRoot] = overlayLayer);
        /// </code>
        /// </param>
        public void Initialize(VisualElement uiRootElement, Action<HostContext> env = null)
        {
            AdoptRootSource(new StaticRootSource(uiRootElement), env);
        }

        /// <summary>
        /// UIDocument-aware overload. In the <b>editor</b> this polls the
        /// document's <c>rootVisualElement</c> once per frame and reparents
        /// the mounted fiber tree onto the new root whenever Unity rebuilds
        /// the panel (undo, asset swap, disable/enable, HMR, and the 6.3
        /// <c>InspectorWindow</c> selection storm). Those are hookless,
        /// editor-only mutations with no callback to observe, so polling is
        /// the only correct detection mechanism; the cost is one reference
        /// compare per frame on a tick source already running.
        ///
        /// In <b>player builds</b> the poll is compiled out entirely: a
        /// running game has no hookless panel swaps (every runtime panel
        /// change is developer-initiated), so this overload simply seeds the
        /// initial root from <paramref name="hostDoc"/>, exactly like
        /// <see cref="Initialize(VisualElement, Action{HostContext})"/>. A
        /// build that deliberately rebuilds a UIDocument at runtime should
        /// re-call <see cref="Render"/> (or this overload) from the code that
        /// triggers the rebuild.
        /// </summary>
        public void Initialize(UIDocument hostDoc, Action<HostContext> env = null)
        {
            AdoptRootSource(new UIDocumentRootSource(hostDoc), env);
        }

#if UNITY_6000_5_OR_NEWER
        /// <summary>
        /// Unity 6.5 <see cref="UnityEngine.UIElements.PanelRenderer"/> host.
        /// The renderer's root is internal, so the mount is callback-driven: a
        /// <see cref="Render"/> call issued before the panel exists is held
        /// and replayed automatically when it does. The fiber tree mounts into
        /// a library-owned sub-root (<c>__ruitk_root</c>) rather than Unity's
        /// root, which Unity rewrites every frame; <c>V.Host</c> props land on
        /// the sub-root. World-space configuration (worldSpaceSizeMode,
        /// worldSpaceSize, position, pivot, pivotReferenceSize) stays on the
        /// <see cref="UnityEngine.UIElements.PanelRenderer"/> component
        /// itself - Unity owns those, and the mounted UI follows.
        ///
        /// <para>Panel rebuilds are handled per the release state of the
        /// mounted tree: reuse in place when nothing moved, retarget (state
        /// preserved) when the tree was orphaned but not released, and a
        /// fresh remount (state dropped) when Unity released the subtree -
        /// e.g. saving a Source Asset .uxml, or reassigning
        /// <c>panelSettings</c> at runtime. Known Unity 6000.5.x issues with
        /// nested renderers are covered by symptom-gated workarounds
        /// (config.json: <c>mount_watchdog</c>, <c>nested_prevention</c>,
        /// <c>nested_repair</c>); see the Unity 6.5 known-issues docs page.</para>
        /// </summary>
        public void Initialize(
            UnityEngine.UIElements.PanelRenderer hostRenderer,
            Action<HostContext> env = null
        )
        {
            AdoptRootSource(new PanelRendererRootSource(hostRenderer), env);
        }
#endif

        private void AdoptRootSource(IRootSource source, Action<HostContext> env)
        {
            EnsureSetup();
            rootSource?.Stop();
            rootSource = source;
            rootElement = source.CurrentRoot as VisualElement;
            env?.Invoke(sharedHostContext);
            source.Start(OnRootSourceChanged);
        }

        private void OnRootSourceChanged()
        {
            var previous = rootElement;
            var next = rootSource?.CurrentRoot as VisualElement;
            rootElement = next;
            if (next == null)
            {
                return;
            }
            if (vnodeHostRenderer != null)
            {
                if (previous != null && !sharedHostContext.HostConfig.IsAlive(previous))
                {
                    // REMOUNT (Unity 6.5): the old container was released -
                    // touching it throws, so the tree is abandoned (cleanups
                    // run, hosts untouched) and the last UI replays into the
                    // fresh container. Hook state is lost by design; a
                    // released tree has nothing salvageable.
                    vnodeHostRenderer.Abandon();
                    vnodeHostRenderer = null;
                    var replay = pendingRootNode ?? lastRootNode;
                    pendingRootNode = null;
                    if (replay != null)
                    {
                        Render(replay);
                    }
                    return;
                }
                // Move the live tree onto the freshly-built root, preserving
                // hook, ref and animation state.
                vnodeHostRenderer.RetargetHost(next);
                return;
            }
            if (pendingRootNode != null)
            {
                var deferred = pendingRootNode;
                pendingRootNode = null;
                Render(deferred);
            }
        }

        public void Render(VirtualNode rootNode)
        {
            EnsureSetup();
            if (rootElement == null)
            {
                pendingRootNode = rootNode;
                return;
            }
            pendingRootNode = null;
            lastRootNode = rootNode;
            if (vnodeHostRenderer == null)
            {
                vnodeHostRenderer = new VNodeHostRenderer(sharedHostContext, rootElement);
            }
            vnodeHostRenderer.Render(rootNode);
        }

        public void Unmount()
        {
            rootSource?.Stop();
            pendingRootNode = null;
            lastRootNode = null;
            if (vnodeHostRenderer != null)
            {
                vnodeHostRenderer.Unmount();
                vnodeHostRenderer = null;
            }
        }
    }
}
