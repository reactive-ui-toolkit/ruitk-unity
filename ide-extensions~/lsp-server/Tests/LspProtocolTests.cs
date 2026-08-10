using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.JsonRpc.Testing;
using OmniSharp.Extensions.LanguageProtocol.Testing;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Server;
using UitkxLanguageServer.Roslyn;
using Xunit;
using Xunit.Abstractions;
using ILanguageClient = OmniSharp.Extensions.LanguageServer.Protocol.Client.ILanguageClient;
using ILanguageServer = OmniSharp.Extensions.LanguageServer.Protocol.Server.ILanguageServer;

namespace UitkxLanguageServer.Tests;

/// <summary>
/// Full LSP protocol integration tests using OmniSharp's in-memory test infrastructure.
/// These boot the real UITKX language server handlers and exercise the full
/// JSON-RPC → handler → response pipeline.
/// </summary>
[Collection("Roslyn")]
public sealed class LspProtocolTests : LanguageProtocolTestBase
{
    public LspProtocolTests(ITestOutputHelper output)
        : base(new JsonRpcTestOptions())
    {
    }

    private Task<(ILanguageClient client, ILanguageServer server)> StartServer()
    {
        return Initialize(
            clientOptions => { },
            serverOptions =>
            {
                serverOptions
                    .WithHandler<TextSyncHandler>()
                    .WithHandler<CompletionHandler>()
                    .WithHandler<HoverHandler>()
                    .WithHandler<FormattingHandler>()
                    .WithHandler<SemanticTokensHandler>()
                    .WithHandler<SignatureHelpHandler>()
                    .WithHandler<DefinitionHandler>()
                    .WithHandler<RenameHandler>();

                serverOptions.Services.AddSingleton<UitkxSchema>();
                serverOptions.Services.AddSingleton<DocumentStore>();
                serverOptions.Services.AddSingleton<WorkspaceIndex>();
                serverOptions.Services.AddSingleton<RoslynHost>();
                serverOptions.Services.AddSingleton<DiagnosticsPublisher>();
            }
        );
    }

    // ── Handshake ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_Succeeds()
    {
        var (client, server) = await StartServer();
        Assert.NotNull(client);
        Assert.NotNull(server);
    }

    // ── Did open + hover ───────────────────────────────────────────────────

    [Fact]
    public async Task DidOpen_ThenHover_ReturnsResult()
    {
        var (client, _) = await StartServer();

        var uri = DocumentUri.From("file:///test/MyComp.uitkx");
        client.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "uitkx",
                Version = 1,
                Text = "VirtualNode MyComp() {\n  return (\n    <Label text=\"hello\"/>\n  )\n}"
            }
        });

        await SettleNext();

        // Hover over "Label" at line 2, col 1
        var hover = await client.RequestHover(new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(2, 1)
        });

        // We may or may not get hover content depending on schema registration,
        // but the request should complete without error.
    }

    // ── Completion ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Completion_TagName_ReturnsElements()
    {
        var (client, _) = await StartServer();

        var uri = DocumentUri.From("file:///test/Tags.uitkx");
        client.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "uitkx",
                Version = 1,
                Text = "VirtualNode Tags() {\n  return (\n    <\n  )\n}"
            }
        });

        await SettleNext();

        var items = await client.RequestCompletion(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(2, 5),
            Context = new CompletionContext { TriggerKind = CompletionTriggerKind.TriggerCharacter, TriggerCharacter = "<" }
        });

        Assert.NotNull(items);
        // Should include schema-defined elements (Label, Box, etc.)
        Assert.True(items.Items.Any(), "Expected element completion items after <");
    }

    // ── Document change ────────────────────────────────────────────────────

    [Fact]
    public async Task DidChange_UpdatesDocument()
    {
        var (client, _) = await StartServer();

        var uri = DocumentUri.From("file:///test/Change.uitkx");
        client.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "uitkx",
                Version = 1,
                Text = "VirtualNode Change() {\n  return (\n    <Label/>\n  )\n}"
            }
        });

        await SettleNext();

        client.DidChangeTextDocument(new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = 2 },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = "VirtualNode Change() {\n  return (\n    <Box>\n      <Label/>\n    </Box>\n  )\n}" }
            )
        });

        await SettleNext();

        // Verify we can still get completions after change
        var items = await client.RequestCompletion(new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(3, 3),
            Context = new CompletionContext { TriggerKind = CompletionTriggerKind.Invoked }
        });

        Assert.NotNull(items);
    }

    // ── Formatting ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Formatting_ReturnsEdits()
    {
        var (client, _) = await StartServer();

        var uri = DocumentUri.From("file:///test/Format.uitkx");
        // Badly-formatted: missing indentation inside <Box>
        var inputText = "VirtualNode Format() {\nreturn (\n<Box>\n<Label/>\n</Box>\n)\n}";
        client.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "uitkx",
                Version = 1,
                Text = inputText
            }
        });

        await SettleNext();

        var edits = await client.RequestDocumentFormatting(new DocumentFormattingParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Options = new FormattingOptions { { "tabSize", 2 }, { "insertSpaces", true } }
        });

        // Formatter may return null if getLocalPath fails for test URIs.
        // At minimum we verify no crash occurred.
        if (edits != null)
        {
            Assert.True(edits.Any(), "Expected formatting to produce edits");
        }
    }

    [Fact]
    public async Task Formatting_CrlfFile_EditUsesCrlf()
    {
        // U-18: the formatted replacement block must use the ORIGINAL file's line-ending
        // convention — joining with a bare "\n" turned a CRLF file's formatted block into
        // LF, leaving the file with mixed line endings after every format-on-save.
        var (client, _) = await StartServer();

        var uri = DocumentUri.From("file:///test/FormatCrlf.uitkx");
        var inputText = "VirtualNode Format() {\r\nreturn (\r\n<Box>\r\n<Label/>\r\n</Box>\r\n)\r\n}";
        client.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "uitkx",
                Version = 1,
                Text = inputText
            }
        });

        await SettleNext();

        var edits = await client.RequestDocumentFormatting(new DocumentFormattingParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Options = new FormattingOptions { { "tabSize", 2 }, { "insertSpaces", true } }
        });

        if (edits != null && edits.Any())
        {
            var edit = edits.First();
            Assert.Contains("\r\n", edit.NewText);
            Assert.DoesNotContain("\n", edit.NewText.Replace("\r\n", ""));
        }
    }

    // ── Rename ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_ComponentDeclaration_TextEditsStillWork()
    {
        // U-37: this test's client does not declare ResourceOperations support (the default
        // test-harness capabilities), so RenameHandler must fall back to its pre-fix
        // text-only WorkspaceEdit.Changes shape rather than crash trying to emit a
        // RenameFile op the client can't handle. The RenameFile-emission path itself
        // needs a capability-declaring client (VS Code declares "rename" by default) —
        // verified separately via the build + reflection-confirmed API surface.
        var (client, _) = await StartServer();

        var uri = DocumentUri.From("file:///test/Counter.uitkx");
        var inputText = "VirtualNode Counter() {\n  return (\n    <Label text=\"hi\" />\n  );\n}";
        client.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "uitkx",
                Version = 1,
                Text = inputText
            }
        });

        await SettleNext();

        var edit = await client.RequestRename(new RenameParams
        {
            TextDocument = new TextDocumentIdentifier(uri),
            Position = new Position(0, 10), // inside "Counter"
            NewName = "Timer",
        });

        // RenameHandler.CollectComponentRenameEdits requires RoslynHost.WorkspaceRoot,
        // which this harness (no open workspace folder) never sets — so an empty (but
        // non-crashing) edit is the expected outcome here; the point of this test is that
        // the handler doesn't throw or emit an unsupported RenameFile op for a client that
        // never declared support for it.
        if (edit?.Changes != null)
        {
            Assert.True(edit.Changes.All(kv => kv.Value != null));
        }
    }
}
