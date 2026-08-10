using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ruitk.Language;
using Ruitk.Language.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace UitkxLanguageServer;

/// <summary>
/// Scans the workspace for <c>*Props.cs</c> files on startup and maintains
/// a set of known element names together with their prop declarations and
/// source locations.
///
/// Example: <c>ButtonProps.cs</c> → element name <c>Button</c> with props
/// <c>string text</c>, <c>Action onClick</c>, etc.
///
/// Used by diagnostics (UITKX0105), completion, hover, and go-to-definition.
/// </summary>
public sealed class WorkspaceIndex : IOnLanguageServerStarted
{
    // ── Inner types ──────────────────────────────────────────────────────────

    /// <summary>A single publicly-declared prop on a *Props class.</summary>
    public sealed class PropInfo
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";

        /// <summary>Cleaned XML doc text (tags stripped), or empty string.</summary>
        public string XmlDoc { get; init; } = "";

        /// <summary>1-based line number of the declaration in the source file.</summary>
        public int Line { get; init; }
    }

    /// <summary>All info gathered for one UITKX element (its <c>*Props</c> class).</summary>
    public sealed class ElementInfo
    {
        public string FilePath { get; init; } = "";

        /// <summary>1-based line of the class declaration.</summary>
        public int FileLine { get; init; }

        /// <summary>All props including inherited (resolved).</summary>
        public List<PropInfo> Props { get; init; } = new();

        /// <summary>Only the props declared directly on this element's Props class.</summary>
        public List<PropInfo> OwnProps { get; init; } = new();

        /// <summary>Base element name if the Props class extends another *Props class.</summary>
        public string? BaseElement { get; init; }
    }

    // ── State ────────────────────────────────────────────────────────────────

    private readonly HashSet<string> _elements = new(StringComparer.Ordinal);

    // ── Multi-valued element index (TECH_DEBT_V2 #21 fix, 2026-05-18) ────────
    //
    // The outer key is the element name; the inner map keys by absolute file
    // path so the same `component <Name>` declared in two .uitkx files lives
    // as two distinct entries. The previous one-to-one `Dictionary<string,
    // ElementInfo>` silently overwrote on the second indexed file and then
    // EvictElementsFromFile would delete the shared name entirely on the
    // winner's next edit — the exact bug surfaced by the PrettyUi folder
    // copy+rename refactor (see TECH_DEBT_20_21_22_RESOLUTION_PLAN.md §2).
    //
    // The inner map is a SortedDictionary keyed by full path (case-insensitive
    // ordering) so the deterministic-first-match used by TryGetElementInfo
    // is stable across process restarts and OS file enumeration order.
    //
    // All access goes through the public accessor methods on this class; no
    // external code touches the dictionary directly (verified by workspace
    // grep before the shape change — 19 hits, all internal).
    private readonly Dictionary<string, SortedDictionary<string, ElementInfo>> _elementInfo =
        new(StringComparer.Ordinal);

    // Reverse map: file path -> set of element names currently contributed by
    // that file. Maintained alongside _elementInfo / _elements so that a file
    // re-index (after edit / rename) or deletion can deterministically evict
    // names the file used to declare but no longer does. Without this, when a
    // file's `component Foo { ... }` is edited to `component Bar { ... }`,
    // the `Foo` entry stays in _elementInfo forever pointing at this file,
    // breaking go-to-definition / hover / diagnostics for any later file
    // that legitimately declares `component Foo`.
    private readonly Dictionary<string, HashSet<string>> _elementsByFile =
        new(StringComparer.OrdinalIgnoreCase);

    // Set of .uitkx file paths whose top-level declarations export members
    // (values/utils/hooks). Maintained incrementally by IndexUitkxFile and consumed by
    // RoslynHost.FindPeerUitkxFiles to enable cross-directory symbol resolution
    // for member references like `Theme.SidebarWidth` when `Theme` lives
    // in a different folder than the consumer .uitkx file.
    private readonly HashSet<string> _moduleHookFiles = new(StringComparer.OrdinalIgnoreCase);

    // Exported top-level declarations per .uitkx file (import/export grammar, leg 3): file path ->
    // its exported component/hook/module names + kinds. Populated by IndexUitkxFile (parsed, so it
    // tracks the `export` flag + hook/module names the regex index does not) and evicted with the
    // file. Consumed by GetPeerExports to feed the strict reference detector for LIVE 2305/2307.
    private readonly Dictionary<string, List<(string Name, StrictImportDetector.ExportKind Kind)>> _exportsByFile =
        new(StringComparer.OrdinalIgnoreCase);

    // Import declarations per .uitkx file (import/export grammar, leg 3): file path -> its imports
    // (specifier + names). Feeds the value-import cycle detector (UITKX2306). Same lifecycle as
    // _exportsByFile (populated in IndexUitkxFile, evicted with the file).
    private readonly Dictionary<string, List<(string Specifier, string[] Names)>> _importsByFile =
        new(StringComparer.OrdinalIgnoreCase);

    // Workspace-wide set of all indexed *.cs file paths. Consumed by
    // RoslynHost.FindCompanionFiles to load partial-class members and types
    // declared in any .cs file belonging to the same asmdef as the .uitkx
    // being opened, not just the sibling-folder ones. AsmdefResolver enforces
    // the asmdef boundary at the call site so this set can be a flat union.
    private readonly HashSet<string> _allCsFiles = new(StringComparer.OrdinalIgnoreCase);

    private readonly ReaderWriterLockSlim _lock = new();

    // ── Patterns ─────────────────────────────────────────────────────────────

    // Matches:  "class FooProps"  "record FooProps"  "struct FooProps"
    private static readonly Regex s_classPattern = new(
        @"\b(?:class|record|struct)\s+(\w+)Props\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // Matches:  "class Props"  "record Props"  "struct Props"  (no prefix — component name = filename stem)
    private static readonly Regex s_nestedPropsPattern = new(
        @"\b(?:class|record|struct)\s+Props\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // Detects a base *Props class in the inheritance list.
    // E.g.  ": BaseProps"  ": BaseProps, ISerializable"
    private static readonly Regex s_basePropsPattern = new(
        @":\s*(?:[\w.]+\s*,\s*)*?([A-Z]\w{1,})Props\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // Matches public property declarations with a Pascal-case name.
    // E.g.  "public string Text {"  "public Action<int>? OnClick {"
    // Also matches expression-bodied properties (U-34):
    // "public string Foo => "bar";"  "public int Count => _items.Count;"
    private static readonly Regex s_propPattern = new(
        @"^\s*public\s+(?<type>[\w<>\[\],\s\?]+?)\s+(?<name>[A-Z][A-Za-z0-9_]*)\s*(?:\{|=>)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    // Matches component declarations inside a .uitkx file, BOTH grammars
    // (ES-modules campaign, M5):
    //   component FooBar {                       (legacy wrapper)
    //   export VirtualNode FooBar(...) {         (plain declaration)
    private static readonly Regex s_uitkxComponentPattern = new(
        @"^(?:export\s+)?(?:component|(?:global::)?(?:[\w.]+\.)?VirtualNode)\s+([A-Z][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline
    );

    // Extended declaration pattern that also captures the optional parameter list.
    // Group "name" = component name.  Group "params" = raw param list if present.
    //   component ShowcaseTopBar(string inputText = "", Action? onSetText = null)
    //   export VirtualNode ShowcaseTopBar(string inputText = "")
    private static readonly Regex s_uitkxDeclPattern = new(
        @"^(?:export\s+)?(?:component|(?:global::)?(?:[\w.]+\.)?VirtualNode)\s+(?<name>[A-Z][A-Za-z0-9_]*)(?:\s*\((?<params>[^)]*)\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline
    );

    // Matches a top-level declaration that contributes non-component symbols:
    //   export <Type> useX( / export <Type> Name = ...    (plain hook/util/value declarations)
    // Multiline so `^` matches each logical line start; the grammar prohibits leading
    // whitespace before a top-level declaration head. (The legacy module/hook wrapper
    // alternates were dropped with the grammar in 0.16.0.)
    private static readonly Regex s_uitkxModuleOrHookPattern = new(
        @"^export\s+(?![A-Za-z_]\w*\s+from\b)[\w<>\[\],\s\.\?\(\)]+?\s[A-Za-z_]\w*\s*(?:<[\w,\s]+>)?\s*[=\(]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline
    );

    // ── IOnLanguageServerStarted ─────────────────────────────────────────────

    /// <summary>
    /// Fired once on the thread-pool after the initial <see cref="ScanDirectory"/> completes.
    /// Subscribe to re-validate open documents that were diagnosed before the scan finished.
    /// </summary>
    public event Action? ScanCompleted;

    /// <summary>
    /// Fired after <see cref="Refresh"/> updates the index for a single file.
    /// Subscribers can re-validate open documents whose diagnostics may now be stale.
    /// </summary>
    public event Action? IndexChanged;

    public Task OnStarted(ILanguageServer server, CancellationToken cancellationToken)
    {
        string? root = null;
        try
        {
            var rawUri = server.ClientSettings.RootUri;
            var rootUri = rawUri?.ToString();
            ServerLog.Log(
                $"WorkspaceIndex.OnStarted: RootUri={rootUri ?? "(null)"}, "
                + $"RootPath={server.ClientSettings.RootPath ?? "(null)"}");
            if (!string.IsNullOrEmpty(rootUri))
                root = new Uri(rootUri).LocalPath;
        }
        catch (Exception ex)
        {
            ServerLog.Log($"WorkspaceIndex.OnStarted: URI parse error: {ex.Message}");
        }

        if (string.IsNullOrEmpty(root))
            root = server.ClientSettings.RootPath;

        if (!string.IsNullOrEmpty(root))
        {
            ServerLog.Log($"WorkspaceIndex.OnStarted: starting scan from '{root}'");
            _ = Task.Run(
                () =>
                {
                    ScanDirectory(root);
                    ScanCompleted?.Invoke();
                },
                cancellationToken
            );
        }
        else
        {
            ServerLog.Log("WorkspaceIndex.OnStarted: no root — scan deferred until EnsureScanned() is called");
        }

        return Task.CompletedTask;
    }

    private volatile bool _hasScanned;

    /// <summary>
    /// Returns <c>true</c> once the initial background workspace scan has
    /// completed.  Before this point the element index is incomplete and
    /// element-existence checks (UITKX0105) should be suppressed to avoid
    /// transient false-positives.
    /// </summary>
    public bool HasCompletedInitialScan => _hasScanned;

    /// <summary>
    /// Scans the workspace from <paramref name="rootPath"/> if no scan has
    /// completed yet.  Called as a fallback by <see cref="Roslyn.RoslynHostStartup"/>
    /// when VS2022 does not provide <c>rootUri</c> / <c>rootPath</c> via the
    /// standard LSP <c>initialize</c> params but the workspace root is known
    /// through other means (e.g. the Roslyn host startup sequence).
    /// </summary>
    public void EnsureScanned(string rootPath)
    {
        if (_hasScanned || string.IsNullOrEmpty(rootPath))
            return;

        ServerLog.Log($"WorkspaceIndex.EnsureScanned: deferred scan from '{rootPath}'");
        ScanDirectory(rootPath);
        ScanCompleted?.Invoke();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>A snapshot of all element names currently in the index.</summary>
    public HashSet<string> KnownElements
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return new HashSet<string>(_elements, StringComparer.Ordinal);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Snapshot of every indexed .uitkx file path that declares at least one
    /// top-level <c>module</c> or <c>hook</c>. Used by the Roslyn host to load
    /// cross-directory peers into the workspace so module-member references
    /// (e.g. <c>Theme.SidebarWidth</c>) resolve from any consumer file.
    /// </summary>
    public IReadOnlyList<string> GetModuleAndHookFiles()
    {
        _lock.EnterReadLock();
        try
        {
            return _moduleHookFiles.ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// The exported declarations of every indexed <c>.uitkx</c> file EXCEPT
    /// <paramref name="currentFilePath"/> — the peer export table the strict reference detector
    /// consumes for live 2305/2307 (import/export grammar, leg 3). When
    /// <paramref name="owningAsmdefDir"/> is non-null, only peers under that directory are returned
    /// (imports are asmdef-scoped — a directory-prefix approximation of the boundary).
    /// </summary>
    public IReadOnlyList<StrictImportDetector.PeerExport> GetPeerExports(
        string currentFilePath, string? owningAsmdefDir)
    {
        var result = new List<StrictImportDetector.PeerExport>();
        string self = NormPath(currentFilePath);
        string? scope = owningAsmdefDir is null ? null : NormPath(owningAsmdefDir);

        _lock.EnterReadLock();
        try
        {
            foreach (var kv in _exportsByFile)
            {
                string file = NormPath(kv.Key);
                if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (scope != null && !file.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var (name, kind) in kv.Value)
                    result.Add(new StrictImportDetector.PeerExport(name, kv.Key, kind));
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
        return result;
    }

    /// <summary>
    /// Import declarations of every indexed <c>.uitkx</c> file under <paramref name="owningAsmdefDir"/>
    /// (all files when null): <c>(sourceFile, specifier, importedNames)</c>. Feeds the value-import
    /// cycle detector (UITKX2306).
    /// </summary>
    public IReadOnlyList<(string File, string Specifier, IReadOnlyList<string> Names)> GetImportEdges(
        string? owningAsmdefDir)
    {
        var result = new List<(string, string, IReadOnlyList<string>)>();
        string? scope = owningAsmdefDir is null ? null : NormPath(owningAsmdefDir);
        _lock.EnterReadLock();
        try
        {
            foreach (var kv in _importsByFile)
            {
                if (scope != null && !NormPath(kv.Key).StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var (specifier, names) in kv.Value)
                    result.Add((kv.Key, specifier, names));
            }
        }
        finally { _lock.ExitReadLock(); }
        return result;
    }

    /// <summary>The kind an <paramref name="name"/> is exported as by <paramref name="filePath"/>, or null.</summary>
    public StrictImportDetector.ExportKind? GetExportKind(string filePath, string name)
    {
        string t = NormPath(filePath);
        _lock.EnterReadLock();
        try
        {
            foreach (var kv in _exportsByFile)
            {
                if (!string.Equals(NormPath(kv.Key), t, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var (n, kind) in kv.Value)
                    if (string.Equals(n, name, StringComparison.Ordinal))
                        return kind;
            }
        }
        finally { _lock.ExitReadLock(); }
        return null;
    }

    private static string NormPath(string p) => p.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Snapshot of every indexed .cs file path. Used by the Roslyn host to
    /// load companion .cs files from anywhere in the same asmdef as the
    /// .uitkx being opened, not just the sibling folder. The asmdef boundary
    /// is enforced at the call site via <see cref="AsmdefResolver"/>.
    /// </summary>
    public IReadOnlyList<string> GetAllCsFiles()
    {
        _lock.EnterReadLock();
        try
        {
            return _allCsFiles.ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns <c>true</c> if any element names have been indexed (i.e. the
    /// background scan has completed at least partially).
    /// </summary>
    public bool IsReady
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _elements.Count > 0;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Returns the props declared on the element, or an empty list.</summary>
    public IReadOnlyList<PropInfo> GetProps(string elementName)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_elementInfo.TryGetValue(elementName, out var bucket) || bucket.Count == 0)
                return Array.Empty<PropInfo>();
            return ResolveProps(bucket.First().Value);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Returns the <see cref="ElementInfo"/> for the element, or <c>null</c>.</summary>
    /// <remarks>
    /// When multiple files declare the same element name, returns the
    /// deterministic-first match (sorted by absolute file path). Callers that
    /// need to enumerate every declarant should call <see cref="GetAllElementInfo"/>.
    /// </remarks>
    public ElementInfo? TryGetElementInfo(string elementName)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_elementInfo.TryGetValue(elementName, out var bucket) || bucket.Count == 0)
                return null;
            return BuildResolved(bucket.First().Value);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns every <see cref="ElementInfo"/> that contributes to <paramref name="elementName"/>,
    /// sorted by absolute file path. Used by <see cref="RenameHandler"/> so a rename
    /// touches every file that declares the same component name, and by
    /// <see cref="DiagnosticsPublisher"/> so the duplicate-component diagnostic
    /// (UITKX0113) fires once per declarant.
    /// </summary>
    public IReadOnlyList<ElementInfo> GetAllElementInfo(string elementName)
    {
        _lock.EnterReadLock();
        try
        {
            if (!_elementInfo.TryGetValue(elementName, out var bucket) || bucket.Count == 0)
                return Array.Empty<ElementInfo>();
            var result = new List<ElementInfo>(bucket.Count);
            foreach (var info in bucket.Values)
                result.Add(BuildResolved(info));
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns every (name, file path) pair where the same element name is
    /// declared in more than one file. Pairs are returned only when the count
    /// for a name exceeds one. Used by <see cref="DiagnosticsPublisher"/> to
    /// publish UITKX0113 duplicate-component-declaration warnings.
    /// </summary>
    public IReadOnlyList<(string Name, IReadOnlyList<string> FilePaths)> GetDuplicateDeclarations()
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<(string, IReadOnlyList<string>)>();
            foreach (var kv in _elementInfo)
            {
                if (kv.Value.Count <= 1)
                    continue;
                var paths = new List<string>(kv.Value.Count);
                foreach (var path in kv.Value.Keys)
                    paths.Add(path);
                result.Add((kv.Key, paths));
            }
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    // Internal helper: returns a wrapped ElementInfo with Props (resolved
    // inheritance) and OwnProps (declared-only). Must be called under the
    // read lock.
    private ElementInfo BuildResolved(ElementInfo info)
    {
        var resolved = ResolveProps(info);
        return new ElementInfo
        {
            FilePath = info.FilePath,
            FileLine = info.FileLine,
            Props = resolved,
            OwnProps = info.Props,
            BaseElement = info.BaseElement,
        };
    }

    /// <summary>
    /// Returns all props for the element, including those inherited from base *Props classes.
    /// Must be called under the read lock.
    /// </summary>
    private List<PropInfo> ResolveProps(ElementInfo info, int depth = 0)
    {
        ElementInfo? baseInfo = null;
        if (
            depth <= 5
            && info.BaseElement is not null
            && _elementInfo.TryGetValue(info.BaseElement, out var baseBucket)
            && baseBucket.Count > 0
        )
        {
            // Deterministic-first base pick when the base name is itself
            // multi-valued. The same selection rule as TryGetElementInfo.
            baseInfo = baseBucket.First().Value;
        }

        if (baseInfo is null)
            return info.Props;

        var baseProps = ResolveProps(baseInfo, depth + 1);
        var ownNames = new HashSet<string>(info.Props.Select(p => p.Name), StringComparer.Ordinal);
        var merged = new List<PropInfo>(info.Props);
        foreach (var bp in baseProps)
        {
            if (!ownNames.Contains(bp.Name))
                merged.Add(bp);
        }
        return merged;
    }

    /// <summary>
    /// Re-index a single file that was added, changed, or deleted.
    /// Called by <see cref="DiagnosticsPublisher"/> on workspace file-change
    /// notifications.
    /// </summary>
    public void Refresh(string filePath)
    {
        // Skip files inside tilde-suffixed folders (dist~, Samples~, etc.)
        if (IsInsideTildeFolder(filePath))
            return;

        if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(filePath))
            {
                _lock.EnterWriteLock();
                try { _allCsFiles.Remove(filePath); }
                finally { _lock.ExitWriteLock(); }

                // Evict element names contributed by the now-deleted .cs file.
                // Without this, deleting a *Props.cs file leaves its element
                // entries dangling in _elementInfo until the next process
                // restart. ScanDirectory below re-indexes survivors but never
                // touches the deleted file's stale entries.
                EvictElementsFromFile(filePath);

                var dir = Path.GetDirectoryName(filePath);
                if (dir is not null)
                    ScanDirectory(dir);
                return;
            }
            IndexFile(filePath);
            IndexChanged?.Invoke();
        }
        else if (filePath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(filePath))
            {
                _lock.EnterWriteLock();
                try { _moduleHookFiles.Remove(filePath); }
                finally { _lock.ExitWriteLock(); }

                // Evict element names contributed by the now-deleted file so
                // go-to-definition / hover / diagnostics don't keep pointing
                // at a phantom path. Without this, after a delete the index
                // entry survives until the next process restart.
                EvictElementsFromFile(filePath);
                IndexChanged?.Invoke();
                return;
            }
            IndexUitkxFile(filePath);
            IndexChanged?.Invoke();
        }
    }

    // ── Scanning ─────────────────────────────────────────────────────────────

    private void ScanDirectory(string rootPath)
    {
        try
        {
            ServerLog.Log($"WorkspaceIndex: scanning '{rootPath}' for *.cs and *.uitkx files…");
            int count = 0;
            foreach (
                var file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            )
            {
                if (IsInsideTildeFolder(file)) continue;
                IndexFile(file);
                count++;
            }
            ServerLog.Log(
                $"WorkspaceIndex: indexed {count} Props file(s) → {_elements.Count} element name(s)."
            );

            // Also scan .uitkx files to discover component names
            // (e.g. `component FooBar { … }`).
            int uitkxCount = 0;
            foreach (
                var file in Directory.EnumerateFiles(
                    rootPath,
                    "*.uitkx",
                    SearchOption.AllDirectories
                )
            )
            {
                if (IsInsideTildeFolder(file)) continue;
                IndexUitkxFile(file);
                uitkxCount++;
            }
            ServerLog.Log(
                $"WorkspaceIndex: indexed {uitkxCount} .uitkx file(s) → {_elements.Count} total element name(s)."
            );

            _hasScanned = true;
        }
        catch (Exception ex)
        {
            ServerLog.Log($"WorkspaceIndex scan error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if any path segment ends with '~' (Unity's convention for
    /// ignored folders like dist~, Samples~, GeneratedPreview~, etc.).
    /// Files in these folders should not be indexed.
    /// </summary>
    private static bool IsInsideTildeFolder(string filePath)
    {
        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var seg in segments)
        {
            if (seg.Length > 1 && seg[seg.Length - 1] == '~')
                return true;
        }
        return false;
    }

    /// <summary>
    /// Extracts component names declared in a <c>.uitkx</c> file and adds them to the index.
    /// Handles the function-style syntax (<c>component FooBar { … }</c>).
    /// When a parameter list is present (<c>component FooBar(type name = default, …)</c>)
    /// the parameters are also stored as props so that UITKX0109 (unknown attribute) does
    /// not fire for valid attributes on those components.
    /// </summary>
    private void IndexUitkxFile(string filePath)
    {
        // Drop every element name this file PREVIOUSLY contributed so that a
        // rename like `component Foo { }` -> `component Bar { }` doesn't leave
        // a stale `Foo` entry pointing at this file. CommitElement re-adds
        // each name found in the new content below.
        EvictElementsFromFile(filePath);

        try
        {
            string content = File.ReadAllText(filePath);

            // Blank comments + string/char literals (offset-preserving) before the regex
            // passes so a declaration-shaped line inside a /* */ block comment can't index
            // a phantom element. The col-0 anchors stay; the parser below still reads the
            // ORIGINAL content.
            string scannable = StrictImportDetector.ScrubNonCode(content);

            bool hasModuleOrHook = s_uitkxModuleOrHookPattern.IsMatch(scannable);

            // Exported declarations (import/export grammar): parse with the real directive parser so
            // the `export` flag + hook/module names (which the regex index doesn't capture) are known.
            var exports = new List<(string Name, StrictImportDetector.ExportKind Kind)>();
            var imports = new List<(string Specifier, string[] Names)>();
            try
            {
                var diags = new List<ParseDiagnostic>();
                var ds = DirectiveParser.Parse(content, filePath, diags);
                if (!ds.ComponentDeclarations.IsDefaultOrEmpty)
                    foreach (var c in ds.ComponentDeclarations)
                        if (c.IsExported) exports.Add((c.Name, StrictImportDetector.ExportKind.Component));
                // Member declarations (ES-modules campaign, M5): hooks map to the Hook
                // kind; values/utils map to Module (the closest reference-shape the strict
                // detector scans — the kind mostly drives import-brace completion + 2305 hints).
                if (!ds.MemberDeclarations.IsDefaultOrEmpty)
                    foreach (var mem in ds.MemberDeclarations)
                        if (mem.IsExported)
                            exports.Add((mem.Name, mem.Kind == DeclKind.Hook
                                ? StrictImportDetector.ExportKind.Hook
                                : StrictImportDetector.ExportKind.Module));
                if (!ds.Imports.IsDefaultOrEmpty)
                    foreach (var imp in ds.Imports)
                        imports.Add((imp.Specifier, imp.Names.ToArray()));
            }
            catch { /* parse hiccup — leave this file with no exports/imports */ }

            _lock.EnterWriteLock();
            try
            {
                if (hasModuleOrHook) _moduleHookFiles.Add(filePath);
                else _moduleHookFiles.Remove(filePath);

                if (exports.Count > 0) _exportsByFile[filePath] = exports;
                else _exportsByFile.Remove(filePath);

                if (imports.Count > 0) _importsByFile[filePath] = imports;
                else _importsByFile.Remove(filePath);
            }
            finally { _lock.ExitWriteLock(); }

            foreach (Match m in s_uitkxDeclPattern.Matches(scannable))
            {
                string componentName = m.Groups["name"].Value;
                if (string.IsNullOrEmpty(componentName))
                    continue;

                // Find the 1-based line number of the match
                int lineNumber = 1;
                for (int i = 0; i < m.Index && i < scannable.Length; i++)
                    if (scannable[i] == '\n')
                        lineNumber++;

                // Parse function-style params if present.
                // Each param looks like:  type paramName  or  type paramName = default
                // Strategy: split by comma, then for each token split by "=" and take the
                // last whitespace-separated word from the part before "=".
                var props = new List<PropInfo>();
                string paramsText = m.Groups["params"].Value;
                if (!string.IsNullOrWhiteSpace(paramsText))
                {
                    foreach (string param in paramsText.Split(','))
                    {
                        string beforeEquals = param.Split('=')[0].Trim();
                        string[] tokens = beforeEquals.Split(
                            new[] { ' ', '\t', '\n', '\r' },
                            StringSplitOptions.RemoveEmptyEntries
                        );
                        if (tokens.Length >= 2)
                        {
                            props.Add(
                                new PropInfo
                                {
                                    Name = tokens[tokens.Length - 1],
                                    Type = string.Join(" ", tokens, 0, tokens.Length - 1),
                                    Line = lineNumber,
                                }
                            );
                        }
                    }
                }

                CommitElement(filePath, componentName, lineNumber, props);
            }
        }
        catch
        {
            // File read errors are silently ignored — best-effort index.
        }
    }

    private void IndexFile(string filePath)
    {
        // Workspace-wide registry write happens BEFORE invoking the .cs parser
        // so the brief write lock cannot nest with CommitElement's write lock
        // (ReaderWriterLockSlim default policy is NoRecursion).
        _lock.EnterWriteLock();
        try { _allCsFiles.Add(filePath); }
        finally { _lock.ExitWriteLock(); }

        // Drop element names this file PREVIOUSLY contributed so a renamed /
        // removed *Props class can't keep its old name pointing at this file.
        // CommitElement re-adds names found in the new content below.
        EvictElementsFromFile(filePath);

        try
        {
            var lines = File.ReadAllLines(filePath);
            string? currentElement = null;
            string? currentBase = null;
            int currentLine = 0;
            var currentProps = new List<PropInfo>();
            var xmlBuf = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var trimmed = raw.Trim();

                // ── Class / record / struct *Props declaration ────────────
                var classMatch = s_classPattern.Match(trimmed);
                if (classMatch.Success)
                {
                    if (currentElement is not null)
                        CommitElement(
                            filePath,
                            currentElement,
                            currentLine,
                            currentProps,
                            currentBase
                        );

                    currentElement = classMatch.Groups[1].Value;
                    currentLine = i + 1;
                    currentProps = new List<PropInfo>();
                    xmlBuf.Clear();

                    // Detect base *Props class (e.g. ": BaseProps")
                    currentBase = null;
                    var baseMatch = s_basePropsPattern.Match(
                        trimmed,
                        classMatch.Index + classMatch.Length
                    );
                    if (baseMatch.Success)
                        currentBase = baseMatch.Groups[1].Value;
                    continue;
                }

                // ── Nested "class Props" without prefix — use filename stem ─
                var nestedPropsMatch = s_nestedPropsPattern.Match(trimmed);
                if (nestedPropsMatch.Success)
                {
                    if (currentElement is not null)
                        CommitElement(
                            filePath,
                            currentElement,
                            currentLine,
                            currentProps,
                            currentBase
                        );

                    currentElement = Path.GetFileNameWithoutExtension(filePath);
                    currentLine = i + 1;
                    currentProps = new List<PropInfo>();
                    currentBase = null;
                    xmlBuf.Clear();
                    continue;
                }

                // ── XML doc comment ───────────────────────────────────────
                if (trimmed.StartsWith("///"))
                {
                    xmlBuf.Add(trimmed.Length > 3 ? trimmed[3..].TrimStart() : "");
                    continue;
                }

                // ── Public property declaration ───────────────────────────
                var propMatch = s_propPattern.Match(raw);
                if (propMatch.Success && currentElement is not null)
                {
                    currentProps.Add(
                        new PropInfo
                        {
                            Name = propMatch.Groups["name"].Value,
                            Type = propMatch.Groups["type"].Value.Trim(),
                            XmlDoc = BuildDoc(xmlBuf),
                            Line = i + 1,
                        }
                    );
                    xmlBuf.Clear();
                    continue;
                }

                // Any non-comment, non-empty line resets the doc buffer.
                if (!string.IsNullOrWhiteSpace(trimmed))
                    xmlBuf.Clear();
            }

            if (currentElement is not null)
                CommitElement(filePath, currentElement, currentLine, currentProps, currentBase);
        }
        catch
        {
            // File read errors are silently ignored — best-effort index.
        }
    }

    private void CommitElement(
        string filePath,
        string elementName,
        int fileLine,
        List<PropInfo> props,
        string? baseElement = null
    )
    {
        var info = new ElementInfo
        {
            FilePath = filePath,
            FileLine = fileLine,
            Props = new List<PropInfo>(props),
            BaseElement = baseElement,
        };
        _lock.EnterWriteLock();
        try
        {
            _elements.Add(elementName);

            // Multi-valued shape: get-or-create the per-name bucket, then
            // assign the per-file slot. The same file re-indexing itself
            // overwrites its own slot; a SECOND file declaring the same name
            // gets a separate slot under its own path key.
            if (!_elementInfo.TryGetValue(elementName, out var bucket))
            {
                bucket = new SortedDictionary<string, ElementInfo>(
                    StringComparer.OrdinalIgnoreCase);
                _elementInfo[elementName] = bucket;
            }
            bucket[filePath] = info;

            if (!_elementsByFile.TryGetValue(filePath, out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                _elementsByFile[filePath] = names;
            }
            names.Add(elementName);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes every element name currently attributed to <paramref name="filePath"/>
    /// from the index but only if the entry's <c>FilePath</c> still matches
    /// (i.e. some other file hasn't already claimed the same name in the
    /// meantime). Call BEFORE re-indexing a file's new content or when the
    /// file is deleted; combined with <see cref="CommitElement"/>'s tracking,
    /// this gives the index correct rename / content-change semantics without
    /// requiring a workspace rescan.
    /// </summary>
    private void EvictElementsFromFile(string filePath)
    {
        _lock.EnterWriteLock();
        try
        {
            _exportsByFile.Remove(filePath); // import/export grammar: drop this file's exports too
            _importsByFile.Remove(filePath);

            if (!_elementsByFile.TryGetValue(filePath, out var names))
                return;

            foreach (var name in names)
            {
                if (!_elementInfo.TryGetValue(name, out var bucket))
                    continue;

                // Multi-valued shape: drop only THIS file's slot. The name
                // survives in the global element set when other files still
                // declare it. This is the core of the TECH_DEBT #21 fix —
                // the old single-valued code deleted the name entirely on
                // the first evict, kicking the still-on-disk declarant out
                // of completion/hover for no reason.
                if (bucket.Remove(filePath) && bucket.Count == 0)
                {
                    _elementInfo.Remove(name);
                    _elements.Remove(name);
                }
            }
            _elementsByFile.Remove(filePath);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildDoc(List<string> xmlBuf)
    {
        if (xmlBuf.Count == 0)
            return "";
        var raw = string.Join(" ", xmlBuf);
        return Regex.Replace(raw, @"<[^>]+>", "").Trim();
    }
}
