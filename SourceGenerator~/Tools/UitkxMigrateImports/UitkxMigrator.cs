using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ruitk.Language;
using Ruitk.Language.Formatter;
using Ruitk.Language.Parser;

namespace Ruitk.SourceGenerator.Tools
{
    /// <summary>
    /// One <c>.uitkx</c> file handed to the migrator: its absolute path (for relative-specifier
    /// computation), the owning-asmdef key it is grouped under (imports are asmdef-scoped — the CLI
    /// derives this from the on-disk asmdef walk; tests pass it explicitly), and its source text.
    /// </summary>
    public sealed record MigratorFile(string AbsPath, string AsmdefKey, string Text);

    /// <summary>
    /// The leg-3 import/export migration codemod core (plan §11), pure and filesystem-free so it is
    /// fully unit-testable. <see cref="Migrate"/> takes the whole file set, builds per-asmdef export
    /// tables (pass 1), then rewrites each file (pass 2) and returns only the files whose text
    /// changed. Idempotent: feeding the output back in yields no further changes.
    /// </summary>
    public static class UitkxMigrator
    {
        private enum DeclKind { Component, Hook, Module }

        private sealed record TopDecl(string Name, DeclKind Kind, int Line, bool IsExported);

        private sealed record ParsedFile(
            MigratorFile File,
            DirectiveSet Directives,
            List<TopDecl> Decls);

        /// <summary>Exported name → the owning file path(s) within one asmdef (one name may collide across files).</summary>
        private sealed class ExportTable
        {
            public readonly Dictionary<string, List<string>> Components = new(StringComparer.Ordinal);
            public readonly Dictionary<string, List<string>> Hooks = new(StringComparer.Ordinal);
            public readonly Dictionary<string, List<string>> Modules = new(StringComparer.Ordinal);

            public static void Put(Dictionary<string, List<string>> map, string name, string path)
            {
                if (!map.TryGetValue(name, out var list))
                    map[name] = list = new List<string>();
                if (!list.Contains(path))
                    list.Add(path);
            }
        }

        /// <summary>A reference discovered in pass 2: an imported name and the file that exports it.</summary>
        private sealed record Ref(string Name, string TargetPath);

        // Component tag in markup:  <StatusChip ...  /  <StatusChip>
        private static readonly Regex s_tagRe = new(@"<\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        // Hook call in setup code:  useCounter(  /  useCounter <T> (   (angle handled loosely)
        private static readonly Regex s_callRe = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*[(<]", RegexOptions.Compiled);
        // Module member access:  CounterStyles.Gap
        private static readonly Regex s_memberRe = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\.", RegexOptions.Compiled);

        /// <summary>The migration errors surfaced to the caller (ambiguous references needing manual fix).</summary>
        public sealed record MigrationError(string FilePath, string Message);

        public static Dictionary<string, string> Migrate(IReadOnlyList<MigratorFile> files)
            => Migrate(files, out _);

        public static Dictionary<string, string> Migrate(
            IReadOnlyList<MigratorFile> files, out List<MigrationError> errors, bool tidyUsings = false,
            bool stampNamespace = true)
        {
            errors = new List<MigrationError>();

            // ── Parse every file once ────────────────────────────────────────────
            var parsed = new List<ParsedFile>(files.Count);
            foreach (var f in files)
            {
                var diags = new List<ParseDiagnostic>();
                var ds = DirectiveParser.ParseLegacyForMigration(f.Text, f.AbsPath, diags);
                parsed.Add(new ParsedFile(f, ds, CollectTopDecls(ds)));
            }

            // ── Pass 1: per-asmdef export tables (export-everything default) ──────
            var tables = new Dictionary<string, ExportTable>(StringComparer.Ordinal);
            foreach (var pf in parsed)
            {
                if (!tables.TryGetValue(pf.File.AsmdefKey, out var table))
                    tables[pf.File.AsmdefKey] = table = new ExportTable();
                foreach (var d in pf.Decls)
                {
                    switch (d.Kind)
                    {
                        case DeclKind.Component: ExportTable.Put(table.Components, d.Name, pf.File.AbsPath); break;
                        case DeclKind.Hook: ExportTable.Put(table.Hooks, d.Name, pf.File.AbsPath); break;
                        case DeclKind.Module: ExportTable.Put(table.Modules, d.Name, pf.File.AbsPath); break;
                    }
                }
            }

            // ── Pass 2: rewrite each file ────────────────────────────────────────
            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pf in parsed)
            {
                var table = tables[pf.File.AsmdefKey];
                var refs = ScanReferences(pf, table, errors);
                string newText = Rewrite(pf, refs, stampNamespace);
                if (tidyUsings)
                    newText = TidyUsings(newText);
                if (!string.Equals(newText, pf.File.Text, StringComparison.Ordinal))
                    output[pf.File.AbsPath] = newText;
            }
            return output;
        }

        /// <summary>
        /// <c>--tidy</c> using-canonicalization (namespace-import unification plan): converts every
        /// <c>@using X</c> line to the unified <c>import "@X"</c> spelling (preserving <c>static</c> /
        /// alias payloads and indentation), and DROPS any using — either spelling — whose namespace is
        /// already auto-injected by the emitters (<see cref="AutoInjectedUsings"/>), e.g.
        /// <c>@using UnityEngine</c> / <c>@using System</c>. Idempotent: a second pass over tidied
        /// output is a no-op. Pure + unit-testable.
        /// </summary>
        public static string TidyUsings(string text)
        {
            var ds = DirectiveParser.ParseLegacyForMigration(text, "tidy.uitkx", new List<ParseDiagnostic>());
            if (ds.UsingDirectives.IsDefaultOrEmpty)
                return text;

            var byLine = new Dictionary<int, UsingDirective>();
            foreach (var u in ds.UsingDirectives)
                byLine[u.Line] = u;

            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            var outLines = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (byLine.TryGetValue(i + 1, out var u))
                {
                    if (AutoInjectedUsings.IsRedundant(u.Payload))
                        continue; // drop a redundant baseline using entirely
                    string raw = lines[i];
                    string indent = raw.Substring(0, raw.Length - raw.TrimStart().Length);
                    outLines.Add($"{indent}import \"@{u.Payload}\"");
                }
                else
                {
                    outLines.Add(lines[i]);
                }
            }
            return string.Join(nl, outLines);
        }

        // ── Pass 1 helpers ───────────────────────────────────────────────────────

        private static List<TopDecl> CollectTopDecls(DirectiveSet ds)
        {
            var list = new List<TopDecl>();
            if (!ds.ComponentDeclarations.IsDefaultOrEmpty)
            {
                foreach (var c in ds.ComponentDeclarations)
                    list.Add(new TopDecl(c.Name, DeclKind.Component, c.DeclarationLine, c.IsExported));
            }
            else if (!string.IsNullOrEmpty(ds.ComponentName))
            {
                // Malformed/first-only fallback: the singular field still names one component.
                list.Add(new TopDecl(ds.ComponentName!, DeclKind.Component, ds.ComponentDeclarationLine, false));
            }
            if (!ds.HookDeclarations.IsDefaultOrEmpty)
                foreach (var h in ds.HookDeclarations)
                    list.Add(new TopDecl(h.Name, DeclKind.Hook, h.DeclarationLine, h.IsExported));
            if (!ds.ModuleDeclarations.IsDefaultOrEmpty)
                foreach (var m in ds.ModuleDeclarations)
                    list.Add(new TopDecl(m.Name, DeclKind.Module, m.DeclarationLine, m.IsExported));
            return list;
        }

        // ── Pass 2 helpers ───────────────────────────────────────────────────────

        private static List<Ref> ScanReferences(ParsedFile pf, ExportTable table, List<MigrationError> errors)
        {
            string code = ScrubNonCode(pf.File.Text);
            var selfNames = new HashSet<string>(pf.Decls.Select(d => d.Name), StringComparer.Ordinal);
            // Deduplicate by (name) — one import line per referenced name.
            var found = new Dictionary<string, string>(StringComparer.Ordinal); // name → targetPath
            // Names already resolved-or-warned, keyed PER KIND. A shared (cross-kind) set is a
            // bug: `List<Dialogs.Item>` matches the tag regex first, so "Dialogs" would be
            // Considered against the COMPONENTS table (a miss), poisoning the name for the
            // later MODULE member-access scan and silently dropping the import.
            var attempted = new HashSet<string>(StringComparer.Ordinal); // "kind\0name"

            void Consider(Dictionary<string, List<string>> map, string name, string kind)
            {
                if (selfNames.Contains(name) || found.ContainsKey(name)) return;
                if (!attempted.Add(kind + "\0" + name)) return; // don't re-warn the same ambiguous name per occurrence
                if (!map.TryGetValue(name, out var candidates)) return;

                var external = new List<string>();
                foreach (var c in candidates)
                    if (!string.Equals(c, pf.File.AbsPath, StringComparison.Ordinal))
                        external.Add(c);
                if (external.Count == 0) return;

                string? target = Disambiguate(pf.File.AbsPath, external, name, errors);
                if (target != null)
                    found[name] = target;
            }

            foreach (Match m in s_tagRe.Matches(code)) Consider(table.Components, m.Groups[1].Value, "c");
            foreach (Match m in s_callRe.Matches(code)) Consider(table.Hooks, m.Groups[1].Value, "h");
            foreach (Match m in s_memberRe.Matches(code)) Consider(table.Modules, m.Groups[1].Value, "m");

            return found.Select(kv => new Ref(kv.Key, kv.Value)).ToList();
        }

        /// <summary>
        /// Pick which exporter a reference resolves to when a name collides across files in one asmdef
        /// (plan §11 — disambiguate before erroring). Prefers the candidate whose directory shares the
        /// longest path prefix with the importer (same feature/game folder wins over a sibling folder);
        /// a genuine tie (equal proximity to two files) is a hard ambiguity → warning + skip.
        /// </summary>
        private static string? Disambiguate(
            string importerPath, List<string> candidates, string name, List<MigrationError> errors)
        {
            if (candidates.Count == 1)
                return candidates[0];

            string[] importerSegs = DirOf(Norm(importerPath)).Split('/');
            int bestScore = -1;
            string? best = null;
            bool tie = false;
            foreach (var c in candidates)
            {
                string[] cSegs = DirOf(Norm(c)).Split('/');
                int score = 0;
                while (score < importerSegs.Length && score < cSegs.Length &&
                       string.Equals(importerSegs[score], cSegs[score], StringComparison.OrdinalIgnoreCase))
                    score++;
                if (score > bestScore) { bestScore = score; best = c; tie = false; }
                else if (score == bestScore) { tie = true; }
            }

            if (tie || best == null)
            {
                errors.Add(new MigrationError(importerPath,
                    $"ambiguous reference '{name}' — declared in equally-near files in this asmdef; add the import manually"));
                return null;
            }
            return best;
        }

        // `allowNamespaceStamp` (ES-modules campaign, M7): the imports-leg identity-freezing stamp
        // is WRONG for the ES-modules pipeline — stamping (with the raw parsed fallback, no less)
        // would pin every migrated file to its pre-migration namespace and defeat G-01's
        // file-keyed derivation. Legacy callers keep the stamp (pinned by CodemodTests).
        private static string Rewrite(ParsedFile pf, List<Ref> refs, bool allowNamespaceStamp = true)
        {
            var ds = pf.Directives;
            string text = pf.File.Text;
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = SplitKeepEndings(text);

            // Lines occupied by existing import declarations (removed + re-emitted canonically).
            var importLineNos = new HashSet<int>();
            if (!ds.Imports.IsDefaultOrEmpty)
                foreach (var imp in ds.Imports)
                    importLineNos.Add(imp.Line);

            // 1) export-prepend: line → keyword to prefix, for non-exported decls.
            var exportLineNos = new HashSet<int>();
            foreach (var d in pf.Decls)
                if (!d.IsExported && d.Line >= 1)
                    exportLineNos.Add(d.Line);

            // 2) desired canonical import block: existing imports ∪ new refs, keyed by specifier.
            var bySpec = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            if (!ds.Imports.IsDefaultOrEmpty)
                foreach (var imp in ds.Imports)
                {
                    if (!bySpec.TryGetValue(imp.Specifier, out var names))
                        bySpec[imp.Specifier] = names = new SortedSet<string>(StringComparer.Ordinal);
                    foreach (var n in imp.Names) names.Add(n);
                }
            foreach (var r in refs)
            {
                string spec = RelativeSpecifier(pf.File.AbsPath, r.TargetPath);
                if (!bySpec.TryGetValue(spec, out var names))
                    bySpec[spec] = names = new SortedSet<string>(StringComparer.Ordinal);
                names.Add(r.Name);
            }

            var importBlock = new StringBuilder();
            foreach (var kv in bySpec)
                importBlock.Append("import { ").Append(string.Join(", ", kv.Value))
                           .Append(" } from \"").Append(kv.Key).Append('"').Append(nl);

            // 3) preamble boundary = first non-trivia line (skip leading blank/comment lines).
            int preambleStart = FirstNonTriviaLine(lines);

            bool stampNamespace = allowNamespaceStamp
                && !ds.HasExplicitNamespace && !string.IsNullOrEmpty(ds.Namespace);

            var sb = new StringBuilder(text.Length + importBlock.Length + 64);
            for (int i = 0; i < preambleStart; i++)
                sb.Append(lines[i]);

            sb.Append(importBlock);
            if (stampNamespace)
                sb.Append("@namespace ").Append(ds.Namespace).Append(nl);

            for (int i = preambleStart; i < lines.Count; i++)
            {
                int lineNo = i + 1; // 1-based
                if (importLineNos.Contains(lineNo))
                    continue; // dropped — re-emitted canonically above
                string line = lines[i];
                if (exportLineNos.Contains(lineNo))
                    line = PrependExport(line);
                sb.Append(line);
            }

            string result = sb.ToString();
            if (string.Equals(result, text, StringComparison.Ordinal))
                return result; // no semantic change → leave the file (and its formatting) untouched

            // Canonicalize on change: adding `export`/imports can push a header past PrintWidth so
            // the params/return wrap, meaning the raw insertion is not formatter-stable. A final
            // format pass guarantees format(output) == output (plan §11/§16 acceptance). Idempotent:
            // re-running makes no semantic change, so this pass never fires twice on a migrated file.
            try
            {
                return new AstFormatter(FormatterOptions.Default).Format(result, pf.File.AbsPath);
            }
            catch
            {
                return result; // never fail the migration on a formatter hiccup
            }
        }

        /// <summary>Insert <c>export </c> before the first component/hook/module keyword on the line.</summary>
        private static string PrependExport(string line)
        {
            var m = Regex.Match(line, @"^(\s*)(component|hook|module)\b");
            if (!m.Success)
                return line;
            int kw = m.Groups[2].Index;
            return line.Substring(0, kw) + "export " + line.Substring(kw);
        }

        /// <summary>First line index that is not blank and not a comment (line or block). Import lines count as non-trivia.</summary>
        private static int FirstNonTriviaLine(List<string> lines)
        {
            bool inBlock = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (inBlock)
                {
                    int end = t.IndexOf("*/", StringComparison.Ordinal);
                    if (end < 0) continue;
                    t = t.Substring(end + 2).Trim();
                    inBlock = false;
                    if (t.Length == 0) continue;
                }
                if (t.Length == 0) continue;
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (t.StartsWith("/*", StringComparison.Ordinal))
                {
                    if (t.IndexOf("*/", StringComparison.Ordinal) < 0) { inBlock = true; }
                    continue;
                }
                return i;
            }
            return lines.Count;
        }

        /// <summary>Blank out C# string/char literals and comments (offset-preserving) so the reference scan ignores them.</summary>
        private static string ScrubNonCode(string text)
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

        /// <summary>Split into lines, KEEPING each line's terminator so the text round-trips exactly.</summary>
        private static List<string> SplitKeepEndings(string text)
        {
            var list = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    list.Add(text.Substring(start, i - start + 1));
                    start = i + 1;
                }
            }
            if (start < text.Length)
                list.Add(text.Substring(start));
            return list;
        }

        /// <summary>
        /// <c>./</c>-relative, extensionless specifier from the importer file to the target file
        /// (plan §11 — never <c>~/</c> in codemod output). Forward slashes; same-dir gets a
        /// leading <c>./</c>, ancestor paths keep their <c>../</c> prefix.
        /// </summary>
        public static string RelativeSpecifier(string fromFileAbs, string toFileAbs)
        {
            string fromDir = DirOf(Norm(fromFileAbs));
            string to = Norm(toFileAbs);
            if (to.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                to = to.Substring(0, to.Length - ".uitkx".Length);

            var fromSegs = fromDir.Length == 0 ? new List<string>() : fromDir.Split('/').ToList();
            var toSegs = to.Split('/').ToList();

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

        private static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');

        private static string DirOf(string normalizedPath)
        {
            int slash = normalizedPath.LastIndexOf('/');
            return slash >= 0 ? normalizedPath.Substring(0, slash) : string.Empty;
        }
    }
}
