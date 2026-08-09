using Ruitk.Core;
using Ruitk.Elements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Core.Fiber
{
    /// <summary>
    /// Simple renderer using Fiber reconciler
    /// Drop-in replacement for VNodeHostRenderer
    /// </summary>
    public partial class FiberRenderer
    {
        private FiberRoot _root;
        private FiberReconciler _reconciler;
        private object _container;
        private FiberHostConfig _hostConfig;

#if UNITY_EDITOR
        /// <summary>HMR: exposes the root for fiber tree walking.</summary>
        internal FiberRoot Root => _root;
#endif

        public FiberRenderer(VisualElement container, HostContext context = null)
        {
            _container = container;

            context ??= CreateDefaultContext();

            _hostConfig = context.HostConfig;
            _reconciler = new FiberReconciler(context);
#if UNITY_EDITOR
            MountRegistry.Register(this);
#endif
        }

        /// <summary>
        /// Backend-agnostic entry: the container is an opaque host handle
        /// interpreted by <paramref name="context"/>'s
        /// <see cref="HostContext.HostConfig"/> (e.g. a RectTransform host
        /// under the uGUI backend).
        /// </summary>
        public FiberRenderer(object container, HostContext context)
        {
            context ??= CreateDefaultContext();

            _container = container;
            _hostConfig = context.HostConfig;
            _reconciler = new FiberReconciler(context);
#if UNITY_EDITOR
            MountRegistry.Register(this);
#endif
        }

        // The null-context default is supplied per compilation flavour (the Unity
        // build resolves the default UI Toolkit ElementRegistry; the host-agnostic
        // test build has no default backend and its part throws). Must never
        // return null.
        private static partial HostContext CreateDefaultContext();

        /// <summary>
        /// Render a virtual node tree (initial mount)
        /// </summary>
        public void Render(VirtualNode vnode)
        {
            if (_root == null)
            {
                // Initial mount - ensure container is clean
                _hostConfig.ClearChildren(_container);
                _root = _reconciler.CreateRoot(_container, vnode);
            }
            else
            {
                // Update
                _reconciler.ScheduleUpdateOnFiber(_root.Current, vnode);
            }
        }

        /// <summary>
        /// Clear and unmount the entire tree.
        ///
        /// <para>
        /// Runs every effect cleanup (UseEffect / UseLayoutEffect) and
        /// disposes every signal subscription before clearing the host
        /// container. This is critical for components that own external
        /// resources (pooled <c>VideoPlayer</c>, <c>AudioSource</c>,
        /// timers, native listeners, etc.) \u2014 without this they leak,
        /// e.g. an &lt;Audio&gt; element keeps playing forever after its
        /// owning EditorWindow is closed.
        /// </para>
        /// </summary>
        public void Clear()
        {
            _reconciler?.UnmountRoot();
            _hostConfig.ClearChildren(_container);
            _root = null;
#if UNITY_EDITOR
            MountRegistry.Unregister(this);
#endif
        }

        /// <summary>
        /// Tear down the tree without touching the host container - the Unity
        /// 6.5 remount path, where the container subtree is released and any
        /// host access throws. Effect cleanups and signal disposals still run
        /// via <see cref="FiberReconciler.AbandonRoot"/>.
        /// </summary>
        public void Abandon()
        {
            _reconciler?.AbandonRoot();
            _root = null;
#if UNITY_EDITOR
            MountRegistry.Unregister(this);
#endif
        }

        /// <summary>
        /// Repoint the renderer at a new container VisualElement without
        /// tearing down the fiber tree. Moves all currently-mounted
        /// children from the old container to the new one (preserving
        /// child VisualElement identity, hooks, refs, animations) and
        /// updates the FiberRoot / root FiberNode host pointers so
        /// subsequent reconciliations write to the correct element.
        ///
        /// Called by <see cref="VNodeHostRenderer.RetargetHost"/> in
        /// response to UIDocument panel rebuilds where the parent
        /// rootVisualElement was replaced but the user's logical UI tree
        /// is unchanged.
        /// </summary>
        public void RetargetContainer(object nextContainer)
        {
            if (nextContainer == null || ReferenceEquals(nextContainer, _container))
            {
                return;
            }
            // Snapshot before moving — Add() removes from current parent and
            // mutates the source collection.
            int childCount = _hostConfig.GetChildCount(_container);
            if (childCount > 0)
            {
                var moved = new object[childCount];
                for (int i = 0; i < childCount; i++)
                {
                    moved[i] = _hostConfig.GetChildAt(_container, i);
                }
                for (int i = 0; i < childCount; i++)
                {
                    _hostConfig.AppendChild(nextContainer, moved[i]);
                }
            }
            _container = nextContainer;
            if (_root != null)
            {
                _root.ContainerElement = nextContainer;
                if (_root.Current != null)
                {
                    _root.Current.HostElement = nextContainer;
                }
            }
        }
    }
}
