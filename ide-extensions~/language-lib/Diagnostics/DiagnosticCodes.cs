namespace Ruitk.Language.Diagnostics
{
    /// <summary>
    /// Diagnostic code constants used by the UITKX language library.
    ///
    /// ID ranges:
    ///   UITKX0013–0016   T2 — Rules of Hooks (shared codes with SourceGenerator);
    ///                         language-lib DiagnosticsAnalyzer
    ///   UITKX0101–0113   T2 — Structural (directive + schema checks); language-lib
    ///                         + lsp-server (UITKX0113 duplicate component)
    ///   UITKX0120         T2 — Asset path validation; language-lib
    ///   UITKX0200–0200   T2v — Version compatibility; lsp-server
    ///   UITKX0210         T2 — HMR-swap field writes; SourceGenerator analyzer
    ///   UITKX0211         RETIRED 0.16.0 — `const` in legacy module body (grammar removed)
    ///   UITKX0300–0306   T1 — Parser syntax errors; emitted by UitkxParser /
    ///                         DirectiveParser into ParseResult.Diagnostics
    ///   UITKX0112         T3 — Roslyn data-flow; lsp-server (unused variable)
    /// </summary>
    public static class DiagnosticCodes
    {
        // ── T2 — Rules of Hooks (shared IDs with SourceGenerator) ────────────

        /// <summary>
        /// Hook called inside an <c>@if</c> / <c>@else</c> branch.
        /// Hooks must be called unconditionally at the top level of the component.
        /// </summary>
        public const string HookInConditional = "UITKX0013";

        /// <summary>
        /// Hook called inside a <c>@foreach</c>, <c>@for</c>, or <c>@while</c> loop.
        /// Hooks must be called unconditionally at the top level of the component.
        /// </summary>
        public const string HookInLoop = "UITKX0014";

        /// <summary>
        /// Hook called inside a <c>@switch</c> case.
        /// Hooks must be called unconditionally at the top level of the component.
        /// </summary>
        public const string HookInSwitch = "UITKX0015";

        /// <summary>
        /// Hook called inside an event-handler attribute expression
        /// (e.g. <c>onClick={() =&gt; UseState(0)}</c>).
        /// Hooks must be called from the component body, not from callbacks.
        /// </summary>
        public const string HookInEventHandler = "UITKX0016";

        // ── T2 — Structural diagnostics (this analyzer) ──────────────────────

        /// <summary>RETIRED, never emitted — <c>@namespace</c> is optional (path-derived when
        /// omitted). Reserved so the number is never reused.</summary>
        public const string MissingNamespace = "UITKX0101";

        /// <summary>RETIRED, never emitted — function-style files need no <c>@component</c>;
        /// hook/module-only files are legal. Reserved.</summary>
        public const string MissingComponent = "UITKX0102";

        /// <summary>RETIRED, never emitted — filename==component is a documentation convention,
        /// not a diagnostic (a file may declare several components). Reserved.</summary>
        public const string FilenameMismatch = "UITKX0103";

        /// <summary>Two sibling elements share the same literal <c>key="…"</c> value.</summary>
        public const string DuplicateKey = "UITKX0104";

        /// <summary>
        /// PascalCase element name is not in the workspace element index.
        /// Only reported when the index is available.
        /// </summary>
        public const string UnknownElement = "UITKX0105";

        /// <summary>
        /// An element inside a <c>@foreach</c> body has no <c>key</c> attribute.
        /// Severity: Warning.
        /// </summary>
        public const string MissingKey = "UITKX0106";

        /// <summary>
        /// Statement appears after an unconditional top-level <c>return</c> in an
        /// <c>@code</c> block and is unreachable.
        /// Severity: Hint (with Unnecessary tag in LSP layer).
        /// </summary>
        public const string UnreachableAfterReturn = "UITKX0107";

        /// <summary>
        /// The component's markup has more than one root render node.
        /// A component must return a single root element; all siblings must be
        /// wrapped in a container element.
        /// Severity: Error.
        /// </summary>
        public const string MultipleRenderRoots = "UITKX0108";

        /// <summary>
        /// An attribute on an element is not part of that element's known prop set.
        /// Only reported when the element's attribute list is available.
        /// Severity: Warning.
        /// </summary>
        public const string UnknownAttribute = "UITKX0109";

        // UITKX0110 (UnreachableAfterBreakOrContinue) was removed: it was never
        // emitted and CANNOT be — @break has no AST node and @continue never
        // parses, so "markup after @break/@continue" is unrepresentable. The
        // code number stays retired.

        /// <summary>
        /// A component parameter is declared in the function-style header but
        /// never referenced in setup code or markup.
        /// Severity: Error.
        /// </summary>
        public const string UnusedParameter = "UITKX0111";

        /// <summary>
        /// A local variable is declared (and possibly assigned) but never read
        /// in the component's setup code or markup expressions.
        /// Detected via <c>SemanticModel.AnalyzeDataFlow()</c> on the virtual
        /// document's render method — catches cases that Roslyn's CS0219 misses
        /// (e.g. <c>new Style { … }</c> initialisers with side-effect constructors).
        /// Severity: Error.
        /// </summary>
        public const string UnusedVariable = "UITKX0112";

        /// <summary>
        /// Two or more <c>.uitkx</c> files in the same asmdef declare a
        /// <c>component</c> with the same name. The deterministic-first
        /// declarant wins for IntelliSense / go-to-definition; the others are
        /// shadowed.  Almost always the result of a copy-paste refactor that
        /// forgot to rename the component. Emitted by the lsp-server
        /// (DiagnosticsPublisher) once per (name, asmdef) pair against the
        /// FIRST declaration line in each duplicated file.
        /// Severity: Warning.
        /// </summary>
        public const string DuplicateComponent = "UITKX0113";

        /// <summary>
        /// Two component parameters collapse to the same PROP name: a leading
        /// underscore marks a parameter deliberately unused without renaming its
        /// public prop (<c>_count</c> and <c>count</c> both expose the prop
        /// <c>count</c>), so declaring both is ambiguous. The generated props
        /// class would also fail with CS0102. Anchored at the later parameter.
        /// Severity: Error.
        /// </summary>
        public const string DuplicatePropName = "UITKX0114";

        /// <summary>
        /// An <c>Asset&lt;T&gt;("path")</c>, <c>Ast&lt;T&gt;("path")</c>, or
        /// <c>@uss "path"</c> references a file that does not exist on disk.
        /// Severity: Error.
        /// </summary>
        public const string AssetNotFound = "UITKX0120";

        /// <summary>
        /// The requested type <c>T</c> in <c>Asset&lt;T&gt;("path")</c> is not
        /// compatible with the file extension (e.g. <c>Asset&lt;AudioClip&gt;("icon.png")</c>).
        /// Severity: Warning.
        /// </summary>
        public const string AssetTypeMismatch = "UITKX0121";

        // ── T2 — HMR-correctness diagnostics ─────────────────────────────────

        /// <summary>
        /// A field marked with <c>[UitkxHmrSwap]</c> (i.e. a <c>static readonly</c>
        /// field stripped by the SG so HMR can refresh its value) is being
        /// assigned outside of its declaration. Such writes silently regress
        /// on every HMR swap because the swapper copies the declaration
        /// initializer back over the slot.
        /// Severity: Warning. Emitted by the SourceGenerator analyzer
        /// <c>UitkxHmrSwapWriteAnalyzer</c>.
        /// </summary>
        public const string HmrSwapFieldWrite = "UITKX0210";

        /// <summary>
        /// A <c>const</c> field is declared inside a <c>module { ... }</c> body.
        /// Const values are inlined at IL emit time, so HMR edits to the value
        /// never propagate to consumers until a full domain reload. Use
        /// <c>static readonly</c> instead — the SG strips <c>readonly</c> and
        /// the HMR static-swapper refreshes the slot on every edit.
        /// RETIRED 0.16.0 with the legacy module grammar (plain-dialect member
        /// bodies have no field-position const); the ID stays reserved.
        /// </summary>
        public const string ConstInModule = "UITKX0211";

        // ── T2v — Version compatibility diagnostics (lsp-server) ─────────────
        // Produced by DiagnosticsPublisher, not DiagnosticsAnalyzer, because
        // version detection requires access to Unity project metadata.

        /// <summary>
        /// An element or style property requires a newer Unity version than the
        /// one detected in the project's <c>ProjectSettings/ProjectVersion.txt</c>.
        /// Severity: Warning (the runtime no-op fallback still works).
        /// </summary>
        public const string VersionMismatch = "UITKX0200";

        // ── T1 — Parser codes (emitted by UitkxParser / DirectiveParser) ─────
        // Listed here for cross-reference only; not produced by DiagnosticsAnalyzer.

        /// <summary>Unexpected token while parsing. Emitted by UitkxParser.</summary>
        public const string UnexpectedToken = "UITKX0300";

        /// <summary>Unclosed element tag. Emitted by UitkxParser.</summary>
        public const string UnclosedTag = "UITKX0301";

        /// <summary>Mismatched closing tag. Emitted by UitkxParser.</summary>
        public const string MismatchedTag = "UITKX0302";

        /// <summary>Unknown <c>@directive</c> keyword. Emitted by UitkxParser.</summary>
        public const string UnknownDirective = "UITKX0305";

        /// <summary>
        /// <c>@(expr)</c> syntax is not supported. Inline child expressions
        /// in markup must use <c>{expr}</c>; in raw C# setup code, assign to a
        /// local variable first. Emitted by <c>UitkxParser</c> (markup context)
        /// and <c>DirectiveParser</c> (setup-code context).
        /// </summary>
        public const string AtExprNotSupported = "UITKX0306";

        // ── Import / export family block — UITKX2300–2315 (frozen leg 1) ──────
        // Codes + messages are identical family-wide (Unreal/Godot/Unity) modulo the
        // UETKX|GUITKX|UITKX prefix and .uetkx/.guitkx/.uitkx extension. 2300–2309 are
        // frozen by the Unreal leg; 2310–2314 are Unity-local (registered into the
        // canonical family table); 2315 is reserved. Only 2304 is a Warning.

        /// <summary>``unknown import specifier `{0}` — no file at {1}(.uitkx)`` (also fires for engine-native specifiers).</summary>
        public const string UnknownImportSpecifier = "UITKX2300";

        /// <summary>`` `{0}` is not exported by {1} — add `export` to its declaration ``</summary>
        public const string NotExported = "UITKX2301";

        /// <summary>`` `{0}` is not declared in {1} ``</summary>
        public const string NotDeclaredInFile = "UITKX2302";

        /// <summary>``duplicate import of `{0}` (already imported from {1})``</summary>
        public const string DuplicateImport = "UITKX2303";

        /// <summary>``unused import `{0}` `` (Warning).</summary>
        public const string UnusedImport = "UITKX2304";

        /// <summary>`` `{0}` is defined in {1} but not imported — add: import {{ {0} }} from "{2}" ``</summary>
        public const string DefinedButNotImported = "UITKX2305";

        /// <summary>``value-import cycle: {0} (hooks/modules load eagerly — break the chain or move to component refs)``</summary>
        public const string ValueImportCycle = "UITKX2306";

        /// <summary>`` `{0}` is used like a uitkx component/hook but no file exports it `` (ambient C# exempt).</summary>
        public const string UsedButNoFileExports = "UITKX2307";

        /// <summary>``import crosses a module/root boundary ({0} -> {1}) — imports are module-scoped in v1`` (Unity boundary = asmdef).</summary>
        public const string ImportCrossesBoundary = "UITKX2308";

        /// <summary>``import must appear in the preamble, before the first declaration``</summary>
        public const string ImportNotInPreamble = "UITKX2309";

        /// <summary>`Cannot derive a namespace for '{0}' (no owning .asmdef); add @namespace.` (Unity-local).</summary>
        public const string CannotDeriveNamespace = "UITKX2310";

        /// <summary>`Export accessibility mismatch across parts merging into '{0}'.` (Unity-local).</summary>
        public const string ExportAccessibilityMismatch = "UITKX2311";

        /// <summary>`Hook container '{0}' merge conflict between '{1}' and '{2}' (duplicate hook / accessibility).` (Unity-local).</summary>
        public const string HookContainerMergeConflict = "UITKX2312";

        /// <summary>Reserved, INTENTIONALLY NOT EMITTED. The layout conventions it would have
        /// covered (one component per file, hooks in a <c>.hooks</c> file, filename == component)
        /// are documentation best-practices, not code-enforced diagnostics — a file may legally
        /// declare any mix of components/hooks/modules in any order. Kept as a reserved slot in
        /// the frozen family band so the number is never reused.</summary>
        public const string ConventionLint = "UITKX2313";

        /// <summary>`'~/' root is not configured or resolves outside the project ('{0}').` (Unity-local).</summary>
        public const string RootNotConfigured = "UITKX2314";

        // UITKX2315 — reserved (family).

        /// <summary>`unknown namespace '{0}' — no such namespace in this assembly or its references`
        /// (namespace-import unification plan; Unity-local). Fires on a <c>@using</c> or
        /// <c>import "@Ns"</c> whose (plain-namespace) payload does not resolve against the
        /// compilation's global namespace ∪ peer .uitkx namespaces. The namespace analogue of
        /// UITKX2300. <b>Warning</b> at build time (never breaks a valid build — the emitted
        /// <c>using</c>'s CS0246 remains the gate), <b>Error</b> in the editor (a located squiggle).</summary>
        public const string UnknownNamespace = "UITKX2316";

        /// <summary>`unused using '{0}'` (namespace-import unification plan; editor-only Hint,
        /// <c>Unnecessary</c> tag → faded). Fires when a <c>@using</c>/namespace-import contributes
        /// nothing to the file. Never emitted by the source generator (a redundant using is a no-op),
        /// never build-breaking.</summary>
        public const string UnusedUsing = "UITKX2317";

        // ── ES-modules family block — UITKX2320–2329 (ES-modules campaign) ────
        // Codes + messages are identical family-wide (Unreal/Godot/Unity) modulo the
        // UETKX|GUITKX|UITKX prefix. Harmonized 2026-07-17 to the family-canonical
        // (Unreal-audited) allocation — see Plans~/archive/ES_MODULES_EXECUTION_PLAN.md §3.1.
        // Never renumber unilaterally; a family renumber is a STOP AND ASK event.

        /// <summary>``the '{0}' wrapper keyword is deprecated — write a plain 'export' declaration
        /// (the UitkxMigrateImports --es-modules codemod rewrites it); the wrapper is removed in a
        /// later minor`` ({0} = <c>component</c> / <c>hook</c> / <c>module</c>). Severity: Warning.</summary>
        public const string DeprecatedWrapperKeyword = "UITKX2320";

        /// <summary>``'{0}' is 'use'-prefixed but returns VirtualNode — did you mean a component?
        /// (components are PascalCase and return VirtualNode)``. Severity: Error.</summary>
        public const string HookReturnsMarkup = "UITKX2321";

        /// <summary>``value export '{0}' cannot infer its type — the initializer must name the type
        /// ('= new T { … }'); otherwise declare 'export &lt;Type&gt; {0} = …'``. Severity: Error.
        /// Emitted in Unity (typed dialect); Godot registers this meaning but never emits it
        /// (recorded family divergence, §3.3).</summary>
        public const string ValueExportTypeInference = "UITKX2322";

        /// <summary>``{0} names '{1}', which is not a top-level declaration in this file`` ({0} =
        /// <c>'export default'</c> / <c>'export { … }'</c>). Severity: Error.</summary>
        public const string ExportOfUndeclaredName = "UITKX2323";

        /// <summary>``'{0}' is already exported — remove the duplicate export``. Severity: Error.</summary>
        public const string DuplicateExport = "UITKX2324";

        /// <summary>``import alias '{0}' collides with {1} — rename the import`` ({1} = <c>a
        /// declaration in this file</c> / <c>another import</c>). Severity: Error.</summary>
        public const string ImportAliasCollision = "UITKX2325";

        /// <summary>``'{0}' has no default export — use a named import: import { {1} } from "{2}"``
        /// ({0} = target file, {1} = suggested name, {2} = specifier). Severity: Error.</summary>
        public const string DefaultImportWithoutDefault = "UITKX2326";

        /// <summary>``duplicate 'export default' — a file has at most one default export``.
        /// Severity: Error.</summary>
        public const string DuplicateDefaultExport = "UITKX2327";

        // UITKX2328/2329 — reserved (family). Do not allocate.

        // ── Unity-local declaration band — UITKX2107–2110 (ES-modules campaign) ──
        // Unity-only concerns outside the family band (companion merge, mixed-style
        // mode ambiguity, migrated-target import gate, hook-rename guard). Emitted
        // as inline `Code = "UITKX21xx"` strings from DirectiveParser, same
        // convention as the existing 2100/2105/2106 codes; declared here for the
        // registry/docs surface only.

        /// <summary>``companion partial-class merging is deprecated — '{0}' merges into '{1}' via
        /// legacy folder namespaces; migrate the companion set to plain declarations and file
        /// imports``. Severity: Warning.
        /// RETIRED 0.16.0 with the legacy module grammar (the merge mechanism is gone);
        /// the ID stays reserved.</summary>
        public const string DeprecatedCompanionMerge = "UITKX2107";

        /// <summary>``legacy wrapper declarations and plain declarations cannot be mixed in one
        /// file — the file's first declaration sets its style``. Severity: Error. (Legal-with-
        /// warnings in Unreal/Godot — recorded family divergence, §3.3.)
        /// RETIRED 0.16.0 with the wrapper grammar: the mixed-mode parser sites now emit the
        /// UITKX2320 removal error instead (mode-independent, so mixed files still freeze
        /// under the codemod's migration parse); the ID stays reserved.</summary>
        public const string MixedDeclarationStyles = "UITKX2108";

        /// <summary>``namespace/default/renamed import of '{0}' requires the target file to use
        /// plain-declaration syntax — migrate '{1}' first``. Severity: Error.
        /// RETIRED 0.16.0 with the wrapper grammar (a legacy target now errors at
        /// parse, so the migrate-first gate has no subject); the ID stays reserved.</summary>
        public const string ImportFormNeedsMigratedTarget = "UITKX2109";

        /// <summary>``renaming hook '{0}' to '{1}' drops the 'use' prefix — hook bindings must stay
        /// 'use'-prefixed``. Severity: Error.</summary>
        public const string HookRenameDropsUsePrefix = "UITKX2110";
    }
}
