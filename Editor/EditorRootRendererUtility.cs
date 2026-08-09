using System;
using System.Collections.Generic;
using Ruitk.Core;
using Ruitk.Core.Diagnostics;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using Ruitk.Signals;
using UnityEditor;
using UnityEngine.UIElements;

namespace Ruitk.EditorSupport
{
    public static class EditorRootRendererUtility
    {
        private static readonly Dictionary<VisualElement, VNodeHostRenderer> renderersByHost =
            new();

        /// <summary>
        /// Mounts a component tree on <paramref name="hostElement"/>.
        /// </summary>
        /// <param name="hostElement">The VisualElement that acts as the React root.</param>
        /// <param name="root">The root VirtualNode to render.</param>
        /// <param name="env">
        /// Optional callback invoked with the freshly-created <see cref="HostContext"/> before
        /// the renderer is started.  Use this to seed named portal target slots:
        /// <code>
        /// env: ctx => ctx.Environment[PortalContextKeys.ModalRoot] = myOverlayPanel
        /// </code>
        /// The callback is only called when a <b>new</b> renderer is created for this host
        /// element; subsequent <c>Mount</c>/<c>Render</c> calls on the same host are no-ops
        /// for context setup (the context is shared for the renderer's lifetime).
        /// </param>
        public static void Mount(
            VisualElement hostElement,
            VirtualNode root,
            Action<HostContext> env = null
        )
        {
            if (hostElement == null || root == null)
            {
                return;
            }
            if (!renderersByHost.TryGetValue(hostElement, out VNodeHostRenderer renderer))
            {
                // Note for the editor world: frame_budget_ms does NOT apply
                // here (the editor scheduler is unbudgeted by design — it
                // drains fully every editor update); time slicing does.
                HostContext hostContext = RuitkBootstrap.CreateHostContext(
                    ElementRegistryProvider.GetDefaultRegistry(),
                    hostConfig: null,
                    scheduler: EditorRenderScheduler.Instance,
                    isEditor: true
                );

                // Caller-supplied environment seeding (portal slots, feature flags, etc.)
                env?.Invoke(hostContext);

                renderer = new VNodeHostRenderer(hostContext, hostElement);
                renderersByHost[hostElement] = renderer;
            }
            renderer.Render(root);
        }

        /// <inheritdoc cref="Mount"/>
        public static void Render(
            VisualElement hostElement,
            VirtualNode root,
            Action<HostContext> env = null
        )
        {
            Mount(hostElement, root, env);
        }

        public static void Unmount(VisualElement hostElement)
        {
            if (hostElement == null)
            {
                return;
            }
            if (renderersByHost.TryGetValue(hostElement, out VNodeHostRenderer renderer))
            {
                renderer.Unmount();
                renderersByHost.Remove(hostElement);
                // Drain all queued effect cleanups synchronously. Without
                // this, effects (e.g. AudioFunc/VideoFunc cleanups that
                // stop playback and return pooled peers) are enqueued on
                // the editor scheduler and only run on the next
                // EditorApplication.update tick. When the calling
                // EditorWindow is being closed the next tick may not
                // reach those cleanups before the user reopens the
                // window, leaving pooled AudioSources still playing
                // \u2014 the \"audio stacks every reopen\" symptom.
                EditorRenderScheduler.Instance.PumpNow();
            }
        }
    }
}
