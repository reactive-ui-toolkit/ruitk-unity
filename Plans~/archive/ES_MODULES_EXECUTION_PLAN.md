# ES-Modules Redesign — UNITY EXECUTION PLAN (.uitkx)

**Status: EXECUTED — archived 2026-08-04.** Shipped as 0.9.0 (M0–M9 complete); the post-execution
audit ledger is `Plans~/archive/ES_MODULES_AUDIT_FINDINGS.md`, deferred items in
`Plans~/REMAINING_WORK.md`.

Original status: **READY TO EXECUTE** (written 2026-07-17; anchors re-audited and corrected against the
working tree on branch `fix/import-diagnostic-anchors`, HEAD `05bfb47a`, post-F5-wave `43f59670` —
baseline is now fully GREEN, see §1.6).
**Family contract:** `Plans~/ES_MODULES_GENERAL_PLAN.md` (owner-approved 2026-07-17). This plan
MAY add Unity detail; it MAY NOT contradict that document. Any conflict = STOP AND ASK the owner.
**Release target:** Unity package **0.8.3 → 0.9.0** (G-11), VS Code + VS2022 extensions
**1.4.4 → 1.5.0**, Rider **1.1.0 → 1.2.0**.
**Branch:** create `feat/es-modules` off the current branch (or off `master` if the owner has
merged — ask if unclear). Never push unless the owner asks. Never add a Co-Authored-By trailer.

Every decision is pre-made below. Where the plan says a table drives an edit, follow the table.
All verification gates are HARD — do not commit a milestone with any gate red.

---

## 0. Locked family decisions (echo — G-01..G-13). Do NOT re-litigate.

| ID | Lock (short) | What it means for Unity specifically |
|---|---|---|
| G-01 | **File = module.** Module identity = file path; renaming a file changes module + hot-reload identity of privates. | Namespaces become FILE-keyed (folder segments + file stem), not folder-keyed. See U-01. |
| G-02 | **One visibility mechanism: imports.** No same-folder ambience, no companion merging, no name-glob magic. Hand-written `.cs` stays ambient (rule A4). | Kill the Unity catch-up layer: companion partial-merging (`X.style.uitkx` → `partial class X`) and same-folder shared-namespace ambience. Deprecation window per G-10. |
| G-03 | **Wrapper keywords removed.** Classification from the SIGNATURE ALONE: `VirtualNode` return ⇒ component (PascalCase ENFORCED); `use`-camel prefix ⇒ hook; `=` after name ⇒ value; else util. Cross-guards are errors. | `component X {}` / `hook useX(..) -> (..) {}` / `module M {}` all replaced by plain C# declarations with `export`. See U-04. |
| G-04 | **Native idiom typing.** Unity/C#: type-first (`export Style container = ...`, `export string F(int x) {..}`, `export VirtualNode Panel(string t) {..}`). Inference sugar ONLY when the initializer names the type (`= new Style {...}`). | Parser reads C# declaration heads; no semantic analysis. |
| G-05 | **Full ES import surface** (supersedes "named-only"): named, `as`-rename, `* as X`, default import + `export default <Name>;`, deferred `export { a, b };`. Re-exports stay DEFERRED. Specifiers stay extensionless `./ ../ ~/`, preamble-only, 2308 boundary rule unchanged. | New import readers + lowerings. See U-03. |
| G-06 | **Privacy is real.** Un-exported = file-private, compile-time-invisible outside the file (per-file namespace). Runtime registries keyed by FILE-QUALIFIED identity for privates. | Per-file namespace + `internal` members inside the per-file `__Exports` container. |
| G-07 | **Escape hatches stay.** Unity: components keep emitting `partial class` so hand-written `.cs` can extend them. | Component emission shape is unchanged (partial class per component); only its namespace moves. |
| G-08 | **Eager/lazy is kind-driven.** Component refs lazy (cycles legal); value/hook refs may be eager, TDZ-style 2306 semantics identical family-wide. | Unity already conforms (2306 in `UitkxImportGraph`); do not regress. |
| G-09 | **Hot-reload identity = (file identity + declared name)** for privates; exported name (unique via 2106-family rule) for exports. State-preservation semantics must not regress; signature change still resets. | Family keys change once (ns gains the file segment; hooks move to `__Exports`) — a one-time state reset at migration, documented. See U-06. |
| G-10 | **Deprecation window.** Old syntax parses for ONE minor with new 2320–2329 diagnostics; idempotent codemod ships in the SAME release; removal later, owner-triggered. | Old wrappers + companion merging + folder namespaces keep working in 0.9.0 with 2320 (wrapper) / 2107 (companion-merge, Unity-local) warnings. See §6 matrix. |
| G-11 | **Versioning: minor bump.** Unity 0.8.x → 0.9.0. | package.json 0.8.3 → 0.9.0; extensions per their own lanes (§9). |
| G-12 | **Full sync surface is part of DONE** — parser+all emitters+formatter+codemod, grammar/schema/LSP, hot reload, ALL changelogs, migration guide, docs pages, version bumps, family corpus in LOCKSTEP. | §9 checklist; corpus §1.6/§5-M0. |
| G-13 | **Rollout: Unreal → Godot → Unity**, but legs may proceed in parallel once the new-grammar corpus cases are agreed and pinned. | M0 checks for the family cases (verified ABSENT 2026-07-17). If absent: proceed on Unity-local tests, corpus/hash UNTOUCHED (R2), re-pin at M7 only family-synced — STOP AND ASK before any unilateral corpus write. |

Reference dialect + lowering (general plan §3) is the normative Unity shape:
per-file namespace (folder path + file stem); ONE static `__Exports` container for
values/utils/hooks; one `partial class` per component; named import ⇒
`using static <ns>.__Exports;`; `* as X` ⇒ `using X = <ns>.__Exports;`; default ⇒ alias to the
default-marked symbol.

---

## 1. Where Unity starts (verified 2026-07-17 — trust it, don't re-derive)

### 1.1 Feature state
- `UitkxFeatureFlags.StrictImports` is **already `true`** (`ide-extensions~/language-lib/UitkxFeatureFlags.cs:31`). Strict imports, path-derived namespaces, per-import injection, path-qualified family keys, export→accessibility are LIVE. This campaign builds on that, it does not re-introduce a flag-off world.
- Samples are already 100% `export`-keyword syntax: **119 `export component`, 7 `export hook`, 39 `export module`** declaration sites across **165 `.uitkx` files**, 261 `import` lines. Companions: 27 `*.style.uitkx`, 7 `*.hooks.uitkx`, 1 `*.types.uitkx`.
- Hook wrapper syntax today: `export hook useX(params) -> (retTuple) { body }` (e.g. `Samples/Components/GalagaGame/components/GameScreen/GameScreen.hooks.uitkx:9-11`).
- Namespaces are FOLDER-keyed: `NamespaceDerivation.cs:15` — "The file NAME is excluded (files in one folder share a namespace)". That line is the heart of what G-01 changes.
- Companion merging is alive in two places: SG `ModuleEmitter.cs:136-151` (cross-file same-name/same-ns partial merge) and HMR `UitkxHmrCompiler.EmitCompanionUitkxSources` (call sites `UitkxHmrCompiler.cs:421, 680`).
- HMR effective-namespace threading + import-target inlining **just landed** (commits `db23c382`, `14070f18`, `43f59670`): HMR reflection-binds `EffectiveNamespace.Resolve` / `UiSourceRootDir` (`UitkxHmrCompiler.cs:114-115, 1556-1558`) and `ImportScopeFacts.ComputeInjectedUsingPayloads` (`:117, 1562-1564`), and treats imported component files as recompile candidates (`:1233-1266`). The same F5 wave also added the VDG `DocumentNamespace` helper (`VirtualDocumentGenerator.cs:342-348`, used at `:308/:366/:452`), the LSP RenameHandler import-list pass, the debounced dependent-revalidation path (`TextSyncHandler.cs:81-104` → `DiagnosticsPublisher.ScheduleDebouncedRevalidation :96-98`), 30 new schema elements pinned by `SchemaRegistryParityTests`, and `HmrModuleNamespaceParityContractTests`. BUILD ON ALL OF THIS — do not fork a second namespace resolver, and route new dependent-diagnostic publication through the debounced revalidation, never per-keystroke.

### 1.2 Four parity emit layers (must change in lockstep — G-12)
1. **SG**: `SourceGenerator~/Emitter/CSharpEmitter.cs` (4024 ln), `HookEmitter.cs` (426), `ModuleEmitter.cs` (205), `PropsResolver.cs` (963), pipeline `SourceGenerator~/UitkxPipeline.cs` (1123).
2. **HMR**: `Editor/HMR/HmrCSharpEmitter.cs` (3714), `HmrHookEmitter.cs` (480), `UitkxHmrCompiler.cs` (3613). All language-lib access is **reflection** into the committed `Analyzers/Ruitk.Language.dll` (`HmrHookEmitter.cs:16-17`, `HmrCSharpEmitter.cs:13`, `UitkxHmrCompiler.cs:1556-1564`) — any renamed/re-signatured public language-lib member breaks HMR silently at runtime, not at compile time.
3. **IDE virtual docs**: `ide-extensions~/language-lib/Roslyn/VirtualDocumentGenerator.cs` (3067).
4. **Shared single-sources-of-truth**: `ide-extensions~/language-lib/{ImportScopeFacts.cs (140), EffectiveNamespace.cs (147), NamespaceDerivation.cs (136), ImportResolver.cs (123), StrictImportDetector.cs, UitkxImportGraph.cs}`.

Parity is pinned by `SourceGenerator~/Tests/{HmrEmitterParityContractTests (33 tests), HmrModuleNamespaceParityContractTests (3), HmrBuiltinTagDiscoveryContractTests (12), HmrFindPropsTypeContractTests (5), AsmdefResolverParityTests (3)}`. If you change one emitter, you WILL update its contract mirror in the same milestone.

### 1.3 Identity/registry seams (G-01/G-06/G-09 audit list — every consumer of the namespace)
- `EffectiveNamespace.Resolve(hasExplicit, rawNs, filePath)` — `EffectiveNamespace.cs:23-32`. Single seam; called by SG (`UitkxPipeline.cs:534-535`), ImportScopeFacts (`:64, 84`), VDG `DocumentNamespace` (`VirtualDocumentGenerator.cs:342-348`), HMR via reflection (`UitkxHmrCompiler.cs:114, 1556-1557, ComputeEffectiveNs :3051-3070`).
- `NamespaceDerivation.Derive(fileAbs, anchorDir, prefix)` — `NamespaceDerivation.cs:32-48` (+ `Sanitize` `:99-120`, `SanitizeDottedName` `:51-61`).
- Component Family/Register key `{ns}.{Component}`: SG `CSharpEmitter.cs:109-112 (SelfFamilyKey)`, `:564-570 (__Register emit)`; HMR `HmrCSharpEmitter.cs:146-156, 231, 547-548`; HMR compile result `UitkxHmrCompiler.cs:392-395 (result.FamilyKey)`.
- Hook family key `{ns}.{Container}::{hook}`: SG `HookEmitter.cs:167-172`; HMR `HmrHookEmitter.cs:175-187`.
- Hook container name `{FileStemBeforeFirstDot}Hooks`: SG `HookEmitter.DeriveContainerClassName` (`HookEmitter.cs:405-418`); mirror `ImportScopeFacts.DeriveHookContainerClassName` (`ImportScopeFacts.cs:128-138`); HMR `HookContainerRegistry.cs:193` (`Fqn = $"{ns}.{container}"`).
- Injected-using payloads: `ImportScopeFacts.ComputeInjectedUsingPayloads` (`ImportScopeFacts.cs:52-126`) — hook ⇒ `static {ns}.{Container}`, module/component ⇒ `{Name} = {ns}.{Name}` alias with same-ns + `ReservedTypeAliases` guards (`:38-44`, mirrored at `UitkxPipeline.cs:738`). SG equivalent from peer tables: `UitkxPipeline.ResolveInjectedUsings` (`:553+`).
- LSP index: `ide-extensions~/lsp-server/WorkspaceIndex.cs` — `IndexUitkxFile` `:684`, keyword regexes `:155/:163/:173`, `GetPeerExports` `:305`, `GetExportKind` `:359`.
- **HMR runtime seams beyond the compiler** (all consume namespace/type identity — audit in M4):
  `Editor/HMR/UitkxHmrController.cs` — import reverse-dependency map (`:79-83, 256-257, 379-381`),
  **companion redirect** `ResolveParentComponentFile` (`:363-366` — compiles `Foo.uitkx` when
  `Foo.style.uitkx` changes; this is a companion-merge consumer, legacy-only after U-07),
  module static-field re-bind + delegate swap dispatch (`:610-634, 654-673`);
  `Editor/HMR/UitkxHmrModuleStaticSwapper.cs` — same-`Type.FullName` project-type matching
  (`:53, 187, 195, 235, 276`; new-mode module members live on `{fileNs}.__Exports`, unique per file);
  `Editor/HMR/UitkxHmrDelegateSwapper.cs` — hook delegate swap keyed via `HookContainerRegistry`.

### 1.4 Parser anchors (language-lib)
`ide-extensions~/language-lib/Parser/DirectiveParser.cs` (3207 ln):
- `FunctionStyleDefaultNamespace = "Ruitk.FunctionStyle"` `:32`; top-level keyword set `:34-47`.
- `Parse` `:55`; `TryParseFunctionStyle` `:138`; **preamble loop** `:164-212` (readers: `TryReadFunctionStyleUsing :1783`, `TryReadFunctionStyleUss :1848`, `TryReadFunctionStyleNamespaceDirective :1714`, `TryReadFunctionStyleNamespaceImport :941`, `TryReadFunctionStyleImport :851`); duplicate-import 2303 `ReportDuplicateImports :1001`.
- Keyword dispatch (`export` peek + component/hook/module) `:218-249`; component parse `:251-518`; **mixed-decl continuation loop** `:520-620` (`ParseSingleComponent :636`); `TryParseHookModuleFile :1041`; `ParseSingleHook :1145`; `ParseSingleModule :1280`; `IsPascalCase :2361`.
- Model: `ide-extensions~/language-lib/Parser/ParseResult.cs` — `HookDeclaration :32`, `ModuleDeclaration :66`, `ImportDeclaration :94-109` (fields `Names, Specifier, Line, Column, NameColumns, SpecifierColumn` — **no alias/star/default fields yet**), `UsingDirective :119`, `ComponentDeclaration :140`, `DirectiveSet :191`.
- Markup tokenizer: `MarkupTokenizer.ReadTagName` `:149-157` — **letters/digits/underscore only, no dots** (needed by U-05).
- `ide-extensions~/language-lib/Lowering/CanonicalLowering.cs` is only 32 lines (thin); the real lowering knowledge lives in the emitters + `ImportScopeFacts`.

### 1.5 Tooling anchors
- Formatter `ide-extensions~/language-lib/Formatter/AstFormatter.cs` (3331 ln): canonical preamble order `:43-51`, `@Ns`-spelling preservation `:88`, data-loss guard note `:196-197`, component header emit `:222-233`, module emit `:406`, hook emit `:444-445, 510-511`, hook/module file re-emit `:358-373`.
- Grammar `ide-extensions~/grammar/uitkx.tmLanguage.json`: `function-style-declaration` `:141-161` (match `:144`), `hook-declaration` `:162-195` (match `:165`, incl. `->`), `module-declaration` `:196-211` (match `:199`), directive alternation `:128`, `namespace-import-declaration` `:72-80`, `import-declaration` `:81-93`. **No** `export {}` list / default / `as` / `* as` patterns exist. VS Code prebuild copies the grammar (`ide-extensions~/vscode/package.json:153` script, registered `:51-55`). VS2022 does NOT use TextMate — hand-written classifier `ide-extensions~/visual-studio/UitkxVsix/UitkxClassifier.cs` with `component` in `_typedDirectives :213` and `hook`/`module` in `_keywords :273-274`. Rider = pure LSP client (no in-repo grammar copy).
- Schema `ide-extensions~/grammar/uitkx-schema.json` (v1.1): `directives :318-343` (incl. `component` entry `:323-326`), `controlFlow :344-377`, `elements :378+`. Parity test: `SchemaRegistryParityTests.cs:29-60`.
- Codemod `SourceGenerator~/Tools/UitkxMigrateImports/`: `Program.cs` `Main :23-90` (flags `--check/--tidy/--format` `:33-35`; asmdef grouping `:43-48, 103-122`); `UitkxMigrator.cs` — `Migrate :68-112`, export tables pass `:83-97`, per-file pass `:100-110`, `ScanReferences :179-213` (regexes `:56/:58/:60`), `Rewrite :251-332`, `PrependExport :335-342` (regex `^(\s*)(component|hook|module)\b`), `TidyUsings :122-151`, `DeclKind :27`, `RelativeSpecifier :413-436`.
- LSP handlers (`ide-extensions~/lsp-server/`): CompletionHandler — `FunctionStylePreambleItems :311` (wrapper-keyword snippets `:342-358`), `GetImportBraceCompletions :1555` (export walk `:1584-1590`), `GetSpecifierCompletions :1600`; HoverHandler — HookRegistry docs `:561-567`, hook hover `:285-297`, `DirectiveExample :582-587`; DefinitionHandler — `TryResolveImportNavigation :370`, `FindDeclarationLine :426-430` (regex `^\s*(?:export\s+)?(?:component|hook|module)\s+{name}\b`), `FindDeclarationInUitkx :628-645`; RenameHandler — **import-list rename pass** `CollectImportListRenameEdits :490-538` (called from `:354, :399, :464`), component decl regex `:966-969`, tag regex `:996-999`, hook rename `:1040, 1092-1116`, peer-symbol `:1162`; DiagnosticsPublisher — strict imports `:205-259`, value-cycle `:317-379`, redundant using `:287`, duplicate component `:857`; ReferencesHandler regexes `:457, 474, 526, 537, 641, 651`; SemanticTokensHandler delegates to language-lib `SemanticTokens/` (`:83-92`); **ImportCodeActionHandler** — the 2305 "add import" quick-fix parses the fix line out of the 2305 message and inserts at the preamble top (`ImportCodeActionHandler.cs:14-17, 41, 155+`); **TextSyncHandler** — edited file publishes immediately, dependents ride the debounced revalidation (`TextSyncHandler.cs:81-104`).
- Semantic tokens source of truth: `ide-extensions~/language-lib/SemanticTokens/SemanticTokensProvider.cs` — `s_importTokenRe :145-148` matches ONLY braced named imports (no `* as`, no default), `s_exportTokenRe :150-153` matches ONLY `export` + wrapper keywords (a plain `export VirtualNode …` gets no keyword token), leading-`component`-keyword scan `:189`.
- Tier-2 structural diagnostics: `ide-extensions~/language-lib/Diagnostics/DiagnosticsAnalyzer.cs` (0013-0016 hook rules, 0103 component-name/filename match, 0107/0108/0111 …) — consumed by `DiagnosticsPublisher`, `CSharpEmitter`, and `UitkxHmrController`; it walks the parsed declaration records, so new-mode `MemberDeclaration` components/hooks must flow through it too (M1 audit item).

### 1.6 Baseline (re-measured 2026-07-17 on this working tree — GREEN)
```
dotnet test SourceGenerator~/Tests/Ruitk.SourceGenerator.Tests.csproj -v q --nologo
  → Passed: 1554/1554
dotnet test ide-extensions~/lsp-server/Tests/UitkxLanguageServer.Tests.csproj -v q --nologo
  → Passed: 123/123
node scripts/corpus-hash.mjs --check → OK (917dd8cd…)
```
The 3 reds an earlier draft of this plan documented are FIXED, but the fixes live as
**uncommitted working-tree edits** (plus binary churn on `Analyzers/*.dll` from a local rebuild):
1. `SourceGenerator~/Tests/ImportCorpusManifestTests.cs:23-24` — `FrozenFamilyHash` re-pinned
   `657e5f4e…` → `917dd8cdf6999647e991f7186bc3e97702c40ba656826d3a7141bdc87de52169` (matches
   `Plans~/family-corpus.hash` as re-pinned by commit `19c582dc`).
2. `Samples/Components/HmrTests/SomeOtherName.style.uitkx` — trailing newline added (formatter
   idempotency fixed point).
M0 commits these two fixes first (see §5). ANY red at baseline = STOP AND ASK.

Corpus machinery: `scripts/corpus-hash.mjs` hashes `_tiers.familyCore` of
`ide-extensions~/lsp-server/test-fixtures/uitkx-scanner-cases.json` (TKX-prefix-normalized) into
`Plans~/family-corpus.hash`; CI hard gate `.github/workflows/test.yml:16-28`; advisory DLL-drift
check `test.yml:54-64`; publish lanes `publish.yml` (§9).

---

## 2. Engine-local decisions (U-01..U-12) — pre-made; do not improvise

**U-01 — File-keyed namespace formula.** For a NEW-SYNTAX file (see U-08 for mode detection):
`EffectiveNamespace = explicit @namespace if present; else Prefix + '.' + Sanitize(folderSeg)* + '.' + Sanitize(fileModuleStem)`
where `fileModuleStem` = file name minus the `.uitkx` extension, with every remaining char
sanitized by the EXISTING `NamespaceDerivation.Sanitize` (so `AppRoot.style` → `AppRoot_style`,
`use-thing` → `use_thing`). Prefix/anchor resolution is UNCHANGED (`EffectiveNamespace.ResolveNamespacePrefix/ResolveDerivationAnchor`).
LEGACY-SYNTAX files keep today's folder-keyed derivation verbatim (deprecation window). The
explicit `@namespace` escape hatch keeps working for both modes (it already wins — do not touch).
Implementation: add `NamespaceDerivation.DeriveFileModule(fileAbs, anchorDir, prefix)` beside
`Derive` (do NOT change `Derive`'s behavior — legacy files still call it), and thread a
`bool fileKeyed` (derived from the parse's syntax mode) through `EffectiveNamespace.Resolve`.
**Signature rule for the reflection seam:** do NOT change the existing
`EffectiveNamespace.Resolve(bool, string, string)` signature — HMR reflection-binds it by
parameter shape (`UitkxHmrCompiler.cs:1556-1557`). ADD an overload
`Resolve(bool hasExplicitNamespace, string? rawNamespace, string filePath, bool fileKeyed)` and make
the old 3-arg overload delegate with `fileKeyed:false`. HMR then binds the 4-arg overload
explicitly — the current lookup is `effNsType?.GetMethod("Resolve", BindingFlags.Public |
BindingFlags.Static)` (`:1557`), which will THROW `AmbiguousMatchException` the moment the DLL has
two `Resolve` overloads, so the same commit that adds the overload MUST switch this call to a
`GetMethod("Resolve", …, types: new[]{typeof(bool), typeof(string), typeof(string), typeof(bool)})`
lookup — and a missing 4-arg method (stale committed DLL) must throw a loud, actionable error, not
silently fall back.

**U-02 — The per-file `__Exports` container (new-syntax files only).**
One `public static partial class __Exports` per file, emitted in the file's (file-keyed)
namespace. Members:
- exported value → `public static {Type} {name} = {init};` (type from declaration, or from the
  `new T` initializer for the inference sugar);
- exported util/hook → `public static {Ret} {Name}({params}) { body }` (or `=> expr;` verbatim);
- non-exported value/util/hook → same shapes with `internal`. NOTE this is file-private only
  ACROSS assemblies / for non-importers — a same-assembly importer's injected
  `using static {targetNs}.__Exports` (U-03 row 1) also exposes `internal` members bare-name;
  see risk R11 for the accepted-vs-ask decision. The file-keyed namespace stays unique per file,
  so there is never a name COLLISION, only same-assembly visibility softness.
Hooks KEEP the HMR-swappable trampoline emission pattern currently produced for hook containers
(`HookEmitter.cs:94-113`) and modules (`ModuleBodyRewriter`); the container simply is `__Exports`
instead of `{Stem}Hooks` / `partial class {ModuleName}`.
Components stay TOP-LEVEL `partial class {Name}` in the same namespace (G-07), `public` when
exported / `internal` when not (existing rule, `CSharpEmitter.cs:265-277`).
Every new-syntax file's own generated sources include `using static {ownNs}.__Exports;` so
same-file members are bare-visible from component bodies (this replaces what companion partial
merging used to provide).

**U-03 — Import lowering table (consumer side).** Extends `ImportScopeFacts` + `ResolveInjectedUsings` + VDG + HMR in lockstep:

| Import form | Target file mode | Lowered payload(s) (text between `using ` and `;`) |
|---|---|---|
| `import { a }` (a = value/util/hook) | new | `static {targetNs}.__Exports` (once per target file) |
| `import { A }` (A = component) | new | `A = {targetNs}.A` (skip if in `ReservedTypeAliases`) |
| `import { a as b }` (member) | new | NO using — emit a typed bridge into the consumer's `__Exports`: method → `internal static {Ret} b({params}) => global::{targetNs}.__Exports.a({argNames});`; value → `internal static {Type} b => global::{targetNs}.__Exports.a;` (bridges are `internal`, never exports of the consumer) |
| `import { A as B }` (component) | new | `B = {targetNs}.A` |
| `import * as X` | new | `X = {targetNs}.__Exports` (alias-to-type). Components under `X` are markup-only via dotted tags (U-05); C#-body type refs through `X` are NOT supported in 0.9.0 — documented; use a named import. |
| `import X from` (default) | new | default = component → `X = {targetNs}.{DefaultName}`; default = member → bridge as in rename |
| `import { … }` named | **legacy** | today's payloads exactly (`static {ns}.{StemHooks}` / `{Name} = {ns}.{Name}` — `ImportScopeFacts.cs:94-123`) |
| `* as` / default / rename | **legacy** | **UITKX2109 error, Unity-local** (target must migrate first) |

Signature data for bridges comes from the parse (new grammar carries full typed heads); extend the
peer tables (`SourceGenerator~/PeerComponentInfo.cs`, `PeerHookContainerInfo.cs`, `PeerModuleInfo.cs`)
with a new `PeerExportInfo` (name, kind, returnType, paramList text, isDefault) built in the SG
pre-scan, and give `ImportScopeFacts` the same data by re-parsing the target (it already
filesystem-parses targets — `ImportScopeFacts.cs:76-79`).

**U-04 — Plain-declaration parse (new declaration loop).** After the (unchanged) preamble, at top
level repeatedly parse:
1. `export default <Ident> ;` → default-export marker (2327 on duplicate; 2323 if unknown at end of parse).
2. `export { a, b } ;` → deferred export list (2323 per unknown name; **2324 if a name is already exported** — inline `export` + list, twice in one list, or across lists; names mark `IsExported` on the matching declarations).
3. `[export] <declaration-head>`:
   - Read optional `export`. Then scan the head: capture tokens up to the FIRST top-level `(`
     (function) or `=` (value) or `;`. The identifier immediately before that delimiter is the
     **Name**; everything before the Name is the **Type text** (may be empty → inference form,
     legal only for `= new T …` values, else **UITKX2322** value-export type-inference failure).
   - Delimiter `=`: **value**. Consume balanced initializer to `;`.
   - Delimiter `(`: read balanced params, then body: balanced `{ … }` OR `=> …;`.
   - Classify per G-03: normalized return-type token (strip `global::` + namespace quals) equals
     `VirtualNode` ⇒ **component** (reuse the existing body machinery: `TryFindTopLevelReturn`,
     setup-code split, markup ranges — the plain form's body is IDENTICAL in content to today's
     `component X(...) { ... }` body, only the header differs); Name matches `^use\p{Lu}` ⇒
     **hook**; else **util**.
   - Cross-guards: hook-classified + `VirtualNode` return ⇒ **2321 error**; component-classified +
     non-PascalCase ⇒ **2100** (existing code, message updated to cover the plain form).
4. Legacy wrapper keyword (`component`/`hook`/`module`, optionally after `export`) → parse via the
   EXISTING paths, emit **2320** warning, set file mode = legacy.
File mode is set by the FIRST declaration; a later declaration of the other style ⇒ **2108 error (Unity-local)**
(parse it best-effort for IDE resilience, but the file emits nothing past the error).
Model changes (`ParseResult.cs`): add `DeclKind { Component, Hook, Value, Util }`; unify
hooks/values/utils under a new `MemberDeclaration` record (Name, Kind, IsExported, ReturnTypeText,
ParamsText, BodyText, spans/columns) while KEEPING `HookDeclaration`/`ModuleDeclaration` for
legacy parses (the HMR reflection readers depend on those property names — additive only, never
rename/remove existing members of these records, `HmrCSharpEmitter.cs:1245-1246`);
`ImportDeclaration` gains `ImmutableArray<string?> Aliases` (parallel to `Names`), `bool IsStar`,
`string? StarAlias`, `bool IsDefault`, `string? DefaultAlias` — all ADDITIVE with defaults;
`DirectiveSet` gains `ImmutableArray<MemberDeclaration> MemberDeclarations`, `string? DefaultExportName`,
`bool UsesLegacySyntax`.

**U-05 — `* as X` in markup: dotted tags.** Extend `MarkupTokenizer.ReadTagName`
(`MarkupTokenizer.cs:149-157`) to accept exactly one interior `.` (`Ident '.' Ident`). A dotted tag
`<X.Comp/>` resolves ONLY when `X` is a `* as` (or default-import) binding in the file's preamble:
`PropsResolver` + the SG tag path + HMR tag map + VDG map `X.Comp` → FQN call of
`{targetNs}.Comp`; closing tags `</X.Comp>` must match verbatim. An unresolvable dotted head falls
into the existing unknown-tag diagnostics path. Semantic tokens color `X` as namespace-ish and
`Comp` as type (language-lib `SemanticTokens/`).

**U-06 — Identity keys (G-09 applied).** New-syntax files:
- component Register/Family key: `{fileNs}.{ComponentName}` (formula unchanged, ns value changes);
- hook family key: `{fileNs}.__Exports::{hookName}`;
- private members: file identity is already IN the namespace (file-keyed), so existing key shapes
  satisfy "file identity + declared name" — no extra disambiguator needed.
Update in lockstep: `CSharpEmitter.SelfFamilyKey` path (no code change, value flows from ns),
`HookEmitter.cs:172`, `HmrHookEmitter.cs:183`, `HookContainerRegistry.cs:193`,
`ImportScopeFacts.DeriveHookContainerClassName` (returns `__Exports` for new-mode targets — it
parses the target, so gate on the parsed `UsesLegacySyntax`), and the Hmr*Contract mirrors.
Document in CHANGELOG + migration guide: **migrating a file resets its hot-reload state once**.

**U-07 — Companion/catch-up removal.** New-syntax files NEVER merge partials across files:
`ModuleEmitter`'s cross-file merge (`ModuleEmitter.cs:136-151`) and HMR companion inlining
(`UitkxHmrCompiler.cs:415-421, 675-702`) run ONLY for legacy-mode files. When the legacy merge
path actually engages, emit **2107** (Unity-local deprecation warning) alongside the existing behavior.
2311/2312 remain live for legacy files during the window; schedule their retirement (→ reserved)
for the removal minor, NOT now.

**U-08 — Deprecation-window rules** (full matrix in §6): per-file, syntax-driven, no config knob.
`UsesLegacySyntax == true` (any wrapper keyword parsed) ⇒ legacy namespace derivation, legacy
emission, legacy import payloads, 2320/2107 warnings. New-syntax ⇒ everything new. A file with no
declarations at all ⇒ new mode.

**U-09 — Codemod = `UitkxMigrateImports` grows an `--es-modules` pass** (details §7). Pipeline per
G-10: tidy → export-normalize → wrapper rewrite → import insert/fix → format → zero-diagnostics
compile gate. Key rewrites: `export component X(p) {b}` → `export VirtualNode X(p) {b}`;
`export hook useX(p) -> (ret) {b}` → `export (ret) useX(p) {b}` (single-type returns lose the
parens: `-> int` → `int`); `export module M { members }` → strip wrapper, hoist members to top
level, each member gets `export`; every OTHER file's `import { M } from spec` →
`import * as M from spec` (preserves `M.member` call sites verbatim). Idempotent: running twice
changes nothing.

**U-10 — Formatter canonical forms.** Preamble order (existing `:43-51` order rule) extends to:
usings/namespace-imports, file imports (named, then `* as`, then default — stable by first
appearance). Canonical spellings: `import { a, b as c } from "./x"`, `import * as X from "./x"`,
`import X from "./x"` (no semicolons — house style, `DirectiveParser.cs:910-915` tolerates `;` on
read). `export default X;` and `export { … };` are canonically printed at END of file, in source
order. Member declarations are re-emitted from the records: `export {Type} {Name}{(params)} {body}`
with the existing body-formatting machinery; the A7f data-loss guard (`AstFormatter.cs:196-197`)
extends to the new record fields — every parsed field must be re-emitted or formatting a file
loses data (add snapshot tests FIRST).

**U-11 — Diagnostics allocation is §3.** The 2320–2329 band is family-frozen to the canonical
(Unreal-audited) meanings in §3.1 — never re-purpose a number. 2318/2319 stay free (they predate
the 2320 band; do not squat them). Unity-only concerns get Unity-LOCAL codes **2107–2110** in the
21xx declaration band (§3.2), with the divergences recorded per §3.3 (the 2105-severity
precedent), NOT squeezed into the family band.

**U-12 — Language-lib public-surface discipline.** Because HMR reflects into the committed
`Language.dll` by member NAME and SHAPE: all model/API changes are ADDITIVE (new members, new
overloads); nothing public is renamed, removed, or re-ordered in this campaign. Any place this
rule seems impossible = STOP AND ASK.

---

## 3. New diagnostics — family band 2320–2329 (exact messages) + Unity-local 2107–2110

Declare the family constants in `ide-extensions~/language-lib/Diagnostics/DiagnosticCodes.cs` after
`:276` (UnusedUsing), following the existing doc-comment style. **Harmonized 2026-07-17 to the
family-canonical (Unreal, audited) allocation** — cross-checked verbatim against the Godot leg's
`plans/ES_MODULES_EXECUTION_PLAN.md` §4, whose table already mirrors it. Meanings and numbering
below are family-frozen (engine prefix + minimal dialect adaptation only); if the family table
moves AGAIN, STOP AND ASK — do not renumber unilaterally.

### 3.1 Family band 2320–2329

| Code | Sev | Const name | Message (verbatim; `{n}` = args) |
|---|---|---|---|
| UITKX2320 | Warning | `DeprecatedWrapperKeyword` | `the '{0}' wrapper keyword is deprecated — write a plain 'export' declaration (the UitkxMigrateImports --es-modules codemod rewrites it); the wrapper is removed in a later minor` ({0} = `component` / `hook` / `module`) |
| UITKX2321 | Error | `HookReturnsMarkup` | `'{0}' is 'use'-prefixed but returns VirtualNode — did you mean a component? (components are PascalCase and return VirtualNode)` |
| UITKX2322 | Error | `ValueExportTypeInference` | `value export '{0}' cannot infer its type — the initializer must name the type ('= new T {{ … }}'); otherwise declare 'export <Type> {0} = …'` — EMITTED in Unity (typed dialect; Godot registers this meaning but never emits it) |
| UITKX2323 | Error | `ExportOfUndeclaredName` | `{0} names '{1}', which is not a top-level declaration in this file` ({0} = `'export default'` / `'export {{ … }}'`) |
| UITKX2324 | Error | `DuplicateExport` | `'{0}' is already exported — remove the duplicate export` |
| UITKX2325 | Error | `ImportAliasCollision` | `import alias '{0}' collides with {1} — rename the import` ({1} = `a declaration in this file` / `another import`) |
| UITKX2326 | Error | `DefaultImportWithoutDefault` | `'{0}' has no default export — use a named import: import {{ {1} }} from "{2}"` ({0} = target file, {1} = suggested name, {2} = specifier) |
| UITKX2327 | Error | `DuplicateDefaultExport` | `duplicate 'export default' — a file has at most one default export` |
| UITKX2328/2329 | — | — | reserved (family; do not allocate) |

### 3.2 Unity-local codes — 21xx declaration band (2107–2110)

Four concerns from earlier drafts of this table are Unity-ONLY and move OUT of the family band into
the local function-style/declaration band **21xx**: 2100–2106 are allocated, **2107–2110 verified
free repo-wide (2026-07-17)**. The 21xx codes are emitted today as inline `Code = "UITKX21xx"`
strings from `DirectiveParser` (2105 at `:84/:112/:193`, 2106 at `:1492`) — emit these the same
way AND add constants to `DiagnosticCodes.cs` for the registry/docs.

| Code | Sev | Const name | Message (verbatim) |
|---|---|---|---|
| UITKX2107 | Warning | `DeprecatedCompanionMerge` | `companion partial-class merging is deprecated — '{0}' merges into '{1}' via legacy folder namespaces; migrate the companion set to plain declarations and file imports` |
| UITKX2108 | Error | `MixedDeclarationStyles` | `legacy wrapper declarations and plain declarations cannot be mixed in one file — the file's first declaration sets its style` |
| UITKX2109 | Error | `ImportFormNeedsMigratedTarget` | `namespace/default/renamed import of '{0}' requires the target file to use plain-declaration syntax — migrate '{1}' first` |
| UITKX2110 | Error | `HookRenameDropsUsePrefix` | `renaming hook '{0}' to '{1}' drops the 'use' prefix — hook bindings must stay 'use'-prefixed` |

### 3.3 Recorded family divergences (register in the family table notes, like the 2105 precedent)

Precedent: the family corpus already records per-engine divergences — UITKX2105 severity
("two components is the one-per-file convention warn UITKX2105 (not an error)",
`uitkx-scanner-cases.json:366`) and the 2313 registry-note divergence (archive
`IMPORT_EXPORT_PLAN.md:61`). Record for this campaign:
1. **Mixed wrapper/plain files are LEGAL (per-decl warnings) in Unreal + Godot but an ERROR in
   Unity (UITKX2108)** — Unity's declaration style also selects the namespace-derivation mode
   (folder-keyed vs file-keyed, U-08), so a mixed file has no coherent namespace identity. That is
   why it is a Unity-LOCAL code, not a family one.
2. The four Unity-local codes **2107/2108/2109/2110** are intentionally OUTSIDE the family band —
   companion merging, mixed-style mode ambiguity, the migrated-target import gate, and the
   hook-rename guard are Unity-only surfaces.
3. **UITKX2322 is EMITTED in Unity** (C# inference sugar is real) while Godot pins the number to
   the meaning but never emits it.

Emit split follows the existing family convention: scan-side (parser/`StrictImportDetector`) codes
surface in both the LSP (`DiagnosticsPublisher`) and the SG pipeline (as Roslyn diagnostics, the
way 2303/2306/2311 flow today — 2306 emission `UitkxPipeline.cs:92`, emit-stage 2311 flow
`:166-186`; `ParseDiagnostic.Code` strings pass through the generic
`spc.ReportDiagnostic` path, `UitkxGenerator.cs:279`, so **no new `DiagnosticDescriptor` entries in
`SourceGenerator~/Diagnostics/UitkxDiagnostics.cs` are needed** for the new codes — family 232x
and Unity-local 2107-2110 alike). PascalCase
enforcement stays on existing **2100** (update its message to also cover the plain form). Add every
new code (both tables) to the docs diagnostics page and to the LSP publisher's mapping.

---

## 4. What "done" looks like (target shapes)

### 4.1 Source dialect (the general plan §3 example is the normative fixture — add it verbatim as a test)
See `Plans~/ES_MODULES_GENERAL_PLAN.md` §3. Key Unity spellings: `export Style container = new Style {…};`,
`export int MaxItems = 5;`, `export theme = new Style {…};` (inference), `export string F(int x) {…}`,
`export (int value, Action reset) useCountdown(int start) {…}`, `Style rowStyle = …;` (private),
`export VirtualNode ScoreRow(string label) { return ( <Label …/> ); }`, private component
`VirtualNode ScorePanel(...)`, `export default ScorePanel;`.

### 4.2 Generated C# for that file (SG + HMR + VDG must agree on shape)
```csharp
namespace Ruitk.Samples.<Folders>.<FileStem>          // file-keyed (U-01)
{
    public static partial class __Exports                     // U-02
    {
        public static Style container = new Style { Padding = 10f };
        public static int MaxItems = 5;
        public static Style theme = new Style { BackgroundColor = ColorGray };
        public static string FormatScore(int score) { … }     // util (HMR trampoline pattern)
        public static (int value, Action reset) useCountdown(int start) { … } // hook (trampoline + [HookSignature])
        internal static Style rowStyle = new Style { MarginTop = 2f };        // private
    }
    public partial class ScoreRow { … }                       // exported component → public
    internal partial class ScorePanel { … }                   // private component → internal
    // __Exports__UitkxHookRefresh companion: family keys "{ns}.__Exports::useCountdown"
    // ScorePanel__UitkxRefresh: Register("{ns}.ScorePanel", …)
}
```
Importer lowering per U-03. The importer's own generated sources always contain
`using static {ownNs}.__Exports;`.

---

## 5. Milestones

House rules for EVERY milestone: research the exact touched lines first (anchors above are the
map, re-verify before editing — the tree moves); develop; add/extend tests IN the milestone; run
the full verify block; bughunt anything red; commit with a `feat(uitkx-esm): M<n> — <summary>`
message; NEVER weaken an existing gate/assertion to get green (if a gate seems wrong, STOP AND
ASK); no push.

The canonical verify block (referenced below as **VERIFY-CORE**):
```bash
# run from the repo root (the checkout you are in — the folder holding config.json)
dotnet test SourceGenerator~/Tests/Ruitk.SourceGenerator.Tests.csproj -v q --nologo | tail -2
dotnet test ide-extensions~/lsp-server/Tests/UitkxLanguageServer.Tests.csproj -v q --nologo | tail -2
node scripts/corpus-hash.mjs --check
```
Unity compile gates (run from the **Unity project root** — `../..` from the repo root, the folder
that contains `Assets/`; these six names verified to exist there — there is NO `Ruitk.csproj`) —
referenced as **VERIFY-UNITY**:
```bash
dotnet build Ruitk.Shared.csproj -v q --nologo          # engine core (Shared/) — 0 errors
dotnet build Ruitk.Runtime.csproj -v q --nologo         # MonoBehaviour adapter — 0 errors
dotnet build Ruitk.Editor.csproj -v q --nologo          # Editor (incl. HMR) — 0 errors
dotnet build Ruitk.Examples.csproj -v q --nologo        # Samples asmdef 1 — 0 errors
dotnet build Ruitk.Samples.csproj -v q --nologo         # Samples asmdef 2 — 0 errors
dotnet build Ruitk.Diagnostics.csproj -v q --nologo     # 0 errors
```
(BOTH `Ruitk.Examples.csproj` and `Ruitk.Samples.csproj` compile `Samples/` code —
two asmdefs. If a csproj name differs on the machine, list
`*.csproj` in the Unity project root and use the obvious match; if none matches, STOP AND ASK.)

### M0 — Baseline, family sync, corpus cases (no product code)
1. `git status --short`. Expected working-tree state (verified 2026-07-17): the two §1.6 baseline
   fixes are UNCOMMITTED — `SourceGenerator~/Tests/ImportCorpusManifestTests.cs` (constant re-pin
   to `917dd8cd…`) and `Samples/Components/HmrTests/SomeOtherName.style.uitkx` (trailing newline) —
   plus binary churn on `Analyzers/*.dll` from a local rebuild with no language/generator source
   change. Anything ELSE dirty — STOP AND ASK.
2. Create branch `feat/es-modules`. Drop the DLL churn (`git checkout -- Analyzers/`) per
   guardrail §10.8 — no source changed, so the committed DLLs stay as HEAD has them.
3. Run VERIFY-CORE; **verify the baseline is fully green first**: 1554/1554 SG + 123/123 LSP +
   corpus hash OK. Any red — STOP AND ASK (do not "fix forward" into M1 on a red base).
4. Commit the two baseline fixes:
   `fix(tests): re-pin FrozenFamilyHash to 917dd8cd + formatter fixed-point newline (baseline green)`.
5. **Family corpus cases (G-13 gate):** check
   `ide-extensions~/lsp-server/test-fixtures/uitkx-scanner-cases.json` `_tiers.familyCore` for
   ES-modules cases (plain declarations, `* as`, `as`-rename, default, export lists — search for
   `"export default"` / `"* as"` in the file). **Verified 2026-07-17: zero such cases exist yet**,
   so expect the defer branch: the emit-side milestones proceed on Unity-local tests, and you DO
   NOT touch the corpus/hash until the family cases arrive (they come from the Unreal leg per
   G-13) — record this in the commit message. If they HAVE arrived by execution time, mirror
   byte-identically and re-pin hash + constant together.

Gate: VERIFY-CORE fully green (1554/1554 + 123/123 + hash OK) and the two fixes committed.

### M1 — Parser + model + diagnostics (language-lib only; nothing emits new shapes yet)
1. `DiagnosticCodes.cs`: add the §3.1 family constants (2320-2327) AND the §3.2 Unity-local
   constants (2107-2110) + doc comments; the 21xx codes are additionally emitted inline-string
   style from `DirectiveParser` like 2105/2106 today (§3.2).
2. `ParseResult.cs`: additive model changes per U-04 (MemberDeclaration, DeclKind, ImportDeclaration
   alias/star/default fields, DirectiveSet.MemberDeclarations/DefaultExportName/UsesLegacySyntax).
3. `DirectiveParser.cs`:
   - Extend `TryReadFunctionStyleImport` (`:851`) for `a as b` inside braces, and add readers for
     `import * as X from "…"` and `import X from "…"` (default). Keep the cursor-restore
     discipline the existing readers use (save/restore `i`/`line` on any mismatch) and the
     trailing-`;`/rest-of-line tolerance (`:910-919`). The bare `import "Ns"` reservation (`:962`)
     stays.
   - Add the plain-declaration loop per U-04 (new methods, e.g. `TryParseMemberDeclaration`,
     `TryReadDeclarationHead`), wired into the dispatch at `:218-249` and the continuation loop at
     `:520-620`; `export default` / `export { … }` list parsing; emission of 2320 (wrapper
     deprecation), 2108 (mixed styles, Unity-local), 2321 (hook returns markup), 2322 (inference
     failure), 2323 (undeclared export), 2324 (duplicate export), 2327 (duplicate default);
     duplicate-import 2303 keying extended to the BOUND name (alias if present).
   - Legacy paths untouched except the 2320 warning + `UsesLegacySyntax` flag.
4. `MarkupTokenizer.ReadTagName` (`:149`): one-dot tag names (U-05). Guard: plain (undotted) tags
   must tokenize byte-identically — the family scanner corpus (`fileScan` tier) is the watchdog.
5. `StrictImportDetector` + `UitkxImportGraph` + `ImportResolver`: teach them the new
   ImportDeclaration fields (star/default resolve to the file; default import against a target
   with no `export default` → **2326**; an `as`-rename or `* as` alias colliding with a
   declaration in the file or another import binding → **2325**; renamed hooks keep `use` prefix
   → **2110** (Unity-local); star/default/rename against a legacy target → **2109** (Unity-local)).
5b. `Diagnostics/DiagnosticsAnalyzer.cs` (Tier-2, §1.5): audit every rule that walks
   `ComponentDeclarations`/`HookDeclarations`/`ModuleDeclarations` — new-mode components/hooks
   parsed into `MemberDeclaration` records must still receive the hook rules (0013-0016), 0107,
   0108, 0111; decide 0103 (component-name/filename match) semantics for multi-declaration
   new-mode files (recommendation: keep it scoped to the default-exported / sole component, as
   today's multi-component files already behave) — if ambiguous, STOP AND ASK.
6. Tests (extend, never rewrite): `ParserTests`, `ImportExportParsingTests` (+ the §4.1 normative
   fixture verbatim), new `PlainDeclarationParsingTests` (classification table: component / hook /
   value / util / inference + 2322 failure / cross-guards 2321+2100 / mixed-file 2108 / default
   2327-dup + 2323-unknown + 2326-no-default-export / export-list 2323 + duplicate-export 2324 /
   rename 2303-on-alias + alias-collision 2325 / hook-prefix 2110).

Gate: VERIFY-CORE green (test totals GREW; nothing skipped). `Analyzers/` DLLs NOT rebuilt yet
(Unity behavior unchanged this milestone — the new grammar is parseable but nothing consumes it).

### M2 — File-keyed namespaces + identity seams
1. `NamespaceDerivation.DeriveFileModule` + `EffectiveNamespace.Resolve` 4-arg overload (U-01,
   U-12). Callers updated: `UitkxPipeline.ResolveEffectiveNamespace :534` (pass the parse's mode),
   `ImportScopeFacts :64/:84` (target mode from the target parse), VDG `:344`, LSP call sites.
2. HMR: update the reflection binding (`UitkxHmrCompiler.cs:114, 1556-1557, 3051-3130` — incl.
   `ComputeEffectiveNsForFile :3131`) to the 4-arg
   overload; thread the mode flag from HMR's own parse (it reflects `DirectiveSet` — read
   `UsesLegacySyntax` by property name, additively).
3. Audit EVERY consumer in §1.3 — for each, decide "value flows through" (most) vs "shape changes"
   (`DeriveHookContainerClassName` → `__Exports` for new-mode targets; `HookContainerRegistry`
   entries pick up new FQNs automatically).
4. Tests: `NamespaceDerivationTests` (+file-stem cases: plain stem, dotted stem `Foo.style` →
   `Foo_style`, digit-leading, reserved keyword), `NamespacePrefixResolutionTests`,
   `ImportScopeFactsTests` (new-mode targets), a new `FileKeyedNamespaceTests` asserting: two
   same-folder new-mode files get DIFFERENT namespaces; legacy files still share.

Gate: VERIFY-CORE green.

### M3 — SG emitters (__Exports + import lowering + deprecation behavior)
1. New `SourceGenerator~/Emitter/ExportsEmitter.cs` emitting `__Exports` per U-02/§4.2. Reuse
   `ModuleBodyRewriter` for the HMR-trampoline member rewriting and `HookEmitter`'s
   `[HookSignature]`/refresh-companion machinery (family keys per U-06). Baseline usings block =
   copy of `ModuleEmitter.cs:43-78` (keep the Unity 6.3 `#if` alias block byte-identical — pinned
   by `Unity63AliasEmissionTests`).
2. `UitkxPipeline.cs`: route new-mode files → ExportsEmitter + CSharpEmitter-per-component; extend
   `ResolveInjectedUsings` (`:553+`) + peer tables per U-03 (incl. bridge emission for
   rename/default member imports — bridges land inside the consumer's `__Exports`); legacy-mode
   files keep today's route byte-identically; 2107/2109 emission (Unity-local).
3. `PropsResolver.cs` + tag path: dotted-tag resolution per U-05.
4. `ModuleEmitter.cs`: legacy-only guard + 2107 at the cross-file merge (`:136-151`).
5. Tests: `EmitterTests` additions (the §4.2 golden shape as inline-expected strings, same style as
   existing tests), `NamespaceImportEmitTests`, `ImportScopedHookInjectionTests` (new-mode),
   bridge-emission tests (method/value/hook rename + default), dotted-tag emission test,
   deprecation tests (legacy file emits byte-identical output + 2320/2107).

Gate: VERIFY-CORE green. Then rebuild committed DLLs + Unity gates:
```powershell
scripts/build-generator.ps1      # Release; commit Analyzers/*.dll in this milestone's commit
```
then VERIFY-UNITY (samples are still legacy-syntax → must compile with only 2320/2107 warnings;
warning STORMS are expected and fine — errors are not).

### M4 — HMR parity (build on the just-landed threading)
1. `HmrCSharpEmitter.cs` / `HmrHookEmitter.cs`: mirror M3's shapes for new-mode files (container
   `__Exports`, family keys U-06, importer payloads via the SAME reflected
   `ImportScopeFacts.ComputeInjectedUsingPayloads` — that seam already exists, `:117/:1562-1564`,
   which is why M2/M3 extended ImportScopeFacts rather than the SG privately).
2. `UitkxHmrCompiler.cs`: companion inlining (`:415-421, 675-702`) legacy-only; import-target
   recompile candidates (`:1233-1266`) extended to `* as`/default targets; `result.FamilyKey`
   (`:392-395`) unchanged in shape.
2b. `UitkxHmrController.cs` (§1.3 seams): the companion redirect `ResolveParentComponentFile`
   (`:363-366`) goes LEGACY-ONLY (a new-mode `X.style.uitkx` is its own module — compile IT, not a
   parent); the import reverse-dependency map (`:79-83, 256-257, 379-381`) must learn `* as`/
   default import edges so edits to a namespace-imported file still fan out to importers.
2c. `UitkxHmrModuleStaticSwapper.cs`: audit the same-`Type.FullName` matching (`:235, 276`) against
   `__Exports` — per-file namespaces make `{fileNs}.__Exports` unique, so FullName matching holds,
   but verify the type filters (`:313-317`) don't skip underscore-prefixed names; and
   `UitkxHmrDelegateSwapper.cs` hook-swap paths pick up the new `{fileNs}.__Exports::{hook}` keys
   via `HookContainerRegistry` (value flows; assert in a contract test).
3. Update ALL Hmr*ContractTests mirrors in the SAME commit (they are source-text/shape pins —
   expect `HmrEmitterParityContractTests` + `HmrModuleNamespaceParityContractTests` churn).
4. Tests: contract suites green; add a parity case for the §4.1 fixture (SG emit vs HMR emit
   shape-compare, the pattern the existing parity tests use).

Gate: VERIFY-CORE green; `scripts/build-generator.ps1` re-run if language-lib changed; VERIFY-UNITY
green (the Editor csproj compiles the HMR code — that is the compile gate for this milestone; live
hot-reload smoke happens in M8).

### M5 — VDG + LSP
1. `VirtualDocumentGenerator.cs`: new-mode files → `__Exports` scaffold (mirror `:379-380` hook
   container shape), per-component partials (`:314-315`), injected payloads already flow from
   ImportScopeFacts (`:243-248`); default/star/rename handled; legacy path untouched.
2. LSP handlers (all anchors §1.5): CompletionHandler — replace wrapper-keyword snippets in
   `FunctionStylePreambleItems :342-358` with plain-declaration snippets (`export VirtualNode `,
   `export `, `import { … } from ""`, `import * as  from ""`, `import  from ""`,
   `export default ;`, `export { };`) and KEEP `component`/`hook`/`module` snippets flagged
   `(deprecated)` for the window; `GetImportBraceCompletions :1584-1590` walks
   `MemberDeclarations` + components + default; DefinitionHandler regexes `:430, :628-645` →
   accept BOTH grammars (`(?:export\s+)?(?:component\s+|hook\s+|module\s+)?` + plain-head match on
   the known name); RenameHandler — decl/tag/hook regexes both grammars; import-list pass
   `:490-538` renames aliases correctly (rename the LOCAL binding: if `a as b`, renaming `b`
   touches only the alias + local uses; renaming the export `a` in its file touches `a` in import
   lists but NOT `b` bindings); 2110 (Unity-local) surfaced on hook alias violations;
   WorkspaceIndex regexes
   `:155/:163/:173` + `IndexUitkxFile :684` → both grammars + export lists + default;
   DiagnosticsPublisher: publish family 2320-2327 + Unity-local 2107-2110 — and route
   dependent-file republication through the
   EXISTING `ScheduleDebouncedRevalidation` path (`:96-98`, F5-wave), never per-keystroke;
   **ReferencesHandler** regexes (`:457, 474, 526, 537, 641, 651`) → both grammars + alias-aware
   (references of `a` include `a as b` import sites but not `b` uses, mirroring the rename rule);
   **HoverHandler** — hook hover (`:285-297`) and HookRegistry docs (`:561-567`) must resolve
   plain-decl hooks; `DirectiveExample :582-587` and any wrapper-keyword hover text gain a
   "(deprecated — plain `export` declarations)" note; **ImportCodeActionHandler** — verify the
   2305 quick-fix insertion point stays correct when the preamble contains `* as`/default lines
   (no new code actions in 0.9.0 — 2109 gets no auto-fix, migration is the codemod's job).
2b. Language-lib `SemanticTokens/SemanticTokensProvider.cs` (§1.5): extend `s_importTokenRe`
   (`:145-148`) to the `as`-rename / `* as` / default forms, extend `s_exportTokenRe` (`:150-153`)
   to plain `export` declarations (`export` + `default` as keywords), and add the U-05 dotted-tag
   coloring (`X` namespace-ish, `Comp` type).
3. Tests: LSP suite additions — plain-decl completion, import completion with alias/star/default,
   go-to-def through alias, and (new coverage, currently ZERO) a `RenameTests.cs` for the
   import-list pass — old-name, aliased, and hook-prefix cases.

Gate: VERIFY-CORE green.

### M6 — Formatter + grammar + schema
1. `AstFormatter.cs` per U-10. Extend the A7f guard consciously: every new parsed field re-emitted.
2. `uitkx.tmLanguage.json`: add patterns — `export-default-declaration`
   (`^\s*(export)\s+(default)\s+([A-Za-z_][A-Za-z0-9_]*)`), `export-list-declaration`
   (`^\s*(export)\s*\{[^}]*\}`), extend `import-declaration :81-93` for `as` + `* as` + default
   forms, add a `plain-declaration` pattern scoping `export` as keyword + `VirtualNode` return +
   name (component=entity.name.class when PascalCase after VirtualNode; hook=entity.name.function
   when `use`-prefixed). KEEP the three wrapper patterns (deprecation window). VS Code prebuild
   copies automatically (`vscode/package.json:153`) — the copy at
   `vscode/syntaxes/uitkx.tmLanguage.json` IS git-tracked (verified), so commit the refreshed copy.
3. `uitkx-schema.json`: bump `version` to "1.2"; directives section — mark the `component`
   directive entry (`:323-326`) deprecated (keep for window); add nothing for plain decls (schema
   describes tags/attributes/directives, not declarations). `SchemaRegistryParityTests` must stay
   green untouched.
4. VS2022 `UitkxClassifier.cs`: add `export`(if absent)/`default`/`from`/`as` to `_keywords`; keep
   `hook`/`module`/`component` for the window.
5. Tests: `FormatterSnapshotTests` — add round-trip + idempotency snapshots for the §4.1 fixture
   and each new import form; `FormatterImportTests` for alias/star/default ordering.

Gate: VERIFY-CORE green; `cd ide-extensions~/vscode && npm ci && npm run build` exits 0.

### M7 — Codemod + Samples migration + goldens/corpus re-pin (the flip)
1. Implement `--es-modules` in `UitkxMigrateImports` per U-09/§7. Unit tests in `CodemodTests`
   (idempotence, each rewrite form, module→`* as` call-site preservation, companion-set handling).
2. Run over samples:
```bash
dotnet run --project SourceGenerator~/Tools/UitkxMigrateImports -- Samples --es-modules
git checkout -- "Samples/Components/UitkxTestFileDoNotTouch/"   # fixture stays byte-identical, ALWAYS
dotnet run --project SourceGenerator~/Tools/UitkxMigrateImports -- Samples --format
git checkout -- "Samples/Components/UitkxTestFileDoNotTouch/"
```
3. Expect: 165 declaration sites rewritten (119+7+39) minus the DoNotTouch fixture's share; all
   sample files become new-mode; `SamplesCorpusGateTests` (zero-error gate over the whole corpus)
   green; `FormatterSnapshotTests` idempotency over all files green.
4. Update the ~50 C# consumer `using` lines: file-keyed namespaces move every sample namespace
   again (`Ruitk.Samples.<Folders>` → `Ruitk.Samples.<Folders>.<FileStem>`). Use the
   same technique as `Plans~/SAMPLES_NAMESPACE_MODERNIZATION_PLAN.md` Appendix B (that file lists
   every consumer; recompute the ADD lines by appending the declaring file's stem). Iterate on
   VERIFY-UNITY compile errors — CS0246 in a `.cs` = missed using.
5. Family corpus: if (M0 step 5) the family ES-modules cases are pinned, ensure the mirrored
   corpus matches byte-identically, then `node scripts/corpus-hash.mjs --write` + update
   `FrozenFamilyHash` + verify the SAME hash is what Unreal/Godot pin (read their repos'
   `plans/family-corpus.hash` if those sibling checkouts are available locally; else
   record "hash re-pin pending family sync" in the commit and DO NOT write a hash the other repos
   don't have — STOP AND ASK instead).
6. Rebuild committed DLLs (`scripts/build-generator.ps1`), VERIFY-UNITY.

Gate: VERIFY-CORE + VERIFY-UNITY fully green; `git diff --stat Samples/Components/UitkxTestFileDoNotTouch/` empty;
re-running the codemod (`--es-modules --check`) reports zero changes (idempotence proof).

### M8 — In-editor verification (manual, with the owner)
Compile gates cannot exercise live HMR. Ask the owner to run the Unity editor smoke: open a sample
window (e.g. Showcase, plus the `Samples/Components/HmrTests/` bed — component + imported style
module + runtime bootstrap, added by `43f59670` exactly as the in-editor repro for these seams),
edit a migrated `.uitkx` (markup tweak → hot swap; hook body tweak →
state preserved; hook signature change → state reset once; `.style` module edit → static swap, no
CS0103), confirm ~50-200 ms swap and zero
console errors. Record results in the PR/commit body. Do NOT skip this milestone silently — if the
owner defers it, note that explicitly in the changelog entry ("editor smoke pending").

### M9 — Sync surface + versions (G-12; checklist §9)
Do every §9 row, then final full VERIFY-CORE + VERIFY-UNITY + `node scripts/changelog.mjs verify`.
Commit `chore(release): package 0.9.0 + extensions 1.5.0 — ES-modules redesign`. NO push, NO tag
(publish.yml is owner-triggered).

---

## 6. Deprecation-window behavior matrix (0.9.0; removal in a later minor, owner-triggered)

| Situation | Parse | Namespace | Emission | Diagnostics |
|---|---|---|---|---|
| File: all legacy wrappers | legacy paths | folder-keyed (unchanged) | legacy, byte-identical | 2320 per wrapper decl; 2107 iff cross-file merge engages |
| File: all plain decls | new loop | file-keyed (U-01) | `__Exports` + component partials | none of the deprecation codes (2321-2327 only on actual errors) |
| File: mixed styles | first decl sets mode; other-style decl errors | per first decl | up to the error only | 2108 (Unity-local; legal-with-warnings in Unreal/Godot — §3.3) |
| New file `import { x }` from legacy file | ok | — | legacy payloads (`{Stem}Hooks` / alias) | none |
| New file `* as`/default/rename from legacy file | parses | — | none for that import | 2109 |
| Legacy file `import { x }` from new file | ok | — | `static {ns}.__Exports` payload | none |
| Legacy file with new import forms (`* as` etc.) | ok (import surface is mode-independent) | — | per U-03 | 2109 only if TARGET legacy |
| Companion set, one file migrated | both parse | migrated: file-keyed; other: folder-keyed | merge silently stops (different ns) → bare refs break | CS0103/CS0246 at build — codemod migrates companion SETS atomically to prevent this; guardrail §10.6 |
| `@namespace` explicit stamp | either mode | stamp wins (unchanged) | per mode | none |

---

## 7. Codemod spec + migration guide outline

### 7.1 `--es-modules` pass (added to `SourceGenerator~/Tools/UitkxMigrateImports`)
Order (idempotent as a whole; each step is a no-op on already-migrated input):
1. **tidy** — existing `TidyUsings` (`UitkxMigrator.cs:122-151`).
2. **export-normalize** — existing pass 1/2 machinery; every referenced decl exported (unchanged
   default), honoring existing behavior for unreferenced decls.
3. **wrapper rewrite** (per file, per decl — U-09 shapes):
   - `export? component N(params?) { body }` → `export? VirtualNode N(params?) { body }`
     (parameterless keeps empty parens: `component N {` → `VirtualNode N() {`).
   - `export? hook useN(params) -> (t1 a, t2 b) { body }` → `export? (t1 a, t2 b) useN(params) { body }`;
     single-type return `-> T` → `T`.
   - `export? module M { members }` → strip header/closing brace, dedent one level, prefix each
     top-level member with `export ` (skip members already `internal`-intended? No — module members
     were all public before; export ALL).
4. **import rewrite** (whole-tree pass): any `import { M } from spec` where `M` was a module decl →
   `import * as M from spec`; imports of hooks/components/values unchanged; insert missing file
   imports for names that used to resolve via companion-merge/folder ambience (reuse
   `ScanReferences`, extended to member names from the old module bodies); DELETE now-dead
   namespace-imports of old folder namespaces.
5. **companion atomicity** — files are processed as companion SETS (`X.uitkx` + `X.*.uitkx` in one
   folder): all migrate or none; a set that cannot fully migrate (parse error) is skipped whole +
   reported.
6. **format** — AstFormatter last.
7. **zero-diagnostics gate** — after writing, re-run the pipeline over the tree; any NEW error
   diagnostic → nonzero exit + report (the runner then iterates or reverts; in this plan's M7 the
   gate is `SamplesCorpusGateTests` + VERIFY-UNITY).
`--check` works with `--es-modules` (dry-run diff count, exit 1 if changes).

### 7.2 Migration guide (`Plans~/MIGRATION_GUIDE.md` — append an "0.9.0 ES modules" section)
Outline: why (file = module); before/after of the §4.1 example; the five mechanical rules
(wrapper→plain, module→`* as`, companions become modules, file-keyed namespaces move C# `using`
lines, hot-reload state resets once); codemod usage (`--es-modules`, `--check`, companion sets);
deprecation timeline (0.9.0 warns via 2320/2107, removal in a later minor); escape hatches
(`@namespace`, partial-class extension per G-07); troubleshooting table (= §11).

---

## 8. Committed-generated-output flag list (things that are BUILT but checked in — update + commit knowingly)

| Artifact | When it changes | How to regenerate |
|---|---|---|
| `Analyzers/Ruitk.SourceGenerator.dll` + `Ruitk.Language.dll` | any SG/language-lib source change (M1-M7) | `scripts/build-generator.ps1` (Release) — commit both |
| `Plans~/family-corpus.hash` + `FrozenFamilyHash` constant (`ImportCorpusManifestTests.cs:23`) | corpus case changes (M0/M7) | `node scripts/corpus-hash.mjs --write` + edit constant — ALWAYS together, family-synced |
| `ide-extensions~/lsp-server/test-fixtures/uitkx-scanner-cases.json` | family corpus adoption | byte-identical mirror from the family source |
| `SourceGenerator~/Tests/Golden/HookRegistry/*.golden.*` | HookRegistry metadata changes | test-driven re-pin (HookRegistryTests) |
| `FormatterSnapshotTests.cs` inline snapshots | formatter canonical-form changes (M6) | update expected strings alongside the change |
| `ide-extensions~/vscode/syntaxes/uitkx.tmLanguage.json` | grammar changes (M6) | `npm run build` prebuild copy (commit if tracked) |
| `ide-extensions~/vscode/README.md` + `ide-extensions~/visual-studio/UitkxVsix/overview.md` | changelog.json changes (M9) | `node scripts/changelog.mjs extract/extract-overview` — never hand-edit; `verify` gates |
| `ide-extensions~/vscode/server/**` (published LSP) | LSP changes | `dotnet publish ide-extensions~/lsp-server -c Release --self-contained false -o ide-extensions~/vscode/server` |

---

## 9. Full sync-surface checklist (G-12 — every row is an M9 gate item)

- [ ] `CHANGELOG.md` — new `## [0.9.0] - <date>` above `## [0.8.3]` (line 9): ES-modules redesign,
      plain declarations, full import surface, file-keyed namespaces, companion deprecation, codemod,
      one-time hot-reload state reset note, final test totals line.
- [ ] `plans/DISCORD_CHANGELOG.md` — prepend 0.9.0 entry, **ASCII only, ≤2000 chars**, house
      format (`### Minor - …`, bold lead-in, footer `Unity package **0.9.0** + IDE extensions **1.5.0**`).
- [ ] `package.json` line 4: `0.8.3` → `0.9.0`.
- [ ] `ide-extensions~/vscode/package.json` line 6: `1.4.4` → `1.5.0`.
- [ ] `ide-extensions~/visual-studio/UitkxVsix/source.extension.vsixmanifest` line 7 Identity
      Version: `1.4.4` → `1.5.0` (keep `<Description>` under 280 chars if touched).
- [ ] `ide-extensions~/rider/gradle.properties` line 3: `1.1.0` → `1.2.0`.
- [ ] `ide-extensions~/changelog.json` — new top entry `{date, versions:{vscode:"1.5.0",
      vs2022:"1.5.0", rider:"1.2.0"}, shared:[…]}`; then `node scripts/changelog.mjs extract --ide vscode --out ide-extensions~/vscode/CHANGELOG.md`,
      `extract` README regen + `extract-overview`, and `node scripts/changelog.mjs verify` → exit 0.
- [ ] Docs site (`ReactiveUIToolkitDocs~/src/pages/`) — NOTE: the code samples live in sibling
      `*.example.ts` files next to each `*Page.tsx`, and wrapper-keyword syntax appears in **24
      files** (verified by grep), not just the two primary pages. Rewrite
      `UITKX/Imports/UitkxImportsPage.*` + `UITKX/CompanionFiles/CompanionFilesPage.*` (primary);
      update every other hit: `UITKX/{AdvancedAPI, Assets, Components, Context, CustomRendering,
      Diagnostics (new §3 tables: family 2320-2327 + Unity-local 2107-2110, incl. the §3.3
      divergence notes), Differences, Events, GettingStarted, Guides, Hooks,
      Introduction, Portal, Reference, Router, Signals, Styling, Suspense}` plus
      `Components/Audio`, `Components/Video`, `Tooling/HMR`. Re-run the sweep before closing:
      `grep -rln "component [A-Z]\|hook use\|module [A-Z]\|export component\|export hook\|export module" ReactiveUIToolkitDocs~/src/pages/`
      → only intentional "legacy syntax" callouts remain. `cd ReactiveUIToolkitDocs~ && npm run build` → 0 errors.
- [ ] `Plans~/MIGRATION_GUIDE.md` §7.2 section.
- [ ] `.claude/skills/rebuild-ide-extensions/SKILL.md` — reflect grammar/build additions if any
      step changed; `.claude/skills/changelog/SKILL.md` untouched unless conventions moved.
- [ ] `CLAUDE.md` — update the "UITKX language pipeline" + conventions paragraphs (wrapper keywords
      gone, `__Exports`, file-keyed namespaces).
- [ ] Family corpus lockstep — hash file + constant + mirrored corpus identical across the three
      repos (or an explicit recorded owner-approved deferral).
- [ ] Committed DLLs fresh (`scripts/build-generator.ps1` on final source state).

---

## 10. Hard guardrails (violating any = stop and ask)

1. **NEVER touch `Samples/Components/UitkxTestFileDoNotTouch/`** — byte-identical fixture; revert
   after every bulk tool run.
2. **NEVER weaken/delete an existing test assertion to get green.** Contract/parity tests are the
   product. Updating a pinned mirror to the NEW intended shape (in the same commit as the change it
   mirrors) is correct; loosening it is not.
3. **Language-lib public surface is additive-only** (U-12) — HMR reflection depends on it.
4. **Legacy-mode output must stay byte-identical** (deprecation window) — any diff in legacy-file
   emission is a regression, not a cleanup opportunity.
5. **Never edit generated marketplace pages by hand** (`vscode/README.md`, `UitkxVsix/overview.md`)
   — templates + `changelog.mjs` only.
6. **Companion sets migrate atomically** — never leave a folder with one migrated + one legacy
   member of the same set (matrix row 8 is a broken build).
7. **Do not change `family-corpus.hash` unilaterally** — corpus is family-synced; hash, constant,
   and mirrored corpus move together or not at all.
8. **Do not commit** the `Errors` scratch file, unrelated `*.meta` churn, or DLLs when no
   generator/language source changed. Commit DLLs WHEN it did (M3+).
9. Write node/scripts to files before running (inline `node -e` mangles backslashes in bash);
   PowerShell 5.1 has no `&&`.
10. Preserve `#if UNITY_EDITOR` gates and the Unity 6.3 `#if UNITY_6000_3_OR_NEWER` alias block.
11. No push, no tags, no publishes — owner-triggered only.

---

## 11. Error-signature table (what a red actually means)

| Signature | Meaning → action |
|---|---|
| `UITKX0109 "declares no parameters"` storm in Unity | Committed DLLs older than source — `scripts/build-generator.ps1`, restart Unity/compile |
| `MSB3027` copying to `Analyzers/` | Unity holds the DLL — close Unity or let the post-build target retry; never copy by hand while loaded |
| `CS0262 partial declarations have conflicting accessibility` | A companion merge got half-migrated OR modifier emitted on a merging module — check U-07 legacy-only guard + matrix row 8 |
| `CS0103 'container' does not exist` in a migrated file | Former companion/module member not imported — codemod step 4 missed a reference; add `import { name } from "./<file>"` |
| `CS0246/CS0234` in a consumer `.cs` | Namespace moved (file-keyed) — update the `using` (M7 step 4) |
| `CS0576 alias conflicts` / `CS1537 duplicate alias` | Alias injection guard missed — check `ReservedTypeAliases` + same-ns guards in `ImportScopeFacts` |
| `CS0434 namespace conflicts with type` | File-stem namespace collides with an exported type FQN (folder `X/…` beside file exporting `X`-pathed type) — see risk R4; add explicit `@namespace` to one side, record the case |
| `PinnedHash_EqualsFrozenFamilyValue` red | Hash file vs constant drift — both must be re-pinned together (§8) |
| `FormatterSnapshotTests` idempotency red | Formatter output not a fixed point — a new emit form isn't canonical on re-parse; fix the formatter, never the snapshot-only |
| `Hmr*ContractTests` red | SG and HMR emitters drifted — update the OTHER side, not the test |
| `SamplesCorpusGateTests` red | Real generator errors over the corpus — read the reported UITKX codes; that's the zero-diagnostics migration gate failing |
| HMR runtime `MissingMethodException`/null MethodInfo | Reflection seam broke — U-12 violated or stale committed Language.dll |
| `corpus-hash.mjs --check` fail in CI | Corpus edited without `--write`, or non-family-synced edit — guardrail 7 |

---

## 12. Risks / watch-list (ambiguous ⇒ STOP AND ASK, never guess)

- **R1 — Family 2320-band drift.** RESOLVED 2026-07-17: §3.1 is harmonized to the family-canonical
  (Unreal, audited) allocation, cross-checked verbatim against the Godot leg's execution-plan §4
  table. Residual risk only if the family renumbers AGAIN — in that case STOP AND ASK; never
  renumber unilaterally, and keep the Unity-local 2107-2110 codes out of any renumbering.
- **R2 — Family corpus timing (G-13).** Unity may reach M7 before the family ES-modules corpus
  cases exist. The plan's rule: proceed on Unity-local tests, defer the hash re-pin, record it.
  Do not invent family cases unilaterally.
- **R3 — Hot-reload state reset.** File-keyed namespaces + `__Exports` change every family key
  once. Accepted (G-01/G-09) but MUST be in CHANGELOG + migration guide; verify no steady-state
  regression in M8.
- **R4 — Namespace/type FQN collisions** (`CS0434`): a file's exported type can collide with a
  sibling folder-derived namespace (e.g. folder `Foo/Bar/` beside `Foo.uitkx` exporting `Bar`).
  Not present in the current samples (verified names in the modernization plan's Appendix A);
  watch for it in M7 gate output; escape hatch = explicit `@namespace`.
- **R5 — Rename-bridge fidelity.** Bridges re-state signatures textually; generics/`params`/
  defaults/ref-kinds must round-trip. Constrain v1: if a bridge's parameter list contains
  `ref`/`out`/`in` or a `params` array, forward them explicitly; if anything unparseable, emit
  2105-style error rather than a wrong bridge.
- **R6 — `* as` C#-body gap** (U-03): namespace-imported components are markup-only in 0.9.0.
  Documented limitation; if the owner wants C#-body support, that is new scope — ask.
- **R7 — VS2022 classifier drift** — it is a hand-copy (no parity test); M6 step 4 is easy to
  forget. It is in the §9 checklist for that reason.
- **R8 — `FormatterSnapshotTests` volume** (435 attrs, 418 KB file): M6/M7 will churn it heavily;
  keep additions surgical, run the class filtered while iterating.
- **R9 — 2311/2312 lifecycle**: they stay ALIVE this minor (legacy path). Retiring them now would
  break the deprecation window.
- **R10 — Unity csproj names** for VERIFY-UNITY are machine-generated; the six names in §5 were
  verified to exist on 2026-07-17 (`Ruitk.{Shared,Runtime,Editor,Examples,Samples,Diagnostics}.csproj`);
  still re-verify with `ls *.csproj` before first use — Unity regenerates them.
- **R11 — G-06 privacy is soft WITHIN one assembly.** U-02 emits non-exported members as
  `internal` on `__Exports`, and U-03's named-import payload is `using static {targetNs}.__Exports`
  — in the SAME Unity assembly (one asmdef, e.g. all of `Samples/`), `internal` members of an
  imported file become bare-name-visible to the importer's generated code. Privacy is fully
  compile-time-enforced only ACROSS assemblies. `private` members won't work (the component partial
  class needs cross-type access via its own `using static`), and C# 11 `file` classes are not
  available in Unity's language version. Options at M3 time: accept + document ("file-private means
  not-importable, and invisible across asmdefs"), or alias-only lowering for named imports (no
  `using static`, bridge every imported member — heavier emit). Default = accept + document; if the
  owner wants hard privacy, STOP AND ASK before M3.

---

*End of plan. Companion documents: `Plans~/ES_MODULES_GENERAL_PLAN.md` (the contract),
`Plans~/SAMPLES_NAMESPACE_MODERNIZATION_PLAN.md` (house-style precedent + consumer inventory),
`Plans~/MIGRATION_GUIDE.md` (user-facing, extended in M9).*
