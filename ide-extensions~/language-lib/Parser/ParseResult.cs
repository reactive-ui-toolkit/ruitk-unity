using System.Collections.Immutable;

namespace Ruitk.Language.Parser
{
    // ── Typed-props function parameter ───────────────────────────────────────

    /// <summary>
    /// A single typed parameter declared inside the function-style component header:
    /// <c>component Name(int X = 0, string Label = "")</c>.
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="Type"/> — verbatim C# type (may include generics, e.g. <c>List&lt;string&gt;</c>).</description></item>
    ///   <item><description><see cref="Name"/> — identifier used as the local variable in the body; the
    ///   PROPS contract derives from <see cref="PropSourceName"/> instead.</description></item>
    ///   <item><description><see cref="DefaultValue"/> — verbatim default expression, or <c>null</c> if omitted (maps to <c>default</c> in the generated class).</description></item>
    /// </list>
    /// </summary>
    public sealed record FunctionParam(string Type, string Name, string? DefaultValue)
    {
        /// <summary>1-based source line where the parameter name appears. 0 when not tracked.</summary>
        public int SourceLine { get; init; } = 0;

        /// <summary>0-based column of the first character of the parameter name. -1 when not tracked.</summary>
        public int NameColumn { get; init; } = -1;

        /// <summary>
        /// The name this parameter contributes to the component's PROPS contract:
        /// <see cref="Name"/> minus any leading underscores. A leading underscore marks a
        /// parameter deliberately unused (the C# discard convention UITKX0111 honors)
        /// WITHOUT renaming the public prop — consumers keep writing the unprefixed
        /// attribute, and the generated props-class property is the PascalCase of THIS
        /// name. Every layer that maps params to props (SG emitter, HMR emitter, LSP
        /// workspace index) derives from this single definition; two params that
        /// collapse to the same prop name are UITKX0114.
        /// </summary>
        public string PropSourceName => ToPropSourceName(Name);

        /// <summary>Static form of <see cref="PropSourceName"/> for callers that hold a raw
        /// parameter name. Falls back to the literal name when stripping would leave nothing
        /// (a bare <c>_</c> cannot become a prop).</summary>
        public static string ToPropSourceName(string name)
        {
            if (string.IsNullOrEmpty(name) || name[0] != '_')
                return name;
            string stripped = name.TrimStart('_');
            return stripped.Length == 0 ? name : stripped;
        }
    }

    // ── Hook declaration ─────────────────────────────────────────────────────

    /// <summary>
    /// A single <c>hook</c> declaration parsed from a .uitkx file:
    /// <c>hook useCounter(int initial = 0) -&gt; (int, Action) { ... }</c>.
    /// </summary>
    public sealed record HookDeclaration(
        /// <summary>Hook name (camelCase), e.g. <c>useCounter</c>.</summary>
        string Name,
        /// <summary>Generic type parameters including angle brackets, e.g. <c>&lt;T&gt;</c>, or <c>null</c> if non-generic.</summary>
        string? GenericParams,
        /// <summary>Typed parameters declared in the hook header.</summary>
        ImmutableArray<FunctionParam> Params,
        /// <summary>Return type text after <c>-&gt;</c>, e.g. <c>(int, Action)</c>, or <c>null</c> for void hooks.</summary>
        string? ReturnType,
        /// <summary>Raw C# body text between the braces.</summary>
        string Body,
        /// <summary>1-based line of the <c>hook</c> keyword.</summary>
        int DeclarationLine,
        /// <summary>1-based line of the first body statement.</summary>
        int BodyStartLine,
        /// <summary>Absolute char offset in source just after the opening <c>{</c>.</summary>
        int BodyStartOffset,
        /// <summary>Absolute char offset in source just before the closing <c>}</c>.</summary>
        int BodyEndOffset
    )
    {
        /// <summary>
        /// True when the declaration is prefixed with <c>export</c> (import/export grammar,
        /// leg 3). Non-exported hooks are file-private / strict-invisible cross-file.
        /// </summary>
        public bool IsExported { get; init; } = false;
    };

    // ── Module declaration ───────────────────────────────────────────────────

    /// <summary>
    /// A single <c>module</c> declaration parsed from a .uitkx file:
    /// <c>module Counter { ... }</c>.
    /// </summary>
    public sealed record ModuleDeclaration(
        /// <summary>Module name (PascalCase), e.g. <c>Counter</c>.</summary>
        string Name,
        /// <summary>Raw C# body text between the braces.</summary>
        string Body,
        /// <summary>1-based line of the <c>module</c> keyword.</summary>
        int DeclarationLine,
        /// <summary>1-based line of the first body statement.</summary>
        int BodyStartLine,
        /// <summary>Absolute char offset in source just after the opening <c>{</c>.</summary>
        int BodyStartOffset,
        /// <summary>Absolute char offset in source just before the closing <c>}</c>.</summary>
        int BodyEndOffset
    )
    {
        /// <summary>
        /// True when the declaration is prefixed with <c>export</c> (import/export grammar,
        /// leg 3). Non-exported modules are file-private / strict-invisible cross-file.
        /// </summary>
        public bool IsExported { get; init; } = false;
    };

    // ── Import declaration ───────────────────────────────────────────────────

    /// <summary>
    /// A single preamble <c>import { A, B } from "specifier"</c> line (import/export grammar,
    /// leg 3). Named imports only; static, string-literal specifier; preamble-only.
    /// </summary>
    public sealed record ImportDeclaration(
        /// <summary>The imported names, in source order (e.g. <c>["StatusChip"]</c>).</summary>
        ImmutableArray<string> Names,
        /// <summary>The raw specifier string (e.g. <c>"./StatusChip"</c>, <c>"~/Shared/Types"</c>). Extensionless — <c>.uitkx</c> implied.</summary>
        string Specifier,
        /// <summary>1-based line of the <c>import</c> keyword.</summary>
        int Line,
        /// <summary>0-based column of the <c>import</c> keyword.</summary>
        int Column,
        /// <summary>0-based column of each imported name, parallel to <see cref="Names"/>. For unused-import / not-exported squiggles.</summary>
        ImmutableArray<int> NameColumns,
        /// <summary>0-based column of the specifier's opening quote (<c>-1</c> = untracked). The full
        /// specifier span is <c>[SpecifierColumn, SpecifierColumn + Specifier.Length + 2)</c> — both
        /// quotes included. For unresolved-specifier (2300/2308/2314) squiggles.</summary>
        int SpecifierColumn = -1,
        /// <summary>ES-modules full import surface (G-05). Rename-on-import target, parallel to
        /// <see cref="Names"/> — <c>null</c> at an index when that name has no <c>as</c> alias
        /// (<c>import { a as b }</c> → <c>Names[i] == "a"</c>, <c>Aliases[i] == "b"</c>). Empty
        /// (not length-matched to <see cref="Names"/>) when the file has no aliased named imports —
        /// callers must guard on <c>Aliases.Length &gt; i</c> before indexing.</summary>
        ImmutableArray<string?> Aliases = default,
        /// <summary>True for <c>import * as X from "specifier"</c> (namespace import). When true,
        /// <see cref="Names"/> is empty and <see cref="StarAlias"/> carries the bound name.</summary>
        bool IsStar = false,
        /// <summary>The bound name for a <c>* as X</c> namespace import. <c>null</c> unless
        /// <see cref="IsStar"/>.</summary>
        string? StarAlias = null,
        /// <summary>True for <c>import X from "specifier"</c> (default import). When true,
        /// <see cref="Names"/> is empty and <see cref="DefaultAlias"/> carries the local binding name.</summary>
        bool IsDefault = false,
        /// <summary>The local binding name for a default import. <c>null</c> unless
        /// <see cref="IsDefault"/>.</summary>
        string? DefaultAlias = null
    );

    // ── Using directive ──────────────────────────────────────────────────────

    /// <summary>
    /// A single <c>@using</c> or namespace-import (<c>import "@Ns"</c>) preamble line, WITH source
    /// positions (namespace-import unification plan). Parallel to <see cref="DirectiveSet.Usings"/>
    /// (which keeps the payload-only string view for the emitters); this record carries the columns
    /// needed to anchor UITKX2316 (unknown namespace) / UITKX2317 (unused using) squiggles.
    /// </summary>
    public sealed record UsingDirective(
        /// <summary>The verbatim payload: <c>"UnityEngine.Audio"</c>, <c>"static DoomTypes"</c>, or <c>"Alias = Ns.Type"</c>.</summary>
        string Payload,
        /// <summary>1-based line of the <c>@using</c>/<c>import</c> keyword.</summary>
        int Line,
        /// <summary>0-based column of the <c>@</c>/<c>import</c> keyword (start of the directive).</summary>
        int Column,
        /// <summary>0-based column of the first char of <see cref="Payload"/> (the namespace token) — the squiggle anchor.</summary>
        int PayloadColumn,
        /// <summary>True when written as <c>import "@Ns"</c>; false when written as <c>@using Ns</c>.</summary>
        bool FromImportSyntax
    );

    // ── Component declaration (per-decl, mixed-decl v1) ──────────────────────

    /// <summary>
    /// A single <c>component</c> declaration parsed from a .uitkx file (mixed-decl v1, leg 3):
    /// a file may declare multiple components/hooks/modules in any order. Carries every field
    /// that was historically singular on <see cref="DirectiveSet"/> so multi-component files
    /// emit one partial class per component.
    /// </summary>
    public sealed record ComponentDeclaration(
        /// <summary>Component (class) name.</summary>
        string Name,
        /// <summary>True when prefixed with <c>export</c> (→ <c>public</c>; else <c>internal</c>).</summary>
        bool IsExported,
        /// <summary><c>@props</c> — optional fully-qualified props type name.</summary>
        string? PropsTypeName,
        /// <summary><c>@key</c> — optional default VirtualNode key.</summary>
        string? DefaultKey,
        /// <summary>Typed parameters from the component header.</summary>
        ImmutableArray<FunctionParam> FunctionParams,
        /// <summary>Setup C# statements (all top-level statements except the <c>return (...)</c>).</summary>
        string? FunctionSetupCode,
        /// <summary>1-based line where setup code begins. -1 when unavailable.</summary>
        int FunctionSetupStartLine,
        /// <summary>Absolute char offset of the first char of trimmed setup code. -1 when not tracked.</summary>
        int FunctionSetupStartOffset,
        /// <summary>1-based line where this component's markup begins.</summary>
        int MarkupStartLine,
        /// <summary>Absolute char index where this component's markup begins.</summary>
        int MarkupStartIndex,
        /// <summary>Exclusive end index for this component's markup. -1 = to EOF.</summary>
        int MarkupEndIndex,
        /// <summary>1-based line of the <c>component Name {</c> declaration.</summary>
        int DeclarationLine,
        /// <summary>0-based column of the component NAME token.</summary>
        int NameColumn,
        /// <summary>1-based line of the <c>;</c> ending <c>return (...);</c>. -1 when not tracked.</summary>
        int ReturnEndLine,
        /// <summary>1-based line of the closing <c>}</c> of the component body. -1 when not tracked.</summary>
        int BodyEndLine
    )
    {
        /// <summary>Paren-wrapped JSX ranges in this component's setup code (start, end, line).</summary>
        public ImmutableArray<(int Start, int End, int Line)> SetupCodeMarkupRanges { get; init; }
            = ImmutableArray<(int Start, int End, int Line)>.Empty;
        /// <summary>Bare (non-paren-wrapped) JSX ranges in this component's setup code.</summary>
        public ImmutableArray<(int Start, int End, int Line)> SetupCodeBareJsxRanges { get; init; }
            = ImmutableArray<(int Start, int End, int Line)>.Empty;
        /// <summary>Offset in setup code where the removed <c>return (…);</c> gap begins. -1 when none.</summary>
        public int FunctionSetupGapOffset { get; init; } = -1;
        /// <summary>Length of the removed <c>return (…);</c> statement.</summary>
        public int FunctionSetupGapLength { get; init; } = 0;
        /// <summary>True when <see cref="IsExported"/> was set only by an <c>export { … }</c> list
        /// or <c>export default</c> marking — no inline <c>export</c> prefix exists in source, so
        /// the formatter must not synthesize one.</summary>
        public bool IsExportImplied { get; init; }
        /// <summary>True for a null-only component (React case 2): the body has no top-level
        /// <c>return (…);</c> but contains an explicit top-level <c>return null;</c> — a component
        /// that always renders nothing. The markup range is empty and the whole body (including
        /// the <c>return null;</c>) lives in <see cref="FunctionSetupCode"/>; the formatter must
        /// not synthesize a markup return for this shape.</summary>
        public bool HasNullReturn { get; init; }
    };

    // ── Plain declaration (ES-modules, U-04) ─────────────────────────────────

    /// <summary>
    /// Classification of a plain (wrapper-keyword-free) top-level declaration, read from the
    /// SIGNATURE ALONE at parse time (G-03): a <c>VirtualNode</c> return classifies as
    /// <see cref="Component"/>; a <c>use</c>-prefixed name classifies as <see cref="Hook"/>; a
    /// bare <c>= initializer</c> classifies as <see cref="Value"/>; anything else is
    /// <see cref="Util"/>.
    /// </summary>
    public enum DeclKind
    {
        Component,
        Hook,
        Value,
        Util
    }

    /// <summary>
    /// A single plain <c>export &lt;Type&gt; Name(...) { ... }</c> / <c>export Type Name = ...;</c>
    /// top-level declaration (ES-modules campaign, U-04). Covers hook/value/util kinds; a
    /// <c>VirtualNode</c>-returning declaration classifies as <see cref="DeclKind.Component"/> but
    /// is parsed into <see cref="ComponentDeclaration"/> instead (its body machinery is unchanged
    /// from the wrapper-keyword form). Legacy <c>hook</c>/<c>module</c> keyword parses keep using
    /// <see cref="HookDeclaration"/>/<see cref="ModuleDeclaration"/> — this record is new-mode only.
    /// </summary>
    public sealed record MemberDeclaration(
        /// <summary>Declared name.</summary>
        string Name,
        /// <summary>Classification read from the signature (G-03).</summary>
        DeclKind Kind,
        /// <summary>True when prefixed with <c>export</c>.</summary>
        bool IsExported,
        /// <summary>Declared type text: the return type for <see cref="DeclKind.Hook"/>/
        /// <see cref="DeclKind.Util"/>, or the declared value type for <see cref="DeclKind.Value"/>
        /// (<c>null</c> when the value uses inference sugar — <c>= new T { ... }</c>).</summary>
        string? ReturnTypeText,
        /// <summary>Raw parameter-list text (between the parens), or <c>null</c> for
        /// <see cref="DeclKind.Value"/> (values have no parameter list).</summary>
        string? ParamsText,
        /// <summary>Raw body text: block-body content between <c>{ }</c>, expression-body text
        /// after <c>=&gt;</c>, or the initializer expression after <c>=</c> for values.</summary>
        string BodyText,
        /// <summary>True when written as <c>=&gt; expr;</c> (expression-bodied); false for a
        /// balanced <c>{ ... }</c> block or a value's <c>= initializer;</c>.</summary>
        bool IsExpressionBodied,
        /// <summary>1-based line of the declaration head (the leading <c>export</c>/type token).</summary>
        int DeclarationLine,
        /// <summary>0-based column of the NAME token.</summary>
        int NameColumn,
        /// <summary>1-based line where the body/initializer begins.</summary>
        int BodyStartLine,
        /// <summary>Absolute char offset just after the opening <c>{</c>/<c>=&gt;</c>/<c>=</c>.</summary>
        int BodyStartOffset,
        /// <summary>Absolute char offset just before the closing <c>}</c>/<c>;</c>.</summary>
        int BodyEndOffset
    )
    {
        /// <summary>
        /// Typed parameters parsed from the head (function-shaped members only; empty for
        /// <see cref="DeclKind.Value"/>). The raw <c>ParamsText</c> stays authoritative for
        /// re-emission (formatter); this parsed view feeds the emitters (trampoline param
        /// lists, delegate types, bridges) exactly like <see cref="HookDeclaration.Params"/>.
        /// </summary>
        public ImmutableArray<FunctionParam> Params { get; init; } = ImmutableArray<FunctionParam>.Empty;
        /// <summary>True when <see cref="IsExported"/> was set only by an <c>export { … }</c> list
        /// or <c>export default</c> marking — no inline <c>export</c> prefix exists in source, so
        /// the formatter must not synthesize one.</summary>
        public bool IsExportImplied { get; init; }
        /// <summary>Generic type-parameter list text verbatim including the angle brackets
        /// (e.g. <c>&lt;T&gt;</c>, <c>&lt;TKey, TValue&gt;</c>), or <c>null</c> for non-generic
        /// members (F9 — generic declaration heads). Function-shaped members only; values never
        /// carry one. Emitters append it to the emitted method name; the formatter re-emits it
        /// between the name and the parameter list.</summary>
        public string? TypeParamsText { get; init; }
    };

    // ── Directive data ────────────────────────────────────────────────────────

    /// <summary>
    /// The validated set of top-level <c>@directives</c> found at the top of a
    /// .uitkx file.
    /// </summary>
    public sealed record DirectiveSet(
        /// <summary><c>@namespace</c> — required. C# namespace for the generated class.</summary>
        string? Namespace,
        /// <summary><c>@component</c> — required. The generated class name.</summary>
        string? ComponentName,
        /// <summary><c>@props</c> — optional. Fully-qualified props type name.</summary>
        string? PropsTypeName,
        /// <summary><c>@key</c> — optional. Default VirtualNode key string.</summary>
        string? DefaultKey,
        /// <summary>All <c>@using</c> namespace values, in declaration order.</summary>
        ImmutableArray<string> Usings,
        /// <summary>All <c>@uss "path"</c> stylesheet paths, in declaration order.</summary>
        ImmutableArray<string> UssFiles,
        /// <summary>
        /// All <c>@inject Type Name</c> declarations, in declaration order.
        /// Each entry carries the fully-qualified type string and the field name.
        /// </summary>
        ImmutableArray<(string Type, string Name)> Injects,
        /// <summary>
        /// 1-based line number of the first non-directive line (i.e. where the
        /// markup begins). Used by the parser to set its initial line counter.
        /// </summary>
        int MarkupStartLine,
        /// <summary>
        /// Character index into the source string where the markup begins.
        /// Passed to the tokenizer/parser so they start at the correct position.
        /// </summary>
        int MarkupStartIndex,
        /// <summary>
        /// Optional exclusive end index for markup parsing in function-style files.
        /// <c>-1</c> means parse until EOF (legacy directive-based form).
        /// </summary>
        int MarkupEndIndex = -1,
        /// <summary>
        /// True when source uses the function-style component form:
        /// <c>component Name { ... return (...) ... }</c>.
        /// </summary>
        bool IsFunctionStyle = false,
        /// <summary>
        /// True when an <c>@namespace X.Y</c> directive was explicitly written in
        /// the source file.  False when the namespace was inferred from a companion
        /// <c>.cs</c> file (or is the hard-coded fallback namespace).
        /// The formatter uses this to decide whether to re-emit the directive.
        /// </summary>
        bool HasExplicitNamespace = false,
        /// <summary>
        /// Setup C# statements extracted from function-style body (all top-level
        /// statements except the <c>return (...)</c> statement). Injected as a
        /// synthetic <c>@code</c> block before markup emission.
        /// </summary>
        string? FunctionSetupCode = null,
        /// <summary>
        /// 1-based line where function-style setup code begins inside
        /// <c>component Name { ... }</c>. <c>-1</c> when unavailable.
        /// </summary>
        int FunctionSetupStartLine = -1,
        /// <summary>
        /// Absolute character offset in the .uitkx source of the first character
        /// of the trimmed <see cref="FunctionSetupCode"/>.
        /// <c>-1</c> when not tracked (fallback: line-based approximation used).
        /// </summary>
        int FunctionSetupStartOffset = -1,
        /// <summary>
        /// Parameters declared in the function-style component header:
        /// <c>component Name(int X = 0, string Label = "")</c>.
        ///
        /// When non-empty the source generator auto-derives a companion props class
        /// named <c>{ComponentName}Props</c> and exposes each parameter as a local
        /// variable in the Render method body.
        ///
        /// Default: <c>default</c> (empty / not used).
        /// </summary>
        ImmutableArray<FunctionParam> FunctionParams = default,
        /// <summary>
        /// 1-based source line of the <c>component Name {</c> or <c>@component</c>
        /// declaration. Used to attach UITKX0103 (filename mismatch) at the right
        /// location. <c>-1</c> when not tracked.
        /// </summary>
        int ComponentDeclarationLine = -1,
        /// <summary>
        /// 0-based column of the first character of the component NAME (not the
        /// <c>component</c> keyword itself). Used to aim the UITKX0103 squiggle at
        /// the name token. <c>-1</c> when not tracked.
        /// </summary>
        int ComponentNameColumn = -1,
        /// <summary>
        /// 1-based line of the <c>;</c> ending the <c>return (...);</c> statement
        /// in function-style components. <c>-1</c> when not tracked or not
        /// function-style.
        /// </summary>
        int FunctionReturnEndLine = -1,
        /// <summary>
        /// 1-based line of the closing <c>}</c> of the function-style component
        /// body. <c>-1</c> when not tracked.
        /// </summary>
        int FunctionBodyEndLine = -1,
        /// <summary>
        /// Absolute (start, end, line) ranges in the original .uitkx source for each
        /// JSX paren block embedded inside function-style setup code, e.g.
        /// <c>var x = (&lt;Box&gt;...&lt;/Box&gt;)</c>.
        /// <para>
        /// <c>start</c> = char index just inside the opening <c>(</c>;<br/>
        /// <c>end</c>   = exclusive index at the closing <c>)</c>;<br/>
        /// <c>line</c>  = 1-based source line of <c>start</c>.
        /// </para>
        /// Default: empty / not used.
        /// </summary>
        ImmutableArray<(int Start, int End, int Line)> SetupCodeMarkupRanges = default,
        /// <summary>
        /// Absolute (start, end, line) ranges in the original .uitkx source for
        /// bare JSX elements in function-style setup code that are NOT
        /// paren-wrapped — e.g. <c>return &lt;Tag/&gt;</c>,
        /// <c>cond ? &lt;A/&gt; : &lt;B/&gt;</c>, <c>var x = &lt;Tag/&gt;</c>.
        /// Used by the virtual-document generator for expression type-checks
        /// but NOT by the formatter (which only handles paren-wrapped blocks).
        /// </summary>
        ImmutableArray<(int Start, int End, int Line)> SetupCodeBareJsxRanges = default,
        /// <summary>
        /// Position inside the trimmed <see cref="FunctionSetupCode"/> where the
        /// gap left by the removed <c>return (…);</c> statement begins.
        /// Characters at or beyond this offset correspond to source positions
        /// shifted by <see cref="FunctionSetupGapLength"/>.
        /// <c>-1</c> when there is no gap (no return was removed).
        /// </summary>
        int FunctionSetupGapOffset = -1,
        /// <summary>
        /// Number of source characters occupied by the removed <c>return (…);</c>
        /// statement.  Added to the base offset for source-map entries whose
        /// position in <see cref="FunctionSetupCode"/> is at or past
        /// <see cref="FunctionSetupGapOffset"/>.
        /// </summary>
        int FunctionSetupGapLength = 0,
        /// <summary>
        /// Hook declarations parsed from this file. Empty/default for component or directive-style files.
        /// </summary>
        ImmutableArray<HookDeclaration> HookDeclarations = default,
        /// <summary>
        /// Module declarations parsed from this file. Empty/default for component or directive-style files.
        /// </summary>
        ImmutableArray<ModuleDeclaration> ModuleDeclarations = default
    )
    {
        /// <summary>
        /// Comments (<c>//</c>, <c>/* */</c>, <c>&lt;!-- --&gt;</c>) consumed while skipping
        /// leading trivia before the preamble (<c>@namespace</c>/<c>@using</c>/<c>@uss</c>/
        /// <c>component</c>), in source order, each with its raw text (including delimiters)
        /// and 1-based line. The formatter re-emits these verbatim so a license header or
        /// other leading comment is not silently dropped on format (see
        /// FINAL_AUDIT_UITKX_FINDINGS.md, finding U-01). Empty when not tracked (e.g. the
        /// lightweight <c>LooksLikeFunctionStyleComponent</c> lookahead probe).
        /// </summary>
        public ImmutableArray<(string Text, bool IsBlock, int Line)> LeadingTrivia { get; init; }
            = ImmutableArray<(string Text, bool IsBlock, int Line)>.Empty;

        /// <summary>
        /// Preamble <c>import { … } from "…"</c> declarations, in source order (import/export
        /// grammar, leg 3). Empty when the file has no imports. See <see cref="ImportDeclaration"/>.
        /// </summary>
        public ImmutableArray<ImportDeclaration> Imports { get; init; }
            = ImmutableArray<ImportDeclaration>.Empty;

        /// <summary>
        /// Positioned view of the preamble <c>@using</c> / <c>import "@Ns"</c> lines, in source order
        /// (namespace-import unification plan). Parallel to <see cref="Usings"/> (the payload-only
        /// string view read by every emitter) but carries columns for UITKX2316/2317 anchoring.
        /// May be shorter than <see cref="Usings"/> when a consumer synthesises a using post-parse
        /// (e.g. the SG injecting a hook container) — only parser-authored lines have positions.
        /// </summary>
        public ImmutableArray<UsingDirective> UsingDirectives { get; init; }
            = ImmutableArray<UsingDirective>.Empty;

        /// <summary>True for a null-only component (React case 2): no top-level
        /// <c>return (…);</c> exists, but the body contains an explicit top-level
        /// <c>return null;</c>. The markup range is empty and the whole body lives in
        /// <see cref="FunctionSetupCode"/>. Hoisted from the primary
        /// <see cref="ComponentDeclaration.HasNullReturn"/> for new-mode files.</summary>
        public bool HasNullReturn { get; init; }

        /// <summary>
        /// All <c>component</c> declarations in this file, in source order (mixed-decl v1, leg 3).
        /// Supersedes the singular <see cref="ComponentName"/>/setup/markup fields, which remain during
        /// the migration and are kept in sync for the first component. Empty for hook/module-only or
        /// directive-style files.
        /// </summary>
        public ImmutableArray<ComponentDeclaration> ComponentDeclarations { get; init; }
            = ImmutableArray<ComponentDeclaration>.Empty;

        /// <summary>
        /// Plain hook/value/util declarations parsed from this file (ES-modules campaign, U-04).
        /// Empty for legacy-mode files (their hooks/modules stay in <see cref="HookDeclarations"/>/
        /// <see cref="ModuleDeclarations"/>) and for component-only new-mode files.
        /// </summary>
        public ImmutableArray<MemberDeclaration> MemberDeclarations { get; init; }
            = ImmutableArray<MemberDeclaration>.Empty;

        /// <summary>
        /// The name bound by <c>export default &lt;Name&gt;;</c>, or <c>null</c> when the file has
        /// no default export. Must match a declaration in this file (else UITKX2323).
        /// </summary>
        public string? DefaultExportName { get; init; } = null;

        /// <summary>
        /// Names exported via deferred <c>export { a, b };</c> lists, in source order (G-05).
        /// The matching declarations are ALSO marked <c>IsExported</c>; this list preserves the
        /// author's chosen spelling so the formatter re-emits the list (at end of file, U-10)
        /// instead of silently converting to inline <c>export</c> prefixes.
        /// </summary>
        public ImmutableArray<string> ExportListNames { get; init; } = ImmutableArray<string>.Empty;

        /// <summary>
        /// True when the file's first declaration used a legacy wrapper keyword
        /// (<c>component</c>/<c>hook</c>/<c>module</c>) — U-08. Migration-only data since
        /// 0.16.0: the wrapper grammar is a hard <c>UITKX2320</c> error on a normal parse
        /// (mixing styles reports the 2320 mixed-file variant), and this flag routes the
        /// SG's <c>#error</c> stub plus the codemod's migrate/freeze decision under
        /// <c>ParseLegacyForMigration</c>. A file with no declarations at all is new-mode
        /// (<c>false</c>).
        /// </summary>
        public bool UsesLegacySyntax { get; init; } = false;

        /// <summary>
        /// The render backend this file targets, from the <c>@backend</c>
        /// directive: <c>"ugui"</c> for the uGUI vocabulary, <c>null</c> for
        /// the default UI Toolkit vocabulary (an explicit <c>@backend uitk</c>
        /// normalizes to null). Selects the tag-resolution table, factory
        /// class, and schema used by the emitters and IDE tooling.
        /// </summary>
        public string? Backend { get; init; } = null;
    };

    // ── Full parse result ─────────────────────────────────────────────────────

    /// <summary>
    /// The complete result of parsing a .uitkx file: directives, AST, and any
    /// diagnostics produced during tokenization and parsing.
    /// All types are Roslyn-free; suitable for use in both the source generator
    /// and the LSP server.
    /// </summary>
    public sealed record ParseResult(
        DirectiveSet Directives,
        ImmutableArray<Ruitk.Language.Nodes.AstNode> RootNodes,
        ImmutableArray<ParseDiagnostic> Diagnostics
    );
}
