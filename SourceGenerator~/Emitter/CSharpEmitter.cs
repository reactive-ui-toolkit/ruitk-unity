using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Ruitk.Language;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;

namespace Ruitk.SourceGenerator.Emitter
{
    /// <summary>
    /// Phase 4 - walks the AST produced by <see cref="UitkxParser"/> and emits a
    /// compilable C# partial-class source string.
    ///
    /// Output structure:
    /// <code>
    ///   // &lt;auto-generated /&gt;
    ///   namespace {Namespace}
    ///   {
    ///       public partial class {ComponentName}
    ///       {
    ///           private static VirtualNode[] __C(params object[] items) { ... }
    ///
    ///           public static VirtualNode Render(
    ///               Dictionary&lt;string,object&gt; __rawProps,
    ///               IReadOnlyList&lt;VirtualNode&gt; __children)
    ///           {
    ///               var props = ...;        // @props binding, if declared
    ///               // @code block content
    ///   #line N "File.uitkx"
    ///               return V.XXX(...);
    ///           }
    ///       }
    ///   }
    /// </code>
    ///
    /// The <c>#line</c> directives map generated C# lines back to the .uitkx source
    /// so that compiler errors and debugger breakpoints point at the template file.
    /// </summary>
    public static class CSharpEmitter
    {
        public static string Emit(
            string filePath,
            DirectiveSet directives,
            ImmutableArray<AstNode> rootNodes,
            PropsResolver resolver,
            IList<Diagnostic> diagnostics,
            IReadOnlyDictionary<string, string>? hookKeyMap = null
        )
        {
            var ctx = new EmitContext(filePath, directives, resolver, diagnostics, hookKeyMap);
            return ctx.BuildSource(rootNodes);
        }
    }

    // -- Implementation --------------------------------------------------------

    internal sealed class EmitContext
    {
        private readonly string _filePath;
        private readonly string _displayName; // bare filename for headers/diagnostics
        private readonly string _linePath; // normalized absolute path for #line directives
        private readonly DirectiveSet _directives;
        private readonly PropsResolver _resolver;
        private readonly IList<Diagnostic> _diagnostics;
        private readonly StringBuilder _sb = new StringBuilder(4096);
        private bool _isRootElement = true; // tracks whether the next element is the root

        // Pool-rent statement buffer: collects "var __p_N = ..." statements
        // that are emitted before the return expression (or before IIFE body code).
        private StringBuilder _rentBuffer = new StringBuilder();
        private int _poolVarId;

        // -- OPT-V2-2 Phase A: Static-Style Hoisting --------------------
        // Collects class-level `private static readonly Style __sty_N = new Style {...};`
        // declarations for `style={new Style{...}}` initializers whose every value is
        // a compile-time constant (literals + named-static dotted refs + new Color(literals)).
        // Flushed into the class body just before the closing brace.
        // The hoisted instance is created with `new Style { ... }` (generation == 0),
        // so Style.__ScheduleReturn short-circuits, the diff walk bails on SameInstance,
        // and no pool churn occurs across renders.
        private readonly StringBuilder _hoistedStyleFields = new StringBuilder();
        private int _hoistCounter;

        // -- UITKX Fast Refresh (always emitted) ----------------------------
        // Collects distinct child-component FQNs referenced at
        // function-component call sites in the rendered tree. After the
        // class body is built we emit one
        //   private static readonly Family __fam_<SanitizedFqn> =
        //       RefreshRuntime.GetFamily("<FQN>");
        // field per entry, plus a [ModuleInitializer] that registers THIS
        // component's own body under its fully-qualified name. The system
        // is active in both Editor and player builds; in player builds
        // Family.Current is never mutated, so the cost is one Family
        // allocation per component type plus one delegate indirection per
        // render call.
        //
        // FQN keys (not simple names) are critical: two components named
        // `Button` in different namespaces must NOT alias to the same
        // Family handle, or one would silently overwrite the other in the
        // registry.
        private readonly HashSet<string> _familyChildRefs = new HashSet<string>(System.StringComparer.Ordinal);

        // Build the FQN used as the Family registry key for THIS component.
        private string SelfFamilyKey() =>
            string.IsNullOrEmpty(_directives.Namespace)
                ? _directives.ComponentName
                : $"{_directives.Namespace}.{_directives.ComponentName}";

        // Sanitize an FQN like "global::MyApp.Foo.Bar" into a valid C#
        // identifier suffix ("global_MyApp_Foo_Bar") for use as the
        // __fam_* field name. Two distinct FQNs map to distinct
        // identifiers (we replace each non-identifier char with '_').
        private static string SanitizeFqnToIdent(string fqn)
        {
            if (string.IsNullOrEmpty(fqn)) return "_";
            var sb = new StringBuilder(fqn.Length);
            foreach (char c in fqn)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        // Strip a leading "global::" from an FQN, so the Family registry
        // key matches what the owning component emits in its own Register
        // call (which never includes "global::").
        private static string NormalizeFamilyKey(string fqn)
        {
            const string g = "global::";
            return fqn.StartsWith(g, System.StringComparison.Ordinal) ? fqn.Substring(g.Length) : fqn;
        }

        // Indent constants
        private const string I2 = "        "; // 8sp - class member level
        private const string I3 = "            "; // 12sp - method body level
        private const string I4 = "                "; // 16sp - inside __C args

        private const string QVNode = "global::Ruitk.Core.VirtualNode";

        private readonly IReadOnlyDictionary<string, string>? _hookKeyMap;

        internal EmitContext(
            string filePath,
            DirectiveSet directives,
            PropsResolver resolver,
            IList<Diagnostic> diagnostics,
            IReadOnlyDictionary<string, string>? hookKeyMap = null
        )
        {
            _filePath = filePath;
            _directives = directives;
            _resolver = resolver;
            _diagnostics = diagnostics;
            _displayName = Path.GetFileName(filePath);
            _linePath = NormalizeLinePath(filePath);
            _hookKeyMap = hookKeyMap;
            _isUgui = directives.Backend == "ugui";
            // Fully qualified for the ugui vocabulary so emission never depends
            // on a using; "V" stays bare so UITK output is byte-identical.
            _factory = _isUgui ? "global::Ruitk.Ugui.U" : "V";
        }

        private readonly bool _isUgui;
        private readonly string _factory;

        // -- Top-level builder -------------------------------------------------

        internal string BuildSource(ImmutableArray<AstNode> rootNodes)
        {
            // Separate markup from non-rendering nodes
            var markupNodes = ImmutableArray.CreateBuilder<AstNode>();

            foreach (var n in rootNodes)
            {
                if (n is CommentNode) { } // comments are markup-only; skip in emitted C#
                else
                    markupNodes.Add(n);
            }

            // -- Header -------------------------------------------------------
            L("// <auto-generated - do not edit - source: " + _displayName + " />");
            L("// Add class members or partial methods in the companion .cs file.");
            L("#nullable enable");
            L("#pragma warning disable CS0105  // duplicate using directives");
            L("#pragma warning disable CS0162  // unreachable code (IIFE fallback returns)");
            L("#pragma warning disable CS8600  // null literal to non-nullable");
            L("#pragma warning disable CS8601  // null literal to nullable reference type");
            L("#pragma warning disable CS8602  // dereference of possibly null reference");
            L("#pragma warning disable CS8603  // possible null reference return");
            L("#pragma warning disable CS8604  // possible null reference argument");
            L("#pragma warning disable CS8625  // cannot convert null literal to non-nullable");
            L("");

            // -- Usings -------------------------------------------------------
            L("using System;");
            L("using System.Collections.Generic;");
            L("using System.Linq;");
            L("using Ruitk;");
            L("using Ruitk.Core;");
            L("using Ruitk.Core.Animation;");
            L("using Ruitk.Router;");
            L("using Ruitk.Props.Typed;");
            L("using UnityEngine;");
            L("using static Ruitk.Props.Typed.StyleKeys;");
            L("using static Ruitk.Props.Typed.CssHelpers;");
            L("using static Ruitk.AssetHelpers;");
            L("using UColor = UnityEngine.Color;");
            // NEW-MODE files (ES-modules campaign, M7): user + injected usings are emitted
            // INSIDE the namespace block instead of here. File-keyed namespaces make every
            // file stem a member of its folder namespace, and C# resolves enclosing-namespace
            // MEMBERS before file-level using-aliases — so a file-level `using Theme =
            // {ns}.__Exports;` silently loses to the sibling namespace `…Folder.Theme` (found
            // live: every star-imported member ref broke with CS0234). A using directive
            // INSIDE the namespace declaration wins that lookup. Legacy files keep the
            // file-level position byte-identically.
            if (_directives.UsesLegacySyntax)
                foreach (var u in _directives.Usings)
                    L($"using {u};");
            // `using static StyleKeys` imports string constants (e.g. FlexDirection = "flexDirection")
            // that collide with identically-named enums/structs from UnityEngine.UIElements.
            // We cannot import UIElements wholesale. Instead, targeted aliases import only
            // the non-conflicting types that CssHelpers returns and users may reference.
            L("using Color = UnityEngine.Color;");
            L("using EasingFunction = UnityEngine.UIElements.EasingFunction;");
            L("using EasingMode = UnityEngine.UIElements.EasingMode;");
            L("using BackgroundRepeat = UnityEngine.UIElements.BackgroundRepeat;");
            L("using BackgroundPosition = UnityEngine.UIElements.BackgroundPosition;");
            L("using BackgroundSize = UnityEngine.UIElements.BackgroundSize;");
            L("using TransformOrigin = UnityEngine.UIElements.TransformOrigin;");
            L(
                "using BackgroundPositionKeyword = UnityEngine.UIElements.BackgroundPositionKeyword;"
            );
            L("using BackgroundSizeType = UnityEngine.UIElements.BackgroundSizeType;");
            L("using Repeat = UnityEngine.UIElements.Repeat;");
            L("using Length = UnityEngine.UIElements.Length;");
            L("using StyleKeyword = UnityEngine.UIElements.StyleKeyword;");
            L("using TextAutoSizeMode = UnityEngine.UIElements.TextAutoSizeMode;");
            // Unity 6.3+ types referenced via Style.UnityMaterial, Style.AspectRatio,
            // Style.Filter and the corresponding CssHelpers (MaterialDef, Ratio, Filter*).
            // Emitted as preprocessor-guarded strings so pre-6.3 builds still compile clean.
            L("#if UNITY_6000_3_OR_NEWER");
            L("using FilterFunction = UnityEngine.UIElements.FilterFunction;");
            L("using Ratio = UnityEngine.UIElements.Ratio;");
            L("using StyleRatio = UnityEngine.UIElements.StyleRatio;");
            L("using MaterialDefinition = UnityEngine.UIElements.MaterialDefinition;");
            L("using StyleMaterialDefinition = UnityEngine.UIElements.StyleMaterialDefinition;");
            L("#endif");
            L("");

            // -- Namespace + class --------------------------------------------
            L($"namespace {_directives.Namespace}");
            L("{");
            // New-mode: user + injected usings INSIDE the namespace (see the note above).
            if (!_directives.UsesLegacySyntax && !_directives.Usings.IsDefaultOrEmpty)
            {
                foreach (var u in _directives.Usings)
                    L($"    using {Ruitk.Language.ImportScopeFacts.GlobalizeUsingPayload(u)};");
                L("");
            }
            L($"    [global::Ruitk.UitkxSource(@\"{_filePath.Replace("\"", "\"\"")}\")]");
            L($"    [global::Ruitk.UitkxElement(\"{_directives.ComponentName}\")]");

            // Emit hook signature for proactive HMR state-reset detection.
            // customHookKeys are PATH-QUALIFIED (§7) via _hookKeyMap so they match the
            // producers' qualified RegisterHook ids; both feed the same runtime string-match.
            string hookSig = ExtractHookSignature(_directives.FunctionSetupCode);
            string[] customHookKeys = HookEmitter.QualifyKeys(
                ExtractCustomHookFamilyKeys(_directives.FunctionSetupCode), _hookKeyMap);
            if (hookSig.Length > 0 || customHookKeys.Length > 0)
            {
                if (customHookKeys.Length > 0)
                {
                    L($"    [global::Ruitk.HookSignature(\"{hookSig}\", {RenderCustomHookFamilyKeysLiteral(customHookKeys)})]");
                }
                else
                {
                    L($"    [global::Ruitk.HookSignature(\"{hookSig}\")]");
                }
            }

            // Accessibility (§6): `export` → public, else `internal`. This DOES emit an explicit
            // `internal` for a non-exported component, so a companion `.cs` declaring
            // `public partial class Foo` becomes CS0262, and a cross-asmdef reference becomes
            // CS0122. That is the intended contract — expose across files only what you `export`;
            // keep the companion `internal` or add `export`. The IsDefaultOrEmpty disjunct covers
            // declaration-less parses (error recovery), which stay public.
            bool componentExported =
                _directives.ComponentDeclarations.IsDefaultOrEmpty
                || _directives.ComponentDeclarations[0].IsExported;
            L($"    {(componentExported ? "public" : "internal")} partial class {_directives.ComponentName}");
            L("    {");

            EmitHelperMethod();
            EmitHookAliasWrappers();

            // -- @inject fields ------------------------------------------------
            // Each @inject directive emits a static field.  Set the value in the
            // companion (non-generated) partial class or via a static initialiser.
            if (!_directives.Injects.IsDefault && !_directives.Injects.IsEmpty)
            {
                L($"{I2}// @inject - set these fields before calling Render.");
                foreach (var inj in _directives.Injects)
                    L($"{I2}public static {inj.Type} {inj.Name};");
                L("");
            }

            // -- @uss stylesheet keys ------------------------------------------
            if (!_directives.UssFiles.IsDefaultOrEmpty)
            {
                string projectRoot = GetProjectRoot(_filePath);

                _sb.Append(
                    $"{I2}[global::Ruitk.UitkxHmrSwap] internal static string[] __uitkx_ussKeys = new string[] {{ "
                );
                for (int idx = 0; idx < _directives.UssFiles.Length; idx++)
                {
                    if (idx > 0)
                        _sb.Append(", ");
                    string rawPath = _directives.UssFiles[idx];
                    // H-03: same canonical rule as ResolveAssetPaths - bare paths resolve
                    // uitkx-dir-relative too, not just explicitly "./"/"../"-prefixed ones.
                    string resolved = Ruitk.Language.AssetPathUtil.ResolveAssetPath(
                        GetUitkxAssetDir(_filePath), rawPath,
                        Ruitk.Language.UitkxConfig.LoadRoot(System.IO.Path.GetDirectoryName(_filePath)));
                    _sb.Append($"\"{resolved}\"");

                    // Validate file existence at compile time (UITKX0022)
                    if (projectRoot != null)
                    {
                        string absolute = Path.Combine(
                            projectRoot,
                            resolved.Replace('/', Path.DirectorySeparatorChar)
                        );
                        if (!File.Exists(absolute))
                        {
                            var loc = Location.Create(_filePath, default, default);
                            _diagnostics.Add(
                                Diagnostic.Create(UitkxDiagnostics.AssetFileNotFound, loc, resolved)
                            );
                        }
                        else
                        {
                            // Type-mismatch check - @uss must reference a .uss file
                            string ext = Path.GetExtension(rawPath);
                            if (
                                !string.IsNullOrEmpty(ext)
                                && s_extensionValidTypes.TryGetValue(ext, out var validTypes)
                                && !validTypes.Contains("StyleSheet")
                            )
                            {
                                var loc = Location.Create(_filePath, default, default);
                                _diagnostics.Add(
                                    Diagnostic.Create(
                                        UitkxDiagnostics.AssetTypeMismatch,
                                        loc,
                                        "StyleSheet",
                                        ext,
                                        string.Join(", ", validTypes)
                                    )
                                );
                            }
                        }
                    }
                }
                _sb.AppendLine(" };");
                L("");
            }

            // -- Public Render entry point -------------------------------------
            // Stable, public method-group target for hand-written
            // V.Func(MyComp.Render, ...) consumers and for the SG-emitted
            // player-build call sites. Editor-build SG call sites instead
            // reference the per-component Family handle (see __fam_* field
            // emission below), so HMR delegate identity flows through the
            // Family.Current cell rather than this method.
            L($"{I2}public static {QVNode} Render(");
            L($"{I2}    global::Ruitk.Core.IProps __rawProps,");
            L($"{I2}    IReadOnlyList<{QVNode}> __children)");
            L($"{I2}{{");
            L($"{I3}return __Render_body(__rawProps, __children);");
            L($"{I2}}}");
            L("");

            // -- Render body (the actual user code) ----------------------------
            // Visibility is `internal` rather than `private` so the editor-only
            // companion class `{ComponentName}__UitkxRefresh` (emitted below)
            // can reference it via a `() => __Render_body` factory passed to
            // RefreshRuntime.Register. The companion lives in the same
            // assembly, so `internal` suffices.
            L($"{I2}[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            L($"{I2}internal static {QVNode} __Render_body(");
            L($"{I2}    global::Ruitk.Core.IProps __rawProps,");
            L($"{I2}    IReadOnlyList<{QVNode}> __children)");
            L($"{I2}{{");

            // Props binding
            if (_directives.PropsTypeName is { } propsType)
            {
                L($"{I3}var props = (__rawProps as {propsType}) ?? new {propsType}();");
                L("");
            }

            // Function-style param variable bindings: var x = props.X;
            if (
                _directives.IsFunctionStyle
                && !_directives.FunctionParams.IsDefault
                && !_directives.FunctionParams.IsEmpty
            )
            {
                foreach (var fp in _directives.FunctionParams)
                {
                    string propName = ToPropName(fp.Name);
                    L($"{I3}var {fp.Name} = props.{propName};");
                }
                L("");
            }

            // -- Function-style setup code -------------------------------------
            if (
                _directives.IsFunctionStyle
                && !string.IsNullOrWhiteSpace(_directives.FunctionSetupCode)
            )
            {
                int setupLine =
                    _directives.FunctionSetupStartLine > 0 ? _directives.FunctionSetupStartLine
                    : _directives.ComponentDeclarationLine > 0
                        ? _directives.ComponentDeclarationLine
                    : _directives.MarkupStartLine;
                L($"#line {setupLine} \"{_linePath}\"");
                string setupCode = SpliceSetupCodeMarkup(_directives.FunctionSetupCode);
                setupCode = ApplyHookAliases(setupCode);
                setupCode = ResolveAssetPaths(setupCode, _filePath, _diagnostics);
                _sb.Append(setupCode);
                if (!setupCode.EndsWith("\n"))
                    _sb.AppendLine();
            }

            // -- Return expression ---------------------------------------------
            var markup = markupNodes.ToImmutable();

            if (markup.IsEmpty)
            {
                L($"{I3}return ({QVNode})null;");
            }
            else if (markup.Length == 1)
            {
                // Single root - capture expression and rent statements separately
                int srcLine = markup[0].SourceLine;

                var savedRent = _rentBuffer;
                _rentBuffer = new StringBuilder();

                int mark = _sb.Length;
                EmitNode(markup[0]);
                string expr = _sb.ToString(mark, _sb.Length - mark);
                _sb.Length = mark;

                // Emit accumulated pool-rent statements before the return
                if (_rentBuffer.Length > 0)
                {
                    L($"#line hidden");
                    _sb.Append($"{I3}");
                    _sb.AppendLine(_rentBuffer.ToString());
                    L($"#line default");
                }
                _rentBuffer = savedRent;

                L($"#line {srcLine} \"{_linePath}\"");
                _sb.Append($"{I3}return ");
                _sb.Append(expr);
                _sb.AppendLine(";");
            }
            else
            {
                // Multiple roots - emit compile-time error
                L(
                    $"#error UITKX0025: Component return must have a single root element. Wrap multiple elements in a container like <VisualElement>."
                );
                _sb.AppendLine($"{I3}return ({QVNode})null;");
            }

            L($"{I2}}}"); // close __Render_body

            // -- Auto-generated props class for function-style components ------
            // Emitted as a NESTED type INSIDE the partial class, before its
            // closing brace (e.g. ComponentName.ComponentNameProps).
            if (
                _directives.IsFunctionStyle
                && !_directives.FunctionParams.IsDefault
                && !_directives.FunctionParams.IsEmpty
            )
            {
                EmitFunctionPropsClass();
            }

            // OPT-V2-2 Phase A: emit any hoisted static Style fields collected
            // during the render-body walk. Placed at class scope, after Render and
            // any auto-generated props class, before the partial class closing brace.
            if (_hoistedStyleFields.Length > 0)
            {
                L("");
                L($"{I2}// -- Hoisted static styles (OPT-V2-2) --");
                _sb.Append(_hoistedStyleFields.ToString());
            }

            // -- UITKX Fast Refresh: family fields + module-initializer Register --
            // Editor-only. The entire block is wrapped in #if UNITY_EDITOR so
            // player builds carry zero Family / RefreshRuntime trace - no
            // family fields, no ModuleInitializer Register call, no static
            // .cctor side effects. This mirrors React Fast Refresh's
            // $RefreshReg$ injection model: dev injects, production does not.
            // The matching V.Func emission site emits the direct-delegate
            // form under #if !UNITY_EDITOR.
            //
            // The __fam_* fields live on the component class because the SG
            // emits `V.Func<P>(__fam_X, ...)` from inside the render body
            // and the field is read at render time (long after editor init
            // hooks have run, so triggering the component .cctor at first
            // render is safe).
            //
            // The [ModuleInitializer] Register call is NOT on the component
            // class. On Mono, calling a static method on a beforefieldinit
            // class triggers that class's .cctor anyway, which would run
            // user static field initializers (e.g.
            // `static readonly Texture2D bg = Asset<T>(...)`) before
            // Unity's [InitializeOnLoadMethod] hooks populate
            // UitkxAssetRegistry. To avoid this we emit the
            // ModuleInitializer on a companion class
            // `{ComponentName}__UitkxRefresh` outside the partial class --
            // calling a method on the companion never triggers the
            // component .cctor, and the `() => __Render_body` factory
            // uses `ldftn` (which also doesn't trigger cctor) so the
            // component .cctor only runs when Family.Current is first
            // read at first render.
            L("");
            L($"#if UNITY_EDITOR");
            L($"{I2}// -- UITKX Fast Refresh (editor-only) --");
            if (_familyChildRefs.Count > 0)
            {
                foreach (var childFqn in _familyChildRefs.OrderBy(s => s, System.StringComparer.Ordinal))
                {
                    string famField = "__fam_" + SanitizeFqnToIdent(childFqn);
                    string famKey = NormalizeFamilyKey(childFqn);
                    // Fallback factory: `() => Child.Render`. For SG-emitted
                    // children the companion's [ModuleInitializer] Register
                    // runs first and the fallback is ignored. For hand-written
                    // children (e.g. router types in the Ruitk
                    // package) the fallback is what Family.Current resolves
                    // to at first render. The `ldftn Child.Render`
                    // instruction inside the lambda does NOT trigger the
                    // child's .cctor (per ECMA-335 -I.8.9.5 ldftn does not
                    // require type init; Mono honors this for ldftn -- the
                    // beforefieldinit divergence is on `call`, not ldftn).
                    L($"{I2}private static readonly global::Ruitk.Refresh.Family {famField} = global::Ruitk.Refresh.RefreshRuntime.GetFamily(\"{famKey}\", () => {childFqn}.Render);");
                }
            }
            L($"#endif // UNITY_EDITOR");

            L("    }"); // close class

            // -- Companion class with [ModuleInitializer] -----------------
            // See the long comment above for why this is a SEPARATE type.
            // Lives in the same namespace as the component, fully-qualified
            // reference to {ComponentName}.__Render_body (internal) keeps
            // the factory closure free of any access that would trigger
            // the component .cctor.
            L($"#if UNITY_EDITOR");
            string companionName = _directives.ComponentName + "__UitkxRefresh";
            string componentRef = string.IsNullOrEmpty(_directives.Namespace)
                ? "global::" + _directives.ComponentName
                : "global::" + _directives.Namespace + "." + _directives.ComponentName;
            L($"    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            L($"    [global::System.Runtime.CompilerServices.CompilerGenerated]");
            L($"    internal static class {companionName}");
            L($"    {{");
            L($"        [global::System.Runtime.CompilerServices.ModuleInitializer]");
            L($"        internal static void __Register()");
            L($"        {{");
            L($"            global::Ruitk.Refresh.RefreshRuntime.Register(");
            L($"                \"{SelfFamilyKey()}\",");
            L($"                () => {componentRef}.__Render_body,");
            L($"                \"{hookSig}\",");
            L($"                {RenderCustomHookFamilyKeysLiteral(customHookKeys)});");
            L($"        }}");
            L($"    }}");
            L($"#endif // UNITY_EDITOR");

            L("}"); // close namespace

            return _sb.ToString();
        }

        // -- Hook alias substitution -------------------------------------------
        //
        // The alias table and the generic-hook regex are sourced from
        // Ruitk.Core.HookRegistry (Shared/Core/HookRegistry.cs) so
        // CSharpEmitter, HmrCSharpEmitter, HmrHookEmitter, HooksValidator,
        // the IDE DiagnosticsAnalyzer, the LSP HoverHandler, and the virtual
        // document generator all stay in lockstep automatically.  Local
        // Regex objects below are kept compiled and instance-static so cold-
        // start performance is identical to the pre-0.5.23 hard-coded form.

        private static readonly (string From, string To)[] s_hookAliases =
            global::Ruitk.Core.HookRegistry.GetAliasTable();

        // Matches generic hook calls including up to 3 levels of nested type args:
        //   useContext<Color>(                         -> level 1
        //   useRef<List<int>>(                         -> level 2
        //   useMemo<Dictionary<string, List<Color>>>(  -> level 3
        // The non-generic form is handled by s_hookAliases simple replacements above.
        private static readonly System.Text.RegularExpressions.Regex s_genericHookAliasRe =
            new System.Text.RegularExpressions.Regex(
                global::Ruitk.Core.HookRegistry.GetGenericHookPattern(),
                System.Text.RegularExpressions.RegexOptions.Compiled
            );

        internal static string ApplyHookAliases(string code)
        {
            // State-setter lambda sugar applies regardless of whether hooks are present:
            //   setFoo(v => v + 1)  ->  setFoo.Set(v => v + 1)
            // Matches conventional React-style setter names: set followed by an
            // upper-case letter, called with a lambda argument.
            // Replacement: "$1.Set(" - group 1 is the setter name; the matched
            // opening "(" is consumed by "\(" in the pattern and rebuilt as ".Set(".
            code = s_setterLambdaRe.Replace(code, "$1.Set(");

            // Fast path: bail early if no lowercase hook names are present
            if (code.IndexOf("use", StringComparison.Ordinal) < 0)
                return code;
            // Handle generic hook calls (e.g. useContext<Color>() -> Hooks.UseContext<Color>())
            code = s_genericHookAliasRe.Replace(
                code,
                m =>
                {
                    string hookName = m.Groups[1].Value;
                    string typeArgs = m.Groups[2].Value;
                    string pascalName = char.ToUpper(hookName[3]) + hookName.Substring(4);
                    return $"Hooks.Use{pascalName}{typeArgs}(";
                }
            );
            foreach (var (from, to) in s_hookAliases)
                code = code.Replace(from, to);

            return code;
        }

        // Matches:  setX(   setFoo(   setState(  followed immediately by a lambda
        // arg (single or multiple params), but NOT when it's setX.Set( already.
        private static readonly System.Text.RegularExpressions.Regex s_setterLambdaRe =
            new System.Text.RegularExpressions.Regex(
                @"\b(set[A-Z][a-zA-Z0-9_]*)\(\s*(?=[a-zA-Z_][a-zA-Z0-9_]*\s*=>|\([^)]*\)\s*=>)",
                System.Text.RegularExpressions.RegexOptions.Compiled
            );

        // Matches Asset<T>("path") or Ast<T>("path") - type in group[1], path in group[2].
        private static readonly System.Text.RegularExpressions.Regex s_assetCallRe =
            new System.Text.RegularExpressions.Regex(
                @"(?:Asset|Ast)\s*<\s*(\w+)\s*>\s*\(\s*""([^""]+)""\s*\)",
                System.Text.RegularExpressions.RegexOptions.Compiled
            );

        // -- Hook signature extraction ------------------------------------------

        /// <summary>
        /// Matches any hook call in setup code - both user-written camelCase
        /// (useState, useEffect) and fully-qualified PascalCase (Hooks.UseState).
        /// Captures the hook name (without "Hooks." prefix) in group 1.
        /// Pattern is sourced from HookRegistry so it stays in lockstep with
        /// the alias table above.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex s_hookSignatureRe =
            new System.Text.RegularExpressions.Regex(
                global::Ruitk.Core.HookRegistry.GetSignatureRegexPattern(),
                System.Text.RegularExpressions.RegexOptions.Compiled
            );

        /// <summary>
        /// Scans raw setup code for hook call patterns and returns
        /// a comma-separated ordered signature string (e.g. "UseState,UseEffect,UseMemo").
        /// Returns empty string if no hooks are found.
        /// </summary>
        internal static string ExtractHookSignature(string setupCode)
        {
            if (string.IsNullOrWhiteSpace(setupCode))
                return string.Empty;

            // Strip comments and string literals before scanning so commented-out
            // hook calls (e.g. `// useEffect(...)`) and hook-name substrings inside
            // strings don't pollute the signature. Without this scrub a comment
            // toggle would leave the signature identical, sigChanged stays false,
            // and FullResetComponentState never runs -- so the now-removed
            // useEffect's cleanup never fires and any pending Timer / subscription
            // it captured leaks until domain reload.
            string scrubbed = ScrubCommentsAndStrings(setupCode);

            var matches = s_hookSignatureRe.Matches(scrubbed);
            if (matches.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < matches.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(NormalizeHookName(matches[i].Groups[1].Value));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Normalizes a hook name to its canonical PascalCase form
        /// matching the runtime hook ID constants in Hooks.cs.
        /// </summary>
        internal static string NormalizeHookName(string name)
        {
            // Already PascalCase
            if (char.IsUpper(name[0]))
                return name;

            // camelCase -> PascalCase: useState -> UseState, provideContext -> ProvideContext
            if (name.StartsWith("use"))
                return "Use" + char.ToUpper(name[3]) + name.Substring(4);
            if (name.StartsWith("provide"))
                return "Provide" + char.ToUpper(name[7]) + name.Substring(8);

            return name;
        }

        // -- Phase 2: custom hook discovery ----------------------------------

        /// <summary>
        /// Matches any hook-shaped call (custom or built-in): identifiers
        /// starting with <c>use[A-Z]</c> / <c>Use[A-Z]</c> /
        /// <c>provide[A-Z]</c> / <c>Provide[A-Z]</c>, optionally preceded by
        /// a <c>Hooks.</c> qualifier, optionally with generic type args, then
        /// an opening paren. The capture group is the bare identifier - we
        /// filter against <see cref="s_builtinHookNames"/> to keep only
        /// CUSTOM hooks (those the SG/HMR registered via <c>RegisterHook</c>).
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex s_anyHookCallRe =
            new System.Text.RegularExpressions.Regex(
                @"(?:Hooks\.)?\b((?:use|Use|provide|Provide)[A-Z][A-Za-z0-9_]*)(?:<(?:[^<>]|<(?:[^<>]|<[^<>]*>)*>)*>)?\s*\(",
                System.Text.RegularExpressions.RegexOptions.Compiled
            );

        /// <summary>
        /// PascalCase canonical names of every built-in hook recognised by
        /// the runtime. Used by
        /// <see cref="ExtractCustomHookFamilyKeys"/> to subtract built-ins
        /// from the broader hook-shaped-call match set.
        /// </summary>
        private static readonly HashSet<string> s_builtinHookNames = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "UseState", "UseEffect", "UseLayoutEffect", "UseRef", "UseCallback",
            "UseMemo", "UseContext", "UseReducer", "UseSignal", "UseDeferredValue",
            "UseTransition", "UseSafeArea", "UseStableFunc", "UseStableAction",
            "UseStableCallback", "UseImperativeHandle", "UseAnimate", "UseTweenFloat",
            "UseUiDocumentRoot", "UseSfx", "ProvideContext",
        };

        /// <summary>
        /// Scans raw body / setup code and returns the distinct, source-order
        /// list of custom hook names called at first level. Built-ins are
        /// filtered out. The returned names are the LITERAL identifiers as
        /// they appear in source (e.g. <c>useUiDocumentSlot</c>), which is
        /// also the family key used by both the consumer (Phase 2) and the
        /// hook author (Phase 1 <c>RegisterHook</c>). Empty when no custom
        /// hooks are called.
        /// </summary>
        internal static string[] ExtractCustomHookFamilyKeys(string setupCode)
        {
            if (string.IsNullOrWhiteSpace(setupCode))
                return Array.Empty<string>();

            string scrubbed = ScrubCommentsAndStrings(setupCode);
            var matches = s_anyHookCallRe.Matches(scrubbed);
            if (matches.Count == 0)
                return Array.Empty<string>();

            List<string> ordered = null;
            HashSet<string> seen = null;
            for (int i = 0; i < matches.Count; i++)
            {
                string name = matches[i].Groups[1].Value;
                if (s_builtinHookNames.Contains(NormalizeHookName(name)))
                    continue;
                if (seen == null)
                {
                    ordered = new List<string>();
                    seen = new HashSet<string>(StringComparer.Ordinal);
                }
                if (seen.Add(name))
                    ordered.Add(name);
            }
            return ordered?.ToArray() ?? Array.Empty<string>();
        }

        /// <summary>
        /// Renders a <c>string[]</c> as a C# array initializer literal:
        /// empty -> <c>null</c>, non-empty -> <c>new string[] { "a", "b" }</c>.
        /// Used by both the <c>[HookSignature(...)]</c> attribute and the
        /// <c>RefreshRuntime.Register(...)</c> call to emit the
        /// customHookFamilyKeys argument in a single canonical form.
        /// </summary>
        internal static string RenderCustomHookFamilyKeysLiteral(string[] keys)
        {
            if (keys == null || keys.Length == 0)
                return "null";
            var sb = new StringBuilder("new string[] { ");
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"');
                sb.Append(EscapeStringLiteralForCSharp(keys[i]));
                sb.Append('"');
            }
            sb.Append(" }");
            return sb.ToString();
        }

        internal static string EscapeStringLiteralForCSharp(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Replaces comment and string-literal contents with spaces so that
        /// hook-name regex matches don't land inside neutralised text. Preserves
        /// positions (length is unchanged) and newlines (so any positional
        /// diagnostics remain accurate). Handles `//` line comments, `/* */`
        /// block comments, `"..."` strings (with `\"` escapes), `@"..."`
        /// verbatim strings (with `""` escapes), `$"..."` interpolated strings
        /// (treated opaquely - hooks inside ${} holes are not counted, which is
        /// an acceptable false-negative; nobody calls hooks inside string
        /// interpolation), and `'.'` char literals.
        /// </summary>
        internal static string ScrubCommentsAndStrings(string code)
        {
            if (string.IsNullOrEmpty(code))
                return code;

            var sb = new StringBuilder(code.Length);
            int i = 0;
            int n = code.Length;
            while (i < n)
            {
                char c = code[i];

                // Line comment
                if (c == '/' && i + 1 < n && code[i + 1] == '/')
                {
                    while (i < n && code[i] != '\n')
                    {
                        sb.Append(code[i] == '\r' ? '\r' : ' ');
                        i++;
                    }
                    continue;
                }

                // Block comment
                if (c == '/' && i + 1 < n && code[i + 1] == '*')
                {
                    sb.Append("  ");
                    i += 2;
                    while (i < n && !(code[i] == '*' && i + 1 < n && code[i + 1] == '/'))
                    {
                        sb.Append(code[i] == '\n' || code[i] == '\r' ? code[i] : ' ');
                        i++;
                    }
                    if (i < n)
                    {
                        sb.Append("  ");
                        i += 2;
                    }
                    continue;
                }

                // Char literal
                if (c == '\'')
                {
                    sb.Append(' ');
                    i++;
                    while (i < n && code[i] != '\'')
                    {
                        if (code[i] == '\\' && i + 1 < n)
                        {
                            sb.Append("  ");
                            i += 2;
                            continue;
                        }
                        sb.Append(' ');
                        i++;
                    }
                    if (i < n)
                    {
                        sb.Append(' ');
                        i++;
                    }
                    continue;
                }

                // Verbatim or interpolated-verbatim string: @"...", $@"...", @$"..."
                bool atVerbatim = c == '@' && i + 1 < n && code[i + 1] == '"';
                bool dollarAtVerbatim =
                    (c == '$' && i + 2 < n && code[i + 1] == '@' && code[i + 2] == '"')
                    || (c == '@' && i + 2 < n && code[i + 1] == '$' && code[i + 2] == '"');
                if (atVerbatim || dollarAtVerbatim)
                {
                    int prefix = dollarAtVerbatim ? 3 : 2;
                    for (int k = 0; k < prefix; k++)
                        sb.Append(' ');
                    i += prefix;
                    while (i < n)
                    {
                        if (code[i] == '"')
                        {
                            if (i + 1 < n && code[i + 1] == '"')
                            {
                                sb.Append("  ");
                                i += 2;
                                continue;
                            }
                            sb.Append(' ');
                            i++;
                            break;
                        }
                        sb.Append(code[i] == '\n' || code[i] == '\r' ? code[i] : ' ');
                        i++;
                    }
                    continue;
                }

                // Regular string (possibly interpolated single-quote-dollar): "..." or $"..."
                bool dollarString = c == '$' && i + 1 < n && code[i + 1] == '"';
                if (c == '"' || dollarString)
                {
                    int prefix = dollarString ? 2 : 1;
                    for (int k = 0; k < prefix; k++)
                        sb.Append(' ');
                    i += prefix;
                    while (i < n && code[i] != '"')
                    {
                        if (code[i] == '\\' && i + 1 < n)
                        {
                            sb.Append("  ");
                            i += 2;
                            continue;
                        }
                        sb.Append(
                            code[i] == '\n' ? '\n' : (code[i] == '\r' ? '\r' : ' ')
                        );
                        i++;
                    }
                    if (i < n)
                    {
                        sb.Append(' ');
                        i++;
                    }
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static readonly Dictionary<string, HashSet<string>> s_extensionValidTypes = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            {
                ".png",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".jpg",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".jpeg",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".bmp",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".tga",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".psd",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".gif",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".tif",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".tiff",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".exr",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".hdr",
                new HashSet<string> { "Texture2D", "Sprite" }
            },
            {
                ".svg",
                new HashSet<string> { "VectorImage" }
            },
            {
                ".wav",
                new HashSet<string> { "AudioClip" }
            },
            {
                ".mp3",
                new HashSet<string> { "AudioClip" }
            },
            {
                ".ogg",
                new HashSet<string> { "AudioClip" }
            },
            {
                ".aiff",
                new HashSet<string> { "AudioClip" }
            },
            {
                ".ttf",
                new HashSet<string> { "Font" }
            },
            {
                ".otf",
                new HashSet<string> { "Font" }
            },
            {
                ".mat",
                new HashSet<string> { "Material" }
            },
            {
                ".uss",
                new HashSet<string> { "StyleSheet" }
            },
            {
                ".renderTexture",
                new HashSet<string> { "RenderTexture" }
            },
        };

        // -- __C helper method -------------------------------------------------

        private void EmitHelperMethod()
        {
            // Generates a child-array builder (used only for the dynamic JSX path:
            // children that contain @if / @for / @foreach / @while / @switch / {expr}).
            //
            //   * skips null VirtualNode entries (from @if without @else)
            //   * flattens VirtualNode[] / IReadOnlyList<VirtualNode> / IEnumerable<VirtualNode>
            //     (from @foreach -> .Select().ToArray() and from {__children} slot
            //     pass-through, where __children has compile-time type
            //     IReadOnlyList<VirtualNode>)
            //
            // Two-pass count-then-fill into a single freshly-allocated VirtualNode[count].
            // No transient List<VirtualNode> + ToArray() copy.
            //
            // For elements whose children are 100% statically simple (only ElementNode /
            // TextNode / CommentNode), the source generator bypasses this helper entirely
            // and passes children directly to the container's `params VirtualNode[]`
            // parameter -- see TryClassifySimpleChildren.
            L($"{I2}private static {QVNode}[] __C(params object[] items)");
            L($"{I2}{{");
            L($"{I3}// Pass 1: count valid VNodes.");
            L($"{I3}int __count = 0;");
            L($"{I3}for (int __i = 0; __i < items.Length; __i++)");
            L($"{I3}{{");
            L($"{I4}var __ci = items[__i];");
            L($"{I4}if (__ci is {QVNode} __vn) {{ if (__vn != null) __count++; }}");
            L(
                $"{I4}else if (__ci is global::System.Collections.Generic.IReadOnlyList<{QVNode}> __ros)"
            );
            L($"{I4}{{");
            L($"{I4}    int __rn = __ros.Count;");
            L($"{I4}    for (int __j = 0; __j < __rn; __j++) if (__ros[__j] != null) __count++;");
            L($"{I4}}}");
            L(
                $"{I4}else if (__ci is global::System.Collections.Generic.IEnumerable<{QVNode}> __seq)"
            );
            L($"{I4}    foreach (var __sn in __seq) if (__sn != null) __count++;");
            L($"{I3}}}");
            L($"{I3}if (__count == 0) return global::System.Array.Empty<{QVNode}>();");
            L("");
            L($"{I3}// Pass 2: fill into pre-sized array.");
            L($"{I3}var __result = new {QVNode}[__count];");
            L($"{I3}int __k = 0;");
            L($"{I3}for (int __i = 0; __i < items.Length; __i++)");
            L($"{I3}{{");
            L($"{I4}var __ci = items[__i];");
            L($"{I4}if (__ci is {QVNode} __vn) {{ if (__vn != null) __result[__k++] = __vn; }}");
            L(
                $"{I4}else if (__ci is global::System.Collections.Generic.IReadOnlyList<{QVNode}> __ros)"
            );
            L($"{I4}{{");
            L($"{I4}    int __rn = __ros.Count;");
            L($"{I4}    for (int __j = 0; __j < __rn; __j++)");
            L($"{I4}    {{");
            L($"{I4}        var __sn = __ros[__j];");
            L($"{I4}        if (__sn != null) __result[__k++] = __sn;");
            L($"{I4}    }}");
            L($"{I4}}}");
            L(
                $"{I4}else if (__ci is global::System.Collections.Generic.IEnumerable<{QVNode}> __seq)"
            );
            L($"{I4}    foreach (var __sn in __seq) if (__sn != null) __result[__k++] = __sn;");
            L($"{I3}}}");
            L($"{I3}return __result;");
            L($"{I2}}}");
            L("");
        }

        // ============================================================
        //  Phase A children classifier
        // ============================================================
        //
        // Returns true when *every* non-comment child is statically guaranteed to
        // produce exactly one non-null VirtualNode (i.e. only ElementNode and
        // TextNode), AND there is at least one such child.  When true, callers may
        // bypass `__C(...)` and emit the children directly into the container
        // method's `params VirtualNode[] children` parameter -- saving the
        // `params object[]` boxing allocation and the `__C` array copy.
        //
        // IfNode / ForNode / ForeachNode / WhileNode / SwitchNode / ExpressionNode
        // all return false: they may yield null (IfNode without @else) or
        // IEnumerable<VirtualNode> (loops, slot pass-through), which __C must
        // null-filter and flatten.
        //
        // Returns false if there are no effective (non-comment) children -- in that
        // case the caller must still take the __C path so that EmitChildArgs has
        // something to emit between the parens (otherwise we'd produce a syntax
        // error like "V.Box(props, key: k, )").
        private static bool TryClassifySimpleChildren(ImmutableArray<AstNode> children)
        {
            int effectiveCount = 0;
            foreach (var c in children)
            {
                if (c is CommentNode)
                    continue;
                if (c is ElementNode || c is TextNode)
                {
                    effectiveCount++;
                    continue;
                }
                return false;
            }
            return effectiveCount > 0;
        }

        /// <summary>
        /// Emits camelCase hook alias wrapper methods that delegate to the real
        /// <c>Hooks.*</c> static API.  This makes <c>useState()</c>,
        /// <c>useEffect()</c>, etc. resolve in ALL scopes - setup code, event
        /// handler lambdas, local functions, directive bodies - without needing
        /// text-based rewriting.
        ///
        /// The wrappers are emitted with <c>#line hidden</c> so they never
        /// appear in stack traces or diagnostic locations.
        /// </summary>
        private void EmitHookAliasWrappers()
        {
            L("#line hidden");
            // useState
            L(
                $"{I2}private static (T value, Hooks.StateSetter<T> set) useState<T>(T initial = default) => Hooks.UseState(initial);"
            );
            // useEffect / useLayoutEffect
            L(
                $"{I2}private static void useEffect(global::System.Func<global::System.Action> effectFactory, params object[] deps) => Hooks.UseEffect(effectFactory, deps);"
            );
            L(
                $"{I2}private static void useLayoutEffect(global::System.Func<global::System.Action> effectFactory, params object[] deps) => Hooks.UseLayoutEffect(effectFactory, deps);"
            );
            // useRef (two overloads)
            L(
                $"{I2}private static global::Ruitk.Core.Ref<T> useRef<T>(T initial = default) => Hooks.UseRef(initial);"
            );
            L(
                $"{I2}private static global::UnityEngine.UIElements.VisualElement useRef() => Hooks.UseRef();"
            );
            // useCallback / useMemo
            L(
                $"{I2}private static global::System.Func<T> useCallback<T>(global::System.Func<T> callback, params object[] deps) => Hooks.UseCallback(callback, deps);"
            );
            L(
                $"{I2}private static T useMemo<T>(global::System.Func<T> factory, params object[] deps) => Hooks.UseMemo(factory, deps);"
            );
            // useContext / provideContext
            L($"{I2}private static T useContext<T>(string key) => Hooks.UseContext<T>(key);");
            L(
                $"{I2}private static void provideContext<T>(string key, T value) => Hooks.ProvideContext(key, value);"
            );
            L(
                $"{I2}private static void provideContext(string key, object value) => Hooks.ProvideContext(key, value);"
            );
            // useSignal (two overloads)
            L(
                $"{I2}private static T useSignal<T>(global::Ruitk.Signals.Signal<T> signal) => Hooks.UseSignal(signal);"
            );
            L(
                $"{I2}private static T useSignal<T>(string key, T initialValue = default) => Hooks.UseSignal(key, initialValue);"
            );
            // useReducer
            L(
                $"{I2}private static (TState state, global::System.Action<TAction> dispatch) useReducer<TState, TAction>(global::System.Func<TState, TAction, TState> reducer, TState initialState) => Hooks.UseReducer(reducer, initialState);"
            );
            // useDeferredValue
            L(
                $"{I2}private static T useDeferredValue<T>(T value, params object[] deps) => Hooks.UseDeferredValue(value, deps);"
            );
            L("#line default");
            L("");
        }

        // -- Node dispatch -----------------------------------------------------

        private void EmitNode(AstNode node)
        {
            switch (node)
            {
                case ElementNode e:
                    EmitElementNode(e);
                    break;
                case TextNode t:
                    EmitTextNode(t);
                    break;
                case ExpressionNode x:
                    EmitExpressionNode(x);
                    break;
                case IfNode i:
                    EmitIfNode(i);
                    break;
                case ForeachNode f:
                    EmitForeachNode(f);
                    break;
                case ForNode fn:
                    EmitForNode(fn);
                    break;
                case WhileNode wn:
                    EmitWhileNode(wn);
                    break;
                case SwitchNode s:
                    EmitSwitchNode(s);
                    break;
                case CommentNode:
                    // comments emit nothing to C#
                    break;
                default:
                    _sb.Append($"({QVNode})null /* unhandled: {node.GetType().Name} */");
                    break;
            }
        }

        // -- ElementNode -------------------------------------------------------

        private void EmitElementNode(ElementNode el)
        {
            bool injectUss = _isRootElement && !_directives.UssFiles.IsDefaultOrEmpty;
            _isRootElement = false;

            // Search the current namespace first, then explicit @using namespaces.
            // This matches C# lookup rules better and avoids cross-namespace peer
            // components falling back to stale metadata matches.
            var searchNamespaces = BuildSearchNamespaces();
            var res = _resolver.Resolve(el.TagName, searchNamespaces, out var diagDesc);

            if (diagDesc != null)
            {
                var loc = MakeLoc(_filePath, el.SourceLine);
                _diagnostics.Add(Diagnostic.Create(diagDesc, loc, el.TagName));
            }

            string keyExpr = ExtractKey(el.Attributes);

            switch (res.Kind)
            {
                case TagResolutionKind.Fragment:
                    EmitFragment(keyExpr, el.Children);
                    break;

                case TagResolutionKind.BuiltinTyped:
                    EmitBuiltinTyped(res, el.Attributes, keyExpr, el.Children, injectUss);
                    break;

                case TagResolutionKind.BuiltinDictionary:
                    EmitBuiltinDict(res, el.Attributes, keyExpr, el.Children, injectUss);
                    break;

                case TagResolutionKind.BuiltinText:
                {
                    string txt = GetAttrValue(el.Attributes, "text") ?? "\"\"";
                    _sb.Append($"V.Text({txt}, key: {keyExpr})");
                    break;
                }

                case TagResolutionKind.BuiltinSuspense:
                    EmitSuspense(el.Attributes, keyExpr, el.Children);
                    break;

                case TagResolutionKind.BuiltinPortal:
                    EmitPortal(el.Attributes, keyExpr, el.Children);
                    break;

                case TagResolutionKind.FuncComponent:
                    EmitFuncComponent(res, el.Attributes, keyExpr, el.Children, searchNamespaces);
                    break;

                case TagResolutionKind.Unknown:
                    _sb.Append($"({QVNode})null /* unknown: {el.TagName} */");
                    break;
            }
        }

        private ImmutableArray<string> BuildSearchNamespaces()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var builder = ImmutableArray.CreateBuilder<string>();

            if (!string.IsNullOrEmpty(_directives.Namespace) && seen.Add(_directives.Namespace!))
                builder.Add(_directives.Namespace!);

            foreach (var ns in _directives.Usings)
            {
                if (seen.Add(ns))
                    builder.Add(ns);
            }

            // The auto-injected baseline (AutoInjectedUsings) is in scope in every generated file,
            // so the resolver MUST search it too or its view of scope drifts from the emitted C#.
            // Regression: when Ruitk.Router joined the baseline, files stopped writing
            // `import "@Ruitk.Router"` — the emitted C# still resolved <Route>/RouteFunc via
            // the baseline `using`, but this list (then usings-only) no longer did → UITKX0008 on
            // <Routes>/<Route>/<Outlet> and false UITKX0109 on their attributes broke the floor-Unity
            // publish. User usings come first so a more-specific type still wins on name collision.
            foreach (var ns in AutoInjectedUsings.Namespaces)
            {
                if (seen.Add(ns))
                    builder.Add(ns);
            }

            // Namespaces of file-imported peer components: `import { X } from "./file"` names the
            // exact target, so its namespace is visible on the file import alone — no separate
            // `import "@Target.Ns"` needed for the tag's attributes to validate.
            foreach (var ns in _resolver.GetImportedPeerNamespaces(_directives, _filePath))
            {
                if (seen.Add(ns))
                    builder.Add(ns);
            }

            return builder.ToImmutable();
        }

        private void EmitBuiltinTyped(
            TagResolution res,
            ImmutableArray<AttributeNode> attrs,
            string keyExpr,
            ImmutableArray<AstNode> children,
            bool injectUssKeys = false
        )
        {
            // UITKX0109 - validate attribute names against known Props properties.
            if (res.PropsTypeName != null)
            {
                var knownProps = _resolver.GetPublicPropertyNames(res.PropsTypeName);
                if (knownProps.Count > 0)
                {
                    foreach (var attr in attrs)
                    {
                        if (IsKey(attr.Name))
                            continue;
                        string mapped = ToPropName(attr.Name);
                        if (!knownProps.Contains(mapped))
                        {
                            string? suggestion = FindClosestMatch(mapped, knownProps);
                            string hint =
                                suggestion != null ? $". Did you mean '{suggestion}'?" : "";
                            var loc = MakeLoc(_filePath, attr.SourceLine);
                            _diagnostics.Add(
                                Diagnostic.Create(
                                    UitkxDiagnostics.UnknownAttribute,
                                    loc,
                                    attr.Name,
                                    res.MethodName.ToLowerInvariant(),
                                    hint
                                )
                            );
                        }
                    }
                }
            }

            _sb.Append($"{_factory}.{res.MethodName}(");

            // ErrorBoundaryProps extends IProps (not BaseProps) - cannot be pooled
            bool skipPooling = res.PropsTypeName == "ErrorBoundaryProps";

            if (skipPooling)
            {
                // Fall back to old-style object initializer
                _sb.Append($"new {res.PropsTypeName} {{ ");
                bool first = true;
                foreach (var attr in attrs)
                {
                    if (IsKey(attr.Name))
                        continue;
                    if (!first)
                        _sb.Append(", ");
                    first = false;
                    _sb.Append($"{ToPropName(attr.Name)} = {AttrVal(attr)}");
                }
                if (injectUssKeys)
                {
                    if (!first)
                        _sb.Append(", ");
                    _sb.Append(
                        "ExtraProps = new Dictionary<string, object> { { \"__ussKeys\", __uitkx_ussKeys } }"
                    );
                }
                _sb.Append($" }}, key: ");
                _sb.Append(keyExpr);
            }
            else
            {
                // -- Pool-rent emission: Props + Style ----------------------
                int pId = _poolVarId++;
                string propsVar = $"__p_{pId}";
                _rentBuffer.Append(
                    _isUgui
                        ? $"var {propsVar} = global::Ruitk.Ugui.UguiBaseProps.__Rent<global::Ruitk.Ugui.{res.PropsTypeName}>(); "
                        : $"var {propsVar} = global::Ruitk.Props.Typed.BaseProps.__Rent<{res.PropsTypeName}>(); "
                );

                // Check for style attribute - try to hoist (Phase A) or pool it.
                // The ugui props family has no Style surface — a stray style attr
                // is caught by attribute validation, not the hoist machinery.
                string? styleVarName = null;
                foreach (var attr in attrs)
                {
                    if (_isUgui)
                        break;
                    if (IsKey(attr.Name))
                        continue;
                    if (string.Equals(ToPropName(attr.Name), "Style", StringComparison.Ordinal))
                    {
                        string val = AttrVal(attr);

                        // -- OPT-V2-2 Phase A: try to hoist all-literal styles --
                        // Handles BOTH `new Style { Width = 5f, ... }` (setter form,
                        // currently goes through pool rent) AND
                        // `new Style { (StyleKeys.Width, 5f), ... }` (tuple form,
                        // currently allocates a fresh `new Style` per render).
                        if (TryHoistStaticStyle(val, out string? hoistedName))
                        {
                            styleVarName = hoistedName;
                        }
                        else if (TryExtractNewStyleInit(val, out string? body))
                        {
                            int sId = _poolVarId++;
                            styleVarName = $"__s_{sId}";
                            _rentBuffer.Append(
                                $"var {styleVarName} = global::Ruitk.Props.Typed.Style.__Rent(); "
                            );
                            // Split initializers and emit assignments
                            var inits = SplitTopLevelCommas(body);
                            foreach (var init in inits)
                            {
                                string trimmed = init.Trim();
                                if (trimmed.Length > 0)
                                    _rentBuffer.Append($"{styleVarName}.{trimmed}; ");
                            }
                        }
                        break;
                    }
                }

                // Emit property assignments to _rentBuffer
                foreach (var attr in attrs)
                {
                    if (IsKey(attr.Name))
                        continue;
                    string propName = ToPropName(attr.Name);
                    string val;
                    if (
                        string.Equals(propName, "Style", StringComparison.Ordinal)
                        && styleVarName != null
                    )
                        val = styleVarName;
                    else
                        val = AttrVal(attr);
                    _rentBuffer.Append($"{propsVar}.{propName} = {val}; ");
                }

                if (injectUssKeys)
                {
                    _rentBuffer.Append(
                        $"{propsVar}.ExtraProps = new Dictionary<string, object> {{ {{ \"__ussKeys\", __uitkx_ussKeys }} }}; "
                    );
                }

                _sb.Append($"{propsVar}, key: ");
                _sb.Append(keyExpr);
            } // end else (!skipPooling)

            if (res.AcceptsChildren && !children.IsEmpty)
            {
                if (TryClassifySimpleChildren(children))
                {
                    // Phase A bypass: emit children directly into params VirtualNode[]
                    _sb.Append(", ");
                    EmitChildArgs(children);
                }
                else
                {
                    _sb.Append(", __C(");
                    EmitChildArgs(children);
                    _sb.Append(")");
                }
            }

            _sb.Append(")");
        }

        private void EmitBuiltinDict(
            TagResolution res,
            ImmutableArray<AttributeNode> attrs,
            string keyExpr,
            ImmutableArray<AstNode> children,
            bool injectUssKeys = false
        )
        {
            bool hasNonKeyAttrs = injectUssKeys;
            foreach (var a in attrs)
            {
                if (!IsKey(a.Name))
                {
                    hasNonKeyAttrs = true;
                    break;
                }
            }

            if (hasNonKeyAttrs)
            {
                _sb.Append($"V.{res.MethodName}(new Dictionary<string, object>");
                _sb.Append(" {");
                bool first = true;
                foreach (var attr in attrs)
                {
                    if (IsKey(attr.Name))
                        continue;
                    if (!first)
                        _sb.Append(", ");
                    first = false;
                    _sb.Append($" {{ \"{attr.Name}\", {AttrVal(attr)} }}");
                }
                if (injectUssKeys)
                {
                    if (!first)
                        _sb.Append(", ");
                    _sb.Append(" { \"__ussKeys\", __uitkx_ussKeys }");
                }
                _sb.Append(" }");
            }
            else
            {
                _sb.Append($"V.{res.MethodName}((Dictionary<string, object>)null");
            }

            _sb.Append($", key: {keyExpr}");

            if (!children.IsEmpty)
            {
                if (TryClassifySimpleChildren(children))
                {
                    _sb.Append(", ");
                    EmitChildArgs(children);
                }
                else
                {
                    _sb.Append(", __C(");
                    EmitChildArgs(children);
                    _sb.Append(")");
                }
            }

            _sb.Append(")");
        }

        private void EmitFuncComponent(
            TagResolution res,
            ImmutableArray<AttributeNode> attrs,
            string keyExpr,
            ImmutableArray<AstNode> children,
            ImmutableArray<string> searchNamespaces
        )
        {
            string typeName = res.FuncTypeName!;

            // Pull out a bare ref={x} attribute before the loop so it can be
            // routed to the component's MutableRef<T> param rather than rendered
            // as a literal "ref" property (which doesn't exist on any Props class).
            AttributeNode? refAttr = null;
            foreach (var a in attrs)
            {
                if (IsRefAttr(a.Name))
                {
                    refAttr = a;
                    break;
                }
            }

            // -- UITKX0109 - validate attribute names against the user-component
            // prop surface (declared parameters only - user components do NOT
            // inherit BaseProps, so style/onClick/etc. are NOT auto-allowed).
            // key/ref are exempt: they are structural-universal (key lives on
            // VirtualNode; ref is routed via MutableRef<T> - handled below).
            //
            // Unknown attributes are collected into `unknownAttrs` so the
            // subsequent emit loops skip them, preventing a follow-on
            // CS0117 / CS0246 against the generated *Props class.
            HashSet<string>? unknownAttrs = null;
            string displayName = SimpleNameForDiagnostic(typeName);
            if (res.FuncPropsTypeName != null)
            {
                var knownProps = _resolver.GetPublicPropertyNamesByQualifiedName(
                    res.FuncPropsTypeName
                );
                if (knownProps.Count > 0)
                {
                    foreach (var attr in attrs)
                    {
                        if (IsKey(attr.Name) || IsRefAttr(attr.Name))
                            continue;
                        string mapped = ToPropName(attr.Name);
                        if (!knownProps.Contains(mapped))
                        {
                            (unknownAttrs ??= new HashSet<string>(StringComparer.Ordinal)).Add(
                                attr.Name
                            );
                            string hint = BuildUnknownAttrHintForUserComponent(
                                mapped,
                                displayName,
                                knownProps
                            );
                            var loc = MakeLoc(_filePath, attr.SourceLine);
                            _diagnostics.Add(
                                Diagnostic.Create(
                                    UitkxDiagnostics.UnknownAttribute,
                                    loc,
                                    attr.Name,
                                    displayName,
                                    hint
                                )
                            );
                        }
                    }
                }
            }
            else
            {
                // No-props user component: any non-structural attribute is unknown.
                foreach (var attr in attrs)
                {
                    if (IsKey(attr.Name) || IsRefAttr(attr.Name))
                        continue;
                    (unknownAttrs ??= new HashSet<string>(StringComparer.Ordinal)).Add(attr.Name);
                    string hint =
                        $". Component '{displayName}' declares no parameters; add one to the component or remove the attribute.";
                    var loc = MakeLoc(_filePath, attr.SourceLine);
                    _diagnostics.Add(
                        Diagnostic.Create(
                            UitkxDiagnostics.UnknownAttribute,
                            loc,
                            attr.Name,
                            displayName,
                            hint
                        )
                    );
                }
            }

            if (res.FuncPropsTypeName != null)
            {
                // -- Typed path: dual-shape V.Func<PropsType>(...) --
                // Editor: V.Func<P>(__fam_X, new P { ... }) -- threads the
                // per-component Family handle so the reconciler can identify
                // fibers by stable Family reference (delegate identity
                // breaks across HMR DLL generations).
                // Player: V.Func<P>(global::Foo.X.Render, new P { ... }) --
                // direct delegate, zero Family / RefreshRuntime trace,
                // identical shape to 0.5.x.
                _familyChildRefs.Add(typeName);
                string propsTypeName = res.FuncPropsTypeName;
                _sb.Append($"V.Func<{propsTypeName}>(\n#if UNITY_EDITOR\n                    __fam_{SanitizeFqnToIdent(typeName)},\n#else\n                    {typeName}.Render,\n#endif\n                    new {propsTypeName} {{");

                bool first = true;
                foreach (var attr in attrs)
                {
                    if (IsKey(attr.Name) || IsRefAttr(attr.Name))
                        continue;
                    if (unknownAttrs != null && unknownAttrs.Contains(attr.Name))
                        continue; // skip unknown - UITKX0109 already emitted; would otherwise produce CS0117
                    if (!first)
                        _sb.Append(", ");
                    first = false;
                    _sb.Append($" {ToPropName(attr.Name)} = {AttrVal(attr)}");
                }

                // Route ref={x} to the component's MutableRef<T> parameter.
                if (refAttr != null)
                {
                    var lookupResult = _resolver.TryGetRefParamPropName(
                        typeName,
                        propsTypeName,
                        searchNamespaces,
                        out string? refPropName
                    );
                    switch (lookupResult)
                    {
                        case PropsResolver.RefParamLookupResult.Found:
                            if (!first)
                                _sb.Append(",");
                            _sb.Append($" {refPropName} = {AttrVal(refAttr)}");
                            break;

                        case PropsResolver.RefParamLookupResult.None:
                        {
                            var loc = MakeLoc(_filePath, refAttr.SourceLine);
                            _diagnostics.Add(
                                Diagnostic.Create(
                                    UitkxDiagnostics.RefOnComponentWithNoRefParam,
                                    loc,
                                    typeName
                                )
                            );
                            break;
                        }

                        case PropsResolver.RefParamLookupResult.Ambiguous:
                        {
                            var loc = MakeLoc(_filePath, refAttr.SourceLine);
                            _diagnostics.Add(
                                Diagnostic.Create(
                                    UitkxDiagnostics.RefOnComponentWithAmbiguousRefParam,
                                    loc,
                                    typeName
                                )
                            );
                            break;
                        }
                    }
                }

                _sb.Append(" }");
            }
            else
            {
                // -- No-props path: dual-shape V.Func(...) --
                // Editor: V.Func(__fam_X, null, ...); Player: V.Func(global::Foo.X.Render, null, ...).
                // ref={x} on a no-props component has no route - emit UITKX0020.
                if (refAttr != null)
                {
                    var loc = MakeLoc(_filePath, refAttr.SourceLine);
                    _diagnostics.Add(
                        Diagnostic.Create(
                            UitkxDiagnostics.RefOnComponentWithNoRefParam,
                            loc,
                            typeName
                        )
                    );
                }
                // Emit explicit positional null for the IProps `props` slot so that
                // the subsequent `key:` named argument lands at its natural slot 3.
                // Required for C# 7.2+ non-trailing-named-argument rules when
                // children follow positionally (Phase-A fast-path).  Without this,
                // `V.Func(R, key: K, c1, c2)` triggers CS8323.
                _familyChildRefs.Add(typeName);
                _sb.Append($"V.Func(\n#if UNITY_EDITOR\n                    __fam_{SanitizeFqnToIdent(typeName)},\n#else\n                    {typeName}.Render,\n#endif\n                    null");
            }

            _sb.Append($", key: {keyExpr}");

            if (!children.IsEmpty)
            {
                if (TryClassifySimpleChildren(children))
                {
                    // Phase A bypass: V.Func<P>(R, props, key: k, child1, child2, ...)
                    _sb.Append(", ");
                    EmitChildArgs(children);
                }
                else
                {
                    _sb.Append(", children: __C(");
                    EmitChildArgs(children);
                    _sb.Append(")");
                }
            }

            _sb.Append(")");
        }

        // -- Auto-generated props class ----------------------------------------

        /// <summary>
        /// Emits a public sealed class <c>{ComponentName}Props : IProps</c> that
        /// holds one auto-property per entry in <see cref="DirectiveSet.FunctionParams"/>.
        ///
        /// Generated structure (inside the same namespace, after the partial class closing brace):
        /// <code>
        ///   public sealed class FooProps : global::Ruitk.Core.IProps
        ///   {
        ///       public int X { get; set; } = 0;
        ///       public string Label { get; set; } = "hi";
        ///
        ///       public override bool Equals(object obj) { ... }
        ///       public override int GetHashCode() { ... }
        ///   }
        /// </code>
        /// </summary>
        private void EmitFunctionPropsClass()
        {
            string className = _directives.ComponentName + "Props";
            var fps = _directives.FunctionParams;

            L("");
            L(
                $"    /// <summary>Auto-generated typed props for <see cref=\"{_directives.ComponentName}\"/>.</summary>"
            );
            L($"    public sealed class {className} : global::Ruitk.Core.IProps");
            L("    {");

            // Properties
            foreach (var fp in fps)
            {
                string propName = ToPropName(fp.Name);
                string def = string.IsNullOrWhiteSpace(fp.DefaultValue)
                    ? "default"
                    : fp.DefaultValue!;
                L($"        public {fp.Type} {propName} {{ get; set; }} = {def};");
            }

            L("");

            // Equals
            L("        public override bool Equals(object? obj)");
            L("        {");
            L($"            if (obj is not {className} other) return false;");
            bool firstEq = true;
            foreach (var fp in fps)
            {
                string propName = ToPropName(fp.Name);
                string conjunction = firstEq ? "            return " : "                && ";
                firstEq = false;
                L(
                    $"{conjunction}global::System.Collections.Generic.EqualityComparer<{fp.Type}>.Default.Equals({propName}, other.{propName})"
                );
            }
            if (firstEq) // no params - always equal
                L("            return true;");
            else
                L("                ;"); // close the boolean chain with a dangling semicolon
            L("        }");

            L("");

            // GetHashCode
            L("        public override int GetHashCode()");
            L("        {");
            L("            unchecked");
            L("            {");
            L("                int hash = 17;");
            foreach (var fp in fps)
            {
                string propName = ToPropName(fp.Name);
                L(
                    $"                hash = hash * 31 + global::System.Collections.Generic.EqualityComparer<{fp.Type}>.Default.GetHashCode({propName}!);"
                );
            }
            L("                return hash;");
            L("            }");
            L("        }");

            L("    }");
        }

        // -- Suspense built-in -------------------------------------------------
        // Emits V.Suspense(isReady, [pendingTask,] fallback, key:..., children...)
        // from well-known attribute names: isReady / is-ready, pendingTask /
        // pending-task, and fallback.
        private void EmitSuspense(
            ImmutableArray<AttributeNode> attrs,
            string keyExpr,
            ImmutableArray<AstNode> children
        )
        {
            string? isReady = GetAttrValue(attrs, "isReady") ?? GetAttrValue(attrs, "is-ready");
            string? pendingTask =
                GetAttrValue(attrs, "pendingTask") ?? GetAttrValue(attrs, "pending-task");
            string? fallbackAttr = GetAttrValue(attrs, "fallback");

            string isReadyArg = isReady ?? "() => false";
            string fallbackArg = fallbackAttr ?? $"({QVNode})null";

            _sb.Append("V.Suspense(");
            _sb.Append(isReadyArg);
            _sb.Append(", ");
            if (pendingTask != null)
            {
                _sb.Append(pendingTask);
                _sb.Append(", ");
            }
            _sb.Append(fallbackArg);
            _sb.Append($", key: {keyExpr}");

            if (!children.IsEmpty)
            {
                _sb.Append(", ");
                EmitChildArgs(children);
            }

            _sb.Append(")");
        }

        private void EmitPortal(
            ImmutableArray<AttributeNode> attrs,
            string keyExpr,
            ImmutableArray<AstNode> children
        )
        {
            string targetArg = GetAttrValue(attrs, "target") ?? "null";

            _sb.Append($"{_factory}.Portal({targetArg}, key: {keyExpr}");

            if (!children.IsEmpty)
            {
                _sb.Append(", ");
                EmitChildArgs(children);
            }

            _sb.Append(")");
        }

        private void EmitFragment(string keyExpr, ImmutableArray<AstNode> children)
        {
            if (children.IsEmpty)
            {
                _sb.Append($"V.Fragment(key: {keyExpr})");
            }
            else if (TryClassifySimpleChildren(children))
            {
                _sb.Append($"V.Fragment(key: {keyExpr}, ");
                EmitChildArgs(children);
                _sb.Append(")");
            }
            else
            {
                _sb.Append($"V.Fragment(key: {keyExpr}, __C(");
                EmitChildArgs(children);
                _sb.Append("))");
            }
        }

        // -- Control flow ------------------------------------------------------

        private void EmitIfNode(IfNode ifn)
        {
            if (ifn.Branches.IsEmpty)
            {
                _sb.Append($"({QVNode})null");
                return;
            }

            // Always use IIFE - body code contains return statements at arbitrary depth
            _sb.Append($"((System.Func<{QVNode}>)(() => {{ ");
            for (int i = 0; i < ifn.Branches.Length; i++)
            {
                var branch = ifn.Branches[i];
                if (branch.Condition == null)
                {
                    _sb.Append("else { ");
                }
                else if (i == 0)
                {
                    _sb.Append($"if ({branch.Condition}) {{ ");
                }
                else
                {
                    _sb.Append($"else if ({branch.Condition}) {{ ");
                }

                if (branch.Payload.BodyCode != null)
                {
                    var savedRent = _rentBuffer;
                    _rentBuffer = new StringBuilder();
                    var code = TransformBodyCode(
                        branch.Payload.BodyCode,
                        branch.Payload.BodyCodeOffset,
                        branch.Payload.BodyMarkupRanges,
                        branch.Payload.BodyBareJsxRanges
                    );
                    string rentStmts = _rentBuffer.ToString();
                    _rentBuffer = savedRent;
                    _sb.Append(code);
                    _sb.Append(" ");
                    if (rentStmts.Length > 0)
                        _sb.Append(rentStmts);
                }
                _sb.Append("} ");
            }
            _sb.Append($"return ({QVNode})null; }}))()");
        }

        private void EmitForNode(ForNode fn)
        {
            _sb.Append($"((System.Func<{QVNode}[]>)(() => {{ ");
            _sb.Append($"var __r = new System.Collections.Generic.List<{QVNode}>(); ");
            _sb.Append($"for ({fn.ForExpression}) {{ ");
            if (fn.Payload.BodyCode != null)
            {
                var savedRent = _rentBuffer;
                _rentBuffer = new StringBuilder();
                var code = TransformBodyCode(
                    fn.Payload.BodyCode,
                    fn.Payload.BodyCodeOffset,
                    fn.Payload.BodyMarkupRanges,
                    fn.Payload.BodyBareJsxRanges
                );
                string rentStmts = _rentBuffer.ToString();
                _rentBuffer = savedRent;
                string inlined = RewriteReturnsForInline(code, "__r");
                _sb.Append($"{rentStmts}{inlined} ");
            }
            _sb.Append("} return __r.ToArray(); }))()");
        }

        private void EmitWhileNode(WhileNode wn)
        {
            _sb.Append($"((System.Func<{QVNode}[]>)(() => {{ ");
            _sb.Append($"var __r = new System.Collections.Generic.List<{QVNode}>(); ");
            _sb.Append($"while ({wn.Condition}) {{ ");
            if (wn.Payload.BodyCode != null)
            {
                var savedRent = _rentBuffer;
                _rentBuffer = new StringBuilder();
                var code = TransformBodyCode(
                    wn.Payload.BodyCode,
                    wn.Payload.BodyCodeOffset,
                    wn.Payload.BodyMarkupRanges,
                    wn.Payload.BodyBareJsxRanges
                );
                string rentStmts = _rentBuffer.ToString();
                _rentBuffer = savedRent;
                string inlined = RewriteReturnsForInline(code, "__r");
                _sb.Append($"{rentStmts}{inlined} ");
            }
            _sb.Append("} return __r.ToArray(); }))()");
        }

        private void EmitForeachNode(ForeachNode forn)
        {
            _sb.Append($"((System.Func<{QVNode}[]>)(() => {{ ");
            _sb.Append($"var __r = new System.Collections.Generic.List<{QVNode}>(); ");
            _sb.Append($"foreach ({forn.IteratorDeclaration} in {forn.CollectionExpression}) {{ ");
            if (forn.Payload.BodyCode != null)
            {
                var savedRent = _rentBuffer;
                _rentBuffer = new StringBuilder();
                var code = TransformBodyCode(
                    forn.Payload.BodyCode,
                    forn.Payload.BodyCodeOffset,
                    forn.Payload.BodyMarkupRanges,
                    forn.Payload.BodyBareJsxRanges
                );
                string rentStmts = _rentBuffer.ToString();
                _rentBuffer = savedRent;
                string inlined = RewriteReturnsForInline(code, "__r");
                _sb.Append($"{rentStmts}{inlined} ");
            }
            _sb.Append("} return __r.ToArray(); }))()");
        }

        private void EmitSwitchNode(SwitchNode sw)
        {
            // Always use IIFE with switch statement - body code has returns at arbitrary depth
            _sb.Append($"((System.Func<{QVNode}>)(() => {{ ");
            _sb.Append($"switch ({sw.SwitchExpression}) {{ ");
            foreach (var c in sw.Cases)
            {
                _sb.Append(c.ValueExpression == null ? "default: " : $"case {c.ValueExpression}: ");
                if (c.Payload.BodyCode != null)
                {
                    var savedRent = _rentBuffer;
                    _rentBuffer = new StringBuilder();
                    var code = TransformBodyCode(
                        c.Payload.BodyCode,
                        c.Payload.BodyCodeOffset,
                        c.Payload.BodyMarkupRanges,
                        c.Payload.BodyBareJsxRanges
                    );
                    string rentStmts = _rentBuffer.ToString();
                    _rentBuffer = savedRent;
                    _sb.Append("{ ");
                    _sb.Append(code);
                    if (rentStmts.Length > 0)
                        _sb.Append(" ").Append(rentStmts);
                    _sb.Append(" } ");
                }
            }
            _sb.Append($"}} return ({QVNode})null; }}))()");
        }

        // -- Leaf nodes --------------------------------------------------------

        private void EmitTextNode(TextNode txt)
        {
            string s = txt.Content.Trim();
            if (string.IsNullOrEmpty(s))
            {
                _sb.Append($"({QVNode})null /* whitespace text */");
                return;
            }
            _sb.Append($"V.Text(\"{EscStr(s)}\")");
        }

        private void EmitExpressionNode(ExpressionNode ex)
        {
            // {expr} - passed as-is into __C which handles both
            // VirtualNode and IEnumerable<VirtualNode> (e.g. {__children} for
            // slot pass-through). No cast here - __C's params object[] dispatch
            // handles the runtime type.
            //
            // Phase 1: any JSX literals embedded inside the expression
            // (e.g. {cond ? <A/> : <B/>}) are spliced to V.Tag(...) calls.
            // For the common case (no JSX), the scanner returns empty and the
            // helper returns the original string unchanged - a single O(n) pass.
            string spliced = SpliceExpressionMarkup(ex.Expression, ex.SourceLine);
            _sb.Append($"({spliced})");
        }

        // -- Body expression helpers -------------------------------------------

        /// <summary>
        /// Emits the argument list for a __C(...) call: one child per line,
        /// each preceded by a #line directive.
        /// </summary>
        private void EmitChildArgs(ImmutableArray<AstNode> children)
        {
            // UITKX0010 - warn on duplicate static string keys among siblings
            CheckDuplicateKeys(children);

            bool first = true;
            for (int i = 0; i < children.Length; i++)
            {
                // Comments emit nothing - skip to avoid dangling commas
                if (children[i] is CommentNode)
                    continue;

                if (!first)
                    _sb.Append(",");
                first = false;

                _sb.AppendLine();
                if (children[i].SourceLine > 0)
                {
                    _sb.AppendLine($"#line {children[i].SourceLine} \"{_linePath}\"");
                }
                _sb.Append(I4);
                EmitNode(children[i]);
            }
            if (!first)
            {
                _sb.AppendLine();
                _sb.Append(I4);
            }
        }

        // -- Attribute helpers -------------------------------------------------

        private static bool HasKey(ImmutableArray<AttributeNode> attrs)
        {
            foreach (var a in attrs)
                if (IsKey(a.Name))
                    return true;
            return false;
        }

        private void CheckDuplicateKeys(ImmutableArray<AstNode> children)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in children)
            {
                if (child is not ElementNode el)
                    continue;
                foreach (var attr in el.Attributes)
                {
                    if (!IsKey(attr.Name))
                        continue;
                    if (attr.Value is StringLiteralValue slv)
                    {
                        if (!seen.Add(slv.Value))
                        {
                            var loc = MakeLoc(_filePath, el.SourceLine);
                            _diagnostics.Add(
                                Diagnostic.Create(
                                    UitkxDiagnostics.DuplicateSiblingKey,
                                    loc,
                                    slv.Value,
                                    _displayName
                                )
                            );
                        }
                    }
                }
            }
        }

        // -- Did-you-mean helpers -------------------------------------

        private static string? FindClosestMatch(string name, HashSet<string> candidates)
        {
            string? best = null;
            int bestDist = 3; // only suggest if within edit-distance 2
            foreach (var c in candidates)
            {
                int dist = LevenshteinDistance(name, c);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = c;
                }
            }
            return best;
        }

        // -- User-component diagnostic helpers ------------------------

        /// <summary>
        /// Strips <c>global::</c> prefix and any namespace qualifier from a
        /// resolved component type name, returning the simple PascalCase name
        /// suitable for diagnostic messages (matches what the user typed in
        /// the JSX tag).
        /// </summary>
        private static string SimpleNameForDiagnostic(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return typeName;
            string s = typeName.StartsWith("global::", StringComparison.Ordinal)
                ? typeName.Substring("global::".Length)
                : typeName;
            int lastDot = s.LastIndexOf('.');
            return lastDot < 0 ? s : s.Substring(lastDot + 1);
        }

        /// <summary>
        /// Builds the actionable hint suffix for a UITKX0109 message on a user
        /// component. Format: <c>". Did you mean 'Foo'?"</c> when a close match
        /// exists, otherwise <c>". Available: a, b, c. Add a parameter to
        /// '&lt;Comp&gt;' or remove the attribute."</c>.
        /// </summary>
        private static string BuildUnknownAttrHintForUserComponent(
            string mappedAttrName,
            string componentDisplayName,
            HashSet<string> knownProps
        )
        {
            string? suggestion = FindClosestMatch(mappedAttrName, knownProps);
            if (suggestion != null)
                return $". Did you mean '{suggestion}'?";

            // No close match - list the available props verbatim with an
            // actionable instruction. Cap at the first 8 names to keep the
            // message readable.
            if (knownProps.Count == 0)
                return $". Component '{componentDisplayName}' declares no parameters; add one or remove the attribute.";
            var ordered = knownProps.OrderBy(s => s, StringComparer.Ordinal).ToList();
            string available =
                ordered.Count <= 8
                    ? string.Join(", ", ordered)
                    : string.Join(", ", ordered.Take(8)) + ", -";
            return $". Available on '{componentDisplayName}': {available}. Add a parameter to the component or remove the attribute.";
        }

        private static int LevenshteinDistance(string a, string b)
        {
            if (a.Length == 0)
                return b.Length;
            if (b.Length == 0)
                return a.Length;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++)
                d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++)
                d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1]
                        + (
                            char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1])
                                ? 0
                                : 1
                        )
                );
            return d[a.Length, b.Length];
        }

        private string ExtractKey(ImmutableArray<AttributeNode> attrs)
        {
            foreach (var a in attrs)
                if (IsKey(a.Name))
                    return AttrVal(a.Value);
            return "null";
        }

        private string? GetAttrValue(ImmutableArray<AttributeNode> attrs, string name)
        {
            foreach (var a in attrs)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                    return AttrVal(a.Value);
            return null;
        }

        private string AttrVal(AttributeValue v) =>
            v switch
            {
                StringLiteralValue slv => $"\"{EscStr(slv.Value)}\"",
                // Apply setter-lambda sugar: setFoo(v => v+1) -> setFoo.Set(v => v+1)
                CSharpExpressionValue cev => TransformExpression(cev.Expression),
                JsxExpressionValue jsx => EmitJsxToString(jsx.Element),
                BooleanShorthandValue => "true",
                _ => "null",
            };

        /// <summary>
        /// Attribute-aware overload that splices JSX literals embedded inside
        /// a <see cref="CSharpExpressionValue"/> using the attribute's source
        /// line for accurate <c>#line</c> directives. Other branches fall
        /// through to the value-only path.
        /// <para>Used by the element-emit pathways. Helpers that look up
        /// values by name (<see cref="ExtractKey"/>, <see cref="GetAttrValue"/>)
        /// keep using the value-only overload - they pre-date Phase 1 and
        /// don't need source-line context.</para>
        /// </summary>
        private string AttrVal(AttributeNode attr)
        {
            if (attr.Value is CSharpExpressionValue cev)
            {
                // Phase 1: splice JSX literals embedded inside the expression
                // (e.g. attr={cond ? <A/> : <B/>} or attr={x => <Item/>}).
                // For the common case (no JSX) the helper returns the input
                // unchanged after a single O(n) scan - no allocation.
                string spliced = SpliceExpressionMarkup(cev.Expression, attr.SourceLine);
                return TransformExpression(spliced);
            }
            return AttrVal(attr.Value);
        }

        /// <summary>
        /// Emits a JSX <see cref="ElementNode"/> to a string by temporarily capturing
        /// <see cref="EmitElementNode"/> output from the shared <see cref="_sb"/>.
        /// </summary>
        private string EmitJsxToString(ElementNode? element)
        {
            if (element == null)
                return $"({QVNode})null";
            int startLen = _sb.Length;
            EmitElementNode(element);
            string result = _sb.ToString(startLen, _sb.Length - startLen);
            _sb.Length = startLen;
            return result;
        }

        // -- Setup-code JSX splice ---------------------------------------------

        /// <summary>
        /// Replaces embedded JSX markup spans inside <paramref name="setupCode"/>
        /// with their emitted C# VirtualNode call equivalents.
        /// Returns the setup code unchanged when there are no markup ranges.
        /// </summary>
        private string SpliceSetupCodeMarkup(string setupCode)
        {
            var markupRanges = _directives.SetupCodeMarkupRanges;
            var bareRanges = _directives.SetupCodeBareJsxRanges;

            bool hasMarkup = !markupRanges.IsDefaultOrEmpty;
            bool hasBare = !bareRanges.IsDefaultOrEmpty;
            if (!hasMarkup && !hasBare)
                return setupCode;

            // Merge both range lists and sort by absolute start position.
            var allRanges = new List<(int Start, int End, int Line)>();
            if (hasMarkup)
                foreach (var r in markupRanges)
                    allRanges.Add(r);
            if (hasBare)
                foreach (var r in bareRanges)
                    allRanges.Add(r);
            allRanges.Sort((a, b) => a.Start.CompareTo(b.Start));

            int fseOffset = _directives.FunctionSetupStartOffset;
            int gapOffset = _directives.FunctionSetupGapOffset;
            int gapLength = _directives.FunctionSetupGapLength;

            var spliced = new StringBuilder(setupCode.Length);
            int prev = 0;

            foreach (var (absStart, absEnd, line) in allRanges)
            {
                // Convert absolute source positions to setupCode-relative offsets.
                int relStart = AbsToSetupOffset(absStart, fseOffset, gapOffset, gapLength);
                int relEnd = AbsToSetupOffset(absEnd, fseOffset, gapOffset, gapLength);

                // Guard against out-of-range offsets.
                if (relStart < 0)
                    relStart = 0;
                if (relEnd < 0)
                    relEnd = 0;
                if (relStart > setupCode.Length)
                    relStart = setupCode.Length;
                if (relEnd > setupCode.Length)
                    relEnd = setupCode.Length;
                if (relEnd <= relStart)
                    continue;

                // Skip inner paren-JSX ranges that fall inside an outer bare-JSX range
                // already processed as a unit by the mini-parser above.
                if (relStart < prev)
                    continue;

                // U-06: `cond && <Tag/>` desugar (see TryEmitLogicalAndDesugar).
                const string setupForeachError =
                    "#error UITKX: @foreach/@for/@while produces a list and cannot appear directly in an expression position. Wrap it in a container element.";
                if (
                    TryEmitLogicalAndDesugar(
                        setupCode, prev, relStart, relEnd, line, 1, spliced,
                        (sb, jsxTxt, absLn) => AppendIsolatedJsxEmission(sb, jsxTxt, absLn, setupForeachError))
                )
                {
                    prev = relEnd;
                    continue;
                }

                // Append C# text before this JSX span.
                if (relStart > prev)
                    spliced.Append(setupCode, prev, relStart - prev);

                string jsxText = setupCode.Substring(relStart, relEnd - relStart);
                AppendIsolatedJsxEmission(spliced, jsxText, line, setupForeachError);

                prev = relEnd;
            }

            if (prev < setupCode.Length)
                spliced.Append(setupCode, prev, setupCode.Length - prev);

            return spliced.ToString();
        }

        /// <summary>
        /// U-06: shared <c>cond &amp;&amp; &lt;Tag/&gt;</c> -> <c>((cond) -> V.Tag(...) :
        /// (VirtualNode?)null)</c> desugar, used by <see cref="SpliceSetupCodeMarkup"/>,
        /// <see cref="SpliceBodyCodeMarkup"/>, and <see cref="SpliceExpressionMarkup"/> so
        /// all three splice sites treat the React <c>&amp;&amp;</c>-short-circuit idiom
        /// identically. Previously only <c>SpliceExpressionMarkup</c> desugared it - the
        /// other two spliced raw JSX into e.g. <c>isOn &amp;&amp; V.Badge(...)</c>, a hard
        /// CS0019 (<c>bool &amp;&amp; VirtualNode</c> has no operator overload).
        /// </summary>
        /// <param name="text">The full text being spliced (setupCode/bodyCode/expr).</param>
        /// <param name="prev">Start of the unspliced region before this JSX range.</param>
        /// <param name="jsxStart">Start offset of the JSX range within <paramref name="text"/>.</param>
        /// <param name="jsxEnd">End offset (exclusive) of the JSX range.</param>
        /// <param name="sourceLine">Absolute line to anchor <c>#line</c> directives to when
        /// <paramref name="lineInRange"/> is 1 (the common case for setup/body ranges, whose
        /// tuples already carry an absolute line); pass the expression's own source line and
        /// the range's expr-relative line for <see cref="SpliceExpressionMarkup"/>.</param>
        /// <param name="lineInRange">1-based line of the range's start within <paramref name="text"/>.</param>
        /// <param name="spliced">Output buffer - this method appends directly to it.</param>
        /// <param name="emitJsxFragment">Emits <c>jsxText</c> (parsed + rendered) into
        /// <paramref name="spliced"/>, using whichever rent-handling contract the caller's
        /// splice site requires (isolate-and-reinsert vs. bubble-to-shared-buffer).</param>
        /// <returns>
        /// <c>true</c> if this was a <c>&amp;&amp;</c> trigger and the caller must NOT also
        /// splice this range itself (this method fully handled it, including advancing past
        /// it); <c>false</c> if there's no <c>&amp;&amp;</c> trigger and the caller should
        /// proceed with its normal (non-desugar) splice path.
        /// </returns>
        private bool TryEmitLogicalAndDesugar(
            string text,
            int prev,
            int jsxStart,
            int jsxEnd,
            int sourceLine,
            int lineInRange,
            StringBuilder spliced,
            Action<StringBuilder, string, int> emitJsxFragment
        )
        {
            int ampStart = TryFindTrailingLogicalAnd(text, prev, jsxStart);
            if (ampStart < 0)
                return false;

            int lhsStart = DirectiveParser.FindLhsStartForLogicalAnd(text, prev, ampStart);
            if (lhsStart < 0)
            {
                // Walker failed - emit prefix verbatim then a UITKX0026 directive that
                // surfaces as a CS1029 at the right line. Drop the JSX entirely so the
                // user sees ONE diagnostic, not the cascade from raw JSX in code.
                if (jsxStart > prev)
                    spliced.Append(text, prev, jsxStart - prev);
                spliced.Append(
                    "\n#error UITKX0026: Could not desugar `&&` JSX expression. "
                        + "Use `cond ? <Tag/> : null` instead.\n"
                );
                return true;
            }

            // Emit text before the LHS verbatim (operators, prior sub-expressions,
            // leading whitespace), then open the ternary.
            if (lhsStart > prev)
                spliced.Append(text, prev, lhsStart - prev);

            int lhsEnd = ampStart;
            while (
                lhsEnd > lhsStart
                && (text[lhsEnd - 1] == ' ' || text[lhsEnd - 1] == '\t'
                    || text[lhsEnd - 1] == '\r' || text[lhsEnd - 1] == '\n')
            )
                lhsEnd--;

            spliced.Append("((");
            spliced.Append(text, lhsStart, lhsEnd - lhsStart);
            spliced.Append(") ? ");

            string jsxText = text.Substring(jsxStart, jsxEnd - jsxStart);
            int absLine = sourceLine + (lineInRange - 1);
            emitJsxFragment(spliced, jsxText, absLine);

            spliced.Append(" : (global::Ruitk.Core.VirtualNode?)null)");
            return true;
        }

        /// <summary>
        /// Parses and emits <paramref name="jsxText"/>, isolating pool-rent statements into
        /// a scratch buffer and re-inserting them at the last top-level statement boundary
        /// (rent declarations must be standalone statements - they cannot follow the ternary
        /// this is normally spliced into, or a bare expression position). Shared by
        /// <see cref="SpliceSetupCodeMarkup"/> and <see cref="SpliceBodyCodeMarkup"/>.
        /// </summary>
        private void AppendIsolatedJsxEmission(
            StringBuilder spliced, string jsxText, int line, string multiRootErrorMessage)
        {
            var miniDirectives = new DirectiveSet(
                Namespace: null,
                ComponentName: null,
                PropsTypeName: null,
                DefaultKey: null,
                Usings: _directives.Usings,
                UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string, string)>.Empty,
                MarkupStartLine: line,
                MarkupStartIndex: 0,
                MarkupEndIndex: jsxText.Length
            );
            var miniDiags = new List<ParseDiagnostic>();
            var nodes = UitkxParser.Parse(
                jsxText, _filePath, miniDirectives, miniDiags, lineOffset: line - 1);

            if (nodes.Length == 0)
            {
                spliced.Append(jsxText);
                return;
            }

            var savedRent = _rentBuffer;
            _rentBuffer = new StringBuilder();

            int savedLen = _sb.Length;
            if (nodes.Length == 1)
            {
                var node = nodes[0];
                if (node is ForeachNode or ForNode or WhileNode)
                    _sb.Append(multiRootErrorMessage);
                else
                    EmitNode(node);
            }
            else
            {
                _sb.Append(
                    "#error UITKX0025: Inline JSX expression must have a single root element."
                );
            }
            string emittedCs = _sb.ToString(savedLen, _sb.Length - savedLen);
            _sb.Length = savedLen;

            string inlineRent = _rentBuffer.ToString();
            _rentBuffer = savedRent;
            if (inlineRent.Length > 0)
            {
                int insertPos = FindLastTopLevelStatementBoundary(spliced);
                spliced.Insert(insertPos, inlineRent);
            }
            spliced.Append(emittedCs.Trim());
        }

        private static int AbsToSetupOffset(int absPos, int fseOffset, int gapOffset, int gapLength)
        {
            int relOffset = absPos - fseOffset;
            if (gapOffset >= 0 && relOffset >= gapOffset)
                relOffset -= gapLength;
            return relOffset;
        }

        /// <summary>
        /// Finds the position immediately after the last top-level statement
        /// boundary (<c>;</c> or <c>}</c>) in <paramref name="text"/>, ignoring
        /// occurrences inside string literals, char literals, line comments,
        /// or block comments. Returns 0 if no boundary is found.
        /// Used to determine where to insert pool-rent declarations such that
        /// they appear as standalone statements outside of any comment/string.
        /// </summary>
        private static int FindLastTopLevelStatementBoundary(StringBuilder text)
        {
            // NOTE: delegates string/char/comment skipping to CSharpLexFacts (see
            // FINAL_AUDIT_UITKX_FINDINGS.md U-20) - the previous hand-rolled scanner here
            // mishandled the @$" interpolated-verbatim ordering (only $@" was recognized),
            // the mirror image of the bug ExpressionExtractor had for $@".
            string code = text.ToString();
            int len = code.Length;
            int lastBoundary = 0;
            int i = 0;
            while (i < len)
            {
                if (CSharpLexFacts.TrySkipNonCode(code, ref i, len))
                    continue;

                char c = code[i];
                if (c == ';' || c == '}')
                    lastBoundary = i + 1;
                i++;
            }
            return lastBoundary;
        }

        /// <summary>
        /// Replaces embedded JSX markup spans inside directive <paramref name="bodyCode"/>
        /// with their emitted C# VirtualNode call equivalents.
        /// Unlike <see cref="SpliceSetupCodeMarkup"/> there is no gap (the body is contiguous).
        /// </summary>
        private string SpliceBodyCodeMarkup(
            string bodyCode,
            int bodyCodeOffset,
            ImmutableArray<(int Start, int End, int Line)> markupRanges,
            ImmutableArray<(int Start, int End, int Line)> bareRanges
        )
        {
            bool hasMarkup = !markupRanges.IsDefaultOrEmpty;
            bool hasBare = !bareRanges.IsDefaultOrEmpty;
            if (!hasMarkup && !hasBare)
                return bodyCode;

            var allRanges = new List<(int Start, int End, int Line)>();
            if (hasMarkup)
                foreach (var r in markupRanges)
                    allRanges.Add(r);
            if (hasBare)
                foreach (var r in bareRanges)
                    allRanges.Add(r);
            allRanges.Sort((a, b) => a.Start.CompareTo(b.Start));

            var spliced = new StringBuilder(bodyCode.Length);
            int prev = 0;

            foreach (var (absStart, absEnd, line) in allRanges)
            {
                int relStart = absStart - bodyCodeOffset;
                int relEnd = absEnd - bodyCodeOffset;

                if (relStart < 0)
                    relStart = 0;
                if (relEnd < 0)
                    relEnd = 0;
                if (relStart > bodyCode.Length)
                    relStart = bodyCode.Length;
                if (relEnd > bodyCode.Length)
                    relEnd = bodyCode.Length;
                if (relEnd <= relStart)
                    continue;

                // Skip inner paren-JSX ranges that fall inside an outer bare-JSX range
                // already processed as a unit by the mini-parser above.
                if (relStart < prev)
                    continue;

                // U-06: `cond && <Tag/>` desugar (see TryEmitLogicalAndDesugar).
                const string bodyForeachError =
                    "#error UITKX: @foreach/@for/@while produces a list and cannot appear directly where a single element is expected. Wrap it in a container element.";
                if (
                    TryEmitLogicalAndDesugar(
                        bodyCode, prev, relStart, relEnd, line, 1, spliced,
                        (sb, jsxTxt, absLn) => AppendIsolatedJsxEmission(sb, jsxTxt, absLn, bodyForeachError))
                )
                {
                    prev = relEnd;
                    continue;
                }

                if (relStart > prev)
                    spliced.Append(bodyCode, prev, relStart - prev);

                string jsxText = bodyCode.Substring(relStart, relEnd - relStart);
                AppendIsolatedJsxEmission(spliced, jsxText, line, bodyForeachError);

                prev = relEnd;
            }

            if (prev < bodyCode.Length)
                spliced.Append(bodyCode, prev, bodyCode.Length - prev);

            return spliced.ToString();
        }

        // -- Inline-expression JSX splice --------------------------------------

        /// <summary>
        /// Splices JSX literals embedded inside an arbitrary C# expression
        /// (used for <c>{ expr }</c> child positions and <c>attr={ expr }</c>
        /// attribute values). Each detected JSX literal is replaced by its
        /// emitted <c>V.Tag(...)</c> equivalent; the surrounding C# remains
        /// verbatim. Pool-rent statements produced by inner JSX flow into the
        /// shared <see cref="_rentBuffer"/> so the surrounding emit context
        /// hoists them above the parent expression - matching how
        /// <see cref="EmitJsxToString"/> handles <c>JsxExpressionValue</c>.
        ///
        /// <para>Returns <paramref name="expr"/> unchanged when no JSX is
        /// detected (the common case - scanner cost is O(n) on the expression
        /// text and runs only when the expression actually contains JSX).</para>
        ///
        /// <para>This is the single emit-time entry point that makes JSX
        /// recognized in arbitrary expression positions, mirroring Babel/TSC's
        /// "JSX is allowed wherever an expression is allowed" semantics.</para>
        /// </summary>
        /// <param name="expr">The raw C# expression text (already extracted
        /// from the <c>{</c>...<c>}</c> or <c>@(</c>...<c>)</c> wrapper).</param>
        /// <param name="sourceLine">1-based line number in the .uitkx source
        /// where this expression begins; used to map <c>#line</c> directives
        /// in the spliced JSX back to the user's file.</param>
        private string SpliceExpressionMarkup(string expr, int sourceLine)
        {
            if (string.IsNullOrEmpty(expr))
                return expr;

            // Re-run the same scanners that already handle preamble &
            // directive bodies. Output ranges are relative to the input
            // string (start = 0), so no offset arithmetic is needed.
            var markupRanges = DirectiveParser.FindJsxBlockRanges(expr, 0, expr.Length);
            var bareRanges = DirectiveParser.FindBareJsxRanges(expr, 0, expr.Length);

            bool hasMarkup = !markupRanges.IsDefaultOrEmpty;
            bool hasBare = !bareRanges.IsDefaultOrEmpty;
            if (!hasMarkup && !hasBare)
                return expr;

            var allRanges = new List<(int Start, int End, int Line)>(
                markupRanges.Length + bareRanges.Length
            );
            if (hasMarkup)
                foreach (var r in markupRanges)
                    allRanges.Add(r);
            if (hasBare)
                foreach (var r in bareRanges)
                    allRanges.Add(r);
            allRanges.Sort((a, b) => a.Start.CompareTo(b.Start));

            var spliced = new StringBuilder(expr.Length);
            int prev = 0;

            foreach (var (start, end, line) in allRanges)
            {
                int s = start;
                int e = end;
                if (s < 0)
                    s = 0;
                if (e < 0)
                    e = 0;
                if (s > expr.Length)
                    s = expr.Length;
                if (e > expr.Length)
                    e = expr.Length;
                if (e <= s)
                    continue;

                // Skip inner paren-JSX ranges that fall inside an outer
                // bare-JSX range already processed as a unit by the mini-parser.
                if (s < prev)
                    continue;

                // U-06: `cond && <Tag/>` desugar (see TryEmitLogicalAndDesugar). Scanner
                // returns 1-based lines relative to expr, so sourceLine + (line-1) combines
                // with the expression's own source line to anchor #line directives correctly.
                if (
                    TryEmitLogicalAndDesugar(
                        expr, prev, s, e, sourceLine, line, spliced, AppendBubbledJsxEmission)
                )
                {
                    prev = e;
                    continue;
                }

                // Normal (non-`&&`) splice: append prefix verbatim.
                if (s > prev)
                    spliced.Append(expr, prev, s - prev);

                string jsxText = expr.Substring(s, e - s);
                int absLine = sourceLine + (line - 1);
                AppendBubbledJsxEmission(spliced, jsxText, absLine);

                prev = e;
            }

            if (prev < expr.Length)
                spliced.Append(expr, prev, expr.Length - prev);

            return spliced.ToString();
        }

        /// <summary>
        /// Parses and emits <paramref name="jsxText"/> into <paramref name="spliced"/>; pool-rent
        /// statements flow into the SHARED <see cref="_rentBuffer"/> so the parent emit context
        /// (component setup or directive body) hoists them above the surrounding expression -
        /// same contract as <c>EmitJsxToString</c>. Used by <see cref="SpliceExpressionMarkup"/>.
        /// </summary>
        private void AppendBubbledJsxEmission(StringBuilder spliced, string jsxText, int absLine)
        {
            var miniDirectives = new DirectiveSet(
                Namespace: null,
                ComponentName: null,
                PropsTypeName: null,
                DefaultKey: null,
                Usings: _directives.Usings,
                UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string, string)>.Empty,
                MarkupStartLine: absLine,
                MarkupStartIndex: 0,
                MarkupEndIndex: jsxText.Length
            );
            var miniDiags = new List<ParseDiagnostic>();
            var nodes = UitkxParser.Parse(
                jsxText, _filePath, miniDirectives, miniDiags, lineOffset: absLine - 1);

            if (nodes.Length == 0)
            {
                // Parse failed - keep the original text so the C# compiler surfaces a
                // clear error rather than silently dropping content.
                spliced.Append(jsxText);
                return;
            }

            int savedLen = _sb.Length;
            if (nodes.Length == 1)
            {
                var node = nodes[0];
                if (node is ForeachNode or ForNode or WhileNode)
                    _sb.Append(
                        "#error UITKX: @foreach/@for/@while produces a list and cannot appear directly in an expression position. Wrap it in a container element."
                    );
                else
                    EmitNode(node);
            }
            else
            {
                _sb.Append(
                    "#error UITKX0025: Inline JSX expression must have a single root element."
                );
            }
            string emittedCs = _sb.ToString(savedLen, _sb.Length - savedLen);
            _sb.Length = savedLen;

            spliced.Append(emittedCs.Trim());
        }

        /// <summary>
        /// Returns the index of the trailing <c>&amp;&amp;</c> in
        /// <paramref name="expr"/><c>[prev..jsxStart]</c> if the prefix ends
        /// with that operator (modulo trailing whitespace), or -1 otherwise.
        /// Used by <see cref="SpliceExpressionMarkup"/> to detect the
        /// logical-AND desugar trigger.
        /// </summary>
        private static int TryFindTrailingLogicalAnd(string expr, int prev, int jsxStart)
        {
            int i = jsxStart - 1;
            // Skip trailing whitespace between `&&` and the JSX `<`
            while (
                i >= prev
                && (expr[i] == ' ' || expr[i] == '\t' || expr[i] == '\r' || expr[i] == '\n')
            )
                i--;
            if (i - 1 < prev)
                return -1;
            if (expr[i] != '&' || expr[i - 1] != '&')
                return -1;
            // Reject `&&&` (degenerate) - the scanner already filters this
            // but defence-in-depth keeps the splice contract local.
            if (i - 2 >= prev && expr[i - 2] == '&')
                return -1;
            return i - 1;
        }

        /// <summary>
        /// Processes directive body code through the full transformation pipeline:
        /// splice JSX -> hook aliases -> asset paths.
        /// </summary>
        private string TransformBodyCode(
            string bodyCode,
            int bodyCodeOffset,
            ImmutableArray<(int Start, int End, int Line)> markupRanges,
            ImmutableArray<(int Start, int End, int Line)> bareRanges
        )
        {
            var code = SpliceBodyCodeMarkup(bodyCode, bodyCodeOffset, markupRanges, bareRanges);
            code = ApplyHookAliases(code);
            code = ResolveAssetPaths(code, _filePath, _diagnostics);
            return code;
        }

        /// <summary>
        /// Rewrites top-level <c>return EXPR;</c> statements in transformed body code
        /// so the code can be inlined directly inside a loop body instead of an IIFE lambda.
        /// <list type="bullet">
        ///   <item><c>return null;</c> -&gt; <c>continue;</c></item>
        ///   <item><c>return EXPR;</c> -&gt; <c>listVar.Add(EXPR); continue;</c></item>
        /// </list>
        /// Returns inside nested lambda bodies (<c>=> { ... }</c>) are left untouched.
        /// </summary>
        internal static string RewriteReturnsForInline(string code, string listVar)
        {
            if (string.IsNullOrEmpty(code))
                return code;

            var result = new StringBuilder(code.Length + 64);
            int len = code.Length;
            int lambdaDepth = 0; // depth inside => { } lambda bodies

            // Stack tracking which braces are lambda-body openers.
            // When we see => {, we push true; for other {, we push false.
            // On }, we pop and adjust lambdaDepth accordingly.
            var braceIsLambda = new Stack<bool>();

            int i = 0;
            while (i < len)
            {
                char c = code[i];

                // -- Skip string literals --
                if (c == '"')
                {
                    // Verbatim string @"..."
                    if (i > 0 && code[i - 1] == '@')
                    {
                        result.Append(c);
                        i++;
                        while (i < len)
                        {
                            result.Append(code[i]);
                            if (code[i] == '"')
                            {
                                if (i + 1 < len && code[i + 1] == '"')
                                {
                                    result.Append(code[i + 1]);
                                    i += 2;
                                }
                                else
                                {
                                    i++;
                                    break;
                                }
                            }
                            else
                            {
                                i++;
                            }
                        }
                        continue;
                    }
                    // Regular string "..."
                    result.Append(c);
                    i++;
                    while (i < len)
                    {
                        result.Append(code[i]);
                        if (code[i] == '\\')
                        {
                            i++;
                            if (i < len)
                            {
                                result.Append(code[i]);
                                i++;
                            }
                        }
                        else if (code[i] == '"')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            i++;
                        }
                    }
                    continue;
                }

                // -- Skip char literals --
                if (c == '\'')
                {
                    result.Append(c);
                    i++;
                    while (i < len)
                    {
                        result.Append(code[i]);
                        if (code[i] == '\\')
                        {
                            i++;
                            if (i < len)
                            {
                                result.Append(code[i]);
                                i++;
                            }
                        }
                        else if (code[i] == '\'')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            i++;
                        }
                    }
                    continue;
                }

                // -- Skip line comments --
                if (c == '/' && i + 1 < len && code[i + 1] == '/')
                {
                    while (i < len && code[i] != '\n')
                    {
                        result.Append(code[i]);
                        i++;
                    }
                    continue;
                }

                // -- Skip block comments --
                if (c == '/' && i + 1 < len && code[i + 1] == '*')
                {
                    result.Append(code[i]);
                    result.Append(code[i + 1]);
                    i += 2;
                    while (i < len)
                    {
                        if (code[i] == '*' && i + 1 < len && code[i + 1] == '/')
                        {
                            result.Append(code[i]);
                            result.Append(code[i + 1]);
                            i += 2;
                            break;
                        }
                        result.Append(code[i]);
                        i++;
                    }
                    continue;
                }

                // -- Track => { for lambda body detection --
                if (c == '=' && i + 1 < len && code[i + 1] == '>')
                {
                    result.Append('=');
                    result.Append('>');
                    i += 2;
                    // Skip whitespace after =>
                    while (
                        i < len
                        && (code[i] == ' ' || code[i] == '\t' || code[i] == '\r' || code[i] == '\n')
                    )
                    {
                        result.Append(code[i]);
                        i++;
                    }
                    // If next char is {, this is a lambda body
                    if (i < len && code[i] == '{')
                    {
                        braceIsLambda.Push(true);
                        lambdaDepth++;
                        result.Append('{');
                        i++;
                    }
                    continue;
                }

                // -- Track braces --
                if (c == '{')
                {
                    braceIsLambda.Push(false);
                    result.Append(c);
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    if (braceIsLambda.Count > 0 && braceIsLambda.Pop())
                        lambdaDepth--;
                    result.Append(c);
                    i++;
                    continue;
                }

                // -- Detect 'return' keyword at lambda depth 0 --
                if (
                    lambdaDepth == 0
                    && c == 'r'
                    && i + 5 < len
                    && code[i + 1] == 'e'
                    && code[i + 2] == 't'
                    && code[i + 3] == 'u'
                    && code[i + 4] == 'r'
                    && code[i + 5] == 'n'
                    && (i + 6 >= len || !char.IsLetterOrDigit(code[i + 6]) && code[i + 6] != '_')
                )
                {
                    // Verify this is not part of a larger identifier by checking char before
                    if (i > 0 && (char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_'))
                    {
                        result.Append(c);
                        i++;
                        continue;
                    }

                    int afterReturn = i + 6;

                    // Skip whitespace after 'return'
                    int exprStart = afterReturn;
                    while (
                        exprStart < len
                        && (
                            code[exprStart] == ' '
                            || code[exprStart] == '\t'
                            || code[exprStart] == '\r'
                            || code[exprStart] == '\n'
                        )
                    )
                        exprStart++;

                    // Case 1: return null;
                    if (
                        exprStart + 4 < len
                        && code[exprStart] == 'n'
                        && code[exprStart + 1] == 'u'
                        && code[exprStart + 2] == 'l'
                        && code[exprStart + 3] == 'l'
                    )
                    {
                        int afterNull = exprStart + 4;
                        // Skip whitespace
                        while (
                            afterNull < len
                            && (
                                code[afterNull] == ' '
                                || code[afterNull] == '\t'
                                || code[afterNull] == '\r'
                                || code[afterNull] == '\n'
                            )
                        )
                            afterNull++;
                        if (afterNull < len && code[afterNull] == ';')
                        {
                            result.Append("continue;");
                            i = afterNull + 1;
                            continue;
                        }
                    }

                    // Case 2: return (EXPR); - unwrap outer parens
                    // Case 3: return EXPR;
                    // Find the matching ';' at depth 0 (accounting for nested parens/braces)
                    int semi = FindStatementEnd(code, exprStart);
                    if (semi >= 0)
                    {
                        string expr = code.Substring(exprStart, semi - exprStart).Trim();

                        // Unwrap a single layer of outer parentheses if present:
                        // return (V.Element(...));  ->  expr = "(V.Element(...))"
                        // We want the inner expression without the outer parens.
                        if (expr.Length >= 2 && expr[0] == '(')
                        {
                            int matchClose = FindMatchingClose(expr, 0, '(', ')');
                            if (matchClose == expr.Length - 1)
                                expr = expr.Substring(1, expr.Length - 2).Trim();
                        }

                        result.Append(listVar).Append(".Add(").Append(expr).Append("); continue;");
                        i = semi + 1;
                        continue;
                    }
                }

                result.Append(c);
                i++;
            }

            return result.ToString();
        }

        /// <summary>
        /// Finds the end-of-statement semicolon for a <c>return</c> expression,
        /// respecting nested parentheses, braces, brackets, strings, and chars.
        /// Returns the index of the <c>;</c> or -1 if not found.
        /// </summary>
        private static int FindStatementEnd(string code, int start)
        {
            int depth = 0; // paren + brace + bracket depth
            int i = start;
            int len = code.Length;

            while (i < len)
            {
                char c = code[i];

                if (c == '"')
                {
                    if (i > 0 && code[i - 1] == '@')
                    {
                        i++;
                        while (i < len)
                        {
                            if (code[i] == '"')
                            {
                                if (i + 1 < len && code[i + 1] == '"')
                                {
                                    i += 2;
                                }
                                else
                                {
                                    i++;
                                    break;
                                }
                            }
                            else
                            {
                                i++;
                            }
                        }
                        continue;
                    }
                    i++;
                    while (i < len)
                    {
                        if (code[i] == '\\')
                        {
                            i += 2;
                        }
                        else if (code[i] == '"')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            i++;
                        }
                    }
                    continue;
                }
                if (c == '\'')
                {
                    i++;
                    while (i < len)
                    {
                        if (code[i] == '\\')
                        {
                            i += 2;
                        }
                        else if (code[i] == '\'')
                        {
                            i++;
                            break;
                        }
                        else
                        {
                            i++;
                        }
                    }
                    continue;
                }

                if (c == '(' || c == '{' || c == '[')
                {
                    depth++;
                    i++;
                    continue;
                }
                if (c == ')' || c == '}' || c == ']')
                {
                    depth--;
                    i++;
                    continue;
                }

                if (c == ';' && depth == 0)
                    return i;

                i++;
            }
            return -1;
        }

        /// <summary>
        /// Finds the index of the matching closing character for an opening
        /// delimiter at <paramref name="start"/>.
        /// Returns -1 if no match is found.
        /// </summary>
        private static int FindMatchingClose(string s, int start, char open, char close)
        {
            int depth = 0;
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == open)
                    depth++;
                else if (s[i] == close)
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }
            return -1;
        }

        private static bool IsKey(string name) =>
            string.Equals(name, "key", StringComparison.OrdinalIgnoreCase);

        private static Location MakeLoc(string filePath, int sourceLine)
        {
            int line0 = Math.Max(0, sourceLine - 1);
            var pos = new LinePosition(line0, 0);
            return Location.Create(filePath, default, new LinePositionSpan(pos, pos));
        }

        private static bool IsRefAttr(string name) =>
            string.Equals(name, "ref", StringComparison.OrdinalIgnoreCase);

        private static string ToPropName(string attrName)
        {
            if (string.IsNullOrEmpty(attrName))
                return attrName;
            return char.ToUpperInvariant(attrName[0]) + attrName.Substring(1);
        }

        // -- String / variable helpers -----------------------------------------

        // -- Pool-rent style expression parsing --------------------------

        /// <summary>
        /// Tries to extract the initializer body from a <c>new Style { ... }</c> expression.
        /// Returns true if the expression is a simple <c>new Style { PropertySetter, ... }</c>.
        /// Returns false for non-matching expressions (variables, ternaries, tuple syntax).
        /// </summary>
        private static bool TryExtractNewStyleInit(string expr, out string? body)
        {
            body = null;
            if (expr == null)
                return false;

            string trimmed = expr.Trim();

            // Must start with "new Style" or "new Style{"
            int idx;
            if (trimmed.StartsWith("new Style{", StringComparison.Ordinal))
                idx = "new Style".Length;
            else if (trimmed.StartsWith("new Style {", StringComparison.Ordinal))
                idx = "new Style ".Length;
            else
                return false;

            // Find the opening brace
            int braceOpen = trimmed.IndexOf('{', idx);
            if (braceOpen < 0)
                return false;

            // Find matching closing brace
            int depth = 0;
            int braceClose = -1;
            for (int i = braceOpen; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        braceClose = i;
                        break;
                    }
                }
            }
            if (braceClose < 0)
                return false;

            // Must be the entire expression (nothing significant after closing brace)
            string after = trimmed.Substring(braceClose + 1).Trim();
            if (after.Length > 0)
                return false;

            body = trimmed.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
            if (body.Length == 0)
            {
                body = "";
                return true;
            } // empty Style

            // Reject tuple initializer syntax: "(StyleKeys.X, val)" - starts with '('
            if (body[0] == '(')
                return false;

            return true;
        }

        // -- OPT-V2-2 Phase A: static-style hoisting classifier --------------

        /// <summary>
        /// Whitelisted unqualified type names for `new T(literal-args...)` ctors
        /// that are safe to evaluate once at class load (pure value-type ctors).
        /// </summary>
        private static readonly HashSet<string> s_literalCtorTypes = new HashSet<string>(
            StringComparer.Ordinal
        )
        {
            "Color",
            "Color32",
            "Vector2",
            "Vector3",
            "Vector4",
            "Vector2Int",
            "Vector3Int",
            "Length",
            "TimeValue",
            "Rect",
            "Quaternion",
            // OPT-V2-2 - newly hoistable struct ctors used by 9-slice / text-shadow / advanced-font helpers
            "TextShadow",
            "FontDefinition",
        };

        /// <summary>
        /// Returns <c>true</c> iff <paramref name="val"/> is a `new Style { ... }`
        /// literal whose every initializer value is a compile-time constant.
        /// On success, allocates a class-level static field name, appends the
        /// `private static readonly Style __sty_N = ...;` declaration to
        /// <see cref="_hoistedStyleFields"/>, and returns the field name in
        /// <paramref name="hoistName"/>.
        /// Handles BOTH property-setter syntax (<c>Width = 5f</c>) and tuple
        /// syntax (<c>(StyleKeys.Width, 5f)</c>).
        /// </summary>
        private bool TryHoistStaticStyle(string val, out string? hoistName)
        {
            hoistName = null;
            if (string.IsNullOrEmpty(val))
                return false;

            string trimmed = val.Trim();
            // Must be exactly `new Style { ... }` (no trailing chained members,
            // no surrounding ternary, no other expression).
            int idx;
            if (trimmed.StartsWith("new Style{", StringComparison.Ordinal))
                idx = "new Style".Length;
            else if (trimmed.StartsWith("new Style {", StringComparison.Ordinal))
                idx = "new Style ".Length;
            else
                return false;

            int braceOpen = trimmed.IndexOf('{', idx);
            if (braceOpen < 0)
                return false;

            int depth = 0;
            int braceClose = -1;
            for (int i = braceOpen; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        braceClose = i;
                        break;
                    }
                }
            }
            if (braceClose < 0)
                return false;

            // Nothing significant after the closing brace.
            string after = trimmed.Substring(braceClose + 1).Trim();
            if (after.Length > 0)
                return false;

            string body = trimmed.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();

            // Empty initializer ({}): not worth hoisting (Phase C - EmptyStyle).
            if (body.Length == 0)
                return false;

            // Validate every initializer is hoist-safe.
            if (!IsHoistableInitializerBody(body))
                return false;

            // Allocate the field name and emit the field declaration.
            int hid = _hoistCounter++;
            hoistName = $"__sty_{hid}";
            _hoistedStyleFields.Append(I2);
            _hoistedStyleFields.Append(
                "[global::Ruitk.UitkxHmrSwap] private static global::Ruitk.Props.Typed.Style "
            );
            _hoistedStyleFields.Append(hoistName);
            _hoistedStyleFields.Append(" = new global::Ruitk.Props.Typed.Style { ");
            _hoistedStyleFields.Append(body);
            _hoistedStyleFields.AppendLine(" };");
            return true;
        }

        /// <summary>
        /// Validates that every comma-separated initializer in the body of a
        /// `new Style { ... }` expression is composed entirely of literal /
        /// named-static expressions. Accepts both setter form and tuple form.
        /// </summary>
        private static bool IsHoistableInitializerBody(string body)
        {
            var parts = SplitTopLevelCommas(body);
            foreach (var raw in parts)
            {
                string part = raw.Trim();
                if (part.Length == 0)
                    continue;

                if (part[0] == '(')
                {
                    // Tuple form: ( KeyExpr , ValueExpr )
                    if (part[part.Length - 1] != ')')
                        return false;
                    string inner = part.Substring(1, part.Length - 2);
                    var tupleArgs = SplitTopLevelCommas(inner);
                    if (tupleArgs.Count != 2)
                        return false;
                    if (!IsLiteralExpression(tupleArgs[0].Trim()))
                        return false;
                    if (!IsLiteralExpression(tupleArgs[1].Trim()))
                        return false;
                }
                else
                {
                    // Setter form: PropName = ValueExpr
                    int eq = FindTopLevelEquals(part);
                    if (eq < 0)
                        return false;
                    string lhs = part.Substring(0, eq).Trim();
                    string rhs = part.Substring(eq + 1).Trim();
                    if (!IsSimpleIdentifier(lhs))
                        return false;
                    if (!IsLiteralExpression(rhs))
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Returns the index of the first top-level <c>=</c> in <paramref name="s"/>
        /// that is a single assignment-equals (not <c>==</c>, <c>!=</c>, <c>&lt;=</c>,
        /// <c>&gt;=</c>, <c>=&gt;</c>) and not nested inside any bracket/paren/brace.
        /// </summary>
        private static int FindTopLevelEquals(string s)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '{' || c == '[')
                    depth++;
                else if (c == ')' || c == '}' || c == ']')
                    depth--;
                else if (c == '=' && depth == 0)
                {
                    if (i + 1 < s.Length && s[i + 1] == '=')
                    {
                        i++;
                        continue;
                    }
                    if (i > 0 && (s[i - 1] == '!' || s[i - 1] == '<' || s[i - 1] == '>'))
                        continue;
                    if (i + 1 < s.Length && s[i + 1] == '>')
                    {
                        i++;
                        continue;
                    }
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Returns true iff <paramref name="expr"/> is a compile-time-constant
        /// expression safe to evaluate at class-load (static field initializer).
        /// Whitelist:
        /// numeric/hex literals (with optional sign + suffix), boolean, null,
        /// string literals, named-static dotted refs (`StyleKeys.Width`,
        /// `Position.Absolute`, `Color.red`), and `new T(literal-args...)` for
        /// a small whitelist of pure value-type ctors.
        /// </summary>
        private static bool IsLiteralExpression(string expr)
        {
            if (string.IsNullOrEmpty(expr))
                return false;
            expr = expr.Trim();
            if (expr.Length == 0)
                return false;

            if (expr == "null" || expr == "true" || expr == "false")
                return true;

            // String literal (no embedded unescaped quote, no interpolation).
            if (expr[0] == '"' && expr[expr.Length - 1] == '"' && expr.Length >= 2)
            {
                for (int i = 1; i < expr.Length - 1; i++)
                {
                    if (expr[i] == '"' && expr[i - 1] != '\\')
                        return false;
                }
                return true;
            }
            if (
                expr.Length >= 3
                && expr[0] == '@'
                && expr[1] == '"'
                && expr[expr.Length - 1] == '"'
            )
            {
                // Verbatim string - be strict: no embedded quote at all.
                for (int i = 2; i < expr.Length - 1; i++)
                    if (expr[i] == '"')
                        return false;
                return true;
            }

            // Char literal: 'x' or '\x'
            if (expr[0] == '\'' && expr[expr.Length - 1] == '\'')
                return true;

            if (IsNumericLiteral(expr))
                return true;
            if (IsHexLiteral(expr))
                return true;
            if (IsDottedReference(expr))
                return true;
            if (TryParseLiteralCtor(expr))
                return true;

            return false;
        }

        private static bool IsNumericLiteral(string s)
        {
            int i = 0;
            if (s[i] == '-' || s[i] == '+')
                i++;
            if (i >= s.Length)
                return false;
            bool seenDigit = false;
            bool seenDot = false;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
            {
                if (s[i] == '.')
                {
                    if (seenDot)
                        return false;
                    seenDot = true;
                }
                else
                {
                    seenDigit = true;
                }
                i++;
            }
            if (!seenDigit)
                return false;
            // Optional suffix(es): f F d D m M u U l L
            while (i < s.Length)
            {
                char c = s[i];
                if (
                    c == 'f'
                    || c == 'F'
                    || c == 'd'
                    || c == 'D'
                    || c == 'm'
                    || c == 'M'
                    || c == 'u'
                    || c == 'U'
                    || c == 'l'
                    || c == 'L'
                )
                {
                    i++;
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsHexLiteral(string s)
        {
            int i = 0;
            if (s[i] == '-' || s[i] == '+')
                i++;
            if (i + 2 > s.Length)
                return false;
            if (s[i] != '0' || (s[i + 1] != 'x' && s[i + 1] != 'X'))
                return false;
            i += 2;
            bool seenDigit = false;
            while (i < s.Length && IsHexDigit(s[i]))
            {
                seenDigit = true;
                i++;
            }
            if (!seenDigit)
                return false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == 'u' || c == 'U' || c == 'l' || c == 'L')
                    i++;
                else
                    return false;
            }
            return true;
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        /// <summary>
        /// True iff <paramref name="s"/> is one or more dot-separated simple identifiers
        /// AND the first segment starts with an uppercase letter (C# naming convention
        /// for type / enum / static class names, e.g. <c>StyleKeys.Width</c>,
        /// <c>Color.red</c>, <c>UnityEngine.UIElements.Position.Absolute</c>).
        /// Rejects instance-member access on locals like <c>box.size</c> or
        /// <c>areaSize.x</c> which would not exist at class-load time.
        /// </summary>
        private static bool IsDottedReference(string s)
        {
            if (s.IndexOf('.') < 0)
                return false;
            // Reject method-call form (parentheses anywhere).
            if (s.IndexOf('(') >= 0 || s.IndexOf('[') >= 0)
                return false;
            var parts = s.Split('.');
            // First segment must start uppercase (type/enum/static-class convention).
            // Locals/parameters/fields are camelCase by convention and are rejected.
            if (parts.Length == 0 || parts[0].Length == 0 || !char.IsUpper(parts[0][0]))
                return false;
            foreach (var p in parts)
            {
                if (!IsSimpleIdentifier(p))
                    return false;
            }
            return true;
        }

        private static bool IsSimpleIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_'))
                return false;
            for (int i = 1; i < s.Length; i++)
            {
                if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                    return false;
            }
            return true;
        }

        private static bool TryParseLiteralCtor(string s)
        {
            if (!s.StartsWith("new ", StringComparison.Ordinal))
                return false;
            int parenOpen = s.IndexOf('(');
            if (parenOpen < 0)
                return false;
            if (s[s.Length - 1] != ')')
                return false;
            string typeName = s.Substring(4, parenOpen - 4).Trim();
            if (!s_literalCtorTypes.Contains(typeName))
                return false;
            string args = s.Substring(parenOpen + 1, s.Length - parenOpen - 2);
            if (args.Trim().Length == 0)
                return true; // parameterless ctor of whitelisted type
            var parts = SplitTopLevelCommas(args);
            foreach (var p in parts)
            {
                if (!IsLiteralExpression(p.Trim()))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Splits a string on top-level commas, respecting nested (), {}, [].
        /// Does not track &lt;&gt; (ambiguous with comparison operators).
        /// </summary>
        private static List<string> SplitTopLevelCommas(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
                return result;

            int depth = 0;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(' || c == '{' || c == '[')
                    depth++;
                else if (c == ')' || c == '}' || c == ']')
                    depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < text.Length)
                result.Add(text.Substring(start));
            return result;
        }

        // -- String / variable helpers -----------------------------------

        private static string EscStr(string s) =>
            s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");

        private static string ExtractVarName(string declaration)
        {
            if (string.IsNullOrWhiteSpace(declaration))
                return "__item";
            var parts = declaration
                .Trim()
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[parts.Length - 1] : "__item";
        }

        private static string NormalizeLinePath(string filePath)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath);
            }
            catch
            {
                fullPath = filePath;
            }

            return fullPath.Replace('\\', '/').Replace("\"", "\\\"");
        }

        // -- Asset path resolution ---------------------------------------------

        /// <summary>
        /// Applies setter-lambda sugar and resolves relative asset paths.
        /// </summary>
        private string TransformExpression(string expr)
        {
            string result = s_setterLambdaRe.Replace(expr, "$1.Set(");
            if (result.Contains("Asset<") || result.Contains("Ast<"))
                result = ResolveAssetPaths(result, _filePath, _diagnostics);
            return result;
        }

        /// <summary>
        /// Rewrites every <c>Asset&lt;T&gt;("./x")</c> / <c>Ast&lt;T&gt;("../x")</c> call
        /// in <paramref name="expression"/> so the path literal becomes the absolute
        /// Unity asset key (e.g. <c>Assets/UI/x.png</c>) that the runtime
        /// <see cref="Ruitk.Core.UitkxAssetRegistry"/> indexes by.
        ///
        /// <para>Reports <c>UITKX0120</c> when the resolved file does not exist on
        /// disk, and <c>UITKX0121</c> on extension/type mismatch. The diagnostic
        /// list may be <c>null</c> when called from a context that has no
        /// diagnostic sink (e.g. validation-only callers); existence and
        /// type-mismatch reporting are then suppressed but the rewrite still
        /// happens so the runtime lookup succeeds.</para>
        ///
        /// <para>This method is a pure function of its inputs and is the single
        /// entry point used by every emitter (<see cref="CSharpEmitter"/>,
        /// <see cref="HookEmitter"/>, <see cref="ModuleEmitter"/>) so that
        /// <c>module</c> / <c>hook</c> bodies receive the same path-rewrite as
        /// component setup code and JSX attributes.</para>
        /// </summary>
        // H-03: bare paths ("styles.uss", no "./" prefix) used to pass through
        // AS-IS (unresolved) here while the LSP analyzer resolved them
        // uitkx-dir-relative - the editor showed no error while the build
        // emitted an unresolvable path. Both now delegate to the single
        // canonical rule in AssetPathUtil (Assets//Packages/ absolute;
        // everything else - bare or "./"/"../" - uitkx-dir-relative).
        internal static string ResolveAssetPaths(
            string expression,
            string filePath,
            IList<Diagnostic> diagnostics
        )
        {
            if (string.IsNullOrEmpty(expression))
                return expression;

            return s_assetCallRe.Replace(
                expression,
                match =>
                {
                    string requestedType = match.Groups[1].Value;
                    string rawPath = match.Groups[2].Value;
                    string uitkxDir = GetUitkxAssetDir(filePath);
                    string resolved = Ruitk.Language.AssetPathUtil.ResolveAssetPath(
                        uitkxDir, rawPath,
                        Ruitk.Language.UitkxConfig.LoadRoot(System.IO.Path.GetDirectoryName(filePath)));

                    // Validate file existence at compile time
                    string projectRoot = GetProjectRoot(filePath);
                    if (projectRoot != null && diagnostics != null)
                    {
                        string absolute = Path.Combine(
                            projectRoot,
                            resolved.Replace('/', Path.DirectorySeparatorChar)
                        );
                        if (!File.Exists(absolute))
                        {
                            var loc = Location.Create(filePath, default, default);
                            diagnostics.Add(
                                Diagnostic.Create(UitkxDiagnostics.AssetFileNotFound, loc, resolved)
                            );
                        }
                        else
                        {
                            // Type-mismatch check
                            string ext = Path.GetExtension(rawPath);
                            if (
                                !string.IsNullOrEmpty(ext)
                                && s_extensionValidTypes.TryGetValue(ext, out var validTypes)
                                && !validTypes.Contains(requestedType)
                            )
                            {
                                var loc = Location.Create(filePath, default, default);
                                diagnostics.Add(
                                    Diagnostic.Create(
                                        UitkxDiagnostics.AssetTypeMismatch,
                                        loc,
                                        requestedType,
                                        ext,
                                        string.Join(", ", validTypes)
                                    )
                                );
                            }
                        }
                    }

                    // Rewrite whenever resolution changed the literal (bare and "./"/"../"
                    // paths all resolve to something different from the raw text; an
                    // already-absolute Assets/ or Packages/ path resolves to itself).
                    if (!string.Equals(resolved, rawPath, StringComparison.Ordinal))
                        return match.Value.Replace($"\"{rawPath}\"", $"\"{resolved}\"");
                    return match.Value;
                }
            );
        }

        /// <summary>
        /// Extracts the Unity asset directory (e.g. <c>Assets/UI</c>) from the
        /// absolute path of the <c>.uitkx</c> file being compiled.
        /// </summary>
        private static string GetUitkxAssetDir(string filePath)
        {
            string? assetDir = Ruitk.Language.AssetPathUtil.GetAssetDir(filePath);
            if (assetDir != null)
                return assetDir;
            // Fallback for non-Unity paths (test environments)
            return Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? "";
        }

        /// <summary>
        /// Extracts the Unity project root (parent of <c>Assets/</c>) from <paramref name="filePath"/>.
        /// Returns <c>null</c> when the path doesn't contain an <c>Assets/</c> segment.
        /// </summary>
        private static string GetProjectRoot(string filePath)
        {
            string normalized = (filePath ?? string.Empty).Replace('\\', '/');
            int assetsIdx = normalized.IndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (assetsIdx >= 0)
                return normalized.Substring(0, assetsIdx);
            return null;
        }

        // -- StringBuilder helpers ---------------------------------------------

        private void L(string text) => _sb.AppendLine(text);
    }
}
