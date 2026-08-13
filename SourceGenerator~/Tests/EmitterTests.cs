using System.Linq;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// Integration tests that run the full <see cref="UitkxGenerator"/> pipeline
/// and assert properties of the generated C# source text.
/// </summary>
public class EmitterTests
{
    /// Wraps markup in a minimal function-style component for test convenience.
    private static string Wrap(string markup) =>
        "VirtualNode MyComp() {\n  return (\n" + markup + "\n  );\n}";

    // ── Element emission ──────────────────────────────────────────────────────

    [Fact]
    public void SimpleElement_GeneratesVCall()
    {
        var result = GeneratorTestHelper.Run(Wrap("<label/>"));

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("V.Label("), "Expected a V.Label( call");
    }

    [Fact]
    public void StringAttribute_AppearsInVCall()
    {
        var result = GeneratorTestHelper.Run(Wrap("""<label text="Hello World"/>"""));

        Assert.True(
            result.SourceContains("Hello World"),
            "Expected string literal in generated source"
        );
    }

    [Fact]
    public void ExpressionAttribute_AppearsVerbatim()
    {
        var result = GeneratorTestHelper.Run(Wrap("<label text={count}/>"));

        Assert.True(
            result.SourceContains("count"),
            "Expected expression verbatim in generated source"
        );
    }

    [Fact]
    public void NestedElements_GeneratesNestedVCalls()
    {
        var result = GeneratorTestHelper.Run(Wrap("<box><label/></box>"));

        Assert.True(result.SourceContains("V.Box("), "Expected V.Box call");
        Assert.True(result.SourceContains("V.Label("), "Expected V.Label call inside box");
    }

    // ── Mixed-decl emit (§7) ──────────────────────────────────────────────────

    [Fact]
    public void MultiComponent_BothPartialClassesEmitted()
    {
        var src = "VirtualNode First() {\n  return (<Box />);\n}\nVirtualNode Second() {\n  return (<Label text=\"x\" />);\n}";
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        // First is the primary source; Second lands in an ExtraSources unit.
        Assert.True(result.SourceContains("class First"), "Expected the First partial class");
        Assert.Contains(result.AllSources, s => s.Text.Contains("class Second"));
        // Every emitted unit must be syntactically valid C# — the concatenated
        // path produced CS1529 (misplaced using after a namespace); the
        // per-section AddSource path must not.
        Assert.Empty(result.SyntaxErrors());
    }

    [Fact]
    public void MixedComponentAndHook_BothEmitted()
    {
        var src = "VirtualNode Screen() {\n  var c = useLocal();\n  return (<Box />);\n}\nvoid useLocal() {\n  return 0;\n}";
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("class Screen"), "Expected the Screen component partial");
        Assert.Contains(result.AllSources, s => s.Text.Contains("useLocal"));
        Assert.Empty(result.SyntaxErrors());
    }

    [Fact]
    public void DuplicateComponentName_InSameFile_RaisesUitkx0113_AndStaysValid()
    {
        // Two `component Foo` in one file would emit duplicate partial classes/members
        // (CS0111/CS0101). UITKX0113 flags it and the duplicate is skipped so the
        // surviving output is still syntactically valid.
        var src = "VirtualNode Foo() {\n  return (<Box />);\n}\nVirtualNode Foo() {\n  return (<Label text=\"x\" />);\n}";
        var result = GeneratorTestHelper.Run(src);

        Assert.Contains(result.Diagnostics, d => d.Id == "UITKX0113");
        Assert.Empty(result.SyntaxErrors());
        // The Location.None diagnostic is re-surfaced as a #warning so Unity shows it
        // (Unity silently drops Location.None diagnostics on a .uitkx).
        Assert.Contains(result.AllSources, s => s.Text.Contains("#warning UITKX0113"));
    }

    [Fact]
    public void MultiComponent_SiblingReference_NoFalseUnknownComponentWarning()
    {
        // Card references its same-file sibling CardList. Both must be registered as
        // peers, else CardList resolves as unknown → false UITKX0008.
        var src = "VirtualNode CardList() {\n  return (<Label text=\"x\" />);\n}\nVirtualNode Card() {\n  return (<CardList />);\n}";
        var result = GeneratorTestHelper.Run(src);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "UITKX0008");
        Assert.Empty(result.SyntaxErrors());
    }

    [Fact]
    public void ComponentNamedLikeSectionLabel_NoAddSourceHintCollision()
    {
        // A component named "Inlinehooks" (PascalCase — lowercase is rejected by UITKX2100)
        // would, without index-prefixed hints, collide CASE-INSENSITIVELY with the
        // inline-hooks section's "inlinehooks" hint (a duplicate hint throws during
        // generation). Index-prefixed labels ("c2_Inlinehooks") keep them disjoint.
        var src = "VirtualNode Screen() {\n  var c = useLocal();\n  return (<Box />);\n}\n"
            + "void useLocal() {\n  return 0;\n}\n"
            + "VirtualNode Inlinehooks() {\n  return (<Label text=\"x\" />);\n}";
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.Empty(result.SyntaxErrors());
        Assert.Contains(result.AllSources, s => s.Text.Contains("partial class Inlinehooks"));
    }

    [Fact]
    public void HookAndModuleOnlyFile_EmitsSeparateValidUnits()
    {
        // A member-only file (hook + value, no component) takes the short-circuit
        // path and emits one valid __Exports unit.
        var src = "void useThing() {\n}\nexport int Gap = 4;\n";
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        var syntaxErrors = result.SyntaxErrors();
        Assert.True(syntaxErrors.IsEmpty,
            "Member-only file must emit valid units; got: "
                + string.Join("; ", syntaxErrors.Select(d => d.ToString())));
        Assert.Contains(result.AllSources, s => s.Text.Contains("useThing"));
        Assert.Contains(result.AllSources, s => s.Text.Contains("Gap = 4"));
    }

    [Fact]
    public void MixedComponentHookAndModule_AllUnitsAreSyntacticallyValid()
    {
        // Regression for the CS1529 concat bug: a component + hook + module in one
        // file previously emitted a single concatenated unit whose 2nd/3rd
        // sections' `using` directives followed a namespace. Now each section is
        // its own compilation unit; all must parse clean.
        var src =
            "VirtualNode Screen() {\n  var c = useLocal();\n  return (<Box />);\n}\n"
            + "void useLocal() {\n}\n"
            + "export int Gap = 4;\n";
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        var syntaxErrors = result.SyntaxErrors();
        Assert.True(syntaxErrors.IsEmpty,
            "Mixed-decl emit must be syntactically valid; got: "
                + string.Join("; ", syntaxErrors.Select(d => d.ToString())));
        // Two distinct units: the component (primary) + the per-file __Exports.
        Assert.True(result.AllSources.Length >= 2, "Expected component + __Exports units");
    }

    // ── Accessibility (§6): export → public, else internal ────────────────────

    [Fact]
    public void ExportedComponent_IsPublic()
    {
        var result = GeneratorTestHelper.Run("export VirtualNode Foo() {\n  return (<Box />);\n}");
        Assert.True(result.SourceContains("public partial class Foo"), "Expected public for exported component");
    }

    [Fact]
    public void NonExportedComponent_IsInternal()
    {
        var result = GeneratorTestHelper.Run("VirtualNode Foo() {\n  return (<Box />);\n}");
        Assert.True(result.SourceContains("internal partial class Foo"), "Expected internal for non-exported component");
    }

    // (The module-accessibility and companion-merge tests died with the legacy module
    // grammar in the 0.16.0 removal wave — __Exports accessibility is pinned by
    // ExportsEmitterTests, and companion partial-class merging no longer exists.)

    [Fact]
    public void HookFamilyKey_ImportedHook_ProducerAndConsumerAgreeOnQualifiedKey()
    {
        // §7: the hook file (producer) registers under a PATH-QUALIFIED family key, and a
        // component that imports+calls the hook (consumer) must emit the BYTE-IDENTICAL key
        // in its customHookFamilyKeys — the runtime matches them by ordinal string equality.
        var files = new[]
        {
            ("Counter.hooks.uitkx", "export void useCounter() {\n  return 0;\n}"),
            ("Screen.uitkx",
                "import { useCounter } from \"./Counter.hooks\"\n"
                + "export VirtualNode Screen() {\n  var c = useCounter();\n  return (<Box />);\n}"),
        };
        var result = GeneratorTestHelper.RunMultiple(files, primaryFileName: "Screen.uitkx");

        // No asmdef/project-root in the test temp dir → both files fall back to the default
        // namespace; the container is the per-file __Exports (0.16.0 — the legacy
        // {Stem}Hooks container is gone).
        const string expectedKey = "Ruitk.FunctionStyle.__Exports::useCounter";

        var hookUnit = result.AllSources.Single(s => s.Text.Contains("RegisterHook("));
        Assert.Contains($"RegisterHook(\"{expectedKey}\"", hookUnit.Text);

        var compUnit = result.AllSources.Single(s => s.Text.Contains("class Screen"));
        Assert.Contains(expectedKey, compUnit.Text);
        // And the bare name must NOT leak as a standalone key on either side.
        Assert.DoesNotContain("RegisterHook(\"useCounter\"", hookUnit.Text);
        Assert.Empty(result.SyntaxErrors());
    }

    [Fact]
    public void HookFamilyKey_SameFileHook_QualifiedWithOwnContainer()
    {
        // A component and the hook it calls in the SAME file (mixed-decl): the consumer key
        // must qualify against the file's own namespace + container, matching the producer.
        var src =
            "VirtualNode Screen() {\n  var c = useLocal();\n  return (<Box />);\n}\n"
            + "void useLocal() {\n  return 0;\n}";
        var result = GeneratorTestHelper.Run(src, fileName: "Panel.uitkx");

        const string expectedKey = "Ruitk.FunctionStyle.__Exports::useLocal";
        Assert.Contains(result.AllSources, s => s.Text.Contains($"RegisterHook(\"{expectedKey}\""));
        Assert.Contains(result.AllSources, s => s.Text.Contains("class Screen") && s.Text.Contains(expectedKey));
        Assert.Empty(result.SyntaxErrors());
    }

    // (CrossFileCompanionModule / ExportMatch merging tests removed with companion
    // partial-class merging — 0.16.0 legacy removal.)

    // ── Namespace / class structure ──────────────────────────────────────────

    [Fact]
    public void Namespace_EmittedInGeneratedSource()
    {
        var result = GeneratorTestHelper.Run(Wrap("<box/>"));

        Assert.True(
            result.SourceContains("namespace Ruitk.FunctionStyle"),
            "Expected namespace declaration"
        );
    }

    [Fact]
    public void ClassName_MatchesComponentDirective()
    {
        var result = GeneratorTestHelper.Run(Wrap("<box/>"));

        Assert.True(
            result.SourceContains("partial class MyComp"),
            "Expected partial class named MyComp"
        );
    }

    [Fact]
    public void FunctionStyleComponent_GeneratesClassAndMarkup()
    {
        const string src = """
            VirtualNode CounterPanel() {
                var (count, setCount) = useState(0);
                return (
                    <Box>
                        <Label text={$"{count}"} />
                    </Box>
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "CounterPanel.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("partial class CounterPanel"));
        Assert.True(result.SourceContains("V.Box("));
        Assert.True(result.SourceContains("V.Label("));
        Assert.True(result.SourceContains("Hooks.UseState(") || result.SourceContains("useState("));
    }

    // ── Control flow ─────────────────────────────────────────────────────────

    [Fact]
    public void IfDirective_GeneratesConditionalExpression()
    {
        var src = Wrap("@if (flag) { return (<label/>); }");
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        // @if → IIFE; the condition should appear in the generated source
        Assert.True(result.SourceContains("flag"), "Condition should appear verbatim");
        Assert.True(
            result.SourceContains("Func<VirtualNode>")
                || result.SourceContains("Func<")
                || result.SourceContains("? ")
                || result.SourceContains("?V.")
                || result.SourceContains("?("),
            "IIFE or ternary expected for @if.\nGenerated:\n" + (result.GeneratedSource ?? "<null>")
        );
    }

    [Fact]
    public void ForeachDirective_GeneratesSelectCall()
    {
        var src = Wrap("@foreach (var item in items) { return (<label key={item.Id}/>); }");
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        // @foreach → IIFE with list-building loop or .Select()
        Assert.True(
            result.SourceContains(".Select(") || result.SourceContains("foreach"),
            "Expected .Select( or foreach loop for @foreach"
        );
    }

    [Fact]
    public void ForeachDirective_WithSetupCode_GeneratesIIFE()
    {
        var src = Wrap(
            """
            <box>
                @foreach (var entry in log) {
                    var a = "test";
                    return (
                        <label key={entry} text={entry} />
                    );
                }
                <label text="after" />
            </box>
            """
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(
            result.SourceWasProduced,
            "Expected generated source.\n"
                + string.Join("\n", result.Diagnostics.Select(d => $"  {d.Id}: {d.GetMessage()}"))
        );
        Assert.True(
            result.SourceContains("var a = \"test\""),
            "Expected setup code in output. Generated:\n" + (result.GeneratedSource ?? "<null>")
        );
        // Inner per-iteration IIFE should NOT exist (OPT-10)
        Assert.False(
            result.SourceContains("Func<global::Ruitk.Core.VirtualNode>>)"),
            "Inner per-iteration IIFE should be eliminated (OPT-10).\nGenerated:\n"
                + (result.GeneratedSource ?? "<null>")
        );
    }

    [Fact]
    public void ForeachDirective_WithSetupCode_InRealComponent_GeneratesValidCSharp()
    {
        // Mimics a real component: function-level hooks PLUS a foreach with setup code
        var src = """
            VirtualNode TestComp() {
                var (count, setCount) = useState(0);
                var (log, setLog) = useState(new System.Collections.Generic.List<string> { "Ready." });
                return (
                    <box>
                        <label text={count.ToString()} />
                        @foreach (var entry in log) {
                            var prefix = "[LOG] ";
                            return (
                                <label key={entry} text={prefix + entry} />
                            );
                        }
                        <label text="footer" />
                    </box>
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src);

        Assert.True(
            result.SourceWasProduced,
            "Expected generated source but got diagnostics:\n"
                + string.Join("\n", result.Diagnostics.Select(d => $"  {d.Id}: {d.GetMessage()}"))
        );

        // Verify structure — outer IIFE brace balance must be correct
        Assert.True(
            result.SourceContains("var prefix = \"[LOG] \""),
            "Expected foreach setup code in generated output.\nGenerated:\n"
                + (result.GeneratedSource ?? "<null>")
        );

        // Verify body code is inlined directly (no inner per-iteration IIFE)
        Assert.True(
            result.SourceContains("__r.Add("),
            "Expected __r.Add() for inlined return.\nGenerated:\n"
                + (result.GeneratedSource ?? "<null>")
        );

        // Verify the closing pattern: foreach body brace + outer IIFE brace
        Assert.True(
            result.SourceContains("} return __r.ToArray(); }))()"),
            "Outer IIFE must have exactly 2 closing braces (foreach body + IIFE lambda).\nGenerated:\n"
                + (result.GeneratedSource ?? "<null>")
        );
        Assert.False(
            result.SourceContains("}} return __r.ToArray();"),
            "Double '}}' in plain string = 4 braces (bug). Must use single '}' each.\nGenerated:\n"
                + (result.GeneratedSource ?? "<null>")
        );

        // Verify no false UITKX0014 (hook in loop)
        Assert.False(
            result.HasDiagnostic("UITKX0014"),
            "UITKX0014 should NOT fire — hooks are at component top level, not inside @foreach.\n"
                + string.Join("\n", result.Diagnostics.Select(d => $"  {d.Id}: {d.GetMessage()}"))
        );
    }

    [Fact]
    public void ForDirective_WithLoopFlow_EmitsBreakAndContinueStatements()
    {
        var src = Wrap(
            """
            @for (var i = 0; i < 10; i++) {
                if (i < 3) { continue; }
                if (i > 6) { break; }
                return (
                    <label text={i.ToString()} />
                );
            }
            """
        );

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("for (var i = 0; i < 10; i++)"), "Expected for loop");
        Assert.True(result.SourceContains("continue;"), "Expected continue statement");
        Assert.True(result.SourceContains("break;"), "Expected break statement");
    }

    [Fact]
    public void WhileDirective_WithLoopFlow_EmitsContinueStatement()
    {
        var src = Wrap(
            """
            @while (running) {
                if (skip) { continue; }
                return (
                    <label />
                );
            }
            """
        );

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("while (running)"), "Expected while loop");
        Assert.True(result.SourceContains("continue;"), "Expected continue statement");
    }

    [Fact]
    public void SwitchDirective_GeneratesSwitchExpression()
    {
        var src = Wrap(
            """
            @switch (mode) {
                @case 0: return (<label text="zero"/>);
                @case 1: return (<label text="one"/>);
            }
            """
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("switch"), "Expected switch statement");
        // Emitter generates IIFE with switch statement
        Assert.True(
            result.SourceContains("Func<") || result.SourceContains("_ =>"),
            "Expected IIFE or switch expression pattern for @switch"
        );
    }

    [Fact]
    public void ElseBranch_GeneratesNullFallback()
    {
        var src = Wrap("@if (flag) { return (<label/>); }");
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced);
        // @if without @else should produce `: null` in the ternary
        Assert.True(
            result.SourceContains(": null") || result.SourceContains("(VirtualNode)null"),
            "Missing @else should generate null fallback"
        );
    }

    // ── Key attribute ─────────────────────────────────────────────────────────

    [Fact]
    public void KeyAttribute_PassedAsSecondArgument()
    {
        var result = GeneratorTestHelper.Run(Wrap("""<label key="my-key" text="Hi"/>"""));

        Assert.True(result.SourceContains("my-key"), "key value should appear in generated source");
    }

    // ── Line directives ───────────────────────────────────────────────────────

    [Fact]
    public void GeneratedSource_ContainsLineDirectives()
    {
        var result = GeneratorTestHelper.Run(Wrap("<label/>"));

        // #line directives must reference the original .uitkx file
        Assert.True(
            result.SourceContains("#line"),
            "Expected #line directives in generated source"
        );

        // Must carry a concrete path to avoid Unity virtual generator hyperlinks
        // like Ruitk.SourceGenerator\...\Foo.uitkx(...).
        Assert.True(
            result.SourceContains("/TestComponent.uitkx\""),
            "Expected #line to include normalized source file path"
        );
        Assert.False(
            result.SourceContains("Ruitk.SourceGenerator\\"),
            "#line should not point to virtual generator paths"
        );
    }

    // ── [UitkxElement] attribute ─────────────────────────────────────────────

    [Fact]
    public void GeneratedClass_HasUitkxElementAttribute()
    {
        // The emitter should emit [global::Ruitk.UitkxElement("MyComp")]
        // on the generated partial class so runtime tooling can discover it.
        var result = GeneratorTestHelper.Run(Wrap("<label/>"));

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("UitkxElement(\"MyComp\")"),
            "Expected [UitkxElement(\"MyComp\")] attribute on the generated partial class"
        );
    }

    [Fact]
    public void CommentInsideElement_DoesNotEmitExtraComma()
    {
        // Before the fix, CommentNode emitted nothing but the comma logic still ran,
        // resulting in invalid C# like V.Box(V.Label(...), , V.Label(...))
        var src = Wrap(
            """
<box>
    // this is a comment
    <label text="a"/>
    // another comment
    <label text="b"/>
</box>
"""
        );
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced, "Source should be produced");
        Assert.False(
            result.SourceContains(", ,"),
            "No double-comma should appear (dangling comma from comment)\n--- GENERATED ---\n"
                + (result.GeneratedSource ?? "(null)")
        );
        Assert.True(result.SourceContains("V.Label("), "Labels should still be emitted");
    }

    [Fact]
    public void CommentAsRootNode_DoesNotBreakSingleRoot()
    {
        // A comment at the root level alongside a single element should not
        // force Fragment wrapping (which would cause a dangling empty argument).
        const string src = """
            VirtualNode MyComp() {
                return (
                    // root comment
                    <box />
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced, "Source should be produced");
        Assert.True(
            result.SourceContains("V.Box("),
            "Box should be emitted\n--- GENERATED ---\n" + (result.GeneratedSource ?? "(null)")
        );
    }

    [Fact]
    public void AssignMarkupWithInterpolatedStrings_GeneratesVCall()
    {
        // Mirrors the real UitkxCounterFunc.uitkx pattern with interpolated strings and onClick lambdas
        const string src = """
            VirtualNode MyComp() {
                var (count, setCount) = useState(0);
                var component = (
                    <box>
                        <button text="-5" onClick={() => setCount(count - 5)} />
                        <label text={$"{count}"} />
                        <button text="+5" onClick={() => setCount(count + 5)} />
                    </box>
                );
                return (
                    <box>{component}</box>
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced, "Source should be produced");
        Assert.True(
            result.SourceContains("V.Box("),
            "Assigned <box> with onClick lambdas should produce V.Box( call.\n--- GENERATED ---\n"
                + (result.GeneratedSource ?? "(null)")
        );
    }

    [Fact]
    public void FunctionStyleComponent_LeadingUsings_EmittedInGeneratedSource()
    {
        const string src = """
            using MyGame.Models;
            using System.Collections.Generic;
            VirtualNode PlayerHUD() {
                return (<Box />);
            }
            """;

        var result = GeneratorTestHelper.Run(src, "PlayerHUD.uitkx");

        Assert.True(result.SourceWasProduced);
        // 0.16.0: user usings emit INSIDE the namespace block, global::-qualified
        // (the file-top legacy position died with legacy mode).
        Assert.True(result.SourceContains("using global::MyGame.Models;"), "Expected using global::MyGame.Models;");
        Assert.True(
            result.SourceContains("using global::System.Collections.Generic;"),
            "Expected using global::System.Collections.Generic;"
        );
    }

    [Fact]
    public void HookAlias_NestedGenericTypeArg_IsExpanded()
    {
        const string src = """
            VirtualNode Foo() {
                var ctx = useContext<Dictionary<string, int>>("myKey");
                return (<Box />);
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Foo.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("Hooks.UseContext<Dictionary<string, int>>("),
            "Nested generic hook alias should be expanded"
        );
    }

    // ── ErrorBoundary emission ───────────────────────────────────────────────

    [Fact]
    public void ErrorBoundary_GeneratesVErrorBoundaryCall()
    {
        const string src = """
            VirtualNode MyComp() {
                return (
                    <ErrorBoundary>
                        <Label text="child" />
                    </ErrorBoundary>
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "MyComp.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("V.ErrorBoundary("),
            $"Expected V.ErrorBoundary( call in generated source. Got:\n{result.GeneratedSource}"
        );
        Assert.True(
            result.SourceContains("ErrorBoundaryProps"),
            "Expected ErrorBoundaryProps in generated source"
        );
    }

    [Fact]
    public void ErrorBoundary_WithFallbackProp_GeneratesCorrectProps()
    {
        const string src = """
            VirtualNode MyComp() {
                var fallback = (<Label text="error!" />);
                return (
                    <ErrorBoundary fallback={fallback} resetKey="v1">
                        <Label text="child" />
                    </ErrorBoundary>
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "MyComp.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("V.ErrorBoundary("),
            $"Expected V.ErrorBoundary( call. Got:\n{result.GeneratedSource}"
        );
        Assert.True(
            result.SourceContains("Fallback = fallback"),
            "Expected Fallback prop assignment"
        );
        Assert.True(
            result.SourceContains("ResetKey = \"v1\""),
            "Expected ResetKey prop assignment"
        );
    }

    [Fact]
    public void Suspense_WithIsReadyAndFallback_GeneratesVSuspenseCall()
    {
        const string src = """
            VirtualNode MyComp() {
                bool IsReady() => true;
                var fallback = (<Label text="loading..." />);
                return (
                    <Suspense isReady={IsReady} fallback={fallback}>
                        <Label text="content" />
                    </Suspense>
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "MyComp.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("V.Suspense("),
            $"Expected V.Suspense( call. Got:\n{result.GeneratedSource}"
        );
        Assert.True(
            result.SourceContains("IsReady"),
            "Expected IsReady expression in V.Suspense call"
        );
        Assert.True(
            result.SourceContains("fallback"),
            "Expected fallback expression in V.Suspense call"
        );
    }

    [Fact]
    public void Portal_WithTargetAndChildren_GeneratesVPortalCall()
    {
        const string src = """
            VirtualNode MyComp() {
                var target = useContext<UnityEngine.UIElements.VisualElement>("slot");
                return (
                    <Portal target={target}>
                        <Label text="inside portal" />
                    </Portal>
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "MyComp.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("V.Portal("),
            $"Expected V.Portal( call. Got:\n{result.GeneratedSource}"
        );
        Assert.True(result.SourceContains("target"), "Expected target expression in V.Portal call");
    }

    // ── Peer component props resolution ──────────────────────────────────────

    [Fact]
    public void PeerComponent_WithProps_EmitsTypedVFuncAndPassesAttributes()
    {
        // ChildComp.uitkx — sub-component with a bool prop
        const string childSrc = """
            VirtualNode ChildComp(bool active = false) {
                return (<Label text="child" />);
            }
            """;

        // ParentComp.uitkx — references ChildComp; its props type is peer-generated
        const string parentSrc = """
            VirtualNode ParentComp() {
                var flag = true;
                return (
                    <ChildComp active={flag} />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("ChildComp.uitkx", childSrc), ("ParentComp.uitkx", parentSrc) },
            primaryFileName: "ParentComp.uitkx"
        );

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}"
        );
        // Props are generated as nested types (ChildComp.ChildCompProps), so the
        // qualified form must be used to avoid CS0246 at the call site.
        Assert.True(
            result.SourceContains(
                "V.Func<global::Ruitk.FunctionStyle.ChildComp.ChildCompProps>"
            ),
            $"Expected typed V.Func<global::Ruitk.FunctionStyle.ChildComp.ChildCompProps> call. Got:\n{result.GeneratedSource}"
        );
        // The 'active' attribute must be passed through as 'Active = flag'
        Assert.True(
            result.SourceContains("Active = flag"),
            $"Expected 'Active = flag' prop in generated source. Got:\n{result.GeneratedSource}"
        );
    }

    // ── Nested Props class resolution (C# convention: TypeName.Props) ─────────

    [Fact]
    public void FuncComponent_WithNestedPropsClass_EmitsTypedVFuncAndPassesAttributes()
    {
        // A C# static class that follows the legacy ValuesBarFunc pattern:
        // nested Props class rather than the {TypeName}Props sibling convention.
        const string extraCSharp = """
            using Ruitk.Core;
            using System.Collections.Generic;

            namespace MyApp
            {
                public static class ValuesBarFunc
                {
                    public sealed class Props : IProps
                    {
                        public IEnumerable<System.Collections.Generic.KeyValuePair<string,string>> Items { get; set; }
                    }

                    public static VirtualNode Render(IProps rawProps, IReadOnlyList<VirtualNode> children)
                        => null;
                }
            }
            """;

        const string uitkx = """
            @namespace MyApp.UI
            @using MyApp

            VirtualNode MyPage() {
                var items = new List<System.Collections.Generic.KeyValuePair<string,string>>();
                return (<ValuesBarFunc items={items} />);
            }
            """;

        var result = GeneratorTestHelper.RunWithExtraCSharp(uitkx, extraCSharp, "MyPage.uitkx");

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}"
        );

        // Must use the typed V.Func<ValuesBarFunc.Props> overload, not the no-props fallback
        Assert.True(
            result.SourceContains("V.Func<global::MyApp.ValuesBarFunc.Props>"),
            $"Expected typed V.Func<global::MyApp.ValuesBarFunc.Props> call. Got:\n{result.GeneratedSource}"
        );

        // The 'items' attribute must be forwarded as 'Items = items'
        Assert.True(
            result.SourceContains("Items = items"),
            $"Expected 'Items = items' in generated source. Got:\n{result.GeneratedSource}"
        );
    }

    // ── ref={x} routing on user (UITKX) components ────────────────────────────

    [Fact]
    public void UserComponent_SingleRefParam_RefAttrRoutedToMutableRefProp()
    {
        // ChildComp.uitkx — declares a single Hooks.MutableRef parameter.
        const string childSrc = """
            VirtualNode ChildComp(
                Hooks.MutableRef<object>? inputRef = null
            ) {
                return (<Label text="child" />);
            }
            """;

        // ParentComp.uitkx — uses ref={myRef} shorthand on ChildComp.
        const string parentSrc = """
            VirtualNode ParentComp() {
                return (
                    <ChildComp ref={myRef} />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("ChildComp.uitkx", childSrc), ("ParentComp.uitkx", parentSrc) },
            primaryFileName: "ParentComp.uitkx"
        );

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}"
        );

        // The ref={myRef} attribute must be routed to InputRef (PascalCase of inputRef).
        Assert.True(
            result.SourceContains("InputRef = myRef"),
            $"Expected 'InputRef = myRef' in generated source. Source:\n{result.GeneratedSource}"
        );

        // No ref-routing diagnostics should be emitted.
        Assert.False(
            result.HasDiagnostic("UITKX0020"),
            "Did not expect UITKX0020 (no ref param) when child has exactly one MutableRef param."
        );
        Assert.False(
            result.HasDiagnostic("UITKX0021"),
            "Did not expect UITKX0021 (ambiguous ref) when child has exactly one MutableRef param."
        );
    }

    [Fact]
    public void UserComponent_NoRefParam_RefAttrEmitsDiagnosticUITKX0020()
    {
        // ChildComp.uitkx — no MutableRef parameter; ref={} cannot be routed.
        const string childSrc = """
            VirtualNode ChildComp(string? label = null) {
                return (<Label text="child" />);
            }
            """;

        const string parentSrc = """
            VirtualNode ParentComp() {
                return (
                    <ChildComp ref={myRef} />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("ChildComp.uitkx", childSrc), ("ParentComp.uitkx", parentSrc) },
            primaryFileName: "ParentComp.uitkx"
        );

        Assert.True(
            result.HasDiagnostic("UITKX0020"),
            $"Expected UITKX0020 when ref={{}} is used on a component with no MutableRef param. "
                + $"Diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}"
        );
    }

    [Fact]
    public void UserComponent_MultipleRefParams_RefAttrEmitsDiagnosticUITKX0021()
    {
        // ChildComp.uitkx — two MutableRef parameters; ref={} is ambiguous.
        const string childSrc = """
            VirtualNode ChildComp(
                Hooks.MutableRef<object>? inputRef  = null,
                Hooks.MutableRef<object>? secondRef = null
            ) {
                return (<Label text="child" />);
            }
            """;

        const string parentSrc = """
            VirtualNode ParentComp() {
                return (
                    <ChildComp ref={myRef} />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("ChildComp.uitkx", childSrc), ("ParentComp.uitkx", parentSrc) },
            primaryFileName: "ParentComp.uitkx"
        );

        Assert.True(
            result.HasDiagnostic("UITKX0021"),
            $"Expected UITKX0021 when ref={{}} is used on a component with multiple MutableRef params. "
                + $"Diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}"
        );
    }

    [Fact]
    public void UserComponent_RefAttrAndOtherProps_CombineCorrectly()
    {
        // ChildComp.uitkx — has a regular string prop + one MutableRef prop.
        const string childSrc = """
            VirtualNode ChildComp(
                string? label = null,
                Hooks.MutableRef<object>? inputRef = null
            ) {
                return (<Label text="child" />);
            }
            """;

        // ParentComp.uitkx — passes both a regular attribute and ref={}.
        const string parentSrc = """
            VirtualNode ParentComp() {
                return (
                    <ChildComp label="hello" ref={myRef} />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("ChildComp.uitkx", childSrc), ("ParentComp.uitkx", parentSrc) },
            primaryFileName: "ParentComp.uitkx"
        );

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}"
        );

        // Regular prop must appear in the Props initializer.
        Assert.True(
            result.SourceContains("Label = \"hello\""),
            $"Expected 'Label = \"hello\"' in generated source. Source:\n{result.GeneratedSource}"
        );

        // ref={} must be routed to InputRef.
        Assert.True(
            result.SourceContains("InputRef = myRef"),
            $"Expected 'InputRef = myRef' in generated source. Source:\n{result.GeneratedSource}"
        );

        // No routing diagnostics.
        Assert.False(result.HasDiagnostic("UITKX0020"), "Unexpected UITKX0020.");
        Assert.False(result.HasDiagnostic("UITKX0021"), "Unexpected UITKX0021.");
    }

    [Fact]
    public void CrossNamespacePeerComponent_WithProps_EmitsFullyQualifiedPeerReferences()
    {
        const string childSrc = """
            @namespace MyApp.Components.Buttons

            VirtualNode JSOAppButton(bool disabled = false) {
                return (<Label text="child" />);
            }
            """;

        const string parentSrc = """
            @namespace MyApp.Components.Sidebar
            using MyApp.Components.Buttons;

            VirtualNode Sidebar() {
                var isDisabled = true;
                return (
                    <JSOAppButton disabled={isDisabled} />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("JSOAppButton.uitkx", childSrc), ("Sidebar.uitkx", parentSrc) },
            primaryFileName: "Sidebar.uitkx"
        );

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}"
        );
        Assert.True(
            result.SourceContains(
                "V.Func<global::MyApp.Components.Buttons.JSOAppButton.JSOAppButtonProps>"
            ),
            $"Expected fully qualified peer props type. Got:\n{result.GeneratedSource}"
        );
        // Per-child Family handle is keyed off the peer's FQN so two
        // components named JSOAppButton in different namespaces do not
        // alias in the global registry.
        Assert.True(
            result.SourceContains(
                "__fam_global__MyApp_Components_Buttons_JSOAppButton"
            ),
            $"Expected sanitized Family field for FQN peer. Got:\n{result.GeneratedSource}"
        );
        Assert.True(
            result.SourceContains(
                "GetFamily(\"MyApp.Components.Buttons.JSOAppButton\", () => global::MyApp.Components.Buttons.JSOAppButton.Render)"
            ),
            $"Expected GetFamily call keyed by peer FQN with fallback factory. Got:\n{result.GeneratedSource}"
        );
        Assert.True(
            result.SourceContains("Disabled = isDisabled"),
            $"Expected forwarded prop initializer. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void CrossNamespacePeerComponent_RefRouting_UsesQualifiedPeerIdentity()
    {
        const string childSrc = """
            @namespace MyApp.Components.Buttons

            VirtualNode JSOAppButton(
                Hooks.MutableRef<object>? inputRef = null
            ) {
                return (<Label text="child" />);
            }
            """;

        const string parentSrc = """
            @namespace MyApp.Components.Sidebar
            using MyApp.Components.Buttons;

            VirtualNode Sidebar() {
                return (
                    <JSOAppButton ref={myRef} />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("JSOAppButton.uitkx", childSrc), ("Sidebar.uitkx", parentSrc) },
            primaryFileName: "Sidebar.uitkx"
        );

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}"
        );
        Assert.True(
            result.SourceContains("InputRef = myRef"),
            $"Expected routed ref prop. Got:\n{result.GeneratedSource}"
        );
        Assert.False(result.HasDiagnostic("UITKX0020"), "Unexpected UITKX0020.");
        Assert.False(result.HasDiagnostic("UITKX0021"), "Unexpected UITKX0021.");
    }

    // ── Asset path resolution ─────────────────────────────────────────────────

    [Fact]
    public void Asset_RelativePath_Resolved()
    {
        var src = """
            VirtualNode Card() {
                return (
                    <label text={Asset<Texture2D>("./avatar.png").name} />
                );
            }
            """;

        // Place the file inside an Assets/ sub-path so GetUitkxAssetDir finds it
        var result = GeneratorTestHelper.Run(src, "Assets/UI/Card.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/UI/avatar.png\""),
            $"Expected resolved path 'Assets/UI/avatar.png'. Got:\n{result.GeneratedSource}"
        );
        Assert.False(
            result.SourceContains("./avatar.png"),
            "Relative path should have been resolved"
        );
    }

    [Fact]
    public void Asset_BarePath_ResolvedUitkxDirRelative()
    {
        // H-03: a bare path (no "./" prefix) previously passed through unresolved in
        // generated code while the LSP analyzer already resolved it uitkx-dir-relative —
        // the editor showed no error while the build emitted an unresolvable path.
        var src = """
            VirtualNode Card() {
                return (
                    <label text={Asset<Texture2D>("avatar.png").name} />
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/Card.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/UI/avatar.png\""),
            $"Expected bare path resolved to 'Assets/UI/avatar.png'. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void Ast_RelativePath_Resolved()
    {
        var src = """
            VirtualNode Card() {
                return (
                    <label text={Ast<Sprite>("../shared/icon.png").name} />
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/Card.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/shared/icon.png\""),
            $"Expected resolved path 'Assets/shared/icon.png'. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void Asset_AbsolutePath_Unchanged()
    {
        var src = """
            VirtualNode Card() {
                return (
                    <label text={Asset<Font>("Assets/Fonts/custom.ttf").name} />
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/Fonts/custom.ttf\""),
            $"Expected absolute path unchanged with extension. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void Asset_AbsoluteNoExtension_Unchanged()
    {
        var src = """
            VirtualNode Card() {
                return (
                    <label text={Asset<Texture2D>("Assets/UI/avatar").name} />
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/UI/avatar\""),
            $"Expected path unchanged. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void Asset_InCodeBlock_PathResolved()
    {
        var src = """
            VirtualNode Card() {
                var tex = Asset<Texture2D>("./bg.png");
                return (
                    <label text={tex?.name ?? ""} />
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/Card.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/UI/bg.png\""),
            $"Expected resolved path in code block. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void AssetHelpers_UsingAutoInjected()
    {
        var src = """
            VirtualNode Card() {
                return (
                    <label text="hello" />
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("using static Ruitk.AssetHelpers;"),
            "Expected auto-injected AssetHelpers using"
        );
    }

    [Fact]
    public void CssHelpers_UsingAutoInjected()
    {
        var src = """
            VirtualNode Card() {
                return (
                    <label text="hello" />
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src);

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("using static Ruitk.Props.Typed.CssHelpers;"),
            "Expected auto-injected CssHelpers using"
        );
    }

    // ── Asset path resolution inside `module` blocks ──────────────────────────
    // Regression coverage for the bug where ModuleEmitter emitted module bodies
    // verbatim, causing Asset<T>("./x") / Asset<T>("../x") literals to reach the
    // runtime UitkxAssetRegistry as raw relative strings (which the registry —
    // keyed by resolved Unity asset paths — would never find).

    [Fact]
    public void Module_AssetCall_RelativePath_IsRewritten()
    {
        var src = """
            @namespace Test
            export string Bg = Asset<Texture2D>("./bg.png")?.name;
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/AppRoot.style.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/UI/bg.png\""),
            $"Expected './bg.png' rewritten to 'Assets/UI/bg.png'.\n{result.GeneratedSource}"
        );
        Assert.False(
            result.SourceContains("\"./bg.png\""),
            "Relative module-body asset path leaked through unrewritten."
        );
    }

    [Fact]
    public void Module_AssetCall_DotDotPath_IsRewritten()
    {
        // The original failing case: `../Resources/background-01.png` inside a
        // `module AppRoot { ... }` block in an .style.uitkx file.
        var src = """
            @namespace Test
            export string Bg = Asset<Texture2D>("../Resources/bg.png")?.name;
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/AppRoot.style.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/Resources/bg.png\""),
            $"Expected '../Resources/bg.png' rewritten to 'Assets/Resources/bg.png'.\n{result.GeneratedSource}"
        );
        Assert.False(
            result.SourceContains("\"../Resources/bg.png\""),
            "Relative module-body asset path leaked through unrewritten."
        );
    }

    [Fact]
    public void Module_AssetCall_AbsolutePath_Unchanged()
    {
        // Absolute Assets/... paths must pass through untouched — no double-prefix.
        var src = """
            @namespace Test
            export string Bg = Asset<Texture2D>("Assets/Fonts/custom.ttf")?.name;
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/AppRoot.style.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/Fonts/custom.ttf\""),
            $"Expected absolute path unchanged. Got:\n{result.GeneratedSource}"
        );
        Assert.False(
            result.SourceContains("\"Assets/UI/Assets/Fonts/custom.ttf\""),
            "Absolute path was incorrectly re-prefixed with the .uitkx directory."
        );
    }

    // ── Asset path resolution inside `hook` bodies ────────────────────────────
    // Parallel coverage for HookEmitter, which previously applied ApplyHookAliases
    // but skipped ResolveAssetPaths.

    [Fact]
    public void Hook_AssetCall_RelativePath_IsRewritten()
    {
        var src = """
            @namespace Test
            string useBg() {
              var tex = Asset<Texture2D>("./bg.png");
              return tex?.name ?? "";
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/useBg.hooks.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.True(
            result.SourceContains("\"Assets/UI/bg.png\""),
            $"Expected './bg.png' rewritten to 'Assets/UI/bg.png' inside hook body.\n{result.GeneratedSource}"
        );
        Assert.False(
            result.SourceContains("\"./bg.png\""),
            "Relative hook-body asset path leaked through unrewritten."
        );
    }

    // ── Sibling top-level Props class resolution ─────────────────────────────
    // Regression coverage for the RouterFunc/RouterFuncProps shape: a function
    // component (`RouterFunc`) and its props class (`RouterFuncProps`) declared
    // as siblings at namespace scope (both top-level, neither nested in the
    // other). HMR previously only walked nested types and shipped
    // `RouterFunc.RouterFuncProps` (CS0426). Source-gen `PropsResolver` already
    // handles this case via `TryGetFuncComponentPropsTypeName`'s sibling lookup;
    // this test pins that contract so any drift fails CI.

    [Fact]
    public void FuncComponent_WithSiblingTopLevelPropsClass_EmitsTypedVFunc()
    {
        // Mirrors the actual Ruitk.Router.RouterFunc / RouterFuncProps
        // declaration shape: both classes at namespace scope, neither nested.
        const string extraCSharp = """
            using Ruitk.Core;
            using System.Collections.Generic;

            namespace MyApp.Routing
            {
                public static class RouterFunc
                {
                    public static VirtualNode Render(IProps rawProps, IReadOnlyList<VirtualNode> children)
                        => null;
                }

                public sealed class RouterFuncProps : IProps
                {
                    public string Path { get; set; }
                }
            }
            """;

        const string uitkx = """
            @namespace MyApp.UI
            @using MyApp.Routing

            VirtualNode MyPage() {
                return (<RouterFunc path="/home" />);
            }
            """;

        var result = GeneratorTestHelper.RunWithExtraCSharp(uitkx, extraCSharp, "MyPage.uitkx");

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics)}"
        );

        // Must use the typed V.Func<RouterFuncProps> overload referencing the
        // sibling top-level class — NOT the nested form RouterFunc.RouterFuncProps
        // (which would produce CS0426 because no such nested type exists).
        Assert.True(
            result.SourceContains("V.Func<global::MyApp.Routing.RouterFuncProps>"),
            $"Expected V.Func<global::MyApp.Routing.RouterFuncProps>. Got:\n{result.GeneratedSource}"
        );
        Assert.False(
            result.SourceContains("RouterFunc.RouterFuncProps"),
            $"Sibling Props was incorrectly emitted as a nested type. Got:\n{result.GeneratedSource}"
        );
        Assert.True(
            result.SourceContains("Path = \"/home\""),
            $"Expected 'Path = \"/home\"' prop forwarded. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void BareAssignWithNestedDirective_InsideDirectiveBody_JsxIsEmitted()
    {
        // bare = <Container>...@if...</Container> in @foreach setup code
        // must not leave raw JSX in the generated source (range collision fix).
        var src = """
            VirtualNode Comp() {
              return (
                <Box>
                  @foreach (var item in items) {
                    var badge = <Box>
                      <Label text={item} />
                      @if (showX) {
                        return (<Label text="extra" />);
                      }
                    </Box>;
                    return (<Label text={item} />);
                  }
                </Box>
              );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Test.uitkx");
        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.False(result.SourceContains("= <Box>"), "Raw bare JSX leaked into generated source");
        Assert.True(
            result.SourceContains("V.Box("),
            "Expected V.Box() call from the bare assignment"
        );
        Assert.True(
            result.SourceContains("V.Label("),
            "Expected V.Label() inside the bare-assigned Box"
        );
    }

    // ── RewriteReturnsForInline unit tests ──────────────────────────────

    [Theory]
    [InlineData("return null;", "continue;")]
    [InlineData("  return null;", "  continue;")]
    [InlineData("return null ;", "continue;")]
    public void RewriteReturns_ReturnNull_BecomesContinue(string input, string expected)
    {
        var result = Ruitk.SourceGenerator.Emitter.EmitContext.RewriteReturnsForInline(
            input,
            "__r"
        );
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RewriteReturns_ReturnExpr_BecomesAddContinue()
    {
        var result = Ruitk.SourceGenerator.Emitter.EmitContext.RewriteReturnsForInline(
            "return V.Label();",
            "__r"
        );
        Assert.Equal("__r.Add(V.Label()); continue;", result);
    }

    [Fact]
    public void RewriteReturns_ReturnParenExpr_UnwrapsParens()
    {
        var result = Ruitk.SourceGenerator.Emitter.EmitContext.RewriteReturnsForInline(
            "return (V.Label());",
            "__r"
        );
        Assert.Equal("__r.Add(V.Label()); continue;", result);
    }

    [Fact]
    public void RewriteReturns_SetupCodeThenReturn_PreservesSetup()
    {
        var input = "var x = 1; return (V.Label());";
        var result = Ruitk.SourceGenerator.Emitter.EmitContext.RewriteReturnsForInline(
            input,
            "__r"
        );
        Assert.Equal("var x = 1; __r.Add(V.Label()); continue;", result);
    }

    [Fact]
    public void RewriteReturns_GuardReturnNull_ThenReturnExpr()
    {
        var input = "if (x) { return null; } return (V.Label());";
        var result = Ruitk.SourceGenerator.Emitter.EmitContext.RewriteReturnsForInline(
            input,
            "__r"
        );
        Assert.Equal("if (x) { continue; } __r.Add(V.Label()); continue;", result);
    }

    [Fact]
    public void RewriteReturns_LambdaReturnNotRewritten()
    {
        var input = "Func<int> f = () => { return 42; }; return (V.Label());";
        var result = Ruitk.SourceGenerator.Emitter.EmitContext.RewriteReturnsForInline(
            input,
            "__r"
        );
        Assert.Contains("return 42;", result);
        Assert.Contains("__r.Add(V.Label()); continue;", result);
    }

    [Fact]
    public void RewriteReturns_StringWithReturnNotRewritten()
    {
        var input = "var s = \"return null;\"; return (V.Label());";
        var result = Ruitk.SourceGenerator.Emitter.EmitContext.RewriteReturnsForInline(
            input,
            "__r"
        );
        Assert.Contains("\"return null;\"", result);
        Assert.Contains("__r.Add(V.Label()); continue;", result);
    }

    [Fact]
    public void RewriteReturns_BreakAndContinuePassThrough()
    {
        var input = "if (i < 3) { continue; } if (i > 6) { break; } return (V.Label());";
        var result = Ruitk.SourceGenerator.Emitter.EmitContext.RewriteReturnsForInline(
            input,
            "__r"
        );
        Assert.Contains("continue;", result);
        Assert.Contains("break;", result);
        Assert.Contains("__r.Add(V.Label()); continue;", result);
    }

    [Fact]
    public void RewriteReturns_CustomListVar()
    {
        var result = Ruitk.SourceGenerator.Emitter.EmitContext.RewriteReturnsForInline(
            "return V.Box();",
            "__items"
        );
        Assert.Equal("__items.Add(V.Box()); continue;", result);
    }

    // ── OPT-V2-2 Phase A: static-style hoisting ────────────────────────────
    //
    // These tests verify the SG hoists `style={new Style { ... }}` initializers
    // to class-level `static readonly Style __sty_N = ...;` fields when every
    // initializer value is a compile-time constant, and falls back to per-render
    // pool rent (or raw allocation for tuple form) otherwise.

    [Fact]
    public void StyleHoist_TupleSyntaxAllLiterals_HoistedToStaticField()
    {
        const string src = """
            VirtualNode HoistTuple() {
                return (
                    <Box style={new Style { (StyleKeys.Width, 100f), (StyleKeys.Height, 50f) }} />
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "HoistTuple.uitkx");

        Assert.True(
            result.SourceWasProduced,
            $"No source produced. Diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.GetMessage()))}"
        );
        Assert.True(
            result.SourceContains(
                "[global::Ruitk.UitkxHmrSwap] private static global::Ruitk.Props.Typed.Style __sty_0"
            ),
            $"Expected hoisted static field __sty_0 with [UitkxHmrSwap]. Got:\n{result.GeneratedSource}"
        );
        Assert.True(
            result.SourceContains("__sty_0"),
            "Expected the hoisted style to be referenced at the call site"
        );
        Assert.False(
            result.SourceContains("Style.__Rent()"),
            "Hoisted styles must not call Style.__Rent()"
        );
    }

    [Fact]
    public void StyleHoist_SetterSyntaxAllLiterals_HoistedToStaticField()
    {
        const string src = """
            VirtualNode HoistSetter() {
                return (
                    <Box style={new Style { Width = 100f, Height = 50f }} />
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "HoistSetter.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains(
                "[global::Ruitk.UitkxHmrSwap] private static global::Ruitk.Props.Typed.Style __sty_0"
            ),
            $"Expected hoisted static field with [UitkxHmrSwap]. Got:\n{result.GeneratedSource}"
        );
        Assert.False(
            result.SourceContains("Style.__Rent()"),
            "Setter-form all-literal style must not call __Rent()"
        );
    }

    [Fact]
    public void StyleHoist_DynamicValue_StaysOnRentPath()
    {
        const string src = """
            VirtualNode DynamicStyle() {
                var w = 100f;
                return (
                    <Box style={new Style { Width = w }} />
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "DynamicStyle.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.False(
            result.SourceContains("__sty_"),
            $"Style with non-literal value must NOT be hoisted. Got:\n{result.GeneratedSource}"
        );
        Assert.True(
            result.SourceContains("Style.__Rent()"),
            "Dynamic style must still rent from the pool"
        );
    }

    [Fact]
    public void StyleHoist_NewColorWithLiteralArgs_Hoists()
    {
        const string src = """
            VirtualNode ColorLiteral() {
                return (
                    <Box style={new Style { (StyleKeys.BackgroundColor, new Color(0.15f, 0.15f, 0.15f, 0.9f)) }} />
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "ColorLiteral.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains(
                "[global::Ruitk.UitkxHmrSwap] private static global::Ruitk.Props.Typed.Style __sty_0"
            ),
            $"new Color(literal-args) must be hoist-safe with [UitkxHmrSwap]. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void StyleHoist_DottedEnumRef_Hoists()
    {
        const string src = """
            VirtualNode EnumRef() {
                return (
                    <Box style={new Style { (StyleKeys.FlexDirection, "column") }} />
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "EnumRef.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("__sty_0"),
            $"Tuple with string-literal value must hoist. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void StyleHoist_MultipleSiblings_GetUniqueIds()
    {
        const string src = """
            VirtualNode TwoStatics() {
                return (
                    <Box>
                        <Label text="a" style={new Style { (StyleKeys.Width, 10f) }} />
                        <Label text="b" style={new Style { (StyleKeys.Width, 20f) }} />
                    </Box>
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "TwoStatics.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("__sty_0"), "Expected __sty_0");
        Assert.True(result.SourceContains("__sty_1"), "Expected __sty_1 for second hoist");
    }

    [Fact]
    public void StyleHoist_EmptyStyleBody_NotHoisted()
    {
        const string src = """
            VirtualNode EmptyStyle() {
                return (
                    <Box style={new Style { }} />
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "EmptyStyle.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.False(
            result.SourceContains("__sty_"),
            "Empty-body Style is deferred to a future EmptyStyle singleton; must not hoist"
        );
    }

    [Fact]
    public void StyleHoist_MixedStaticAndDynamic_HoistsOnlyStatic()
    {
        const string src = """
            VirtualNode Mixed() {
                var sz = 50f;
                return (
                    <Box>
                        <Label text="a" style={new Style { (StyleKeys.Width, 10f) }} />
                        <Label text="b" style={new Style { Width = sz }} />
                    </Box>
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "Mixed.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("__sty_0"), "Static style must hoist");
        Assert.True(result.SourceContains("Style.__Rent()"), "Dynamic style must still rent");
        Assert.False(result.SourceContains("__sty_1"), "Only one hoist expected");
    }

    [Fact]
    public void StyleHoist_TupleWithVariableValue_NotHoisted()
    {
        const string src = """
            VirtualNode TupleDynamic() {
                var w = 100f;
                return (
                    <Box style={new Style { (StyleKeys.Width, w) }} />
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "TupleDynamic.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.False(
            result.SourceContains("__sty_"),
            $"Tuple with bare-identifier value must NOT hoist. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void StyleHoist_NonStyleAttributeUnaffected()
    {
        const string src = """
            VirtualNode PlainBox() {
                return (
                    <Box>
                        <Label text="hi" />
                    </Box>
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "PlainBox.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.False(result.SourceContains("__sty_"), "No style attribute → no hoist");
    }

    [Fact]
    public void StyleHoist_InstanceMemberOnLocal_NotHoisted()
    {
        // Regression: `areaSize.x` is a local-variable member access, NOT a static
        // reference. Must not be hoisted to a class-level field (where the local
        // would be out of scope, causing CS0103). The classifier requires the
        // first dotted segment to start uppercase (PascalCase = type/enum/static
        // class convention) — locals are camelCase and are correctly rejected.
        const string src = """
            VirtualNode AreaBox() {
                var areaSize = new UnityEngine.Vector2(100f, 50f);
                return (
                    <Box style={new Style { Width = areaSize.x, Height = areaSize.y }} />
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "AreaBox.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.False(
            result.SourceContains("__sty_"),
            $"Instance-member access on local must NOT hoist (would cause CS0103). Got:\n{result.GeneratedSource}"
        );
        Assert.True(
            result.SourceContains("Style.__Rent()"),
            "Local-derived style must still rent from the pool"
        );
    }

    [Fact]
    public void StyleHoist_InstanceMemberInTuple_NotHoisted()
    {
        // Same regression in tuple form: (StyleKeys.Width, box.size) — `box.size`
        // is a local foreach-variable member access; must not hoist.
        const string src = """
            VirtualNode Boxes() {
                var boxes = new[] { new { size = 5f } };
                return (
                    <Box>
                        @foreach (var box in boxes) {
                            return (
                                <Label text="x" style={new Style { (StyleKeys.Width, box.size) }} />
                            );
                        }
                    </Box>
                );
            }
            """;
        var result = GeneratorTestHelper.Run(src, "Boxes.uitkx");

        Assert.True(result.SourceWasProduced);
        Assert.False(
            result.SourceContains("__sty_"),
            $"Instance-member on loop local must NOT hoist. Got:\n{result.GeneratedSource}"
        );
    }

    // ── Router primitives — alias-map resolution ────────────────────────────
    // Verifies the source generator wires every <RouterTagAliases.Map> entry
    // through to a V.Func(__fam_<TypeName>, ...) emission backed by a
    // per-type Family handle, exercising the shared dictionary at
    // Shared/Core/Router/RouterTagAliases.cs.

    [Fact]
    public void RouterTag_EmitsRouterFunc()
    {
        var result = GeneratorTestHelper.Run(Wrap("<Router/>"));
        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("__fam_RouterFunc"),
            $"Expected V.Func(__fam_RouterFunc, ...) backed by per-type Family handle. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void RouteTag_EmitsRouteFunc()
    {
        var result = GeneratorTestHelper.Run(Wrap("<Route path=\"/\"/>"));
        Assert.True(result.SourceContains("__fam_RouteFunc"));
    }

    [Fact]
    public void OutletTag_EmitsOutletFunc()
    {
        var result = GeneratorTestHelper.Run(Wrap("<Outlet/>"));
        Assert.True(
            result.SourceContains("__fam_OutletFunc"),
            $"Expected __fam_OutletFunc Family field. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void RoutesTag_EmitsRoutesFunc()
    {
        var result = GeneratorTestHelper.Run(Wrap("<Routes><Route path=\"/\"/></Routes>"));
        Assert.True(
            result.SourceContains("__fam_RoutesFunc"),
            $"Expected __fam_RoutesFunc Family field. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void NavLinkTag_EmitsNavLinkFunc()
    {
        var result = GeneratorTestHelper.Run(Wrap("<NavLink to=\"/\" label=\"Home\"/>"));
        Assert.True(
            result.SourceContains("__fam_NavLinkFunc"),
            $"Expected __fam_NavLinkFunc Family field. Got:\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void NavigateTag_EmitsNavigateFunc()
    {
        var result = GeneratorTestHelper.Run(Wrap("<Navigate to=\"/home\"/>"));
        Assert.True(
            result.SourceContains("__fam_NavigateFunc"),
            $"Expected __fam_NavigateFunc Family field. Got:\n{result.GeneratedSource}"
        );
    }

    // NOTE: Per-attribute pass-through coverage for the new Index/End/CaseSensitive
    // properties cannot be validated in this isolated test compilation because the
    // generator skips its strongly-typed props block when the *FuncProps type is
    // not resolvable (no Unity reference to Ruitk.Shared.dll here).  The
    // Index/End/CaseSensitive attributes follow the exact same emission path as
    // the existing Router/Route/Link properties, which are exercised end-to-end
    // by the FormatterSnapshotTests against real .uitkx samples
    // (Samples/Components/RouterDemoFunc/RouterOutletDemo.uitkx now uses these
    // attributes in production-shaped markup).

    // ── UITKX0109 — User-component strict attribute validation ──────────────
    //
    // Pretty-UI bug regression: <UserComp style={x}/> when the user component
    // didn't declare a `style` parameter used to silently allow it (since
    // `style` was lumped under `universalAttributes`), then explode at C#
    // compile time as CS0117 against the generated *Props class.
    //
    // After the SG-side fix in EmitFuncComponent:
    //   1. Unknown attributes on user components emit UITKX0109 with an
    //      actionable hint (did-you-mean or "Available: …").
    //   2. The unknown attribute assignment is SKIPPED in the generated C#
    //      so we don't pile a follow-on CS0117/CS0246 on top of the warning.
    //   3. `key` and `ref` are exempt — structural-universal at the JSX
    //      surface (key lives on VirtualNode; ref is routed to MutableRef<T>).

    [Fact]
    public void UITKX0109_UserComponent_StyleNotForwarded_EmitsDiagnostic_AndSkipsAssignment()
    {
        // ChildComp declares `text` only — `style` is not a parameter.
        const string childSrc = """
            VirtualNode ChildComp(string? text = null) {
                return (<Label text="child" />);
            }
            """;

        // Parent passes `style={...}`, which is intrinsic-only and must NOT
        // be silently allowed on a user component.
        const string parentSrc = """
            VirtualNode ParentComp() {
                return (
                    <ChildComp text="hi" style={SomeStyle} />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("ChildComp.uitkx", childSrc), ("ParentComp.uitkx", parentSrc) },
            primaryFileName: "ParentComp.uitkx"
        );

        // (1) UITKX0109 must fire.
        Assert.True(
            result.HasDiagnostic("UITKX0109"),
            $"Expected UITKX0109 for unknown 'style' attr on <ChildComp>. "
                + $"Diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id + ':' + d.GetMessage()))}"
        );

        // (2) The bad `Style = SomeStyle` assignment must NOT be in the emitted source —
        // otherwise the C# compiler produces CS0117 against ChildCompProps.
        Assert.False(
            result.SourceContains("Style = SomeStyle"),
            "Generated source must NOT contain the bad 'Style = SomeStyle' assignment "
                + "for an unknown attribute (would cascade to CS0117).\n"
                + result.GeneratedSource
        );
    }

    [Fact]
    public void UITKX0109_UserComponent_KeyAndRef_AlwaysExempt()
    {
        // ChildComp has a MutableRef param so `ref` routes cleanly.
        const string childSrc = """
            VirtualNode ChildComp(
                string? text = null,
                Hooks.MutableRef<object>? inputRef = null
            ) {
                return (<Label text="child" />);
            }
            """;

        const string parentSrc = """
            VirtualNode ParentComp() {
                return (
                    <ChildComp text="hi" key="k1" ref={myRef} />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("ChildComp.uitkx", childSrc), ("ParentComp.uitkx", parentSrc) },
            primaryFileName: "ParentComp.uitkx"
        );

        // key/ref must NEVER produce UITKX0109.
        Assert.False(
            result.HasDiagnostic("UITKX0109"),
            "key= and ref= must be structural-universal — never UITKX0109.\n"
                + string.Join("\n", result.Diagnostics.Select(d => d.Id + ':' + d.GetMessage()))
        );
    }

    [Fact]
    public void UITKX0109_UserComponent_DeclaredAttribute_NoDiagnostic()
    {
        const string childSrc = """
            VirtualNode ChildComp(string? text = null) {
                return (<Label text="child" />);
            }
            """;

        const string parentSrc = """
            VirtualNode ParentComp() {
                return (
                    <ChildComp text="hi" />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("ChildComp.uitkx", childSrc), ("ParentComp.uitkx", parentSrc) },
            primaryFileName: "ParentComp.uitkx"
        );

        Assert.False(
            result.HasDiagnostic("UITKX0109"),
            "Declared attribute 'text' must not raise UITKX0109."
        );

        // Sanity: the declared attribute IS emitted.
        Assert.True(
            result.SourceContains("Text = "),
            $"Expected 'Text = ' assignment in generated source.\n{result.GeneratedSource}"
        );
    }

    [Fact]
    public void UITKX0109_UserComponent_NoParams_AnyAttrIsUnknown()
    {
        // Edge case: component declares zero parameters at all.
        const string childSrc = """
            VirtualNode ChildComp() {
                return (<Label text="child" />);
            }
            """;

        const string parentSrc = """
            VirtualNode ParentComp() {
                return (
                    <ChildComp text="hi" />
                );
            }
            """;

        var result = GeneratorTestHelper.RunMultiple(
            new[] { ("ChildComp.uitkx", childSrc), ("ParentComp.uitkx", parentSrc) },
            primaryFileName: "ParentComp.uitkx"
        );

        Assert.True(
            result.HasDiagnostic("UITKX0109"),
            "A no-params user component must reject any non-structural attribute."
        );
    }

    // ── HMR architectural invariants ──────────────────────────────────────────
    //
    // These tests lock in load-time emission shapes that solved real
    // production-blocking 0.6.0 bugs. Each test names the bug it guards
    // against; do not relax the assertion without re-validating against the
    // original Unity scenario.

    /// <summary>
    /// Regression test for the PrettyUi 0.6.0 Router playmode crash. The
    /// SG must emit a fallback factory `() =&gt; {ChildFqn}.Render` as the
    /// second argument to `GetFamily(...)` for every child reference,
    /// unconditionally. Hand-written children that ship without an
    /// `[ModuleInitializer]` Register call (e.g. router types in the
    /// Ruitk package) rely on this fallback so that
    /// `Family.Current` resolves to a real delegate at first render
    /// instead of the throwing placeholder. Optimising the SG to skip
    /// the fallback for any reason would silently re-introduce the
    /// playmode crash, so this test enforces the contract.
    /// </summary>
    [Fact]
    public void ChildFamily_GetFamilyCall_AlwaysIncludesFallbackFactory()
    {
        // <Router/> resolves via router-tag aliases to RouterFunc, which
        // ships hand-written in Ruitk.Router and has no companion
        // class -- the exact production scenario.
        var result = GeneratorTestHelper.Run(Wrap("<Router/>"));
        Assert.True(result.SourceWasProduced);

        Assert.True(
            result.SourceContains("__fam_RouterFunc"),
            $"Expected __fam_RouterFunc Family field. Got:\n{result.GeneratedSource}"
        );

        // The whole GetFamily call must be on a single line per the
        // current emitter. Asserting the full shape (id + factory) means
        // a future change that splits the args, drops the factory, or
        // emits null for the factory will fail loudly here.
        Assert.True(
            result.SourceContains("RefreshRuntime.GetFamily(\"RouterFunc\", () =>")
            && result.SourceContains(".Render);"),
            "Expected GetFamily(\"RouterFunc\", () => global::...RouterFunc.Render) " +
            "fallback factory. The fallback factory is what lets non-SG children " +
            "(e.g. Ruitk.Router types) resolve at first render. " +
            $"Got:\n{result.GeneratedSource}"
        );
    }

    /// <summary>
    /// Regression test for the PrettyUi 0.6.0 cold-open crash. Mono's
    /// runtime diverges from ECMA-335 §I.8.9.5: it triggers a
    /// `beforefieldinit` type's `.cctor` on ANY static method call
    /// against the type, including `call MyComp::__Register()` from
    /// `&lt;Module&gt;::.cctor`. If `[ModuleInitializer]` lives on the
    /// component class itself, the component `.cctor` runs at module
    /// load -- before Unity's `[InitializeOnLoadMethod]` hooks have
    /// populated registries -- and any user `static readonly Texture2D
    /// bg = Asset&lt;T&gt;(...)` initializer crashes.
    ///
    /// The structural fix: `[ModuleInitializer]` methods live on a
    /// SEPARATE companion class named `{ComponentName}__UitkxRefresh`,
    /// emitted as a sibling type in the same namespace. The companion
    /// class has no user state, so triggering its `.cctor` is harmless.
    ///
    /// This test parses the generated source as a Roslyn syntax tree
    /// and asserts that every `[ModuleInitializer]` method lives on a
    /// type whose name ends with `__UitkxRefresh`. A future refactor
    /// that moves Register back inline would silently re-introduce the
    /// cold-open crash; this test makes that impossible.
    /// </summary>
    [Fact]
    public void ModuleInitializer_OnlyEmittedOnCompanionClass_NeverOnComponentItself()
    {
        var result = GeneratorTestHelper.Run(Wrap("<label/>"));
        Assert.True(result.SourceWasProduced);

        // The companion class lives inside an #if UNITY_EDITOR region, so
        // parse with that symbol defined — otherwise the Roslyn parser
        // strips the conditional block and the test sees zero
        // [ModuleInitializer] methods (false negative).
        var parseOpts = Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default
            .WithPreprocessorSymbols("UNITY_EDITOR");
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            result.GeneratedSource!, parseOpts);
        var moduleInitMethods = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .Where(m => m.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(a => a.Name.ToString().EndsWith("ModuleInitializer", System.StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(moduleInitMethods);

        foreach (var m in moduleInitMethods)
        {
            var owner = m.Ancestors()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .First();

            Assert.True(
                owner.Identifier.Text.EndsWith("__UitkxRefresh", System.StringComparison.Ordinal),
                $"[ModuleInitializer] method '{m.Identifier.Text}' lives on class " +
                $"'{owner.Identifier.Text}', but must live on a '*__UitkxRefresh' " +
                "companion class. Background: Mono's BeforeFieldInit divergence " +
                "fires the containing type's .cctor when <Module>::.cctor invokes " +
                "the initializer -- triggering user static readonly Asset<T>(...) " +
                "initializers BEFORE Unity populates UitkxAssetRegistry, which " +
                "crashes cold-open. See /memories/repo/mono-beforefieldinit-cctor.md " +
                "and CHANGELOG entry for 0.6.0.\n" +
                $"Generated source:\n{result.GeneratedSource}"
            );
        }
    }

    // ── U-06: `cond && <Tag/>` desugar must apply in setup/body splice sites too ──
    // Previously only SpliceExpressionMarkup (the `{expr}` child/attribute path)
    // desugared this; setup-code and directive-body splice sites spliced raw JSX
    // into `isOn && V.Badge(...)`, a hard CS0019 (bool && VirtualNode has no
    // operator overload).

    [Fact]
    public void LogicalAnd_Jsx_InSetupCode_Desugars()
    {
        var src = """
            VirtualNode Foo() {
                var isOn = true;
                var x = (isOn && <Label text="on" />);
                return (
                    <Box>{x}</Box>
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/Foo.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.Contains("isOn) ?", result.GeneratedSource);
        Assert.Contains(": (global::Ruitk.Core.VirtualNode?)null)", result.GeneratedSource);
        Assert.DoesNotContain("isOn && global::Ruitk.Core.V.", result.GeneratedSource);
    }

    [Fact]
    public void LogicalAnd_Jsx_InExpressionChild_StillDesugars()
    {
        // Regression guard: the pre-existing `{expr}`-position desugar (the one
        // working case before U-06) must still work after the shared-helper refactor.
        var src = """
            VirtualNode Foo() {
                return (
                    <Box>{true && <Label text="on" />}</Box>
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/Foo.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.Contains(": (global::Ruitk.Core.VirtualNode?)null)", result.GeneratedSource);
    }

    // ── U-06/U-23: `=` must be an LHS boundary for the `&&` desugar walker ──────

    [Fact]
    public void LogicalAnd_Jsx_AfterAssignment_LhsStopsAtEquals()
    {
        var src = """
            VirtualNode Foo() {
                bool isOn;
                var x = (isOn = true && <Label text="on" />);
                return (
                    <Box>{x}</Box>
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/Foo.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        // LHS of `&&` should be `true`, not `isOn = true` — i.e. the assignment
        // `isOn =` must survive verbatim before the desugared ternary opens.
        Assert.Contains("isOn = ((true) ?", result.GeneratedSource);
    }

    [Fact]
    public void EqualityComparison_BeforeLogicalAnd_IsNotTreatedAsAssignmentBoundary()
    {
        // `==` must NOT be mistaken for the `=` assignment boundary.
        var src = """
            VirtualNode Foo() {
                var mode = 1;
                var x = (mode == 1 && <Label text="on" />);
                return (
                    <Box>{x}</Box>
                );
            }
            """;

        var result = GeneratorTestHelper.Run(src, "Assets/UI/Foo.uitkx");

        Assert.True(result.SourceWasProduced, "No source produced");
        Assert.Contains("((mode == 1) ?", result.GeneratedSource);
    }
}
