using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Ruitk.Language.Nodes;
using Ruitk.Language.Parser;

namespace Ruitk.Language.Roslyn
{
    // -- Collected expression --------------------------------------------------

    /// <summary>
    /// A single C# expression (inline or attribute) gathered during the AST walk,
    /// ready to be written into the virtual document as a type-checking wrapper.
    /// </summary>
    internal sealed class CollectedExpression
    {
        /// <summary>Trimmed C# expression text.</summary>
        public string Text { get; init; } = "";

        /// <summary>Absolute char offset in the .uitkx source where <see cref="Text"/> starts.</summary>
        public int UitkxOffset { get; init; }

        /// <summary>1-based line in the .uitkx source.</summary>
        public int UitkxLine { get; init; }

        /// <summary>Label appended to the generated method/variable name (e.g. "attr_0_text").</summary>
        public string Label { get; init; } = "";

        public SourceRegionKind Kind { get; init; }
    }

    // -- Virtual document ------------------------------------------------------

    /// <summary>
    /// The generated virtual C# document for a single .uitkx file, together with
    /// the <see cref="SourceMap"/> that maps positions back to the .uitkx source.
    /// </summary>
    public sealed class VirtualDocument
    {
        /// <summary>
        /// Full text of the virtual C# file, encoded with LF-only line endings.
        /// Feed this to <c>AdhocWorkspace.AddDocument</c> to get a Roslyn document.
        /// </summary>
        public string Text { get; }

        /// <summary>Bidirectional position map between virtual C# and .uitkx source.</summary>
        public SourceMap Map { get; }

        /// <summary>The .uitkx source path this document was generated from.</summary>
        public string UitkxFilePath { get; }

        internal VirtualDocument(string text, SourceMap map, string uitkxFilePath)
        {
            Text = text;
            Map = map;
            UitkxFilePath = uitkxFilePath;
        }
    }

    // -- Builder (internal) ----------------------------------------------------

    /// <summary>
    /// Stateful builder used during document generation.  Tracks the current
    /// byte offset in the output <see cref="StringBuilder"/> so mapped spans can be
    /// recorded precisely.
    /// </summary>
    internal sealed class VirtualDocBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder(4096);
        private readonly List<SourceMapEntry> _entries = new List<SourceMapEntry>();
        private int _virtualPos;

        // -- Scaffold text (no source-map entry) ------------------------------

        /// <summary>Appends generated-scaffold text that does not map to any .uitkx position.</summary>
        public void Scaffold(string text)
        {
            _sb.Append(text);
            _virtualPos += text.Length;
        }

        // -- Mapped region ----------------------------------------------------

        /// <summary>
        /// Appends text that was copied verbatim from the .uitkx source, recording
        /// the bidirectional span so the source map can translate positions.
        /// </summary>
        public void Mapped(string text, int uitkxStart, SourceRegionKind kind, int uitkxLine)
        {
            if (text.Length == 0)
                return;

            int vStart = _virtualPos;
            _sb.Append(text);
            _virtualPos += text.Length;

            _entries.Add(
                new SourceMapEntry(
                    VirtualStart: vStart,
                    VirtualEnd: _virtualPos,
                    UitkxStart: uitkxStart,
                    UitkxEnd: uitkxStart + text.Length,
                    Kind: kind,
                    UitkxLine: uitkxLine
                )
            );
        }

        // -- Output -----------------------------------------------------------

        public VirtualDocument Build(string uitkxFilePath) =>
            new VirtualDocument(
                text: _sb.ToString(),
                map: new SourceMap(_entries.ToImmutableArray()),
                uitkxFilePath: uitkxFilePath
            );

        public int CurrentPos => _virtualPos;
    }

    // -- Generator ------------------------------------------------------------

    /// <summary>
    /// Generates an in-memory C# source file (the "virtual document") from a
    /// parsed UITKX file.
    ///
    /// <para><b>Purpose:</b> The virtual document is fed to a Roslyn
    /// <c>AdhocWorkspace</c> so the language server can answer type-related
    /// questions about the C# regions embedded inside the .uitkx file:
    /// diagnostics, completions, hover, and semantic token classification.</para>
    ///
    /// <para><b>Virtual document structure:</b>
    /// All expressions plus the setup code are placed inside a
    /// <c>__uitkx_render()</c> method so local variables declared in setup code
    /// are visible to expressions that reference them.</para>
    ///
    /// <para><b>Source map:</b> Every C# expression region records a
    /// <see cref="SourceMapEntry"/> so the calling code can convert positions in
    /// either direction between the virtual document and the .uitkx source.</para>
    ///
    /// <para><b>Language-lib constraint:</b> This class has NO dependency on Roslyn
    /// NuGet packages.  It lives in <c>language-lib</c> which targets
    /// <c>netstandard2.0</c> and must stay Roslyn-free so it can be referenced by
    /// the Unity source-generator project.</para>
    /// </summary>
    public sealed class VirtualDocumentGenerator
    {
        // -- Standard using directives -----------------------------------------

        /// <summary>
        /// Usings always injected into every virtual document.
        /// These cover the types most commonly found in .uitkx @code blocks and
        /// expressions, preventing false CS0246 errors.
        /// The list is intentionally conservative - only assemblies that the
        /// Unity project always contains.
        /// </summary>
        private static readonly string[] s_standardUsings =
        {
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "UnityEngine",
            "Ruitk.Core",
            "Ruitk.Core.Animation",
            "Ruitk.Router",
            "Ruitk.Props.Typed",
        };

        /// <summary>
        /// Extra lines appended after the normal using directives.
        /// These are not simple namespace usings (static / alias forms).
        /// </summary>
        private static readonly string[] s_extraUsingLines =
        {
            "using static Ruitk.Props.Typed.StyleKeys;",
            "using static Ruitk.Props.Typed.CssHelpers;",
            // Brings Asset<T>(...) and Ast<T>(...) into scope for component
            // setup blocks, hook bodies, and module initializers - matches the
            // using-static injected by every runtime emitter (CSharpEmitter,
            // ModuleEmitter, HookEmitter, HmrCSharpEmitter, HmrHookEmitter).
            "using static Ruitk.AssetHelpers;",
            "using UColor = UnityEngine.Color;",
            // `using static StyleKeys` imports string constants (e.g. FlexDirection = "flexDirection")
            // that collide with identically-named enums/structs from UnityEngine.UIElements.
            // We cannot import UIElements wholesale, but CssHelpers returns types from it
            // (EasingFunction, BackgroundRepeat, etc.) so users need those type names.
            // Targeted aliases import only the non-conflicting types users would reference.
            "using Color = UnityEngine.Color;",
            "using EasingFunction = UnityEngine.UIElements.EasingFunction;",
            "using EasingMode = UnityEngine.UIElements.EasingMode;",
            "using BackgroundRepeat = UnityEngine.UIElements.BackgroundRepeat;",
            "using BackgroundPosition = UnityEngine.UIElements.BackgroundPosition;",
            "using BackgroundSize = UnityEngine.UIElements.BackgroundSize;",
            "using TransformOrigin = UnityEngine.UIElements.TransformOrigin;",
            "using BackgroundPositionKeyword = UnityEngine.UIElements.BackgroundPositionKeyword;",
            "using BackgroundSizeType = UnityEngine.UIElements.BackgroundSizeType;",
            "using Repeat = UnityEngine.UIElements.Repeat;",
            "using Length = UnityEngine.UIElements.Length;",
            "using StyleKeyword = UnityEngine.UIElements.StyleKeyword;",
            "using TextAutoSizeMode = UnityEngine.UIElements.TextAutoSizeMode;",
            // Unity 6.3+ types - guarded so the virtual document parses cleanly under both
            // 6.2- and 6.3+ targets (the LSP simply forwards the directives to Roslyn).
            "#if UNITY_6000_3_OR_NEWER",
            "using FilterFunction = UnityEngine.UIElements.FilterFunction;",
            "using Ratio = UnityEngine.UIElements.Ratio;",
            "using StyleRatio = UnityEngine.UIElements.StyleRatio;",
            "using MaterialDefinition = UnityEngine.UIElements.MaterialDefinition;",
            "using StyleMaterialDefinition = UnityEngine.UIElements.StyleMaterialDefinition;",
            "#endif",
        };

        // -- Public API --------------------------------------------------------

        /// <summary>
        /// Generates the virtual C# document for <paramref name="parseResult"/> and
        /// returns a <see cref="VirtualDocument"/> that includes both the text and
        /// the bidirectional source map.
        /// </summary>
        /// <param name="parseResult">
        /// The fully-parsed UITKX document (directives + AST + diagnostics).
        /// </param>
        /// <param name="source">
        /// The raw .uitkx source text.  Must be the same string that was used to
        /// produce <paramref name="parseResult"/> so source offsets are valid.
        /// </param>
        /// <param name="uitkxFilePath">
        /// Absolute path to the .uitkx file, written into <c>#line</c> directives.
        /// </param>
        /// <summary>Lower a file's <c>import</c> declarations to the same injected-using
        /// payloads the real emit produces (single source of truth: ImportScopeFacts, with the
        /// target's EFFECTIVE namespace — shared with the SG and, via reflection, the HMR
        /// compiler), emitted INSIDE the namespace block (ES-modules campaign, M7): file-keyed
        /// namespaces make sibling file stems enclosing-namespace members, which shadow
        /// FILE-level using-aliases — the editor must resolve exactly like the build. Degrades
        /// silently (no using emitted) when a target is unresolvable/unreadable.</summary>
        private static void AppendImportUsingsInsideNamespace(
            VirtualDocBuilder b, DirectiveSet d, string uitkxFilePath, HashSet<string> seen)
        {
            foreach (string payload in Ruitk.Language.ImportScopeFacts
                         .ComputeInjectedUsingPayloads(d, uitkxFilePath))
            {
                if (seen.Add(payload))
                    b.Scaffold($"    using {ImportScopeFacts.GlobalizeUsingPayload(payload)};\n");
            }
            // The file's own @using / namespace-import lines move inside too (same relative-
            // resolution + shadowing rules apply to them once the namespace is file-keyed).
            foreach (var u in d.Usings)
            {
                if (seen.Add(u))
                    b.Scaffold($"    using {ImportScopeFacts.GlobalizeUsingPayload(u)};\n");
            }
        }

        public VirtualDocument Generate(
            ParseResult parseResult,
            string source,
            string uitkxFilePath,
            IPropsTypeProvider? propsTypes = null
        )
        {
            var b = new VirtualDocBuilder();
            var d = parseResult.Directives;

            // -- File header --------------------------------------------------
            b.Scaffold("// <auto-generated: UITKX Roslyn virtual document>\n");
            b.Scaffold($"// Source: {EscapeForComment(uitkxFilePath)}\n");
            b.Scaffold("// DO NOT EDIT - regenerated on every document change.\n");
            b.Scaffold("#line hidden\n");
            b.Scaffold("#nullable enable annotations\n");
            // CS0246 removed from global list - suppressed only on specific scaffold lines
            // that are known to reference external types (event types, props type, injects).
            // This allows Roslyn to surface real CS0246 errors in user-authored C# regions.
            b.Scaffold(
                "#pragma warning disable CS0169, CS0414, CS8618, CS8019, CS1591, CS0649, CS0411, CS1660, CS1026, CS1513, CS8632, CS8974\n\n"
            );

            // -- Using directives ---------------------------------------------
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in s_standardUsings)
            {
                if (seen.Add(u))
                    b.Scaffold($"using {u};\n");
            }
            // The file's own usings go INSIDE the namespace block (globalized) via
            // AppendImportUsingsInsideNamespace — emitting them here too would both diverge from
            // the build's resolution AND poison `seen` so the inside-namespace pass skips them.
            // Import payloads are emitted INSIDE the namespace block too (mirror of the real
            // emitters) — the editor must resolve exactly like the build.
            // Static / alias usings that cannot go through the simple "using X;" path
            foreach (var line in s_extraUsingLines)
                b.Scaffold(line + "\n");
            b.Scaffold("\n");

            // (The legacy hook/module document dispatch was removed in the 0.16.0 legacy
            // wave — member files scaffold through the __Exports shape below.)

            // -- Namespace + class header -------------------------------------
            string ns = DocumentNamespace(d, uitkxFilePath);
            string className = !string.IsNullOrEmpty(d.ComponentName)
                ? d.ComponentName!
                : "Component";
            string escapedPath = EscapePathForLineDirective(uitkxFilePath);

            // -- __Exports scaffold (ES-modules campaign, U-02/M5) ----
            // Plain member declarations (values/utils/hooks) live on the per-file __Exports
            // container in the real build; scaffold the same shape here — with mapped bodies so
            // IntelliSense/diagnostics work inside them — plus stub bridges for aliased/default
            // member imports. The component partial (below) sees them bare-name through the
            // own-exports using scaffolded right below (same namespace is NOT enough for
            // static-class members).
            bool hasMembers = !d.MemberDeclarations.IsDefaultOrEmpty;
            var bridges = ImportScopeFacts.ComputeImportedMemberBridges(d, uitkxFilePath);
            if (hasMembers || bridges.Count > 0)
            {
                b.Scaffold($"namespace {ns}\n{{\n");
                AppendImportUsingsInsideNamespace(b, d, uitkxFilePath, seen);
                b.Scaffold($"    using static global::{ns}.__Exports;\n\n");
                EmitExportsScaffold(b, d, bridges, escapedPath, source);
                if (string.IsNullOrEmpty(d.ComponentName))
                {
                    b.Scaffold("}\n");
                    return b.Build(uitkxFilePath);
                }
                b.Scaffold($"    partial class {className}\n    {{\n");
                b.Scaffold("#line hidden\n");
                EmitFunctionStyleBody(b, parseResult, source, escapedPath, propsTypes);
                b.Scaffold("    }\n}\n");
                return b.Build(uitkxFilePath);
            }

            b.Scaffold($"namespace {ns}\n{{\n");
            AppendImportUsingsInsideNamespace(b, d, uitkxFilePath, seen);
            b.Scaffold($"    partial class {className}\n    {{\n");
            b.Scaffold("#line hidden\n");

            // Ref<T> resolves to the workspace-shared canonical type:
            //   - real Ruitk.Core.Ref<T> when the runtime DLL is loaded
            //   - the polyfill stub in __UitkxPolyfill__.g.cs otherwise
            // Both share the same fully-qualified name so cross-document calls
            // (e.g. component -> peer hook(Ref<T>)) bind to a single nominal type.

            EmitFunctionStyleBody(b, parseResult, source, escapedPath, propsTypes);

            // -- Close class + namespace --------------------------------------
            b.Scaffold("    }\n}\n");

            return b.Build(uitkxFilePath);
        }

        /// <summary>
        /// The namespace a virtual document's types are declared in. Must be the file's
        /// EFFECTIVE namespace (explicit <c>@namespace</c> wins, else path-derived + config
        /// prefix) — the same namespace the real build emits into — so the virtual-doc type
        /// SHADOWS the identical compiled-DLL symbol instead of coexisting with it. Emitting
        /// under the RAW parsed namespace (the parser default <c>Ruitk.FunctionStyle</c>
        /// for every stamp-less file) creates a SECOND container in a foreign namespace; with
        /// both pulled into scope (peer-enrich uses one, import payloads the other) every
        /// companion-hook call turns CS0121-ambiguous in the editor while the build is clean.
        /// </summary>
        private static string DocumentNamespace(DirectiveSet d, string uitkxFilePath)
        {
            string? effective = EffectiveNamespace.Resolve(
                d.HasExplicitNamespace, d.Namespace, uitkxFilePath,
                fileKeyed: true);
            if (!string.IsNullOrEmpty(effective))
                return effective!;
            return !string.IsNullOrEmpty(d.Namespace) ? d.Namespace! : "Ruitk.Generated";
        }

        /// <summary>
        /// Scaffolds the per-file <c>__Exports</c> container for a NEW-MODE file (ES-modules
        /// campaign, U-02/M5): hook stubs (static form), every member declaration with its body
        /// MAPPED for IntelliSense (value initializers, util/hook bodies), and signature-only
        /// stub bridges for aliased/default member imports (bodies are <c>default!</c> — the
        /// bound NAME and signature are what setup-code resolution needs; the real forwarding
        /// lives in the build's ExportsEmitter output).
        /// </summary>
        private static void EmitExportsScaffold(
            VirtualDocBuilder b,
            DirectiveSet d,
            IReadOnlyList<(string Alias, string? ReturnType, string? ParamsText, bool IsValue, string? TypeParams)> bridges,
            string escapedPath,
            string source)
        {
            b.Scaffold("    static partial class __Exports\n    {\n");
            b.Scaffold("#line hidden\n");
            b.Scaffold(global::Ruitk.Core.HookRegistry.GenerateVirtualDocStubs(staticForm: true));

            if (!d.MemberDeclarations.IsDefaultOrEmpty)
            {
                foreach (var m in d.MemberDeclarations)
                {
                    string access = m.IsExported ? "public" : "internal";
                    if (m.Kind == DeclKind.Value)
                    {
                        string type = m.ReturnTypeText
                            ?? ImportScopeFacts.ExtractNewInitializerTypeName(m.BodyText) ?? "object";
                        b.Scaffold($"        {access} static {type} ");
                        EmitMemberName(b, m, source);
                        b.Scaffold(" = ");
                        b.Scaffold($"\n#line {m.BodyStartLine} \"{escapedPath}\"\n");
                        b.Mapped(m.BodyText, m.BodyStartOffset, SourceRegionKind.ModuleBody, m.BodyStartLine);
                        b.Scaffold("\n#line hidden\n");
                        b.Scaffold("        ;\n\n");
                        continue;
                    }

                    string ret = string.Equals(m.ReturnTypeText?.Trim(), "void", StringComparison.Ordinal)
                        || string.IsNullOrEmpty(m.ReturnTypeText)
                        ? "void" : m.ReturnTypeText!;
                    string paramsText = m.ParamsText ?? string.Empty;
                    string typeParams = m.TypeParamsText ?? string.Empty;
                    var kind = m.Kind == DeclKind.Hook ? SourceRegionKind.HookBody : SourceRegionKind.ModuleBody;
                    if (m.IsExpressionBodied)
                    {
                        b.Scaffold($"        {access} static {ret} ");
                        EmitMemberName(b, m, source);
                        b.Scaffold($"{typeParams}({paramsText}) => ");
                        b.Scaffold($"\n#line {m.BodyStartLine} \"{escapedPath}\"\n");
                        b.Mapped(m.BodyText, m.BodyStartOffset, kind, m.BodyStartLine);
                        b.Scaffold("\n#line hidden\n");
                        b.Scaffold("        ;\n\n");
                    }
                    else
                    {
                        b.Scaffold($"        {access} static {ret} ");
                        EmitMemberName(b, m, source);
                        b.Scaffold($"{typeParams}({paramsText})\n");
                        b.Scaffold("        {\n");
                        b.Scaffold($"#line {m.BodyStartLine} \"{escapedPath}\"\n");
                        b.Mapped(m.BodyText, m.BodyStartOffset, kind, m.BodyStartLine);
                        b.Scaffold("\n#line hidden\n");
                        b.Scaffold("        }\n\n");
                    }
                }
            }

            foreach (var (alias, retType, paramsText, isValue, bridgeTypeParams) in bridges)
            {
                if (isValue)
                {
                    b.Scaffold($"        internal static {retType ?? "object"} {alias} => default!;\n");
                    continue;
                }
                string bridgeRet = string.IsNullOrEmpty(retType)
                    || string.Equals(retType!.Trim(), "void", StringComparison.Ordinal)
                    ? "void" : retType!;
                string bridgeBody = bridgeRet == "void" ? "{ }" : "=> default!;";
                b.Scaffold($"        internal static {bridgeRet} {alias}{bridgeTypeParams ?? string.Empty}({paramsText ?? string.Empty}) {bridgeBody}\n");
            }

            b.Scaffold("    }\n\n");
        }

        /// <summary>
        /// Emits a member declaration's NAME token. The name is user-authored source
        /// text, not scaffold — mapping it makes Roslyn authoritative for cursor-on-name
        /// (semantic tokens, definition, references, rename) instead of falling to the
        /// text-scan/TextMate heuristics. Emitted bytes are identical on both paths;
        /// the position is verified against the source before mapping so a stale
        /// <c>NameColumn</c> degrades to scaffold rather than mapping a wrong span.
        /// </summary>
        private static void EmitMemberName(VirtualDocBuilder b, MemberDeclaration m, string source)
        {
            if (m.DeclarationLine > 0 && m.NameColumn >= 0)
            {
                int offset = 0;
                for (int line = 1; line < m.DeclarationLine && offset >= 0; line++)
                {
                    offset = source.IndexOf('\n', offset);
                    if (offset >= 0)
                        offset++;
                }
                if (offset >= 0)
                {
                    int nameOffset = offset + m.NameColumn;
                    if (nameOffset + m.Name.Length <= source.Length
                        && string.CompareOrdinal(source, nameOffset, m.Name, 0, m.Name.Length) == 0)
                    {
                        b.Mapped(m.Name, nameOffset,
                            m.Kind == DeclKind.Hook ? SourceRegionKind.HookBody : SourceRegionKind.ModuleBody,
                            m.DeclarationLine);
                        return;
                    }
                }
            }
            b.Scaffold(m.Name);
        }

        // -- Function-style component ------------------------------------------

        private static void EmitFunctionStyleBody(
            VirtualDocBuilder b,
            ParseResult parseResult,
            string source,
            string escapedPath,
            IPropsTypeProvider? propsTypes = null
        )
        {
            var d = parseResult.Directives;

            // Issue 1 (UITKX0112): publish source + directives in a thread-static
            // context so leaf emitters (EmitExpressionStatement / EmitTypedPropsCheck /
            // EmitBodyWithReturnFix) can produce deferred Pattern-B JSX type-check
            // blocks for attribute-lambda JSX subtrees that would otherwise be silently
            // dropped. Restored in finally so nested / sequential calls aren't disturbed.
            var __savedJsxCtx = t_jsxAttrContext;
            t_jsxAttrContext = new JsxAttrCheckContext { Source = source, Directives = d };
            try
            {
                EmitFunctionStyleBodyCore(b, parseResult, source, escapedPath, propsTypes);
            }
            finally
            {
                t_jsxAttrContext = __savedJsxCtx;
            }
        }

        private static void EmitFunctionStyleBodyCore(
            VirtualDocBuilder b,
            ParseResult parseResult,
            string source,
            string escapedPath,
            IPropsTypeProvider? propsTypes
        )
        {
            var d = parseResult.Directives;

            // Typed parameters become private fields so the render method can access them.
            if (!d.FunctionParams.IsDefaultOrEmpty)
            {
                foreach (var p in d.FunctionParams)
                    b.Scaffold($"        private {p.Type} {p.Name};\n");
                b.Scaffold("\n");
            }

            // -- Hook shorthand stubs ------------------------------------------
            // The authoring shorthands useState / useMemo / useEffect etc. are NOT
            // rewritten before being fed to Roslyn, so we scaffold private methods
            // with the correct return types so Roslyn can type-check the setup code
            // without CS0103 / CS8130 / CS1026 errors.
            // CS0246: hook stubs reference Unity/Ruitk types that may not
            // be resolvable before the first Unity compile (e.g. VisualElement).
            //
            // State setter delegate: void __StateSetter__<T>(Func<T,T> updater).
            // The real API (StateSetter<T> + StateUpdate<T>) supports both
            //   setX(newValue)        - direct value
            //   setX(prev => prev+1)  - updater function
            // via implicit operators on StateUpdate<T>.
            //
            // We model the setter as a delegate accepting Func<T,T> so that
            // Roslyn properly type-checks lambda bodies - the lambda parameter
            // `prev` is correctly inferred as `T`, enabling full semantic analysis
            // inside updater lambdas.
            //
            // Direct-value calls like setCount(5) produce CS1503 (can't convert
            // int to Func<int,int>); this is suppressed in RoslynDiagnosticMapper
            // by checking for the state-setter pattern in the error message.
            //
            // useRef<T>() returns the workspace-shared global::Ruitk.Core.Ref<T>
            //   (real DLL when loaded, polyfill stub otherwise) so cross-document
            //   calls bind to one nominal type and .Current completions work.
            //
            // Stub block sourced from Ruitk.Core.HookRegistry; see the
            // matching static-form emission in GenerateHookContainerDocument
            // for the design rationale (single source of truth across SG / HMR
            // / IDE / VDG).  The instance form differs only in the method
            // modifier (private vs private static) and the header-comment
            // box-drawing tail length - both encoded inside the registry.
            b.Scaffold(global::Ruitk.Core.HookRegistry.GenerateVirtualDocStubs(staticForm: false));

            // Collect markup nodes - skip non-rendering nodes.
            var markupOnlyNodes = ImmutableArray.CreateBuilder<AstNode>(
                parseResult.RootNodes.Length
            );
            foreach (var n in parseResult.RootNodes)
                markupOnlyNodes.Add(n);

            // All code goes inside a single render method so local vars from setup
            // code are visible to expressions.
            // Return type is `object` (not void) so that JSX paren blocks replaced with
            // `return (object)null!` in conditional branches (e.g. `if (...) return (...)`)
            // are valid C# - a void method can't return a value.
            b.Scaffold("        private object __uitkx_render()\n        {\n");

            // __children is always available in every component's render scope
            // (the SG emits it as a parameter: IReadOnlyList<VirtualNode> __children).
            // Declare it here so {__children} expressions don't produce CS0103.
            // Uses dynamic so member access (.Count etc.) compiles without the
            // Ruitk assembly and without false-positive CS1061.
            b.Scaffold("            dynamic __children = null!;\n");

            // Setup code - emitted in segments so that JSX paren blocks
            // (e.g. `var x = (<Box>...</Box>)`) are replaced with a valid C#
            // placeholder and never seen by Roslyn as markup.
            int __exprCtr = 0;
            int __attrCtr = 0;
            if (!string.IsNullOrEmpty(d.FunctionSetupCode) && d.FunctionSetupStartLine > 0)
            {
                // Use the exact character offset of the trimmed content when available;
                // fall back to line-start approximation for older/generated DirectiveSets.
                int setupStartOffset =
                    d.FunctionSetupStartOffset >= 0
                        ? d.FunctionSetupStartOffset
                        : OffsetOfLine(source, d.FunctionSetupStartLine);

                // Count newlines in the gap (removed return statement) so that
                // the straddle case in EmitMappedWithGap can compute the correct
                // #line directive for post-gap code.
                int gapNewlines = 0;
                if (d.FunctionSetupGapOffset >= 0 && d.FunctionSetupGapLength > 0)
                {
                    int gapSourceStart = setupStartOffset + d.FunctionSetupGapOffset;
                    int gapSourceEnd = gapSourceStart + d.FunctionSetupGapLength;
                    gapNewlines = CountNewlines(
                        source,
                        gapSourceStart,
                        Math.Min(gapSourceEnd, source.Length)
                    );
                }

                EmitFunctionStyleSetupSegmented(
                    b,
                    d.FunctionSetupCode!,
                    uitkxSetupStartOffset: setupStartOffset,
                    uitkxSetupStartLine: d.FunctionSetupStartLine,
                    escapedPath: escapedPath,
                    gapOffset: d.FunctionSetupGapOffset,
                    gapLength: d.FunctionSetupGapLength,
                    gapNewlines: gapNewlines,
                    source: source,
                    directives: d,
                    exprCtr: ref __exprCtr,
                    attrCtr: ref __attrCtr
                );
            }

            // Expression checks - emitted in-scope so that loop variables declared
            // in @for / @foreach / @while headers are visible inside the body.
            b.Scaffold(
                "            // -- Expression type checks ---------------------------------\n"
            );
            b.Scaffold("#pragma warning disable 0162\n");
            EmitNodeExpressionsScoped(
                markupOnlyNodes.ToImmutable(),
                b,
                escapedPath,
                indent: "            ",
                ref __exprCtr,
                ref __attrCtr,
                propsTypes
            );

            b.Scaffold("#pragma warning restore 0162\n");

            // Ensure all code paths return - components whose setup code only has
            // conditional `return (object)null!` branches need a fallback. This sentinel
            // is ITSELF sometimes unreachable by design (e.g. an exhaustive @switch with a
            // @default case, now emitted as a real switch/case above, always returns) — it
            // needs its OWN tightly-scoped disable/restore pair (matching the pattern
            // already used at the other two `return default!;` sentinel sites in this
            // file), NOT reliance on the pragma above staying in effect: a `disable 0162`
            // several statements earlier does NOT survive a nested `restore 0162` emitted
            // in between (e.g. by EmitDirectiveBodyCode inside a switch case body) —
            // pragma disable/restore is a flat, non-stacking toggle, so the LAST restore
            // anywhere before this point wins, regardless of nesting.
            b.Scaffold("#pragma warning disable CS0162\n");
            b.Scaffold("            return default!;\n");
            b.Scaffold("#pragma warning restore CS0162\n");
            b.Scaffold("        }\n"); // close render method
        }

        // -- Expression wrappers / statements ---------------------------------

        /// <summary>
        /// Issue 1 (UITKX0112) - thread-static context that lets leaf expression
        /// emitters (<see cref="EmitExpressionStatement"/>, <see cref="EmitTypedPropsCheck"/>,
        /// <see cref="EmitBodyWithReturnFix"/>) recover the source text and
        /// <see cref="DirectiveSet"/> needed to emit deferred Pattern-B JSX
        /// type-check blocks for stripped attribute-lambda JSX subtrees.
        ///
        /// <para>Set by the top-level entry methods (<see cref="EmitFunctionStyleBody"/>,
        /// <see cref="EmitInlineExprChecks"/>) inside a try/finally pair so the
        /// previous value is always restored. Avoids threading two extra optional
        /// parameters through six intermediate methods.</para>
        ///
        /// <para>Marked <c>[ThreadStatic]</c> so concurrent LSP document workers
        /// don't observe each other's state.</para>
        /// </summary>
        private sealed class JsxAttrCheckContext
        {
            public string Source = string.Empty;
            public DirectiveSet Directives = null!;
        }

        [ThreadStatic]
        private static JsxAttrCheckContext? t_jsxAttrContext;

        /// <summary>
        /// Phase 1 - emit expression text into the virtual document, replacing
        /// any embedded JSX literals with a <c>(VirtualNode)null!</c> stub so
        /// Roslyn can parse and type-check the surrounding C# without seeing
        /// JSX literals it does not understand.
        ///
        /// <para>Mirrors <c>CSharpEmitter.SpliceExpressionMarkup</c> at the
        /// IDE/Roslyn layer. Where the source generator emits real
        /// <c>V.Tag(...)</c> calls, the virtual document emits typed-null
        /// stubs - sufficient for Roslyn parsing. Source-mapped regions
        /// surrounding the stub still produce column-precise diagnostics
        /// for the user's C# code outside the JSX.</para>
        ///
        /// <para>For the common case (no JSX), the helper falls through to a
        /// single <see cref="VirtualDocBuilder.Mapped"/> call - same shape as
        /// before Phase 1.</para>
        /// </summary>
        private static void EmitMappedExpressionStrippingJsx(
            VirtualDocBuilder b,
            string text,
            int uitkxOffset,
            SourceRegionKind kind,
            int uitkxLine,
            List<(int SrcStart, int SrcEnd)>? deferredJsxRanges = null
        )
        {
            if (string.IsNullOrEmpty(text))
            {
                b.Mapped(text, uitkxOffset, kind, uitkxLine);
                return;
            }

            var markupRanges = DirectiveParser.FindJsxBlockRanges(text, 0, text.Length);
            var bareRanges = DirectiveParser.FindBareJsxRanges(text, 0, text.Length);

            if (markupRanges.IsDefaultOrEmpty && bareRanges.IsDefaultOrEmpty)
            {
                b.Mapped(text, uitkxOffset, kind, uitkxLine);
                return;
            }

            var allRanges = new List<(int Start, int End, int Line)>(
                markupRanges.Length + bareRanges.Length
            );
            foreach (var r in markupRanges)
                allRanges.Add(r);
            foreach (var r in bareRanges)
                allRanges.Add(r);
            allRanges.Sort((a, c) => a.Start.CompareTo(c.Start));

            int prev = 0;
            foreach (var (start, end, _) in allRanges)
            {
                int s = start;
                int e = end;
                if (s < 0)
                    s = 0;
                if (e < 0)
                    e = 0;
                if (s > text.Length)
                    s = text.Length;
                if (e > text.Length)
                    e = text.Length;
                if (e <= s)
                    continue;
                if (s < prev)
                    continue;

                // -- Logical-AND desugar parity (mirrors CSharpEmitter) ----
                // If the prefix text[prev..s] ends in `&&`, the runtime
                // splicer rewrites to a ternary. To prevent a permanent
                // CS0019 squiggle in the editor on the user's `&&` line,
                // emit the prefix WITHOUT the `&&`, then replace the `&&`
                // and JSX with a ternary placeholder that Roslyn typechecks
                // identically to the runtime form.
                int ampStart = TryFindTrailingLogicalAnd(text, prev, s);
                if (ampStart >= 0)
                {
                    // Map the LHS portion (everything up to but not including
                    // the `&&`) so completions / hovers / squiggles on `cond`
                    // still work at their original source positions.
                    if (ampStart > prev)
                    {
                        string lhsSeg = text.Substring(prev, ampStart - prev);
                        b.Mapped(lhsSeg, uitkxOffset + prev, kind, uitkxLine);
                    }
                    // Replace `&& <Tag/>` with `? ((VirtualNode)null!) : null`.
                    // Same typed-null technique as the standard placeholder so
                    // both ternary branches return `VirtualNode` and the outer
                    // expression's type stays well-defined.
                    b.Scaffold("? ((global::Ruitk.Core.VirtualNode)null!) : null");
                    // Also defer JSX subtree type-checking so diagnostics inside
                    // the `<Tag/>` body are not silently dropped.
                    deferredJsxRanges?.Add((uitkxOffset + s, uitkxOffset + e));
                    prev = e;
                    continue;
                }

                if (s > prev)
                {
                    string seg = text.Substring(prev, s - prev);
                    b.Mapped(seg, uitkxOffset + prev, kind, uitkxLine);
                }

                // Stub: typed-null lets the surrounding expression compile
                // and flow into both VirtualNode-typed and object-typed
                // contexts. Same pattern as EmitDirectiveBodyCode line 1244.
                b.Scaffold("((global::Ruitk.Core.VirtualNode)null!)");
                // Record source-space range so callers (when source/directives
                // are available) can emit a deferred Pattern-B JSX-subtree
                // type-check block - preserves squiggles inside the dropped
                // attribute-lambda JSX body.
                deferredJsxRanges?.Add((uitkxOffset + s, uitkxOffset + e));

                prev = e;
            }

            if (prev < text.Length)
            {
                string tail = text.Substring(prev);
                b.Mapped(tail, uitkxOffset + prev, kind, uitkxLine);
            }
        }

        /// <summary>
        /// Returns the index of the trailing <c>&amp;&amp;</c> in
        /// <paramref name="text"/><c>[prev..jsxStart]</c> if the prefix ends
        /// with that operator (modulo trailing whitespace), or -1 otherwise.
        /// IDE-side mirror of <c>CSharpEmitter.TryFindTrailingLogicalAnd</c>.
        /// Used by <see cref="EmitMappedExpressionStrippingJsx"/> to trigger
        /// the logical-AND desugar parity rewrite so the editor doesn't show
        /// a permanent CS0019 squiggle on `cond &amp;&amp; &lt;Tag/&gt;`.
        /// </summary>
        private static int TryFindTrailingLogicalAnd(string text, int prev, int jsxStart)
        {
            int i = jsxStart - 1;
            while (i >= prev && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n'))
                i--;
            if (i - 1 < prev) return -1;
            if (text[i] != '&' || text[i - 1] != '&') return -1;
            if (i - 2 >= prev && text[i - 2] == '&') return -1;
            return i - 1;
        }

        /// <summary>
        /// Issue 1 (UITKX0112) - emits a deferred Pattern-B JSX-subtree type-check
        /// block for a JSX literal that was stripped (replaced with a typed-null
        /// stub) inside an attribute-lambda body, an inline expression, a typed
        /// props expression, or a body-with-return-fix segment.
        ///
        /// <para>Without this helper, JSX literals embedded inside expressions
        /// (e.g. <c>onClick={e =&gt; &lt;Inner /&gt;}</c>) were silently dropped
        /// and never type-checked, so users got no diagnostics for typos, missing
        /// props, or invalid expressions inside those JSX subtrees.</para>
        ///
        /// <para>The block is wrapped in a <c>dynamic __uitkx_jsxattr{pos}()</c>
        /// local function (same shape as <see cref="EmitInlineExprChecks"/>) so
        /// any <c>return</c> statements in the JSX subtree return from the local
        /// function and not from <c>__uitkx_render()</c>.</para>
        /// </summary>
        private static void EmitDeferredJsxAttributeChecks(
            VirtualDocBuilder b,
            string source,
            DirectiveSet directives,
            string escapedPath,
            int srcJsxStart,
            int srcJsxEnd,
            string indent
        )
        {
            if (string.IsNullOrEmpty(source) || directives == null)
                return;
            if (srcJsxStart < 0 || srcJsxEnd <= srcJsxStart || srcJsxEnd > source.Length)
                return;

            // Compute 1-based source line of srcJsxStart.
            int srcLine = 1;
            for (int i = 0; i < srcJsxStart && i < source.Length; i++)
                if (source[i] == '\n')
                    srcLine++;

            var jsxDirectives = directives with
            {
                MarkupStartIndex = srcJsxStart,
                MarkupEndIndex = srcJsxEnd,
                MarkupStartLine = srcLine,
            };
            var diags = new List<ParseDiagnostic>();
            var nodes = UitkxParser.Parse(source, escapedPath, jsxDirectives, diags);
            if (nodes.Length == 0)
                return;

            int exprCtr = 0;
            int attrCtr = 0;
            string funcName = $"__uitkx_jsxattr{b.CurrentPos}";
            b.Scaffold("#line hidden\n");
            b.Scaffold($"{indent}{{\n");
            b.Scaffold($"{indent}    dynamic {funcName}() {{\n");
            b.Scaffold("#pragma warning disable 0162, 0219\n");
            EmitNodeExpressionsScoped(
                nodes,
                b,
                escapedPath,
                indent: indent + "        ",
                ref exprCtr,
                ref attrCtr
            );
            b.Scaffold("#pragma warning restore 0162, 0219\n");
            b.Scaffold("#pragma warning disable CS0162\n");
            b.Scaffold($"{indent}        return default!;\n");
            b.Scaffold("#pragma warning restore CS0162\n");
            b.Scaffold($"{indent}    }}\n");
            b.Scaffold($"{indent}    _ = {funcName}();\n");
            b.Scaffold($"{indent}}}\n");
        }

        /// <summary>
        /// Emits a block-statement expression check.
        /// Used inside a render method (function-style context).
        /// </summary>
        private static void EmitExpressionStatement(
            VirtualDocBuilder b,
            CollectedExpression expr,
            string escapedPath,
            string indent
        )
        {
            b.Scaffold($"#line {expr.UitkxLine} \"{escapedPath}\"\n");

            // Issue 1 (UITKX0112): collect JSX subtree ranges that get stripped
            // by EmitMappedExpressionStrippingJsx so we can type-check them in a
            // deferred Pattern-B block (preserves diagnostics on attribute-lambda
            // bodies like onClick={e => <Tag/>}). Source + directives come from
            // a thread-static context set by the top-level entry methods
            // (EmitFunctionStyleBody, EmitInlineExprChecks).
            var ctx = t_jsxAttrContext;
            List<(int SrcStart, int SrcEnd)>? deferred = ctx != null ? new List<(int, int)>() : null;

            // Lambdas (`expr =>` / `_ =>` / `(x, y) =>`) cannot be stored as
            // `object` - Roslyn emits CS1660 and a secondary "delegate type could
            // not be inferred" diagnostic.  Cast to Action / Action<dynamic> so:
            //  - Zero-arg lambdas `() => ...` -> Action (no type param, no arg mismatch)
            //  - One-arg lambdas `e => ...`   -> Action<dynamic> so e.newValue etc. compile
            //  - The lambda body is still type-checked against the surrounding scope
            if (expr.Text.Contains("=>"))
            {
                // Block-body lambdas: `dm => { dm.AppendAction(..., _ => ...) }`
                // Casting to Action<dynamic> makes the param `dynamic`, then passing
                // a nested lambda to a dynamic method call triggers CS1977 - an error
                // that cannot be pragma-suppressed.  Skip emitting these entirely so
                // no false squiggles appear; the body is not type-checked (acceptable).
                int arrowIdx = expr.Text.IndexOf("=>");
                bool isBlockBody =
                    arrowIdx >= 0 && expr.Text.Substring(arrowIdx + 2).TrimStart().StartsWith("{");
                if (isBlockBody)
                {
                    // Emit the block body with dynamic-typed parameters so the contents
                    // get completions and type-checking, while avoiding CS1977 (cannot
                    // use a lambda as argument to a dynamically-dispatched call - a
                    // compiler error that cannot be pragma-suppressed).
                    EmitBlockBodyLambda(b, expr, escapedPath, indent, arrowIdx);
                }
                else
                {
                    // () => ... is zero-arg; everything else is treated as single-arg dynamic
                    bool isZeroArg = expr.Text.TrimStart().StartsWith("()");
                    string castType = isZeroArg
                        ? "global::System.Action"
                        : "global::System.Action<dynamic>";
                    b.Scaffold($"{indent}{{ (({castType})(");
                    EmitMappedExpressionStrippingJsx(b, expr.Text, expr.UitkxOffset, expr.Kind, expr.UitkxLine, deferred);
                    b.Scaffold(")); }\n");
                }
            }
            else
            {
                // Inline expressions ({expr} in markup) are passed into __C(params object[])
                // which handles both VirtualNode and IEnumerable<VirtualNode> at runtime.
                // No VirtualNode cast is emitted by the SG, so we use object here to allow
                // both VirtualNode and IReadOnlyList<VirtualNode> (e.g. {__children}).
                // Attribute expressions also use object since their type varies.
                string checkType = "object";
                b.Scaffold($"{indent}{{ {checkType} __uitkx_{expr.Label} = (");
                EmitMappedExpressionStrippingJsx(b, expr.Text, expr.UitkxOffset, expr.Kind, expr.UitkxLine, deferred);
                b.Scaffold("); }\n");
            }

            b.Scaffold("#line hidden\n");

            // Emit deferred Pattern-B blocks for any JSX subtrees that were
            // stripped above. Done after the wrapping `{ ... }` so the deferred
            // local function lives in the surrounding render-method scope and
            // can capture the same locals the original JSX subtree would have.
            if (deferred != null && deferred.Count > 0 && ctx != null)
            {
                foreach (var (s, e) in deferred)
                    EmitDeferredJsxAttributeChecks(b, ctx.Source, ctx.Directives, escapedPath, s, e, indent);
            }
        }

        /// <summary>
        /// Emits a typed property assignment that mirrors what the source generator
        /// produces, so Roslyn can validate that the expression is assignable to
        /// the property's declared type.
        /// <para>
        /// For <c>&lt;Label text={Something} /&gt;</c> with <c>propsType="LabelProps"</c>
        /// and <c>propName="Text"</c>, this emits:
        /// <code>
        /// #line 5 "Component.uitkx"
        ///     { string __uitkx_check = (Something); }
        /// #line hidden
        /// </code>
        /// If <c>Something</c> is <c>float</c> and the expected type is
        /// <c>string</c>, Roslyn emits CS0029 - exactly matching the Unity build
        /// error.
        /// </para>
        /// <para>Uses a direct typed variable instead of a props-class assignment
        /// so that function-style components (whose Props class is only generated
        /// at Unity build time) are also type-checked.</para>
        /// </summary>
        private static void EmitTypedPropsCheck(
            VirtualDocBuilder b,
            CollectedExpression expr,
            string escapedPath,
            string indent,
            string propType
        )
        {
            b.Scaffold($"#line {expr.UitkxLine} \"{escapedPath}\"\n");

            // Suppress CS0246 (type not found) as a safety net for types that
            // may not be resolvable in the current Roslyn workspace.
            b.Scaffold("#pragma warning disable CS0246\n");

            // Scaffold: direct typed variable assignment up to the expression.
            // Props properties are nullable (int?, float?, bool? ...) so the check
            // must accept both T and T? to match what Unity's compiler sees when
            // assigning into the object-initializer (e.g. new TabViewProps { SelectedIndex = expr }).
            // For reference types under #nullable-enable, the extra -> is just an annotation.
            b.Scaffold($"{indent}{{ {propType}? __uitkx_check = (");

            // Issue 1 (UITKX0112): collect stripped JSX subtree ranges for deferred
            // type-checking - see comment in EmitExpressionStatement.
            var ctx = t_jsxAttrContext;
            List<(int SrcStart, int SrcEnd)>? deferred = ctx != null ? new List<(int, int)>() : null;

            // Mapped region: the expression text itself (source-map preserved).
            // Phase 1: any embedded JSX literals are replaced with stub
            // (VirtualNode)null! so Roslyn parses the surrounding C#
            // without seeing JSX it can't understand.
            EmitMappedExpressionStrippingJsx(b, expr.Text, expr.UitkxOffset, expr.Kind, expr.UitkxLine, deferred);

            // Scaffold: close assignment + block
            b.Scaffold("); }\n");
            b.Scaffold("#pragma warning restore CS0246\n");
            b.Scaffold("#line hidden\n");

            if (deferred != null && deferred.Count > 0 && ctx != null)
            {
                foreach (var (s, e) in deferred)
                    EmitDeferredJsxAttributeChecks(b, ctx.Source, ctx.Directives, escapedPath, s, e, indent);
            }
        }

        // -- AST expression collector ------------------------------------------

        /// <summary>
        /// Walks the entire AST and collects every C# expression that needs a
        /// type-checking wrapper in the virtual document:
        /// <list type="bullet">
        ///   <item><c>{expr}</c> - <see cref="ExpressionNode"/></item>
        ///   <item><c>attr={expr}</c> - <see cref="AttributeNode"/> with <see cref="CSharpExpressionValue"/></item>
        /// </list>
        /// Numbers each expression to produce unique method/variable names.
        /// </summary>
        private static void CollectExpressions(
            ImmutableArray<AstNode> nodes,
            List<CollectedExpression> output
        )
        {
            int exprCounter = 0;
            int attrCounter = 0;
            CollectFromNodeList(nodes, output, ref exprCounter, ref attrCounter);
        }

        private static void CollectFromNodeList(
            ImmutableArray<AstNode> nodes,
            List<CollectedExpression> output,
            ref int exprCounter,
            ref int attrCounter
        )
        {
            foreach (var node in nodes)
                CollectFromNode(node, output, ref exprCounter, ref attrCounter);
        }

        private static void CollectFromNode(
            AstNode node,
            List<CollectedExpression> output,
            ref int exprCounter,
            ref int attrCounter
        )
        {
            switch (node)
            {
                case ExpressionNode en:
                    if (!string.IsNullOrEmpty(en.Expression) && en.ExpressionOffset > 0)
                        output.Add(
                            new CollectedExpression
                            {
                                Text = en.Expression,
                                UitkxOffset = en.ExpressionOffset,
                                UitkxLine = en.SourceLine,
                                Label = $"expr_{exprCounter++}",
                                Kind = SourceRegionKind.InlineExpression,
                            }
                        );
                    else if (!string.IsNullOrEmpty(en.Expression))
                        output.Add(
                            new CollectedExpression
                            {
                                Text = en.Expression,
                                UitkxOffset = 0,
                                UitkxLine = en.SourceLine,
                                Label = $"expr_{exprCounter++}",
                                Kind = SourceRegionKind.InlineExpression,
                            }
                        );
                    break;

                case ElementNode el:
                    // Attribute expressions on this element
                    foreach (var attr in el.Attributes)
                    {
                        if (
                            attr.Value is CSharpExpressionValue cev
                            && !string.IsNullOrEmpty(cev.Expression)
                        )
                        {
                            output.Add(
                                new CollectedExpression
                                {
                                    Text = cev.Expression,
                                    UitkxOffset = cev.ExpressionOffset,
                                    UitkxLine = attr.SourceLine,
                                    Label = $"attr_{attrCounter++}_{SanitizeLabel(attr.Name)}",
                                    Kind = SourceRegionKind.AttributeExpression,
                                }
                            );
                        }
                        else if (attr.Value is JsxExpressionValue jsx && jsx.Element != null)
                        {
                            CollectFromNode(jsx.Element, output, ref exprCounter, ref attrCounter);
                        }
                    }
                    // Recurse into children
                    CollectFromNodeList(el.Children, output, ref exprCounter, ref attrCounter);
                    break;

                case IfNode ifn:
                    foreach (var branch in ifn.Branches)
                        CollectFromNodeList(branch.Payload.Body, output, ref exprCounter, ref attrCounter);
                    break;

                case ForeachNode fe:
                    CollectFromNodeList(fe.Payload.Body, output, ref exprCounter, ref attrCounter);
                    break;

                case ForNode fo:
                    CollectFromNodeList(fo.Payload.Body, output, ref exprCounter, ref attrCounter);
                    break;

                case WhileNode wh:
                    CollectFromNodeList(wh.Payload.Body, output, ref exprCounter, ref attrCounter);
                    break;

                case SwitchNode sw:
                    foreach (var sc in sw.Cases)
                        CollectFromNodeList(sc.Payload.Body, output, ref exprCounter, ref attrCounter);
                    break;
            }
        }

        // -- Scoped expression emitter -----------------------------------------

        /// <summary>
        /// Recursively walks <paramref name="nodes"/> and emits expression type-check
        /// statements, wrapping <c>@for</c> / <c>@foreach</c> / <c>@while</c> bodies
        /// in proper C# block scopes so that loop variables declared in headers are
        /// visible for attribute expressions inside the body.
        /// </summary>
        private static void EmitNodeExpressionsScoped(
            ImmutableArray<AstNode> nodes,
            VirtualDocBuilder b,
            string escapedPath,
            string indent,
            ref int exprCtr,
            ref int attrCtr,
            IPropsTypeProvider? propsTypes = null,
            int uitkxOffsetAdjust = 0
        )
        {
            foreach (var node in nodes)
                EmitNodeExpressionScoped(
                    node,
                    b,
                    escapedPath,
                    indent,
                    ref exprCtr,
                    ref attrCtr,
                    propsTypes,
                    uitkxOffsetAdjust
                );
        }

        private static void EmitNodeExpressionScoped(
            AstNode node,
            VirtualDocBuilder b,
            string escapedPath,
            string indent,
            ref int exprCtr,
            ref int attrCtr,
            IPropsTypeProvider? propsTypes = null,
            int uitkxOffsetAdjust = 0
        )
        {
            switch (node)
            {
                case ExpressionNode en:
                {
                    if (!string.IsNullOrEmpty(en.Expression))
                    {
                        var expr = new CollectedExpression
                        {
                            Text = en.Expression,
                            UitkxOffset = en.ExpressionOffset + uitkxOffsetAdjust,
                            UitkxLine = en.SourceLine,
                            Label = $"expr_{exprCtr++}",
                            Kind = SourceRegionKind.InlineExpression,
                        };
                        EmitExpressionStatement(b, expr, escapedPath, indent);
                    }
                    break;
                }

                case ElementNode el:
                    foreach (var attr in el.Attributes)
                    {
                        if (
                            attr.Value is CSharpExpressionValue cev
                            && !string.IsNullOrEmpty(cev.Expression)
                        )
                        {
                            var expr = new CollectedExpression
                            {
                                Text = cev.Expression,
                                UitkxOffset = cev.ExpressionOffset + uitkxOffsetAdjust,
                                UitkxLine = attr.SourceLine,
                                Label = $"attr_{attrCtr++}_{SanitizeLabel(attr.Name)}",
                                Kind = SourceRegionKind.AttributeExpression,
                            };

                            // Try to emit a direct typed variable for type checking.
                            // Falls back to the untyped object wrapper for:
                            //   - unknown elements / unknown attributes (propType == null)
                            //   - universal / event attributes (propType == null)
                            //   - lambda expressions (need special handling)
                            string? propType = propsTypes?.GetPropType(el.TagName, attr.Name);

                            if (propType != null && !cev.Expression.Contains("=>"))
                            {
                                EmitTypedPropsCheck(b, expr, escapedPath, indent, propType);
                            }
                            else
                            {
                                EmitExpressionStatement(b, expr, escapedPath, indent);
                            }
                        }
                        else if (attr.Value is JsxExpressionValue jsx && jsx.Element != null)
                        {
                            EmitNodeExpressionScoped(
                                jsx.Element,
                                b,
                                escapedPath,
                                indent,
                                ref exprCtr,
                                ref attrCtr,
                                propsTypes,
                                uitkxOffsetAdjust
                            );
                        }
                    }
                    EmitNodeExpressionsScoped(
                        el.Children,
                        b,
                        escapedPath,
                        indent,
                        ref exprCtr,
                        ref attrCtr,
                        propsTypes,
                        uitkxOffsetAdjust
                    );
                    break;

                case IfNode ifn:
                {
                    bool isFirstBranch = true;
                    foreach (var branch in ifn.Branches)
                    {
                        // Emit #line so Roslyn maps condition errors back to the .uitkx file
                        b.Scaffold($"#line {branch.SourceLine} \"{escapedPath}\"\n");
                        if (branch.Condition != null)
                        {
                            b.Scaffold(isFirstBranch ? $"{indent}if (" : $"{indent}else if (");
                            // Tier 3: b.Mapped gives column-accurate squiggles inside the condition
                            b.Mapped(
                                branch.Condition,
                                branch.ConditionOffset + uitkxOffsetAdjust,
                                SourceRegionKind.InlineExpression,
                                branch.SourceLine
                            );
                            b.Scaffold($") {{\n");
                        }
                        else
                        {
                            b.Scaffold($"{indent}else {{\n");
                        }
                        b.Scaffold("#line hidden\n");
                        if (branch.Payload.BodyCode != null)
                        {
                            EmitDirectiveBodyCode(
                                b,
                                branch.Payload.BodyCode,
                                branch.Payload.BodyCodeOffset,
                                branch.Payload.BodyCodeLine,
                                branch.Payload.BodyMarkupRanges,
                                branch.Payload.BodyBareJsxRanges,
                                escapedPath,
                                indent + "    ",
                                ref exprCtr,
                                ref attrCtr,
                                propsTypes,
                                uitkxOffsetAdjust
                            );
                        }
                        b.Scaffold($"{indent}}}\n");
                        isFirstBranch = false;
                    }
                    break;
                }

                case ForeachNode fe:
                    // Tier 1: #line maps errors in the foreach header to the .uitkx line
                    b.Scaffold($"#line {fe.SourceLine} \"{escapedPath}\"\n");
                    b.Scaffold($"{indent}foreach (");
                    // Tier 3: b.Mapped gives column-accurate squiggles for the iterator expression
                    b.Mapped(
                        fe.ForeachExpression,
                        fe.ForeachExpressionOffset + uitkxOffsetAdjust,
                        SourceRegionKind.InlineExpression,
                        fe.SourceLine
                    );
                    b.Scaffold($") {{\n");
                    b.Scaffold("#line hidden\n");
                    if (fe.Payload.BodyCode != null)
                    {
                        EmitDirectiveBodyCode(
                            b,
                            fe.Payload.BodyCode,
                            fe.Payload.BodyCodeOffset,
                            fe.Payload.BodyCodeLine,
                            fe.Payload.BodyMarkupRanges,
                            fe.Payload.BodyBareJsxRanges,
                            escapedPath,
                            indent + "    ",
                            ref exprCtr,
                            ref attrCtr,
                            propsTypes,
                            uitkxOffsetAdjust
                        );
                    }
                    b.Scaffold($"{indent}}}\n");
                    break;

                case ForNode fo:
                    // Tier 1: #line maps errors in the for header to the .uitkx line
                    b.Scaffold($"#line {fo.SourceLine} \"{escapedPath}\"\n");
                    b.Scaffold($"{indent}for (");
                    // Tier 3: b.Mapped gives column-accurate squiggles for the for expression
                    b.Mapped(
                        fo.ForExpression,
                        fo.ForExpressionOffset + uitkxOffsetAdjust,
                        SourceRegionKind.InlineExpression,
                        fo.SourceLine
                    );
                    b.Scaffold($") {{\n");
                    b.Scaffold("#line hidden\n");
                    if (fo.Payload.BodyCode != null)
                    {
                        EmitDirectiveBodyCode(
                            b,
                            fo.Payload.BodyCode,
                            fo.Payload.BodyCodeOffset,
                            fo.Payload.BodyCodeLine,
                            fo.Payload.BodyMarkupRanges,
                            fo.Payload.BodyBareJsxRanges,
                            escapedPath,
                            indent + "    ",
                            ref exprCtr,
                            ref attrCtr,
                            propsTypes,
                            uitkxOffsetAdjust
                        );
                    }
                    b.Scaffold($"{indent}}}\n");
                    break;

                case WhileNode wh:
                    // Tier 1: #line maps errors in the while header to the .uitkx line
                    b.Scaffold($"#line {wh.SourceLine} \"{escapedPath}\"\n");
                    b.Scaffold($"{indent}while (");
                    // Tier 3: b.Mapped gives column-accurate squiggles for the condition
                    b.Mapped(
                        wh.Condition,
                        wh.ConditionOffset + uitkxOffsetAdjust,
                        SourceRegionKind.InlineExpression,
                        wh.SourceLine
                    );
                    b.Scaffold($") {{\n");
                    b.Scaffold("#line hidden\n");
                    if (wh.Payload.BodyCode != null)
                    {
                        EmitDirectiveBodyCode(
                            b,
                            wh.Payload.BodyCode,
                            wh.Payload.BodyCodeOffset,
                            wh.Payload.BodyCodeLine,
                            wh.Payload.BodyMarkupRanges,
                            wh.Payload.BodyBareJsxRanges,
                            escapedPath,
                            indent + "    ",
                            ref exprCtr,
                            ref attrCtr,
                            propsTypes,
                            uitkxOffsetAdjust
                        );
                    }
                    b.Scaffold($"{indent}}}\n");
                    break;

                case SwitchNode sw:
                    // U-33: the switch expression itself was never emitted into the
                    // virtual document, so it had no Roslyn-backed hover/diagnostics/
                    // go-to-def in the editor. U-33 follow-up: the first cut emitted case
                    // bodies as flat, sequential top-level statements (a discard statement
                    // for the switch expression, then each case's body one after another,
                    // each wrapped in `if (true) { }` for "isolation"). That doesn't work —
                    // the C# spec special-cases a constant `if (true)` condition for
                    // reachability analysis (same as `while (true)`), so Roslyn still saw
                    // the FIRST case's `return` as unconditional and flagged every later
                    // case's body as unreachable (CS0162 -> dimmed in the editor). Emitting
                    // a REAL `switch (expr) { case ...: { ... } }` — mirroring the real
                    // compiled output in CSharpEmitter.EmitSwitchNode exactly, including no
                    // synthesized `break` (case bodies are required to end in their own
                    // return/break already, same as the real emitter assumes) — gives
                    // Roslyn genuine mutually-exclusive branches, so its reachability
                    // analysis is correct instead of needing to be tricked.
                    b.Scaffold($"#line {sw.SourceLine} \"{escapedPath}\"\n");
                    b.Scaffold($"{indent}switch (");
                    b.Mapped(
                        sw.SwitchExpression,
                        sw.SwitchExpressionOffset + uitkxOffsetAdjust,
                        SourceRegionKind.InlineExpression,
                        sw.SourceLine
                    );
                    b.Scaffold($")\n{indent}{{\n");
                    b.Scaffold("#line hidden\n");
                    foreach (var sc in sw.Cases)
                    {
                        b.Scaffold(
                            sc.ValueExpression == null
                                ? $"{indent}default:\n"
                                : $"{indent}case {sc.ValueExpression}:\n"
                        );
                        b.Scaffold($"{indent}{{\n");
                        if (sc.Payload.BodyCode != null)
                        {
                            EmitDirectiveBodyCode(
                                b,
                                sc.Payload.BodyCode,
                                sc.Payload.BodyCodeOffset,
                                sc.Payload.BodyCodeLine,
                                sc.Payload.BodyMarkupRanges,
                                sc.Payload.BodyBareJsxRanges,
                                escapedPath,
                                indent + "    ",
                                ref exprCtr,
                                ref attrCtr,
                                propsTypes,
                                uitkxOffsetAdjust
                            );
                        }
                        b.Scaffold($"{indent}}}\n");
                    }
                    b.Scaffold($"{indent}}}\n");
                    break;
            }
        }

        // -- Directive body code emitter ---------------------------------------

        /// <summary>
        /// Emits directive body code as mapped C# segments, replacing JSX ranges
        /// with valid C# placeholders. A simpler, gap-free variant of
        /// <see cref="EmitFunctionStyleSetupSegmented"/>.
        /// </summary>
        private static void EmitDirectiveBodyCode(
            VirtualDocBuilder b,
            string bodyCode,
            int bodyCodeOffset,
            int bodyCodeLine,
            ImmutableArray<(int Start, int End, int Line)> markupRanges,
            ImmutableArray<(int Start, int End, int Line)> bareRanges,
            string escapedPath,
            string indent,
            ref int exprCtr,
            ref int attrCtr,
            IPropsTypeProvider? propsTypes,
            int uitkxOffsetAdjust = 0
        )
        {
            bool hasMarkup = !markupRanges.IsDefaultOrEmpty;
            bool hasBare = !bareRanges.IsDefaultOrEmpty;

            if (!hasMarkup && !hasBare)
            {
                // No JSX - emit entire body as a single mapped region
                b.Scaffold($"#line {bodyCodeLine} \"{escapedPath}\"\n");
                b.Mapped(
                    bodyCode,
                    bodyCodeOffset + uitkxOffsetAdjust,
                    SourceRegionKind.CodeBlock,
                    bodyCodeLine
                );
                b.Scaffold("\n#line hidden\n");
                return;
            }

            // Merge and sort all JSX ranges by absolute start position
            var allRanges = new List<(int Start, int End, int Line)>();
            if (hasMarkup)
                foreach (var r in markupRanges)
                    allRanges.Add(r);
            if (hasBare)
                foreach (var r in bareRanges)
                    allRanges.Add(r);
            allRanges.Sort((a, c) => a.Start.CompareTo(c.Start));

            int prev = 0; // relative offset within bodyCode

            // Collect JSX ranges that need expression checks so we can emit
            // them AFTER the entire body code (including the tail that closes
            // any "return (...)" expression).  Emitting checks inline between
            // the placeholder and the closing ")" inserts C# statements inside
            // an expression context, which confuses Roslyn's parser and causes
            // spurious "Invalid token ')'" and "name does not exist" errors.
            var deferredChecks = new List<(int AbsStart, int AbsEnd)>();

            foreach (var (absStart, absEnd, jsxLine) in allRanges)
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

                // Emit C# text before this JSX range
                if (relStart > prev)
                {
                    string seg = bodyCode.Substring(prev, relStart - prev);
                    int segAbsOffset = bodyCodeOffset + prev;
                    int segLine = CountLinesUpTo(bodyCode, prev, bodyCodeLine);
                    b.Scaffold($"#line {segLine} \"{escapedPath}\"\n");
                    b.Mapped(
                        seg,
                        segAbsOffset + uitkxOffsetAdjust,
                        SourceRegionKind.CodeBlock,
                        segLine
                    );
                    b.Scaffold("\n");
                }

                // Replace JSX with a C# placeholder
                b.Scaffold("#line hidden\n");
                b.Scaffold($"{indent}(global::Ruitk.Core.VirtualNode)null!\n");

                // Defer expression checks - see comment on deferredChecks above.
                deferredChecks.Add((absStart, absEnd));

                prev = relEnd;
            }

            // Emit remaining C# after last JSX range
            if (prev < bodyCode.Length)
            {
                string tail = bodyCode.Substring(prev);
                int tailAbsOffset = bodyCodeOffset + prev;
                int tailLine = CountLinesUpTo(bodyCode, prev, bodyCodeLine);
                b.Scaffold($"#line {tailLine} \"{escapedPath}\"\n");
                b.Mapped(
                    tail,
                    tailAbsOffset + uitkxOffsetAdjust,
                    SourceRegionKind.CodeBlock,
                    tailLine
                );
                b.Scaffold("\n#line hidden\n");
            }
            else
            {
                b.Scaffold("#line hidden\n");
            }

            // Now emit all deferred expression checks.  At this point the
            // "return (...)" statement (if any) is already closed, so all
            // for/foreach/if blocks and attribute-check statements are valid
            // C# at the current block scope.  The preceding "return (null!);"
            // makes these checks unreachable; CS0162 ("unreachable code") is
            // already suppressed by the surrounding #pragma warning disable 0162.
            if (deferredChecks.Count > 0)
            {
                b.Scaffold("#pragma warning disable 0162\n");
                foreach (var (absStart, absEnd) in deferredChecks)
                {
                    EmitDirectiveJsxExprChecks(
                        b,
                        bodyCode,
                        bodyCodeOffset,
                        bodyCodeLine,
                        absStart,
                        absEnd,
                        escapedPath,
                        indent,
                        ref exprCtr,
                        ref attrCtr,
                        propsTypes,
                        uitkxOffsetAdjust
                    );
                }
                b.Scaffold("#pragma warning restore 0162\n");
            }
        }

        /// <summary>
        /// Parses a JSX range within directive body code and emits expression
        /// type checks for attributes and inline expressions.
        /// </summary>
        private static void EmitDirectiveJsxExprChecks(
            VirtualDocBuilder b,
            string bodyCode,
            int bodyCodeOffset,
            int bodyCodeLine,
            int absJsxStart,
            int absJsxEnd,
            string escapedPath,
            string indent,
            ref int exprCtr,
            ref int attrCtr,
            IPropsTypeProvider? propsTypes,
            int uitkxOffsetAdjust = 0
        )
        {
            // Extract JSX text from bodyCode
            int relStart = absJsxStart - bodyCodeOffset;
            int relEnd = absJsxEnd - bodyCodeOffset;
            if (relStart < 0 || relEnd > bodyCode.Length || relEnd <= relStart)
                return;

            string jsxText = bodyCode.Substring(relStart, relEnd - relStart);

            // Compute the 1-based source line where this JSX range starts
            int srcLine = CountLinesUpTo(bodyCode, relStart, bodyCodeLine);

            // Parse the JSX text
            var jsxDirectives = new DirectiveSet(
                Namespace: null,
                ComponentName: null,
                PropsTypeName: null,
                DefaultKey: null,
                Usings: ImmutableArray<string>.Empty,
                UssFiles: ImmutableArray<string>.Empty,
                Injects: ImmutableArray<(string, string)>.Empty,
                MarkupStartLine: srcLine,
                MarkupStartIndex: 0,
                MarkupEndIndex: jsxText.Length
            );
            var diags = new List<ParseDiagnostic>();
            var nodes = UitkxParser.Parse(
                jsxText,
                escapedPath,
                jsxDirectives,
                diags,
                lineOffset: srcLine - 1
            );

            if (nodes.Length > 0)
            {
                // The parser produced nodes with offsets relative to jsxText
                // (0-based). To create correct source map entries, we need
                // absolute offsets in the original .uitkx source. The jsxText
                // starts at position absJsxStart in the parent parse source.
                // Adding the accumulated uitkxOffsetAdjust converts to absolute.
                int newAdjust = uitkxOffsetAdjust + absJsxStart;

                b.Scaffold("#pragma warning disable 0162\n");
                EmitNodeExpressionsScoped(
                    nodes,
                    b,
                    escapedPath,
                    indent,
                    ref exprCtr,
                    ref attrCtr,
                    propsTypes,
                    newAdjust
                );
                b.Scaffold("#pragma warning restore 0162\n");
            }
        }

        /// <summary>
        /// Counts newlines in <paramref name="text"/> from index 0 up to
        /// (exclusive) <paramref name="upTo"/> and returns the resulting
        /// line number starting from <paramref name="startLine"/>.
        /// </summary>
        private static int CountLinesUpTo(string text, int upTo, int startLine)
        {
            int line = startLine;
            int end = Math.Min(upTo, text.Length);
            for (int i = 0; i < end; i++)
                if (text[i] == '\n')
                    line++;
            return line;
        }

        // -- Utility helpers ---------------------------------------------------

        /// <summary>
        /// Returns the 0-based character offset of the start of <paramref name="line1"/>
        /// (1-based) within <paramref name="source"/>.  Returns 0 when out of range.
        /// </summary>
        private static int OffsetOfLine(string source, int line1)
        {
            if (line1 <= 1)
                return 0;

            int current = 1;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                {
                    current++;
                    if (current == line1)
                        return i + 1;
                }
            }
            return 0;
        }

        /// <summary>
        /// Strips characters from an attribute name that are not valid in a C#
        /// method name suffix (e.g. hyphens in <c>data-id</c>).
        /// </summary>
        private static string SanitizeLabel(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        /// <summary>
        /// Escapes backslashes in a file path for use inside a C# string literal
        /// (used in <c>#line N "path"</c> directives).
        /// </summary>
        private static string EscapePathForLineDirective(string path) => path.Replace("\\", "\\\\");

        /// <summary>Strips newlines from a string for safe inclusion in a // comment.</summary>
        private static string EscapeForComment(string text) =>
            text.Replace('\r', ' ').Replace('\n', ' ');

        // -- Block-body lambda emitter -----------------------------------------

        /// <summary>
        /// Maps sanitised attribute names (as they appear in a <c>CollectedExpression.Label</c>)
        /// to the fully-qualified C# type of their single callback parameter.
        ///
        /// <para>When a block-body attribute lambda has exactly ONE named parameter and
        /// the attribute name is in this table, the parameter is declared with the specific
        /// event type instead of <c>dynamic</c>, so that member completions (e.g.
        /// <c>.newValue</c>, <c>.button</c>) are available in the editor.</para>
        ///
        /// <para><c>ChangeEvent&lt;dynamic&gt;</c> is used for change-event callbacks
        /// because the concrete value type depends on the host element and is not
        /// available at virtual-doc generation time.  The <c>dynamic</c> type arg still
        /// exposes <c>.newValue</c> and <c>.previousValue</c> to Roslyn's completion
        /// engine.</para>
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<
            string,
            string
        > s_eventCallbackParamTypes = new System.Collections.Generic.Dictionary<string, string>(
            System.StringComparer.Ordinal
        )
        {
            // -- Value-change events --------------------------------------------
            ["onChange"] = "global::UnityEngine.UIElements.ChangeEvent<dynamic>",
            ["onValueChanged"] = "global::UnityEngine.UIElements.ChangeEvent<dynamic>",
            // -- Click / Pointer -----------------------------------------------
            ["onClick"] = "global::UnityEngine.UIElements.ClickEvent",
            ["onPointerDown"] = "global::UnityEngine.UIElements.PointerDownEvent",
            ["onPointerUp"] = "global::UnityEngine.UIElements.PointerUpEvent",
            ["onPointerMove"] = "global::UnityEngine.UIElements.PointerMoveEvent",
            ["onPointerEnter"] = "global::UnityEngine.UIElements.PointerEnterEvent",
            ["onPointerLeave"] = "global::UnityEngine.UIElements.PointerLeaveEvent",
            ["onPointerCancel"] = "global::UnityEngine.UIElements.PointerCancelEvent",
            // -- Mouse ---------------------------------------------------------
            ["onMouseDown"] = "global::UnityEngine.UIElements.MouseDownEvent",
            ["onMouseUp"] = "global::UnityEngine.UIElements.MouseUpEvent",
            ["onMouseMove"] = "global::UnityEngine.UIElements.MouseMoveEvent",
            ["onMouseEnter"] = "global::UnityEngine.UIElements.MouseEnterEvent",
            ["onMouseLeave"] = "global::UnityEngine.UIElements.MouseLeaveEvent",
            ["onMouseOut"] = "global::UnityEngine.UIElements.MouseOutEvent",
            ["onMouseOver"] = "global::UnityEngine.UIElements.MouseOverEvent",
            ["onMouseCaptureOut"] = "global::UnityEngine.UIElements.MouseCaptureOutEvent",
            ["onContextClick"] = "global::UnityEngine.UIElements.ContextClickEvent",
            // -- Keyboard ------------------------------------------------------
            ["onKeyDown"] = "global::UnityEngine.UIElements.KeyDownEvent",
            ["onKeyUp"] = "global::UnityEngine.UIElements.KeyUpEvent",
            // -- Focus ---------------------------------------------------------
            ["onFocus"] = "global::UnityEngine.UIElements.FocusEvent",
            ["onFocusIn"] = "global::UnityEngine.UIElements.FocusInEvent",
            ["onFocusOut"] = "global::UnityEngine.UIElements.FocusOutEvent",
            ["onBlur"] = "global::UnityEngine.UIElements.BlurEvent",
            // -- Geometry / Style ----------------------------------------------
            ["onGeometryChanged"] = "global::UnityEngine.UIElements.GeometryChangedEvent",
            ["onCustomStyleResolved"] = "global::UnityEngine.UIElements.CustomStyleResolvedEvent",
            // -- Panel lifecycle -----------------------------------------------
            ["onAttachToPanel"] = "global::UnityEngine.UIElements.AttachToPanelEvent",
            ["onDetachFromPanel"] = "global::UnityEngine.UIElements.DetachFromPanelEvent",
            // -- Custom rendering ----------------------------------------------
            ["onGenerateVisualContent"] =
                "global::UnityEngine.UIElements.MeshGenerationContext",
            // -- Navigation ----------------------------------------------------
            ["onNavigationMove"] = "global::UnityEngine.UIElements.NavigationMoveEvent",
            ["onNavigationSubmit"] = "global::UnityEngine.UIElements.NavigationSubmitEvent",
            ["onNavigationCancel"] = "global::UnityEngine.UIElements.NavigationCancelEvent",
            // -- Drag-and-drop -------------------------------------------------
            ["onDragEnter"] = "global::UnityEngine.UIElements.DragEnterEvent",
            ["onDragLeave"] = "global::UnityEngine.UIElements.DragLeaveEvent",
            ["onDragUpdated"] = "global::UnityEngine.UIElements.DragUpdatedEvent",
            ["onDragPerform"] = "global::UnityEngine.UIElements.DragPerformEvent",
            ["onDragExited"] = "global::UnityEngine.UIElements.DragExitedEvent",
            // -- Input / Commands ----------------------------------------------
            ["onInput"] = "global::UnityEngine.UIElements.InputEvent",
            ["onExecuteCommand"] = "global::UnityEngine.UIElements.ExecuteCommandEvent",
            ["onValidateCommand"] = "global::UnityEngine.UIElements.ValidateCommandEvent",
            ["onTooltip"] = "global::UnityEngine.UIElements.TooltipEvent",
            // -- Capture-phase variants ----------------------------------------
            ["onClickCapture"] = "global::UnityEngine.UIElements.ClickEvent",
            ["onPointerDownCapture"] = "global::UnityEngine.UIElements.PointerDownEvent",
            ["onPointerUpCapture"] = "global::UnityEngine.UIElements.PointerUpEvent",
            ["onPointerMoveCapture"] = "global::UnityEngine.UIElements.PointerMoveEvent",
            ["onPointerEnterCapture"] = "global::UnityEngine.UIElements.PointerEnterEvent",
            ["onPointerLeaveCapture"] = "global::UnityEngine.UIElements.PointerLeaveEvent",
            ["onWheelCapture"] = "global::UnityEngine.UIElements.WheelEvent",
            ["onScrollCapture"] = "global::UnityEngine.UIElements.WheelEvent",
            ["onDragEnterCapture"] = "global::UnityEngine.UIElements.DragEnterEvent",
            ["onDragLeaveCapture"] = "global::UnityEngine.UIElements.DragLeaveEvent",
            ["onDragUpdatedCapture"] = "global::UnityEngine.UIElements.DragUpdatedEvent",
            ["onDragPerformCapture"] = "global::UnityEngine.UIElements.DragPerformEvent",
            ["onDragExitedCapture"] = "global::UnityEngine.UIElements.DragExitedEvent",
            ["onFocusCapture"] = "global::UnityEngine.UIElements.FocusEvent",
            ["onBlurCapture"] = "global::UnityEngine.UIElements.BlurEvent",
            ["onFocusInCapture"] = "global::UnityEngine.UIElements.FocusInEvent",
            ["onFocusOutCapture"] = "global::UnityEngine.UIElements.FocusOutEvent",
            ["onKeyDownCapture"] = "global::UnityEngine.UIElements.KeyDownEvent",
            ["onKeyUpCapture"] = "global::UnityEngine.UIElements.KeyUpEvent",
            ["onInputCapture"] = "global::UnityEngine.UIElements.InputEvent",
            ["onChangeCapture"] = "global::UnityEngine.UIElements.ChangeEvent<dynamic>",
        };

        /// <summary>
        /// Emits a block-body lambda attribute-expression as a local C# function so that
        /// <c>return</c> statements inside the body are valid (they return from the local
        /// function, not from the enclosing <c>__uitkx_render()</c>).
        ///
        /// <para>For known UIElements callback attributes (e.g. <c>onChange</c>,
        /// <c>onClick</c>) the single parameter is declared with the specific event type
        /// so that member completions like <c>.newValue</c> are available.  Unknown or
        /// multi-parameter lambdas fall back to <c>dynamic</c>.</para>
        /// </summary>
        private static void EmitBlockBodyLambda(
            VirtualDocBuilder b,
            CollectedExpression expr,
            string escapedPath,
            string indent,
            int arrowIdx
        )
        {
            // -- 1. Extract parameter names ------------------------------------
            // Everything before '=>', stripped of whitespace and outer parens.
            string paramPart = expr.Text.Substring(0, arrowIdx).Trim();
            if (paramPart.StartsWith("(") && paramPart.EndsWith(")"))
                paramPart = paramPart.Substring(1, paramPart.Length - 2).Trim();

            // Collect the valid (non-discard) parameter names.
            var paramNames = new System.Collections.Generic.List<string>();
            foreach (string rawParam in paramPart.Split(','))
            {
                string p = rawParam.Trim();
                if (IsValidCSharpIdentifier(p))
                    paramNames.Add(p);
            }

            // -- 2. Locate the opening brace of the block body  ----------------
            int afterArrow = arrowIdx + 2;
            while (
                afterArrow < expr.Text.Length
                && (
                    expr.Text[afterArrow] == ' '
                    || expr.Text[afterArrow] == '\t'
                    || expr.Text[afterArrow] == '\r'
                    || expr.Text[afterArrow] == '\n'
                )
            )
                afterArrow++;

            if (afterArrow >= expr.Text.Length || expr.Text[afterArrow] != '{')
            {
                b.Scaffold($"{indent}// (block-body lambda: could not locate opening brace)\n");
                return;
            }

            // -- 3. Find the balanced closing brace ----------------------------
            int bodyStart = afterArrow + 1; // first character after '{'
            int depth = 1;
            int k = bodyStart;
            while (k < expr.Text.Length && depth > 0)
            {
                char c = expr.Text[k];
                if (c == '{')
                    depth++;
                else if (c == '}')
                    depth--;
                if (depth > 0)
                    k++;
                else
                    break;
            }
            // expr.Text[bodyStart..k) is the body content; k points at the closing '}'
            string bodyText = k > bodyStart ? expr.Text.Substring(bodyStart, k - bodyStart) : "";
            int bodyUitkxOffset = expr.UitkxOffset + bodyStart;

            // -- 4. Determine callback parameter type -------------------------
            // For single-parameter lambdas on known UIElements event attributes,
            // use the actual event type so member completions (evt.newValue etc.)
            // are available.  Multi-param or unknown-attr lambdas use dynamic.
            string paramCSharpType = "dynamic";
            if (paramNames.Count == 1)
            {
                string attrName = GetAttrNameFromLabel(expr.Label);
                if (
                    !string.IsNullOrEmpty(attrName)
                    && s_eventCallbackParamTypes.TryGetValue(attrName, out string? evtType)
                )
                    paramCSharpType = evtType!;
            }

            // -- 5. Emit as a local function so 'return' inside the body is valid --
            // A bare scoped block `{ return -; }` is illegal C# because `return`
            // targets the enclosing __uitkx_render() method whose type may not match.
            // A local function can return anything (`dynamic`) from its own scope.
            string funcName = $"__uitkx_h{b.CurrentPos}";
            b.Scaffold($"{indent}{{\n");
            b.Scaffold($"{indent}    dynamic {funcName}() {{\n");

            // CS0246: event parameter types (ClickEvent, ChangeEvent<dynamic>, etc.)
            // reference Unity UIElements which may not be in the Roslyn references yet.
            if (paramNames.Count > 0)
                b.Scaffold("#pragma warning disable CS0246\n");
            foreach (string pName in paramNames)
                b.Scaffold($"{indent}        {paramCSharpType} {pName} = default!;\n");
            if (paramNames.Count > 0)
                b.Scaffold("#pragma warning restore CS0246\n");

            // -- 6. Map the body text verbatim ---------------------------------
            // Bare `return;` (no value) in a dynamic-returning local function is a
            // hard compiler error (CS0126).  Since #pragma warning disable cannot
            // suppress actual errors, we emit the body in segments - replacing each
            // standalone `return;` with `return default!;` as scaffolded text so the
            // rest of the body retains full source-map fidelity for completions/hover.
            if (!string.IsNullOrWhiteSpace(bodyText))
            {
                EmitBodyWithReturnFix(
                    b,
                    bodyText,
                    bodyUitkxOffset,
                    expr.Kind,
                    expr.UitkxLine,
                    escapedPath
                );
            }

            // Sentinel `return default!` suppresses CS0161 (not all paths return).
            // CS0162 suppression handles the unreachable-code case when the body
            // always returns explicitly.
            b.Scaffold($"#pragma warning disable CS0162\n");
            b.Scaffold($"{indent}        return default!;\n");
            b.Scaffold($"#pragma warning restore CS0162\n");
            b.Scaffold($"{indent}    }}\n");
            b.Scaffold($"{indent}    _ = {funcName}();\n");
            b.Scaffold($"{indent}}}\n");
        }

        /// <summary>
        /// Extracts the attribute name from a <c>CollectedExpression.Label</c>.
        /// Labels follow the format <c>attr_{counter}_{sanitizedAttrName}</c>.
        /// </summary>
        private static string GetAttrNameFromLabel(string label)
        {
            // Skip the "attr" prefix and the numeric counter, take the rest.
            int first = label.IndexOf('_');
            if (first < 0)
                return string.Empty;
            int second = label.IndexOf('_', first + 1);
            if (second < 0 || second + 1 >= label.Length)
                return string.Empty;
            return label.Substring(second + 1);
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="s"/> is a valid single C# identifier
        /// (starts with a letter or underscore, rest alphanumeric/underscore) and is NOT
        /// the conventional discard placeholder <c>_</c>.
        /// </summary>
        private static bool IsValidCSharpIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "_")
                return false;
            if (!char.IsLetter(s[0]) && s[0] != '_')
                return false;
            foreach (char c in s)
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;
            return true;
        }

        // -- JSX-stripping setup-code emitter ----------------------------------

        /// <summary>
        /// Emits the function-style setup code in segments, replacing any
        /// parenthesised JSX blocks - i.e. <c>(<Tag ...>...</Tag>)</c> or
        /// <c>(<Tag .../> )</c> - with the scaffold placeholder <c>(null!)</c>.
        ///
        /// <para>JSX paren blocks produce invalid C# when placed verbatim inside
        /// the generated <c>__uitkx_render()</c> method, causing cascading Roslyn
        /// errors.  By replacing them with <c>(null!)</c> and padding with the
        /// same number of newlines, both Roslyn's <c>#line</c> tracking and the
        /// internal source map stay accurate for all surrounding C# code.</para>
        /// </summary>
        private static void EmitFunctionStyleSetupSegmented(
            VirtualDocBuilder b,
            string setupCode,
            int uitkxSetupStartOffset,
            int uitkxSetupStartLine,
            string escapedPath,
            int gapOffset,
            int gapLength,
            int gapNewlines,
            string source,
            DirectiveSet directives,
            ref int exprCtr,
            ref int attrCtr
        )
        {
            int segStart = 0;
            int currentLine = uitkxSetupStartLine;
            int i = 0;

            // JSX ranges whose expression checks cannot be emitted inline
            // at the replacement site (ternary, arrow, assignment, paren-
            // wrapped).  They are flushed at the first statement boundary
            // (';' at paren-depth 0) in the NEXT C# segment, using
            // EmitSegmentAndFlushPending which splits the segment so checks
            // land in the correct lexical scope without breaking expression
            // contexts.
            var pendingExprChecks = new List<(int jsxStart, int jsxEnd)>();

            while (i < setupCode.Length)
            {
                // -- Skip comments so branches never fire inside them -----------
                // Block comment /* ... */
                if (setupCode[i] == '/' && i + 1 < setupCode.Length && setupCode[i + 1] == '*')
                {
                    int end = setupCode.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end >= 0 ? end + 2 : setupCode.Length;
                    continue;
                }
                // Line comment // ...
                if (setupCode[i] == '/' && i + 1 < setupCode.Length && setupCode[i + 1] == '/')
                {
                    int end = setupCode.IndexOf('\n', i + 2);
                    i = end >= 0 ? end + 1 : setupCode.Length;
                    continue;
                }

                // Skip string and char literals so branches never fire inside them
                {
                    int before = i;
                    if (
                        DirectiveParser.TrySkipStringOrCharLiteral(
                            setupCode,
                            setupCode.Length,
                            ref i
                        )
                    )
                        continue;
                }

                // -- Branch 0: return <Tag  (bare return with inline markup) ----
                // Handles `return <Label text="..." />;` without wrapping parens.
                if (
                    setupCode[i] == 'r'
                    && i + 5 < setupCode.Length
                    && setupCode.Substring(i, 6) == "return"
                    && (
                        i == 0 || !char.IsLetterOrDigit(setupCode[i - 1]) && setupCode[i - 1] != '_'
                    )
                    && (
                        i + 6 >= setupCode.Length
                        || !char.IsLetterOrDigit(setupCode[i + 6]) && setupCode[i + 6] != '_'
                    )
                )
                {
                    int peek0 = i + 6;
                    while (
                        peek0 < setupCode.Length
                        && (
                            setupCode[peek0] == ' '
                            || setupCode[peek0] == '\t'
                            || setupCode[peek0] == '\r'
                            || setupCode[peek0] == '\n'
                        )
                    )
                        peek0++;

                    if (
                        peek0 < setupCode.Length
                        && setupCode[peek0] == '<'
                        && peek0 + 1 < setupCode.Length
                        && char.IsLetter(setupCode[peek0 + 1])
                    )
                    {
                        int jsxStart = peek0;
                        if (i > segStart)
                        {
                            string seg = setupCode.Substring(segStart, i - segStart);
                            EmitSegmentAndFlushPending(
                                b,
                                seg,
                                segStart,
                                uitkxSetupStartOffset,
                                gapOffset,
                                gapLength,
                                gapNewlines,
                                ref currentLine,
                                escapedPath,
                                pendingExprChecks,
                                source,
                                directives,
                                ref exprCtr,
                                ref attrCtr
                            );
                        }

                        int jsxEnd = FindJsxElementEnd(setupCode, jsxStart, setupCode.Length);

                        // Consume trailing whitespace + `;` so the replacement
                        // forms a complete statement before expression checks.
                        int afterJsx = jsxEnd;
                        while (
                            afterJsx < setupCode.Length
                            && (setupCode[afterJsx] == ' ' || setupCode[afterJsx] == '\t')
                        )
                            afterJsx++;
                        bool hasSemicolon =
                            afterJsx < setupCode.Length && setupCode[afterJsx] == ';';
                        int consumeEnd = hasSemicolon ? afterJsx + 1 : jsxEnd;

                        int jsxNewlines = CountNewlines(setupCode, jsxStart, consumeEnd);
                        // Emit expression checks BEFORE the return so they are
                        // reachable and don't trigger CS0162 dimming.
                        EmitInlineExprChecks(
                            b,
                            source,
                            directives,
                            escapedPath,
                            jsxStart,
                            jsxEnd,
                            uitkxSetupStartOffset,
                            gapOffset,
                            gapLength,
                            ref exprCtr,
                            ref attrCtr
                        );
                        b.Scaffold($"#line {currentLine} \"{escapedPath}\"\n");
                        b.Scaffold("return (global::Ruitk.Core.VirtualNode)null!");
                        if (hasSemicolon)
                            b.Scaffold(";");
                        b.Scaffold("\n");
                        for (int k = 0; k < jsxNewlines; k++)
                            b.Scaffold("\n");

                        currentLine += jsxNewlines;
                        segStart = consumeEnd;
                        i = consumeEnd;
                        continue;
                    }
                }

                // -- Branch 0b: ? <Tag  or  : <Tag  (ternary branches) ----------
                // Handles `cond ? <Label .../> : <Other .../>;` without parens.
                // Excluded: ?. (null-conditional), ?? (null-coalescing), :: (scope)
                if (setupCode[i] == '?' || setupCode[i] == ':')
                {
                    char ch = setupCode[i];
                    bool valid = true;
                    if (
                        ch == '?'
                        && i + 1 < setupCode.Length
                        && (setupCode[i + 1] == '.' || setupCode[i + 1] == '?')
                    )
                        valid = false;
                    if (ch == ':' && i + 1 < setupCode.Length && setupCode[i + 1] == ':')
                        valid = false;

                    if (valid)
                    {
                        int peek0t = i + 1;
                        while (
                            peek0t < setupCode.Length
                            && (
                                setupCode[peek0t] == ' '
                                || setupCode[peek0t] == '\t'
                                || setupCode[peek0t] == '\r'
                                || setupCode[peek0t] == '\n'
                            )
                        )
                            peek0t++;

                        if (
                            peek0t < setupCode.Length
                            && setupCode[peek0t] == '<'
                            && peek0t + 1 < setupCode.Length
                            && char.IsLetter(setupCode[peek0t + 1])
                        )
                        {
                            int jsxStart = peek0t;
                            // Emit everything up to and including '?' or ':'
                            int emitUpTo = i + 1;
                            if (emitUpTo > segStart)
                            {
                                string seg = setupCode.Substring(segStart, emitUpTo - segStart);
                                EmitSegmentAndFlushPending(
                                    b,
                                    seg,
                                    segStart,
                                    uitkxSetupStartOffset,
                                    gapOffset,
                                    gapLength,
                                    gapNewlines,
                                    ref currentLine,
                                    escapedPath,
                                    pendingExprChecks,
                                    source,
                                    directives,
                                    ref exprCtr,
                                    ref attrCtr
                                );
                            }

                            int jsxEnd = FindJsxElementEnd(setupCode, jsxStart, setupCode.Length);

                            int jsxNewlines = CountNewlines(setupCode, jsxStart, jsxEnd);
                            b.Scaffold($"#line {currentLine} \"{escapedPath}\"\n");
                            b.Scaffold(" (global::Ruitk.Core.VirtualNode)null!\n");
                            for (int k = 0; k < jsxNewlines; k++)
                                b.Scaffold("\n");
                            pendingExprChecks.Add((jsxStart, jsxEnd));

                            currentLine += jsxNewlines;
                            segStart = jsxEnd;
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                // -- Branch 2: => <Tag  (lambda arrow with bare inline markup) --
                // Handles `() => <Label text="..." />` without wrapping parens.
                if (setupCode[i] == '=' && i + 1 < setupCode.Length && setupCode[i + 1] == '>')
                {
                    int arrowEnd = i + 2;
                    int peek2 = arrowEnd;
                    while (
                        peek2 < setupCode.Length
                        && (
                            setupCode[peek2] == ' '
                            || setupCode[peek2] == '\t'
                            || setupCode[peek2] == '\r'
                            || setupCode[peek2] == '\n'
                        )
                    )
                        peek2++;

                    if (
                        peek2 < setupCode.Length
                        && setupCode[peek2] == '<'
                        && peek2 + 1 < setupCode.Length
                        && char.IsLetter(setupCode[peek2 + 1])
                    )
                    {
                        // Emit the C# segment up to the JSX start (includes `=> `).
                        int jsxStart = peek2;
                        if (jsxStart > segStart)
                        {
                            string seg = setupCode.Substring(segStart, jsxStart - segStart);
                            EmitSegmentAndFlushPending(
                                b,
                                seg,
                                segStart,
                                uitkxSetupStartOffset,
                                gapOffset,
                                gapLength,
                                gapNewlines,
                                ref currentLine,
                                escapedPath,
                                pendingExprChecks,
                                source,
                                directives,
                                ref exprCtr,
                                ref attrCtr
                            );
                        }

                        int jsxEnd = FindJsxElementEnd(setupCode, jsxStart, setupCode.Length);

                        int jsxNewlines = CountNewlines(setupCode, jsxStart, jsxEnd);
                        b.Scaffold($"#line {currentLine} \"{escapedPath}\"\n");
                        b.Scaffold("(global::Ruitk.Core.VirtualNode)null!\n");
                        for (int k = 0; k < jsxNewlines; k++)
                            b.Scaffold("\n");
                        pendingExprChecks.Add((jsxStart, jsxEnd));

                        currentLine += jsxNewlines;
                        segStart = jsxEnd;
                        i = jsxEnd;
                        continue;
                    }
                }

                // -- Branch 2b: = <Tag  (bare assignment with inline markup) ----
                // Handles `var x = <Label text="..." />` without wrapping parens.
                // Must distinguish bare `=` from `=>`, `==`, `!=`, `<=`, `>=`.
                if (
                    setupCode[i] == '='
                    && i + 1 < setupCode.Length
                    && setupCode[i + 1] != '>'
                    && setupCode[i + 1] != '='
                )
                {
                    // Exclude !=, <=, >=
                    bool preceded =
                        i > 0
                        && (
                            setupCode[i - 1] == '!'
                            || setupCode[i - 1] == '<'
                            || setupCode[i - 1] == '>'
                        );
                    if (!preceded)
                    {
                        int peek2b = i + 1;
                        while (
                            peek2b < setupCode.Length
                            && (
                                setupCode[peek2b] == ' '
                                || setupCode[peek2b] == '\t'
                                || setupCode[peek2b] == '\r'
                                || setupCode[peek2b] == '\n'
                            )
                        )
                            peek2b++;

                        if (
                            peek2b < setupCode.Length
                            && setupCode[peek2b] == '<'
                            && peek2b + 1 < setupCode.Length
                            && char.IsLetter(setupCode[peek2b + 1])
                        )
                        {
                            int jsxStart = peek2b;
                            if (jsxStart > segStart)
                            {
                                string seg = setupCode.Substring(segStart, jsxStart - segStart);
                                EmitSegmentAndFlushPending(
                                    b,
                                    seg,
                                    segStart,
                                    uitkxSetupStartOffset,
                                    gapOffset,
                                    gapLength,
                                    gapNewlines,
                                    ref currentLine,
                                    escapedPath,
                                    pendingExprChecks,
                                    source,
                                    directives,
                                    ref exprCtr,
                                    ref attrCtr
                                );
                            }

                            int jsxEnd = FindJsxElementEnd(setupCode, jsxStart, setupCode.Length);

                            int jsxNewlines = CountNewlines(setupCode, jsxStart, jsxEnd);
                            b.Scaffold($"#line {currentLine} \"{escapedPath}\"\n");
                            b.Scaffold("(global::Ruitk.Core.VirtualNode)null!\n");
                            for (int k = 0; k < jsxNewlines; k++)
                                b.Scaffold("\n");
                            pendingExprChecks.Add((jsxStart, jsxEnd));

                            currentLine += jsxNewlines;
                            segStart = jsxEnd;
                            i = jsxEnd;
                            continue;
                        }
                    }
                }

                // -- Branch 3: @( - strip @ so Roslyn sees plain (expr) ---------
                if (setupCode[i] == '@' && i + 1 < setupCode.Length && setupCode[i + 1] == '(')
                {
                    if (i > segStart)
                    {
                        string seg = setupCode.Substring(segStart, i - segStart);
                        EmitSegmentAndFlushPending(
                            b,
                            seg,
                            segStart,
                            uitkxSetupStartOffset,
                            gapOffset,
                            gapLength,
                            gapNewlines,
                            ref currentLine,
                            escapedPath,
                            pendingExprChecks,
                            source,
                            directives,
                            ref exprCtr,
                            ref attrCtr
                        );
                    }
                    // Skip the `@` - the `(` will be re-processed next iteration.
                    segStart = i + 1;
                    i = i + 1;
                    continue;
                }

                // -- Branch 1: `(` that immediately precedes JSX ----------------
                if (setupCode[i] != '(')
                {
                    i++;
                    continue;
                }

                // Peek past whitespace to see if the first non-ws char is '<'
                int peek = i + 1;
                while (
                    peek < setupCode.Length
                    && (
                        setupCode[peek] == ' '
                        || setupCode[peek] == '\t'
                        || setupCode[peek] == '\r'
                        || setupCode[peek] == '\n'
                    )
                )
                    peek++;

                if (peek >= setupCode.Length || setupCode[peek] != '<')
                {
                    i++;
                    continue;
                }

                // -- Found a JSX paren block starting at i ------------------

                // 1. Emit the C# segment that precedes this block.
                if (i > segStart)
                {
                    string seg = setupCode.Substring(segStart, i - segStart);
                    EmitSegmentAndFlushPending(
                        b,
                        seg,
                        segStart,
                        uitkxSetupStartOffset,
                        gapOffset,
                        gapLength,
                        gapNewlines,
                        ref currentLine,
                        escapedPath,
                        pendingExprChecks,
                        source,
                        directives,
                        ref exprCtr,
                        ref attrCtr
                    );
                }

                // 2. Find the matching close paren (depth-balanced, ignores {}).
                int depth = 1;
                int j = i + 1;
                while (j < setupCode.Length && depth > 0)
                {
                    if (setupCode[j] == '(')
                        depth++;
                    else if (setupCode[j] == ')')
                        depth--;
                    j++;
                }
                // setupCode[i..j) is the complete `(<JSX>...)` block.

                // 3. Scaffold a valid C# placeholder with the same newline count
                //    so Roslyn's #line tracking stays in sync.
                //    Emit under a #line directive so that syntax errors at the
                //    boundary (e.g. missing ';' after the paren block) map back
                //    to the .uitkx file instead of being silently dropped in
                //    #line hidden territory.
                int jsxNewlines2 = CountNewlines(setupCode, i, j);
                int placeholderLine = currentLine;
                b.Scaffold($"#line {placeholderLine} \"{escapedPath}\"\n");
                b.Scaffold("(global::Ruitk.Core.VirtualNode)null!\n");
                for (int k = 0; k < jsxNewlines2; k++)
                    b.Scaffold("\n");
                pendingExprChecks.Add((i + 1, j - 1));

                currentLine += jsxNewlines2;
                segStart = j;
                i = j;
            }

            // 4. Emit any trailing C# segment after the last JSX block.
            if (segStart < setupCode.Length)
            {
                string seg = setupCode.Substring(segStart);
                EmitSegmentAndFlushPending(
                    b,
                    seg,
                    segStart,
                    uitkxSetupStartOffset,
                    gapOffset,
                    gapLength,
                    gapNewlines,
                    ref currentLine,
                    escapedPath,
                    pendingExprChecks,
                    source,
                    directives,
                    ref exprCtr,
                    ref attrCtr
                );
            }

            // Safety net: flush any remaining pending checks at the end
            // of setup code (e.g. when no trailing ';' was found).
            if (pendingExprChecks.Count > 0)
            {
                foreach (var (pStart, pEnd) in pendingExprChecks)
                    EmitInlineExprChecks(
                        b,
                        source,
                        directives,
                        escapedPath,
                        pStart,
                        pEnd,
                        uitkxSetupStartOffset,
                        gapOffset,
                        gapLength,
                        ref exprCtr,
                        ref attrCtr
                    );
                pendingExprChecks.Clear();
            }

            b.Scaffold("\n");
        }

        /// <summary>
        /// Emits a C# segment, flushing any pending expression checks at the
        /// first top-level semicolon (paren-depth 0) if present.  Splitting
        /// the segment at a true statement boundary ensures that expression
        /// checks land in the correct lexical scope (inside the lambda / loop
        /// where their JSX appeared) without being inserted mid-expression
        /// (which would break lambdas, ternaries, and assignment RHS).
        /// </summary>
        private static void EmitSegmentAndFlushPending(
            VirtualDocBuilder b,
            string seg,
            int segStartInSetup,
            int uitkxSetupStartOffset,
            int gapOffset,
            int gapLength,
            int gapNewlines,
            ref int currentLine,
            string escapedPath,
            List<(int jsxStart, int jsxEnd)> pendingExprChecks,
            string source,
            DirectiveSet directives,
            ref int exprCtr,
            ref int attrCtr
        )
        {
            if (pendingExprChecks.Count == 0)
            {
                EmitMappedWithGap(
                    b,
                    seg,
                    segStartInSetup,
                    uitkxSetupStartOffset,
                    gapOffset,
                    gapLength,
                    gapNewlines,
                    currentLine,
                    escapedPath
                );
                currentLine += CountNewlines(seg);
                return;
            }

            // Find first ';' at paren-depth 0 - a true statement boundary.
            int splitPos = -1;
            int pd = 0;
            for (int k = 0; k < seg.Length; k++)
            {
                char c = seg[k];
                if (c == '(')
                    pd++;
                else if (c == ')')
                {
                    if (pd > 0)
                        pd--;
                }
                else if (c == ';' && pd == 0)
                {
                    splitPos = k;
                    break;
                }
            }

            if (splitPos < 0)
            {
                // No statement boundary - emit as-is, checks stay pending.
                EmitMappedWithGap(
                    b,
                    seg,
                    segStartInSetup,
                    uitkxSetupStartOffset,
                    gapOffset,
                    gapLength,
                    gapNewlines,
                    currentLine,
                    escapedPath
                );
                currentLine += CountNewlines(seg);
                return;
            }

            // Emit up to and including the ';'.
            string before = seg.Substring(0, splitPos + 1);
            EmitMappedWithGap(
                b,
                before,
                segStartInSetup,
                uitkxSetupStartOffset,
                gapOffset,
                gapLength,
                gapNewlines,
                currentLine,
                escapedPath
            );
            currentLine += CountNewlines(before);

            // Flush pending expression checks at the statement boundary.
            foreach (var (pStart, pEnd) in pendingExprChecks)
                EmitInlineExprChecks(
                    b,
                    source,
                    directives,
                    escapedPath,
                    pStart,
                    pEnd,
                    uitkxSetupStartOffset,
                    gapOffset,
                    gapLength,
                    ref exprCtr,
                    ref attrCtr
                );
            pendingExprChecks.Clear();

            // Emit the remainder of the segment.
            if (splitPos + 1 < seg.Length)
            {
                string after = seg.Substring(splitPos + 1);
                EmitMappedWithGap(
                    b,
                    after,
                    segStartInSetup + splitPos + 1,
                    uitkxSetupStartOffset,
                    gapOffset,
                    gapLength,
                    gapNewlines,
                    currentLine,
                    escapedPath
                );
                currentLine += CountNewlines(after);
            }
        }

        /// <summary>
        /// Parses a JSX range (in setupCode-space) and emits inline expression
        /// checks so that variables from nested scopes (lambdas, local functions)
        /// are type-checked in their correct scope rather than at the outer
        /// render-method level.
        /// </summary>
        private static void EmitInlineExprChecks(
            VirtualDocBuilder b,
            string source,
            DirectiveSet directives,
            string escapedPath,
            int jsxStartInSetup,
            int jsxEndInSetup,
            int uitkxSetupStartOffset,
            int gapOffset,
            int gapLength,
            ref int exprCtr,
            ref int attrCtr
        )
        {
            // Convert setupCode-space to source-space.
            bool pastGap = gapOffset >= 0 && jsxStartInSetup >= gapOffset;
            int srcStart = uitkxSetupStartOffset + jsxStartInSetup + (pastGap ? gapLength : 0);
            int srcEnd = uitkxSetupStartOffset + jsxEndInSetup + (pastGap ? gapLength : 0);
            // Compute 1-based source line.
            int srcLine = 1;
            for (int li = 0; li < srcStart && li < source.Length; li++)
                if (source[li] == '\n')
                    srcLine++;
            var jsxDirectives = directives with
            {
                MarkupStartIndex = srcStart,
                MarkupEndIndex = srcEnd,
                MarkupStartLine = srcLine,
            };
            var diags = new List<ParseDiagnostic>();
            var nodes = UitkxParser.Parse(source, escapedPath, jsxDirectives, diags);
            if (nodes.Length > 0)
            {
                // Wrap in a local function so any `return` statements inside
                // directive bodies (e.g. @if/@else with `return (<JSX>)`) return
                // from the local function - not from __uitkx_render(), which would
                // make all subsequent setup code unreachable.
                // This mirrors the EmitBlockBodyLambda pattern used for event handlers.
                b.Scaffold("#line hidden\n");
                string funcName = $"__uitkx_sc{b.CurrentPos}";
                b.Scaffold("            {\n");
                b.Scaffold($"                dynamic {funcName}() {{\n");
                b.Scaffold("#pragma warning disable 0162, 0219\n");
                EmitNodeExpressionsScoped(
                    nodes,
                    b,
                    escapedPath,
                    indent: "                    ",
                    ref exprCtr,
                    ref attrCtr
                );
                b.Scaffold("#pragma warning restore 0162, 0219\n");
                b.Scaffold("#pragma warning disable CS0162\n");
                b.Scaffold("                    return default!;\n");
                b.Scaffold("#pragma warning restore CS0162\n");
                b.Scaffold("                }\n");
                b.Scaffold($"                _ = {funcName}();\n");
                b.Scaffold("            }\n");
            }
        }

        /// <summary>
        /// Emits a mapped C# segment, splitting it at the gap boundary when the
        /// setup code was formed by concatenating disjoint source ranges (before
        /// and after a removed <c>return (...);</c> statement).
        /// </summary>
        private static void EmitMappedWithGap(
            VirtualDocBuilder b,
            string seg,
            int segStart,
            int baseOffset,
            int gapOffset,
            int gapLength,
            int gapNewlines,
            int segLine,
            string escapedPath
        )
        {
            bool hasGap = gapOffset >= 0 && gapLength > 0;
            int segEnd = segStart + seg.Length;

            if (!hasGap || segEnd <= gapOffset)
            {
                // No gap or entirely before the gap - emit directly.
                int uitkxOff = baseOffset + segStart;
                b.Scaffold($"#line {segLine} \"{escapedPath}\"\n");
                b.Mapped(seg, uitkxOff, SourceRegionKind.FunctionSetup, segLine);
                b.Scaffold("\n#line hidden\n");
            }
            else if (segStart >= gapOffset)
            {
                // Entirely after the gap - shift offset by gapLength.
                int uitkxOff = baseOffset + segStart + gapLength;
                b.Scaffold($"#line {segLine} \"{escapedPath}\"\n");
                b.Mapped(seg, uitkxOff, SourceRegionKind.FunctionSetup, segLine);
                b.Scaffold("\n#line hidden\n");
            }
            else
            {
                // Straddles the gap - split into two mapped regions.
                int splitAt = gapOffset - segStart;
                string seg1 = seg.Substring(0, splitAt);
                string seg2 = seg.Substring(splitAt);
                int seg2Line = segLine + CountNewlines(seg1) + gapNewlines;

                b.Scaffold($"#line {segLine} \"{escapedPath}\"\n");
                b.Mapped(seg1, baseOffset + segStart, SourceRegionKind.FunctionSetup, segLine);
                b.Scaffold("\n#line hidden\n");

                b.Scaffold($"#line {seg2Line} \"{escapedPath}\"\n");
                b.Mapped(
                    seg2,
                    baseOffset + gapOffset + gapLength,
                    SourceRegionKind.FunctionSetup,
                    seg2Line
                );
                b.Scaffold("\n#line hidden\n");
            }
        }

        /// <summary>
        /// Regex that matches a standalone <c>return;</c> statement - a bare return
        /// with no value, at a word boundary, with optional surrounding whitespace.
        /// Group 1 captures the text before <c>return;</c>, group 2 is the whitespace
        /// between <c>return</c> and <c>;</c>.
        /// </summary>
        private static readonly Regex s_bareReturnRegex = new Regex(
            @"\breturn(\s*);",
            RegexOptions.Compiled
        );

        /// <summary>
        /// Emits block-body lambda content to the virtual document, replacing each
        /// standalone <c>return;</c> with a scaffolded <c>return default!;</c> so
        /// that guard-clause patterns like <c>if (x == null) return;</c> are valid
        /// inside a <c>dynamic</c>-returning local function without triggering the
        /// hard compiler error CS0126 (which cannot be suppressed via
        /// <c>#pragma warning disable</c>).
        ///
        /// All other content is emitted via <see cref="VirtualDocBuilder.Mapped"/>
        /// so completions and hover work correctly throughout the body.
        /// </summary>
        private static void EmitBodyWithReturnFix(
            VirtualDocBuilder b,
            string bodyText,
            int bodyUitkxOffset,
            SourceRegionKind kind,
            int uitkxLine,
            string escapedPath
        )
        {
            // Fast path: no bare return; in body -> emit entire block as one mapped segment.
            if (!s_bareReturnRegex.IsMatch(bodyText))
            {
                b.Scaffold($"#line {uitkxLine} \"{escapedPath}\"\n");
                b.Mapped(bodyText, bodyUitkxOffset, kind, uitkxLine);
                b.Scaffold("\n#line hidden\n");
                return;
            }

            // Issue 1 (UITKX0112): aggregate stripped JSX subtree ranges from
            // every segment so the deferred Pattern-B blocks can be flushed
            // after the body is fully emitted (avoids interleaving statements
            // mid-expression).
            var ctx = t_jsxAttrContext;
            List<(int SrcStart, int SrcEnd)>? deferred = ctx != null ? new List<(int, int)>() : null;

            int segStart = 0;
            int currentLine = uitkxLine;

            foreach (Match m in s_bareReturnRegex.Matches(bodyText))
            {
                // Emit text before this `return;` match as a mapped segment.
                if (m.Index > segStart)
                {
                    string seg = bodyText.Substring(segStart, m.Index - segStart);
                    b.Scaffold($"#line {currentLine} \"{escapedPath}\"\n");
                    EmitMappedExpressionStrippingJsx(b, seg, bodyUitkxOffset + segStart, kind, currentLine, deferred);
                    b.Scaffold("\n#line hidden\n");
                    currentLine += CountNewlines(seg);
                }

                // Scaffold the replacement (same newline count as original to keep
                // Roslyn's #line tracking in sync - `return;` is always one line).
                b.Scaffold("return default!;");
                currentLine += CountNewlines(bodyText, m.Index, m.Index + m.Length);

                segStart = m.Index + m.Length;
            }

            // Emit any trailing text after the last match.
            if (segStart < bodyText.Length)
            {
                string seg = bodyText.Substring(segStart);
                b.Scaffold($"#line {currentLine} \"{escapedPath}\"\n");
                EmitMappedExpressionStrippingJsx(b, seg, bodyUitkxOffset + segStart, kind, currentLine, deferred);
                b.Scaffold("\n#line hidden\n");
            }

            if (deferred != null && deferred.Count > 0 && ctx != null)
            {
                // Emitted at the body's outer indent - still inside the lambda's
                // local function, before its sentinel `return default!`.
                foreach (var (s, e) in deferred)
                    EmitDeferredJsxAttributeChecks(b, ctx.Source, ctx.Directives, escapedPath, s, e, "        ");
            }
        }

        /// <summary>Counts '\n' characters in <paramref name="s"/>.</summary>
        private static int CountNewlines(string s)
        {
            int count = 0;
            foreach (char c in s)
                if (c == '\n')
                    count++;
            return count;
        }

        /// <summary>Counts '\n' characters in <paramref name="s"/> between
        /// <paramref name="start"/> (inclusive) and <paramref name="end"/> (exclusive).</summary>
        private static int CountNewlines(string s, int start, int end)
        {
            int count = 0;
            for (int i = start; i < end && i < s.Length; i++)
                if (s[i] == '\n')
                    count++;
            return count;
        }

        /// <summary>
        /// Finds the end position (exclusive) of a JSX element starting at
        /// <paramref name="start"/> (which must point to <c>&lt;</c>).
        /// Handles self-closing (<c>/&gt;</c>) and container elements with
        /// nested children.  Skips over string literals and <c>{expr}</c> blocks.
        /// Returns <paramref name="start"/> if the element cannot be parsed.
        /// </summary>
        private static int FindJsxElementEnd(string text, int start, int limit)
        {
            if (start >= limit || text[start] != '<')
                return start;

            int depth = 0;
            int i = start;

            while (i < limit)
            {
                char ch = text[i];

                // Skip string literals inside attributes
                if (ch == '"')
                {
                    i++;
                    while (i < limit && text[i] != '"')
                        i++;
                    if (i < limit)
                        i++; // skip closing "
                    continue;
                }

                // Skip C# expression blocks {expr}
                if (ch == '{')
                {
                    i++;
                    int braceDepth = 1;
                    while (i < limit && braceDepth > 0)
                    {
                        if (text[i] == '{')
                            braceDepth++;
                        else if (text[i] == '}')
                            braceDepth--;
                        else if (text[i] == '"')
                        {
                            i++;
                            while (i < limit && text[i] != '"')
                            {
                                if (text[i] == '\\')
                                    i++;
                                i++;
                            }
                        }
                        if (braceDepth > 0)
                            i++;
                    }
                    if (i < limit)
                        i++; // skip closing }
                    continue;
                }

                // Self-closing end: />
                if (ch == '/' && i + 1 < limit && text[i + 1] == '>')
                {
                    depth--;
                    i += 2;
                    if (depth <= 0)
                        return i;
                    continue;
                }

                // Closing tag: </Tag>
                if (ch == '<' && i + 1 < limit && text[i + 1] == '/')
                {
                    depth--;
                    i += 2;
                    while (i < limit && text[i] != '>')
                        i++;
                    if (i < limit)
                        i++; // skip >
                    if (depth <= 0)
                        return i;
                    continue;
                }

                // Opening tag: <Tag
                if (ch == '<' && i + 1 < limit && char.IsLetter(text[i + 1]))
                {
                    depth++;
                    i++;
                    continue;
                }

                i++;
            }

            return i; // reached end of text
        }
    }
}
