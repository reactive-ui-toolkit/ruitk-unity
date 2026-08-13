using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Ruitk.Language;
using Ruitk.Language.Lowering;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using UitkxLanguageServer;
using UitkxLanguageServer.Roslyn;
using Xunit;

namespace UitkxLanguageServer.Tests;

/// <summary>
/// Tests for <see cref="RoslynCompletionProvider"/> in-process.
/// Serialised with RoslynHostTests to avoid MefHostServices.DefaultHost race.
/// </summary>
[Collection("Roslyn")]
public sealed class RoslynCompletionTests : IAsyncLifetime
{
    private RoslynHost _host = null!;
    private RoslynCompletionProvider _provider = null!;

    public Task InitializeAsync()
    {
        // ILanguageServerFacade is stored but never called in the hot paths.
        _host = new RoslynHost(null!, new UitkxSchema(), new WorkspaceIndex());
        _host.SetWorkspaceRoot(null); // BCL-only references
        _provider = new RoslynCompletionProvider(_host);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ParseResult Parse(string source, string path = "Test.uitkx")
    {
        var diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, path, diags);
        var parsedNodes = UitkxParser.Parse(source, path, directives, diags);
        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, path);
        return new ParseResult(directives, nodes, ImmutableArray.CreateRange(diags));
    }

    private async Task<IReadOnlyList<CompletionItem>> GetCompletions(
        string source, int cursorOffset, string path = "c:/test/Test.uitkx", char? trigger = null)
    {
        var parseResult = Parse(source, path);
        return await _provider.GetCompletionsAsync(
            path, source, parseResult, cursorOffset, trigger, CancellationToken.None);
    }

    /// <summary>
    /// Parse source with a | pipe cursor marker, returning (cleanSource, offset).
    /// </summary>
    private static (string source, int offset) ExtractCursor(string raw)
    {
        int idx = raw.IndexOf('|');
        Assert.True(idx >= 0, "Test source must contain a | cursor marker");
        return (raw.Remove(idx, 1), idx);
    }

    // ── Expression attribute completions ───────────────────────────────────

    [Fact]
    public async Task ExpressionAttribute_DotCompletion_ReturnsMethods()
    {
        var raw = "VirtualNode C() {\n  string msg = \"hello\";\n  return (\n    <Label text={msg.|}/>\n  )\n}";
        var (source, offset) = ExtractCursor(raw);
        var items = await GetCompletions(source, offset, trigger: '.');
        Assert.True(items.Count > 0, "Expected dot-completion to return items");
        Assert.Contains(items, i => i.Label == "Length");
    }

    [Fact]
    public async Task ExpressionAttribute_DotCompletion_HasToString()
    {
        var raw = "VirtualNode C() {\n  int count = 42;\n  return (\n    <Label text={count.|}/>\n  )\n}";
        var (source, offset) = ExtractCursor(raw);
        var items = await GetCompletions(source, offset, trigger: '.');
        Assert.Contains(items, i => i.Label == "ToString");
    }

    // ── Code block completions ─────────────────────────────────────────────

    [Fact]
    public async Task CodeBlock_DotCompletion_StringMethods()
    {
        // Simpler code-block test: string variable dot-completion
        var raw = "VirtualNode C() {\n  string s = \"hello\";\n  var x = s.|;\n  return (\n    <Label/>\n  )\n}";
        var (source, offset) = ExtractCursor(raw);
        var items = await GetCompletions(source, offset, trigger: '.');
        Assert.True(items.Count > 0, "Expected code-block dot-completion items");
        Assert.Contains(items, i => i.Label == "Length");
    }

    // ── Function-style completions ─────────────────────────────────────────

    [Fact]
    public async Task FunctionStyle_SetupCode_DotCompletion()
    {
        var raw = "VirtualNode Counter() {\n  string s = \"test\";\n  var len = s.|;\n  return (\n    <Label/>\n  );\n}";
        var (source, offset) = ExtractCursor(raw);
        var items = await GetCompletions(source, offset, trigger: '.');
        Assert.True(items.Count > 0, "Expected function-style setup code completions");
        Assert.Contains(items, i => i.Label == "Length");
    }

    // ── No completions in non-C# regions ───────────────────────────────────

    [Fact]
    public async Task MarkupRegion_NoCompletions()
    {
        // Cursor is inside markup text, not a C# expression — should return empty.
        var raw = "VirtualNode C() {\n  return (\n    <Label text=\"hel|lo\"/>\n  )\n}";
        var (source, offset) = ExtractCursor(raw);
        var items = await GetCompletions(source, offset);
        Assert.Empty(items);
    }

    // ── Keyword completions in code blocks ─────────────────────────────────

    [Fact]
    public async Task CodeBlock_IntDotCompletion()
    {
        // Completions after int variable in a code block, verifying non-dot context also works
        var raw = "VirtualNode C() {\n  int count = 0;\n  var s = count.|;\n  return (\n    <Label/>\n  )\n}";
        var (source, offset) = ExtractCursor(raw);
        var items = await GetCompletions(source, offset, trigger: '.');
        Assert.True(items.Count > 0, "Expected dot completions on int in code block");
        Assert.Contains(items, i => i.Label == "ToString");
    }

    // ── Inline expression completions ──────────────────────────────────────

    [Fact]
    public async Task InlineExpression_DotCompletion()
    {
        var raw = "VirtualNode C() {\n  string greeting = \"hi\";\n  return (\n    <Box>\n      {greeting.|}\n    </Box>\n  )\n}";
        var (source, offset) = ExtractCursor(raw);
        var items = await GetCompletions(source, offset, trigger: '.');
        Assert.True(items.Count > 0, "Expected inline expression dot-completions");
        Assert.Contains(items, i => i.Label == "ToUpper");
    }

    // ── useRef completions ─────────────────────────────────────────────────

    [Fact]
    public async Task FunctionStyle_UseRef_DotCompletion_ShowsCurrentAndValue()
    {
        var raw = "VirtualNode Test() {\n  var myRef = useRef(false);\n  var x = myRef.|;\n  return (\n    <Label/>\n  );\n}";
        var (source, offset) = ExtractCursor(raw);
        var items = await GetCompletions(source, offset, trigger: '.');
        Assert.True(items.Count > 0, "Expected useRef dot-completion items");
        Assert.Contains(items, i => i.Label == "Current");
        Assert.Contains(items, i => i.Label == "Value");
    }

    [Fact]
    public async Task FunctionStyle_UseRef_ReturnTypeIsCanonicalRefT()
    {
        // TD-S1 regression: useRef<T>() must return global::Ruitk.Core.Ref<T>
        // (the workspace-shared canonical type), not a per-document nested stub.
        // This guarantees that values flow without CS1503 to peer hooks
        // declaring `Ref<T>` parameters.
        var raw = "VirtualNode Test() {\n  var myRef = useRef<int>(0);\n  var t = myRef.GetType().FullName;\n  return (\n    <Label text={t}/>\n  );\n}";
        // Force virtual doc generation by requesting completions at an arbitrary point.
        var items = await GetCompletions(raw, raw.IndexOf("myRef.G", System.StringComparison.Ordinal) + 6, trigger: '.');
        Assert.Contains(items, i => i.Label == "GetType"); // myRef.GetType bound → type resolved
        // Inspect the generated virtual doc directly to lock the canonical type name.
        var doc = _host.GetRoslynDocument("c:/test/Test.uitkx");
        Assert.NotNull(doc);
        var text = (await doc!.GetTextAsync()).ToString();
        Assert.Contains("global::Ruitk.Core.Ref<T> useRef<T>", text);
        Assert.DoesNotContain("__UitkxRef__", text);
    }
}
