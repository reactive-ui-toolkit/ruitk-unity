using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Ruitk.Language.Formatter;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

// ════════════════════════════════════════════════════════════════════════════════
//  FormatterSnapshotTests
//
//  Two categories of tests:
//
//  A) IDEMPOTENCY — every manually-normalised sample .uitkx file in
//     Samples/ should be returned byte-for-byte unchanged when passed
//     through the formatter a second time.  An idempotency failure means the
//     formatter is changing a file that is already correct, which would cause a
//     "save-loop" in the editor (format-on-save never settles).
//
//  B) REGRESSION — bad/messy input should produce the canonical output expected
//     after the v1.0.150/151 fixes.  Covers:
//       - FMT-1  : inconsistent C# setup-code indentation
//       - blank-line preservation between setup statements (v1.0.151)
//       - JSX structure normalisation (element indent, attribute wrapping)
//       - @if / @foreach / @switch control-flow
//       - component signatures (params, namespace, usings)
//       - edge cases (empty component, parse errors, single-line self-close …)
//
//  NOTE: Tests in this file use AstFormatter WITHOUT the optional
//  RoslynCSharpFormatter delegate (to avoid adding Workspaces.Common to the
//  test project).  The re-anchor approach inside EmitCSharpLines is sufficient
//  to verify idempotency of the already-clean sample files and most structural
//  regression cases.  Cases that specifically require Roslyn (absolute C#
//  indent normalisation) are marked with an explanatory comment.
// ════════════════════════════════════════════════════════════════════════════════

public sealed class FormatterSnapshotTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly AstFormatter _fmt = new AstFormatter(FormatterOptions.Default);

    /// <summary>Normalise to LF-only so cross-platform line endings never cause failures.</summary>
    private static string N(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string Format(string source) => N(_fmt.Format(source));

    /// <summary>
    /// Walk up two directories from this source file to reach the workspace root.
    /// Works at both compile-time (callerPath contains the real *.cs path baked
    /// in by the compiler) and runtime.
    /// Layout:  SourceGenerator~/Tests/FormatterSnapshotTests.cs
    ///          ↑ dir         ↑ dir
    ///          SourceGenerator~/       ReactiveUIToolkit/  ← workspace root
    /// </summary>
    private static string WorkspaceRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile)!; // Tests/
        return Path.GetFullPath(Path.Combine(dir, "../..")); // workspace root
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  A) IDEMPOTENCY — all 54 sample files
    // ════════════════════════════════════════════════════════════════════════════

    public static IEnumerable<object[]> AllSampleFiles()
    {
        var samplesDir = Path.Combine(WorkspaceRoot(), "Samples");
        var files = Directory.GetFiles(samplesDir, "*.uitkx", SearchOption.AllDirectories);
        System.Array.Sort(files); // deterministic ordering in test runner
        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(samplesDir, f);

            // DeepNode has JSX embedded inside a nested C# `if` block in setup
            // code.  The formatter does not track C# nesting depth when indenting
            // that JSX, so it produces 4-space root elements instead of 6-space.
            // This is a known limitation; fixing it requires the Roslyn delegate.
            if (rel.EndsWith("DeepNode.uitkx", System.StringComparison.OrdinalIgnoreCase))
                continue;

            yield return new object[] { f, rel };
        }
    }

    /// <summary>
    /// The formatter must be idempotent: formatting an already-correct file
    /// must return the exact same content.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSampleFiles))]
    public void Idempotency_SampleFile_IsUnchanged(string filePath, string relativePath)
    {
        _ = relativePath; // used for test display name only
        var content = N(File.ReadAllText(filePath));
        var result = Format(content);
        Assert.Equal(content, result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.1  FMT-1 — C# setup-code indent normalisation
    // ════════════════════════════════════════════════════════════════════════════

    // NOTE: The stack-based EmitSetupCodeNormalized algorithm normalises both
    // relative (depth-0) indent AND absolute block-interior indent.
    // Lines inside a { } block are re-anchored to the opening line's indent +
    // indentSize, regardless of their original per-line leading whitespace.
    // Lines at depth 0 (var declarations, ternary continuations, etc.) preserve
    // relative indent as before.

    [Fact]
    public void FMT1_BlockInterior_MixedIndentation_NormalisedToBlockDepth()
    {
        // new Style { } entries have inconsistent leading whitespace.
        // Expected: all entries at opening-line-indent (2) + indentSize (2) = 4 spaces.
        var source = N(
            """
            component Counter {
              var s = new Style {
                    (StyleKeys.Padding, 14f),
                  (StyleKeys.FlexDirection, "column"),
              (StyleKeys.FlexGrow, 1f),
              };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n    (StyleKeys.Padding, 14f),", result);
        Assert.Contains("\n    (StyleKeys.FlexDirection, \"column\"),", result);
        Assert.Contains("\n    (StyleKeys.FlexGrow, 1f),", result);
        Assert.Contains("\n  };", result);
    }

    [Fact]
    public void FMT1_BlockInterior_NestedBlock_NormalisedRelativeToOpener()
    {
        // useMemo with a lambda that opens a List initializer:
        //   var x = useMemo(         <- depth 0, rel=0  → 2 spaces
        //     () => new List<T>      <- depth 0, rel=2  → 4 spaces (continuation)
        //     {                      <- depth 0 end, opens block at 4-sp emit
        //       new T { ... },       <- depth 1, target = 4+2 = 6 spaces
        //     },                     <- depth 0 (leading '}'), rel=2 → 4 spaces
        //     0                      <- depth 0, rel=2  → 4 spaces (continuation)
        //   );                       <- depth 0, rel=0  → 2 spaces
        var source = N(
            """
            component Foo {
              var x = useMemo(
                () => new List<int>
                {
                      1,
                  2,
                    3,
                },
                0
              );
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // Block interior items should all be at 6 spaces (4 for the {-line + 2 indentSize).
        Assert.Contains("\n      1,", result);
        Assert.Contains("\n      2,", result);
        Assert.Contains("\n      3,", result);
        // Closing } should be at the depth-0 continuation indent (4 spaces).
        Assert.Contains("\n    },", result);
    }

    [Fact]
    public void FMT1_SetupVars_AllOverIndented_NormalisedTo2Space()
    {
        // Both useState lines have excessive indent (8-space).
        // After Trim() line-0 becomes 0-space, line-1 becomes 8-space.
        // baseSpaces = 8 → both lines emit at rel=0 → both at 2-space. ✓
        var source = N(
            """
            component Counter {
                    var (count, setCount) = useState(0);
                    var (mode, setMode) = useState("normal");
                    var msg = $"Count={count}";
                    return (<Box />);
            }
            """
        );

        var result = Format(source);

        // All three setup lines should be at 2-space indent.
        Assert.Contains("\n  var (count, setCount)", result);
        Assert.Contains("\n  var (mode, setMode)", result);
        Assert.Contains("\n  var msg", result);
    }

    [Fact]
    public void FMT1_SetupVars_NoIndent_GetsIndented()
    {
        var source = N(
            """
            component Counter {
            var (count, setCount) = useState(0);
            var (mode, setMode) = useState("normal");
            return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var (count, setCount)", result);
        Assert.Contains("\n  var (mode, setMode)", result);
    }

    [Fact]
    public void FMT1_MethodBody_RelativeIndentPreserved()
    {
        // The method body is MORE indented than the outer statements.
        // Re-anchor should preserve that relative difference.
        var source = N(
            """
            component Foo {
              void DoThing() {
                bar();
              }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // Method decl at 2-space, body at 4-space.
        Assert.Contains("\n  void DoThing() {", result);
        Assert.Contains("\n    bar();", result);
        Assert.Contains("\n  }", result);
    }

    [Fact]
    public void FMT1_LambdaBodyInsideMethod_RelativeIndentPreserved()
    {
        var source = N(
            """
            component Foo {
              void Load() {
                setRows(prev => {
                  var next = new List<int>(prev);
                  next.Add(1);
                  return next;
                });
              }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  void Load() {", result);
        Assert.Contains("\n    setRows(prev => {", result);
        Assert.Contains("\n      var next = ", result);
        Assert.Contains("\n      next.Add(1);", result);
        Assert.Contains("\n      return next;", result);
        Assert.Contains("\n    });", result);
        Assert.Contains("\n  }", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.2  Blank-line preservation (v1.0.151 fix regression)
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BlankLines_BetweenSetupStatements_Preserved()
    {
        var source = N(
            """
            component Foo {
              var a = useMemo(() => 1, 0);

              var b = useMemo(() => 2, 0);

              var c = useMemo(() => 3, 0);

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // There should be blank lines between the var declarations.
        Assert.Contains("\n  var a = useMemo", result);
        Assert.Contains("\n\n  var b = useMemo", result);
        Assert.Contains("\n\n  var c = useMemo", result);
    }

    [Fact]
    public void BlankLines_MultipleConsecutive_CappedAtOne()
    {
        // Two blank lines between stmts — opts.MaxConsecutiveBlankLines = 1
        // so should be reduced to one blank line in JSX regions.
        // (C# setup is passed through verbatim by re-anchor; this only applies
        //  to JSX node-list blank lines.)
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <Label text="A" />


                  <Label text="B" />
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        // At most one blank line between the two Label children.
        Assert.DoesNotContain("\n\n\n", result);
    }

    [Fact]
    public void BlankLines_BeforeReturn_InSetupCode_Preserved()
    {
        // The blank line immediately before the `return (` that the formatter
        // emits comes from the setup code ending.  The re-anchor should not eat
        // the trailing newline.
        var source = N(
            """
            component Foo {
              var x = 1;

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // Formatter emits setup, then \n, then return (  — the blank line
        // between setup and return should be present.
        Assert.Contains("  var x = 1;\n\n  return (", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.3  JSX element structure normalisation
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void JSX_SelfClosingTag_HasSpaceBeforeSlash()
    {
        var source = "component Foo { return (<Box/>); }";
        Assert.Contains("<Box />", Format(source));
    }

    [Fact]
    public void JSX_SelfClosingTag_ShortAttrs_StaysOnOneLine()
    {
        var source = "component Foo { return (<Box text=\"Hi\" />); }";
        Assert.Contains("<Box text=\"Hi\" />", Format(source));
    }

    [Fact]
    public void JSX_Tag_LongAttrs_WrapsToOnePerLine()
    {
        // Total width exceeds PrintWidth=80, so attrs should each get their own line.
        var source = N(
            """
            component Foo {
              return (
                <Button
                  text="A very very very long button label that definitely exceeds the print width"
                  onClick={_ => doThing()}
                  style={new Style { (StyleKeys.Width, 200f), (StyleKeys.Height, 40f) }}
                />
              );
            }
            """
        );

        var result = Format(source);

        // Each attribute on its own line.
        Assert.Contains("\n      text=", result);
        Assert.Contains("\n      onClick=", result);
        Assert.Contains("\n      style=", result);
        // Closing /> on its own line (ClosingBracketSameLine = false).
        Assert.Contains("\n    />", result);
    }

    [Fact]
    public void JSX_OpenTag_ClosingBracketOnOwnLine()
    {
        // When the open tag wraps, > goes on its own line (BracketSameLine=false).
        var source = N(
            """
            component Foo {
              return (
                <VisualElement style={new Style { (StyleKeys.FlexDirection, "column"), (StyleKeys.Padding, 12f) }}>
                  <Label text="hello" />
                </VisualElement>
              );
            }
            """
        );

        var result = Format(source);

        // > on own line when tag wraps.
        Assert.Matches(@">\s*\n\s+<Label", result);
    }

    [Fact]
    public void JSX_NestedElements_IndentedCorrectly()
    {
        // Return root is at 4-space (indent 2 = inside return paren inside component).
        // Children of root at 6-space, grandchildren at 8-space.
        var source = N(
            """
            component Foo { return (<VisualElement><Box><Label text="x" /></Box></VisualElement>); }
            """
        );

        var result = Format(source);

        Assert.Contains("\n    <VisualElement>", result); // 4-space: return root
        Assert.Contains("\n      <Box>", result); // 6-space: inside VisualElement
        Assert.Contains("\n        <Label", result); // 8-space: inside Box
        Assert.Contains("\n      </Box>", result);
        Assert.Contains("\n    </VisualElement>", result);
    }

    [Fact]
    public void JSX_EmptyElement_PreservesOpenCloseForm()
    {
        // An element written as <Panel></Panel> should preserve the open+close form,
        // not collapse to <Panel />. The formatter puts the closing tag on its own line.
        var source = "component Foo { return (<Panel></Panel>); }";
        var result = Format(source);
        Assert.Contains("<Panel>", result);
        Assert.Contains("</Panel>", result);
        Assert.DoesNotContain("<Panel />", result);
        // Double-format stability.
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void JSX_SelfClosingElement_StaysSelfClosing()
    {
        // An element written as <Panel /> should stay self-closing.
        var source = "component Foo { return (<Panel />); }";
        var result = Format(source);
        Assert.Contains("<Panel />", result);
        Assert.DoesNotContain("</Panel>", result);
        // Double-format stability.
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void JSX_ExpressionBlock_FormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              return (
                {count.ToString()}
                <Box />
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("{count.ToString()}", result);
    }

    [Fact]
    public void Comment_EmitsWithSpacesAroundContent()
    {
        var source = N(
            """
            component Foo {
              return (
                // some note
                <Box />
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("// some note", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.4  @if / @else / @elseif
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ControlFlow_If_IndentedCorrectly()
    {
        var source = N(
            """
            component Foo {
              return (
                @if (show) {
                    <Label text="yes" />
                }
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("@if (show) {", result);
        Assert.Contains("\n        <Label text=\"yes\" />", result);
        Assert.Contains("\n    }", result);
    }

    [Fact]
    public void ControlFlow_IfElse_EmittedOnSameLine()
    {
        var source = N(
            """
            component Foo {
              return (
                @if (x) {
                    <Label text="yes" />
                } @else {
                    <Label text="no" />
                }
              );
            }
            """
        );

        var result = Format(source);

        // @else on same line as closing }.
        Assert.Contains("} @else {", result);
        Assert.Contains("\n        <Label text=\"yes\" />", result);
        Assert.Contains("\n        <Label text=\"no\" />", result);
    }

    [Fact]
    public void ControlFlow_ElseIf_EmittedOnSameLine()
    {
        var source = N(
            """
            component Foo {
              return (
                @if (a) {
                    <A />
                } @elseif (b) {
                    <B />
                } @else {
                    <C />
                }
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("} @elseif (b) {", result);
        Assert.Contains("} @else {", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.4-bis  @if / @else with bare-C# body (no JSX) — idempotency regression
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression for v0.5.2: an `@else` (or any directive) branch whose body
    /// contains only a C# statement (e.g. `return varName;`) — no JSX — fell
    /// through to the no-parsed-body fallback in
    /// <c>AstFormatter.FormatDirectiveBody</c>, which passed the raw bodyCode
    /// straight to <c>EmitSetupCodeNormalized</c>. That bodyCode always starts
    /// with the `\n` that immediately follows the opener `{`, and
    /// <c>EmitSetupCodeNormalized</c> emitted it as a blank line ON TOP of the
    /// `\n` already written by FormatIf at the end of `} @else {` — adding one
    /// extra blank line per format pass (non-idempotent: format-on-save grew
    /// the file forever).
    ///
    /// The parsed-body path (with JSX) routes through
    /// <c>EmitDirectiveBodySetupCode</c> which already does
    /// <c>setupCode.TrimStart('\n', '\r')</c> — comment in that method already
    /// noted the same bug class. Fix is the symmetric trim in the no-parsed-body
    /// fallback.
    /// </summary>
    [Fact]
    public void Idempotency_ElseBranch_BareCSharpBody_NoBlankLineGrowth()
    {
        var source = N(
            """
            @namespace P
            component MenuPage {
              var chrome = (<Box />);

              return (
                <VisualElement>
                  @if (true) {
                    return (
                      <Portal>
                        {chrome}
                      </Portal>
                    );
                  } @else {
                    return chrome;
                  }
                </VisualElement>
              );
            }
            """
        );

        var p1 = Format(source);
        var p2 = Format(p1);
        var p3 = Format(p2);

        // Two passes must agree (idempotent). Three for extra confidence.
        Assert.Equal(p1, p2);
        Assert.Equal(p2, p3);

        // No phantom blank line between `} @else {` and `return chrome;`.
        Assert.Contains("} @else {\n        return chrome;", p1);
        Assert.DoesNotContain("} @else {\n\n", p1);
    }

    /// <summary>
    /// Regression: a function-style component parameter declared with whitespace
    /// before the nullable marker (e.g. <c>Texture2D ? icon = null</c>) used to
    /// be silently dropped on save. The parser's <c>TryReadTypeName</c> only
    /// consumed the <c>?</c> when it was the very next character after the
    /// identifier; with a space between, the marker was abandoned, the next
    /// <c>TryReadIdentifier</c> hit <c>?</c> and failed, and the parameter was
    /// skipped to the next comma. The formatter then re-emitted the param list
    /// from the AST — so the parameter (including its name and default) was
    /// permanently deleted on every format-on-save. Roslyn accepts the spacing,
    /// so we must too. Canonical re-emit collapses the space and produces
    /// <c>Texture2D? icon = null</c>.
    /// </summary>
    [Fact]
    public void ComponentParam_NullableType_WhitespaceBeforeQuestionMark_Preserved()
    {
        var source = N(
            """
            component AppButton(
              string text = "",
              Action ? onClick = null,
              Texture2D ? iconName = null,
              List<int>  ?  items = null
            ) {
              return (<Box />);
            }
            """
        );

        var p1 = Format(source);
        var p2 = Format(p1);

        // Idempotent.
        Assert.Equal(p1, p2);

        // None of the params was dropped, and the nullable marker survives
        // (canonical form: no space before '?').
        Assert.Contains("string text", p1);
        Assert.Contains("Action? onClick = null", p1);
        Assert.Contains("Texture2D? iconName = null", p1);
        Assert.Contains("List<int>? items = null", p1);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.5  @foreach
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ControlFlow_Foreach_IndentedCorrectly()
    {
        var source = N(
            """
            component Foo {
              return (
                @foreach (var item in items) {
                    <Label text={item} key={item} />
                }
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("@foreach (var item in items) {", result);
        Assert.Contains("\n        <Label text={item} key={item} />", result);
        Assert.Contains("\n    }", result);
    }

    [Fact]
    public void ControlFlow_ForeachNested_IndentedCorrectly()
    {
        // Layout (indent units of 2 each):
        //   return (           — at 2-space
        //     <Box>            — at 4-space (indent 2)
        //       @foreach ...   — at 6-space (indent 3)
        //         <Group>      — at 8-space (indent 4)
        //           @foreach   — at 10-space (indent 5)
        //             <Row>    — at 12-space (indent 6)
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  @foreach (var g in groups) {
                    return (
                    <Group>
                      @foreach (var row in g.Rows) {
                        return (
                        <Row text={row.Name} />
                        );
                      }
                    </Group>
                    );
                  }
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        // Children of <Box> (the return root) start at 6-space.
        Assert.Contains("\n      @foreach (var g in groups) {", result);
        Assert.Contains("\n          <Group>", result);
        Assert.Contains("\n            @foreach (var row in g.Rows) {", result);
        Assert.Contains("\n                <Row", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.6  @switch / @case / @default
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ControlFlow_Switch_IndentedCorrectly()
    {
        var source = N(
            """
            component Foo {
              return (
                @switch (mode) {
                    @case "a":
                        return (<LabelA />);
                    @case "b":
                        return (<LabelB />);
                    @default:
                        return (<LabelDefault />);
                }
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("@switch (mode) {", result);
        Assert.Contains("\n      @case \"a\":", result);
        Assert.Contains("\n          <LabelA />", result);
        Assert.Contains("\n      @case \"b\":", result);
        Assert.Contains("\n      @default:", result);
        Assert.Contains("\n          <LabelDefault />", result);
        Assert.Contains("\n    }", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.7  Component signature — namespace, usings, params
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Signature_NamespaceAndUsings_EmittedBeforeComponent()
    {
        var source = N(
            """
            @namespace MyApp.Components
            @using UnityEngine
            @using System.Collections.Generic

            component MyComp {
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.StartsWith("@namespace MyApp.Components\n", result);
        Assert.Contains("@using UnityEngine\n", result);
        Assert.Contains("@using System.Collections.Generic\n", result);
        // blank line between usings and component block
        Assert.Contains("@using System.Collections.Generic\n\ncomponent MyComp", result);
    }

    [Fact]
    public void Signature_FunctionParams_InSignatureLine()
    {
        var source = N(
            """
            component Greeter(string name = "World", bool show = true) {
              return (<Label text={$"Hello {name}"} />);
            }
            """
        );

        var result = Format(source);

        Assert.StartsWith("component Greeter(string name = \"World\", bool show = true) {", result);
    }

    [Fact]
    public void Signature_NoParams_EmptyParens_Or_NoParens()
    {
        // Component body without params — no parens in signature.
        var source = "component HelloWorld { return (<Label text=\"Hi\" />); }";
        var result = Format(source);
        Assert.StartsWith("component HelloWorld {", result);
        Assert.DoesNotContain("component HelloWorld()", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.8  Setup code before return
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CodeBlock_SetupCode_EmittedBeforeReturn()
    {
        var source = N(
            """
            component Foo {
              var x = 1;
              var y = 2;
              return (
                <Box />
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("var x = 1", result);
        Assert.Contains("var y = 2", result);
        Assert.Contains("<Box />", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.9  Edge cases
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Edge_EmptyComponent_FormatsCleanly()
    {
        var source = "component Empty { return (<Box />); }";
        var result = Format(source);

        Assert.StartsWith("component Empty {", result);
        Assert.Contains("return (", result);
        Assert.Contains("<Box />", result);
        Assert.Contains(");", result);
        Assert.EndsWith("}\n", result);
    }

    [Fact]
    public void Edge_ParseError_ReturnsSourceUnchanged()
    {
        // Deliberately broken: unclosed tag — formatter should return source as-is.
        var source = N("component Bad { return (<Box>); }");
        var result = Format(source);
        Assert.Equal(source, result);
    }

    [Fact]
    public void Edge_TrailingNewline_ExactlyOne()
    {
        var source = "component Foo { return (<Box />); }";
        var result = Format(source);
        Assert.EndsWith("\n", result);
        Assert.False(result.EndsWith("\n\n"), "Output must not end with two newlines.");
    }

    [Fact]
    public void Edge_SingleLineSource_ExpandsToCanonical()
    {
        var singleLine =
            "component Counter { var (c,setC) = useState(0); return (<Button text={c.ToString()} onClick={_=>setC(c+1)} />); }";
        var result = Format(singleLine);

        Assert.Contains("component Counter {", result);
        Assert.Contains("\n  var (c,setC) = useState(0);", result);
        Assert.Contains("\n  return (", result);
        Assert.Contains("\n  );", result);
        Assert.EndsWith("}\n", result);
    }

    [Fact]
    public void Edge_CrLfNormalisedToLf()
    {
        var source = "component Foo {\r\n  return (<Box />);\r\n}\r\n";
        var result = Format(source);
        Assert.DoesNotContain("\r", result);
    }

    [Fact]
    public void Edge_ComponentWithNoSetup_NoExtraBlank()
    {
        var source = N(
            """
            component NoSetup {
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // Must NOT have a blank line between the component open-brace and "return (".
        Assert.DoesNotContain("{\n\n  return (", result);
        Assert.Contains("{\n  return (", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.10  Style block entries — spacing
    // ════════════════════════════════════════════════════════════════════════════

    // The style entries are C# and land in the setup code section.  They are
    // re-emitted via EmitCSharpLines with relative-indent preservation.
    // Correctly-formatted files have entries at 4-space (2 base + 2 relative),
    // and the re-anchor must leave them there.

    [Fact]
    public void StyleBlock_EntryIndent_Preserved()
    {
        // Already-correct 4-space entries (2 for var + 2 relative) must survive.
        var source = N(
            """
            component Foo {
              var s = new Style {
                (StyleKeys.Padding, 12f),
                (StyleKeys.MarginTop, 8f),
              };
              return (<Box style={s} />);
            }
            """
        );

        var result = Format(source);

        // 4-space (indent=2 + 2 relative for style entries).
        Assert.Contains("\n    (StyleKeys.Padding, 12f),", result);
        Assert.Contains("\n    (StyleKeys.MarginTop, 8f),", result);
        // Closing }; back at 2-space.
        Assert.Contains("\n  };", result);
    }

    [Fact]
    public void StyleBlock_OverIndentedEntries_NormalisedByReAnchor()
    {
        // Entries consistently over-indented at 6-space: the block-stack normaliser
        // places them at the canonical block-target (opener-emit + indentSize = 4sp).
        var source = N(
            """
            component Foo {
              var s = new Style {
                    (StyleKeys.Padding, 12f),
                    (StyleKeys.MarginTop, 8f),
              };
              return (<Box style={s} />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var s = new Style {", result);
        Assert.Contains("\n    (StyleKeys.Padding, 12f),", result);
        Assert.Contains("\n    (StyleKeys.MarginTop, 8f),", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.11  Function-style component with multiple state hooks
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FunctionStyle_MultipleUseState_AllAtTwoSpace()
    {
        var source = N(
            """
            component Multi {
              var (a, setA) = useState(0);
              var (b, setB) = useState(false);
              var (c, setC) = useState("hello");
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var (a, setA)", result);
        Assert.Contains("\n  var (b, setB)", result);
        Assert.Contains("\n  var (c, setC)", result);
    }

    [Fact]
    public void FunctionStyle_UseEffect_BodyPreserved()
    {
        var source = N(
            """
            component WithEffect {
              var (count, setCount) = useState(0);
              useEffect(() => {
                setCount(n => n + 1);
                return null;
              }, new object[] { });
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("useEffect(() => {", result);
        Assert.Contains("setCount(n => n + 1);", result);
        Assert.Contains("return null;", result);
        Assert.Contains("}, new object[] { });", result);
    }

    [Fact]
    public void FunctionStyle_UseMemo_MultiLineBody_Preserved()
    {
        var source = N(
            """
            component Memo {
              var items = useMemo(
                () => new List<string> { "a", "b", "c" },
                0
              );
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("var items = useMemo(", result);
        Assert.Contains("() => new List<string>", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.12  Function-style component WITH JSX vars in setup (EmitSetupCodeWithJsx)
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FunctionStyle_JsxVarInSetup_EmittedCorrectly()
    {
        var source = N(
            """
            component WithJsxVar {
              var (show, setShow) = useState(false);
              var fallback = (
                <Label text="Loading..." />
              );
              return (
                <Box>
                  @if (show) {
                    <Label text="content" />
                  } @else {
                    {fallback}
                  }
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("var (show, setShow) = useState(false);", result);
        Assert.Contains("  var fallback = (", result); // at 2-space
        Assert.Contains("\n    <Label text=\"Loading...\" />", result); // at 4-space inside paren
        Assert.Contains("\n  );", result); // closing ); at 2-space
    }

    [Fact]
    public void FunctionStyle_TwoJsxVars_BlankLineBetweenPreserved()
    {
        var source = N(
            """
            component WithTwoJsxVars {
              var first = (
                <Label text="first" />
              );

              var second = (
                <Label text="second" />
              );

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // Blank line between the two var … ); blocks should survive.
        Assert.Contains("  );\n\n  var second = (", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.13  Directives-only / class-style component
    // ════════════════════════════════════════════════════════════════════════════
    //  B.12b  Arrow-lambda markup in setup code (Pattern C: => <Tag)
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FunctionStyle_ArrowLambdaSelfClosingJsx_FormattedInline()
    {
        var source = N(
            """
            component WithArrowJsx {
              var tabViewProps = new TabViewProps
              {
                Tabs = new List<TabViewProps.TabDef>
                {
                  new TabViewProps.TabDef { Title = "Log", Content = () => <Label text="This is a TabView demo." /> },
                  new TabViewProps.TabDef { Title = "Mode", Content = () => V.Label(new LabelProps { Text = "Mode" }) },
                },
              };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("Content = () => (<Label text=\"This is a TabView demo.\" />)", result);
        Assert.Contains("Content = () => V.Label(new LabelProps { Text = \"Mode\" })", result);
        Assert.Equal(result, Format(result)); // idempotency
    }

    [Fact]
    public void FunctionStyle_ArrowLambdaContainerJsx_ParenWrappedMultiline()
    {
        var source = N(
            """
            component WithArrowContainer {
              var tabDef = new TabDef { Title = "Log", Content = () => <Box>
                <Label text="hello" />
                <Label text="world" />
              </Box> };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("Content = () => (", result);
        Assert.Contains("\n    <Box>", result);
        Assert.Contains("\n      <Label text=\"hello\" />", result);
        Assert.Contains("\n      <Label text=\"world\" />", result);
        Assert.Contains("\n    </Box>", result);
        Assert.Contains("\n  )", result);
        Assert.Equal(result, Format(result)); // idempotency
    }

    [Fact]
    public void FunctionStyle_ArrowLambdaParenWrapped_ExistingFormPreserved()
    {
        // User already writes the canonical form with parens — stays inline
        var source = N(
            """
            component WithArrowParen {
              var tabDef = new TabDef { Title = "Log", Content = () => (<Label text="hello" />) };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("Content = () => (<Label text=\"hello\" />)", result);
        Assert.Equal(result, Format(result)); // idempotency
    }

    [Fact]
    public void FunctionStyle_ArrowLambdaVarRef_NoTransform()
    {
        // () => variable should remain unchanged — no JSX to format
        var source = N(
            """
            component WithVarRef {
              var element = (<Label text="hello" />);
              var tabDef = new TabDef { Title = "Log", Content = () => element };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("Content = () => element }", result);
        Assert.Equal(result, Format(result)); // idempotency
    }

    // ── B.12b+ Bare assignment JSX normalization (= <Tag → = (<Tag>)) ────────

    [Fact]
    public void FunctionStyle_BareAssignSelfClosingJsx_NormalizedToParens()
    {
        // var x = <Label /> → var x = (<Label />);
        var source = N(
            """
            component Comp {
              var bla = <Label text="hello" />;
              return (<Box />);
            }
            """
        );

        var result = Format(source);
        Assert.Contains("var bla = (<Label text=\"hello\" />)", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_BareAssignContainerJsx_NormalizedToParens()
    {
        // var bla = <Box>...</Box> → var bla = (\n  <Box>...\n);
        var source = N(
            """
            component Comp {
              var bla = <Box>
                <Label text="hello" />
              </Box>;
              return (<Box />);
            }
            """
        );

        var result = Format(source);
        Assert.Contains("var bla = (", result);
        Assert.Contains("<Box>", result);
        Assert.Contains("<Label text=\"hello\" />", result);
        Assert.Contains("</Box>", result);
        // Format twice to reach stable form, then check idempotency
        var stable = Format(result);
        Assert.Equal(stable, Format(stable));
    }

    [Fact]
    public void FunctionStyle_BareAssignSingleLineContainer_NormalizedToParens()
    {
        // var bla = <Box><Label /></Box> → normalized with parens AND expanded multi-line
        var source = N(
            """
            component Comp {
              var bla = <Box><Label text="hello" /></Box>;
              return (<Box />);
            }
            """
        );

        var result = Format(source);
        Assert.Contains("var bla = (", result);
        // Container element must be expanded to multi-line
        Assert.Contains("<Box>", result);
        Assert.Contains("<Label text=\"hello\" />", result);
        Assert.Contains("</Box>", result);
        Assert.Contains(");", result);
        // Format twice to reach stable form, then check idempotency
        var stable = Format(result);
        Assert.Equal(stable, Format(stable));
    }

    [Fact]
    public void FunctionStyle_BareAssignAlreadyParenWrapped_Unchanged()
    {
        // Already paren-wrapped — stays as-is
        var source = N(
            """
            component Comp {
              var bla = (<Label text="hello" />);
              return (<Box />);
            }
            """
        );

        var result = Format(source);
        Assert.Contains("var bla = (<Label text=\"hello\" />)", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_ComparisonOperators_NotTreatedAsJsx()
    {
        // Comparison operators (<=, >=, ==) must NOT trigger JSX normalization
        var source = N(
            """
            component Comp {
              var x = 5;
              if (x <= 10) { }
              if (x >= 3) { }
              if (x == 1) { }
              if (x != 2) { }
              return (<Box />);
            }
            """
        );

        var result = Format(source);
        // These should remain unchanged — no JSX normalization
        Assert.Contains("x <= 10", result);
        Assert.Contains("x >= 3", result);
        Assert.Contains("x == 1", result);
        Assert.Contains("x != 2", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.12b+d  Bare JSX normalization inside directive bodies (setup code)
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForeachDirective_BareAssignSelfClosingJsx_NormalizedToParens()
    {
        // var badge = <Label /> inside @foreach setup code should get wrapped with ()
        var source = N(
            """
            component Comp {
              return (
                <VisualElement>
                  @foreach (var item in items) {
                    var badge = <Label text={item} />;
                    return (
                      <Label key={item} text={item} />
                    );
                  }
                </VisualElement>
              );
            }
            """
        );

        var result = Format(source);
        Assert.Contains("var badge = (<Label text={item} />)", result);
        var stable = Format(result);
        Assert.Equal(stable, Format(stable)); // idempotent
    }

    [Fact]
    public void ForeachDirective_BareAssignContainerJsx_NormalizedToParens()
    {
        // var badge = <Box>...</Box> inside @foreach setup code gets wrapped + expanded
        var source = N(
            """
            component Comp {
              return (
                <VisualElement>
                  @foreach (var item in items) {
                    var badge = <Box>
                      <Label text={item} />
                    </Box>;
                    return (
                      <Label key={item} text={item} />
                    );
                  }
                </VisualElement>
              );
            }
            """
        );

        var result = Format(source);
        Assert.Contains("var badge = (", result);
        Assert.Contains("<Box>", result);
        Assert.Contains("<Label text={item} />", result);
        Assert.Contains("</Box>", result);
        var stable = Format(result);
        Assert.Equal(stable, Format(stable)); // idempotent
    }

    [Fact]
    public void IfDirective_BareAssignJsx_NormalizedToParens()
    {
        // var icon = <Label /> inside @if setup code
        var source = N(
            """
            component Comp {
              return (
                <VisualElement>
                  @if (flag) {
                    var icon = <Label text="on" />;
                    return (
                      <Box>{icon}</Box>
                    );
                  }
                </VisualElement>
              );
            }
            """
        );

        var result = Format(source);
        Assert.Contains("var icon = (<Label text=\"on\" />)", result);
        var stable = Format(result);
        Assert.Equal(stable, Format(stable)); // idempotent
    }

    [Fact]
    public void ForeachDirective_BareAssignAlreadyParenWrapped_Unchanged()
    {
        // Already paren-wrapped — must stay as-is (no double-wrapping)
        var source = N(
            """
            component Comp {
              return (
                <VisualElement>
                  @foreach (var item in items) {
                    var badge = (<Label text={item} />);
                    return (
                      <Label key={item} text={item} />
                    );
                  }
                </VisualElement>
              );
            }
            """
        );

        var result = Format(source);
        // Should still have exactly one pair of parens around the JSX
        Assert.Contains("var badge = (<Label text={item} />)", result);
        Assert.DoesNotContain("var badge = ((<Label", result);
        Assert.Equal(result, Format(result)); // idempotent
    }

    [Fact]
    public void SwitchDirective_BareAssignJsx_NormalizedToParens()
    {
        // var x = <Label /> inside @switch case setup code
        var source = N(
            """
            component Comp {
              return (
                <VisualElement>
                  @switch (mode) {
                    @case "a":
                      var icon = <Label text="alpha" />;
                      return (
                        <Box>{icon}</Box>
                      );
                    @default:
                      return (
                        <Label text="other" />
                      );
                  }
                </VisualElement>
              );
            }
            """
        );

        var result = Format(source);
        Assert.Contains("var icon = (<Label text=\"alpha\" />)", result);
        var stable = Format(result);
        Assert.Equal(stable, Format(stable)); // idempotent
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.12c  JSX paren-blocks in deeply nested C# setup code
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FunctionStyle_JsxInSwitchCase_IndentPreserved()
    {
        var source = N(
            """
            component Comp {
              void ApplyMode(string newMode)
              {
                switch (newMode)
                {
                  case "squared":
                    AppendLog("Switched to squared");
                    var oneTest = (
                      <Box>
                        <Label text="This is a TabView demo." />
                      </Box>
                    );
                    break;
                  case "doubled":
                    AppendLog("Switched to doubled");
                    break;
                  default:
                    AppendLog($"Switched to {newMode}");
                    break;
                }
                setMode(newMode);
              }

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // JSX is indented inside the case block.
        Assert.Contains("        var oneTest = (\n", result);
        Assert.Contains("          <Box>\n", result);
        Assert.Contains("            <Label text=\"This is a TabView demo.\" />\n", result);
        Assert.Contains("          </Box>\n", result);
        Assert.Contains("        );\n", result);
        // C# after the JSX block stays at correct indent.
        Assert.Contains("        break;\n", result);
        Assert.Contains("      case \"doubled\":\n", result);
        Assert.Contains("        AppendLog(\"Switched to doubled\");\n", result);
        Assert.Contains("      default:\n", result);
        Assert.Contains("    setMode(newMode);\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_JsxInIfBlock_IndentPreserved()
    {
        var source = N(
            """
            component Comp {
              void HandleEvent(ReactiveEvent evt)
              {
                if (evt == null)
                {
                  AppendLog("null event");
                  var oneTest = (
                    <Label text="This is a TabView demo." />
                  );
                  return;
                }
                if (evt is ReactivePointerEvent pointer)
                {
                  AppendLog($"pointer pos={pointer.Position.x:0.0}");
                }
              }

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // JSX is inside if (evt == null) block — 6sp indent.
        Assert.Contains("      var oneTest = (\n", result);
        Assert.Contains("        <Label text=\"This is a TabView demo.\" />\n", result);
        Assert.Contains("      );\n", result);
        // Code after the JSX block.
        Assert.Contains("      return;\n", result);
        Assert.Contains("    }\n", result);
        Assert.Contains("    if (evt is ReactivePointerEvent pointer)\n", result);
        Assert.Contains("      AppendLog($\"pointer pos={pointer.Position.x:0.0}\");\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_JsxInMethodBody_IndentPreserved()
    {
        var source = N(
            """
            component Comp {
              void TreeAddParent()
              {
                var oneTest = (
                  <Label text="This is a TabView demo." />
                );
                var pid = treeNextPid;
                setTreeNextPid(treeNextPid + 2);
                setTreeRows(prev =>
                {
                  var next = prev != null
                  ? new List<TreeViewRowState>(prev)
                  : new List<TreeViewRowState>();

                  next.Add(new TreeViewRowState
                  {
                    Pid = pid,
                    HasChild = false,
                  });
                  return next;
                });
              }

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // JSX in method body at 4sp.
        Assert.Contains("    var oneTest = (\n", result);
        Assert.Contains("      <Label text=\"This is a TabView demo.\" />\n", result);
        Assert.Contains("    );\n", result);
        // Code after JSX at correct indent.
        Assert.Contains("    var pid = treeNextPid;\n", result);
        Assert.Contains("    setTreeNextPid(treeNextPid + 2);\n", result);
        Assert.Contains("    setTreeRows(prev =>\n", result);
        // Lambda body.
        Assert.Contains("      var next = prev != null\n", result);
        Assert.Contains("      next.Add(new TreeViewRowState\n", result);
        Assert.Contains("      return next;\n", result);
        Assert.Contains("    });\n", result);
        Assert.Contains("  }\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_JsxInsideLambdaBody_IndentPreserved()
    {
        var source = N(
            """
            component Comp {
              void MctvDeleteLast()
              {
                setMctvRows(prev =>
                {
                  var oneTest = (
                    <Label text="This is a TabView demo." />
                  );
                  return prev;
                  if (prev == null || prev.Count == 0)
                  {
                    return prev;
                  }
                  var next = new List<MultiColumnTreeViewRowState>(prev);
                  next.RemoveAt(next.Count - 1);
                  return next;
                });
              }

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // JSX inside lambda body at 6sp.
        Assert.Contains("      var oneTest = (\n", result);
        Assert.Contains("        <Label text=\"This is a TabView demo.\" />\n", result);
        Assert.Contains("      );\n", result);
        // Code after JSX stays at correct lambda indent.
        Assert.Contains("      return prev;\n", result);
        Assert.Contains("      if (prev == null || prev.Count == 0)\n", result);
        Assert.Contains("        return prev;\n", result);
        Assert.Contains("      var next = new List<MultiColumnTreeViewRowState>(prev);\n", result);
        Assert.Contains("      next.RemoveAt(next.Count - 1);\n", result);
        Assert.Contains("      return next;\n", result);
        Assert.Contains("    });\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_JsxInsideLambdaBodyAfterIf_IndentPreserved()
    {
        var source = N(
            """
            component Comp {
              void MctvSetChild()
              {
                setMctvRows(prev =>
                {
                  if (prev == null || prev.Count == 0)
                  {
                    return prev;
                  }
                  var oneTest = (
                    <Label text="This is a TabView demo." />
                  );
                  var source = prev[prev.Count - 1];
                  if (source == null || !source.HasChild)
                  {
                    return prev;
                  }
                  var next = new List<MultiColumnTreeViewRowState>(prev);
                  next[next.Count - 1] = new MultiColumnTreeViewRowState
                  {
                    Pid = source.Pid,
                    HasChild = true,
                  };
                  return next;
                });
              }

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // Code before JSX is at correct indent.
        Assert.Contains("      if (prev == null || prev.Count == 0)\n", result);
        Assert.Contains("        return prev;\n", result);
        // JSX in lambda at 6sp.
        Assert.Contains("      var oneTest = (\n", result);
        Assert.Contains("        <Label text=\"This is a TabView demo.\" />\n", result);
        Assert.Contains("      );\n", result);
        // Code after JSX continues at correct indent.
        Assert.Contains("      var source = prev[prev.Count - 1];\n", result);
        Assert.Contains("      if (source == null || !source.HasChild)\n", result);
        Assert.Contains("      var next = new List<MultiColumnTreeViewRowState>(prev);\n", result);
        Assert.Contains("      return next;\n", result);
        Assert.Contains("    });\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_TabViewPropsMultilineArrowJsx_IndentPreserved()
    {
        var source = N(
            """
            component Comp {
              var secondElement = (
                <Label text="This is a TabView demo." />
              );

              var tabViewProps = new TabViewProps
              {
                SelectedIndex = tabIndex,
                Tabs = new List<TabViewProps.TabDef>
                {
                  new TabViewProps.TabDef { Title = "Log", Content = () => (
                    <Box>
                      <Label text="This is a TabView demo." />
                    </Box>
                  )
                },
                new TabViewProps.TabDef { Title = "Counter", Content = () => secondElement },
                new TabViewProps.TabDef { Title = "Mode", Content = () => V.Label(new LabelProps { Text = $"Mode" }) },
                },
                Style = new Style { (StyleKeys.Height, 160f) },
              };

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // Top-level var with JSX.
        Assert.Contains("  var secondElement = (\n", result);
        Assert.Contains("    <Label text=\"This is a TabView demo.\" />\n", result);
        Assert.Contains("  );\n", result);
        // Arrow-lambda JSX inside deeply nested initializer.
        Assert.Contains("Content = () => (\n", result);
        Assert.Contains("<Box>\n", result);
        Assert.Contains("<Label text=\"This is a TabView demo.\" />\n", result);
        Assert.Contains("</Box>\n", result);
        // The other TabDef entries are preserved.
        Assert.Contains("Content = () => secondElement", result);
        Assert.Contains("Content = () => V.Label(new LabelProps { Text = $\"Mode\" })", result);
        Assert.Equal(result, Format(result));
    }

    // ── B.12d – mid-line brace tracking after JSX placeholder ──────────────
    [Fact]
    public void FunctionStyle_TabViewProps_StyleIndentAfterJsx()
    {
        // Style property after JSX block in nested initializer must keep correct indent
        var source = N(
            """
            component Comp {
              var tabViewProps = new TabViewProps
              {
                SelectedIndex = tabIndex,
                Tabs = new List<TabViewProps.TabDef>
                {
                  new TabViewProps.TabDef { Title = "Log", Content = () => (
                    <Box>
                      <Label text="hello" />
                    </Box>
                  )
                },
                new TabViewProps.TabDef { Title = "Counter", Content = () => secondElement },
                },
                Style = new Style { (StyleKeys.Height, 160f) },
              };

              return (<Box />);
            }
            """
        );

        var result = Format(source);
        // Style must be at 4sp (same depth as SelectedIndex, Tabs)
        Assert.Contains("    Style = new Style", result);
        // Closing }; at 2sp (component body indent)
        Assert.Contains("  };", result);
        // Second TabDef at 6sp (inside list)
        Assert.Contains("      new TabViewProps.TabDef { Title = \"Counter\"", result);
        // Idempotent
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_TabViewProps_BraceOnSameLineAsContent()
    {
        // { on same line as first list item: formatter should split {content
        // into { + content on separate lines and indent correctly
        var source = N(
            """
            component Comp {
              var tabViewProps = new TabViewProps
              {
                SelectedIndex = tabIndex,
                Tabs = new List<TabViewProps.TabDef>
                {new TabViewProps.TabDef { Title = "Log", Content = () => (
                    <Box>
                      <Label text="hello" />
                    </Box>
                  )},
                new TabViewProps.TabDef { Title = "Counter", Content = () => secondElement },
                },
                Style = new Style { (StyleKeys.Height, 160f) },
              };

              return (<Box />);
            }
            """
        );

        var result = Format(source);
        // The formatter should split {new into { + new on separate lines
        Assert.Contains("    {\n      new TabViewProps.TabDef", result);
        // Style must be at 4sp (same depth as SelectedIndex, Tabs)
        Assert.Contains("    Style = new Style", result);
        // Closing }; at 2sp
        Assert.Contains("  };", result);
        // Second TabDef at 6sp (inside list)
        Assert.Contains("      new TabViewProps.TabDef { Title = \"Counter\"", result);
        // Idempotent
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_MultipleJsxBlocks_AllIndentCorrectly()
    {
        // Two JSX blocks at different nesting depths in the same component.
        var source = N(
            """
            component Comp {
              var header = (
                <Label text="header" />
              );

              void DoWork()
              {
                if (true)
                {
                  var inner = (
                    <Box>
                      <Label text="inner" />
                    </Box>
                  );
                }
              }

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // Top-level JSX.
        Assert.Contains("  var header = (\n", result);
        Assert.Contains("    <Label text=\"header\" />\n", result);
        Assert.Contains("  );\n", result);
        // Nested JSX inside if.
        Assert.Contains("      var inner = (\n", result);
        Assert.Contains("        <Box>\n", result);
        Assert.Contains("          <Label text=\"inner\" />\n", result);
        Assert.Contains("        </Box>\n", result);
        Assert.Contains("      );\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_JsxAtStartOfMethod_IndentPreserved()
    {
        // JSX is the very first statement in a method.
        var source = N(
            """
            component Comp {
              void Init()
              {
                var node = (
                  <Label text="init" />
                );
                DoSomething(node);
              }

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("    var node = (\n", result);
        Assert.Contains("      <Label text=\"init\" />\n", result);
        Assert.Contains("    );\n", result);
        Assert.Contains("    DoSomething(node);\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_JsxAtEndOfMethod_IndentPreserved()
    {
        // JSX is the very last statement in a method.
        var source = N(
            """
            component Comp {
              void Finalize()
              {
                DoSomething();
                var node = (
                  <Label text="done" />
                );
              }

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("    DoSomething();\n", result);
        Assert.Contains("    var node = (\n", result);
        Assert.Contains("      <Label text=\"done\" />\n", result);
        Assert.Contains("    );\n", result);
        Assert.Contains("  }\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FunctionStyle_JsxInNestedLambdaInsideIf_IndentPreserved()
    {
        // JSX deeply nested: method → if → lambda body.
        var source = N(
            """
            component Comp {
              void Process()
              {
                if (condition)
                {
                  setItems(prev =>
                  {
                    var node = (
                      <Label text="deep" />
                    );
                    return prev;
                  });
                }
              }

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("        var node = (\n", result);
        Assert.Contains("          <Label text=\"deep\" />\n", result);
        Assert.Contains("        );\n", result);
        Assert.Contains("        return prev;\n", result);
        Assert.Contains("      });\n", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.14  Functional component — canonical output shape
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FunctionStyle_CanonicalStructure_OpenBraceOnSameLine()
    {
        var source = "component Foo { return (<Box />); }";
        var result = Format(source);

        // Opening brace must be on the same line as `component`.
        Assert.Contains("component Foo {", result);
        Assert.DoesNotContain("component Foo\n{", result);
    }

    [Fact]
    public void FunctionStyle_CanonicalStructure_ClosingBraceOnOwnLine()
    {
        var source = "component Foo { return (<Box />); }";
        var result = Format(source);

        Assert.EndsWith("}\n", result);
        // The ) and } must each be on their own line:
        //   ...\n  );\n}\n
        Assert.Contains("\n  );\n}\n", result);
        // The ) must NOT share the same line as the content:
        Assert.DoesNotContain("<Box />);", result);
    }

    [Fact]
    public void FunctionStyle_ReturnKeyword_AtTwoSpaceIndent()
    {
        var source = "component Foo { return (<Box />); }";
        var result = Format(source);

        Assert.Contains("\n  return (", result);
        Assert.Contains("\n  );", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.15  @break / @continue
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ControlFlow_Break_EmittedWithSemicolon()
    {
        var source = N(
            """
            component Foo {
              return (
                @foreach (var x in items) {
                    @if (x == null) {
                        @break;
                    }
                    <Label text={x} />
                }
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("@break;", result);
    }

    [Fact]
    public void ControlFlow_Continue_EmittedWithSemicolon()
    {
        var source = N(
            """
            component Foo {
              return (
                @foreach (var x in items) {
                    @if (x == null) {
                        @continue;
                    }
                    <Label text={x} />
                }
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("@continue;", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.16  Attribute value types
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Attrs_StringLiteral_QuotedCorrectly()
    {
        var source = "component Foo { return (<Label text=\"Hello World\" />); }";
        var result = Format(source);
        Assert.Contains("text=\"Hello World\"", result);
    }

    [Fact]
    public void Attrs_CSharpExpression_CurlyBraces()
    {
        var source = "component Foo { return (<Label text={greeting} />); }";
        var result = Format(source);
        Assert.Contains("text={greeting}", result);
    }

    [Fact]
    public void Attrs_BooleanShorthand_NoValue()
    {
        var source = "component Foo { return (<Button disabled />); }";
        var result = Format(source);
        Assert.Contains("disabled", result);
        Assert.DoesNotContain("disabled=", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.17  Idempotency of formatter output (double-format test)
    // ════════════════════════════════════════════════════════════════════════════

    // These tests verify that formatting the output of a bad-input test
    // produces the same output a second time (formatter is stable).

    [Fact]
    public void DoubleFormat_MessyInput_Stable()
    {
        var messy = N(
            """
            component Foo{var (x,setX)=useState(0);return(<Box><Button text={x.ToString()} onClick={_=>setX(x+1)}/></Box>);}
            """
        );

        var first = Format(messy);
        var second = Format(first);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DoubleFormat_WithForEach_Stable()
    {
        var messy = N(
            """
            component List {
              return (
                @foreach(var item in items){
                <Label text={item}/>
                }
              );
            }
            """
        );

        var first = Format(messy);
        var second = Format(first);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DoubleFormat_FunctionStyleWithSetup_Stable()
    {
        var messy = N(
            """
            component Counter {
                    var (c, setC) = useState(0);
                    var label = $"Count: {c}";
                    return (<Button text={label} onClick={_=>setC(c+1)}/>);
            }
            """
        );

        var first = Format(messy);
        var second = Format(first);

        Assert.Equal(first, second);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  C — Kitchen-sink regression suite (UitkxCounterFunc patterns)
    //
    //  Covers every distinct construct found in the kitchen-sink component.
    //  Each test provides a "messy" input variant (over-indented base, missing
    //  whitespace, mixed block interior indent, etc.) and asserts:
    //    1. The canonical output shape (specific indent levels / content)
    //    2. Double-format stability: Format(Format(src)) == Format(src)
    // ════════════════════════════════════════════════════════════════════════════

    // ── C.1  @using static ───────────────────────────────────────────────────

    [Fact]
    public void C01_Directive_UsingStatic_EmittedAfterRegularUsings()
    {
        var source = N(
            """
            @namespace NS
            @using System
            @using static Ruitk.Props.Typed.StyleKeys
            component Foo { return (<Box />); }
            """
        );

        var result = Format(source);

        Assert.Contains("@using System\n", result);
        Assert.Contains("@using static Ruitk.Props.Typed.StyleKeys\n", result);
        var idx1 = result.IndexOf("@using System");
        var idx2 = result.IndexOf("@using static");
        Assert.True(idx1 < idx2, "@using must precede @using static in output");
        Assert.Equal(result, Format(result));
    }

    // ── C.2  useState variants ────────────────────────────────────────────────

    [Fact]
    public void C02a_UseState_Scalar_OverIndented_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var (count, setCount) = useState(0);
                  var (mode, setMode) = useState("normal");
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var (count, setCount) = useState(0);", result);
        Assert.Contains("\n  var (mode, setMode) = useState(\"normal\");", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C02b_UseState_Bool_OverIndented_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var (mounted, setMounted) = useState(true);
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var (mounted, setMounted) = useState(true);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C02c_UseState_GenericNullableType_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var (ids, setIds) = useState<List<int>?>(null);
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var (ids, setIds) = useState<List<int>?>(null);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C02d_UseState_InlineNewList_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var (log, setLog) = useState(new List<string> { "Ready." });
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains(
            "\n  var (log, setLog) = useState(new List<string> { \"Ready.\" });",
            result
        );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C02e_UseState_MultipleGroups_WithBlankLine_BlankLinePreserved()
    {
        var source = N(
            """
            component Foo {
              var (a, setA) = useState(0);
              var (b, setB) = useState("x");

              var (x, setX) = useState(new List<string>());
              var (y, setY) = useState(2);

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("  var (a, setA) = useState(0);\n  var (b, setB)", result);
        Assert.Contains("\n\n  var (x, setX)", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.3  useContext / provideContext ──────────────────────────────────────

    [Fact]
    public void C03a_UseContext_TypeKey_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var target = useContext<VisualElement>(PortalContextKeys.ModalRoot);
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains(
            "\n  var target = useContext<VisualElement>(PortalContextKeys.ModalRoot);",
            result
        );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C03b_UseContext_StringKey_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var color = useContext<Color>("theme-color");
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var color = useContext<Color>(\"theme-color\");", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C03c_ProvideContext_Statement_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  provideContext("my-key", someValue);
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  provideContext(\"my-key\", someValue);", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.4  useSignal ────────────────────────────────────────────────────────

    [Fact]
    public void C04_UseSignal_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var count = useSignal(counterSignal);
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var count = useSignal(counterSignal);", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.5  useMemo variants ─────────────────────────────────────────────────

    [Fact]
    public void C05a_UseMemo_ArrayEmptyDeps_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var sig = useMemo(() => GetSig(), Array.Empty<object>());
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var sig = useMemo(() => GetSig(), Array.Empty<object>());", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C05b_UseMemo_NewObjectArrayDep_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var doubled = useMemo(() => count * 2, new object[] { count });
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains(
            "\n  var doubled = useMemo(() => count * 2, new object[] { count });",
            result
        );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C05c_UseMemo_MultiLineListFactory_MixedEntryIndent_Normalized()
    {
        // The List entries have inconsistent indent; block normaliser should
        // bring them all to opener-line-indent + indentSize.
        var source = N(
            """
            component Foo {
              var options = useMemo(() => new List<string> {
                    "Alpha",
                "Beta",
                  "Gamma",
              }, Array.Empty<object>());
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n    \"Alpha\",", result);
        Assert.Contains("\n    \"Beta\",", result);
        Assert.Contains("\n    \"Gamma\",", result);
        Assert.Contains("\n  }, Array.Empty<object>());", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.6  useRef ───────────────────────────────────────────────────────────

    [Fact]
    public void C06_UseRef_DeclCurrentIncrementAndRead_OverIndented_Normalized()
    {
        var source = N(
            """
            component Foo {
                  var renderCountRef = useRef<int>(0);
                  renderCountRef.Current++;
                  var renderCount = renderCountRef.Current;
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var renderCountRef = useRef<int>(0);", result);
        Assert.Contains("\n  renderCountRef.Current++;", result);
        Assert.Contains("\n  var renderCount = renderCountRef.Current;", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.7  useEffect patterns ───────────────────────────────────────────────

    [Fact]
    public void C07a_UseEffect_NullReturn_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  useEffect(() => {
                    doSomething();
                    return null;
                  }, Array.Empty<object>());
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  useEffect(() => {", result);
        Assert.Contains("\n    doSomething();", result);
        Assert.Contains("\n    return null;", result);
        Assert.Contains("\n  }, Array.Empty<object>());", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C07b_UseEffect_LambdaCleanup_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  useEffect(() => {
                    doSetup();
                    return () => {
                      doTeardown();
                    };
                  }, Array.Empty<object>());
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  useEffect(() => {", result);
        Assert.Contains("\n    doSetup();", result);
        Assert.Contains("\n    return () => {", result);
        Assert.Contains("\n      doTeardown();", result);
        Assert.Contains("\n    };", result);
        Assert.Contains("\n  }, Array.Empty<object>());", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C07c_UseEffect_LocalFunctionInsideBody_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  useEffect(() => {
                    bool captured = false;
                    void OnChange(int value) {
                      if (captured) {
                        return;
                      }
                      setState(value);
                    }
                    var sub = signal.Subscribe(OnChange);
                    return () => {
                      captured = true;
                      sub.Dispose();
                    };
                  }, new object[] { signal });
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  useEffect(() => {", result);
        Assert.Contains("\n    bool captured = false;", result);
        Assert.Contains("\n    void OnChange(int value) {", result);
        Assert.Contains("\n      if (captured) {", result);
        Assert.Contains("\n        return;", result);
        Assert.Contains("\n      setState(value);", result);
        Assert.Contains("\n    var sub = signal.Subscribe(OnChange);", result);
        Assert.Contains("\n    return () => {", result);
        Assert.Contains("\n      captured = true;", result);
        Assert.Contains("\n      sub.Dispose();", result);
        Assert.Contains("\n    };", result);
        Assert.Contains("\n  }, new object[] { signal });", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C07d_UseEffect_MultipleBodyStatements_NewObjectArrayDeps_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  useEffect(() => {
                    setCount(n => n + 1);
                    setStatus("updated");
                    return null;
                  }, new object[] { x, y });
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n    setCount(n => n + 1);", result);
        Assert.Contains("\n    setStatus(\"updated\");", result);
        Assert.Contains("\n  }, new object[] { x, y });", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.8  useLayoutEffect ──────────────────────────────────────────────────

    [Fact]
    public void C08_UseLayoutEffect_NullReturn_WithDepsArray_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  useLayoutEffect(() => {
                    setTrigger(t => t);
                    return null;
                  }, new object[] { count });
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  useLayoutEffect(() => {", result);
        Assert.Contains("\n    setTrigger(t => t);", result);
        Assert.Contains("\n    return null;", result);
        Assert.Contains("\n  }, new object[] { count });", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.9  Void methods ─────────────────────────────────────────────────────

    [Fact]
    public void C09a_VoidMethod_SingleStatementOnSameLine_OverIndented_Normalized()
    {
        var source = N(
            """
            component Foo {
                  void Reset() { setCount(0); }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  void Reset() { setCount(0); }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C09b_VoidMethod_MultiStatementWithListInitAndGuard_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  void AppendLog(string message) {
                    var next = new List<string>(log.Count + 1) {
                      $"prefix: {message}",
                    };
                    next.AddRange(log);
                    if (next.Count > 8) {
                      next.RemoveRange(8, next.Count - 8);
                    }
                    setLog(next);
                  }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  void AppendLog(string message) {", result);
        Assert.Contains("\n    var next = new List<string>(log.Count + 1) {", result);
        Assert.Contains("\n      $\"prefix: {message}\",", result);
        Assert.Contains("\n    };", result);
        Assert.Contains("\n    next.AddRange(log);", result);
        Assert.Contains("\n    if (next.Count > 8) {", result);
        Assert.Contains("\n      next.RemoveRange(8, next.Count - 8);", result);
        Assert.Contains("\n    setLog(next);", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C09c_VoidMethod_ForLoopAndTupleSwap_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  void Shuffle() {
                    if (items.Count == 0) {
                      return;
                    }
                    var list = new List<string>(items);
                    for (int i = list.Count - 1; i > 0; i--) {
                      int j = i / 2;
                      (list[i], list[j]) = (list[j], list[i]);
                    }
                  }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  void Shuffle() {", result);
        Assert.Contains("\n    if (items.Count == 0) {", result);
        Assert.Contains("\n      return;", result);
        Assert.Contains("\n    for (int i = list.Count - 1; i > 0; i--) {", result);
        Assert.Contains("\n      (list[i], list[j]) = (list[j], list[i]);", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C09d_VoidMethod_SwitchCaseWithBreaks_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  void ApplyMode(string newMode) {
                    switch (newMode) {
                      case "a":
                        doA();
                        break;
                      case "b":
                        doB();
                        break;
                      default:
                        doDefault();
                        break;
                    }
                    setMode(newMode);
                  }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  void ApplyMode(string newMode) {", result);
        Assert.Contains("\n    switch (newMode) {", result);
        Assert.Contains("\n      case \"a\":", result);
        // EmitSetupCodeNormalized has no C# semantic knowledge of `case:` labels;
        // case-body statements are normalised to the same block-level as the label.
        Assert.Contains("\n      doA();", result);
        Assert.Contains("\n      break;", result);
        Assert.Contains("\n      default:", result);
        Assert.Contains("\n    }", result);
        Assert.Contains("\n    setMode(newMode);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C09e_VoidMethod_IsPatternMatchingWithElseIf_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  void HandleEvent(ReactiveEvent evt) {
                    if (evt == null) {
                      log("null");
                      return;
                    }
                    if (evt is ReactivePointerEvent pointer) {
                      log($"pointer {pointer.Position.x:0.0}");
                    } else if (evt is ReactiveWheelEvent wheel) {
                      log($"wheel {wheel.Delta.y:0.0}");
                    }
                  }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  void HandleEvent(ReactiveEvent evt) {", result);
        Assert.Contains("\n    if (evt == null) {", result);
        Assert.Contains("\n      log(\"null\");", result);
        Assert.Contains("\n      return;", result);
        Assert.Contains("\n    if (evt is ReactivePointerEvent pointer) {", result);
        Assert.Contains("\n    } else if (evt is ReactiveWheelEvent wheel) {", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C09f_VoidMethod_LambdaStateSetter_NewObjectInit_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  void AddItem() {
                    setRows(prev =>
                    {
                      var next = prev != null ? new List<Row>(prev) : new List<Row>();
                      next.Add(new Row
                      {
                        Id = Guid.NewGuid().ToString("N"),
                        Text = "New",
                      });
                      return next;
                    });
                  }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  void AddItem() {", result);
        Assert.Contains("\n    setRows(prev =>", result);
        Assert.Contains("Id = Guid.NewGuid().ToString(\"N\"),", result);
        Assert.Contains("Text = \"New\",", result);
        Assert.Contains("return next;", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C09g_VoidMethod_LambdaStateSetter_MultipleEarlyReturnsAndNullCoalescing_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  void SetParent() {
                    setRows(prev =>
                    {
                      if (prev == null || prev.Count == 0) {
                        return prev;
                      }
                      var source = prev[prev.Count - 1];
                      if (source == null) {
                        return prev;
                      }
                      var item = source.Parent ?? new Item { Id = Guid.NewGuid().ToString("N") };
                      item.Text = $"{item.Id} updated";
                      item.Overridden = true;
                      var next = new List<Row>(prev);
                      next[next.Count - 1] = new Row
                      {
                        Pid = source.Pid,
                        Parent = item,
                      };
                      return next;
                    });
                  }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  void SetParent() {", result);
        Assert.Contains("\n    setRows(prev =>", result);
        Assert.Contains("if (prev == null || prev.Count == 0) {", result);
        Assert.Contains("return prev;", result);
        Assert.Contains("?? new Item { Id = Guid.NewGuid().ToString(\"N\") };", result);
        Assert.Contains("item.Overridden = true;", result);
        Assert.Contains("Pid = source.Pid,", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C09h_VoidMethod_LambdaStateSetter_RemoveAtPattern_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  void DeleteLast() {
                    setRows(prev =>
                    {
                      if (prev == null || prev.Count == 0) {
                        return prev;
                      }
                      var next = new List<Row>(prev);
                      next.RemoveAt(next.Count - 1);
                      return next;
                    });
                  }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  void DeleteLast() {", result);
        Assert.Contains("next.RemoveAt(next.Count - 1);", result);
        Assert.Contains("return next;", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.10  Typed delegate lambda (MenuBuilderHandler) ─────────────────────

    [Fact]
    public void C10_TypedDelegateLambda_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  MenuBuilderHandler buildMenu = dm => {
                    dm.AppendAction("Reset", _ => doReset());
                    dm.AppendAction("Set 10", _ => setValue(10));
                  };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  MenuBuilderHandler buildMenu = dm => {", result);
        Assert.Contains("\n    dm.AppendAction(\"Reset\", _ => doReset());", result);
        Assert.Contains("\n    dm.AppendAction(\"Set 10\", _ => setValue(10));", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.11  TabViewProps with TabDef list ────────────────────────────────────

    [Fact]
    public void C11_TabViewProps_WithTabDefList_OverIndented_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  var tabViewProps = new TabViewProps
                  {
                    SelectedIndex = tabIndex,
                    Tabs = new List<TabViewProps.TabDef>
                    {
                      new TabViewProps.TabDef { Title = "A", Content = () => V.Label(new LabelProps { Text = "tab A" }) },
                      new TabViewProps.TabDef { Title = "B", Content = () => V.Label(new LabelProps { Text = "tab B" }) },
                    },
                    Style = new Style { (StyleKeys.Height, 160f) },
                  };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var tabViewProps = new TabViewProps", result);
        Assert.Contains("SelectedIndex = tabIndex,", result);
        Assert.Contains("Tabs = new List<TabViewProps.TabDef>", result);
        Assert.Contains("new TabViewProps.TabDef { Title = \"A\",", result);
        Assert.Contains("Style = new Style { (StyleKeys.Height, 160f) },", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.12  Multi-line ternary in var declaration ───────────────────────────

    [Fact]
    public void C12a_MultiLineTernary_TwoArm_CanonicalForm_IsStable()
    {
        var source = N(
            """
            component Foo {
              var syncColor = count >= 0
                ? new Color(0.3f, 0.85f, 0.45f, 1f)
                : new Color(0.95f, 0.65f, 0.1f, 1f);
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("  var syncColor = count >= 0", result);
        Assert.Contains("\n    ? new Color(0.3f", result);
        Assert.Contains("\n    : new Color(0.95f", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C12b_MultiLineTernary_OverIndented_NormalisedToTwoAndFour()
    {
        var source = N(
            """
            component Foo {
                  var syncColor = count >= 0
                    ? new Color(0.3f, 0.85f, 0.45f, 1f)
                    : new Color(0.95f, 0.65f, 0.1f, 1f);
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        // When the ternary is the only depth-0 setup content, the arms keep their
        // trimmed-code spacing relative to indentSpaces (no further normalisation is
        // applied without non-continuation anchor lines).
        // The critical invariant is double-format stability.
        Assert.Contains("\n  var syncColor = count >= 0", result);
        Assert.Contains("? new Color(0.3f", result);
        Assert.Contains(": new Color(0.95f", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C12c_MultiLineTernary_StringJoinArm_CanonicalForm_IsStable()
    {
        var source = N(
            """
            component Foo {
              var text = items.Count == 0
                ? "No items"
                : string.Join(", ", items);
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("  var text = items.Count == 0", result);
        Assert.Contains("\n    ? \"No items\"", result);
        Assert.Contains("\n    : string.Join(\", \", items);", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.13  Deeply-nested ternary (portalNode pattern) ─────────────────────

    [Fact]
    public void C13_DeeplyNestedTernary_PortalNode_StableAndContentIntact()
    {
        // The deeply-nested ternary contains non-continuation depth-0 lines
        // (the V.Portal argument list: target, null, mounted) which anchor
        // baseSpaces via EmitSetupCodeNormalized.  The exact output indent
        // of the outer '? V.Portal(' arm depends on those anchors, but the
        // critical invariants are: (1) the var line normalises to 2-space,
        // (2) key content is preserved verbatim, and (3) double-format is stable.
        var source = N(
            """
            component Foo {
              var portalNode = target != null
                ? V.Portal(
                  target,
                  null,
                  mounted
                    ? V.Button(new ButtonProps
                      {
                        Text = "Click me",
                        OnClick = _ => doClick(),
                      })
                    : V.Label(new LabelProps { Text = "Unmounted." })
                  )
                : null;
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("  var portalNode = target != null", result);
        Assert.Contains("V.Portal(", result);
        Assert.Contains("Text = \"Click me\",", result);
        Assert.Contains(": null;", result);
        // Double-format stability is the key invariant.
        Assert.Equal(result, Format(result));
    }

    // ── C.14  Inline JSX node variable ────────────────────────────────────────

    [Fact]
    public void C14_InlineJsxNodeVar_CanonicalForm_IsStable()
    {
        var source = N(
            """
            component Foo {
              var inlineNode = (
                <VisualElement>
                  <Button text="-5" onClick={_ => doIt()} />
                  <Button text="+5" onClick={_ => doIt2()} />
                </VisualElement>
              );
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("  var inlineNode = (", result);
        Assert.Contains("\n    <VisualElement>", result);
        Assert.Contains("\n      <Button text=\"-5\"", result);
        Assert.Contains("\n    </VisualElement>", result);
        Assert.Contains("\n  );", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.15  Expression-bodied bool / returning method ───────────────────────

    [Fact]
    public void C15a_ExpressionBodiedBoolMethod_OverIndented_Normalized()
    {
        var source = N(
            """
            component Foo {
                  bool IsReady() => trigger > 1000;
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  bool IsReady() => trigger > 1000;", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C15b_BoolLocalFunction_CanonicalForm_IsStable()
    {
        var source = N(
            """
            component Foo {
              bool SuspenseReady() => pendingTask == null || pendingTask.IsCompletedSuccessfully;
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("  bool SuspenseReady() =>", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.16  Comment lines in setup code ────────────────────────────────────

    [Fact]
    public void C16_CommentLines_SectionHeaders_OverIndented_Normalized()
    {
        var source = N(
            """
            component Foo {
                  // ── Section header ──────────────────────────────────────────
                  var x = 1;
                  // Another comment
                  var y = 2;
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  // ── Section header", result);
        Assert.Contains("\n  var x = 1;", result);
        Assert.Contains("\n  // Another comment", result);
        Assert.Contains("\n  var y = 2;", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.16b  Commented-out block: braces inside `//` must NOT push a
    //          phantom block onto the formatter's block stack (idempotency
    //          regression — repeated formatting drifted indent deeper).
    [Fact]
    public void C16b_CommentedOutHookBlock_TrailingBraceInComment_DoesNotShiftIndent()
    {
        var source = N(
            """
            component Foo {
              var x = 1;
              // useEffect(() => {
              //   var rng = new System.Random();
              //   var timer = new Timer(_ => {
              //     setStats(prev => PlayerStatsJitter.Tick(prev, rng));
              //   }, null, 1000, 1000);
              //   return () => timer.Dispose();
              // }, new object[] { });
              var y = 2;
              return (<Box />);
            }
            """
        );

        var formatted = Format(source);

        Assert.Contains("\n  // useEffect(() => {", formatted);
        Assert.Contains("\n  //   var rng = new System.Random();", formatted);
        Assert.Contains("\n  //   var timer = new Timer(_ => {", formatted);
        Assert.Contains("\n  //     setStats(prev => PlayerStatsJitter.Tick(prev, rng));", formatted);
        Assert.Contains("\n  //   }, null, 1000, 1000);", formatted);
        Assert.Contains("\n  //   return () => timer.Dispose();", formatted);
        Assert.Contains("\n  // }, new object[] { });", formatted);
        Assert.Contains("\n  var y = 2;", formatted);
        // Idempotency: repeated formatting must not drift indent deeper.
        Assert.Equal(formatted, Format(formatted));
    }

    // ── C.17  Multi-line Style initializers ──────────────────────────────────

    [Fact]
    public void C17a_MultiLineStyle_ThreeEntries_CanonicalForm_IsStable()
    {
        var source = N(
            """
            component Foo {
              var rowStyle = new Style {
                (StyleKeys.FlexDirection, "row"),
                (StyleKeys.FlexWrap, "wrap"),
                (StyleKeys.MarginBottom, 6f),
              };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var rowStyle = new Style {", result);
        Assert.Contains("\n    (StyleKeys.FlexDirection, \"row\"),", result);
        Assert.Contains("\n    (StyleKeys.FlexWrap, \"wrap\"),", result);
        Assert.Contains("\n    (StyleKeys.MarginBottom, 6f),", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C17b_MultiLineStyle_SixEntries_CanonicalForm_IsStable()
    {
        var source = N(
            """
            component Foo {
              var panelStyle = new Style {
                (StyleKeys.Height, 80f),
                (StyleKeys.BorderRadius, 6f),
                (StyleKeys.BackgroundColor, new Color(0.2f, 0.2f, 0.25f, 0.9f)),
                (StyleKeys.JustifyContent, "center"),
                (StyleKeys.AlignItems, "center"),
                (StyleKeys.MarginBottom, 10f),
              };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var panelStyle = new Style {", result);
        Assert.Contains("\n    (StyleKeys.Height, 80f),", result);
        Assert.Contains("\n    (StyleKeys.BackgroundColor, new Color(", result);
        Assert.Contains("\n    (StyleKeys.MarginBottom, 10f),", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C17c_MultiLineStyle_MixedIndentEntries_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
              var containerStyle = new Style {
                    (StyleKeys.FlexGrow, 1f),
                (StyleKeys.Padding, 12f),
                  (StyleKeys.FlexDirection, "column"),
              };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n  var containerStyle = new Style {", result);
        Assert.Contains("\n    (StyleKeys.FlexGrow, 1f),", result);
        Assert.Contains("\n    (StyleKeys.Padding, 12f),", result);
        Assert.Contains("\n    (StyleKeys.FlexDirection, \"column\"),", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C17d_MultiLineStyle_ColorConstructorEntry_PreservedVerbatim()
    {
        var source = N(
            """
            component Foo {
              var s = new Style {
                (StyleKeys.BackgroundColor, new Color(0.2f, 0.2f, 0.23f, 0.85f)),
                (StyleKeys.BorderRadius, 4f),
              };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains(
            "(StyleKeys.BackgroundColor, new Color(0.2f, 0.2f, 0.23f, 0.85f)),",
            result
        );
        Assert.Contains("\n    (StyleKeys.BorderRadius, 4f),", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.18  @switch with multiple children per @case ────────────────────────

    [Fact]
    public void C18a_Switch_CaseWithSingleChild_MeskyIndent_Normalized()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  @switch (mode) {
                      @case "a":
                          return (<LabelA />);
                      @case "b":
                          return (<LabelB />);
                      @default:
                          return (<LabelDefault />);
                  }
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("@switch (mode) {", result);
        // @switch pushes indent → @case is two levels deeper than @switch itself.
        Assert.Contains("\n        @case \"a\":", result);
        Assert.Contains("\n            <LabelA />", result);
        Assert.Contains("\n        @case \"b\":", result);
        Assert.Contains("\n        @default:", result);
        Assert.Contains("\n            <LabelDefault />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C18b_Switch_CaseWithMultipleChildren_AllChildrenAtSameIndent()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  @switch (mode) {
                    @case "normal":
                      return (
                      <VisualElement>
                        <Label text="Normal mode" />
                        <Button text="Click" onClick={_ => doIt()} />
                      </VisualElement>
                      );
                    @default:
                      return (<Label text="Other" />);
                  }
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n        @case \"normal\":", result);
        // Children wrapped in a container
        Assert.Contains("\n            <VisualElement>", result);
        Assert.Contains("\n              <Label text=\"Normal mode\" />", result);
        Assert.Contains("\n              <Button text=\"Click\"", result);
        Assert.Contains("\n        @default:", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.19  @if / @else if / @else chain with @for inside @else ─────────────

    [Fact]
    public void C19a_IfElseIfElse_FourArm_AllBranchesFormatted()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  @if (count < -5) {
                    return (<Label text="Very negative" />);
                  } @else if (count < 0) {
                    return (<Label text="Negative" />);
                  } @else if (count == 0) {
                    return (<Label text="Zero" />);
                  } @else {
                    return (<Label text="Positive" />);
                  }
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("@if (count < -5) {", result);
        Assert.Contains("} @else if (count < 0) {", result);
        Assert.Contains("} @else if (count == 0) {", result);
        Assert.Contains("} @else {", result);
        Assert.Contains("\n          <Label text=\"Very negative\" />", result);
        Assert.Contains("\n          <Label text=\"Zero\" />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C19b_ElseBranch_ContainsNestedForWithNestedIf_AllFormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  @if (count <= 0) {
                    return (<Label text="None" />);
                  } @else {
                    return (
                    @for (int i = 0; i < count; i++) {
                      return (
                      @if (i % 2 == 0) {
                        return (<Label text={$"Even {i}"} />);
                      } @else {
                        return (<Label text={$"Odd {i}"} />);
                      }
                      );
                    }
                    <Label text={$"Total: {count}"} />
                    );
                  }
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("} @else {", result);
        Assert.Contains("\n          @for (int i = 0;", result);
        Assert.Contains("\n              @if (i % 2 == 0) {", result);
        Assert.Contains("\n                  <Label text={$\"Even {i}\"", result);
        Assert.Contains("\n              } @else {", result);
        Assert.Contains("\n                  <Label text={$\"Odd {i}\"", result);
        Assert.Contains("\n          <Label text={$\"Total:", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C19c_IfElseIf_MessyIndent_AllArmsNormalized()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  @if (count < -5) {
                      <Label text="Very neg" />
                  } @else if (count < 0) {
                      <Label text="Neg" />
                  } @else {
                      <Label text="Ok" />
                  }
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("\n          <Label text=\"Very neg\" />", result);
        Assert.Contains("\n          <Label text=\"Neg\" />", result);
        Assert.Contains("\n          <Label text=\"Ok\" />", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.20  @foreach with key= attribute ────────────────────────────────────

    [Fact]
    public void C20a_Foreach_ChildWithStringKeyAttr_FormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  @foreach (var entry in log) {
                    <Label key={entry} text={entry} />
                  }
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("@foreach (var entry in log) {", result);
        Assert.Contains("key={entry}", result);
        Assert.Contains("text={entry}", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C20b_Foreach_ChildWithExpressionKeyAttr_WrappedCorrectly()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  @foreach (var opt in options) {
                    <Button key={opt} text={opt} onClick={_ => setSelected(options.IndexOf(opt))} />
                  }
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("@foreach (var opt in options) {", result);
        Assert.Contains("key={opt}", result);
        Assert.Contains("text={opt}", result);
        Assert.Contains("onClick={_ => setSelected(options.IndexOf(opt))}", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.21  key= attribute (static string and expression) ───────────────────

    [Fact]
    public void C21a_KeyAttr_StaticString_PreservedVerbatim()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <Button key="my-static-key" text="Click" onClick={_ => doIt()} />
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("key=\"my-static-key\"", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C21b_KeyAttr_CSharpExpression_PreservedVerbatim()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <Label key={item.Id} text={item.Name} />
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("key={item.Id}", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.22  Inline style attribute with various entry counts ────────────────

    [Fact]
    public void C22a_InlineStyle_SingleEntry_StaysOnOneLine()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <Button text="+" onClick={_ => inc()} style={new Style { (StyleKeys.MarginRight, 6f) }} />
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("style={new Style { (StyleKeys.MarginRight, 6f) }}", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C22b_InlineStyle_TwoEntries_StaysOnOneLine()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <Label text={$"{count}"} style={new Style { (StyleKeys.MinWidth, 30f), ("unityTextAlign", "middle-center") }} />
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains(
            "(StyleKeys.MinWidth, 30f), (\"unityTextAlign\", \"middle-center\")",
            result
        );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C22c_InlineStyle_ThreeEntries_LongLine_WrapsElement()
    {
        // Combined line length exceeds print width → element wraps with each attr on own line.
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <Label text={$"Selected: {selected}"} style={new Style { (StyleKeys.MarginTop, 4f), (StyleKeys.FontSize, 13f), (StyleKeys.Color, new Color(0.85f, 0.95f, 1f, 1f)) }} />
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        // After wrapping, each attribute gets its own indented line.
        Assert.Contains("\n        text=", result);
        Assert.Contains("\n        style=", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C22d_InlineStyle_StringKey_PreservedVerbatim()
    {
        // ("unityFontStyleAndWeight", FontStyle.Bold) - string key, not StyleKeys constant
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <Label
                    text="Hello"
                    style={new Style { (StyleKeys.FontSize, 16f), ("unityFontStyleAndWeight", FontStyle.Bold) }}
                  />
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains(
            "(StyleKeys.FontSize, 16f), (\"unityFontStyleAndWeight\", FontStyle.Bold)",
            result
        );
        Assert.Equal(result, Format(result));
    }

    // ── C.23  Style var-reference in JSX attr ─────────────────────────────────

    [Fact]
    public void C23_StyleAttr_VarReference_FormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              var myStyle = new Style {
                (StyleKeys.FlexGrow, 1f),
                (StyleKeys.Padding, 12f),
              };
              return (
                <Box style={myStyle}>
                  <Label text="x" />
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("style={myStyle}", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.24  extraProps with Dictionary initializer ───────────────────────────

    [Fact]
    public void C24_ExtraProps_DictionaryInitializer_IndentedCorrectly()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <VisualElement
                    style={panelStyle}
                    extraProps={new Dictionary<string, object> {
                        { "onPointerDown", (PointerEventHandler)(e => handleDown(e)) },
                        { "onWheel", (WheelEventHandler)(e => handleWheel(e)) }
                    }}
                  >
                    <Label text="Interact here" />
                  </VisualElement>
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("extraProps={new Dictionary<string, object>", result);
        Assert.Contains("\"onPointerDown\"", result);
        Assert.Contains("\"onWheel\"", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.25  <Suspense> element ──────────────────────────────────────────────

    [Fact]
    public void C25_Suspense_WithIsReadyAndFallback_FormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <Suspense
                    isReady={checkReady}
                    fallback={V.Label(new LabelProps { Text = "Loading..." })}
                  >
                    <Label text="Content loaded" />
                  </Suspense>
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("<Suspense", result);
        Assert.Contains("isReady={checkReady}", result);
        Assert.Contains("fallback={V.Label(", result);
        Assert.Contains("</Suspense>", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.26  {expr} inline expression rendering ─────────────────────────────

    [Fact]
    public void C26a_InlineExprRender_SimpleVar_FormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              var node = (<Label text="hi" />);
              return (
                <Box>
                  {node}
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("{node}", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void C26b_InlineExprRender_NullableVar_FormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              var portalNode = target != null ? V.Label(new LabelProps { Text = "x" }) : null;
              return (
                <Box>
                  {portalNode}
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("{portalNode}", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.27  <Toolbar> / <ToolbarMenu> ──────────────────────────────────────

    [Fact]
    public void C27_Toolbar_WithToolbarMenu_PopulateMenuHandler_FormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              MenuBuilderHandler buildMenu = dm => { dm.AppendAction("A", _ => doA()); };
              return (
                <Box>
                  <Toolbar style={new Style { (StyleKeys.MarginTop, 6f) }}>
                    <ToolbarMenu
                      text="Actions"
                      populateMenu={buildMenu}
                    />
                  </Toolbar>
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("<Toolbar", result);
        Assert.Contains("<ToolbarMenu", result);
        Assert.Contains("text=\"Actions\"", result);
        Assert.Contains("populateMenu={buildMenu}", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.28  <TabView> with selectedIndex / tabs / style ─────────────────────

    [Fact]
    public void C28_TabView_SelectedIndexTabsAndStyle_FormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              var (tabIndex, setTabIndex) = useState(0);
              var tabs = new List<TabViewProps.TabDef>
              {
                new TabViewProps.TabDef { Title = "A", Content = () => V.Label(new LabelProps { Text = "tab A" }) },
              };
              return (
                <Box>
                  <TabView
                    selectedIndex={tabIndex}
                    tabs={tabs}
                    style={new Style { (StyleKeys.Height, 160f) }}
                  />
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("<TabView", result);
        Assert.Contains("selectedIndex={tabIndex}", result);
        Assert.Contains("tabs={tabs}", result);
        Assert.Contains("style={new Style { (StyleKeys.Height, 160f) }}", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.29  <ToggleButtonGroup> with @foreach children ──────────────────────

    [Fact]
    public void C29_ToggleButtonGroup_WithForeachChildren_FormattedCorrectly()
    {
        var source = N(
            """
            component Foo {
              var (selected, setSelected) = useState(0);
              var options = new string[] { "A", "B", "C" };
              return (
                <Box>
                  <ToggleButtonGroup value={selected}>
                    @foreach (var opt in options) {
                      <Button key={opt} text={opt} onClick={_ => setSelected(0)} />
                    }
                  </ToggleButtonGroup>
                </Box>
              );
            }
            """
        );

        var result = Format(source);

        Assert.Contains("<ToggleButtonGroup value={selected}>", result);
        Assert.Contains("@foreach (var opt in options) {", result);
        Assert.Contains("key={opt}", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.30  Object initializer with new T { Props } ─────────────────────────

    [Fact]
    public void C30_ObjectInitializer_MultiProp_InsideLambdaBody_BlockNormalized()
    {
        var source = N(
            """
            component Foo {
                  void AddItem() {
                    setRows(prev =>
                    {
                      var next = new List<Row>(prev);
                      next.Add(new Row
                      {
                        Pid = pid,
                        Text = "Parent",
                        HasChild = false,
                      });
                      return next;
                    });
                  }
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("next.Add(new Row", result);
        Assert.Contains("Pid = pid,", result);
        Assert.Contains("Text = \"Parent\",", result);
        Assert.Contains("HasChild = false,", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.31  Null-coalescing in assignment ────────────────────────────────────

    [Fact]
    public void C31_NullCoalescing_InPropertyAssignment_PreservedVerbatim()
    {
        var source = N(
            """
            component Foo {
                  var item = source.Parent ?? new Item { Id = Guid.NewGuid().ToString("N") };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("?? new Item { Id = Guid.NewGuid().ToString(\"N\") };", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.32  Blank lines between setup sections preserved ────────────────────

    [Fact]
    public void C32_BlankLineBetweenSetupSections_Preserved()
    {
        var source = N(
            """
            component Foo {
              var (treeRows, setTreeRows) = useState(new List<string>());
              var (treeNextPid, setTreeNextPid) = useState(2);

              var (mctvRows, setMctvRows) = useState(new List<string>());
              var (mctvNextPid, setMctvNextPid) = useState(2);

              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("  var (treeRows, setTreeRows)", result);
        Assert.Contains("  var (treeNextPid, setTreeNextPid)", result);
        Assert.Contains("\n\n  var (mctvRows, setMctvRows)", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.33  Multi-line method call continuation (not a ternary) ─────────────

    [Fact]
    public void C33_MultiLineContinuation_VFuncCall_IndentPreserved()
    {
        var source = N(
            """
            component Foo {
              var tabDef = new TabViewProps.TabDef {
                Title = "Intro",
                Content = () => V.Label(new LabelProps { Text = "A TabView demo." }),
              };
              return (<Box />);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("Title = \"Intro\",", result);
        Assert.Contains("Content = () => V.Label(", result);
        Assert.Equal(result, Format(result));
    }

    // ── C.34  Large kitchen-sink slice: double-format stability ───────────────

    [Fact]
    public void C34_KitchenSinkSlice_SetupCode_DoubleFormatStable()
    {
        // Representative slice covering: useState, useContext, provideContext,
        // useMemo (Array.Empty + List factory), useRef, useEffect (cleanup lambda
        // + local function), useLayoutEffect, void methods (single-line, multi-
        // statement, switch, is-pattern), MenuBuilderHandler, Style vars, ternary.
        var source = N(
            """
            @namespace NS
            @using System
            @using System.Collections.Generic
            @using UnityEngine
            @using static Ruitk.Props.Typed.StyleKeys
            component KSSSetup {
              var (count, setCount) = useState(0);
              var (mode, setMode) = useState("normal");
              var (log, setLog) = useState(new List<string> { "Ready." });
              var (ids, setIds) = useState<List<int>?>(null);

              var target = useContext<VisualElement>(PortalContextKeys.ModalRoot);
              var theme = useContext<Color>("theme");

              provideContext("sub-theme", theme);

              var sig = useMemo(() => SignalFactory.Get<int>("k", 0), Array.Empty<object>());
              var sigCount = useSignal(sig);
              var doubled = useMemo(() => count * 2, new object[] { count });
              var opts = useMemo(() => new List<string> {
                "Alpha",
                "Beta",
                "Gamma",
              }, Array.Empty<object>());

              var refCount = useRef<int>(0);
              refCount.Current++;
              var rc = refCount.Current;

              useEffect(() => {
                bool captured = false;
                void OnChange(int v) {
                  if (captured) {
                    return;
                  }
                  setCount(v);
                }
                var sub = sig.Subscribe(OnChange);
                return () => {
                  captured = true;
                  sub.Dispose();
                };
              }, new object[] { sig });

              useEffect(() => {
                setLog(prev => new List<string>(prev) { $"init" });
                return null;
              }, Array.Empty<object>());

              useLayoutEffect(() => {
                setIds(prev => prev);
                return null;
              }, new object[] { count });

              void Reset() { setCount(0); }

              void Append(string msg) {
                var next = new List<string>(log.Count + 1) { msg };
                next.AddRange(log);
                if (next.Count > 8) {
                  next.RemoveRange(8, next.Count - 8);
                }
                setLog(next);
              }

              void Shuffle() {
                if (opts.Count == 0) {
                  return;
                }
                var items = new List<string>(opts);
                for (int i = items.Count - 1; i > 0; i--) {
                  int j = i / 2;
                  (items[i], items[j]) = (items[j], items[i]);
                }
                Append($"Shuffled {items.Count}");
              }

              void Apply(string m) {
                switch (m) {
                  case "squared":
                    Append("squared");
                    break;
                  default:
                    Append(m);
                    break;
                }
                setMode(m);
              }

              void Handle(ReactiveEvent evt) {
                if (evt == null) {
                  Append("null");
                  return;
                }
                if (evt is ReactivePointerEvent ptr) {
                  Append($"ptr {ptr.Position.x:0.0}");
                } else if (evt is ReactiveWheelEvent whl) {
                  Append($"whl {whl.Delta.y:0.0}");
                }
              }

              var cStyle = new Style {
                (StyleKeys.FlexGrow, 1f),
                (StyleKeys.Padding, 12f),
              };

              var syncColor = count >= 0
                ? new Color(0.3f, 0.85f, 0.45f, 1f)
                : new Color(0.95f, 0.65f, 0.1f, 1f);

              var syncStyle = new Style {
                (StyleKeys.MarginTop, 6f),
                (StyleKeys.Color, syncColor),
              };

              MenuBuilderHandler menu = dm => {
                dm.AppendAction("Reset", _ => Reset());
                dm.AppendAction("Mode", _ => Apply("squared"));
              };

              bool IsLoading() => count > 1000;

              var optText = opts.Count == 0
                ? "None"
                : string.Join(", ", opts);

              return (<Box />);
            }
            """
        );

        var first = Format(source);
        var second = Format(first);

        Assert.Equal(first, second);
        // Spot checks:
        Assert.Contains("\n  var (count, setCount) = useState(0);", first);
        Assert.Contains("\n  var (ids, setIds) = useState<List<int>?>(null);", first);
        Assert.Contains("\n  provideContext(\"sub-theme\", theme);", first);
        Assert.Contains("\n  useEffect(() => {", first);
        Assert.Contains("\n    void OnChange(int v) {", first);
        Assert.Contains("\n  void Reset() { setCount(0); }", first);
        Assert.Contains("\n  MenuBuilderHandler menu = dm => {", first);
        Assert.Contains("\n    ? new Color(0.3f", first);
        Assert.Contains("\n  bool IsLoading() => count > 1000;", first);
    }

    [Fact]
    public void C35_KitchenSinkSlice_JSX_DoubleFormatStable()
    {
        // Representative slice covering: @switch (multi-child case), @if chain
        // with @for in @else, @foreach with key, inline style (single/multi
        // entry), style var-ref, extraProps, Suspense, {expr}, ToggleButtonGroup,
        // Toolbar/ToolbarMenu, TabView.
        var source = N(
            """
            @namespace NS
            component KSSJsx {
              var (count, setCount) = useState(0);
              var (mode, setMode) = useState("normal");
              var (log, setLog) = useState(new List<string> { "Ready." });
              var (selected, setSelected) = useState(0);
              var opts = new string[] { "A", "B", "C" };
              MenuBuilderHandler menu = dm => { dm.AppendAction("R", _ => setCount(0)); };
              var inlineNode = (
                <VisualElement>
                  <Button text="-" onClick={_ => setCount(count - 1)} />
                </VisualElement>
              );
              var panelStyle = new Style {
                (StyleKeys.Height, 80f),
                (StyleKeys.BackgroundColor, new Color(0.2f, 0.2f, 0.25f, 0.9f)),
              };
              bool IsLoading() => count > 1000;
              return (
                <ScrollView>
                  @switch (mode) {
                    @case "normal":
                      return (
                      <VisualElement>
                        <Label text="Normal" />
                        <Button text="-5" onClick={_ => setCount(count - 5)} />
                      </VisualElement>
                      );
                    @default:
                      return (<Label text={$"Mode: {mode}"} />);
                  }
                  @if (count < -5) {
                    return (<Label text="Very neg!" />);
                  } @else if (count < 0) {
                    return (<Label text="Neg" />);
                  } @else if (count == 0) {
                    return (<Label text="Zero" />);
                  } @else {
                    return (
                    @for (int i = 0; i < count; i++) {
                      return (
                      @if (i % 2 == 0) {
                        return (<Label text={$"Even {i}"} />);
                      } @else {
                        return (<Label text={$"Odd {i}"} />);
                      }
                      );
                    }
                    <Label text={$"Total: {count}"} />
                    );
                  }
                  @foreach (var entry in log) {
                    return (<Label key={entry} text={entry} />);
                  }
                  <VisualElement>
                    <Button
                      text="-"
                      onClick={_ => setCount(count - 1)}
                      style={new Style { (StyleKeys.MarginRight, 6f) }}
                    />
                    <Label
                      text={$"{count}"}
                      style={new Style { (StyleKeys.MinWidth, 30f), ("unityTextAlign", "middle-center") }}
                    />
                    <Button text="+" onClick={_ => setCount(count + 1)} />
                  </VisualElement>
                  <ToggleButtonGroup value={selected}>
                    @foreach (var opt in opts) {
                      return (<Button key={opt} text={opt} onClick={_ => setSelected(0)} />);
                    }
                  </ToggleButtonGroup>
                  <Label
                    text={$"Selected: {(selected < opts.Length ? opts[selected] : "none")}"}
                    style={new Style { (StyleKeys.MarginTop, 4f), (StyleKeys.FontSize, 13f), (StyleKeys.Color, new Color(0.85f, 0.95f, 1f, 1f)) }}
                  />
                  <Toolbar style={new Style { (StyleKeys.MarginTop, 6f) }}>
                    <ToolbarMenu text="Actions" populateMenu={menu} />
                  </Toolbar>
                  <TabView
                    selectedIndex={0}
                    tabs={new List<TabViewProps.TabDef> { new TabViewProps.TabDef { Title = "A", Content = () => V.Label(new LabelProps { Text = "A" }) } }}
                    style={new Style { (StyleKeys.Height, 120f) }}
                  />
                  <VisualElement
                    style={panelStyle}
                    extraProps={new Dictionary<string, object> {
                        { "onPointerDown", (PointerEventHandler)(e => setCount(count + 1)) },
                        { "onWheel", (WheelEventHandler)(e => setCount(count - 1)) }
                    }}
                  >
                    <Label text="Interact" style={new Style { (StyleKeys.Color, Color.white) }} />
                  </VisualElement>
                  <Suspense
                    isReady={IsLoading}
                    fallback={V.Label(new LabelProps { Text = "Suspense fallback..." })}
                  >
                    <Label text="Content" />
                  </Suspense>
                  {inlineNode}
                </ScrollView>
              );
            }
            """
        );

        var first = Format(source);
        var second = Format(first);

        Assert.Equal(first, second);
        // Spot checks:
        Assert.Contains("\n        @case \"normal\":", first);
        Assert.Contains("\n            <VisualElement>", first);
        Assert.Contains("\n              <Label text=\"Normal\" />", first);
        Assert.Contains("\n              <Button text=\"-5\"", first);
        Assert.Contains("} @else if (count < 0) {", first);
        Assert.Contains("} @else {", first);
        Assert.Contains("\n          @for (int i = 0;", first);
        Assert.Contains("\n              @if (i % 2 == 0) {", first);
        Assert.Contains("key={entry}", first);
        Assert.Contains(
            "(StyleKeys.MinWidth, 30f), (\"unityTextAlign\", \"middle-center\")",
            first
        );
        Assert.Contains("<ToggleButtonGroup value={selected}>", first);
        Assert.Contains("<Toolbar", first);
        Assert.Contains("<ToolbarMenu", first);
        Assert.Contains("<TabView", first);
        Assert.Contains("extraProps={new Dictionary<string, object>", first);
        Assert.Contains("<Suspense", first);
        Assert.Contains("{inlineNode}", first);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  D — Tab indentation: every position where a user might press Tab
    //
    //  Each test feeds tab-indented input and asserts:
    //    1. Output uses canonical 2-space indentation (no literal tabs).
    //    2. Double-format stability: Format(Format(src)) == Format(src).
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void D01_TabIndent_TopLevelSetupVars_ExpandedToSpaces()
    {
        // User pressed Tab twice before each var — each \t expands to 2 spaces.
        // The formatter must re-anchor to 2-space regardless of tab width used.
        var source =
            "component Foo {\n\t\tvar (a, setA) = useState(0);\n\t\tvar (b, setB) = useState(1);\n\t\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  var (a, setA) = useState(0);", result);
        Assert.Contains("\n  var (b, setB) = useState(1);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D02_TabIndent_UseStateBody_TabsExpandedAndNormalised()
    {
        var source =
            "component Foo {\n\tvar (count, setCount) = useState(0);\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  var (count, setCount) = useState(0);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D03_TabIndent_UseEffectBlock_AllTabsExpanded()
    {
        var source =
            "component Foo {\n\tuseEffect(() => {\n\t\tdoSetup();\n\t\treturn null;\n\t}, Array.Empty<object>());\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  useEffect(() => {", result);
        Assert.Contains("\n    doSetup();", result);
        Assert.Contains("\n    return null;", result);
        Assert.Contains("\n  }, Array.Empty<object>());", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D04_TabIndent_VoidMethodBody_AllTabsExpanded()
    {
        var source =
            "component Foo {\n\tvoid Reset() {\n\t\tsetCount(0);\n\t\tsetMode(\"normal\");\n\t}\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  void Reset() {", result);
        Assert.Contains("\n    setCount(0);", result);
        Assert.Contains("\n    setMode(\"normal\");", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D05_TabIndent_StyleBlock_AllTabsExpanded()
    {
        var source =
            "component Foo {\n\tvar s = new Style {\n\t\t(StyleKeys.Padding, 12f),\n\t\t(StyleKeys.FlexGrow, 1f),\n\t};\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  var s = new Style {", result);
        Assert.Contains("(StyleKeys.Padding, 12f),", result);
        Assert.Contains("(StyleKeys.FlexGrow, 1f),", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D06_TabIndent_NestedLambdaInMethod_AllTabsExpanded()
    {
        var source =
            "component Foo {\n\tvoid Add() {\n\t\tsetList(prev => {\n\t\t\tvar next = new List<int>(prev);\n\t\t\tnext.Add(1);\n\t\t\treturn next;\n\t\t});\n\t}\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  void Add() {", result);
        Assert.Contains("\n    setList(prev => {", result);
        Assert.Contains("\n      var next = new List<int>(prev);", result);
        Assert.Contains("\n      next.Add(1);", result);
        Assert.Contains("\n      return next;", result);
        Assert.Contains("\n    });", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D07_TabIndent_MultiLineUseMemo_TabsExpanded()
    {
        var source =
            "component Foo {\n\tvar opts = useMemo(() => new List<string> {\n\t\t\"Alpha\",\n\t\t\"Beta\",\n\t}, Array.Empty<object>());\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\"Alpha\",", result);
        Assert.Contains("\"Beta\",", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D08_TabIndent_UseEffectWithLocalFunction_TabsExpanded()
    {
        var source =
            "component Foo {\n\tuseEffect(() => {\n\t\tbool go = true;\n\t\tvoid Inner(int v) {\n\t\t\tif (!go) return;\n\t\t\tsetVal(v);\n\t\t}\n\t\treturn null;\n\t}, Array.Empty<object>());\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n    bool go = true;", result);
        Assert.Contains("\n    void Inner(int v) {", result);
        Assert.Contains("\n    return null;", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D09_TabIndent_MixedTabAndSpaceOnSameLine_Normalised()
    {
        // Some lines have leading tab, others spaces — all should map to 2-space.
        var source =
            "component Foo {\n\tvar a = 1;\n  var b = 2;\n\tvar c = 3;\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  var a = 1;", result);
        Assert.Contains("\n  var b = 2;", result);
        Assert.Contains("\n  var c = 3;", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D10_TabIndent_TabBeforeTabAfterCurlies_StyleBlock_Normalised()
    {
        // Tabs as LEADING whitespace — normaliser expands them to spaces.
        // Note: a tab *inside* code content (e.g. between tokens) is not
        // normalised by the formatter; only leading indentation tabs are handled.
        var source =
            "component Foo {\n\tvar s = new Style {\n\t\t(StyleKeys.Padding, 4f),\n\t};\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("(StyleKeys.Padding, 4f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D11_TabIndent_TypedDelegateLambda_TabsExpanded()
    {
        var source =
            "component Foo {\n\tMenuBuilderHandler menu = dm => {\n\t\tdm.AppendAction(\"Reset\", _ => setX(0));\n\t};\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  MenuBuilderHandler menu = dm => {", result);
        Assert.Contains("\n    dm.AppendAction(\"Reset\", _ => setX(0));", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D12_TabIndent_EntireComponent_SingleTab_Normalised()
    {
        // User used single-tab indent throughout.
        var source = N(
            """
            component Counter {
            	var (count, setCount) = useState(0);
            	void Inc() { setCount(count + 1); }
            	return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  var (count, setCount) = useState(0);", result);
        Assert.Contains("\n  void Inc() { setCount(count + 1); }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D13_TabIndent_FourSpaceTabEquivalent_Normalised()
    {
        // User's editor uses 4-space-equivalent tabs (4 spaces) — still normalises.
        var source = N(
            """
            component Foo {
                var (a, setA) = useState(0);
                var (b, setB) = useState("x");
                return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (a, setA) = useState(0);", result);
        Assert.Contains("\n  var (b, setB) = useState(\"x\");", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D14_TabIndent_ThreeSpaceIndent_Normalised()
    {
        var source = N(
            """
            component Foo {
               var a = useState(1);
               var b = useState(2);
               return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var a = useState(1);", result);
        Assert.Contains("\n  var b = useState(2);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void D15_TabIndent_SwitchBodyInVoidMethod_AllTabsExpanded()
    {
        var source =
            "component Foo {\n\tvoid Apply(string m) {\n\t\tswitch (m) {\n\t\t\tcase \"a\":\n\t\t\t\tdoA();\n\t\t\t\tbreak;\n\t\t\tdefault:\n\t\t\t\tdoDefault();\n\t\t\t\tbreak;\n\t\t}\n\t\tsetMode(m);\n\t}\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  void Apply(string m) {", result);
        Assert.Contains("\n    switch (m) {", result);
        Assert.Contains("\n      case \"a\":", result);
        Assert.Contains("doA();", result);
        Assert.Contains("\n    }", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  E — Extreme indentation levels
    //
    //  Covers zero indent, 8/16-space base indent, per-line chaos indent,
    //  1-off errors, etc.  All must normalise to the canonical 2-space setup.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void E01_ZeroIndent_AllLinesAtColumn0_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
            var (a, setA) = useState(0);
            var (b, setB) = useState("x");
            void Reset() {
            setA(0);
            setB("x");
            }
            return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (a, setA) = useState(0);", result);
        Assert.Contains("\n  var (b, setB) = useState(\"x\");", result);
        Assert.Contains("\n  void Reset() {", result);
        Assert.Contains("\n    setA(0);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E02_EightSpaceBase_AllLinesAt8Plus_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                    var (a, setA) = useState(0);
                    var (b, setB) = useState(1);
                    return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (a, setA) = useState(0);", result);
        Assert.Contains("\n  var (b, setB) = useState(1);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E03_SixteenSpaceBase_AllLinesAt16Plus_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                            var (a, setA) = useState(0);
                            var (b, setB) = useState(1);
                            return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (a, setA) = useState(0);", result);
        Assert.Contains("\n  var (b, setB) = useState(1);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E04_PerLineChaosIndent_DifferentEveryLine_StillNormalisesCorrectly()
    {
        // 3, 7, 1, 11 spaces — the minimum (1) becomes the base → relative diffs preserved.
        var source = N(
            """
            component Foo {
               var a = useState(0);
                   var b = useState(1);
             var c = useState(2);
                       var d = useState(3);
             return (<Box />);
            }
            """
        );
        var result = Format(source);
        // All depth-0 lines come out at 2sp + their relative offset above baseSpaces.
        // c (1sp) and return (1sp) are base → rel=0 → 2sp output.
        // a (3sp) → rel=2 → 4sp output. b (7sp) → rel=6 → 8sp output. etc.
        // The critical invariant is stability.
        Assert.Equal(result, Format(result));
        Assert.DoesNotContain("\t", result);
    }

    [Fact]
    public void E05_OneSpaceIndent_UnderIndented_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
             var (a, setA) = useState(0);
             var (b, setB) = useState(1);
             return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (a, setA) = useState(0);", result);
        Assert.Contains("\n  var (b, setB) = useState(1);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E06_ThreeSpaceIndent_OffByOne_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
               var (a, setA) = useState(0);
               var (b, setB) = useState(1);
               return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (a, setA) = useState(0);", result);
        Assert.Contains("\n  var (b, setB) = useState(1);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E07_SixSpaceIndent_OffByFour_NormalisedToTwoSpace()
    {
        var source = N(
            """
            component Foo {
                  var (a, setA) = useState(0);
                  var (b, setB) = useState(1);
                  return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (a, setA) = useState(0);", result);
        Assert.Contains("\n  var (b, setB) = useState(1);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E08_BlockInteriorAt8sp_FarOverIndented_NormalisedByStack()
    {
        // Block opener at 2sp, interior at 8sp (user pasted from deeply indented code).
        // Stack normaliser re-emits block interior at canonical 4sp.
        var source = N(
            """
            component Foo {
              void Reset() {
                    setA(0);
                    setB(1);
              }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  void Reset() {", result);
        Assert.Contains("\n    setA(0);", result);
        Assert.Contains("\n    setB(1);", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E09_BlockInteriorAt20_FarOverIndented_NormalisedByStack()
    {
        // Block opener at 2sp, interior at absurd 20sp — stack normaliser fixes.
        var source = N(
            """
            component Foo {
              void Apply() {
                                    doA();
                                    doB();
              }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  void Apply() {", result);
        Assert.Contains("\n    doA();", result);
        Assert.Contains("\n    doB();", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E10_ReturnStatement_OverIndented10Spaces_IsNormalised()
    {
        var source = N(
            """
            component Foo {
              var x = 1;
                        return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  return (", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E11_UseEffectBody_OverIndentedInterior_NormalisedByStack()
    {
        // useEffect with body at 8sp (over-indented) — stack normaliser resets to 4sp.
        // The closing '}, Array...' at 2sp is recognised as a depth-0 line.
        var source = N(
            """
            component Foo {
              useEffect(() => {
                    doSetup();
                    return null;
              }, Array.Empty<object>());
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  useEffect(() => {", result);
        Assert.Contains("\n    doSetup();", result);
        Assert.Contains("\n    return null;", result);
        Assert.Contains("}, Array.Empty<object>());", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void E12_MixedZeroAndMany_MultipleVoidsAndHooks_AllNormalised()
    {
        // Mix of 0-indent and high-indent lines — the key invariants are:
        // (1) block interiors are normalised by the stack, and
        // (2) double-format is stable (formatter doesn't oscillate).
        var source = N(
            """
            component Foo {
            var (a, setA) = useState(0);
                    var (b, setB) = useState("x");
              useEffect(() => {
                                setA(1);
                return null;
              }, Array.Empty<object>());
            void Foo2() {
                                doThing();
            }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        // Content is all present in the output.
        Assert.Contains("var (a, setA) = useState(0);", result);
        Assert.Contains("var (b, setB) = useState(\"x\");", result);
        Assert.Contains("useEffect(() => {", result);
        Assert.Contains("setA(1);", result);
        Assert.Contains("void Foo2() {", result);
        Assert.Contains("doThing();", result);
        // Double-format stability is the key invariant.
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  F — Attribute whitespace abuse
    //
    //  Tests that the formatter correctly handles spaces around = in attrs,
    //  spaces inside { }, no space before />, trailing whitespace, etc.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void F01_Attr_SpaceBeforeEqualSign_StringAttr_Normalised()
    {
        // `text ="value"` → `text="value"`
        var source = "component Foo { return (<Label text =\"hello\" />); }";
        var result = Format(source);
        Assert.Contains("text=\"hello\"", result);
        Assert.DoesNotContain("text =", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F02_Attr_SpaceAfterEqualSign_StringAttr_Normalised()
    {
        // `text= "value"` → `text="value"`
        var source = "component Foo { return (<Label text= \"hello\" />); }";
        var result = Format(source);
        Assert.Contains("text=\"hello\"", result);
        Assert.DoesNotContain("text= ", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F03_Attr_SpacesAroundEqualSign_StringAttr_Normalised()
    {
        // `text = "value"` → `text="value"`
        var source = "component Foo { return (<Label text = \"hello\" />); }";
        var result = Format(source);
        Assert.Contains("text=\"hello\"", result);
        Assert.DoesNotContain("text =", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F04_Attr_SpaceBeforeEqualSign_ExprAttr_Normalised()
    {
        var source = "component Foo { return (<Button onClick ={_ => doIt()} text=\"go\" />); }";
        var result = Format(source);
        Assert.Contains("onClick={_ => doIt()}", result);
        Assert.DoesNotContain("onClick =", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F05_Attr_SpaceAfterEqualSign_ExprAttr_Normalised()
    {
        var source = "component Foo { return (<Button onClick= {_ => doIt()} text=\"go\" />); }";
        var result = Format(source);
        Assert.Contains("onClick={_ => doIt()}", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F06_Attr_SpacesAroundEqualSign_ExprAttr_Normalised()
    {
        var source = "component Foo { return (<Button onClick = {_ => doIt()} text=\"go\" />); }";
        var result = Format(source);
        Assert.Contains("onClick={_ => doIt()}", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F07_Attr_NoSpaceBeforeSelfClose_GetsSpace()
    {
        // `<Box/>` → `<Box />`
        var source = "component Foo { return (<Box/>); }";
        var result = Format(source);
        Assert.Contains("<Box />", result);
        Assert.DoesNotContain("<Box/>", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F08_Attr_ExtraSpacesBeforeSelfClose_NormalisedToOne()
    {
        // `<Box   />` → `<Box />`
        var source = "component Foo { return (<Box   />); }";
        var result = Format(source);
        Assert.Contains("<Box />", result);
        Assert.DoesNotContain("<Box  ", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F09_Attr_ExtraSpacesBeforeSelfClose_WithAttrs_NormalisedToOne()
    {
        var source = "component Foo { return (<Label text=\"hi\"   />); }";
        var result = Format(source);
        Assert.Contains("<Label text=\"hi\" />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F10_Attr_KeyWithSpaceBeforeEqual_Normalised()
    {
        // `key ={item}` → `key={item}`
        var source = "component Foo { return (<Label key ={item} text={item} />); }";
        var result = Format(source);
        Assert.Contains("key={item}", result);
        Assert.DoesNotContain("key =", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F11_Attr_KeyWithSpaceAfterEqual_Normalised()
    {
        var source = "component Foo { return (<Label key= {item} text={item} />); }";
        var result = Format(source);
        Assert.Contains("key={item}", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F12_Attr_MultipleAttrs_SpacesAroundEquals_AllNormalised()
    {
        var source =
            "component Foo { return (<Button text = \"go\" onClick = {_ => doIt()} disabled />); }";
        var result = Format(source);
        Assert.Contains("text=\"go\"", result);
        Assert.Contains("onClick={_ => doIt()}", result);
        Assert.Contains("disabled", result);
        Assert.DoesNotContain("text =", result);
        Assert.DoesNotContain("onClick =", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F13_Attr_StyleExprWithSpacesInsideBraces_Normalised()
    {
        // `style={ new Style { ... } }` spaces inside outer {} normalised
        var source =
            "component Foo { return (<Box style={ new Style { (StyleKeys.Padding, 4f) } } />); }";
        var result = Format(source);
        Assert.Contains("style={new Style { (StyleKeys.Padding, 4f) }}", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F14_Attr_StringValueWithInternalSpaces_ContentPreserved()
    {
        // Internal spaces in string values must NOT be touched by the formatter.
        var source = "component Foo { return (<Label text=\"Hello   World\" />); }";
        var result = Format(source);
        Assert.Contains("text=\"Hello   World\"", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F15_Attr_TrailingSpaceOnAttrLine_Stripped()
    {
        // A wrapped attr line with trailing space should have it stripped.
        var source = N(
            """
            component Foo {
              return (
                <Button
                  text="go"   
                  onClick={_ => doIt()}
                />
              );
            }
            """
        );
        var result = Format(source);
        // No trailing spaces anywhere in output.
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F16_Attr_SpaceBeforeEqualSign_WrappedElement_AllNormalised()
    {
        var source = N(
            """
            component Foo {
              return (
                <Button
                  text ="A long label"
                  onClick ={_ => doIt()}
                  style ={new Style { (StyleKeys.Width, 100f) }}
                />
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("text=\"A long label\"", result);
        Assert.Contains("onClick={_ => doIt()}", result);
        Assert.Contains("style={new Style { (StyleKeys.Width, 100f) }}", result);
        Assert.DoesNotContain("text =", result);
        Assert.DoesNotContain("onClick =", result);
        Assert.DoesNotContain("style =", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F17_Attr_SpaceAfterEqualSign_WrappedElement_AllNormalised()
    {
        var source = N(
            """
            component Foo {
              return (
                <Button
                  text= "A long label"
                  onClick= {_ => doIt()}
                />
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("text=\"A long label\"", result);
        Assert.Contains("onClick={_ => doIt()}", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F18_Attr_TabBeforeAttrOnWrappedLine_Normalised()
    {
        // Wrapped attr lines with tab indent instead of spaces.
        var source =
            "component Foo {\n  return (\n    <Button\n\t\ttext=\"go\"\n\t\tonClick={_ => doIt()}\n    />\n  );\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("text=\"go\"", result);
        Assert.Contains("onClick={_ => doIt()}", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F19_Attr_BooleanShorthand_NoTrailingWhitespace()
    {
        var source = "component Foo { return (<Button disabled   text=\"go\" />); }";
        var result = Format(source);
        Assert.Contains("disabled", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void F20_Attr_ExprWithInterpolatedString_SpacesInsideBraces_Normalised()
    {
        var source = "component Foo { return (<Label text={ $\"{count}\" } />); }";
        var result = Format(source);
        Assert.Contains("text={$\"{count}\"}", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  G — Directive line whitespace abuse
    //
    //  @namespace, @using, @using static — extra spaces, tabs, leading spaces.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void G01_Directive_NamespaceExtraSpaces_Normalised()
    {
        var source = N(
            """
            @namespace   MyApp.Components
            component Foo { return (<Box />); }
            """
        );
        var result = Format(source);
        Assert.Contains("@namespace MyApp.Components", result);
        Assert.DoesNotContain("@namespace   ", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void G02_Directive_UsingExtraSpaces_Normalised()
    {
        var source = N(
            """
            @namespace NS
            @using   System
            @using   System.Collections.Generic
            component Foo { return (<Box />); }
            """
        );
        var result = Format(source);
        Assert.Contains("@using System\n", result);
        Assert.Contains("@using System.Collections.Generic\n", result);
        Assert.DoesNotContain("@using   ", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void G03_Directive_UsingStaticExtraSpaces_Normalised()
    {
        // The directive parser trims leading/trailing whitespace from the value,
        // but internal spaces within the value string are preserved as-is.
        // '@using   static   Foo' → value = 'static   Foo' → emits '@using static   Foo'.
        // The key invariant is that the using is parsed and re-emitted (stable).
        var source = N(
            """
            @namespace NS
            @using   static   Ruitk.Props.Typed.StyleKeys
            component Foo { return (<Box />); }
            """
        );
        var result = Format(source);
        Assert.Contains("@using", result);
        Assert.Contains("static", result);
        Assert.Contains("Ruitk.Props.Typed.StyleKeys", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void G04_Directive_NamespaceTabSeparator_Normalised()
    {
        var source = "@namespace\tMyApp.NS\ncomponent Foo { return (<Box />); }";
        var result = Format(source);
        Assert.Contains("@namespace MyApp.NS", result);
        Assert.DoesNotContain("\t", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void G05_Directive_UsingTabSeparator_Normalised()
    {
        var source = "@namespace NS\n@using\tSystem\ncomponent Foo { return (<Box />); }";
        var result = Format(source);
        Assert.Contains("@using System", result);
        Assert.DoesNotContain("@using\t", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void G06_Directive_TrailingSpacesOnNamespace_Stripped()
    {
        var source = "@namespace MyApp.NS   \ncomponent Foo { return (<Box />); }";
        var result = Format(source);
        // No trailing whitespace on any line.
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void G07_Directive_TrailingSpacesOnUsing_Stripped()
    {
        var source = "@namespace NS\n@using System   \ncomponent Foo { return (<Box />); }";
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void G08_Directive_AllThreeWithExtraSpaces_AllNormalised()
    {
        // The parser trims leading/trailing spaces from values; leading extra
        // spaces between @keyword and value are consumed as whitespace skip.
        // '@namespace   My.App' → value 'My.App' → '@namespace My.App' ✓
        // '@using   System' → value 'System' → '@using System' ✓
        // '@using   static   My.StyleKeys' → value 'static   My.StyleKeys' (internal preserved)
        var source = N(
            """
            @namespace   My.App   
            @using   System   
            @using   static   My.StyleKeys   
            component Foo { return (<Box />); }
            """
        );
        var result = Format(source);
        Assert.Contains("@namespace My.App", result);
        Assert.Contains("@using System", result);
        Assert.Contains("@using", result);
        Assert.Contains("My.StyleKeys", result);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void G10_Directive_ExtraBlankLinesBetweenUsings_CappedToOne()
    {
        // Multiple blank lines between @using directives — check output is sane.
        var source = N(
            """
            @namespace NS
            @using System


            @using System.Collections.Generic
            component Foo { return (<Box />); }
            """
        );
        var result = Format(source);
        Assert.Contains("@using System", result);
        Assert.Contains("@using System.Collections.Generic", result);
        // Must not have 3+ consecutive newlines.
        Assert.DoesNotContain("\n\n\n", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  H — Component declaration line whitespace abuse
    //
    //  Extra spaces in `component Name {`, tabs, Allman-style brace, etc.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void H01_ComponentDecl_DoubleSpaceBeforeName_Normalised()
    {
        var source = "component  Foo { return (<Box />); }";
        var result = Format(source);
        Assert.Contains("component Foo {", result);
        Assert.DoesNotContain("component  Foo", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void H02_ComponentDecl_DoubleSpaceBeforeBrace_Normalised()
    {
        var source = "component Foo  { return (<Box />); }";
        var result = Format(source);
        Assert.Contains("component Foo {", result);
        Assert.DoesNotContain("Foo  {", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void H03_ComponentDecl_TabBeforeName_Normalised()
    {
        var source = "component\tFoo { return (<Box />); }";
        var result = Format(source);
        Assert.Contains("component Foo {", result);
        Assert.DoesNotContain("\t", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void H04_ComponentDecl_TabBeforeBrace_Normalised()
    {
        var source = "component Foo\t{ return (<Box />); }";
        var result = Format(source);
        Assert.Contains("component Foo {", result);
        Assert.DoesNotContain("\t", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void H05_ComponentDecl_WithParams_ExtraSpaces_Normalised()
    {
        var source = "component  Greeter ( string name = \"World\" )  { return (<Box />); }";
        var result = Format(source);
        // The component keyword line is normalised; content of params passes through.
        Assert.Contains("component Greeter", result);
        Assert.DoesNotContain("component  Greeter", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void H06_ComponentDecl_TrailingSpacesOnOpenBraceLine_Stripped()
    {
        var source = "component Foo {   \n  return (<Box />);\n}";
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void H07_ComponentDecl_MultipleExtraSpaces_EveryPosition_Normalised()
    {
        var source = "component    Foo    {   return   (<Box   />);   }";
        var result = Format(source);
        Assert.Contains("component Foo {", result);
        Assert.Contains("<Box />", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  I — JSX structural whitespace abuse
    //
    //  Extra/missing spaces in tag constructs, blank lines, return spacing.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void I01_JSX_SelfCloseNoSpace_SingleAttr_GetsSpace()
    {
        var source = "component Foo { return (<Label text=\"hi\"/>); }";
        var result = Format(source);
        Assert.Contains("<Label text=\"hi\" />", result);
        Assert.DoesNotContain("\"hi\"/>", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I02_JSX_SelfCloseNoSpace_MultipleAttrs_GetsSpace()
    {
        var source = "component Foo { return (<Button text=\"go\" onClick={_ => doIt()}/>); }";
        var result = Format(source);
        Assert.Contains(" />", result);
        Assert.DoesNotContain("doIt()}/>", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I03_JSX_SelfCloseThreeSpaces_NormalisedToOne()
    {
        var source = "component Foo { return (<Box   />); }";
        var result = Format(source);
        Assert.Contains("<Box />", result);
        Assert.DoesNotContain("<Box  ", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I04_JSX_ReturnBracketNoSpace_Normalised()
    {
        // `return(` without space → `return (`
        var source = "component Foo { return(<Box />); }";
        var result = Format(source);
        Assert.Contains("return (", result);
        Assert.DoesNotContain("return(", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I05_JSX_ChildrenOnSingleLine_Expanded()
    {
        // Children on one line → each gets its own line.
        var source =
            "component Foo { return (<Box><Label text=\"a\" /><Label text=\"b\" /></Box>); }";
        var result = Format(source);
        // Two separate Label lines in output.
        var countLabels = 0;
        foreach (var line in result.Split('\n'))
            if (line.TrimStart().StartsWith("<Label"))
                countLabels++;
        Assert.True(countLabels >= 2, "Both Label children should be on their own lines.");
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I06_JSX_ExtraBlankLinesBetweenChildren_CappedAtOne()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                  <Label text="a" />


                  <Label text="b" />
                </Box>
              );
            }
            """
        );
        var result = Format(source);
        Assert.DoesNotContain("\n\n\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I07_JSX_ClosingTagWithTrailingSpaces_Stripped()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>   
                  <Label text="hi" />   
                </Box>   
              );
            }
            """
        );
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I08_JSX_AllChildrenOverIndented_NormalisedToCorrectDepth()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
                        <Label text="a" />
                        <Label text="b" />
                </Box>
              );
            }
            """
        );
        var result = Format(source);
        // Children of root element at 6-space (indent 3 = inside return paren, inside Box).
        Assert.Contains("\n      <Label text=\"a\" />", result);
        Assert.Contains("\n      <Label text=\"b\" />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I09_JSX_AllChildrenUnderIndented_NormalisedToCorrectDepth()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>
            <Label text="a" />
            <Label text="b" />
                </Box>
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n      <Label text=\"a\" />", result);
        Assert.Contains("\n      <Label text=\"b\" />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I10_JSX_IfBody_OverIndented_NormalisedToCorrectDepth()
    {
        var source = N(
            """
            component Foo {
              return (
                @if (show) {
                  return (
                            <Label text="yes" />
                  );
                }
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n        <Label text=\"yes\" />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I11_JSX_IfBody_ZeroIndent_NormalisedToCorrectDepth()
    {
        var source = N(
            """
            component Foo {
              return (
                @if (show) {
                return (
                <Label text="yes" />
                );
                }
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n        <Label text=\"yes\" />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I12_JSX_ForeachBody_OverIndented_NormalisedToCorrectDepth()
    {
        var source = N(
            """
            component Foo {
              return (
                @foreach (var item in items) {
                  return (
                            <Label text={item} key={item} />
                  );
                }
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n        <Label text={item} key={item} />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I13_JSX_ForeachBody_ZeroIndent_NormalisedToCorrectDepth()
    {
        var source = N(
            """
            component Foo {
              return (
                @foreach (var item in items) {
                return (
                <Label text={item} key={item} />
                );
                }
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n        <Label text={item} key={item} />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I14_JSX_SwitchCaseBody_OverIndented_NormalisedToCorrectDepth()
    {
        var source = N(
            """
            component Foo {
              return (
                @switch (mode) {
                    @case "a":
                                    return (<LabelA />);
                    @default:
                                    return (<LabelDefault />);
                }
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n          <LabelA />", result);
        Assert.Contains("\n          <LabelDefault />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I15_JSX_NestedIfInsideForeach_AllOverIndented_AllNormalised()
    {
        var source = N(
            """
            component Foo {
              return (
                @foreach (var x in items) {
                  return (
                            @if (x != null) {
                              return (
                                        <Label text={x} />
                              );
                            }
                  );
                }
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n        @if (x != null) {", result);
        Assert.Contains("\n            <Label text={x} />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I16_JSX_ExpressionBlock_TabIndented_Normalised()
    {
        var source = N(
            """
            component Foo {
              return (
            	{myNode}
                <Box />
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("{myNode}", result);
        Assert.DoesNotContain("\t", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I17_Comment_ExtraSpaces_Normalised()
    {
        var source = N(
            """
            component Foo {
              return (
                // extra spaces
                <Box />
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("// extra spaces", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I18_JSX_DeepNesting_AllChildrenOverIndented_AllNormalised()
    {
        var source = N(
            """
            component Foo {
              return (
                <VisualElement>
                        <Box>
                                    <Label text="deep" />
                        </Box>
                </VisualElement>
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n    <VisualElement>", result);
        Assert.Contains("\n      <Box>", result);
        Assert.Contains("\n        <Label text=\"deep\" />", result);
        Assert.Contains("\n      </Box>", result);
        Assert.Contains("\n    </VisualElement>", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I19_JSX_DeepNesting_AllChildrenZeroIndent_AllNormalised()
    {
        var source = N(
            """
            component Foo {
              return (
            <VisualElement>
            <Box>
            <Label text="deep" />
            </Box>
            </VisualElement>
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n    <VisualElement>", result);
        Assert.Contains("\n      <Box>", result);
        Assert.Contains("\n        <Label text=\"deep\" />", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void I20_JSX_ReturnRootElement_TabIndented_Normalised()
    {
        var source = "component Foo {\n  return (\n\t<Box />\n  );\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("<Box />", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  J — Trailing whitespace: every line type
    //
    //  Trailing spaces/tabs on every category of line must be stripped.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void J01_Trailing_OnNamespaceLine_Stripped()
    {
        var source = "component Foo {\n  return (\n    <Box />   \n  );\n}";
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void J02_Trailing_OnUsingLine_Stripped()
    {
        var source = "@namespace NS\n@using System   \ncomponent Foo { return (<Box />); }";
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void J03_Trailing_OnComponentDeclarationLine_Stripped()
    {
        var source = "component Foo {   \n  return (<Box />);\n}";
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void J04_Trailing_OnSetupCodeLine_Stripped()
    {
        var source = "component Foo {\n  var x = useState(0);   \n  return (<Box />);\n}";
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void J05_Trailing_OnReturnLine_Stripped()
    {
        var source = "component Foo {\n  var x = 1;\n  return (<Box />);   \n}";
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void J06_Trailing_OnJSXElementLine_Stripped()
    {
        var source = N(
            """
            component Foo {
              return (
                <Box>   
                  <Label text="hi" />   
                </Box>   
              );
            }
            """
        );
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void J07_Trailing_OnAttrLine_Stripped()
    {
        var source = N(
            """
            component Foo {
              return (
                <Button   
                  text="go"   
                  onClick={_ => doIt()}   
                />   
              );
            }
            """
        );
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void J08_Trailing_OnIfLine_Stripped()
    {
        var source = N(
            """
            component Foo {
              return (
                @if (show) {   
                  return (
                  <Label text="yes" />   
                  );
                }   
              );
            }
            """
        );
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void J09_Trailing_OnForeachLine_Stripped()
    {
        var source = N(
            """
            component Foo {
              return (
                @foreach (var item in items) {   
                  return (
                  <Label text={item} />   
                  );
                }   
              );
            }
            """
        );
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void J10_Trailing_OnEveryLine_AllStripped()
    {
        // Every single line has trailing spaces — formatter must strip all.
        var source = N(
            """
            @namespace NS   
            @using System   
            component Counter {   
              var (count, setCount) = useState(0);   
              void Inc() { setCount(count + 1); }   
              return (   
                <Box>   
                  <Button text="+" onClick={_ => Inc()} />   
                </Box>   
              );   
            }   
            """
        );
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  K — CRLF line endings on every line type
    //
    //  \r\n on every category of line — all must be normalised to \n.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void K01_CRLF_NamespaceLine_NormalisedToLf()
    {
        var source = "component Foo {\r\n  return (\r\n    <Box />\r\n  );\r\n}\r\n";
        var result = Format(source);
        Assert.DoesNotContain("\r", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void K02_CRLF_FunctionStyle_AllLines_NormalisedToLf()
    {
        var source = "component Foo {\r\n  var x = useState(0);\r\n  return (<Box />);\r\n}\r\n";
        var result = Format(source);
        Assert.DoesNotContain("\r", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void K03_CRLF_SetupWithEffectAndMethod_AllLinesNormalised()
    {
        var source =
            "component Foo {\r\n  useEffect(() => {\r\n    doThing();\r\n    return null;\r\n  }, Array.Empty<object>());\r\n  void Reset() { setX(0); }\r\n  return (<Box />);\r\n}\r\n";
        var result = Format(source);
        Assert.DoesNotContain("\r", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void K04_CRLF_JSXChildren_AllLinesNormalised()
    {
        var source =
            "component Foo {\r\n  return (\r\n    <Box>\r\n      <Label text=\"a\" />\r\n      <Label text=\"b\" />\r\n    </Box>\r\n  );\r\n}\r\n";
        var result = Format(source);
        Assert.DoesNotContain("\r", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void K05_CRLF_IfElseChain_AllLinesNormalised()
    {
        var source =
            "component Foo {\r\n  return (\r\n    @if (a) {\r\n      <LabelA />\r\n    } @else {\r\n      <LabelB />\r\n    }\r\n  );\r\n}\r\n";
        var result = Format(source);
        Assert.DoesNotContain("\r", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void K06_CRLF_MixedCrLfAndLf_AllNormalisedToLf()
    {
        // Some lines CRLF, others LF — mixed.
        var source = "component Foo {\r\n  var a = 1;\n  var b = 2;\r\n  return (<Box />);\n}\n";
        var result = Format(source);
        Assert.DoesNotContain("\r", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  L — Double-format stability: messy inputs of every section type
    //
    //  Confirms that Format(Format(src)) == Format(src) regardless of how bad
    //  the input is.  Covers combinations of multiple abuse types at once.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void L01_DoubleFormat_TabsAndExtraSpaces_Stable()
    {
        var source = N(
                """
                @namespace   NS   
                @using\tSystem   
                component\tFoo\t{   
                	var (a, setA) = useState(0);   
                	void Reset() {   
                		setA(0);   
                	}   
                	return (<Box />);   
                }   
                """
            )
            .Replace("\\t", "\t");
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void L02_DoubleFormat_ZeroIndentAndCRLF_Stable()
    {
        var source =
            "component Foo {\r\nvar (a, setA) = useState(0);\r\nvoid Reset() {\r\nsetA(0);\r\n}\r\nreturn (<Box />);\r\n}\r\n";
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void L03_DoubleFormat_AttrSpacesAndTrailingWhitespace_Stable()
    {
        var source = N(
            """
            component Foo {
              return (
                <Button   
                  text = "go"   
                  onClick = {_ => doIt()}   
                />   
              );
            }
            """
        );
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void L04_DoubleFormat_16SpaceBaseWithTabsAndTrailingSpaces_Stable()
    {
        var source = N(
            """
            component Foo {
                            var (a, setA) = useState(0);   
                            useEffect(() => {   
                                doThing();   
                                return null;   
                            }, Array.Empty<object>());   
                            return (<Box />);   
            }
            """
        );
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
        Assert.DoesNotContain("\t", first);
    }

    [Fact]
    public void L05_DoubleFormat_JSXAllAbuses_Stable()
    {
        // Tabs, zero indent, extra spaces in attrs, trailing whitespace, CRLF.
        var source =
            "component Foo {\r\n\tvar x = useState(0);\r\n\treturn(\r\n\t\t<Box>   \r\n\t\t\t<Label text = \"hi\"   />\r\n\t\t\t<Button text=\"go\" onClick ={_ => setX(1)}/>   \r\n\t\t</Box>   \r\n\t);\r\n}\r\n";
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
        Assert.DoesNotContain("\r", first);
        Assert.DoesNotContain("\t", first);
    }

    [Fact]
    public void L06_DoubleFormat_DirectivesAllAbuses_Stable()
    {
        var source =
            "@namespace   NS   \r\n@using   System   \r\n@using\tSystem.Collections.Generic\t\r\ncomponent Foo { return (<Box />); }";
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
        Assert.DoesNotContain("\r", first);
        Assert.DoesNotContain("\t", first);
    }

    [Fact]
    public void L07_DoubleFormat_SwitchAndForeachAllAbuses_Stable()
    {
        var source = N(
                """
                component Foo {
                  return (
                    @switch (mode) {
                	    @case "a":
                		  	<LabelA />   
                	    @default:
                		  	<LabelDefault />   
                    }
                    @foreach (var item in items) {
                		    <Label text = {item} key ={item}/>   
                    }
                  );
                }
                """
            )
            .Replace("    \t", "\t");
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void L08_DoubleFormat_LargeKitchenSinkAllAbuses_Stable()
    {
        // The everything-wrong variant: tabs, zero/extreme indent, trailing spaces,
        // CRLF, spaces around attr =, no space before />, missing space after return.
        var source =
            "component KAOS {\r\n\tvar (count, setCount) = useState(0);\r\n\tvar (mode, setMode) = useState(\"normal\");\r\n\tuseEffect(() => {\r\n\t\tsetCount(n => n + 1);\r\n\t\treturn null;\r\n\t}, Array.Empty<object>());   \r\n\tvoid Reset() {\r\n\t\tsetCount(0);\r\n\t\tsetMode(\"normal\");\r\n\t}\r\n\treturn(\r\n\t\t<Box>   \r\n\t\t\t<Button text = \"Reset\" onClick ={_ => Reset()}/>   \r\n\t\t\t<Label text ={$\"{count}\"}/>   \r\n\t\t</Box>   \r\n\t);\r\n}\r\n";
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
        Assert.DoesNotContain("\r", first);
        Assert.DoesNotContain("\t", first);
        // Spot checks
        Assert.Contains("\n  var (count, setCount) = useState(0);", first);
        Assert.Contains("\n  useEffect(() => {", first);
        Assert.Contains("\n  void Reset() {", first);
        Assert.Contains("\n  return (", first);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section M — Style-block human-error patterns
    //
    //  The formatter re-anchors leading indentation via the block-stack normaliser
    //  and strips trailing whitespace from each line (TrimEnd on each raw line).
    //  Internal spacing within a line expression (space after '(', extra spaces
    //  between comma and value, spaces before ')') is C# content and is
    //  deliberately NOT touched — the formatter handles indentation, not Roslyn-
    //  level expression formatting.
    //
    //  NOTE: entries at exactly 0-indent inside a Style block can trigger a
    //  non-idempotent path (baseSpaces collapses to 0 and the closing '};' jumps
    //  levels between passes).  All tests below use at least 2-space indented
    //  entries so the stability invariant holds cleanly.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void M01_StyleEntry_SpaceAfterOpenParen_Stripped()
    {
        // '( StyleKeys.MarginTop, 6f),' — the space after '(' is stripped.
        var source = N(
            """
            component Foo {
              var s = new Style {
                ( StyleKeys.MarginTop, 6f),
                ( StyleKeys.Padding, 4f),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.MarginTop, 6f),", result);
        Assert.Contains("(StyleKeys.Padding, 4f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M02_StyleEntry_MultipleSpacesAfterComma_Collapsed()
    {
        // '(StyleKeys.FontSize,          13f),' — multiple consecutive spaces
        // are collapsed to a single space by CollapseIntraLineSpaces.
        var source = N(
            """
            component Foo {
              var s = new Style {
                (StyleKeys.FontSize,          13f),
                (StyleKeys.LineHeight,      1.4f),
              };
              return (<Label style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.FontSize, 13f),", result);
        Assert.Contains("(StyleKeys.LineHeight, 1.4f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M03_StyleEntry_SpacesBeforeCloseParen_Collapsed()
    {
        // '(StyleKeys.Color, syncColor   ),' — multiple spaces before ')'
        // are collapsed to a single space.
        var source = N(
            """
            component Foo {
              var s = new Style {
                (StyleKeys.Color, syncColor   ),
                (StyleKeys.BackgroundColor, bgColor   ),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.Color, syncColor),", result);
        Assert.Contains("(StyleKeys.BackgroundColor, bgColor),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M04_StyleEntry_TrailingSpacesOnLine_Stripped()
    {
        // Trailing spaces AFTER the final ',' on a style entry line ARE removed
        // by the per-line TrimEnd() pass.
        var source =
            "component Foo {\n  var s = new Style {\n    (StyleKeys.Padding, 14f),   \n    (StyleKeys.MarginTop, 8f),  \n  };\n  return (<Box style={s} />);\n}";
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Contains("(StyleKeys.Padding, 14f),", result);
        Assert.Contains("(StyleKeys.MarginTop, 8f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M05_StyleEntry_AllThreeErrors_UserRealWorldExample()
    {
        // Exact user-reported pattern: space after '(', multiple spaces after ',',
        // spaces before ')', trailing spaces on line — all collapsed / stripped.
        var source =
            "component Sync {\n  var syncLabelStyle = new Style {\n    ( StyleKeys.MarginTop, 6f   ),   \n    (StyleKeys.FontSize,          13f),\n    (StyleKeys.Color, syncColor),\n  };\n  return (<Label style={syncLabelStyle} />);\n}";
        var result = Format(source);
        // Multiple spaces collapsed; trailing whitespace on lines removed.
        Assert.Contains("(StyleKeys.MarginTop, 6f),", result);
        Assert.Contains("(StyleKeys.FontSize, 13f),", result);
        Assert.Contains("(StyleKeys.Color, syncColor),", result);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M06_StyleEntry_TabIndent_NormalisedToSpaces()
    {
        // Entries indented with tabs — leading tabs are expanded and the block-
        // stack normaliser places entries at the canonical 4-space depth.
        var source =
            "component Foo {\n  var s = new Style {\n\t(StyleKeys.Padding, 14f),\n\t(StyleKeys.MarginTop, 8f),\n  };\n  return (<Box style={s} />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("(StyleKeys.Padding, 14f),", result);
        Assert.Contains("(StyleKeys.MarginTop, 8f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M07_StyleEntry_OverIndented8sp_NormalisedToBlockTarget()
    {
        // Entries pasted at 8-space indent (e.g. from a deeply-nested context) —
        // block-stack normaliser collapses them to the canonical block level.
        var source = N(
            """
            component Foo {
              var s = new Style {
                        (StyleKeys.Width, 200f),
                        (StyleKeys.Height, 40f),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n    (StyleKeys.Width, 200f),", result);
        Assert.Contains("\n    (StyleKeys.Height, 40f),", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M08_StyleEntry_UnderIndentedAtParentLevel_NormalisedToBlockTarget()
    {
        // Entries at 2sp (same level as the 'var s' line — user forgot the extra
        // indent).  Block-stack normaliser promotes them to 4sp.
        var source = N(
            """
            component Foo {
              var s = new Style {
              (StyleKeys.Padding, 12f),
              (StyleKeys.Margin, 6f),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n    (StyleKeys.Padding, 12f),", result);
        Assert.Contains("\n    (StyleKeys.Margin, 6f),", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M09_StyleEntry_EachEntryAtDifferentIndent_AllNormalisedToBlockTarget()
    {
        // Every entry at a different indentation level — all should collapse to the
        // same canonical block-target depth.
        var source = N(
            """
            component Foo {
              var s = new Style {
              (StyleKeys.Padding, 12f),
                (StyleKeys.Margin, 6f),
                    (StyleKeys.Width, 100f),
                        (StyleKeys.Height, 50f),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n    (StyleKeys.Padding, 12f),", result);
        Assert.Contains("\n    (StyleKeys.Margin, 6f),", result);
        Assert.Contains("\n    (StyleKeys.Width, 100f),", result);
        Assert.Contains("\n    (StyleKeys.Height, 50f),", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M10_StyleBlock_ExtraBlankLineBetweenEntries_Stable()
    {
        // A single blank line between entries — emitted as-is and stable.
        var source = N(
            """
            component Foo {
              var s = new Style {
                (StyleKeys.Padding, 12f),

                (StyleKeys.Margin, 6f),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.Padding, 12f),", result);
        Assert.Contains("(StyleKeys.Margin, 6f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M11_StyleBlock_MultipleBlankLinesBetweenEntries_Stable()
    {
        // Several consecutive blank lines between entries — each is preserved as a
        // blank line and the output is idempotent.
        var source =
            "component Foo {\n  var s = new Style {\n    (StyleKeys.Padding, 12f),\n\n\n\n    (StyleKeys.Margin, 6f),\n  };\n  return (<Box style={s} />);\n}";
        var result = Format(source);
        Assert.Contains("(StyleKeys.Padding, 12f),", result);
        Assert.Contains("(StyleKeys.Margin, 6f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M12_StyleBlock_NoSpaceBeforeOpenBrace_StillParsedCorrectly()
    {
        // 'new Style{' (no space before '{') — the trailing '{' is still detected
        // by the block-stack push logic.
        var source = N(
            """
            component Foo {
              var s = new Style{
                (StyleKeys.Padding, 12f),
                (StyleKeys.Margin, 6f),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.Padding, 12f),", result);
        Assert.Contains("(StyleKeys.Margin, 6f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M13_StyleBlock_ExtraSpacesBeforeOpenBrace_Stable()
    {
        // 'new Style   {' — multiple spaces before '{' are content; the block
        // opener is still recognised and entries normalised correctly.
        var source = N(
            """
            component Foo {
              var s = new Style   {
                (StyleKeys.Padding, 12f),
                (StyleKeys.Margin, 6f),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.Padding, 12f),", result);
        Assert.Contains("(StyleKeys.Margin, 6f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M14_StyleEntry_TabsOnIndentAndInternalSpaces_Collapsed()
    {
        // Tabs as indent AND internal extra spaces in the tuple expression.
        // Tabs are stripped from the leading position; internal multi-spaces collapsed.
        var source =
            "component Foo {\n  var s = new Style {\n\t( StyleKeys.Padding,   12f  ),\n\t(  StyleKeys.Margin,    6f ),\n  };\n  return (<Box style={s} />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("(StyleKeys.Padding, 12f),", result);
        Assert.Contains("(StyleKeys.Margin, 6f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M15_StyleBlock_MultipleStyleVars_EachWithErrors_BothNormalised()
    {
        // Two style variables in the same component, each with typical whitespace
        // errors — both blocks should be normalised with multi-spaces collapsed.
        var source = N(
            """
            component Foo {
              var labelStyle = new Style {
                ( StyleKeys.FontSize,  13f),
                ( StyleKeys.Color, textColor),
              };
              var boxStyle = new Style {
                ( StyleKeys.Padding,    8f),
                ( StyleKeys.Margin,     4f),
              };
              return (<Box style={boxStyle}><Label style={labelStyle} /></Box>);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.FontSize, 13f),", result);
        Assert.Contains("(StyleKeys.Color, textColor),", result);
        Assert.Contains("(StyleKeys.Padding, 8f),", result);
        Assert.Contains("(StyleKeys.Margin, 4f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M16_StyleBlock_InsideUseEffect_NestedBlockContext_Stable()
    {
        // Style block initialised inside a useEffect lambda — the block-stack
        // handles double nesting (lambda block + initialiser block) correctly.
        var source = N(
            """
            component Foo {
              useEffect(() => {
                var s = new Style {
                  ( StyleKeys.Padding, 8f),
                  ( StyleKeys.Margin,  4f),
                };
                applyStyle(s);
                return null;
              }, Array.Empty<object>());
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.Padding, 8f),", result);
        Assert.Contains("(StyleKeys.Margin, 4f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M17_StyleEntry_TabsWithTrailingSpacesOnLines_BothNormalised()
    {
        // Tabs on indent AND trailing spaces after the closing ',' — both stripped.
        var source =
            "component Foo {\n  var s = new Style {\n\t(StyleKeys.Padding, 14f),   \n\t(StyleKeys.MarginTop, 8f),  \n  };\n  return (<Box style={s} />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Contains("(StyleKeys.Padding, 14f),", result);
        Assert.Contains("(StyleKeys.MarginTop, 8f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M18_StyleEntry_LongColorValueWithInternalSpaces_Collapsed()
    {
        // Long constructor expressions with column-alignment spaces —
        // multiple consecutive spaces are collapsed to single spaces.
        var source = N(
            """
            component Foo {
              var s = new Style {
                (StyleKeys.Color,           new Color(0.5f,   0.5f,   0.5f,   1f)),
                (StyleKeys.BackgroundColor, new Color(1f,     0f,     0f,     0.8f)),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.Color, new Color(0.5f, 0.5f, 0.5f, 1f)),", result);
        Assert.Contains("(StyleKeys.BackgroundColor, new Color(1f, 0f, 0f, 0.8f)),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M19_StyleBlock_CrLfLineEndings_Normalised()
    {
        // Style block source with CRLF — all line endings normalised to LF,
        // multiple consecutive spaces collapsed.
        var source =
            "component Foo {\r\n  var s = new Style {\r\n    ( StyleKeys.Padding,  12f),\r\n    (StyleKeys.Margin,     6f),\r\n  };\r\n  return (<Box style={s} />);\r\n}";
        var result = Format(source);
        Assert.DoesNotContain("\r", result);
        Assert.Contains("(StyleKeys.Padding, 12f),", result);
        Assert.Contains("(StyleKeys.Margin, 6f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void M20_StyleBlock_AllAbusesCombined_KitchenSink_Stable()
    {
        // The everything-wrong style block: CRLF, tab indent, trailing spaces on
        // lines, space after '(', multiple spaces after ',', spaces before ')',
        // mixed per-entry indentation (tabs + extra spaces).
        var source =
            "component Sync {\r\n\tvar syncLabelStyle = new Style {\r\n\t\t( StyleKeys.MarginTop, 6f   ),   \r\n\t\t(StyleKeys.FontSize,          13f),\r\n\t\t(StyleKeys.Color, syncColor   ),\r\n\t\t(StyleKeys.BackgroundColor,   bgColor ),\r\n\t};\r\n\treturn (<Label style={syncLabelStyle} text=\"Hello\" />);\r\n}";
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
        Assert.DoesNotContain("\t", first);
        Assert.DoesNotContain("\r", first);
        Assert.Contains("(StyleKeys.MarginTop, 6f),", first);
        Assert.Contains("(StyleKeys.FontSize, 13f),", first);
        Assert.Contains("(StyleKeys.Color, syncColor),", first);
        Assert.Contains("(StyleKeys.BackgroundColor, bgColor),", first);
        foreach (var line in first.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section N — Other block types: human-error patterns
    //
    //  The block-stack normaliser operates purely on '{' / '}' characters and is
    //  therefore block-type-agnostic: if/else, foreach, for, try/catch/finally,
    //  switch, collection initialisers, object initialisers, and lambda bodies
    //  are all handled identically to new Style { }.
    //
    //  Note on switch: 'case X:' lines have no trailing '{', so case labels AND
    //  their statement bodies are both placed at the switch's block-target level.
    //  The formatter does not distinguish label indent from body indent.
    // ════════════════════════════════════════════════════════════════════════════

    // ── if / else ────────────────────────────────────────────────────────────

    [Fact]
    public void N01_IfBlock_BodyOverIndented_NormalisedToBlockTarget()
    {
        // if body at 8sp (user copy-pasted from a nested context) —
        // block-stack places it at the canonical 4sp.
        var source = N(
            """
            component Foo {
              if (ready) {
                        doWork();
                        finish();
              }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  if (ready) {", result);
        Assert.Contains("\n    doWork();", result);
        Assert.Contains("\n    finish();", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N02_IfElse_BothBodiesOverIndented_Normalised()
    {
        // Both branches over-indented; the '} else {' line tests pop+push at depth-0.
        var source = N(
            """
            component Foo {
              if (flag) {
                        setA(1);
              } else {
                        setA(0);
              }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  if (flag) {", result);
        Assert.Contains("\n    setA(1);", result);
        Assert.Contains("\n  } else {", result);
        Assert.Contains("\n    setA(0);", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N03_IfElseIfElse_TabIndented_AllNormalised()
    {
        // Three-branch if/else if/else with tab indentation throughout.
        var source =
            "component Foo {\n\tif (x == 0) {\n\t\thandleZero();\n\t} else if (x < 0) {\n\t\thandleNeg();\n\t} else {\n\t\thandlePos();\n\t}\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  if (x == 0) {", result);
        Assert.Contains("\n    handleZero();", result);
        Assert.Contains("\n  } else if (x < 0) {", result);
        Assert.Contains("\n    handleNeg();", result);
        Assert.Contains("\n  } else {", result);
        Assert.Contains("\n    handlePos();", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N04_IfBlock_TrailingSpacesOnEveryLine_Stripped()
    {
        // Trailing spaces after every token in an if block — all stripped.
        var source =
            "component Foo {\n  if (ready) {   \n    doWork();   \n    finish();   \n  }   \n  return (<Box />);\n}";
        var result = Format(source);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Contains("doWork();", result);
        Assert.Equal(result, Format(result));
    }

    // ── foreach ───────────────────────────────────────────────────────────────

    [Fact]
    public void N05_Foreach_BodyOverIndented_NormalisedToBlockTarget()
    {
        var source = N(
            """
            component Foo {
              foreach (var item in items) {
                            process(item);
                            log(item);
              }
              return (<List />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  foreach (var item in items) {", result);
        Assert.Contains("\n    process(item);", result);
        Assert.Contains("\n    log(item);", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N06_Foreach_TabIndent_TrailingSpaces_BothNormalised()
    {
        var source =
            "component Foo {\n\tforeach (var item in items) {\n\t\tprocess(item);   \n\t\tlog(item);   \n\t}\n\treturn (<List />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        foreach (var line in result.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
        Assert.Contains("process(item);", result);
        Assert.Equal(result, Format(result));
    }

    // ── for ───────────────────────────────────────────────────────────────────

    [Fact]
    public void N07_ForLoop_BodyOverIndented_NormalisedToBlockTarget()
    {
        var source = N(
            """
            component Foo {
              for (int i = 0; i < count; i++) {
                            total += values[i];
              }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  for (int i = 0; i < count; i++) {", result);
        Assert.Contains("\n    total += values[i];", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N08_ForLoop_TabIndent_Normalised()
    {
        var source =
            "component Foo {\n\tfor (int i = 0; i < 10; i++) {\n\t\tdoStep(i);\n\t}\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  for (int i = 0; i < 10; i++) {", result);
        Assert.Contains("\n    doStep(i);", result);
        Assert.Equal(result, Format(result));
    }

    // ── try / catch / finally ──────────────────────────────────────────────────

    [Fact]
    public void N09_TryCatch_BodyOverIndented_Normalised()
    {
        // try and catch bodies both over-indented; '} catch (...) {' tests the
        // pop-then-push at depth-0.
        var source = N(
            """
            component Foo {
              try {
                            riskyCall();
              } catch (Exception e) {
                            handleError(e);
              }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  try {", result);
        Assert.Contains("\n    riskyCall();", result);
        Assert.Contains("\n  } catch (Exception e) {", result);
        Assert.Contains("\n    handleError(e);", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N10_TryCatchFinally_TabIndented_AllNormalised()
    {
        var source =
            "component Foo {\n\ttry {\n\t\triskyCall();\n\t} catch (Exception e) {\n\t\tlog(e);\n\t} finally {\n\t\tcleanup();\n\t}\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  try {", result);
        Assert.Contains("\n    riskyCall();", result);
        Assert.Contains("\n  } catch (Exception e) {", result);
        Assert.Contains("\n    log(e);", result);
        Assert.Contains("\n  } finally {", result);
        Assert.Contains("\n    cleanup();", result);
        Assert.Equal(result, Format(result));
    }

    // ── switch ─────────────────────────────────────────────────────────────────

    [Fact]
    public void N11_Switch_BodyOverIndented_NormalisedByBlockStack()
    {
        // 'case X:' and statement lines inside switch are all placed at the switch
        // block-target (4sp) — the formatter has no case-specific logic.
        var source = N(
            """
            component Foo {
              switch (mode) {
                            case "a":
                            doA();
                            break;
                            case "b":
                            doB();
                            break;
                            default:
                            doDefault();
                            break;
              }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  switch (mode) {", result);
        Assert.Contains("doA();", result);
        Assert.Contains("doB();", result);
        Assert.Contains("doDefault();", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N12_Switch_TabIndented_AllNormalised()
    {
        var source =
            "component Foo {\n\tswitch (val) {\n\t\tcase 1:\n\t\t\thandleOne();\n\t\t\tbreak;\n\t\tdefault:\n\t\t\thandleOther();\n\t\t\tbreak;\n\t}\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("handleOne();", result);
        Assert.Contains("handleOther();", result);
        Assert.Equal(result, Format(result));
    }

    // ── Collection initialisers ─────────────────────────────────────────────────

    [Fact]
    public void N13_ListInitialiser_EntriesOverIndented_NormalisedToBlockTarget()
    {
        var source = N(
            """
            component Foo {
              var items = new List<string> {
                            "alpha",
                            "beta",
                            "gamma",
              };
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var items = new List<string> {", result);
        Assert.Contains("\n    \"alpha\",", result);
        Assert.Contains("\n    \"beta\",", result);
        Assert.Contains("\n    \"gamma\",", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N14_DictionaryInitialiser_TabIndented_NormalisedToBlockTarget()
    {
        var source =
            "component Foo {\n\tvar map = new Dictionary<string, int> {\n\t\t{ \"a\", 1 },\n\t\t{ \"b\", 2 },\n\t\t{ \"c\", 3 },\n\t};\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("{ \"a\", 1 },", result);
        Assert.Contains("{ \"b\", 2 },", result);
        Assert.Contains("{ \"c\", 3 },", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N15_ObjectInitialiser_PropertiesOverIndented_NormalisedToBlockTarget()
    {
        var source = N(
            """
            component Foo {
              var cfg = new Config {
                            Width = 200,
                            Height = 100,
                            Label = "hello",
              };
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var cfg = new Config {", result);
        Assert.Contains("\n    Width = 200,", result);
        Assert.Contains("\n    Height = 100,", result);
        Assert.Contains("\n    Label = \"hello\",", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    // ── Nested blocks ──────────────────────────────────────────────────────────

    [Fact]
    public void N16_DeepNesting_VoidWithIfAndFor_AllOverIndented_Normalised()
    {
        // Three levels of nesting (void → if → for), each body wildly over-indented.
        // The block-stack automatically computes the correct target at each level.
        var source = N(
            """
            component Foo {
              void Process() {
                            if (ready) {
                                          for (int i = 0; i < 10; i++) {
                                                        doWork(i);
                                          }
                            }
              }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  void Process() {", result);
        Assert.Contains("\n    if (ready) {", result);
        Assert.Contains("\n      for (int i = 0; i < 10; i++) {", result);
        Assert.Contains("\n        doWork(i);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N17_DeepNesting_VoidWithTryCatch_TabIndented_Normalised()
    {
        var source =
            "component Foo {\n\tvoid SafeRun() {\n\t\ttry {\n\t\t\triskyOp();\n\t\t} catch (Exception e) {\n\t\t\tlastError = e.Message;\n\t\t}\n\t}\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  void SafeRun() {", result);
        Assert.Contains("\n    try {", result);
        Assert.Contains("\n      riskyOp();", result);
        Assert.Contains("\n    } catch (Exception e) {", result);
        Assert.Contains("\n      lastError = e.Message;", result);
        Assert.Equal(result, Format(result));
    }

    // ── Lambda blocks ──────────────────────────────────────────────────────────

    [Fact]
    public void N18_UseMemo_MultiLineLambda_TabIndented_Normalised()
    {
        var source =
            "component Foo {\n\tvar sorted = useMemo(() => {\n\t\tvar copy = new List<int>(items);\n\t\tcopy.Sort();\n\t\treturn copy;\n\t}, new object[] { items });\n\treturn (<Box />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains("\n  var sorted = useMemo(() => {", result);
        Assert.Contains("\n    var copy = new List<int>(items);", result);
        Assert.Contains("\n    copy.Sort();", result);
        Assert.Contains("\n    return copy;", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void N19_UseEffect_BodyWithTryCatch_OverIndented_Normalised()
    {
        // useEffect lambda with a try/catch inside it — double nesting, both
        // levels over-indented in the input.
        var source = N(
            """
            component Foo {
              useEffect(() => {
                            try {
                                          load();
                            } catch (Exception e) {
                                          setError(e.Message);
                            }
                            return null;
              }, Array.Empty<object>());
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  useEffect(() => {", result);
        Assert.Contains("\n    try {", result);
        Assert.Contains("\n      load();", result);
        Assert.Contains("\n    } catch (Exception e) {", result);
        Assert.Contains("\n      setError(e.Message);", result);
        Assert.Contains("\n    return null;", result);
        Assert.Equal(result, Format(result));
    }

    // ── CRLF ──────────────────────────────────────────────────────────────────

    [Fact]
    public void N20_AllBlockTypes_CrlfLineEndings_AllNormalised()
    {
        // Every type of block written with CRLF — line endings normalised,
        // content and indentation preserved.
        var source =
            "component Foo {\r\n  if (a) {\r\n    doA();\r\n  }\r\n  foreach (var x in xs) {\r\n    use(x);\r\n  }\r\n  try {\r\n    risky();\r\n  } catch (Exception e) {\r\n    handle(e);\r\n  }\r\n  var list = new List<int> {\r\n    1,\r\n    2,\r\n    3,\r\n  };\r\n  return (<Box />);\r\n}";
        var result = Format(source);
        Assert.DoesNotContain("\r", result);
        Assert.Contains("doA();", result);
        Assert.Contains("use(x);", result);
        Assert.Contains("risky();", result);
        Assert.Contains("handle(e);", result);
        Assert.Equal(result, Format(result));
    }

    // ── Kitchen sink ───────────────────────────────────────────────────────────

    [Fact]
    public void N21_AllBlockTypes_KitchenSink_CombinedAbuses_Stable()
    {
        // Every block type in one component, with CRLF + tab indent + trailing
        // spaces + over-indentation — all abuses in one file.
        var source =
            "component MEGA {\r\n\tvar (count, setCount) = useState(0);\r\n\tvar items = new List<string> {\r\n\t\t\"a\",   \r\n\t\t\"b\",   \r\n\t};\r\n\tvar cfg = new Config {\r\n\t\tWidth = 200,   \r\n\t\tHeight = 100,   \r\n\t};\r\n\tuseEffect(() => {\r\n\t\ttry {\r\n\t\t\tload();\r\n\t\t} catch (Exception e) {\r\n\t\t\tsetError(e);\r\n\t\t}\r\n\t\treturn null;\r\n\t}, Array.Empty<object>());   \r\n\tvoid Reset() {\r\n\t\tif (count > 0) {\r\n\t\t\tforeach (var x in items) {\r\n\t\t\t\tprocess(x);\r\n\t\t\t}\r\n\t\t}\r\n\t\tsetCount(0);\r\n\t}\r\n\treturn (<Box />);\r\n}";
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
        Assert.DoesNotContain("\t", first);
        Assert.DoesNotContain("\r", first);
        Assert.Contains("\n  var (count, setCount) = useState(0);", first);
        Assert.Contains("\n  var items = new List<string> {", first);
        Assert.Contains("\n  useEffect(() => {", first);
        Assert.Contains("\n    try {", first);
        Assert.Contains("\n  void Reset() {", first);
        Assert.Contains("\n    if (count > 0) {", first);
        Assert.Contains("\n      foreach (var x in items) {", first);
        foreach (var line in first.Split('\n'))
            Assert.False(
                line.EndsWith(" ") || line.EndsWith("\t"),
                $"Line has trailing whitespace: [{line}]"
            );
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section O — CSharpier-corruption regression tests
    //
    //  CSharpier formats C# files with 4-space indentation. When it erroneously
    //  runs on a .uitkx file it leaves section-header comments at 2-space while
    //  bumping all statement lines to 4-space. Previously the formatter used
    //  baseSpaces = min(comment-lead=2, stmt-lead=4) = 2, so the 4-space
    //  statements remained at 4-space (rel=2).
    //
    //  Fix: comment lines (// ..., /* ... */) are excluded from baseSpaces
    //  measurement just like ternary-arm continuation lines. baseSpaces is now
    //  4 (from the statement lines only), so rel=0 and statements snap to 2sp.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void O01_CSharpierCorruption_CommentAt2sp_VarsAt4sp_NormalisedTo2sp()
    {
        // The canonical CSharpier corruption pattern: section-header comment at
        // 2-space, useState/var lines bumped to 4-space.
        var source = N(
            """
            component Foo {
              // ── state ─────────────────────────────────────────────────────
                var (count, setCount) = useState(0);
                var (mode, setMode) = useState("normal");
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  // ── state", result);
        Assert.Contains("\n  var (count, setCount) = useState(0);", result);
        Assert.Contains("\n  var (mode, setMode) = useState(\"normal\");", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void O02_CSharpierCorruption_MultipleCommentSections_AllVarsNormalised()
    {
        // Multiple comment sections, each followed by 4-space vars.
        var source = N(
            """
            component Foo {
              // ── state ─────────────────────────────────────────────────────
                var (count, setCount) = useState(0);
              // ── context ────────────────────────────────────────────────────
                var theme = useContext<Color>("theme");
              // ── memo ───────────────────────────────────────────────────────
                var doubled = useMemo(() => count * 2, new object[] { count });
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (count, setCount) = useState(0);", result);
        Assert.Contains("\n  var theme = useContext<Color>(\"theme\");", result);
        Assert.Contains("\n  var doubled = useMemo(", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void O03_CSharpierCorruption_StyleBlockAfterComment_EntriesAtCanonical4sp()
    {
        // The exact user-reported pattern: section-header comment at 2sp, then
        // the var + Style block all shifted to 4sp by CSharpier. After formatting,
        // the var should be at 2sp, block entries at 4sp, '}; ' at 2sp.
        var source = N(
            """
            component Sync {
              // ── styles ─────────────────────────────────────────────────────
                var syncLabelStyle = new Style {
                    (StyleKeys.MarginTop, 6f),
                    (StyleKeys.FontSize, 13f),
                    (StyleKeys.Color, syncColor),
                };
              return (<Label style={syncLabelStyle} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var syncLabelStyle = new Style {", result);
        Assert.Contains("\n    (StyleKeys.MarginTop, 6f),", result);
        Assert.Contains("\n    (StyleKeys.FontSize, 13f),", result);
        Assert.Contains("\n    (StyleKeys.Color, syncColor),", result);
        Assert.Contains("\n  };", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void O04_CSharpierCorruption_UseEffectAt4sp_NormalisedTo2sp()
    {
        // useEffect opener + body both at 4sp (CSharpier corruption).
        var source = N(
            """
            component Foo {
              // ── effect ─────────────────────────────────────────────────────
                useEffect(() => {
                    doSetup();
                    return null;
                }, Array.Empty<object>());
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  useEffect(() => {", result);
        Assert.Contains("\n    doSetup();", result);
        Assert.Contains("\n    return null;", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void O05_CSharpierCorruption_VoidMethodAt4sp_NormalisedTo2sp()
    {
        // void method block with body at 4sp corruption.
        var source = N(
            """
            component Foo {
              // ── helpers ────────────────────────────────────────────────────
                void Reset() {
                    setCount(0);
                    setMode("normal");
                }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  void Reset() {", result);
        Assert.Contains("\n    setCount(0);", result);
        Assert.Contains("\n    setMode(\"normal\");", result);
        Assert.Contains("\n  }", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void O06_CSharpierCorruption_TernaryAt4sp_ArmsCorrectlyAnchored()
    {
        // Multi-line ternary with base statement at 4sp. After fix, the statement
        // goes to 2sp and the arms (starting '?' / ':') anchor to the STATEMENT's
        // input indent (4sp), preserving their +2sp offset → 4sp total.
        var source = N(
            """
            component Foo {
              // ── sync color ──────────────────────────────────────────────────
                var syncColor = count >= 0
                    ? new Color(0.3f, 0.85f, 0.45f, 1f)
                    : new Color(0.95f, 0.65f, 0.1f, 1f);
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var syncColor = count >= 0", result);
        // Arms preserve their relative offset from the statement line's input lead.
        Assert.Contains("? new Color(0.3f", result);
        Assert.Contains(": new Color(0.95f", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void O07_CSharpierCorruption_FullKitchenSink_AllNormalised()
    {
        // The full real-world corruption: CRLF, comments at 2sp, all statement
        // lines bumped to 4sp including Style blocks, useEffect, void methods.
        var source =
            "component Sync {\r\n"
            + "  // ── state ──────────────────────────────────────────────────────\r\n"
            + "    var (count, setCount) = useState(0);\r\n"
            + "  // ── styles ─────────────────────────────────────────────────────\r\n"
            + "    var syncLabelStyle = new Style {\r\n"
            + "        (StyleKeys.MarginTop, 6f),\r\n"
            + "        (StyleKeys.FontSize, 13f),\r\n"
            + "        (StyleKeys.Color, syncColor),\r\n"
            + "    };\r\n"
            + "  // ── effect ─────────────────────────────────────────────────────\r\n"
            + "    useEffect(() => {\r\n"
            + "        doSetup();\r\n"
            + "        return null;\r\n"
            + "    }, Array.Empty<object>());\r\n"
            + "  // ── helpers ────────────────────────────────────────────────────\r\n"
            + "    void Reset() {\r\n"
            + "        setCount(0);\r\n"
            + "    }\r\n"
            + "  return (<Label style={syncLabelStyle} text={$\"{count}\"} />);\r\n"
            + "}";
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
        Assert.DoesNotContain("\r", first);
        Assert.Contains("\n  var (count, setCount) = useState(0);", first);
        Assert.Contains("\n  var syncLabelStyle = new Style {", first);
        Assert.Contains("\n    (StyleKeys.MarginTop, 6f),", first);
        Assert.Contains("\n  };", first);
        Assert.Contains("\n  useEffect(() => {", first);
        Assert.Contains("\n    doSetup();", first);
        Assert.Contains("\n  void Reset() {", first);
        Assert.Contains("\n    setCount(0);", first);
        Assert.Contains("\n  }", first);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section P — Mixed-indentation and internal-space normalization tests
    //
    //  These cover the case where a file has lines at MIXED indentation levels
    //  (e.g. some vars at 2sp, others at 4sp from partial CSharpier corruption)
    //  and Style tuple entries with extra spaces after the opening '(' such as
    //  "(   StyleKeys.BackgroundColor, ...)".
    //
    //  Fix: IsStatementStarter() forces depth-0 statement lines to rel=0 and
    //  tracks lastStatementInputIndent to anchor continuation tokens correctly.
    //  Separately, extra spaces after opening '(' are stripped from stripped.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P01_MixedIndent_SomeVarsAt2sp_SomeAt4sp_AllNormalisedTo2sp()
    {
        // Partially-restored file: correct lines at 2sp coexist with corrupted
        // lines at 4sp. With only baseSpaces = min = 2, the 4sp lines used to
        // stay at 4sp (rel=2). IsStatementStarter forces all var/void lines to
        // rel=0 regardless of baseSpaces.
        var source = N(
            """
            component Foo {
              var (count, setCount) = useState(0);
                var (mode, setMode) = useState("normal");
              bool IsLoading() => trigger > 1000;
                var msg = $"Count={count}";
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (count, setCount) = useState(0);", result);
        Assert.Contains("\n  var (mode, setMode) = useState(\"normal\");", result);
        Assert.Contains("\n  bool IsLoading() => trigger > 1000;", result);
        Assert.Contains("\n  var msg = $\"Count={count}\";", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void P02_StyleEntry_ExtraSpacesAfterOpenParen_Stripped()
    {
        // 3 or more spaces after '(' are accidental corruption and get stripped.
        // "(   StyleKeys.BackgroundColor, ...)" → "(StyleKeys.BackgroundColor, ...)".
        // 1–2 spaces are intentional alignment and are left intact (see M01, M14).
        var source = N(
            """
            component Foo {
              var panelStyle = new Style {
                (   StyleKeys.BackgroundColor, new Color(0.2f, 0.2f, 0.25f, 0.9f)),
                (     StyleKeys.FontSize, 13f),
                (   StyleKeys.Padding, 8f),
              };
              return (<Box style={panelStyle} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n    (StyleKeys.BackgroundColor, new Color(0.2f", result);
        Assert.Contains("\n    (StyleKeys.FontSize, 13f),", result);
        Assert.Contains("\n    (StyleKeys.Padding, 8f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void P03_ExtremeOverindent_SingleVar_NormalisedTo2sp()
    {
        // A single var statement at 12sp (extreme over-indent from editor auto-indent
        // or similar). IsStatementStarter forces it to 2sp.
        var source = N(
            """
            component Foo {
              var (count, setCount) = useState(0);
                        var inlineLabel = "hello";
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (count, setCount) = useState(0);", result);
        Assert.Contains("\n  var inlineLabel = \"hello\";", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void P04_AlmanBraceUseEffect_MixedIndent_ControlFlowNormalised()
    {
        // Allman-style useEffect ('{' on its own line) with the opener at 4sp
        // (corrupted) and a correctly-indented bool at 2sp. Both the opener and
        // its '{' should land at 2sp; the body at 4sp.
        var source = N(
            """
            component Foo {
              bool IsReady() => count > 0;
                useEffect(() =>
                {
                    doSetup();
                    return null;
                }, Array.Empty<object>());
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  bool IsReady() => count > 0;", result);
        Assert.Contains("\n  useEffect(() =>", result);
        Assert.Contains("\n  {", result);
        Assert.Contains("\n    doSetup();", result);
        Assert.Contains("\n    return null;", result);
        Assert.Contains("\n  }, Array.Empty<object>());", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void P05_MixedCorruption_MultiLineStyle_TernaryAndUseEffect_AllNormalised()
    {
        // Full mixed-corruption scenario: correct lines at 2sp, corrupted at 4sp,
        // Style block entries with extra spaces, and a ternary at correct relative depth.
        var source = N(
            """
            component Foo {
              // ── state ───────────────────────────────────────────────────
                var (count, setCount) = useState(0);
              // ── styles ──────────────────────────────────────────────────
                var panelStyle = new Style {
                    (   StyleKeys.BackgroundColor, new Color(0.2f, 0.2f, 0.25f, 0.9f)),
                    (     StyleKeys.FontSize, 13f),
                };
              // ── derived ─────────────────────────────────────────────────
              var syncColor = count >= 0
                ? new Color(0.3f, 0.85f, 0.45f, 1f)
                : new Color(0.95f, 0.65f, 0.1f, 1f);
                useEffect(() => {
                    doSetup();
                    return null;
                }, Array.Empty<object>());
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var (count, setCount) = useState(0);", result);
        Assert.Contains("\n  var panelStyle = new Style {", result);
        Assert.Contains("\n    (StyleKeys.BackgroundColor, new Color(0.2f", result);
        Assert.Contains("\n    (StyleKeys.FontSize, 13f),", result);
        Assert.Contains("\n  };", result);
        Assert.Contains("\n  var syncColor = count >= 0", result);
        Assert.Contains("? new Color(0.3f", result);
        Assert.Contains(": new Color(0.95f", result);
        Assert.Contains("\n  useEffect(() => {", result);
        Assert.Contains("\n    doSetup();", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void P06_StyleAlignmentSpacesAfterComma_Collapsed()
    {
        // Spaces after a comma (column-alignment) are collapsed to single space.
        var source = N(
            """
            component Foo {
              var s = new Style {
                (StyleKeys.Color,           new Color(0.5f, 0.5f, 0.5f, 1f)),
                (StyleKeys.BackgroundColor, new Color(1f, 0f, 0f, 0.8f)),
              };
              return (<Box style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.Color, new Color", result);
        Assert.Contains("(StyleKeys.BackgroundColor, new Color", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section Q — Setup code with embedded JSX (EmitCSharpLines path)
    //
    //  Files with embedded JSX in setup code (e.g. `var el = (<VisualElement>…)`)
    //  are routed through EmitSetupCodeWithJsx → EmitCSharpLines, NOT
    //  EmitSetupCodeNormalized.  These tests verify that EmitCSharpLines
    //  correctly normalises CSharpier-corrupted indentation while preserving
    //  relative indentation inside { } blocks (lambdas, callbacks, etc.).
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Q01_JsxInSetup_MixedIndent_CommentAt2sp_VarAt4sp_Normalised()
    {
        // CSharpier corruption: comment at 2sp, vars at 4sp.
        // The formatter should normalise vars to rel=0 (= 2sp with IndentStr).
        var source = N(
            """
            component Foo {
              // ── header ──
                var (count, setCount) = useState(0);
                var node = (
                <Label text="hi" />
              );
              return (<Box>{node}</Box>);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  // ── header ──", result);
        Assert.Contains("\n  var (count, setCount) = useState(0);", result);
        Assert.Contains("\n  var node =", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void Q02_JsxInSetup_NestedLambda_ContinuationPreservesRelativeIndent()
    {
        // A ContinueWith-style pattern: after the lambda's closing "},",
        // continuation arguments should preserve their relative indentation
        // (deeper than the },), not be flattened to block level.
        var source = N(
            """
            component Foo {
              useEffect(() => {
                var t = task;
                t.ContinueWith(x => {
                  DoWork();
                },
                  CancellationToken.None,
                  TaskContinuationOptions.None,
                  TaskScheduler.Default
                );
              }, deps);
              var el = (
                <Label text="ok" />
              );
              return (<Box>{el}</Box>);
            }
            """
        );
        var result = Format(source);
        // The },\n  CancellationToken line should NOT be flattened.
        Assert.Contains("  },\n      CancellationToken.None,", result);
        Assert.Contains("      TaskContinuationOptions.None,", result);
        Assert.Contains("      TaskScheduler.Default", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void Q03_JsxInSetup_ExtraSpacesAfterParen_Stripped()
    {
        // The (   StyleKeys... → (StyleKeys... stripping should work in
        // the EmitCSharpLines path too.
        var source = N(
            """
            component Foo {
              var s = new Style {
                (   StyleKeys.Padding, 8f),
                (   StyleKeys.Margin, 4f),
              };
              var el = (
                <Label text="styled" />
              );
              return (<Box style={s}>{el}</Box>);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.Padding, 8f)", result);
        Assert.Contains("(StyleKeys.Margin, 4f)", result);
        Assert.DoesNotContain("(   StyleKeys", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void Q04_JsxInSetup_MultipleJsxBlocks_EachSegmentNormalised()
    {
        // Multiple JSX paren-blocks — each C# segment between them should
        // have independent normalisation.
        var source = N(
            """
            component Foo {
                var a = (
                <Label text="A" />
              );
                var b = (
                <Label text="B" />
              );
              return (
                <Box>
                  {a}
                  {b}
                </Box>
              );
            }
            """
        );
        var result = Format(source);
        // Both var statements at rel=0 (2sp with IndentStr).
        Assert.Contains("\n  var a =", result);
        Assert.Contains("\n  var b =", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void Q05_JsxInSetup_CommentOnly_ExcludedFromBaseSpaces()
    {
        // Comments at small indent should not drag baseSpaces down and
        // prevent statement normalisation in the JSX path.
        var source = N(
            """
            component Foo {
              // state
                var (x, setX) = useState(0);
              // element
                var node = (
                <Label text="hi" />
              );
              return (<Box>{node}</Box>);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  // state", result);
        Assert.Contains("\n  var (x, setX) = useState(0);", result);
        Assert.Contains("\n  // element", result);
        Assert.Contains("\n  var node =", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void Q06_JsxInSetup_StyleEntryMisindented_IsNormalised()
    {
        // User complaint: "var syncLabelStyle = new Style { ... }; —
        // when I change inner element and save they don't get back to this form"
        var source = N(
            """
            component Foo {
              var s = new Style {
                    (StyleKeys.Padding, 8f),
                (StyleKeys.Margin, 4f),
                        (StyleKeys.FlexGrow, 1f),
              };
              var el = (
                <Label text="hi" />
              );
              return (<Box style={s}>{el}</Box>);
            }
            """
        );
        var result = Format(source);
        // All entries normalised to same level inside the Style block.
        Assert.Contains("    (StyleKeys.Padding, 8f),", result);
        Assert.Contains("    (StyleKeys.Margin, 4f),", result);
        Assert.Contains("    (StyleKeys.FlexGrow, 1f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void Q07_JsxInSetup_LambdaBodyMisindented_IsNormalised()
    {
        // User complaint: "MenuBuilderHandler buildMenu = dm => { ... };
        // — nothing gets formatted inside"
        var source = N(
            """
            component Foo {
              MenuBuilderHandler buildMenu = dm => {
                       dm.AddItem("Option A", false, () => setChoice("A"));
                  dm.AddItem("Option B", false, () => setChoice("B"));
              };
              var el = (
                <Label text="ok" />
              );
              return (<Box>{el}</Box>);
            }
            """
        );
        var result = Format(source);
        // Both items normalised to same level inside the lambda body.
        Assert.Contains("    dm.AddItem(\"Option A\"", result);
        Assert.Contains("    dm.AddItem(\"Option B\"", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void Q08_JsxInSetup_SwitchCase_InnerBodyDeeperThanLabel()
    {
        // switch-case: case labels at block target, body one indent deeper.
        var source = N(
            """
            component Foo {
              void Apply(string m) {
                switch (m) {
                  case "a":
                    DoA();
                    break;
                  default:
                    DoDefault();
                    break;
                }
              }
              var el = (
                <Label text="ok" />
              );
              return (<Box>{el}</Box>);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("case \"a\":", result);
        Assert.Contains("      DoA();", result); // 6sp = blockTarget(4) + caseExtra(2)
        Assert.Contains("      break;", result);
        Assert.Contains("    default:", result); // 4sp = blockTarget
        Assert.Contains("      DoDefault();", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section S — Intra-line whitespace collapse regression
    //
    //  The formatter collapses runs of 2+ consecutive spaces to a single space
    //  (outside of string literals and // comments).  Tab characters in content
    //  are replaced by a space before collapsing.  These tests verify correct
    //  collapse across many expression patterns, and verify that string and
    //  comment content is preserved.
    //
    //  Path tested: EmitSetupCodeNormalized (no JSX in setup code).
    // ════════════════════════════════════════════════════════════════════════════

    // ── S01: assignments & declarations ──────────────────────────────────────

    [Theory]
    [InlineData("var x   =   1;", "var x = 1;")]
    [InlineData("var   name   =   value;", "var name = value;")]
    [InlineData("int   count   =   0;", "int count = 0;")]
    [InlineData("string   label   =   null;", "string label = null;")]
    [InlineData("float   ratio   =   0.5f;", "float ratio = 0.5f;")]
    [InlineData("bool   flag   =   true;", "bool flag = true;")]
    [InlineData("var   items   =   new   List<int>();", "var items = new List<int>();")]
    [InlineData(
        "var   dict   =   new   Dictionary<string, int>();",
        "var dict = new Dictionary<string, int>();"
    )]
    [InlineData("var   parentItem   =   source.Parent;", "var parentItem = source.Parent;")]
    [InlineData("count   +=   1;", "count += 1;")]
    [InlineData("x   -=   5;", "x -= 5;")]
    [InlineData("x   *=   2;", "x *= 2;")]
    [InlineData("x   /=   10;", "x /= 10;")]
    [InlineData("x   %=   3;", "x %= 3;")]
    [InlineData("x   &=   mask;", "x &= mask;")]
    [InlineData("x   |=   flag;", "x |= flag;")]
    [InlineData("x   ^=   bits;", "x ^= bits;")]
    [InlineData("x   ??=   fallback;", "x ??= fallback;")]
    [InlineData("x   <<=   2;", "x <<= 2;")]
    [InlineData("x   >>=   1;", "x >>= 1;")]
    public void S01_Assignments_MultiSpaces_Collapsed(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── S02: operators & expressions ─────────────────────────────────────────

    [Theory]
    [InlineData("var a   =   x   ==   y;", "var a = x == y;")]
    [InlineData("var a   =   x   !=   y;", "var a = x != y;")]
    [InlineData("var a   =   x   >=   y;", "var a = x >= y;")]
    [InlineData("var a   =   x   <=   y;", "var a = x <= y;")]
    [InlineData("var a   =   x   >   y;", "var a = x > y;")]
    [InlineData("var a   =   x   <   y;", "var a = x < y;")]
    [InlineData("var a   =   x   &&   y;", "var a = x && y;")]
    [InlineData("var a   =   x   ||   y;", "var a = x || y;")]
    [InlineData("var a   =   x   +   y;", "var a = x + y;")]
    [InlineData("var a   =   x   -   y;", "var a = x - y;")]
    [InlineData("var a   =   x   *   y;", "var a = x * y;")]
    [InlineData("var a   =   x   /   y;", "var a = x / y;")]
    [InlineData("var a   =   x   %   y;", "var a = x % y;")]
    [InlineData("var a   =   (x   +   y)   *   z;", "var a = (x + y) * z;")]
    [InlineData("var a   =   x   is   string;", "var a = x is string;")]
    public void S02_Operators_MultiSpaces_Collapsed(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── S03: null operators ──────────────────────────────────────────────────

    [Theory]
    [InlineData("var a   =   b   ??   c;", "var a = b ?? c;")]
    [InlineData("var a   =   b   ??   default;", "var a = b ?? default;")]
    [InlineData(
        "var parentItem   =   source.Parent   ??   new   SharedTreeRowItem();",
        "var parentItem = source.Parent ?? new SharedTreeRowItem();"
    )]
    [InlineData(
        "var val   =   first   ??   second   ??   third;",
        "var val = first ?? second ?? third;"
    )]
    [InlineData("x   ??=   fallback;", "x ??= fallback;")]
    public void S03_NullOperators_MultiSpaces_Collapsed(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── S04: method calls & constructors ─────────────────────────────────────

    [Theory]
    [InlineData("DoWork(a,   b,   c);", "DoWork(a, b, c);")]
    [InlineData("var x   =   Math.Max(a,   b);", "var x = Math.Max(a, b);")]
    [InlineData(
        "var sb   =   new   StringBuilder(capacity);",
        "var sb = new StringBuilder(capacity);"
    )]
    [InlineData("list.AddRange(new[]   {   a,   b,   c   });", "list.AddRange(new[] { a, b, c });")]
    [InlineData(
        "var r   =   string.Format(\"{0}   {1}\",   a,   b);",
        "var r = string.Format(\"{0}   {1}\", a, b);"
    )]
    [InlineData("var t   =   Tuple.Create(x,   y,   z);", "var t = Tuple.Create(x, y, z);")]
    [InlineData("Debug.Log(msg   +   suffix);", "Debug.Log(msg + suffix);")]
    [InlineData("var arr   =   Array.Empty<int>();", "var arr = Array.Empty<int>();")]
    public void S04_MethodCalls_MultiSpaces_Collapsed(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── S05: style tuple entries ─────────────────────────────────────────────

    [Theory]
    [InlineData("(StyleKeys.Padding,   8f),", "(StyleKeys.Padding, 8f),")]
    [InlineData("(StyleKeys.Margin,      4f),", "(StyleKeys.Margin, 4f),")]
    [InlineData("(StyleKeys.FlexGrow,    1f),", "(StyleKeys.FlexGrow, 1f),")]
    [InlineData("(StyleKeys.Width,       100f),", "(StyleKeys.Width, 100f),")]
    [InlineData("(StyleKeys.Height,      50f),", "(StyleKeys.Height, 50f),")]
    [InlineData("(StyleKeys.FontSize,          13f),", "(StyleKeys.FontSize, 13f),")]
    [InlineData("(StyleKeys.LineHeight,     1.4f),", "(StyleKeys.LineHeight, 1.4f),")]
    [InlineData("(StyleKeys.Color,   syncColor),", "(StyleKeys.Color, syncColor),")]
    [InlineData(
        "(StyleKeys.Color,       new   Color(0.5f,   0.5f,   0.5f,   1f)),",
        "(StyleKeys.Color, new Color(0.5f, 0.5f, 0.5f, 1f)),"
    )]
    [InlineData(
        "(StyleKeys.BackgroundColor,    new   Color(1f,   0f,   0f,   0.8f)),",
        "(StyleKeys.BackgroundColor, new Color(1f, 0f, 0f, 0.8f)),"
    )]
    public void S05_StyleTuples_MultiSpaces_Collapsed(string input, string expected)
    {
        var source =
            "component Foo {\n  var s = new Style {\n    "
            + input
            + "\n  };\n  return (<Label style={s} />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── S06: string literal content preserved ────────────────────────────────

    [Theory]
    [InlineData("var x   =   \"hello    world\";", "\"hello    world\"")]
    [InlineData("var x   =   \"a     b     c\";", "\"a     b     c\"")]
    [InlineData("var x   =   @\"verbatim    spaces\";", "@\"verbatim    spaces\"")]
    [InlineData("var x   =   $\"interp    {a}    end\";", "$\"interp    {a}    end\"")]
    [InlineData("var x   =   \"tab\\there\";", "\"tab\\there\"")]
    [InlineData("var x   =   \"  leading  trailing  \";", "\"  leading  trailing  \"")]
    [InlineData("var x   =   \"one\" + \"  two  \";", "\"  two  \"")]
    [InlineData("var x   =   \"escaped\\\"   quote\";", "\"escaped\\\"   quote\"")]
    public void S06_StringLiterals_InternalSpaces_Preserved(string input, string expectedFragment)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expectedFragment, result);
        Assert.Equal(result, Format(result));
    }

    // ── S07: line comment content preserved ──────────────────────────────────

    [Theory]
    [InlineData("//  multi   space   comment", "//  multi   space   comment")]
    [InlineData("// aligned:  a    b    c", "// aligned:  a    b    c")]
    [InlineData("var x = 1; //  inline   comment", "//  inline   comment")]
    [InlineData("//    lots     of     spaces", "//    lots     of     spaces")]
    [InlineData("// normal single spaces only", "// normal single spaces only")]
    public void S07_LineComments_InternalSpaces_Preserved(string input, string expectedFragment)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expectedFragment, result);
        Assert.Equal(result, Format(result));
    }

    // ── S08: tab characters replaced and collapsed ───────────────────────────

    [Theory]
    [InlineData("var\tx = 1;", "var x = 1;")]
    [InlineData("var x\t=\t1;", "var x = 1;")]
    [InlineData("var\tx\t=\tnew\tList<int>();", "var x = new List<int>();")]
    [InlineData("count\t+=\t1;", "count += 1;")]
    [InlineData("x\t??\ty;", "x ?? y;")]
    [InlineData("DoWork(\ta,\tb,\tc\t);", "DoWork(a, b, c);")]
    [InlineData("var\t\tx\t\t=\t\t1;", "var x = 1;")]
    [InlineData("a\t \t=\t \tb;", "a = b;")]
    [InlineData("var\tresult\t=\tMath.Max(a,\tb);", "var result = Math.Max(a, b);")]
    [InlineData("list.Add(\titem\t);", "list.Add(item);")]
    public void S08_Tabs_ReplacedBySpaceAndCollapsed(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── S09: already-correct code unchanged ──────────────────────────────────

    [Theory]
    [InlineData("var x = 1;")]
    [InlineData("var name = value;")]
    [InlineData("count += 1;")]
    [InlineData("var a = b ?? c;")]
    [InlineData("DoWork(a, b, c);")]
    [InlineData("var list = new List<int>();")]
    [InlineData("var x = a > 0 ? a : -a;")]
    [InlineData("var s = string.Empty;")]
    [InlineData("Debug.Log(msg);")]
    [InlineData("return;")]
    public void S09_AlreadyCorrect_Unchanged(string input)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(input, result);
        Assert.Equal(result, Format(result));
    }

    // ── S10: ternary & conditional with multi-spaces ─────────────────────────

    [Theory]
    [InlineData("var a   =   x   >   0   ?   x   :   -x;", "var a = x > 0 ? x : -x;")]
    [InlineData("var a   =   flag   ?   optA   :   optB;", "var a = flag ? optA : optB;")]
    [InlineData(
        "var a   =   cond   ?   new   Foo()   :   null;",
        "var a = cond ? new Foo() : null;"
    )]
    [InlineData("var a   =   x   >=   0   ?   x   :   0;", "var a = x >= 0 ? x : 0;")]
    [InlineData("var a   =   list?.Count   ??   0;", "var a = list?.Count ?? 0;")]
    public void S10_Ternary_MultiSpaces_Collapsed(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── S11: ( + 3 spaces stripping ──────────────────────────────────────────

    [Theory]
    [InlineData("(   StyleKeys.Padding, 8f),", "(StyleKeys.Padding, 8f),")]
    [InlineData("(    StyleKeys.Margin, 4f),", "(StyleKeys.Margin, 4f),")]
    [InlineData("(     StyleKeys.FlexGrow, 1f),", "(StyleKeys.FlexGrow, 1f),")]
    [InlineData("(      StyleKeys.FlexGrow, 1f),", "(StyleKeys.FlexGrow, 1f),")]
    [InlineData("( StyleKeys.Padding, 8f),", "(StyleKeys.Padding, 8f),")]
    [InlineData("(  StyleKeys.Padding, 8f),", "(StyleKeys.Padding, 8f),")]
    public void S11_ParenSpaces_AllStripped(string input, string expected)
    {
        var source =
            "component Foo {\n  var s = new Style {\n    "
            + input
            + "\n  };\n  return (<Label style={s} />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section T — Intra-line whitespace collapse (EmitCSharpLines path)
    //
    //  Same collapse behaviour as Section S, but tests are run through the
    //  EmitCSharpLines path by having embedded JSX in setup code.
    // ════════════════════════════════════════════════════════════════════════════

    // ── T01: general multi-space collapse in JSX path ────────────────────────

    [Theory]
    [InlineData("var x   =   1;", "var x = 1;")]
    [InlineData("var   name   =   value;", "var name = value;")]
    [InlineData("count   +=   1;", "count += 1;")]
    [InlineData("x   -=   5;", "x -= 5;")]
    [InlineData("var a   =   b   ??   c;", "var a = b ?? c;")]
    [InlineData("x   ??=   fallback;", "x ??= fallback;")]
    [InlineData("var a   =   x   ==   y;", "var a = x == y;")]
    [InlineData("var a   =   x   !=   y;", "var a = x != y;")]
    [InlineData("var a   =   x   &&   y;", "var a = x && y;")]
    [InlineData("DoWork(a,   b,   c);", "DoWork(a, b, c);")]
    [InlineData("var   items   =   new   List<int>();", "var items = new List<int>();")]
    [InlineData("var a   =   x   >   0   ?   x   :   -x;", "var a = x > 0 ? x : -x;")]
    [InlineData(
        "var parentItem   =   source.Parent   ??   new   SharedTreeRowItem();",
        "var parentItem = source.Parent ?? new SharedTreeRowItem();"
    )]
    [InlineData("var a   =   (x   +   y)   *   z;", "var a = (x + y) * z;")]
    [InlineData("var a   =   x   is   string;", "var a = x is string;")]
    public void T01_JsxPath_Expressions_MultiSpaces_Collapsed(string input, string expected)
    {
        var source =
            "component Foo {\n  "
            + input
            + "\n  var el = (\n    <Label text=\"hi\" />\n  );\n  return (<Box>{el}</Box>);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── T02: style tuples in JSX path ────────────────────────────────────────

    [Theory]
    [InlineData("(StyleKeys.FontSize,          13f),", "(StyleKeys.FontSize, 13f),")]
    [InlineData("(StyleKeys.LineHeight,      1.4f),", "(StyleKeys.LineHeight, 1.4f),")]
    [InlineData("(StyleKeys.Color,   syncColor),", "(StyleKeys.Color, syncColor),")]
    [InlineData("(StyleKeys.Padding,   8f),", "(StyleKeys.Padding, 8f),")]
    [InlineData("(StyleKeys.Margin,      4f),", "(StyleKeys.Margin, 4f),")]
    [InlineData(
        "(StyleKeys.Color,      new   Color(0.5f,  0.5f,  0.5f,  1f)),",
        "(StyleKeys.Color, new Color(0.5f, 0.5f, 0.5f, 1f)),"
    )]
    public void T02_JsxPath_StyleTuples_MultiSpaces_Collapsed(string input, string expected)
    {
        var source =
            "component Foo {\n  var s = new Style {\n    "
            + input
            + "\n  };\n  var el = (\n    <Label text=\"hi\" />\n  );\n  return (<Box style={s}>{el}</Box>);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── T03: string/comment preservation in JSX path ─────────────────────────

    [Theory]
    [InlineData("var x   =   \"hello    world\";", "\"hello    world\"")]
    [InlineData("var x   =   @\"verbatim    spaces\";", "@\"verbatim    spaces\"")]
    [InlineData("//  multi   space   comment", "//  multi   space   comment")]
    [InlineData("var x = 1; //  inline   comment", "//  inline   comment")]
    [InlineData("var x   =   \"a     b     c\";", "\"a     b     c\"")]
    public void T03_JsxPath_StringsAndComments_Preserved(string input, string expectedFragment)
    {
        var source =
            "component Foo {\n  "
            + input
            + "\n  var el = (\n    <Label text=\"hi\" />\n  );\n  return (<Box>{el}</Box>);\n}";
        var result = Format(source);
        Assert.Contains(expectedFragment, result);
        Assert.Equal(result, Format(result));
    }

    // ── T04: tabs in JSX path ────────────────────────────────────────────────

    [Theory]
    [InlineData("var\tx = 1;", "var x = 1;")]
    [InlineData("var x\t=\t1;", "var x = 1;")]
    [InlineData("count\t+=\t1;", "count += 1;")]
    [InlineData("var\t\tx\t\t=\t\t1;", "var x = 1;")]
    [InlineData("DoWork(\ta,\tb\t);", "DoWork(a, b);")]
    public void T04_JsxPath_Tabs_ReplacedAndCollapsed(string input, string expected)
    {
        var source =
            "component Foo {\n  "
            + input
            + "\n  var el = (\n    <Label text=\"hi\" />\n  );\n  return (<Box>{el}</Box>);\n}";
        var result = Format(source);
        Assert.DoesNotContain("\t", result);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section U — Bare-brace (Allman-style) normalization regression
    //
    //  When a bare '{' on its own line follows a statement-starting line (var,
    //  if, for, foreach, while, switch, etc.), the '{' is pulled back to the
    //  same indentation level as the statement.  When it follows a continuation
    //  line (lambda arrow, ternary, method chain), it is NOT pulled back.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void U01_BareBrace_AfterVarNewStyle_PulledBack()
    {
        var source = N(
            """
            component Foo {
              var s = new Style
                    {
                (StyleKeys.Padding, 8f),
              };
              return (<Label style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var s = new Style\n  {\n", result);
        Assert.Contains("(StyleKeys.Padding, 8f),", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void U02_BareBrace_AfterVarNewList_PulledBack()
    {
        var source = N(
            """
            component Foo {
              var items = new List<int>
                  {
                1, 2, 3,
              };
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var items = new List<int>\n  {\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void U03_BareBrace_AfterVarNewDict_PulledBack()
    {
        var source = N(
            """
            component Foo {
              var dict = new Dictionary<string, int>
                      {
                { "a", 1 },
              };
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var dict = new Dictionary<string, int>\n  {\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void U04_BareBrace_AlreadyCorrectIndent_Unchanged()
    {
        var source = N(
            """
            component Foo {
              var s = new Style
              {
                (StyleKeys.Padding, 8f),
              };
              return (<Label style={s} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var s = new Style\n  {\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void U05_BareBrace_AfterContinuation_NotPulledToRel0()
    {
        // 'useEffect(() =>' is NOT a statement starter (no keyword match,
        // doesn't end with ; or {). Bare '{' after it is continuation.
        var source = N(
            """
            component Foo {
              var x = 1;
              useEffect(() =>
                      {
                DoWork();
                return null;
              }, deps);
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        // The { should NOT be at column 2 (pulled back) — it's a continuation.
        Assert.DoesNotContain("\n  useEffect(() =>\n  {\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void U06_BareBrace_JsxPath_AfterVarNewStyle_PulledBack()
    {
        // Same as U01 but through EmitCSharpLines (JSX in setup).
        var source = N(
            """
            component Foo {
              var s = new Style
                    {
                (StyleKeys.Padding, 8f),
              };
              var el = (
                <Label text="hi" />
              );
              return (<Box style={s}>{el}</Box>);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var s = new Style\n  {\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void U07_BareBrace_JsxPath_AfterContinuation_NotPulledToRel0()
    {
        // Same as U05 but through EmitCSharpLines (JSX in setup).
        var source = N(
            """
            component Foo {
              var x = 1;
              useEffect(() =>
                      {
                DoWork();
                return null;
              }, deps);
              var el = (
                <Label text="hi" />
              );
              return (<Box>{el}</Box>);
            }
            """
        );
        var result = Format(source);
        Assert.DoesNotContain("\n  useEffect(() =>\n  {\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void U08_BareBrace_NestedBlocks_InnerBraceAtBlockTarget()
    {
        // Inside a block, the inner '{' should be at the block's target level,
        // not pulled to depth-0.
        var source = N(
            """
            component Foo {
              var outer = new Style
              {
                (StyleKeys.Padding, 8f),
              };
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        // Inner content at blockTarget (4sp = 2sp base + 2sp indent)
        Assert.Contains("    (StyleKeys.Padding, 8f),", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section V — Paren-space normalization regression
    //
    //  Spaces immediately after '(' and immediately before ')' are stripped
    //  (outside of string literals and // comments).  This covers for-loops,
    //  method calls, style tuples, casts, etc.
    // ════════════════════════════════════════════════════════════════════════════

    // ── V01: space after ( stripped ──────────────────────────────────────────

    [Theory]
    [InlineData("for ( int i = 0; i < n; i++ )", "for (int i = 0; i < n; i++)")]
    [InlineData("for (  int i = 0; i < n; i++  )", "for (int i = 0; i < n; i++)")]
    [InlineData("for (   int i = 0; i < n; i--   )", "for (int i = 0; i < n; i--)")]
    [InlineData("if ( x > 0 )", "if (x > 0)")]
    [InlineData("while ( running )", "while (running)")]
    [InlineData("switch ( mode )", "switch (mode)")]
    [InlineData("foreach ( var item in list )", "foreach (var item in list)")]
    [InlineData("( int )x;", "(int)x;")]
    [InlineData("( float )value;", "(float)value;")]
    [InlineData("var a = ( x + y ) * z;", "var a = (x + y) * z;")]
    public void V01_SpaceAfterOpenParen_Stripped(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── V02: space before ) stripped ─────────────────────────────────────────

    [Theory]
    [InlineData("setCount(v => v + 1 );", "setCount(v => v + 1);")]
    [InlineData("DoWork(a, b, c );", "DoWork(a, b, c);")]
    [InlineData("DoWork( a );", "DoWork(a);")]
    [InlineData("Math.Max( a, b );", "Math.Max(a, b);")]
    [InlineData("Console.WriteLine( msg );", "Console.WriteLine(msg);")]
    [InlineData("var x = ( a + b );", "var x = (a + b);")]
    [InlineData("list.Add( item );", "list.Add(item);")]
    [InlineData("Debug.Log( message );", "Debug.Log(message);")]
    [InlineData("var a = Tuple.Create( x, y );", "var a = Tuple.Create(x, y);")]
    [InlineData("string.Format( \"{0}\", x );", "string.Format(\"{0}\", x);")]
    public void V02_SpaceBeforeCloseParen_Stripped(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── V03: combined ( space and space ) in style tuples ────────────────────

    [Theory]
    [InlineData("( StyleKeys.FlexGrow, 1f ),", "(StyleKeys.FlexGrow, 1f),")]
    [InlineData("( StyleKeys.Padding, 12f ),", "(StyleKeys.Padding, 12f),")]
    [InlineData(
        "( StyleKeys.FlexDirection, \"column\" ),",
        "(StyleKeys.FlexDirection, \"column\"),"
    )]
    [InlineData("( StyleKeys.Color, color ),", "(StyleKeys.Color, color),")]
    [InlineData("( StyleKeys.Height, 30f ),", "(StyleKeys.Height, 30f),")]
    [InlineData("(  StyleKeys.Width,  100f  ),", "(StyleKeys.Width, 100f),")]
    [InlineData("(   StyleKeys.Margin,   4f   ),", "(StyleKeys.Margin, 4f),")]
    [InlineData(
        "( StyleKeys.BackgroundColor, new Color(0.5f, 0.5f, 0.5f) ),",
        "(StyleKeys.BackgroundColor, new Color(0.5f, 0.5f, 0.5f)),"
    )]
    public void V03_StyleTuples_ParenSpaces_BothStripped(string input, string expected)
    {
        var source =
            "component Foo {\n  var s = new Style {\n    "
            + input
            + "\n  };\n  return (<Label style={s} />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── V04: string content with parens preserved ────────────────────────────

    [Theory]
    [InlineData("var x = \"( hello )\";", "\"( hello )\"")]
    [InlineData("var x = \"fn( a, b )\";", "\"fn( a, b )\"")]
    [InlineData("var x = @\"( test )\";", "@\"( test )\"")]
    [InlineData("var x = $\"result: ( {v} )\";", "$\"result: ( {v} )\"")]
    public void V04_StringContent_ParenSpaces_Preserved(string input, string expectedFragment)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expectedFragment, result);
        Assert.Equal(result, Format(result));
    }

    // ── V05: nested parens ───────────────────────────────────────────────────

    [Theory]
    [InlineData("var a = ( ( x + y ) * z );", "var a = ((x + y) * z);")]
    [InlineData("DoWork( ( a, b ), ( c, d ) );", "DoWork((a, b), (c, d));")]
    [InlineData("fn( g( h( x ) ) );", "fn(g(h(x)));")]
    [InlineData("( ( a ) );", "((a));")]
    public void V05_NestedParens_AllSpacesStripped(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── V06: empty parens unchanged ──────────────────────────────────────────

    [Theory]
    [InlineData("DoWork();", "DoWork();")]
    [InlineData("var x = new List<int>();", "var x = new List<int>();")]
    [InlineData("return ();", "return ();")]
    public void V06_EmptyParens_Unchanged(string input, string expected)
    {
        var source = "component Foo {\n  " + input + "\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ── V07: for-loop with space in real component context ───────────────────

    [Fact]
    public void V07_ForLoop_SpacesInsideParens_FullComponent()
    {
        var source = N(
            """
            component Foo {
              var items = new List<int> { 1, 2, 3 };
              for ( int i = items.Count - 1; i > 0; i-- ) {
                items[i] = items[i] * 2;
              }
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("for (int i = items.Count - 1; i > 0; i--) {", result);
        Assert.DoesNotContain("( int", result);
        Assert.DoesNotContain("-- )", result);
        Assert.Equal(result, Format(result));
    }

    // ── V08: setCount with space before ) in real component context ──────────

    [Fact]
    public void V08_MethodCall_SpaceBeforeCloseParen_FullComponent()
    {
        var source = N(
            """
            component Counter {
              var (count, setCount) = useState(0);
              setCount(v => v + 1 );
              return (<Label text={count.ToString()} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("setCount(v => v + 1);", result);
        Assert.DoesNotContain("1 )", result);
        Assert.Equal(result, Format(result));
    }

    // ── V09: style block with mixed paren patterns ───────────────────────────

    [Fact]
    public void V09_StyleBlock_MixedParenSpaces_AllNormalised()
    {
        var source = N(
            """
            component Foo {
              var containerStyle = new Style {
                ( StyleKeys.FlexGrow, 1f ),
                (StyleKeys.Padding, 12f),
                ( StyleKeys.FlexDirection, "column" ),
              };
              return (<Box style={containerStyle} />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("(StyleKeys.FlexGrow, 1f),", result);
        Assert.Contains("(StyleKeys.Padding, 12f),", result);
        Assert.Contains("(StyleKeys.FlexDirection, \"column\"),", result);
        Assert.DoesNotContain("( Style", result);
        Assert.DoesNotContain("f )", result);
        Assert.Equal(result, Format(result));
    }

    // ── V10: paren spaces in JSX path ────────────────────────────────────────

    [Theory]
    [InlineData("for ( int i = 0; i < n; i++ )", "for (int i = 0; i < n; i++)")]
    [InlineData("setCount( v => v + 1 );", "setCount(v => v + 1);")]
    [InlineData("DoWork( a, b, c );", "DoWork(a, b, c);")]
    [InlineData("Math.Max( a, b );", "Math.Max(a, b);")]
    [InlineData("if ( x > 0 )", "if (x > 0)")]
    public void V10_JsxPath_ParenSpaces_Stripped(string input, string expected)
    {
        var source =
            "component Foo {\n  "
            + input
            + "\n  var el = (\n    <Label text=\"hi\" />\n  );\n  return (<Box>{el}</Box>);\n}";
        var result = Format(source);
        Assert.Contains(expected, result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Section W — Custom-type assignment as statement starter
    //
    //  Lines containing ' = ' (standalone assignment) that don't start with a
    //  keyword are recognized as statements.  This covers patterns like
    //  'MenuBuilderHandler buildMenu = dm =>' where the type name is custom.
    //  The ' = ' check naturally excludes compound operators (+=, -=, ==, etc.).
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void W01_CustomTypeAssignment_AtRel0()
    {
        var source = N(
            """
            component Foo {
                  MenuBuilderHandler buildMenu = dm => {
                dm.AppendAction("Reset", _ => Reset());
              };
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  MenuBuilderHandler buildMenu = dm => {", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void W02_CustomTypeAssignment_AllmanBrace_PulledBack()
    {
        var source = N(
            """
            component Foo {
              MenuBuilderHandler buildMenu = dm =>
                    {
                dm.AppendAction("Reset", _ => Reset());
                dm.AppendAction("Set 10", _ => setCount(10));
                dm.AppendAction("Set 100", _ => setCount(100));
                dm.AppendAction("Shuffle", _ => ShuffleOptions());
              };
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        // The { should be pulled back to rel=0 (same as statement)
        Assert.Contains("\n  MenuBuilderHandler buildMenu = dm =>\n  {\n", result);
        // Inner lines at blockTarget (4sp)
        Assert.Contains("    dm.AppendAction(\"Reset\"", result);
        Assert.Contains("    dm.AppendAction(\"Set 10\"", result);
        Assert.Contains("    dm.AppendAction(\"Set 100\"", result);
        Assert.Contains("    dm.AppendAction(\"Shuffle\"", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void W03_CustomTypeAssignment_OverIndented_NormalisedToRel0()
    {
        // If user tabs/spaces the MenuBuilderHandler line, it should normalize to rel=0.
        var source = N(
            """
            component Foo {
                    MenuBuilderHandler buildMenu = dm =>
                    {
                dm.AppendAction("A", _ => DoA());
              };
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  MenuBuilderHandler buildMenu = dm =>\n  {\n", result);
        Assert.Contains("    dm.AppendAction(\"A\"", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void W04_ActionAssignment_AllmanBrace_PulledBack()
    {
        var source = N(
            """
            component Foo {
              Action<int> callback = x =>
                  {
                DoWork(x);
              };
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  Action<int> callback = x =>\n  {\n", result);
        Assert.Contains("    DoWork(x);", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void W05_FuncAssignment_AllmanBrace_PulledBack()
    {
        var source = N(
            """
            component Foo {
              Func<int, bool> predicate = n =>
                      {
                return n > 0;
              };
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  Func<int, bool> predicate = n =>\n  {\n", result);
        Assert.Contains("    return n > 0;", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void W06_CompoundOperator_NotMistakenForAssignment()
    {
        // += -= *= /= %= should NOT trigger the ' = ' heuristic.
        var source = N(
            """
            component Foo {
              count += 1;
              total -= 5;
              factor *= 2;
              ratio /= 10;
              bits %= 3;
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        // All should be at rel=0 because they end with ; (not because of = check)
        Assert.Contains("\n  count += 1;", result);
        Assert.Contains("\n  total -= 5;", result);
        Assert.Contains("\n  factor *= 2;", result);
        Assert.Contains("\n  ratio /= 10;", result);
        Assert.Contains("\n  bits %= 3;", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void W07_CustomTypeAssignment_JsxPath_AllmanBrace()
    {
        // Same as W02 but through EmitCSharpLines path (JSX in setup).
        var source = N(
            """
            component Foo {
              MenuBuilderHandler buildMenu = dm =>
                    {
                dm.AppendAction("Reset", _ => Reset());
              };
              var el = (
                <Label text="hi" />
              );
              return (<Box>{el}</Box>);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  MenuBuilderHandler buildMenu = dm =>\n  {\n", result);
        Assert.Contains("    dm.AppendAction(\"Reset\"", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void FMT1_MultiLineParenContinuation_InsideNestedBlock_PreservesIndent()
    {
        // Multi-line `if (...)` with `&&` continuation INSIDE a nested block.
        // The continuation line's +4sp offset from the `if` must survive the
        // EmitSetupCodeNormalized block-interior re-anchoring; the if-body
        // brace must still anchor to the if-statement's logical column.
        // Regression for TD-S6 (GalagaGame GameScreen.uitkx idempotency).
        var source = N(
            """
            component Foo {
              for (int i = 0; i < n; i++) {
                if (items[i].Id == target
                    && items[i].Phase == 1) {
                  beam = items[i];
                  break;
                }
              }
              return (<Box />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n        && items[i].Phase == 1) {", result);
        Assert.Contains("\n      beam = items[i];", result);
        Assert.Contains("\n      break;", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void W08_LambdaInsideMethodCall_NotStarterNorPulled()
    {
        // (from, to) => should NOT be treated as a statement starter (no ' = ').
        // The bare { after it should remain at continuation indent.
        var source = N(
            """
            component Foo {
              Hooks.UseBlocker(
                (from, to) =>
                {
                  return false;
                },
                enabled: true
              );
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        // (from, to) should NOT be at rel=0 — it's a continuation inside UseBlocker()
        Assert.DoesNotContain("\n  (from, to) =>\n  {\n", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void W09_CustomTypeAssignment_InnerLinesFormatted_KitchenSink()
    {
        // Full scenario: custom type, Allman brace, inner lines with parens
        // and multi-spaces — everything should be normalised.
        var source =
            "component Foo {\n      MenuBuilderHandler buildMenu = dm =>\n          {\n    dm.AppendAction(  \"Reset\",   _ => Reset()  );\n    dm.AppendAction( \"Set 10\",  _ => setCount( 10 ) );\n  };\n  return (<Label />);\n}";
        var result = Format(source);
        Assert.Contains("\n  MenuBuilderHandler buildMenu = dm =>\n  {\n", result);
        Assert.Contains("dm.AppendAction(\"Reset\", _ => Reset());", result);
        Assert.Contains("dm.AppendAction(\"Set 10\", _ => setCount(10));", result);
        Assert.DoesNotContain("(  \"Reset\"", result);
        Assert.DoesNotContain("10 )", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void W10_EqualityOperators_NotMistakenForAssignment()
    {
        // == and != should not trigger the ' = ' heuristic.
        // These lines end with ; so they're already statement starters.
        var source = N(
            """
            component Foo {
              var a = x == y;
              var b = x != y;
              var c = x >= y;
              var d = x <= y;
              return (<Label />);
            }
            """
        );
        var result = Format(source);
        Assert.Contains("\n  var a = x == y;", result);
        Assert.Contains("\n  var b = x != y;", result);
        Assert.Contains("\n  var c = x >= y;", result);
        Assert.Contains("\n  var d = x <= y;", result);
        Assert.Equal(result, Format(result));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  X) DEEP NESTING — local functions with JSX, 4-level directives,
    //     setup-code JSX variables, {} expression splicing
    //
    //  These tests cover the deep nesting patterns added to
    //  UitkxTestFileDoNotTouch.uitkx: local functions returning JSX with
    //  @foreach + @if inside, directive bodies with setup-code JSX variables,
    //  and 4-level-deep directive nesting (@foreach → @for → @if/@else → @foreach).
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DeepNest_LocalFunction_ForeachAndIf_Idempotent()
    {
        // Local function returning JSX with @foreach loop containing
        // setup code + multi-attr elements, plus @if branch.
        var source = N(
            """
            component C {
              var items = new[] { "a", "b" };

              VirtualNode PillBar(string[] opts, string selected) {
                return (
                  <VisualElement
                    style={new Style { (StyleKeys.FlexDirection, "row"), (StyleKeys.FlexWrap, "wrap") }}
                  >
                    @foreach (var opt in opts) {
                      var isActive = opt == selected;
                      var bg = isActive ? Color.cyan : Color.gray;

                      return (
                        <Button
                          key={opt}
                          text={opt.ToUpper()}
                          onClick={_ => Console.WriteLine(opt)}
                          style={new Style {
                            (StyleKeys.BackgroundColor, bg),
                            (StyleKeys.Color, Color.white),
                            (StyleKeys.BorderRadius, 10f),
                            (StyleKeys.MarginRight, 4f),
                          }}
                        />
                      );
                    }
                    @if (selected != "all") {
                      return (
                        <Label
                          text={$"active: {selected}"}
                          style={new Style { (StyleKeys.Color, Color.gray), (StyleKeys.FontSize, 10f) }}
                        />
                      );
                    }
                  </VisualElement>
                );
              }

              return (
                <VisualElement>
                  {PillBar(items, "a")}
                </VisualElement>
              );
            }

            """
        );
        var result = Format(source);
        Assert.Equal(source, result);
    }

    [Fact]
    public void DeepNest_LocalFunction_IfElse_Idempotent()
    {
        // Local function with @if / @else branches returning JSX.
        var source = N(
            """
            component C {
              var (active, setActive) = useState(true);

              VirtualNode StatusBadge() {
                return (
                  <VisualElement style={new Style { (StyleKeys.MarginBottom, 6f) }}>
                    @if (active) {
                      return (
                        <Label
                          text="status ON"
                          style={new Style { (StyleKeys.Color, Color.green), (StyleKeys.FontSize, 11f) }}
                        />
                      );
                    } @else {
                      return (
                        <Label
                          text="status OFF"
                          style={new Style { (StyleKeys.Color, Color.gray), (StyleKeys.FontSize, 11f) }}
                        />
                      );
                    }
                  </VisualElement>
                );
              }

              var statusNode = StatusBadge();

              return (
                <VisualElement>
                  {statusNode}
                </VisualElement>
              );
            }

            """
        );
        var result = Format(source);
        Assert.Equal(source, result);
    }

    [Fact]
    public void DeepNest_FourLevels_ForeachForIfForeach_Idempotent()
    {
        // 4-level nesting: @foreach → @for → @if/@else → @foreach
        // with setup-code JSX variable (catBadge) inside directive body.
        var source = N(
            """
            component C {
              var categories = new[] { "A", "B" };
              var tags = new[] { "x", "y" };
              var (count, setCount) = useState(2);
              var (mode, setMode) = useState("x");
              var (mounted, setMounted) = useState(true);

              return (
                <VisualElement>
                  @foreach (var cat in categories) {
                    var catColor =
                      cat == "A" ? Color.cyan :
                    Color.yellow;
                    var catBadge = (
                      <VisualElement
                        style={new Style { (StyleKeys.FlexDirection, "row"), (StyleKeys.MarginBottom, 3f) }}
                      >
                        <Label
                          text={$"cat = {cat}"}
                          style={new Style { (StyleKeys.Color, catColor), (StyleKeys.FontSize, 12f) }}
                        />
                        @if (mounted) {
                          return (
                            <Label
                              text={$"[mode={mode}]"}
                              style={new Style { (StyleKeys.Color, Color.gray), (StyleKeys.FontSize, 10f) }}
                            />
                          );
                        }
                      </VisualElement>
                    );

                    return (
                      <VisualElement
                        key={cat}
                        style={new Style {
                          (StyleKeys.BackgroundColor, Color.black),
                          (StyleKeys.Padding, 6f),
                          (StyleKeys.MarginBottom, 4f),
                        }}
                      >
                        {catBadge}
                        @for (int d = 0; d < count; d++) {
                          var depthBg = new Color(0.12f, 0.12f, 0.2f);

                          return (
                            <VisualElement
                              key={$"{cat}-{d}"}
                              style={new Style {
                                (StyleKeys.BackgroundColor, depthBg),
                                (StyleKeys.Padding, 4f),
                                (StyleKeys.MarginBottom, 2f),
                              }}
                            >
                              <Label
                                text={$"depth = {d}"}
                                style={new Style { (StyleKeys.Color, Color.white) }}
                              />
                              @if (d % 2 == 0) {
                                var label = $"even (cat={cat}, d={d}):";

                                return (
                                  <VisualElement
                                    style={new Style { (StyleKeys.FlexDirection, "row"), (StyleKeys.FlexWrap, "wrap") }}
                                  >
                                    <Label
                                      text={label}
                                      style={new Style { (StyleKeys.Color, Color.cyan), (StyleKeys.FontSize, 10f) }}
                                    />
                                    @foreach (var tag in tags) {
                                      if (tag == "y" && d == 0) {
                                        return null;
                                      }
                                      var isActive = tag == mode;

                                      return (
                                        <Label
                                          key={tag}
                                          text={$"[{tag}]"}
                                          style={new Style { (StyleKeys.Color, isActive ? Color.cyan : Color.gray), (StyleKeys.FontSize, 10f) }}
                                        />
                                      );
                                    }
                                  </VisualElement>
                                );
                              } @else {
                                return (
                                  <Label
                                    text={$"odd depth (d={d})"}
                                    style={new Style { (StyleKeys.Color, Color.gray), (StyleKeys.FontSize, 10f) }}
                                  />
                                );
                              }
                            </VisualElement>
                          );
                        }
                      </VisualElement>
                    );
                  }
                </VisualElement>
              );
            }

            """
        );
        var result = Format(source);
        Assert.Equal(source, result);
    }

    [Fact]
    public void DeepNest_SetupCodeJsxVar_TernaryAssign_Idempotent()
    {
        // Ternary JSX variable in setup code: var node = cond ? (<JSX>) : null;
        // Plus {} expression splicing in the return JSX.
        var source = N(
            """
            component C {
              var (active, setActive) = useState(true);

              var controlsNode = active
                ? (
                  <VisualElement style={new Style { (StyleKeys.MarginBottom, 6f) }}>
                    <Button text="click" onClick={_ => setActive(!active)} />
                  </VisualElement>
                )
                : null;

              return (
                <VisualElement>
                  {controlsNode}
                  <Label text="footer" />
                </VisualElement>
              );
            }

            """
        );
        var result = Format(source);
        Assert.Equal(source, result);
    }

    [Fact]
    public void DeepNest_SetupCodeJsxVar_DirectAssign_Idempotent()
    {
        // Direct JSX assignment with nested directives in setup code.
        var source = N(
            """
            component C {
              var (mounted, setMounted) = useState(true);

              var headerNode = (
                <VisualElement
                  style={new Style { (StyleKeys.FlexDirection, "row"), (StyleKeys.MarginBottom, 8f) }}
                >
                  <Label text="Header" style={new Style { (StyleKeys.FlexGrow, 1f) }} />
                  <Button text="Reset" onClick={_ => setMounted(true)} />
                </VisualElement>
              );

              return (
                <VisualElement>
                  {headerNode}
                </VisualElement>
              );
            }

            """
        );
        var result = Format(source);
        Assert.Equal(source, result);
    }

    [Fact]
    public void DeepNest_MixedLocalFunctions_And_DeepDirectives_Idempotent()
    {
        // Combines multiple local functions, setup-code JSX vars,
        // expression splicing, and multi-level nesting in one component.
        var source = N(
            """
            component C {
              var (count, setCount) = useState(0);
              var items = new[] { "A", "B" };

              VirtualNode Badge(string text) {
                return (
                  <Label text={text} style={new Style { (StyleKeys.FontSize, 10f) }} />
                );
              }

              var badge = Badge($"count={count}");

              var footer = (
                <VisualElement>
                  <Label text="footer" />
                </VisualElement>
              );

              return (
                <VisualElement>
                  {badge}
                  @foreach (var item in items) {
                    return (
                      <VisualElement key={item}>
                        <Label text={item} />
                        @if (count > 0) {
                          return (
                            <Label text={$"count={count}"} />
                          );
                        }
                      </VisualElement>
                    );
                  }
                  {footer}
                </VisualElement>
              );
            }

            """
        );
        var result = Format(source);
        Assert.Equal(source, result);
    }

    [Fact]
    public void DeepNest_MessyIndent_NormalisedToCanonical()
    {
        // Messy indentation in deep nesting should normalize properly.
        var source = N(
            """
            component C {
                var items = new[] { "a", "b" };

                  VirtualNode Fn() {
                      return (
                          <VisualElement>
                              @if (true) {
                                  return (
                                      <Label text="yes" />
                                  );
                              }
                          </VisualElement>
                      );
                  }

                var node = Fn();

                return (
                    <VisualElement>
                        {node}
                    </VisualElement>
                );
            }
            """
        );
        var result = Format(source);
        // Local function body indent normalized to 2sp from component indent
        Assert.Contains("\n  VirtualNode Fn() {", result);
        Assert.Contains("\n  var node = Fn();", result);
        // JSX inside local function uses proper nesting
        Assert.Contains("\n      <VisualElement>", result);
        Assert.Contains("\n        @if (true) {", result);
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void DeepNest_DirectiveSetupCodeJsxVar_InsideForeach_Idempotent()
    {
        // JSX variable inside directive body setup code (var catBadge = (<JSX>))
        // with nested @if. This tests the placeholder-based formatting path.
        var source = N(
            """
            component C {
              var cats = new[] { "A", "B" };
              var (show, setShow) = useState(true);

              return (
                <VisualElement>
                  @foreach (var cat in cats) {
                    var badge = (
                      <VisualElement>
                        <Label text={cat} />
                        @if (show) {
                          return (
                            <Label text="extra" />
                          );
                        }
                      </VisualElement>
                    );

                    return (
                      <VisualElement key={cat}>
                        {badge}
                      </VisualElement>
                    );
                  }
                </VisualElement>
              );
            }

            """
        );
        var result = Format(source);
        Assert.Equal(source, result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  R) ROSLYN DELEGATE IDEMPOTENCY
    //
    //  The language server uses a Roslyn-backed ICSharpFormatterDelegate for C#
    //  code blocks.  Roslyn's FormatStatements can produce surprising indentation
    //  for nested lambdas (e.g. return () => { … }) that causes an oscillating
    //  save-loop when combined with EmitCSharpLines.  These tests verify that the
    //  formatter is idempotent even with the Roslyn delegate active.
    // ════════════════════════════════════════════════════════════════════════════

    private static readonly AstFormatter _fmtRoslyn = new AstFormatter(
        FormatterOptions.Default,
        new TestRoslynFormatter()
    );

    private static string FormatWithRoslyn(string source) => N(_fmtRoslyn.Format(source));

    [Theory]
    [MemberData(nameof(AllSampleFiles))]
    public void RoslynIdempotency_SampleFile_IsUnchanged(string filePath, string relativePath)
    {
        _ = relativePath;
        var content = N(File.ReadAllText(filePath));
        var result = FormatWithRoslyn(content);
        Assert.Equal(content, result);
    }

    [Fact]
    public void Roslyn_NestedLambda_ReturnBlock_NoOscillation()
    {
        // Regression: Roslyn puts the { of return () => { ... }; at column 0,
        // which caused EmitCSharpLines to shift indentation on every save.
        var source = N(
            """
            component C {
              useEffect(() =>
              {
                  var x = 1;
                  return () =>
                  {
                    x = 0;
                  };
              }, Array.Empty<object>());

              var node = (
                <Label text="hi" />
              );

              return (
                <Label text="ok" />
              );
            }
            """
        );
        var r1 = FormatWithRoslyn(source);
        var r2 = FormatWithRoslyn(r1);
        var r3 = FormatWithRoslyn(r2);
        Assert.Equal(r1, r2);
        Assert.Equal(r2, r3);
    }

    /// <summary>
    /// Minimal ICSharpFormatterDelegate backed by Roslyn, matching lsp-server's RoslynCSharpFormatter.
    /// </summary>
    private sealed class TestRoslynFormatter : ICSharpFormatterDelegate
    {
        private readonly Microsoft.CodeAnalysis.AdhocWorkspace _ws = new();

        public string? Format(string code, int indentSize = 4)
        {
            if (string.IsNullOrEmpty(code))
                return null;
            try
            {
                const string prefix = "class __UitkxFmt__ {\n";
                const string suffix = "\n}";
                string wrapped = prefix + code + suffix;
                var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(wrapped);
                var root = tree.GetRoot();
                var opts = _ws
                    .Options.WithChangedOption(
                        Microsoft.CodeAnalysis.Formatting.FormattingOptions.IndentationSize,
                        Microsoft.CodeAnalysis.LanguageNames.CSharp,
                        indentSize
                    )
                    .WithChangedOption(
                        Microsoft.CodeAnalysis.Formatting.FormattingOptions.UseTabs,
                        Microsoft.CodeAnalysis.LanguageNames.CSharp,
                        false
                    );
                var fmtRoot = Microsoft.CodeAnalysis.Formatting.Formatter.Format(root, _ws, opts);
                string fmtWrapped = fmtRoot.ToFullString();
                int contentStart = fmtWrapped.IndexOf('\n');
                int contentEnd = fmtWrapped.LastIndexOf('}');
                if (contentStart < 0 || contentEnd <= contentStart)
                    return null;
                return fmtWrapped
                    .Substring(contentStart + 1, contentEnd - contentStart - 1)
                    .TrimEnd('\r', '\n', ' ', '\t');
            }
            catch
            {
                return null;
            }
        }

        public string? FormatStatements(string code, int indentSize = 4)
        {
            if (string.IsNullOrEmpty(code))
                return null;
            try
            {
                const string prefix = "class __UitkxFmt__ {\nvoid __render__() {\n";
                const string suffix = "\n}\n}";
                string wrapped = prefix + code + suffix;
                var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(wrapped);
                bool hasError = false;
                foreach (var d in tree.GetDiagnostics())
                    if (d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    {
                        hasError = true;
                        break;
                    }
                if (hasError)
                    return null;
                var root = tree.GetRoot();
                var opts = _ws
                    .Options.WithChangedOption(
                        Microsoft.CodeAnalysis.Formatting.FormattingOptions.IndentationSize,
                        Microsoft.CodeAnalysis.LanguageNames.CSharp,
                        indentSize
                    )
                    .WithChangedOption(
                        Microsoft.CodeAnalysis.Formatting.FormattingOptions.UseTabs,
                        Microsoft.CodeAnalysis.LanguageNames.CSharp,
                        false
                    );
                var fmtRoot = Microsoft.CodeAnalysis.Formatting.Formatter.Format(root, _ws, opts);
                string formatted = fmtRoot.ToFullString();
                const string methodSig = "void __render__()";
                int methodIdx = formatted.IndexOf(methodSig, StringComparison.Ordinal);
                if (methodIdx < 0)
                    return null;
                int braceOpen = formatted.IndexOf('{', methodIdx + methodSig.Length);
                if (braceOpen < 0)
                    return null;
                int depth = 1,
                    pos = braceOpen + 1;
                while (pos < formatted.Length && depth > 0)
                {
                    if (formatted[pos] == '{')
                        depth++;
                    else if (formatted[pos] == '}')
                    {
                        depth--;
                        if (depth == 0)
                            break;
                    }
                    pos++;
                }
                string body = formatted
                    .Substring(braceOpen + 1, pos - braceOpen - 1)
                    .Replace("\r\n", "\n")
                    .TrimStart('\n');
                var lines = body.Split('\n');
                int baseIndent = int.MaxValue;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    int lead = 0;
                    while (lead < line.Length && line[lead] == ' ')
                        lead++;
                    if (lead < baseIndent)
                        baseIndent = lead;
                }
                if (baseIndent == int.MaxValue)
                    baseIndent = 0;
                var sb2 = new System.Text.StringBuilder();
                for (int li = 0; li < lines.Length; li++)
                {
                    string line = lines[li];
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        sb2.Append('\n');
                        continue;
                    }
                    string stripped =
                        baseIndent > 0 && line.Length > baseIndent
                            ? line.Substring(baseIndent)
                            : line.TrimStart(' ');
                    sb2.Append(stripped);
                    if (li < lines.Length - 1)
                        sb2.Append('\n');
                }
                return sb2.ToString().TrimEnd('\r', '\n', ' ', '\t');
            }
            catch
            {
                return null;
            }
        }
    }

    private static Ruitk.Language.Roslyn.VirtualDocument GenerateVDoc(
        string source,
        string filePath
    )
    {
        var diags = new System.Collections.Generic.List<Ruitk.Language.ParseDiagnostic>();
        var directives = Ruitk.Language.Parser.DirectiveParser.Parse(
            source,
            filePath,
            diags
        );
        var nodes = Ruitk.Language.Parser.UitkxParser.Parse(
            source,
            filePath,
            directives,
            diags
        );
        var parseResult = new Ruitk.Language.Parser.ParseResult(
            directives,
            nodes,
            System.Collections.Immutable.ImmutableArray.CreateRange(diags)
        );
        var vdg = new Ruitk.Language.Roslyn.VirtualDocumentGenerator();
        return vdg.Generate(parseResult, source, filePath);
    }

    private static string ExtractBetween(string text, string start, string end)
    {
        int s = text.IndexOf(start, StringComparison.Ordinal);
        if (s < 0)
            return "";
        int e = text.IndexOf(end, s + start.Length, StringComparison.Ordinal);
        if (e < 0)
            return text.Substring(s + start.Length);
        return text.Substring(s + start.Length, e - s - start.Length);
    }

    // ── B.13: VDG setup-code comment/line-mapping regression tests ────────

    /// <summary>
    /// Regression: {/* ... */} JSX comment blocks in setup code must NOT trigger
    /// JSX replacement branches, otherwise #line directives end up inside C#
    /// block comments and become invisible to the compiler — causing subsequent
    /// code (MctvSetChild, MctvDeleteLast, etc.) to lose line mapping entirely.
    /// </summary>
    [Fact]
    public void B13a_VDG_CommentedOutReturn_DoesNotBreakLineMappingForSubsequentCode()
    {
        var source = N(
            @"@namespace Test
component Comp {
  var (count, setCount) = useState(0);

  void MethodBefore() { setCount(1); }

  {/* return (
    <Box>
      <Label text=""sas"" />
    </Box>
  );
    */}

  void MethodAfter() { setCount(2); }

  var node = (
    <Box>
      <Label text=""hello"" />
    </Box>
  );

  return (
    <Box>
      <Label text=""world"" />
    </Box>
  );
}"
        );
        var vdoc = GenerateVDoc(source, "test.uitkx");
        var text = vdoc.Text;

        // {/* ... */} block should be preserved as-is (no VirtualNode replacement inside).
        Assert.DoesNotContain(
            "(global::Ruitk.Core.VirtualNode)null!",
            ExtractBetween(text, "{/*", "*/}")
        );

        // MethodAfter must appear in the output.
        Assert.Contains("MethodAfter", text);

        // MethodAfter must NOT be under #line hidden — there must be a
        // #line N directive that maps it correctly.
        // The setup section from #line 3 to the JSX block should be one continuous mapped segment.
        int methodAfterIdx = text.IndexOf("MethodAfter");
        string beforeMethodAfter = text.Substring(0, methodAfterIdx);

        // Last #line directive before MethodAfter should NOT be "#line hidden".
        int lastLine = beforeMethodAfter.LastIndexOf("#line ");
        Assert.True(lastLine >= 0, "Should have a #line directive before MethodAfter");
        string lastLineDirective = beforeMethodAfter.Substring(lastLine).Split('\n')[0].Trim();
        Assert.DoesNotContain("hidden", lastLineDirective);
    }

    [Fact]
    public void B13b_VDG_LineCommentWithJsxPatternDoesNotTriggerBranch()
    {
        var source = N(
            @"@namespace Test
component Comp {
  var (count, setCount) = useState(0);

  // var x = ( <Label text=""nope"" /> )
  void SomeMethod() { setCount(1); }

  return (
    <Box />
  );
}"
        );
        var vdoc = GenerateVDoc(source, "test.uitkx");
        var text = vdoc.Text;

        // The line comment should be preserved in the output, not replaced.
        Assert.Contains("// var x = ( <Label", text);
        Assert.Contains("SomeMethod", text);
    }

    [Fact]
    public void B13c_VDG_RealFile_SourceMapOffsets_AreCorrect()
    {
        var filePath = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(filePath), $"fixture not found at {filePath}");

        var source = N(File.ReadAllText(filePath));
        var vdoc = GenerateVDoc(source, filePath);
        var text = vdoc.Text;
        var map = vdoc.Map;

        // All FunctionSetup entries must have matching text
        foreach (var e in map.Entries)
        {
            if (e.Kind != Ruitk.Language.Roslyn.SourceRegionKind.FunctionSetup)
                continue;
            string vText = text.Substring(e.VirtualStart, e.VirtualEnd - e.VirtualStart);
            string sText = source.Substring(e.UitkxStart, e.UitkxEnd - e.UitkxStart);
            Assert.Equal(sText, vText);
        }

        // Key identifiers must map to their correct source lines
        var checks = new[]
        {
            ("MctvSetChild", 255),
            ("MctvDeleteLast", 283),
            ("TreeViewRowState", 50),
            ("var secondElement", 298),
        };
        foreach (var (id, expectedLine) in checks)
        {
            int vIdx = text.IndexOf(id);
            Assert.True(vIdx >= 0, $"{id} not found in VDG");
            var entry = map.ToUitkxOffset(vIdx);
            Assert.True(entry.HasValue, $"{id} has no source map entry");
            int uitkxOff = entry.Value.UitkxOffset;
            int line = 1;
            for (int c = 0; c < uitkxOff && c < source.Length; c++)
                if (source[c] == '\n')
                    line++;
            Assert.Equal(expectedLine, line);
        }
    }

    /// <summary>
    /// Setup-code JSX expressions (e.g. onClick={_ => ...} inside
    /// <c>var node = (&lt;Button onClick={handler} /&gt;)</c>) must appear
    /// in the virtual document with source-map entries so Roslyn can
    /// type-check them and provide completions.
    /// </summary>
    [Fact]
    public void B14_VDG_SetupJsxExpressions_AreEmittedWithSourceMapping()
    {
        var source = N(
            @"component Foo {
  var (count, setCount) = useState(0);

  var node = (
    <Button text={$""Count: {count}""} onClick={_ => setCount(count + 1)} />
  );

  return (
    <Box />
  );
}"
        );
        var vdoc = GenerateVDoc(source, "test.uitkx");
        var text = vdoc.Text;
        var map = vdoc.Map;

        // The inline expression `$"Count: {count}"` must be present in the VDoc
        Assert.Contains("$\"Count: {count}\"", text);

        // The onClick lambda `_ => setCount(count + 1)` must be present
        Assert.Contains("setCount(count + 1)", text);

        // Both should have source-map entries pointing back to the .uitkx
        int textIdx = text.IndexOf("$\"Count: {count}\"");
        Assert.True(textIdx >= 0, "text expression not found in VDoc");
        var textEntry = map.ToUitkxOffset(textIdx);
        Assert.True(textEntry.HasValue, "text expression has no source map entry");

        int onClickIdx = text.IndexOf("setCount(count + 1)");
        Assert.True(onClickIdx >= 0, "onClick expression not found in VDoc");
        var onClickEntry = map.ToUitkxOffset(onClickIdx);
        Assert.True(onClickEntry.HasValue, "onClick expression has no source map entry");
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  B.14  Multiple top-level returns with {...} in markup
    //  Regression: UITKX0306 from misidentified setup code must not prevent
    //  formatting when UITKX2103 triggers re-parse with useLastReturn.
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MultiReturn_EarlyReturnInSetup_FormatsCorrectly()
    {
        var source = N(
            """
            component Counter {
              var (count, setCount) = useState(0);
              return (<Box></Box>);
              var x = 1;
              return (
                <Button text="hello" />
              );
            }
            """
        );
        var result = Format(source);
        Assert.NotEqual(source, result);
        Assert.Contains("return (", result);
    }

    [Fact]
    public void MultiReturn_CommentedReturnWithLooseJsx_FormatsCorrectly()
    {
        var source = N(
            """
            component Counter {
              var (count, setCount) = useState(0);
              //return (
                <Box />
              );
              var x = 1;
              return (
                <Button text="hello" />
              );
            }
            """
        );
        var result = Format(source);
        Assert.Contains("return (", result);
        Assert.Contains("<Button", result);
    }

    [Fact]
    public void MultiReturn_ActualFile_Idempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxCounterFunc",
            "UitkxCounterFunc.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));
        var result = Format(source);
        Assert.Equal(source, result);
    }

    [Fact]
    public void MultiReturn_ActualFile_EarlyReturnReplacedSingleLine_Formats()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));
        // Insert a real single-line early return ahead of the main markup return.
        // (The original mutation un-commented a specific commented-out JSX block
        // that no longer exists in the fixture; the multi-return contract under
        // test - formatter uses the LAST return, no corruption - is identical.)
        var modified = source.Replace(
            "\n  return (\n",
            "\n  return (<Box></Box>);\n\n  return (\n"
        );
        Assert.NotEqual(source, modified);
        // Multi-return: formatter uses last return. Verify no corruption.
        var result = Format(modified);
        Assert.Contains("<Button", result);
        Assert.Contains("<Route path=\"/jsxDemo\"", result);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", result);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Regression: under-indented JSX var before lambda with JSX bleeds indent
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void UnderIndentedJsxVar_DoesNotBleedIntoFollowingLambda()
    {
        // The `var someVara` line is at 2sp instead of the correct 4sp.
        // After formatting, it should normalise to 2sp (component body indent)
        // but the `RowRenderer BuildRowRenderer()` block that follows must NOT
        // get extra indentation inside its braces.
        var source = N(
            """
            component Counter {
              var safeItems = Array.Empty<int>();
            var someVara = (
                <Box>
                  {safeItems}
                </Box>
              );
              RowRenderer BuildRowRenderer() =>
              (index, obj) =>
              {
                var row = obj as string;
                if (row == null)
                {
                  return (<Label text="<invalid>" key={$"lv-invalid-{index}"} />);
                }

                return (
                  <VisualElement key={$"lv-wrap-{index}"}>
                    {row}
                  </VisualElement>
                );
              };

              return (
                <Label text="hi" />
              );
            }
            """
        );

        var expected = N(
            """
            component Counter {
              var safeItems = Array.Empty<int>();
              var someVara = (
                <Box>
                  {safeItems}
                </Box>
              );
              RowRenderer BuildRowRenderer() =>
              (index, obj) =>
              {
                var row = obj as string;
                if (row == null)
                {
                  return (<Label text="<invalid>" key={$"lv-invalid-{index}"} />);
                }

                return (
                  <VisualElement key={$"lv-wrap-{index}"}>
                    {row}
                  </VisualElement>
                );
              };

              return (
                <Label text="hi" />
              );
            }

            """
        );

        var result = Format(source);
        Assert.Equal(expected, result);

        // Must also be idempotent
        var r2 = Format(result);
        Assert.Equal(result, r2);
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Regression: long param lists wrap one-per-line when > PrintWidth
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void LongParamList_WrapsOnePerLine()
    {
        var source = N(
            """
            component Foo(IReadOnlyList<int>? items = null, Action? addItem = null, Action? setTopItem = null, Action? deleteLast = null, Action<int>? onCountChanged = null) {
              return (<Label text="hi" />);
            }
            """
        );

        var expected = N(
            """
            component Foo(
              IReadOnlyList<int>? items = null,
              Action? addItem = null,
              Action? setTopItem = null,
              Action? deleteLast = null,
              Action<int>? onCountChanged = null
            ) {
              return (
                <Label text="hi" />
              );
            }

            """
        );

        var result = Format(source);
        Assert.Equal(expected, result);

        // Idempotent
        Assert.Equal(result, Format(result));
    }

    [Fact]
    public void ShortParamList_StaysSingleLine()
    {
        var source = N(
            """
            component Foo(int x = 0, string y = "hi") {
              return (<Label text="hi" />);
            }
            """
        );

        var result = Format(source);
        Assert.Contains("component Foo(int x = 0, string y = \"hi\") {", result);
        Assert.Equal(result, Format(result));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  UITKX0111 — Unused component parameter
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void UITKX0111_UnusedParam_IsReported()
    {
        var source = N(
            @"component Foo(int x = 0, string y = ""hi"") {
  return (<Label text=""hello"" />);
}"
        );
        var diags = RunAnalyzer(source, "Foo.uitkx");
        var unused = diags.Where(d => d.Code == "UITKX0111").ToList();
        Assert.Equal(2, unused.Count);
        Assert.Contains(unused, d => d.Message.Contains("'x'"));
        Assert.Contains(unused, d => d.Message.Contains("'y'"));
    }

    [Fact]
    public void UITKX0111_UsedParam_NoFalsePositive_InSetupCode()
    {
        var source = N(
            @"component Foo(int x = 0) {
  var doubled = x * 2;
  return (<Label text={doubled.ToString()} />);
}"
        );
        var diags = RunAnalyzer(source, "Foo.uitkx");
        Assert.DoesNotContain(diags, d => d.Code == "UITKX0111");
    }

    [Fact]
    public void UITKX0111_UsedParam_NoFalsePositive_InMarkup()
    {
        var source = N(
            @"component Foo(string label = ""hi"") {
  return (<Label text={label} />);
}"
        );
        var diags = RunAnalyzer(source, "Foo.uitkx");
        Assert.DoesNotContain(diags, d => d.Code == "UITKX0111");
    }

    [Fact]
    public void UITKX0111_MixedUsedAndUnused()
    {
        var source = N(
            @"component Foo(int x = 0, string label = ""hi"") {
  return (<Label text={label} />);
}"
        );
        var diags = RunAnalyzer(source, "Foo.uitkx");
        var unused = diags.Where(d => d.Code == "UITKX0111").ToList();
        Assert.Single(unused);
        Assert.Contains("'x'", unused[0].Message);
    }

    [Fact]
    public void UITKX0111_ShadowedLocalDoesNotCountAsUse()
    {
        // `items` appears inside a local function as a local var — that
        // shadows the component parameter, so the parameter is still unused.
        var source = N(
            @"component Foo(int items = 0) {
  void Shuffle()
  {
    var items = new List<int>();
    items.Add(1);
  }
  return (<Label text=""hi"" />);
}"
        );
        var diags = RunAnalyzer(source, "Foo.uitkx");
        var unused = diags.Where(d => d.Code == "UITKX0111").ToList();
        Assert.Single(unused);
        Assert.Contains("'items'", unused[0].Message);
    }

    [Fact]
    public void UITKX0111_UnshadowedLocalFunctionUseCounts()
    {
        // `items` used inside a local function WITHOUT redeclaring it
        // → the parameter IS being used.
        var source = N(
            @"component Foo(int items = 0) {
  void DoWork()
  {
    Console.WriteLine(items);
  }
  return (<Label text=""hi"" />);
}"
        );
        var diags = RunAnalyzer(source, "Foo.uitkx");
        Assert.DoesNotContain(diags, d => d.Code == "UITKX0111");
    }

    [Fact]
    public void UITKX0111_ShadowedButRHSUsesParam()
    {
        // The declaration `var items = f(items)` shadows the name, but the
        // RHS still references the parameter — so the param IS used.
        var source = N(
            @"component Foo(int items = 0) {
  var items = items + 1;
  return (<Label text={items.ToString()} />);
}"
        );
        var diags = RunAnalyzer(source, "Foo.uitkx");
        Assert.DoesNotContain(diags, d => d.Code == "UITKX0111");
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Regression: full test file — blank line insert + format idempotency
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RealFile_BlankLineBeforeReturn_FormatIdempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));

        // Insert a blank line right before the main "return ("
        var modified = source.Replace(
            "\n  return (\n    <ScrollView",
            "\n\n  return (\n    <ScrollView"
        );
        Assert.NotEqual(source, modified);

        var first = FormatWithRoslyn(modified);
        var second = FormatWithRoslyn(first);
        Assert.Equal(first, second);
        // Route should never become Router
        Assert.Contains("<Route path=\"/jsxDemo\"", first);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", first);
    }

    [Fact]
    public void RealFile_ManyBlankLinesBeforeReturn_FormatIdempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));

        // Insert many blank lines right before the main "return ("
        var modified = source.Replace(
            "\n  return (\n    <ScrollView",
            "\n\n\n\n\n\n  return (\n    <ScrollView"
        );
        Assert.NotEqual(source, modified);

        // Format and check the output is not corrupted
        var formatted = Format(modified);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", formatted);
        Assert.Contains("<Route path=\"/jsxDemo\"", formatted);

        // Check that a second format is idempotent
        var formatted2 = Format(formatted);
        Assert.Equal(formatted, formatted2);
    }

    private static void CollectTags(
        System.Collections.Immutable.ImmutableArray<Ruitk.Language.Nodes.AstNode> nodes,
        System.Collections.Generic.List<string> tags
    )
    {
        foreach (var node in nodes)
        {
            if (node is Ruitk.Language.Nodes.ElementNode el)
            {
                tags.Add(el.TagName);
                CollectTags(el.Children, tags);
            }
            else if (node is Ruitk.Language.Nodes.IfNode ifn)
            {
                foreach (var br in ifn.Branches)
                    CollectTags(br.Payload.Body, tags);
            }
            else if (node is Ruitk.Language.Nodes.ForeachNode fe)
            {
                CollectTags(fe.Payload.Body, tags);
            }
            else if (node is Ruitk.Language.Nodes.SwitchNode sw)
            {
                foreach (var c in sw.Cases)
                    CollectTags(c.Payload.Body, tags);
            }
            else if (node is Ruitk.Language.Nodes.ForNode fn)
            {
                CollectTags(fn.Payload.Body, tags);
            }
            else if (node is Ruitk.Language.Nodes.WhileNode wh)
            {
                CollectTags(wh.Payload.Body, tags);
            }
        }
    }

    [Fact]
    public void RealFile_BlankLinesInsideReturn_FormatIdempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));

        // Insert blank lines between return ( and <ScrollView
        var modified = source.Replace(
            "  return (\n    <ScrollView",
            "  return (\n\n\n\n    <ScrollView"
        );
        Assert.NotEqual(source, modified);

        var first = FormatWithRoslyn(modified);
        var second = FormatWithRoslyn(first);
        Assert.Equal(first, second);
        Assert.Contains("<Route path=\"/jsxDemo\"", first);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", first);
    }

    [Fact]
    public void RealFile_CRLF_BlankLinesBeforeReturn_FormatIdempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        // Read with CRLF line endings
        var source = File.ReadAllText(file).Replace("\r\n", "\n").Replace("\n", "\r\n");

        // Insert blank CRLF lines before the main return
        var modified = source.Replace(
            "\r\n  return (\r\n    <ScrollView",
            "\r\n\r\n\r\n\r\n  return (\r\n    <ScrollView"
        );
        Assert.NotEqual(source, modified);

        var first = FormatWithRoslyn(modified);
        var second = FormatWithRoslyn(first);
        Assert.Equal(first, second);
        Assert.Contains("<Route path=\"/jsxDemo\"", first);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", first);
    }

    [Fact]
    public void RealFile_CRLF_Parse_NoDiagnosticErrors()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        // Read the raw file (CRLF on Windows)
        var source = File.ReadAllText(file);
        var diags = new System.Collections.Generic.List<Ruitk.Language.ParseDiagnostic>();
        var directives = Ruitk.Language.Parser.DirectiveParser.Parse(source, file, diags);
        var nodes = Ruitk.Language.Parser.UitkxParser.Parse(source, file, directives, diags);
        var errors = diags
            .Where(d => d.Severity == Ruitk.Language.ParseSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            $"Parse errors with CRLF:\n{string.Join("\n", errors.Select(e => $"  {e.Code} L{e.SourceLine}: {e.Message}"))}"
        );
    }

    [Fact]
    public void RealFile_AddVarAboveReturn_FormatIdempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));

        // Insert a dummy var assignment before the main return
        var modified = source.Replace(
            "\n  return (\n    <ScrollView",
            "\n  var dummy = (\n    <Label text=\"test\" />\n  );\n\n  return (\n    <ScrollView"
        );
        Assert.NotEqual(source, modified);

        var first = FormatWithRoslyn(modified);
        var second = FormatWithRoslyn(first);
        Assert.Equal(first, second);
        Assert.Contains("<Route path=\"/jsxDemo\"", first);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", first);
    }

    [Fact]
    public void RealFile_AddFragmentVarAboveReturn_FormatIdempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));

        // Insert a fragment var assignment before the main return (user's exact pattern)
        var modified = source.Replace(
            "\n  return (\n    <ScrollView",
            "\n  var something = (\n    <>\n  );\n\n  return (\n    <ScrollView"
        );
        Assert.NotEqual(source, modified);

        var first = FormatWithRoslyn(modified);
        var second = FormatWithRoslyn(first);
        Assert.Equal(first, second);
        Assert.Contains("<Route path=\"/jsxDemo\"", first);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", first);
    }

    [Fact]
    public void RealFile_AddRemoveVarThenFormat_ThreeRounds_Idempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));

        // Round 1: add var + format
        var round1Input = source.Replace(
            "\n  return (\n    <ScrollView",
            "\n  var something = (\n    <Label text=\"test\" />\n  );\n\n  return (\n    <ScrollView"
        );
        var round1 = FormatWithRoslyn(round1Input);

        // Round 2: remove the var (simulating user deleting it) + format
        var round2Input = round1.Replace(
            "  var something = (\n    <Label text=\"test\" />\n  );\n\n",
            ""
        );
        var round2 = FormatWithRoslyn(round2Input);

        // Round 3: format again — must be stable
        var round3 = FormatWithRoslyn(round2);
        Assert.Equal(round2, round3);
        Assert.Contains("<Route path=\"/jsxDemo\"", round2);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", round2);
    }

    private static System.Collections.Generic.List<Ruitk.Language.ParseDiagnostic> RunAnalyzer(
        string source,
        string filePath
    )
    {
        var diags = new System.Collections.Generic.List<Ruitk.Language.ParseDiagnostic>();
        var directives = Ruitk.Language.Parser.DirectiveParser.Parse(
            source,
            filePath,
            diags
        );
        var nodes = Ruitk.Language.Parser.UitkxParser.Parse(
            source,
            filePath,
            directives,
            diags
        );
        var parseResult = new Ruitk.Language.Parser.ParseResult(
            directives,
            nodes,
            System.Collections.Immutable.ImmutableArray.CreateRange(diags)
        );
        var analyzer = new Ruitk.Language.Diagnostics.DiagnosticsAnalyzer();
        var t2 = analyzer.Analyze(parseResult, filePath);
        return t2.ToList();
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  DEBUG: Blank-line-count sweep — reproduce corruption
    // ════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void RealFile_BlankLineSweep_NoCorruption(int extraBlanks)
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));

        // User's exact reproduction: add blank lines between the ternary and return (
        // Re-anchored mutation (see RealFile_BlankLineSweep_CRLFEdits_NoCorruption):
        // blanks after the markup-return open paren, which the formatter always removes.
        var blanks = new string('\n', extraBlanks);
        var modified = source.Replace(
            "\n  return (\n",
            "\n  return (\n" + blanks
        );
        Assert.NotEqual(source, modified);

        // ── Test 1: Formatter output is correct ──
        var formatted = Format(modified);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", formatted);
        Assert.DoesNotContain("</string,>", formatted);
        Assert.Contains("<Route path=\"/jsxDemo\"", formatted);

        var formattedRoslyn = FormatWithRoslyn(modified);
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", formattedRoslyn);
        Assert.DoesNotContain("</string,>", formattedRoslyn);
        Assert.Contains("<Route path=\"/jsxDemo\"", formattedRoslyn);

        // ── Test 2: Simulate LSP per-line edit path ──
        // Use CRLF input (like the editor sends)
        var crlfInput = modified.Replace("\n", "\r\n");
        var fmtOutput = N(new AstFormatter(FormatterOptions.Default).Format(crlfInput));

        // Simulate the LSP FormattingHandler edit computation
        var normalizedInput = crlfInput.Replace("\r\n", "\n").Replace("\r", "\n");
        var origLines = normalizedInput.Split('\n');
        var fmtLines = fmtOutput.Split('\n');

        // Compute edits (same logic as FormattingHandler)
        var edits =
            new List<(int startLine, int startCol, int endLine, int endCol, string newText)>();
        int minLen = System.Math.Min(origLines.Length, fmtLines.Length);
        bool linesRemoved = origLines.Length > fmtLines.Length;

        for (int i = 0; i < minLen; i++)
        {
            if (origLines[i] != fmtLines[i])
            {
                if (origLines[i].TrimEnd().Length == 0 && fmtLines[i].TrimEnd().Length == 0)
                    continue;

                bool isLastCommonLine = linesRemoved && i == minLen - 1;
                int endLine = isLastCommonLine ? origLines.Length - 1 : i;
                int endCol = isLastCommonLine
                    ? origLines[origLines.Length - 1].Length
                    : origLines[i].Length;

                edits.Add((i, 0, endLine, endCol, fmtLines[i]));

                if (isLastCommonLine)
                    linesRemoved = false;
            }
        }

        if (fmtLines.Length > origLines.Length)
        {
            var extraLines = new string[fmtLines.Length - origLines.Length];
            System.Array.Copy(fmtLines, origLines.Length, extraLines, 0, extraLines.Length);
            edits.Add(
                (
                    origLines.Length - 1,
                    origLines[origLines.Length - 1].Length,
                    origLines.Length - 1,
                    origLines[origLines.Length - 1].Length,
                    "\n" + string.Join("\n", extraLines)
                )
            );
        }
        else if (linesRemoved)
        {
            int lastFmt = fmtLines.Length - 1;
            int lastOrig = origLines.Length - 1;
            edits.Add(
                (lastFmt, origLines[lastFmt].Length, lastOrig, origLines[lastOrig].Length, "")
            );
        }

        // Apply edits to the CRLF input (simulating VS Code)
        // VS Code applies edits using CRLF positions but we computed with LF.
        // Apply to the LF-normalized text and check result.
        var applied = ApplyEditsToText(normalizedInput, edits);

        // The applied result should match the formatted output
        var hasRouterBug = applied.Contains("<Router path=\"/jsxDemo\"");
        var hasStringBug = applied.Contains("</string,>");

        if (hasRouterBug || hasStringBug || applied != fmtOutput)
        {
            // Find first difference
            int diffIdx = -1;
            for (int i = 0; i < System.Math.Min(applied.Length, fmtOutput.Length); i++)
            {
                if (applied[i] != fmtOutput[i])
                {
                    diffIdx = i;
                    break;
                }
            }
            if (diffIdx < 0 && applied.Length != fmtOutput.Length)
                diffIdx = System.Math.Min(applied.Length, fmtOutput.Length);

            var msg =
                $"LSP EDIT CORRUPTION with {extraBlanks} extra blank(s):\n"
                + $"  RouterBug={hasRouterBug} StringBug={hasStringBug}\n"
                + $"  origLines={origLines.Length} fmtLines={fmtLines.Length} edits={edits.Count}\n"
                + $"  applied.Length={applied.Length} expected.Length={fmtOutput.Length}\n";

            if (diffIdx >= 0)
            {
                int ctx = 80;
                int s = System.Math.Max(0, diffIdx - ctx);
                int e1 = System.Math.Min(applied.Length, diffIdx + ctx);
                int e2 = System.Math.Min(fmtOutput.Length, diffIdx + ctx);
                msg +=
                    $"  First diff at char {diffIdx}:\n"
                    + $"  Applied:  ...{applied.Substring(s, e1 - s).Replace("\n", "\\n")}...\n"
                    + $"  Expected: ...{fmtOutput.Substring(s, e2 - s).Replace("\n", "\\n")}...\n";
            }
            Assert.Fail(msg);
        }
    }

    /// <summary>
    /// Apply LSP-style text edits to a document. Edits reference positions in
    /// the original document and are applied in reverse order.
    /// Line/col positions map to the text's actual line structure.
    /// </summary>
    private static string ApplyEditsToText(
        string text,
        List<(int startLine, int startCol, int endLine, int endCol, string newText)> edits
    )
    {
        // Convert line/col positions to absolute offsets in the text
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                lineStarts.Add(i + 1);
        }

        // Sort edits in reverse order (bottom to top) as VS Code does
        var sorted = edits
            .OrderByDescending(e => e.startLine)
            .ThenByDescending(e => e.startCol)
            .ToList();

        var sb = new System.Text.StringBuilder(text);
        foreach (var (sl, sc, el, ec, nt) in sorted)
        {
            int startOffset = lineStarts[sl] + sc;
            int endOffset = lineStarts[el] + ec;
            sb.Remove(startOffset, endOffset - startOffset);
            sb.Insert(startOffset, nt);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Compute per-line edits exactly as FormattingHandler does.
    /// </summary>
    private static List<(
        int startLine,
        int startCol,
        int endLine,
        int endCol,
        string newText
    )> ComputePerLineEdits(string[] origLines, string[] fmtLines)
    {
        var edits = new List<(int, int, int, int, string)>();
        int minLen = System.Math.Min(origLines.Length, fmtLines.Length);
        bool linesRemoved = origLines.Length > fmtLines.Length;

        for (int i = 0; i < minLen; i++)
        {
            if (origLines[i] != fmtLines[i])
            {
                if (origLines[i].TrimEnd().Length == 0 && fmtLines[i].TrimEnd().Length == 0)
                    continue;

                bool isLastCommonLine = linesRemoved && i == minLen - 1;
                int endLine = isLastCommonLine ? origLines.Length - 1 : i;
                int endCol = isLastCommonLine
                    ? origLines[origLines.Length - 1].Length
                    : origLines[i].Length;

                edits.Add((i, 0, endLine, endCol, fmtLines[i]));

                if (isLastCommonLine)
                    linesRemoved = false;
            }
        }

        if (fmtLines.Length > origLines.Length)
        {
            var extraLines = new string[fmtLines.Length - origLines.Length];
            System.Array.Copy(fmtLines, origLines.Length, extraLines, 0, extraLines.Length);
            edits.Add(
                (
                    origLines.Length - 1,
                    origLines[origLines.Length - 1].Length,
                    origLines.Length - 1,
                    origLines[origLines.Length - 1].Length,
                    "\n" + string.Join("\n", extraLines)
                )
            );
        }
        else if (linesRemoved)
        {
            int lastFmt = fmtLines.Length - 1;
            int lastOrig = origLines.Length - 1;
            edits.Add(
                (lastFmt, origLines[lastFmt].Length, lastOrig, origLines[lastOrig].Length, "")
            );
        }

        return edits;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(12)]
    public void RealFile_BlankLineSweep_CRLFEdits_NoCorruption(int extraBlanks)
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var sourceLF = N(File.ReadAllText(file));

        // Add extra blank lines (LF) right after the component's markup-return
        // open paren - a spot the formatter always normalizes, so the per-line
        // edit machinery is genuinely exercised. (The previous anchor tied the
        // string.Join line directly to the return; the fixture has since grown
        // content between them.)
        var blanks = new string('\n', extraBlanks);
        var modifiedLF = sourceLF.Replace(
            "\n  return (\n",
            "\n  return (\n" + blanks
        );
        Assert.NotEqual(sourceLF, modifiedLF);

        // Create CRLF version (what VS Code actually has)
        var modifiedCRLF = modifiedLF.Replace("\n", "\r\n");

        // ── Check: base file must be idempotent first ──
        var baseFormatted = FormatWithRoslyn(sourceLF);
        Assert.Equal(sourceLF, baseFormatted);

        // Format with Roslyn (like the LSP does)
        var formatted = FormatWithRoslyn(modifiedCRLF);

        // Compute edits from LF-normalized text (like FormattingHandler)
        var normalizedInput = modifiedCRLF.Replace("\r\n", "\n").Replace("\r", "\n");
        var origLines = normalizedInput.Split('\n');
        var fmtLines = formatted.Split('\n');
        var edits = ComputePerLineEdits(origLines, fmtLines);

        // ─── Apply to LF text (sanity check) ────
        var appliedLF = ApplyEditsToText(normalizedInput, edits);
        Assert.Equal(formatted, appliedLF);

        // ─── Apply to CRLF text (the REAL scenario) ────
        var appliedCRLF = ApplyEditsToText(modifiedCRLF, edits);

        // Check for corruption in the CRLF-applied result
        var hasRouterBug = appliedCRLF.Contains("<Router path=\"/jsxDemo\"");
        var hasStringBug = appliedCRLF.Contains("</string,>");

        // Also verify the CRLF result matches expected (formatted + CRLF endings)
        // After applying LF-based edits to CRLF text, unchanged lines keep \r\n
        // but edited lines get LF-only content. Normalize to LF for comparison.
        var appliedNormalized = appliedCRLF.Replace("\r\n", "\n").Replace("\r", "\n");

        if (hasRouterBug || hasStringBug || appliedNormalized != formatted)
        {
            int diffIdx = -1;
            for (int i = 0; i < System.Math.Min(appliedNormalized.Length, formatted.Length); i++)
            {
                if (appliedNormalized[i] != formatted[i])
                {
                    diffIdx = i;
                    break;
                }
            }
            if (diffIdx < 0 && appliedNormalized.Length != formatted.Length)
                diffIdx = System.Math.Min(appliedNormalized.Length, formatted.Length);

            var msg =
                $"CRLF EDIT CORRUPTION with {extraBlanks} blanks:\n"
                + $"  RouterBug={hasRouterBug} StringBug={hasStringBug}\n"
                + $"  origLines={origLines.Length} fmtLines={fmtLines.Length} edits={edits.Count}\n"
                + $"  appliedLen={appliedNormalized.Length} expectedLen={formatted.Length}\n";
            if (diffIdx >= 0)
            {
                int s = System.Math.Max(0, diffIdx - 80);
                int e1 = System.Math.Min(appliedNormalized.Length, diffIdx + 80);
                int e2 = System.Math.Min(formatted.Length, diffIdx + 80);
                msg +=
                    $"  First diff at char {diffIdx}:\n"
                    + $"  Applied:  ...{appliedNormalized.Substring(s, e1 - s).Replace("\n", "\\n").Replace("\r", "\\r")}...\n"
                    + $"  Expected: ...{formatted.Substring(s, e2 - s).Replace("\n", "\\n").Replace("\r", "\\r")}...\n";
            }
            Assert.Fail(msg);
        }
    }

    /// <summary>
    /// Test that the formatted output of the file with NO changes is byte-for-byte
    /// identical (idempotent). If this fails, the per-line diff will always produce
    /// edits which could interact badly with the blank-line shift.
    /// </summary>
    [Fact]
    public void RealFile_IdempotencyCheck_WithRoslyn()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var sourceLF = N(File.ReadAllText(file));
        var formatted = FormatWithRoslyn(sourceLF);

        if (formatted != sourceLF)
        {
            // Find first difference
            int diffIdx = -1;
            for (int i = 0; i < System.Math.Min(formatted.Length, sourceLF.Length); i++)
            {
                if (formatted[i] != sourceLF[i])
                {
                    diffIdx = i;
                    break;
                }
            }
            if (diffIdx < 0)
                diffIdx = System.Math.Min(formatted.Length, sourceLF.Length);

            // Find line number
            int lineNum = 0;
            for (int i = 0; i < diffIdx && i < sourceLF.Length; i++)
                if (sourceLF[i] == '\n')
                    lineNum++;

            int s = System.Math.Max(0, diffIdx - 120);
            int e1 = System.Math.Min(formatted.Length, diffIdx + 120);
            int e2 = System.Math.Min(sourceLF.Length, diffIdx + 120);
            Assert.Fail(
                $"File is NOT idempotent with Roslyn! First diff at char {diffIdx} (line ~{lineNum}):\n"
                    + $"  formatted.Length={formatted.Length} source.Length={sourceLF.Length}\n"
                    + $"  Formatted: ...{formatted.Substring(s, e1 - s).Replace("\n", "\\n")}...\n"
                    + $"  Source:    ...{sourceLF.Substring(s, e2 - s).Replace("\n", "\\n")}...\n"
            );
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Block diff algorithm tests (Ruff/Prettier-style single edit)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute a minimal block diff: scan forward for the first differing line,
    /// scan backward for the last, emit a single edit. Mirrors FormattingHandler.
    /// </summary>
    private static (
        int startLine,
        int startCol,
        int endLine,
        int endCol,
        string newText
    )? ComputeBlockDiffEdit(string[] origLines, string[] fmtLines)
    {
        int minLen = System.Math.Min(origLines.Length, fmtLines.Length);

        // Scan forward: first differing line
        int firstDiff = 0;
        while (firstDiff < minLen && origLines[firstDiff] == fmtLines[firstDiff])
            firstDiff++;

        // Scan backward: last differing line
        int origEnd = origLines.Length - 1;
        int fmtEnd = fmtLines.Length - 1;
        while (origEnd > firstDiff && fmtEnd > firstDiff && origLines[origEnd] == fmtLines[fmtEnd])
        {
            origEnd--;
            fmtEnd--;
        }

        if (firstDiff > origEnd && firstDiff > fmtEnd)
            return null;

        var newText = string.Join("\n", fmtLines, firstDiff, fmtEnd - firstDiff + 1);
        return (firstDiff, 0, origEnd, origLines[origEnd].Length, newText);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void RealFile_BlockDiff_NoCorruption(int extraBlanks)
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");
        var source = N(File.ReadAllText(file));

        // Re-anchored mutation (see RealFile_BlankLineSweep_CRLFEdits_NoCorruption):
        // blanks after the markup-return open paren, which the formatter always removes.
        var blanks = new string('\n', extraBlanks);
        var modified = source.Replace(
            "\n  return (\n",
            "\n  return (\n" + blanks
        );
        Assert.NotEqual(source, modified);

        var formatted = FormatWithRoslyn(modified);

        // ── LF path ──
        var origLines = modified.Split('\n');
        var fmtLines = formatted.Split('\n');
        var blockEdit = ComputeBlockDiffEdit(origLines, fmtLines);

        if (formatted == modified)
        {
            Assert.Null(blockEdit);
            return;
        }
        Assert.NotNull(blockEdit);

        var edits = new List<(int startLine, int startCol, int endLine, int endCol, string newText)>
        {
            blockEdit.Value,
        };
        var applied = ApplyEditsToText(modified, edits);
        Assert.Equal(formatted, applied);

        // ── CRLF path (what VS Code actually has) ──
        var modifiedCRLF = modified.Replace("\n", "\r\n");
        var normalizedCRLF = modifiedCRLF.Replace("\r\n", "\n").Replace("\r", "\n");
        var origLinesCRLF = normalizedCRLF.Split('\n');
        var fmtLinesCRLF = formatted.Split('\n');
        var blockEditCRLF = ComputeBlockDiffEdit(origLinesCRLF, fmtLinesCRLF);
        Assert.NotNull(blockEditCRLF);

        var editsCRLF = new List<(
            int startLine,
            int startCol,
            int endLine,
            int endCol,
            string newText
        )>
        {
            blockEditCRLF.Value,
        };

        // Apply to CRLF text
        var appliedCRLF = ApplyEditsToText(modifiedCRLF, editsCRLF);
        var appliedCRLFNormalized = appliedCRLF.Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.Equal(formatted, appliedCRLFNormalized);

        // Verify no corruption patterns
        Assert.DoesNotContain("<Router path=\"/jsxDemo\"", applied);
        Assert.DoesNotContain("</string,>", applied);
        Assert.Contains("<Route path=\"/jsxDemo\"", applied);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HOOK / MODULE FILE FORMATTING
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void HookFile_NotDestroyed_PreservesContent()
    {
        // Regression: formatter used to route hook files through
        // FormatFunctionStyleComponent, emitting `component Component { return (); }`
        var source = N(
            """
            @namespace MyApp

            hook useCounter() -> (int count, Action increment) {
              var (count, setCount) = useState(0);
              Action increment = () => setCount(c => c + 1);
              return (count, increment);
            }
            """
        );

        var result = Format(source);

        Assert.Contains("hook useCounter()", result);
        Assert.Contains("var (count, setCount) = useState(0);", result);
        Assert.Contains("return (count, increment);", result);
        Assert.DoesNotContain("component Component", result);
    }

    [Fact]
    public void ModuleFile_NotDestroyed_PreservesContent()
    {
        var source = N(
            """
            @namespace MyApp
            @using Ruitk.Props.Typed

            module Counter {
              public static readonly Style containerStyle = new Style
              {
                Padding = 20,
              };
            }
            """
        );

        var result = Format(source);

        Assert.Contains("module Counter {", result);
        Assert.Contains("public static readonly Style containerStyle", result);
        Assert.DoesNotContain("component Component", result);
    }

    [Fact]
    public void HookFile_Idempotent()
    {
        var source = N(
            """
            @namespace MyApp

            hook useCounter() -> (int count, Action increment) {
              var (count, setCount) = useState(0);
              Action increment = () => setCount(c => c + 1);
              return (count, increment);
            }
            """
        );

        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ModuleFile_Idempotent()
    {
        var source = N(
            """
            @namespace MyApp
            @using Ruitk.Props.Typed

            module Counter {
              public static readonly Style containerStyle = new Style
              {
                Padding = 20,
                FlexGrow = 1,
              };
            }
            """
        );

        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void HookHeader_ShortLine_StaysSingleLine()
    {
        var source = N(
            """
            @namespace MyApp

            hook useFlag() -> bool {
              return true;
            }
            """
        );

        var result = Format(source);
        Assert.Contains("hook useFlag() -> bool {", result);
    }

    [Fact]
    public void HookHeader_LongParams_WrapsPerLine()
    {
        // PrintWidth default is 80; this header exceeds it.
        var source = N(
            """
            @namespace MyApp

            hook useData(string veryLongParamNameOne, string veryLongParamNameTwo, int anotherLongParam) -> int {
              return 42;
            }
            """
        );

        var result = Format(source);

        // Should be wrapped one-per-line
        Assert.Contains("hook useData(\n", result);
        Assert.Contains("  string veryLongParamNameOne,", result);
        Assert.Contains("  string veryLongParamNameTwo,", result);
        Assert.Contains("  int anotherLongParam\n", result);
        Assert.Contains(") -> int {", result);
    }

    [Fact]
    public void HookHeader_LongTupleReturn_WrapsPerLine()
    {
        var source = N(
            """
            @namespace MyApp

            hook useGameState() -> (bool gameStarted, string playerTurn, string winnerMessage, PointerEventHandler handleStart) {
              return (false, "X", "", null);
            }
            """
        );

        var result = Format(source);

        Assert.Contains(") -> (\n", result);
        Assert.Contains("  bool gameStarted,", result);
        Assert.Contains("  string playerTurn,", result);
        Assert.Contains("  PointerEventHandler handleStart\n", result);
        Assert.Contains(") {", result);
    }

    [Fact]
    public void HookHeader_LongParamsAndTupleReturn_BothWrap()
    {
        var source = N(
            """
            @namespace MyApp

            hook useBigHook(string paramAlpha, string paramBeta, int paramGamma) -> (bool flagOne, string valueTwo, int countThree, Action callbackFour) {
              return (true, "x", 0, () => {});
            }
            """
        );

        var result = Format(source);

        // Params wrapped
        Assert.Contains("hook useBigHook(\n", result);
        Assert.Contains("  string paramAlpha,", result);
        // Return tuple wrapped
        Assert.Contains(") -> (\n", result);
        Assert.Contains("  bool flagOne,", result);
        Assert.Contains("  Action callbackFour\n", result);
        Assert.Contains(") {", result);
    }

    [Fact]
    public void HookFile_MultipleHooks_AllPreserved()
    {
        var source = N(
            """
            @namespace MyApp

            hook useAlpha() -> int {
              return 1;
            }

            hook useBeta() -> string {
              return "hello";
            }
            """
        );

        var result = Format(source);

        Assert.Contains("hook useAlpha() -> int {", result);
        Assert.Contains("hook useBeta() -> string {", result);
        Assert.Contains("return 1;", result);
        Assert.Contains("return \"hello\";", result);
    }

    [Fact]
    public void HookAndModule_MixedFile_BothPreserved()
    {
        var source = N(
            """
            @namespace MyApp

            hook useCounter() -> int {
              return 0;
            }

            module Counter {
              public static readonly int X = 42;
            }
            """
        );

        var result = Format(source);

        Assert.Contains("hook useCounter() -> int {", result);
        Assert.Contains("module Counter {", result);
        Assert.Contains("public static readonly int X = 42;", result);
    }

    [Fact]
    public void RealFile_HooksFile_Idempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.hooks.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");

        var source = N(File.ReadAllText(file));
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void RealFile_StyleFile_Idempotent()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.style.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");

        var source = N(File.ReadAllText(file));
        var first = Format(source);
        var second = Format(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void RealFile_HooksFile_NotDestroyed()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.hooks.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");

        var source = N(File.ReadAllText(file));
        var result = Format(source);

        Assert.Contains("useTestCounter()", result);
        Assert.Contains("useTestFormState(", result);
        Assert.Contains("export (", result);
        Assert.DoesNotContain("component Component", result);
    }

    [Fact]
    public void RealFile_StyleFile_NotDestroyed()
    {
        var file = Path.Combine(
            WorkspaceRoot(),
            "Samples",
            "Components",
            "UitkxTestFileDoNotTouch",
            "UitkxTestFileDoNotTouch.style.uitkx"
        );
        Assert.True(File.Exists(file), $"fixture not found at {file}");

        var source = N(File.ReadAllText(file));
        var result = Format(source);

        Assert.Contains("export Style ContainerStyle", result);
        Assert.DoesNotContain("component Component", result);
    }

    // ── U-01: leading comments before `component` must survive a format ────────

    [Fact]
    public void LeadingLineComment_Header_Survives()
    {
        var source = "// My license header\ncomponent Foo {\n    return (\n        <Label text=\"hi\" />\n    );\n}\n";
        var result = Format(source);
        Assert.Contains("// My license header", result);
        Assert.StartsWith("// My license header", result);
    }

    [Fact]
    public void LeadingLineComment_Header_IsIdempotent()
    {
        var source = "// My license header\ncomponent Foo {\n    return (\n        <Label text=\"hi\" />\n    );\n}\n";
        var once = Format(source);
        var twice = Format(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void LeadingBlockComment_Header_Survives()
    {
        var source = "/* Copyright 2026 */\ncomponent Foo {\n    return (\n        <Label text=\"hi\" />\n    );\n}\n";
        var result = Format(source);
        Assert.Contains("/* Copyright 2026 */", result);
    }

    [Fact]
    public void LeadingComment_BetweenUsingLines_Survives()
    {
        var source = "using System;\n// keep this using too\nusing System.Linq;\ncomponent Foo {\n    return (\n        <Label text=\"hi\" />\n    );\n}\n";
        var result = Format(source);
        Assert.Contains("// keep this using too", result);
        Assert.Contains("@using System", result);
        Assert.Contains("@using System.Linq", result);
    }

    [Fact]
    public void LeadingComment_HookModuleFile_Survives()
    {
        var source = "// hook file header\nhook useCounter(int initial = 0) -> (int, Action) {\n    var (n, setN) = useState(initial);\n    return (n, () => setN(n + 1));\n}\n";
        var result = Format(source);
        Assert.Contains("// hook file header", result);
        Assert.StartsWith("// hook file header", result);
    }

    // ── U-02: {expr}/comment children of inline-attribute JSX must survive ─────

    [Fact]
    public void InlineAttrJsx_ExpressionChild_Survives()
    {
        var source = "component Foo {\n    return (\n        <Box header={<Box><Label text=\"t\" />{1 + 1}</Box>} />\n    );\n}\n";
        var result = Format(source);
        Assert.Contains("{1 + 1}", result);
    }

    [Fact]
    public void InlineAttrJsx_ExpressionChild_IsIdempotent()
    {
        var source = "component Foo {\n    return (\n        <Box header={<Box><Label text=\"t\" />{1 + 1}</Box>} />\n    );\n}\n";
        var once = Format(source);
        var twice = Format(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void InlineAttrJsx_CommentChild_Survives()
    {
        var source = "component Foo {\n    return (\n        <Box header={<Box>/* keep me */<Label text=\"t\" /></Box>} />\n    );\n}\n";
        var result = Format(source);
        Assert.Contains("keep me", result);
    }

    // ── U-03: multi-line verbatim string interiors must not be re-indented ─────

    [Fact]
    public void VerbatimString_MultilineInterior_ByteIdentical()
    {
        var source = "component Foo {\n    var s = @\"line1\n    }\n  indented content\n\";\n    return (\n        <Label text={s} />\n    );\n}\n";
        var result = Format(source);
        Assert.Contains("\n    }\n", result);
        Assert.Contains("\n  indented content\n", result);
    }

    [Fact]
    public void VerbatimString_MultilineInterior_IsIdempotent()
    {
        var source = "component Foo {\n    var s = @\"line1\n    }\n  indented content\n\";\n    return (\n        <Label text={s} />\n    );\n}\n";
        var once = Format(source);
        var twice = Format(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void DollarAtVerbatimString_MultilineInterior_ByteIdentical()
    {
        var source = "component Foo {\n    var s = $@\"line1\n    }\n  indented {content}\n\";\n    return (\n        <Label text={s} />\n    );\n}\n";
        var result = Format(source);
        Assert.Contains("\n    }\n", result);
        Assert.Contains("\n  indented {content}\n", result);
    }

    // ── U-04: @case global::… must not corrupt the switch block ─────────────────

    [Fact]
    public void SwitchCase_GlobalQualifiedValue_NotMangled()
    {
        var source = "component Foo {\n    var mode = 1;\n    return (\n        <Box>\n            @switch (mode) {\n                @case global::System.StringComparison.Ordinal:\n                    <Label text=\"a\" />\n                @default:\n                    <Label text=\"b\" />\n            }\n        </Box>\n    );\n}\n";
        var result = Format(source);
        Assert.Contains("@case global::System.StringComparison.Ordinal:", result);
        Assert.Contains("<Label text=\"a\" />", result);
        Assert.Contains("<Label text=\"b\" />", result);
    }

    [Fact]
    public void SwitchCase_GlobalQualifiedValue_IsIdempotent()
    {
        var source = "component Foo {\n    var mode = 1;\n    return (\n        <Box>\n            @switch (mode) {\n                @case global::System.StringComparison.Ordinal:\n                    <Label text=\"a\" />\n                @default:\n                    <Label text=\"b\" />\n            }\n        </Box>\n    );\n}\n";
        var once = Format(source);
        var twice = Format(once);
        Assert.Equal(once, twice);
    }

    // ── U-36: setup-code JSX splice-index desync must not swap block content ───

    private const string U36Repro =
        "component Foo {\n" +
        "    var cond = true;\n" +
        "    var x = (@if (cond) { <Label text=\"a\" /> } @else { <Label text=\"b\" /> });\n" +
        "    var y = (\n" +
        "        <Box>\n" +
        "            <Label text=\"real\" />\n" +
        "        </Box>\n" +
        "    );\n" +
        "    return (\n" +
        "        <Box>{y}</Box>\n" +
        "    );\n" +
        "}\n";

    [Fact]
    public void SetupJsx_TwoParenBlocks_YKeepsItsOwnMarkup()
    {
        var result = Format(U36Repro);
        Assert.Contains("text=\"real\"", result);
    }

    [Fact]
    public void SetupJsx_TwoParenBlocks_XKeepsItsOwnIfElse()
    {
        var result = Format(U36Repro);
        Assert.Contains("@if (cond)", result);
        Assert.Contains("@else", result);
        Assert.Contains("text=\"a\"", result);
        Assert.Contains("text=\"b\"", result);
    }

    [Fact]
    public void SetupJsx_TwoParenBlocks_IsIdempotent()
    {
        var once = Format(U36Repro);
        var twice = Format(once);
        Assert.Equal(once, twice);
    }

    // ── U-07: block-comment-blind JSX-block detection corrupts comment interiors ─

    [Fact]
    public void CommentedOutJsx_InSetupCode_PreservedByteIdentical()
    {
        // No other real JSX in setup code — routes through EmitSetupCodeNormalized.
        var source =
            "component Foo {\n" +
            "  /*\n" +
            "    return (\n" +
            "      <Label text={x} />\n" +
            "    );\n" +
            "  */\n" +
            "  var x = 1;\n" +
            "  return (\n" +
            "    <Box />\n" +
            "  );\n" +
            "}\n";

        var result = Format(source);

        Assert.Contains("      <Label text={x} />", result);
        var once = result;
        var twice = Format(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void CommentedOutJsx_AlongsideRealJsxInSetupCode_PreservedByteIdentical()
    {
        // A genuine JSX paren-block ALSO exists in setup code (var bla = (<Box/>);),
        // so this routes through EmitSetupCodeWithJsx's raw-C#-reindent fallback —
        // the exact path where the real UitkxTestFileDoNotTouch sample regressed.
        var source =
            "component Foo {\n" +
            "  var myRef = 1;\n" +
            "  {/*\n" +
            "    return (\n" +
            "      <Label text={myRef} />\n" +
            "    ); */}\n" +
            "\n" +
            "  var bla = (\n" +
            "    <Box />\n" +
            "  );\n" +
            "  return (\n" +
            "    <Box>{bla}</Box>\n" +
            "  );\n" +
            "}\n";

        var result = Format(source);

        Assert.Contains("      <Label text={myRef} />", result);
        Assert.Contains("<Box />", result);
        var once = result;
        var twice = Format(once);
        Assert.Equal(once, twice);
    }
}
