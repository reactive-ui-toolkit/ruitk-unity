using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ruitk.Language;
using Ruitk.Language.Lowering;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using UitkxLanguageServer.Roslyn;
using Xunit;

namespace UitkxLanguageServer.Tests;

// LSP-side parity for SG cross-namespace hook resolution (Issue #18, reshaped by
// the 0.16.0 legacy wave): the virtual document must carry the import-driven
// `using static <HookNs>.__Exports;` payload (shared ImportScopeFacts route) for
// every peer hook file the consumer explicitly imports, regardless of whether it
// shares the consumer's namespace. Mirrors the SG's ResolveInjectedUsings.
[Collection("Roslyn")]
public sealed class HookCrossNamespaceVirtualDocTests : IAsyncLifetime, IDisposable
{
    private RoslynHost _host = null!;
    private string _tempDir = null!;

    public Task InitializeAsync()
    {
        _host = new RoslynHost(null!, new UitkxSchema(), new WorkspaceIndex());
        _host.SetWorkspaceRoot(null);
        _tempDir = Path.Combine(Path.GetTempPath(),
            "uitkx_lsp_xns_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task PeerHook_InDifferentNamespace_StillContributesUsingStatic()
    {
        // Consumer in MyApp.UI consumes a hook declared in MyApp.Hooks. Both
        // files sit in the same temp directory so FindPeerUitkxFiles' same-dir
        // scan picks the hook up without any WorkspaceIndex priming.
        var hookContent =
            "@namespace MyApp.Hooks\n\n"
            + "export bool useFlag() {\n"
            + "  var (v, _) = useState(false);\n"
            + "  return v;\n"
            + "}\n";
        var consumerContent =
            "import { useFlag } from \"./UseFlag.hooks\"\n"
            + "@namespace MyApp.UI\n\n"
            + "VirtualNode ConsumerComp() {\n"
            + "  return (\n"
            + "    <VisualElement />\n"
            + "  );\n"
            + "}\n";

        string hookPath = Path.Combine(_tempDir, "UseFlag.hooks.uitkx");
        string consumerPath = Path.Combine(_tempDir, "ConsumerComp.uitkx");
        File.WriteAllText(hookPath, hookContent);
        File.WriteAllText(consumerPath, consumerContent);

        var parseResult = Parse(consumerContent, consumerPath);
        await _host.EnsureReadyAsync(consumerPath, consumerContent, parseResult, CancellationToken.None);

        var vdoc = _host.GetVirtualDocument(consumerPath);
        Assert.NotNull(vdoc);
        // FQN must use the HOOK file's namespace, not the consumer's; the container
        // is the per-file __Exports (0.16.0 - the legacy {Stem}Hooks container is gone).
        Assert.True(vdoc!.Text.Contains("MyApp.Hooks.__Exports"), vdoc.Text);
    }

    [Fact]
    public async Task StamplessPeerHook_UsesEffectiveNamespace_NotParserDefault()
    {
        // Path-derived world (no @namespace stamps, uitkx.config.json namespacePrefix —
        // the bundled-samples shape since 0.8.2): the injected using-static must name the
        // hook container's EFFECTIVE namespace. The RAW parsed namespace is the parser
        // default `Ruitk.FunctionStyle`, where no real container ever exists — a
        // second phantom container there made every companion-hook call CS0121-ambiguous
        // in the editor while the build was clean.
        // An .asmdef makes the temp dir a resolvable derivation anchor (without one —
        // no asmdef, no Assets/ ancestor — Resolve correctly degrades to the raw value).
        File.WriteAllText(Path.Combine(_tempDir, "Test.asmdef"), "{ \"name\": \"TestAsm\" }\n");
        File.WriteAllText(Path.Combine(_tempDir, "uitkx.config.json"),
            "{ \"namespacePrefix\": \"TestApp.Derived\" }\n");
        var hookContent =
            "export bool useFlag() {\n"
            + "  var (v, _) = useState(false);\n"
            + "  return v;\n"
            + "}\n";
        var consumerContent =
            "import { useFlag } from \"./UseFlag.hooks\"\n"
            + "VirtualNode ConsumerComp() {\n"
            + "  return (\n"
            + "    <VisualElement />\n"
            + "  );\n"
            + "}\n";

        string hookPath = Path.Combine(_tempDir, "UseFlag.hooks.uitkx");
        string consumerPath = Path.Combine(_tempDir, "ConsumerComp.uitkx");
        File.WriteAllText(hookPath, hookContent);
        File.WriteAllText(consumerPath, consumerContent);

        var parseResult = Parse(consumerContent, consumerPath);
        await _host.EnsureReadyAsync(consumerPath, consumerContent, parseResult, CancellationToken.None);

        var vdoc = _host.GetVirtualDocument(consumerPath);
        Assert.NotNull(vdoc);
        // Effective (derived) namespace + the per-file __Exports container (0.16.0).
        Assert.Contains("TestApp.Derived.", vdoc!.Text);
        Assert.Contains("__Exports", vdoc.Text);
        Assert.DoesNotContain("Ruitk.FunctionStyle.", vdoc.Text);
    }

    [Fact]
    public async Task GoToDefinition_OnExportedHookCall_JumpsToHookDeclaration()
    {
        // `export hook useX()` — the `export ` prefix (0.7.0 grammar) must not break
        // DefinitionHandler.FindDeclarationInUitkx's keyword match. Before the fix,
        // go-to-definition on any EXPORTED hook's call site silently returned nothing:
        // the hook method's signature is scaffold (no source-map entry), so the peer
        // text-search fallback was the only route — and it only knew bare `hook `.
        var hookContent =
            "export bool useFlag() {\n"
            + "  var (v, _) = useState(false);\n"
            + "  return v;\n"
            + "}\n";
        var consumerContent =
            "import { useFlag } from \"./UseFlag.hooks\"\n\n"
            + "VirtualNode ConsumerComp() {\n"
            + "  var flag = useFlag();\n"
            + "  return (\n"
            + "    <VisualElement />\n"
            + "  );\n"
            + "}\n";

        string hookPath = Path.Combine(_tempDir, "UseFlag.hooks.uitkx");
        string consumerPathRaw = Path.Combine(_tempDir, "ConsumerComp.uitkx");
        File.WriteAllText(hookPath, hookContent);
        File.WriteAllText(consumerPathRaw, consumerContent);

        // Use the exact path shape the handler derives from the URI (Uri.LocalPath)
        // so the host's per-file registry lookups match.
        var uri = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri
            .FromFileSystemPath(consumerPathRaw);
        string consumerPath = new Uri(uri.ToString()).LocalPath;

        var parseResult = Parse(consumerContent, consumerPath);
        await _host.EnsureReadyAsync(consumerPath, consumerContent, parseResult, CancellationToken.None);

        // Cursor inside the CALL `useFlag()` (line 4, not the import line).
        int callOffset = consumerContent.IndexOf("useFlag()", StringComparison.Ordinal);
        int line0 = consumerContent.Substring(0, callOffset).Count(c => c == '\n');
        int lineStart = consumerContent.LastIndexOf('\n', callOffset - 1) + 1;
        int char0 = callOffset - lineStart + 2;

        var store = new DocumentStore();
        store.Set(uri, consumerContent);
        var handler = new DefinitionHandler(store, new WorkspaceIndex(), _host);

        var result = await handler.Handle(
            new OmniSharp.Extensions.LanguageServer.Protocol.Models.DefinitionParams
            {
                TextDocument = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                    .TextDocumentIdentifier(uri),
                Position = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                    .Position(line0, char0),
            },
            CancellationToken.None);

        Assert.NotNull(result);
        var loc = result!.First().Location;
        Assert.NotNull(loc);
        Assert.EndsWith("UseFlag.hooks.uitkx", loc!.Uri.GetFileSystemPath(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, (int)loc.Range.Start.Line); // decl is line 1 (0-based 0)
    }

    [Fact]
    public async Task GoToDefinition_OnFirstMemberOfStyleFile_ReturnsCursorForReferencesFallback()
    {
        // Field find (StressTest.style.uitkx): a member-only file carries
        // FunctionSetupStartLine == MarkupStartLine == its FIRST declaration's line
        // with EMPTY setup code, so the setup-var scan's derived range collapsed
        // onto exactly that line and `export Style container =` false-matched the
        // `Type name =` variable shape — the click on the FIRST member jumped
        // within its own line instead of returning the exact cursor position that
        // triggers VS Code's Find All References fallback. Later members (outside
        // the one-line range) always worked.
        var styleContent =
            "export Style container = new Style {\n"
            + "  Padding = 10f,\n"
            + "};\n"
            + "\n"
            + "export Style headerBar = new Style {\n"
            + "  Margin = 4f,\n"
            + "};\n";

        string stylePathRaw = Path.Combine(_tempDir, "Panel.style.uitkx");
        File.WriteAllText(stylePathRaw, styleContent);
        var uri = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri
            .FromFileSystemPath(stylePathRaw);

        var store = new DocumentStore();
        store.Set(uri, styleContent);
        var handler = new DefinitionHandler(store, new WorkspaceIndex(), _host);

        // Mid-name cursors: `container` line 0 col 15, `headerBar` line 4 col 15.
        foreach (var (line0, char0) in new[] { (0, 15), (4, 15) })
        {
            var result = await handler.Handle(
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.DefinitionParams
                {
                    TextDocument = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                        .TextDocumentIdentifier(uri),
                    Position = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                        .Position(line0, char0),
                },
                CancellationToken.None);

            Assert.NotNull(result);
            var loc = result!.First().Location;
            Assert.NotNull(loc);
            // The EXACT cursor position must come back — that is what makes the
            // client run Find All References instead of jumping the cursor.
            Assert.Equal(line0, (int)loc!.Range.Start.Line);
            Assert.Equal(char0, (int)loc.Range.Start.Character);
        }
    }

    [Fact]
    public async Task References_OnStyleFileExports_FindConsumerUsages()
    {
        // Field repro (TicTacToe.style.uitkx): Find All References on the FIRST
        // export of a member-only style file returned nothing while later exports
        // worked. Mirrors the real server flow — the handler ensures the Roslyn
        // vdoc itself, so the C# path (not just the text fallback) is exercised,
        // with the consumer present as a same-dir peer.
        var styleContent =
            "import \"@static Ruitk.Props.Typed.StyleKeys\"\n"
            + "\n"
            + "export Style containerStyle = new Style\n"
            + "{\n"
            + "  FlexGrow = 1,\n"
            + "};\n"
            + "\n"
            + "export Style ButtonStyles = new Style\n"
            + "{\n"
            + "  Padding = 10f,\n"
            + "};\n";
        var consumerContent =
            "import { ButtonStyles, containerStyle } from \"./Board.style\"\n"
            + "\n"
            + "export VirtualNode Board() {\n"
            + "  return (\n"
            + "    <VisualElement style={containerStyle}>\n"
            + "      <Button style={ButtonStyles} />\n"
            + "    </VisualElement>\n"
            + "  );\n"
            + "}\n";

        string stylePathRaw = Path.Combine(_tempDir, "Board.style.uitkx");
        string consumerPathRaw = Path.Combine(_tempDir, "Board.uitkx");
        File.WriteAllText(stylePathRaw, styleContent);
        File.WriteAllText(consumerPathRaw, consumerContent);

        var styleUri = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri
            .FromFileSystemPath(stylePathRaw);
        var consumerUri = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri
            .FromFileSystemPath(consumerPathRaw);

        var store = new DocumentStore();
        store.Set(styleUri, styleContent);
        store.Set(consumerUri, consumerContent);
        var handler = new ReferencesHandler(store, new WorkspaceIndex(), _host);

        foreach (var name in new[] { "containerStyle", "ButtonStyles" })
        {
            int declOffset = styleContent.IndexOf(name, StringComparison.Ordinal);
            int line0 = styleContent.Substring(0, declOffset).Count(c => c == '\n');
            int lineStart = styleContent.LastIndexOf('\n', declOffset - 1) + 1;
            int char0 = declOffset - lineStart + 2;

            var result = await handler.Handle(
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.ReferenceParams
                {
                    TextDocument = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                        .TextDocumentIdentifier(styleUri),
                    Position = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                        .Position(line0, char0),
                    Context = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                        .ReferenceContext { IncludeDeclaration = true },
                },
                CancellationToken.None);

            int inConsumer = result == null ? 0 : result.Count(l =>
                l.Uri.GetFileSystemPath().EndsWith(
                    "Board.uitkx", StringComparison.OrdinalIgnoreCase));
            Assert.True(inConsumer > 0,
                $"{name}: expected consumer references, got "
                + (result == null ? "null" : $"{result.Count()} location(s), none in Board.uitkx"));
        }
    }

    [Fact]
    public async Task Rename_HookFromCallSite_AlsoRenamesImportListName()
    {
        // F2 on the CALL site renames the declaration (hook path) and every C#
        // usage (Roslyn) — but `import { useFlag }` is uitkx-only preamble syntax
        // that neither collector can see. The import-list pass must patch it, or
        // the rename leaves the file broken (import binds a name that no longer
        // exists → UITKX2300).
        var hookContent =
            "export bool useFlag() {\n"
            + "  var (v, _) = useState(false);\n"
            + "  return v;\n"
            + "}\n";
        var consumerContent =
            "import { useFlag } from \"./UseFlag.hooks\"\n\n"
            + "VirtualNode ConsumerComp() {\n"
            + "  var flag = useFlag();\n"
            + "  return (\n"
            + "    <VisualElement />\n"
            + "  );\n"
            + "}\n";

        string hookPathRaw = Path.Combine(_tempDir, "UseFlag.hooks.uitkx");
        string consumerPathRaw = Path.Combine(_tempDir, "ConsumerComp.uitkx");
        File.WriteAllText(hookPathRaw, hookContent);
        File.WriteAllText(consumerPathRaw, consumerContent);

        var uri = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri
            .FromFileSystemPath(consumerPathRaw);
        string consumerPath = new Uri(uri.ToString()).LocalPath;

        var parseResult = Parse(consumerContent, consumerPath);
        await _host.EnsureReadyAsync(consumerPath, consumerContent, parseResult, CancellationToken.None);

        int callOffset = consumerContent.IndexOf("useFlag()", StringComparison.Ordinal);
        int line0 = consumerContent.Substring(0, callOffset).Count(c => c == '\n');
        int lineStart = consumerContent.LastIndexOf('\n', callOffset - 1) + 1;
        int char0 = callOffset - lineStart + 2;

        var store = new DocumentStore();
        store.Set(uri, consumerContent);
        var handler = new RenameHandler(store, new WorkspaceIndex(), _host, null!);

        var edit = await handler.Handle(
            new OmniSharp.Extensions.LanguageServer.Protocol.Models.RenameParams
            {
                TextDocument = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                    .TextDocumentIdentifier(uri),
                Position = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                    .Position(line0, char0),
                NewName = "useFlagRenamed",
            },
            CancellationToken.None);

        Assert.NotNull(edit);
        Assert.NotNull(edit!.Changes);
        // The consumer file's edits must include one on LINE 0 (the import line)
        // replacing the imported name.
        var consumerEdits = edit.Changes!
            .First(kvp => kvp.Key.GetFileSystemPath()
                .EndsWith("ConsumerComp.uitkx", StringComparison.OrdinalIgnoreCase))
            .Value.ToList();
        Assert.Contains(consumerEdits, e =>
            e.Range.Start.Line == 0 && e.NewText == "useFlagRenamed");
    }

    [Fact]
    public async Task Formatting_ModuleCompanionFile_FixesIndentation()
    {
        // Style companion (modern export value, 0.16.0) with a misindented closing
        // `};` — the formatting handler must return an edit that canonicalizes it
        // (field-reported as "formatting doesn't work" on a .style.uitkx).
        var styleContent =
            "export Style container = new Style {\n"
            + "  BackgroundColor = ColorGray,\n"
            + "  Padding = 10f,\n"
            + "    };\n";
        string stylePathRaw = Path.Combine(_tempDir, "HmrTests.style.uitkx");
        File.WriteAllText(stylePathRaw, styleContent);
        var uri = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri
            .FromFileSystemPath(stylePathRaw);

        var store = new DocumentStore();
        store.Set(uri, styleContent);
        var handler = new FormattingHandler(store);

        var edits = await handler.Handle(
            new OmniSharp.Extensions.LanguageServer.Protocol.Models.DocumentFormattingParams
            {
                TextDocument = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                    .TextDocumentIdentifier(uri),
                Options = new OmniSharp.Extensions.LanguageServer.Protocol.Models
                    .FormattingOptions { TabSize = 2, InsertSpaces = true },
            },
            CancellationToken.None);

        Assert.NotNull(edits);
        Assert.Contains(edits!, e => e.NewText.Contains("  };"));
    }

    private static ParseResult Parse(string source, string path)
    {
        var diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, path, diags);
        var parsedNodes = UitkxParser.Parse(source, path, directives, diags);
        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, path);
        return new ParseResult(directives, nodes, ImmutableArray.CreateRange(diags));
    }
}
