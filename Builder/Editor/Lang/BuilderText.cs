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

        /// <summary>
        /// Moves the line range [<paramref name="fromIndex"/>,
        /// <paramref name="toIndex"/>] INSIDE the self-closing tag at
        /// <paramref name="targetIndex"/>, re-opening it: a line ending "/>"
        /// becomes ">", the moved block follows as its children, and a closing
        /// tag is written after them.
        ///
        /// Pure list-to-list so the rule is checked outside Unity. The ORDER is
        /// the whole subtlety: the block is lifted out FIRST, because re-opening
        /// the target inserts a line and would otherwise shift the range out from
        /// under the extraction when the target sits above it.
        ///
        /// Returns false, leaving <paramref name="result"/> null, when the target
        /// is not self-closing or lies inside the range being moved - a tag
        /// cannot be moved into itself.
        /// </summary>
        public static bool TryMoveIntoSelfClosingTag(
            System.Collections.Generic.IReadOnlyList<string> lines,
            int targetIndex,
            int fromIndex,
            int toIndex,
            string tagName,
            out System.Collections.Generic.List<string> result)
        {
            result = null;
            if (lines == null || string.IsNullOrEmpty(tagName))
                return false;
            if (targetIndex < 0 || targetIndex >= lines.Count)
                return false;
            if (fromIndex < 0 || toIndex < fromIndex || toIndex >= lines.Count)
                return false;
            if (targetIndex >= fromIndex && targetIndex <= toIndex)
                return false;

            int slash = lines[targetIndex].LastIndexOf("/>", System.StringComparison.Ordinal);
            if (slash < 0)
                return false;

            var working = new System.Collections.Generic.List<string>(lines);
            string targetIndent = LeadingIndent(working[targetIndex]);

            var moved = working.GetRange(fromIndex, toIndex - fromIndex + 1);
            string srcIndent = LeadingIndent(moved[0]);
            for (int i = 0; i < moved.Count; i++)
            {
                if (moved[i].StartsWith(srcIndent, System.StringComparison.Ordinal))
                    moved[i] = targetIndent + "  " + moved[i].Substring(srcIndent.Length);
            }

            working.RemoveRange(fromIndex, toIndex - fromIndex + 1);
            int target = targetIndex > toIndex
                ? targetIndex - (toIndex - fromIndex + 1)
                : targetIndex;

            working[target] = working[target].Remove(slash, 2).TrimEnd() + ">";
            working.InsertRange(target + 1, moved);
            working.Insert(target + 1 + moved.Count, targetIndent + "</" + tagName + ">");

            result = working;
            return true;
        }
    }
}
#endif
