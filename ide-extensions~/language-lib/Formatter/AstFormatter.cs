using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;

namespace Ruitk.Language.Formatter
{
    /// <summary>
    /// Why a <see cref="AstFormatter.Format(string, string, out FormatOutcome)"/> pass
    /// produced its result. Every value except <see cref="Formatted"/> means the input
    /// was returned unchanged by a deliberate data-loss guard — a caller performing a
    /// write-back (the RUITK Builder) must treat those as failures, never as "already
    /// canonical".
    /// </summary>
    public enum FormatOutcome
    {
        Formatted,
        ParseErrors,
        InlineJsxControlFlow,
        MultipleComponents,
        MissingComponentName,
    }

    /// <summary>
    /// AST-based formatter for <c>.uitkx</c> files.
    ///
    /// Call <see cref="Format"/> with the source text; on any parse error the
    /// original source is returned unchanged so the formatter can never corrupt a
    /// file.  Formatting options come from <see cref="FormatterOptions"/>; load
    /// them from disk with <see cref="ConfigLoader.LoadFormatterOptions"/>.
    ///
    /// Architecture: parse the source into a <see cref="ParseResult"/>, walk the
    /// AST tree, and emit canonical text into a <see cref="StringBuilder"/>.
    /// No regular-expression post-processing is needed because we work from the
    /// structured representation, not from the original character stream.
    /// </summary>
    public sealed class AstFormatter
    {
        private readonly FormatterOptions _opts;
        private readonly ICSharpFormatterDelegate? _csharpFormatter;
        private readonly StringBuilder _sb = new StringBuilder();
        private int _indent;

        // Set at the start of each Format() call; used by EmitSetupCodeWithJsx
        // and EmitBodySetupCodeWithJsx.
        private string _source = "";
        private string _filePath = "";
        private DirectiveSet _directives = null!;

        public AstFormatter(FormatterOptions opts, ICSharpFormatterDelegate? csharpFormatter = null)
        {
            _opts = opts;
            _csharpFormatter = csharpFormatter;
        }

        /// <summary>
        /// Emits the canonical preamble order (namespace-import unification plan) and returns whether
        /// anything was emitted. Single source of ordering truth for BOTH the component and
        /// hook/module format paths, so they can never drift:
        /// <list type="number">
        ///   <item><c>@namespace</c> — the file's own identity, first (only when explicit).</item>
        ///   <item>File imports (<c>import { X } from "./…"</c>) — peer <c>.uitkx</c> exports.</item>
        ///   <item>Namespace imports (<c>import "@Ns"</c> / <c>@using Ns</c>) — grouped with the file
        ///   imports so all <c>import</c> lines are contiguous under the identity line.</item>
        ///   <item><c>@uss</c> stylesheet attachments (component files only).</item>
        /// </list>
        /// </summary>
        private bool EmitPreamble(DirectiveSet directives, bool includeUss)
        {
            bool has = false;

            if (!string.IsNullOrEmpty(directives.Backend))
            {
                Ln($"@backend {directives.Backend}");
                has = true;
            }

            if (directives.HasExplicitNamespace && !string.IsNullOrWhiteSpace(directives.Namespace))
            {
                Ln($"@namespace {directives.Namespace}");
                has = true;
            }

            // U-10 canonical file-import grouping: named → `* as` → default, each group stable
            // by first appearance. The imports-then-usings relative order is the pre-existing
            // pinned canonical order (legacy output stays byte-identical — campaign guardrail).
            if (!directives.Imports.IsDefaultOrEmpty)
            {
                void EmitImport(ImportDeclaration imp)
                {
                    // Canonical spellings (U-10): named (with `as` renames preserved), `* as`,
                    // default, and the ES COMBINED forms (`import Def, { a } from` /
                    // `import Def, * as X from`) — all semicolon-less (the parser tolerates
                    // `;` on read). One declaration = one line; every part prints.
                    bool isDef = imp.IsDefault && imp.DefaultAlias != null;
                    bool isStar = imp.IsStar && imp.StarAlias != null;
                    var pieces = new List<string>(2);
                    if (isDef)
                        pieces.Add(imp.DefaultAlias!);
                    if (isStar)
                        pieces.Add($"* as {imp.StarAlias}");
                    if (imp.Names.Length > 0 || (!isDef && !isStar))
                    {
                        bool hasAliases = !imp.Aliases.IsDefaultOrEmpty;
                        var entries = new List<string>(imp.Names.Length);
                        for (int ni = 0; ni < imp.Names.Length; ni++)
                        {
                            string? alias = hasAliases && ni < imp.Aliases.Length ? imp.Aliases[ni] : null;
                            entries.Add(alias != null ? $"{imp.Names[ni]} as {alias}" : imp.Names[ni]);
                        }
                        pieces.Add($"{{ {string.Join(", ", entries)} }}");
                    }
                    Ln($"import {string.Join(", ", pieces)} from \"{imp.Specifier}\"");
                    has = true;
                }

                foreach (var imp in directives.Imports)
                    if (!(imp.IsStar && imp.StarAlias != null) && !(imp.IsDefault && imp.DefaultAlias != null))
                        EmitImport(imp);
                foreach (var imp in directives.Imports)
                    if (imp.IsStar && imp.StarAlias != null && !(imp.IsDefault && imp.DefaultAlias != null))
                        EmitImport(imp);
                foreach (var imp in directives.Imports)
                    if (imp.IsDefault && imp.DefaultAlias != null)
                        EmitImport(imp);
            }

            foreach (string usingLine in FormatUsings(directives))
            {
                Ln(usingLine);
                has = true;
            }

            if (includeUss && !directives.UssFiles.IsDefaultOrEmpty)
                foreach (var uss in directives.UssFiles)
                {
                    Ln($"@uss \"{uss}\"");
                    has = true;
                }

            return has;
        }

        /// <summary>
        /// Formats each preamble using line in the spelling the author used — <c>import "@Ns"</c> when
        /// it came from the namespace-import form, else <c>@using Ns</c> — so a format pass round-trips
        /// both (namespace-import unification plan). Falls back to <c>@using</c> for any payload with no
        /// positioned <see cref="UsingDirective"/> (e.g. a synthesised entry). The formatter does NOT
        /// auto-convert between spellings — that is the codemod's <c>--tidy</c> job.
        /// </summary>
        private static IEnumerable<string> FormatUsings(DirectiveSet directives)
        {
            var uds = directives.UsingDirectives;
            if (!uds.IsDefaultOrEmpty && uds.Length == directives.Usings.Length)
            {
                foreach (var u in uds)
                    yield return u.FromImportSyntax ? $"import \"@{u.Payload}\"" : $"@using {u.Payload}";
                yield break;
            }
            foreach (var u in directives.Usings)
                yield return $"@using {u}";
        }

        public AstFormatter()
            : this(FormatterOptions.Default) { }

        // -----------------------------------------------------------------------
        //  PUBLIC ENTRY POINT
        // -----------------------------------------------------------------------

        /// <summary>
        /// Parse <paramref name="source"/>, format it, and return the result.
        /// Returns the original source unchanged if there are any parse errors.
        /// <paramref name="filePath"/> is only used for diagnostic position
        /// reporting and may be empty.
        /// </summary>
        public string Format(string source, string filePath = "")
        {
            return Format(source, filePath, out _);
        }

        /// <summary>
        /// Same as <see cref="Format(string, string)"/> but surfaces WHY a pass
        /// returned its input unchanged. Callers that treat "output == input" as
        /// meaningful (the RUITK Builder treats it as a hard error on a dirty
        /// transaction) must use this overload — the identity fallbacks below are
        /// deliberate data-loss guards, not formatting successes.
        /// </summary>
        public string Format(string source, string filePath, out FormatOutcome outcome)
        {
            // Normalise to LF-only so all AppendLine helpers stay consistent.
            source = source.Replace("\r\n", "\n").Replace("\r", "\n");

            var diags = new List<ParseDiagnostic>();
            var directives = DirectiveParser.Parse(source, filePath, diags);
            var nodes = UitkxParser.Parse(source, filePath, directives, diags);

            bool hasErrors = false;
            foreach (var d in diags)
                if (d.Severity == ParseSeverity.Error)
                {
                    hasErrors = true;
                    break;
                }

            return FormatCore(source, filePath, directives, nodes, hasErrors, out outcome);
        }

        /// <summary>
        /// Printer entry point (VE-14): format an already-parsed file WITHOUT the
        /// internal re-parse. CONTRACT: <paramref name="source"/> must be the exact
        /// LF-normalized text <paramref name="parseResult"/> was parsed from — the
        /// emitters slice setup-code ranges by offset into it, so any drift between
        /// the two silently corrupts output. A source containing CR is therefore
        /// rejected loudly instead of normalized (normalizing would shift every
        /// parsed offset).
        /// </summary>
        public string Format(
            ParseResult parseResult,
            string source,
            string filePath,
            out FormatOutcome outcome
        )
        {
            if (parseResult == null)
                throw new ArgumentNullException(nameof(parseResult));
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (source.IndexOf('\r') >= 0)
                throw new ArgumentException(
                    "source must be LF-normalized and identical to the text the ParseResult "
                        + "was parsed from - offsets in the parse result index into it.",
                    nameof(source)
                );

            bool hasErrors = false;
            foreach (var d in parseResult.Diagnostics)
                if (d.Severity == ParseSeverity.Error)
                {
                    hasErrors = true;
                    break;
                }

            return FormatCore(
                source,
                filePath,
                parseResult.Directives,
                parseResult.RootNodes,
                hasErrors,
                out outcome
            );
        }

        private string FormatCore(
            string source,
            string filePath,
            DirectiveSet directives,
            ImmutableArray<AstNode> nodes,
            bool hasParseErrors,
            out FormatOutcome outcome
        )
        {
            _sb.Clear();
            _indent = 0;
            _source = source;
            _filePath = filePath;
            _directives = directives;

            if (hasParseErrors)
            {
                outcome = FormatOutcome.ParseErrors;
                return source;
            }

            // U-02: inline-attribute JSX (attr={<Tag>...</Tag>}) is serialized to a
            // single line by SerializeJsxInlineCore, which cannot represent a
            // multi-branch/multi-statement control-flow child (@if/@foreach/@for/
            // @while/@switch). AttributeNode carries no raw source span to fall back
            // to, so the safe minimal guarantee is: detect this shape up front and
            // leave the whole file untouched rather than silently dropping content.
            if (ContainsInlineJsxControlFlow(nodes))
            {
                outcome = FormatOutcome.InlineJsxControlFlow;
                return source;
            }

            // -- Plain-declaration files (ES-modules campaign, U-10) --
            // Must be checked FIRST: these files carry MemberDeclarations/DefaultExportName/
            // ExportListNames that no other path re-emits — falling through would DELETE them
            // (the A7f data-loss guard extended to the new record fields).
            if (
                !directives.MemberDeclarations.IsDefaultOrEmpty
                || !directives.ComponentDeclarations.IsDefaultOrEmpty
                || directives.DefaultExportName != null
                || !directives.ExportListNames.IsDefaultOrEmpty
            )
            {
                // Losslessly re-formatting the 2nd+ component needs per-component markup
                // re-parsing this formatter does not do — safe minimal guarantee: leave the
                // whole file untouched (same precedent as ContainsInlineJsxControlFlow).
                if (directives.ComponentDeclarations.Length > 1)
                {
                    outcome = FormatOutcome.MultipleComponents;
                    return source;
                }
                FormatPlainDeclarationFile(directives, nodes);
            }
            else if (directives.IsFunctionStyle)
            {
                // No component name means there is nothing this path can faithfully re-emit
                // (an empty or preamble-only file must NOT become a fabricated
                // `component Component { … }` — found by the audit's empty-file probe).
                if (string.IsNullOrEmpty(directives.ComponentName))
                {
                    outcome = FormatOutcome.MissingComponentName;
                    return source;
                }
                FormatFunctionStyleComponent(directives, nodes);
            }
            else
            {
                FormatNodeList(nodes, topLevel: true);
            }

            // Trim any trailing whitespace/newlines and add exactly one trailing \n.
            var result = _sb.ToString().TrimEnd('\r', '\n', ' ', '\t');
            outcome = FormatOutcome.Formatted;
            return result + "\n";
        }

        // -----------------------------------------------------------------------
        //  DIRECTIVES
        // -----------------------------------------------------------------------

        private void FormatFunctionStyleComponent(
            DirectiveSet directives,
            ImmutableArray<AstNode> nodes
        )
        {
            var componentName = string.IsNullOrWhiteSpace(directives.ComponentName)
                ? "Component"
                : directives.ComponentName;

            // -- Leading trivia: re-emit verbatim (license headers, etc. - see U-01) -
            // Appended directly (not via Ln) so multi-line block comments are reproduced
            // byte-for-byte rather than re-anchored to the current (zero) indent.
            bool hasLeadingTrivia = !directives.LeadingTrivia.IsDefaultOrEmpty;
            foreach (var (text, _, _) in directives.LeadingTrivia)
            {
                _sb.Append(text);
                _sb.Append('\n');
            }

            // -- Preamble: re-emit @namespace / import / @uss lines before the component block -
            // A7f data-loss guard: the formatter re-prints the preamble from DirectiveSet fields and
            // DROPS anything it does not model, so every preamble line MUST be re-emitted here or a
            // format pass would delete it. Canonical order (namespace-import unification plan) is
            // owned by EmitPreamble: @namespace → imports (grouped) → @uss.
            bool hasPreamble = EmitPreamble(directives, includeUss: true);

            if (hasPreamble || hasLeadingTrivia)
                _sb.Append('\n');

            string paramList = "";
            if (!directives.FunctionParams.IsDefaultOrEmpty)
            {
                var parts = directives
                    .FunctionParams.Select(p =>
                        p.DefaultValue != null
                            ? $"{p.Type} {p.Name} = {p.DefaultValue}"
                            : $"{p.Type} {p.Name}"
                    )
                    .ToList();
                paramList = $"({string.Join(", ", parts)})";
            }

            // A7f: re-emit the `export ` prefix (import/export grammar) or a format pass drops it.
            string exportPrefix =
                !directives.ComponentDeclarations.IsDefaultOrEmpty
                && directives.ComponentDeclarations[0].IsExported
                    ? "export "
                    : "";
            string headerLine = $"{exportPrefix}component {componentName}{paramList} {{";
            if (paramList.Length > 0 && headerLine.Length > _opts.PrintWidth)
            {
                // Wrap params one-per-line, each indented one level
                var parts = directives.FunctionParams.Select(p =>
                    p.DefaultValue != null
                        ? $"{p.Type} {p.Name} = {p.DefaultValue}"
                        : $"{p.Type} {p.Name}"
                );
                _sb.Append($"{exportPrefix}component {componentName}(\n");
                _indent++;
                var paramArray = parts.ToArray();
                for (int pi = 0; pi < paramArray.Length; pi++)
                {
                    bool isLast = pi == paramArray.Length - 1;
                    Ln(isLast ? paramArray[pi] : paramArray[pi] + ",");
                }
                _indent--;
                Ln(") {");
            }
            else
            {
                Ln(headerLine);
            }
            _indent++;

            EmitFunctionStyleComponentBodyAndClose(directives, nodes);
        }

        /// <summary>
        /// Emits a component's BODY (setup split, <c>return (…)</c> markup, unreachable
        /// after-return code) and the closing <c>}</c>. Extracted from
        /// <see cref="FormatFunctionStyleComponent"/> so the plain-declaration path (ES-modules
        /// campaign, U-10) shares it byte-for-byte — only the HEADER differs between the
        /// grammars. Caller has already emitted the header + opening brace and bumped
        /// <c>_indent</c>; this restores it.
        /// </summary>
        private void EmitFunctionStyleComponentBodyAndClose(
            DirectiveSet directives,
            ImmutableArray<AstNode> nodes
        )
        {
            var fullSetupCode = directives.FunctionSetupCode?.Trim();
            string? beforeReturnCode = fullSetupCode;
            string? afterReturnCode = null;

            // When setup code is a concatenation of code-before-return +
            // code-after-return (gap left by the removed return statement),
            // split it so the return stays in its original position and
            // formatting is idempotent.
            if (
                fullSetupCode != null
                && directives.FunctionSetupGapOffset >= 0
                && directives.FunctionSetupGapOffset < fullSetupCode.Length
            )
            {
                beforeReturnCode = fullSetupCode
                    .Substring(0, directives.FunctionSetupGapOffset)
                    .TrimEnd();
                afterReturnCode = fullSetupCode
                    .Substring(directives.FunctionSetupGapOffset)
                    .TrimStart();
                if (string.IsNullOrWhiteSpace(beforeReturnCode))
                    beforeReturnCode = null;
                if (string.IsNullOrWhiteSpace(afterReturnCode))
                    afterReturnCode = null;
            }

            if (!string.IsNullOrWhiteSpace(beforeReturnCode))
            {
                string tabExp = new string(' ', _opts.IndentSize);
                string codeToFormat = NormalizeBareJsx(
                    beforeReturnCode!,
                    out var insertedPositions,
                    out var normalizedToRawOffset
                );

                // Check for JSX paren blocks OR bare JSX in the before-return portion only.
                bool hasJsxInSetup = false;
                int gapSrcOffset =
                    directives.FunctionSetupStartOffset >= 0
                        ? directives.FunctionSetupStartOffset + directives.FunctionSetupGapOffset
                        : int.MaxValue;
                if (!directives.SetupCodeMarkupRanges.IsDefaultOrEmpty)
                {
                    foreach (var (s, _, _) in directives.SetupCodeMarkupRanges)
                    {
                        if (s < gapSrcOffset)
                        {
                            hasJsxInSetup = true;
                            break;
                        }
                    }
                }
                if (!hasJsxInSetup && !directives.SetupCodeBareJsxRanges.IsDefaultOrEmpty)
                {
                    foreach (var (s, _, _) in directives.SetupCodeBareJsxRanges)
                    {
                        if (s < gapSrcOffset)
                        {
                            hasJsxInSetup = true;
                            break;
                        }
                    }
                }

                if (hasJsxInSetup)
                {
                    EmitSetupCodeWithJsx(
                        codeToFormat,
                        directives,
                        tabExp,
                        insertedPositions,
                        normalizedToRawOffset
                    );
                }
                else
                {
                    EmitSetupCodeNormalized(codeToFormat, tabExp);
                    _sb.Append('\n');
                }
            }

            // Null-only component (React case 2): there is no markup return to
            // print — the explicit `return null;` is part of the setup code
            // emitted above. Synthesizing a `return ( … );` here would inject
            // markup the author never wrote.
            if (directives.HasNullReturn)
            {
                while (
                    _sb.Length >= 2
                    && _sb[_sb.Length - 1] == '\n'
                    && _sb[_sb.Length - 2] == '\n'
                )
                    _sb.Length--;
                _indent--;
                Ln("}");
                return;
            }

            Ln("return (");
            _indent++;
            FormatNodeList(nodes, topLevel: false);
            _indent--;
            Ln(");");

            // Emit unreachable code after return (if any) with basic
            // indentation normalization - this preserves the structure
            // of the file without rearranging returns.
            if (!string.IsNullOrWhiteSpace(afterReturnCode))
            {
                string tabExp = new string(' ', _opts.IndentSize);
                _sb.Append('\n');
                EmitSetupCodeNormalized(afterReturnCode!, tabExp);
                _sb.Append('\n');
            }

            _indent--;
            Ln("}");
        }

        // -----------------------------------------------------------------------
        //  PLAIN-DECLARATION FILES (ES-modules campaign, U-10)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Formats a NEW-MODE file: preamble (full import surface), then every declaration in
        /// SOURCE ORDER — values (<c>[export] [Type] name = init;</c>, the inference form kept
        /// type-less), utils/hooks (<c>[export] Ret Name(params) { body }</c> / <c>=&gt; expr;</c>,
        /// raw params text preserved for ref/out/params fidelity — R5), and the (single)
        /// component with a plain <c>VirtualNode</c> header sharing the legacy body machinery —
        /// then <c>export { … };</c> lists and <c>export default X;</c> at end of file (U-10).
        /// Inline <c>export</c> prefixes are printed ONLY for names not exported via a list
        /// (printing both would re-parse as UITKX2324).
        /// </summary>
        private void FormatPlainDeclarationFile(
            DirectiveSet directives,
            ImmutableArray<AstNode> nodes
        )
        {
            // LeadingTrivia holds EVERY comment the declaration loop skipped — including ones
            // BETWEEN declarations (each entry carries its source line). Emitting them all at the
            // top would hoist an author's "// belongs to B" comment away from B (found by probe
            // test). Interleave positionally instead: entries before the first declaration print
            // here; the declaration loop prints the rest just before the declaration they precede.
            var trivia = directives.LeadingTrivia.IsDefaultOrEmpty
                ? System.Array.Empty<(string Text, bool IsBlock, int Line)>()
                : System.Linq.Enumerable.ToArray(
                    System.Linq.Enumerable.OrderBy(directives.LeadingTrivia, t => t.Line));
            int triviaIdx = 0;
            int firstDeclLine = int.MaxValue;
            if (!directives.MemberDeclarations.IsDefaultOrEmpty)
                foreach (var m0 in directives.MemberDeclarations)
                    if (m0.DeclarationLine < firstDeclLine) firstDeclLine = m0.DeclarationLine;
            if (directives.ComponentDeclarations.Length == 1
                && directives.ComponentDeclarations[0].DeclarationLine < firstDeclLine)
                firstDeclLine = directives.ComponentDeclarations[0].DeclarationLine;

            bool hasLeadingTrivia = false;
            while (triviaIdx < trivia.Length && trivia[triviaIdx].Line < firstDeclLine)
            {
                _sb.Append(trivia[triviaIdx].Text);
                _sb.Append('\n');
                hasLeadingTrivia = true;
                triviaIdx++;
            }

            bool hasPreamble = EmitPreamble(directives, includeUss: true);
            if (hasPreamble || hasLeadingTrivia)
                _sb.Append('\n');

            var listExported = new HashSet<string>(System.StringComparer.Ordinal);
            if (!directives.ExportListNames.IsDefaultOrEmpty)
                foreach (var n in directives.ExportListNames)
                    listExported.Add(n);

            // IsExportImplied: the export came from a list/default marking only — re-emitting an
            // inline prefix would rewrite the author's export surface (audit F8).
            string ExportPrefix(string name, bool isExported, bool exportImplied)
                => isExported && !exportImplied && !listExported.Contains(name) ? "export " : "";

            // A previously-wrapped head carries embedded newlines/indent in its raw text.
            static string CollapseHeadWhitespace(string s)
                => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

            // Top-level comma split of a tuple TYPE's items ("(a, b<c,d>, e)" -> 3 items).
            static string[] SplitTupleItems(string tupleText)
            {
                string inner = tupleText.Substring(1, tupleText.Length - 2);
                var parts = new List<string>();
                int depth = 0, start = 0;
                for (int k = 0; k < inner.Length; k++)
                {
                    char ch = inner[k];
                    if (ch == '(' || ch == '<' || ch == '[') depth++;
                    else if (ch == ')' || ch == '>' || ch == ']') depth--;
                    else if (ch == ',' && depth == 0)
                    {
                        parts.Add(inner.Substring(start, k - start).Trim());
                        start = k + 1;
                    }
                }
                parts.Add(inner.Substring(start).Trim());
                return parts.ToArray();
            }

            // Merge members + the single component in source order. OrderBy (stable) — a
            // same-line tie must never reorder declarations (audit F2).
            var entries = new List<(int Line, int MemberIndex, bool IsComponent)>();
            if (!directives.MemberDeclarations.IsDefaultOrEmpty)
                for (int i = 0; i < directives.MemberDeclarations.Length; i++)
                    entries.Add((directives.MemberDeclarations[i].DeclarationLine, i, false));
            bool hasComponent = directives.ComponentDeclarations.Length == 1;
            if (hasComponent)
                entries.Add((directives.ComponentDeclarations[0].DeclarationLine, -1, true));
            entries = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.OrderBy(entries, e => e.Line));

            string tabExp = new string(' ', _opts.IndentSize);
            bool firstDecl = true;
            foreach (var entry in entries)
            {
                if (!firstDecl)
                    _sb.Append('\n');
                firstDecl = false;

                // Interleaved trivia: comments the parser skipped just before THIS declaration.
                while (triviaIdx < trivia.Length && trivia[triviaIdx].Line < entry.Line)
                {
                    _sb.Append(trivia[triviaIdx].Text);
                    _sb.Append('\n');
                    triviaIdx++;
                }

                if (entry.IsComponent)
                {
                    var c = directives.ComponentDeclarations[0];
                    string[] compParams = c.FunctionParams.IsDefaultOrEmpty
                        ? System.Array.Empty<string>()
                        : c.FunctionParams.Select(p =>
                            p.DefaultValue != null
                                ? $"{p.Type} {p.Name} = {p.DefaultValue}"
                                : $"{p.Type} {p.Name}").ToArray();
                    string compPrefix = ExportPrefix(c.Name, c.IsExported, c.IsExportImplied);
                    string compHeader =
                        $"{compPrefix}VirtualNode {c.Name}({string.Join(", ", compParams)}) {{";
                    if (compParams.Length > 0 && compHeader.Length > _opts.PrintWidth)
                    {
                        // Long-head wrap: params one-per-line, one level in — the same
                        // shape the legacy component head used.
                        _sb.Append($"{compPrefix}VirtualNode {c.Name}(\n");
                        _indent++;
                        for (int pi = 0; pi < compParams.Length; pi++)
                            Ln(pi == compParams.Length - 1 ? compParams[pi] : compParams[pi] + ",");
                        _indent--;
                        Ln(") {");
                    }
                    else
                    {
                        Ln(compHeader);
                    }
                    _indent++;
                    EmitFunctionStyleComponentBodyAndClose(directives, nodes);
                    continue;
                }

                var m = directives.MemberDeclarations[entry.MemberIndex];
                string prefix = ExportPrefix(m.Name, m.IsExported, m.IsExportImplied);
                if (m.Kind == Ruitk.Language.Parser.DeclKind.Value)
                {
                    string typePart = m.ReturnTypeText != null ? m.ReturnTypeText + " " : "";
                    Ln($"{prefix}{typePart}{m.Name} = {m.BodyText};");
                    continue;
                }

                // Canonicalize the head pieces: a previously-wrapped tuple return or param
                // list carries embedded newlines/indent in its raw text — collapse so the
                // wrap decision (and the emitted single-line form) is deterministic.
                string ret = CollapseHeadWhitespace(m.ReturnTypeText ?? "void");
                string typeParams = m.TypeParamsText ?? string.Empty;
                string[] memberParams = m.Params.IsDefaultOrEmpty
                    ? System.Array.Empty<string>()
                    : m.Params.Select(p =>
                        p.DefaultValue != null
                            ? $"{p.Type} {p.Name} = {p.DefaultValue}"
                            : $"{p.Type} {p.Name}").ToArray();
                string paramsText = memberParams.Length > 0
                    ? string.Join(", ", memberParams)
                    : CollapseHeadWhitespace(m.ParamsText ?? string.Empty);
                if (m.IsExpressionBodied)
                {
                    Ln($"{prefix}{ret} {m.Name}{typeParams}({paramsText}) => {m.BodyText};");
                }
                else
                {
                    string headerLine = $"{prefix}{ret} {m.Name}{typeParams}({paramsText}) {{";
                    bool tooLong = headerLine.Length > _opts.PrintWidth;
                    bool retIsTuple = ret.StartsWith("(", System.StringComparison.Ordinal) && ret.Contains(',');
                    // Wrap the tuple RETURN when even the pre-params head is over budget;
                    // wrap PARAMS whenever the single-line head is over budget.
                    bool wrapRet = tooLong && retIsTuple
                        && $"{prefix}{ret} {m.Name}{typeParams}(".Length > _opts.PrintWidth;
                    bool wrapParams = tooLong && memberParams.Length > 0;
                    if (!wrapRet && !wrapParams)
                    {
                        Ln(headerLine);
                    }
                    else
                    {
                        if (wrapRet)
                        {
                            var items = SplitTupleItems(ret);
                            _sb.Append($"{prefix}(\n");
                            _indent++;
                            for (int ti = 0; ti < items.Length; ti++)
                                Ln(ti == items.Length - 1 ? items[ti] : items[ti] + ",");
                            _indent--;
                            if (wrapParams)
                            {
                                Ln($") {m.Name}{typeParams}(");
                            }
                            else
                            {
                                Ln($") {m.Name}{typeParams}({paramsText}) {{");
                            }
                        }
                        else
                        {
                            _sb.Append($"{prefix}{ret} {m.Name}{typeParams}(\n");
                        }
                        if (wrapParams)
                        {
                            _indent++;
                            for (int pi = 0; pi < memberParams.Length; pi++)
                                Ln(pi == memberParams.Length - 1 ? memberParams[pi] : memberParams[pi] + ",");
                            _indent--;
                            Ln(") {");
                        }
                    }
                    _indent++;
                    EmitSetupCodeNormalized(m.BodyText.Trim(), tabExp);
                    _indent--;
                    Ln("}");
                }
            }

            // Trailing trivia: comments after the last declaration.
            if (triviaIdx < trivia.Length)
            {
                _sb.Append('\n');
                while (triviaIdx < trivia.Length)
                {
                    _sb.Append(trivia[triviaIdx].Text);
                    _sb.Append('\n');
                    triviaIdx++;
                }
            }

            // U-10: deferred export lists + the default marker print at END, in source order.
            if (!directives.ExportListNames.IsDefaultOrEmpty)
            {
                _sb.Append('\n');
                Ln($"export {{ {string.Join(", ", directives.ExportListNames)} }};");
            }
            if (directives.DefaultExportName != null)
            {
                _sb.Append('\n');
                Ln($"export default {directives.DefaultExportName};");
            }
        }

        // -----------------------------------------------------------------------
        //  NODE LIST
        // -----------------------------------------------------------------------

        private void FormatNodeList(ImmutableArray<AstNode> nodes, bool topLevel)
        {
            bool firstVisible = true;

            foreach (var node in nodes)
            {
                // Drop whitespace-only text nodes - they are layout artefacts.
                if (node is TextNode tn && string.IsNullOrWhiteSpace(tn.Content))
                    continue;

                // Blank line(s) between top-level siblings, capped by MaxConsecutiveBlankLines.
                if (!firstVisible && topLevel)
                {
                    // EndLine is not yet tracked (Phase 3), so we always have at most
                    // 1 blank line to insert; honour MaxConsecutiveBlankLines as a cap.
                    int blanks = _opts.PreserveBlankLines
                        ? System.Math.Min(1, _opts.MaxConsecutiveBlankLines)
                        : 0;
                    for (int b = 0; b < blanks; b++)
                        _sb.Append('\n');
                }

                FormatNode(node);
                firstVisible = false;
            }
        }

        // -----------------------------------------------------------------------
        //  NODE DISPATCH
        // -----------------------------------------------------------------------

        private void FormatNode(AstNode node)
        {
            switch (node)
            {
                case ElementNode el:
                    FormatElement(el);
                    break;
                case IfNode ifn:
                    FormatIf(ifn);
                    break;
                case ForeachNode fe:
                    FormatForeach(fe);
                    break;
                case ForNode fn:
                    FormatFor(fn);
                    break;
                case WhileNode wh:
                    FormatWhile(wh);
                    break;
                case SwitchNode sw:
                    FormatSwitch(sw);
                    break;

                case TextNode tn:
                {
                    var t = tn.Content.Trim();
                    if (t.Length > 0)
                        Ln(t);
                    break;
                }

                case ExpressionNode en:
                    Ln($"{{{en.Expression}}}");
                    break;

                case CommentNode jc:
                {
                    string trimmed = jc.Content.Trim();
                    if (jc.IsBlock || trimmed.Contains('\n'))
                    {
                        Ln($"/* {trimmed} */");
                    }
                    else
                    {
                        Ln($"// {trimmed}");
                    }
                    break;
                }
            }
        }

        // -----------------------------------------------------------------------
        //  ELEMENT
        // -----------------------------------------------------------------------

        private void FormatElement(ElementNode el)
        {
            bool selfClose = el.Children.IsEmpty && el.CloseTagLine == 0;
            string selfCloseSeq = _opts.InsertSpaceBeforeSelfClose ? " />" : "/>";
            var attrStrings = BuildAttrStrings(el.Attributes);

            if (attrStrings.Count == 0)
            {
                // -- No attributes ---------------------------------------------
                Ln(selfClose ? $"<{el.TagName}{selfCloseSeq}" : $"<{el.TagName}>");
            }
            else
            {
                // -- Decide wrapping -------------------------------------------
                var singleLine = selfClose
                    ? $"<{el.TagName} {string.Join(" ", attrStrings)}{selfCloseSeq}"
                    : $"<{el.TagName} {string.Join(" ", attrStrings)}>";

                bool wrap =
                    _opts.SingleAttributePerLine
                    || IndentStr().Length + singleLine.Length > _opts.PrintWidth;

                if (!wrap)
                {
                    Ln(singleLine);
                }
                else
                {
                    EmitWrappedOpenTag(el.TagName, attrStrings, selfClose, selfCloseSeq);
                }
            }

            // -- Children + closing tag -----------------------------------------
            if (!selfClose)
            {
                if (el.Children.IsEmpty)
                {
                    // Empty element with explicit close tag: keep on same line
                    _sb.Remove(_sb.Length - 1, 1); // strip trailing \n
                    _sb.Append($"</{el.TagName}>\n");
                }
                else
                {
                    _indent++;
                    FormatNodeList(el.Children, topLevel: false);
                    _indent--;
                    Ln($"</{el.TagName}>");
                }
            }
        }

        /// <summary>
        /// Emit the opening tag with each attribute on its own line.
        /// Manages <see cref="_indent"/> internally.
        /// </summary>
        private void EmitWrappedOpenTag(
            string tagName,
            List<string> attrStrings,
            bool selfClose,
            string selfCloseSeq
        )
        {
            // Tag name line: e.g. "    <Box"
            Ln($"<{tagName}");
            _indent++;

            for (int i = 0; i < attrStrings.Count; i++)
            {
                bool isLast = i == attrStrings.Count - 1;
                string attr = attrStrings[i];

                if (!isLast)
                {
                    Ln(attr);
                    continue;
                }

                // -- Last attribute --------------------------------------------

                if (selfClose)
                {
                    if (_opts.ClosingBracketSameLine)
                    {
                        // <Foo
                        //     lastAttr />
                        Ln($"{attr}{selfCloseSeq}");
                        _indent--;
                    }
                    else
                    {
                        // <Foo
                        //     lastAttr
                        // />
                        Ln(attr);
                        _indent--;
                        Ln(selfCloseSeq.TrimStart());
                    }
                }
                else // opening tag
                {
                    if (_opts.BracketSameLine)
                    {
                        // <Foo
                        //     lastAttr>
                        Ln($"{attr}>");
                        _indent--;
                    }
                    else
                    {
                        // <Foo
                        //     lastAttr
                        // >
                        Ln(attr);
                        _indent--;
                        Ln(">");
                    }
                }
            }
        }

        /// <summary>
        /// Render each <see cref="AttributeNode"/> to its canonical string form.
        /// </summary>
        private static List<string> BuildAttrStrings(ImmutableArray<AttributeNode> attrs)
        {
            var list = new List<string>(attrs.Length);
            foreach (var a in attrs)
            {
                switch (a.Value)
                {
                    case StringLiteralValue s:
                        list.Add($"{a.Name}=\"{s.Value}\"");
                        break;
                    case CSharpExpressionValue e:
                        list.Add($"{a.Name}={{{e.Expression}}}");
                        break;
                    case JsxExpressionValue jsx when jsx.Element != null:
                        list.Add($"{a.Name}={{{SerializeJsxInline(jsx.Element)}}}");
                        break;
                    case BooleanShorthandValue:
                    default:
                        list.Add(a.Name);
                        break;
                }
            }
            return list;
        }

        /// <summary>
        /// Serializes a JSX <see cref="ElementNode"/> into a single-line string
        /// representation suitable for embedding inside a <c>{...}</c> attribute value.
        /// </summary>
        private static string SerializeJsxInline(ElementNode el)
        {
            var sb = new StringBuilder();
            SerializeJsxInlineCore(el, sb);
            return sb.ToString();
        }

        private static void SerializeJsxInlineCore(ElementNode el, StringBuilder sb)
        {
            var attrStrings = BuildAttrStrings(el.Attributes);
            string attrPart = attrStrings.Count > 0 ? " " + string.Join(" ", attrStrings) : "";

            if (el.Children.IsEmpty)
            {
                sb.Append($"<{el.TagName}{attrPart} />");
            }
            else
            {
                sb.Append($"<{el.TagName}{attrPart}>");
                foreach (var child in el.Children)
                {
                    if (child is ElementNode childEl)
                        SerializeJsxInlineCore(childEl, sb);
                    else if (child is TextNode tn && !string.IsNullOrWhiteSpace(tn.Content))
                        sb.Append(tn.Content.Trim());
                    else if (child is ExpressionNode en)
                        sb.Append('{').Append(en.Expression).Append('}');
                    else if (child is CommentNode cn)
                        sb.Append(cn.IsBlock ? $"/* {cn.Content.Trim()} */" : $"/* {cn.Content.TrimStart('/').Trim()} */");
                }
                sb.Append($"</{el.TagName}>");
            }
        }

        /// <summary>
        /// True if any inline-attribute JSX value (<c>attr={&lt;Tag&gt;...&lt;/Tag&gt;}</c>)
        /// anywhere in <paramref name="nodes"/> contains a control-flow node
        /// (@if/@foreach/@for/@while/@switch) that <see cref="SerializeJsxInlineCore"/>
        /// cannot represent on a single line. See U-02.
        /// </summary>
        private static bool ContainsInlineJsxControlFlow(ImmutableArray<AstNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is not ElementNode el)
                    continue;

                foreach (var attr in el.Attributes)
                    if (attr.Value is JsxExpressionValue { Element: { } jsxEl } && HasControlFlowNode(jsxEl))
                        return true;

                if (ContainsInlineJsxControlFlow(el.Children))
                    return true;
            }
            return false;
        }

        private static bool HasControlFlowNode(AstNode node)
        {
            switch (node)
            {
                case IfNode:
                case ForeachNode:
                case ForNode:
                case WhileNode:
                case SwitchNode:
                    return true;
                case ElementNode el:
                    foreach (var attr in el.Attributes)
                        if (attr.Value is JsxExpressionValue { Element: { } jsxEl } && HasControlFlowNode(jsxEl))
                            return true;
                    foreach (var child in el.Children)
                        if (HasControlFlowNode(child))
                            return true;
                    return false;
                default:
                    return false;
            }
        }

        // -----------------------------------------------------------------------
        //  C# SETUP CODE EMISSION
        // -----------------------------------------------------------------------

        /// <summary>
        /// Emit lines of C# code with relative-indent re-anchoring.
        /// <para><paramref name="firstLineStripped"/> - set when line[0] had its
        /// leading whitespace stripped by ExpressionExtractor (it always gets
        /// relSpaces = 0 regardless of its actual content).</para>
        /// <para><paramref name="suppressLastNewline"/> - when true the last
        /// non-blank line is appended without a trailing '\n' so a JSX element
        /// can continue on the same line.</para>
        /// </summary>
        private void EmitCSharpLines(
            string code,
            string tabExp,
            bool firstLineStripped,
            bool suppressLastNewline
        )
        {
            if (string.IsNullOrEmpty(code))
                return;
            var lines = code.Split('\n');

            // U-03: per line (against the ORIGINAL, pre-split code), whether it STARTS
            // inside a multi-line verbatim string VALUE opened on an earlier line. Those
            // lines are string data, not code - excluded from the `{content` splitter
            // below, from brace/baseSpaces bookkeeping, and re-emitted byte-verbatim.
            var origStringMask = CSharpLexFacts.ComputeMultilineStringLineMask(code);

            // U-07: same treatment for lines entirely inside a multi-line /* ... */
            // block comment (e.g. old removed JSX kept commented out for reference) -
            // see the identical merge in EmitSetupCodeNormalized for the full rationale.
            {
                var blockCommentMask = CSharpLexFacts.ComputeMultilineBlockCommentLineMask(code);
                for (int m = 0; m < origStringMask.Length && m < blockCommentMask.Length; m++)
                    if (blockCommentMask[m])
                        origStringMask[m] = true;
            }

            // -- Pre-process: split `{content` lines ---------------------------
            // When a line starts with `{` followed by content (not just `{`
            // alone) and the line has net-positive open braces, the `{` opens
            // a multi-line block while content sits on the same line.  Split
            // into two lines so the formatter can indent the content inside
            // the block correctly.  Skip `{/*` (JSX/C# comment expressions).
            bool[] stringMask;
            {
                var split = new System.Collections.Generic.List<string>(lines.Length);
                var splitMask = new System.Collections.Generic.List<bool>(lines.Length);
                for (int si = 0; si < lines.Length; si++)
                {
                    bool masked = si < origStringMask.Length && origStringMask[si];
                    string raw = lines[si];
                    if (!masked)
                    {
                        string t = raw.TrimStart();
                        if (t.Length > 1 && t[0] == '{')
                        {
                            int after = 1;
                            while (after < t.Length && (t[after] == ' ' || t[after] == '\t'))
                                after++;

                            bool hasContent = after < t.Length && t[after] != '}';
                            bool isBlockComment =
                                after + 1 < t.Length && t[after] == '/' && t[after + 1] == '*';

                            if (hasContent && !isBlockComment)
                            {
                                int opens = 0,
                                    closes = 0;
                                for (int c = 0; c < t.Length; c++)
                                {
                                    if (t[c] == '{')
                                        opens++;
                                    else if (t[c] == '}')
                                        closes++;
                                }
                                if (opens > closes)
                                {
                                    int leadWs = raw.Length - raw.TrimStart().Length;
                                    string wsPrefix = leadWs > 0 ? raw.Substring(0, leadWs) : "";
                                    split.Add(wsPrefix + "{");
                                    splitMask.Add(false);
                                    split.Add(wsPrefix + t.Substring(1).TrimStart());
                                    splitMask.Add(false);
                                    continue;
                                }
                            }
                        }
                    }
                    split.Add(raw);
                    splitMask.Add(masked);
                }
                lines = split.ToArray();
                stringMask = splitMask.ToArray();
            }

            // Find last non-blank line index (for suppressLastNewline).
            int lastMeaningful = lines.Length - 1;
            while (lastMeaningful >= 0 && string.IsNullOrWhiteSpace(lines[lastMeaningful]))
                lastMeaningful--;
            if (lastMeaningful < 0)
                return;

            int indentSpaces = _indent * _opts.IndentSize; // spaces contributed by IndentStr()

            // -- baseSpaces for depth-0 lines ----------------------------------
            // Excludes comments and continuation-style lines (ternary arms,
            // method chains) so that CSharpier-corrupted files where comments
            // sit at 2sp but statements at 4sp are correctly normalised.
            int baseSpaces = int.MaxValue;
            {
                int d = 0;
                int startLine = firstLineStripped ? 1 : 0;
                for (int i = startLine; i <= lastMeaningful; i++)
                {
                    if (i < stringMask.Length && stringMask[i])
                        continue; // string-literal interior: not code, doesn't affect depth/baseSpaces

                    string stripped = lines[i].Trim();
                    if (!string.IsNullOrWhiteSpace(stripped))
                    {
                        int lc = 0;
                        while (lc < stripped.Length && stripped[lc] == '}')
                            lc++;
                        d = System.Math.Max(0, d - lc);
                    }

                    if (d == 0 && !string.IsNullOrWhiteSpace(lines[i]))
                    {
                        string lStripped = stripped;
                        bool isContinuation =
                            lStripped.Length > 0
                            && (lStripped[0] == '?' || lStripped[0] == ':' || lStripped[0] == '.');
                        bool isComment = lStripped.StartsWith("//") || lStripped.StartsWith("/*");
                        if (!isContinuation && !isComment)
                        {
                            var exp = lines[i].Replace("\t", tabExp);
                            int lead = exp.Length - exp.TrimStart().Length;
                            if (lead < baseSpaces)
                                baseSpaces = lead;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(lines[i]))
                    {
                        string stripped2 = lines[i].Trim().TrimEnd();
                        if (stripped2.Length > 0 && stripped2[stripped2.Length - 1] == '{')
                            d++;
                    }
                }
            }
            if (baseSpaces == int.MaxValue)
                baseSpaces = 0;

            // -- Emit with stack-based block normalisation ---------------------
            // The stack holds the TOTAL column (including IndentStr) at which
            // the content of each open block should appear.  This normalises
            // indentation inside { } blocks (Style initialisers, lambda bodies,
            // etc.) while depth-0 lines use the IsStatementStarter / anchor
            // approach to handle multi-line expressions and continuations.
            //
            // caseExtra: inside a { } block, lines following a `case X:` /
            // `default:` label or a `},` brace-comma continuation get an
            // additional indentSize offset.  This is saved/restored on the
            // caseExtraStack whenever a nested block is entered/exited so
            // that the outer context is preserved.
            var blockStack = new System.Collections.Generic.Stack<int>();
            var caseExtraStack = new System.Collections.Generic.Stack<int>();
            var blockAnchorStack = new System.Collections.Generic.Stack<int>();
            var isLambdaStack = new System.Collections.Generic.Stack<bool>();
            int caseExtra = 0;
            int lastBlockAnchor = -1;
            int lastStatementInputIndent = -1;
            bool prevWasStatementStarter = false;

            for (int li = 0; li <= lastMeaningful; li++)
            {
                if (li < stringMask.Length && stringMask[li])
                {
                    // Inside a multi-line verbatim string VALUE: emit byte-verbatim,
                    // untouched by trim/tab-expansion/collapse/brace-count/indent
                    // bookkeeping (see U-03) - this line is string data, not code.
                    _sb.Append(lines[li]);
                    _sb.Append('\n');
                    continue;
                }

                string raw = lines[li].TrimEnd();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    _sb.Append('\n');
                    continue;
                }

                string stripped = raw.Trim();

                // Convert intra-line tabs to spaces, then collapse runs of 2+
                // whitespace characters to a single space (outside strings/comments).
                if (stripped.IndexOf('\t') >= 0)
                    stripped = stripped.Replace('\t', ' ');

                stripped = CollapseIntraLineSpaces(stripped);

                // Pop for each leading '}'.
                int leadClose = 0;
                while (leadClose < stripped.Length && stripped[leadClose] == '}')
                    leadClose++;
                bool lastPoppedWasLambda = false;
                for (int p = 0; p < leadClose; p++)
                {
                    if (blockStack.Count > 0)
                    {
                        blockStack.Pop();
                        lastPoppedWasLambda = isLambdaStack.Count > 0 && isLambdaStack.Pop();
                        caseExtra = caseExtraStack.Count > 0 ? caseExtraStack.Pop() : 0;
                        lastBlockAnchor = blockAnchorStack.Count > 0 ? blockAnchorStack.Pop() : -1;
                    }
                }

                var expL = raw.Replace("\t", tabExp);
                int leadL = expL.Length - expL.TrimStart().Length;
                int emittedTotal;

                if (blockStack.Count == 0)
                {
                    // Depth-0: statement-opening lines at indentSpaces (rel=0).
                    // Continuation/closure tokens anchor to the most-recent
                    // statement's INPUT indent so relative offsets survive
                    // mixed-corruption files.
                    int rel;
                    if (firstLineStripped && li == 0)
                    {
                        rel = 0;
                    }
                    else if (IsStatementStarter(stripped))
                    {
                        rel = 0;
                        if (li > 0)
                            lastStatementInputIndent = leadL;
                    }
                    else if (stripped == "{" && prevWasStatementStarter)
                    {
                        // Allman-style block opener after a statement: always at rel=0 (same
                        // indent as the statement that precedes it).  Does NOT apply when
                        // the preceding line is a continuation (e.g. () => new List<int>).
                        rel = 0;
                    }
                    else
                    {
                        int anchor =
                            lastStatementInputIndent >= 0 ? lastStatementInputIndent : baseSpaces;
                        rel = System.Math.Max(0, leadL - anchor);
                    }
                    string relPrefix = rel > 0 ? new string(' ', rel) : string.Empty;

                    if (li == lastMeaningful && suppressLastNewline)
                        _sb.Append(IndentStr() + relPrefix + stripped);
                    else
                        Ln(relPrefix + stripped);
                    emittedTotal = indentSpaces + rel;

                    prevWasStatementStarter =
                        IsStatementStarter(stripped) || (firstLineStripped && li == 0);
                }
                else
                {
                    // Inside a { } block: normalise to the block's expected
                    // indentation so inner content is always consistent.
                    // caseExtra adds one indentSize for lines inside a case
                    // body or after a `},` brace-comma continuation.
                    // Continuation lines (starting with ?, :, .) preserve
                    // their relative offset from the last non-continuation line.
                    int blockTarget = blockStack.Peek();

                    // Determine the extra indent for THIS line.
                    int extra = caseExtra;
                    bool isCaseLabel =
                        stripped.StartsWith("case ", System.StringComparison.Ordinal)
                        || stripped.StartsWith("default:", System.StringComparison.Ordinal);
                    if (isCaseLabel)
                        extra = 0; // label itself at blockTarget
                    if (stripped[0] == ')')
                        extra = 0; // closing paren of outer call

                    // Check for continuation lines (ternary arms, method chains).
                    bool isBlockContinuation =
                        stripped[0] == '?' || stripped[0] == ':' || stripped[0] == '.';
                    // Exclude case/default labels that start with the keyword, not ':'
                    if (isCaseLabel)
                        isBlockContinuation = false;

                    int target;
                    if (isBlockContinuation && lastBlockAnchor >= 0)
                    {
                        int rel = System.Math.Max(0, leadL - lastBlockAnchor);
                        target = blockTarget + extra + rel;
                    }
                    else
                    {
                        target = blockTarget + extra;
                        lastBlockAnchor = leadL;
                    }

                    int prefixSpaces = System.Math.Max(0, target - indentSpaces);
                    string blockPrefix =
                        prefixSpaces > 0 ? new string(' ', prefixSpaces) : string.Empty;

                    if (li == lastMeaningful && suppressLastNewline)
                        _sb.Append(IndentStr() + blockPrefix + stripped);
                    else
                        Ln(blockPrefix + stripped);
                    emittedTotal = target;

                    // Update caseExtra for the NEXT line.
                    if (isCaseLabel)
                    {
                        caseExtra = _opts.IndentSize;
                    }
                    else if (leadClose > 0)
                    {
                        // After a line starting with '}': check for brace-comma
                        // continuation `},` which means call arguments follow,
                        // but only if the closed block was a lambda (opened
                        // with `=> {`).  Object-initialiser `},` should NOT
                        // trigger deeper indent for the next property.
                        int ci = leadClose;
                        while (ci < stripped.Length && stripped[ci] == ' ')
                            ci++;
                        if (ci < stripped.Length && stripped[ci] == ',' && lastPoppedWasLambda)
                            caseExtra = _opts.IndentSize;
                        else
                            caseExtra = 0;
                    }
                    else if (stripped[0] == ')')
                    {
                        caseExtra = 0;
                    }
                    // else: keep caseExtra as-is (continuation lines stay at
                    // the same extra level).
                }

                // Push for net unmatched '{' in the line (after leading '}' chars).
                // Previously only a trailing '{' triggered a push, but mid-line
                // '{' whose matching '}' lands on a later line (e.g. after a
                // JSX placeholder) also needs to be tracked.
                // Similarly, pop for net unmatched '}' (e.g. `*/}` closing a
                // `{/*` comment block) so depth returns to the correct level.
                // Single-line comments are skipped entirely: `{` and `}` chars
                // inside `//` text are not real syntax and must not push
                // phantom blocks (regression: a commented-out
                // `useEffect(() => {` block drifted indent deeper on every save).
                if (stripped.StartsWith("//"))
                    continue;
                int midOpens = 0,
                    midCloses = 0;
                for (int ci = leadClose; ci < stripped.Length; ci++)
                {
                    if (stripped[ci] == '{')
                        midOpens++;
                    else if (stripped[ci] == '}')
                        midCloses++;
                }
                int netOpens = midOpens - midCloses;
                if (netOpens > 0)
                {
                    string tail = stripped.TrimEnd();
                    bool trailingBrace = tail.Length > 0 && tail[tail.Length - 1] == '{';
                    bool lineHasArrow = stripped.Contains("=>");
                    for (int p = 0; p < netOpens; p++)
                    {
                        blockStack.Push(emittedTotal + (p + 1) * _opts.IndentSize);
                        caseExtraStack.Push(caseExtra);
                        blockAnchorStack.Push(lastBlockAnchor);
                        // Only the last push can be a lambda - and only when the
                        // line actually ends with '{' (the traditional pattern).
                        bool lambda = (p == netOpens - 1) && trailingBrace && lineHasArrow;
                        isLambdaStack.Push(lambda);
                    }
                    caseExtra = 0;
                    lastBlockAnchor = -1;
                }
                else if (netOpens < 0)
                {
                    // Mid-line net closes (e.g. `*/}` ending a `{/*` block).
                    // Pop after the line is emitted - affects subsequent lines.
                    for (int p = 0; p < -netOpens; p++)
                    {
                        if (blockStack.Count > 0)
                        {
                            blockStack.Pop();
                            if (isLambdaStack.Count > 0)
                                isLambdaStack.Pop();
                            caseExtra = caseExtraStack.Count > 0 ? caseExtraStack.Pop() : 0;
                            lastBlockAnchor =
                                blockAnchorStack.Count > 0 ? blockAnchorStack.Pop() : -1;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Emits function-style setup code (no embedded JSX) with two rules:
        /// <list type="bullet">
        ///   <item>Lines INSIDE a <c>{ }</c> block use the indentation of the
        ///         line that opened the block plus one <paramref name="tabExp"/>
        ///         unit, normalising any inconsistent per-line indentation (e.g.
        ///         a <c>new Style { }</c> whose entries have mixed leading-space
        ///         counts).</item>
        ///   <item>Lines at depth 0 (not inside any <c>{ }</c> block) preserve
        ///         their relative indentation anchored to <c>baseSpaces</c>, so
        ///         ternary continuations, method chains, and similar multi-line
        ///         expressions are not disturbed.</item>
        /// </list>
        /// Uses a stack to track the expected indentation for each block level,
        /// seeded from the actual emitted position of the line that opened the
        /// block.  This makes the normalisation relative (not absolute), so a
        /// <c>{</c> opened inside a deeply-indented call-argument list produces
        /// correctly-relative children.
        /// </summary>
        private void EmitSetupCodeNormalized(string code, string tabExp)
        {
            if (string.IsNullOrEmpty(code))
                return;

            var lines = code.Split('\n');

            // U-03: per line, whether it STARTS inside a multi-line verbatim string
            // VALUE (@"..."/@$"".../$@"...") opened on an earlier line. Those lines are
            // string data, not code - they must be excluded from brace/baseSpaces
            // bookkeeping and re-emitted byte-verbatim, never Trim/collapse/re-indented.
            var stringMask = CSharpLexFacts.ComputeMultilineStringLineMask(code);

            // U-07: same treatment for lines that lie entirely inside a multi-line
            // /* ... */ block comment - e.g. old removed JSX kept commented out for
            // reference. Without this, once SetupCodeMarkupRanges/ScanJsxParenBlocks
            // correctly stop mis-detecting a commented-out "(<Tag/>)" as live JSX, the
            // comment's interior fell through to this generic reindenter and had its
            // author-chosen indentation flattened to the surrounding brace depth.
            var blockCommentMask = CSharpLexFacts.ComputeMultilineBlockCommentLineMask(code);
            for (int m = 0; m < stringMask.Length && m < blockCommentMask.Length; m++)
                if (blockCommentMask[m])
                    stringMask[m] = true;

            // Find last non-blank line index.
            int lastMeaningful = lines.Length - 1;
            while (lastMeaningful >= 0 && string.IsNullOrWhiteSpace(lines[lastMeaningful]))
                lastMeaningful--;
            if (lastMeaningful < 0)
                return;

            int indentSpaces = _indent * _opts.IndentSize; // spaces contributed by IndentStr()

            // -- baseSpaces for depth-0 lines (excluding line[0]) --------------
            // Used to anchor continuation-line relative indentation.
            int baseSpaces = int.MaxValue;
            {
                // We need a quick depth scan to find which lines are depth-0 for
                // the baseSpaces calculation.
                int d = 0;
                for (int i = 1; i <= lastMeaningful; i++)
                {
                    if (i < stringMask.Length && stringMask[i])
                        continue; // string-literal interior: not code, doesn't affect depth/baseSpaces

                    string stripped = lines[i].Trim();
                    if (!string.IsNullOrWhiteSpace(stripped))
                    {
                        int lc = 0;
                        while (lc < stripped.Length && stripped[lc] == '}')
                            lc++;
                        d = System.Math.Max(0, d - lc);
                    }

                    // Measure depth-0 line for baseSpaces before potential push.
                    // Skip continuation-style lines (ternary arms, method chains)
                    // so they don't collapse the base indent calculation.
                    if (d == 0 && !string.IsNullOrWhiteSpace(lines[i]))
                    {
                        string lStripped = stripped;
                        bool isContinuation =
                            lStripped.Length > 0
                            && (lStripped[0] == '?' || lStripped[0] == ':' || lStripped[0] == '.');
                        // Comments are not statements - they must not pull baseSpaces
                        // down and prevent over-indented statement lines from being
                        // corrected (e.g. when CSharpier has added 4-space indent to
                        // setup-code lines that sit next to 2-space comment headers).
                        bool isComment = lStripped.StartsWith("//") || lStripped.StartsWith("/*");
                        if (!isContinuation && !isComment)
                        {
                            var exp = lines[i].Replace("\t", tabExp);
                            int lead = exp.Length - exp.TrimStart().Length;
                            if (lead < baseSpaces)
                                baseSpaces = lead;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(lines[i]))
                    {
                        string stripped2 = lines[i].Trim().TrimEnd();
                        if (stripped2.Length > 0 && stripped2[stripped2.Length - 1] == '{')
                            d++;
                    }
                }
            }
            // If only continuation lines appeared at depth-0, fall back to
            // indentSpaces so ternary arms / method chains preserve their
            // relative indent rather than collapsing to the same level.
            if (baseSpaces == int.MaxValue)
                baseSpaces = indentSpaces;

            // -- Emit with stack-based block normalisation ---------------------
            // The stack holds the TOTAL column (including IndentStr) at which
            // the content of each open block should appear.
            var blockStack = new System.Collections.Generic.Stack<int>();
            // Records the paren-depth that was active when each block on
            // blockStack was opened.  A line is treated as a paren continuation
            // only when parenDepth > peek of this stack (i.e., the open `(` was
            // started AFTER the current block was entered).  Without this guard,
            // `useEffect(() => { body }, ...)` would falsely mark `body` as a
            // continuation of the outer call's argument list.
            var blockParenBaseStack = new System.Collections.Generic.Stack<int>();
            int lastStatementInputIndent = -1; // input lead of most-recent depth-0 statement opener
            bool prevWasStatementStarter = false;

            // Open-paren continuation tracking. When a line opens a `(` that
            // does not close on the same line AND no enclosing block intervenes,
            // subsequent lines are paren continuations whose indent must be
            // preserved RELATIVE to the opener line's input indent (e.g. `&&`
            // arms of a multi-line `if (...)` stay at +4sp from the `if`).
            int parenDepth = 0;
            int parenOpenLineEmitted = -1; // emitted-column anchor of opener line
            int parenOpenLineInputIndent = -1; // input lead-spaces of opener line
            int parenOpenAtBlockDepth = -1; // blockStack.Count when opener fired

            for (int li = 0; li <= lastMeaningful; li++)
            {
                if (li < stringMask.Length && stringMask[li])
                {
                    // Inside a multi-line verbatim string VALUE: emit byte-verbatim,
                    // untouched by trim/tab-expansion/collapse/brace-count/indent
                    // bookkeeping (see U-03) - this line is string data, not code.
                    _sb.Append(lines[li]);
                    _sb.Append('\n');
                    continue;
                }

                string raw = lines[li].TrimEnd();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    _sb.Append('\n');
                    continue;
                }

                string stripped = raw.Trim();

                // Convert intra-line tabs to spaces, then collapse runs of 2+
                // whitespace characters to a single space (outside strings/comments).
                if (stripped.IndexOf('\t') >= 0)
                    stripped = stripped.Replace('\t', ' ');

                stripped = CollapseIntraLineSpaces(stripped);

                // Pop for each leading '}'.
                int leadClose = 0;
                while (leadClose < stripped.Length && stripped[leadClose] == '}')
                    leadClose++;
                for (int p = 0; p < leadClose; p++)
                {
                    if (blockStack.Count > 0)
                        blockStack.Pop();
                    if (blockParenBaseStack.Count > 0)
                        blockParenBaseStack.Pop();
                }

                // Compute the indentation for this line and emit it.
                int emittedTotal; // total column position of the emitted line content
                int blockTargetForPush; // logical block target (used when pushing a new block)
                int currentBlockParenBase =
                    blockParenBaseStack.Count > 0 ? blockParenBaseStack.Peek() : 0;
                bool isParenContinuation =
                    parenDepth > currentBlockParenBase
                    && parenOpenLineEmitted >= 0
                    && parenOpenLineInputIndent >= 0
                    && parenOpenAtBlockDepth == blockStack.Count;
                var expRaw = raw.Replace("\t", tabExp);
                int leadRaw = expRaw.Length - expRaw.TrimStart().Length;

                if (blockStack.Count == 0)
                {
                    // Depth-0: statement-opening lines are always at indentSpaces (rel=0).
                    // Continuation/closure tokens anchor to the most-recent statement's
                    // INPUT indent so relative offsets survive mixed-corruption files
                    // (e.g. some lines at 2sp, others at 4sp or 12sp due to CSharpier).
                    int leadL = leadRaw;
                    int rel;
                    if (isParenContinuation)
                    {
                        // Inside an open `(` from a previous depth-0 line: preserve
                        // the relative offset from the opener's input indent so
                        // multi-line `if (...)` / call-arg continuations keep their
                        // visible column. Do NOT update lastStatementInputIndent
                        // because this line is not a statement opener.
                        int relOpener = System.Math.Max(0, leadL - parenOpenLineInputIndent);
                        emittedTotal = parenOpenLineEmitted + relOpener;
                        rel = System.Math.Max(0, emittedTotal - indentSpaces);
                    }
                    else if (li == 0 || IsStatementStarter(stripped))
                    {
                        rel = 0;
                        // li==0 is Trim()'d by the caller so its leadL is 0 regardless of
                        // the original source indent - not a reliable anchor for subsequent
                        // non-starter lines.  Only record the input indent for li > 0.
                        if (li > 0)
                            lastStatementInputIndent = leadL;
                        emittedTotal = indentSpaces;
                    }
                    else if (stripped == "{" && prevWasStatementStarter)
                    {
                        // Allman-style block opener after a statement: always at rel=0.
                        rel = 0;
                        emittedTotal = indentSpaces;
                    }
                    else
                    {
                        int anchor =
                            lastStatementInputIndent >= 0 ? lastStatementInputIndent : baseSpaces;
                        rel = System.Math.Max(0, leadL - anchor);
                        emittedTotal = indentSpaces + rel;
                    }
                    string relPrefix = rel > 0 ? new string(' ', rel) : string.Empty;
                    Ln(relPrefix + stripped);

                    // For pushing a new block, the LOGICAL anchor is indentSpaces
                    // (the statement column), not the continuation column.
                    blockTargetForPush = isParenContinuation ? indentSpaces : emittedTotal;

                    if (!isParenContinuation)
                        prevWasStatementStarter = IsStatementStarter(stripped) || li == 0;
                }
                else
                {
                    // Inside a block: normalise to the block's expected indentation.
                    int blockTarget = blockStack.Peek();
                    if (isParenContinuation)
                    {
                        // Paren-continuation inside a block: preserve the relative
                        // offset from the opener line so e.g. `&&` arms of a
                        // multi-line `if (...)` stay at +4sp from the `if`.
                        int relOpener = System.Math.Max(0, leadRaw - parenOpenLineInputIndent);
                        emittedTotal = parenOpenLineEmitted + relOpener;
                    }
                    else
                    {
                        emittedTotal = blockTarget;
                    }
                    int prefixSpaces = System.Math.Max(0, emittedTotal - indentSpaces);
                    string blockPrefix =
                        prefixSpaces > 0 ? new string(' ', prefixSpaces) : string.Empty;
                    Ln(blockPrefix + stripped);

                    // For pushing a new block, the LOGICAL anchor is the block
                    // target (the statement column inside this block), not the
                    // continuation column.
                    blockTargetForPush = blockTarget;
                }

                // Update paren-depth tracking BEFORE the trailing-{ push so that a
                // line which both opens a `(` and ends with `{` correctly anchors
                // future inside-block continuations (the `(` becomes the block's
                // own base paren-depth, NOT a continuation context).
                // Single-line comments are skipped entirely: `(`, `)` and `{`
                // inside `//` text are not real syntax and must not push phantom
                // blocks or shift paren depth (regression: a commented-out
                // `useEffect(() => {` block drifted indent deeper on every save).
                bool isLineComment = stripped.StartsWith("//");
                if (isLineComment)
                {
                    prevWasStatementStarter = false;
                    continue;
                }
                ScanParens(stripped, out int opens, out int closes);
                int prevParenDepth = parenDepth;
                parenDepth = System.Math.Max(0, parenDepth + opens - closes);

                // If a paren that started on a previous line just closed, drop
                // the anchors. Re-open if a fresh `(` opened on this line and
                // is still open at end-of-line.
                if (parenDepth <= currentBlockParenBase)
                {
                    parenOpenLineEmitted = -1;
                    parenOpenLineInputIndent = -1;
                    parenOpenAtBlockDepth = -1;
                }
                if (parenDepth > prevParenDepth && li > 0)
                {
                    // A new `(` opened on this line and at least one is still
                    // unclosed at end-of-line. Anchor to this line so future
                    // continuations can preserve their relative offset.
                    // li==0 is Trim()'d by the caller so its leadRaw=0
                    // regardless of the original source indent - skip
                    // anchoring to avoid blowing up the offset on next line.
                    parenOpenLineEmitted = emittedTotal;
                    parenOpenLineInputIndent = leadRaw;
                    parenOpenAtBlockDepth = blockStack.Count;
                }

                // Push for a trailing '{' - next lines should be one tabExp deeper.
                string tail = stripped.TrimEnd();
                if (tail.Length > 0 && tail[tail.Length - 1] == '{')
                {
                    // Distinguish bare-brace initializers (`{` alone, opens at
                    // the brace's actual column) from statement-body braces
                    // (content before `{`, opens at the surrounding logical
                    // block column).  Only paren-continuation statement bodies
                    // need the logical-column re-anchor; bare braces always
                    // anchor to their own emitted column.
                    bool isBareBrace = stripped == "{";
                    int pushBase =
                        (isParenContinuation && !isBareBrace) ? blockTargetForPush : emittedTotal;
                    blockStack.Push(pushBase + _opts.IndentSize);
                    // The new block's "base paren depth" is the current
                    // parenDepth (post-update). Parens opened inside the block
                    // count as continuations; parens from outside do not.
                    blockParenBaseStack.Push(parenDepth);
                    // Block opener resets paren-anchor scope: any pending
                    // anchor belongs to an OUTER scope and must not leak into
                    // the new block.
                    parenOpenLineEmitted = -1;
                    parenOpenLineInputIndent = -1;
                    parenOpenAtBlockDepth = -1;
                }
            }
        }

        /// <summary>
        /// Returns <c>true</c> when a stripped depth-0 line begins a new C# statement
        /// rather than being a continuation fragment (continuation arg, named arg, bare
        /// block opener, ternary arm, method chain, etc.).
        /// A line is a statement starter when it:
        ///   - begins with a well-known C# keyword (var, void, if, foreach, return, ...) or
        ///   - ends with <c>;</c> (expression-statement terminator) or
        ///   - ends with <c>{</c> AND has content before the brace (not just a bare <c>{</c>)
        /// Used in <see cref="EmitSetupCodeNormalized"/> to anchor depth-0 statement lines
        /// at <c>indentSpaces</c> regardless of their input indentation.
        /// </summary>
        private static bool IsStatementStarter(string s)
        {
            if (s.Length == 0)
                return false;

            // Ternary arms ('?', ':') and method-chain continuations ('.') are
            // never statement openers regardless of their ending character.
            char first = s[0];
            if (first == '?' || first == ':' || first == '.')
                return false;

            // -- keyword prefix -> always a statement opener -------------------
            foreach (var kw in s_statementKeywords)
                if (s.StartsWith(kw, System.StringComparison.Ordinal))
                    return true;

            // -- trailing character heuristics ---------------------------------
            string t = s.TrimEnd();
            if (t.Length == 0)
                return false;
            char last = t[t.Length - 1];

            // Lines ending with ';' are complete expression statements.
            if (last == ';')
                return true;

            // Lines ending with '{' that have content before the brace open a block
            // as part of a new statement (method, if-body, lambda inline, etc.).
            // A bare '{' alone (Allman-style continuation) is deliberately excluded.
            if (last == '{' && t.Length > 1)
                return true;

            // Lines containing a standalone ' = ' are assignments/declarations
            // (e.g. "MyType name = value =>").  The ' = ' pattern naturally
            // excludes compound operators (+=, -=, ==, !=, >=, <=, ??=, etc.)
            // because they don't produce space-equals-space.
            if (t.IndexOf(" = ", System.StringComparison.Ordinal) >= 0)
                return true;

            // Expression-bodied method declarations: e.g.
            //   RowRenderer BuildRowRenderer() =>
            // These end with '=>' and start with a type name (uppercase letter).
            // Lambda continuations like `(index, obj) =>` are excluded because
            // they start with '(' not a letter.
            if (
                t.Length >= 2
                && t[t.Length - 1] == '>'
                && t[t.Length - 2] == '='
                && char.IsUpper(first)
            )
                return true;

            return false;
        }

        /// <summary>
        /// Counts unmatched <c>(</c> and <c>)</c> in a single line of C# source,
        /// skipping over string literals (regular, verbatim, interpolated),
        /// character literals, and <c>// line</c> / <c>/* block */</c> comments.
        /// Used by <see cref="EmitSetupCodeNormalized"/> to track open-paren
        /// depth across consecutive lines so multi-line <c>(...)</c> expressions
        /// preserve their relative indentation.
        /// </summary>
        private static void ScanParens(string line, out int opens, out int closes)
        {
            opens = 0;
            closes = 0;
            bool inStr = false;
            bool verbatim = false;
            bool inChar = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inStr)
                {
                    if (verbatim)
                    {
                        if (c == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"')
                                i++; // doubled-quote escape
                            else
                                inStr = false;
                        }
                    }
                    else
                    {
                        if (c == '\\' && i + 1 < line.Length)
                            i++; // escape
                        else if (c == '"')
                            inStr = false;
                    }
                    continue;
                }

                if (inChar)
                {
                    if (c == '\\' && i + 1 < line.Length)
                        i++;
                    else if (c == '\'')
                        inChar = false;
                    continue;
                }

                // Line comment - rest of line is non-code.
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    break;

                // Block comment - skip to closing */.
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < line.Length && !(line[i] == '*' && line[i + 1] == '/'))
                        i++;
                    i++; // consume the '/' of */
                    continue;
                }

                // Detect verbatim @"..." or interpolated $"..." (and $@", @$").
                if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    inStr = true;
                    verbatim = true;
                    i++;
                    continue;
                }
                if (c == '$' && i + 1 < line.Length)
                {
                    if (line[i + 1] == '"')
                    {
                        inStr = true;
                        verbatim = false;
                        i++;
                        continue;
                    }
                    if (line[i + 1] == '@' && i + 2 < line.Length && line[i + 2] == '"')
                    {
                        inStr = true;
                        verbatim = true;
                        i += 2;
                        continue;
                    }
                }
                if (c == '"')
                {
                    inStr = true;
                    verbatim = false;
                    continue;
                }
                if (c == '\'')
                {
                    inChar = true;
                    continue;
                }

                if (c == '(')
                    opens++;
                else if (c == ')')
                {
                    if (opens > 0)
                        opens--; // pair with same-line open
                    else
                        closes++;
                }
            }
        }

        private static readonly string[] s_statementKeywords =
        {
            "var ",
            "void ",
            "if (",
            "if(",
            "else ",
            "else{",
            "else if",
            "foreach ",
            "foreach(",
            "for ",
            "for(",
            "while ",
            "while(",
            "do ",
            "do{",
            "switch ",
            "switch(",
            "return ",
            "throw ",
            "break;",
            "break ",
            "continue;",
            "continue ",
            "bool ",
            "int ",
            "uint ",
            "long ",
            "ulong ",
            "float ",
            "double ",
            "decimal ",
            "char ",
            "string ",
            "string?",
            "object ",
            "object?",
            "byte ",
            "sbyte ",
            "short ",
            "ushort ",
            "using ",
            "using(",
            "try ",
            "try{",
            "catch ",
            "catch(",
            "finally ",
            "finally{",
            "static ",
            "readonly ",
            "const ",
            "public ",
            "private ",
            "protected ",
            "internal ",
            "abstract ",
            "override ",
            "virtual ",
            "sealed ",
            "partial ",
            "async ",
            "await ",
        };

        /// <summary>
        /// Normalises intra-line whitespace outside of string literals and
        /// <c>// -</c> line comments:
        /// <list type="bullet">
        ///   <item>Runs of 2+ consecutive spaces -> single space.</item>
        ///   <item>Spaces immediately after <c>(</c> are removed.</item>
        ///   <item>Spaces immediately before <c>)</c> are removed.</item>
        /// </list>
        /// Tab characters should be replaced with spaces before calling
        /// this method.
        /// </summary>
        private static string CollapseIntraLineSpaces(string line)
        {
            if (
                line.IndexOf("  ", System.StringComparison.Ordinal) < 0
                && line.IndexOf("( ", System.StringComparison.Ordinal) < 0
                && line.IndexOf(" )", System.StringComparison.Ordinal) < 0
            )
                return line;

            var sb = new System.Text.StringBuilder(line.Length);
            bool inStr = false;
            bool verbatim = false;
            bool pendingSp = false;
            bool afterOpen = false; // just emitted '(' outside strings

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inStr)
                {
                    sb.Append(c);
                    if (verbatim)
                    {
                        if (c == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"')
                                sb.Append(line[++i]); // doubled-quote escape
                            else
                                inStr = false;
                        }
                    }
                    else
                    {
                        if (c == '\\' && i + 1 < line.Length)
                            sb.Append(line[++i]); // backslash escape
                        else if (c == '"')
                            inStr = false;
                    }
                    pendingSp = false;
                    afterOpen = false;
                    continue;
                }

                // Line comment - flush pending space, then rest is literal.
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    if (pendingSp)
                    {
                        sb.Append(' ');
                        pendingSp = false;
                    }
                    sb.Append(line, i, line.Length - i);
                    break;
                }

                // String opener.
                if (c == '"')
                {
                    if (pendingSp)
                    {
                        sb.Append(' ');
                        pendingSp = false;
                    }
                    sb.Append(c);
                    inStr = true;
                    verbatim =
                        (i > 0 && line[i - 1] == '@')
                        || (i > 1 && line[i - 1] == '$' && line[i - 2] == '@')
                        || (i > 1 && line[i - 1] == '@' && line[i - 2] == '$');
                    afterOpen = false;
                    continue;
                }

                if (c == ' ')
                {
                    // Skip spaces right after '('.
                    if (!afterOpen)
                        pendingSp = true;
                    continue;
                }

                // Non-space character.
                if (c == ')')
                {
                    // Absorb pending spaces before ')'.
                    pendingSp = false;
                }
                else if (pendingSp)
                {
                    sb.Append(' ');
                    pendingSp = false;
                }

                sb.Append(c);
                afterOpen = (c == '(');
            }

            return sb.ToString();
        }

        private static string BuildRepeat(string s, int count)
        {
            if (count <= 0 || string.IsNullOrEmpty(s))
                return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length * count);
            for (int i = 0; i < count; i++)
                sb.Append(s);
            return sb.ToString();
        }

        /// <summary>
        /// Append a JSX element tag inline onto the current <see cref="_sb"/> line
        /// (no leading indent prefix, no trailing newline).
        /// </summary>
        private void AppendElementInline(ElementNode el, bool selfClose)
        {
            var attrStrings = BuildAttrStrings(el.Attributes);
            string closing = selfClose ? (_opts.InsertSpaceBeforeSelfClose ? " />" : "/>") : ">";
            if (attrStrings.Count == 0)
                _sb.Append($"<{el.TagName}{closing}");
            else
                _sb.Append($"<{el.TagName} {string.Join(" ", attrStrings)}{closing}");
        }

        // -----------------------------------------------------------------------
        //  CONTROL FLOW
        // -----------------------------------------------------------------------

        private void FormatIf(IfNode node)
        {
            string tabExp = new string(' ', _opts.IndentSize);
            for (int i = 0; i < node.Branches.Length; i++)
            {
                var branch = node.Branches[i];

                if (i == 0)
                {
                    // First branch: @if (cond) {
                    Ln($"@if ({branch.Condition}) {{");
                }
                else
                {
                    // Remove the '\n' that was appended after the previous closing '}'
                    // so we can emit '} @else...' on the same line.
                    if (_sb.Length > 0 && _sb[_sb.Length - 1] == '\n')
                        _sb.Length--;

                    if (branch.Condition != null)
                        _sb.Append($" @else if ({branch.Condition}) {{\n");
                    else
                        _sb.Append(" @else {\n");
                }

                _indent++;
                FormatDirectiveBody(
                    branch.Payload.BodyCode,
                    branch.Payload.BodyCodeOffset,
                    branch.Payload.BodyMarkupRanges,
                    branch.Payload.BodyBareJsxRanges,
                    branch.Payload.Body,
                    tabExp
                );
                _indent--;
                Ln("}");
            }
        }

        private void FormatForeach(ForeachNode node)
        {
            string tabExp = new string(' ', _opts.IndentSize);
            Ln($"@foreach ({node.IteratorDeclaration} in {node.CollectionExpression}) {{");
            _indent++;
            FormatDirectiveBody(
                node.Payload.BodyCode,
                node.Payload.BodyCodeOffset,
                node.Payload.BodyMarkupRanges,
                node.Payload.BodyBareJsxRanges,
                node.Payload.Body,
                tabExp
            );
            _indent--;
            Ln("}");
        }

        private void FormatFor(ForNode node)
        {
            string tabExp = new string(' ', _opts.IndentSize);
            Ln($"@for ({node.ForExpression}) {{");
            _indent++;
            FormatDirectiveBody(
                node.Payload.BodyCode,
                node.Payload.BodyCodeOffset,
                node.Payload.BodyMarkupRanges,
                node.Payload.BodyBareJsxRanges,
                node.Payload.Body,
                tabExp
            );
            _indent--;
            Ln("}");
        }

        private void FormatWhile(WhileNode node)
        {
            string tabExp = new string(' ', _opts.IndentSize);
            Ln($"@while ({node.Condition}) {{");
            _indent++;
            FormatDirectiveBody(
                node.Payload.BodyCode,
                node.Payload.BodyCodeOffset,
                node.Payload.BodyMarkupRanges,
                node.Payload.BodyBareJsxRanges,
                node.Payload.Body,
                tabExp
            );
            _indent--;
            Ln("}");
        }

        private void FormatSwitch(SwitchNode node)
        {
            string tabExp = new string(' ', _opts.IndentSize);
            Ln($"@switch ({node.SwitchExpression}) {{");
            _indent++;

            foreach (var sc in node.Cases)
            {
                // @case / @default label
                if (sc.ValueExpression != null)
                    Ln($"@case {sc.ValueExpression}:");
                else
                    Ln("@default:");

                // Body is indented under the case label
                _indent++;
                FormatDirectiveBody(
                    sc.Payload.BodyCode,
                    sc.Payload.BodyCodeOffset,
                    sc.Payload.BodyMarkupRanges,
                    sc.Payload.BodyBareJsxRanges,
                    sc.Payload.Body,
                    tabExp
                );
                _indent--;
            }

            _indent--;
            Ln("}");
        }

        /// <summary>
        /// Formats a directive body using the classic pattern: emit C# setup code
        /// with <see cref="EmitSetupCodeNormalized"/>, then <c>return (</c>, then
        /// the parsed JSX/directive body with <see cref="FormatNodeList"/>, then
        /// <c>);</c>.  Falls back to raw code output when no parsed body is available.
        /// </summary>
        private void FormatDirectiveBody(
            string? bodyCode,
            int bodyCodeOffset,
            ImmutableArray<(int Start, int End, int Line)> markupRanges,
            ImmutableArray<(int Start, int End, int Line)> bareRanges,
            ImmutableArray<AstNode> body,
            string tabExp
        )
        {
            if (string.IsNullOrEmpty(bodyCode) && body.IsDefaultOrEmpty)
                return;

            // If we have parsed body nodes (from ParseBodyForIde), use the
            // classic setup + return (...JSX...) pattern.
            if (!body.IsDefaultOrEmpty)
            {
                // Extract setup code: C# statements before the depth-0 'return'
                if (
                    !string.IsNullOrEmpty(bodyCode)
                    && ReturnFinder.TryFindTopLevelReturn(
                        bodyCode!,
                        0,
                        bodyCode!.Length,
                        out int returnStart,
                        out _,
                        out _,
                        out _,
                        useLastReturn: false
                    )
                    && returnStart > 0
                )
                {
                    string setupCode = bodyCode!.Substring(0, returnStart).TrimEnd();
                    if (!string.IsNullOrWhiteSpace(setupCode))
                    {
                        EmitDirectiveBodySetupCode(setupCode, tabExp);
                    }
                }

                Ln("return (");
                _indent++;
                FormatNodeList(body, topLevel: false);
                _indent--;
                Ln(");");
                return;
            }

            // No parsed body -> pure C# code
            if (!string.IsNullOrEmpty(bodyCode))
            {
                // Strip leading newlines that come from the `{\n` at the
                // directive opener - same reason as EmitDirectiveBodySetupCode
                // (the setup-code path above): keeping them produces a phantom
                // blank line between the opener `{` and the first body line
                // that grows by one on every format pass (non-idempotent).
                // This branch fires when the body contains only C# (e.g.
                // `} @else { return value; }` with no JSX) so the parsed-body
                // path is skipped and we land here directly.
                string trimmed = bodyCode!.TrimStart('\n', '\r');
                if (!string.IsNullOrEmpty(trimmed))
                    EmitSetupCodeNormalized(trimmed, tabExp);
            }
        }

        // -----------------------------------------------------------------------
        //  OUTPUT HELPERS
        // -----------------------------------------------------------------------

        private void EmitSetupCodeLines(string setupCode)
        {
            foreach (var line in setupCode.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                    Ln(trimmed);
            }
        }

        /// <summary>
        /// Append an indented line (or block of lines) terminating with a single LF.
        /// When <paramref name="text"/> contains embedded newlines (e.g. a multi-line
        /// attribute expression value), each continuation line is re-anchored to the
        /// current indent level plus its relative indentation from the first line.
        /// </summary>
        private void Ln(string text)
        {
            if (!text.Contains('\n'))
            {
                if (_indent > 0)
                    _sb.Append(IndentStr());
                _sb.Append(text);
                _sb.Append('\n');
                return;
            }

            // Multi-line: re-anchor every continuation line to current indent + relative.
            string tabExp = new string(' ', _opts.IndentSize);
            // Normalise CR/LF and strip any trailing blank lines before splitting.
            var lines = text.TrimEnd('\r', '\n')
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n');

            // Compute the minimum leading-space count of all non-blank continuation lines.
            int baseSpaces = int.MaxValue;
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                var exp = lines[i].Replace("\t", tabExp);
                int lead = exp.Length - exp.TrimStart().Length;
                if (lead < baseSpaces)
                    baseSpaces = lead;
            }
            if (baseSpaces == int.MaxValue)
                baseSpaces = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (i == 0)
                {
                    // First line - emit with the current indent prefix as-is.
                    if (_indent > 0)
                        _sb.Append(IndentStr());
                    _sb.Append(line.TrimEnd());
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    // Blank line - emit as truly empty (just the newline below).
                }
                else
                {
                    // Continuation line - strip the common base indentation and
                    // re-prefix with current indent + remaining relative indent.
                    var expLine = line.Replace("\t", tabExp);
                    int lead = expLine.Length - expLine.TrimStart().Length;
                    int rel = System.Math.Max(0, lead - baseSpaces);
                    if (_indent > 0)
                        _sb.Append(IndentStr());
                    if (rel > 0)
                        _sb.Append(new string(' ', rel));
                    _sb.Append(expLine.TrimStart().TrimEnd());
                }
                _sb.Append('\n');
            }
        }

        private string IndentStr()
        {
            if (_indent <= 0)
                return string.Empty;
            return _opts.UseTabIndent
                ? new string('\t', _indent)
                : new string(' ', _indent * _opts.IndentSize);
        }

        // -- JSX-in-setup formatting -------------------------------------------

        /// <summary>
        /// Emits function-style setup code that contains embedded JSX paren-blocks
        /// (e.g. <c>var x = (&lt;Box/&gt;);</c>).
        ///
        /// C# segments between JSX blocks are re-indented via
        /// <see cref="EmitCSharpLines"/>.  Each JSX block is parsed and formatted
        /// through <see cref="FormatNodeList"/> so it gets the same canonical
        /// element / attribute layout as markup inside <c>return (...)</c>.
        /// </summary>
        private void EmitSetupCodeWithJsx(
            string setupCode,
            DirectiveSet directives,
            string tabExp,
            HashSet<int> insertedPositions,
            int[] normalizedToRawOffset
        )
        {
            // setupCode is already normalised by NormalizeBareJsx; insertedPositions
            // tracks which '(' were inserted so we can distinguish synthetic vs original.

            var blocks = ScanJsxParenBlocks(setupCode);
            if (blocks.Count == 0)
            {
                EmitCSharpLines(
                    setupCode,
                    tabExp,
                    firstLineStripped: true,
                    suppressLastNewline: false
                );
                _sb.Append('\n');
                return;
            }

            // -- Build a csharpWithPlaceholders string -------------------------
            // Replace each multi-line JSX paren-block with a single-line
            // placeholder that preserves the brace context.  EmitCSharpLines
            // formats the entire C# as one pass (correct brace tracking), then
            // we splice the formatted JSX back in place of the placeholders.
            //
            // Single-line paren blocks are left as-is (they don't break brace
            // context and EmitCSharpLines handles them fine).
            const string PLACEHOLDER_PREFIX = "___UITKX_JSX_PLACEHOLDER_";
            // U-36: identify each ORIGINAL (non-synthetic) block by its ABSOLUTE .uitkx
            // source offset (the content start, i.e. the position right after '('),
            // recovered via normalizedToRawOffset + FunctionSetupStartOffset - NOT by a
            // running counter into directives.SetupCodeMarkupRanges. ScanJsxParenBlocks
            // (this method's own paren-block detector, run against the NORMALISED text)
            // and DirectiveParser.FindJsxBlockRanges (which built SetupCodeMarkupRanges,
            // run against the RAW source and ALSO recording `(@if ...)` directive-paren
            // blocks and bare `=> <Tag` ranges that ScanJsxParenBlocks does not recognize)
            // are independent detectors that do NOT agree on which parens are "JSX
            // blocks" - a running counter silently desyncs the moment the two detectors'
            // counts diverge, splicing one block's markup into a different block's
            // placeholder. Matching by absolute source offset is correct regardless of
            // how the two detectors' counts compare.
            var multiLineBlocks =
                new List<(int AbsContentStart, int Start, int End, bool IsSynthetic)>();

            var csBuilder = new System.Text.StringBuilder(setupCode.Length);
            int pos = 0;

            foreach (var (jS, jE) in blocks)
            {
                // A block is synthetic if its '(' was inserted by NormalizeBareJsx.
                // insertedPositions contains output-string positions of inserted '('.
                bool isSynthetic = insertedPositions.Contains(jS);

                // Check if this block spans multiple lines, or is a single-line
                // container element (has `</` closing tag) that should be expanded.
                bool isMultiLine = false;
                bool hasContainerClose = false;
                for (int k = jS; k < jE; k++)
                {
                    if (setupCode[k] == '\n')
                    {
                        isMultiLine = true;
                        break;
                    }
                    if (setupCode[k] == '<' && k + 1 < jE && setupCode[k + 1] == '/')
                        hasContainerClose = true;
                }

                if (!isMultiLine && !hasContainerClose)
                    continue; // simple single-line self-closing blocks stay in the C# text

                // Append C# before this block.
                csBuilder.Append(setupCode, pos, jS - pos);

                // Emit a placeholder line: keep the opening `(` and replace
                // the content+closing with a marker that EmitCSharpLines will
                // output as a normal statement.
                csBuilder.Append($"{PLACEHOLDER_PREFIX}{multiLineBlocks.Count}___");

                int absContentStart = -1;
                if (!isSynthetic && jS >= 0 && jS < normalizedToRawOffset.Length)
                    absContentStart = directives.FunctionSetupStartOffset + normalizedToRawOffset[jS] + 1;

                multiLineBlocks.Add((absContentStart, jS, jE, isSynthetic));

                // Skip past the `)` and its trailing `;` or `,`.
                int afterPos = jE;
                while (
                    afterPos < setupCode.Length
                    && (setupCode[afterPos] == ' ' || setupCode[afterPos] == '\t')
                )
                    afterPos++;

                if (
                    afterPos < setupCode.Length
                    && (setupCode[afterPos] == ';' || setupCode[afterPos] == ',')
                )
                {
                    csBuilder.Append(setupCode[afterPos]); // keep the ; or ,
                    pos = afterPos + 1;
                }
                else
                {
                    pos = jE;
                }
            }

            // Append remaining C# after the last block.
            if (pos < setupCode.Length)
                csBuilder.Append(setupCode, pos, setupCode.Length - pos);

            // -- Format the entire C# (with placeholders) in one pass ----------
            int savedSbLen = _sb.Length;
            EmitCSharpLines(
                csBuilder.ToString(),
                tabExp,
                firstLineStripped: true,
                suppressLastNewline: false
            );
            _sb.Append('\n');

            // If no multi-line blocks, we're done.
            if (multiLineBlocks.Count == 0)
                return;

            // -- Extract the formatted C# and splice JSX in -------------------
            string formattedCs = _sb.ToString(savedSbLen, _sb.Length - savedSbLen);
            _sb.Length = savedSbLen; // rewind

            var lines = formattedCs.Split('\n');
            int baseIndent = _indent; // component-level indent (typically 1)

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];

                // Check if this line contains a placeholder.
                int phIdx = line.IndexOf(PLACEHOLDER_PREFIX, System.StringComparison.Ordinal);
                if (phIdx < 0)
                {
                    _sb.Append(line);
                    if (li < lines.Length - 1)
                        _sb.Append('\n');
                    continue;
                }

                // Extract placeholder index.
                int markerStart = phIdx + PLACEHOLDER_PREFIX.Length;
                int markerEnd = line.IndexOf("___", markerStart, System.StringComparison.Ordinal);
                if (markerEnd < 0)
                {
                    _sb.Append(line);
                    if (li < lines.Length - 1)
                        _sb.Append('\n');
                    continue;
                }

                int placeholderIdx;
                if (
                    !int.TryParse(
                        line.Substring(markerStart, markerEnd - markerStart),
                        out placeholderIdx
                    )
                    || placeholderIdx < 0
                    || placeholderIdx >= multiLineBlocks.Count
                )
                {
                    _sb.Append(line);
                    if (li < lines.Length - 1)
                        _sb.Append('\n');
                    continue;
                }

                var (absContentStart, origStart, origEnd, blockIsSynthetic) = multiLineBlocks[
                    placeholderIdx
                ];

                // Everything before the placeholder is the C# prefix (e.g. "    var oneTest = ").
                string prefix = line.Substring(0, phIdx);
                // Trailing punctuation (e.g. ";") after the placeholder marker.
                string suffix = line.Substring(markerEnd + 3).TrimEnd();

                // Measure the indent of this line (in spaces).
                int lineIndentSpaces = 0;
                foreach (char ch in prefix)
                {
                    if (ch == ' ')
                        lineIndentSpaces++;
                    else if (ch == '\t')
                        lineIndentSpaces += _opts.IndentSize;
                    else
                        break;
                }

                // Set _indent to match the C# nesting.
                int jsxIndent = lineIndentSpaces / _opts.IndentSize;

                // Emit the line prefix (e.g. "    var oneTest = ") and opening `(`.
                _sb.Append(prefix);
                _sb.Append("(\n");

                // -- Format JSX content ----------------------------------------
                _indent = jsxIndent + 1;
                bool jsxEmitted = false;

                bool foundRange = false;
                int rangeStart = 0, rangeEnd = 0, rangeLine = 0;
                if (!blockIsSynthetic && absContentStart >= 0 && !directives.SetupCodeMarkupRanges.IsDefaultOrEmpty)
                {
                    foreach (var (s, e, ln) in directives.SetupCodeMarkupRanges)
                    {
                        if (s == absContentStart)
                        {
                            rangeStart = s;
                            rangeEnd = e;
                            rangeLine = ln;
                            foundRange = true;
                            break;
                        }
                    }
                }

                if (foundRange && !string.IsNullOrEmpty(_source))
                {
                    try
                    {
                        var jsxDiags = new List<ParseDiagnostic>();
                        var jsxDirectives = directives with
                        {
                            MarkupStartIndex = rangeStart,
                            MarkupEndIndex = rangeEnd,
                            MarkupStartLine = rangeLine,
                        };
                        var jsxNodes = UitkxParser.Parse(
                            _source,
                            _filePath,
                            jsxDirectives,
                            jsxDiags
                        );
                        bool hasErrors = false;
                        foreach (var d in jsxDiags)
                            if (d.Severity == ParseSeverity.Error)
                            {
                                hasErrors = true;
                                break;
                            }

                        if (!hasErrors && jsxNodes.Length > 0)
                        {
                            FormatNodeList(jsxNodes, topLevel: false);
                            jsxEmitted = true;
                        }
                    }
                    catch
                    { /* best-effort */
                    }
                }

                // For synthetic blocks (created by NormalizeBareJsx), try
                // parsing the JSX content directly from the normalised text.
                if (!jsxEmitted && blockIsSynthetic)
                {
                    int contentStart = origStart + 1; // skip '('
                    int contentEnd = origEnd - 1; // skip ')'
                    int contentLen = contentEnd - contentStart;
                    if (contentLen > 0)
                    {
                        string jsxText = setupCode.Substring(contentStart, contentLen).Trim();
                        try
                        {
                            var synthDiags = new List<ParseDiagnostic>();
                            var synthDirectives = directives with
                            {
                                MarkupStartIndex = 0,
                                MarkupEndIndex = jsxText.Length,
                                MarkupStartLine = 1,
                            };
                            var synthNodes = UitkxParser.Parse(
                                jsxText,
                                _filePath,
                                synthDirectives,
                                synthDiags
                            );
                            bool hasErrors = false;
                            foreach (var d in synthDiags)
                                if (d.Severity == ParseSeverity.Error)
                                {
                                    hasErrors = true;
                                    break;
                                }

                            if (!hasErrors && synthNodes.Length > 0)
                            {
                                FormatNodeList(synthNodes, topLevel: false);
                                jsxEmitted = true;
                            }
                        }
                        catch
                        { /* best-effort */
                        }
                    }
                }

                if (!jsxEmitted)
                {
                    // Fallback: raw re-indent.
                    int contentStart = origStart + 1;
                    int contentEnd = origEnd - 1;
                    int contentLen = contentEnd - contentStart;
                    if (contentLen < 0)
                        contentLen = 0;
                    string jsxContent = setupCode.Substring(contentStart, contentLen);
                    EmitCSharpLines(
                        jsxContent,
                        tabExp,
                        firstLineStripped: false,
                        suppressLastNewline: false
                    );
                }

                // -- Closing ')' -----------------------------------------------
                _indent = jsxIndent;
                Ln(")" + suffix);
                _indent = baseIndent;

                // Don't append \n here - the `Ln` already did.
                // But the for loop expects to append \n, so skip it for this line.
                // Actually, Ln already appended \n. We just need to not double it.
                // The loop would append \n if li < lines.Length - 1, but Ln already did.
                // So we skip the \n append for this line.
                continue;
            }

            _indent = baseIndent;
        }

        // -- Directive body setup code with bare-JSX support ---------------------------

        /// <summary>
        /// Formats directive body setup code (C# statements before the
        /// <c>return</c> inside an <c>@foreach</c>, <c>@if</c>, etc.) with
        /// full bare-JSX normalisation.  Bare JSX forms (e.g. <c>var x = &lt;Tag/&gt;</c>
        /// or <c>return &lt;Tag/&gt;</c>) are wrapped in <c>()</c> by
        /// <see cref="NormalizeBareJsx"/> before formatting so they are treated
        /// identically to already-parenthesised blocks.
        ///
        /// Appends the formatted code plus a trailing blank-separator line to
        /// <see cref="_sb"/> (replacing the <c>EmitSetupCodeNormalized + \\n</c>
        /// pattern used in callers for the no-JSX path).
        /// </summary>
        private void EmitDirectiveBodySetupCode(string setupCode, string tabExp)
        {
            // Strip leading newlines that come from the `{\n` at the directive
            // opener.  Keeping them would produce a phantom blank line between
            // `{` and the first setup-code statement that grows by one line on
            // every format pass, causing divergence.
            string stripped = setupCode.TrimStart('\n', '\r');
            if (string.IsNullOrWhiteSpace(stripped))
                return;

            string normalized = NormalizeBareJsx(stripped, out var insertedPositions);
            var jsxBlocks = ScanJsxParenBlocks(normalized);
            if (jsxBlocks.Count == 0)
            {
                EmitSetupCodeNormalized(normalized, tabExp);
                _sb.Append('\n'); // blank separator before `return (`
                return;
            }
            EmitBodySetupCodeWithJsx(normalized, tabExp, insertedPositions);
            // EmitBodySetupCodeWithJsx appends the blank separator internally.
        }

        /// <summary>
        /// Variant of <see cref="EmitSetupCodeWithJsx"/> for directive-body
        /// setup code.  All JSX paren-blocks are parsed from the <paramref
        /// name="normalizedCode"/> string directly (no <c>_source</c> lookup
        /// via <c>SetupCodeMarkupRanges</c>) because directive-body JSX is
        /// always either originally parenthesised inline or synthetically
        /// wrapped by <see cref="NormalizeBareJsx"/>.  Both cases produce JSX
        /// that lives entirely within the provided string.
        /// </summary>
        private void EmitBodySetupCodeWithJsx(
            string normalizedCode,
            string tabExp,
            HashSet<int> insertedPositions
        )
        {
            var blocks = ScanJsxParenBlocks(normalizedCode);
            if (blocks.Count == 0)
            {
                EmitCSharpLines(
                    normalizedCode,
                    tabExp,
                    firstLineStripped: true,
                    suppressLastNewline: false
                );
                _sb.Append('\n');
                return;
            }

            // -- Build csharpWithPlaceholders ----------------------------------------
            const string PLACEHOLDER_PREFIX = "___UITKX_JSX_PLACEHOLDER_";
            var multiLineBlocks = new List<(int Start, int End)>();
            var csBuilder = new System.Text.StringBuilder(normalizedCode.Length);
            int pos = 0;

            foreach (var (jS, jE) in blocks)
            {
                // Check if this block spans multiple lines or is a container element.
                bool isMultiLine = false;
                bool hasContainerClose = false;
                for (int k = jS; k < jE; k++)
                {
                    if (normalizedCode[k] == '\n')
                    {
                        isMultiLine = true;
                        break;
                    }
                    if (normalizedCode[k] == '<' && k + 1 < jE && normalizedCode[k + 1] == '/')
                        hasContainerClose = true;
                }

                if (!isMultiLine && !hasContainerClose)
                    continue; // simple single-line self-closing block: stay in C# text

                // Append C# before this block.
                csBuilder.Append(normalizedCode, pos, jS - pos);

                // Replace with a placeholder marker.
                csBuilder.Append($"{PLACEHOLDER_PREFIX}{multiLineBlocks.Count}___");
                multiLineBlocks.Add((jS, jE));

                // Skip past the `)` and its trailing `;` or `,`.
                int afterPos = jE;
                while (
                    afterPos < normalizedCode.Length
                    && (normalizedCode[afterPos] == ' ' || normalizedCode[afterPos] == '\t')
                )
                    afterPos++;

                if (
                    afterPos < normalizedCode.Length
                    && (normalizedCode[afterPos] == ';' || normalizedCode[afterPos] == ',')
                )
                {
                    csBuilder.Append(normalizedCode[afterPos]); // keep the ; or ,
                    pos = afterPos + 1;
                }
                else
                {
                    pos = jE;
                }
            }

            if (pos < normalizedCode.Length)
                csBuilder.Append(normalizedCode, pos, normalizedCode.Length - pos);

            // -- Format the entire C# (with placeholders) in one pass ----------------
            int savedSbLen = _sb.Length;
            EmitCSharpLines(
                csBuilder.ToString(),
                tabExp,
                firstLineStripped: true,
                suppressLastNewline: false
            );
            _sb.Append('\n'); // blank separator line (also terminates the placeholder region)

            if (multiLineBlocks.Count == 0)
                return;

            // -- Splice JSX back in place of the placeholders ------------------------
            string formattedCs = _sb.ToString(savedSbLen, _sb.Length - savedSbLen);
            _sb.Length = savedSbLen; // rewind

            var lines = formattedCs.Split('\n');
            int baseIndent = _indent;

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];

                int phIdx = line.IndexOf(PLACEHOLDER_PREFIX, System.StringComparison.Ordinal);
                if (phIdx < 0)
                {
                    _sb.Append(line);
                    if (li < lines.Length - 1)
                        _sb.Append('\n');
                    continue;
                }

                int markerStart = phIdx + PLACEHOLDER_PREFIX.Length;
                int markerEnd = line.IndexOf("___", markerStart, System.StringComparison.Ordinal);
                if (markerEnd < 0)
                {
                    _sb.Append(line);
                    if (li < lines.Length - 1)
                        _sb.Append('\n');
                    continue;
                }

                int placeholderIdx;
                if (
                    !int.TryParse(
                        line.Substring(markerStart, markerEnd - markerStart),
                        out placeholderIdx
                    )
                    || placeholderIdx < 0
                    || placeholderIdx >= multiLineBlocks.Count
                )
                {
                    _sb.Append(line);
                    if (li < lines.Length - 1)
                        _sb.Append('\n');
                    continue;
                }

                var (origStart, origEnd) = multiLineBlocks[placeholderIdx];

                string prefix = line.Substring(0, phIdx);
                string suffix = line.Substring(markerEnd + 3).TrimEnd();

                // Measure the indent of this line (in spaces).
                int lineIndentSpaces = 0;
                foreach (char ch in prefix)
                {
                    if (ch == ' ')
                        lineIndentSpaces++;
                    else if (ch == '\t')
                        lineIndentSpaces += _opts.IndentSize;
                    else
                        break;
                }
                int jsxIndent = lineIndentSpaces / _opts.IndentSize;

                _sb.Append(prefix);
                _sb.Append("(\n");

                // Parse JSX from the normalised code string.
                // All blocks (original paren-wrapped or synthetic) are parsed
                // from the text directly - no _source/SetupCodeMarkupRanges lookup.
                _indent = jsxIndent + 1;
                bool jsxEmitted = false;

                int contentStart = origStart + 1; // skip '('
                int contentEnd = origEnd - 1; // skip ')'
                int contentLen = contentEnd - contentStart;
                if (contentLen > 0)
                {
                    string jsxText = normalizedCode.Substring(contentStart, contentLen).Trim();
                    try
                    {
                        var synthDiags = new List<ParseDiagnostic>();
                        var synthDirectives = _directives with
                        {
                            MarkupStartIndex = 0,
                            MarkupEndIndex = jsxText.Length,
                            MarkupStartLine = 1,
                        };
                        var synthNodes = UitkxParser.Parse(
                            jsxText,
                            _filePath,
                            synthDirectives,
                            synthDiags
                        );
                        bool hasErrors = false;
                        foreach (var d in synthDiags)
                            if (d.Severity == ParseSeverity.Error)
                            {
                                hasErrors = true;
                                break;
                            }

                        if (!hasErrors && synthNodes.Length > 0)
                        {
                            FormatNodeList(synthNodes, topLevel: false);
                            jsxEmitted = true;
                        }
                    }
                    catch
                    { /* best-effort */
                    }
                }

                if (!jsxEmitted)
                {
                    // Fallback: raw re-indent.
                    int rawLen = System.Math.Max(0, contentEnd - contentStart);
                    string jsxContent = normalizedCode.Substring(contentStart, rawLen);
                    EmitCSharpLines(
                        jsxContent,
                        tabExp,
                        firstLineStripped: false,
                        suppressLastNewline: false
                    );
                }

                _indent = jsxIndent;
                Ln(")" + suffix);
                _indent = baseIndent;
                continue;
            }

            _indent = baseIndent;
        }

        /// <summary>
        /// Scans <paramref name="code"/> for JSX paren-blocks: a <c>'('</c> whose
        /// first non-whitespace content is <c>'&lt;'</c>.  Returns a list of
        /// <c>(BlockStart, BlockEnd)</c> tuples where
        /// <c>code[BlockStart]='('</c> and <c>code[BlockEnd-1]=')'</c>.
        /// Bare forms (<c>=&gt; &lt;Tag</c> and <c>= &lt;Tag</c>) should be
        /// pre-normalised via <see cref="NormalizeBareArrows"/> before calling
        /// this method.
        /// </summary>
        private static List<(int Start, int End)> ScanJsxParenBlocks(string code)
        {
            var result = new List<(int, int)>();
            int i = 0;
            while (i < code.Length)
            {
                // Skip // line comments
                if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
                {
                    while (i < code.Length && code[i] != '\n')
                        i++;
                    continue;
                }

                // Skip /* ... */ block comments (U-07 sibling gap: this detector had the
                // same block-comment blindness as DirectiveParser.FindJsxBlockRanges, so a
                // commented-out "(<Tag/>)" was mis-treated as a live JSX paren-block).
                if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
                {
                    int end = code.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                    i = end >= 0 ? end + 2 : code.Length;
                    continue;
                }

                // Skip string and char literals
                if (DirectiveParser.TrySkipStringOrCharLiteral(code, code.Length, ref i))
                    continue;

                if (code[i] != '(')
                {
                    i++;
                    continue;
                }

                int peek = i + 1;
                while (
                    peek < code.Length
                    && (
                        code[peek] == ' '
                        || code[peek] == '\t'
                        || code[peek] == '\r'
                        || code[peek] == '\n'
                    )
                )
                    peek++;

                if (
                    peek >= code.Length
                    || code[peek] != '<'
                    || peek + 1 >= code.Length
                    || !char.IsLetter(code[peek + 1])
                )
                {
                    i++;
                    continue;
                }

                int depth = 1,
                    j = i + 1;
                while (j < code.Length && depth > 0)
                {
                    if (DirectiveParser.TrySkipStringOrCharLiteral(code, code.Length, ref j))
                        continue;
                    if (code[j] == '(')
                        depth++;
                    else if (code[j] == ')')
                        depth--;
                    j++;
                }

                if (depth == 0)
                {
                    result.Add((i, j)); // i = '(' position, j = right after ')'
                    i = j;
                }
                else
                {
                    i++;
                }
            }
            return result;
        }

        /// <summary>
        /// Pre-processes <paramref name="code"/> to normalise bare JSX after
        /// <c>=&gt;</c> (arrow) or <c>=</c> (assignment) into paren-wrapped form.
        /// <list type="bullet">
        ///   <item><c>=&gt; &lt;Tag .../&gt;</c>  -&gt;  <c>=&gt; (&lt;Tag .../&gt;)</c></item>
        ///   <item><c>= &lt;Tag .../&gt;</c>  -&gt;  <c>= (&lt;Tag .../&gt;)</c></item>
        /// </list>
        /// Also handles:
        /// <list type="bullet">
        ///   <item><c>return &lt;Tag .../&gt;</c>  -&gt;  <c>return (&lt;Tag .../&gt;)</c></item>
        ///   <item><c>? &lt;Tag .../&gt;</c> (ternary)  -&gt;  <c>? (&lt;Tag .../&gt;)</c></item>
        ///   <item><c>: &lt;Tag .../&gt;</c> (ternary)  -&gt;  <c>: (&lt;Tag .../&gt;)</c></item>
        /// </list>
        /// Already paren-wrapped expressions are left unchanged.  The result
        /// can then be scanned by <see cref="ScanJsxParenBlocks"/>.
        /// <para>
        /// <paramref name="insertedOpenParenPositions"/> receives the original
        /// source positions where <c>(</c> was inserted.  This lets callers
        /// distinguish blocks that already existed in the original source from
        /// those that were synthesised by normalisation.
        /// </para>
        /// </summary>
        private static string NormalizeBareJsx(
            string code,
            out HashSet<int> insertedOpenParenPositions
        ) => NormalizeBareJsx(code, out insertedOpenParenPositions, out _);

        /// <summary>
        /// Overload additionally returning a NORMALISED-string-offset -> RAW(<paramref name="code"/>)
        /// -offset map (length = normalized length + 1, so the exclusive end position is valid too).
        /// Insertions only ADD characters, so every non-inserted character maps 1:1 back to its
        /// original position; an inserted character maps to the raw position it was inserted at.
        /// Lets a caller recover the ORIGINAL absolute source offset of a block found in the
        /// normalised text without re-deriving it from a fragile parallel counter (see U-36).
        /// </summary>
        private static string NormalizeBareJsx(
            string code,
            out HashSet<int> insertedOpenParenPositions,
            out int[] normalizedToRawOffset
        )
        {
            var insertions = new List<(int Position, char Char)>();
            int i = 0;
            while (i < code.Length)
            {
                // Skip // line comments
                if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
                {
                    while (i < code.Length && code[i] != '\n')
                        i++;
                    continue;
                }

                // Skip string and char literals
                if (
                    i < code.Length
                    && (
                        code[i] == '"'
                        || code[i] == '\''
                        || (code[i] == '@' && i + 1 < code.Length && code[i + 1] == '"')
                        || (code[i] == '$' && i + 1 < code.Length && code[i + 1] == '"')
                        || (code[i] == '$' && i + 1 < code.Length && code[i + 1] == '@')
                    )
                )
                {
                    int saved = i;
                    if (DirectiveParser.TrySkipStringOrCharLiteral(code, code.Length, ref i))
                        continue;
                    i = saved;
                }

                // -- return <Tag -----------------------------------------
                if (
                    code[i] == 'r'
                    && i + 5 < code.Length
                    && code.Substring(i, 6) == "return"
                    && (i == 0 || !(char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_'))
                    && (
                        i + 6 >= code.Length
                        || !(char.IsLetterOrDigit(code[i + 6]) || code[i + 6] == '_')
                    )
                )
                {
                    int peek = i + 6;
                    while (
                        peek < code.Length
                        && (
                            code[peek] == ' '
                            || code[peek] == '\t'
                            || code[peek] == '\r'
                            || code[peek] == '\n'
                        )
                    )
                        peek++;

                    if (
                        peek < code.Length
                        && code[peek] == '<'
                        && peek + 1 < code.Length
                        && char.IsLetter(code[peek + 1])
                    )
                    {
                        int jsxEnd = FindJsxElementEnd(code, peek, code.Length);
                        if (jsxEnd > peek)
                        {
                            // Insert '(' then '\n' so the normalised text reads
                            // "return (\n<Tag..." matching the canonical form.
                            // Without the '\n', the first format pass produces
                            // "return (<Tag..." requiring a second pass to
                            // break the line - an idempotency bug.
                            insertions.Add((peek, '('));
                            insertions.Add((peek, '\n'));
                            insertions.Add((jsxEnd, ')'));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                // -- ? <Tag  (ternary true, but NOT ?. or ??) ------------
                if (
                    code[i] == '?'
                    && i + 1 < code.Length
                    && code[i + 1] != '.'
                    && code[i + 1] != '?'
                )
                {
                    int peek = i + 1;
                    while (
                        peek < code.Length
                        && (
                            code[peek] == ' '
                            || code[peek] == '\t'
                            || code[peek] == '\r'
                            || code[peek] == '\n'
                        )
                    )
                        peek++;

                    if (
                        peek < code.Length
                        && code[peek] == '<'
                        && peek + 1 < code.Length
                        && char.IsLetter(code[peek + 1])
                    )
                    {
                        int jsxEnd = FindJsxElementEnd(code, peek, code.Length);
                        if (jsxEnd > peek)
                        {
                            insertions.Add((peek, '('));
                            insertions.Add((jsxEnd, ')'));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                // -- : <Tag  (ternary false, but NOT ::) -----------------
                if (code[i] == ':' && i + 1 < code.Length && code[i + 1] != ':')
                {
                    int peek = i + 1;
                    while (
                        peek < code.Length
                        && (
                            code[peek] == ' '
                            || code[peek] == '\t'
                            || code[peek] == '\r'
                            || code[peek] == '\n'
                        )
                    )
                        peek++;

                    if (
                        peek < code.Length
                        && code[peek] == '<'
                        && peek + 1 < code.Length
                        && char.IsLetter(code[peek + 1])
                    )
                    {
                        int jsxEnd = FindJsxElementEnd(code, peek, code.Length);
                        if (jsxEnd > peek)
                        {
                            insertions.Add((peek, '('));
                            insertions.Add((jsxEnd, ')'));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                if (code[i] == '=')
                {
                    int peek;
                    bool isArrow = i + 1 < code.Length && code[i + 1] == '>';

                    if (isArrow)
                    {
                        // => <Tag
                        peek = i + 2;
                    }
                    else
                    {
                        // Bare = : skip ==, !=, <=, >=
                        if (i + 1 < code.Length && code[i + 1] == '=')
                        {
                            i++;
                            continue;
                        }
                        if (
                            i > 0
                            && (code[i - 1] == '!' || code[i - 1] == '<' || code[i - 1] == '>')
                        )
                        {
                            i++;
                            continue;
                        }
                        peek = i + 1;
                    }

                    while (
                        peek < code.Length
                        && (
                            code[peek] == ' '
                            || code[peek] == '\t'
                            || code[peek] == '\r'
                            || code[peek] == '\n'
                        )
                    )
                        peek++;

                    if (
                        peek < code.Length
                        && code[peek] == '<'
                        && peek + 1 < code.Length
                        && char.IsLetter(code[peek + 1])
                    )
                    {
                        int jsxEnd = FindJsxElementEnd(code, peek, code.Length);
                        if (jsxEnd > peek)
                        {
                            insertions.Add((peek, '('));
                            insertions.Add((jsxEnd, ')'));
                            i = jsxEnd;
                            continue;
                        }
                    }
                }
                i++;
            }

            if (insertions.Count == 0)
            {
                insertedOpenParenPositions = new HashSet<int>();
                normalizedToRawOffset = new int[code.Length + 1];
                for (int k = 0; k <= code.Length; k++)
                    normalizedToRawOffset[k] = k;
                return code;
            }

            // Build the output and compute normalised-string positions of
            // inserted '(' characters so callers can identify synthetic blocks.
            insertedOpenParenPositions = new HashSet<int>();
            var sb = new System.Text.StringBuilder(code.Length + insertions.Count);
            var normToRaw = new List<int>(code.Length + insertions.Count + 1);
            int pos = 0;
            for (int idx = 0; idx < insertions.Count; idx++)
            {
                var (insPos, ch) = insertions[idx];
                for (int k = pos; k < insPos; k++)
                    normToRaw.Add(k);
                sb.Append(code, pos, insPos - pos);
                if (ch == '(')
                    insertedOpenParenPositions.Add(sb.Length);
                sb.Append(ch);
                normToRaw.Add(insPos); // inserted char maps to the raw insertion point
                pos = insPos;
            }
            for (int k = pos; k < code.Length; k++)
                normToRaw.Add(k);
            sb.Append(code, pos, code.Length - pos);
            normToRaw.Add(code.Length);
            normalizedToRawOffset = normToRaw.ToArray();
            return sb.ToString();
        }

        /// <summary>Backward-compat overload that discards insertion tracking.</summary>
        private static string NormalizeBareArrows(string code)
        {
            return NormalizeBareJsx(code, out _);
        }

        /// <summary>
        /// Finds the end position (exclusive) of a JSX element starting at
        /// <paramref name="start"/> (which must point to <c>&lt;</c>).
        /// Handles self-closing (<c>/&gt;</c>) and container elements with
        /// nested children.  Skips over string literals and <c>{expr}</c> blocks.
        /// </summary>
        private static int FindJsxElementEnd(string text, int start, int limit) =>
            ReturnFinder.FindJsxElementEnd(text, start, limit);
    }
}
