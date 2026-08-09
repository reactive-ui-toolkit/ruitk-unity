using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Ruitk.Language;
using Ruitk.Language.Diagnostics;
using Ruitk.Language.Lowering;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using Ruitk.Language.Roslyn;
using UitkxLanguageServer.Roslyn;
using LspDiagnosticSeverity = OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticSeverity;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace UitkxLanguageServer;

/// <summary>
/// Parses a .uitkx document, runs all diagnostic tiers, and pushes the results
/// to the LSP client as a <c>textDocument/publishDiagnostics</c> notification.
///
/// <b>Tier overview:</b>
/// <list type="bullet">
///   <item>Tier 1 (parser syntax) â€” from <see cref="ParseResult.Diagnostics"/>.</item>
///   <item>Tier 2 (structural)    â€” produced by <see cref="DiagnosticsAnalyzer"/>.</item>
///   <item>Tier 3 (Roslyn / C#)  â€” produced asynchronously by <see cref="RoslynHost"/>
///     after the virtual document is compiled.  Pushed via <see cref="PushTier3"/>.</item>
/// </list>
///
/// <b>Push flow:</b>
/// <list type="number">
///   <item><c>textDocument/didOpen|didChange</c> â†’ <see cref="Publish"/> â†’
///     T1+T2 computed synchronously and pushed immediately.</item>
///   <item>In parallel, <see cref="RoslynHost.EnqueueRebuild"/> is queued.
///     ~300 ms later Roslyn compiles and calls <see cref="PushTier3"/>, which
///     merges T1+T2+T3 and re-pushes (replacing the previous notification).</item>
/// </list>
/// </summary>
public sealed class DiagnosticsPublisher
{
    private readonly ILanguageServerFacade _server;
    private readonly UitkxSchema _schema;
    private readonly WorkspaceIndex _index;
    private readonly DocumentStore _documentStore;
    private readonly DiagnosticsAnalyzer _analyzer = new DiagnosticsAnalyzer();
    private readonly RoslynDiagnosticMapper _roslynMapper = new RoslynDiagnosticMapper();

    // Per-URI snapshot of the last T1+T2 diagnostics pushed.
    // Key = local file path (normalised), Value = diagnostic list.
    // LSP small: file paths are case-insensitive on Windows/macOS — Ordinal comparison let
    // the same file be tracked under two different-cased keys (e.g. from a URI-cased vs.
    // disk-cased path), leaking a stale entry that Forget() below could never evict.
    private readonly ConcurrentDictionary<string, IReadOnlyList<ParseDiagnostic>> _lastT1T2 =
        new ConcurrentDictionary<string, IReadOnlyList<ParseDiagnostic>>(StringComparer.OrdinalIgnoreCase);

    // Per-URI snapshot of the last T3 (Roslyn) diagnostics.
    // Carried forward in T1+T2 pushes so the error list never flashes empty
    // during the 300ms debounce gap between edits and Roslyn rebuild.
    private readonly ConcurrentDictionary<string, IReadOnlyList<ParseDiagnostic>> _lastT3 =
        new ConcurrentDictionary<string, IReadOnlyList<ParseDiagnostic>>(StringComparer.OrdinalIgnoreCase);

    // Debounce timer for IndexChanged → RevalidateOpenDocuments.
    // When many .cs files change in a burst (e.g. Unity recompilation),
    // each fires IndexChanged individually; we coalesce into one revalidation.
    private CancellationTokenSource? _revalidateCts;

    // Lazily resolved via SetRoslynHost so that RevalidateOpenDocuments can
    // trigger full T3 Roslyn rebuilds (not just T1+T2).
    private Roslyn.RoslynHost? _roslynHost;

    public DiagnosticsPublisher(
        ILanguageServerFacade server,
        UitkxSchema schema,
        WorkspaceIndex index,
        DocumentStore documentStore
    )
    {
        _server = server;
        _schema = schema;
        _index = index;
        _documentStore = documentStore;

        // When the background workspace scan finishes, re-validate every open .uitkx
        // document immediately so that components indexed after initial open are no
        // longer flagged as unknown elements.
        _index.ScanCompleted += RevalidateOpenDocuments;

        // When individual files change (didChangeWatchedFiles), debounce the
        // revalidation so a burst of .cs changes produces only one re-publish.
        _index.IndexChanged += ScheduleDebouncedRevalidation;
    }

    /// <summary>
    /// Sets the <see cref="Roslyn.RoslynHost"/> used by
    /// <see cref="RevalidateOpenDocuments"/> so it can trigger full T3
    /// Roslyn rebuilds (not just T1+T2).  Called once after all singletons
    /// have been resolved to avoid a circular-dependency in DI.
    /// </summary>
    public void SetRoslynHost(Roslyn.RoslynHost roslynHost)
    {
        _roslynHost = roslynHost;
    }

    /// <summary>
    /// LSP small: call on <c>textDocument/didClose</c> — evicts the closed file's cached
    /// T1+T2/T3 diagnostic snapshots (otherwise they leak for the life of the server and
    /// could be carried forward into a stale re-publish if the same path is reopened before
    /// a full reparse) and pushes an empty diagnostics set so closed-file squiggles don't
    /// linger in the client's Problems panel until the file is reopened.
    /// </summary>
    public void Forget(DocumentUri uri)
    {
        string? localPath = GetLocalPath(uri);
        if (!string.IsNullOrEmpty(localPath))
        {
            _lastT1T2.TryRemove(localPath, out _);
            _lastT3.TryRemove(localPath, out _);
        }

        PushToClient(uri, Array.Empty<ParseDiagnostic>());
    }

    private void RevalidateOpenDocuments()
    {
        foreach (var (uriString, text) in _documentStore.GetAll())
        {
            if (!uriString.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var docUri = DocumentUri.From(uriString);
                Publish(docUri, text, _roslynHost);
            }
            catch (Exception ex)
            {
                ServerLog.Log($"[Diagnostics] index re-publish error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Public entry to the debounced open-document revalidation. Used by the
    /// text-sync handler for PEER-DEPENDENT refreshes: republishing every
    /// dependent inline inside a didChange notification serializes seconds of
    /// analysis into the message pipeline — queued requests (notably
    /// textDocument/formatting on save) then miss VS Code's format-on-save
    /// budget and their responses are silently discarded. The changed file
    /// itself still gets its immediate synchronous publish; only the fan-out
    /// rides the debounce.
    /// </summary>
    public void ScheduleRevalidation() => ScheduleDebouncedRevalidation();

    private void ScheduleDebouncedRevalidation()
    {
        // LSP small: read-cancel-then-write on a plain field races when
        // IndexChanged fires from multiple threads in a burst (e.g. a Unity
        // recompile touching many .cs files) — two threads could each read the
        // same _revalidateCts, both cancel it, then both overwrite the field,
        // leaving one CTS never cancelled (double revalidation) or a lost
        // reference. Interlocked.Exchange makes the swap atomic: whichever CTS
        // this call displaces is the one it cancels, never one another thread
        // already displaced.
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _revalidateCts, cts);
        old?.Cancel();
        _ = Task.Delay(500, cts.Token).ContinueWith(_ =>
        {
            RevalidateOpenDocuments();
        }, cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    // â”€â”€ Tier 1 + 2: immediate synchronous push â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Parses <paramref name="text"/>, runs Tier-1 and Tier-2 analysis, and
    /// immediately pushes the resulting diagnostics to the client.
    ///
    /// Also enqueues the Roslyn T3 rebuild on <paramref name="roslynHost"/>
    /// (pass <c>null</c> to skip, e.g. in unit tests).
    /// </summary>
    /// <returns>The <see cref="ParseResult"/> produced during this invocation.</returns>
    // Builtin/ambient hooks (from the single-source HookRegistry) that need no import.
    // AmbientHookNames = canonical PascalCase + camelCase alias forms (see HookRegistry) —
    // CanonicalNames alone missed camelCase call sites → false UITKX2307 in the editor.
    private static readonly HashSet<string> s_builtinHooks =
        new HashSet<string>(global::Ruitk.Core.HookRegistry.AmbientHookNames, StringComparer.Ordinal);

    private static readonly Regex s_asmdefNameRe =
        new(@"""name""\s*:\s*""([^""]+)""", RegexOptions.CultureInvariant);

    /// <summary>
    /// Strict import diagnostics for the live editor (import/export grammar, leg 3): the reference
    /// detector (2305/2307) + import validation (2300/2301/2308/2314), fed by the workspace export
    /// table (<see cref="WorkspaceIndex.GetPeerExports"/>). Returns empty unless the file is on
    /// disk under an asmdef.
    /// </summary>
    private List<ParseDiagnostic> ComputeStrictImportDiagnostics(
        DirectiveSet directives, string localPath, string text)
    {
        var diags = new List<ParseDiagnostic>();
        if (string.IsNullOrEmpty(localPath))
            return diags;
        if (!_index.HasCompletedInitialScan)
            return diags; // avoid false 2305 before peers are indexed

        string? asmdefDir = FindAsmdefDir(localPath);
        var peerExports = _index.GetPeerExports(localPath, asmdefDir);

        ParseDiagnostic ToDiag(StrictImportDetector.Finding f) => new ParseDiagnostic
        {
            Code = f.Code,
            // Heuristic findings (hook-call / module member-access scans over C# expression
            // text) are warnings — ambient C# legitimately produces those shapes. Mirrors the
            // SG pipeline's mapping. 2304 is ERROR-tier since 0.9.1 (owner decision — the
            // "used" scan over-approximates, so a firing 2304 is always real).
            Severity = f.IsHeuristic ? ParseSeverity.Warning : ParseSeverity.Error,
            SourceLine = f.Line,
            // Column span of the offending token (specifier string / imported name / referenced
            // identifier) when tracked; -1 → line-start fallback (a 1-char squiggle at col 0).
            SourceColumn = Math.Max(0, f.Column),
            EndLine = f.EndColumn > 0 ? f.Line : 0,
            EndColumn = Math.Max(0, f.EndColumn),
            Message = f.Message,
        };

        // 2305 / 2307 — references without an import.
        string scannable = StrictImportDetector.ScrubNonCode(text);
        foreach (var f in StrictImportDetector.Detect(
                     directives, localPath, scannable, peerExports, s_builtinHooks.Contains))
            diags.Add(ToDiag(f));

        // 2304 — imported binding never referenced (error since 0.9.1).
        foreach (var f in StrictImportDetector.DetectUnusedImports(directives, scannable))
            diags.Add(ToDiag(f));

        // 2300 / 2301 / 2308 / 2314 — imports that don't resolve.
        if (!directives.Imports.IsDefaultOrEmpty)
        {
            string importerDir = (Path.GetDirectoryName(localPath) ?? string.Empty).Replace('\\', '/');
            string? projectRoot = AssetPathUtil.GetProjectRoot(localPath);
            string rootDir = projectRoot != null ? projectRoot + "/" + UitkxConfig.LoadRoot(importerDir) : importerDir;
            string? importerAsmdef = FindAsmdefName(localPath);

            var exportedSet = new HashSet<(string Path, string Name)>();
            foreach (var pe in peerExports)
                exportedSet.Add((pe.TargetFilePath.Replace('\\', '/'), pe.Name));
            bool IsExportedByFile(string name, string targetPath) =>
                exportedSet.Contains((targetPath.Replace('\\', '/'), name));

            DirectiveSet? ParseTargetFile(string path)
            {
                if (!File.Exists(path)) return null;
                try { return DirectiveParser.Parse(File.ReadAllText(path), path, new List<ParseDiagnostic>()); }
                catch { return null; }
            }

            foreach (var f in StrictImportDetector.ValidateImports(
                         directives, importerDir, rootDir, importerAsmdef,
                         File.Exists, FindAsmdefName, IsExportedByFile, ParseTargetFile))
                diags.Add(ToDiag(f));
        }

        // 2306 — value-import cycle (hooks/modules load eagerly). Only compute when THIS file
        // imports a hook/module (a necessary condition for it to be part of a value cycle).
        var cycleDiag = ComputeValueCycleDiagnostic(directives, localPath, asmdefDir);
        if (cycleDiag != null)
            diags.Add(cycleDiag);

        // 2317 — a @using/namespace-import that exactly duplicates the auto-injected baseline
        // (e.g. `@using UnityEngine`, `@using System`). Provably redundant (no compilation needed),
        // editor-only Hint (faded, never build-breaking). The compilation-dependent 2316 is pushed
        // separately from the Roslyn tier (RoslynHost.ValidateNamespaceUsings).
        foreach (var d in ComputeRedundantUsingDiagnostics(directives))
            diags.Add(d);

        return diags;
    }

    /// <summary>
    /// UITKX2317 (namespace-import unification plan): flags each plain-namespace <c>@using</c> /
    /// <c>import "@Ns"</c> whose payload is already in <see cref="AutoInjectedUsings"/> — a provably
    /// redundant line the author can delete. Hint severity (rendered faded via the Unnecessary tag in
    /// <see cref="ToLsp"/>); sound and compilation-free, so it never false-positives.
    /// </summary>
    private static List<ParseDiagnostic> ComputeRedundantUsingDiagnostics(DirectiveSet directives)
    {
        var diags = new List<ParseDiagnostic>();
        if (directives.UsingDirectives.IsDefaultOrEmpty)
            return diags;
        foreach (var u in directives.UsingDirectives)
        {
            if (!AutoInjectedUsings.IsRedundant(u.Payload))
                continue;
            int col = u.PayloadColumn >= 0 ? u.PayloadColumn : 0;
            diags.Add(new ParseDiagnostic
            {
                Code = DiagnosticCodes.UnusedUsing,
                Severity = ParseSeverity.Hint,
                SourceLine = u.Line,
                SourceColumn = col,
                EndLine = u.Line,
                EndColumn = col + u.Payload.Length,
                Message = $"redundant using `{u.Payload}` — already in scope by default; safe to remove",
            });
        }
        return diags;
    }

    /// <summary>
    /// UITKX2306 for a value-import cycle through <paramref name="localPath"/> (import/export
    /// grammar §6). Edges are import relationships where the imported name is a HOOK or MODULE
    /// (components load lazily and are exempt). Returns null when this file has no value-imports or
    /// is not part of a cycle.
    /// </summary>
    private ParseDiagnostic? ComputeValueCycleDiagnostic(DirectiveSet directives, string localPath, string? asmdefDir)
    {
        if (directives.Imports.IsDefaultOrEmpty)
            return null;

        // Necessary condition: this file imports at least one hook/module.
        bool hasValueImport = false;
        foreach (var imp in directives.Imports)
        {
            string dir = (Path.GetDirectoryName(localPath) ?? string.Empty).Replace('\\', '/');
            string? root = AssetPathUtil.GetProjectRoot(localPath) is string pr ? pr + "/Assets" : dir;
            string? tgt = ImportResolver.MapSpecifierToPath(dir, imp.Specifier, root, out _);
            if (tgt != null && ImportsValueName(tgt, imp.Names)) { hasValueImport = true; break; }
        }
        if (!hasValueImport)
            return null;

        // Build the asmdef-wide value-edge graph.
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, specifier, names) in _index.GetImportEdges(asmdefDir))
        {
            string dir = (Path.GetDirectoryName(file) ?? string.Empty).Replace('\\', '/');
            string? root = AssetPathUtil.GetProjectRoot(file) is string pr ? pr + "/Assets" : dir;
            string? tgt = ImportResolver.MapSpecifierToPath(dir, specifier, root, out _);
            if (tgt == null || !ImportsValueName(tgt, names))
                continue;
            // Normalize BOTH endpoints to forward slashes: MapSpecifierToPath yields forward-slashed
            // targets, but the index keys are OS-native — a mismatch would disconnect the graph.
            string key = file.Replace('\\', '/');
            string dst = tgt.Replace('\\', '/');
            if (!edges.TryGetValue(key, out var list))
                edges[key] = list = new List<string>();
            ((List<string>)list).Add(dst);
        }

        var cycle = UitkxImportGraph.FindCycle(edges);
        if (cycle == null)
            return null;

        string norm(string p) => p.Replace('\\', '/').TrimEnd('/');
        bool involvesSelf = cycle.Any(c => string.Equals(norm(c), norm(localPath), StringComparison.OrdinalIgnoreCase));
        if (!involvesSelf)
            return null;

        string chain = string.Join(" -> ", cycle.Select(c => Path.GetFileName(c)));
        var imp0 = directives.Imports[0];
        // Squiggle the whole first import statement (keyword through closing quote) when the
        // specifier span is tracked; line-start fallback otherwise.
        int endCol = imp0.SpecifierColumn >= 0 ? imp0.SpecifierColumn + imp0.Specifier.Length + 2 : 0;
        return new ParseDiagnostic
        {
            Code = "UITKX2306",
            Severity = ParseSeverity.Error,
            SourceLine = imp0.Line,
            SourceColumn = Math.Max(0, imp0.Column),
            EndLine = endCol > 0 ? imp0.Line : 0,
            EndColumn = endCol,
            Message = $"value-import cycle: {chain} (hooks/modules load eagerly — break the chain or move to component refs)",
        };
    }

    /// <summary>True when any of <paramref name="names"/> is exported as a hook or module by <paramref name="targetFile"/>.</summary>
    private bool ImportsValueName(string targetFile, IReadOnlyList<string> names)
    {
        foreach (var n in names)
        {
            var kind = _index.GetExportKind(targetFile, n);
            if (kind == StrictImportDetector.ExportKind.Hook || kind == StrictImportDetector.ExportKind.Module)
                return true;
        }
        return false;
    }

    /// <summary>Directory of the nearest owning <c>*.asmdef</c> walking up from a file, or null.</summary>
    private static string? FindAsmdefDir(string filePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.GetFiles(dir, "*.asmdef").Length > 0)
                    return dir;
                if (string.Equals(Path.GetFileName(dir), "Assets", StringComparison.OrdinalIgnoreCase))
                    break;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Assembly name of the nearest owning <c>*.asmdef</c>, or null (default assembly).</summary>
    private static string? FindAsmdefName(string filePath)
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
        catch { }
        return null;
    }

    public ParseResult Publish(DocumentUri uri, string text, RoslynHost? roslynHost = null)
    {
        string localPath = GetLocalPath(uri) ?? string.Empty;

        // â”€â”€ Parse â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var parseDiags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(text, localPath, parseDiags);
        var parsedNodes = UitkxParser.Parse(text, localPath, directives, parseDiags);

        // Also validate UITKX markup embedded inside setup-code JSX blocks,
        // e.g. `var x = (<Box> @if (broken) { ... } </Box>)`.
        // These blocks are replaced by (object)null! in the Roslyn virtual doc,
        // so Roslyn never sees them — we must check them here at T1/T2 level.
        var setupJsxNodes = ImmutableArray<AstNode>.Empty;
        var allSetupJsxRanges = directives.SetupCodeMarkupRanges;
        if (!directives.SetupCodeBareJsxRanges.IsDefaultOrEmpty)
        {
            allSetupJsxRanges = allSetupJsxRanges.IsDefaultOrEmpty
                ? directives.SetupCodeBareJsxRanges
                : allSetupJsxRanges.AddRange(directives.SetupCodeBareJsxRanges);
        }
        if (!allSetupJsxRanges.IsDefaultOrEmpty)
        {
            var setupBuilder = ImmutableArray.CreateBuilder<AstNode>();
            foreach (var (jsxStart, jsxEnd, jsxLine) in allSetupJsxRanges)
            {
                var jsxDirectives = directives with
                {
                    MarkupStartIndex = jsxStart,
                    MarkupEndIndex = jsxEnd,
                    MarkupStartLine = jsxLine,
                };
                var jsxNodes = UitkxParser.Parse(text, localPath, jsxDirectives, parseDiags, validateSingleRoot: true);
                setupBuilder.AddRange(jsxNodes);
            }
            setupJsxNodes = setupBuilder.ToImmutable();
        }

        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, localPath);

        var parseResult = new ParseResult(
            directives,
            nodes,
            ImmutableArray.CreateRange(parseDiags)
        );

        // â”€â”€ T2 structural analysis â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Seed projectElements with the component declared in this file so that
        // self-referencing components (e.g. recursive <DeepNode />) and sibling
        // components not yet reached by the async scan are never falsely flagged.
        //
        // When the workspace index hasn't completed its initial scan, pass null
        // for both projectElements and knownAttributes.  This suppresses
        // UITKX0105 (unknown element) and UITKX0109 (unknown attribute) until
        // the scan finishes and ScanCompleted triggers RevalidateOpenDocuments.
        var projectElements = _index.HasCompletedInitialScan
            ? BuildProjectElements(directives.ComponentName, directives.Backend)
            : null;
        var knownAttributes = projectElements != null
            ? BuildKnownAttributes(projectElements, directives, localPath, directives.Backend)
            : null;
        var t2Diags = _analyzer.Analyze(parseResult, localPath, projectElements, knownAttributes, text);

        // Also run T2 analysis on setup code JSX (local functions, JSX variable assignments).
        if (!setupJsxNodes.IsEmpty)
            t2Diags = t2Diags.Concat(_analyzer.AnalyzeNodes(setupJsxNodes, projectElements, knownAttributes, text)).ToList();

        // ── T2v version-compatibility diagnostics ────────────────────────────
        // Check elements and style properties against the detected Unity version.
        // Only produces diagnostics when schema entries carry sinceUnity annotations.
        var versionDiags = CheckVersionCompatibility(
            parsedNodes, roslynHost?.DetectedUnityVersion ?? UnityVersion.Unknown,
            directives.Backend);

        // ── UITKX0113 — duplicate component declaration, same namespace ──────
        // The multi-valued WorkspaceIndex (TECH_DEBT_V2 #21 fix, 2026-05-18) keeps
        // every declarant alive so this analyzer can surface the ambiguity to the
        // user. Fires only when 2+ declarants land in the same asmdef AND the same
        // effective namespace — file-keyed namespaces (ES modules) make same-named
        // components in different folders legal.
        var duplicateDiags = ComputeDuplicateComponentDiagnostics(directives, localPath);

        // ── Strict import diagnostics (2300/2301/2305/2307/2308/2314) ────────
        // Live editor parity with the build: reference detector + import validation, fed by the
        // workspace export table. Only when the file is on disk.
        var strictDiags = ComputeStrictImportDiagnostics(directives, localPath, text);

        // ── UITKX2107 — deprecated companion partial-class merge ─────────────
        // SG parity: the source generator warns when a legacy `module X` silently merges
        // into a component declared in ANOTHER file via shared folder namespaces; surface
        // the same deprecation live so editors see it before a build.
        var companionDiags = ComputeCompanionMergeDiagnostics(directives, localPath);


        // â”€â”€ Combine T1 + T2 and push immediately â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Suppress T1 parser diagnostics that fall inside unreachable regions
        // (e.g. UITKX2103 'Multiple top-level returns' when the second return
        // is unreachable after the first).
        var unreachableT2 = t2Diags
            .Where(d =>
                d.Code == DiagnosticCodes.UnreachableAfterReturn
                || d.Code == DiagnosticCodes.UnreachableAfterBreakOrContinue
            )
            .ToList();

        IEnumerable<ParseDiagnostic> filteredT1 = parseResult.Diagnostics;
        if (unreachableT2.Count > 0)
        {
            filteredT1 = parseResult.Diagnostics.Where(pd =>
            {
                foreach (var ur in unreachableT2)
                {
                    int urStart = ur.SourceLine;
                    int urEnd = ur.EndLine > 0 ? ur.EndLine : ur.SourceLine;
                    if (pd.SourceLine >= urStart && pd.SourceLine <= urEnd)
                        return false;
                }
                return true;
            });
        }

        var t1t2 = filteredT1.Concat(t2Diags).Concat(versionDiags).Concat(duplicateDiags).Concat(strictDiags).Concat(companionDiags).ToList();
        if (!string.IsNullOrEmpty(localPath))
            _lastT1T2[localPath] = t1t2;

        // Carry forward the last T3 diagnostics so the error list doesn't
        // flash empty during the 300ms debounce gap before Roslyn rebuilds.
        // BUT strip unreachable diagnostics from the carry-forward: they
        // cause stale gray when the user deletes a return statement.
        IEnumerable<ParseDiagnostic> combined = t1t2;
        if (
            !string.IsNullOrEmpty(localPath)
            && _lastT3.TryGetValue(localPath, out var cachedT3)
            && cachedT3.Count > 0
        )
        {
            var nonUnreachable = cachedT3.Where(d =>
                d.Code != "CS0162"
                && d.Code != DiagnosticCodes.UnreachableAfterReturn
                && d.Code != DiagnosticCodes.UnreachableAfterBreakOrContinue
            );
            combined = t1t2.Concat(nonUnreachable);
        }

        PushToClient(uri, combined);

        // â”€â”€ Kick off T3 Roslyn rebuild in the background â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (roslynHost != null && !string.IsNullOrEmpty(localPath))
            roslynHost.EnqueueRebuild(localPath, text, parseResult, this);

        return parseResult;
    }

    // â”€â”€ Tier 3: async Roslyn push â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Called by <see cref="RoslynHost"/> when the Roslyn compilation is ready.
    /// Re-pushes the combined T1+T2+T3 diagnostics as a single notification,
    /// replacing the earlier T1+T2-only notification for this file.
    /// </summary>
    public void PushTier3(
        string uitkxFilePath,
        IReadOnlyList<(
            Microsoft.CodeAnalysis.Diagnostic Diagnostic,
            SourceMapEntry? MapEntry,
            bool IsStateSetterCS1503
        )> roslynDiags,
        string? uitkxSource = null,
        IReadOnlyList<ParseDiagnostic>? extraUitkxDiags = null
    )
    {
        try
        {
            // Map Roslyn diagnostics → ParseDiagnostic
            var t3 = _roslynMapper.Map(roslynDiags, uitkxFilePath, uitkxSource);

            // Namespace-import diagnostics (UITKX2316/2317) are computed by RoslynHost from the same
            // compilation but have no source-map entry (using lines are scaffold), so they arrive as
            // ready-made ParseDiagnostics rather than through the mapper. Fold them into the T3 tier
            // so they cache + carry-forward like every other Roslyn-tier diagnostic.
            if (extraUitkxDiags != null && extraUitkxDiags.Count > 0)
                t3 = t3.Concat(extraUitkxDiags).ToList();

            // Expand CS0162 (unreachable code) from a single statement to the
            // full unreachable range up to the enclosing closing `}`.
            if (!string.IsNullOrEmpty(uitkxSource))
                t3 = ExpandUnreachableRanges(t3, uitkxSource);

            // Retrieve the last T1+T2 snapshot for this file (may be missing
            // if Publish hasn't run yet — that's fine, an empty list is safe).
            _lastT1T2.TryGetValue(uitkxFilePath, out var t1t2);

            // Suppress Roslyn warnings/errors that fall within unreachable
            // regions (UITKX0107/UITKX0110).  Those diagnostics (CS8321
            // "local function never used", CS0219 "variable never used", etc.)
            // are false‐positives caused by dead code after return — the
            // unreachable hint and fade are sufficient.
            var unreachableRanges = (t1t2 ?? Array.Empty<ParseDiagnostic>())
                .Where(d =>
                    d.Code == DiagnosticCodes.UnreachableAfterReturn
                    || d.Code == DiagnosticCodes.UnreachableAfterBreakOrContinue
                )
                .ToList();

            IEnumerable<ParseDiagnostic> filteredT3 = t3;
            if (unreachableRanges.Count > 0)
            {
                filteredT3 = t3.Where(rd =>
                {
                    // Drop any Roslyn diagnostic whose start line is inside
                    // an unreachable range (including CS0162, to avoid
                    // double-marking with our own UITKX0107).
                    foreach (var ur in unreachableRanges)
                    {
                        int urStart = ur.SourceLine;
                        int urEnd = ur.EndLine > 0 ? ur.EndLine : ur.SourceLine;
                        if (rd.SourceLine >= urStart && rd.SourceLine <= urEnd)
                            return false;
                    }
                    return true;
                });
            }

            var filteredT3List = filteredT3.ToList();

            // Cache the T3 snapshot so it can be carried forward in T1+T2 pushes.
            _lastT3[uitkxFilePath] = filteredT3List;

            var combined = ((IEnumerable<ParseDiagnostic>)(t1t2 ?? Array.Empty<ParseDiagnostic>()))
                .Concat(filteredT3List)
                .ToList();

            DocumentUri uri = DocumentUri.File(uitkxFilePath);
            PushToClient(uri, combined);
        }
        catch (Exception ex)
        {
            ServerLog.Log($"[Diagnostics] PushTier3 error: {ex.Message}");
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Expands CS0162 diagnostics from a single statement to the full
    /// unreachable range within the enclosing scope block.
    /// </summary>
    private static IReadOnlyList<ParseDiagnostic> ExpandUnreachableRanges(
        IReadOnlyList<ParseDiagnostic> diagnostics, string source)
    {
        var result = new List<ParseDiagnostic>(diagnostics.Count);
        var lineOffsets = BuildLineOffsets(source);

        foreach (var d in diagnostics)
        {
            if (d.Code != "CS0162")
            {
                result.Add(d);
                continue;
            }

            int endLine = FindEnclosingScopeEnd(source, lineOffsets, d.SourceLine);
            if (endLine > d.SourceLine)
            {
                // Re-emit as UITKX0107 with Hint severity so it gets identical
                // treatment to our own T2 unreachable diagnostic: dim-only, no
                // squiggly, and proper Unnecessary tag in ToLsp().
                result.Add(new ParseDiagnostic
                {
                    Code = DiagnosticCodes.UnreachableAfterReturn,
                    Severity = ParseSeverity.Hint,
                    Message = "Unreachable code after 'return'.",
                    SourceLine = d.SourceLine,
                    SourceColumn = 0,
                    EndLine = endLine - 1,
                    EndColumn = 9999,
                });
            }
            else
            {
                // Single-statement fallback: still convert to UITKX0107.
                result.Add(new ParseDiagnostic
                {
                    Code = DiagnosticCodes.UnreachableAfterReturn,
                    Severity = ParseSeverity.Hint,
                    Message = "Unreachable code after 'return'.",
                    SourceLine = d.SourceLine,
                    SourceColumn = d.SourceColumn,
                    EndLine = d.EndLine,
                    EndColumn = d.EndColumn,
                });
            }
        }

        return result;
    }

    private static List<int> BuildLineOffsets(string source)
    {
        var offsets = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
                offsets.Add(i + 1);
        }
        return offsets;
    }

    /// <summary>
    /// From <paramref name="startLine"/> (1-based), scans forward to find the
    /// closing <c>}</c> at brace depth 0, skipping strings, comments, and
    /// nested blocks. Returns the 1-based line of that <c>}</c>.
    /// </summary>
    private static int FindEnclosingScopeEnd(string source, List<int> lineOffsets, int startLine)
    {
        if (startLine < 1 || startLine > lineOffsets.Count)
            return startLine;

        int i = lineOffsets[startLine - 1];
        int braceDepth = 0;
        int currentLine = startLine;

        while (i < source.Length)
        {
            char c = source[i];
            if (c == '\n') { currentLine++; i++; continue; }
            if (c == '\r') { i++; continue; }

            // Skip strings/chars/comments via the shared lexer (see U-20): the previous
            // hand-rolled scan here had no verbatim-string (@"..."/@$".../$@"...) awareness
            // at all, so a literal '{' or '}' inside a multi-line verbatim string body would
            // corrupt the brace-depth tracking below and mis-anchor the diagnostic's range.
            int before = i;
            if (CSharpLexFacts.TrySkipNonCode(source, ref i, source.Length))
            {
                for (int k = before; k < i; k++)
                    if (source[k] == '\n') currentLine++;
                continue;
            }

            if (c == '{') { braceDepth++; i++; continue; }
            if (c == '}')
            {
                if (braceDepth > 0) { braceDepth--; i++; continue; }
                return currentLine;
            }

            i++;
        }

        return startLine;
    }

    private void PushToClient(DocumentUri uri, IEnumerable<ParseDiagnostic> diagnostics)
    {
        var lspDiags = diagnostics.Select(ToLsp).ToArray();
        _server.TextDocument.PublishDiagnostics(
            new PublishDiagnosticsParams
            {
                Uri = uri,
                Diagnostics = new Container<Diagnostic>(lspDiags),
            }
        );
    }

    private static Diagnostic ToLsp(ParseDiagnostic d)
    {
        // SourceLine is 1-based; LSP is 0-based.
        int startLine = Math.Max(0, d.SourceLine - 1);
        int startChar = Math.Max(0, d.SourceColumn);

        // EndLine/EndColumn are 0 when not tracked â†’ fall back to same position.
        int endLine = d.EndLine > 0 ? Math.Max(0, d.EndLine - 1) : startLine;
        // Ensure end is always strictly past start so VS Code renders a visible squiggle.
        int endChar = d.EndColumn > 0 ? Math.Max(d.EndColumn, startChar + 1) : startChar + 1;

        return new Diagnostic
        {
            Range = new LspRange(
                new Position(startLine, startChar),
                new Position(endLine, endChar)
            ),
            Severity = ToLspSeverity(d.Severity),
            Code = (DiagnosticCode)d.Code,
            Source = "uitkx",
            Message = d.Message,
            Tags =
                d.Code == DiagnosticCodes.UnreachableAfterReturn
                || d.Code == DiagnosticCodes.UnreachableAfterBreakOrContinue
                || d.Code == "CS0162" // Roslyn: Unreachable code detected
                || d.Code == DiagnosticCodes.UnusedParameter // UITKX0111
                || d.Code == DiagnosticCodes.UnusedUsing // UITKX2317 redundant using
                || d.Code == "CS0219" // Roslyn: unused local variable
                || d.Code == "CS8321" // Roslyn: unused local function
                    ? new Container<DiagnosticTag>(DiagnosticTag.Unnecessary)
                    : null,
        };
    }

    private static LspDiagnosticSeverity ToLspSeverity(ParseSeverity s) =>
        s switch
        {
            ParseSeverity.Error => LspDiagnosticSeverity.Error,
            ParseSeverity.Warning => LspDiagnosticSeverity.Warning,
            ParseSeverity.Information => LspDiagnosticSeverity.Information,
            ParseSeverity.Hint => LspDiagnosticSeverity.Hint,
            _ => LspDiagnosticSeverity.Information,
        };

    private static string? GetLocalPath(DocumentUri uri)
    {
        try
        {
            var sysUri = new Uri(uri.ToString());
            return sysUri.IsFile ? sysUri.LocalPath : null;
        }
        catch
        {
            return null;
        }
    }

    // ── UITKX0113 — duplicate component declaration in the same namespace ──

    /// <summary>
    /// Emits UITKX0113 against this file's component declaration(s) when
    /// another <c>.uitkx</c> in the SAME asmdef declares the same name into
    /// the SAME effective namespace — the only case where the generated
    /// classes actually collide. Since the ES-modules redesign namespaces are
    /// FILE-keyed (folder segments + file stem), so same-named components in
    /// different folders are legal and must not be flagged; legacy files
    /// colliding via a shared explicit <c>@namespace</c> still are. The
    /// namespace comparison mirrors the SG seam exactly
    /// (<see cref="EffectiveNamespace.Resolve"/>), same as
    /// <see cref="ComputeCompanionMergeDiagnostics"/>. Fires per-declarant
    /// (each duplicate file gets its own diagnostic) so the user sees the
    /// warning regardless of which file they open. Suppressed when the
    /// workspace scan hasn't completed (transient state).
    /// </summary>
    internal List<ParseDiagnostic> ComputeDuplicateComponentDiagnostics(
        DirectiveSet directives,
        string localPath)
    {
        var diags = new List<ParseDiagnostic>();
        if (!_index.HasCompletedInitialScan || string.IsNullOrEmpty(localPath))
            return diags;

        var ownNames = new List<string>();
        if (!directives.ComponentDeclarations.IsDefaultOrEmpty)
        {
            foreach (var comp in directives.ComponentDeclarations)
                if (!string.IsNullOrEmpty(comp.Name) && !ownNames.Contains(comp.Name))
                    ownNames.Add(comp.Name);
        }
        else if (!string.IsNullOrEmpty(directives.ComponentName))
        {
            ownNames.Add(directives.ComponentName!);
        }
        if (ownNames.Count == 0)
            return diags;

        string ownAsmdef = AsmdefResolver.OwningAsmdefName(localPath);
        string? ownNsResolved = EffectiveNamespace.Resolve(
            directives.HasExplicitNamespace, directives.Namespace, localPath,
            fileKeyed: !directives.UsesLegacySyntax);
        string ownNs = string.IsNullOrEmpty(ownNsResolved) ? "Ruitk.Generated" : ownNsResolved!;

        foreach (var componentName in ownNames)
        {
            var declarants = _index.GetAllElementInfo(componentName);
            if (declarants.Count < 2)
                continue;

            // A duplicate is real only when the peer is a .uitkx in the same
            // asmdef (cross-asmdef duplicates are separate compilation units)
            // that declares the same component name into the same effective
            // namespace. Peer directives are parsed only for same-named
            // declarants, so the cost is bounded by genuine collisions.
            var otherPaths = new List<string>();
            foreach (var d in declarants)
            {
                if (string.Equals(d.FilePath, localPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!d.FilePath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(
                        AsmdefResolver.OwningAsmdefName(d.FilePath), ownAsmdef,
                        StringComparison.Ordinal))
                    continue;

                var peerDs = TryParsePeerDirectives(d.FilePath);
                if (peerDs == null || !PeerDeclaresComponent(peerDs, componentName))
                    continue;

                string? peerNsResolved = EffectiveNamespace.Resolve(
                    peerDs.HasExplicitNamespace, peerDs.Namespace, d.FilePath,
                    fileKeyed: !peerDs.UsesLegacySyntax);
                string peerNs = string.IsNullOrEmpty(peerNsResolved)
                    ? "Ruitk.Generated"
                    : peerNsResolved!;
                if (string.Equals(peerNs, ownNs, StringComparison.Ordinal))
                    otherPaths.Add(d.FilePath);
            }
            if (otherPaths.Count == 0)
                continue;

            // Locate the declaration line for the squiggle. Pull the FileLine from
            // the index entry for THIS file (always populated by IndexUitkxFile).
            int line = 1;
            foreach (var d in declarants)
            {
                if (string.Equals(d.FilePath, localPath, StringComparison.OrdinalIgnoreCase))
                {
                    line = d.FileLine > 0 ? d.FileLine : 1;
                    break;
                }
            }

            string others = otherPaths.Count == 1
                ? System.IO.Path.GetFileName(otherPaths[0])
                : $"{otherPaths.Count} other files";

            diags.Add(new ParseDiagnostic
            {
                Code = DiagnosticCodes.DuplicateComponent,
                Severity = ParseSeverity.Warning,
                Message =
                    $"Component '{componentName}' is also declared in {others} with the same "
                    + $"effective namespace ('{ownNs}'); the generated classes collide. "
                    + "Duplicate declarations are almost always a copy-paste refactor that "
                    + "forgot to rename. Pick a unique name or move one file to a different folder.",
                SourceLine = line,
                SourceColumn = 0,
                EndLine = line,
                EndColumn = 9999,
            });
        }

        return diags;
    }

    private static bool PeerDeclaresComponent(DirectiveSet peer, string name)
    {
        if (!peer.ComponentDeclarations.IsDefaultOrEmpty)
        {
            foreach (var pc in peer.ComponentDeclarations)
                if (pc.Name == name)
                    return true;
            return false;
        }
        return string.Equals(peer.ComponentName, name, StringComparison.Ordinal);
    }

    // ── UITKX2107 — deprecated companion partial-class merge ────────────────

    /// <summary>
    /// LSP mirror of the source generator's UITKX2107 branch (ModuleEmitter): a legacy
    /// <c>module X</c> with NO same-file <c>component X</c>, where a component named
    /// <c>X</c> declared in ANOTHER <c>.uitkx</c> file resolves to the SAME effective
    /// namespace, silently becomes part of that component's partial type. Warning per
    /// merging module, anchored at its declaration line. Emission conditions match the
    /// SG exactly — same namespace seam (<see cref="EffectiveNamespace.Resolve"/>),
    /// same message. Candidate peers come from the workspace index keyed by the module
    /// name, so at most the same-named declarant files are parsed per publish.
    /// </summary>
    internal List<ParseDiagnostic> ComputeCompanionMergeDiagnostics(
        DirectiveSet directives, string localPath)
    {
        var diags = new List<ParseDiagnostic>();
        if (directives.ModuleDeclarations.IsDefaultOrEmpty || string.IsNullOrEmpty(localPath))
            return diags;
        if (!_index.HasCompletedInitialScan)
            return diags;

        string? resolvedNs = EffectiveNamespace.Resolve(
            directives.HasExplicitNamespace, directives.Namespace, localPath,
            fileKeyed: !directives.UsesLegacySyntax);
        string ns = string.IsNullOrEmpty(resolvedNs) ? "Ruitk.Generated" : resolvedNs!;

        foreach (var module in directives.ModuleDeclarations)
        {
            bool mergesSameFile = false;
            if (!directives.ComponentDeclarations.IsDefaultOrEmpty)
                foreach (var comp in directives.ComponentDeclarations)
                    if (comp.Name == module.Name)
                    {
                        mergesSameFile = true;
                        break;
                    }
            if (mergesSameFile)
                continue;

            foreach (var info in _index.GetAllElementInfo(module.Name))
            {
                if (!info.FilePath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(
                        Path.GetFullPath(info.FilePath), Path.GetFullPath(localPath),
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                DirectiveSet? peerDs = TryParsePeerDirectives(info.FilePath);
                if (peerDs == null)
                    continue;

                bool declaresComponent = false;
                if (!peerDs.ComponentDeclarations.IsDefaultOrEmpty)
                {
                    foreach (var pc in peerDs.ComponentDeclarations)
                        if (pc.Name == module.Name)
                        {
                            declaresComponent = true;
                            break;
                        }
                }
                else if (string.Equals(peerDs.ComponentName, module.Name, StringComparison.Ordinal))
                {
                    declaresComponent = true;
                }
                if (!declaresComponent)
                    continue;

                string? peerNs = EffectiveNamespace.Resolve(
                    peerDs.HasExplicitNamespace, peerDs.Namespace, info.FilePath,
                    fileKeyed: !peerDs.UsesLegacySyntax);
                if (string.IsNullOrEmpty(peerNs)
                    || !string.Equals(peerNs, ns, StringComparison.Ordinal))
                    continue;

                int line = module.DeclarationLine > 0 ? module.DeclarationLine : 1;
                diags.Add(new ParseDiagnostic
                {
                    Code = DiagnosticCodes.DeprecatedCompanionMerge,
                    Severity = ParseSeverity.Warning,
                    SourceLine = line,
                    SourceColumn = 0,
                    EndLine = line,
                    EndColumn = 9999,
                    Message =
                        $"companion partial-class merging is deprecated — '{Path.GetFileName(localPath)}' "
                        + $"merges into '{Path.GetFileName(info.FilePath)}' via legacy folder namespaces; "
                        + "migrate the companion set to plain declarations and file imports",
                });
                break;
            }
        }
        return diags;
    }

    /// <summary>Parses a peer file's directives, preferring an open editor buffer over disk.
    /// Null on any read/parse failure (best-effort — no diagnostic then).</summary>
    private DirectiveSet? TryParsePeerDirectives(string filePath)
    {
        try
        {
            if (!_documentStore.TryGetByPath(filePath, out var text))
            {
                if (!File.Exists(filePath))
                    return null;
                text = File.ReadAllText(filePath);
            }
            return DirectiveParser.Parse(text, filePath, new List<ParseDiagnostic>());
        }
        catch
        {
            return null;
        }
    }

    // ── Version-compatibility diagnostics ──────────────────────────────────

    /// <summary>
    /// Walks the parsed element tree and emits <see cref="DiagnosticCodes.VersionMismatch"/>
    /// warnings for any elements (or style properties) whose schema annotations
    /// indicate they require a newer Unity version than the one detected in the project.
    /// Returns an empty list when the user version is unknown or no annotations exist.
    /// </summary>
    private List<ParseDiagnostic> CheckVersionCompatibility(
        ImmutableArray<AstNode> nodes, UnityVersion userVersion, string? backend)
    {
        var diags = new List<ParseDiagnostic>();
        if (!userVersion.IsKnown)
            return diags;
        foreach (var node in nodes)
            WalkForVersionDiags(node, userVersion, backend, diags);
        return diags;
    }

    private void WalkForVersionDiags(AstNode node, UnityVersion userVersion, string? backend, List<ParseDiagnostic> diags)
    {
        if (node is ElementNode el)
        {
            // Check element version requirement
            var elMinVersion = _schema.GetElementMinVersion(el.TagName);
            if (elMinVersion.IsKnown && userVersion < elMinVersion)
            {
                diags.Add(new ParseDiagnostic
                {
                    Code = DiagnosticCodes.VersionMismatch,
                    Severity = ParseSeverity.Warning,
                    Message = $"Element '<{el.TagName}>' requires {elMinVersion.ToDisplayString()}+, "
                            + $"but this project targets {userVersion.ToDisplayString()}.",
                    SourceLine = el.SourceLine,
                    SourceColumn = el.SourceColumn + 1, // +1 to point past '<'
                    EndLine = el.SourceLine,
                    EndColumn = el.SourceColumn + 1 + el.TagName.Length,
                });
            }

            // Check attribute version requirements
            foreach (var attr in el.Attributes)
            {
                var schemaAttr = _schema.GetAttributesForElement(el.TagName, backend)
                    .FirstOrDefault(a => a.Name.Equals(attr.Name, StringComparison.OrdinalIgnoreCase));
                if (schemaAttr?.SinceUnity is not null
                    && UnityVersion.TryParse(schemaAttr.SinceUnity, out var attrMinVersion)
                    && userVersion < attrMinVersion)
                {
                    diags.Add(new ParseDiagnostic
                    {
                        Code = DiagnosticCodes.VersionMismatch,
                        Severity = ParseSeverity.Warning,
                        Message = $"Attribute '{attr.Name}' on '<{el.TagName}>' requires {attrMinVersion.ToDisplayString()}+, "
                                + $"but this project targets {userVersion.ToDisplayString()}.",
                        SourceLine = attr.SourceLine,
                        SourceColumn = attr.SourceColumn,
                        EndLine = attr.SourceLine,
                        EndColumn = attr.NameEndColumn > 0 ? attr.NameEndColumn : attr.SourceColumn + attr.Name.Length,
                    });
                }
                if (schemaAttr?.RemovedIn is not null
                    && UnityVersion.TryParse(schemaAttr.RemovedIn, out var removedVersion)
                    && userVersion >= removedVersion)
                {
                    diags.Add(new ParseDiagnostic
                    {
                        Code = DiagnosticCodes.VersionMismatch,
                        Severity = ParseSeverity.Warning,
                        Message = $"Attribute '{attr.Name}' on '<{el.TagName}>' was removed in {removedVersion.ToDisplayString()}.",
                        SourceLine = attr.SourceLine,
                        SourceColumn = attr.SourceColumn,
                        EndLine = attr.SourceLine,
                        EndColumn = attr.NameEndColumn > 0 ? attr.NameEndColumn : attr.SourceColumn + attr.Name.Length,
                    });
                }
            }

            // Recurse into children
            foreach (var child in el.Children)
                WalkForVersionDiags(child, userVersion, backend, diags);
        }
    }

    // ── Project element / attribute helpers ───────────────────────────────────

    /// <summary>
    /// Builds the combined set of known element names from the static schema
    /// (built-in UITKX elements) and the dynamic workspace index (*Props.cs scan).
    /// Used for UITKX0105 unknown-element checks.
    /// </summary>
    private HashSet<string> BuildProjectElements(string? ownComponentName = null, string? backend = null)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in _schema.GetElements(backend).Keys)
            set.Add(key);
        foreach (var e in _index.KnownElements)
            set.Add(e);
        // Always include the component declared in the file being validated so that
        // recursive self-references and race conditions with the async scan never
        // produce a false-positive UITKX0105 "Unknown element" diagnostic.
        if (!string.IsNullOrEmpty(ownComponentName))
            set.Add(ownComponentName);
        return set;
    }

    /// <summary>
    /// Builds a map of element-name → valid attribute names for the UITKX0109
    /// unknown-attribute check.
    ///
    /// <para><b>Built-in (schema) elements</b> get the schema's per-element
    /// attributes plus <c>IntrinsicElementAttributes</c> (BaseProps surface)
    /// plus <c>StructuralAttributes</c> (<c>key</c>, <c>ref</c>) — i.e. the full
    /// set returned by <see cref="UitkxSchema.GetAttributesForElement"/>.</para>
    ///
    /// <para><b>User-component (workspace) elements</b> get <i>only</i> their
    /// declared parameters plus <c>StructuralAttributes</c>. Intrinsic-element
    /// attributes are deliberately <b>NOT</b> auto-allowed: a user component
    /// has no underlying <c>VisualElement</c>, so <c>style</c>/<c>onClick</c>/
    /// <c>extraProps</c>/etc. are structurally meaningless unless the author
    /// opts in by declaring the parameter explicitly. This mirrors React/Vue/
    /// Svelte (typed) component-prop semantics.</para>
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlyCollection<string>> BuildKnownAttributes(
        HashSet<string> projectElements,
        DirectiveSet? directives = null,
        string? localPath = null,
        string? backend = null
    )
    {
        var result = new Dictionary<string, IReadOnlyCollection<string>>(
            StringComparer.OrdinalIgnoreCase
        );

        // Built-in elements — schema per-element + intrinsic + structural.
        foreach (var tagName in _schema.GetElements(backend).Keys)
        {
            var attrs = _schema
                .GetAttributesForElement(tagName, backend)
                .Select(a => a.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            result[tagName] = attrs;
        }

        // User components (workspace elements) — declared params + structural ONLY.
        // Same-named components in different files are DIFFERENT components since
        // the ES-modules redesign (file-keyed namespaces), so a name with multiple
        // declarants must resolve per the CURRENT file's view, not first-wins.
        foreach (var tagName in _index.KnownElements)
        {
            if (result.ContainsKey(tagName))
                continue; // schema wins if there's a conflict

            var declarants = _index.GetAllElementInfo(tagName);
            IReadOnlyList<WorkspaceIndex.PropInfo> props = declarants.Count > 1
                ? ResolveVisibleProps(tagName, declarants, directives, localPath)
                : declarants.Count == 1 ? declarants[0].Props : _index.GetProps(tagName);
            var attrs = props.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sa in _schema.Root.StructuralAttributes)
                attrs.Add(sa.Name);
            result[tagName] = attrs;
        }

        return result;
    }

    /// <summary>
    /// Picks the declarant of <paramref name="tagName"/> that the CURRENT file
    /// actually sees, mirroring the SG's resolution order: the same file's own
    /// declaration, then a named/aliased/default <c>import</c> whose specifier
    /// resolves to a declarant (build-identical path rule via
    /// <see cref="LspHelpers.ResolveImportTarget"/>), then namespace visibility
    /// (the file's own effective namespace plus its <c>import "@Ns"</c> /
    /// <c>@using</c> lines). When resolution is ambiguous or impossible, the
    /// UNION of all declarants' props is returned — attribute validation must
    /// fail open, never invent an unknown-attribute error against the wrong
    /// file's component (the strict-import diagnostics own the missing-import
    /// case).
    /// </summary>
    private IReadOnlyList<WorkspaceIndex.PropInfo> ResolveVisibleProps(
        string tagName,
        IReadOnlyList<WorkspaceIndex.ElementInfo> declarants,
        DirectiveSet? directives,
        string? localPath)
    {
        if (!string.IsNullOrEmpty(localPath))
        {
            foreach (var d in declarants)
                if (PathsEqual(d.FilePath, localPath!))
                    return d.Props;
        }

        if (directives != null && !string.IsNullOrEmpty(localPath))
        {
            foreach (var imp in directives.Imports)
            {
                bool bindsTag = false;
                if (imp.IsDefault)
                {
                    bindsTag = string.Equals(imp.DefaultAlias, tagName, StringComparison.Ordinal);
                }
                else if (!imp.IsStar)
                {
                    for (int i = 0; i < imp.Names.Length; i++)
                    {
                        string local = imp.Aliases.Length > i && imp.Aliases[i] != null
                            ? imp.Aliases[i]!
                            : imp.Names[i];
                        if (string.Equals(local, tagName, StringComparison.Ordinal))
                        {
                            bindsTag = true;
                            break;
                        }
                    }
                }
                if (!bindsTag)
                    continue;

                string? target = LspHelpers.ResolveImportTarget(localPath!, imp.Specifier);
                if (target == null)
                    continue;
                foreach (var d in declarants)
                    if (PathsEqual(d.FilePath, target))
                        return d.Props;
            }

            var visibleNs = new HashSet<string>(StringComparer.Ordinal);
            string? ownNs = EffectiveNamespace.Resolve(
                directives.HasExplicitNamespace, directives.Namespace, localPath!,
                fileKeyed: !directives.UsesLegacySyntax);
            if (!string.IsNullOrEmpty(ownNs))
                visibleNs.Add(ownNs!);
            foreach (var u in directives.Usings)
            {
                if (u.StartsWith("static ", StringComparison.Ordinal) || u.Contains('='))
                    continue;
                visibleNs.Add(u);
            }

            WorkspaceIndex.ElementInfo? single = null;
            bool ambiguous = false;
            foreach (var d in declarants)
            {
                if (!d.FilePath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                    continue;
                var peer = TryParsePeerDirectives(d.FilePath);
                if (peer == null)
                    continue;
                string? peerNs = EffectiveNamespace.Resolve(
                    peer.HasExplicitNamespace, peer.Namespace, d.FilePath,
                    fileKeyed: !peer.UsesLegacySyntax);
                if (string.IsNullOrEmpty(peerNs) || !visibleNs.Contains(peerNs!))
                    continue;
                if (single == null)
                    single = d;
                else
                {
                    ambiguous = true;
                    break;
                }
            }
            if (single != null && !ambiguous)
                return single.Props;
        }

        var union = new List<WorkspaceIndex.PropInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in declarants)
            foreach (var p in d.Props)
                if (seen.Add(p.Name))
                    union.Add(p);
        return union;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
