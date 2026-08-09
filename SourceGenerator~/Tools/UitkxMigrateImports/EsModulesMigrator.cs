using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ruitk.Language;
using Ruitk.Language.Formatter;
using Ruitk.Language.Parser;

namespace Ruitk.SourceGenerator.Tools
{
    /// <summary>
    /// The ES-modules migration pass (Plans~/archive/ES_MODULES_EXECUTION_PLAN.md §7.1, U-09): rewrites
    /// legacy wrapper-keyword files to plain declarations. Pipeline per G-10 (each step a no-op
    /// on already-migrated input, so the whole is idempotent):
    /// <list type="number">
    ///   <item><description>tidy — <see cref="UitkxMigrator.TidyUsings"/>;</description></item>
    ///   <item><description>export-normalize + import-insert — the existing
    ///   <see cref="UitkxMigrator.Migrate(IReadOnlyList{MigratorFile})"/> machinery;</description></item>
    ///   <item><description>wrapper rewrite — <c>component N</c> → <c>VirtualNode N(…)</c>;
    ///   <c>hook useN(p) -&gt; (r)</c> → <c>(r) useN(p)</c>; <c>module M { members }</c> hoisted
    ///   to plain member declarations (Roslyn-parsed);</description></item>
    ///   <item><description>import rewrite — <c>import { M }</c> of an exploded module →
    ///   <c>import * as M</c> (call sites <c>M.x</c> keep working verbatim), plus member imports
    ///   for names that used to resolve via companion partial-merging;</description></item>
    ///   <item><description>companion atomicity — sets (<c>X.uitkx</c> + <c>X.*.uitkx</c>) migrate
    ///   whole or not at all;</description></item>
    ///   <item><description>format — <see cref="AstFormatter"/> last.</description></item>
    /// </list>
    /// The zero-diagnostics gate (§7.1 step 7) is the caller's job (SamplesCorpusGateTests +
    /// VERIFY-UNITY in M7). Files whose shapes the plain dialect cannot express (modules with
    /// properties/nested types/attributed members/initializer-less fields — move those members
    /// to ambient C#, ruling D2), files with parse errors, and files with declarations sharing
    /// a source line are SKIPPED whole-set with a reported error. Generic hooks and generic
    /// module methods migrate to generic declaration heads (F9, 0.16.0). Companion-file module
    /// members auto-export regardless of modifiers (the partial-class merge made them all
    /// reachable pre-migration).
    /// </summary>
    public static class EsModulesMigrator
    {
        public static Dictionary<string, string> Migrate(
            IReadOnlyList<MigratorFile> inputFiles, out List<UitkxMigrator.MigrationError> errors)
        {
            errors = new List<UitkxMigrator.MigrationError>();

            // Normalize to LF FIRST, before ANY parse: the parser's Body*Offset values are indices
            // into the text it was handed, and the line-span/slice surgery below reads the same
            // text — a CRLF file parsed raw but sliced normalized shifts every offset (found live:
            // ArgumentOutOfRange on the first CRLF working tree). The final AstFormatter pass
            // outputs LF-only anyway, so LF intake changes nothing downstream.
            var files = new List<MigratorFile>(inputFiles.Count);
            foreach (var inF in inputFiles)
                files.Add(inF with { Text = inF.Text.Replace("\r\n", "\n").Replace("\r", "\n") });

            // ── Step 1+2: tidy + export-normalize/import-insert (existing passes) ──
            // LEGACY files only: the legacy migrator's @namespace stamping (identity freezing)
            // and using-tidy are wrong for already-migrated (file-keyed) files — those pass
            // through byte-untouched, which is also what makes the whole pipeline idempotent.
            // Legacy files whose parse produced ERROR diagnostics are frozen before ANY rewrite:
            // surgically rewriting a half-parsed file leaves mixed-grammar garbage (the parser
            // only recorded the declarations it could read, so the invalid tail survives next to
            // exploded output). They fail loudly and drag their companion set with them.
            var newMode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parseErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Pre-migration folder-keyed namespaces of stamp-less legacy files: after those
            // files migrate to file-keyed namespaces, an `import "@OldFolderNs"` in ANY file
            // points at a namespace that no longer has members (PC-12b). Collected here to
            // warn on survivors after the rewrite.
            var preMigrationNamespaces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                var probeDiags = new List<ParseDiagnostic>();
                var dsProbe = DirectiveParser.Parse(f.Text, f.AbsPath, probeDiags);
                if (!dsProbe.UsesLegacySyntax)
                {
                    newMode.Add(f.AbsPath);
                    continue;
                }
                if (!dsProbe.HasExplicitNamespace)
                {
                    string? oldNs = EffectiveNamespace.Resolve(
                        hasExplicitNamespace: false, dsProbe.Namespace, f.AbsPath, fileKeyed: false);
                    if (!string.IsNullOrEmpty(oldNs))
                        preMigrationNamespaces.Add(oldNs!);
                }
                var errDiags = probeDiags.Where(d => d.Severity == ParseSeverity.Error).ToList();
                if (errDiags.Count > 0)
                    parseErrors[f.AbsPath] = string.Join("; ",
                        errDiags.Select(d => $"{d.Code} (line {d.SourceLine}) {d.Message}"));
            }

            var tidied = new List<MigratorFile>(files.Count);
            foreach (var f in files)
                tidied.Add(newMode.Contains(f.AbsPath) || parseErrors.ContainsKey(f.AbsPath)
                    ? f : f with { Text = UitkxMigrator.TidyUsings(f.Text) });
            var step2 = UitkxMigrator.Migrate(tidied, out var step2Errors, tidyUsings: false, stampNamespace: false);
            errors.AddRange(step2Errors);
            var current = new List<MigratorFile>(tidied.Count);
            foreach (var f in tidied)
                current.Add(!newMode.Contains(f.AbsPath) && !parseErrors.ContainsKey(f.AbsPath)
                    && step2.TryGetValue(f.AbsPath, out var t)
                    ? f with { Text = t } : f);

            // ── Step 3: per-file wrapper rewrite (attempt) ──────────────────────
            // moduleExports: file → exploded module names (for the importer rewrite).
            // memberExports: file → hoisted member names (for companion member-import insertion).
            var rewritten = new Dictionary<string, string>(StringComparer.Ordinal);
            var failed = new Dictionary<string, string>(StringComparer.Ordinal); // path → reason
            var moduleNamesByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var memberNamesByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var warningsByFile = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var f in current)
            {
                var ds = DirectiveParser.Parse(f.Text, f.AbsPath, new List<ParseDiagnostic>());
                if (!ds.UsesLegacySyntax)
                {
                    rewritten[f.AbsPath] = f.Text; // already migrated — pass through (idempotence)
                    continue;
                }
                if (parseErrors.TryGetValue(f.AbsPath, out var parseErr))
                {
                    failed[f.AbsPath] = $"parse errors — file left unmigrated: {parseErr}";
                    continue;
                }
                string? reason = TryRewriteFile(f, ds, out string newText, out var modNames, out var memNames,
                    out var fileWarnings);
                if (reason != null)
                {
                    failed[f.AbsPath] = reason;
                    continue;
                }
                rewritten[f.AbsPath] = newText;
                if (fileWarnings.Count > 0) warningsByFile[f.AbsPath] = fileWarnings;
                // Keyed by NORMALIZED path: ImportResolver.MapSpecifierToPath returns
                // forward-slash paths, while the CLI hands in Windows backslash AbsPaths —
                // an un-normalized key made every cross-file module lookup a silent miss
                // (found live: `import { Theme }` never became `import * as Theme`, 2301).
                if (modNames.Count > 0) moduleNamesByFile[Norm(f.AbsPath)] = modNames;
                if (memNames.Count > 0) memberNamesByFile[Norm(f.AbsPath)] = memNames;
            }

            // ── Step 5 (before 4 by necessity): companion atomicity ─────────────
            // A set = X.uitkx + X.*.uitkx in one folder. If ANY member failed, the whole set
            // reverts to its step-2 text (all-or-none — matrix row 8 is a broken build).
            var setsBySetKey = current
                .GroupBy(f => SetKey(f.AbsPath), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var migratedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in setsBySetKey)
            {
                var members = set.ToList();
                var failures = members.Where(m => failed.ContainsKey(m.AbsPath)).ToList();
                if (failures.Count > 0)
                {
                    foreach (var m in members)
                    {
                        rewritten[m.AbsPath] = m.Text; // revert to step-2 (still-legacy) text
                        moduleNamesByFile.Remove(Norm(m.AbsPath));
                        memberNamesByFile.Remove(Norm(m.AbsPath));
                        warningsByFile.Remove(m.AbsPath);
                    }
                    foreach (var m in failures)
                        errors.Add(new UitkxMigrator.MigrationError(m.AbsPath,
                            $"companion set '{set.Key}' left legacy: {failed[m.AbsPath]}"));
                }
                else
                {
                    foreach (var m in members)
                    {
                        migratedFiles.Add(Norm(m.AbsPath));
                        if (warningsByFile.TryGetValue(m.AbsPath, out var ws))
                            foreach (var w in ws)
                                errors.Add(new UitkxMigrator.MigrationError(m.AbsPath, w));
                    }
                }
            }

            // ── Step 4: importer rewrite against MIGRATED targets ───────────────
            // `import { M }` where the TARGET exploded module M → `import * as M` (call sites
            // `M.x` keep working; a star import is legal even from a legacy or failed-set
            // importer — 2109 only fires when the TARGET is legacy). This pass runs over EVERY
            // file: an importer whose own set stayed legacy would otherwise keep a named import
            // of a name that no longer exists on the migrated target (UITKX2301). Companion
            // member imports are migrated-importers-only.
            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in current)
            {
                bool isMigrated = migratedFiles.Contains(Norm(f.AbsPath));
                bool remainsLegacy = !isMigrated && !newMode.Contains(f.AbsPath);
                string text = RewriteImports(f.AbsPath, rewritten[f.AbsPath], moduleNamesByFile,
                    memberNamesByFile, migratedFiles, isMigrated, remainsLegacy, errors);

                // ── Step 6: format last ─────────────────────────────────────────
                if (!string.Equals(text, f.Text, StringComparison.Ordinal)
                    || !string.Equals(text, files.First(x => x.AbsPath == f.AbsPath).Text, StringComparison.Ordinal))
                {
                    try { text = new AstFormatter(FormatterOptions.Default).Format(text, f.AbsPath); }
                    catch { /* never fail the migration on a formatter hiccup */ }
                }

                string original = files.First(x => x.AbsPath == f.AbsPath).Text;
                if (!string.Equals(text, original, StringComparison.Ordinal))
                    output[f.AbsPath] = text;

                // PC-12b: a surviving `import "@X"` / `@using X` aimed at a PRE-migration
                // folder namespace is now dead - the members moved to file-keyed namespaces.
                // Warn (never silently rewrite): the editor's UITKX2316/2305 quick-fixes name
                // the exact replacement imports.
                if (isMigrated && preMigrationNamespaces.Count > 0)
                    foreach (Match nsImp in Regex.Matches(text,
                        "^(?:import\\s+\"@([\\w.]+)\"|@using\\s+([\\w.]+))\\s*$", RegexOptions.Multiline))
                    {
                        string nsRef = nsImp.Groups[1].Success ? nsImp.Groups[1].Value : nsImp.Groups[2].Value;
                        if (preMigrationNamespaces.Contains(nsRef))
                            errors.Add(new UitkxMigrator.MigrationError(f.AbsPath,
                                $"import \"@{nsRef}\" points at a pre-migration folder namespace whose members "
                                + "moved to file-keyed namespaces - replace it with explicit imports "
                                + "(the editor's UITKX2316/2305 quick-fixes list the exact names)"));
                    }
            }
            return output;
        }

        // ── Per-file wrapper rewrite ─────────────────────────────────────────────

        /// <summary>Null on success (out newText valid); else the skip reason.</summary>
        private static string? TryRewriteFile(
            MigratorFile f, DirectiveSet ds, out string newText,
            out List<string> explodedModuleNames, out List<string> exportedMemberNames,
            out List<string> warnings)
        {
            newText = f.Text;
            explodedModuleNames = new List<string>();
            exportedMemberNames = new List<string>();
            warnings = new List<string>();

            string src = f.Text.Replace("\r\n", "\n");
            var lines = src.Split('\n').ToList();

            // Collect line-span replacements (1-based inclusive), applied bottom-up.
            var replacements = new List<(int StartLine, int EndLine, List<string> NewLines)>();

            // Hooks — regenerate from the parsed record (multi-line headers are span-replaced).
            if (!ds.HookDeclarations.IsDefaultOrEmpty)
                foreach (var h in ds.HookDeclarations)
                {
                    // BodyEndOffset is the exact index of the closing `}` (parser contract), so
                    // its line IS the last line of the declaration — never scan the text for a
                    // `}`-first line: when the brace shares a line with the last statement, a
                    // scan latches onto the NEXT declaration's brace and deletes it wholesale.
                    int closeLine = LineAt(src, h.BodyEndOffset);
                    string ret = string.IsNullOrEmpty(h.ReturnType) ? "void" : h.ReturnType!;
                    // F9: generic hooks migrate to generic declaration heads verbatim
                    // (`export (T v, Action<T> set) useSel<T>(…)`).
                    string typeParams = h.GenericParams ?? string.Empty;
                    string paramList = string.Join(", ", h.Params.IsDefaultOrEmpty
                        ? Enumerable.Empty<string>()
                        : h.Params.Select(p => p.DefaultValue != null
                            ? $"{p.Type} {p.Name} = {p.DefaultValue}"
                            : $"{p.Type} {p.Name}"));
                    var body = SliceLines(src, h.BodyStartOffset, h.BodyEndOffset);
                    var repl = new List<string>
                    {
                        $"{(h.IsExported ? "export " : "")}{ret} {h.Name}{typeParams}({paramList}) {{"
                    };
                    repl.AddRange(body);
                    repl.Add("}");
                    replacements.Add((h.DeclarationLine, closeLine, repl));
                }

            // Modules — Roslyn-parse the body, hoist members.
            if (!ds.ModuleDeclarations.IsDefaultOrEmpty)
                foreach (var m in ds.ModuleDeclarations)
                {
                    // Companion files (dotted stem, e.g. X.style.uitkx): every hoisted member
                    // auto-exports. Pre-migration, ALL companion members - public or not -
                    // were reachable from the base file through the partial-class merge; a
                    // public-only rule silently strands the common modifier-less style
                    // companion shape at CS0103 (T15, the highest-frequency field break).
                    bool exportAll = Path.GetFileNameWithoutExtension(f.AbsPath).Contains('.');
                    string? reason = ExplodeModule(m, exportAll, out var hoisted, out var memberNames, warnings);
                    if (reason != null)
                        return $"module '{m.Name}': {reason}";
                    int closeLine = LineAt(src, m.BodyEndOffset);
                    replacements.Add((m.DeclarationLine, closeLine, hoisted));
                    explodedModuleNames.Add(m.Name);
                    exportedMemberNames.AddRange(memberNames);
                }

            // Components — keyword surgery on the declaration line (params may wrap; only the
            // keyword token and, for the parameterless form, the missing `()` change).
            if (!ds.ComponentDeclarations.IsDefaultOrEmpty)
                foreach (var c in ds.ComponentDeclarations)
                {
                    int li = c.DeclarationLine - 1;
                    if (li < 0 || li >= lines.Count)
                        return $"component '{c.Name}': declaration line out of range";
                    string line = lines[li];
                    var mkw = Regex.Match(line, @"^(\s*)(export\s+)?component\b");
                    if (!mkw.Success)
                        return $"component '{c.Name}': wrapper keyword not found on its declaration line";
                    string rewrittenLine = Regex.Replace(line, @"^(\s*)(export\s+)?component\b", "$1$2VirtualNode");
                    if (c.FunctionParams.IsDefaultOrEmpty)
                    {
                        // Parameterless keeps empty parens: `component N {` → `VirtualNode N() {`.
                        rewrittenLine = Regex.Replace(
                            rewrittenLine,
                            $@"\b{Regex.Escape(c.Name)}\b(?!\s*\()",
                            c.Name + "()");
                    }
                    replacements.Add((c.DeclarationLine, c.DeclarationLine, new List<string> { rewrittenLine }));
                }

            // Overlap gate: two replacements sharing a line (`} export hook useB(...) -> (int) {`)
            // cannot both be applied — half-rewriting would corrupt the file silently. Fail the
            // whole file (and, via set atomicity, its companions) loudly instead.
            var ordered = replacements.OrderBy(r => r.StartLine).ToList();
            for (int ri = 1; ri < ordered.Count; ri++)
                if (ordered[ri].StartLine <= ordered[ri - 1].EndLine)
                    return $"two declarations share source line {ordered[ri].StartLine} — "
                        + "put each declaration on its own line and re-run";

            // Apply bottom-up so earlier line numbers stay valid.
            for (int ri = ordered.Count - 1; ri >= 0; ri--)
            {
                var (start, end, newLines) = ordered[ri];
                lines.RemoveRange(start - 1, end - start + 1);
                lines.InsertRange(start - 1, newLines);
            }

            newText = string.Join("\n", lines);
            if (!newText.EndsWith("\n", StringComparison.Ordinal))
                newText += "\n";
            return null;
        }

        /// <summary>
        /// Hoists a legacy module body's members to plain declarations. Null on success;
        /// else the reason the shape cannot be expressed in the plain dialect. Comment/doc
        /// trivia is preserved verbatim. Export rule: explicitly <c>public</c> members export;
        /// with <paramref name="exportAll"/> (companion files) EVERY member exports, because
        /// the partial-class merge made all companion members reachable regardless of
        /// modifiers. <c>const</c> migrates with a per-member warning.
        /// <paramref name="exportedNames"/> carries the exported member names only (the
        /// companion member-import pass must never import a file-private name).
        /// </summary>
        private static string? ExplodeModule(
            ModuleDeclaration m, bool exportAll, out List<string> hoisted, out List<string> exportedNames,
            List<string> warnings)
        {
            hoisted = new List<string>();
            exportedNames = new List<string>();

            var tree = CSharpSyntaxTree.ParseText("class __W {\n" + m.Body + "\n}");
            var errorsInBody = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errorsInBody.Count > 0)
                return $"body does not parse as C# members ({errorsInBody[0].GetMessage()})";
            var cls = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (cls == null)
                return "no members found";

            foreach (var member in cls.Members)
            {
                foreach (var commentLine in TriviaCommentLines(member.GetLeadingTrivia()))
                    hoisted.Add(commentLine);
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                    {
                        string firstName = field.Declaration.Variables.Count > 0
                            ? field.Declaration.Variables[0].Identifier.Text : "<field>";
                        if (field.AttributeLists.Count > 0)
                            return $"member '{firstName}' has attributes — the plain dialect has no attribute form";
                        bool isPublic = field.Modifiers.Any(SyntaxKind.PublicKeyword);
                        bool isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
                        bool fieldExports = isPublic || exportAll;
                        string type = field.Declaration.Type.ToString();
                        foreach (var v in field.Declaration.Variables)
                        {
                            if (v.Initializer == null)
                                return $"field '{v.Identifier.Text}' has no initializer — plain values require '= …'";
                            string prefix = fieldExports ? "export " : "";
                            hoisted.Add($"{prefix}{type} {v.Identifier.Text} = {v.Initializer.Value};");
                            if (isConst)
                                warnings.Add($"module '{m.Name}': const member '{v.Identifier.Text}' migrated to a "
                                    + "plain value — const-ness is lost (const-required contexts will fail at C# compile)");
                            if (fieldExports)
                            {
                                exportedNames.Add(v.Identifier.Text);
                                if (!isPublic)
                                    warnings.Add($"module '{m.Name}': companion member '{v.Identifier.Text}' "
                                        + "auto-exported (it was reachable through the partial-class merge)");
                            }
                        }
                        break;
                    }
                    case MethodDeclarationSyntax method:
                    {
                        if (method.AttributeLists.Count > 0)
                            return $"member '{method.Identifier.Text}' has attributes — the plain dialect has no attribute form";
                        bool isPublic = method.Modifiers.Any(SyntaxKind.PublicKeyword);
                        bool methodExports = isPublic || exportAll;
                        string prefix = methodExports ? "export " : "";
                        string ret = method.ReturnType.ToString();
                        // F9: generic module methods migrate to generic declaration heads.
                        string typeParams = method.TypeParameterList?.ToString() ?? string.Empty;
                        string plist = method.ParameterList.Parameters.ToFullString().Trim();
                        if (method.ExpressionBody != null)
                        {
                            hoisted.Add($"{prefix}{ret} {method.Identifier.Text}{typeParams}({plist}) => {method.ExpressionBody.Expression};");
                        }
                        else if (method.Body != null)
                        {
                            hoisted.Add($"{prefix}{ret} {method.Identifier.Text}{typeParams}({plist}) {{");
                            foreach (var bodyLine in DedentBlock(method.Body))
                                hoisted.Add(bodyLine);
                            hoisted.Add("}");
                        }
                        else
                        {
                            return $"method '{method.Identifier.Text}' has no body";
                        }
                        if (methodExports)
                        {
                            exportedNames.Add(method.Identifier.Text);
                            if (!isPublic)
                                warnings.Add($"module '{m.Name}': companion member '{method.Identifier.Text}' "
                                    + "auto-exported (it was reachable through the partial-class merge)");
                        }
                        break;
                    }
                    default:
                        return $"member kind '{member.Kind()}' cannot be hoisted to a plain declaration "
                            + "(properties, nested types, events, and constructors have no plain-dialect form)";
                }
                hoisted.Add(string.Empty);
            }
            foreach (var commentLine in TriviaCommentLines(cls.CloseBraceToken.LeadingTrivia))
                hoisted.Add(commentLine);
            if (hoisted.Count > 0 && hoisted[^1].Length == 0)
                hoisted.RemoveAt(hoisted.Count - 1);
            return null;
        }

        /// <summary>
        /// Comment and doc-comment trivia as emit-ready lines: floating/banner comments and
        /// member docs survive the module explosion verbatim (the plain grammar and the
        /// formatter both tolerate comment lines between declarations). Continuation lines
        /// are dedented by the comment's own leading indentation so relative alignment
        /// (<c>*</c>-columns, doc continuations) is kept.
        /// </summary>
        private static IEnumerable<string> TriviaCommentLines(SyntaxTriviaList trivia)
        {
            string indent = string.Empty;
            foreach (var t in trivia)
            {
                if (t.IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    indent = t.ToString();
                    continue;
                }
                if (t.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    indent = string.Empty;
                    continue;
                }
                if (!t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    && !t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                    && !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                    && !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    continue;
                string[] commentLines = t.ToFullString().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                yield return commentLines[0].TrimEnd();
                for (int i = 1; i < commentLines.Length; i++)
                {
                    string line = commentLines[i];
                    if (indent.Length > 0 && line.StartsWith(indent, StringComparison.Ordinal))
                        line = line.Substring(indent.Length);
                    yield return line.TrimEnd();
                }
            }
        }

        private static IEnumerable<string> DedentBlock(BlockSyntax body)
        {
            var inner = body.Statements.ToFullString().Replace("\r\n", "\n").TrimEnd('\n');
            foreach (var line in inner.Split('\n'))
                yield return line.TrimEnd();
        }

        // ── Importer rewrite (step 4) ────────────────────────────────────────────

        private static string RewriteImports(
            string absPath,
            string text,
            Dictionary<string, List<string>> moduleNamesByFile,
            Dictionary<string, List<string>> memberNamesByFile,
            HashSet<string> migratedFiles,
            bool importerMigrated,
            bool importerRemainsLegacy,
            List<UitkxMigrator.MigrationError> notes)
        {
            var ds = DirectiveParser.Parse(text, absPath, new List<ParseDiagnostic>());
            if (ds.Imports.IsDefaultOrEmpty && memberNamesByFile.Count == 0)
                return text;

            string importerDir = Norm(System.IO.Path.GetDirectoryName(absPath) ?? string.Empty);
            // `~/` specifiers resolve against the UI source ROOT, not the importer's folder —
            // the same root the SG pipeline and HMR use (§7 parity). Passing importerDir here
            // made every `~/`-rooted import of an exploded module miss the rewrite silently.
            string rootDir = EffectiveNamespace.UiSourceRootDir(absPath) ?? importerDir;
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            bool changed = false;

            // `import { M, x } from spec` → star import when M is an exploded module of the
            // (migrated) target; the other names keep a named import line. Applies to EVERY
            // importer — a failed-set or already-new-mode importer keeping `import { M }` of an
            // exploded target would break on the next build (UITKX2301).
            if (!ds.Imports.IsDefaultOrEmpty)
                foreach (var imp in ds.Imports.OrderByDescending(i => i.Line))
                {
                    if (imp.IsStar || imp.IsDefault)
                        continue;
                    string? target = ImportResolver.MapSpecifierToPath(importerDir, imp.Specifier, rootDir, out _);
                    if (target == null || !migratedFiles.Contains(Norm(target))
                        || !moduleNamesByFile.TryGetValue(Norm(target), out var modNames))
                        continue;

                    var starNames = imp.Names.Where(n => modNames.Contains(n)).ToList();
                    if (starNames.Count == 0)
                        continue;
                    var namedNames = imp.Names.Where(n => !modNames.Contains(n)).ToList();

                    var repl = new List<string>();
                    foreach (var sn in starNames)
                        repl.Add($"import * as {sn} from \"{imp.Specifier}\"");
                    if (namedNames.Count > 0)
                        repl.Add($"import {{ {string.Join(", ", namedNames)} }} from \"{imp.Specifier}\"");
                    lines.RemoveAt(imp.Line - 1);
                    lines.InsertRange(imp.Line - 1, repl);
                    changed = true;
                    if (importerRemainsLegacy)
                        notes.Add(new UitkxMigrator.MigrationError(absPath,
                            $"note: still-legacy file's import of {string.Join(", ", starNames)} from "
                            + $"\"{imp.Specifier}\" rewritten to `import * as` — the target's module "
                            + "form migrated to plain declarations"));
                }

            // Companion member imports: bare references that used to resolve via companion
            // partial-merging. Within a set EVERY other member is a potential exporter —
            // base←companion, companion←base, and companion←sibling all merged legacy-side.
            // Only for migrated importers (member imports into a still-legacy file would be
            // UITKX2301) and only for EXPORTED names (importing a file-private name is 2302).
            if (importerMigrated)
            {
                string setKey = SetKey(absPath);
                var selfNames = new HashSet<string>(StringComparer.Ordinal);
                var dsSelf = DirectiveParser.Parse(string.Join("\n", lines), absPath, new List<ParseDiagnostic>());
                foreach (var c in dsSelf.ComponentDeclarations) selfNames.Add(c.Name);
                foreach (var md in dsSelf.MemberDeclarations) selfNames.Add(md.Name);
                var neededBySpec = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                string scrub = ScrubForScan(string.Join("\n", lines));
                foreach (var kv in memberNamesByFile)
                {
                    if (!migratedFiles.Contains(kv.Key)) continue;
                    if (string.Equals(kv.Key, Norm(absPath), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(SetKey(kv.Key), setKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var already = new HashSet<string>(StringComparer.Ordinal);
                    var dsNow = DirectiveParser.Parse(string.Join("\n", lines), absPath, new List<ParseDiagnostic>());
                    if (!dsNow.Imports.IsDefaultOrEmpty)
                        foreach (var imp in dsNow.Imports)
                            foreach (var n in imp.Names)
                                already.Add(n);

                    foreach (var name in kv.Value)
                    {
                        if (already.Contains(name) || selfNames.Contains(name)) continue;
                        if (!Regex.IsMatch(scrub, $@"\b{Regex.Escape(name)}\b")) continue;
                        string spec = UitkxMigrator.RelativeSpecifier(absPath, kv.Key);
                        if (!neededBySpec.TryGetValue(spec, out var set))
                            neededBySpec[spec] = set = new SortedSet<string>(StringComparer.Ordinal);
                        set.Add(name);
                    }
                }
                if (neededBySpec.Count > 0)
                {
                    int insertAt = 0;
                    var dsNow = DirectiveParser.Parse(string.Join("\n", lines), absPath, new List<ParseDiagnostic>());
                    if (!dsNow.Imports.IsDefaultOrEmpty)
                        insertAt = dsNow.Imports.Max(i => i.Line); // after the last import line (1-based → insert index)
                    var newLines = neededBySpec
                        .Select(kv2 => $"import {{ {string.Join(", ", kv2.Value)} }} from \"{kv2.Key}\"")
                        .ToList();
                    lines.InsertRange(insertAt, newLines);
                    changed = true;
                }
            }

            if (!changed)
                return text;
            string result = string.Join("\n", lines);
            if (!result.EndsWith("\n", StringComparison.Ordinal))
                result += "\n";
            return result;
        }

        // ── Utilities ────────────────────────────────────────────────────────────

        private static string SetKey(string absPath)
        {
            string dir = Norm(System.IO.Path.GetDirectoryName(absPath) ?? string.Empty);
            string stem = StemOf(absPath);
            int dot = stem.IndexOf('.');
            if (dot > 0) stem = stem.Substring(0, dot);
            return dir + "/" + stem;
        }

        private static string StemOf(string absPath)
            => System.IO.Path.GetFileNameWithoutExtension(absPath);

        private static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');

        private static int LineAt(string src, int offset)
        {
            int line = 1;
            int end = Math.Min(offset, src.Length);
            for (int i = 0; i < end; i++)
                if (src[i] == '\n') line++;
            return line;
        }

        private static List<string> SliceLines(string src, int startOffset, int endOffset)
        {
            string slice = src.Substring(startOffset, Math.Max(0, endOffset - startOffset))
                .TrimEnd('\n', ' ', '\t');
            return slice.Split('\n').Select(l => l.TrimEnd()).ToList();
        }

        private static string ScrubForScan(string text)
        {
            var sb = new StringBuilder(text);
            int i = 0;
            while (i < text.Length)
            {
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
    }
}
