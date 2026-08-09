using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Ruitk.Core;
using Ruitk.Props;
using Ruitk.Props.Typed;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    public sealed class MultiColumnTreeViewElementAdapter
        : StatefulElementAdapter<MultiColumnTreeView, MultiColumnTreeViewElementAdapter.Cached>
    {
        public sealed class Cached
            : ISortState,
                IColumnLayoutState,
                IExpansionState,
                IAdjustmentSuspendState,
                IScrollState
        {
            public IList LastRoot;
            internal List<ColumnSignature> ColSig;
            public List<RowRenderer> CellFns;

            public HashSet<int> DesiredExpanded { get; set; } = new();
            public Dictionary<int, bool> ExpandAllById { get; set; } = new();
            public bool OurHandlerAttached { get; set; }
            public Delegate UserExpandedHandler { get; set; }
            public Action<TreeViewExpansionChangedArgs> UserExpandedHandlerWrapped { get; set; }
            public bool TrackUserExpansion { get; set; } = true;
            internal ExpansionStateTracker<MultiColumnTreeView, Cached> ExpansionTracker =
                new ExpansionStateTracker<MultiColumnTreeView, Cached>();

            public Dictionary<string, (IVNodeHostRenderer renderer, VisualElement mount)> Pool =
                new();

            public Dictionary<string, float> ColumnWidths { get; set; } = new();
            public Dictionary<string, bool> ColumnVisibility { get; set; } = new();
            public Dictionary<string, int> ColumnDisplayIndex { get; set; } = new();
            public IElementStateTracker<MultiColumnTreeView, Cached> LayoutTracker =
                new MultiColumnLayoutTracker<MultiColumnTreeView, Cached>();
            public Delegate ColumnLayoutChanged { get; set; }
            internal ColumnLayoutSnapshot LastLayoutSnapshot { get; set; }

            public List<(
                string name,
                SortDirection direction,
                int index
            )> SortedColumns { get; set; } = new();
            public IElementStateTracker<MultiColumnTreeView, Cached> SortTracker =
                new MultiColumnSortTracker<MultiColumnTreeView, Cached>();
            public Delegate UserSortNotify { get; set; }
            public Action InternalSortHandler { get; set; }

            public bool IsAdjusting { get; set; }
            public bool HeaderWired { get; set; }
            public IReadOnlyDictionary<string, object> PendingPrev { get; set; }
            public IReadOnlyDictionary<string, object> PendingNext { get; set; }
            public IElementStateTracker<MultiColumnTreeView, Cached> AdjustmentTracker =
                new MultiColumnAdjustmentTracker<MultiColumnTreeView, Cached>(
                    new MultiColumnHeaderOps<MultiColumnTreeView>(),
                    ApplyAdjustmentFlush
                );
            public bool DetachWired { get; set; }

            public bool IsScrolling { get; set; }
            public bool ScrollWired { get; set; }
            public float ScrollX { get; set; }
            public float ScrollY { get; set; }
            public int ScrollActivityId { get; set; }
            public IElementStateTracker<MultiColumnTreeView, Cached> ScrollTracker =
                new MultiColumnScrollTracker<MultiColumnTreeView, Cached>(
                    new MultiColumnScrollOps<MultiColumnTreeView>(),
                    ApplyAdjustmentFlush
                );

            public bool CommitQueued { get; set; }
            public bool IsCommitting { get; set; }
            public IReadOnlyDictionary<string, object> PendingCommit { get; set; }
        }

        private static HostContext host;
        private static HostContext Host =>
            host ??= new HostContext(ElementRegistryProvider.GetDefaultRegistry());

        internal sealed class ColumnLayoutSnapshot
        {
            public Dictionary<string, float> Widths;
            public Dictionary<string, bool> Visibility;
            public Dictionary<string, int> DisplayIndex;

            public ColumnLayoutSnapshot Clone() =>
                new ColumnLayoutSnapshot
                {
                    Widths = CloneDict(Widths),
                    Visibility = CloneDict(Visibility),
                    DisplayIndex = CloneDict(DisplayIndex),
                };
        }

        private static Dictionary<string, T> CloneDict<T>(Dictionary<string, T> source)
        {
            if (source == null || source.Count == 0)
            {
                return new Dictionary<string, T>();
            }
            return new Dictionary<string, T>(source);
        }

        private static ColumnLayoutSnapshot CaptureLayoutSnapshot(Cached parts)
        {
            if (parts == null)
            {
                return new ColumnLayoutSnapshot();
            }
            return new ColumnLayoutSnapshot
            {
                Widths = CloneDict(parts.ColumnWidths),
                Visibility = CloneDict(parts.ColumnVisibility),
                DisplayIndex = CloneDict(parts.ColumnDisplayIndex),
            };
        }

        private static bool LayoutEqual(ColumnLayoutSnapshot a, ColumnLayoutSnapshot b)
        {
            return DictEqual(a?.Widths, b?.Widths)
                && DictEqual(a?.Visibility, b?.Visibility)
                && DictEqual(a?.DisplayIndex, b?.DisplayIndex);
        }

        private static bool DictEqual<T>(Dictionary<string, T> left, Dictionary<string, T> right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left == null || right == null)
            {
                return false;
            }
            if (left.Count != right.Count)
            {
                return false;
            }
            foreach (var kv in left)
            {
                if (!right.TryGetValue(kv.Key, out var rv))
                {
                    return false;
                }
                if (!EqualityComparer<T>.Default.Equals(kv.Value, rv))
                {
                    return false;
                }
            }
            return true;
        }

        private static void DispatchLayoutChanged(MultiColumnTreeView view, Cached parts)
        {
            if (view == null || parts == null)
            {
                return;
            }

            var snapshot = CaptureLayoutSnapshot(parts);
            var changed = !LayoutEqual(parts.LastLayoutSnapshot, snapshot);
            parts.LastLayoutSnapshot = snapshot?.Clone();

            var callback = parts.ColumnLayoutChanged;
            if (callback == null || !changed)
            {
                return;
            }

            var payload = new ColumnLayoutState
            {
                ColumnWidths = CloneDict(snapshot?.Widths),
                ColumnVisibility = CloneDict(snapshot?.Visibility),
                ColumnDisplayIndex = CloneDict(snapshot?.DisplayIndex),
            };

            void Invoke()
            {
                try
                {
                    if (!TryDispatchLayoutDelegate(view, callback, payload))
                    {
                        callback.DynamicInvoke(payload);
                    }
                }
                catch { }
            }

#if UNITY_EDITOR
            try
            {
                UnityEditor.EditorApplication.delayCall += Invoke;
            }
            catch
            {
                Invoke();
            }
#else
            try
            {
                view.schedule?.Execute(Invoke)?.ExecuteLater(0);
            }
            catch
            {
                Invoke();
            }
#endif
        }

        private static bool TryDispatchLayoutDelegate(
            MultiColumnTreeView view,
            Delegate callback,
            ColumnLayoutState payload
        )
        {
            if (callback == null || payload == null)
            {
                return true;
            }

            switch (callback)
            {
                case ColumnLayoutEventHandler typedNew:
                    typedNew(payload);
                    return true;
                case Action<ColumnLayoutState> typed:
                    typed(payload);
                    return true;
                case Action<VisualElement, ColumnLayoutState> typedWithView:
                    typedWithView(view, payload);
                    return true;
                case Action<Dictionary<string, float>> widthsOnly:
                    widthsOnly(payload.ColumnWidths);
                    return true;
                case Action<
                    Dictionary<string, float>,
                    Dictionary<string, bool>,
                    Dictionary<string, int>
                > triple:
                    triple(
                        payload.ColumnWidths,
                        payload.ColumnVisibility,
                        payload.ColumnDisplayIndex
                    );
                    return true;
                case Action action:
                    action();
                    return true;
            }

            try
            {
                var parameters = callback.Method.GetParameters();
                if (parameters.Length == 0)
                {
                    callback.DynamicInvoke();
                    return true;
                }
                var args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var pt = parameters[i].ParameterType;
                    if (typeof(VisualElement).IsAssignableFrom(pt))
                    {
                        args[i] = view;
                        continue;
                    }
                    if (typeof(ColumnLayoutState).IsAssignableFrom(pt))
                    {
                        args[i] = payload;
                        continue;
                    }
                    if (
                        typeof(Dictionary<string, float>).IsAssignableFrom(pt)
                        || typeof(IReadOnlyDictionary<string, float>).IsAssignableFrom(pt)
                    )
                    {
                        args[i] = payload.ColumnWidths;
                        continue;
                    }
                    if (
                        typeof(Dictionary<string, bool>).IsAssignableFrom(pt)
                        || typeof(IReadOnlyDictionary<string, bool>).IsAssignableFrom(pt)
                    )
                    {
                        args[i] = payload.ColumnVisibility;
                        continue;
                    }
                    if (
                        typeof(Dictionary<string, int>).IsAssignableFrom(pt)
                        || typeof(IReadOnlyDictionary<string, int>).IsAssignableFrom(pt)
                    )
                    {
                        args[i] = payload.ColumnDisplayIndex;
                        continue;
                    }
                    if (pt == typeof(object))
                    {
                        args[i] = payload;
                        continue;
                    }
                    args[i] = payload;
                }
                callback.DynamicInvoke(args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override VisualElement Create() => new MultiColumnTreeView();

        /// <summary>
        /// Host-removal hook (§5.4 retention cleanup): unmounts every pooled
        /// cell renderer and drops the cached state for the deleted view.
        /// </summary>
        internal static void NotifyHostRemoved(VisualElement element)
        {
            if (element is not MultiColumnTreeView mctv || !TryGetState(mctv, out var parts))
            {
                return;
            }
            foreach (var entry in parts.Pool.Values)
            {
                entry.renderer?.Unmount();
            }
            parts.Pool.Clear();
            RemoveState(mctv);
        }

        private static void SetRootItems(MultiColumnTreeView tv, object root)
        {
            if (tv == null || root == null)
            {
                return;
            }
            var mi = typeof(MultiColumnTreeView)
                .GetMethods()
                .FirstOrDefault(m => m.Name == "SetRootItems" && m.IsGenericMethodDefinition);
            if (mi != null)
            {
                try
                {
                    mi.MakeGenericMethod(typeof(object)).Invoke(tv, new object[] { root });
                    return;
                }
                catch { }
            }
            var any = typeof(MultiColumnTreeView)
                .GetMethods()
                .FirstOrDefault(m => m.Name == "SetRootItems" && m.GetParameters().Length == 1);
            try
            {
                any?.Invoke(tv, new object[] { root });
            }
            catch { }
        }

        private static object GetItemForRow(MultiColumnTreeView tv, int index)
        {
            try
            {
                var mi = typeof(MultiColumnTreeView)
                    .GetMethods()
                    .FirstOrDefault(m =>
                        m.Name == "GetItemDataForIndex" && m.IsGenericMethodDefinition
                    );
                if (mi != null)
                {
                    return mi.MakeGenericMethod(typeof(object)).Invoke(tv, new object[] { index });
                }
            }
            catch { }
            return null;
        }

        private static void ApplySortingMode(MultiColumnTreeView tv, object mode)
        {
            if (tv == null || mode == null)
            {
                return;
            }
            try
            {
                var pi = typeof(MultiColumnTreeView).GetProperty(
                    "sortingMode",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic
                );
                if (pi == null)
                {
                    return;
                }
                var enumType = pi.PropertyType;
                object val = null;
                if (mode.GetType().IsEnum && mode.GetType().Name == enumType.Name)
                {
                    val = mode;
                }
                else if (mode is string s)
                {
                    try
                    {
                        val = Enum.Parse(enumType, s, true);
                    }
                    catch { }
                }
                else if (mode is int i)
                {
                    try
                    {
                        val = Enum.ToObject(enumType, i);
                    }
                    catch { }
                }
                if (val != null)
                {
                    pi.SetValue(tv, val);
                }
            }
            catch { }
        }

        private static string DeriveRowKey(MultiColumnTreeView tv, int index, object item)
        {
            try
            {
                if (item != null)
                {
                    var t = item.GetType();
                    var f = t.GetField(
                        "Id",
                        System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.Public
                    );
                    if (f?.FieldType == typeof(string))
                    {
                        var s = f.GetValue(item) as string;
                        if (!string.IsNullOrEmpty(s))
                        {
                            return s;
                        }
                    }
                    var p = t.GetProperty(
                        "Id",
                        System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.Public
                    );
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
                var mi = typeof(MultiColumnTreeView).GetMethod(
                    "GetIdForIndex",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic,
                    null,
                    new[] { typeof(int) },
                    null
                );
                if (mi != null)
                {
                    var id = mi.Invoke(tv, new object[] { index });
                    if (id != null)
                    {
                        return id.ToString();
                    }
                }
            }
            catch { }
            return $"row-{index}";
        }

        public override void ApplyProperties(
            VisualElement element,
            IReadOnlyDictionary<string, object> properties
        )
        {
            if (element is not MultiColumnTreeView tv)
            {
                PropsApplier.Apply(element, properties);
                return;
            }
            var parts = GetState(tv);
            EnsureViewDataKey(tv, properties);
            EnsureDetachHook(tv, parts);
            parts.AdjustmentTracker.Attach(tv, parts, properties);
            parts.ScrollTracker.Attach(tv, parts, properties);
            parts.LayoutTracker.Attach(tv, parts, properties);
            Delegate layoutCallback = null;
            if (
                properties != null
                && properties.TryGetValue("columnLayoutChanged", out var layoutObj)
            )
            {
                layoutCallback = layoutObj as Delegate;
            }
            if (!ReferenceEquals(parts.ColumnLayoutChanged, layoutCallback))
            {
                parts.ColumnLayoutChanged = layoutCallback;
                parts.LastLayoutSnapshot = null;
            }
            if (parts.IsAdjusting || parts.IsScrolling)
            {
                parts.AdjustmentTracker.Reapply(tv, parts, null, properties);
                parts.ScrollTracker.Reapply(tv, parts, null, properties);
                return;
            }
            if (properties != null)
            {
                try
                {
                    var ops = Ruitk.Elements.MultiColumnTreeViewExpansionOps.Instance;
                    parts.ExpansionTracker.Attach(tv, parts, properties, ops);
                }
                catch { }

                if (properties.TryGetValue("rootItems", out var r))
                {
                    var nextList = r as IList;
                    if (!ReferenceEquals(parts.LastRoot, nextList))
                    {
                        parts.LastRoot = nextList;
                        SetRootItems(tv, nextList);
                        try
                        {
                            tv.Rebuild();
                        }
                        catch { }
                        try
                        {
                            var ops = Ruitk
                                .Elements
                                .MultiColumnTreeViewExpansionOps
                                .Instance;
                            parts.ExpansionTracker.Reapply(tv, parts, null, properties, ops);
                        }
                        catch { }
                        parts.LayoutTracker.Reapply(tv, parts, null, properties);
                    }
                }
                TryApplyProp<float>(properties, "fixedItemHeight", f => tv.fixedItemHeight = f);
                if (properties.TryGetValue("selectionType", out var sel) && sel is SelectionType st)
                {
                    tv.selectionType = st;
                }
                TryApplyProp<int>(properties, "selectedIndex", i => tv.selectedIndex = i);
                TryApplyProp<object>(properties, "sortingMode", m => ApplySortingMode(tv, m));
            }

            if (
                properties.TryGetValue("columns", out var cols)
                && cols is IEnumerable<Dictionary<string, object>> list
            )
            {
                var sig = new List<ColumnSignature>();
                var fns = new List<RowRenderer>();
                foreach (var c in list)
                {
                    c.TryGetValue("name", out var n);
                    c.TryGetValue("title", out var t);
                    c.TryGetValue("cell", out var cell);
                    sig.Add(new ColumnSignature { Name = n as string, Title = t as string });
                    fns.Add(cell as RowRenderer);
                }
                bool same = parts.ColSig != null && parts.ColSig.Count == sig.Count;
                if (same)
                {
                    for (int i = 0; i < sig.Count; i++)
                    {
                        var a = parts.ColSig[i];
                        var b = sig[i];
                        if (!string.Equals(a.Name, b.Name))
                        {
                            same = false;
                            break;
                        }
                        if (!string.Equals(a.Title, b.Title))
                        {
                            same = false;
                            break;
                        }
                    }
                }
                parts.ColSig = sig;
                parts.CellFns = fns;
                if (!same)
                {
                    RebuildColumnsPreservingState(tv, list, parts);

                    parts.LayoutTracker.Reapply(tv, parts, null, properties);
                }
            }

            ApplySlots(tv, properties);
            parts.LayoutTracker.Attach(tv, parts, properties);
            parts.LayoutTracker.Reapply(tv, parts, null, properties);
            DispatchLayoutChanged(tv, parts);
            parts.SortTracker.Attach(tv, parts, properties);
            parts.SortTracker.Reapply(tv, parts, null, properties);
            PropsApplier.Apply(element, properties);
        }

        public override void ApplyPropertiesDiff(
            VisualElement element,
            IReadOnlyDictionary<string, object> previous,
            IReadOnlyDictionary<string, object> next
        )
        {
            if (element is not MultiColumnTreeView tv)
            {
                PropsApplier.ApplyDiff(element, previous, next);
                return;
            }
            previous ??= s_emptyProps;
            next ??= s_emptyProps;
            var parts = GetState(tv);
            EnsureViewDataKey(tv, next);
            EnsureDetachHook(tv, parts);
            parts.AdjustmentTracker.Attach(tv, parts, next);
            parts.ScrollTracker.Attach(tv, parts, next);
            parts.LayoutTracker.Attach(tv, parts, next);
            Delegate layoutCallback = null;
            if (next != null && next.TryGetValue("columnLayoutChanged", out var layoutObj))
            {
                layoutCallback = layoutObj as Delegate;
            }
            if (!ReferenceEquals(parts.ColumnLayoutChanged, layoutCallback))
            {
                parts.ColumnLayoutChanged = layoutCallback;
                parts.LastLayoutSnapshot = null;
            }
            if (parts.IsAdjusting || parts.IsScrolling)
            {
                parts.AdjustmentTracker.Reapply(tv, parts, previous, next);
                parts.ScrollTracker.Reapply(tv, parts, previous, next);
                return;
            }

            try
            {
                var ops = Ruitk.Elements.MultiColumnTreeViewExpansionOps.Instance;
                parts.ExpansionTracker.Attach(tv, parts, next, ops);
            }
            catch { }

            previous.TryGetValue("rootItems", out var pr);
            next.TryGetValue("rootItems", out var nr);
            if (!ReferenceEquals(pr, nr))
            {
                parts.LastRoot = nr as IList;
                SetRootItems(tv, nr);
                try
                {
                    tv.Rebuild();
                }
                catch { }
                try
                {
                    var ops = Ruitk.Elements.MultiColumnTreeViewExpansionOps.Instance;
                    parts.ExpansionTracker.Reapply(tv, parts, previous, next, ops);
                }
                catch { }
                parts.LayoutTracker.Reapply(tv, parts, previous, next);
            }
            if (next.TryGetValue("selectionType", out var sel) && sel is SelectionType st)
            {
                tv.selectionType = st;
            }
            TryDiffProp<int>(previous, next, "selectedIndex", i => tv.selectedIndex = i);
            TryDiffProp<object>(previous, next, "sortingMode", m => ApplySortingMode(tv, m));

            previous.TryGetValue("columns", out var pc);
            next.TryGetValue("columns", out var nc);
            if (!ReferenceEquals(pc, nc) && nc is IEnumerable<Dictionary<string, object>> list)
            {
                var (sig, fns) = ColumnSignatureUtil.Extract(list);
                parts.ColSig = sig;
                parts.CellFns = fns;
                RebuildColumnsPreservingState(tv, list, parts);

                parts.LayoutTracker.Reapply(tv, parts, previous, next);
            }
            ApplySlotsDiff(tv, previous, next);

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
                try
                {
                    var ops = Ruitk.Elements.MultiColumnTreeViewExpansionOps.Instance;
                    parts.ExpansionTracker.Reapply(tv, parts, previous, next, ops);
                }
                catch { }
            }

            parts.LayoutTracker.Reapply(tv, parts, previous, next);
            DispatchLayoutChanged(tv, parts);
            parts.SortTracker.Reapply(tv, parts, previous, next);
            parts.ScrollTracker.Reapply(tv, parts, previous, next);
            PropsApplier.ApplyDiff(element, previous, next);
            try
            {
                var ops = Ruitk.Elements.MultiColumnTreeViewExpansionOps.Instance;
                parts.ExpansionTracker.Reapply(tv, parts, previous, next, ops);
            }
            catch { }
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

        private static void RebuildColumnsPreservingState(
            MultiColumnTreeView tv,
            IEnumerable<Dictionary<string, object>> cols,
            Cached parts
        )
        {
            tv.columns.Clear();

            var colList = cols?.ToList() ?? new List<Dictionary<string, object>>();
            var withKey = new List<(Dictionary<string, object> c, string name, int orig)>();
            for (int i = 0; i < colList.Count; i++)
            {
                var c = colList[i];
                object nObj = null;
                if (c != null)
                {
                    c.TryGetValue("name", out nObj);
                }
                var name = nObj as string;
                withKey.Add((c, string.IsNullOrEmpty(name) ? null : name, i));
            }

            if (parts?.ColumnDisplayIndex != null && parts.ColumnDisplayIndex.Count > 0)
            {
                withKey.Sort(
                    (a, b) =>
                    {
                        bool ah =
                            a.name != null
                            && parts.ColumnDisplayIndex.TryGetValue(a.name, out var ai);
                        bool bh =
                            b.name != null
                            && parts.ColumnDisplayIndex.TryGetValue(b.name, out var bi);
                        if (ah && bh)
                        {
                            var aiVal = parts.ColumnDisplayIndex[a.name];
                            var biVal = parts.ColumnDisplayIndex[b.name];
                            int cmp = aiVal.CompareTo(biVal);
                            if (cmp != 0)
                            {
                                return cmp;
                            }
                            return a.orig.CompareTo(b.orig);
                        }
                        if (ah && !bh)
                        {
                            return -1;
                        }
                        if (!ah && bh)
                        {
                            return 1;
                        }
                        return a.orig.CompareTo(b.orig);
                    }
                );
            }

            var cellFnByName = new Dictionary<string, RowRenderer>();
            if (
                parts?.ColSig != null
                && parts?.CellFns != null
                && parts.ColSig.Count == parts.CellFns.Count
            )
            {
                for (int i = 0; i < parts.ColSig.Count; i++)
                {
                    var keyName = parts.ColSig[i].Name;
                    if (!string.IsNullOrEmpty(keyName))
                    {
                        var fn = parts.CellFns[i];
                        if (fn != null)
                        {
                            cellFnByName[keyName] = fn;
                        }
                    }
                }
            }

            int idx = 0;
            foreach (var entry in withKey)
            {
                var c = entry.c;
                c.TryGetValue("title", out var t);
                c.TryGetValue("name", out var n);
                var col = new Column { title = t as string };
                if (n is string ns && !string.IsNullOrEmpty(ns))
                {
                    col.name = ns;
                }
                else if (string.IsNullOrEmpty(col.name))
                {
                    col.name = $"col{idx}";
                }
                if (c.TryGetValue("width", out var w))
                {
                    try
                    {
                        col.width = Convert.ToSingle(w);
                    }
                    catch { }
                }
                if (c.TryGetValue("minWidth", out var mw))
                {
                    try
                    {
                        col.minWidth = Convert.ToSingle(mw);
                    }
                    catch { }
                }
                if (c.TryGetValue("maxWidth", out var xw))
                {
                    try
                    {
                        col.maxWidth = Convert.ToSingle(xw);
                    }
                    catch { }
                }
                if (c.TryGetValue("resizable", out var rz) && rz is bool rb)
                {
                    col.resizable = rb;
                }
                if (c.TryGetValue("stretchable", out var st) && st is bool sb)
                {
                    col.stretchable = sb;
                }
                if (c.TryGetValue("sortable", out var so) && so is bool srt)
                {
                    col.sortable = srt;
                }

                col.makeCell = () => new VisualElement();
                int captured = idx;
                col.bindCell = (ve, rowIndex) =>
                {
                    object item = GetItemForRow(tv, rowIndex);
                    var key = DeriveRowKey(tv, rowIndex, item) + "|c=" + captured;
                    if (!parts.Pool.TryGetValue(key, out var entry))
                    {
                        var mount = new VisualElement();
                        try
                        {
                            mount.pickingMode = PickingMode.Ignore;
                        }
                        catch { }
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
                    RowRenderer fn = null;

                    if (
                        !string.IsNullOrEmpty(col.name)
                        && cellFnByName.TryGetValue(col.name, out var byName)
                    )
                    {
                        fn = byName;
                    }
                    else if (parts.CellFns != null && captured < parts.CellFns.Count)
                    {
                        fn = parts.CellFns[captured];
                    }
                    if (fn != null)
                    {
                        var vnode = fn(rowIndex, item);
                        vnode = EnsureVisualElementRoot(vnode, "MultiColumnTreeViewCell");
                        entry.renderer.Render(vnode);
                    }
                };
                col.unbindCell = (ve, i) =>
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

                if (
                    !string.IsNullOrEmpty(col.name)
                    && parts.ColumnWidths != null
                    && parts.ColumnWidths.TryGetValue(col.name, out var savedW)
                )
                {
                    try
                    {
                        col.width = savedW;
                    }
                    catch { }
                }

                if (
                    !string.IsNullOrEmpty(col.name)
                    && parts.ColumnVisibility != null
                    && parts.ColumnVisibility.TryGetValue(col.name, out var isVisible)
                )
                {
                    try
                    {
                        col.visible = isVisible;
                    }
                    catch { }
                }
                tv.columns.Add(col);
                idx++;
            }
            try
            {
                tv.Rebuild();
            }
            catch { }
            try
            {
                var ops = Ruitk.Elements.MultiColumnTreeViewExpansionOps.Instance;
                parts.ExpansionTracker.Reapply(tv, parts, null, null, ops);
            }
            catch { }
        }

        private static void ApplySlots(
            MultiColumnTreeView tv,
            IReadOnlyDictionary<string, object> properties
        )
        {
            if (properties == null)
            {
                return;
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
        }

        private static void ApplySlotsDiff(
            MultiColumnTreeView tv,
            IReadOnlyDictionary<string, object> previous,
            IReadOnlyDictionary<string, object> next
        )
        {
            previous ??= s_emptyProps;
            next ??= s_emptyProps;
            previous.TryGetValue("contentContainer", out var pcc);
            next.TryGetValue("contentContainer", out var ncc);
            if (!ReferenceEquals(pcc, ncc) && ncc is Dictionary<string, object> ccMap)
            {
                PropsApplier.Apply(tv.contentContainer, ccMap);
            }
            previous.TryGetValue("scrollView", out var psv);
            next.TryGetValue("scrollView", out var nsv);
            if (!ReferenceEquals(psv, nsv) && nsv is Dictionary<string, object> svMap)
            {
                var scroll = tv.Q<ScrollView>();
                if (scroll != null)
                {
                    PropsApplier.Apply(scroll, svMap);
                }
            }
        }

        private static void ApplyAdjustmentFlush(
            MultiColumnTreeView tv,
            Cached parts,
            IReadOnlyDictionary<string, object> prev,
            IReadOnlyDictionary<string, object> next
        )
        {
            if (tv == null || parts == null)
            {
                return;
            }

            parts.PendingCommit = next ?? parts.PendingCommit;
            ScheduleCommit(tv, parts);
        }

        private static void ScheduleCommit(MultiColumnTreeView tv, Cached parts)
        {
            if (tv == null || parts == null)
            {
                return;
            }
            if (parts.CommitQueued)
            {
                return;
            }
            parts.CommitQueued = true;
            try
            {
                tv.schedule?.Execute(() => RunCommit(tv, parts))?.ExecuteLater(0);
            }
            catch
            {
                try
                {
                    RunCommit(tv, parts);
                }
                catch { }
            }
        }

        private static void RunCommit(MultiColumnTreeView tv, Cached parts)
        {
            parts.CommitQueued = false;
            if (tv == null || parts == null)
            {
                return;
            }
            if (parts.IsCommitting)
            {
                return;
            }
            var n = parts.PendingCommit;
            parts.PendingCommit = null;
            if (n == null)
            {
                return;
            }
            parts.IsCommitting = true;
            try
            {
                if (n.TryGetValue("rootItems", out var nr))
                {
                    var nextList = nr as IList;
                    if (!ReferenceEquals(parts.LastRoot, nextList))
                    {
                        parts.LastRoot = nextList;
                        SetRootItems(tv, nextList);
                        try
                        {
                            tv.Rebuild();
                        }
                        catch { }
                    }
                }

                if (
                    n.TryGetValue("columns", out var colsObj)
                    && colsObj is IEnumerable<Dictionary<string, object>> list
                )
                {
                    var (sig, fns) = ColumnSignatureUtil.Extract(list);
                    bool changed =
                        parts.ColSig == null || !ColumnSignatureUtil.Equal(parts.ColSig, sig);
                    parts.ColSig = sig;
                    parts.CellFns = fns;
                    if (changed)
                    {
                        RebuildColumnsPreservingState(tv, list, parts);
                    }
                }

                parts.LayoutTracker.Reapply(tv, parts, null, n);
                DispatchLayoutChanged(tv, parts);

                if (n.TryGetValue("fixedItemHeight", out var fih) && fih is float fh)
                {
                    tv.fixedItemHeight = fh;
                }
                if (n.TryGetValue("selectionType", out var st) && st is SelectionType sel)
                {
                    tv.selectionType = sel;
                }
                if (n.TryGetValue("selectedIndex", out var si) && si is int idx)
                {
                    tv.selectedIndex = idx;
                }
                if (n.TryGetValue("sortingMode", out var sm))
                {
                    ApplySortingMode(tv, sm);
                }

                ApplySlotsDiff(tv, new Dictionary<string, object>(), n);

                try
                {
                    var ops = Ruitk.Elements.MultiColumnTreeViewExpansionOps.Instance;
                    parts.ExpansionTracker.Reapply(tv, parts, null, n, ops);
                }
                catch { }

                parts.SortTracker.Reapply(tv, parts, null, n);
                parts.ScrollTracker.Reapply(tv, parts, null, n);
            }
            catch { }
            finally
            {
                parts.IsCommitting = false;
            }
        }

        private static void EnsureViewDataKey(
            MultiColumnTreeView view,
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

        private static void EnsureDetachHook(MultiColumnTreeView tv, Cached parts)
        {
            if (tv == null || parts == null || parts.DetachWired)
            {
                return;
            }
            parts.DetachWired = true;
            Ruitk.Core.PanelDetachGuard.Wire(
                tv,
                () =>
                {
                    try
                    {
                        parts.AdjustmentTracker.Detach(tv, parts);
                    }
                    catch { }
                    try
                    {
                        parts.SortTracker.Detach(tv, parts);
                    }
                    catch { }
                    try
                    {
                        parts.LayoutTracker.Detach(tv, parts);
                    }
                    catch { }
                }
            );
        }
    }
}
