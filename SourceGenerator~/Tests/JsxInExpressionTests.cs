using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Phase 1 — JSX literals in arbitrary expression positions.
///
/// <para>The generator's <see cref="DirectiveParser"/> scanner has long
/// supported JSX in component preambles and directive bodies. Phase 1 wires
/// the same scanner into the two remaining expression positions:</para>
/// <list type="bullet">
///   <item>Child expressions: <c>{cond ? &lt;A/&gt; : &lt;B/&gt;}</c></item>
///   <item>Attribute values:  <c>attr={cond ? &lt;A/&gt; : null}</c>,
///         <c>attr={x =&gt; &lt;Item/&gt;}</c></item>
/// </list>
/// <para>Result: JSX is allowed wherever a C# expression is allowed,
/// matching React/Babel semantics.</para>
/// </summary>
public class JsxInExpressionTests
{
    private static string Wrap(string markup) =>
        "VirtualNode MyComp() {\n  return (\n" + markup + "\n  );\n}";

    private static string WrapWithSetup(string setup, string markup) =>
        "VirtualNode MyComp() {\n" + setup + "\n  return (\n" + markup + "\n  );\n}";

    // ── Child expression: {cond ? <A/> : <B/>} ──────────────────────────────

    [Fact]
    public void Child_TernaryWithJsxBranches_BothSpliced()
    {
        var src = WrapWithSetup(
            "  bool flag = true;",
            "<box>{flag ? <label text=\"A\"/> : <label text=\"B\"/>}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("), "Expected V.Label inside ternary branches");
        Assert.True(result.SourceContains("V.Box("), "Expected outer V.Box");
        // No raw JSX should leak into emitted code.
        Assert.False(
            result.SourceContains("<label text=\"A\"/>"),
            "Raw JSX should be spliced, not pass through"
        );
    }

    [Fact]
    public void Child_TernaryWithSingleJsxBranch_Spliced()
    {
        var src = WrapWithSetup(
            "  bool flag = true;",
            "<box>{flag ? <label text=\"hi\"/> : null}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("));
        Assert.True(result.SourceContains("? V.Label(") || result.SourceContains("?V.Label("));
    }

    // ── Attribute value: attr={cond ? <A/> : null} ──────────────────────────

    [Fact]
    public void Attribute_TernaryWithJsx_Spliced()
    {
        // attr={cond ? <A/> : null} on a built-in element. We use a dictionary-style
        // built-in to sidestep prop-type lookups.
        var src = WrapWithSetup(
            "  bool flag = true;",
            "<box class={flag ? \"on\" : \"off\"}/>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Box("));
        Assert.True(result.SourceContains("\"on\""));
    }

    [Fact]
    public void Attribute_LambdaReturningJsx_Spliced()
    {
        // attr={x => <Item/>} — lambda body contains JSX.
        // We verify scanner detects bare JSX after `=>` and splices.
        var src = WrapWithSetup(
            "  System.Func<int, global::Ruitk.Core.VirtualNode> renderItem = i => <label text=\"item\"/>;",
            "<box>{renderItem(0)}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("), "Lambda body JSX should splice");
    }

    // ── Edge cases the scanner must NOT confuse with JSX ────────────────────

    [Fact]
    public void GenericLessThan_NotConfusedWithJsx()
    {
        var src = WrapWithSetup(
            "  var items = new System.Collections.Generic.List<int>();\n  bool small = items.Count < 1;",
            "<label text={small ? \"empty\" : \"has\"}/>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        // Comparison operator must be preserved, not interpreted as JSX.
        Assert.True(result.SourceContains("Count < 1"));
    }

    [Fact]
    public void StringContainingTagLikeText_NotSpliced()
    {
        var src = WrapWithSetup(
            "  string s = \"<label/>\";",
            "<label text={s}/>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        // The string literal "<label/>" must remain a string, not become V.Label(...).
        Assert.True(result.SourceContains("\"<label/>\""));
    }

    [Fact]
    public void NullCoalesceWithJsx_Spliced()
    {
        var src = WrapWithSetup(
            "  global::Ruitk.Core.VirtualNode? fallback = null;",
            "<box>{fallback ?? <label text=\"fallback\"/>}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("));
        Assert.True(result.SourceContains("?? V.Label(") || result.SourceContains("??V.Label("));
    }

    [Fact]
    public void NoJsxInExpression_ScannerIsNoOp()
    {
        // Sanity: expressions without JSX are emitted verbatim — the scanner
        // should run, find nothing, and return the input unchanged.
        var src = WrapWithSetup(
            "  int n = 42;",
            "<label text={n.ToString()}/>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("n.ToString()"));
    }

    // ── Existing JsxExpressionValue path is preserved (cleaner emit) ────────

    [Fact]
    public void Attribute_BareJsxLiteral_StillUsesJsxExpressionValue()
    {
        // attr={<Tag/>} — opt-in path predates Phase 1 and produces cleaner emit.
        // Phase 1 must NOT regress it.
        var src = Wrap("<box class={\"red\"}><label text=\"hi\"/></box>");
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("));
        Assert.True(result.SourceContains("V.Box("));
    }

    // ── Phase 1.5 — `cond && <Tag/>` short-circuit desugar ─────────────────
    //
    // The React idiom `{cond && <Tag/>}` is impossible to emit verbatim because
    // C# `&&` is bool-only and `bool && VirtualNode` is CS0019. The splice
    // layer rewrites these expressions to a ternary
    //   ((cond) ? V.Tag(...) : (VirtualNode?)null)
    // reusing the already-tested ternary emit path. The `null` branch is
    // dropped at render time by `__C(params object[])` which filters nulls.

    [Fact]
    public void LogicalAnd_SimpleBool_DesugaredToTernary()
    {
        var src = WrapWithSetup(
            "  bool flag = true;",
            "<box>{flag && <label text=\"hi\"/>}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("));
        // Ternary opening: ((flag) ?
        Assert.True(
            result.SourceContains("((flag) ? "),
            "Expected `((flag) ? ` opening of the desugared ternary."
        );
        // Null fallback branch
        Assert.True(
            result.SourceContains(": (global::Ruitk.Core.VirtualNode?)null)"),
            "Expected typed-null fallback branch."
        );
        // Raw `&&` should NOT survive in the emitted code (sanity).
        Assert.False(
            result.SourceContains("flag && V.Label"),
            "Raw `&&` should be desugared, not pass through."
        );
    }

    [Fact]
    public void LogicalAnd_NullCheck_DesugaredToTernary()
    {
        // The exact user-reported repro: `icon != null && <Image .../>`.
        // Ensures the LHS walker preserves the `!=` comparison expression
        // as the ternary condition, not just the bare identifier.
        var src = WrapWithSetup(
            "  global::UnityEngine.Texture2D? icon = null;",
            "<box>{icon != null && <Image texture={icon} />}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Image("));
        Assert.True(
            result.SourceContains("((icon != null) ? "),
            "Expected `((icon != null) ? ` — LHS walker must keep the full comparison."
        );
        Assert.True(
            result.SourceContains(": (global::Ruitk.Core.VirtualNode?)null)")
        );
    }

    [Fact]
    public void LogicalAnd_ParenthesizedLhs_PreservedByWalker()
    {
        // `(x.Count > 0) && <X/>` — the LHS walker must include the entire
        // parenthesised expression, not just `0)`.
        var src = WrapWithSetup(
            "  System.Collections.Generic.List<int> x = new();",
            "<box>{(x.Count > 0) && <label text=\"non-empty\"/>}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("));
        Assert.True(
            result.SourceContains("(((x.Count > 0)) ? "),
            "Expected `(((x.Count > 0)) ? ` — LHS walker must wrap the full parenthesised expression."
        );
    }

    [Fact]
    public void LogicalAnd_MethodCallLhs_PreservedByWalker()
    {
        // `IsActive(item) && <X/>` — the walker must balance method-call parens
        // and treat the whole call as a single LHS expression.
        var src = WrapWithSetup(
            "  bool IsActive(int x) => x > 0; var item = 1;",
            "<box>{IsActive(item) && <label text=\"on\"/>}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("));
        Assert.True(
            result.SourceContains("((IsActive(item)) ? "),
            "Expected `((IsActive(item)) ? ` — walker must balance method-call parens."
        );
    }

    [Fact]
    public void LogicalAnd_NestedInTernary_RespectsBindingBoundaries()
    {
        // `a ? b : c && <X/>` parses as `a ? b : (c && <X/>)` per C# precedence.
        // The LHS of `&&` is just `c`, NOT `a ? b : c`. The walker must stop
        // at the `:` boundary.
        var src = WrapWithSetup(
            "  bool a = true; global::Ruitk.Core.VirtualNode? b = null; bool c = true;",
            "<box>{(a ? b : c && <label text=\"x\"/>)}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("));
        // Walker must produce `((c) ?`, NOT `((a ? b : c) ?`.
        Assert.True(
            result.SourceContains("((c) ? "),
            "Expected `((c) ? ` — LHS walker must stop at the `:` ternary boundary."
        );
        Assert.False(
            result.SourceContains("((a ? b : c) ?"),
            "LHS walker must NOT include the outer ternary in the LHS expression."
        );
    }

    [Fact]
    public void LogicalAnd_NestedInOr_RespectsBindingBoundaries()
    {
        // `a || b && <X/>` — `&&` binds tighter than `||`, so LHS = `b`.
        var src = WrapWithSetup(
            "  bool a = false; bool b = true;",
            "<box>{a || b && <label text=\"x\"/>}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("));
        Assert.True(
            result.SourceContains("((b) ? "),
            "Expected `((b) ? ` — LHS walker must stop at the `||` boundary."
        );
    }

    [Fact]
    public void BitwiseAnd_NotMistakenForLogical()
    {
        // `(a & b) > 0 ? <x/> : <y/>` — the single `&` is bitwise, not the
        // logical-AND trigger. Only the existing `?:` splice should fire.
        var src = WrapWithSetup(
            "  int a = 1; int b = 2;",
            "<box>{((a & b) > 0 ? <label text=\"on\"/> : <label text=\"off\"/>)}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("));
        // No `&&` desugar should have fired — its hallmark is the typed-null
        // fallback branch, which the user-written `?:` would not produce.
        Assert.False(
            result.SourceContains("(global::Ruitk.Core.VirtualNode?)null)"),
            "Bitwise `&` must not trigger the logical-AND desugar (no typed-null fallback)."
        );
    }

    [Fact]
    public void LogicalAnd_AtExpressionStart_EmitsUITKX0026()
    {
        // Degenerate input: `&&` with no LHS. Walker returns -1 → splicer
        // emits `#error UITKX0026` directive and drops the JSX so the user
        // gets ONE clear diagnostic instead of a CS0019 cascade.
        var src = WrapWithSetup(
            "",
            "<box>{ && <label text=\"x\"/>}</box>"
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("UITKX0026"),
            "Expected `UITKX0026` diagnostic when LHS walker fails."
        );
    }
}
