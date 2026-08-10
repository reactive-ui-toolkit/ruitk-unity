using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Ruitk.Language;
using Ruitk.Language.Lowering;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using Ruitk.Language.SemanticTokens;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Tests for <see cref="SemanticTokensProvider"/> — verifies that the correct
/// semantic token types are produced for each UITKX syntax construct.
/// These are pure language-lib tests (no LSP server, no Roslyn).
/// </summary>
public sealed class SemanticTokenTests
{
    private static readonly SemanticTokensProvider _provider = new();

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ParseResult Parse(string source)
    {
        var diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, "test.uitkx", diags);
        var parsedNodes = UitkxParser.Parse(source, "test.uitkx", directives, diags);
        var nodes = CanonicalLowering.LowerToRenderRoots(directives, parsedNodes, "test.uitkx");
        return new ParseResult(directives, nodes, ImmutableArray.CreateRange(diags));
    }

    private static SemanticTokenData[] GetTokens(string source) =>
        _provider.GetTokens(Parse(source), source);

    private static bool HasToken(SemanticTokenData[] tokens, string source, string tokenType, string text)
    {
        var lines = source.Split('\n');
        return tokens.Any(t =>
            t.TokenType == tokenType &&
            t.Line < lines.Length &&
            t.Column + t.Length <= lines[t.Line].Length &&
            lines[t.Line].Substring(t.Column, t.Length) == text);
    }

    // ── Import / export tokens (import/export grammar §10) ──────────────────

    [Fact]
    public void ImportLine_TokenizesImportFromAndSpecifier()
    {
        var source = "import { useCounter } from \"./Counter.hooks\"\nexport VirtualNode Screen() {\n    return (<Box />);\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Keyword, "import"));
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Keyword, "from"));
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.String, "\"./Counter.hooks\""));
    }

    [Fact]
    public void ExportPrefix_TokenizedAsKeyword()
    {
        var source = "export VirtualNode Screen() {\n    return (<Box />);\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Keyword, "export"));
    }

    // ── Element & attribute tests ──────────────────────────────────────────

    [Fact]
    public void ElementName_IsElement()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text=\"hi\"/>\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Element, "Label"),
            "Expected Label to be a uitkxElement token");
    }

    [Fact]
    public void AttributeName_IsAttribute()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text=\"hi\"/>\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Attribute, "text"),
            "Expected text to be a uitkxAttribute token");
    }

    [Fact]
    public void NestedElement_BothTagged()
    {
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      <Label text=\"hi\"/>\n    </Box>\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Element, "Box"),
            "Expected Box to be a uitkxElement token");
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Element, "Label"),
            "Expected Label to be a uitkxElement token");
    }

    // ── U-31: SourceColumn preferred over first-occurrence text search ─────

    [Fact]
    public void DuplicateTagNamesOnSameLine_GetDistinctColumns()
    {
        // Before U-31 both "Label" tokens resolved via FindOnLine, which always
        // finds the FIRST occurrence — so the second element's tag-name token
        // collided with the first element's column instead of its own.
        var source = "VirtualNode C() {\n  return (\n    <Label text=\"a\"/><Label text=\"b\"/>\n  );\n}";
        var tokens = GetTokens(source);
        var lines = source.Split('\n');
        int lineIdx = 2;

        var labelTokens = tokens
            .Where(t => t.TokenType == SemanticTokenTypes.Element && t.Line == lineIdx)
            .Where(t => lines[t.Line].Substring(t.Column, t.Length) == "Label")
            .OrderBy(t => t.Column)
            .ToArray();

        Assert.Equal(2, labelTokens.Length);
        Assert.NotEqual(labelTokens[0].Column, labelTokens[1].Column);

        int firstLt = lines[lineIdx].IndexOf('<');
        int secondLt = lines[lineIdx].IndexOf('<', firstLt + 1);
        Assert.Equal(firstLt + 1, labelTokens[0].Column);
        Assert.Equal(secondLt + 1, labelTokens[1].Column);
    }

    // ── Control flow tests ─────────────────────────────────────────────────

    [Fact]
    public void IfDirective_IsDirective()
    {
        var source = "VirtualNode C() {\n  return (\n    @if (true) {\n      <Label/>\n    }\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Directive, "@if"),
            "Expected @if to be a uitkxDirective token");
    }

    [Fact]
    public void ElseDirective_IsDirective()
    {
        var source = "VirtualNode C() {\n  return (\n    @if (true) {\n      <Label/>\n    } @else {\n      <Box/>\n    }\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Directive, "@else"),
            "Expected @else to be a uitkxDirective token");
    }

    [Fact]
    public void ForeachDirective_IsDirective()
    {
        var source = "VirtualNode C() {\n  return (\n    @foreach (var x in items) {\n      <Label key={x}/>\n    }\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Directive, "@foreach"),
            "Expected @foreach to be a uitkxDirective token");
    }

    [Fact]
    public void ForDirective_IsDirective()
    {
        var source = "VirtualNode C() {\n  return (\n    @for (int i = 0; i < 5; i++) {\n      <Label key={i}/>\n    }\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Directive, "@for"),
            "Expected @for to be a uitkxDirective token");
    }

    [Fact]
    public void SwitchDirective_IsDirective()
    {
        var source = "VirtualNode C() {\n  return (\n    @switch (mode) {\n      @case \"a\":\n        <Label/>\n    }\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Directive, "@switch"),
            "Expected @switch to be a uitkxDirective token");
    }

    // ── Function-style component tests ─────────────────────────────────────

    [Fact]
    public void PlainDeclaration_NoLegacyKeywordToken()
    {
        // 0.16.0: the `component` keyword colorization died with the wrapper grammar;
        // a plain head must not surface any legacy directive token.
        var source = "VirtualNode Counter() {\n  return (\n    <Label text=\"0\"/>\n  );\n}";
        var tokens = GetTokens(source);
        Assert.False(HasToken(tokens, source, SemanticTokenTypes.Directive, "component"),
            "No legacy component keyword token expected on a plain declaration head");
    }

    [Fact]
    public void FunctionStyle_ElementInReturn_IsElement()
    {
        var source = "VirtualNode Counter() {\n  return (\n    <Label text=\"0\"/>\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(HasToken(tokens, source, SemanticTokenTypes.Element, "Label"),
            "Expected Label inside return to be a uitkxElement token");
    }

    // ── Comment test ───────────────────────────────────────────────────────

    [Fact]
    public void LineComment_IsComment()
    {
        var source = "VirtualNode C() {\n  return (\n    <Box>\n      // hello\n    </Box>\n  );\n}";
        var tokens = GetTokens(source);
        Assert.True(tokens.Any(t => t.TokenType == SemanticTokenTypes.Comment),
            "Expected a comment token for //");
    }

    [Fact]
    public void LineComment_SetterInsideComment_IsSuppressed()
    {
        // function-style component with useState and a commented-out setter call
        var source =
            "VirtualNode Counter() {\n" +
            "  var (count, setCount) = useState(0);\n" +
            "  return (\n" +
            "    <Box>\n" +
            "      // <Button onClick={_ => setCount(count + 1)} />\n" +
            "    </Box>\n" +
            "  );\n" +
            "}";
        var tokens = GetTokens(source);
        // The comment line (line 4, 0-indexed) must NOT contain a Function token
        var line4Fn = tokens.Where(t => t.Line == 4 && t.TokenType == SemanticTokenTypes.Function).ToArray();
        Assert.Empty(line4Fn);
        // But the comment token itself must exist on that line
        Assert.True(tokens.Any(t => t.Line == 4 && t.TokenType == SemanticTokenTypes.Comment),
            "Expected a comment token on the comment line");
    }

    [Fact]
    public void BlockComment_MultiLine_SettersSuppressed()
    {
        var source =
            "VirtualNode Counter() {\n" +
            "  var (count, setCount) = useState(0);\n" +
            "  return (\n" +
            "    <Box>\n" +
            "      /* multi-line comment\n" +
            "        setCount(count + 1)\n" +
            "        setCount(count - 1)\n" +
            "      */\n" +
            "    </Box>\n" +
            "  );\n" +
            "}";
        var tokens = GetTokens(source);
        // Lines 5 and 6 (0-indexed) are inside the comment — no Function tokens allowed
        var fnInComment = tokens.Where(t =>
            (t.Line == 5 || t.Line == 6) && t.TokenType == SemanticTokenTypes.Function).ToArray();
        Assert.Empty(fnInComment);
        // Comment tokens must exist on lines 4-7
        for (int l = 4; l <= 7; l++)
            Assert.True(tokens.Any(t => t.Line == l && t.TokenType == SemanticTokenTypes.Comment),
                $"Expected a comment token on line {l}");
    }

    // NOTE: Setter Function-token tests (CStyleComment_SettersSuppressed,
    // LineComment_SetterSuppressed, SetterOutsideComment_StillColored) were
    // removed — they tested Function tokens which are produced by the LSP
    // server's RoslynSemanticTokensProvider, not the language-lib
    // SemanticTokensProvider tested here.

    // ── No tokens in wrong places ──────────────────────────────────────────

    [Fact]
    public void PlainStringAttribute_NoExpressionToken()
    {
        var source = "VirtualNode C() {\n  return (\n    <Label text=\"hello\"/>\n  );\n}";
        var tokens = GetTokens(source);
        Assert.False(tokens.Any(t => t.TokenType == SemanticTokenTypes.Expression),
            "Plain string attribute should not produce expression tokens");
    }
}
