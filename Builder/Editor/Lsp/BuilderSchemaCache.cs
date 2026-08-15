#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace Ruitk.Builder
{
    /// <summary>
    /// The typed-attribute vocabulary for context menus (POC 6.4 A.1):
    /// per-element attributes from the LSP's embedded schema, plus the schema's
    /// own intrinsic + structural attribute sets as the common tail. The
    /// hand-written common list survives only as the offline fallback, and
    /// <see cref="HasSchema"/> lets menus say so instead of degrading silently
    /// (UB-09).
    /// </summary>
    internal static class BuilderSchemaCache
    {
        public readonly struct AttrInfo
        {
            public readonly string Name;
            public readonly string Type;
            public readonly string Description;

            public AttrInfo(string name, string type, string description = "")
            {
                Name = name;
                Type = type;
                Description = description ?? "";
            }
        }

        private static readonly Dictionary<string, List<AttrInfo>> s_byElement =
            new Dictionary<string, List<AttrInfo>>(StringComparer.Ordinal);

        private static List<AttrInfo> s_commonFromSchema;

        /// <summary>Offline-only fallback (schema unavailable): the real subset
        /// of intrinsic + structural names. The previous list carried three
        /// attributes that do not exist (usageHints, onMouseEnter, onMouseLeave).</summary>
        private static readonly AttrInfo[] s_offlineFallback =
        {
            new AttrInfo("style", "Style"),
            new AttrInfo("name", "string"),
            new AttrInfo("className", "string"),
            new AttrInfo("tooltip", "string"),
            new AttrInfo("focusable", "bool"),
            new AttrInfo("pickingMode", "PickingMode"),
            new AttrInfo("viewDataKey", "string"),
            new AttrInfo("onClick", "Action"),
            new AttrInfo("onPointerEnter", "PointerEventHandler"),
            new AttrInfo("onPointerLeave", "PointerEventHandler"),
            new AttrInfo("onGeometryChanged", "GeometryChangedEventHandler"),
            new AttrInfo("key", "string"),
            new AttrInfo("ref", "object"),
        };

        public static bool HasSchema => s_byElement.Count > 0;

        public static IEnumerable<string> ElementNames => s_byElement.Keys;

        public static bool HasElement(string element) => s_byElement.ContainsKey(element);

        public static void Register(string element, List<AttrInfo> attrs) =>
            s_byElement[element] = attrs;

        /// <summary>The schema's intrinsicElementAttributes + structuralAttributes,
        /// appended after every element's own list (and after a custom
        /// component's props, where the menu labels them as needing a prop).</summary>
        public static void RegisterCommon(List<AttrInfo> attrs) =>
            s_commonFromSchema = attrs;

        private static List<string> s_controlFlow;

        /// <summary>Schema controlFlow directive NAMES (the descriptions are
        /// stale — UB-04 — and must never be displayed). Null until the schema
        /// loads.</summary>
        public static IReadOnlyList<string> ControlFlow => s_controlFlow;

        public static void RegisterControlFlow(List<string> names) =>
            s_controlFlow = names;

        public static IEnumerable<AttrInfo> AttributesFor(string element)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (s_byElement.TryGetValue(element, out var attrs))
                foreach (var attr in attrs)
                    if (seen.Add(attr.Name))
                        yield return attr;
            if (s_commonFromSchema != null)
            {
                foreach (var attr in s_commonFromSchema)
                    if (seen.Add(attr.Name))
                        yield return attr;
                yield break;
            }
            foreach (var attr in s_offlineFallback)
                if (seen.Add(attr.Name))
                    yield return attr;
        }

        /// <summary>Type-driven default so an inserted attribute COMPILES
        /// (UB-14): the old name heuristic wrote <c>focusable={value}</c>. The
        /// inline editor still opens on the value immediately after the insert,
        /// so these are starting points, not guesses at intent. HARD RULE: no
        /// default may contain a nested '}' — the attribute edit/remove regexes
        /// match values with <c>\{[^}]*\}</c>, and a brace-bearing default
        /// (lambda bodies, object initializers) corrupts the tag on its very
        /// next edit (review finding, 2026-08-16). Delegates and objects
        /// default to null, which every prop accepts.</summary>
        public static string DefaultValueFor(string name, string type)
        {
            type = type ?? "";
            switch (type)
            {
                case "string":
                    return "\"text\"";
                case "bool":
                    return "{true}";
                case "int":
                case "uint":
                case "long":
                case "ulong":
                    return "{0}";
                case "float":
                case "double":
                    return "{0f}";
            }
            if (type == "Action" || type.StartsWith("Action<", StringComparison.Ordinal)
                || type.StartsWith("Func", StringComparison.Ordinal)
                || type.EndsWith("Handler", StringComparison.Ordinal)
                || type == "Style")
                return "{null}";
            if (BuilderStyleSurface.TryEnumToken(type, out string token))
                return "{" + token + "}";
            if (type.Length == 0 && (name == "text" || name == "label"))
                return "\"text\"";
            return "{null}";
        }
    }
}
#endif
