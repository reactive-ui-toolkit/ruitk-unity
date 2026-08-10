using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Ruitk.Language.Parser;

using LspLocation = OmniSharp.Extensions.LanguageServer.Protocol.Models.Location;
using Ruitk.Language.Roslyn;
using UitkxLanguageServer.Roslyn;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace UitkxLanguageServer;

/// <summary>
/// Handles <c>textDocument/references</c> (Shift+F12) for <c>.uitkx</c> files.
///
/// <para><b>Two paths (mirrors RenameHandler):</b></para>
/// <list type="bullet">
///   <item><b>C# path</b> — cursor is inside a source-mapped C# region.
///   Resolves the symbol via Roslyn, uses <see cref="SymbolFinder"/> to find
///   all references, then maps locations back to <c>.uitkx</c> coordinates
///   via the <see cref="SourceMap"/>.</item>
///   <item><b>Component-name path</b> — cursor is on a tag name or
///   <c>component</c> declaration.  Workspace-wide text scan for all tag
///   usages and declarations.</item>
/// </list>
/// </summary>
public sealed class ReferencesHandler : IReferencesHandler
{
    private readonly DocumentStore _store;
    private readonly WorkspaceIndex _index;
    private readonly RoslynHost _roslynHost;

    public ReferencesHandler(DocumentStore store, WorkspaceIndex index, RoslynHost roslynHost)
    {
        _store = store;
        _index = index;
        _roslynHost = roslynHost;
    }

    public ReferenceRegistrationOptions GetRegistrationOptions(
        ReferenceCapability capability,
        ClientCapabilities clientCapabilities
    ) =>
        new ReferenceRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.uitkx" },
                new TextDocumentFilter { Pattern = "**/*.cs" }
            ),
        };

    public async Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken ct)
    {
        var localPath = LspHelpers.UriToPath(request.TextDocument.Uri);
        if (localPath == null)
            return null;

        if (!LspHelpers.TryGetText(_store, request.TextDocument.Uri, localPath, out var text))
            return null;

        bool includeDeclaration = request.Context?.IncludeDeclaration ?? true;

        // ── .cs companion file ───────────────────────────────────────────
        if (localPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return await FindReferencesInCsAsync(localPath, text, request.Position, includeDeclaration, ct)
                .ConfigureAwait(false);

        var parseResult = LspHelpers.ParseText(text, localPath);
        var offset = LspHelpers.ToOffset(text, request.Position);

        await _roslynHost
            .EnsureReadyAsync(localPath, text, parseResult, ct)
            .ConfigureAwait(false);

        // ── C# path: cursor in a source-mapped C# region ─────────────────
        var vdoc = _roslynHost.GetVirtualDocument(localPath);
        if (vdoc != null)
        {
            var virtualResult = vdoc.Map.ToVirtualOffset(offset);
            if (virtualResult.HasValue)
            {
                var roslynDoc = _roslynHost.GetRoslynDocument(localPath);
                if (roslynDoc != null)
                {
                    var symbol = LspHelpers.ResolveSymbol(
                        roslynDoc, virtualResult.Value.VirtualOffset, ct);

                    if (symbol != null && LspHelpers.IsUserSymbol(symbol))
                    {
                        ServerLog.Log(
                            $"[References] C# symbol: '{symbol.Name}' ({symbol.Kind})");

                        var peerVDocs = _roslynHost.GetPeerVirtualDocuments(localPath);
                        var locations = CollectRoslynReferences(
                            roslynDoc, symbol, localPath, text, vdoc,
                            includeDeclaration, ct, peerVDocs);

                        // For methods that match a peer hook, also collect
                        // text-based hook references across peer .uitkx files.
                        if (symbol is IMethodSymbol && peerVDocs != null && peerVDocs.Count > 0)
                        {
                            CollectHookReferences(
                                symbol.Name, localPath, includeDeclaration, locations);
                        }

                        // For fields/properties (module styles), also collect
                        // text-based references across peer .uitkx files.
                        if ((symbol is IFieldSymbol || symbol is IPropertySymbol)
                            && peerVDocs != null && peerVDocs.Count > 0)
                        {
                            CollectPeerSymbolReferences(
                                symbol.Name, localPath, includeDeclaration, locations);
                        }

                        ServerLog.Log($"[References] C# path: {locations.Count} location(s)");
                        return new LocationContainer(locations);
                    }
                }
            }
        }

        // ── Component-name path: tag name or component declaration ────────
        var (word, _, _) = LspHelpers.GetWordAtOffset(text, offset);
        if (!string.IsNullOrEmpty(word))
        {
            bool isDeclaration =
                parseResult.Directives.IsFunctionStyle
                && word == parseResult.Directives.ComponentName;

            bool isTagRef = false;
            if (!isDeclaration && LspHelpers.IsOnTagName(text, offset, word))
            {
                var elementInfo = _index.TryGetElementInfo(word);
                isTagRef = elementInfo != null
                    && elementInfo.FilePath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase);
            }

            if (isDeclaration || isTagRef)
            {
                ServerLog.Log($"[References] Component: '{word}'");
                var locations = CollectComponentReferences(word, includeDeclaration);
                ServerLog.Log($"[References] Component path: {locations.Count} location(s)");
                return new LocationContainer(locations);
            }

            // ── Member-name path: cursor on a value/util/hook declaration ──
            if (!parseResult.Directives.MemberDeclarations.IsDefaultOrEmpty)
            {
                foreach (var member in parseResult.Directives.MemberDeclarations)
                {
                    if (word != member.Name)
                        continue;
                    var locations = new List<LspLocation>();
                    if (member.Kind == DeclKind.Hook)
                    {
                        ServerLog.Log($"[References] Hook member: '{word}'");
                        CollectHookReferences(word, localPath, includeDeclaration, locations);
                    }
                    else
                    {
                        ServerLog.Log($"[References] Member: '{word}'");
                        CollectMemberReferences(word, localPath, includeDeclaration, locations);
                    }
                    ServerLog.Log($"[References] Member path: {locations.Count} location(s)");
                    return new LocationContainer(locations);
                }
            }
        }

        return null;
    }

    // ── .cs companion file references ─────────────────────────────────────────

    private async Task<LocationContainer?> FindReferencesInCsAsync(
        string csPath, string text, Position position,
        bool includeDeclaration, CancellationToken ct)
    {
        var refreshedDoc = _roslynHost.RefreshCompanionDocument(csPath, text);
        if (refreshedDoc == null)
            return null;

        var result = _roslynHost.FindCompanionDocument(csPath);
        if (result == null)
            return null;

        var (companionDoc, uitkxPath, mainDocId, vdoc) = result.Value;
        var offset = LspHelpers.ToOffset(text, position);
        var symbol = LspHelpers.ResolveSymbol(companionDoc, offset, ct);
        if (symbol == null || !LspHelpers.IsUserSymbol(symbol))
            return null;

        // Read the .uitkx text for SourceMap mapping
        string? uitkxText = null;
        var uitkxUri = DocumentUri.FromFileSystemPath(uitkxPath);
        if (!_store.TryGet(uitkxUri, out uitkxText!) || uitkxText == null)
        {
            if (File.Exists(uitkxPath))
                uitkxText = File.ReadAllText(uitkxPath);
        }

        var roslynDoc = _roslynHost.GetRoslynDocument(uitkxPath);
        if (roslynDoc == null || uitkxText == null || vdoc == null)
            return null;

        var locations = CollectRoslynReferences(
            roslynDoc, symbol, uitkxPath, uitkxText, vdoc,
            includeDeclaration, ct);

        return locations.Count > 0 ? new LocationContainer(locations) : null;
    }

    // ── Roslyn-based reference collection ─────────────────────────────────────

    private List<LspLocation> CollectRoslynReferences(
        Document roslynDoc,
        ISymbol symbol,
        string uitkxFilePath,
        string uitkxText,
        VirtualDocument vdoc,
        bool includeDeclaration,
        CancellationToken ct,
        Dictionary<DocumentId, (string PeerPath, VirtualDocument PeerVDoc)>? peerVDocs = null)
    {
        var locations = new List<LspLocation>();

        try
        {
#pragma warning disable VSTHRD002
            var refs = SymbolFinder
                .FindReferencesAsync(symbol, roslynDoc.Project.Solution, ct)
                .GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

            foreach (var refSymbol in refs)
            {
                // Include declaration location(s)
                if (includeDeclaration)
                {
                    foreach (var declLoc in refSymbol.Definition.Locations)
                    {
                        var loc = MapRoslynLocation(
                            declLoc, roslynDoc, uitkxFilePath, uitkxText, vdoc, peerVDocs);
                        if (loc != null)
                            locations.Add(loc);
                    }
                }

                // Include all reference locations
                foreach (var refLoc in refSymbol.Locations)
                {
                    var loc = MapRoslynLocation(
                        refLoc.Location, roslynDoc, uitkxFilePath, uitkxText, vdoc, peerVDocs);
                    if (loc != null)
                        locations.Add(loc);
                }
            }
        }
        catch (Exception ex)
        {
            ServerLog.Log($"[References] SymbolFinder error: {ex.Message}");
        }

        return locations;
    }

    /// <summary>
    /// Maps a Roslyn <see cref="Microsoft.CodeAnalysis.Location"/> back to an
    /// LSP <see cref="LspLocation"/> — either via SourceMap for the main virtual
    /// document, or directly for companion .cs files.
    /// </summary>
    private LspLocation? MapRoslynLocation(
        Microsoft.CodeAnalysis.Location roslynLoc,
        Document roslynDoc,
        string uitkxFilePath,
        string uitkxText,
        VirtualDocument vdoc,
        Dictionary<DocumentId, (string PeerPath, VirtualDocument PeerVDoc)>? peerVDocs = null)
    {
        if (!roslynLoc.IsInSource)
            return null;

        var tree = roslynLoc.SourceTree;
        if (tree == null)
            return null;

        var span = roslynLoc.SourceSpan;

        // Check if this location is in the main virtual document
        var mainDoc = roslynDoc.Project.Solution.GetDocument(roslynDoc.Id);
        if (mainDoc != null && tree == mainDoc.GetSyntaxTreeAsync().GetAwaiter().GetResult())
        {
            // Map via SourceMap to .uitkx coordinates
            var startResult = vdoc.Map.ToUitkxOffset(span.Start);
            var endResult = vdoc.Map.ToUitkxOffset(span.End);
            if (startResult.HasValue && endResult.HasValue)
            {
                return new LspLocation
                {
                    Uri = DocumentUri.FromFileSystemPath(uitkxFilePath),
                    Range = LspHelpers.OffsetRangeToLspRange(
                        uitkxText,
                        startResult.Value.UitkxOffset,
                        endResult.Value.UitkxOffset),
                };
            }

            // If end doesn't map, try start + symbol name length
            if (startResult.HasValue)
            {
                int nameLen = span.Length;
                return new LspLocation
                {
                    Uri = DocumentUri.FromFileSystemPath(uitkxFilePath),
                    Range = LspHelpers.OffsetRangeToLspRange(
                        uitkxText,
                        startResult.Value.UitkxOffset,
                        startResult.Value.UitkxOffset + nameLen),
                };
            }

            return null;
        }

        // ── Check if location is in a peer virtual document ──────────────
        if (peerVDocs != null)
        {
            foreach (var doc in roslynDoc.Project.Documents)
            {
                var docTree = doc.GetSyntaxTreeAsync().GetAwaiter().GetResult();
                if (docTree != tree)
                    continue;

                if (peerVDocs.TryGetValue(doc.Id, out var peerEntry))
                {
                    var (peerPath, peerVDoc) = peerEntry;
                    var peerStartResult = peerVDoc.Map.ToUitkxOffset(span.Start);
                    if (peerStartResult.HasValue)
                    {
                        // U-42: prefer the store (an open, possibly-unsaved buffer) over disk —
                        // matching this file's own CollectComponentReferences/CollectHookReferences
                        // pattern (lines ~438/508) — so a peer file with unsaved edits doesn't
                        // compute the reference location against stale text.
                        string peerSource;
                        if (_store.TryGetByPath(peerPath, out var livePeerSource))
                            peerSource = livePeerSource;
                        else
                        {
                            try { peerSource = File.Exists(peerPath) ? File.ReadAllText(peerPath) : ""; }
                            catch { break; }
                        }

                        var peerEndResult = peerVDoc.Map.ToUitkxOffset(span.End);
                        int endOffset = peerEndResult.HasValue
                            ? peerEndResult.Value.UitkxOffset
                            : peerStartResult.Value.UitkxOffset + span.Length;

                        return new LspLocation
                        {
                            Uri = DocumentUri.FromFileSystemPath(peerPath),
                            Range = LspHelpers.OffsetRangeToLspRange(
                                peerSource,
                                peerStartResult.Value.UitkxOffset,
                                endOffset),
                        };
                    }
                    break;
                }
            }
        }

        // Location is in a companion .cs document
        var dir = Path.GetDirectoryName(uitkxFilePath);
        if (dir == null)
            return null;

        // Find which document this tree belongs to
        foreach (var doc in roslynDoc.Project.Documents)
        {
            var docTree = doc.GetSyntaxTreeAsync().GetAwaiter().GetResult();
            if (docTree == tree)
            {
                var companionPath = Path.Combine(dir, doc.Name);
                if (!File.Exists(companionPath))
                    continue;

                var lineSpan = roslynLoc.GetLineSpan();
                return new LspLocation
                {
                    Uri = DocumentUri.FromFileSystemPath(companionPath),
                    Range = new LspRange(
                        new Position(lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                        new Position(lineSpan.EndLinePosition.Line, lineSpan.EndLinePosition.Character)),
                };
            }
        }

        return null;
    }

    // ── Component/tag name reference collection ───────────────────────────────

    private List<LspLocation> CollectComponentReferences(string componentName, bool includeDeclaration)
    {
        var locations = new List<LspLocation>();
        var workspaceRoot = _roslynHost.WorkspaceRoot;
        if (string.IsNullOrEmpty(workspaceRoot))
            return locations;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(workspaceRoot, "*.uitkx", SearchOption.AllDirectories);
        }
        catch
        {
            return locations;
        }

        foreach (var uitkxFile in files)
        {
            if (LspHelpers.IsInsideTildeFolder(uitkxFile))
                continue;

            string fileText;
            var fileUri = DocumentUri.FromFileSystemPath(uitkxFile);
            if (!_store.TryGet(fileUri, out fileText!))
            {
                try { fileText = File.ReadAllText(uitkxFile); }
                catch { continue; }
            }

            // Component declaration
            if (includeDeclaration)
            {
                var declPattern = new Regex(
                    $@"(?:^|\n)\s*(?:@component|component)\s+({Regex.Escape(componentName)})\b",
                    RegexOptions.CultureInvariant);
                var declMatch = declPattern.Match(fileText);
                if (declMatch.Success)
                {
                    var group = declMatch.Groups[1];
                    locations.Add(new LspLocation
                    {
                        Uri = fileUri,
                        Range = LspHelpers.OffsetRangeToLspRange(
                            fileText, group.Index, group.Index + group.Length),
                    });
                }
            }

            // Tag usages: <Name …>  </Name>  <Name />
            var tagPattern = new Regex(
                $@"<(/?)({Regex.Escape(componentName)})(?=[\s/>])",
                RegexOptions.CultureInvariant);

            foreach (Match m in tagPattern.Matches(fileText))
            {
                var nameGroup = m.Groups[2];
                locations.Add(new LspLocation
                {
                    Uri = fileUri,
                    Range = LspHelpers.OffsetRangeToLspRange(
                        fileText,
                        nameGroup.Index,
                        nameGroup.Index + nameGroup.Length),
                });
            }
        }

        return locations;
    }

    // ── Hook reference collection ─────────────────────────────────────────────

    /// <summary>
    /// Collects hook declaration and call-site references across .uitkx files
    /// in the same directory. Deduplicates against locations already collected.
    /// </summary>
    private void CollectHookReferences(
        string hookName,
        string originFilePath,
        bool includeDeclaration,
        List<LspLocation> locations)
    {
        var dir = Path.GetDirectoryName(originFilePath);
        if (dir == null) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.uitkx"); }
        catch { return; }

        foreach (var uitkxFile in files)
        {
            string fileText;
            var fileUri = DocumentUri.FromFileSystemPath(uitkxFile);
            if (!_store.TryGet(fileUri, out fileText!))
            {
                try { fileText = File.ReadAllText(uitkxFile); }
                catch { continue; }
            }

            CollectHookReferencesInText(fileText, fileUri, hookName, includeDeclaration, locations);
        }
    }

    /// <summary>Per-file text scan of <see cref="CollectHookReferences"/> (pure — directly
    /// unit-testable): the plain declaration head plus call sites.</summary>
    internal static void CollectHookReferencesInText(
        string fileText,
        DocumentUri fileUri,
        string hookName,
        bool includeDeclaration,
        List<LspLocation> locations)
    {
        // Hook declaration head (plain dialect): `[export] <ret> useX[<T>](…)`. The
        // name must be preceded by a REAL type token (audit B2, same guard as the
        // rename scan) — a whitespace-tolerant prefix would let an indented,
        // `=`-free call statement classify as a declaration and be dropped from
        // includeDeclaration=false results. The `)` alternative is the wrapped
        // tuple-return continuation line the formatter emits for over-width heads
        // (`) useX(…` at column 0) — the type-token alternative only spans such
        // heads by accident and gives up past one level of tuple nesting.
        var hookDeclPattern = new Regex(
            $@"^(?:(?:export\s+)?{LspHelpers.DeclTypePattern}\s+|\)\s*)({Regex.Escape(hookName)})(?=\s*(?:<[\w,\s]+>\s*)?\()",
            RegexOptions.CultureInvariant | RegexOptions.Multiline);
        var declNameOffsets = new HashSet<int>();
        foreach (Match m in hookDeclPattern.Matches(fileText))
        {
            var g = m.Groups[1];
            declNameOffsets.Add(g.Index);
            if (includeDeclaration)
                AddLocationDedup(locations, fileUri, fileText, g.Index, g.Length);
        }

        // Call sites: `hookName(` / `hookName<T>(` — whole-word
        var callPattern = new Regex(
            $@"\b{Regex.Escape(hookName)}(?=\s*(?:<[\w,\s]+>\s*)?\()",
            RegexOptions.CultureInvariant);
        foreach (Match m in callPattern.Matches(fileText))
        {
            // Skip declaration heads (handled above, gated on includeDeclaration)
            if (declNameOffsets.Contains(m.Index))
                continue;

            AddLocationDedup(locations, fileUri, fileText, m.Index, m.Length);
        }
    }

    // ── Peer symbol reference collection (module fields/styles) ───────────────

    /// <summary>
    /// Collects bare identifier references for module fields/properties
    /// across peer .uitkx files (accessed via <c>using static</c>).
    /// </summary>
    private void CollectPeerSymbolReferences(
        string symbolName,
        string definingFilePath,
        bool includeDeclaration,
        List<LspLocation> locations)
    {
        var dir = Path.GetDirectoryName(definingFilePath);
        if (dir == null) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.uitkx"); }
        catch { return; }

        foreach (var uitkxFile in files)
        {
            // Skip the defining file — Roslyn already found references within it
            if (string.Equals(
                    Path.GetFullPath(uitkxFile),
                    Path.GetFullPath(definingFilePath),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            string fileText;
            var fileUri = DocumentUri.FromFileSystemPath(uitkxFile);
            if (!_store.TryGet(fileUri, out fileText!))
            {
                try { fileText = File.ReadAllText(uitkxFile); }
                catch { continue; }
            }

            var pattern = new Regex(
                $@"\b{Regex.Escape(symbolName)}\b",
                RegexOptions.CultureInvariant);

            foreach (Match m in pattern.Matches(fileText))
            {
                // Skip directive lines and comments
                int lineStart = fileText.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                var linePrefix = fileText.Substring(lineStart, m.Index - lineStart).TrimStart();
                if (linePrefix.StartsWith("@", StringComparison.Ordinal))
                    continue;
                if (linePrefix.StartsWith("//", StringComparison.Ordinal))
                    continue;

                AddLocationDedup(locations, fileUri, fileText, m.Index, m.Length);
            }
        }
    }

    // ── Member reference collection (values/utils) ────────────────────────────

    /// <summary>
    /// Collects value/util member declaration and usage references across
    /// .uitkx files in the same directory.  Finds the plain declaration head
    /// (<c>[export] &lt;Type&gt; name = …</c> / <c>[export] &lt;ret&gt; name(…)</c>)
    /// and bare identifier usages of the member name.
    /// </summary>
    private void CollectMemberReferences(
        string memberName,
        string originFilePath,
        bool includeDeclaration,
        List<LspLocation> locations)
    {
        var dir = Path.GetDirectoryName(originFilePath);
        if (dir == null) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.uitkx"); }
        catch { return; }

        foreach (var uitkxFile in files)
        {
            string fileText;
            var fileUri = DocumentUri.FromFileSystemPath(uitkxFile);
            if (!_store.TryGet(fileUri, out fileText!))
            {
                try { fileText = File.ReadAllText(uitkxFile); }
                catch { continue; }
            }

            CollectMemberReferencesInText(fileText, fileUri, memberName, includeDeclaration, locations);
        }
    }

    /// <summary>Per-file text scan of <see cref="CollectMemberReferences"/> (pure — directly
    /// unit-testable): the plain declaration head plus bare identifier usages.</summary>
    internal static void CollectMemberReferencesInText(
        string fileText,
        DocumentUri fileUri,
        string memberName,
        bool includeDeclaration,
        List<LspLocation> locations)
    {
        // Declaration head: `[export] <Type> name = …` (value) or
        // `[export] <ret> name[<T>](…)` (util). The name must be preceded by a REAL
        // type token (audit B2, same guard as the rename scan) so statement-shaped
        // lines can never classify as declarations. The `)` alternative is a
        // wrapped tuple-return util head's continuation line (column 0).
        var declPattern = new Regex(
            $@"^(?:(?:export\s+)?{LspHelpers.DeclTypePattern}\s+|\)\s*)({Regex.Escape(memberName)})(?=\s*(?:=(?!=)|(?:<[\w,\s]+>\s*)?\())",
            RegexOptions.CultureInvariant | RegexOptions.Multiline);
        var declNameOffsets = new HashSet<int>();
        foreach (Match m in declPattern.Matches(fileText))
        {
            var g = m.Groups[1];
            declNameOffsets.Add(g.Index);
            if (includeDeclaration)
                AddLocationDedup(locations, fileUri, fileText, g.Index, g.Length);
        }

        // Usages of the member name as an identifier
        var usagePattern = new Regex(
            $@"\b{Regex.Escape(memberName)}\b",
            RegexOptions.CultureInvariant);
        foreach (Match m in usagePattern.Matches(fileText))
        {
            // Skip the declaration head (handled above, gated on includeDeclaration)
            if (declNameOffsets.Contains(m.Index))
                continue;
            // Skip directives and comments
            int lineStart = fileText.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            var linePrefix = fileText.Substring(lineStart, m.Index - lineStart).TrimStart();
            if (linePrefix.StartsWith("@", StringComparison.Ordinal))
                continue;
            if (linePrefix.StartsWith("//", StringComparison.Ordinal))
                continue;

            AddLocationDedup(locations, fileUri, fileText, m.Index, m.Length);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a location only if no existing location covers the same file and range.
    /// Prevents duplicates when Roslyn SourceMap and text-based scan both find
    /// the same reference.
    /// </summary>
    private static void AddLocationDedup(
        List<LspLocation> locations,
        DocumentUri uri,
        string text,
        int offset,
        int length)
    {
        var range = LspHelpers.OffsetRangeToLspRange(text, offset, offset + length);
        var uriStr = uri.ToString();
        foreach (var existing in locations)
        {
            if (existing.Uri.ToString() == uriStr
                && existing.Range.Start.Line == range.Start.Line
                && existing.Range.Start.Character == range.Start.Character
                && existing.Range.End.Line == range.End.Line
                && existing.Range.End.Character == range.End.Character)
                return;
        }
        locations.Add(new LspLocation { Uri = uri, Range = range });
    }
}
