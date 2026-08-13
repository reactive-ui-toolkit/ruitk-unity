using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Ruitk.Language;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Direct unit tests for <see cref="DirectiveParser"/> and <see cref="UitkxParser"/>.
/// These tests exercise the internal parser types independently of the full
/// Roslyn generator pipeline.
/// </summary>
public class ParserTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DirectiveSet ParseDirectives(string source, out List<ParseDiagnostic> diags)
    {
        diags = new List<ParseDiagnostic>();
        return DirectiveParser.Parse(source, "test.uitkx", diags);
    }

    private static ImmutableArray<AstNode> ParseMarkup(
        string source,
        out List<ParseDiagnostic> diags
    )
    {
        diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(source, "test.uitkx", diags);
        return UitkxParser.Parse(source, "test.uitkx", directives, diags);
    }

    /// Wraps markup in a minimal function-style component for test convenience.
    private static string Wrap(string markup) =>
        "VirtualNode MyComp() {\n  return (\n" + markup + "\n  );\n}";

    /// Wraps code + markup in a function-style component (code runs before return).
    private static string WrapWithCode(string code, string markup) =>
        "VirtualNode MyComp() {\n  " + code + "\n  return (\n" + markup + "\n  );\n}";

    // ── Directive parsing ─────────────────────────────────────────────────────

    [Fact]
    public void Directives_FunctionStyle_ParsesComponentAndReturnRange()
    {
        const string src =
            """
            VirtualNode CounterPanel() {
                var (count, setCount) = useState(0);
                return (
                    <Box><Label text={$"{count}"} /></Box>
                );
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        Assert.True(set.IsFunctionStyle);
        Assert.Equal("CounterPanel", set.ComponentName);
        Assert.Equal("Ruitk.FunctionStyle", set.Namespace);
        Assert.True(set.MarkupStartIndex > 0);
        Assert.True(set.MarkupEndIndex > set.MarkupStartIndex);
        Assert.Contains("useState", set.FunctionSetupCode ?? string.Empty);
    }

    // ── U-09: duplicate @namespace must not fail the whole file ─────────────────

    [Fact]
    public void DuplicateNamespace_YieldsTargetedDiagnostic_NotWholeFileFailure()
    {
        const string src =
            "@namespace First.Ns\n@namespace Second.Ns\nVirtualNode Foo() {\n    return (\n        <Label text=\"x\" />\n    );\n}\n";

        var set = ParseDirectives(src, out var diags);

        Assert.Single(diags, d => d.Code == "UITKX2105");
        Assert.Equal("First.Ns", set.Namespace);
        Assert.True(set.IsFunctionStyle);
        Assert.Equal("Foo", set.ComponentName);
    }

    [Fact]
    public void DuplicateNamespace_MarkupStillParses()
    {
        const string src =
            "@namespace First.Ns\n@namespace Second.Ns\nVirtualNode Foo() {\n    return (\n        <Label text=\"x\" />\n    );\n}\n";

        var nodes = ParseMarkup(src, out var diags);
        Assert.Single(nodes);
    }

    // ── H-04: ParseFragment (standalone JSX snippet, no header text needed) ────

    [Fact]
    public void ParseFragment_SimpleElement_ParsesOneRoot()
    {
        var diags = new List<ParseDiagnostic>();
        var nodes = UitkxParser.ParseFragment("<Label text=\"hi\" />", "test.uitkx", 1, diags);

        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        Assert.Single(nodes);
        var el = Assert.IsType<ElementNode>(nodes[0]);
        Assert.Equal("Label", el.TagName);
    }

    [Fact]
    public void ParseFragment_NestedElement_ParsesChildren()
    {
        var diags = new List<ParseDiagnostic>();
        var nodes = UitkxParser.ParseFragment(
            "<Box><Label text=\"a\" /><Label text=\"b\" /></Box>", "test.uitkx", 1, diags);

        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        Assert.Single(nodes);
        var box = Assert.IsType<ElementNode>(nodes[0]);
        Assert.Equal(2, box.Children.Length);
    }

    [Fact]
    public void ParseFragment_StartLine_OffsetsDiagnosticLines()
    {
        // An unclosed tag should report an error anchored at the fragment's
        // startLine offset, not line 1 of the fragment text in isolation.
        var diags = new List<ParseDiagnostic>();
        UitkxParser.ParseFragment("<Box>", "test.uitkx", 42, diags);

        Assert.Contains(diags, d => d.Severity == ParseSeverity.Error && d.SourceLine >= 42);
    }

    // ── U-25: attr={(<Tag/>)} — paren-wrapped inline JSX attribute value ────────

    [Fact]
    public void Attribute_ParenWrappedJsx_ParsesAsJsxExpressionValue()
    {
        var diags = new List<ParseDiagnostic>();
        var nodes = UitkxParser.ParseFragment(
            "<Box icon={(<Label text=\"hi\" />)} />", "test.uitkx", 1, diags);

        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        var box = Assert.IsType<ElementNode>(Assert.Single(nodes));
        var attr = Assert.Single(box.Attributes);
        var jsxValue = Assert.IsType<JsxExpressionValue>(attr.Value);
        Assert.NotNull(jsxValue.Element);
        Assert.Equal("Label", jsxValue.Element!.TagName);
    }

    [Fact]
    public void Attribute_BareJsx_StillParsesAsJsxExpressionValue()
    {
        // Regression guard: the U-25 paren-peek must not disturb the
        // pre-existing unwrapped form attr={<Tag/>}.
        var diags = new List<ParseDiagnostic>();
        var nodes = UitkxParser.ParseFragment(
            "<Box icon={<Label text=\"hi\" />} />", "test.uitkx", 1, diags);

        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        var box = Assert.IsType<ElementNode>(Assert.Single(nodes));
        var attr = Assert.Single(box.Attributes);
        var jsxValue = Assert.IsType<JsxExpressionValue>(attr.Value);
        Assert.Equal("Label", jsxValue.Element!.TagName);
    }

    [Fact]
    public void Attribute_ParenWrappedNonJsxExpression_StillOpaqueCSharp()
    {
        // A plain parenthesised C# expression (no '<' after unwrapping) must
        // NOT be misdetected as JSX — only `(<Tag` triggers the JSX path.
        var diags = new List<ParseDiagnostic>();
        var nodes = UitkxParser.ParseFragment(
            "<Box count={(1 + 2)} />", "test.uitkx", 1, diags);

        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        var box = Assert.IsType<ElementNode>(Assert.Single(nodes));
        var attr = Assert.Single(box.Attributes);
        var csValue = Assert.IsType<CSharpExpressionValue>(attr.Value);
        Assert.Equal("(1 + 2)", csValue.Expression);
    }

    // ── U-05: Allman-style @else must parse like Allman @if ─────────────────────

    [Fact]
    public void AllmanElse_AfterAllmanIf_ParsesCleanly()
    {
        const string src =
            """
            VirtualNode Foo() {
                return (
                    <Box>
                        @if (true)
                        {
                            <Label text="a" />
                        }
                        @else
                        {
                            <Label text="b" />
                        }
                    </Box>
                );
            }
            """;

        var nodes = ParseMarkup(src, out var diags);
        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
    }

    [Fact]
    public void AllmanElseIf_ThenAllmanElse_ParsesCleanly()
    {
        const string src =
            """
            VirtualNode Foo() {
                return (
                    <Box>
                        @if (true)
                        {
                            <Label text="a" />
                        }
                        @else if (false)
                        {
                            <Label text="b" />
                        }
                        @else
                        {
                            <Label text="c" />
                        }
                    </Box>
                );
            }
            """;

        var nodes = ParseMarkup(src, out var diags);
        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
    }

    // ── U-08: literal '@' in text must not error ────────────────────────────────

    [Fact]
    public void LiteralAtSign_InText_NoDiagnostic()
    {
        var nodes = ParseMarkup(Wrap("<Label>contact me @ home</Label>"), out var diags);
        Assert.DoesNotContain(diags, d => d.Code == "UITKX0305");
    }

    [Fact]
    public void LiteralAtSign_InText_PreservedInOutput()
    {
        var nodes = ParseMarkup(Wrap("<Label>contact me @ home</Label>"), out var diags);
        var label = Assert.IsType<ElementNode>(nodes[0]);
        string combined = string.Concat(
            label.Children.OfType<TextNode>().Select(t => t.Content));
        Assert.Contains("@", combined);
        Assert.Equal("contact me @ home", combined);
    }

    [Fact]
    public void RealIfDirective_StillParsesAfterAtSignFix()
    {
        var nodes = ParseMarkup(
            Wrap("<Box>@if (true) { <Label text=\"a\" /> }</Box>"), out var diags);
        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
    }

    [Fact]
    public void MisspelledDirectiveTypo_FollowedByParen_StillErrors()
    {
        // `@foreech (x in y) {` — an unknown identifier immediately followed by
        // '(' still looks like an attempted (misspelled) directive, not literal text.
        var nodes = ParseMarkup(
            Wrap("<Box>@foreech (x in y) { <Label text=\"a\" /> }</Box>"), out var diags);
        Assert.Contains(diags, d => d.Code == "UITKX0305");
    }

    // ── U-13: mismatched closing tag anchors at the found tag, with a column ──

    [Fact]
    public void MismatchedClosingTag_ReportsFoundLineAndColumn_NoFalseOpenLine()
    {
        // <Label> is never closed; the next closing tag encountered while looking
        // for </Label> is </Box>, which doesn't match — the diagnostic must anchor
        // at the CLOSING tag's line/column (not the opening tag's), and must not
        // claim a bogus "opened at line" when the opening line isn't tracked at
        // this call site (ParseContent's stop-tag check only has the tag NAME).
        var src = Wrap("<Box>\n  <Label>\n  </Box>");
        var nodes = ParseMarkup(src, out var diags);

        var mismatch = Assert.Single(diags, d => d.Code == "UITKX0302");
        Assert.Equal(5, mismatch.SourceLine); // line of </Box>
        Assert.Equal(2, mismatch.SourceColumn); // "  </Box>" — '<' is 2 spaces in
        Assert.True(mismatch.EndColumn > mismatch.SourceColumn, "Expected EndColumn to span the closing tag");
        Assert.Contains("Found '</Box>' but expected '</Label>'", mismatch.Message);
        Assert.DoesNotContain("opened at line", mismatch.Message);
    }

    // ── U-11: unwrapped `return expr;` in a control-block body must not be
    //          silently invisible ─────────────────────────────────────────────

    [Fact]
    public void UnwrappedReturnExpression_InIfBody_EmitsDiagnostic()
    {
        var src = Wrap("@if (x) { return items.First(); }");
        var diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(src, "test.uitkx", diags);
        UitkxParser.Parse(src, "test.uitkx", directives, diags);

        Assert.Contains(diags, d => d.Code == "UITKX2102" && d.Severity == ParseSeverity.Error);
    }

    [Fact]
    public void ProperReturnParen_InIfBody_NoUitkx2102()
    {
        var src = Wrap("@if (x) { return (<Label text=\"a\" />); }");
        var diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(src, "test.uitkx", diags);
        UitkxParser.Parse(src, "test.uitkx", directives, diags);

        Assert.DoesNotContain(diags, d => d.Code == "UITKX2102");
    }

    // ── U-24: unbalanced '{' in a control-block body must emit a diagnostic ────
    // Uses ParseFragment (a bounded, self-contained "source") so the unclosed brace
    // genuinely has no matching '}' to find anywhere — reproducing the snippet
    // mini-parse scenario the finding describes (in a full top-level component parse,
    // there is always at least the component's OWN closing '}' later in the file for
    // the naive brace-counter to (mis)match against).

    [Fact]
    public void UnclosedIfBody_InFragment_EmitsDiagnostic()
    {
        var diags = new List<ParseDiagnostic>();
        UitkxParser.ParseFragment("<Box>@if (true) { <Label text=\"a\" />", "test.uitkx", 1, diags);

        Assert.Contains(diags, d => d.Code == "UITKX0300" && d.Severity == ParseSeverity.Error);
    }

    // ── U-28: unclosed '{expr}' must emit a diagnostic, not silently EOF-close ──

    [Fact]
    public void UnclosedExpression_InFragment_EmitsDiagnostic()
    {
        // A child expression {expr} (not an attribute value) — the code path this
        // fix touches (UitkxParser.ParseContent's '{' branch).
        var diags = new List<ParseDiagnostic>();
        UitkxParser.ParseFragment("<Box>{x.ToString()", "test.uitkx", 1, diags);

        Assert.Contains(diags, d => d.Code == "UITKX0300" && d.Message.Contains("Unclosed"));
    }

    [Fact]
    public void ClosedExpression_NoUnclosedDiagnostic()
    {
        var src = Wrap("<Box>{1 + 1}</Box>");
        var diags = new List<ParseDiagnostic>();
        var directives = DirectiveParser.Parse(src, "test.uitkx", diags);
        UitkxParser.Parse(src, "test.uitkx", directives, diags);

        Assert.DoesNotContain(diags, d => d.Message.Contains("Unclosed"));
    }

    // ── U-29: multi-line text content's TextNode.SourceLine is the START line ───

    [Fact]
    public void MultilineTextContent_ReportsStartLine()
    {
        const string src =
            "VirtualNode Foo() {\n    return (\n        <Label>\n            line one\n            line two\n        </Label>\n    );\n}\n";
        var nodes = ParseMarkup(src, out _);
        var label = Assert.IsType<ElementNode>(nodes[0]);
        var textNode = Assert.IsType<TextNode>(label.Children[0]);
        // The text starts on the line right after <Label> (line 4, 1-based) — not the
        // line it ends on (line 5, where "line two" sits before the closing tag).
        Assert.Equal(4, textNode.SourceLine);
    }

    [Fact]
    public void KAndRElse_StillParsesCleanly()
    {
        const string src =
            """
            VirtualNode Foo() {
                return (
                    <Box>
                        @if (true) {
                            <Label text="a" />
                        } @else {
                            <Label text="b" />
                        }
                    </Box>
                );
            }
            """;

        var nodes = ParseMarkup(src, out var diags);
        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
    }

    [Fact]
    public void Directives_FunctionStyle_DoesNotInferNamespace_FromCompanionCs()
    {
        // The parser no longer reads a companion .cs to infer the namespace (plan §4 —
        // a target's identity must never flip on a .cs edit). It returns the stable default;
        // the pipeline derives the real (path-based) namespace downstream.
        string dir = Path.Combine(
            Path.GetTempPath(),
            "uitkx-func-ns-" + System.Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);

        try
        {
            string uitkxPath = Path.Combine(dir, "CounterPanel.uitkx");
            string companionPath = Path.Combine(dir, "CounterPanel.cs");

            File.WriteAllText(
                companionPath,
                "namespace MyGame.Sample.UI { public partial class CounterPanel { } }"
            );

            const string src =
                """
                VirtualNode CounterPanel() {
                    return (<Box />);
                }
                """;

            var diags = new List<ParseDiagnostic>();
            var set = DirectiveParser.Parse(src, uitkxPath, diags);

            Assert.True(set.IsFunctionStyle);
            Assert.Equal("CounterPanel", set.ComponentName);
            // The companion's MyGame.Sample.UI is IGNORED — default, not .cs-derived.
            Assert.Equal("Ruitk.FunctionStyle", set.Namespace);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Directives_FunctionStyle_WithLeadingComments_IsDetected()
    {
        const string src =
            """
            // file banner
            /* parser should skip this block comment */
            <!-- and this UITKX comment -->
            VirtualNode CounterPanel() {
                return (<Box />);
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.True(set.IsFunctionStyle);
        Assert.Equal("CounterPanel", set.ComponentName);
        Assert.Equal("Ruitk.FunctionStyle", set.Namespace);
        Assert.DoesNotContain(diags, d => d.Code == "UITKX0005");
    }

    [Fact]
    public void Directives_FunctionStyle_MissingReturn_EmitsUITKX2101()
    {
        const string src =
            """
            VirtualNode CounterPanel() {
                var (count, setCount) = useState(0);
                <Box />
            }
            """;

        ParseDirectives(src, out var diags);
        Assert.Contains(diags, d => d.Code == "UITKX2101");
    }

    [Fact]
    public void Directives_FunctionStyle_NonPascalName_EmitsUITKX2100()
    {
        const string src = "VirtualNode counterPanel() { return (<Box />); }";

        ParseDirectives(src, out var diags);
        Assert.Contains(diags, d => d.Code == "UITKX2100");
    }

    [Fact]
    public void Directives_FunctionStyle_ReturnNonMarkup_EmitsUITKX2102()
    {
        const string src =
            """
            VirtualNode CounterPanel() {
                var count = 1;
                return (count);
            }
            """;

        ParseDirectives(src, out var diags);
        Assert.Contains(diags, d => d.Code == "UITKX2102");
    }

    [Fact]
    public void Directives_FunctionStyle_MalformedReturn_EmitsUITKX2102()
    {
        const string src =
            """
            VirtualNode CounterPanel() {
                return count;
            }
            """;

        ParseDirectives(src, out var diags);
        Assert.Contains(diags, d => d.Code == "UITKX2102");
    }

    // ── Null-only components (React case 2) ──────────────────────────────────

    [Fact]
    public void Directives_FunctionStyle_NullOnlyReturn_Accepted()
    {
        const string src =
            """
            VirtualNode SoundEffect() {
                useEffect(() => {
                    Play();
                    return null;
                }, System.Array.Empty<object>());
                return null;
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.DoesNotContain(diags, d => d.Code == "UITKX2101");
        Assert.DoesNotContain(diags, d => d.Code == "UITKX2102");
        Assert.True(set.IsFunctionStyle);
        Assert.True(set.HasNullReturn);
        Assert.Contains("return null;", set.FunctionSetupCode ?? string.Empty);
        Assert.Equal(set.MarkupStartIndex, set.MarkupEndIndex);
    }

    [Fact]
    public void PlainDeclaration_NullOnlyComponent_Accepted()
    {
        const string src =
            """
            export VirtualNode Gone() {
              return null;
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.DoesNotContain(diags, d => d.Code == "UITKX2101");
        Assert.DoesNotContain(diags, d => d.Code == "UITKX2102");
        var component = Assert.Single(set.ComponentDeclarations);
        Assert.True(component.HasNullReturn);
        Assert.True(set.HasNullReturn);
        Assert.Contains("return null;", component.FunctionSetupCode ?? string.Empty);
    }

    [Fact]
    public void Directives_GuardReturnNull_WithMarkupReturn_NotNullOnly()
    {
        const string src =
            """
            VirtualNode CounterPanel() {
                if (hidden) { return null; }
                return (<Box />);
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.DoesNotContain(diags, d => d.Code == "UITKX2101" || d.Code == "UITKX2102");
        Assert.False(set.HasNullReturn);
        Assert.True(set.MarkupEndIndex > set.MarkupStartIndex);
    }

    [Fact]
    public void Directives_ReturnNullExpression_StillEmitsUITKX2102()
    {
        const string src =
            """
            VirtualNode CounterPanel() {
                return nullableThing;
            }
            """;

        ParseDirectives(src, out var diags);
        Assert.Contains(diags, d => d.Code == "UITKX2102");
    }

    [Fact]
    public void Directives_TrailingNamespaceDirective_IsAnError()
    {
        // @namespace stays a live preamble escape hatch (ruling D1); AFTER declarations
        // it is a top-level error like any other stray directive.
        const string src =
            """
            VirtualNode CounterPanel() {
                return (<Box />);
            }
            @namespace Test.NS
            """;

        ParseDirectives(src, out var diags);
        Assert.Contains(diags, d => d.Severity == ParseSeverity.Error);
    }

    // ── Mixed-decl v1 (leg 3): a file is a SEQUENCE of declarations ──────────────

    [Fact]
    public void HookFile_WithUss_NoLongerErrors_2210Lifted()
    {
        // §5 (import/export grammar): @uss is legal in any file — the old UITKX2210 hard error
        // (hook/module files may not carry @uss) is lifted.
        const string src =
            "@uss \"styles.uss\"\nvoid useThing() {\n    return 0;\n}\n";

        ParseDirectives(src, out var diags);

        Assert.DoesNotContain(diags, d => d.Code == "UITKX2210");
    }

    [Fact]
    public void MixedDecl_TwoComponentsInOneFile_BothParsedNoError()
    {
        const string src =
            """
            VirtualNode First() {
                return (<Box />);
            }
            VirtualNode Second() {
                return (<Label text="x" />);
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        Assert.Equal(2, set.ComponentDeclarations.Length);
        Assert.Equal("First", set.ComponentDeclarations[0].Name);
        Assert.Equal("Second", set.ComponentDeclarations[1].Name);
        // Singular back-compat fields still track the FIRST component.
        Assert.Equal("First", set.ComponentName);
    }

    [Fact]
    public void MixedDecl_ComponentThenHookAndValue_AllParsed()
    {
        const string src =
            """
            VirtualNode Screen() {
                return (<Box />);
            }
            export void useCounter(int start) {
            }
            export int Gap = 4;
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        Assert.Single(set.ComponentDeclarations);
        Assert.Equal(2, set.MemberDeclarations.Length);
        Assert.Contains(set.MemberDeclarations, m => m.Kind == DeclKind.Hook && m.Name == "useCounter");
        Assert.Contains(set.MemberDeclarations, m => m.Kind == DeclKind.Value && m.Name == "Gap");
        Assert.All(set.MemberDeclarations, m => Assert.True(m.IsExported));
    }

    [Fact]
    public void MixedDecl_ExportedSecondComponent_MarkedExported()
    {
        const string src =
            """
            VirtualNode First() {
                return (<Box />);
            }
            export VirtualNode Second() {
                return (<Label text="x" />);
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        Assert.Equal(2, set.ComponentDeclarations.Length);
        Assert.False(set.ComponentDeclarations[0].IsExported);
        Assert.True(set.ComponentDeclarations[1].IsExported);
    }

    [Fact]
    public void Directives_FunctionStyle_LeadingUsingLines_AreParsedIntoDirectiveSet()
    {
        const string src =
            """
            using MyGame.Models;
            using System.Collections.Generic;
            VirtualNode PlayerHUD() {
                return (<Box />);
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.True(set.IsFunctionStyle);
        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        Assert.Contains(set.Usings, u => u == "MyGame.Models");
        Assert.Contains(set.Usings, u => u == "System.Collections.Generic");
    }

    [Fact]
    public void Directives_FunctionStyle_InlineNamespaceDirective_UsedOverDefault()
    {
        const string src =
            """
            @namespace MyGame.UI
            VirtualNode PlayerHUD() {
                return (<Box />);
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.True(set.IsFunctionStyle);
        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        Assert.Equal("PlayerHUD", set.ComponentName);
        Assert.Equal("MyGame.UI", set.Namespace);
    }

    [Fact]
    public void Directives_FunctionStyle_InlineNamespaceDirective_AfterUsings()
    {
        const string src =
            """
            using System.Collections.Generic;
            @namespace MyGame.Screens
            VirtualNode PlayerHUD() {
                return (<Box />);
            }
            """;

        var set = ParseDirectives(src, out var diags);

        Assert.True(set.IsFunctionStyle);
        Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
        Assert.Equal("MyGame.Screens", set.Namespace);
        Assert.Contains(set.Usings, u => u == "System.Collections.Generic");
    }

    [Fact]
    public void Directives_FunctionStyle_InlineNamespaceDirective_TakesPriorityOverCompanion()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "uitkx-ns-priority-" + System.Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);

        try
        {
            string uitkxPath = Path.Combine(dir, "PlayerHUD.uitkx");
            string companionPath = Path.Combine(dir, "PlayerHUD.cs");

            File.WriteAllText(
                companionPath,
                "namespace CompanionNamespace { public partial class PlayerHUD { } }"
            );

            const string src =
                """
                @namespace InlineNamespace
                VirtualNode PlayerHUD() {
                    return (<Box />);
                }
                """;

            var diags = new List<ParseDiagnostic>();
            var set = DirectiveParser.Parse(src, uitkxPath, diags);

            Assert.True(set.IsFunctionStyle);
            Assert.DoesNotContain(diags, d => d.Severity == ParseSeverity.Error);
            // Inline @namespace must win over the companion .cs
            Assert.Equal("InlineNamespace", set.Namespace);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ── Markup: elements ─────────────────────────────────────────────────────

    [Fact]
    public void Markup_SelfClosingElement_ProducesElementNode()
    {
        var nodes = ParseMarkup(Wrap("<label/>"), out _);

        var el = Assert.Single(nodes.OfType<ElementNode>());
        Assert.Equal("label", el.TagName);
        Assert.Empty(el.Children);
    }

    [Fact]
    public void Markup_FunctionStyle_ReturnMarkup_ProducesElementNode()
    {
        const string src =
            """
            VirtualNode CounterPanel() {
                var (count, setCount) = useState(0);
                return (
                    <Box><Label text={$"{count}"} /></Box>
                );
            }
            """;

        var nodes = ParseMarkup(src, out _);

        var box = Assert.Single(nodes.OfType<ElementNode>());
        Assert.Equal("Box", box.TagName);
    }

    [Fact]
    public void Markup_NestedElement_ProducesChildren()
    {
        var nodes = ParseMarkup(Wrap("<box><label/></box>"), out _);

        var root = Assert.Single(nodes.OfType<ElementNode>());
        Assert.Equal("box", root.TagName);

        var child = Assert.Single(root.Children.OfType<ElementNode>());
        Assert.Equal("label", child.TagName);
    }

    [Fact]
    public void Markup_StringAttribute_ProducesStringLiteralValue()
    {
        var nodes = ParseMarkup(Wrap("""<label text="Hello"/>"""), out _);

        var el = Assert.Single(nodes.OfType<ElementNode>());
        var attr = Assert.Single(el.Attributes, a => a.Name == "text");

        var value = Assert.IsType<StringLiteralValue>(attr.Value);
        Assert.Equal("Hello", value.Value);
    }

    [Fact]
    public void Markup_ExpressionAttribute_ProducesCSharpExpressionValue()
    {
        var nodes = ParseMarkup(Wrap("<label text={count}/>"), out _);

        var el = Assert.Single(nodes.OfType<ElementNode>());
        var attr = Assert.Single(el.Attributes, a => a.Name == "text");

        var value = Assert.IsType<CSharpExpressionValue>(attr.Value);
        Assert.Equal("count", value.Expression.Trim());
    }

    [Fact]
    public void Markup_BooleanShorthandAttribute_ProducesBooleanShorthandValue()
    {
        var nodes = ParseMarkup(Wrap("<toggle disabled/>"), out _);

        var el = Assert.Single(nodes.OfType<ElementNode>());
        var attr = Assert.Single(el.Attributes, a => a.Name == "disabled");

        Assert.IsType<BooleanShorthandValue>(attr.Value);
    }

    [Fact]
    public void Markup_MultipleAttributes_AllParsed()
    {
        var nodes = ParseMarkup(
            Wrap("""<button text="Click" onClick={handler}/>"""),
            out _
        );

        var el = Assert.Single(nodes.OfType<ElementNode>());
        Assert.Equal(2, el.Attributes.Length);
        Assert.Contains(el.Attributes, a => a.Name == "text");
        Assert.Contains(el.Attributes, a => a.Name == "onClick");
    }

    // ── Markup: control flow ─────────────────────────────────────────────────

    [Fact]
    public void Markup_IfDirective_ProducesIfNode()
    {
        var src = Wrap("@if (x > 0) { <label/> }");
        var nodes = ParseMarkup(src, out _);

        var ifNode = Assert.Single(nodes.OfType<IfNode>());
        Assert.Single(ifNode.Branches);
        Assert.Equal("x > 0", ifNode.Branches[0].Condition?.Trim());
    }

    [Fact]
    public void Markup_IfElse_ProducesTwoBranches()
    {
        var src = Wrap("@if (flag) { <label/> } @else { <button/> }");
        var nodes = ParseMarkup(src, out _);

        var ifNode = Assert.Single(nodes.OfType<IfNode>());
        Assert.Equal(2, ifNode.Branches.Length);
        Assert.Null(ifNode.Branches[1].Condition); // @else has no condition
    }

    [Fact]
    public void Markup_ForeachDirective_ProducesForeachNode()
    {
        var src = Wrap("@foreach (var item in items) { <label/> }");
        var nodes = ParseMarkup(src, out _);

        var forNode = Assert.Single(nodes.OfType<ForeachNode>());
        Assert.Contains("item", forNode.IteratorDeclaration);
        Assert.Contains("items", forNode.CollectionExpression);
    }

    [Fact]
    public void Markup_SwitchDirective_ProducesSwitchNode()
    {
        var src = Wrap(
            """
            @switch (mode) {
                @case 0: <label text="zero"/>
                @case 1: <label text="one"/>
            }
            """);
        var nodes = ParseMarkup(src, out _);

        var sw = Assert.Single(nodes.OfType<SwitchNode>());
        Assert.Contains("mode", sw.SwitchExpression);
        Assert.Equal(2, sw.Cases.Length);
    }

    [Fact]
    public void Markup_BreakOutsideLoop_EmitsUnexpectedToken()
    {
        var src = Wrap("@break;\n<label/>");
        ParseMarkup(src, out var diags);

        Assert.Contains(diags, d => d.Code == "UITKX0300" && d.Message.Contains("@break"));
    }

    [Fact]
    public void Markup_InlineExpression_ProducesExpressionNode()
    {
        var src = Wrap("<box>{someCall()}</box>");
        var nodes = ParseMarkup(src, out _);

        var box = Assert.Single(nodes.OfType<ElementNode>());
        var expr = Assert.Single(box.Children.OfType<ExpressionNode>());
        Assert.Contains("someCall()", expr.Expression);
    }

    // ── Markup: line tracking ─────────────────────────────────────────────────

    [Fact]
    public void Markup_SourceLineIsOneBasedAndApproximate()
    {
        var src = Wrap("<label/>");
        var nodes = ParseMarkup(src, out _);

        var el = Assert.Single(nodes.OfType<ElementNode>());
        Assert.True(el.SourceLine >= 1, "SourceLine should be 1-based");
    }

    // ── Missing semicolon after paren-wrapped JSX in setup code ───────────────

    [Fact]
    public void SetupJsx_MissingSemicolon_EmitsCS1002()
    {
        const string src =
            """
            VirtualNode Counter(int count = 0) {
              var inlineNode = (
                <VisualElement>
                  <Button text="-5" onClick={_ => setCount(count - 5)} />
                  <Button text="+5" onClick={_ => setCount(count + 5)} />
                </VisualElement>
              )

              return (<Box />);
            }
            """;

        var diags = new List<ParseDiagnostic>();
        var set = DirectiveParser.Parse(src, "test.uitkx", diags);

        // Debug output
        System.Console.WriteLine($"IsFunctionStyle: {set.IsFunctionStyle}");
        System.Console.WriteLine($"SetupCodeMarkupRanges: {(set.SetupCodeMarkupRanges.IsDefault ? 0 : set.SetupCodeMarkupRanges.Length)}");
        if (!set.SetupCodeMarkupRanges.IsDefaultOrEmpty)
        {
            foreach (var (s, e, l) in set.SetupCodeMarkupRanges)
                System.Console.WriteLine($"  Range: Start={s} End={e} Line={l}  src[Start-1]='{src[s-1]}' src[End]='{src[e]}'");
        }
        foreach (var d in diags)
            System.Console.WriteLine($"  [{d.Code}] L{d.SourceLine}:C{d.SourceColumn} {d.Message}");

        var cs1002 = diags.Where(d => d.Code == "CS1002").ToList();
        Assert.True(cs1002.Count > 0, "Expected CS1002 for missing ';' after paren-wrapped JSX block");
    }

    [Fact]
    public void SetupJsx_WithSemicolon_NoCS1002()
    {
        const string src =
            """
            VirtualNode Counter(int count = 0) {
              var inlineNode = (
                <VisualElement>
                  <Button text="-5" onClick={_ => setCount(count - 5)} />
                  <Button text="+5" onClick={_ => setCount(count + 5)} />
                </VisualElement>
              );

              return (<Box />);
            }
            """;

        var diags = new List<ParseDiagnostic>();
        DirectiveParser.Parse(src, "test.uitkx", diags);

        var cs1002 = diags.Where(d => d.Code == "CS1002").ToList();
        Assert.Empty(cs1002);
    }

    // ── U-07: block-comment-blind JSX-block detection → false CS1002 ─────────

    [Fact]
    public void SetupCode_CommentedOutJsx_NoFalseCS1002()
    {
        // The exact repro from the audit doc (P3b): a block comment containing
        // old removed JSX must not be misdetected as a live JSX paren-block by
        // DirectiveParser.FindJsxBlockRanges — which previously had no '/* */'
        // skip (unlike its sibling FindBareJsxRanges), so the comment's content
        // leaked into the JSX-splice scaffold and broke Roslyn's scaffold parse.
        const string src =
            """
            VirtualNode Counter(int count = 0) {
              /* old UI: (<Label/>) was removed */
              var x = count;

              return (<Box />);
            }
            """;

        var diags = new List<ParseDiagnostic>();
        DirectiveParser.Parse(src, "test.uitkx", diags);

        var cs1002 = diags.Where(d => d.Code == "CS1002").ToList();
        Assert.Empty(cs1002);
    }

    [Fact]
    public void SetupCode_CommentedOutJsx_NotInMarkupRanges()
    {
        // Directly pins the root cause: the comment's parenthesised content must
        // not appear in SetupCodeMarkupRanges at all.
        const string src =
            """
            VirtualNode Counter(int count = 0) {
              /* old UI: (<Label/>) was removed */
              var x = count;

              return (<Box />);
            }
            """;

        var diags = new List<ParseDiagnostic>();
        var set = DirectiveParser.Parse(src, "test.uitkx", diags);

        int commentParenStart = src.IndexOf("(<Label/>)", System.StringComparison.Ordinal);
        Assert.True(commentParenStart >= 0);

        if (!set.SetupCodeMarkupRanges.IsDefaultOrEmpty)
        {
            foreach (var (s, e, _) in set.SetupCodeMarkupRanges)
                Assert.False(
                    s >= commentParenStart && s <= commentParenStart + "(<Label/>)".Length,
                    "Commented-out JSX must not appear in SetupCodeMarkupRanges");
        }
    }
}
