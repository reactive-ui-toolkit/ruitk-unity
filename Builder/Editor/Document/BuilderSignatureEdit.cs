using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruitk.Builder
{
    /// <summary>One declared parameter of a component or hook export.</summary>
    public sealed class BuilderParam
    {
        public string Type = "";
        public string Name = "";

        /// <summary>The written default, WITHOUT the '='. Null when the
        /// parameter was written without one, which makes the prop required at
        /// every call site (UITKX0115).</summary>
        public string Default;

        /// <summary>The prop a call site writes, which drops the
        /// leading-underscore "deliberately unused" marker.</summary>
        public string PropName =>
            Name.Length > 1 && Name[0] == '_' ? Name.Substring(1) : Name;

        public bool IsRequired => Default == null;

        public override string ToString() =>
            Default == null ? Type + " " + Name : Type + " " + Name + " = " + Default;
    }

    /// <summary>
    /// Text surgery on an export's PARAMETER LIST and on a call site's open tag.
    ///
    /// Every operation is a pure string transform over buffer text: the builder
    /// edits the tree's in-memory buffers and projects them to disk on Save, so
    /// nothing here opens a file or asks the filesystem anything.
    ///
    /// The parameter list is scanned rather than matched with a single regex.
    /// A regex ending at the first ')' truncates a lambda default, and splitting
    /// the inside on ',' cuts Dictionary&lt;string,int&gt; map in half - both are
    /// legal parameters that the display-only signature regexes never had to
    /// survive, because they only ever had to produce a label.
    /// </summary>
    public static class BuilderSignatureEdit
    {
        /// <summary>
        /// Compiled patterns, cached by the name they were built for.
        ///
        /// These are built per EXPORT NAME and per ATTRIBUTE NAME, and both are
        /// asked on hot paths - the builder re-derives every component's required
        /// props on every diagnostics pass, which is every settled keystroke.
        /// A `new Regex(..., Compiled)` per call is the worst of both worlds: it
        /// JIT-compiles the pattern EVERY time, which costs far more than the
        /// interpreted match it was meant to speed up. Cached, Compiled is right.
        ///
        /// The key spaces are the export and attribute names of one open tree, so
        /// these are small and bounded by the project rather than by input.
        /// Editor-thread only, like everything else in the builder.
        /// </summary>
        private static readonly Dictionary<string, Regex> s_headCache =
            new Dictionary<string, Regex>(StringComparer.Ordinal);

        private static readonly Dictionary<string, Regex> s_attrCache =
            new Dictionary<string, Regex>(StringComparer.Ordinal);

        /// <summary>Matches an export declaration head up TO its opening paren.
        /// The paren itself is where scanning takes over.</summary>
        private static Regex HeadOf(string exportName)
        {
            if (s_headCache.TryGetValue(exportName, out var cached))
                return cached;
            var built = new Regex(
                @"(?:^|\n)[ \t]*export\s+(?:VirtualNode\s+|\([^)]*\)\s*)"
                + Regex.Escape(exportName) + @"\s*\(",
                RegexOptions.Compiled);
            s_headCache[exportName] = built;
            return built;
        }

        private static Regex AttributeNamed(string attrName)
        {
            if (s_attrCache.TryGetValue(attrName, out var cached))
                return cached;
            var built = new Regex(
                @"(?<![\w-])" + Regex.Escape(attrName) + @"\s*=", RegexOptions.Compiled);
            s_attrCache[attrName] = built;
            return built;
        }

        /// <summary>Offsets of the parameter list of <paramref name="exportName"/>:
        /// the index just AFTER '(' and the index OF the matching ')'. Both -1
        /// when the export is not found or its list never closes.</summary>
        public static bool TryFindParamSpan(
            string text, string exportName, out int start, out int end)
        {
            start = -1;
            end = -1;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(exportName))
                return false;
            var head = HeadOf(exportName).Match(text);
            if (!head.Success)
                return false;
            start = head.Index + head.Length;
            end = MatchingParen(text, start);
            if (end < 0)
            {
                start = -1;
                return false;
            }
            return true;
        }

        /// <summary>Index of the ')' that closes the '(' just before
        /// <paramref name="from"/>, skipping string literals and nesting.</summary>
        private static int MatchingParen(string text, int from)
        {
            int depth = 0;
            for (int i = from; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(text, i);
                    continue;
                }
                if (c == '(' || c == '[')
                    depth++;
                else if (c == ']')
                    depth--;
                else if (c == ')')
                {
                    if (depth == 0)
                        return i;
                    depth--;
                }
            }
            return -1;
        }

        /// <summary>Index of the closing quote of the literal that opens at
        /// <paramref name="at"/>, honouring backslash escapes. Returns the last
        /// index when the literal never closes, so callers always advance.</summary>
        private static int SkipQuoted(string text, int at)
        {
            char quote = text[at];
            for (int i = at + 1; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }
                if (text[i] == quote)
                    return i;
            }
            return text.Length - 1;
        }

        private static readonly Regex s_anyExport = new Regex(
            @"(?:^|\n)[ \t]*export\s+(?:VirtualNode\s+|\([^)]*\)\s*)(?<name>\w+)\s*\(",
            RegexOptions.Compiled);

        /// <summary>
        /// The first component or hook this text declares, or null.
        ///
        /// Used to say what a buffer IS at the moment it is written: a module
        /// whose file is MiddleSide.uitkx but whose text declares NewComponent is
        /// the corruption of UB-224, and this is what lets the write announce it
        /// rather than leaving it to be noticed later.
        /// </summary>
        public static string FirstExportName(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var m = s_anyExport.Match(text);
            return m.Success ? m.Groups["name"].Value : null;
        }

        /// <summary>The declared parameters of <paramref name="exportName"/>, in
        /// declaration order. Empty when the export is not found.</summary>
        public static List<BuilderParam> Parse(string text, string exportName)
        {
            if (!TryFindParamSpan(text, exportName, out int start, out int end))
                return new List<BuilderParam>();
            return ParseList(text.Substring(start, end - start));
        }

        /// <summary>The parameters inside an already-extracted list - what the
        /// card's display signature carries, which has the parentheses but not
        /// the declaration around them.</summary>
        public static List<BuilderParam> ParseList(string inner)
        {
            var result = new List<BuilderParam>();
            foreach (string piece in SplitTopLevel(inner))
            {
                var param = ParseOne(piece);
                if (param != null)
                    result.Add(param);
            }
            return result;
        }

        /// <summary>Splits a parameter list on the commas that separate
        /// PARAMETERS, not the ones inside a generic argument list, a nested
        /// call, a collection initialiser or a string.</summary>
        public static List<string> SplitTopLevel(string inner)
        {
            var parts = new List<string>();
            if (inner == null)
                return parts;
            int depth = 0;
            int from = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(inner, i);
                    continue;
                }
                if (c == '(' || c == '[' || c == '<' || c == '{')
                    depth++;
                else if (c == ')' || c == ']' || c == '>' || c == '}')
                    depth--;
                else if (c == ',' && depth <= 0)
                {
                    parts.Add(inner.Substring(from, i - from));
                    from = i + 1;
                }
            }
            parts.Add(inner.Substring(from));
            return parts;
        }

        private static BuilderParam ParseOne(string piece)
        {
            string trimmed = (piece ?? "").Trim();
            if (trimmed.Length == 0)
                return null;

            string defaultValue = null;
            int eq = TopLevelEquals(trimmed);
            if (eq >= 0)
            {
                defaultValue = trimmed.Substring(eq + 1).Trim();
                trimmed = trimmed.Substring(0, eq).Trim();
            }

            int split = LastTopLevelSpace(trimmed);
            if (split <= 0)
                return null;
            return new BuilderParam
            {
                Type = trimmed.Substring(0, split).Trim(),
                Name = trimmed.Substring(split + 1).Trim(),
                Default = defaultValue,
            };
        }

        /// <summary>Index of the '=' that introduces a DEFAULT. A lambda default
        /// is legal and its arrow is not an assignment, so "=&gt;" and the
        /// comparison operators are stepped over.</summary>
        private static int TopLevelEquals(string text)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(text, i);
                    continue;
                }
                if (c == '(' || c == '[' || c == '{')
                    depth++;
                else if (c == ')' || c == ']' || c == '}')
                    depth--;
                else if (c == '=' && depth <= 0)
                {
                    if (i + 1 < text.Length && (text[i + 1] == '=' || text[i + 1] == '>'))
                    {
                        i++;
                        continue;
                    }
                    if (i > 0 && (text[i - 1] == '!' || text[i - 1] == '<'
                        || text[i - 1] == '>' || text[i - 1] == '='))
                        continue;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Index of the space separating the TYPE from the NAME. Inside
        /// a generic argument list a space means nothing
        /// (<c>Dictionary&lt;string, int&gt; map</c>), so depth is tracked.</summary>
        private static int LastTopLevelSpace(string text)
        {
            int depth = 0;
            int found = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<' || c == '(' || c == '[')
                    depth++;
                else if (c == '>' || c == ')' || c == ']')
                    depth--;
                else if ((c == ' ' || c == '\t' || c == '\n' || c == '\r') && depth <= 0)
                    found = i;
            }
            return found;
        }

        /// <summary>Rewrites the parameter list with <paramref name="rewrite"/>,
        /// which receives the current parameters and returns the replacement
        /// list, or null to leave the text alone.</summary>
        private static string RewriteParams(
            string text, string exportName, Func<List<BuilderParam>, List<BuilderParam>> rewrite)
        {
            if (!TryFindParamSpan(text, exportName, out int start, out int end))
                return text;
            var current = Parse(text, exportName);
            var next = rewrite(current);
            if (next == null)
                return text;

            bool multiLine = text.IndexOf('\n', start, end - start) >= 0;
            var sb = new StringBuilder();
            if (multiLine)
            {
                // From the line carrying the '(', which is one BEFORE `start`:
                // `start` sits on the newline that ends it, so measuring there
                // reads the indent of the first parameter instead.
                string indent = IndentOfLineAt(text, start - 1);
                for (int i = 0; i < next.Count; i++)
                {
                    sb.Append('\n').Append(indent).Append("  ").Append(next[i].ToString());
                    if (i < next.Count - 1)
                        sb.Append(',');
                }
                if (next.Count > 0)
                    sb.Append('\n').Append(indent);
            }
            else
            {
                for (int i = 0; i < next.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(next[i].ToString());
                }
            }
            return text.Substring(0, start) + sb.ToString() + text.Substring(end);
        }

        private static string IndentOfLineAt(string text, int index)
        {
            int at = Math.Max(0, Math.Min(index, text.Length - 1));
            int lineStart = text.LastIndexOf('\n', at);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int i = lineStart;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
                i++;
            return text.Substring(lineStart, i - lineStart);
        }

        /// <summary>Adds a parameter. One WITHOUT a default is required, so it
        /// lands before the first optional one - C# rejects a required parameter
        /// that follows an optional one, and the builder must not author code the
        /// compiler refuses.</summary>
        public static string AddParam(
            string text, string exportName, string type, string name, string defaultValue)
        {
            return RewriteParams(text, exportName, current =>
            {
                var added = new BuilderParam
                {
                    Type = (type ?? "").Trim(),
                    Name = (name ?? "").Trim(),
                    Default = string.IsNullOrEmpty(defaultValue) ? null : defaultValue.Trim(),
                };
                if (added.Type.Length == 0 || added.Name.Length == 0)
                    return null;
                foreach (var p in current)
                    if (string.Equals(p.Name, added.Name, StringComparison.Ordinal))
                        return null;

                if (added.IsRequired)
                {
                    int at = current.Count;
                    for (int i = 0; i < current.Count; i++)
                    {
                        if (!current[i].IsRequired)
                        {
                            at = i;
                            break;
                        }
                    }
                    current.Insert(at, added);
                }
                else
                {
                    current.Add(added);
                }
                return current;
            });
        }

        public static string RenameParam(
            string text, string exportName, string oldName, string newName)
        {
            return RewriteParams(text, exportName, current =>
            {
                bool hit = false;
                foreach (var p in current)
                {
                    if (!string.Equals(p.Name, oldName, StringComparison.Ordinal))
                        continue;
                    p.Name = newName;
                    hit = true;
                }
                return hit ? current : null;
            });
        }

        public static string RemoveParam(string text, string exportName, string name)
        {
            return RewriteParams(text, exportName, current =>
            {
                int at = -1;
                for (int i = 0; i < current.Count; i++)
                {
                    if (string.Equals(current[i].Name, name, StringComparison.Ordinal))
                    {
                        at = i;
                        break;
                    }
                }
                if (at < 0)
                    return null;
                current.RemoveAt(at);
                return current;
            });
        }

        /// <summary>Offsets of the export's BODY: the index of its opening '{'
        /// and of the matching '}'. False when the export or its body is not
        /// found.</summary>
        public static bool TryFindBodySpan(
            string text, string exportName, out int start, out int end)
        {
            start = -1;
            end = -1;
            if (!TryFindParamSpan(text, exportName, out int _, out int close))
                return false;
            int open = text.IndexOf('{', close);
            if (open < 0)
                return false;
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(text, i);
                    continue;
                }
                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        start = open;
                        end = i;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Renames the parameter's USES inside its own export's body. Renaming
        /// the declaration alone leaves the body referring to a name that is no
        /// longer declared, so the module stops compiling.
        ///
        /// Scoped to the one export - another export in the same file may have a
        /// local of the same name - and two things are deliberately skipped: text
        /// inside a string literal, and an identifier immediately followed by
        /// '=', which in markup is an ATTRIBUTE NAME (<c>label="x"</c>) rather
        /// than a use of the parameter. The trade is that assigning TO a
        /// parameter is missed; that is rare, and it fails loudly at compile
        /// time, where silently rewriting an unrelated attribute name would
        /// change what the component renders.
        /// </summary>
        public static string RenameParamUses(
            string text, string exportName, string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
                return text;
            if (!TryFindBodySpan(text, exportName, out int start, out int end))
                return text;

            string body = text.Substring(start, end - start + 1);
            var uses = FindUses(body, oldName);
            if (uses.Count == 0)
                return text;

            var sb = new StringBuilder(body.Length);
            int copied = 0;
            foreach (int at in uses)
            {
                sb.Append(body, copied, at - copied).Append(newName);
                copied = at + oldName.Length;
            }
            sb.Append(body, copied, body.Length - copied);
            return text.Substring(0, start) + sb.ToString() + text.Substring(end + 1);
        }

        /// <summary>
        /// Start offsets of every USE of <paramref name="name"/> in a body, in
        /// order. One linear pass, because seeking each match with a regex and
        /// jumping to it steps straight over the string literals in between -
        /// which is exactly how a word inside a literal ends up rewritten.
        /// </summary>
        private static List<int> FindUses(string body, string name)
        {
            var uses = new List<int>();
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(body, i);
                    continue;
                }
                if (!IsIdentStart(c))
                    continue;

                int j = i;
                while (j < body.Length && IsIdentPart(body[j]))
                    j++;
                if (j - i == name.Length
                    && string.CompareOrdinal(body, i, name, 0, name.Length) == 0
                    && (i == 0 || body[i - 1] != '.'))
                {
                    int probe = j;
                    while (probe < body.Length && (body[probe] == ' ' || body[probe] == '\t'))
                        probe++;
                    bool isAttributeName = probe < body.Length && body[probe] == '='
                        && (probe + 1 >= body.Length || body[probe + 1] != '=');
                    if (!isAttributeName)
                        uses.Add(i);
                }
                i = j - 1;
            }
            return uses;
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

        private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>How many times the parameter is still referenced inside its
        /// own export's body - what a caller checks before removing it, so the
        /// gesture can say that the body will no longer compile rather than
        /// leaving the user to find out.</summary>
        public static int CountParamUses(string text, string exportName, string name)
        {
            if (!TryFindBodySpan(text, exportName, out int start, out int end))
                return 0;
            return FindUses(text.Substring(start, end - start + 1), name).Count;
        }

        // -- Call sites --------------------------------------------------------

        /// <summary>Start and end offsets of <paramref name="attrName"/> inside an
        /// open tag: from the first character of the name to just past the end of
        /// its value. False when the tag does not carry the attribute.</summary>
        public static bool TryFindAttribute(
            string tag, string attrName, out int start, out int end)
        {
            start = -1;
            end = -1;
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(attrName))
                return false;
            var match = AttributeNamed(attrName).Match(tag);
            if (!match.Success)
                return false;
            start = match.Index;
            int i = match.Index + match.Length;
            while (i < tag.Length && char.IsWhiteSpace(tag[i]))
                i++;
            if (i >= tag.Length)
                return false;
            if (tag[i] == '"' || tag[i] == '\'')
            {
                end = SkipQuoted(tag, i) + 1;
                return true;
            }
            if (tag[i] == '{')
            {
                int depth = 0;
                for (; i < tag.Length; i++)
                {
                    char c = tag[i];
                    if (c == '"' || c == '\'')
                    {
                        i = SkipQuoted(tag, i);
                        continue;
                    }
                    if (c == '{')
                        depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            end = i + 1;
                            return true;
                        }
                    }
                }
                return false;
            }
            while (i < tag.Length && !char.IsWhiteSpace(tag[i]) && tag[i] != '>' && tag[i] != '/')
                i++;
            end = i;
            return true;
        }

        /// <summary>Renames one attribute on an open tag, leaving its value alone.
        /// Returns the tag unchanged when the attribute is not there.</summary>
        public static string RenameAttribute(string tag, string oldName, string newName)
        {
            if (!TryFindAttribute(tag, oldName, out int start, out int _))
                return tag;
            return tag.Substring(0, start) + newName
                + tag.Substring(start + oldName.Length);
        }

        /// <summary>
        /// Applies <paramref name="transformTag"/> to the open tag of EVERY
        /// <c>&lt;tagName ...&gt;</c> in one buffer, as a single string transform -
        /// so a tree-wide prop rename or removal is one edit per file, and one
        /// Ctrl+Z takes the whole sweep back.
        ///
        /// Occurrences inside a string literal are left alone: a .uitkx file can
        /// carry markup inside a C# string (the builder's own CodeField spike
        /// seeds its editor with one), and rewriting that would corrupt a
        /// literal that has nothing to do with the component.
        /// </summary>
        public static string RewriteCallSites(
            string text, string tagName, Func<string, string> transformTag)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(tagName)
                || transformTag == null)
                return text;

            var sb = new StringBuilder(text.Length);
            int copied = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(text, i);
                    continue;
                }
                if (c != '<' || !IsTagAt(text, i + 1, tagName))
                    continue;

                int close = OpenTagEnd(text, i);
                if (close < 0)
                    continue;
                string tag = text.Substring(i, close - i + 1);
                string rewritten = transformTag(tag);
                if (rewritten == null || rewritten == tag)
                {
                    i = close;
                    continue;
                }
                sb.Append(text, copied, i - copied).Append(rewritten);
                copied = close + 1;
                i = close;
            }
            if (copied == 0)
                return text;
            sb.Append(text, copied, text.Length - copied);
            return sb.ToString();
        }

        private static bool IsTagAt(string text, int at, string tagName)
        {
            if (at + tagName.Length > text.Length)
                return false;
            if (string.CompareOrdinal(text, at, tagName, 0, tagName.Length) != 0)
                return false;
            int after = at + tagName.Length;
            if (after >= text.Length)
                return false;
            char c = text[after];
            return char.IsWhiteSpace(c) || c == '/' || c == '>';
        }

        /// <summary>Index of the '&gt;' that ends the open tag starting at
        /// <paramref name="from"/>, skipping strings and braced expressions.
        /// -1 when it never closes.</summary>
        private static int OpenTagEnd(string text, int from)
        {
            int braces = 0;
            for (int i = from; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(text, i);
                    continue;
                }
                if (c == '{')
                    braces++;
                else if (c == '}')
                    braces--;
                else if (c == '>' && braces <= 0)
                    return i;
            }
            return -1;
        }

        /// <summary>Removes one attribute and the whitespace that separated it
        /// from what came before, so the tag is not left double-spaced or with a
        /// blank line where the attribute used to be.</summary>
        public static string RemoveAttribute(string tag, string attrName)
        {
            if (!TryFindAttribute(tag, attrName, out int start, out int end))
                return tag;
            int from = start;
            while (from > 0 && (tag[from - 1] == ' ' || tag[from - 1] == '\t'))
                from--;
            if (from > 0 && tag[from - 1] == '\n')
            {
                from--;
                if (from > 0 && tag[from - 1] == '\r')
                    from--;
            }
            return tag.Substring(0, from) + tag.Substring(end);
        }
    }
}
