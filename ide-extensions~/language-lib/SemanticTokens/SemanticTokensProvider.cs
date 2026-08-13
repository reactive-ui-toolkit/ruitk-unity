using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;

namespace Ruitk.Language.SemanticTokens
{
    /// <summary>
    /// Walks a <see cref="ParseResult"/> and the matching source text to produce
    /// <see cref="SemanticTokenData"/> records that a language server can encode
    /// as LSP <c>textDocument/semanticTokens/full</c> data.
    ///
    /// Token categories emitted:
    /// <list type="bullet">
    ///   <item>Directive-block keywords (<c>@namespace</c>, <c>@component</c>, …) and their values</item>
    ///   <item>Element open-tag names (<c>Box</c>, <c>Label</c>, custom components)</item>
    ///   <item>Attribute names (<c>text</c>, <c>key</c>, …)</item>
    ///   <item>Control-flow keywords (<c>@if</c>, <c>@else</c>, <c>@foreach</c>, <c>@switch</c>, …)</item>
    ///   <item>Inline expression delimiters (<c>@(</c>)</item>
    /// </list>
    ///
    /// Element/attribute open-tag columns prefer the parser-tracked
    /// <see cref="AstNode.SourceColumn"/> where available (U-31); most other token
    /// kinds (directive keywords, close tags, control-flow keywords) still resolve
    /// their column by scanning the raw source text, since <c>SourceColumn</c> is
    /// not tracked for every node kind.
    /// </summary>
    public sealed class SemanticTokensProvider
    {
        private static readonly string[] s_noMods = Array.Empty<string>();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all semantic tokens for <paramref name="parseResult"/>,
        /// resolved to absolute 0-based line/column positions via
        /// <paramref name="source"/>.  The returned array is sorted by line,
        /// then column.
        /// </summary>
        /// <param name="parseResult">Fully-parsed UITKX document.</param>
        /// <param name="source">The raw source text of the same document.</param>
        /// <param name="knownElements">
        /// Set of component names that exist in the workspace (suffix "Props" stripped).
        /// When <c>null</c> (e.g. the index is not yet ready) every element receives a
        /// token so the file is never un-highlighted.  When non-null, PascalCase element
        /// names that are NOT in the set are skipped — the LSP diagnostic squiggle
        /// becomes their sole visual indicator, avoiding a double-underline effect.
        /// </param>
        public SemanticTokenData[] GetTokens(
            ParseResult parseResult,
            string source,
            HashSet<string>? knownElements = null,
            string? filePath = null
        )
        {
            var tokens = new List<SemanticTokenData>(64);
            int[] lineStarts = BuildLineStarts(source);

            // 1. Top-level declaration tokens
            CollectFunctionStyleDeclarationTokens(source, lineStarts, tokens);

            // 1b. import / export / from keywords + specifier strings (import/export grammar §10)
            CollectImportExportTokens(source, lineStarts, tokens);

            // 1c. Imported binding names colored by the KIND of the export they bind (field
            //     find, 0.9.1): components as elements, hooks/utils as functions, values as
            //     variables — instead of the grammar's one-color-fits-all type tint. Needs the
            //     importer's path to resolve targets; skipped when unavailable (pure callers).
            if (filePath != null)
                CollectImportBindingKindTokens(parseResult, source, lineStarts, tokens, filePath);

            // 2. AST markup nodes
            foreach (var node in parseResult.RootNodes)
                CollectNodeTokens(node, source, lineStarts, tokens, knownElements);

            // 3. Setup code JSX (local functions, variable assignments with JSX)
            CollectSetupCodeJsxTokens(parseResult, source, lineStarts, tokens, knownElements);

            return NormalizeTokenConflicts(tokens)
                .OrderBy(t => t.Line)
                .ThenBy(t => t.Column)
                .ToArray();
        }

        private static IEnumerable<SemanticTokenData> NormalizeTokenConflicts(
            List<SemanticTokenData> tokens
        )
        {
            var merged = new Dictionary<(int Line, int Col, int Len), SemanticTokenData>();

            foreach (var token in tokens)
            {
                var key = (token.Line, token.Column, token.Length);
                if (!merged.TryGetValue(key, out var existing))
                {
                    merged[key] = token;
                    continue;
                }

                bool preferFunction =
                    existing.TokenType == SemanticTokenTypes.Variable
                    && token.TokenType == SemanticTokenTypes.Function;

                bool preferComment =
                    existing.TokenType != SemanticTokenTypes.Comment
                    && token.TokenType == SemanticTokenTypes.Comment;

                if (preferFunction || preferComment)
                    merged[key] = token;
            }

            // Suppress non-comment tokens that are contained within a comment
            // token on the same line (e.g. setCount(...) inside {/* ... */}).
            var commentTokens = merged
                .Values.Where(t => t.TokenType == SemanticTokenTypes.Comment)
                .ToList();

            if (commentTokens.Count > 0)
            {
                var keysToRemove = new List<(int, int, int)>();
                foreach (var kvp in merged)
                {
                    var t = kvp.Value;
                    if (t.TokenType == SemanticTokenTypes.Comment)
                        continue;

                    foreach (var ct in commentTokens)
                    {
                        if (
                            ct.Line == t.Line
                            && t.Column >= ct.Column
                            && t.Column + t.Length <= ct.Column + ct.Length
                        )
                        {
                            keysToRemove.Add(kvp.Key);
                            break;
                        }
                    }
                }

                foreach (var key in keysToRemove)
                    merged.Remove(key);
            }

            return merged.Values;
        }

        // ── Import / export tokens (import/export grammar §10) ───────────────

        // Full ES import surface (ES-modules campaign, G-05/M5): braced named lists (with
        // optional `as` renames), `* as X` namespace imports, and bare default imports.
        private static readonly Regex s_importTokenRe = new Regex(
            @"^(?<lead>\s*)(?<import>import)\s*(?:[A-Za-z_]\w*\s*,\s*)?(?:\{(?<names>[^}]*)\}|\*\s*(?<staras>as)\s+[A-Za-z_]\w*|[A-Za-z_]\w*)\s*(?<from>from)\s*(?<spec>""[^""]*"")",
            RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );

        // The `as` keyword inside a braced import list: `import { a as b, c as d }`.
        private static readonly Regex s_importAsRe = new Regex(
            @"\b(?<as>as)\b",
            RegexOptions.CultureInvariant | RegexOptions.Compiled
        );

        // Export keyword — wrapper forms, plain declarations, `export default`, and
        // `export { … }` lists (ES-modules campaign).
        private static readonly Regex s_exportTokenRe = new Regex(
            @"^\s*(?<export>export)\s+(?:(?<default>default)\b|\{|(?:component|hook|module)\b|[A-Za-z_(])",
            RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled
        );

        private static void CollectImportExportTokens(
            string source, int[] lineStarts, List<SemanticTokenData> tokens)
        {
            foreach (Match m in s_importTokenRe.Matches(source))
            {
                EmitGroup(tokens, lineStarts, m.Groups["import"], SemanticTokenTypes.Keyword);
                EmitGroup(tokens, lineStarts, m.Groups["staras"], SemanticTokenTypes.Keyword);
                EmitGroup(tokens, lineStarts, m.Groups["from"], SemanticTokenTypes.Keyword);
                EmitGroup(tokens, lineStarts, m.Groups["spec"], SemanticTokenTypes.String);
                var namesGroup = m.Groups["names"];
                if (namesGroup.Success && namesGroup.Length > 0)
                    foreach (Match asMatch in s_importAsRe.Matches(namesGroup.Value))
                        EmitToken(tokens,
                            OffsetToLineCol0(lineStarts, namesGroup.Index + asMatch.Groups["as"].Index).Item1,
                            OffsetToLineCol0(lineStarts, namesGroup.Index + asMatch.Groups["as"].Index).Item2,
                            asMatch.Groups["as"].Length, SemanticTokenTypes.Keyword, s_noMods);
            }
            foreach (Match m in s_exportTokenRe.Matches(source))
            {
                EmitGroup(tokens, lineStarts, m.Groups["export"], SemanticTokenTypes.Keyword);
                EmitGroup(tokens, lineStarts, m.Groups["default"], SemanticTokenTypes.Keyword);
            }
        }

        private static void EmitGroup(
            List<SemanticTokenData> tokens, int[] lineStarts, Group g, string type)
        {
            if (!g.Success || g.Length == 0)
                return;
            var (line0, col0) = OffsetToLineCol0(lineStarts, g.Index);
            EmitToken(tokens, line0, col0, g.Length, type, s_noMods);
        }

        /// <summary>Colors every imported BINDING name by the kind of the export it binds:
        /// components as elements, hooks/utils as functions, values as variables, legacy
        /// modules as types; star aliases are plain variable bindings. Resolves each import's
        /// target from disk (same degrade-silently contract as ImportScopeFacts). Semantic
        /// tokens override the grammar's uniform tint in every LSP client.</summary>
        private static void CollectImportBindingKindTokens(
            ParseResult parseResult,
            string source,
            int[] lineStarts,
            List<SemanticTokenData> tokens,
            string filePath)
        {
            var imports = parseResult.Directives.Imports;
            if (imports.IsDefaultOrEmpty)
                return;

            string importerDir = (System.IO.Path.GetDirectoryName(filePath) ?? string.Empty)
                .Replace('\\', '/');
            string rootDir = EffectiveNamespace.UiSourceRootDir(filePath) ?? importerDir;

            foreach (var imp in imports)
            {
                string? target;
                try
                {
                    target = ImportResolver.MapSpecifierToPath(
                        importerDir, imp.Specifier, rootDir, out _);
                }
                catch { continue; }
                if (target == null || !System.IO.File.Exists(target))
                    continue;

                Parser.DirectiveSet tds;
                try
                {
                    tds = Parser.DirectiveParser.Parse(
                        System.IO.File.ReadAllText(target), target,
                        new List<ParseDiagnostic>());
                }
                catch { continue; }

                var kindOf = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!tds.ComponentDeclarations.IsDefaultOrEmpty)
                    foreach (var c in tds.ComponentDeclarations)
                        kindOf[c.Name] = SemanticTokenTypes.Element;
                if (!tds.MemberDeclarations.IsDefaultOrEmpty)
                    foreach (var mem in tds.MemberDeclarations)
                        kindOf[mem.Name] = mem.Kind == Parser.DeclKind.Value
                            ? SemanticTokenTypes.Variable
                            : SemanticTokenTypes.Function;

                int line0 = imp.Line - 1;
                if (line0 < 0 || line0 >= lineStarts.Length)
                    continue;
                int lineStart = lineStarts[line0];
                int lineEnd = line0 + 1 < lineStarts.Length ? lineStarts[line0 + 1] : source.Length;
                string lineText = source.Substring(lineStart, lineEnd - lineStart);

                void EmitAt(int col, string name, string type)
                {
                    if (col >= 0 && name.Length > 0)
                        EmitToken(tokens, line0, col, name.Length, type, s_noMods);
                }
                int FindWord(string word, int fromCol)
                {
                    for (int idx = fromCol; idx >= 0 && idx < lineText.Length; )
                    {
                        idx = lineText.IndexOf(word, idx, StringComparison.Ordinal);
                        if (idx < 0) return -1;
                        bool leftOk = idx == 0 || !char.IsLetterOrDigit(lineText[idx - 1]) && lineText[idx - 1] != '_';
                        int after = idx + word.Length;
                        bool rightOk = after >= lineText.Length
                            || !char.IsLetterOrDigit(lineText[after]) && lineText[after] != '_';
                        if (leftOk && rightOk) return idx;
                        idx = after;
                    }
                    return -1;
                }

                var aliases = imp.Aliases.IsDefaultOrEmpty
                    ? System.Collections.Immutable.ImmutableArray<string?>.Empty : imp.Aliases;
                for (int k = 0; k < imp.Names.Length; k++)
                {
                    string name = imp.Names[k];
                    if (!kindOf.TryGetValue(name, out string? kind))
                        continue;
                    int nameCol = k < imp.NameColumns.Length ? imp.NameColumns[k] : FindWord(name, 0);
                    EmitAt(nameCol, name, kind);
                    string? alias = k < aliases.Length ? aliases[k] : null;
                    if (alias != null && nameCol >= 0)
                        EmitAt(FindWord(alias, nameCol + name.Length), alias, kind);
                }
                if (imp.IsStar && imp.StarAlias != null)
                {
                    int starIdx = lineText.IndexOf('*');
                    if (starIdx >= 0)
                        EmitAt(FindWord(imp.StarAlias, starIdx), imp.StarAlias,
                            SemanticTokenTypes.Variable);
                }
                if (imp.IsDefault && imp.DefaultAlias != null)
                {
                    string defKind = tds.DefaultExportName != null
                        && kindOf.TryGetValue(tds.DefaultExportName, out string? dk)
                        ? dk : SemanticTokenTypes.Variable;
                    EmitAt(FindWord(imp.DefaultAlias, 0), imp.DefaultAlias, defKind);
                }
            }
        }

        // ── Function-style declaration ───────────────────────────────────────

        private static void CollectFunctionStyleDeclarationTokens(
            string source,
            int[] lineStarts,
            List<SemanticTokenData> tokens
        )
        {
            int i = 0;
            while (i < source.Length && char.IsWhiteSpace(source[i]))
                i++;

            const string keyword = "component";
            if (
                i + keyword.Length > source.Length
                || !string.Equals(
                    source.Substring(i, keyword.Length),
                    keyword,
                    StringComparison.Ordinal
                )
            )
                return;

            var (line0, col0) = OffsetToLineCol0(lineStarts, i);
            EmitToken(tokens, line0, col0, keyword.Length, SemanticTokenTypes.Directive, s_noMods);

            int j = i + keyword.Length;
            while (j < source.Length && (source[j] == ' ' || source[j] == '\t'))
                j++;

            int nameStart = j;
            while (j < source.Length && (char.IsLetterOrDigit(source[j]) || source[j] == '_'))
                j++;

            if (j > nameStart)
            {
                var (nameLine0, nameCol0) = OffsetToLineCol0(lineStarts, nameStart);
                EmitToken(
                    tokens,
                    nameLine0,
                    nameCol0,
                    j - nameStart,
                    SemanticTokenTypes.DirectiveName,
                    s_noMods
                );
            }
        }

        // ── Setup code JSX tokens ────────────────────────────────────────────

        /// <summary>
        /// Parses JSX ranges embedded in function-style setup code (local
        /// functions, variable assignments) and collects semantic tokens for
        /// the markup inside them.
        /// </summary>
        private static void CollectSetupCodeJsxTokens(
            ParseResult parseResult,
            string source,
            int[] lineStarts,
            List<SemanticTokenData> tokens,
            HashSet<string>? knownElements
        )
        {
            var d = parseResult.Directives;
            var allRanges = d.SetupCodeMarkupRanges;
            if (!d.SetupCodeBareJsxRanges.IsDefaultOrEmpty)
            {
                allRanges = allRanges.IsDefaultOrEmpty
                    ? d.SetupCodeBareJsxRanges
                    : allRanges.AddRange(d.SetupCodeBareJsxRanges);
            }
            if (allRanges.IsDefaultOrEmpty)
                return;

            var diags = new List<ParseDiagnostic>();
            foreach (var (jsxStart, jsxEnd, jsxLine) in allRanges)
            {
                var jsxDirectives = d with
                {
                    MarkupStartIndex = jsxStart,
                    MarkupEndIndex = jsxEnd,
                    MarkupStartLine = jsxLine,
                };
                diags.Clear();
                var jsxNodes = UitkxParser.Parse(source, "", jsxDirectives, diags);
                foreach (var n in jsxNodes)
                    CollectNodeTokens(n, source, lineStarts, tokens, knownElements);
            }
        }

        // ── AST node walk ─────────────────────────────────────────────────────

        /// <summary>
        /// Emits semantic tokens for <paramref name="node"/>.
        /// </summary>
        private static void CollectNodeTokens(
            AstNode node,
            string source,
            int[] lineStarts,
            List<SemanticTokenData> tokens,
            HashSet<string>? knownElements
        )
        {
            switch (node)
            {
                case ElementNode el:
                    CollectElementTokens(el, source, lineStarts, tokens, knownElements);
                    break;

                case IfNode ifN:
                    CollectIfTokens(ifN, source, lineStarts, tokens, knownElements);
                    break;

                case ForeachNode fe:
                    EmitKeyword(tokens, source, lineStarts, fe.SourceLine, "@foreach");
                    CollectBodyNodes(fe.Payload.Body, source, lineStarts, tokens, knownElements);
                    break;

                case ForNode fo:
                    EmitKeyword(tokens, source, lineStarts, fo.SourceLine, "@for");
                    CollectBodyNodes(fo.Payload.Body, source, lineStarts, tokens, knownElements);
                    break;

                case WhileNode wh:
                    EmitKeyword(tokens, source, lineStarts, wh.SourceLine, "@while");
                    CollectBodyNodes(wh.Payload.Body, source, lineStarts, tokens, knownElements);
                    break;

                case SwitchNode sw:
                    CollectSwitchTokens(sw, source, lineStarts, tokens, knownElements);
                    break;

                // U-30: ExpressionNode always originates from {expr} syntax (curly braces) —
                // `@(expr)` is removed syntax that never parses into an ExpressionNode (see
                // UitkxParser's AtExprNotSupported diagnostic). This case searched the source
                // line for the literal "@(" and, since that text is never actually there,
                // always silently found nothing — dead code that could, in the rare case an
                // unrelated "@(" substring appeared elsewhere on the same line (e.g. inside a
                // verbatim string), have highlighted the wrong text as a directive token.
                case ExpressionNode:
                    break;

                case CommentNode jc:
                    EmitCommentTokens(jc, source, lineStarts, tokens);
                    break;

                case TextNode _:
                    break; // plain text — no semantic tokens
            }
        }

        // ── Body-list traversal ────────────────────────────────────────────

        private static void CollectBodyNodes(
            IEnumerable<AstNode> body,
            string source,
            int[] lineStarts,
            List<SemanticTokenData> tokens,
            HashSet<string>? knownElements
        )
        {
            foreach (var node in body)
                CollectNodeTokens(node, source, lineStarts, tokens, knownElements);
        }

        // ── Element ───────────────────────────────────────────────────────────

        private static void CollectElementTokens(
            ElementNode el,
            string source,
            int[] lineStarts,
            List<SemanticTokenData> tokens,
            HashSet<string>? knownElements
        )
        {
            var mods = s_noMods;

            // Open-tag name column. Prefer the parser-tracked SourceColumn (U-31):
            // ParseElement always records the 0-based column of the opening '<', so
            // the tag name starts one character past it. Falling back to a textual
            // "<TagName" search (below) finds the FIRST occurrence on the line and
            // collides when two elements with the same tag name share a line, e.g.
            // <Label text="a" /><Label text="b" />.
            int openNameCol = el.SourceColumn > 0
                ? el.SourceColumn + 1
                : FindOpenTagName(source, lineStarts, el.SourceLine, el.TagName);
            if (openNameCol >= 0)
                EmitTagNameTokens(tokens, el.SourceLine - 1, openNameCol, el.TagName, mods);

            // Attribute names (always emit — attributes on unknown elements are still valid)
            foreach (var attr in el.Attributes)
            {
                int attrCol = FindOnLine(source, lineStarts, attr.SourceLine, attr.Name);
                if (attrCol >= 0)
                    EmitToken(
                        tokens,
                        attr.SourceLine - 1,
                        attrCol,
                        attr.Name.Length,
                        SemanticTokenTypes.Attribute,
                        mods
                    );
            }

            // Recurse into children
            foreach (var child in el.Children)
                CollectNodeTokens(child, source, lineStarts, tokens, knownElements);

            // Close-tag name (block elements only — self-closing have CloseTagLine == 0)
            if (el.CloseTagLine > 0)
            {
                int closeNameCol = FindCloseTagName(
                    source,
                    lineStarts,
                    el.CloseTagLine,
                    el.TagName
                );
                if (closeNameCol >= 0)
                    EmitTagNameTokens(tokens, el.CloseTagLine - 1, closeNameCol, el.TagName, mods);
            }
        }

        /// <summary>Tag-name token(s). Dotted tags (U-05, <c>&lt;X.Comp/&gt;</c>) split at the
        /// dot: the namespace-import binding <c>X</c> colors as a variable, the component part
        /// as an element — matching how the import line itself is colored.</summary>
        private static void EmitTagNameTokens(
            List<SemanticTokenData> tokens, int line0, int nameCol, string tagName, string[] mods)
        {
            int dot = tagName.IndexOf('.');
            if (dot > 0 && dot < tagName.Length - 1)
            {
                EmitToken(tokens, line0, nameCol, dot, SemanticTokenTypes.Variable, mods);
                EmitToken(
                    tokens, line0, nameCol + dot + 1, tagName.Length - dot - 1,
                    SemanticTokenTypes.Element, mods);
                return;
            }
            EmitToken(tokens, line0, nameCol, tagName.Length, SemanticTokenTypes.Element, mods);
        }

        // ── @if / @else if / @else ────────────────────────────────────────────

        private static void CollectIfTokens(
            IfNode ifNode,
            string source,
            int[] lineStarts,
            List<SemanticTokenData> tokens,
            HashSet<string>? knownElements
        )
        {
            bool isFirst = true;
            foreach (var branch in ifNode.Branches)
            {
                if (isFirst)
                {
                    EmitKeyword(tokens, source, lineStarts, branch.SourceLine, "@if");
                    isFirst = false;
                }
                else
                {
                    // '@else if (cond)' or '@else'  – emit '@else' in both cases
                    EmitKeyword(tokens, source, lineStarts, branch.SourceLine, "@else");
                    // If there is a condition, also emit 'if' keyword that follows '@else '
                    if (branch.Condition != null)
                    {
                        int elseCol = FindOnLine(source, lineStarts, branch.SourceLine, "@else");
                        if (elseCol >= 0)
                        {
                            // '@else' is 5 chars; skip it plus 1 space → look for 'if'
                            int ifCol = FindOnLine(
                                source,
                                lineStarts,
                                branch.SourceLine,
                                "if",
                                elseCol + "@else".Length
                            );
                            if (ifCol >= 0)
                                EmitToken(
                                    tokens,
                                    branch.SourceLine - 1,
                                    ifCol,
                                    2,
                                    SemanticTokenTypes.Directive,
                                    s_noMods
                                );
                        }
                    }
                }

                CollectBodyNodes(branch.Payload.Body, source, lineStarts, tokens, knownElements);
            }
        }

        // ── @switch / @case / @default ────────────────────────────────────────

        private static void CollectSwitchTokens(
            SwitchNode sw,
            string source,
            int[] lineStarts,
            List<SemanticTokenData> tokens,
            HashSet<string>? knownElements
        )
        {
            EmitKeyword(tokens, source, lineStarts, sw.SourceLine, "@switch");

            foreach (var c in sw.Cases)
            {
                string kw = c.ValueExpression != null ? "@case" : "@default";
                EmitKeyword(tokens, source, lineStarts, c.SourceLine, kw);

                CollectBodyNodes(c.Payload.Body, source, lineStarts, tokens, knownElements);
            }
        }

        // ── @code body scanner ────────────────────────────────────────────────

        private static void EmitCommentTokens(
            CommentNode jc,
            string source,
            int[] lineStarts,
            List<SemanticTokenData> tokens
        )
        {
            // Determine the marker to search for in source based on comment kind
            string marker = jc.IsBlock ? "/*" : "//";
            int col = FindOnLine(source, lineStarts, jc.SourceLine, marker);
            if (col < 0)
                return;

            int startOffset = lineStarts[jc.SourceLine - 1] + col;

            if (!jc.IsBlock)
            {
                // Line comment — single token to end of line
                int lineEnd =
                    jc.SourceLine < lineStarts.Length ? lineStarts[jc.SourceLine] : source.Length;
                // Trim trailing newline chars
                while (
                    lineEnd > startOffset
                    && (source[lineEnd - 1] == '\r' || source[lineEnd - 1] == '\n')
                )
                    lineEnd--;
                int len = lineEnd - startOffset;
                if (len > 0)
                {
                    EmitToken(
                        tokens,
                        jc.SourceLine - 1,
                        col,
                        len,
                        SemanticTokenTypes.Comment,
                        s_noMods
                    );
                }
                return;
            }

            // Block comment — find closing */
            int closeAt = source.IndexOf("*/", startOffset + 2, StringComparison.Ordinal);
            if (closeAt < 0)
            {
                int fallbackLen = "/*".Length + jc.Content.Length + "*/".Length;
                EmitToken(
                    tokens,
                    jc.SourceLine - 1,
                    col,
                    fallbackLen,
                    SemanticTokenTypes.Comment,
                    s_noMods
                );
                return;
            }

            int endExclusive = closeAt + 2;
            int startLineIdx = Array.BinarySearch(lineStarts, startOffset);
            if (startLineIdx < 0)
                startLineIdx = (~startLineIdx) - 1;
            if (startLineIdx < 0)
                startLineIdx = 0;

            for (int li = startLineIdx; li < lineStarts.Length; li++)
            {
                int lineStart = lineStarts[li];
                if (lineStart >= endExclusive)
                    break;

                int lineEnd = li + 1 < lineStarts.Length ? lineStarts[li + 1] : source.Length;
                int segStart = Math.Max(lineStart, startOffset);
                int segEnd = Math.Min(lineEnd, endExclusive);
                while (
                    segEnd > segStart && (source[segEnd - 1] == '\r' || source[segEnd - 1] == '\n')
                )
                    segEnd--;

                if (segEnd > segStart)
                {
                    EmitToken(
                        tokens,
                        li,
                        segStart - lineStart,
                        segEnd - segStart,
                        SemanticTokenTypes.Comment,
                        s_noMods
                    );
                }
            }
        }

        private static (int Line0, int Col0) OffsetToLineCol0(int[] lineStarts, int offset)
        {
            int idx = Array.BinarySearch(lineStarts, offset);
            if (idx < 0)
                idx = (~idx) - 1;
            if (idx < 0)
                idx = 0;

            int col = offset - lineStarts[idx];
            if (col < 0)
                col = 0;

            return (idx, col);
        }

        // ── Position helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Returns the 0-based column of the element tag name inside its
        /// open tag (<c>&lt;TagName</c>) on the given 1-based source line.
        /// Close tags (<c>&lt;/TagName&gt;</c>) are excluded.
        /// Returns -1 if not found.
        /// </summary>
        private static int FindOpenTagName(
            string source,
            int[] lineStarts,
            int line1,
            string tagName
        )
        {
            // Search for '<TagName'. Close tags start with '</' so they won't
            // match because the char after '<' would be '/' not the tag name.
            int col = FindOnLine(source, lineStarts, line1, "<" + tagName);
            if (col < 0)
                return -1;
            return col + 1; // +1 to skip the '<' character
        }

        /// <summary>
        /// Returns the 0-based column of the tag name inside a close tag
        /// (<c>&lt;/TagName&gt;</c>) on the given 1-based source line.
        /// Returns -1 if not found.
        /// </summary>
        private static int FindCloseTagName(
            string source,
            int[] lineStarts,
            int line1,
            string tagName
        )
        {
            int col = FindOnLine(source, lineStarts, line1, "</" + tagName);
            if (col < 0)
                return -1;
            return col + 2; // +2 to skip '</'
        }

        /// <summary>
        /// Returns the 0-based column of the first occurrence of
        /// <paramref name="search"/> on the given 1-based source line,
        /// starting at character offset <paramref name="startCol"/>.
        /// Returns -1 if not found.
        /// </summary>
        private static int FindOnLine(
            string source,
            int[] lineStarts,
            int line1,
            string search,
            int startCol = 0
        )
        {
            if (line1 < 1 || line1 > lineStarts.Length)
                return -1;

            int lineStart = lineStarts[line1 - 1];
            int lineEnd = line1 < lineStarts.Length ? lineStarts[line1] : source.Length;
            int from = lineStart + startCol;
            if (from >= lineEnd || from >= source.Length)
                return -1;

            int searchLen = lineEnd - from;
            if (searchLen <= 0)
                return -1;

            int idx = source.IndexOf(search, from, searchLen, StringComparison.Ordinal);
            return idx >= 0 ? idx - lineStart : -1;
        }

        /// <summary>
        /// Searches for <paramref name="keyword"/> on <paramref name="line1"/>
        /// and emits a <see cref="SemanticTokenTypes.Directive"/> token.
        /// </summary>
        private static void EmitKeyword(
            List<SemanticTokenData> tokens,
            string source,
            int[] lineStarts,
            int line1,
            string keyword
        )
        {
            int col = FindOnLine(source, lineStarts, line1, keyword);
            if (col >= 0)
                EmitToken(
                    tokens,
                    line1 - 1,
                    col,
                    keyword.Length,
                    SemanticTokenTypes.Directive,
                    s_noMods
                );
        }

        private static void EmitToken(
            List<SemanticTokenData> tokens,
            int line0,
            int col0,
            int length,
            string tokenType,
            string[] modifiers
        )
        {
            if (length <= 0)
                return;
            tokens.Add(
                new SemanticTokenData
                {
                    Line = line0,
                    Column = col0,
                    Length = length,
                    TokenType = tokenType,
                    Modifiers = modifiers,
                }
            );
        }

        // ── Source scanning utility ───────────────────────────────────────────

        /// <summary>
        /// Builds a lookup array where <c>lineStarts[i]</c> is the character
        /// index in <paramref name="source"/> where 0-based line <c>i</c> begins.
        /// </summary>
        private static int[] BuildLineStarts(string source)
        {
            var starts = new List<int>(64) { 0 };
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    starts.Add(i + 1);
                }
                else if (source[i] == '\r')
                {
                    // Handle both \r and \r\n
                    if (i + 1 < source.Length && source[i + 1] == '\n')
                        i++;
                    starts.Add(i + 1);
                }
            }
            return starts.ToArray();
        }
    }
}
