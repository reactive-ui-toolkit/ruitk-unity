using Microsoft.CodeAnalysis;

namespace Ruitk.SourceGenerator
{
    /// <summary>
    /// Central registry of all <see cref="DiagnosticDescriptor"/> instances used
    /// by the UITKX source generator.
    ///
    /// IDs in this file are aligned with the live analyzer's canonical codes
    /// (see <c>ide-extensions~/language-lib/Diagnostics/DiagnosticCodes.cs</c>)
    /// so that the same logical issue is reported with the same code regardless
    /// of whether it surfaces in the Unity Console (source generator) or in the
    /// VS Code Problems pane (live analyzer).
    ///
    /// ID ranges:
    ///   UITKX0001       Unknown built-in element                       (Phase 3, source-gen-only)
    ///   UITKX0005       (retired — @namespace/@component now optional; reserved)
    ///   UITKX0008       Unknown function component                      (Phase 3, source-gen-only)
    ///   UITKX0012       Directive ordering                              (Phase 5, source-gen-only)
    ///   UITKX0013–0015  Rules-of-Hooks violations                       (Phase 5, shared with analyzer)
    ///   UITKX0016       Hook in attribute / event handler               (Phase 5, shared with analyzer)
    ///   UITKX0018       UseEffect missing dependency array              (Phase 5, source-gen-only)
    ///   UITKX0019       Loop index used as element key                  (Phase 5, source-gen-only)
    ///   UITKX0020       ref={} on user component with no Ref<T> param
    ///   UITKX0021       ref={} on user component with multiple Ref<T> params (ambiguous)
    ///   UITKX0103       (retired — filename match is a documentation convention; reserved)
    ///   UITKX0104       Duplicate sibling key                           (aligns with analyzer)
    ///   UITKX0106       Element inside loop missing key                 (aligns with analyzer)
    ///   UITKX0108       Multiple root elements                          (aligns with analyzer)
    ///   UITKX0109       Unknown attribute on element                    (aligns with analyzer)
    ///   UITKX0120       Asset path references a non-existent file       (aligns with analyzer)
    ///   UITKX0121       Asset type parameter incompatible with extension (aligns with analyzer)
    ///   UITKX0300–0305  Parse errors
    /// </summary>
    internal static class UitkxDiagnostics
    {
        private const string Category = "Ruitk.Parser";

        // ── Phase 3 — tag resolution ──────────────────────────────────────────

        /// <summary>UITKX0001 — A lowercase tag name has no matching built-in V.* element.</summary>
        public static readonly DiagnosticDescriptor UnknownElement = new DiagnosticDescriptor(
            id: "UITKX0001",
            title: "Unknown built-in element",
            messageFormat: "Unknown element '<{0}>'. Check the tag name — "
                + "built-in elements use PascalCase (e.g. <Button>, <Label>).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The tag does not match any V.* built-in element."
        );

        /// <summary>UITKX0008 — A PascalCase tag has no matching component type in the compilation.</summary>
        public static readonly DiagnosticDescriptor UnknownComponent = new DiagnosticDescriptor(
            id: "UITKX0008",
            title: "Unknown function component",
            messageFormat: "Component '<{0}>' could not be found. "
                + "Ensure the type exists and has a public static Render method.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The PascalCase tag does not match any accessible type with a Render method."
        );

        // ── Directive validation ──────────────────────────────────────────────
        // UITKX0005 (missing @namespace/@component) and UITKX0103 (filename mismatch) are
        // RETIRED: @namespace is optional (path-derived) and function-style files need no
        // @component; filename==component is a documentation convention, not a diagnostic.
        // The code slots stay reserved so the numbers are never reused.

        // ── Parse errors ──────────────────────────────────────────────────────

        /// <summary>UITKX0300 — General unexpected-token error.</summary>
        public static readonly DiagnosticDescriptor UnexpectedToken = new DiagnosticDescriptor(
            id: "UITKX0300",
            title: "Unexpected token",
            messageFormat: "Unexpected '{0}' at line {1} in '{2}'. Expected {3}.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        /// <summary>UITKX0301 — An opening tag was never closed.</summary>
        public static readonly DiagnosticDescriptor UnclosedTag = new DiagnosticDescriptor(
            id: "UITKX0301",
            title: "Unclosed tag",
            messageFormat: "Tag '<{0}>' opened at line {1} in '{2}' was never closed",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        /// <summary>UITKX0302 — A closing tag doesn't match the open tag.</summary>
        public static readonly DiagnosticDescriptor MismatchedClosingTag = new DiagnosticDescriptor(
            id: "UITKX0302",
            title: "Mismatched closing tag",
            messageFormat: "Found '</{0}>' but expected '</{1}>' (opened at line {2}) in '{3}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        /// <summary>UITKX0303 — Unexpected end-of-file.</summary>
        public static readonly DiagnosticDescriptor UnexpectedEof = new DiagnosticDescriptor(
            id: "UITKX0303",
            title: "Unexpected end of file",
            messageFormat: "Unexpected end of file in '{0}': {1}",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        /// <summary>UITKX0304 — An expression or block was opened but never closed.</summary>
        public static readonly DiagnosticDescriptor UnclosedExpression = new DiagnosticDescriptor(
            id: "UITKX0304",
            title: "Unclosed expression or block",
            messageFormat: "Unclosed '{0}' at line {1} in '{2}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true
        );

        // ── Phase 5 — semantic diagnostics ────────────────────────────────────

        /// <summary>UITKX0109 — An attribute name is not a known property on the resolved Props type. Aligned with analyzer's <c>DiagnosticCodes.UnknownAttribute</c>.</summary>
        public static readonly DiagnosticDescriptor UnknownAttribute = new DiagnosticDescriptor(
            id: "UITKX0109",
            title: "Unknown attribute on element",
            messageFormat: "Unknown attribute '{0}' on <{1}>{2}",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "The attribute does not match any public settable property on the Props type. "
                + "On built-in elements this is checked against the element's *Props class. "
                + "On user components this is checked against the declared parameters — user components do not inherit BaseProps, so style/onClick/etc. must be explicitly declared as parameters to be accepted (key and ref are always allowed)."
        );

        /// <summary>UITKX0106 — A direct element child of a loop (@foreach/@for/@while) lacks a key attribute. Aligned with analyzer's <c>DiagnosticCodes.MissingKey</c>.</summary>
        public static readonly DiagnosticDescriptor ForeachMissingKey = new DiagnosticDescriptor(
            id: "UITKX0106",
            title: "Element inside loop missing key",
            messageFormat: "Element <{0}> inside a loop at line {1} in '{2}' should have a 'key' attribute for stable reconciliation",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Add a 'key' attribute whose value is unique within the collection to avoid reconciler instability."
        );

        /// <summary>UITKX0104 — Two or more sibling elements share the same static key literal. Aligned with analyzer's <c>DiagnosticCodes.DuplicateKey</c>.</summary>
        public static readonly DiagnosticDescriptor DuplicateSiblingKey = new DiagnosticDescriptor(
            id: "UITKX0104",
            title: "Duplicate key among sibling elements",
            messageFormat: "Duplicate key '{0}' found among sibling elements in '{1}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Each sibling should have a unique key to allow the reconciler to track elements across renders."
        );

        /// <summary>UITKX0113 — a component name is declared more than once. Shares the id +
        /// <b>Warning</b> severity of the LSP's cross-file duplicate-component diagnostic; this
        /// is the same-file variant. The generator recovers by skipping the duplicate (so the
        /// file still compiles with the first declaration), which is why it is a warning rather
        /// than an error.</summary>
        public static readonly DiagnosticDescriptor DuplicateComponentInFile = new DiagnosticDescriptor(
            id: "UITKX0113",
            title: "Duplicate component declaration",
            messageFormat: "Component '{0}' is declared more than once; the duplicate is ignored",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Each component name should be unique; the generator emits only the first declaration and ignores later ones."
        );

        /// <summary>UITKX0012 — @namespace must be declared before @component.</summary>
        public static readonly DiagnosticDescriptor DirectiveOrderError = new DiagnosticDescriptor(
            id: "UITKX0012",
            title: "Directive declared out of order",
            messageFormat: "'@namespace' must be declared before '@component' in '{0}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Place @namespace on the first line, followed by @component."
        );

        // ── Rules of Hooks ────────────────────────────────────────────────────

        /// <summary>UITKX0016 — A hook call appears inside an attribute expression (e.g. event handler lambda).</summary>
        public static readonly DiagnosticDescriptor HookInEventHandler = new DiagnosticDescriptor(
            id: "UITKX0016",
            title: "Hook called inside attribute expression",
            messageFormat: "Hook '{0}' at line {1} is inside an attribute expression (e.g. an event handler). "
                + "Hooks must be called unconditionally at component top level.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Move the hook call into the @code block at the top of the file."
        );

        /// <summary>UITKX0108 — Component has more than one root element node. Aligned with analyzer's <c>DiagnosticCodes.MultipleRenderRoots</c>.</summary>
        public static readonly DiagnosticDescriptor MultipleRootElements = new DiagnosticDescriptor(
            id: "UITKX0108",
            title: "Multiple root elements",
            messageFormat: "Component in '{0}' has more than one root element. Wrap them in a single container.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "A component must return a single root virtual node."
        );

        /// <summary>UITKX0018 — UseEffect called with only a callback and no dependency array.</summary>
        public static readonly DiagnosticDescriptor UseEffectMissingDeps = new DiagnosticDescriptor(
            id: "UITKX0018",
            title: "UseEffect missing dependency array",
            messageFormat: "Hooks.UseEffect at line {0} in '{1}' has no dependency array — it will run on every render. "
                + "Pass an explicit array (or Array.Empty<object>()) as the second argument.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Add a dependency array to control when the effect re-runs, or pass an empty array to run only once."
        );

        /// <summary>UITKX0019 — The loop iterator variable is used directly as a key inside @foreach.</summary>
        public static readonly DiagnosticDescriptor IndexAsKey = new DiagnosticDescriptor(
            id: "UITKX0019",
            title: "Loop variable used as element key",
            messageFormat: "Element <{0}> uses the loop variable '{1}' directly as its key at line {2} in '{3}'. "
                + "Use a stable unique identifier from the item instead.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Using the loop iterator variable as a key causes reconciler instability when the list is reordered or filtered."
        );

        /// <summary>UITKX0013 — A hook call appears inside an @if / @else body.</summary>
        public static readonly DiagnosticDescriptor HookInConditional = new DiagnosticDescriptor(
            id: "UITKX0013",
            title: "Hook called inside conditional",
            messageFormat: "Hook '{0}' at line {1} is inside an '@if' branch. "
                + "Hooks must be called unconditionally at component top level.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Move the hook call into the @code block at the top of the file."
        );

        /// <summary>UITKX0014 — A hook call appears inside a @foreach body.</summary>
        public static readonly DiagnosticDescriptor HookInLoop = new DiagnosticDescriptor(
            id: "UITKX0014",
            title: "Hook called inside loop",
            messageFormat: "Hook '{0}' at line {1} is inside a '@foreach' loop. "
                + "Hooks must be called unconditionally at component top level.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Move the hook call into the @code block at the top of the file."
        );

        /// <summary>UITKX0015 — A hook call appears inside a @switch body.</summary>
        public static readonly DiagnosticDescriptor HookInSwitch = new DiagnosticDescriptor(
            id: "UITKX0015",
            title: "Hook called inside switch",
            messageFormat: "Hook '{0}' at line {1} is inside a '@switch' case. "
                + "Hooks must be called unconditionally at component top level.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Move the hook call into the @code block at the top of the file."
        );

        // ── ref-as-prop routing ───────────────────────────────────────────────

        /// <summary>
        /// UITKX0020 — <c>ref={...}</c> was used on a user component that declares no
        /// <c>Ref&lt;T&gt;</c> parameter (or the deprecated <c>Hooks.MutableRef&lt;T&gt;</c>),
        /// so the ref cannot be routed.
        /// </summary>
        public static readonly DiagnosticDescriptor RefOnComponentWithNoRefParam =
            new DiagnosticDescriptor(
                id: "UITKX0020",
                title: "ref attribute on component with no Ref<T> parameter",
                messageFormat: "Component '{0}' does not declare a 'Ref<T>' parameter. "
                    + "Remove 'ref={{...}}' or add a 'Ref<T>?' parameter to the component.",
                category: Category,
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: """
                The ref-as-prop pattern requires the target component to accept a
                Ref<T> parameter so the emitter knows which prop to populate.
                Either remove the ref attribute or declare a suitable parameter in the component.
                (Hooks.MutableRef<T> is deprecated; use Ref<T> obtained via Hooks.UseRef<T>().)
                """
            );

        /// <summary>
        /// UITKX0021 — <c>ref={...}</c> was used on a user component that declares <em>multiple</em>
        /// <c>Ref&lt;T&gt;</c> parameters (or deprecated <c>Hooks.MutableRef&lt;T&gt;</c>);
        /// the emitter cannot determine which one to populate without an explicit prop name.
        /// </summary>
        public static readonly DiagnosticDescriptor RefOnComponentWithAmbiguousRefParam =
            new DiagnosticDescriptor(
                id: "UITKX0021",
                title: "ref attribute is ambiguous — component has multiple Ref<T> parameters",
                messageFormat: "Component '{0}' declares multiple 'Ref<T>' parameters. "
                    + "Use an explicit prop name (e.g. inputRef={{x}}) instead of the 'ref={{...}}' shorthand.",
                category: Category,
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: """
                The ref={} shorthand can only be used when the target component has exactly one
                Ref<T> parameter.  When multiple Ref<T> parameters exist, pass
                the ref by its explicit prop name to avoid ambiguity.
                (Hooks.MutableRef<T> is deprecated; use Ref<T> obtained via Hooks.UseRef<T>().)
                """
            );

        /// <summary>
        /// UITKX0120 — An <c>Asset&lt;T&gt;("path")</c> or <c>Ast&lt;T&gt;("path")</c>
        /// expression references a file that does not exist on disk at compile time.
        /// Aligned with analyzer's <c>DiagnosticCodes.AssetNotFound</c>.
        /// </summary>
        public static readonly DiagnosticDescriptor AssetFileNotFound =
            new DiagnosticDescriptor(
                id: "UITKX0120",
                title: "Asset file not found",
                messageFormat: "Asset file not found: \"{0}\"",
                category: Category,
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: "The referenced asset path does not exist on disk. Check the path for typos or ensure the file has been imported into the project."
            );

        /// <summary>
        /// UITKX0121 — The requested <c>Asset&lt;T&gt;</c> type is not compatible
        /// with the file extension of the referenced asset.
        /// Aligned with analyzer's <c>DiagnosticCodes.AssetTypeMismatch</c>.
        /// </summary>
        public static readonly DiagnosticDescriptor AssetTypeMismatch =
            new DiagnosticDescriptor(
                id: "UITKX0121",
                title: "Asset type mismatch",
                messageFormat: "Type '{0}' is not compatible with '{1}' files. Valid types: {2}",
                category: Category,
                defaultSeverity: DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: "The generic type argument does not match the file extension. For example, a .png should be loaded as Texture2D or Sprite, not AudioClip."
            );

        // ── Module HMR rewrite ───────────────────────────────────────────────

        /// <summary>
        /// UITKX0150 — The Roslyn parse of a module body failed, so the module
        /// emitter fell back to verbatim emission. The module still compiles;
        /// only per-method HMR for that module is unavailable until the parse
        /// error is resolved. Severity is Info — verbatim emit is a graceful
        /// fallback, not a build break.
        /// </summary>
        public static readonly DiagnosticDescriptor ModuleBodyParseFailed =
            new DiagnosticDescriptor(
                id: "UITKX0150",
                title: "Module body could not be parsed for HMR rewrite",
                messageFormat:
                    "Module '{0}' body could not be parsed for HMR method-trampoline rewrite ({1}). Falling back to verbatim emission — the module still compiles, but per-method HMR for this module is unavailable until the parse error is fixed.",
                category: Category,
                defaultSeverity: DiagnosticSeverity.Info,
                isEnabledByDefault: true,
                description: "The source generator wraps each module body in a synthetic class and parses it with Roslyn so it can rewrite each top-level static method into a trampoline + delegate field + body method (the pattern that enables HMR). If parsing fails — usually because of a syntactic issue with the user code — the emitter falls back to writing the body verbatim, which is what the pre-HMR emitter always did."
            );
    }
}
