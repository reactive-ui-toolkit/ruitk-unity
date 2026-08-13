using System.Linq;
using Ruitk.SourceGenerator.Tests.Helpers;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// HMR ↔ Source-Generator emitter parity contract tests.
///
/// <para>
/// HMR's <c>HmrCSharpEmitter</c> (under <c>Editor/HMR/</c>, in the Unity
/// <c>Ruitk.Editor</c> assembly) is a hand-written transpiler that
/// must emit the same C# shape as the Roslyn-based source generator
/// (<c>SourceGenerator~/Emitter/CSharpEmitter.cs</c>) for any given
/// <c>.uitkx</c> input. When the SG learns a new emit shape, HMR must be
/// updated in lockstep — there is no shared emitter assembly.
/// </para>
///
/// <para>
/// These tests pin the SG-side emit invariants HMR currently mirrors. Each
/// test runs the SG against a tiny <c>.uitkx</c> source and asserts that the
/// generated C# contains the marker substring(s) HMR's emitter is expected
/// to produce too. If SG ever stops emitting one of these markers (because
/// the emit shape changed), the test fails and the corresponding HMR site
/// must be reviewed for parity.
/// </para>
///
/// <para>
/// This file does NOT (and cannot) load the HMR emitter directly — the
/// Editor asmdef pulls in <c>UnityEditor</c> which is not loadable from a
/// standalone .NET test runner. Instead, this is a one-way drift tripwire:
/// SG is the ground truth; HMR's parity is reviewed manually (and, where
/// the algorithm is small enough, mirrored verbatim — see
/// <see cref="HmrFindPropsTypeContractTests"/> for the existing example).
/// </para>
/// </summary>
public class HmrEmitterParityContractTests
{
    // ── Underscore-marked params keep their public prop name ─────────────────

    /// <summary>
    /// A leading underscore marks a component parameter deliberately unused
    /// WITHOUT renaming its public prop: the props-class property and the
    /// props binding derive from <c>FunctionParam.PropSourceName</c>, while the
    /// LOCAL keeps the literal source name. HMR mirrors this in
    /// <c>HmrCSharpEmitter</c>'s props-binding loop — its reflective
    /// <c>__HmrProp&lt;T&gt;(__rawProps, "Count", …)</c> lookup reads the
    /// SG-generated property BY NAME, so both derivations must stay identical.
    /// </summary>
    [Fact]
    public void Sg_UnderscoreParam_PropKeepsUnprefixedName()
    {
        var result = GeneratorTestHelper.Run(
            """
            export VirtualNode Panel(int _count = 0, string title = "t") {
              return (
                <Label text={title}/>
              );
            }
            """,
            "Panel.uitkx"
        );

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("public int Count { get; set; }"),
            "The props-class property must strip the deliberately-unused marker"
        );
        Assert.True(
            result.SourceContains("var _count = props.Count;"),
            "The local binding keeps the source name; the property does not"
        );
        Assert.False(
            result.SourceContains("props._count"),
            "No surface may expose the underscored name as a prop"
        );
    }

    // ── Null-only components (React case 2) ─────────────────────────────────

    /// <summary>
    /// A component whose only top-level return is <c>return null;</c> is valid
    /// (always renders nothing). SG emits the whole body as setup code —
    /// including the author's <c>return null;</c> — followed by the
    /// empty-markup fallback <c>return (VirtualNode)null;</c>. HMR mirrors
    /// this via the same <c>markupNodes.Count == 0</c> branch in
    /// <c>HmrCSharpEmitter</c>; its header pragma already suppresses the
    /// CS0162 the unreachable fallback produces.
    /// </summary>
    [Fact]
    public void Sg_NullOnlyComponent_EmitsSetupThenFallbackReturn()
    {
        var result = GeneratorTestHelper.Run(
            """
            export VirtualNode Gone() {
              useEffect(() => {
                return null;
              }, new object[] { });
              return null;
            }
            """
        );

        Assert.True(result.SourceWasProduced);
        Assert.True(result.SourceContains("__Render_body"));
        Assert.True(
            result.SourceContains("return null;"),
            "The author's explicit return null must be spliced verbatim"
        );
        Assert.True(
            result.SourceContains("return (global::Ruitk.Core.VirtualNode)null;"),
            "The empty-markup fallback return must close the render body"
        );
    }

    /// <summary>
    /// The full React shape: a braced markup guard plus a final top-level
    /// <c>return null;</c>. The guard's JSX is converted by the setup-code
    /// splicer (paren-block ranges), so V.* factories must appear even though
    /// the component has no top-level markup return. HMR shares
    /// <c>SpliceSetupCodeMarkup</c> semantics.
    /// </summary>
    [Fact]
    public void Sg_NullOnlyComponent_WithBracedMarkupGuard_SplicesGuardJsx()
    {
        var result = GeneratorTestHelper.Run(
            """
            export VirtualNode MaybeBadge(bool show = false) {
              if (show) {
                return (<Label text="badge" />);
              }
              return null;
            }
            """
        );

        Assert.True(result.SourceWasProduced);
        Assert.True(
            result.SourceContains("V.Label("),
            "Braced guard JSX must be spliced into V.* calls"
        );
        Assert.True(result.SourceContains("return (global::Ruitk.Core.VirtualNode)null;"));
    }

    // ── Issue 5 — ref={x} routing on function components ────────────────────────

    /// <summary>
    /// SG resolves <c>ref={x}</c> on a function component to the matching
    /// <c>Ref&lt;T&gt;</c> property on the synthesized props object. HMR
    /// mirrors this at <c>HmrCSharpEmitter.EmitFuncComponent</c> by extracting
    /// <c>ref={x}</c> before the attribute filter loop and routing it through
    /// <c>FindPropsTypeAndRefSlot</c>. If SG ever stops emitting the routed
    /// assignment, the post-fix HMR code is wrong too.
    /// </summary>
    [Fact]
    public void Sg_RefOnFuncComponent_RoutesIntoPropsSlot()
    {
        // Two files: a peer component declaring a Ref<T> param + the
        // consumer that passes ref={x} into it. SG resolves the ref slot
        // via PropsResolver.TryGetRefParamPropName.
        var output = GeneratorTestHelper.RunMultiple(
            new[]
            {
                (
                    "MyTextField.uitkx",
                    """
                    @namespace Ruitk.HmrParity
                    @using Ruitk.Core

                    VirtualNode MyTextField(Ref<object> inputRef = null) {
                        return (<TextField />);
                    }
                    """
                ),
                (
                    "App.uitkx",
                    """
                    @namespace Ruitk.HmrParity
                    @using Ruitk.Core
                    @using Ruitk.HmrParity

                    VirtualNode App() {
                        var myRef = Hooks.UseRef<object>();
                        return (
                            <VisualElement>
                                <MyTextField ref={myRef} />
                            </VisualElement>
                        );
                    }
                    """
                ),
            },
            primaryFileName: "App.uitkx"
        );

        Assert.NotNull(output.GeneratedSource);
        // SG must emit the ref-routing assignment with the resolved prop name
        // (here: InputRef from `Ref<object> inputRef`). HMR mirrors this with
        // FindRefSlotName scanning Props for Ref<>/MutableRef<> properties.
        Assert.Contains("InputRef = myRef", output.GeneratedSource);
        // And no literal `Ref = ` should leak (that would indicate the resolver
        // fell through to "treat ref as a literal prop" — the pre-fix shape).
        Assert.DoesNotContain(" Ref = myRef", output.GeneratedSource);
    }

    // ── Issue 6 — JSX-valued attributes (the React-Router element={<X/>} case) ──

    /// <summary>
    /// SG recursively emits a nested <c>ElementNode</c> when it appears as the
    /// value of a JSX attribute, via <c>EmitJsxToString</c>. HMR mirrors this
    /// in <c>HmrCSharpEmitter.AttrToExpr</c>'s <c>JsxExpressionValue</c> case
    /// using the same <c>_sb</c>-capture pattern. If SG ever changes the
    /// nested-element representation (e.g. a different <c>V.*</c> entry-point),
    /// HMR's mirror must change too.
    /// </summary>
    [Fact]
    public void Sg_JsxAsAttributeValue_EmitsNestedElement()
    {
        var output = GeneratorTestHelper.RunMultiple(
            new[]
            {
                (
                    "Wrapper.uitkx",
                    """
                    @namespace Ruitk.HmrParity
                    @using Ruitk.Core

                    VirtualNode Wrapper(VirtualNode header = null) {
                        return (<VisualElement />);
                    }
                    """
                ),
                (
                    "Page.uitkx",
                    """
                    @namespace Ruitk.HmrParity
                    @using Ruitk.HmrParity

                    VirtualNode Page() {
                        return (
                            <Wrapper header={<Label text="Hi" />} />
                        );
                    }
                    """
                ),
            },
            primaryFileName: "Page.uitkx"
        );

        Assert.NotNull(output.GeneratedSource);
        // The inner <Label> must actually be emitted as a V.Label call inline
        // inside the props initializer — not collapsed to null (which is what
        // HMR's pre-fix stub did, and which would make any element-typed prop
        // silently disappear after a hot reload).
        Assert.Contains("V.Label(", output.GeneratedSource);
        Assert.Contains("Header = V.Label(", output.GeneratedSource);
        Assert.DoesNotContain("Header = null", output.GeneratedSource);
    }

    // ── Phase 1 — JSX literals in arbitrary expression positions ────────────────

    /// <summary>
    /// Phase 1: SG splices JSX literals embedded inside C# expressions
    /// (ternaries, lambdas, attribute values, child <c>{...}</c> /
    /// <c>@(...)</c>) at emit time via <c>SpliceExpressionMarkup</c>. HMR
    /// mirrors this in <c>HmrCSharpEmitter.SpliceExpressionMarkup</c>, wired
    /// into <c>EmitExpression</c> and the <c>CSharpExpressionValue</c> case
    /// of <c>AttrToExpr</c>. Both paths share the same scanner output
    /// (<c>DirectiveParser.FindBareJsxRanges</c> and
    /// <c>FindJsxBlockRanges</c>) — HMR reaches them via reflection
    /// delegates piped through <c>UitkxHmrCompiler</c>.
    /// <para>If SG ever stops splicing or changes the splice contract
    /// (e.g. how rent statements flow), HMR must follow.</para>
    /// </summary>
    [Fact]
    public void Sg_JsxInChildTernary_SplicedToVCalls_HmrMustMirror()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode Conditional() {
                bool flag = true;
                return (
                    <box>{flag ? <label text="A" /> : <label text="B" />}</box>
                );
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // Both ternary branches must be spliced to V.Label calls.
        Assert.Contains("V.Box(", output.GeneratedSource);
        Assert.Contains("V.Label(", output.GeneratedSource);
        // Raw JSX must NOT leak through — would cause a Roslyn parse error
        // in the generated file.
        Assert.DoesNotContain("<label text=\"A\"", output.GeneratedSource);
    }

    // ── Issue 10 — UITKX0104 duplicate-key warning is emitted by SG ─────────────

    /// <summary>
    /// SG raises <c>UITKX0104</c> at compile time when sibling elements
    /// share a static-string <c>key={...}</c> value. HMR mirrors this with
    /// a <c>Debug.LogWarning</c> from <c>CheckDuplicateKeys</c> in its
    /// <c>EmitChildArgs</c>. The diagnostic id is the contract: if SG ever
    /// renumbers / suppresses the diagnostic, HMR must follow.
    /// </summary>
    [Fact]
    public void Sg_DuplicateSiblingKey_RaisesUitkx0104()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode Dup() {
                return (
                    <VisualElement>
                        <Label key="a" text="one" />
                        <Label key="a" text="two" />
                    </VisualElement>
                );
            }
            """
        );

        Assert.Contains(output.Diagnostics, d => d.Id == "UITKX0104");
    }

    /// <summary>
    /// Negative for <see cref="Sg_DuplicateSiblingKey_RaisesUitkx0104"/>:
    /// distinct keys must NOT trip the diagnostic. Pins the warning to the
    /// duplicate case alone so HMR's mirror does not over-fire.
    /// </summary>
    [Fact]
    public void Sg_DistinctSiblingKeys_NoUitkx0104()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode Distinct() {
                return (
                    <VisualElement>
                        <Label key="a" text="one" />
                        <Label key="b" text="two" />
                    </VisualElement>
                );
            }
            """
        );

        Assert.DoesNotContain(output.Diagnostics, d => d.Id == "UITKX0104");
    }

    // ── Issue 9 (retracted) — function-component props are NOT pooled ───────────

    /// <summary>
    /// Pinned regression: HMR's <c>EmitFuncComponent</c> uses
    /// <c>new {PropsName} { ... }</c> for function-component invocations,
    /// not <c>BaseProps.__Rent</c>. This test confirms SG does the same —
    /// any future SG change toward pooling here would require HMR to follow
    /// (and would invalidate the original Issue 9 retraction).
    /// </summary>
    [Fact]
    public void Sg_FuncComponentInvocation_UsesNewNotRent()
    {
        var output = GeneratorTestHelper.RunMultiple(
            new[]
            {
                (
                    "Greeter.uitkx",
                    """
                    @namespace Ruitk.HmrParity

                    VirtualNode Greeter(string name = "world") {
                        return (<Label text={"hi " + name} />);
                    }
                    """
                ),
                (
                    "Host.uitkx",
                    """
                    @namespace Ruitk.HmrParity
                    @using Ruitk.HmrParity

                    VirtualNode Host() {
                        return (<Greeter name="copilot" />);
                    }
                    """
                ),
            },
            primaryFileName: "Host.uitkx"
        );

        Assert.NotNull(output.GeneratedSource);
        // Greeter is a function component — its props must be constructed with
        // `new ...GreeterProps { ... }`, NOT `BaseProps.__Rent<GreeterProps>()`.
        // The synthesized {Name}Props class derives from IProps (not BaseProps)
        // and cannot be pooled. SG emits the FQN form.
        Assert.Contains(
            "new global::Ruitk.HmrParity.Greeter.GreeterProps",
            output.GeneratedSource
        );
        Assert.DoesNotContain("__Rent", output.GeneratedSource);
    }

    // ── Component-level Fast Refresh Family emission ─────────────────────────

    /// <summary>
    /// Every function-component must compile into the Fast Refresh shape:
    /// <list type="bullet">
    ///   <item>Public <c>Render</c> that forwards directly to
    ///         <c>__Render_body</c> (no preprocessor branch, no trampoline
    ///         field).</item>
    ///   <item>Private <c>__Render_body</c> holding the user code, marked
    ///         <c>[EditorBrowsable(Never)]</c>.</item>
    ///   <item>A <c>[ModuleInitializer]</c> that publishes the body and the
    ///         hook signature to <c>RefreshRuntime.Register</c> so the
    ///         component's Family handle is canonical from assembly load
    ///         onwards.</item>
    /// </list>
    /// Reconciler identity now comes from the Family handle threaded into
    /// the VNode by parents - not from <c>ReferenceEquals</c> on the public
    /// render delegate - so the public method needs no special shape.
    /// </summary>
    [Fact]
    public void Sg_FunctionComponent_GeneratesFamilyRegistration()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode MyComponent() {
                return (<VisualElement />);
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        var src = output.GeneratedSource;

        // Public Render forwards plainly to the body - no HmrState branch,
        // no __hmr_Render field.
        Assert.Contains("public static global::Ruitk.Core.VirtualNode Render(", src);
        Assert.DoesNotContain("__hmr_Render", src);
        Assert.DoesNotContain("global::Ruitk.Core.HmrState.IsActive", src);

        // Body method exists and is EditorBrowsable-suppressed.
        // Emitted as `internal` so the editor-only companion class
        // `{ComponentName}__UitkxRefresh` (which holds [ModuleInitializer])
        // can reference it via `() => Component.__Render_body` without
        // triggering the component .cctor on Mono.
        Assert.Contains("internal static global::Ruitk.Core.VirtualNode __Render_body(", src);
        Assert.Contains(
            "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]",
            src);

        // ModuleInitializer publishes body + signature to RefreshRuntime so
        // the Family handle's Current slot is populated at assembly load.
        Assert.Contains(
            "[global::System.Runtime.CompilerServices.ModuleInitializer]",
            src);
        Assert.Contains("global::Ruitk.Refresh.RefreshRuntime.Register(", src);
        Assert.Contains("\"MyComponent\"", src);
        Assert.Contains("__Render_body", src);
    }

    /// <summary>
    /// Real-world component shape regression: hooks and setup must live
    /// inside <c>__Render_body</c>, never inside the public <c>Render</c>
    /// forwarder, and the component must still publish a hook signature so
    /// <c>RefreshRuntime.PerformRefresh</c> can decide between compatible
    /// (state-preserving) and incompatible (force-remount) edits.
    /// </summary>
    [Fact]
    public void Sg_FunctionComponent_BodyContainsHooksAndSetup_RenderStaysThin()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode Counter(int initial = 0) {
                var (count, setCount) = useState(initial);
                return (
                    <Button text={count.ToString()} />
                );
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        var src = output.GeneratedSource;

        Assert.Contains("__Render_body(", src);

        // Hooks live in the body method, not the public forwarder. Land
        // past the first reference (forwarder call site) to inspect the
        // method definition itself.
        int firstRef = src.IndexOf("__Render_body(", System.StringComparison.Ordinal);
        Assert.True(firstRef > 0);
        int afterFirst = src.IndexOf("__Render_body(", firstRef + 1, System.StringComparison.Ordinal);
        int searchFrom = afterFirst > 0 ? afterFirst : firstRef;
        int useStateAt = src.IndexOf("UseState", searchFrom, System.StringComparison.Ordinal);
        Assert.True(useStateAt > 0,
            "UseState call must be inside __Render_body, not in the public forwarder.");

        // Hook signature still emitted - PerformRefresh reads it to gate
        // FullResetComponentState on shape-changing edits.
        Assert.Contains("HookSignature", src);

        // Signature is published to the runtime via the registration call.
        Assert.Contains("global::Ruitk.Refresh.RefreshRuntime.Register(", src);
    }

    // ── Module static-method HMR rewrite (v0.4.20, Issue (a)) ───────────────────

    /// <summary>
    /// Every <c>public static</c> method declared inside a <c>module {…}</c>
    /// body must compile into the HMR trampoline triplet:
    /// <c>__hmr_&lt;Name&gt;_h&lt;sig&gt;</c> delegate field,
    /// <c>__&lt;Name&gt;_body_h&lt;sig&gt;</c> private body method,
    /// and a public trampoline that checks <c>HmrState.IsActive</c>.
    /// </summary>
    [Fact]
    public void Sg_ModuleStaticMethod_GeneratesTrampoline()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            export int Add(int a, int b) {
                return a + b;
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // HMR delegate slot — initialised to body method.
        Assert.Contains("__hmr_Add_h", output.GeneratedSource);
        // Body method synthesized with __<Name>_body_h<sig> shape.
        Assert.Contains("__Add_body_h", output.GeneratedSource);
        // Trampoline guards on HmrState.IsActive — exact pattern match.
        Assert.Contains("global::Ruitk.Core.HmrState.IsActive", output.GeneratedSource);
        // Public surface is preserved (caller signature unchanged).
        Assert.Contains("public static int Add(int a, int b)", output.GeneratedSource);
    }

    /// <summary>
    /// <c>ref</c>/<c>out</c>/<c>in</c>/<c>params</c> parameters cannot be
    /// expressed by <c>Func&lt;&gt;</c>/<c>Action&lt;&gt;</c>. The rewriter
    /// MUST emit a custom delegate type so by-ref methods (e.g. DoomGame's
    /// <c>Tick(ref GameState st, …)</c>) hot-reload correctly.
    /// </summary>
    [Fact]
    public void Sg_ModuleStaticMethod_RefParam_UsesCustomDelegate()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            export void Bump(ref int value) {
                value++;
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // Custom delegate declaration carrying the `ref` modifier — the whole
        // point of NOT using Action<int>. Visibility tracks the original method
        // (here `public`) so the synthesized declarations never tighten access.
        Assert.Contains("public delegate void __Bump_h", output.GeneratedSource);
        Assert.Contains("ref int value", output.GeneratedSource);
        // Argument forwarding must keep the `ref` keyword.
        Assert.Contains("(ref value)", output.GeneratedSource);
        // Sanity: framework Action<int> must NOT be used as the field type.
        Assert.DoesNotContain("global::System.Action<int> __hmr_Bump", output.GeneratedSource);
    }

    /// <summary>
    /// Two static methods sharing the same name but different signatures
    /// must produce two distinct <c>__hmr_*</c> field names. The signature
    /// hash is the disambiguator; without it overloads would collide on
    /// the same field slot and only the first emitted would HMR.
    /// </summary>
    [Fact]
    public void Sg_ModuleStaticMethod_Overloads_GetDistinctHashes()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            export int Foo(int x) => x;
            export string Foo(string x) => x;
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // Both overloads must appear with their own __hmr_Foo_h<hash> field.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            output.GeneratedSource,
            @"__hmr_Foo_h[0-9a-f]{8}"
        );
        // Each overload produces exactly one field declaration AND one
        // call inside the trampoline body — so we expect at least 2
        // distinct hashes across all matches.
        var distinctHashes = matches
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Value)
            .Distinct()
            .ToList();
        Assert.True(
            distinctHashes.Count >= 2,
            $"Expected at least 2 distinct overload hashes, got {distinctHashes.Count}: "
                + string.Join(", ", distinctHashes)
        );
    }

    /// <summary>
    /// Generic methods use the <c>MethodInfo</c> + <c>ConcurrentDictionary</c>
    /// cache pattern (mirrors HookEmitter generic case). The cache keys per
    /// closed type, the <c>MethodInfo</c> field is rebound by the swapper,
    /// and the value is a delegate built via <c>MakeGenericMethod</c>.
    /// </summary>
    [Fact]
    public void Sg_ModuleGenericMethod_UsesMethodInfoCache()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            export T Identity<T>(T x) {
                return x;
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // MethodInfo backing field for the generic case. Visibility tracks the
        // original method (here `public`).
        Assert.Contains(
            "public static global::System.Reflection.MethodInfo __hmr_Identity_h",
            output.GeneratedSource
        );
        // Per-closed-type delegate cache.
        Assert.Contains(
            "global::System.Collections.Concurrent.ConcurrentDictionary<global::System.Type, global::System.Delegate>",
            output.GeneratedSource
        );
        // MakeGenericMethod call that closes the open MethodInfo per call site.
        Assert.Contains("MakeGenericMethod", output.GeneratedSource);
        // Trampoline preserves the generic signature.
        Assert.Contains("public static T Identity<T>(T x)", output.GeneratedSource);
    }

    /// <summary>
    /// Regression for v0.5.1: generic module methods generated invalid C#
    /// in v0.4.19 / v0.5.0 due to two emit bugs in <c>ModuleBodyRewriter</c>:
    /// <list type="number">
    ///   <item><description>
    ///     <c>AppendTypeArgs</c> emitted bare type-parameter names into the
    ///     <c>MakeGenericMethod(...)</c> argument list, producing
    ///     <c>MakeGenericMethod(TProps, TResult)</c> — CS0119
    ///     ('TProps' is a type, which is not valid in the given context).
    ///     The fix wraps each name in <c>typeof(...)</c>.
    ///   </description></item>
    ///   <item><description>
    ///     The synthesized <c>MethodInfo</c> HMR field was initialized as
    ///     <c>= null;</c>. The field MUST start null (the trampoline checks
    ///     <c>!= null</c> to fall through to the body method until the HMR
    ///     swapper fills it via reflection), but consumer projects with
    ///     <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c> fired CS8625. The
    ///     fix emits <c>= null!;</c> — same runtime value, no warning.
    ///   </description></item>
    /// </list>
    /// This test compiles the generated C# end-to-end with
    /// <c>UNITY_EDITOR</c> defined and nullable-context enabled. Substring
    /// checks alone (as in <see cref="Sg_ModuleGenericMethod_UsesMethodInfoCache"/>)
    /// missed both bugs because the broken output still contained
    /// <c>MakeGenericMethod</c> and the field declaration.
    /// </summary>
    [Fact]
    public void Sg_ModuleGenericMethod_GeneratedCodeCompiles_NoCS0119_NoCS8625()
    {
        // Multi-type-parameter generics — the exact emit shape that broke in
        // JustStayOn (Dialogs.utils.uitkx Register/Open). Generic CONSTRAINTS have
        // no plain-dialect form (0.16.0, ruling D2 — constrained members live in
        // ambient C#); the CS0119/CS8625 regression pins are constraint-independent.
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            export int Register<TProps, TResult>(TProps props, TResult result) {
                return 1;
            }

            export TResult Open<TResult>(string id) {
                return default(TResult);
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);

        // Stub for Ruitk.Core.HmrState (referenced by the trampoline).
        const string Stubs = """
            namespace Ruitk.Core
            {
                public static class HmrState
                {
                    public static bool IsActive => false;
                }
            }
            """;

        var parseOptions = new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
            languageVersion: Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest,
            preprocessorSymbols: new[] { "UNITY_EDITOR" }
        );

        var trees = new[]
        {
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                output.GeneratedSource!,
                parseOptions
            ),
            Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(Stubs, parseOptions),
        };

        // Reference every assembly the host runtime trusts. Simpler &
        // more reliable than hand-picking System.Reflection / System.Collections.Concurrent
        // / System.ComponentModel under .NET 10 where types are split across
        // many ref assemblies.
        var trustedAssemblies = (string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var refs = trustedAssemblies
            .Split(System.IO.Path.PathSeparator)
            .Where(p => !string.IsNullOrEmpty(p) && System.IO.File.Exists(p))
            .Select(p =>
                (Microsoft.CodeAnalysis.MetadataReference)
                    Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)
            )
            .ToArray();

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "ModuleGenericCompileCheck",
            trees,
            refs,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable
            )
        );

        // Bug-targeted assertion. We don't try to compile the *whole*
        // generated file (it `using`s UnityEngine, Ruitk.Props,
        // AssetHelpers, etc. — providing all those stubs would balloon
        // this test). Instead we filter to the two diagnostic IDs that
        // pinpoint the v0.4.19 / v0.5.0 regression:
        //   • CS0119 — `MakeGenericMethod(TProps, TResult)` (bare type
        //     parameter names instead of typeof(...)).
        //   • CS8625 — `MethodInfo … = null;` under nullable-enabled.
        // Either appearing means the bug is back; neither appearing in a
        // file that exercises the generic-module-method path means the
        // emitter is producing valid C# for that shape.
        var bugDiags = compilation
            .GetDiagnostics()
            .Where(d => d.Id == "CS0119" || d.Id == "CS8625")
            .ToList();

        Assert.True(
            bugDiags.Count == 0,
            "Generic module method emit regressed (CS0119 or CS8625):\n"
                + string.Join("\n", bugDiags.Select(d => d.ToString()))
                + "\n\n--- Generated Source ---\n"
                + output.GeneratedSource
        );
    }

    /// <summary>
    /// <c>const</c> fields, mutable static fields, instance methods,
    /// properties, and nested types must all be emitted verbatim — never
    /// wrapped in trampolines. <c>static readonly</c> fields are an exception:
    /// they are rewritten to <c>[UitkxHmrSwap] static</c> (B28 — see
    /// StaticReadonlyStripper) so the HMR pipeline can re-initialise them
    /// across edit-save cycles. Anything else would be a regression on
    /// existing module behaviour.
    /// </summary>
    // (Sg_ModuleNonMethodMembers_StayVerbatim and Sg_ModuleInstanceMethod_NotRewritten
    // removed with the legacy module grammar — 0.16.0. Value static-readonly rewriting
    // is pinned by ExportsEmitterTests.ValueExport_GetsUitkxHmrSwapAttribute; nested
    // types and instance members have no plain-dialect form (ruling D2 — ambient C#).)

    /// <summary>
    /// Default parameter values must survive on the public trampoline
    /// (callers rely on them) but be elided from the body method (which
    /// receives explicit arguments from the trampoline).
    /// </summary>
    [Fact]
    public void Sg_ModuleStaticMethod_DefaultParams_PreservedOnTrampoline()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            export int Add(int a, int b = 5) {
                return a + b;
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // Trampoline: defaults present.
        Assert.Contains("public static int Add(int a, int b = 5)", output.GeneratedSource);
        // Body method: defaults stripped (explicit values forwarded).
        Assert.Contains("private static int __Add_body_h", output.GeneratedSource);
    }

    // ── Media (v0.4.20) — <Audio>, <Video>, useSfx ─────────────────────────────

    /// <summary>
    /// <c>&lt;Audio&gt;</c> is a Func-Component but, because
    /// <c>V.Audio(AudioProps, key, params children)</c> matches the
    /// auto-discovery shape (first param ends in <c>Props</c>, last param
    /// is <c>params VirtualNode[]</c>), SG and HMR both emit it through the
    /// Typed-with-children path: <c>V.Audio(BaseProps.__Rent&lt;AudioProps&gt;()…)</c>.
    /// HMR auto-discovery (Phase 1) ensures parity.
    /// </summary>
    [Fact]
    public void Sg_AudioElement_EmitsThroughTypedPath()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode MusicPlayer() {
                return (<Audio Autoplay={true} Loop={true} Volume={0.5f} />);
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // Typed-path emission: V.Audio(...) with rented AudioProps.
        Assert.Contains("V.Audio(", output.GeneratedSource);
        Assert.Contains("AudioProps", output.GeneratedSource);
        // Specific prop assignments survive.
        Assert.Contains("Autoplay = true", output.GeneratedSource);
        Assert.Contains("Loop = true", output.GeneratedSource);
        // Pooled (BaseProps.__Rent), NOT new — Audio is a typed built-in
        // whose props extend BaseProps.
        Assert.Contains("__Rent<", output.GeneratedSource);
    }

    /// <summary>
    /// <c>&lt;Video&gt;</c> mirrors <c>&lt;Audio&gt;</c>'s emit shape via
    /// auto-discovery. Verifies the URL streaming case (<c>Url</c> string
    /// attribute) round-trips through the typed-prop pipeline.
    /// </summary>
    [Fact]
    public void Sg_VideoElement_EmitsThroughTypedPath()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode VideoPanel() {
                return (<Video Url="https://example.com/clip.mp4" Autoplay={true} Muted={true} />);
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("V.Video(", output.GeneratedSource);
        Assert.Contains("VideoProps", output.GeneratedSource);
        Assert.Contains("Url = ", output.GeneratedSource);
        Assert.Contains("Autoplay = true", output.GeneratedSource);
        Assert.Contains("Muted = true", output.GeneratedSource);
    }

    /// <summary>
    /// <c>&lt;Video&gt;</c> accepts overlay children (e.g. play/pause buttons
    /// rendered on top of the video). Confirms SG emits the children-array
    /// argument so HMR's TypedC dispatch produces the same shape.
    /// </summary>
    [Fact]
    public void Sg_VideoElement_AcceptsOverlayChildren()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode VideoWithOverlay() {
                return (
                    <Video Loop={true}>
                        <Button text="Play" />
                    </Video>
                );
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // Both V.Video and the inner V.Button must appear in the same
        // generated unit — confirms TypedC dispatch flattened the child.
        Assert.Contains("V.Video(", output.GeneratedSource);
        Assert.Contains("V.Button(", output.GeneratedSource);
    }

    /// <summary>
    /// <c>useSfx()</c> hook reports as <c>UseSfx</c> in the generated
    /// <c>HookSignatureAttribute</c>. The signature is what HMR uses to
    /// detect rude hook-order edits; if the hook is not in the regex
    /// whitelist on either side, it's invisible to that mechanism.
    /// </summary>
    [Fact]
    public void Sg_UseSfxHook_AppearsInHookSignature()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode SfxButton() {
                var playSfx = useSfx();
                return (<Button text="Click" />);
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // Lower-case alias rewritten to canonical Hooks.UseSfx call.
        Assert.Contains("Hooks.UseSfx(", output.GeneratedSource);
        // Hook-signature attribute carries the UseSfx token.
        Assert.Contains("HookSignature", output.GeneratedSource);
        Assert.Contains("UseSfx", output.GeneratedSource);
    }

    /// <summary>
    /// PascalCase form (<c>Hooks.UseSfx</c>) is also matched by the hook
    /// signature regex. Tests parity with the camelCase alias above.
    /// </summary>
    [Fact]
    public void Sg_UseSfxPascalCase_AlsoDetectedBySignatureRegex()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode SfxButtonPascal() {
                var playSfx = Hooks.UseSfx();
                return (<Button text="Click" />);
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("Hooks.UseSfx(", output.GeneratedSource);
        // Even though no alias-rewrite was needed (already PascalCase), the
        // signature regex must still detect it.
        Assert.Contains("UseSfx", output.GeneratedSource);
    }

    // -- Issue 15 coverage: hooks added in earlier releases whose camelCase
    // alias forms were never wired into s_hookAliases / s_genericHookAliasRe.
    // Without these tests, future hook additions that forget to update the
    // alias tables would silently produce CS0103 in consumer .uitkx files.

    [Fact]
    public void Sg_UseSafeAreaHook_LowercaseAliasRewritten()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode SafeAreaUser() {
                var insets = useSafeArea();
                return (<Box />);
            }
            """
        );
        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("Hooks.UseSafeArea(", output.GeneratedSource);
        Assert.Contains("UseSafeArea", output.GeneratedSource);
    }

    [Fact]
    public void Sg_UseStableFuncHook_GenericAliasRewritten()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode StableFuncUser() {
                var f = useStableFunc<int>(() => 42);
                return (<Box />);
            }
            """
        );
        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("Hooks.UseStableFunc<int>(", output.GeneratedSource);
        Assert.Contains("UseStableFunc", output.GeneratedSource);
    }

    [Fact]
    public void Sg_UseStableActionHook_GenericAliasRewritten()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode StableActionUser() {
                var a = useStableAction<int>(v => { });
                return (<Box />);
            }
            """
        );
        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("Hooks.UseStableAction<int>(", output.GeneratedSource);
        Assert.Contains("UseStableAction", output.GeneratedSource);
    }

    [Fact]
    public void Sg_UseStableCallbackHook_LowercaseAliasRewritten()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode StableCallbackUser() {
                var cb = useStableCallback(() => { });
                return (<Box />);
            }
            """
        );
        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("Hooks.UseStableCallback(", output.GeneratedSource);
        Assert.Contains("UseStableCallback", output.GeneratedSource);
    }

    [Fact]
    public void Sg_UseImperativeHandleHook_GenericAliasRewritten()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode ImperativeHandleUser() {
                var h = useImperativeHandle<object>(() => null);
                return (<Box />);
            }
            """
        );
        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("Hooks.UseImperativeHandle<object>(", output.GeneratedSource);
        Assert.Contains("UseImperativeHandle", output.GeneratedSource);
    }

    [Fact]
    public void Sg_UseAnimateHook_LowercaseAliasRewritten()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode AnimateUser() {
                useAnimate(null, false);
                return (<Box />);
            }
            """
        );
        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("Hooks.UseAnimate(", output.GeneratedSource);
        Assert.Contains("UseAnimate", output.GeneratedSource);
    }

    [Fact]
    public void Sg_UseTweenFloatHook_LowercaseAliasRewritten()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode TweenFloatUser() {
                useTweenFloat(0f, 1f, 1f, default, 0f, v => { }, null);
                return (<Box />);
            }
            """
        );
        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("Hooks.UseTweenFloat(", output.GeneratedSource);
        Assert.Contains("UseTweenFloat", output.GeneratedSource);
    }

    [Fact]
    public void Sg_UseTransitionHook_LowercaseAliasRewritten()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode TransitionUser() {
                var (isPending, startTransition) = useTransition();
                return (<Box />);
            }
            """
        );
        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("Hooks.UseTransition(", output.GeneratedSource);
    }

    // ── U-06 — `cond && <Tag/>` desugar must apply in setup code too ───────────

    /// <summary>
    /// SG's <c>SpliceSetupCodeMarkup</c> now shares <c>TryEmitLogicalAndDesugar</c>
    /// with <c>SpliceExpressionMarkup</c> (previously only the latter desugared the
    /// React <c>&amp;&amp;</c>-short-circuit idiom; setup code spliced raw JSX into
    /// <c>isOn &amp;&amp; V.Badge(...)</c>, a hard CS0019). HMR's
    /// <c>HmrCSharpEmitter.EmitCtx.SpliceSetupCodeMarkup</c> mirrors this via its own
    /// <c>TryEmitLogicalAndDesugar</c> + <c>AppendIsolatedJsxEmission</c>. If SG's
    /// ternary shape ever changes, HMR's mirror must change too.
    /// </summary>
    [Fact]
    public void Sg_LogicalAndJsx_InSetupCode_DesugarsToTernary()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode BadgeUser() {
                var isOn = true;
                var x = (isOn && <Label text="on" />);
                return (<Box>{x}</Box>);
            }
            """
        );
        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("isOn) ?", output.GeneratedSource);
        Assert.Contains(": (global::Ruitk.Core.VirtualNode?)null)", output.GeneratedSource);
        Assert.DoesNotContain("isOn && global::Ruitk.Core.V.", output.GeneratedSource);
    }

    // ── §7 path-qualified hook family keys (SG↔HMR parity) ──────────────────

    /// <summary>
    /// §7: the hook producer registers under a PATH-QUALIFIED family key
    /// <c>{EffectiveNs}.{Container}::{HookName}</c> (the convention the runtime documents).
    /// HMR's <c>HmrHookEmitter</c> mirrors this, and both worlds qualify CONSUMER keys
    /// (<c>customHookFamilyKeys</c>) through a per-file map built from the SHARED language-lib
    /// functions (<c>EffectiveNamespace.Resolve</c>/<c>UiSourceRootDir</c>,
    /// <c>ImportResolver.MapSpecifierToPath</c>) so producer ids and consumer keys are
    /// byte-identical. The runtime matches them by ordinal string equality — if the SG format
    /// changes here, the HMR mirror (<c>HmrHookEmitter</c> + <c>UitkxHmrCompiler.BuildHookFamilyKeyMap</c>)
    /// must change in lockstep or hot-swap silently breaks.
    /// </summary>
    [Fact]
    public void Sg_HookRegistration_UsesPathQualifiedFamilyKey_HmrMustMirror()
    {
        var output = GeneratorTestHelper.Run(
            "@namespace Ruitk.HmrParity\nvoid useThing() {\n  return 0;\n}");

        Assert.NotNull(output.GeneratedSource);
        Assert.Contains("RegisterHook(\"Ruitk.HmrParity.", output.GeneratedSource);
        Assert.Contains("::useThing\"", output.GeneratedSource);
        // The bare-name key must NOT be emitted.
        Assert.DoesNotContain("RegisterHook(\"useThing\"", output.GeneratedSource);
    }

    // ── §6 export accessibility + §7 multi-component (GATE-3) ───────────────────

    /// <summary>
    /// §6: an <c>export</c>ed function-component emits <c>public partial class</c>;
    /// a non-exported one emits <c>internal partial class</c>. The SG is the ground
    /// truth for the SHIPPED accessibility. HMR deliberately DIVERGES — it always
    /// emits <c>public</c> (see the notes at <c>HmrCSharpEmitter</c> ~L325 and
    /// <c>HmrHookEmitter</c> ~L282): HMR compiles each changed file into an isolated
    /// assembly and swaps by delegate/family-key, never by type identity, so a wider
    /// access modifier there is a benign superset and can never cause CS0262 against
    /// the SG partial. This test pins the SG side so any regression (e.g. dropping the
    /// modifier distinction) trips here and prompts a review of that documented
    /// divergence.
    /// </summary>
    [Fact]
    public void Sg_ExportAccessibility_PublicWhenExported_InternalOtherwise()
    {
        var exported = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            export VirtualNode Widget() {
                return (<Box />);
            }
            """
        );
        Assert.NotNull(exported.GeneratedSource);
        Assert.Contains("public partial class Widget", exported.GeneratedSource);

        var internalComp = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode Widget() {
                return (<Box />);
            }
            """
        );
        Assert.NotNull(internalComp.GeneratedSource);
        Assert.Contains("internal partial class Widget", internalComp.GeneratedSource);
    }

    /// <summary>
    /// §7: a file declaring more than one <c>component</c> emits one
    /// <c>partial class</c> per declaration (SynthesizePerComponent projects the
    /// DirectiveSet to each component in turn). HMR's per-file recompile must reach
    /// every declared component too — this pins the SG shape as the contract.
    /// </summary>
    [Fact]
    public void Sg_MultiComponentFile_EmitsOnePartialPerComponent()
    {
        var output = GeneratorTestHelper.Run(
            """
            @namespace Ruitk.HmrParity

            VirtualNode First() {
                return (<Box />);
            }
            VirtualNode Second() {
                return (<Label text="x" />);
            }
            """
        );

        Assert.NotNull(output.GeneratedSource);
        // First is the primary unit; Second is emitted as its own compilation unit
        // (ExtraSources) that merges as a partial across CUs — NOT concatenated
        // (concatenation would place Second's usings after First's namespace → CS1529).
        Assert.Contains("partial class First", output.GeneratedSource);
        Assert.Contains(output.AllSources, s => s.Text.Contains("partial class Second"));
        // Both bodies emitted — not one collapsed away.
        Assert.Contains(output.AllSources, s => s.Text.Contains("V.Box("));
        Assert.Contains(output.AllSources, s => s.Text.Contains("V.Label("));
        // Every unit is syntactically valid.
        Assert.Empty(output.SyntaxErrors());
    }
}
