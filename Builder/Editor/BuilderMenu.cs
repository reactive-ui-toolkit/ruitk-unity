#if UNITY_EDITOR
using UnityEditor;

namespace Ruitk.Builder
{
    /// <summary>
    /// Entry points. Double-click behavior is deliberately untouched (owner
    /// decision VE-Q3): the context item is the only asset-open route into the
    /// builder, and <c>UitkxConsoleNavigation</c> is never edited.
    /// </summary>
    internal static class BuilderMenu
    {
        [MenuItem("Reactive UI Toolkit/UI Builder", priority = 100)]
        private static void OpenWindow() => BuilderWindow.OpenEmpty();

        private const string ContextItem = "Assets/Open in RUITK UI Builder";

        [MenuItem(ContextItem, priority = 20)]
        private static void OpenFromAsset()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(path))
                BuilderWindow.OpenFor(System.IO.Path.GetFullPath(path));
        }

        [MenuItem(ContextItem, validate = true)]
        private static bool ValidateOpenFromAsset()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(".uitkx", System.StringComparison.OrdinalIgnoreCase);
        }

        private const string ImportItem = "Assets/Convert UXML to UITKX";

        [MenuItem(ImportItem, priority = 21)]
        private static void ImportUxml()
        {
            string assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(assetPath))
                return;
            string fullPath = System.IO.Path.GetFullPath(assetPath);
            string stem = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            string componentName = char.ToUpperInvariant(stem[0]) + stem.Substring(1);

            var result = Ruitk.Language.Import.UxmlToUitkx.Convert(
                System.IO.File.ReadAllText(fullPath), componentName);
            if (string.IsNullOrEmpty(result.UitkxText))
            {
                UnityEditor.EditorUtility.DisplayDialog(
                    "UXML import failed", string.Join("\n", result.Warnings), "OK");
                return;
            }

            string target = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(fullPath) ?? "", componentName + ".uitkx");
            if (System.IO.File.Exists(target)
                && !UnityEditor.EditorUtility.DisplayDialog(
                    "Overwrite?", componentName + ".uitkx already exists.", "Overwrite", "Cancel"))
                return;

            System.IO.File.WriteAllText(target, result.UitkxText);
            AssetDatabase.Refresh();
            foreach (string warning in result.Warnings)
                UnityEngine.Debug.LogWarning("[RUITK Builder] UXML import: " + warning);
            BuilderWindow.OpenFor(target);
        }

        [MenuItem(ImportItem, validate = true)]
        private static bool ValidateImportUxml()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(".uxml", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
#endif
