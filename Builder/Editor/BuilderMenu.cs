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
    }
}
#endif
