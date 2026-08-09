using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ruitk.Core;
using Ruitk.Props;
using Ruitk.Props.Typed;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    public sealed class TreeViewElementAdapter
        : StatefulElementAdapter<TreeView, TreeViewElementAdapter.Cached>
    {
        public sealed class Cached : IExpansionState, IScrollState
        {
            public bool RowWired;
            public RowRenderer RowFn;

            public Dictionary<string, (IVNodeHostRenderer renderer, VisualElement mount)> Pool =
                new();

            internal ExpansionStateTracker<TreeView, Cached> ExpansionTracker =
                new ExpansionStateTracker<TreeView, Cached>();
            public HashSet<int> DesiredExpanded { get; set; } = new();
            public Dictionary<int, bool> ExpandAllById { get; set; } = new();
            public bool OurHandlerAttached { get; set; }
            public Delegate UserExpandedHandler { get; set; }
            public Action<TreeViewExpansionChangedArgs> UserExpandedHandlerWrapped { get; set; }
            public bool TrackUserExpansion { get; set; } = true;

            public bool IsScrolling { get; set; }
            public bool ScrollWired { get; set; }
            public IReadOnlyDictionary<string, object> PendingPrev { get; set; }
            public IReadOnlyDictionary<string, object> PendingNext { get; set; }
            public float ScrollX { get; set; }
            public float ScrollY { get; set; }
            public int ScrollActivityId { get; set; }

            public IElementStateTracker<TreeView, Cached> ScrollTracker =
                new MultiColumnScrollTracker<TreeView, Cached>(
                    new MultiColumnScrollOps<TreeView>(),
                    flush: null
                );
        }

        private static HostContext sharedHost;
        private static HostContext Host =>
            sharedHost ??= new HostContext(ElementRegistryProvider.GetDefaultRegistry());

        public override VisualElement Create() => new TreeView();

        /// <summary>
        /// Host-removal hook (§5.4 retention cleanup): unmounts every pooled
        /// row renderer and drops the cached state for the deleted TreeView.
        /// </summary>
        internal static void NotifyHostRemoved(VisualElement element)
        {
            if (element is not TreeView tv || !TryGetState(tv, out var parts))
            {
                return;
            }
            foreach (var entry in parts.Pool.Values)
            {
                entry.renderer?.Unmount();
            }
            parts.Pool.Clear();
            RemoveState(tv);
        }

        public override void ApplyProperties(
            VisualElement element,
            IReadOnlyDictionary<string, object> properties
        )
        {
            if (element is not TreeView tv)
            {
                PropsApplier.Apply(element, properties);
                return;
            }
            var parts = GetState(tv);
            if (properties == null)
            {
                PropsApplier.Apply(element, properties);
                return;
            }
            EnsureViewDataKey(tv, properties);

            try
            {
                var ops = Ruitk.Elements.TreeViewExpansionOps.Instance;
                parts.ExpansionTracker.Attach(tv, parts, properties, ops);
            }
            catch { }

            parts.ScrollTracker.Attach(tv, parts, properties);

            if (properties.TryGetValue("expandedItemIds", out var expObj))
            {
                var ids = BaseElementAdapter.CoerceIds(expObj);
                parts.DesiredExpanded.Clear();
                parts.ExpandAllById.Clear();
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        parts.DesiredExpanded.Add(id);
                    }
                }
                ReapplyExpansion(tv, parts, null, properties);
            }

            if (properties.TryGetValue("rootItems", out var roots))
            {
                var scrollSnapshot = CaptureScrollSnapshot(tv);
                SetRootItems(tv, roots);
                try
                {
                    tv.RefreshItems();
                }
                catch { }
                ReapplyExpansion(tv, parts, null, properties, scrollSnapshot);
            }

            TryApplyProp<float>(properties, "fixedItemHeight", f => tv.fixedItemHeight = f);
            if (properties.TryGetValue("selectionType", out var sel) && sel is SelectionType st)
            {
                tv.selectionType = st;
            }
            TryApplyProp<int>(properties, "selectedIndex", i => tv.SetSelection(i));

            if (properties.TryGetValue("row", out var rowFn) && rowFn is RowRenderer rf)
            {
                parts.RowFn = rf;
                if (!parts.RowWired)
                {
                    parts.RowWired = true;
                    tv.makeItem = () => new VisualElement();
                    tv.bindItem = (ve, index) =>
                    {
                        object item = GetItemForIndex(tv, index);
                        var key = DeriveRowKey(tv, index, item) ?? $"row-{index}";
                        if (!parts.Pool.TryGetValue(key, out var entry))
                        {
                            var mount = new VisualElement();
                            var rrNew = new VNodeHostRenderer(Host, mount);
                            entry = (rrNew, mount);
                            parts.Pool[key] = entry;
                        }
                        if (entry.mount.parent != ve)
                        {
                            try
                            {
                                entry.mount.RemoveFromHierarchy();
                            }
                            catch { }
                            ve.Add(entry.mount);
                        }
                        var f = parts.RowFn;
                        if (f != null)
                        {
                            var vnode = EnsureVisualElementRoot(f(index, item), "TreeViewRow");
                            entry.renderer.Render(vnode);
                        }
                    };
                    tv.unbindItem = (ve, i) =>
                    {
                        foreach (var kv in parts.Pool)
                        {
                            var mount = kv.Value.mount;
                            if (mount != null && mount.parent == ve)
                            {
                                try
                                {
                                    mount.RemoveFromHierarchy();
                                }
                                catch { }
                            }
                        }
                    };
                }
            }

            if (
                properties.TryGetValue("contentContainer", out var cc)
                && cc is Dictionary<string, object> ccMap
            )
            {
                PropsApplier.Apply(tv.contentContainer, ccMap);
            }
            if (
                properties.TryGetValue("scrollView", out var sv)
                && sv is Dictionary<string, object> svMap
            )
            {
                var scroll = tv.Q<ScrollView>();
                if (scroll != null)
                {
                    PropsApplier.Apply(scroll, svMap);
                }
            }

            PropsApplier.Apply(element, properties);
            parts.ScrollTracker.Reapply(tv, parts, null, properties);
        }

        public override void ApplyPropertiesDiff(
            VisualElement element,
            IReadOnlyDictionary<string, object> previous,
            IReadOnlyDictionary<string, object> next
        )
        {
            if (element is not TreeView tv)
            {
                PropsApplier.ApplyDiff(element, previous, next);
                return;
            }
            previous ??= s_emptyProps;
            next ??= s_emptyProps;
            var parts = GetState(tv);
            EnsureViewDataKey(tv, next);

            try
            {
                var ops = Ruitk.Elements.TreeViewExpansionOps.Instance;
                parts.ExpansionTracker.Attach(tv, parts, next, ops);
            }
            catch { }

            previous.TryGetValue("rootItems", out var pr);
            next.TryGetValue("rootItems", out var nr);
            if (!ReferenceEquals(pr, nr))
            {
                var scrollSnapshot = CaptureScrollSnapshot(tv);
                SetRootItems(tv, nr);
                try
                {
                    tv.RefreshItems();
                }
                catch { }
                ReapplyExpansion(tv, parts, previous, next, scrollSnapshot);
            }

            TryDiffProp<float>(previous, next, "fixedItemHeight", f => tv.fixedItemHeight = f);
            if (next.TryGetValue("selectionType", out var sel) && sel is SelectionType st)
            {
                tv.selectionType = st;
            }
            TryDiffProp<int>(previous, next, "selectedIndex", i => tv.SetSelection(i));

            if (next.TryGetValue("expandedItemIds", out var nextExp))
            {
                var ids = BaseElementAdapter.CoerceIds(nextExp);
                parts.DesiredExpanded.Clear();
                parts.ExpandAllById.Clear();
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        parts.DesiredExpanded.Add(id);
                    }
                }
                ReapplyExpansion(tv, parts, previous, next);
            }

            PropsApplier.ApplyDiff(element, previous, next);

            ReapplyExpansion(tv, parts, previous, next);
            parts.ScrollTracker.Reapply(tv, parts, previous, next);
        }

        public override void ApplyTypedFull(VisualElement element, BaseProps props)
        {
            var dict = props.ToDictionary();
            if (dict != null && dict.Count > 0)
                ApplyProperties(element, dict);
            base.ApplyTypedFull(element, props);
        }

        public override void ApplyTypedDiff(VisualElement element, BaseProps prev, BaseProps next)
        {
            var prevDict = prev.ToDictionary();
            var nextDict = next.ToDictionary();
            if (nextDict != null && nextDict.Count > 0)
                ApplyPropertiesDiff(element, prevDict, nextDict);
            base.ApplyTypedDiff(element, prev, next);
        }

        private static void SetRootItems(TreeView tv, object root)
        {
            if (tv == null)
            {
                return;
            }
            try
            {
                var mi = typeof(TreeView)
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "SetRootItems" && m.IsGenericMethodDefinition);
                if (mi != null)
                {
                    mi.MakeGenericMethod(typeof(object)).Invoke(tv, new object[] { root });
                    return;
                }
            }
            catch { }
            try
            {
                var any = typeof(TreeView)
                    .GetMethods()
                    .FirstOrDefault(m => m.Name == "SetRootItems" && m.GetParameters().Length == 1);
                any?.Invoke(tv, new object[] { root });
            }
            catch { }
        }

        private static object GetItemForIndex(TreeView tv, int index)
        {
            try
            {
                var methods = typeof(TreeView).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );
                var gen = methods.FirstOrDefault(m =>
                    m.Name == "GetItemDataForIndex" && m.IsGenericMethodDefinition
                );
                if (gen != null)
                {
                    try
                    {
                        return gen.MakeGenericMethod(typeof(object))
                            .Invoke(tv, new object[] { index });
                    }
                    catch { }
                }
                var nonGen = methods.FirstOrDefault(m =>
                    m.Name == "GetItemDataForIndex" && !m.IsGenericMethod
                );
                if (nonGen != null)
                {
                    try
                    {
                        return nonGen.Invoke(tv, new object[] { index });
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static string DeriveRowKey(TreeView tv, int index, object item)
        {
            try
            {
                if (item != null)
                {
                    var t = item.GetType();
                    var f = t.GetField("Id", BindingFlags.Instance | BindingFlags.Public);
                    if (f?.FieldType == typeof(string))
                    {
                        var s = f.GetValue(item) as string;
                        if (!string.IsNullOrEmpty(s))
                        {
                            return s;
                        }
                    }
                    var p = t.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public);
                    if (p?.PropertyType == typeof(string))
                    {
                        var s = p.GetValue(item) as string;
                        if (!string.IsNullOrEmpty(s))
                        {
                            return s;
                        }
                    }
                }
            }
            catch { }
            try
            {
                var mi = typeof(TreeView).GetMethod(
                    "GetIdForIndex",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(int) },
                    null
                );
                var idObj = mi?.Invoke(tv, new object[] { index });
                if (idObj != null)
                {
                    return idObj.ToString();
                }
            }
            catch { }
            return $"row-{index}";
        }

        private static void ReapplyExpansion(
            TreeView view,
            Cached parts,
            IReadOnlyDictionary<string, object> previous,
            IReadOnlyDictionary<string, object> next,
            ScrollSnapshot? snapshotOverride = null
        )
        {
            if (view == null || parts == null)
            {
                return;
            }

            var snapshot = snapshotOverride.HasValue
                ? snapshotOverride.Value
                : CaptureScrollSnapshot(view);

            try
            {
                var ops = Ruitk.Elements.TreeViewExpansionOps.Instance;
                parts.ExpansionTracker.Reapply(view, parts, previous, next, ops);
            }
            catch { }

            RestoreScrollSnapshot(view, snapshot);
        }

        private static ScrollSnapshot CaptureScrollSnapshot(TreeView view)
        {
            if (view == null)
            {
                return default;
            }

            try
            {
                var scroll = view.Q<ScrollView>();
                if (scroll == null)
                {
                    return default;
                }

                var offset = scroll.scrollOffset;
                var maxX = GetHighValue(scroll.horizontalScroller);
                var maxY = GetHighValue(scroll.verticalScroller);
                return new ScrollSnapshot(true, offset, maxX, maxY);
            }
            catch
            {
                return default;
            }
        }

        private static void RestoreScrollSnapshot(TreeView view, ScrollSnapshot snapshot)
        {
            if (view == null || !snapshot.IsValid)
            {
                return;
            }

            try
            {
                var scroll = view.Q<ScrollView>();
                if (scroll == null)
                {
                    return;
                }

                var target = new Vector2(
                    ResolveAxis(
                        snapshot.Offset.x,
                        snapshot.MaxX,
                        GetHighValue(scroll.horizontalScroller)
                    ),
                    ResolveAxis(
                        snapshot.Offset.y,
                        snapshot.MaxY,
                        GetHighValue(scroll.verticalScroller)
                    )
                );

                ApplyScrollOffset(scroll, target);
            }
            catch { }
        }

        private static void ApplyScrollOffset(ScrollView scroll, Vector2 target)
        {
            if (scroll == null)
            {
                return;
            }

            void Apply()
            {
                try
                {
                    scroll.scrollOffset = target;
                }
                catch { }
            }

            Apply();
            try
            {
                scroll.schedule?.Execute(Apply)?.ExecuteLater(0);
            }
            catch { }
        }

        private static float ResolveAxis(float previousValue, float previousMax, float newMax)
        {
            newMax = Math.Max(newMax, 0f);
            if (previousMax <= 0f)
            {
                return Clamp(previousValue, 0f, newMax);
            }

            var tolerance = Math.Max(previousMax * 0.01f, 2f);
            var distanceFromEnd = previousMax - previousValue;
            if (distanceFromEnd <= tolerance)
            {
                return newMax;
            }

            return Clamp(previousValue, 0f, newMax);
        }

        private static float GetHighValue(Scroller scroller)
        {
            if (scroller == null)
            {
                return 0f;
            }
            try
            {
                return scroller.highValue;
            }
            catch
            {
                return 0f;
            }
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }

        private readonly struct ScrollSnapshot
        {
            public ScrollSnapshot(bool isValid, Vector2 offset, float maxX, float maxY)
            {
                IsValid = isValid;
                Offset = offset;
                MaxX = maxX;
                MaxY = maxY;
            }

            public bool IsValid { get; }
            public Vector2 Offset { get; }
            public float MaxX { get; }
            public float MaxY { get; }
        }

        private static void EnsureViewDataKey(
            TreeView view,
            IReadOnlyDictionary<string, object> properties
        )
        {
            if (view == null)
            {
                return;
            }

            string desired = null;
            if (
                properties != null
                && properties.TryGetValue("viewDataKey", out var raw)
                && raw is string explicitKey
                && !string.IsNullOrEmpty(explicitKey)
            )
            {
                desired = explicitKey;
            }

            if (string.IsNullOrEmpty(desired))
            {
                if (
                    (view.userData as NodeMetadata)?.Key is string metadataKey
                    && !string.IsNullOrEmpty(metadataKey)
                )
                {
                    desired = metadataKey;
                }
                else if (!string.IsNullOrEmpty(view.name))
                {
                    desired = view.name;
                }
            }

            if (string.IsNullOrEmpty(desired))
            {
                return;
            }

            if (string.Equals(view.viewDataKey, desired, StringComparison.Ordinal))
            {
                return;
            }

            view.viewDataKey = desired;
        }
    }
}
