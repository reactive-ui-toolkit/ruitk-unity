using System.Collections.Generic;
using Ruitk.Elements;
using Ruitk.Props.Typed;
using UnityEngine.UIElements;

namespace Ruitk.Core.Fiber
{
    /// <summary>
    /// UI Toolkit host backend. Host handles are <see cref="VisualElement"/>s;
    /// element creation and property application resolve through the
    /// <see cref="ElementRegistry"/> adapters.
    /// </summary>
    public sealed class UitkHostConfig : FiberHostConfig
    {
        private readonly ElementRegistry _registry;

        public UitkHostConfig(ElementRegistry registry)
        {
            _registry = registry;
        }

        public override object CreateElement(string elementType)
        {
            var adapter = _registry.Resolve(elementType);
            VisualElement element;
            if (adapter != null)
            {
                element = adapter.Create();
            }
            else
            {
                // Fallback to generic VisualElement
                element = new VisualElement { name = elementType };
            }
            return element;
        }

        public override void ApplyProperties(
            object element,
            string elementType,
            IReadOnlyDictionary<string, object> oldProps,
            IReadOnlyDictionary<string, object> newProps
        )
        {
            var adapter = _registry.Resolve(elementType);
            if (adapter != null && newProps != null)
            {
                var ve = (VisualElement)element;
                if (oldProps != null)
                {
                    adapter.ApplyPropertiesDiff(ve, oldProps, newProps);
                }
                else
                {
                    adapter.ApplyProperties(ve, newProps);
                }
            }
        }

        public override void ApplyTypedProperties(
            object element,
            string elementType,
            HostPropsBase oldProps,
            HostPropsBase newProps
        )
        {
            var adapter = _registry.Resolve(elementType);
            if (adapter is ITypedElementAdapter typed && newProps != null)
            {
                var ve = (VisualElement)element;
                if (oldProps != null)
                    typed.ApplyTypedDiff(ve, (BaseProps)oldProps, (BaseProps)newProps);
                else
                    typed.ApplyTypedFull(ve, (BaseProps)newProps);
            }
        }

        public override void AppendChild(object parent, object child)
        {
            ((VisualElement)parent).Add((VisualElement)child);
        }

        public override void InsertBefore(object parent, object child, object beforeChild)
        {
            var parentVe = (VisualElement)parent;
            var childVe = (VisualElement)child;

            // Same-parent move (keyed reorder): detach first so the anchor
            // index is computed on the post-removal list — DOM insertBefore
            // semantics; VisualElement.Insert's internal remove would
            // otherwise shift the index and land the child after the anchor.
            if (childVe.parent == parentVe)
            {
                parentVe.Remove(childVe);
            }

            if (beforeChild == null)
            {
                parentVe.Add(childVe);
            }
            else
            {
                int index = parentVe.IndexOf((VisualElement)beforeChild);
                if (index >= 0)
                {
                    parentVe.Insert(index, childVe);
                }
                else
                {
                    parentVe.Add(childVe);
                }
            }
        }

        public override void RemoveChild(object parent, object child)
        {
            var parentVe = (VisualElement)parent;
            var childVe = (VisualElement)child;
            if (childVe != null && childVe.parent == parentVe)
            {
                parentVe.Remove(childVe);
            }
        }

        public override object GetParent(object element)
        {
            return ((VisualElement)element)?.parent;
        }

        public override void ClearChildren(object element)
        {
            ((VisualElement)element)?.Clear();
        }

        public override int GetChildCount(object element)
        {
            return ((VisualElement)element)?.childCount ?? 0;
        }

        public override object GetChildAt(object element, int index)
        {
            return ((VisualElement)element)[index];
        }

        public override void OnHostRemoved(object element)
        {
            // The "element left the tree for good" hub: every UITK retention
            // site releases here so a deleted element cannot be reached from
            // static state again (§5.4 of the 6.5 plan). PropsApplier drops the
            // previousStyles entry and detaches any user ref (both the dict and
            // typed pipelines funnel refs into NodeMetadata.AttachedRef);
            // Animator stops tick lambdas that captured the element; the
            // virtualized list/tree adapters unmount their pooled row
            // renderers, running row effect cleanups that never ran before.
            var ve = (VisualElement)element;
            Ruitk.Props.PropsApplier.NotifyElementRemoved(ve);
            Animation.Animator.StopAllFor(ve);
            ListViewElementAdapter.NotifyHostRemoved(ve);
            TreeViewElementAdapter.NotifyHostRemoved(ve);
            MultiColumnListViewElementAdapter.NotifyHostRemoved(ve);
            MultiColumnTreeViewElementAdapter.NotifyHostRemoved(ve);
        }

        public override bool IsAlive(object element)
        {
            if (element is not VisualElement ve)
            {
                return element != null;
            }
#if UNITY_6000_5_OR_NEWER
            return !ve.resourcesReleased;
#else
            return true;
#endif
        }

        public override string GetDebugName(object element)
        {
            return (element as VisualElement)?.name ?? element?.GetType().Name;
        }
    }
}
