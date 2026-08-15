#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// The style-key vocabulary for the "+ entry" menus (UB-08), reflected from
    /// the REAL <see cref="Ruitk.Props.Typed.Style"/> surface so the menu can
    /// never offer a key the type does not have (the hand-written table shipped
    /// two: Gap, UnityTextAlign). Value templates come from the property's
    /// declared type; enum-typed keys pull their helper tokens from
    /// <see cref="Ruitk.Props.Typed.CssHelpers"/> by return-type reflection, so
    /// a new helper or a new style property shows up here without an edit.
    /// </summary>
    internal static class BuilderStyleSurface
    {
        public readonly struct KeyInfo
        {
            public readonly string Name;
            public readonly string TypeLabel;
            public readonly string[] Templates;

            public KeyInfo(string name, string typeLabel, string[] templates)
            {
                Name = name;
                TypeLabel = typeLabel;
                Templates = templates;
            }
        }

        public static readonly string[] GenericTemplates =
            { "Px(8)", "Pct(100)", "Hex(\"#ffffff\")", "0" };

        /// <summary>POC key order for the head of the menu; entries that do not
        /// survive the reflection filter are dropped, never offered.</summary>
        private static readonly string[] s_curatedOrder =
        {
            "FlexGrow", "FlexShrink", "FlexDirection", "JustifyContent", "AlignItems",
            "AlignSelf", "Width", "Height", "MinWidth", "MaxWidth", "MinHeight",
            "MaxHeight", "Padding", "Margin", "BorderRadius", "BorderWidth",
            "BackgroundColor", "Color", "BorderColor", "FontSize", "UnityFontStyle",
            "TextAlign", "Opacity", "Display", "Position",
        };

        private static List<KeyInfo> s_keys;
        private static Dictionary<Type, List<string>> s_helperTokens;

        public static IReadOnlyList<KeyInfo> Keys
        {
            get
            {
                if (s_keys == null)
                    Build();
                return s_keys;
            }
        }

        /// <summary>First CssHelpers token whose type matches the (simple) type
        /// name — lets attribute defaults use <c>PickPosition</c> for a
        /// <c>PickingMode</c> prop instead of an uncompilable placeholder.</summary>
        public static bool TryEnumToken(string typeName, out string token)
        {
            if (s_helperTokens == null)
                Build();
            foreach (var pair in s_helperTokens)
            {
                if (pair.Key.Name == typeName && pair.Value.Count > 0)
                {
                    token = pair.Value[0];
                    return true;
                }
            }
            token = null;
            return false;
        }

        private static void Build()
        {
            s_helperTokens = new Dictionary<Type, List<string>>();
            foreach (var prop in typeof(Ruitk.Props.Typed.CssHelpers)
                .GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                if (!s_helperTokens.TryGetValue(prop.PropertyType, out var list))
                    s_helperTokens[prop.PropertyType] = list = new List<string>();
                list.Add(prop.Name);
            }

            var byName = new Dictionary<string, KeyInfo>(StringComparer.Ordinal);
            foreach (var prop in typeof(Ruitk.Props.Typed.Style)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0
                    || !prop.CanWrite || prop.GetSetMethod() == null)
                    continue;
                byName[prop.Name] = new KeyInfo(
                    prop.Name, LabelFor(prop.PropertyType), TemplatesFor(prop.PropertyType));
            }

            s_keys = new List<KeyInfo>(byName.Count);
            foreach (string name in s_curatedOrder)
            {
                if (byName.TryGetValue(name, out var info))
                {
                    s_keys.Add(info);
                    byName.Remove(name);
                }
            }
            var rest = new List<string>(byName.Keys);
            rest.Sort(StringComparer.Ordinal);
            foreach (string name in rest)
                s_keys.Add(byName[name]);
        }

        private static string LabelFor(Type type)
        {
            if (type == typeof(StyleLength))
                return "length";
            if (type == typeof(StyleFloat) || type == typeof(float))
                return "number";
            if (type == typeof(int))
                return "int";
            if (type == typeof(Color))
                return "color";
            if (type.IsEnum)
                return type.Name;
            if (type.IsGenericType)
                return type.Name.Substring(0, type.Name.IndexOf('`'))
                    + "<" + type.GetGenericArguments()[0].Name + ">";
            return type.Name;
        }

        private static string[] TemplatesFor(Type type)
        {
            if (type == typeof(StyleLength))
                return new[] { "Px(8)", "Px(16)", "Pct(100)", "Pct(50)", "StyleAuto" };
            if (type == typeof(StyleFloat))
                return new[] { "1", "0", "0.5f" };
            if (type == typeof(float))
                return new[] { "0f", "45f" };
            if (type == typeof(int))
                return new[] { "0", "1", "8" };
            if (type == typeof(Color))
                return new[] { "Hex(\"#1b1b1f\")", "Hex(\"#4fc3f7\")", "Rgba(0, 0, 0, 128)" };
            if (type.IsEnum)
            {
                if (s_helperTokens.TryGetValue(type, out var tokens))
                    return tokens.ToArray();
                var names = Enum.GetNames(type);
                int count = Math.Min(names.Length, 4);
                var qualified = new string[count];
                for (int i = 0; i < count; i++)
                    qualified[i] = type.Name + "." + names[i];
                return qualified;
            }
            return Array.Empty<string>();
        }
    }
}
#endif
