using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ruitk.Language;
using Ruitk.Language.Parser;

namespace Ruitk.SourceGenerator.Tools
{
    /// <summary>
    /// CLI wrapper around <see cref="UitkxMigrator"/>/<see cref="EsModulesMigrator"/> — the only
    /// layer that touches the filesystem.
    ///
    /// <code>
    ///   dotnet run --project SourceGenerator~/Tools/UitkxMigrateImports -- &lt;dir&gt; [flags]
    /// </code>
    ///
    /// Walks <c>&lt;dir&gt;</c> for <c>.uitkx</c> files (skipping <c>~</c>-suffixed tooling folders),
    /// groups them by owning asmdef (nearest <c>*.asmdef</c> "name"), runs the migration, and writes
    /// the changed files back preserving each file's BOM and dominant line endings. Unknown flags
    /// are a hard error (a typo like <c>--es-module</c> must never silently run a different pass).
    ///
    /// Exit codes: 0 ok · 1 <c>--check</c> found pending changes · 2 usage/config error ·
    /// 3 one or more files could not be read/written.
    /// </summary>
    public static class Program
    {
        private static readonly string[] KnownFlags =
            { "--check", "--tidy", "--es-modules", "--format", "--report", "--help", "-h" };

        public static int Main(string[] args)
        {
            if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            {
                PrintUsage(args.Length == 0 ? Console.Error : Console.Out);
                return args.Length == 0 ? 2 : 0;
            }

            string? root = null;
            string? reportPath = null;
            bool check = false, tidy = false, format = false, esModules = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("-", StringComparison.Ordinal))
                {
                    if (!KnownFlags.Contains(a))
                    {
                        Console.Error.WriteLine($"error: unknown flag '{a}' (did you mean one of: {string.Join(", ", KnownFlags)}?)");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    switch (a)
                    {
                        case "--check": check = true; break;
                        case "--tidy": tidy = true; break;
                        case "--format": format = true; break;
                        case "--es-modules": esModules = true; break;
                        case "--report":
                            if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            {
                                Console.Error.WriteLine("error: --report requires a file path argument");
                                return 2;
                            }
                            reportPath = args[++i];
                            break;
                    }
                    continue;
                }
                if (root != null)
                {
                    Console.Error.WriteLine($"error: more than one directory argument ('{root}', '{a}')");
                    return 2;
                }
                root = Path.GetFullPath(a);
            }

            if (root == null)
            {
                Console.Error.WriteLine("error: no directory argument");
                PrintUsage(Console.Error);
                return 2;
            }
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"error: directory not found: {root}");
                return 2;
            }

            var files = new List<MigratorFile>();
            var codecs = new Dictionary<string, FileTextCodec>(StringComparer.OrdinalIgnoreCase);
            var ioErrors = new List<string>();
            foreach (string path in Directory.EnumerateFiles(root, "*.uitkx", SearchOption.AllDirectories))
            {
                if (IsInsideIgnoredFolder(path)) continue;
                try
                {
                    var codec = FileTextCodec.Read(path);
                    codecs[Path.GetFullPath(path)] = codec;
                    string asmdef = FindOwningAsmdefName(path) ?? "<Assembly-CSharp>";
                    files.Add(new MigratorFile(Path.GetFullPath(path), asmdef, codec.Text));
                }
                catch (Exception ex)
                {
                    ioErrors.Add($"read failed: {path}: {ex.Message}");
                }
            }

            var report = new List<string>();
            int exitCode;

            if (format)
            {
                exitCode = RunFormat(files, codecs, check, ioErrors, report);
            }
            else
            {
                // Pre-migration namespace snapshot for the ledger (old folder-keyed identity).
                var oldNamespaces = esModules ? SnapshotNamespaces(files, fileKeyed: false) : null;

                var changed = esModules
                    ? EsModulesMigrator.Migrate(files, out var errors)
                    : UitkxMigrator.Migrate(files, out errors, tidyUsings: tidy);

                foreach (var e in errors)
                {
                    Console.Error.WriteLine($"warn: {e.FilePath}: {e.Message}");
                    report.Add($"warn: {e.FilePath}: {e.Message}");
                }

                if (check)
                {
                    foreach (var kv in changed)
                        Console.Error.WriteLine($"would change: {kv.Key}");
                    Console.WriteLine($"{files.Count} file(s) scanned; {changed.Count} would change; {errors.Count} warning(s).");
                    exitCode = changed.Count == 0 ? 0 : 1;
                }
                else
                {
                    foreach (var kv in changed)
                    {
                        try
                        {
                            (codecs.TryGetValue(kv.Key, out var codec) ? codec : FileTextCodec.Default)
                                .Write(kv.Key, kv.Value);
                        }
                        catch (Exception ex)
                        {
                            ioErrors.Add($"write failed: {kv.Key}: {ex.Message}");
                        }
                    }

                    // Namespace-move ledger (es-modules only): every changed file whose generated
                    // namespace moved gets an old -> new row. Hand-written .cs consumers of the
                    // OLD namespace must be updated by hand — the ledger names exactly which.
                    if (esModules && oldNamespaces != null && changed.Count > 0)
                    {
                        var newNamespaces = SnapshotNamespaces(
                            changed.Select(kv => new MigratorFile(kv.Key, "<post>", kv.Value)).ToList(),
                            fileKeyed: true);
                        foreach (var kv in changed.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            if (oldNamespaces.TryGetValue(kv.Key, out var oldNs)
                                && newNamespaces.TryGetValue(kv.Key, out var newNs)
                                && !string.Equals(oldNs, newNs, StringComparison.Ordinal))
                            {
                                string row = $"namespace moved: {kv.Key}: {oldNs} -> {newNs}";
                                Console.WriteLine(row);
                                report.Add(row);
                            }
                        }
                        if (report.Any(r => r.StartsWith("namespace moved:", StringComparison.Ordinal)))
                            Console.WriteLine(
                                "note: update any hand-written .cs (using directives, partial classes, FQN references) "
                                + "that named an old namespace above; the C# compiler will flag every stale site.");
                    }

                    Console.WriteLine($"{files.Count} file(s) scanned; {changed.Count} rewritten; {errors.Count} warning(s).");
                    exitCode = 0;
                }
            }

            foreach (var e in ioErrors)
            {
                Console.Error.WriteLine($"error: {e}");
                report.Add($"error: {e}");
            }
            if (ioErrors.Count > 0 && exitCode == 0)
                exitCode = 3;

            if (reportPath != null)
            {
                try { File.WriteAllLines(reportPath, report, new UTF8Encoding(false)); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"error: could not write report '{reportPath}': {ex.Message}");
                    if (exitCode == 0) exitCode = 3;
                }
            }
            return exitCode;
        }

        private static int RunFormat(
            List<MigratorFile> files, Dictionary<string, FileTextCodec> codecs, bool check,
            List<string> ioErrors, List<string> report)
        {
            var fmt = new Ruitk.Language.Formatter.AstFormatter(
                Ruitk.Language.Formatter.FormatterOptions.Default);
            var fmtChanged = new List<string>();
            foreach (var f in files)
            {
                string formatted = fmt.Format(f.Text, f.AbsPath);
                if (string.Equals(formatted, f.Text, StringComparison.Ordinal))
                    continue;
                fmtChanged.Add(f.AbsPath);
                if (check) continue;
                try
                {
                    (codecs.TryGetValue(f.AbsPath, out var codec) ? codec : FileTextCodec.Default)
                        .Write(f.AbsPath, formatted);
                }
                catch (Exception ex)
                {
                    ioErrors.Add($"write failed: {f.AbsPath}: {ex.Message}");
                }
            }
            if (check)
                foreach (var p in fmtChanged)
                {
                    Console.Error.WriteLine($"would reformat: {p}");
                    report.Add($"would reformat: {p}");
                }
            Console.WriteLine($"{files.Count} file(s) scanned; {fmtChanged.Count} {(check ? "would reformat" : "reformatted")}.");
            return check && fmtChanged.Count > 0 ? 1 : 0;
        }

        /// <summary>Effective namespace per file under the given keying, parse-driven. Explicit
        /// <c>@namespace</c> stamps resolve identically in both modes and thus never produce a
        /// ledger row.</summary>
        private static Dictionary<string, string> SnapshotNamespaces(
            IReadOnlyList<MigratorFile> files, bool fileKeyed)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                try
                {
                    var ds = DirectiveParser.Parse(f.Text, f.AbsPath, new List<ParseDiagnostic>());
                    string? ns = EffectiveNamespace.Resolve(
                        ds.HasExplicitNamespace, ds.Namespace, f.AbsPath, fileKeyed);
                    if (!string.IsNullOrEmpty(ns))
                        map[f.AbsPath] = ns!;
                }
                catch { /* a file that does not parse has no namespace identity to track */ }
            }
            return map;
        }

        private static void PrintUsage(TextWriter w)
        {
            w.WriteLine("usage: UitkxMigrateImports <dir> [--check] [--tidy] [--es-modules] [--format] [--report <file>]");
            w.WriteLine("  --es-modules     rewrite legacy wrapper keywords (component/hook/module) to plain declarations");
            w.WriteLine("  --tidy           canonicalize usings: @using X -> import \"@X\", drop baseline-redundant usings");
            w.WriteLine("  --format         batch-run the canonical AST formatter only (no migration)");
            w.WriteLine("  --check          dry run; exit 1 if anything would change");
            w.WriteLine("  --report <file>  also write warnings + the namespace-move ledger to <file>");
            w.WriteLine("  --help           this text");
        }

        private static bool IsInsideIgnoredFolder(string path)
        {
            foreach (string part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                if (part.EndsWith("~", StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static readonly Regex s_asmdefNameRe =
            new(@"""name""\s*:\s*""([^""]+)""", RegexOptions.CultureInvariant);

        private static string? FindOwningAsmdefName(string filePath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                while (!string.IsNullOrEmpty(dir))
                {
                    foreach (string asmdef in Directory.GetFiles(dir, "*.asmdef"))
                    {
                        var m = s_asmdefNameRe.Match(File.ReadAllText(asmdef));
                        if (m.Success) return m.Groups[1].Value.Trim();
                    }
                    if (string.Equals(Path.GetFileName(dir), "Assets", StringComparison.OrdinalIgnoreCase))
                        break;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { /* fall through to default assembly key */ }
            return null;
        }

        /// <summary>
        /// Per-file text round-trip: remembers the BOM and the dominant line ending seen on read
        /// and restores both on write, so a CRLF repo does not get whole-tree EOL churn and BOMs
        /// are neither dropped nor invented (the migrator itself works LF-only in memory).
        /// </summary>
        private sealed class FileTextCodec
        {
            public string Text { get; private init; } = string.Empty;
            private bool _hadBom;
            private bool _crlf;

            public static FileTextCodec Default { get; } = new() { _hadBom = false, _crlf = false };

            public static FileTextCodec Read(string path)
            {
                byte[] bytes = File.ReadAllBytes(path);
                bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                string text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                int crlf = Regex.Matches(text, "\r\n").Count;
                int lf = Regex.Matches(text, "(?<!\r)\n").Count;
                return new FileTextCodec { Text = text, _hadBom = bom, _crlf = crlf > lf };
            }

            public void Write(string path, string newText)
            {
                string normalized = newText.Replace("\r\n", "\n").Replace("\r", "\n");
                if (_crlf)
                    normalized = normalized.Replace("\n", "\r\n");
                File.WriteAllText(path, normalized, new UTF8Encoding(_hadBom));
            }
        }
    }
}
