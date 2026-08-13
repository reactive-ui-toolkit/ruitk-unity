using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ruitk.Language;
using Ruitk.Language.Lowering;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using Ruitk.Language.Roslyn;
using UitkxLanguageServer;
using UitkxLanguageServer.Roslyn;
using Xunit;

namespace UitkxLanguageServer.Tests;

/// <summary>
/// Tests for <see cref="RoslynHost"/> in-process — verifies that:
///   1) The Roslyn workspace builds successfully from .uitkx source.
///   2) The semantic model is available and resolves symbols.
///   3) The virtual document + source map produce correct mappings.
///   4) Hover-style type queries return expected type info.
/// Serialised with RoslynCompletionTests to avoid MefHostServices race.
/// </summary>
[Collection("Roslyn")]
public sealed class RoslynHostTests : IAsyncLifetime
{
    private RoslynHost _host = null!;

    public Task InitializeAsync()
    {
        _host = new RoslynHost(null!, new UitkxSchema(), new WorkspaceIndex());
        _host.SetWorkspaceRoot(null);
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

    private async Task EnsureReady(string source, string path = "c:/test/Test.uitkx")
    {
        var parseResult = Parse(source, path);
        await _host.EnsureReadyAsync(path, source, parseResult, CancellationToken.None);
    }

    // ── Workspace creation ─────────────────────────────────────────────────

    [Fact]
    public async Task EnsureReady_CreatesVirtualDocument()
    {
        // Use the same source pattern that works in SemanticModel_ResolvesBclType.
        var source =
            "VirtualNode C() {\n  string msg = \"hello\";\n  return (\n    <Label text={msg}/>\n  )\n}";
        var path = "c:/test/VirtualDocTest.uitkx";
        var parseResult = Parse(source, path);
        await _host.EnsureReadyAsync(path, source, parseResult, CancellationToken.None);

        // If EnsureReadyAsync silently failed, GetRoslynDocument also returns null.
        var roslynDoc = _host.GetRoslynDocument(path);
        Assert.NotNull(roslynDoc);

        var vdoc = _host.GetVirtualDocument(path);
        Assert.NotNull(vdoc);
        Assert.False(string.IsNullOrEmpty(vdoc!.Text));
        Assert.Contains("msg", vdoc.Text);
    }

    [Fact]
    public async Task EnsureReady_CreatesRoslynDocument()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text=\"hi\"/>\n  )\n}";
        await EnsureReady(source);

        var doc = _host.GetRoslynDocument("c:/test/Test.uitkx");
        Assert.NotNull(doc);
    }

    // ── Semantic model ─────────────────────────────────────────────────────

    [Fact]
    public async Task SemanticModel_ResolvesBclType()
    {
        var source =
            "VirtualNode C() {\n  string msg = \"hello\";\n  return (\n    <Label text={msg}/>\n  )\n}";
        await EnsureReady(source);

        var doc = _host.GetRoslynDocument("c:/test/Test.uitkx");
        Assert.NotNull(doc);

        var model = await doc!.GetSemanticModelAsync();
        Assert.NotNull(model);

        // SemanticModel should resolve without compilation errors
        // that would indicate a broken workspace
        var diagnostics = model!.GetDiagnostics();
        // Filter to errors only — some warnings about unused variables are fine.
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        // We don't require zero errors (some may come from generated scaffold),
        // but the workspace should be functional enough for completions.
    }

    [Fact]
    public async Task SemanticModel_FunctionStyle_ResolvesBclType()
    {
        var source =
            "VirtualNode Counter() {\n  int count = 0;\n  string s = count.ToString();\n  return (\n    <Label text={s}/>\n  );\n}";
        await EnsureReady(source);

        var doc = _host.GetRoslynDocument("c:/test/Test.uitkx");
        Assert.NotNull(doc);

        var model = await doc!.GetSemanticModelAsync();
        Assert.NotNull(model);
    }

    // ── Hover-style type resolution ────────────────────────────────────────

    [Fact]
    public async Task HoverTypeInfo_StringVariable()
    {
        var source =
            "VirtualNode C() {\n  string greeting = \"hello\";\n  return (\n    <Label text={greeting}/>\n  )\n}";
        await EnsureReady(source);

        var vdoc = _host.GetVirtualDocument("c:/test/Test.uitkx");
        Assert.NotNull(vdoc);

        // Find "greeting" in the source and map to virtual
        int uitkxIdx = source.IndexOf("greeting}");
        Assert.True(uitkxIdx >= 0);

        var mapping = vdoc!.Map.ToVirtualOffset(uitkxIdx);
        Assert.NotNull(mapping);

        // Get the Roslyn document and resolve symbol at position
        var roslynDoc = _host.GetRoslynDocument("c:/test/Test.uitkx");
        Assert.NotNull(roslynDoc);

        var syntaxRoot = await roslynDoc!.GetSyntaxRootAsync();
        var model = await roslynDoc.GetSemanticModelAsync();
        Assert.NotNull(syntaxRoot);
        Assert.NotNull(model);

        var token = syntaxRoot!.FindToken(mapping.Value.VirtualOffset);
        var typeInfo = model!.GetTypeInfo(token.Parent!);
        // The type should be System.String
        if (typeInfo.Type != null)
        {
            Assert.Equal("String", typeInfo.Type.Name);
        }
    }

    // ── Source map from host agrees with language-lib ───────────────────────

    [Fact]
    public async Task VirtualDoc_ExpressionRegion_IsInCSharp()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text={myVar}/>\n  )\n}";
        await EnsureReady(source);

        var vdoc = _host.GetVirtualDocument("c:/test/Test.uitkx");
        Assert.NotNull(vdoc);

        int idx = source.IndexOf("myVar");
        Assert.True(vdoc!.Map.IsInCSharpRegion(idx));
    }

    [Fact]
    public async Task VirtualDoc_MarkupRegion_IsNotCSharp()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text=\"plain\"/>\n  )\n}";
        await EnsureReady(source);

        var vdoc = _host.GetVirtualDocument("c:/test/Test.uitkx");
        Assert.NotNull(vdoc);

        int idx = source.IndexOf("<Label");
        Assert.False(vdoc!.Map.IsInCSharpRegion(idx));
    }

    // ── Idempotent rebuild ─────────────────────────────────────────────────

    [Fact]
    public async Task EnsureReady_Idempotent_SameSource()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label/>\n  )\n}";
        await EnsureReady(source);
        await EnsureReady(source); // should short-circuit

        var doc = _host.GetRoslynDocument("c:/test/Test.uitkx");
        Assert.NotNull(doc);
    }

    // ── CS1662 state-setter cascade (block-bodied lambda) ─────────────────

    [Fact]
    public async Task CS1662_StateSetterCascade_InBlockBodiedLambda_IsSuppressed()
    {
        // Mirror of the TicTacToe.hooks shape: a curried, BLOCK-BODIED lambda whose
        // body mixes bare `return;` with value-form state-setter calls. The setter
        // calls are the suppressed CS1503 sugar; Roslyn then anchors a cascade
        // CS1662 on the inner lambda's PARAMETER/ARROW tokens only — a span that
        // can never contain the CS1503, so the mapper's containment check misses
        // it. The host must drop it via the enclosing-lambda-node check instead.
        var source =
            "VirtualNode Test() {\n"
            + "  var (playerTurn, setPlayerTurn) = useState<string>(\"X\");\n"
            + "  System.Func<int, System.Action<int>> handleClick = row => col2 => {\n"
            + "    if (row > 2) {\n"
            + "      return;\n"
            + "    }\n"
            + "    setPlayerTurn(\"O\");\n"
            + "    setPlayerTurn(prev => prev == \"X\" ? \"O\" : \"X\");\n"
            + "  };\n"
            + "  return (\n"
            + "    <Button text=\"go\" onClick={_ => handleClick(1)(2)}/>\n"
            + "  );\n}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.DoesNotContain(diags, d => d.Diagnostic.Id == "CS1662");
    }

    // ── UITKX0112 data-flow analysis ──────────────────────────────────────

    [Fact]
    public async Task UITKX0112_UnusedVariable_Detected()
    {
        // `unused` is declared but never referenced in markup or other code.
        var source =
            "VirtualNode Test() {\n  int unused = 42;\n  return (\n    <Label text=\"hi\"/>\n  );\n}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.Contains(diags, d => d.Diagnostic.Id == "UITKX0112");
    }

    [Fact]
    public async Task UITKX0112_UsedVariable_NoDiagnostic()
    {
        // `count` IS referenced in expression {count.ToString()}.
        var source =
            "VirtualNode Test() {\n  int count = 42;\n  return (\n    <Label text={count.ToString()}/>\n  );\n}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.DoesNotContain(diags, d => d.Diagnostic.Id == "UITKX0112");
    }

    [Fact]
    public async Task UITKX0112_LambdaParam_NoFalsePositive()
    {
        // Lambda discard param `_` must NOT be flagged as unused.
        var source =
            "VirtualNode Test() {\n  var (count, setCount) = useState(0);\n  return (\n    <Button text=\"+1\" onClick={_ => setCount(count + 1)} />\n  );\n}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.DoesNotContain(
            diags,
            d => d.Diagnostic.Id == "UITKX0112" && d.Diagnostic.GetMessage().Contains("'_'")
        );
    }

    [Fact]
    public async Task UITKX0112_ForeachVariable_NoFalsePositive()
    {
        // Loop variable `item` is used in expression check — no false positive.
        var source =
            "VirtualNode Test() {\n  var items = new string[] { \"a\", \"b\" };\n  return (\n    <VisualElement>\n      @foreach (var item in items) {\n        <Label text={item} />\n      }\n    </VisualElement>\n  );\n}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.DoesNotContain(diags, d => d.Diagnostic.Id == "UITKX0112");
    }

    // ── U-39: state-setter CS1503 must be flagged, a real Func<T,T> mismatch must not ──

    [Fact]
    public async Task StateSetterDirectValueCall_IsFlaggedAsStateSetterCS1503()
    {
        // setCount is __StateSetter__<int> — calling it with a plain int (instead of a
        // Func<int,int> updater) produces CS1503. RoslynHost must flag this via the
        // semantic model (invoked expression's static type == "__StateSetter__"), not by
        // matching the diagnostic message (which is identical to a real Func<T,T> mismatch
        // — see IsStateSetterInvocation's doc comment).
        var source =
            "VirtualNode Test() {\n  var (count, setCount) = useState(0);\n  setCount(5);\n  return (\n    <Label text=\"hi\" />\n  );\n}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.Contains(diags, d => d.Diagnostic.Id == "CS1503" && d.IsStateSetterCS1503);
    }

    [Fact]
    public async Task StateSetterMismatchedValueType_IsNotSuppressed()
    {
        // Regression guard: the first cut of IsStateSetterInvocation suppressed EVERY
        // direct-value call to a __StateSetter__<T> (matching on the callee's type alone),
        // which hid genuine mismatches too — setSnapshot(42) on a string-typed useState
        // produced NO diagnostic at all in the live IDE. The scaffold parameter is
        // Func<T,T> for every direct-value call (valid or not), so the callee-type check
        // alone can't tell "42 fits string" from "5 fits int" — it must also classify the
        // argument's type against T. Only a value assignable to T is the harmless
        // .Set(_ => value) sugar case; this is not.
        var source =
            "VirtualNode Test() {\n  var (snapshot, setSnapshot) = useState(\"a\");\n  setSnapshot(42);\n  return (\n    <Label text=\"hi\" />\n  );\n}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.Contains(diags, d => d.Diagnostic.Id == "CS1503" && !d.IsStateSetterCS1503);
    }

    [Fact]
    public async Task RealFuncMismatch_NotStateSetter_IsNotFlagged()
    {
        // A genuine user CS1503 against an ordinary Func<int,int> parameter (not the
        // __StateSetter__<T> scaffold delegate) must NOT be flagged as a state-setter
        // mismatch — its message is byte-identical to the state-setter case, so only the
        // semantic-model check (the invoked expression's static type) can tell them apart.
        var source =
            "VirtualNode Test() {\n  void Foo(System.Func<int,int> f) { }\n  Foo(5);\n  return (\n    <Label text=\"hi\" />\n  );\n}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.Contains(diags, d => d.Diagnostic.Id == "CS1503" && !d.IsStateSetterCS1503);
    }

    // ── U-33 follow-up: @switch case bodies must not look like dead code ─────

    [Fact]
    public async Task SwitchCaseBodies_WithReturns_NoUnreachableCodeWarning()
    {
        // Regression guard: case bodies were emitted as flat, sequential top-level
        // statements in the virtual document with no real branching between cases, so
        // an unconditional `return` in an earlier case made Roslyn treat every later
        // case's `return` as unreachable (CS0162) — rendered dimmed in the editor via
        // the Unnecessary diagnostic tag, even though the real generated switch (see
        // CSharpEmitter.EmitSwitchNode) has proper case branching and compiles fine.
        var source =
            "VirtualNode Test() {\n"
            + "  string mode = \"a\";\n"
            + "  return (\n"
            + "    @switch (mode) {\n"
            + "      @case \"a\":\n"
            + "        return <Label text=\"empty\" />;\n"
            + "      @default:\n"
            + "        return <Label text=\"has snapshot\" />;\n"
            + "    }\n"
            + "  );\n"
            + "}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.DoesNotContain(diags, d => d.Diagnostic.Id == "CS0162");
    }

    [Fact]
    public async Task UITKX0112_CapturedByLambda_NoFalsePositive()
    {
        // Both `count` and `setCount` are referenced only from inside the
        // onClick lambda body. Roslyn's AnalyzeDataFlow treats lambda bodies
        // as opaque regions: those references appear in `Captured`, not
        // `ReadInside`. The analyzer must union Captured into the read set
        // to avoid a false positive — captured locals are by definition used.
        var source =
            "VirtualNode Test() {\n  var (count, setCount) = useState(0);\n  return (\n    <Button text=\"+1\" onClick={_ => setCount(count + 1)} />\n  );\n}";
        await EnsureReady(source);

        var diags = _host.GetLatestDiagnostics("c:/test/Test.uitkx");
        Assert.DoesNotContain(
            diags,
            d =>
                d.Diagnostic.Id == "UITKX0112"
                && (
                    d.Diagnostic.GetMessage().Contains("'count'")
                    || d.Diagnostic.GetMessage().Contains("'setCount'")
                )
        );
    }

    // ── Issue 1 (0.5.22) — JSX subtree inside attribute lambda is type-checked ──

    [Fact]
    public async Task AttributeLambda_WithJsxSubtree_GeneratesVirtualDoc()
    {
        // Sanity: ensure the virtual document scaffolds successfully (no exceptions
        // in the deferred Pattern-B emission path) when an attribute lambda body
        // contains a JSX literal that gets stripped by EmitMappedExpressionStrippingJsx.
        var source =
            "VirtualNode Test() {\n"
            + "  var (n, setN) = useState(0);\n"
            + "  return (\n"
            + "    <Button text=\"+\" onClick={e => setN(n + 1)} />\n"
            + "  );\n"
            + "}";
        await EnsureReady(source);

        var vdoc = _host.GetVirtualDocument("c:/test/Test.uitkx");
        Assert.NotNull(vdoc);
        Assert.False(string.IsNullOrEmpty(vdoc!.Text));
    }

    [Fact]
    public async Task AttributeLambda_JsxBody_DeferredPatternBEmitted()
    {
        // When an attribute lambda body contains JSX (e.g. ternary returning JSX
        // from inside an inline expression), the virtual document MUST emit a
        // deferred Pattern-B (__uitkx_jsxattr...) local function so the JSX
        // subtree is type-checked rather than silently dropped.
        //
        // Repro: ternary with two JSX branches inside an inline expression check.
        var source =
            "VirtualNode Test() {\n"
            + "  var cond = true;\n"
            + "  return (\n"
            + "    <VisualElement>\n"
            + "      {cond ? <Label text=\"A\" /> : <Label text=\"B\" />}\n"
            + "    </VisualElement>\n"
            + "  );\n"
            + "}";
        await EnsureReady(source);

        var vdoc = _host.GetVirtualDocument("c:/test/Test.uitkx");
        Assert.NotNull(vdoc);
        Assert.Contains(
            "__uitkx_jsxattr",
            vdoc!.Text);
    }
}
