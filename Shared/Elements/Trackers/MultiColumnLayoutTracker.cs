using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine.UIElements;

namespace Ruitk.Elements
{
    internal sealed class MultiColumnLayoutTracker<TView, TState>
        : IElementStateTracker<TView, TState>
        where TView : UnityEngine.UIElements.VisualElement
        where TState : class, IColumnLayoutState
    {
        private static System.Collections.Generic.IEnumerable<Column> GetColumns(TView tv)
        {
            var list = new System.Collections.Generic.List<Column>();
            try
            {
                var prop = tv.GetType()
                    .GetProperty(
                        "columns",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    );
                var val = prop?.GetValue(tv);
                if (val is System.Collections.IEnumerable en)
                {
                    foreach (var it in en)
                    {
                        if (it is Column c)
                        {
                            list.Add(c);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private static Dictionary<string, float> CaptureCurrentWidths(TView tv)
        {
            var map = new Dictionary<string, float>();
            if (tv == null)
            {
                return map;
            }
            try
            {
                foreach (var col in GetColumns(tv))
                {
                    if (col == null)
                    {
                        continue;
                    }
                    var name = string.IsNullOrEmpty(col.name) ? null : col.name;
                    if (name == null)
                    {
                        continue;
                    }
                    try
                    {
                        map[name] = col.width.value;
                    }
                    catch { }
                }
            }
            catch { }
            return map;
        }

        private static Dictionary<string, bool> CaptureCurrentVisibility(TView tv)
        {
            var map = new Dictionary<string, bool>();
            if (tv == null)
            {
                return map;
            }
            try
            {
                foreach (var col in GetColumns(tv))
                {
                    if (col == null)
                    {
                        continue;
                    }
                    var name = string.IsNullOrEmpty(col.name) ? null : col.name;
                    if (name == null)
                    {
                        continue;
                    }
                    try
                    {
                        map[name] = col.visible;
                    }
                    catch { }
                }
            }
            catch { }
            return map;
        }

        private static PropertyInfo FindIndexProperty(Column col, bool requireWritable)
        {
            if (col == null)
            {
                return null;
            }
            var t = col.GetType();
            var props = new[] { "visibleIndex", "displayIndex", "logicalIndex", "index" };
            foreach (var name in props)
            {
                var p = t.GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                );
                if (p == null)
                {
                    continue;
                }
                if (requireWritable && !p.CanWrite)
                {
                    continue;
                }
                return p;
            }
            return null;
        }

        private static Dictionary<string, int> CaptureCurrentIndices(TView tv)
        {
            var map = new Dictionary<string, int>();
            if (tv == null)
            {
                return map;
            }
            foreach (var col in GetColumns(tv))
            {
                if (col == null)
                {
                    continue;
                }
                var name = string.IsNullOrEmpty(col.name) ? null : col.name;
                if (name == null)
                {
                    continue;
                }
                int ord = -1;
                try
                {
                    var pi = FindIndexProperty(col, requireWritable: false);
                    if (pi != null)
                    {
                        var v = pi.GetValue(col);
                        if (v is int iv)
                        {
                            ord = iv;
                        }
                    }
                }
                catch { }
                map[name] = ord;
            }
            return map;
        }

        private static void ApplySavedIndices(TView tv, Dictionary<string, int> saved) { }

        public void Attach(TView tv, TState state, IReadOnlyDictionary<string, object> props)
        {
            state.ColumnWidths ??= new Dictionary<string, float>();
            state.ColumnVisibility ??= new Dictionary<string, bool>();
            state.ColumnDisplayIndex ??= new Dictionary<string, int>();

            bool widthsProvided = false;
            bool visibilityProvided = false;
            bool displayProvided = false;

            if (props != null && props.TryGetValue("columnWidths", out var widthsObj))
            {
                widthsProvided = true;
                state.ColumnWidths.Clear();
                if (widthsObj is IDictionary<string, object> map)
                {
                    foreach (var kv in map)
                    {
                        try
                        {
                            state.ColumnWidths[kv.Key] = Convert.ToSingle(kv.Value);
                        }
                        catch { }
                    }
                }
                else if (widthsObj is IDictionary<string, float> fMap)
                {
                    foreach (var kv in fMap)
                    {
                        state.ColumnWidths[kv.Key] = kv.Value;
                    }
                }
                else if (widthsObj is IReadOnlyDictionary<string, float> roMap)
                {
                    foreach (var kv in roMap)
                    {
                        state.ColumnWidths[kv.Key] = kv.Value;
                    }
                }
            }

            if (props != null && props.TryGetValue("columnVisibility", out var visibilityObj))
            {
                visibilityProvided = true;
                state.ColumnVisibility.Clear();
                if (visibilityObj is IDictionary<string, object> map)
                {
                    foreach (var kv in map)
                    {
                        try
                        {
                            state.ColumnVisibility[kv.Key] = Convert.ToBoolean(kv.Value);
                        }
                        catch { }
                    }
                }
                else if (visibilityObj is IDictionary<string, bool> bMap)
                {
                    foreach (var kv in bMap)
                    {
                        state.ColumnVisibility[kv.Key] = kv.Value;
                    }
                }
                else if (visibilityObj is IReadOnlyDictionary<string, bool> roMap)
                {
                    foreach (var kv in roMap)
                    {
                        state.ColumnVisibility[kv.Key] = kv.Value;
                    }
                }
            }

            if (props != null && props.TryGetValue("columnDisplayIndex", out var displayObj))
            {
                displayProvided = true;
                state.ColumnDisplayIndex.Clear();
                if (displayObj is IDictionary<string, object> map)
                {
                    foreach (var kv in map)
                    {
                        try
                        {
                            state.ColumnDisplayIndex[kv.Key] = Convert.ToInt32(kv.Value);
                        }
                        catch { }
                    }
                }
                else if (displayObj is IDictionary<string, int> iMap)
                {
                    foreach (var kv in iMap)
                    {
                        state.ColumnDisplayIndex[kv.Key] = kv.Value;
                    }
                }
                else if (displayObj is IReadOnlyDictionary<string, int> roMap)
                {
                    foreach (var kv in roMap)
                    {
                        state.ColumnDisplayIndex[kv.Key] = kv.Value;
                    }
                }
            }

            if (!widthsProvided && (state.ColumnWidths == null || state.ColumnWidths.Count == 0))
            {
                state.ColumnWidths = CaptureCurrentWidths(tv);
            }
            if (
                !visibilityProvided
                && (state.ColumnVisibility == null || state.ColumnVisibility.Count == 0)
            )
            {
                state.ColumnVisibility = CaptureCurrentVisibility(tv);
            }
            if (
                !displayProvided
                && (state.ColumnDisplayIndex == null || state.ColumnDisplayIndex.Count == 0)
            )
            {
                state.ColumnDisplayIndex = CaptureCurrentIndices(tv);
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

            try
            {
                var widths = CaptureCurrentWidths(tv);
                if (widths != null && widths.Count > 0)
                {
                    foreach (var kv in widths)
                    {
                        state.ColumnWidths[kv.Key] = kv.Value;
                    }
                }
                var vis = CaptureCurrentVisibility(tv);
                if (vis != null && vis.Count > 0)
                {
                    foreach (var kv in vis)
                    {
                        state.ColumnVisibility[kv.Key] = kv.Value;
                    }
                }
            }
            catch { }

            foreach (var col in GetColumns(tv))
            {
                if (col == null)
                {
                    continue;
                }
                var name = col.name;
                if (!string.IsNullOrEmpty(name))
                {
                    if (
                        state.ColumnWidths != null
                        && state.ColumnWidths.TryGetValue(name, out var w)
                    )
                    {
                        try
                        {
                            col.width = w;
                        }
                        catch { }
                    }
                    if (
                        state.ColumnVisibility != null
                        && state.ColumnVisibility.TryGetValue(name, out var vv)
                    )
                    {
                        try
                        {
                            col.visible = vv;
                        }
                        catch { }
                    }
                }
            }

            var indicesNow = CaptureCurrentIndices(tv);
            bool differ =
                (state.ColumnDisplayIndex?.Count ?? 0) != (indicesNow?.Count ?? 0)
                || (state.ColumnDisplayIndex ?? new Dictionary<string, int>()).Any(kv =>
                    !indicesNow.TryGetValue(kv.Key, out var v) || v != kv.Value
                );
            if (differ)
            {
                state.ColumnDisplayIndex = indicesNow;
            }
        }
    }
}
