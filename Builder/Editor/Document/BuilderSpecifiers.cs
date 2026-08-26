#if UNITY_EDITOR
using System;
using System.IO;

namespace Ruitk.Builder
{
    /// <summary>
    /// The two directions of an import specifier, kept together because they are
    /// only correct as a PAIR: whatever <see cref="Relative"/> writes,
    /// <see cref="Map"/> has to read back to the same file. A move re-spells every
    /// specifier it invalidated, so a disagreement between these two would not
    /// produce one bad import - it would silently rewrite every import in the tree
    /// to something that no longer resolves.
    ///
    /// Pure: paths and the language's own resolver, no Unity, no filesystem. That
    /// is what lets Builder~/ModelTests drive the round trip directly.
    /// </summary>
    internal static class BuilderSpecifiers
    {
        /// <summary>The absolute path a specifier names, with no check that
        /// anything is there. The ONE place in the builder that turns an import
        /// into a path - the preview compiler orders its compiles with the same
        /// answer the canvas draws its edges from.</summary>
        public static string Map(string fromFile, string specifier)
        {
            string fromDir = Path.GetDirectoryName(fromFile);
            if (string.IsNullOrEmpty(fromDir) || string.IsNullOrEmpty(specifier))
                return null;
            string mapped = Ruitk.Language.ImportResolver.MapSpecifierToPath(
                fromDir, specifier, null, out bool escaped);
            if (escaped || string.IsNullOrEmpty(mapped))
                return null;
            try
            {
                return Path.GetFullPath(mapped);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The specifier an importer in <paramref name="fromFolder"/> has
        /// to write to reach <paramref name="targetPath"/>.</summary>
        public static string Relative(string fromFolder, string targetPath)
        {
            if (string.IsNullOrEmpty(fromFolder) || string.IsNullOrEmpty(targetPath))
                return null;
            string rel;
            try
            {
                rel = Path.GetRelativePath(fromFolder, targetPath).Replace('\\', '/');
            }
            catch (Exception)
            {
                return null;
            }
            if (rel.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(0, rel.Length - ".uitkx".Length);
            if (!rel.StartsWith(".", StringComparison.Ordinal))
                rel = "./" + rel;
            return rel;
        }
    }
}
#endif
