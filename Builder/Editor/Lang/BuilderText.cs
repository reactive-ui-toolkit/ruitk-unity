#if UNITY_EDITOR
namespace Ruitk.Builder
{
    /// <summary>
    /// Buffer-text primitives shared by every edit path. The leading-space scan
    /// had four independent copies (two in the window, one in the move-payload
    /// re-indent, one inside the graph service's block dedent); a divergence
    /// there re-indents an edit differently depending on which path applied it.
    /// </summary>
    internal static class BuilderText
    {
        public static int LeadingSpaceCount(string line)
        {
            if (string.IsNullOrEmpty(line))
                return 0;
            int i = 0;
            while (i < line.Length && line[i] == ' ')
                i++;
            return i;
        }

        /// <summary>UB-123: the NAME an import binds, from its row text. Handles
        /// the three shapes the language allows — "import * as Alias from …",
        /// "import { A, B } from …" (the first name), and a bare namespace
        /// import, which binds nothing and returns null.</summary>
        public static string ImportAliasOf(string importLine)
        {
            if (string.IsNullOrEmpty(importLine))
                return null;
            var star = System.Text.RegularExpressions.Regex.Match(
                importLine, @"\*\s+as\s+([A-Za-z_][A-Za-z0-9_]*)");
            if (star.Success)
                return star.Groups[1].Value;
            var braced = System.Text.RegularExpressions.Regex.Match(
                importLine, @"\{\s*([A-Za-z_][A-Za-z0-9_]*)");
            if (braced.Success)
                return braced.Groups[1].Value;
            var direct = System.Text.RegularExpressions.Regex.Match(
                importLine, @"^\s*import\s+([A-Za-z_][A-Za-z0-9_]*)\s+from");
            return direct.Success ? direct.Groups[1].Value : null;
        }

        public static string LeadingIndent(string line) =>
            string.IsNullOrEmpty(line) ? "" : line.Substring(0, LeadingSpaceCount(line));
    }
}
#endif
