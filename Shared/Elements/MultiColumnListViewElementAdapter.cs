using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Ruitk.Core;
using Ruitk.Props;
using Ruitk.Props.Typed;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    public sealed class MultiColumnListViewElementAdapter
        : StatefulElementAdapter<MultiColumnListView, MultiColumnListViewElementAdapter.Cached>
    {
        public sealed class Cached
            : ISortState,
                IColumnLayoutState,
                IAdjustmentSuspendState,
                IScrollState
        {
            public IList LastItems;
            internal List<ColumnSignature> LastColSignature;
            public List<RowRenderer> CellFns;

            internal Dictionary<string, (IVNodeHostRenderer renderer, VisualElement mount)> Pool =
                new();

            public List<(
                string name,
                SortDirection direction,
                int index
            )> SortedColumns { get; set; } = new();
            public Delegate UserSortNotify { get; set; }
            public Action InternalSortHandler { get; set; }
            public IElementStateTracker<MultiColumnListView, Cached> SortTracker =
                new MultiColumnSortTracker<MultiColumnListView, Cached>();

            public Dictionary<string, float> ColumnWidths { get; set; } = new();
            public Dictionary<string, bool> ColumnVisibility { get; set; } = new();
            public Dictionary<string, int> ColumnDisplayIndex { get; set; } = new();
            public IElementStateTracker<MultiColumnListView, Cached> LayoutTracker =
                new MultiColumnLayoutTracker<MultiColumnListView, Cached>();
            public Delegate ColumnLayoutChanged { get; set; }
            internal ColumnLayoutSnapshot LastLayoutSnapshot { get; set; }

            public bool IsAdjusting { get; set; }
            public bool HeaderWired { get; set; }
            public IReadOnlyDictionary<string, object> PendingPrev { get; set; }
            public IReadOnlyDictionary<string, object> PendingNext { get; set; }
            public IElementStateTracker<MultiColumnListView, Cached> AdjustmentTracker =
                new MultiColumnAdjustmentTracker<MultiColumnListView, Cached>(
                    new MultiColumnHeaderOps<MultiColumnListView>(),
                    ApplyAdjustmentFlush
                );
            public bool DetachWired { get; set; }

            public bool IsScrolling { get; set; }
            public bool ScrollWired { get; set; }
            public float ScrollX { get; set; }
            public float ScrollY { get; set; }
            public int ScrollActivityId { get; set; }
            public IElementStateTracker<MultiColumnListView, Cached> ScrollTracker =
                new MultiColumnScrollTracker<MultiColumnListView, Cached>(
                    new MultiColumnScrollOps<MultiColumnListView>(),
                    ApplyAdjustmentFlush
                );

            public bool CommitQueued { get; set; }
            public bool IsCommitting { get; set; }
            public IReadOnlyDictionary<string, object> PendingCommit { get; set; }
        }

        private static HostContext sharedHostContext;

        private static HostContext GetCellHostContext()
        {
            if (sharedHostContext == null)
            {
                var registry = ElementRegistryProvider.GetDefaultRegistry();
                sharedHostContext = new HostContext(registry);
            }
            return sharedHostContext;
        }

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

        private static VirtualNode EnsureVisualRoot(VirtualNode node)
        {
            if (node == null)
            {
                return null;
            }
            if (
                node.NodeType == VirtualNodeType.Element
                && string.Equals(node.ElementTypeName, "VisualElement", StringComparison.Ordinal)
            )
            {
                return node;
            }
            return new VirtualNode(
                VirtualNodeType.Element,
                elementTypeName: "VisualElement",
                textContent: null,
                key: node.Key,
                properties: new Dictionary<string, object>(0),
                children: new List<VirtualNode> { node }
            );
        }

        private static IList NormalizeItems(object itemsObj)
        {
            if (itemsObj == null)
            {
                return null;
            }
            if (itemsObj is IList il)
            {
                return il;
            }
            if (itemsObj is IEnumerable en)
            {
                var list = new List<object>();
                foreach (var it in en)
                {
                    list.Add(it);
                }
                return list;
            }
            return null;
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

        private static void DispatchLayoutChanged(MultiColumnListView view, Cached parts)
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
            MultiColumnListView view,
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

        public override VisualElement Create() => new MultiColumnListView();

        /// <summary>
        /// Host-removal hook (§5.4 retention cleanup): unmounts every pooled
        /// cell renderer and drops the cached state for the deleted view.
        /// </summary>
        internal static void NotifyHostRemoved(VisualElement element)
        {
            if (element is not MultiColumnListView mclv || !TryGetState(mclv, out var parts))
            {
                return;
            }
            foreach (var entry in parts.Pool.Values)
            {
                entry.renderer?.Unmount();
            }
            parts.Pool.Clear();
            RemoveState(mclv);
        }

        public override void ApplyProperties(
            VisualElement element,
            IReadOnlyDictionary<string, object> properties
        )
        {
            if (element is not MultiColumnListView view || properties == null)
            {
                PropsApplier.Apply(element, properties);
                return;
            }
            var parts = GetState(view);
            EnsureViewDataKey(view, properties);
            EnsureDetachHook(view, parts);
            parts.AdjustmentTracker.Attach(view, parts, properties);
            parts.ScrollTracker.Attach(view, parts, properties);
            parts.LayoutTracker.Attach(view, parts, properties);
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
                parts.AdjustmentTracker.Reapply(view, parts, null, properties);
                parts.ScrollTracker.Reapply(view, parts, null, properties);
                parts.PendingCommit = properties;
                ScheduleCommit(view, parts);
                return;
            }

            if (properties.TryGetValue("items", out var itemsObj))
            {
                var incoming = NormalizeItems(itemsObj);
                if (!ReferenceEquals(parts.LastItems, incoming))
                {
                    parts.LastItems = incoming;
                    view.itemsSource = incoming as IList;
                    try
                    {
                        view.Rebuild();
                    }
                    catch { }
                }
            }
            else if (parts.LastItems != null)
            {
                parts.LastItems = null;
                view.itemsSource = null;
                try
                {
                    view.Rebuild();
                }
                catch { }
            }

            TryApplyProp<int>(properties, "selectedIndex", i => view.selectedIndex = i);
            TryApplyProp<float>(properties, "fixedItemHeight", h => view.fixedItemHeight = h);
            if (
                properties.TryGetValue("selectionType", out var selObj)
                && selObj is SelectionType sel
            )
            {
                view.selectionType = sel;
            }

            TryApplyProp<object>(properties, "sortingMode", m => ApplySortingMode(view, m));

            if (properties.TryGetValue("columns", out var colsObj) && colsObj is IEnumerable cols)
            {
                var (newSig, newFns) = ColumnSignatureUtil.Extract(cols);
                if (!SignaturesEqual(parts.LastColSignature, newSig))
                {
                    parts.CellFns = newFns;
                    parts.LastColSignature = newSig;
                    RebuildColumnsPreservingState(view, cols, parts);
                }
                else
                {
                    parts.CellFns = newFns;
                    try
                    {
                        view.RefreshItems();
                    }
                    catch { }
                }
            }

            ApplySlots(view, properties);

            parts.LayoutTracker.Reapply(view, parts, null, properties);
            DispatchLayoutChanged(view, parts);
            parts.SortTracker.Attach(view, parts, properties);
            parts.SortTracker.Reapply(view, parts, null, properties);
            PropsApplier.Apply(element, properties);
        }

        public override void ApplyPropertiesDiff(
            VisualElement element,
            IReadOnlyDictionary<string, object> previous,
            IReadOnlyDictionary<string, object> next
        )
        {
            if (element is not MultiColumnListView view)
            {
                PropsApplier.ApplyDiff(element, previous, next);
                return;
            }
            previous ??= s_emptyProps;
            next ??= s_emptyProps;

            var parts = GetState(view);
            EnsureViewDataKey(view, next);
            EnsureDetachHook(view, parts);
            parts.AdjustmentTracker.Attach(view, parts, next);
            parts.ScrollTracker.Attach(view, parts, next);
            parts.LayoutTracker.Attach(view, parts, next);
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
                parts.AdjustmentTracker.Reapply(view, parts, previous, next);
                parts.ScrollTracker.Reapply(view, parts, previous, next);
                parts.PendingCommit = next;
                ScheduleCommit(view, parts);
                return;
            }

            previous.TryGetValue("items", out var prevItemsObj);
            next.TryGetValue("items", out var nextItemsObj);
            var prevItems = NormalizeItems(prevItemsObj);
            var nextItems = NormalizeItems(nextItemsObj);
            if (!ReferenceEquals(prevItems, nextItems))
            {
                parts.LastItems = nextItems;
                view.itemsSource = nextItems;
                try
                {
                    view.Rebuild();
                }
                catch { }
            }

            TryDiffProp<int>(previous, next, "selectedIndex", i => view.selectedIndex = i);
            TryDiffProp<float>(previous, next, "fixedItemHeight", h => view.fixedItemHeight = h);
            if (
                next.TryGetValue("selectionType", out var selNext)
                && selNext is SelectionType selType
            )
            {
                if (view.selectionType != selType)
                {
                    view.selectionType = selType;
                }
            }

            TryDiffProp<object>(previous, next, "sortingMode", m => ApplySortingMode(view, m));

            previous.TryGetValue("columns", out var prevCols);
            next.TryGetValue("columns", out var nextCols);
            if (nextCols is IEnumerable ncols)
            {
                var (newSig, newFns) = ColumnSignatureUtil.Extract(ncols);
                if (!SignaturesEqual(parts.LastColSignature, newSig))
                {
                    parts.CellFns = newFns;
                    parts.LastColSignature = newSig;
                    RebuildColumnsPreservingState(view, ncols, parts);
                }
                else
                {
                    parts.CellFns = newFns;
                    try
                    {
                        view.RefreshItems();
                    }
                    catch { }
                }
            }

            ApplySlotsDiff(view, previous, next);

            parts.LayoutTracker.Reapply(view, parts, previous, next);
            DispatchLayoutChanged(view, parts);
            parts.SortTracker.Reapply(view, parts, previous, next);
            parts.ScrollTracker.Reapply(view, parts, previous, next);
            PropsApplier.ApplyDiff(element, previous, next);
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

        private static void ApplySortingMode(MultiColumnListView view, object mode)
        {
            if (view == null || mode == null)
            {
                return;
            }
            try
            {
                var pi = typeof(MultiColumnListView).GetProperty(
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
                    pi.SetValue(view, val);
                }
            }
            catch { }
        }

        private static void RebuildColumnsPreservingState(
            MultiColumnListView view,
            IEnumerable newCols,
            Cached parts
        )
        {
            var existingByName = new Dictionary<string, Column>();
            var existingByIndex = new List<Column>();
            for (int ci = 0; ci < view.columns.Count; ci++)
            {
                var col = view.columns[ci];
                existingByIndex.Add(col);
                if (!string.IsNullOrEmpty(col.name) && !existingByName.ContainsKey(col.name))
                {
                    existingByName[col.name] = col;
                }
            }

            view.columns.Clear();

            var cellFnByName = new Dictionary<string, RowRenderer>();
            if (
                parts?.LastColSignature != null
                && parts?.CellFns != null
                && parts.LastColSignature.Count == parts.CellFns.Count
            )
            {
                for (int i = 0; i < parts.LastColSignature.Count; i++)
                {
                    var keyName = parts.LastColSignature[i].Name;
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

            var normalized =
                new List<(IDictionary<string, object> map, string name, int originalIndex)>();
            int original = 0;
            foreach (var co in newCols)
            {
                if (co is IDictionary<string, object> cm)
                {
                    string nm = null;
                    if (
                        cm.TryGetValue("name", out var nameObj)
                        && nameObj is string ns
                        && !string.IsNullOrEmpty(ns)
                    )
                    {
                        nm = ns;
                    }
                    normalized.Add((cm, nm, original));
                }
                original++;
            }

            if (parts?.ColumnDisplayIndex != null && parts.ColumnDisplayIndex.Count > 0)
            {
                normalized.Sort(
                    (a, b) =>
                    {
                        int ai = 0;
                        int bi = 0;
                        bool aHas =
                            a.name != null && parts.ColumnDisplayIndex.TryGetValue(a.name, out ai);
                        bool bHas =
                            b.name != null && parts.ColumnDisplayIndex.TryGetValue(b.name, out bi);
                        if (aHas && bHas)
                        {
                            var cmp = ai.CompareTo(bi);
                            if (cmp != 0)
                            {
                                return cmp;
                            }
                            return a.originalIndex.CompareTo(b.originalIndex);
                        }
                        if (aHas && !bHas)
                        {
                            return -1;
                        }
                        if (!aHas && bHas)
                        {
                            return 1;
                        }
                        return a.originalIndex.CompareTo(b.originalIndex);
                    }
                );
            }

            int index = 0;
            int colIndex = 0;
            foreach (var entry in normalized)
            {
                var colMap = entry.map;
                if (colMap == null)
                {
                    index++;
                    continue;
                }
                string name = entry.name;
                string title = colMap.TryGetValue("title", out var t) ? t as string : null;
                RowRenderer cellFn = null;
                if (colMap.TryGetValue("cell", out var c) && c is RowRenderer cf)
                {
                    cellFn = cf;
                }
                float width = 0f;
                bool widthProvided = false;
                if (colMap.TryGetValue("width", out var w))
                {
                    try
                    {
                        width = Convert.ToSingle(w);
                        widthProvided = true;
                    }
                    catch
                    {
                        width = 0f;
                    }
                }
                float minWidth = 0f;
                bool minWidthProvided = false;
                if (colMap.TryGetValue("minWidth", out var mw))
                {
                    try
                    {
                        minWidth = Convert.ToSingle(mw);
                        minWidthProvided = true;
                    }
                    catch { }
                }
                float maxWidth = 0f;
                bool maxWidthProvided = false;
                if (colMap.TryGetValue("maxWidth", out var xw))
                {
                    try
                    {
                        maxWidth = Convert.ToSingle(xw);
                        maxWidthProvided = true;
                    }
                    catch
                    {
                        maxWidth = 0f;
                    }
                }
                bool resizable = true;
                bool resizableProvided = false;
                if (colMap.TryGetValue("resizable", out var rz) && rz is bool rb)
                {
                    resizable = rb;
                    resizableProvided = true;
                }
                bool stretchable = true;
                bool stretchableProvided = false;
                if (colMap.TryGetValue("stretchable", out var st) && st is bool sb)
                {
                    stretchable = sb;
                    stretchableProvided = true;
                }

                var column = new Column { title = title };
                if (!string.IsNullOrEmpty(name))
                {
                    column.name = name;
                }

                Column prev = null;
                if (!string.IsNullOrEmpty(name))
                {
                    existingByName.TryGetValue(name, out prev);
                }
                if (prev == null && index < existingByIndex.Count)
                {
                    prev = existingByIndex[index];
                }

                if (widthProvided)
                {
                    column.width = width;
                }
                else if (prev != null)
                {
                    column.width = prev.width;
                }
                if (minWidthProvided)
                {
                    column.minWidth = minWidth;
                }
                else if (prev != null)
                {
                    column.minWidth = prev.minWidth;
                }
                if (maxWidthProvided)
                {
                    column.maxWidth = maxWidth;
                }
                else if (prev != null)
                {
                    column.maxWidth = prev.maxWidth;
                }
                if (resizableProvided)
                {
                    column.resizable = resizable;
                }
                else if (prev != null)
                {
                    column.resizable = prev.resizable;
                }
                if (stretchableProvided)
                {
                    column.stretchable = stretchable;
                }
                else if (prev != null)
                {
                    column.stretchable = prev.stretchable;
                }
                if (colMap.TryGetValue("sortable", out var so) && so is bool srt)
                {
                    column.sortable = srt;
                }

                column.makeCell = () => new VisualElement();
                int capturedIndex = colIndex;
                column.bindCell = (ve, rowIndex) =>
                {
                    object item = null;
                    if (view.itemsSource is IList il && rowIndex >= 0 && rowIndex < il.Count)
                    {
                        item = il[rowIndex];
                    }

                    var rowKey = DeriveRowKeyList(view, rowIndex, item);
                    var key = rowKey + "|c=" + capturedIndex;
                    if (!parts.Pool.TryGetValue(key, out var entry))
                    {
                        var mount = new VisualElement();
                        try
                        {
                            mount.pickingMode = PickingMode.Ignore;
                        }
                        catch { }
                        var rrNew = new VNodeHostRenderer(GetCellHostContext(), mount);
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

                    RowRenderer activeFn = null;
                    if (
                        !string.IsNullOrEmpty(column.name)
                        && cellFnByName.TryGetValue(column.name, out var byName)
                    )
                    {
                        activeFn = byName;
                    }
                    else
                    {
                        var fnList = parts.CellFns;
                        if (fnList != null && capturedIndex >= 0 && capturedIndex < fnList.Count)
                        {
                            activeFn = fnList[capturedIndex];
                        }
                    }
                    if (activeFn != null)
                    {
                        var vnode = EnsureVisualRoot(activeFn(rowIndex, item));
                        entry.renderer.Render(vnode);
                    }
                };

                column.unbindCell = (ve, i) =>
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
                    !string.IsNullOrEmpty(column.name)
                    && parts.ColumnWidths != null
                    && parts.ColumnWidths.TryGetValue(column.name, out var savedW)
                )
                {
                    try
                    {
                        column.width = savedW;
                    }
                    catch { }
                }

                if (
                    !string.IsNullOrEmpty(column.name)
                    && parts.ColumnVisibility != null
                    && parts.ColumnVisibility.TryGetValue(column.name, out var isVisible)
                )
                {
                    try
                    {
                        column.visible = isVisible;
                    }
                    catch { }
                }

                view.columns.Add(column);
                index++;
                colIndex++;
            }

            try
            {
                view.Rebuild();
            }
            catch { }
        }

        private static bool SignaturesEqual(List<ColumnSignature> a, List<ColumnSignature> b)
        {
            return ColumnSignatureUtil.Equal(a, b);
        }

        private static void ApplySlots(
            MultiColumnListView view,
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
                PropsApplier.Apply(view.contentContainer, ccMap);
            }
            if (
                properties.TryGetValue("scrollView", out var sv)
                && sv is Dictionary<string, object> svMap
            )
            {
                var scroll = view.Q<ScrollView>();
                if (scroll != null)
                {
                    PropsApplier.Apply(scroll, svMap);
                }
            }
        }

        private static void ApplySlotsDiff(
            MultiColumnListView view,
            IReadOnlyDictionary<string, object> previous,
            IReadOnlyDictionary<string, object> next
        )
        {
            previous ??= s_emptyProps;
            next ??= s_emptyProps;
            previous.TryGetValue("contentContainer", out var prevCC);
            next.TryGetValue("contentContainer", out var nextCC);
            if (!ReferenceEquals(prevCC, nextCC) && nextCC is Dictionary<string, object> ccMap)
            {
                PropsApplier.Apply(view.contentContainer, ccMap);
            }
            previous.TryGetValue("scrollView", out var prevSV);
            next.TryGetValue("scrollView", out var nextSV);
            if (!ReferenceEquals(prevSV, nextSV) && nextSV is Dictionary<string, object> svMap)
            {
                var scroll = view.Q<ScrollView>();
                if (scroll != null)
                {
                    PropsApplier.Apply(scroll, svMap);
                }
            }
        }

        private static void ApplyAdjustmentFlush(
            MultiColumnListView view,
            Cached parts,
            IReadOnlyDictionary<string, object> prev,
            IReadOnlyDictionary<string, object> next
        )
        {
            if (view == null || parts == null)
            {
                return;
            }
            parts.PendingCommit = next ?? parts.PendingCommit;
            ScheduleCommit(view, parts);
        }

        private static void ScheduleCommit(MultiColumnListView view, Cached parts)
        {
            if (view == null || parts == null)
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
                view.schedule?.Execute(() => RunCommit(view, parts))?.ExecuteLater(0);
            }
            catch
            {
                try
                {
                    RunCommit(view, parts);
                }
                catch { }
            }
        }

        private static void RunCommit(MultiColumnListView view, Cached parts)
        {
            parts.CommitQueued = false;
            if (view == null || parts == null)
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
                if (n.TryGetValue("items", out var nextItemsObj))
                {
                    var nextItems = NormalizeItems(nextItemsObj);
                    if (!ReferenceEquals(parts.LastItems, nextItems))
                    {
                        parts.LastItems = nextItems;
                        view.itemsSource = nextItems as IList;
                        try
                        {
                            view.Rebuild();
                        }
                        catch { }
                    }
                }

                if (n.TryGetValue("columns", out var nextCols) && nextCols is IEnumerable ncols)
                {
                    var (newSig, newFns) = ColumnSignatureUtil.Extract(ncols);
                    bool changed = !SignaturesEqual(parts.LastColSignature, newSig);
                    parts.CellFns = newFns;
                    parts.LastColSignature = newSig;
                    if (changed)
                    {
                        RebuildColumnsPreservingState(view, ncols, parts);
                    }
                    else
                    {
                        try
                        {
                            view.RefreshItems();
                        }
                        catch { }
                    }
                }

                parts.LayoutTracker.Reapply(view, parts, null, n);
                DispatchLayoutChanged(view, parts);

                if (n.TryGetValue("fixedItemHeight", out var fv) && fv is float ff)
                {
                    view.fixedItemHeight = ff;
                }
                if (n.TryGetValue("selectionType", out var selObj) && selObj is SelectionType sel)
                {
                    view.selectionType = sel;
                }
                if (n.TryGetValue("selectedIndex", out var si) && si is int idx)
                {
                    view.selectedIndex = idx;
                }
                if (n.TryGetValue("sortingMode", out var sm))
                {
                    ApplySortingMode(view, sm);
                }

                ApplySlotsDiff(view, new Dictionary<string, object>(), n);

                parts.SortTracker.Reapply(view, parts, null, n);
                parts.ScrollTracker.Reapply(view, parts, null, n);
            }
            catch { }
            finally
            {
                parts.IsCommitting = false;
            }
        }

        private static string DeriveRowKeyList(MultiColumnListView view, int index, object item)
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
            return $"row-{index}";
        }

        private static void EnsureDetachHook(MultiColumnListView view, Cached parts)
        {
            if (view == null || parts == null || parts.DetachWired)
            {
                return;
            }
            parts.DetachWired = true;
            Ruitk.Core.PanelDetachGuard.Wire(
                view,
                () =>
                {
                    try
                    {
                        parts.AdjustmentTracker.Detach(view, parts);
                    }
                    catch { }
                    try
                    {
                        parts.SortTracker.Detach(view, parts);
                    }
                    catch { }
                    try
                    {
                        parts.LayoutTracker.Detach(view, parts);
                    }
                    catch { }
                    try
                    {
                        parts.ScrollTracker.Detach(view, parts);
                    }
                    catch { }
                }
            );
        }

        private static void EnsureViewDataKey(
            MultiColumnListView view,
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
