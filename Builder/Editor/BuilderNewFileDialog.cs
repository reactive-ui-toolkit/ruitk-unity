#if UNITY_EDITOR
using System.IO;
using UnityEditor;

namespace Ruitk.Builder
{
    /// <summary>
    /// New-file creation. The POC's create flow is an at-cursor name popup
    /// (openCreateMenu → openNameMenu), so validation and the inline error live
    /// in <see cref="BuilderSearchMenu.ShowNamePrompt"/>; what remains here is the
    /// write half — filename suffix per kind and the new-mode templates. The file
    /// is written to disk (creation is inherently a disk operation) and opened
    /// into the current builder tree by the caller.
    /// </summary>
    internal static class BuilderNewFileDialog
    {
        /// <summary>Writes the template for <paramref name="kind"/> beside the
        /// tree; returns the created path, or null when the file exists.</summary>
        public static string Create(string directory, string kind, string name)
        {
            string fileName = kind switch
            {
                "Hooks" => name + ".hooks.uitkx",
                "Style" => name + ".style.uitkx",
                _ => name + ".uitkx",
            };
            string path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                return null;
            File.WriteAllText(path, TemplateFor(kind, name));
            AssetDatabase.Refresh();
            return path;
        }

        private static string TemplateFor(string kind, string name)
        {
            switch (kind)
            {
                case "Style":
                    return "export Style " + name + "Root = new Style {\n  FlexGrow = 1,\n};\n";
                case "Hooks":
                    return "export (int value, Action increment) " + name
                        + "() {\n  var (value, setValue) = useState(0);\n"
                        + "  return (value, () => setValue(value + 1));\n}\n";
                case "Utils":
                    return "export string " + name + "Text(string input) {\n  return input;\n}\n";
                default:
                    return "export VirtualNode " + name + "() {\n  return (\n    <VisualElement>\n"
                        + "      <Label text=\"" + name + "\" />\n    </VisualElement>\n  );\n}\n";
            }
        }
    }
}
#endif
