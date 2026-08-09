using System.Collections.Generic;
using Ruitk.Core;
using Ruitk.Core.Fiber;
using Ruitk.Props.Typed;
using UnityEngine;

namespace Ruitk.Ugui
{
    /// <summary>
    /// uGUI host backend. Host handles are <see cref="GameObject"/>s under a
    /// Canvas. Fresh elements are parented to a hidden inactive staging root
    /// until the commit phase places them (the render phase is time-sliced,
    /// so an unparented GameObject would otherwise sit visible at the scene
    /// root mid-render; staging also defers Awake until real placement,
    /// matching "not mounted" semantics).
    /// </summary>
    public sealed class UguiHostConfig : FiberHostConfig
    {
        private readonly UguiElementRegistry _registry;
        private Transform _stagingRoot;

        // Per-element-type pool for stateless visuals. An element joins only
        // when its adapter's TryResetForPool restores the pristine Create()
        // state, so reuse can never leak state between mounts. Stateful
        // controls are destroyed as before. The host_node_pool setting gates
        // the whole pool (read once at construction — bootstrap-read
        // discipline); the capacity stays a per-leg constant by family ruling.
        private const int PoolCapacityPerType = 128;
        private readonly bool _poolEnabled;
        private readonly Dictionary<UguiElementAdapter, Stack<GameObject>> _pool =
            new Dictionary<UguiElementAdapter, Stack<GameObject>>();


        public UguiHostConfig(UguiElementRegistry registry)
        {
            _registry = registry;
            _poolEnabled = BuildDefinesConfig.ResolveHostNodePool();
        }

        private Transform StagingRoot
        {
            get
            {
                if (_stagingRoot == null)
                {
                    var go = new GameObject("Ruitk.Ugui.Staging");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    go.SetActive(false);
                    _stagingRoot = go.transform;

                }
                return _stagingRoot;
            }
        }

        public override object CreateElement(string elementType)
        {
            var adapter = _registry.Resolve(elementType);
            GameObject go = null;
            if (_poolEnabled && adapter != null && _pool.TryGetValue(adapter, out var stack))
            {
                while (stack.Count > 0 && go == null)
                {
                    var candidate = stack.Pop();
                    if (candidate != null)
                        go = candidate;
                }
            }
            if (go == null)
            {
                if (adapter != null)
                {
                    go = adapter.Create();
                    UguiNodeTag.GetOrAdd(go).Adapter = adapter;
                }
                else
                {
                    go = new GameObject(elementType, typeof(RectTransform));
                }
            }

            go.transform.SetParent(StagingRoot, false);
            return go;
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
                var go = (GameObject)element;
                if (oldProps != null)
                    adapter.ApplyPropertiesDiff(go, oldProps, newProps);
                else
                    adapter.ApplyProperties(go, newProps);
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
            if (adapter != null && newProps != null)
            {
                var go = (GameObject)element;
                if (oldProps != null)
                    adapter.ApplyTypedDiff(go, (UguiBaseProps)oldProps, (UguiBaseProps)newProps);
                else
                    adapter.ApplyTypedFull(go, (UguiBaseProps)newProps);
            }
        }

        public override void AppendChild(object parent, object child)
        {
            var childGo = (GameObject)child;
            if (childGo == null)
                return;
            var host = ResolveChildHostTransform(parent);
            if (host == null)
                return;

            childGo.transform.SetParent(host, false);
            childGo.transform.SetAsLastSibling();
        }

        public override void InsertBefore(object parent, object child, object beforeChild)
        {
            var childGo = (GameObject)child;
            if (childGo == null)
                return;
            var host = ResolveChildHostTransform(parent);
            if (host == null)
                return;

            // Same-parent move (keyed reorder): detach first so the anchor
            // index is computed on the post-removal sibling list (DOM
            // insertBefore semantics).
            if (childGo.transform.parent == host)
            {
                childGo.transform.SetParent(StagingRoot, false);
            }

            var beforeGo = (GameObject)beforeChild;
            if (beforeGo == null || beforeGo.transform.parent != host)
            {
                childGo.transform.SetParent(host, false);
                childGo.transform.SetAsLastSibling();
                return;
            }


            int index = beforeGo.transform.GetSiblingIndex();
            childGo.transform.SetParent(host, false);
            childGo.transform.SetSiblingIndex(index);
        }

        public override void RemoveChild(object parent, object child)
        {
            var childGo = (GameObject)child;
            if (childGo == null)
                return;
            var host = ResolveChildHostTransform(parent);
            if (host != null && childGo.transform.parent == host)
            {

                childGo.transform.SetParent(StagingRoot, false);
            }
        }

        public override object GetParent(object element)
        {
            var go = (GameObject)element;
            if (go == null)
                return null;
            var parent = go.transform.parent;
            if (parent == null || parent == _stagingRoot)
                return null;
            return parent.gameObject;
        }

        public override void ClearChildren(object element)
        {
            var go = (GameObject)element;
            if (go == null)
                return;
            var t = go.transform;
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                DestroySafely(t.GetChild(i).gameObject);
            }
        }

        public override int GetChildCount(object element)
        {
            var go = (GameObject)element;
            return go != null ? go.transform.childCount : 0;
        }

        public override object GetChildAt(object element, int index)
        {
            return ((GameObject)element).transform.GetChild(index).gameObject;
        }

        public override void OnHostRemoved(object element)
        {
            var go = (GameObject)element;
            if (go == null)
                return;
            var tag = UguiNodeTag.Find(go);
            if (tag != null && tag.AssignedRef != null)
            {
                UguiRefUtility.Assign(tag.AssignedRef, null);
                tag.AssignedRef = null;
            }

            var adapter = tag != null ? tag.Adapter : null;
            if (
                _poolEnabled
                && adapter != null
                && go.transform.childCount == 0
                && adapter.TryResetForPool(go)
            )
            {
                if (!_pool.TryGetValue(adapter, out var stack))
                {
                    stack = new Stack<GameObject>();
                    _pool[adapter] = stack;
                }
                if (stack.Count < PoolCapacityPerType)
                {

                    go.transform.SetParent(StagingRoot, false);
                    stack.Push(go);
                    return;
                }
            }

            DestroySafely(go);
        }

        public override string GetDebugName(object element)
        {
            var go = (GameObject)element;
            return go != null ? go.name : "GameObject(destroyed)";
        }

        public override bool IsAlive(object element)
        {
            // Unity fake-null: a destroyed GameObject compares equal to null
            // through the engine's overloaded operator even though the managed
            // wrapper is still reachable.
            return element is GameObject go ? go != null : element != null;
        }

        private Transform ResolveChildHostTransform(object parent)
        {
            var parentGo = (GameObject)parent;
            if (parentGo == null)
                return null;
            var tag = UguiNodeTag.Find(parentGo);
            var host = tag?.Adapter?.ResolveChildHost(parentGo) ?? parentGo;
            return host.transform;
        }

        private static void DestroySafely(GameObject go)
        {
            if (go == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(go);
            else
                Object.DestroyImmediate(go);
        }
    }
}
