using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Ruitk.Language.Import
{
    /// <summary>
    /// One-way UXML → UITKX conversion (visual-editor VE-17). Produces new-mode
    /// source (<c>export VirtualNode Name() { return (...); }</c>); inline USS
    /// declarations map to the typed <c>Style</c> initializer where a mapping
    /// exists. Anything without a representation (templates, stylesheet refs,
    /// unknown USS declarations) is dropped and reported in
    /// <see cref="UxmlImportResult.Warnings"/> — never silently.
    /// </summary>
    public sealed class UxmlImportResult
    {
        public string UitkxText { get; set; } = "";
        public List<string> Warnings { get; } = new List<string>();
    }

    public static class UxmlToUitkx
    {
        public static UxmlImportResult Convert(string uxmlText, string componentName)
        {
            var result = new UxmlImportResult();
            XDocument doc;
            try
            {
                doc = XDocument.Parse(uxmlText);
            }
            catch (Exception ex)
            {
                result.Warnings.Add("UXML parse failed: " + ex.Message);
                return result;
            }

            var roots = new List<XElement>();
            if (doc.Root != null)
            {
                foreach (var child in doc.Root.Elements())
                {
                    string local = child.Name.LocalName;
                    if (local == "Style")
                    {
                        result.Warnings.Add(
                            "Stylesheet reference dropped (src=\""
                            + (child.Attribute("src")?.Value ?? "") + "\") - link styles via a .style.uitkx module.");
                        continue;
                    }
                    if (local == "Template")
                    {
                        result.Warnings.Add("Template definition dropped - import the template's own .uxml instead.");
                        continue;
                    }
                    roots.Add(child);
                }
            }

            var sb = new StringBuilder();
            sb.Append("export VirtualNode ").Append(componentName).Append("() {\n");
            sb.Append("  return (\n");
            if (roots.Count == 0)
            {
                sb.Append("    <VisualElement />\n");
            }
            else if (roots.Count == 1)
            {
                EmitElement(sb, roots[0], 4, result);
            }
            else
            {
                sb.Append("    <VisualElement>\n");
                foreach (var root in roots)
                    EmitElement(sb, root, 6, result);
                sb.Append("    </VisualElement>\n");
            }
            sb.Append("  );\n");
            sb.Append("}\n");
            result.UitkxText = sb.ToString();
            return result;
        }

        private static void EmitElement(StringBuilder sb, XElement element, int indent, UxmlImportResult result)
        {
            string pad = new string(' ', indent);
            string tag = MapTag(element.Name.LocalName, result);
            if (tag == null)
                return;

            var attrs = new List<string>();
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                    continue;
                string mapped = MapAttribute(attribute.Name.LocalName, attribute.Value, result);
                if (mapped != null)
                    attrs.Add(mapped);
            }

            var children = element.Elements().ToList();
            sb.Append(pad).Append('<').Append(tag);
            foreach (string attr in attrs)
                sb.Append(' ').Append(attr);

            if (children.Count == 0)
            {
                sb.Append(" />\n");
                return;
            }
            sb.Append(">\n");
            foreach (var child in children)
                EmitElement(sb, child, indent + 2, result);
            sb.Append(pad).Append("</").Append(tag).Append(">\n");
        }

        private static string MapTag(string localName, UxmlImportResult result)
        {
            switch (localName)
            {
                case "Instance":
                    result.Warnings.Add("Template <Instance> dropped - reference the imported component directly.");
                    return null;
                default:
                    return localName;
            }
        }

        private static string MapAttribute(string name, string value, UxmlImportResult result)
        {
            switch (name)
            {
                case "class":
                    return "className=\"" + Escape(value) + "\"";
                case "style":
                    return MapInlineStyle(value, result);
                case "picking-mode":
                    return value == "Ignore" ? "pickingMode={PickIgnore}" : null;
                case "template":
                case "src":
                    result.Warnings.Add("Attribute '" + name + "' dropped (no uitkx equivalent).");
                    return null;
                default:
                {
                    string camel = KebabToCamel(name);
                    if (value == "true" || value == "false")
                        return camel + "={" + value + "}";
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        return camel + "={" + value + "}";
                    return camel + "=\"" + Escape(value) + "\"";
                }
            }
        }

        private static string MapInlineStyle(string ussInline, UxmlImportResult result)
        {
            var entries = new List<string>();
            foreach (string raw in ussInline.Split(';'))
            {
                string declaration = raw.Trim();
                if (declaration.Length == 0)
                    continue;
                int colon = declaration.IndexOf(':');
                if (colon <= 0)
                    continue;
                string property = declaration.Substring(0, colon).Trim();
                string value = declaration.Substring(colon + 1).Trim();
                string mapped = MapUssDeclaration(property, value);
                if (mapped != null)
                    entries.Add(mapped);
                else
                    result.Warnings.Add("Inline USS declaration dropped: " + declaration);
            }
            if (entries.Count == 0)
                return null;
            return "style={new Style { " + string.Join(", ", entries) + " }}";
        }

        private static string MapUssDeclaration(string property, string value)
        {
            string prop = KebabToPascal(property.StartsWith("-unity-", StringComparison.Ordinal)
                ? "unity" + property.Substring(6)
                : property);

            switch (property)
            {
                case "flex-direction":
                    return value switch
                    {
                        "row" => "FlexDirection = FlexRow",
                        "row-reverse" => "FlexDirection = FlexRowReverse",
                        "column" => "FlexDirection = FlexColumn",
                        "column-reverse" => "FlexDirection = FlexColumnReverse",
                        _ => null,
                    };
                case "align-items":
                case "align-self":
                case "align-content":
                    return MapAlign(prop, value);
                case "justify-content":
                    return value switch
                    {
                        "flex-start" => "JustifyContent = JustifyFlexStart",
                        "flex-end" => "JustifyContent = JustifyFlexEnd",
                        "center" => "JustifyContent = JustifyCenter",
                        "space-between" => "JustifyContent = JustifySpaceBetween",
                        "space-around" => "JustifyContent = JustifySpaceAround",
                        _ => null,
                    };
                case "position":
                    return value == "absolute" ? "Position = PosAbsolute"
                        : value == "relative" ? "Position = PosRelative"
                        : null;
                case "overflow":
                    return value == "hidden" ? "Overflow = OverflowHidden"
                        : value == "visible" ? "Overflow = OverflowVisible"
                        : null;
                case "-unity-font-style":
                    return value switch
                    {
                        "bold" => "UnityFontStyle = FontBold",
                        "italic" => "UnityFontStyle = FontItalic",
                        "bold-and-italic" => "UnityFontStyle = FontBoldAndItalic",
                        "normal" => "UnityFontStyle = FontNormal",
                        _ => null,
                    };
                case "flex-grow":
                case "flex-shrink":
                case "opacity":
                    return Number(value, out string bare) ? prop + " = " + bare : null;
                default:
                {
                    string colorLiteral = MapColor(value);
                    if (colorLiteral != null)
                        return prop + " = " + colorLiteral;
                    string length = MapLength(value);
                    if (length != null)
                        return prop + " = " + length;
                    return null;
                }
            }
        }

        private static string MapAlign(string prop, string value)
        {
            string align = value switch
            {
                "flex-start" => "AlignFlexStart",
                "flex-end" => "AlignFlexEnd",
                "center" => "AlignCenter",
                "stretch" => "AlignStretch",
                "auto" => "AlignAuto",
                _ => null,
            };
            return align == null ? null : prop + " = " + align;
        }

        private static bool Number(string value, out string bare)
        {
            bare = null;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                return false;
            bare = parsed.ToString("0.###", CultureInfo.InvariantCulture) + "f";
            return true;
        }

        private static string MapLength(string value)
        {
            if (value.EndsWith("px", StringComparison.Ordinal)
                && double.TryParse(value.Substring(0, value.Length - 2), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double px))
                return "Px(" + px.ToString("0.###", CultureInfo.InvariantCulture) + ")";
            if (value.EndsWith("%", StringComparison.Ordinal)
                && double.TryParse(value.Substring(0, value.Length - 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double pct))
                return "Pct(" + pct.ToString("0.###", CultureInfo.InvariantCulture) + ")";
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double bare))
                return "Px(" + bare.ToString("0.###", CultureInfo.InvariantCulture) + ")";
            return null;
        }

        private static string MapColor(string value)
        {
            if (value.StartsWith("#", StringComparison.Ordinal))
                return "Hex(\"" + value + "\")";
            if (value.StartsWith("rgba(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal))
            {
                var parts = value.Substring(5, value.Length - 6).Split(',');
                if (parts.Length == 4)
                    return "Rgba(" + string.Join(", ", parts.Select(p => p.Trim())) + ")";
            }
            if (value.StartsWith("rgb(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal))
            {
                var parts = value.Substring(4, value.Length - 5).Split(',');
                if (parts.Length == 3)
                    return "Rgba(" + string.Join(", ", parts.Select(p => p.Trim())) + ", 255)";
            }
            return null;
        }

        private static string KebabToCamel(string kebab)
        {
            string pascal = KebabToPascal(kebab);
            return pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        }

        private static string KebabToPascal(string kebab)
        {
            var sb = new StringBuilder(kebab.Length);
            bool upper = true;
            foreach (char c in kebab)
            {
                if (c == '-')
                {
                    upper = true;
                    continue;
                }
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            return sb.ToString();
        }

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
