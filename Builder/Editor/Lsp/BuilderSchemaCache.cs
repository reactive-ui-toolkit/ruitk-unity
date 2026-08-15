#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace Ruitk.Builder
{
    /// <summary>
    /// The typed-attribute vocabulary for context menus (POC 6.4 A.1):
    /// per-element attributes from the LSP's embedded schema plus the POC's
    /// common attribute set, cached when the library pane loads the schema.
    /// </summary>
    internal static class BuilderSchemaCache
    {
        public readonly struct AttrInfo
        {
            public readonly string Name;
            public readonly string Type;

            public AttrInfo(string name, string type)
            {
                Name = name;
                Type = type;
            }
        }

        private static readonly Dictionary<string, List<AttrInfo>> s_byElement =
            new Dictionary<string, List<AttrInfo>>(StringComparer.Ordinal);

        private static readonly AttrInfo[] s_common =
        {
            new AttrInfo("style", "Style"),
            new AttrInfo("name", "string"),
            new AttrInfo("tooltip", "string"),
            new AttrInfo("focusable", "bool"),
            new AttrInfo("pickingMode", "PickingMode"),
            new AttrInfo("onClick", "Action"),
            new AttrInfo("onMouseEnter", "Action"),
            new AttrInfo("onMouseLeave", "Action"),
        };

        public static void Register(string element, List<AttrInfo> attrs) =>
            s_byElement[element] = attrs;

        public static IEnumerable<AttrInfo> AttributesFor(string element)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (s_byElement.TryGetValue(element, out var attrs))
                foreach (var attr in attrs)
                    if (seen.Add(attr.Name))
                        yield return attr;
            foreach (var attr in s_common)
                if (seen.Add(attr.Name))
                    yield return attr;
        }

        /// <summary>POC defaultAttrValue: handlers get {handler}, styles get
        /// {styleName}, strings get "text", everything else {value}.</summary>
        public static string DefaultValueFor(string name, string type)
        {
            if ((name.Length > 2 && name.StartsWith("on", StringComparison.Ordinal)
                    && char.IsUpper(name[2]))
                || type.Contains("Action"))
                return "{handler}";
            if (name == "style" || type == "Style")
                return "{styleName}";
            if (type == "string" || name == "text" || name == "label")
                return "\"text\"";
            return "{value}";
        }
    }
}
#endif
