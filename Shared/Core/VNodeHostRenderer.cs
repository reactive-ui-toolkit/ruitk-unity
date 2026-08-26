using System;
using System.Collections.Generic;
using Ruitk.Core.Fiber;
using Ruitk.Props;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Core
{
    public interface IVNodeHostRenderer
    {
        void Render(VirtualNode vnode);
        void Unmount();
    }

    public sealed class VNodeHostRenderer : IVNodeHostRenderer
    {
        private readonly FiberRenderer fiberRenderer;
        private VisualElement hostElement;
        private IReadOnlyDictionary<string, object> lastHostProps;

        public VNodeHostRenderer(HostContext hostContext, VisualElement host)
        {
            hostElement = host;
            fiberRenderer = new FiberRenderer(host, hostContext);
        }

        internal FiberRenderer Fiber => fiberRenderer;

        public void Render(VirtualNode vnode)
        {
            fiberRenderer.Render(NormalizeHostRoot(vnode));
        }

        public void Unmount()
        {
            ClearHostProps();
            fiberRenderer?.Clear();
        }

        /// <summary>
        /// Tear down without touching the host element or its subtree - the
        /// Unity 6.5 remount path (the host is released; even a style write
        /// throws). Cleanups still run; see <see cref="FiberRenderer.Abandon"/>.
        /// </summary>
        public void Abandon()
        {
            lastHostProps = null;
            fiberRenderer?.Abandon();
        }

        /// <summary>
        /// Repoints this renderer at a new host element. Used by
        /// <see cref="RootRenderer"/> when Unity rebuilds the panel
        /// (UIDocument asset swap, undo, disable/enable, editor selection
        /// storm in playmode) so the rendered fiber tree can be moved to
        /// the freshly-created root without unmount/remount - preserving
        /// hook, ref and animation state. Re-applies the last host props
        /// to the new element so style overrides (flexGrow, etc.) survive
        /// the swap. The handle is object-typed to match the host-config
        /// seam; for this UI Toolkit renderer it must be a
        /// <see cref="VisualElement"/>.
        /// </summary>
        public void RetargetHost(object nextHostHandle)
        {
            var nextHost = nextHostHandle as VisualElement;
            if (nextHost == null || ReferenceEquals(nextHost, hostElement))
            {
                return;
            }
            // The old root is being abandoned: drop its style tracking and
            // detach any host-level ref (previously this leaked one
            // previousStyles entry per retarget), and stop animations that
            // captured it. The children move to the new root and keep theirs.
            if (hostElement != null)
            {
                PropsApplier.NotifyElementRemoved(hostElement);
                Animation.Animator.StopAllFor(hostElement);
            }
            hostElement = nextHost;
            if (lastHostProps != null)
            {
                PropsApplier.Apply(hostElement, lastHostProps);
            }
            fiberRenderer.RetargetContainer(nextHost);
        }

        private VirtualNode NormalizeHostRoot(VirtualNode vnode)
        {
            if (vnode == null)
            {
                ClearHostProps();
                return null;
            }

            if (vnode.NodeType != VirtualNodeType.Host)
            {
                ClearHostProps();
                return vnode;
            }

            ApplyHostProps(vnode.Properties ?? VirtualNode.EmptyProps);
            return WrapHostChildren(vnode);
        }

        private void ApplyHostProps(IReadOnlyDictionary<string, object> nextProps)
        {
            if (lastHostProps == null)
            {
                PropsApplier.Apply(hostElement, nextProps);
            }
            else
            {
                PropsApplier.ApplyDiff(hostElement, lastHostProps, nextProps);
            }

            lastHostProps = nextProps;
        }

        private void ClearHostProps()
        {
            if (lastHostProps == null)
            {
                return;
            }

            PropsApplier.ApplyDiff(hostElement, lastHostProps, VirtualNode.EmptyProps);
            lastHostProps = null;
        }

        private static VirtualNode WrapHostChildren(VirtualNode hostNode)
        {
            var children = hostNode.Children;
            int count = children?.Count ?? 0;
            if (count == 0)
            {
                return Ruitk.V.Fragment(hostNode.Key);
            }

            var buffer = new VirtualNode[count];
            for (int i = 0; i < count; i++)
            {
                buffer[i] = children[i];
            }
            return Ruitk.V.Fragment(hostNode.Key, buffer);
        }
    }
}
