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

        public static string LeadingIndent(string line) =>
            string.IsNullOrEmpty(line) ? "" : line.Substring(0, LeadingSpaceCount(line));
    }
}
#endif
