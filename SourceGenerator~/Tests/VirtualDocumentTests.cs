using System.Collections.Generic;
using System.Collections.Immutable;
using Ruitk.Language;
using Ruitk.Language.Lowering;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using Ruitk.Language.Roslyn;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Tests for <see cref="VirtualDocumentGenerator"/> — verifies that the generated
/// virtual C# document:
///   1) Contains the expected C# scaffolding and user expressions.
///   2) Produces a <see cref="SourceMap"/> that bidirectionally maps positions
///      between .uitkx offsets and virtual C# offsets.
/// Pure language-lib tests (no LSP, no Roslyn compilation).
/// </summary>
public sealed class VirtualDocumentTests
{
    private static readonly VirtualDocumentGenerator _gen = new();

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ParseResult Parse(string source, string path = "Test.uitkx")
    {
        var diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, path, diags);
        var parsedNodes = UitkxParser.Parse(source, path, directives, diags);
        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, path);
        return new ParseResult(directives, nodes, ImmutableArray.CreateRange(diags));
    }

    private static VirtualDocument Generate(string source, string path = "Test.uitkx") =>
        _gen.Generate(Parse(source, path), source, path);

    // ── Basic scaffolding ──────────────────────────────────────────────────

    [Fact]
    public void BasicScaffolding_ContainsNamespace()
    {
        var source = "VirtualNode Foo() {\n  return (\n    <Label text=\"hi\"/>\n  );\n}";
        var doc = Generate(source);
        Assert.Contains("namespace Ruitk.FunctionStyle", doc.Text);
    }

    [Fact]
    public void BasicScaffolding_ContainsClassName()
    {
        var source = "VirtualNode Foo() {\n  return (\n    <Label text=\"hi\"/>\n  );\n}";
        var doc = Generate(source);
        Assert.Contains("Foo", doc.Text);
    }

    [Fact]
    public void BasicScaffolding_MapIsNotEmpty()
    {
        var source = "VirtualNode Foo() {\n  return (\n    <Label text={myVar}/>\n  );\n}";
        var doc = Generate(source);
        Assert.True(doc.Map.Entries.Length > 0,
            "Expected at least one mapped entry for an expression attribute");
    }

    // ── Expression embedding ───────────────────────────────────────────────

    [Fact]
    public void ExpressionAttribute_MappedInVirtualDoc()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text={myVar}/>\n  );\n}";
        var doc = Generate(source);
        Assert.Contains("myVar", doc.Text);
    }

    [Fact]
    public void InlineExpression_MappedInVirtualDoc()
    {
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      {someExpr}\n    </Box>\n  );\n}";
        var doc = Generate(source);
        Assert.Contains("someExpr", doc.Text);
    }

    // ── Setup code in component body ───────────────────────────────────────

    [Fact]
    public void CodeBlock_VerbatimCopied()
    {
        var source = "VirtualNode C() {\n  int counter = 42;\n  return (\n    <Label/>\n  );\n}";
        var doc = Generate(source);
        Assert.Contains("counter = 42", doc.Text);
    }

    // ── Function-style component ───────────────────────────────────────────

    [Fact]
    public void FunctionStyle_SetupCodeCopied()
    {
        var source = "VirtualNode Counter() {\n  int x = 10;\n  return (\n    <Label/>\n  );\n}";
        var doc = Generate(source);
        Assert.Contains("int x = 10", doc.Text);
    }

    // ── Source map: bidirectional ───────────────────────────────────────────

    [Fact]
    public void SourceMap_RoundTripsExpressionOffset()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text={myVar}/>\n  );\n}";
        var doc = Generate(source);

        // Find "myVar" in the uitkx source
        int uitkxIdx = source.IndexOf("myVar");
        Assert.True(uitkxIdx >= 0, "myVar must appear in source");

        // Map to virtual
        var toVirtual = doc.Map.ToVirtualOffset(uitkxIdx);
        Assert.NotNull(toVirtual);

        // The virtual offset should point at "myVar" in virtual doc
        Assert.True(doc.Text.Length > toVirtual.Value.VirtualOffset);
        var virtualSub = doc.Text.Substring(toVirtual.Value.VirtualOffset, 5);
        Assert.Equal("myVar", virtualSub);

        // Round-trip back
        var backToUitkx = doc.Map.ToUitkxOffset(toVirtual.Value.VirtualOffset);
        Assert.NotNull(backToUitkx);
        Assert.Equal(uitkxIdx, backToUitkx.Value.UitkxOffset);
    }

    // ── U-33: @switch expression is now Roslyn-mapped ───────────────────────

    [Fact]
    public void SwitchExpression_MappedInVirtualDoc()
    {
        // Before U-33 the virtual doc never emitted the switch expression at
        // all, so it had no Roslyn-backed hover/diagnostics/go-to-def.
        var source =
            "VirtualNode C() {\n  string mode = \"a\";\n  return (\n    @switch (mode) {\n      @case \"a\":\n        <Label/>\n    }\n  );\n}";
        var doc = Generate(source);
        Assert.Contains("mode", doc.Text);
    }

    [Fact]
    public void SwitchExpression_SourceMap_RoundTrips()
    {
        var source =
            "VirtualNode C() {\n  string switchVar = \"a\";\n  return (\n    @switch (switchVar) {\n      @case \"a\":\n        <Label/>\n    }\n  );\n}";
        var doc = Generate(source);

        int uitkxIdx = source.LastIndexOf("switchVar"); // the usage inside @switch(...), not the declaration
        Assert.True(uitkxIdx >= 0);

        var toVirtual = doc.Map.ToVirtualOffset(uitkxIdx);
        Assert.NotNull(toVirtual);

        var virtualSub = doc.Text.Substring(toVirtual.Value.VirtualOffset, "switchVar".Length);
        Assert.Equal("switchVar", virtualSub);

        var backToUitkx = doc.Map.ToUitkxOffset(toVirtual.Value.VirtualOffset);
        Assert.NotNull(backToUitkx);
        Assert.Equal(uitkxIdx, backToUitkx.Value.UitkxOffset);
    }

    [Fact]
    public void SourceMap_NonCSharpRegion_ReturnsNull()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text=\"plain\"/>\n  );\n}";
        var doc = Generate(source);

        // "component" keyword is not C#; should not map
        var result = doc.Map.ToVirtualOffset(0);
        Assert.Null(result);
    }

    [Fact]
    public void SourceMap_CodeBlockRegion_RoundTrips()
    {
        var source = "VirtualNode C() {\n  int val = 99;\n  return (\n    <Label/>\n  );\n}";
        var doc = Generate(source);

        int uitkxIdx = source.IndexOf("val = 99");
        Assert.True(uitkxIdx >= 0);

        var toVirtual = doc.Map.ToVirtualOffset(uitkxIdx);
        Assert.NotNull(toVirtual);
        Assert.Equal(SourceRegionKind.FunctionSetup, toVirtual.Value.Entry.Kind);

        var virtualSub = doc.Text.Substring(toVirtual.Value.VirtualOffset, 3);
        Assert.Equal("val", virtualSub);
    }

    [Fact]
    public void SourceMap_FunctionSetup_RegionKind()
    {
        var source = "VirtualNode Foo() {\n  int z = 7;\n  return (\n    <Label/>\n  );\n}";
        var doc = Generate(source);

        int uitkxIdx = source.IndexOf("int z = 7");
        Assert.True(uitkxIdx >= 0);

        var toVirtual = doc.Map.ToVirtualOffset(uitkxIdx);
        Assert.NotNull(toVirtual);
        Assert.Equal(SourceRegionKind.FunctionSetup, toVirtual.Value.Entry.Kind);
    }

    // ── IsInCSharpRegion ───────────────────────────────────────────────────

    [Fact]
    public void IsInCSharpRegion_TrueForExpression()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text={myExpr}/>\n  );\n}";
        var doc = Generate(source);

        int idx = source.IndexOf("myExpr");
        Assert.True(doc.Map.IsInCSharpRegion(idx));
    }

    [Fact]
    public void IsInCSharpRegion_FalseForMarkup()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text=\"plain\"/>\n  );\n}";
        var doc = Generate(source);

        int idx = source.IndexOf("<Label");
        Assert.False(doc.Map.IsInCSharpRegion(idx));
    }

    // ── Multiple expressions ───────────────────────────────────────────────

    [Fact]
    public void MultipleExpressions_AllMapped()
    {
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      <Label text={aaa}/>\n      <Label text={bbb}/>\n    </Box>\n  );\n}";
        var doc = Generate(source);

        Assert.Contains("aaa", doc.Text);
        Assert.Contains("bbb", doc.Text);

        Assert.True(doc.Map.IsInCSharpRegion(source.IndexOf("aaa")));
        Assert.True(doc.Map.IsInCSharpRegion(source.IndexOf("bbb")));
    }

    // ── Phase 1 — JSX literals embedded in expressions ────────────────────

    /// <summary>
    /// Phase 1: when JSX literals appear inside an arbitrary C# expression
    /// (ternary branches, attribute expressions, lambda bodies), the virtual
    /// document must replace them with a <c>(VirtualNode)null!</c> stub so
    /// Roslyn can parse and type-check the surrounding C# without emitting
    /// phantom errors on the JSX. The user's surrounding code stays
    /// source-mapped so completions and squiggles still work outside the JSX.
    /// </summary>
    [Fact]
    public void JsxInChildTernary_StubbedInVirtualDoc()
    {
        var source =
            "VirtualNode C() {\n"
            + "  bool flag = true;\n"
            + "  return (\n"
            + "    <Box>{flag ? <Label/> : null}</Box>\n"
            + "  );\n"
            + "}";
        var doc = Generate(source);

        // Surrounding C# is preserved.
        Assert.Contains("flag", doc.Text);
        // Raw JSX must not leak — Roslyn would reject it.
        Assert.DoesNotContain("<Label/>", doc.Text);
        // Stub must appear so Roslyn parses cleanly.
        Assert.Contains("(global::Ruitk.Core.VirtualNode)null!", doc.Text);
    }

    [Fact]
    public void JsxInAttributeTernary_StubbedInVirtualDoc()
    {
        var source =
            "VirtualNode C() {\n"
            + "  bool flag = true;\n"
            + "  return (\n"
            + "    <Label text={flag ? \"on\" : \"off\"}/>\n"
            + "  );\n"
            + "}";
        var doc = Generate(source);

        // No JSX in this attribute — scanner returns empty, single Mapped
        // segment as before Phase 1.
        Assert.Contains("\"on\"", doc.Text);
        Assert.Contains("\"off\"", doc.Text);
    }

    [Fact]
    public void NoJsxInExpression_VirtualDocUnchanged()
    {
        // Phase 1 must not regress the no-JSX path: scanner runs, finds
        // nothing, single Mapped segment is emitted (same as pre-Phase-1).
        var source =
            "VirtualNode C() {\n"
            + "  int n = 42;\n"
            + "  return (\n"
            + "    <Label text={n.ToString()}/>\n"
            + "  );\n"
            + "}";
        var doc = Generate(source);

        Assert.Contains("n.ToString()", doc.Text);
        // The stub must NOT appear when there's no JSX.
        Assert.DoesNotContain("(global::Ruitk.Core.VirtualNode)null!", doc.Text);
    }
}
