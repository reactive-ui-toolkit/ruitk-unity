using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Ruitk.Core;
using Ruitk.Props.Typed;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    internal sealed class MultiColumnSortTracker<TView, TState>
        : IElementStateTracker<TView, TState>
        where TView : UnityEngine.UIElements.VisualElement
        where TState : class, ISortState
    {
        public void Attach(TView tv, TState state, IReadOnlyDictionary<string, object> props)
        {
            if (props != null && props.TryGetValue("sortedColumns", out var sortedObj))
            {
                var fromProps = CoerceSorted(sortedObj);
                if (fromProps != null)
                {
                    state.SortedColumns = fromProps;
                }
            }

            if (props != null && props.TryGetValue("columnSortingChanged", out var user))
            {
                state.UserSortNotify = user as Delegate;
            }

            if (state.InternalSortHandler == null)
            {
                state.InternalSortHandler = () =>
                {
                    List<(string name, SortDirection direction, int index)> snap = null;
                    try
                    {
                        snap = SnapshotSorted(tv);
                    }
                    catch { }
                    snap ??= new List<(string, SortDirection, int)>();
                    var prev = state.SortedColumns ?? new List<(string, SortDirection, int)>();
                    bool changed = !SortedEqual(prev, snap);
                    state.SortedColumns = snap;
                    if (!changed)
                    {
                        return;
                    }
                    try
                    {
                        if (state.UserSortNotify != null)
                        {
                            var defs = ToSortedDefs(state.SortedColumns);
#if UNITY_EDITOR
                            UnityEditor.EditorApplication.delayCall += () =>
                            {
                                try
                                {
                                    DispatchUserNotify(tv, state.UserSortNotify, defs);
                                }
                                catch { }
                            };
#else
                            try
                            {
                                tv?.schedule?.Execute(() =>
                                    {
                                        try
                                        {
                                            DispatchUserNotify(tv, state.UserSortNotify, defs);
                                        }
                                        catch { }
                                    })
                                    ?.ExecuteLater(0);
                            }
                            catch
                            {
                                try
                                {
                                    DispatchUserNotify(tv, state.UserSortNotify, defs);
                                }
                                catch { }
                            }
#endif
                        }
                    }
                    catch { }
                };
                try
                {
                    var ev = tv.GetType()
                        .GetEvent(
                            "columnSortingChanged",
                            System.Reflection.BindingFlags.Instance
                                | System.Reflection.BindingFlags.Public
                                | System.Reflection.BindingFlags.NonPublic
                        );
                    ev?.AddEventHandler(tv, state.InternalSortHandler);
                }
                catch { }
            }
        }

        public void Detach(TView tv, TState state) { }

        public void Reapply(
            TView tv,
            TState state,
            IReadOnlyDictionary<string, object> previousProps,
            IReadOnlyDictionary<string, object> nextProps
        )
        {
            if (tv == null || state == null)
            {
                return;
            }

            if (nextProps != null && nextProps.TryGetValue("sortedColumns", out var sortedObj))
            {
                var fromProps = CoerceSorted(sortedObj);
                if (fromProps != null)
                {
                    state.SortedColumns = fromProps;
                }
            }

            if (nextProps != null && nextProps.TryGetValue("columnSortingChanged", out var user))
            {
                state.UserSortNotify = user as Delegate;
            }

            try
            {
                var desired = state.SortedColumns ?? new List<(string, SortDirection, int)>();
                var current = SnapshotSorted(tv);
                bool differs = !SortedEqual(current, desired);
                var scdProp = tv.GetType()
                    .GetProperty(
                        "sortColumnDescriptions",
                        System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.NonPublic
                    );
                var scd = scdProp?.GetValue(tv);
                if (differs && scd != null)
                {
                    try
                    {
                        scd.GetType().GetMethod("Clear")?.Invoke(scd, null);
                    }
                    catch { }
                    foreach (var item in desired.OrderBy(x => x.index))
                    {
                        if (string.IsNullOrEmpty(item.name))
                        {
                            continue;
                        }
                        var desc = new SortColumnDescription(item.name, item.direction);
                        try
                        {
                            scd.GetType().GetMethod("Add")?.Invoke(scd, new object[] { desc });
                        }
                        catch { }
                    }
                }
            }
            catch { }

            try
            {
                state.SortedColumns = SnapshotSorted(tv);
            }
            catch { }
        }

        private static List<(string name, SortDirection direction, int index)> CoerceSorted(
            object obj
        )
        {
            if (obj == null)
            {
                return null;
            }
            var list = new List<(string, SortDirection, int)>();
            try
            {
                if (obj is IEnumerable en)
                {
                    int i = 0;
                    foreach (var it in en)
                    {
                        if (it is IDictionary<string, object> map)
                        {
                            map.TryGetValue("name", out var n);
                            map.TryGetValue("direction", out var d);
                            map.TryGetValue("index", out var idx);
                            var name = n as string;
                            SortDirection dir = SortDirection.Ascending;
                            if (d is SortDirection sd)
                            {
                                dir = sd;
                            }
                            else if (d is string ds)
                            {
                                if (
                                    string.Equals(
                                        ds,
                                        "Descending",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    dir = SortDirection.Descending;
                                }
                            }
                            int ord = idx is int ii ? ii : i;
                            if (!string.IsNullOrEmpty(name))
                            {
                                list.Add((name, dir, ord));
                            }
                            i++;
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static List<(string name, SortDirection direction, int index)> SnapshotSorted(
            UnityEngine.UIElements.VisualElement tv
        )
        {
            var list = new List<(string, SortDirection, int)>();
            if (tv == null)
            {
                return list;
            }
            try
            {
                var prop = tv.GetType()
                    .GetProperty(
                        "sortedColumns",
                        System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.NonPublic
                    );
                var sorted = prop?.GetValue(tv) as System.Collections.IEnumerable;
                if (sorted != null)
                {
                    int i = 0;
                    foreach (var s in sorted)
                    {
                        try
                        {
                            var t = s.GetType();
                            var name = t.GetProperty("columnName")?.GetValue(s) as string;
                            var dirObj = t.GetProperty("direction")?.GetValue(s);
                            var dir = dirObj is SortDirection sd ? sd : SortDirection.Ascending;
                            if (!string.IsNullOrEmpty(name))
                            {
                                list.Add((name, dir, i));
                            }
                        }
                        catch { }
                        i++;
                    }
                }
            }
            catch { }
            return list;
        }

        private static List<SortedColumnDef> ToSortedDefs(
            List<(string name, SortDirection direction, int index)> src
        )
        {
            var list = new List<SortedColumnDef>();
            if (src == null)
            {
                return list;
            }
            foreach (var (name, direction, index) in src.OrderBy(x => x.index))
            {
                list.Add(
                    new SortedColumnDef
                    {
                        Name = name,
                        Direction = direction,
                        Index = index,
                    }
                );
            }
            return list;
        }

        private static void DispatchUserNotify(
            UnityEngine.UIElements.VisualElement tv,
            Delegate notify,
            List<SortedColumnDef> defs
        )
        {
            if (notify == null)
            {
                return;
            }
            if (notify is ColumnSortEventHandler cseh)
            {
                cseh.Invoke(defs);
                return;
            }
            if (notify is Action<UnityEngine.UIElements.VisualElement, List<SortedColumnDef>> a2)
            {
                a2.Invoke(tv, defs);
                return;
            }
            if (notify is Action<List<SortedColumnDef>> a)
            {
                a.Invoke(defs);
                return;
            }
            if (notify is Action<object> ao)
            {
                ao.Invoke(defs);
                return;
            }
            if (notify is Action g)
            {
                g.Invoke();
            }
        }

        private static bool SortedEqual(
            List<(string name, SortDirection direction, int index)> a,
            List<(string name, SortDirection direction, int index)> b
        )
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null)
            {
                return false;
            }
            if (a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].name, b[i].name, StringComparison.Ordinal))
                {
                    return false;
                }
                if (a[i].direction != b[i].direction)
                {
                    return false;
                }
                if (a[i].index != b[i].index)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
