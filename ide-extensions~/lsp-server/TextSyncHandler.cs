using System;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using UitkxLanguageServer.Roslyn;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;

namespace UitkxLanguageServer;

/// <summary>
/// Minimal text-document sync handler: tracks open / change / close.
/// Full incremental sync is not required — we always use the full document text.
/// </summary>
public sealed class TextSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly DocumentStore _store;
    private readonly DiagnosticsPublisher _diagnostics;
    private readonly RoslynHost _roslynHost;
    private readonly WorkspaceIndex _index;

    // Per-path debouncers for buffer-driven index refreshes (VE-07): didChange
    // arrives per keystroke; the index re-parse rides a 300 ms trailing edge so
    // typing never pays for it inline. IndexChanged only fires when the file's
    // cross-file-visible surface actually changed (see ComputeFileSurface).
    private static readonly object s_refreshLock = new();
    private static readonly Dictionary<string, CancellationTokenSource> s_pendingRefresh =
        new(StringComparer.OrdinalIgnoreCase);
    private const int IndexRefreshDebounceMs = 300;

    public TextSyncHandler(
        DocumentStore store,
        DiagnosticsPublisher diagnostics,
        RoslynHost roslynHost,
        WorkspaceIndex index
    )
    {
        _store       = store;
        _diagnostics = diagnostics;
        _roslynHost  = roslynHost;
        _index       = index;
    }

    private void ScheduleIndexRefresh(string path)
    {
        CancellationTokenSource cts;
        lock (s_refreshLock)
        {
            if (s_pendingRefresh.TryGetValue(path, out var old))
                old.Cancel();
            cts = new CancellationTokenSource();
            s_pendingRefresh[path] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IndexRefreshDebounceMs, cts.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
            lock (s_refreshLock)
            {
                if (s_pendingRefresh.TryGetValue(path, out var current) && current == cts)
                    s_pendingRefresh.Remove(path);
                else
                    return;
            }
            try { _index.Refresh(path); }
            catch (Exception ex) { ServerLog.Log($"buffer index refresh failed for '{path}': {ex.Message}"); }
        });
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        var path = uri.GetFileSystemPath();
        if (path != null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return new TextDocumentAttributes(uri, "csharp");
        return new TextDocumentAttributes(uri, "uitkx");
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities
    ) =>
        new TextDocumentSyncRegistrationOptions
        {
            DocumentSelector = new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.uitkx" },
                new TextDocumentFilter { Pattern = "**/*.cs" }
            ),
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = false },
        };

    public override Task<Unit> Handle(
        DidOpenTextDocumentParams request,
        CancellationToken cancellationToken
    )
    {
        ServerLog.Log(
            $"didOpen: {request.TextDocument.Uri}  lang={request.TextDocument.LanguageId}  len={request.TextDocument.Text?.Length}"
        );
        _store.Set(request.TextDocument.Uri, request.TextDocument.Text ?? string.Empty);

        // Only trigger diagnostics/rebuild for .uitkx files.
        var path = request.TextDocument.Uri.GetFileSystemPath();
        if (path == null || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            _diagnostics.Publish(request.TextDocument.Uri, request.TextDocument.Text ?? string.Empty, _roslynHost);

        // Buffer-first index (VE-07): an opened document — including one that has
        // never been saved to disk — contributes its exports/props to menus and
        // the workspace graph immediately.
        if (path != null && path.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
            _index.Refresh(path);

        return Unit.Task;
    }

    public override Task<Unit> Handle(
        DidChangeTextDocumentParams request,
        CancellationToken cancellationToken
    )
    {
        var text = request.ContentChanges.LastOrDefault()?.Text ?? "";
        _store.Set(request.TextDocument.Uri, text);

        var path = request.TextDocument.Uri.GetFileSystemPath();
        if (path != null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // Companion .cs changed — store the overlay inline (cheap), then let the
            // dependent .uitkx files refresh via the debounced revalidation. Publishing
            // them inline here serialized seconds of analysis into the notification
            // pipeline and starved queued requests (see ScheduleRevalidation's doc).
            _roslynHost.SetCompanionOverlay(path, text);
            _diagnostics.ScheduleRevalidation();
        }
        else
        {
            // The CHANGED file keeps its immediate synchronous publish — per-keystroke
            // feedback on the file being edited must not lag behind a debounce.
            _diagnostics.Publish(request.TextDocument.Uri, text, _roslynHost);

            // When a .uitkx peer file changes, invalidate dependent workspaces (cheap
            // dictionary ops) but refresh their diagnostics via the debounced
            // revalidation instead of inline — a companion consumed by open files
            // otherwise turns every keystroke/save into a full dependents re-analysis
            // INSIDE the notification handler, and a queued textDocument/formatting
            // (format-on-save) misses the client budget: the edits come back correct
            // but VS Code has already saved unformatted and drops them.
            if (path != null && path.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
            {
                var invalidated = _roslynHost.InvalidatePeerDependents(path);
                if (invalidated.Count > 0)
                    _diagnostics.ScheduleRevalidation();

                ScheduleIndexRefresh(path);
            }
        }

        return Unit.Task;
    }

    public override Task<Unit> Handle(
        DidSaveTextDocumentParams request,
        CancellationToken cancellationToken
    ) => Unit.Task;

    public override Task<Unit> Handle(
        DidCloseTextDocumentParams request,
        CancellationToken cancellationToken
    )
    {
        _store.Remove(request.TextDocument.Uri);
        // Release Roslyn workspace resources for this file.
        var localPath = request.TextDocument.Uri.ToUri().LocalPath;
        if (!string.IsNullOrEmpty(localPath))
        {
            _roslynHost.CloseDocument(localPath);

            // U-15: closing a companion .cs file must clear its overlay (else stale
            // post-rename text could re-surface on a later reopen) and re-publish
            // dependents so their workspace picks up the DocumentStore/disk content
            // instead of a now-orphaned overlay — mirrors DidChange's .cs branch.
            if (localPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                _roslynHost.ClearCompanionOverlay(localPath);
                foreach (var uitkxPath in _roslynHost.FindUitkxFilesForCompanion(localPath))
                {
                    var uitkxUri = DocumentUri.FromFileSystemPath(uitkxPath);
                    if (_store.TryGet(uitkxUri, out var uitkxText) && uitkxText != null)
                        _diagnostics.Publish(uitkxUri, uitkxText, _roslynHost);
                }
            }
        }
        // LSP small: evict cached diagnostics + clear the client's Problems panel for this
        // file (see DiagnosticsPublisher.Forget's doc comment).
        _diagnostics.Forget(request.TextDocument.Uri);

        // Buffer-first index (VE-07): with the overlay gone the file re-indexes
        // from disk — or, for a never-saved session file, evicts entirely.
        if (localPath != null && localPath.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase))
            _index.Refresh(localPath);

        return Unit.Task;
    }
}
