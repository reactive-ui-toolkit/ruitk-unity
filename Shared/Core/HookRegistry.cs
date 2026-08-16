// SPDX-License-Identifier: LicenseRef-ReactiveUI-Community-1.0
// ReactiveUIToolkit — see THIRDPARTY.md
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;

namespace Ruitk.Core
{
    /// <summary>
    /// Single source of truth for hook metadata across every tooling layer in
    /// the package.  Eliminates the eight historically-drift-prone hand-edited
    /// tables that previously had to be kept in lockstep across the source
    /// generator, the HMR emitter, the IDE diagnostics analyzer, the LSP hover
    /// handler, and the IDE virtual-document generator.
    ///
    /// <para>
    /// <b>Layered consumption.</b>  The file is linked into three csproj builds
    /// in addition to its native Unity compile, with each consumer reading
    /// from a different accessor:
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <b>Unity runtime</b> — compiled into <c>Ruitk.Shared.dll</c>.
    ///     Wrapped in <c>#if UNITY_EDITOR</c> so the registry is stripped from
    ///     player builds (the Shared asmdef has no <c>defineConstraints</c> and
    ///     ships every <c>Shared/Core/</c> file into all players by default).
    ///   </item>
    ///   <item>
    ///     <b>Source generator</b> — linked into
    ///     <c>Ruitk.SourceGenerator.csproj</c> via <c>&lt;Compile Include
    ///     Link&gt;</c>.  That csproj defines <c>UNITY_EDITOR</c> at build time
    ///     so the registry compiles into the analyzer DLL.
    ///   </item>
    ///   <item>
    ///     <b>IDE language library</b> — linked into
    ///     <c>ide-extensions~/language-lib/UitkxLanguage.csproj</c> the same
    ///     way; that csproj also defines <c>UNITY_EDITOR</c>.  The LSP server
    ///     (<c>Ruitk.LanguageServer</c>) consumes the registry via the
    ///     language-lib DLL reference, which is why the type is
    ///     <see langword="public"/> rather than <see langword="internal"/>.
    ///   </item>
    /// </list>
    ///
    /// <para>
    /// <b>Performance contract.</b>  Every accessor returns a <i>cached</i>
    /// field reference.  No accessor ever allocates or rebuilds on call.  This
    /// matters because <see cref="GetValidationPatterns"/> is invoked per
    /// attribute scan in the IDE diagnostics analyzer (per keystroke).
    /// A unit test asserts repeated calls return the same reference.
    /// </para>
    ///
    /// <para>
    /// <b>Adding a hook.</b>  When a new hook is added to
    /// <c>Shared/Core/Hooks.cs</c>, update exactly one place — this file —
    /// in six sub-steps:
    /// </para>
    /// <list type="number">
    ///   <item>Add the canonical PascalCase name to <see cref="s_signatureOrder"/>
    ///     (controls signature regex and validation pattern order).</item>
    ///   <item>Add it to <see cref="s_aliasOrder"/> (controls alias-table
    ///     replacement order — this is the order the SG emitter walks).</item>
    ///   <item>If the hook accepts generic type arguments, add it to
    ///     <see cref="s_genericOrder"/>.</item>
    ///   <item>Add one stub line (or one per overload) into
    ///     <see cref="HookStubsStaticForm"/> AND
    ///     <see cref="HookStubsInstanceForm"/>, preserving the existing
    ///     emission order.</item>
    ///   <item>Add two hover-doc entries (camelCase shorthand and qualified
    ///     <c>Hooks.UseFoo</c>) to <see cref="BuildDocMap"/>.</item>
    ///   <item>Add a <see cref="s_callSiteTable"/> row: the FIBER-path
    ///     <c>HookStates</c> slot count (what the Builder's STATE panel
    ///     indexes) and the call-site insertion snippet (what the Builder's
    ///     palette drops into a component body).</item>
    /// </list>
    /// <para>
    /// Then bump the <c>Registry_HookCount_IsExpected</c> test constant.  The
    /// test suite gates every other consumer site automatically against the
    /// golden snapshots under
    /// <c>SourceGenerator~/Tests/Golden/HookRegistry/</c>.
    /// </para>
    /// </summary>
#if RUITK_EDITOR_VARIANT
    // Ruitk.Language.Editor only: the Builder compile-references both this DLL
    // and Ruitk.Shared (which compiles this same linked file), and two public
    // HookRegistry types are CS0433 at every Builder call site. Internal here;
    // the in-assembly analyzer/virtual-doc consumers are unaffected.
    internal static class HookRegistry
#else
    public static class HookRegistry
#endif
    {
        // ── Canonical hook lists ─────────────────────────────────────────────
        //
        // Three separate orderings are deliberately preserved because each
        // controls the byte-exact output of a different consumer:
        //
        //   * s_signatureOrder — order in the signature regex and validation
        //     pattern table.  Changing it would re-shape the regex string,
        //     which is captured in the signature_regex.golden.txt fixture.
        //
        //   * s_aliasOrder — order in which CSharpEmitter.ApplyHookAliases
        //     iterates over the (from, to) replacement tuples.  Hook names
        //     don't overlap, so this is functionally insensitive, but the
        //     golden table fixes the order for review-diff stability.
        //
        //   * s_genericOrder — subset that appears in the generic-call regex
        //     (i.e. hooks with at least one <T>-bearing overload).  Same
        //     stability rationale.
        //
        // The lists are PRIVATE fields, not exposed APIs.  Accessor methods
        // build derived caches from them once at type init.

        /// <summary>
        /// PascalCase hook names in the order used by the signature regex
        /// and the validation pattern table.  Matches the pre-0.5.23
        /// CSharpEmitter.s_hookSignatureRe ordering byte-for-byte.
        /// </summary>
        private static readonly string[] s_signatureOrder =
        {
            "UseState", "UseEffect", "UseLayoutEffect", "UseRef", "UseCallback",
            "UseMemo", "UseContext", "UseReducer", "UseSignal", "UseDeferredValue",
            "UseTransition", "UseSafeArea", "UseStableFunc", "UseStableAction",
            "UseStableCallback", "UseImperativeHandle", "UseAnimate", "UseTweenFloat",
            "UseUiDocumentRoot", "UseSfx", "ProvideContext",
        };

        /// <summary>
        /// PascalCase hook names in the order used by the alias-replacement
        /// table (camelCase shorthand → <c>Hooks.UseFoo</c>).  Matches the
        /// pre-0.5.23 CSharpEmitter.s_hookAliases ordering byte-for-byte.
        /// </summary>
        private static readonly string[] s_aliasOrder =
        {
            "UseState", "UseEffect", "UseLayoutEffect", "UseRef", "UseCallback",
            "UseMemo", "UseContext", "UseReducer", "UseSignal", "UseDeferredValue",
            "UseTransition", "UseSfx", "UseUiDocumentRoot", "UseSafeArea",
            "UseStableFunc", "UseStableAction", "UseStableCallback",
            "UseImperativeHandle", "UseAnimate", "UseTweenFloat", "ProvideContext",
        };

        /// <summary>
        /// PascalCase names of hooks that accept generic type arguments.
        /// Used to build the generic-form alias regex.  Matches the pre-0.5.23
        /// CSharpEmitter.s_genericHookAliasRe ordering byte-for-byte.
        /// </summary>
        private static readonly string[] s_genericOrder =
        {
            "UseState", "UseEffect", "UseLayoutEffect", "UseRef", "UseCallback",
            "UseMemo", "UseContext", "UseReducer", "UseSignal", "UseDeferredValue",
            "UseTransition", "UseStableFunc", "UseStableAction", "UseImperativeHandle",
        };

        /// <summary>
        /// Validation-pattern hook order, used by HooksValidator and
        /// DiagnosticsAnalyzer.  Pre-0.5.23 these tables had only 20 entries
        /// (missing <c>UseLayoutEffect</c>); the registry adds it in via
        /// <see cref="s_signatureOrder"/>, which expands UITKX0013–0016 to
        /// also catch conditional/looping <c>useLayoutEffect</c> calls.  This
        /// is a pure coverage expansion — no legitimate code breaks.
        /// </summary>
        private static readonly string[] s_validationOrder = s_signatureOrder;

        // ── Public accessors (return cached references — DO NOT MUTATE) ──────

        /// <summary>
        /// Camel-to-Pascal alias table for the source generator's
        /// <c>ApplyHookAliases</c> string-replacement pass.  Each tuple is
        /// (e.g. <c>"useState("</c>, <c>"Hooks.UseState("</c>).  Returned
        /// array is the SAME cached reference on every call; consumers MUST
        /// NOT mutate it.
        /// </summary>
        public static (string From, string To)[] GetAliasTable() => s_aliasCache;

        /// <summary>
        /// Raw regex pattern string matching any hook call site — both bare
        /// <c>useState(</c> shorthand and qualified <c>Hooks.UseState(</c>
        /// forms.  Group 1 captures the hook name without the <c>Hooks.</c>
        /// prefix.  Consumers wrap this string in their own
        /// <c>new Regex(..., RegexOptions.Compiled)</c>; the registry does
        /// not own a shared <c>Regex</c> instance because consumers compile
        /// regexes per call site for cold-start determinism.
        /// </summary>
        public static string GetSignatureRegexPattern() => s_signaturePatternCache;

        /// <summary>
        /// Raw regex pattern string matching generic-form hook calls (e.g.
        /// <c>useState&lt;int&gt;(</c>).  Group 1 is the hook name; group 2
        /// is the angle-bracketed type-argument list including the brackets.
        /// Same caching/contract as <see cref="GetSignatureRegexPattern"/>.
        /// </summary>
        public static string GetGenericHookPattern() => s_genericPatternCache;

        /// <summary>
        /// Hover-documentation map keyed by both camelCase shorthand
        /// (<c>"useState"</c>) and qualified PascalCase
        /// (<c>"Hooks.UseState"</c>).  Values are pre-formatted Markdown
        /// strings.  Returned dictionary is the SAME cached reference on every
        /// call; consumers MUST NOT mutate it.
        /// </summary>
        public static IReadOnlyDictionary<string, string> GetDocMap() => s_docMapCache;

        /// <summary>
        /// Validation-pattern array used by the rules-of-hooks analyzer.
        /// Contains three forms per hook in this order: <c>Hooks.UseFoo(</c>,
        /// <c>UseFoo(</c>, <c>useFoo(</c>.  Returned array is the SAME cached
        /// reference on every call.  This is a per-keystroke hot path —
        /// allocation here would be observed as IDE typing lag.
        /// </summary>
        public static string[] GetValidationPatterns() => s_validationPatternsCache;

        /// <summary>
        /// Pre-rendered hook-stub block injected by the IDE virtual-document
        /// generator into every UITKX virtual C# document.  The
        /// <paramref name="staticForm"/> flag picks between the
        /// <c>private static</c> form (used by the static-container hook
        /// document) and the <c>private</c> instance form (used by the
        /// function-style component document).  Both forms are byte-identical
        /// to the pre-0.5.23 hand-maintained scaffold blocks.
        /// </summary>
        public static string GenerateVirtualDocStubs(bool staticForm) =>
            staticForm ? s_staticStubsCache : s_instanceStubsCache;

        /// <summary>
        /// FIBER-path <c>FunctionComponentState.HookStates</c> slot count per
        /// hook, keyed by BOTH casings.  This is what positional STATE-panel
        /// indexing must use: effects live in separate lists (0), UseContext
        /// stores in ContextDependencies (0), and the four metadata-gated
        /// hooks (UseSafeArea, UseStableFunc/Action/Callback) early-return
        /// under a fiber owner and consume 0 there despite taking a slot on
        /// the legacy metadata path.  Returned dictionary is the SAME cached
        /// reference on every call; consumers MUST NOT mutate it.
        /// </summary>
        public static IReadOnlyDictionary<string, int> GetStateSlotArity() =>
            s_stateSlotArityCache;

        /// <summary>
        /// Call-site declaration snippet per hook (camelCase shorthand form,
        /// compilable against the virtual-doc stub signatures), keyed by BOTH
        /// casings — the single source the Builder's palette inserts from.
        /// Returned dictionary is the SAME cached reference on every call;
        /// consumers MUST NOT mutate it.
        /// </summary>
        public static IReadOnlyDictionary<string, string> GetInsertionSnippets() =>
            s_insertionSnippetCache;

        // ── Per-hook call-site table (fiber slot arity + insertion snippet) ──

        private static readonly (string Name, int FiberSlots, string Snippet)[] s_callSiteTable =
        {
            ("UseState", 1, "var (value, setValue) = useState(0);"),
            ("UseEffect", 0, "useEffect(() => { return null; }, null);"),
            ("UseLayoutEffect", 0, "useLayoutEffect(() => { return null; }, null);"),
            ("UseRef", 1, "var elRef = useRef<UnityEngine.UIElements.VisualElement?>(null);"),
            ("UseCallback", 1, "var callback = useCallback(() => 0, null);"),
            ("UseMemo", 1, "var memo = useMemo(() => 0, null);"),
            ("UseContext", 0, "var context = useContext<object>(\"key\");"),
            ("UseReducer", 1, "var (state, dispatch) = useReducer<int, int>((s, a) => s, 0);"),
            ("UseSignal", 1, "var value = useSignal<int>(\"key\", 0);"),
            ("UseDeferredValue", 1, "var deferred = useDeferredValue(0, null);"),
            ("UseTransition", 1, "var (isPending, startTransition) = useTransition();"),
            ("UseSafeArea", 0, "var safeArea = useSafeArea();"),
            ("UseStableFunc", 0, "var stableFunc = useStableFunc(() => 0);"),
            ("UseStableAction", 0, "var stableAction = useStableAction<int>(_ => { });"),
            ("UseStableCallback", 0, "var stableCallback = useStableCallback(() => { });"),
            ("UseImperativeHandle", 1, "var handle = useImperativeHandle(() => new object(), null);"),
            ("UseAnimate", 1, "useAnimate(null);"),
            ("UseTweenFloat", 1,
                "useTweenFloat(0f, 1f, 0.3f, Ruitk.Core.Animation.Ease.Linear, 0f, v => { }, null);"),
            ("UseUiDocumentRoot", 1, "var docRoot = useUiDocumentRoot(\"context\");"),
            ("UseSfx", 1, "var playSfx = useSfx();"),
            ("ProvideContext", 0, "provideContext(\"key\", null);"),
        };

        // ── Cached fields ────────────────────────────────────────────────────

        private static readonly (string, string)[] s_aliasCache             = BuildAliases();
        private static readonly string             s_signaturePatternCache  = BuildSignaturePattern();
        private static readonly string             s_genericPatternCache    = BuildGenericPattern();
        private static readonly Dictionary<string, string> s_docMapCache    = BuildDocMap();
        private static readonly string[]           s_validationPatternsCache = BuildValidationPatterns();
        private static readonly string             s_staticStubsCache       = HookStubsStaticForm;
        private static readonly string             s_instanceStubsCache     = HookStubsInstanceForm;
        private static readonly Dictionary<string, int> s_stateSlotArityCache = BuildStateSlotArity();
        private static readonly Dictionary<string, string> s_insertionSnippetCache = BuildInsertionSnippets();

        private static Dictionary<string, int> BuildStateSlotArity()
        {
            var map = new Dictionary<string, int>(s_callSiteTable.Length * 2);
            foreach (var (name, slots, _) in s_callSiteTable)
            {
                map[name] = slots;
                map[CamelOf(name)] = slots;
            }
            return map;
        }

        private static Dictionary<string, string> BuildInsertionSnippets()
        {
            var map = new Dictionary<string, string>(s_callSiteTable.Length * 2);
            foreach (var (name, _, snippet) in s_callSiteTable)
            {
                map[name] = snippet;
                map[CamelOf(name)] = snippet;
            }
            return map;
        }

        // ── Builders ─────────────────────────────────────────────────────────

        private static string CamelOf(string pascal)
        {
            // UseState → useState; ProvideContext → provideContext.
            // All hook names have an uppercase first letter; lowercase it.
            return char.ToLower(pascal[0]) + pascal.Substring(1);
        }

        private static (string, string)[] BuildAliases()
        {
            var arr = new (string, string)[s_aliasOrder.Length];
            for (int i = 0; i < s_aliasOrder.Length; i++)
            {
                string pascal = s_aliasOrder[i];
                arr[i] = (CamelOf(pascal) + "(", "Hooks." + pascal + "(");
            }
            return arr;
        }

        private static string BuildSignaturePattern()
        {
            // Pattern shape (exactly as captured in signature_regex.golden.txt):
            //   (?:Hooks\.)?\b(camel1|camel2|...|Pascal1|Pascal2|...)(?:<[^>]*>)?\s*\(
            var sb = new StringBuilder(512);
            sb.Append(@"(?:Hooks\.)?\b(");
            for (int i = 0; i < s_signatureOrder.Length; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(CamelOf(s_signatureOrder[i]));
            }
            for (int i = 0; i < s_signatureOrder.Length; i++)
            {
                sb.Append('|');
                sb.Append(s_signatureOrder[i]);
            }
            sb.Append(@")(?:<[^>]*>)?\s*\(");
            return sb.ToString();
        }

        private static string BuildGenericPattern()
        {
            // Pattern shape (exactly as captured in generic_alias_regex.golden.txt):
            //   \b(camel1|camel2|...)(<(?:[^<>]|<(?:[^<>]|<[^<>]*>)*>)*>)\s*\(
            // The angle-bracket group permits up to 3 levels of nested type
            // arguments (e.g. useMemo<Dictionary<string, List<Color>>>(...).
            var sb = new StringBuilder(256);
            sb.Append(@"\b(");
            for (int i = 0; i < s_genericOrder.Length; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(CamelOf(s_genericOrder[i]));
            }
            sb.Append(@")(<(?:[^<>]|<(?:[^<>]|<[^<>]*>)*>)*>)\s*\(");
            return sb.ToString();
        }

        private static string[] BuildValidationPatterns()
        {
            // Three forms per hook in this order:
            //   Hooks.UseFoo(
            //   UseFoo(
            //   useFoo(
            // (Matches the pre-0.5.23 HooksValidator.s_hookPatterns layout.)
            int n = s_validationOrder.Length;
            var arr = new string[n * 3];
            int idx = 0;
            for (int i = 0; i < n; i++)
                arr[idx++] = "Hooks." + s_validationOrder[i] + "(";
            for (int i = 0; i < n; i++)
                arr[idx++] = s_validationOrder[i] + "(";
            for (int i = 0; i < n; i++)
                arr[idx++] = CamelOf(s_validationOrder[i]) + "(";
            return arr;
        }

        private static Dictionary<string, string> BuildDocMap()
        {
            // Markdown strings lifted byte-identical from the pre-0.5.23
            // HoverHandler.s_hookDocs dictionary (lines 555-700 in that file).
            // The golden snapshot hover_docs.golden.json gates this map for
            // drift detection — adding or editing an entry here MUST be
            // accompanied by a matching regeneration of the golden file.
            //
            // useLayoutEffect entries (camel + qualified) are NEW in 0.5.23 —
            // they fill the historical gap noted in the validation_order doc
            // comment above.  The minimal markdown follows the same shape as
            // the existing entries (heading, shorthand banner, single-line
            // signature description, code-fence example).
            var d = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["useState"] =
                    "## `useState<T>(initialValue)`\n\n**Shorthand for `Hooks.UseState`.** Returns a `(value, setter)` tuple.  \nCall `setter(newValue)` to schedule a re-render with the new state.\n\n```csharp\nvar (count, setCount) = useState(0);\n```",
                ["Hooks.UseState"] =
                    "## `Hooks.UseState<T>(initialValue)`\n\nReturns a `(value, setter)` tuple.  \nCall `setter(newValue)` to schedule a re-render with the new state.\n\n```csharp\nvar (count, setCount) = Hooks.UseState(0);\n```",
                ["useEffect"] =
                    "## `useEffect(action, deps?)`\n\n**Shorthand for `Hooks.UseEffect`.** Runs `action` after each render.  \nPass a `deps` array to run only when those values change.\n\n```csharp\nuseEffect(() => { /* side-effect */ }, new object[] { count });\n```",
                ["Hooks.UseEffect"] =
                    "## `Hooks.UseEffect(action, deps?)`\n\nRuns `action` after each render.  \nPass a `deps` array to run only when those values change.\n\n```csharp\nHooks.UseEffect(() => { /* side-effect */ }, new object[] { count });\n```",
                ["useRef"] =
                    "## `useRef<T>(initialValue?)`\n\n**Shorthand for `Hooks.UseRef`.** Returns a mutable ref object whose `.Current` persists across re-renders without causing a re-render on write.",
                ["Hooks.UseRef"] =
                    "## `Hooks.UseRef<T>(initialValue?)`\n\nReturns a mutable ref object whose `.Current` persists across re-renders without causing a re-render on write.",
                ["useMemo"] =
                    "## `useMemo<T>(factory, deps)`\n\n**Shorthand for `Hooks.UseMemo`.** Returns a memoised value. Re-computes `factory()` only when `deps` change.",
                ["Hooks.UseMemo"] =
                    "## `Hooks.UseMemo<T>(factory, deps)`\n\nReturns a memoised value. Re-computes `factory()` only when `deps` change.",
                ["useCallback"] =
                    "## `useCallback(fn, deps)`\n\n**Shorthand for `Hooks.UseCallback`.** Returns a memoised delegate. Re-creates `fn` only when `deps` change.",
                ["Hooks.UseCallback"] =
                    "## `Hooks.UseCallback(fn, deps)`\n\nReturns a memoised delegate. Re-creates `fn` only when `deps` change.",
                ["useSignal"] =
                    "## `useSignal<T>(initialValue)`\n\n**Shorthand for `Hooks.UseSignal`.** Like `useState` but backed by a reactive signal — updates propagate without a full re-render.",
                ["Hooks.UseSignal"] =
                    "## `Hooks.UseSignal<T>(initialValue)`\n\nLike `UseState` but backed by a reactive signal — updates propagate without a full re-render.",
                ["useContext"] =
                    "## `useContext<T>()`\n\n**Shorthand for `Hooks.UseContext`.** Reads the nearest context value of type `T` provided by a parent component.",
                ["Hooks.UseContext"] =
                    "## `Hooks.UseContext<T>()`\n\nReads the nearest context value of type `T` provided by a parent component.",
                ["useReducer"] =
                    "## `useReducer<TState, TAction>(reducer, initialState)`\n\n**Shorthand for `Hooks.UseReducer`.** Returns `(state, dispatch)`. Calls `reducer(state, action)` on each `dispatch(action)`.",
                ["Hooks.UseReducer"] =
                    "## `Hooks.UseReducer<TState, TAction>(reducer, initialState)`\n\nReturns `(state, dispatch)`. Calls `reducer(state, action)` on each `dispatch(action)`.",
                ["useDeferredValue"] =
                    "## `useDeferredValue<T>(value, deps?)`\n\n**Shorthand for `Hooks.UseDeferredValue`.** Returns the value, deferred via batched effect when deps change. UITKX renders synchronously, so the value is returned immediately and tracked for dependency validation.\n\n```csharp\nvar deferred = useDeferredValue(searchTerm, new object[] { searchTerm });\n```",
                ["Hooks.UseDeferredValue"] =
                    "## `Hooks.UseDeferredValue<T>(value, deps?)`\n\nReturns the value, deferred via batched effect when deps change. UITKX renders synchronously, so the value is returned immediately and tracked for dependency validation.\n\n```csharp\nvar deferred = Hooks.UseDeferredValue(searchTerm, new object[] { searchTerm });\n```",
                ["useTransition"] =
                    "## `useTransition()`\n\n**Shorthand for `Hooks.UseTransition`.** Returns `(isPending, startTransition)`.\n\n> **UITKX note:** UITKX has no concurrent renderer. `isPending` is **always `false`** and `startTransition(action)` runs `action` synchronously. Provided for source compatibility with React.\n\n```csharp\nvar (isPending, startTransition) = useTransition();\nstartTransition(() => setSlowValue(newValue));\n```",
                ["Hooks.UseTransition"] =
                    "## `Hooks.UseTransition()`\n\nReturns `(isPending, startTransition)` matching React's API surface.\n\n> **UITKX note:** synchronous rendering only. `isPending` is always `false` and the callback runs synchronously.",
                ["useImperativeHandle"] =
                    "## `useImperativeHandle<THandle>(factory, deps?)`\n\n**Shorthand for `Hooks.UseImperativeHandle`.** Returns the handle produced by `factory()`, re-created only when `deps` change. Useful for exposing imperative APIs to a parent component via a `Ref`.\n\n```csharp\nuseImperativeHandle(() => new MyHandle(...), new object[] { dep });\n```",
                ["Hooks.UseImperativeHandle"] =
                    "## `Hooks.UseImperativeHandle<THandle>(factory, deps?)`\n\nReturns the handle produced by `factory()`, re-created only when `deps` change. Useful for exposing imperative APIs to a parent component via a `Ref`.",
                ["useSafeArea"] =
                    "## `useSafeArea(tolerance?)`\n\n**Shorthand for `Hooks.UseSafeArea`.** Returns the current `SafeAreaInsets` (top, bottom, left, right). Re-renders when the safe area changes by more than `tolerance` pixels.\n\n```csharp\nvar insets = useSafeArea();\n```",
                ["Hooks.UseSafeArea"] =
                    "## `Hooks.UseSafeArea(tolerance?)`\n\nReturns the current `SafeAreaInsets` (top, bottom, left, right). Re-renders when the safe area changes by more than `tolerance` pixels.",
                ["useStableFunc"] =
                    "## `useStableFunc<T>(function)`\n\n**Shorthand for `Hooks.UseStableFunc`.** Returns a `Func<T>` whose identity is stable across renders, while always invoking the latest `function` body. Use for callbacks passed to dependent components without busting their memoisation.",
                ["Hooks.UseStableFunc"] =
                    "## `Hooks.UseStableFunc<T>(function)`\n\nReturns a `Func<T>` whose identity is stable across renders, while always invoking the latest `function` body.",
                ["useStableAction"] =
                    "## `useStableAction<T>(action)`\n\n**Shorthand for `Hooks.UseStableAction`.** Returns an `Action<T>` whose identity is stable across renders, while always invoking the latest `action` body.",
                ["Hooks.UseStableAction"] =
                    "## `Hooks.UseStableAction<T>(action)`\n\nReturns an `Action<T>` whose identity is stable across renders, while always invoking the latest `action` body.",
                ["useStableCallback"] =
                    "## `useStableCallback(callback)`\n\n**Shorthand for `Hooks.UseStableCallback`.** Returns an `Action` whose identity is stable across renders, while always invoking the latest `callback` body.",
                ["Hooks.UseStableCallback"] =
                    "## `Hooks.UseStableCallback(callback)`\n\nReturns an `Action` whose identity is stable across renders, while always invoking the latest `callback` body.",
                ["useAnimate"] =
                    "## `useAnimate(tracks, autoplay?, deps?)`\n\n**Shorthand for `Hooks.UseAnimate`.** Plays a sequence of animation tracks on the component's host element. `autoplay` controls whether playback starts immediately.\n\n```csharp\nuseAnimate(tracks, autoplay: true);\n```",
                ["Hooks.UseAnimate"] =
                    "## `Hooks.UseAnimate(tracks, autoplay?, deps?)`\n\nPlays a sequence of animation tracks on the component's host element. `autoplay` controls whether playback starts immediately.",
                ["useTweenFloat"] =
                    "## `useTweenFloat(from, to, duration, ease, delay, onUpdate, onComplete, deps?)`\n\n**Shorthand for `Hooks.UseTweenFloat`.** Animates a float from `from` to `to` over `duration` seconds. Calls `onUpdate(value)` each frame and `onComplete()` when finished.\n\n```csharp\nuseTweenFloat(0f, 1f, 0.3f, Ease.OutCubic, 0f, v => style.opacity = v, null);\n```",
                ["Hooks.UseTweenFloat"] =
                    "## `Hooks.UseTweenFloat(from, to, duration, ease, delay, onUpdate, onComplete, deps?)`\n\nAnimates a float from `from` to `to` over `duration` seconds. Calls `onUpdate(value)` each frame and `onComplete()` when finished.",
                ["useSfx"] =
                    "## `useSfx(mixer?)`\n\n**Shorthand for `Hooks.UseSfx`.** Returns a stable `Action<AudioClip, float>` for one-shot sound effects. Pass an optional `AudioMixerGroup` to route playback.\n\n```csharp\nvar play = useSfx();\nplay(clickSound, 1.0f);\n```",
                ["Hooks.UseSfx"] =
                    "## `Hooks.UseSfx(mixer?)`\n\nReturns a stable `Action<AudioClip, float>` for one-shot sound effects. Pass an optional `AudioMixerGroup` to route playback.",
                ["useUiDocumentRoot"] =
                    "## `useUiDocumentRoot(doc | contextKey)`\n\n**Shorthand for `Hooks.UseUiDocumentRoot`.** Returns the current `rootVisualElement` of a `UIDocument`, re-rendering when it rebuilds (undo, asset swap, playmode toggle). Designed for portal targeting.",
                ["Hooks.UseUiDocumentRoot"] =
                    "## `Hooks.UseUiDocumentRoot(doc | contextKey)`\n\nReturns the current `rootVisualElement` of a `UIDocument`, re-rendering when it rebuilds (undo, asset swap, playmode toggle).",
                ["provideContext"] =
                    "## `provideContext<T>(key, value)`\n\n**Shorthand for `Hooks.ProvideContext`.** Provides a context value to all descendant components. Children read it via `useContext<T>(key)`.\n\n```csharp\nprovideContext(\"theme\", new ThemeData { ... });\n```",
                ["Hooks.ProvideContext"] =
                    "## `Hooks.ProvideContext<T>(key, value)`\n\nProvides a context value to all descendant components. Children read it via `Hooks.UseContext<T>(key)`.",
                // ── NEW in 0.5.23: useLayoutEffect hover docs (was missing pre-refactor) ──
                ["useLayoutEffect"] =
                    "## `useLayoutEffect(action, deps?)`\n\n**Shorthand for `Hooks.UseLayoutEffect`.** Runs `action` synchronously after layout but before paint.  Use this when an effect must observe or mutate layout values before the user sees the next frame.  For most side-effects prefer `useEffect`.\n\n```csharp\nuseLayoutEffect(() => { /* read layout, set styles */ return null; }, new object[] { width });\n```",
                ["Hooks.UseLayoutEffect"] =
                    "## `Hooks.UseLayoutEffect(action, deps?)`\n\nRuns `action` synchronously after layout but before paint.  Use this when an effect must observe or mutate layout values before the user sees the next frame.  For most side-effects prefer `Hooks.UseEffect`.\n\n```csharp\nHooks.UseLayoutEffect(() => { /* read layout, set styles */ return null; }, new object[] { width });\n```",
            };
            return d;
        }

        // ── Virtual-document stub blocks ─────────────────────────────────────
        //
        // Two near-identical scaffold blobs that the IDE virtual-document
        // generator splices into every UITKX virtual C# document.  Both are
        // captured byte-identical to the pre-0.5.23 hand-maintained strings
        // at VirtualDocumentGenerator.cs L342-367 (static form) and L515-540
        // (instance form).  The differences between the two forms are:
        //
        //   1. Method modifier: `private static` vs `private`
        //   2. Header-comment box-drawing tail length:
        //      static form  — `──────────`         (10 dashes)
        //      instance form — `──────────────────` (18 dashes)
        //
        // The two strings are stored separately rather than templated from a
        // single source because (a) the duplication is bounded (~70 lines × 2),
        // (b) any byte-level edit must update BOTH and is caught by the golden
        // snapshot tests, and (c) the verbatim form makes the diff against the
        // pre-refactor source trivial to review.  Asset<T> and Ast<T> appear in
        // both blocks because they're emitted alongside the hook stubs by the
        // existing VDG code — they are NOT hooks but they ARE part of the
        // scaffold that consumers expect.

        private const string HookStubsStaticForm =
            "\n" +
            "        // ── Roslyn-only hook stubs (never called at runtime) ──────────\n" +
            "#pragma warning disable CS8603, CS8625, CS1998, CS0246\n" +
            "        private delegate void __StateSetter__<T>(global::System.Func<T, T> updater);\n" +
            "        private static (T value, __StateSetter__<T> set)\n" +
            "            useState<T>(T initial = default) => (initial, null!);\n" +
            "        private static T useMemo<T>(global::System.Func<T> factory, params object[] deps)\n" +
            "            => factory != null ? factory() : default!;\n" +
            "        private static void useEffect(\n" +
            "            global::System.Func<global::System.Action> effectFactory,\n" +
            "            params object[] deps) { }\n" +
            "        private static global::Ruitk.Core.Ref<T> useRef<T>(T initial = default) => new();\n" +
            "        private static global::UnityEngine.UIElements.VisualElement useRef() => null!;\n" +
            "        private static global::System.Func<T> useCallback<T>(\n" +
            "            global::System.Func<T> callback, params object[] deps) => callback!;\n" +
            "        private static T useSignal<T>(object signal) => default!;\n" +
            "        private static T useSignal<T>(string key, T initialValue = default) => initialValue;\n" +
            "        private static T useContext<T>(string key) => default!;\n" +
            "        private static void provideContext<T>(string key, T value) { }\n" +
            "        private static void provideContext(string key, object value) { }\n" +
            "        private static void useLayoutEffect(\n" +
            "            global::System.Func<global::System.Action> effectFactory,\n" +
            "            params object[] deps) { }\n" +
            "        private static global::System.Action<global::UnityEngine.AudioClip, float>\n" +
            "            useSfx(global::UnityEngine.Audio.AudioMixerGroup mixer = null) => (_, __) => { };\n" +
            "        private static global::UnityEngine.UIElements.VisualElement useUiDocumentRoot(global::UnityEngine.UIElements.UIDocument doc) => default!;\n" +
            "        private static global::UnityEngine.UIElements.VisualElement useUiDocumentRoot(string contextKey) => default!;\n" +
            "        private static (TState state, global::System.Action<TAction> dispatch) useReducer<TState, TAction>(global::System.Func<TState, TAction, TState> reducer, TState initialState) => (initialState, _ => { });\n" +
            "        private static T useDeferredValue<T>(T value, params object[] deps) => value;\n" +
            "        private static THandle useImperativeHandle<THandle>(global::System.Func<THandle> factory, params object[] deps) where THandle : class => factory();\n" +
            "        private static global::System.Func<T> useStableFunc<T>(global::System.Func<T> function) => function;\n" +
            "        private static global::System.Action<T> useStableAction<T>(global::System.Action<T> action) => action;\n" +
            "        private static global::System.Action useStableCallback(global::System.Action callback) => callback;\n" +
            "        private static void useTweenFloat(float from, float to, float duration, global::Ruitk.Core.Animation.Ease ease, float delay, global::System.Action<float> onUpdate, global::System.Action onComplete, params object[] deps) { }\n" +
            "        private static void useAnimate(global::System.Collections.Generic.IReadOnlyList<global::Ruitk.Core.Animation.AnimateTrack> tracks, bool autoplay = true, params object[] deps) { }\n" +
            "        private static global::Ruitk.Core.SafeAreaInsets useSafeArea(float tolerance = 0.5f) => default!;\n" +
            "        private static (bool isPending, global::System.Action<global::System.Action> startTransition) useTransition() => (false, _ => { });\n" +
            "        private static T Asset<T>(string path) where T : global::UnityEngine.Object => default!;\n" +
            "        private static T Ast<T>(string path) where T : global::UnityEngine.Object => default!;\n" +
            "#pragma warning restore CS8603, CS8625, CS1998, CS0246\n" +
            "\n";

        private const string HookStubsInstanceForm =
            "\n" +
            "        // ── Roslyn-only hook stubs (never called at runtime) ──────────────\n" +
            "#pragma warning disable CS8603, CS8625, CS1998, CS0246\n" +
            "        private delegate void __StateSetter__<T>(global::System.Func<T, T> updater);\n" +
            "        private (T value, __StateSetter__<T> set)\n" +
            "            useState<T>(T initial = default) => (initial, null!);\n" +
            "        private T useMemo<T>(global::System.Func<T> factory, params object[] deps)\n" +
            "            => factory != null ? factory() : default!;\n" +
            "        private void useEffect(\n" +
            "            global::System.Func<global::System.Action> effectFactory,\n" +
            "            params object[] deps) { }\n" +
            "        private global::Ruitk.Core.Ref<T> useRef<T>(T initial = default) => new();\n" +
            "        private global::UnityEngine.UIElements.VisualElement useRef() => null!;\n" +
            "        private global::System.Func<T> useCallback<T>(\n" +
            "            global::System.Func<T> callback, params object[] deps) => callback!;\n" +
            "        private T useSignal<T>(object signal) => default!;\n" +
            "        private T useSignal<T>(string key, T initialValue = default) => initialValue;\n" +
            "        private T useContext<T>(string key) => default!;\n" +
            "        private void provideContext<T>(string key, T value) { }\n" +
            "        private void provideContext(string key, object value) { }\n" +
            "        private void useLayoutEffect(\n" +
            "            global::System.Func<global::System.Action> effectFactory,\n" +
            "            params object[] deps) { }\n" +
            "        private global::System.Action<global::UnityEngine.AudioClip, float>\n" +
            "            useSfx(global::UnityEngine.Audio.AudioMixerGroup mixer = null) => (_, __) => { };\n" +
            "        private global::UnityEngine.UIElements.VisualElement useUiDocumentRoot(global::UnityEngine.UIElements.UIDocument doc) => default!;\n" +
            "        private global::UnityEngine.UIElements.VisualElement useUiDocumentRoot(string contextKey) => default!;\n" +
            "        private (TState state, global::System.Action<TAction> dispatch) useReducer<TState, TAction>(global::System.Func<TState, TAction, TState> reducer, TState initialState) => (initialState, _ => { });\n" +
            "        private T useDeferredValue<T>(T value, params object[] deps) => value;\n" +
            "        private THandle useImperativeHandle<THandle>(global::System.Func<THandle> factory, params object[] deps) where THandle : class => factory();\n" +
            "        private global::System.Func<T> useStableFunc<T>(global::System.Func<T> function) => function;\n" +
            "        private global::System.Action<T> useStableAction<T>(global::System.Action<T> action) => action;\n" +
            "        private global::System.Action useStableCallback(global::System.Action callback) => callback;\n" +
            "        private void useTweenFloat(float from, float to, float duration, global::Ruitk.Core.Animation.Ease ease, float delay, global::System.Action<float> onUpdate, global::System.Action onComplete, params object[] deps) { }\n" +
            "        private void useAnimate(global::System.Collections.Generic.IReadOnlyList<global::Ruitk.Core.Animation.AnimateTrack> tracks, bool autoplay = true, params object[] deps) { }\n" +
            "        private global::Ruitk.Core.SafeAreaInsets useSafeArea(float tolerance = 0.5f) => default!;\n" +
            "        private (bool isPending, global::System.Action<global::System.Action> startTransition) useTransition() => (false, _ => { });\n" +
            "        private T Asset<T>(string path) where T : global::UnityEngine.Object => default!;\n" +
            "        private T Ast<T>(string path) where T : global::UnityEngine.Object => default!;\n" +
            "#pragma warning restore CS8603, CS8625, CS1998, CS0246\n" +
            "\n";

        // ── Diagnostics-only API ─────────────────────────────────────────────

        /// <summary>
        /// Total number of unique hook names known to the registry, equal to
        /// the canonical list length.  Exposed primarily for tests that lock
        /// the count against silent drift.
        /// </summary>
        public static int HookCount => s_signatureOrder.Length;

        /// <summary>
        /// Read-only view of canonical PascalCase hook names.  Test code and
        /// internal diagnostics use this; production consumers should prefer
        /// the cached accessors above.
        /// </summary>
        public static IReadOnlyList<string> CanonicalNames => s_signatureOrder;

        /// <summary>
        /// Every name a <c>.uitkx</c> file may use to call a BUILT-IN hook bare:
        /// the canonical PascalCase form (<c>UseState</c>) AND its camelCase alias
        /// (<c>useState</c>).  This is the strict-import exemption set — builtin
        /// hooks never need an <c>import</c>, so the 2305/2307 reference detector
        /// must skip both spellings.  Single source of truth for the SG pipeline
        /// and the LSP DiagnosticsPublisher; checking only
        /// <see cref="CanonicalNames"/> (PascalCase, ordinal) silently missed the
        /// camelCase call sites every real file uses, storming UITKX2307 across a
        /// whole project the first time peer exports existed.
        /// </summary>
        public static IReadOnlyCollection<string> AmbientHookNames => s_ambientNamesCache;

        private static readonly HashSet<string> s_ambientNamesCache = BuildAmbientNames();

        private static HashSet<string> BuildAmbientNames()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in s_signatureOrder)
            {
                set.Add(n);
                set.Add(CamelOf(n));
            }
            return set;
        }
    }
}
#endif
