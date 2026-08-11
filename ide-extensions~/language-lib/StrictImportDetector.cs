using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Ruitk.Language.Parser;

namespace Ruitk.Language
{
    /// <summary>
    /// The strict-mode reference detector (import/export grammar, leg 3, plan §6 "strict detector").
    /// A purely syntactic name-table match (NOT semantic): over a file's markup tags and setup/attr
    /// expressions it finds references to names that OTHER files in the same asmdef export, and
    /// reports the frozen family diagnostics when the reference is not backed by an <c>import</c>:
    /// <list type="bullet">
    ///   <item><description><b>UITKX2305</b> — the name is exported by a peer file but not imported
    ///   (carries the exact <c>import { … } from "…"</c> line to add).</description></item>
    ///   <item><description><b>UITKX2307</b> — a <c>useX()</c> hook-shaped call resolves to no peer
    ///   export and is not an ambient/builtin hook (hand-written C# hooks are exempt, A4).</description></item>
    /// </list>
    /// Pure and host-agnostic (filesystem + builtin allowlists injected) so it is shared by the source
    /// generator (build) and the LSP (live editor diagnostics) and is directly unit-testable.
    /// </summary>
    public static class StrictImportDetector
    {
        /// <summary><see cref="Util"/> is deliberately OUTSIDE the dotted-access scan:
        /// a util export binds a FUNCTION, so a `Name.member` reference can never be
        /// satisfied by importing it — matching utils there produced self-inconsistent
        /// 2305 hints (field find: `Tick.CreateInitialState()` suggested importing the
        /// unrelated util `Tick` from another game's GameLogic).</summary>
        public enum ExportKind { Component, Hook, Module, Util }

        /// <summary>An exported name in a peer file of the same asmdef.</summary>
        public sealed record PeerExport(string Name, string TargetFilePath, ExportKind Kind);

        /// <summary>One strict-mode finding: the frozen code, the rendered message, and its source line.
        /// <para><see cref="IsHeuristic"/> marks findings produced by scanning C# EXPRESSION text
        /// (bare <c>useX(</c> calls, <c>Name.member</c> access) — shapes that plain ambient C# can
        /// legitimately produce (hand-written hooks, nested enums via <c>@using static</c>,
        /// <c>UnityEngine.Screen.width</c>, …). Consumers MUST surface heuristic findings as
        /// warnings, never build-breaking errors: a real missing import still fails the emitted
        /// C# with CS0103/CS0246, while a false positive here would break an otherwise-valid
        /// build (seen in the wild: an own-module nested enum flagged as another file's module).
        /// Component-tag findings (<c>&lt;X&gt;</c> is uitkx-only syntax) are sound and stay
        /// non-heuristic.</para>
        /// <para><see cref="Column"/>/<see cref="EndColumn"/> are the 0-based squiggle span on
        /// <see cref="Line"/> (<c>EndColumn</c> exclusive; <c>-1</c> = untracked → consumers fall
        /// back to a line-start anchor). Import-shaped findings anchor to the specifier string
        /// (2300/2308/2314) or the offending imported name (2301/2304); reference findings
        /// (2305/2307) anchor to the referenced identifier.</para></summary>
        public sealed record Finding(
            string Code, string Message, int Line, bool IsHeuristic = false,
            int Column = -1, int EndColumn = -1);

        // <TagName ...   — a markup component tag (PascalCase peers only matter; builtins are lowercase/known).
        private static readonly System.Text.RegularExpressions.Regex s_tagRe =
            new(@"<\s*([A-Za-z_][A-Za-z0-9_]*)", System.Text.RegularExpressions.RegexOptions.Compiled);
        // useX( — a hook-shaped call (frozen scan shape \buse[A-Z_]\w*\s*\().
        private static readonly System.Text.RegularExpressions.Regex s_hookRe =
            new(@"\b(use[A-Z_][A-Za-z0-9_]*)\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);
        // Module. — a module member access.
        private static readonly System.Text.RegularExpressions.Regex s_moduleRe =
            new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\.", System.Text.RegularExpressions.RegexOptions.Compiled);
        // Any identifier — the unused-import scan's reference universe (bare value refs included).
        private static readonly System.Text.RegularExpressions.Regex s_identRe =
            new(@"[A-Za-z_][A-Za-z0-9_]*", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Detect unimported references in <paramref name="importerFilePath"/>'s code.
        /// <paramref name="scannableCode"/> is the file's setup + markup text (string/comment content
        /// is blanked out by the caller). <paramref name="peerExports"/> are the exports of OTHER files
        /// in the same asmdef. <paramref name="isBuiltinHook"/> returns true for ambient/builtin hooks
        /// (e.g. <c>useState</c>) that need no import. Line numbers are approximate (1 when unknown) —
        /// callers that need precise anchors can enrich later.
        /// </summary>
        public static List<Finding> Detect(
            DirectiveSet directives,
            string importerFilePath,
            string scannableCode,
            IReadOnlyList<PeerExport> peerExports,
            Func<string, bool> isBuiltinHook)
        {
            var findings = new List<Finding>();
            if (peerExports.Count == 0)
                return findings;

            // Names this file already imports (any specifier) — those are satisfied. The
            // satisfied name is the BOUND one: for `import { Widget as W }` only `W` resolves
            // in this file (a bare `Widget` reference still needs its own import); star and
            // default imports bind their alias.
            var imported = new HashSet<string>(StringComparer.Ordinal);
            if (!directives.Imports.IsDefaultOrEmpty)
                foreach (var imp in directives.Imports)
                {
                    var boundAliases = imp.Aliases.IsDefaultOrEmpty
                        ? ImmutableArray<string?>.Empty : imp.Aliases;
                    for (int k = 0; k < imp.Names.Length; k++)
                        imported.Add(k < boundAliases.Length && boundAliases[k] != null
                            ? boundAliases[k]! : imp.Names[k]);
                    if (imp.IsStar && imp.StarAlias != null) imported.Add(imp.StarAlias);
                    if (imp.IsDefault && imp.DefaultAlias != null) imported.Add(imp.DefaultAlias);
                }

            // Names declared locally in THIS file — never need importing.
            var selfNames = new HashSet<string>(StringComparer.Ordinal);
            if (!directives.ComponentDeclarations.IsDefaultOrEmpty)
                foreach (var c in directives.ComponentDeclarations) selfNames.Add(c.Name);
            if (!string.IsNullOrEmpty(directives.ComponentName)) selfNames.Add(directives.ComponentName!);
            if (!directives.MemberDeclarations.IsDefaultOrEmpty)
                foreach (var m in directives.MemberDeclarations) selfNames.Add(m.Name);

            // Index peer exports by (kind, name).
            var byKind = new Dictionary<ExportKind, Dictionary<string, PeerExport>>
            {
                [ExportKind.Component] = new(StringComparer.Ordinal),
                [ExportKind.Hook] = new(StringComparer.Ordinal),
                [ExportKind.Module] = new(StringComparer.Ordinal),
                [ExportKind.Util] = new(StringComparer.Ordinal),
            };
            foreach (var pe in peerExports)
                byKind[pe.Kind][pe.Name] = pe;

            var reported = new HashSet<string>(StringComparer.Ordinal);

            void Report(string code, string message, int line, string dedupeKey,
                bool heuristic = false, int col = -1, int endCol = -1)
            {
                if (reported.Add(dedupeKey))
                    findings.Add(new Finding(code, message, line, heuristic, col, endCol));
            }

            string importerDir = DirOf(importerFilePath);

            // The scrub is offset-preserving, so match indices in scannableCode are exact
            // source positions — anchor the squiggle to the referenced identifier itself.
            void FlagUnimportedPeer(string name, ExportKind kind, int nameIndex, bool heuristic)
            {
                if (selfNames.Contains(name) || imported.Contains(name)) return;
                if (!byKind[kind].TryGetValue(name, out var pe)) return;
                string spec = RelativeSpecifier(importerDir, pe.TargetFilePath);
                int col = ColAt(scannableCode, nameIndex);
                Report("UITKX2305",
                    $"`{name}` is defined in {ShortName(pe.TargetFilePath)} but not imported — add: import {{ {name} }} from \"{spec}\"",
                    LineAt(scannableCode, nameIndex), "2305:" + name, heuristic, col, col + name.Length);
            }

            // Component tags are uitkx-only syntax — a peer-exported <Tag> without an import is
            // sound evidence of a missing import (error-worthy).
            foreach (System.Text.RegularExpressions.Match m in s_tagRe.Matches(scannableCode))
                FlagUnimportedPeer(m.Groups[1].Value, ExportKind.Component, m.Groups[1].Index, heuristic: false);

            // Module member access is plain C# expression text: `GameScreen.MainMenu` may be a
            // nested enum reachable via `@using static`, a hand-written static class, or a Unity
            // type (`Screen.width`) that merely COLLIDES with some file's exported module name.
            // Heuristic → warning-tier only.
            foreach (System.Text.RegularExpressions.Match m in s_moduleRe.Matches(scannableCode))
                FlagUnimportedPeer(m.Groups[1].Value, ExportKind.Module, m.Groups[1].Index, heuristic: true);

            foreach (System.Text.RegularExpressions.Match m in s_hookRe.Matches(scannableCode))
            {
                string name = m.Groups[1].Value;
                if (selfNames.Contains(name) || imported.Contains(name)) continue;
                if (byKind[ExportKind.Hook].ContainsKey(name))
                {
                    // A same-named hand-written C# hook can shadow a peer export — heuristic.
                    FlagUnimportedPeer(name, ExportKind.Hook, m.Groups[1].Index, heuristic: true);
                }
                else if (!isBuiltinHook(name))
                {
                    // Not in any export table and not builtin. This is EXACTLY the shape a
                    // hand-written C# hook produces (they resolve ambiently and are documented
                    // as exempt), so it can only ever be a hint — heuristic/warning-tier.
                    int col = ColAt(scannableCode, m.Groups[1].Index);
                    Report("UITKX2307",
                        $"`{name}` is used like a uitkx component/hook but no file exports it",
                        LineAt(scannableCode, m.Groups[1].Index), "2307:" + name, heuristic: true,
                        col: col, endCol: col + name.Length);
                }
            }

            return findings;
        }

        /// <summary>
        /// Validate a file's <c>import</c> declarations (plan §6): resolve each specifier and report
        /// the frozen family diagnostics — 2300 (unresolvable / engine-native), 2308 (crosses an
        /// asmdef boundary), 2314 (<c>~/</c> escapes the root), and 2301 (the name is not exported by
        /// the resolved target). Complementary to <see cref="Detect"/> (which flags references that
        /// have no import); this flags imports that don't resolve. Filesystem + asmdef + export
        /// lookups are injected so it is pure and unit-testable.
        /// </summary>
        public static List<Finding> ValidateImports(
            DirectiveSet directives,
            string importerDir,
            string rootDir,
            string? importerAsmdef,
            Func<string, bool> fileExists,
            Func<string, string?> owningAsmdefOf,
            Func<string, string, bool> isExportedByFile,
            Func<string, DirectiveSet?>? parseTargetFile = null)
        {
            var findings = new List<Finding>();
            if (directives.Imports.IsDefaultOrEmpty)
                return findings;

            // UITKX2325 (G-05): an import alias colliding with a declaration in THIS file. Import-
            // vs-import bound-name collisions are already the parser's UITKX2303 (keyed on the
            // bound name, DirectiveParser.ReportDuplicateImports) — this covers the sub-case 2303
            // cannot see: the local declaration namespace.
            var localNames = new HashSet<string>(StringComparer.Ordinal);
            if (!directives.ComponentDeclarations.IsDefaultOrEmpty)
                foreach (var c in directives.ComponentDeclarations) localNames.Add(c.Name);
            if (!directives.MemberDeclarations.IsDefaultOrEmpty)
                foreach (var m in directives.MemberDeclarations) localNames.Add(m.Name);

            void CheckAliasCollision(string alias, int line, int col)
            {
                if (localNames.Contains(alias))
                    findings.Add(new Finding("UITKX2325",
                        $"import alias '{alias}' collides with a declaration in this file — rename the import",
                        line, Column: col, EndColumn: col >= 0 ? col + alias.Length : -1));
            }

            foreach (var imp in directives.Imports)
            {
                var res = ImportResolver.Resolve(
                    importerDir, imp.Specifier, rootDir, fileExists, owningAsmdefOf, importerAsmdef);

                // Specifier-shaped findings squiggle the quoted string (both quotes included).
                int specCol = imp.SpecifierColumn;
                int specEnd = specCol >= 0 ? specCol + imp.Specifier.Length + 2 : -1;

                switch (res.Status)
                {
                    case ImportResolveStatus.UnknownSpecifier:
                        findings.Add(new Finding("UITKX2300",
                            $"unknown import specifier `{imp.Specifier}` — no file at {imp.Specifier}(.uitkx)",
                            imp.Line, Column: specCol, EndColumn: specEnd));
                        break;
                    case ImportResolveStatus.CrossesBoundary:
                        findings.Add(new Finding("UITKX2308",
                            $"import crosses a module/root boundary ({importerAsmdef} -> {owningAsmdefOf(res.ProjectRelativePath ?? string.Empty)}) — imports are module-scoped in v1",
                            imp.Line, Column: specCol, EndColumn: specEnd));
                        break;
                    case ImportResolveStatus.RootEscape:
                        findings.Add(new Finding("UITKX2314",
                            $"'~/' root is not configured or resolves outside the project ('{imp.Specifier}')",
                            imp.Line, Column: specCol, EndColumn: specEnd));
                        break;
                    case ImportResolveStatus.Ok:
                        for (int k = 0; k < imp.Names.Length; k++)
                        {
                            string name = imp.Names[k];
                            if (isExportedByFile(name, res.ProjectRelativePath!))
                                continue;
                            int nameCol = k < imp.NameColumns.Length ? imp.NameColumns[k] : -1;
                            findings.Add(new Finding("UITKX2301",
                                $"`{name}` is not exported by {ShortName(res.ProjectRelativePath!)} — add `export` to its declaration",
                                imp.Line,
                                Column: nameCol,
                                EndColumn: nameCol >= 0 ? nameCol + name.Length : -1));
                        }

                        // G-05 full import surface: alias-collision (2325), default-import (2326),
                        // legacy-target (2109, Unity-local), and hook-rename-prefix (2110, Unity-local)
                        // checks. All require the target's parsed DirectiveSet.
                        // Aliases defaults to an uninitialized ImmutableArray (record default params
                        // must be compile-time constants, so `= ImmutableArray<string?>.Empty` is not
                        // legal there) — normalize before indexing/length checks, matching the
                        // IsDefaultOrEmpty convention used throughout this file/the pipeline.
                        var aliases = imp.Aliases.IsDefaultOrEmpty ? ImmutableArray<string?>.Empty : imp.Aliases;

                        for (int k = 0; k < imp.Names.Length && k < aliases.Length; k++)
                        {
                            string? alias = aliases[k];
                            if (alias == null) continue;
                            int nameCol = k < imp.NameColumns.Length ? imp.NameColumns[k] : imp.Column;
                            CheckAliasCollision(alias, imp.Line, nameCol);
                        }
                        if (imp.IsStar && imp.StarAlias != null)
                            CheckAliasCollision(imp.StarAlias, imp.Line, imp.Column);
                        if (imp.IsDefault && imp.DefaultAlias != null)
                            CheckAliasCollision(imp.DefaultAlias, imp.Line, imp.Column);

                        if ((imp.IsStar || imp.IsDefault || aliases.Any(a => a != null))
                            && parseTargetFile != null)
                        {
                            var target = parseTargetFile(res.ProjectRelativePath!);
                            if (target != null)
                            {
                                // (The UITKX2109 legacy-target gate retired with the wrapper
                                // grammar in 0.16.0 — a legacy target now errors at parse.)
                                if (imp.IsDefault && target.DefaultExportName == null)
                                {
                                    string suggested = target.ComponentDeclarations.Length > 0
                                        ? target.ComponentDeclarations[0].Name
                                        : (target.MemberDeclarations.Length > 0 ? target.MemberDeclarations[0].Name : "Name");
                                    findings.Add(new Finding("UITKX2326",
                                        $"'{ShortName(res.ProjectRelativePath!)}' has no default export — use a named import: import {{ {suggested} }} from \"{imp.Specifier}\"",
                                        imp.Line, Column: specCol, EndColumn: specEnd));
                                }

                                for (int k = 0; k < imp.Names.Length && k < aliases.Length; k++)
                                {
                                    string? alias = aliases[k];
                                    if (alias == null) continue;
                                    string original = imp.Names[k];
                                    bool originalIsHook = !target.MemberDeclarations.IsDefaultOrEmpty
                                        && target.MemberDeclarations.Any(m => m.Name == original && m.Kind == DeclKind.Hook);
                                    if (originalIsHook && !(alias.Length > 3 && alias[0] == 'u' && alias[1] == 's' && alias[2] == 'e' && char.IsUpper(alias[3])))
                                    {
                                        int nameCol = k < imp.NameColumns.Length ? imp.NameColumns[k] : imp.Column;
                                        findings.Add(new Finding("UITKX2110",
                                            $"renaming hook '{original}' to '{alias}' drops the 'use' prefix — hook bindings must stay 'use'-prefixed",
                                            imp.Line, Column: nameCol, EndColumn: nameCol >= 0 ? nameCol + alias.Length : -1));
                                    }
                                }
                            }
                        }
                        break;
                }
            }
            return findings;
        }

        /// <summary>
        /// UITKX2304 (error since 0.9.1): an imported binding never referenced in the file — named
        /// entries, star aliases, and default bindings alike. <paramref name="scannableCode"/>
        /// is the string/comment-scrubbed FULL file text (line-aligned with the parse: the
        /// scrub is offset-preserving), so import lines can be excluded from the reference
        /// universe. Pure and unit-testable.
        /// </summary>
        public static List<Finding> DetectUnusedImports(DirectiveSet directives, string scannableCode)
        {
            var findings = new List<Finding>();
            if (directives.Imports.IsDefaultOrEmpty)
                return findings;

            // Every identifier counts as a reference — new-mode VALUE imports are used as
            // BARE identifiers (`style={container}`), which the tag/hook-call/dotted scans
            // used by Detect() never see. Over-approximating "used" is the right direction
            // for a warning-tier check — EXCEPT on the import lines themselves: a binding
            // must not count ITSELF (field find: self-counting silenced 2304 entirely).
            var importLines = new HashSet<int>();
            foreach (var imp in directives.Imports)
                importLines.Add(imp.Line);

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            int scanLine = 1, scanIdx = 0;
            foreach (System.Text.RegularExpressions.Match m in s_identRe.Matches(scannableCode))
            {
                while (scanIdx < m.Index)
                {
                    if (scannableCode[scanIdx] == '\n') scanLine++;
                    scanIdx++;
                }
                if (!importLines.Contains(scanLine))
                    referenced.Add(m.Value);
            }

            foreach (var imp in directives.Imports)
            {
                var boundAliases = imp.Aliases.IsDefaultOrEmpty
                    ? ImmutableArray<string?>.Empty : imp.Aliases;
                for (int k = 0; k < imp.Names.Length; k++)
                {
                    string name = imp.Names[k];
                    // The file references the BOUND name — for `import { Widget as W }` usage
                    // of `W` marks the import used; the original `Widget` never appears.
                    string bound = k < boundAliases.Length && boundAliases[k] != null
                        ? boundAliases[k]! : name;
                    if (referenced.Contains(bound))
                        continue;
                    int nameCol = k < imp.NameColumns.Length ? imp.NameColumns[k] : -1;
                    findings.Add(new Finding("UITKX2304", $"unused import `{name}`", imp.Line,
                        Column: nameCol, EndColumn: nameCol >= 0 ? nameCol + name.Length : -1));
                }

                // Star and default BINDINGS are imports too (field find: an unused `* as X`
                // or default binding was never flagged — including the default part of a
                // combined import whose named part is used). The squiggle spans the WHOLE
                // alias token, located textually on the (line-aligned) import line — the
                // parse model does not track star/default alias columns.
                if (imp.IsStar && imp.StarAlias != null && !referenced.Contains(imp.StarAlias))
                {
                    int col = FindTokenColumnOnLine(scannableCode, imp.Line, imp.StarAlias);
                    findings.Add(new Finding("UITKX2304", $"unused import `{imp.StarAlias}`",
                        imp.Line,
                        Column: col >= 0 ? col : imp.Column,
                        EndColumn: col >= 0 ? col + imp.StarAlias.Length : -1));
                }
                if (imp.IsDefault && imp.DefaultAlias != null && !referenced.Contains(imp.DefaultAlias))
                {
                    int col = FindTokenColumnOnLine(scannableCode, imp.Line, imp.DefaultAlias);
                    findings.Add(new Finding("UITKX2304", $"unused import `{imp.DefaultAlias}`",
                        imp.Line,
                        Column: col >= 0 ? col : imp.Column,
                        EndColumn: col >= 0 ? col + imp.DefaultAlias.Length : -1));
                }
            }

            return findings;
        }

        /// <summary>Blank C# string/char literals + comments (offset-preserving) so the scan
        /// ignores them — EXCEPT interpolation holes: identifiers inside <c>$"{Gap}"</c> are
        /// real references and must survive the scrub (field find — with 2304 at error tier, a
        /// binding used only inside an interpolated string would otherwise fail the build).</summary>
        public static string ScrubNonCode(string text)
        {
            var sb = new StringBuilder(text);
            int i = 0;
            while (i < text.Length)
            {
                if (TryBlankInterpolatedString(text, sb, ref i))
                    continue;
                int start = i;
                if (CSharpLexFacts.TrySkipNonCode(text, ref i, text.Length) && i > start)
                {
                    for (int k = start; k < i && k < sb.Length; k++)
                        if (!char.IsWhiteSpace(sb[k])) sb[k] = ' ';
                    continue;
                }
                i++;
            }
            return sb.ToString();
        }

        /// <summary>Blanks an interpolated string's literal text while PRESERVING its
        /// interpolation-hole contents (offset-preserving). Handles <c>$"…"</c>, <c>$@"…"</c>,
        /// and <c>@$"…"</c>, the <c>{{</c>/<c>}}</c> escapes, nested braces inside holes, and
        /// nested plain literals/comments inside holes (blanked recursively). Returns false
        /// (cursor untouched) when <paramref name="i"/> is not at an interpolated string.</summary>
        private static bool TryBlankInterpolatedString(string text, StringBuilder sb, ref int i)
        {
            int len = text.Length;
            int start = i;
            bool verbatim;
            if (text[i] == '$' && i + 1 < len && text[i + 1] == '"')
            { verbatim = false; i += 2; }
            else if (text[i] == '$' && i + 2 < len && text[i + 1] == '@' && text[i + 2] == '"')
            { verbatim = true; i += 3; }
            else if (text[i] == '@' && i + 2 < len && text[i + 1] == '$' && text[i + 2] == '"')
            { verbatim = true; i += 3; }
            else
                return false;

            for (int k = start; k < i; k++) sb[k] = ' ';

            int holeDepth = 0;
            while (i < len)
            {
                char c = text[i];
                if (holeDepth == 0)
                {
                    if (c == '{' && i + 1 < len && text[i + 1] == '{')
                    { sb[i] = ' '; sb[i + 1] = ' '; i += 2; continue; }
                    if (c == '}' && i + 1 < len && text[i + 1] == '}')
                    { sb[i] = ' '; sb[i + 1] = ' '; i += 2; continue; }
                    if (c == '{') { holeDepth = 1; sb[i] = ' '; i++; continue; }
                    if (c == '"')
                    {
                        if (verbatim && i + 1 < len && text[i + 1] == '"')
                        { sb[i] = ' '; sb[i + 1] = ' '; i += 2; continue; }
                        sb[i] = ' '; i++; return true;
                    }
                    if (!verbatim && c == '\\')
                    {
                        sb[i] = ' ';
                        if (i + 1 < len) sb[i + 1] = ' ';
                        i += 2; continue;
                    }
                    if (!char.IsWhiteSpace(c)) sb[i] = ' ';
                    i++;
                }
                else
                {
                    if (c == '{') { holeDepth++; i++; continue; }
                    if (c == '}') { holeDepth--; if (holeDepth == 0) sb[i] = ' '; i++; continue; }
                    if (TryBlankInterpolatedString(text, sb, ref i))
                        continue;
                    int before = i;
                    if (CSharpLexFacts.TrySkipNonCode(text, ref i, len) && i > before)
                    {
                        for (int k = before; k < i && k < sb.Length; k++)
                            if (!char.IsWhiteSpace(sb[k])) sb[k] = ' ';
                        continue;
                    }
                    i++;
                }
            }
            return true;
        }

        /// <summary>0-based column of the first word-boundary occurrence of
        /// <paramref name="word"/> on 1-based <paramref name="line1"/>; -1 when absent.</summary>
        private static int FindTokenColumnOnLine(string text, int line1, string word)
        {
            int start = 0;
            for (int l = 1; l < line1; l++)
            {
                start = text.IndexOf('\n', start);
                if (start < 0) return -1;
                start++;
            }
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            for (int idx = start; idx < end; )
            {
                idx = text.IndexOf(word, idx, end - idx, StringComparison.Ordinal);
                if (idx < 0) return -1;
                bool leftOk = idx == 0
                    || (!char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] != '_');
                int after = idx + word.Length;
                bool rightOk = after >= text.Length
                    || (!char.IsLetterOrDigit(text[after]) && text[after] != '_');
                if (leftOk && rightOk) return idx - start;
                idx = after;
            }
            return -1;
        }

        /// <summary>1-based line of <paramref name="index"/> in <paramref name="text"/>.</summary>
        private static int LineAt(string text, int index)
        {
            int line = 1;
            int end = Math.Min(index, text.Length);
            for (int i = 0; i < end; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        /// <summary>0-based column of <paramref name="index"/> in <paramref name="text"/>.</summary>
        private static int ColAt(string text, int index)
        {
            int end = Math.Min(index, text.Length);
            if (end <= 0) return 0;
            int lineStart = text.LastIndexOf('\n', end - 1) + 1;
            return end - lineStart;
        }

        private static string ShortName(string path)
        {
            string p = path.Replace('\\', '/');
            int slash = p.LastIndexOf('/');
            return slash >= 0 ? p.Substring(slash + 1) : p;
        }

        private static string DirOf(string path)
        {
            string p = path.Replace('\\', '/').TrimEnd('/');
            int slash = p.LastIndexOf('/');
            return slash >= 0 ? p.Substring(0, slash) : string.Empty;
        }

        /// <summary><c>./</c>-relative extensionless specifier from importer dir to target file.</summary>
        internal static string RelativeSpecifier(string fromDir, string toFileAbs)
        {
            string to = toFileAbs.Replace('\\', '/');
            if (to.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                to = to.Substring(0, to.Length - ".uitkx".Length);

            var fromSegs = fromDir.Length == 0 ? new List<string>() : new List<string>(fromDir.Split('/'));
            var toSegs = new List<string>(to.Split('/'));

            int common = 0;
            while (common < fromSegs.Count && common < toSegs.Count - 1 &&
                   string.Equals(fromSegs[common], toSegs[common], StringComparison.OrdinalIgnoreCase))
                common++;

            var rel = new List<string>();
            for (int i = common; i < fromSegs.Count; i++) rel.Add("..");
            for (int i = common; i < toSegs.Count; i++) rel.Add(toSegs[i]);

            string result = string.Join("/", rel);
            if (!result.StartsWith("../", StringComparison.Ordinal) && result != "..")
                result = "./" + result;
            return result;
        }
    }
}
