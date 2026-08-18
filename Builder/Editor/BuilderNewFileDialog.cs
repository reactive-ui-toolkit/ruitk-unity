#if UNITY_EDITOR
using System.IO;

namespace Ruitk.Builder
{
    /// <summary>
    /// New-file PLANNING. The POC's create flow is an at-cursor name popup
    /// (openCreateMenu → openNameMenu), so validation and the inline error live
    /// in <see cref="BuilderSearchMenu.ShowNamePrompt"/>; what remains here is
    /// deciding the path and the starting text.
    ///
    /// <para>UB-111: this used to write the file and call AssetDatabase.Refresh
    /// immediately, which broke the save-only disk contract in the one direction
    /// nobody could undo — a created file survived Abort, survived closing
    /// without saving, and Ctrl+Z could not remove it. Nothing here touches
    /// disk now; the caller opens a NEW SESSION with this text and Save writes
    /// it, exactly like an edit.</para>
    /// </summary>
    internal static class BuilderNewFileDialog
    {
        /// <summary>Where a new module of <paramref name="kind"/> belongs,
        /// relative to the tree the user is editing.
        /// <para>The house layout puts a component in its OWN folder and nests
        /// child components under a "components" folder:</para>
        /// <code>
        /// ComponentName/
        ///     ComponentName.uitkx
        ///     ComponentName.style.uitkx
        ///     components/
        ///         SubComponent/
        ///             SubComponent.uitkx
        /// </code>
        /// So a new COMPONENT created while editing ComponentName becomes
        /// ComponentName/components/New/New.uitkx, while a style, hook or util
        /// module belongs to the component being edited and lands beside it.
        /// </summary>
        public static string PathFor(string focusDirectory, string kind, string name)
        {
            if (string.IsNullOrEmpty(focusDirectory))
                return null;
            if (kind == "Component")
                return Path.Combine(
                    focusDirectory, "components", name, name + ".uitkx");
            string fileName = kind switch
            {
                "Hooks" => name + ".hooks.uitkx",
                "Style" => name + ".style.uitkx",
                _ => name + ".uitkx",
            };
            return Path.Combine(focusDirectory, fileName);
        }

        /// <summary>The starting text for a new module.
        /// <para>UB-112: only the export the user NAMED is emitted — nothing
        /// else. The old templates invented extra members ("nameRoot" for a
        /// style, "nameText" for a util) that the user never asked for and had
        /// to delete. A style or util module starts EMPTY: its exports are
        /// named one at a time through the card's own "+ style" / "+ entry"
        /// affordances. A component and a hook ARE the export the user just
        /// named, so those are emitted, with the smallest legal body.</para>
        /// </summary>
        public static string TemplateFor(string kind, string name)
        {
            switch (kind)
            {
                case "Style":
                case "Utils":
                    return "";
                case "Hooks":
                    return "export (int value) " + name
                        + "() {\n  var (value, setValue) = useState(0);\n"
                        + "  return (value);\n}\n";
                default:
                    return "export VirtualNode " + name
                        + "() {\n  return (\n    <VisualElement />\n  );\n}\n";
            }
        }
    }
}
#endif
