using System.Collections.Generic;
using System.Collections.Immutable;
using Ruitk.Language;
using Ruitk.Language.IntelliSense;
using Ruitk.Language.Lowering;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Tests for <see cref="AstCursorContext"/> — verifies that cursor positions
/// are correctly classified for IntelliSense (completions, hover, go-to-def).
/// Pure language-lib tests (no LSP, no Roslyn).
/// </summary>
public sealed class CursorContextTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static ParseResult Parse(string source)
    {
        var diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, "test.uitkx", diags);
        var parsedNodes = UitkxParser.Parse(source, "test.uitkx", directives, diags);
        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, "test.uitkx");
        return new ParseResult(directives, nodes, ImmutableArray.CreateRange(diags));
    }

    /// <summary>
    /// Locate the cursor from a pipe character <c>|</c> embedded in the source.
    /// Returns the source with <c>|</c> removed, plus the 1-based line and 0-based column.
    /// </summary>
    private static (string source, int line1, int col0) ExtractCursor(string raw)
    {
        int idx = raw.IndexOf('|');
        Assert.True(idx >= 0, "Test source must contain a | cursor marker");
        string source = raw.Remove(idx, 1);

        int line1 = 1;
        int col0 = 0;
        for (int i = 0; i < idx; i++)
        {
            if (raw[i] == '\n')
            {
                line1++;
                col0 = 0;
            }
            else
            {
                col0++;
            }
        }
        return (source, line1, col0);
    }

    private static CursorContext FindAtPipe(string rawSource)
    {
        var (source, line1, col0) = ExtractCursor(rawSource);
        return AstCursorContext.Find(Parse(source), source, line1, col0);
    }

    // ── Tag names ──────────────────────────────────────────────────────────

    [Fact]
    public void TagName_AfterOpenAngle()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <|Label/>\n  );\n}");
        Assert.Equal(CursorKind.TagName, ctx.Kind);
    }

    [Fact]
    public void TagName_MidElement()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Lab|el/>\n  );\n}");
        Assert.Equal(CursorKind.TagName, ctx.Kind);
    }

    [Fact]
    public void TagName_AtEnd()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Label| />\n  );\n}");
        Assert.Equal(CursorKind.TagName, ctx.Kind);
    }

    // ── Attribute names ────────────────────────────────────────────────────

    [Fact]
    public void AttributeName_AfterSpace()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Label |text=\"hi\"/>\n  );\n}");
        Assert.Equal(CursorKind.AttributeName, ctx.Kind);
    }

    [Fact]
    public void AttributeName_MidWord()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Label te|xt=\"hi\"/>\n  );\n}");
        Assert.Equal(CursorKind.AttributeName, ctx.Kind);
    }

    [Fact]
    public void AttributeName_TagNamePreserved()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Label te|xt=\"hi\"/>\n  );\n}");
        Assert.Equal("Label", ctx.TagName);
    }

    // ── Attribute values ───────────────────────────────────────────────────

    [Fact]
    public void AttributeValue_InsideQuotes()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Label text=\"h|i\"/>\n  );\n}");
        Assert.Equal(CursorKind.AttributeValue, ctx.Kind);
    }

    [Fact]
    public void AttributeValue_InsideBraces()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Label text={|val}/>\n  );\n}");
        Assert.Equal(CursorKind.AttributeValue, ctx.Kind);
    }

    // ── Control flow ───────────────────────────────────────────────────────

    [Fact]
    public void ControlFlowName_AfterAtInMarkup()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Box>\n    @|if (true) {\n      <Label/>\n    }\n    </Box>\n  );\n}");
        Assert.Equal(CursorKind.ControlFlowName, ctx.Kind);
    }

    [Fact]
    public void ControlFlowName_MidKeyword()
    {
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Box>\n    @fore|ach (var x in items) {\n      <Label key={x}/>\n    }\n    </Box>\n  );\n}");
        Assert.Equal(CursorKind.ControlFlowName, ctx.Kind);
    }

    // ── CSharp expression ──────────────────────────────────────────────────

    [Fact]
    public void CSharpExpression_InlineAtExpr()
    {
        // {expr} in markup body — an inline C# expression
        var ctx = FindAtPipe("VirtualNode C() {\n  return (\n    <Box>\n      {my|Var}\n    </Box>\n  );\n}");
        Assert.Equal(CursorKind.CSharpExpression, ctx.Kind);
    }

    // ── U-32: multi-line child expression, resolved via real AST spans ──────

    [Fact]
    public void CSharpExpression_MultiLineChildExpression_ContinuationLine()
    {
        // The '{' opens on its own line; the cursor sits two lines later, still
        // inside the same (unterminated-on-this-line) expression. Resolved via
        // the ExpressionNode's tracked ExpressionOffset/ExpressionLength span,
        // not a text-based brace scan.
        var ctx = FindAtPipe(
            "VirtualNode C() {\n  return (\n    <Box>\n      {\n        fl|ag\n          ? <Label/>\n          : null\n      }\n    </Box>\n  );\n}");
        Assert.Equal(CursorKind.CSharpExpression, ctx.Kind);
    }

    [Fact]
    public void CSharpExpression_MultiLineChildExpression_LastLine()
    {
        // Cursor on the line containing the closing '}' — still inside the span.
        var ctx = FindAtPipe(
            "VirtualNode C() {\n  return (\n    <Box>\n      {\n        flag\n          ? <Label/>\n          : nu|ll\n      }\n    </Box>\n  );\n}");
        Assert.Equal(CursorKind.CSharpExpression, ctx.Kind);
    }

    [Fact]
    public void CSharpExpression_OutsideMultiLineExpression_NotMisclassified()
    {
        // A cursor on a line OUTSIDE the expression's span (past the closing '}',
        // inside the enclosing <Box>'s own body) must not be swept in by an
        // overbroad match — regression guard for the reverted brace-depth attempt.
        var ctx = FindAtPipe(
            "VirtualNode C() {\n  return (\n    <Box>\n      {\n        flag\n          ? <Label/>\n          : null\n      }\n      <La|bel/>\n    </Box>\n  );\n}");
        Assert.NotEqual(CursorKind.CSharpExpression, ctx.Kind);
    }

    // ── Function-style component ───────────────────────────────────────────

    [Fact]
    public void CSharpCodeBlock_FunctionSetup()
    {
        var ctx = FindAtPipe("VirtualNode Foo() {\n  int x| = 5;\n  return (\n    <Label/>\n  );\n}");
        Assert.Equal(CursorKind.CSharpCodeBlock, ctx.Kind);
    }

    [Fact]
    public void TagName_FunctionReturnMarkup()
    {
        var ctx = FindAtPipe("VirtualNode Foo() {\n  return (\n    <Lab|el/>\n  );\n}");
        Assert.Equal(CursorKind.TagName, ctx.Kind);
    }
}
