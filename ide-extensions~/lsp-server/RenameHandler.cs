using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using Ruitk.Language.Parser;
using Ruitk.Language.Roslyn;
using UitkxLanguageServer.Roslyn;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace UitkxLanguageServer;

/// <summary>
/// Handles <c>textDocument/prepareRename</c> and <c>textDocument/rename</c>
/// for <c>.uitkx</c> files.
///
/// <para><b>Two rename paths:</b></para>
/// <list type="bullet">
///   <item><b>C# path</b> — cursor is inside a source-mapped C# region
///   (code block, inline expression).  Roslyn's <c>Renamer.RenameSymbolAsync</c>
///   renames the symbol inside the per-file AdhocWorkspace then diffs are mapped
///   back to <c>.uitkx</c> coordinates via the <see cref="SourceMap"/>.</item>
///   <item><b>Component-name path</b> — cursor is on a tag name
///   (<c>&lt;Counter /&gt;</c>) or a <c>component Counter { … }</c> declaration.
///   A workspace-wide text scan replaces all tag references and the declaration.</item>
/// </list>
///
/// When a C# rename targets the component class itself (the <c>partial class</c>
/// generated from the <c>component</c> directive), both paths execute: Roslyn
/// renames code-level references and the text scan renames tags + declaration.
/// </summary>
public sealed class RenameHandler : IRenameHandler, IPrepareRenameHandler
{
    private readonly DocumentStore _store;
    private readonly WorkspaceIndex _index;
    private readonly RoslynHost _roslynHost;
    private readonly ILanguageServerFacade _server;

    // U-37: whether the connected client declared support for the "rename" resource
    // operation in WorkspaceEdit.DocumentChanges (LSP spec:
    // ClientCapabilities.Workspace.WorkspaceEdit.ResourceOperations). Captured once at
    // registration time; used to decide whether a component rename can include a
    // RenameFile op or must fall back to text-only edits + a manual-rename message.
    private bool _clientSupportsFileRename;

    public RenameHandler(
        DocumentStore store,
        WorkspaceIndex index,
        RoslynHost roslynHost,
        ILanguageServerFacade server)
    {
        _store = store;
        _index = index;
        _roslynHost = roslynHost;
        _server = server;
    }

    // ── Registration ──────────────────────────────────────────────────────────

    public RenameRegistrationOptions GetRegistrationOptions(
        RenameCapability capability,
        ClientCapabilities clientCapabilities
    )
    {
        var resourceOps = clientCapabilities.Workspace?.WorkspaceEdit.Value?.ResourceOperations;
        _clientSupportsFileRename =
            resourceOps != null && resourceOps.Contains(ResourceOperationKind.Rename);

        return new RenameRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.uitkx" },
                new TextDocumentFilter { Pattern = "**/*.cs" }
            ),
            PrepareProvider = true,
        };
    }

    // ── textDocument/prepareRename ────────────────────────────────────────────

    public async Task<RangeOrPlaceholderRange?> Handle(
        PrepareRenameParams request,
        CancellationToken ct
    )
    {
        var localPath = UriToPath(request.TextDocument.Uri);
        if (localPath == null)
            return null;

        if (!TryGetText(request.TextDocument.Uri, localPath, out var text))
            return null;

        // ── .cs companion file path ──────────────────────────────────────
        if (localPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return await PrepareRenameCsAsync(localPath, text, request.Position, ct).ConfigureAwait(false);

        var parseResult = ParseText(text, localPath);
        var offset = ToOffset(text, request.Position);

        // Ensure workspace is up-to-date for Roslyn queries
        await _roslynHost
            .EnsureReadyAsync(localPath, text, parseResult, ct)
            .ConfigureAwait(false);

        // ── C# path: cursor in a source-mapped C# region ─────────────────
        var vdoc = _roslynHost.GetVirtualDocument(localPath);
        if (vdoc != null)
        {
            var virtualResult = vdoc.Map.ToVirtualOffset(offset);
            ServerLog.Log($"[Rename] PrepareRename: offset={offset}, virtualOffset={virtualResult?.VirtualOffset.ToString() ?? "NULL"}, kind={virtualResult?.Entry.Kind.ToString() ?? "N/A"}");
            if (virtualResult.HasValue)
            {
                var roslynDoc = _roslynHost.GetRoslynDocument(localPath);
                if (roslynDoc != null)
                {
                    var symbol = ResolveSymbol(roslynDoc, virtualResult.Value.VirtualOffset, ct);
                    ServerLog.Log($"[Rename] PrepareRename: symbol={symbol?.Name ?? "NULL"}, kind={symbol?.Kind.ToString() ?? "N/A"}, renameable={symbol != null && IsRenameable(symbol)}");
                    if (symbol != null && IsRenameable(symbol))
                    {
                        var (word, wordStart, wordEnd) = GetWordAtOffset(text, offset);
                        if (!string.IsNullOrEmpty(word))
                        {
                            ServerLog.Log(
                                $"[Rename] PrepareRename (C#): '{word}' ({symbol.Kind})"
                            );
                            return new RangeOrPlaceholderRange(
                                new PlaceholderRange
                                {
                                    Range = OffsetRangeToLspRange(text, wordStart, wordEnd),
                                    Placeholder = word,
                                }
                            );
                        }
                    }
                }
            }
        }

        // ── Component-name path: tag name or component declaration ────────
        var (cWord, cStart, cEnd) = GetWordAtOffset(text, offset);
        if (!string.IsNullOrEmpty(cWord))
        {
            // Component declaration: `component FooBar { … }`
            if (
                parseResult.Directives.IsFunctionStyle
                && cWord == parseResult.Directives.ComponentName
            )
            {
                ServerLog.Log($"[Rename] PrepareRename (declaration): '{cWord}'");
                return new RangeOrPlaceholderRange(
                    new PlaceholderRange
                    {
                        Range = OffsetRangeToLspRange(text, cStart, cEnd),
                        Placeholder = cWord,
                    }
                );
            }

            // Tag reference: <FooBar … /> — the element must be a UITKX component
            if (IsOnTagName(text, offset, cWord))
            {
                var elementInfo = _index.TryGetElementInfo(cWord);
                if (
                    elementInfo != null
                    && elementInfo.FilePath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase)
                )
                {
                    ServerLog.Log($"[Rename] PrepareRename (tag): '{cWord}'");
                    return new RangeOrPlaceholderRange(
                        new PlaceholderRange
                        {
                            Range = OffsetRangeToLspRange(text, cStart, cEnd),
                            Placeholder = cWord,
                        }
                    );
                }
            }

            // Hook member declaration: `[export] <ret> useMyHook(...)`
            if (!parseResult.Directives.MemberDeclarations.IsDefaultOrEmpty)
            {
                foreach (var member in parseResult.Directives.MemberDeclarations)
                {
                    if (member.Kind == DeclKind.Hook && cWord == member.Name)
                    {
                        ServerLog.Log($"[Rename] PrepareRename (hook): '{cWord}'");
                        return new RangeOrPlaceholderRange(
                            new PlaceholderRange
                            {
                                Range = OffsetRangeToLspRange(text, cStart, cEnd),
                                Placeholder = cWord,
                            }
                        );
                    }
                }
            }
        }

        return null;
    }

    // ── textDocument/rename ───────────────────────────────────────────────────

    public async Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken ct)
    {
        var localPath = UriToPath(request.TextDocument.Uri);
        if (localPath == null)
            return null;

        if (!TryGetText(request.TextDocument.Uri, localPath, out var text))
            return null;

        var newName = request.NewName;
        if (string.IsNullOrWhiteSpace(newName) || !IsValidIdentifier(newName))
            return null;

        // ── .cs companion file path ──────────────────────────────────────
        if (localPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return await RenameCsAsync(localPath, text, newName, request.Position, ct).ConfigureAwait(false);

        var parseResult = ParseText(text, localPath);
        var offset = ToOffset(text, request.Position);

        await _roslynHost
            .EnsureReadyAsync(localPath, text, parseResult, ct)
            .ConfigureAwait(false);

        var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();

        // ── C# path ──────────────────────────────────────────────────────
        var vdoc = _roslynHost.GetVirtualDocument(localPath);
        if (vdoc != null)
        {
            var virtualResult = vdoc.Map.ToVirtualOffset(offset);
            if (virtualResult.HasValue)
            {
                var roslynDoc = _roslynHost.GetRoslynDocument(localPath);
                if (roslynDoc != null)
                {
                    var symbol = ResolveSymbol(
                        roslynDoc,
                        virtualResult.Value.VirtualOffset,
                        ct
                    );
                    if (symbol != null && IsRenameable(symbol))
                    {
                        ServerLog.Log(
                            $"[Rename] Rename (C#): '{symbol.Name}' → '{newName}' ({symbol.Kind})"
                        );

                        try
                        {
                        // Run Roslyn Renamer
                        var oldSolution = roslynDoc.Project.Solution;
                        // Use a dedicated timeout token — the LSP request token
                        // can be cancelled by a concurrent didChange notification,
                        // which races with Renamer's internal formatting pass.
                        using var renameCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        var newSolution = await Renamer
                            .RenameSymbolAsync(
                                oldSolution,
                                symbol,
                                new SymbolRenameOptions(),
                                newName,
                                renameCts.Token
                            )
                            .ConfigureAwait(false);

                        CollectRoslynEdits(
                            oldSolution,
                            newSolution,
                            roslynDoc.Id,
                            localPath,
                            text,
                            vdoc.Map,
                            symbol.Name,
                            newName,
                            changes,
                            ct,
                            _roslynHost.GetPeerVirtualDocuments(localPath)
                        );

                        // Store post-rename .cs text so the upcoming workspace
                        // rebuild reads it instead of stale disk content.
                        SnapshotCompanionOverlay(
                            oldSolution, newSolution, roslynDoc.Id, localPath);
                        }
                        catch (Exception ex)
                        {
                            ServerLog.Log($"[Rename] ERROR: {ex}");
                            return null;
                        }

                        // If the symbol is the component class, also do
                        // cross-file tag + declaration rename.
                        if (symbol is INamedTypeSymbol namedType)
                        {
                            if (
                                parseResult.Directives.IsFunctionStyle
                                && namedType.Name == parseResult.Directives.ComponentName
                            )
                            {
                                ServerLog.Log(
                                    $"[Rename] Component class rename: '{namedType.Name}' → '{newName}'"
                                );
                                CollectComponentRenameEdits(
                                    namedType.Name,
                                    newName,
                                    changes
                                );
                            }
                        }

                        // If the symbol is a method matching a peer hook
                        // declaration, also rename the hook keyword line
                        // and call sites across .uitkx files.
                        // Skip the current file — Roslyn already renamed call sites there.
                        if (symbol is IMethodSymbol)
                        {
                            var peerVDocs = _roslynHost.GetPeerVirtualDocuments(localPath);
                            if (peerVDocs != null && peerVDocs.Count > 0)
                            {
                                // Pick any peer path to anchor the directory scan
                                var firstPeerPath = peerVDocs.Values.First().PeerPath;
                                ServerLog.Log(
                                    $"[Rename] Hook method rename from consumer: '{symbol.Name}' → '{newName}', scanning peers from {Path.GetFileName(firstPeerPath)}");
                                CollectHookRenameEdits(
                                    firstPeerPath, symbol.Name, newName, changes,
                                    skipFilePath: localPath);
                            }
                        }

                        // If the symbol is a field/property (e.g. module style),
                        // propagate to peer .uitkx files that use it via `using static`.
                        if (symbol is IFieldSymbol || symbol is IPropertySymbol)
                        {
                            ServerLog.Log(
                                $"[Rename] Field/property rename: '{symbol.Name}' → '{newName}', scanning peers");
                            CollectPeerSymbolRenameEdits(
                                localPath, symbol.Name, newName, changes);
                        }

                        CollectImportListRenameEdits(symbol.Name, newName, changes, localPath);

                        // Log what we're returning
                        int totalEdits = 0;
                        foreach (var kvp in changes)
                        {
                            int count = 0;
                            foreach (var _ in kvp.Value) count++;
                            totalEdits += count;
                        }
                        ServerLog.Log($"[Rename] C# path: {changes.Count} file(s), {totalEdits} edit(s)");

                        return new WorkspaceEdit { Changes = changes };
                    }
                }
            }
        }

        // ── Component-name path ──────────────────────────────────────────
        var (word, _, _) = GetWordAtOffset(text, offset);
        if (!string.IsNullOrEmpty(word))
        {
            bool isDeclaration =
                parseResult.Directives.IsFunctionStyle
                && word == parseResult.Directives.ComponentName;

            bool isTagRef = false;
            string? declaringFilePath = isDeclaration ? localPath : null;
            if (!isDeclaration && IsOnTagName(text, offset, word))
            {
                var elementInfo = _index.TryGetElementInfo(word);
                isTagRef =
                    elementInfo != null
                    && elementInfo.FilePath.EndsWith(
                        ".uitkx",
                        StringComparison.OrdinalIgnoreCase
                    );
                if (isTagRef)
                    declaringFilePath = elementInfo!.FilePath;
            }

            if (isDeclaration || isTagRef)
            {
                ServerLog.Log($"[Rename] Component rename: '{word}' → '{newName}'");
                CollectComponentRenameEdits(word, newName, changes);
                CollectImportListRenameEdits(word, newName, changes, localPath);

                // U-37: F2-renaming a component only renamed the TEXT (declaration + tag
                // references); the declaring .uitkx file itself kept its old name, so the
                // component instantly violated the "file stem == component name" convention
                // and the very next compile hit UITKX0103. When the declaring file follows
                // that convention (OldName.uitkx), append a RenameFile op so the file moves
                // in lockstep with the text edits — but only if the client declared support
                // for the "rename" resource operation (LSP spec); VS2022's support for this
                // is spotty, so the text-only fallback + manual-rename notice below is not
                // a legacy code path, it is load-bearing there too.
                if (
                    declaringFilePath != null
                    && string.Equals(
                        Path.GetFileNameWithoutExtension(declaringFilePath),
                        word,
                        StringComparison.Ordinal)
                )
                {
                    if (_clientSupportsFileRename)
                    {
                        var docChanges = new List<WorkspaceEditDocumentChange>();
                        foreach (var (uri, edits) in changes)
                        {
                            docChanges.Add(new WorkspaceEditDocumentChange(new TextDocumentEdit
                            {
                                TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri },
                                Edits = new TextEditContainer(edits),
                            }));
                        }

                        var newFilePath = Path.Combine(
                            Path.GetDirectoryName(declaringFilePath) ?? string.Empty,
                            newName + ".uitkx");
                        docChanges.Add(new WorkspaceEditDocumentChange(new RenameFile
                        {
                            OldUri = DocumentUri.FromFileSystemPath(declaringFilePath),
                            NewUri = DocumentUri.FromFileSystemPath(newFilePath),
                        }));

                        ServerLog.Log($"[Rename] Renaming file: '{declaringFilePath}' → '{newFilePath}'");
                        return new WorkspaceEdit { DocumentChanges = new Container<WorkspaceEditDocumentChange>(docChanges) };
                    }

                    _server.Window.ShowMessage(new ShowMessageParams
                    {
                        Type = MessageType.Info,
                        Message = $"UITKX: renamed '{word}' → '{newName}' in code. Your editor doesn't support " +
                            $"automatic file rename — rename '{Path.GetFileName(declaringFilePath)}' to " +
                            $"'{newName}.uitkx' manually to match.",
                    });
                }

                return new WorkspaceEdit { Changes = changes };
            }

            // ── Hook-name path ───────────────────────────────────────────
            if (!parseResult.Directives.MemberDeclarations.IsDefaultOrEmpty)
            {
                foreach (var member in parseResult.Directives.MemberDeclarations)
                {
                    if (member.Kind == DeclKind.Hook && word == member.Name)
                    {
                        ServerLog.Log($"[Rename] Hook rename: '{word}' → '{newName}'");
                        CollectHookRenameEdits(localPath, word, newName, changes);
                        CollectImportListRenameEdits(word, newName, changes, localPath);
                        return new WorkspaceEdit { Changes = changes };
                    }
                }
            }
        }

        return null;
    }

    // ── Import-list rename ────────────────────────────────────────────────────

    /// <summary>
    /// The single interpretation of the renamed identifier inside `import` lists,
    /// decided from the rename ORIGIN (audit B3): renaming an EXPORT touches the
    /// identifier where it appears as an imported NAME, never alias tokens (an alias
    /// that happens to equal the export name is a LOCAL binding of a different
    /// export); renaming a LOCAL binding touches only the binding token — named
    /// alias, star alias, or default alias — in the invoking file.
    /// </summary>
    internal enum ImportRenameOrigin
    {
        ExportName,
        LocalBinding,
    }

    /// <summary>Classifies a rename of <paramref name="oldName"/> triggered in the file whose
    /// <paramref name="directives"/> these are. A file can never both alias-bind and
    /// declare/import-by-name the same identifier (duplicate binding), so an alias/star/default
    /// binding of <paramref name="oldName"/> means the rename targets that LOCAL binding.</summary>
    internal static ImportRenameOrigin ClassifyImportRenameOrigin(DirectiveSet directives, string oldName)
    {
        if (directives.Imports.IsDefaultOrEmpty)
            return ImportRenameOrigin.ExportName;
        foreach (var imp in directives.Imports)
        {
            if (imp.IsStar && imp.StarAlias == oldName)
                return ImportRenameOrigin.LocalBinding;
            if (imp.IsDefault && imp.DefaultAlias == oldName)
                return ImportRenameOrigin.LocalBinding;
            if (!imp.Aliases.IsDefaultOrEmpty)
                foreach (var alias in imp.Aliases)
                    if (alias == oldName)
                        return ImportRenameOrigin.LocalBinding;
        }
        return ImportRenameOrigin.ExportName;
    }

    /// <summary>The import-line edits for one file under a single, already-decided
    /// <paramref name="origin"/> (pure — directly unit-testable).</summary>
    internal static List<TextEdit> ComputeImportListEdits(
        DirectiveSet directives,
        string[] fileLines,
        string oldName,
        string newName,
        ImportRenameOrigin origin)
    {
        var edits = new List<TextEdit>();
        if (directives.Imports.IsDefaultOrEmpty)
            return edits;

        static TextEdit EditAt(int line0, int col, string oldName, string newName) => new TextEdit
        {
            Range = new LspRange(
                new Position(line0, col),
                new Position(line0, col + oldName.Length)),
            NewText = newName,
        };

        foreach (var imp in directives.Imports)
        {
            int line0 = imp.Line - 1;
            if (line0 < 0 || line0 >= fileLines.Length)
                continue;
            string lineText = fileLines[line0].TrimEnd('\r');

            if (origin == ImportRenameOrigin.LocalBinding)
            {
                // Binding tokens live before the `from` keyword; capping the search there
                // keeps a matching path segment inside the specifier string untouched.
                var fromMatch = Regex.Match(lineText, @"\bfrom\b");
                int bindingEnd = fromMatch.Success ? fromMatch.Index : lineText.Length;

                if (imp.IsStar && imp.StarAlias == oldName)
                {
                    int star = lineText.IndexOf('*');
                    int col = FindWholeWord(lineText, oldName, star < 0 ? 0 : star + 1, bindingEnd);
                    if (col >= 0)
                        edits.Add(EditAt(line0, col, oldName, newName));
                    continue;
                }
                if (imp.IsDefault && imp.DefaultAlias == oldName)
                {
                    int col = FindWholeWord(lineText, oldName, Math.Max(0, imp.Column), bindingEnd);
                    if (col >= 0)
                        edits.Add(EditAt(line0, col, oldName, newName));
                    continue;
                }
                if (imp.Aliases.IsDefaultOrEmpty)
                    continue;
                for (int i = 0; i < imp.Names.Length && i < imp.Aliases.Length; i++)
                {
                    if (imp.Aliases[i] != oldName)
                        continue;
                    // The alias token's column is not in the model; locate it textually
                    // after the name token on the import line.
                    int searchFrom = i < imp.NameColumns.Length
                        ? imp.NameColumns[i] + imp.Names[i].Length : 0;
                    int col = FindWholeWord(lineText, oldName, searchFrom, bindingEnd);
                    if (col >= 0)
                        edits.Add(EditAt(line0, col, oldName, newName));
                }
                continue;
            }

            // ExportName origin: only imported-NAME tokens. Alias tokens equal to oldName
            // are local bindings of OTHER exports and must stay (audit B3).
            for (int i = 0; i < imp.Names.Length; i++)
            {
                if (imp.Names[i] != oldName)
                    continue;
                if (i >= imp.NameColumns.Length || imp.NameColumns[i] < 0)
                    continue;
                edits.Add(EditAt(line0, imp.NameColumns[i], oldName, newName));
            }
        }
        return edits;
    }

    /// <summary>Whole-word occurrence of <paramref name="word"/> in <paramref name="text"/> at or
    /// after <paramref name="startIndex"/> and (when <paramref name="endExclusive"/> is non-negative) starting
    /// before it, or -1.</summary>
    private static int FindWholeWord(string text, string word, int startIndex, int endExclusive = -1)
    {
        int from = Math.Max(0, Math.Min(startIndex, text.Length));
        int limit = endExclusive < 0 ? text.Length : Math.Min(endExclusive, text.Length);
        while (from < limit)
        {
            int idx = text.IndexOf(word, from, StringComparison.Ordinal);
            if (idx < 0 || idx >= limit)
                return -1;
            bool leftOk = idx == 0 || (!char.IsLetterOrDigit(text[idx - 1]) && text[idx - 1] != '_');
            int end = idx + word.Length;
            bool rightOk = end >= text.Length || (!char.IsLetterOrDigit(text[end]) && text[end] != '_');
            if (leftOk && rightOk)
                return idx;
            from = idx + 1;
        }
        return -1;
    }

    /// <summary>
    /// Renames <paramref name="oldName"/> inside `import { … }` name lists. Import
    /// names are uitkx-only preamble syntax: they never appear in the C# virtual
    /// document (imports lower to `using` lines), so Roslyn's renamer cannot see
    /// them, and the text-based collectors match call/tag shapes (`name(`, `&lt;Name`)
    /// that an import list doesn't have. Without this pass an F2 rename updates the
    /// declaration and every call site but leaves the import binding the OLD name —
    /// instantly breaking the file with UITKX2300/2304.
    ///
    /// The interpretation of <paramref name="oldName"/> is decided ONCE from the
    /// invoking file (audit B3) and applied uniformly: a LocalBinding rename edits
    /// only the invoking file's binding token; an ExportName rename edits imported
    /// NAME tokens in the candidate files. Candidate files are the ones already
    /// participating in the rename (keys of <paramref name="changes"/>) plus the
    /// invoking file — i.e. every file where the symbol is actually used. A file
    /// that imports the name but never uses it is already flagged UITKX2304 and is
    /// deliberately out of scope.
    /// </summary>
    internal void CollectImportListRenameEdits(
        string oldName,
        string newName,
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes,
        string invokingFilePath)
    {
        var origin = ImportRenameOrigin.ExportName;
        if (invokingFilePath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var invokerUri = DocumentUri.FromFileSystemPath(invokingFilePath);
                if (TryGetText(invokerUri, invokingFilePath, out var invokerText))
                {
                    var invokerDirectives = DirectiveParser.Parse(
                        invokerText, invokingFilePath, new List<Ruitk.Language.ParseDiagnostic>());
                    origin = ClassifyImportRenameOrigin(invokerDirectives, oldName);
                }
            }
            catch (Exception ex)
            {
                ServerLog.Log($"[Rename] import-list origin classification failed: {ex.Message}");
            }
        }

        var candidatePaths = new Dictionary<string, DocumentUri>(StringComparer.OrdinalIgnoreCase);
        if (origin == ImportRenameOrigin.ExportName)
        {
            foreach (var uri in changes.Keys.ToList())
            {
                var p = UriToPath(uri);
                if (p != null && p.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
                    candidatePaths[p] = uri;
            }
        }
        if (invokingFilePath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase)
            && !candidatePaths.ContainsKey(invokingFilePath))
        {
            candidatePaths[invokingFilePath] = DocumentUri.FromFileSystemPath(invokingFilePath);
        }

        foreach (var (path, uri) in candidatePaths)
        {
            try
            {
                if (!TryGetText(uri, path, out var fileText))
                    continue;

                var diags = new List<Ruitk.Language.ParseDiagnostic>();
                var directives = DirectiveParser.Parse(fileText, path, diags);
                if (directives.Imports.IsDefaultOrEmpty)
                    continue;

                var importEdits = ComputeImportListEdits(
                    directives, fileText.Split('\n'), oldName, newName, origin);
                if (importEdits.Count == 0)
                    continue;

                ServerLog.Log(
                    $"[Rename] import-list ({origin}): {importEdits.Count} edit(s) in {Path.GetFileName(path)}");
                // AddEdit keeps the "changes values are List<TextEdit>" invariant (audit B4)
                // and dedups against edits other collectors already produced.
                foreach (var edit in importEdits)
                    AddEdit(changes, uri, edit);
            }
            catch (Exception ex)
            {
                ServerLog.Log($"[Rename] import-list scan failed for {path}: {ex.Message}");
            }
        }
    }

    // ── .cs companion rename ──────────────────────────────────────────────────

    private async Task<RangeOrPlaceholderRange?> PrepareRenameCsAsync(
        string csPath, string text, Position position, CancellationToken ct)
    {
        // Refresh the companion doc text so Roslyn sees the latest content
        // (e.g. after a rename from the .uitkx side applied edits here).
        var refreshedDoc = _roslynHost.RefreshCompanionDocument(csPath, text);
        if (refreshedDoc == null)
        {
            ServerLog.Log($"[Rename] PrepareRename (.cs): no companion workspace for {Path.GetFileName(csPath)}");
            return null;
        }

        var offset = ToOffset(text, position);
        var symbol = ResolveSymbol(refreshedDoc, offset, ct);
        if (symbol == null || !IsRenameable(symbol))
            return null;

        var (word, wordStart, wordEnd) = GetWordAtOffset(text, offset);
        if (string.IsNullOrEmpty(word))
            return null;

        ServerLog.Log($"[Rename] PrepareRename (.cs): '{word}' ({symbol.Kind}) in {Path.GetFileName(csPath)}");
        return new RangeOrPlaceholderRange(
            new PlaceholderRange
            {
                Range = OffsetRangeToLspRange(text, wordStart, wordEnd),
                Placeholder = word,
            }
        );
    }

    private async Task<WorkspaceEdit?> RenameCsAsync(
        string csPath, string text, string newName, Position position, CancellationToken ct)
    {
        // Refresh the companion doc text so Roslyn sees the latest content.
        var refreshedDoc = _roslynHost.RefreshCompanionDocument(csPath, text);
        if (refreshedDoc == null)
        {
            ServerLog.Log($"[Rename] Rename (.cs): no companion workspace for {Path.GetFileName(csPath)}");
            return null;
        }

        // Re-lookup the full companion info (uitkxPath, mainDocId, vdoc)
        var result = _roslynHost.FindCompanionDocument(csPath);
        if (result == null)
            return null;

        var (companionDoc, uitkxPath, mainDocId, vdoc) = result.Value;
        var offset = ToOffset(text, position);
        var symbol = ResolveSymbol(companionDoc, offset, ct);
        if (symbol == null || !IsRenameable(symbol))
            return null;

        ServerLog.Log($"[Rename] Rename (.cs): '{symbol.Name}' → '{newName}' ({symbol.Kind}) in {Path.GetFileName(csPath)}");

        var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
        try
        {
            var oldSolution = companionDoc.Project.Solution;
            using var renameCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var newSolution = await Renamer
                .RenameSymbolAsync(
                    oldSolution,
                    symbol,
                    new SymbolRenameOptions(),
                    newName,
                    renameCts.Token
                )
                .ConfigureAwait(false);

            // Read the .uitkx text for SourceMap mapping
            string? uitkxText = null;
            var uitkxUri = DocumentUri.FromFileSystemPath(uitkxPath);
            if (!_store.TryGet(uitkxUri, out uitkxText!) || uitkxText == null)
            {
                if (File.Exists(uitkxPath))
                    uitkxText = File.ReadAllText(uitkxPath);
            }

            if (uitkxText != null && vdoc != null)
            {
                CollectRoslynEdits(
                    oldSolution, newSolution, mainDocId,
                    uitkxPath, uitkxText, vdoc.Map,
                    symbol.Name, newName, changes, ct);
            }

            // Store post-rename .cs text so the upcoming workspace
            // rebuild reads it instead of stale disk content.
            SnapshotCompanionOverlay(
                oldSolution, newSolution, mainDocId, uitkxPath);
        }
        catch (Exception ex)
        {
            ServerLog.Log($"[Rename] ERROR (.cs): {ex}");
            return null;
        }

        int totalEdits = 0;
        foreach (var kvp in changes)
        {
            int count = 0;
            foreach (var _ in kvp.Value) count++;
            totalEdits += count;
        }
        ServerLog.Log($"[Rename] .cs path: {changes.Count} file(s), {totalEdits} edit(s)");

        return totalEdits > 0 ? new WorkspaceEdit { Changes = changes } : null;
    }

    // ── Roslyn edit collection ────────────────────────────────────────────────

    /// <summary>
    /// Diffs the old and new Roslyn <see cref="Solution"/> and collects text
    /// edits.  Main-document changes are mapped via the <see cref="SourceMap"/>
    /// to <c>.uitkx</c> coordinates.  Companion-document changes target the
    /// real <c>.cs</c> file.
    /// </summary>
    private static void CollectRoslynEdits(
        Solution oldSolution,
        Solution newSolution,
        DocumentId mainDocId,
        string uitkxFilePath,
        string uitkxText,
        SourceMap map,
        string oldName,
        string newName,
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes,
        CancellationToken ct,
        Dictionary<DocumentId, (string PeerPath, VirtualDocument PeerVDoc)>? peerVDocs = null
    )
    {
        var solutionChanges = newSolution.GetChanges(oldSolution);
        int projectCount = 0;
        foreach (var projectChanges in solutionChanges.GetProjectChanges())
        {
            projectCount++;
            var changedDocs = projectChanges.GetChangedDocuments();
            foreach (var docId in changedDocs)
            {
                var oldDoc = oldSolution.GetDocument(docId);
                var newDoc = newSolution.GetDocument(docId);
                if (oldDoc == null || newDoc == null)
                    continue;

#pragma warning disable VSTHRD002
                var oldText = oldDoc.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult();
                var newText = newDoc.GetTextAsync(CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                var textChanges = newText.GetTextChanges(oldText);

                if (docId == mainDocId)
                {
                    // ── Main virtual document → map back to .uitkx ───────
                    // Roslyn's Renamer often returns a single whole-document
                    // replacement rather than surgical per-token edits.
                    // We can't map scaffold-region offsets back to .uitkx,
                    // so instead we scan each SourceMap entry for occurrences
                    // of the old symbol name that became the new name in the
                    // Renamer output, and emit per-occurrence edits.
                    var uri = DocumentUri.FromFileSystemPath(uitkxFilePath);
                    var oldStr = oldText.ToString();
                    var newStr = newText.ToString();

                    // Walk entries in order.  Track cumulative offset shift
                    // caused by earlier renames (name length may change).
                    int cumulativeShift = 0;

                    foreach (var entry in map.Entries)
                    {
                        int oldLen = entry.VirtualEnd - entry.VirtualStart;
                        if (entry.VirtualStart >= oldStr.Length)
                            continue;

                        var oldSlice = oldStr.Substring(
                            entry.VirtualStart,
                            Math.Min(oldLen, oldStr.Length - entry.VirtualStart));

                        int newStart = entry.VirtualStart + cumulativeShift;
                        if (newStart >= newStr.Length)
                            continue;

                        int newLen = oldLen; // start with same length estimate
                        // Compute actual new-region length by counting how many
                        // renames happened inside this region.
                        int occurrences = CountOccurrences(oldSlice, oldName);
                        int lengthDelta = occurrences * (newName.Length - oldName.Length);
                        newLen = oldLen + lengthDelta;

                        if (newStart + newLen > newStr.Length)
                            newLen = newStr.Length - newStart;

                        var newSlice = newStr.Substring(newStart, newLen);

                        if (oldSlice == newSlice)
                        {
                            cumulativeShift += lengthDelta;
                            continue;
                        }

                        // Emit one edit per occurrence within this mapped region.
                        // Walk the old slice finding occurrences as whole-word matches.
                        int searchPos = 0;
                        while (searchPos < oldSlice.Length)
                        {
                            int idx = oldSlice.IndexOf(oldName, searchPos, StringComparison.Ordinal);
                            if (idx < 0) break;

                            // Whole-word check
                            bool leftOk = idx == 0 || !char.IsLetterOrDigit(oldSlice[idx - 1]) && oldSlice[idx - 1] != '_';
                            int endIdx = idx + oldName.Length;
                            bool rightOk = endIdx >= oldSlice.Length || !char.IsLetterOrDigit(oldSlice[endIdx]) && oldSlice[endIdx] != '_';

                            if (leftOk && rightOk)
                            {
                                int uitkxOffset = entry.UitkxStart + idx;
                                AddEdit(
                                    changes,
                                    uri,
                                    new TextEdit
                                    {
                                        Range = OffsetRangeToLspRange(
                                            uitkxText,
                                            uitkxOffset,
                                            uitkxOffset + oldName.Length),
                                        NewText = newName,
                                    }
                                );
                            }

                            searchPos = idx + oldName.Length;
                        }

                        cumulativeShift += lengthDelta;
                    }
                }
                else if (peerVDocs != null && peerVDocs.TryGetValue(docId, out var peerEntry))
                {
                    // ── Peer .uitkx virtual document → map back via peer SourceMap ──
                    var (peerPath, peerVDoc) = peerEntry;
                    string peerSource;
                    try { peerSource = File.Exists(peerPath) ? File.ReadAllText(peerPath) : ""; }
                    catch { continue; }

                    var peerUri = DocumentUri.FromFileSystemPath(peerPath);
                    var oldStr = oldText.ToString();
                    var newStr = newText.ToString();
                    int cumulativeShift = 0;

                    foreach (var entry in peerVDoc.Map.Entries)
                    {
                        int oldLen = entry.VirtualEnd - entry.VirtualStart;
                        if (entry.VirtualStart >= oldStr.Length)
                            continue;

                        var oldSlice = oldStr.Substring(
                            entry.VirtualStart,
                            Math.Min(oldLen, oldStr.Length - entry.VirtualStart));

                        int newStart = entry.VirtualStart + cumulativeShift;
                        if (newStart >= newStr.Length)
                            continue;

                        int occurrences = CountOccurrences(oldSlice, oldName);
                        int lengthDelta = occurrences * (newName.Length - oldName.Length);
                        int newLen = oldLen + lengthDelta;

                        if (newStart + newLen > newStr.Length)
                            newLen = newStr.Length - newStart;

                        var newSlice = newStr.Substring(newStart, newLen);

                        if (oldSlice == newSlice)
                        {
                            cumulativeShift += lengthDelta;
                            continue;
                        }

                        int searchPos = 0;
                        while (searchPos < oldSlice.Length)
                        {
                            int idx = oldSlice.IndexOf(oldName, searchPos, StringComparison.Ordinal);
                            if (idx < 0) break;

                            bool leftOk = idx == 0 || !char.IsLetterOrDigit(oldSlice[idx - 1]) && oldSlice[idx - 1] != '_';
                            int endIdx = idx + oldName.Length;
                            bool rightOk = endIdx >= oldSlice.Length || !char.IsLetterOrDigit(oldSlice[endIdx]) && oldSlice[endIdx] != '_';

                            if (leftOk && rightOk)
                            {
                                int peerUitkxOffset = entry.UitkxStart + idx;
                                AddEdit(
                                    changes,
                                    peerUri,
                                    new TextEdit
                                    {
                                        Range = OffsetRangeToLspRange(
                                            peerSource,
                                            peerUitkxOffset,
                                            peerUitkxOffset + oldName.Length),
                                        NewText = newName,
                                    }
                                );
                            }

                            searchPos = idx + oldName.Length;
                        }

                        cumulativeShift += lengthDelta;
                    }
                }
                else
                {
                    // ── Companion .cs document → edits target the real file ──
                    var dir = Path.GetDirectoryName(uitkxFilePath);
                    if (dir == null)
                        continue;

                    var companionPath = Path.Combine(dir, oldDoc.Name);
                    if (!File.Exists(companionPath))
                        continue;

                    var uri = DocumentUri.FromFileSystemPath(companionPath);
                    foreach (var change in textChanges)
                    {
                        var startLinePos = oldText.Lines.GetLinePosition(change.Span.Start);
                        var endLinePos = oldText.Lines.GetLinePosition(
                            change.Span.Start + change.Span.Length
                        );

                        AddEdit(
                            changes,
                            uri,
                            new TextEdit
                            {
                                Range = new LspRange(
                                    new Position(startLinePos.Line, startLinePos.Character),
                                    new Position(endLinePos.Line, endLinePos.Character)
                                ),
                                NewText = change.NewText!,
                            }
                        );
                    }
                }
            }
        }
        if (projectCount == 0)
            ServerLog.Log("[Rename] CollectRoslynEdits: NO project changes found in solution diff");
    }

    /// <summary>
    /// Scans the entire workspace for <c>component</c> declarations and tag
    /// references matching <paramref name="oldName"/> and generates edits to
    /// replace them with <paramref name="newName"/>.
    /// </summary>
    private void CollectComponentRenameEdits(
        string oldName,
        string newName,
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes
    )
    {
        var workspaceRoot = _roslynHost.WorkspaceRoot;
        if (string.IsNullOrEmpty(workspaceRoot))
            return;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(workspaceRoot, "*.uitkx", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var uitkxFile in files)
        {
            if (IsInsideTildeFolder(uitkxFile))
                continue;

            string fileText;
            var fileUri = DocumentUri.FromFileSystemPath(uitkxFile);
            if (!_store.TryGet(fileUri, out fileText!))
            {
                try
                {
                    fileText = File.ReadAllText(uitkxFile);
                }
                catch
                {
                    continue;
                }
            }

            RenameComponentDeclarationInFile(uitkxFile, fileText, oldName, newName, changes);
            RenameTagUsages(uitkxFile, fileText, oldName, newName, changes);
        }
    }

    internal static void RenameComponentDeclarationInFile(
        string filePath,
        string text,
        string oldName,
        string newName,
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes
    )
    {
        // Match the component declaration head, BOTH grammars (ES-modules campaign, M5):
        // `component OldName` / `@component OldName` (legacy) and the plain form
        // `[export] [global::][Ns.]VirtualNode OldName(`. Both grammars prohibit leading
        // whitespace before a top-level declaration head, and the plain form requires the
        // parameter list `(` — otherwise an indented `VirtualNode Foo = …` local in a
        // NON-declaring file could take the per-file declaration edit (audit B6).
        var pattern = new Regex(
            $@"(?:^|\n)(?:export\s+)?(?:(?:@component|component)\s+(?<name>{Regex.Escape(oldName)})\b"
            + $@"|(?:global::)?(?:[\w.]+\.)?VirtualNode\s+(?<name>{Regex.Escape(oldName)})\s*\()",
            RegexOptions.CultureInvariant
        );
        var match = pattern.Match(text);
        if (!match.Success)
            return;

        var group = match.Groups["name"];
        AddEdit(
            changes,
            DocumentUri.FromFileSystemPath(filePath),
            new TextEdit
            {
                Range = OffsetRangeToLspRange(text, group.Index, group.Index + group.Length),
                NewText = newName,
            }
        );
    }

    private static void RenameTagUsages(
        string filePath,
        string text,
        string oldName,
        string newName,
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes
    )
    {
        // Match opening, closing, and self-closing tag names:
        //   <OldName …>  </OldName>  <OldName />
        var tagPattern = new Regex(
            $@"<(/?)({Regex.Escape(oldName)})(?=[\s/>])",
            RegexOptions.CultureInvariant
        );

        var uri = DocumentUri.FromFileSystemPath(filePath);
        foreach (Match m in tagPattern.Matches(text))
        {
            var nameGroup = m.Groups[2];

            // U-40: a plain regex scan renamed <OldName inside // and /* */ comments too
            // (e.g. "// old UI: <OldName/>"). Note this does NOT catch <!-- --> HTML
            // comments — CSharpLexFacts is a C#-literal/comment scanner, not a markup one —
            // so a tag commented out that way still renames; that is a narrower, documented
            // gap rather than the previous "renames inside any comment" behavior.
            if (IsInsideCommentOrString(text, nameGroup.Index))
                continue;

            AddEdit(
                changes,
                uri,
                new TextEdit
                {
                    Range = OffsetRangeToLspRange(
                        text,
                        nameGroup.Index,
                        nameGroup.Index + nameGroup.Length
                    ),
                    NewText = newName,
                }
            );
        }
    }

    // ── Hook rename ─────────────────────────────────────────────────────────

    /// <summary>
    /// Renames a hook declaration and all its usages across .uitkx files
    /// in the same directory. Handles:
    /// <list type="bullet">
    ///   <item><c>hook oldName(...)</c> → <c>hook newName(...)</c> in the declaring file</item>
    ///   <item><c>oldName()</c> call sites in peer component files (setup code, expressions)</item>
    /// </list>
    /// </summary>
    private void CollectHookRenameEdits(
        string hookFilePath,
        string oldName,
        string newName,
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes,
        string? skipFilePath = null
    )
    {
        // U-38: hooks are consumed WORKSPACE-WIDE (any .uitkx file can `using static` a
        // hook container and call the hook) — scanning only the declaring file's own
        // directory silently missed every cross-directory call site. Mirrors
        // CollectComponentRenameEdits's workspace-wide walk.
        var workspaceRoot = _roslynHost.WorkspaceRoot;
        if (string.IsNullOrEmpty(workspaceRoot))
            return;

        ServerLog.Log(
            $"[Rename] CollectHookRenameEdits: '{oldName}' → '{newName}' (workspace-wide), skip={Path.GetFileName(skipFilePath ?? "none")}");

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(workspaceRoot, "*.uitkx", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var uitkxFile in files)
        {
            if (IsInsideTildeFolder(uitkxFile))
                continue;

            // Skip the file that Roslyn already handled (avoids double-edits)
            if (skipFilePath != null && string.Equals(
                    Path.GetFullPath(uitkxFile),
                    Path.GetFullPath(skipFilePath),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            string fileText;
            var fileUri = DocumentUri.FromFileSystemPath(uitkxFile);
            if (!_store.TryGet(fileUri, out fileText!))
            {
                try { fileText = File.ReadAllText(uitkxFile); }
                catch { continue; }
            }

            var uri = DocumentUri.FromFileSystemPath(uitkxFile);
            CollectHookRenameEditsInText(fileText, uri, oldName, newName, changes);
        }
    }

    /// <summary>Per-file text scan of <see cref="CollectHookRenameEdits"/> (pure — directly
    /// unit-testable): the hook declaration head plus `oldName(` call sites.</summary>
    internal static void CollectHookRenameEditsInText(
        string fileText,
        DocumentUri uri,
        string oldName,
        string newName,
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes
    )
    {
        // Rename the plain declaration head `[export] <Type> oldName[<T>](`. It requires
        // a REAL type token before the name (audit B2 — the previous whitespace-tolerant
        // lookbehind let indented statement-shaped `oldName(…)` lines match), and
        // declaration matches get the same comment/string guard the call pattern has, so
        // occurrences inside block comments and multi-line verbatim strings stay untouched.
        // The `)` alternative is the wrapped tuple-return continuation line the formatter
        // emits for over-width heads (`) useX(…` at column 0).
        var hookDeclPattern = new Regex(
            $@"^(?:(?:export\s+)?{LspHelpers.DeclTypePattern}\s+|\)\s*)(?<name>{Regex.Escape(oldName)})(?=\s*(?:<[\w,\s]+>\s*)?\()",
            RegexOptions.CultureInvariant | RegexOptions.Multiline
        );
        foreach (Match m in hookDeclPattern.Matches(fileText))
        {
            var g = m.Groups["name"];
            if (IsInsideCommentOrString(fileText, g.Index))
                continue;

            AddEdit(changes, uri, new TextEdit
            {
                Range = OffsetRangeToLspRange(fileText, g.Index, g.Index + g.Length),
                NewText = newName,
            });
        }

        // Rename `oldName(` / `oldName<T>(` call sites — whole-word followed by an
        // (optionally generic) argument list; a re-match on the declaration head is
        // range-deduplicated by AddEdit.
        var callPattern = new Regex(
            $@"\b{Regex.Escape(oldName)}(?=\s*(?:<[\w,\s]+>\s*)?\()",
            RegexOptions.CultureInvariant
        );
        foreach (Match m in callPattern.Matches(fileText))
        {
            // U-38: a plain regex scan renamed `oldName(` occurrences inside comments
            // and strings too (e.g. `// call useCounter() to see`) — token-boundary +
            // comment/string awareness matches the U-10 fix's semantics.
            if (IsInsideCommentOrString(fileText, m.Index))
                continue;

            AddEdit(changes, uri, new TextEdit
            {
                Range = OffsetRangeToLspRange(fileText, m.Index, m.Index + m.Length),
                NewText = newName,
            });
        }
    }

    /// <summary>
    /// U-38/U-40: true when <paramref name="pos"/> falls inside a comment or string/char
    /// literal in <paramref name="text"/> — used to keep text-scan renames from touching
    /// commented-out or quoted occurrences of an identifier.
    /// </summary>
    private static bool IsInsideCommentOrString(string text, int pos)
    {
        int i = 0;
        while (i < pos && i < text.Length)
        {
            if (CSharpLexFacts.TrySkipNonCode(text, ref i, text.Length))
            {
                if (i > pos)
                    return true;
                continue;
            }
            i++;
        }
        return false;
    }

    // ── Peer symbol rename (module fields/styles) ─────────────────────────────

    /// <summary>
    /// Scans peer .uitkx files in the same directory for bare identifier usages
    /// of a module field/property (e.g. style constants accessed via <c>using static</c>).
    /// Skips the defining file itself (Roslyn already handled it).
    /// </summary>
    private void CollectPeerSymbolRenameEdits(
        string definingFilePath,
        string oldName,
        string newName,
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes
    )
    {
        var dir = Path.GetDirectoryName(definingFilePath);
        if (dir == null) return;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.uitkx"); }
        catch { return; }

        foreach (var uitkxFile in files)
        {
            // Skip the defining file — Roslyn already renamed within it
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

            // Whole-word match for the identifier
            var pattern = new Regex(
                $@"\b{Regex.Escape(oldName)}\b",
                RegexOptions.CultureInvariant
            );

            var uri = DocumentUri.FromFileSystemPath(uitkxFile);
            int editCount = 0;

            foreach (Match m in pattern.Matches(fileText))
            {
                // Skip occurrences in directive lines (@namespace, @using, etc.)
                int lineStart = fileText.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                var linePrefix = fileText.Substring(lineStart, m.Index - lineStart).TrimStart();
                if (linePrefix.StartsWith("@", StringComparison.Ordinal))
                    continue;
                // Skip occurrences in comments
                if (linePrefix.StartsWith("//", StringComparison.Ordinal))
                    continue;

                AddEdit(changes, uri, new TextEdit
                {
                    Range = OffsetRangeToLspRange(fileText, m.Index, m.Index + m.Length),
                    NewText = newName,
                });
                editCount++;
            }

            if (editCount > 0)
                ServerLog.Log(
                    $"[Rename] PeerSymbol: {editCount} edit(s) in {Path.GetFileName(uitkxFile)}");
        }
    }

    // ── Symbol resolution ─────────────────────────────────────────────────────

    private static ISymbol? ResolveSymbol(
        Document doc,
        int virtualOffset,
        CancellationToken ct
    ) => LspHelpers.ResolveSymbol(doc, virtualOffset, ct);

    private static bool IsRenameable(ISymbol symbol)
    {
        // Metadata-only symbols (BCL types, Unity API, etc.) cannot be renamed
        if (symbol.Locations.Length > 0 && symbol.Locations[0].IsInMetadata)
            return false;

        // Compiler-generated or scaffold symbols (__ prefix)
        if (symbol.IsImplicitlyDeclared)
            return false;
        if (symbol.Name.StartsWith("__", StringComparison.Ordinal))
            return false;

        // Built-in property names on the generated props class
        if (symbol.Name == "Key" || symbol.Name == "Ref")
        {
            if (symbol.ContainingType?.Name?.EndsWith("Props") == true)
                return false;
        }

        return true;
    }

    // ── Helpers (delegated to LspHelpers) ──────────────────────────────────

    /// <summary>
    /// After a successful rename, snapshot the post-rename text of every
    /// changed companion .cs document into the RoslynHost overlay so the
    /// next workspace rebuild doesn't read stale content from disk.
    /// </summary>
    private void SnapshotCompanionOverlay(
        Solution oldSolution,
        Solution newSolution,
        DocumentId mainDocId,
        string uitkxFilePath)
    {
        var dir = Path.GetDirectoryName(uitkxFilePath);
        if (dir == null) return;

        foreach (var projectChanges in newSolution.GetChanges(oldSolution).GetProjectChanges())
        {
            foreach (var docId in projectChanges.GetChangedDocuments())
            {
                if (docId == mainDocId) continue;

                var newDoc = newSolution.GetDocument(docId);
                if (newDoc == null) continue;

                var companionPath = Path.Combine(dir, newDoc.Name);
#pragma warning disable VSTHRD002
                var newText = newDoc.GetTextAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                _roslynHost.SetCompanionOverlay(companionPath, newText.ToString());
            }
        }
    }

    private static int CountOccurrences(string text, string word)
        => LspHelpers.CountOccurrences(text, word);

    private static void AddEdit(
        Dictionary<DocumentUri, IEnumerable<TextEdit>> changes,
        DocumentUri uri,
        TextEdit edit
    )
    {
        if (!changes.TryGetValue(uri, out var existing))
        {
            changes[uri] = new List<TextEdit> { edit };
        }
        else
        {
            var list = (List<TextEdit>)existing;
            // Deduplicate: skip if an edit with the same range already exists
            foreach (var e in list)
            {
                if (e.Range.Start.Line == edit.Range.Start.Line
                    && e.Range.Start.Character == edit.Range.Start.Character
                    && e.Range.End.Line == edit.Range.End.Line
                    && e.Range.End.Character == edit.Range.End.Character)
                    return;
            }
            list.Add(edit);
        }
    }

    private bool TryGetText(DocumentUri uri, string localPath, out string text)
        => LspHelpers.TryGetText(_store, uri, localPath, out text);

    private static ParseResult ParseText(string text, string filePath)
        => LspHelpers.ParseText(text, filePath);

    private static string? UriToPath(DocumentUri uri)
        => LspHelpers.UriToPath(uri);

    private static int ToOffset(string text, Position position)
        => LspHelpers.ToOffset(text, position);

    private static Position OffsetToPosition(string text, int offset)
        => LspHelpers.OffsetToPosition(text, offset);

    private static LspRange OffsetRangeToLspRange(string text, int start, int end)
        => LspHelpers.OffsetRangeToLspRange(text, start, end);

    private static (string Word, int Start, int End) GetWordAtOffset(string text, int offset)
        => LspHelpers.GetWordAtOffset(text, offset);

    private static bool IsIdentChar(char c) => LspHelpers.IsIdentChar(c);

    private static bool IsOnTagName(string text, int offset, string word)
        => LspHelpers.IsOnTagName(text, offset, word);

    private static bool IsValidIdentifier(string name)
        => LspHelpers.IsValidIdentifier(name);

    private static bool IsInsideTildeFolder(string filePath)
        => LspHelpers.IsInsideTildeFolder(filePath);
}
