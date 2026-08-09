# Legacy syntax removal — UNITY EXECUTION PLAN (0.16.0)

Status: **EXECUTING** — all §2 rulings made by the owner 2026-08-10; campaign started
2026-08-10 on `feat/legacy-syntax-removal` (off `dev` @ `168dfddf`, post-v0.15.0 publish).
Target: package **0.16.0**. Written 2026-08-10 from five parallel read-only research
sweeps (parser, SG+HMR, LSP, codemod, content surface) against `d219b7fd`; anchors
re-verified during execution.

Owner directives added at execution start (2026-08-10), same branch:
- Samples move fully to the new syntax (incl. the DoNotTouch fixture per D6) AND the
  owner's JustStayOn project gets migrated via the hardened codemod.
- Docs: drop "Unity-only"-style qualifiers (it is a Unity site); ADD the missing
  mounting/PanelRenderer documentation (0.15.0 shipped it, docs never covered it);
  full docs audit — correctness vs code + version-gate chips everywhere applicable.
- Full release staging: 0.16.0, all changelogs (package/json+extensions/discord),
  normal publish flow. Production-grade throughout.

**Goal.** Remove the pre-0.9 legacy `.uitkx` surface: the `component` / `hook` / `module`
wrapper keywords, companion partial-class merging, implicit same-namespace visibility,
folder-keyed namespace derivation for legacy files, the pre-0.8 directive-header forms
(`@component`, `@props`, `@key`, `@inject`), and the whole `UsesLegacySyntax`
classification machinery. After this wave, file path = component identity everywhere,
UITKX0113's legacy trigger disappears, and the legacy-vs-new drift bug class (two field
instances in the 0.15 cycle) becomes structurally impossible.

**Policy basis.** 0.9.0 (2026-07-18) shipped the deprecation window: UITKX2320 on every
wrapper, UITKX2107 on every companion merge, codemod documented. CHANGELOG 0.9.0
promised "removal comes in a later minor" (no specific version). Six minors have
elapsed. VERSIONING.md's deprecate-then-remove bar is met.

---

## 1. Scope ruling (what removes, what stays)

### REMOVES
| Surface | Where it lives (headline anchors) |
|---|---|
| `component X {}` / `hook useX {}` / `module X {}` (+ `export` forms) | Parser: `DirectiveParser.cs` dispatch 277-336, first-component parse 348-631, tail loop 645-755, `TryParseHookModuleFile` 1471-1588, `ParseSingleComponent` 785-853, `ParseSingleHook` 1594-1728, `ParseSingleModule` 1729-1816 |
| Companion partial-class merging | SG `Emitter/ModuleEmitter.cs` (whole file, 218 L); HMR `HmrHookEmitter.EmitModules` 204-317 + `UitkxHmrCompiler.EmitCompanionUitkxSources` 1359-1600 + controller save-redirect `UitkxHmrController.cs` 558-601 |
| Implicit same-namespace visibility | Emergent from folder-keyed ns: single seam `UitkxPipeline.ResolveEffectiveNamespace` 705-717 (`fileKeyed: !UsesLegacySyntax` → `true`); plus `ImportScopeFacts.cs` 95-234 legacy payload block |
| `UsesLegacySyntax` + legacy DirectiveSet members | `ParseResult.cs`: `UsesLegacySyntax` :505, `HookDeclaration` :32-58, `ModuleDeclaration` :66-86, `HookDeclarations` :424, `ModuleDeclarations` :428 |
| Pre-0.8 directive-header forms `@component`/`@props`/`@key`/`@inject` | Schema `uitkx-schema.json` directives 323-338; grammar `directive-declaration` alternation 209-225; UITKX2104 sites |
| Legacy `{Stem}Hooks` hook container | `HookEmitter.DeriveContainerClassName`, `ImportScopeFacts.cs:568-587`, `RoslynHost.DerivePeerHookContainerClass` 1648-1662 — all collapse to `__Exports` |
| Diagnostics that die | UITKX2320 (becomes the permanent error, see D3), 2107, 2109, 2111-adjacent 2311/2312, 2200-2205 (hook/module grammar), 0211 (const-in-module), 0100/0012 (directive order), 2104/2108 (mixed styles — see D3) |

### STAYS (verified, do not touch)
- **`@using`** — NOT legacy. Documented promise x2: docs Imports page L113 "`@using` keeps
  working indefinitely and is never flagged"; `Plans~/MIGRATION_GUIDE.md:119` "keeps working
  forever". UITKX2316/2317 deliberately cover both spellings; the formatter is forbidden
  from auto-converting (`AstFormatter.cs:130-136`). It remains a live alternate spelling of
  `import "@Ns"`. (The original scoping brief wrongly listed it; the research corrected this.)
- `import "@Ns"` namespace imports, all ES import forms, typed `export` declarations,
  file-keyed namespaces, `__Exports`, `@uss`, `@backend`.
- `Usings` / `UsingDirectives` on DirectiveSet (fed by both spellings).
- UITKX2316/2317, 2110 (drop its `!UsesLegacySyntax` guard), 2105 (has new-mode paths — do
  NOT blanket-delete; only its legacy arms).
- `ModuleBodyRewriter`, `StaticReadonlyStripper` (shared with `ExportsEmitter`), the module
  static/method swappers (`__Exports` reuses them), `ParseComponentBodyAt` (shared body
  machinery), UITKX0150 (shared with ExportsEmitter — consider renaming off "Module").

### FREE CLEANUP riding along (dead in BOTH modes today)
- `DirectiveSet.IsFunctionStyle` — always `true` at all 8 construction sites; 4 constant
  branches. `DefaultKey` — 15 occurrences, all literal `null`. `Injects` — 11, all `Empty`.
  Positional record params → ctor churn across SG/LSP/HMR; do as its own commit.
- Every `!UitkxFeatureFlags.StrictImports` branch (flag is const `true`):
  `UitkxPipeline.cs:752-762`, `ModuleEmitter.cs:183`, `CSharpEmitter.cs:297`,
  `EffectiveNamespace.cs:42-43`. Zero-behavior-change prerequisite commit.
- Two PRE-EXISTING LSP bugs to FIX (not delete): `ReferencesHandler.cs:456-458` and
  :526-528 — the find-references declaration regexes match ONLY legacy heads, so
  find-references already misses modern `export VirtualNode` / typed-hook declarations
  (RenameHandler has both alternatives; ReferencesHandler never got them).
- 16 dead tests encode the nonexistent `Samples/UITKX/` layout (15 in
  `FormatterSnapshotTests.cs:9393-10990` + `lsp-server/Tests/ParseFileTest.cs:24`) — all
  silently no-op behind `File.Exists` guards. Fix or delete FIRST so green means green.
  Same stale layout in root `README.md:24`.

---

## 2. Owner rulings — ALL RULED 2026-08-10

| # | Decision | RULING |
|---|---|---|
| D1 | `@namespace` | **KEEP, escape-hatch only** (hand-written partial `.cs` interop). Out of completions; docs say "not for normal components"; samples never use it. UITKX0113 stays (its namespace-scoped check keeps its legitimate trigger) |
| D2 | Un-migratable shapes | **Generic hooks: solve in-grammar — F9 is IN SCOPE** (`export (T x, Action<T> set) useSel<T>(...)`), shipped before/with removal so the codemod migrates them. **Type definitions (`enum`/`class`/`struct`): stay in `.cs`** — the dialect exports values of any type, functions, hooks; it does not define types. `export enum` parked on the family agenda. Properties/dictionaries/arrays/any typed value: already expressible, no gap |
| D3 | Error UX | **Reuse UITKX2320**, Warning → Error: "legacy wrapper syntax was removed in 0.16.0 — run `UitkxMigrateImports --es-modules`". No family renumber. 2104/2107/2109/2108/2200-2205/0211/0100 retire as reserved slots |
| D4 | Family corpus | **Modernize the 16 cases in place** (they test import semantics; `component Foo {` is scaffolding), re-pin `family-corpus.hash` + the `FrozenFamilyHash` const in lockstep, stage identical case-edit patches for Godot/Unreal to adopt on their own removal waves; divergence recorded with trigger |
| D5 | Codemod parse path | **Quarantined internal legacy-parse entry point** (`ParseLegacyForMigration`-style), reachable ONLY by the codemod + the D3 error reporter. Builds/IDE never parse legacy |
| D6 | DoNotTouch fixture | **Migrate to modern syntax**; fix the 16 dead stale-path tests so the kitchen-sink fixture is asserted again; keep the store-omit |
| — | `@using` | **Keep parsing forever (promise honored), but hidden**: out of docs and completions; `--tidy` converts on request |
| — | References regexes | **Fix** (teach modern declaration heads), don't delete |

Original decision write-ups follow for context (superseded by the table above).

**D1 — `@namespace`: keep or remove?** It is mode-independent (works on new-syntax files,
`EffectiveNamespace.cs:44-45`) and the docs present it as a CURRENT interop escape hatch
in five places — most importantly: it is the only way to share a namespace with a
hand-written companion `partial .cs`, the first rung of the Config page's precedence
chain, and the remediation named in UITKX2310's message ("add @namespace").
- **Recommendation: KEEP as a live feature.** It is ~2 small parser methods
  (`TryReadFunctionStyleNamespaceDirective` + preamble read), it is not the source of the
  drift-bug class (wrappers + merging were), and removing it strands the partial-`.cs`
  interop scenario with no replacement (config `namespacePrefix` sets only the prefix).
- Consequence if KEPT: **UITKX0113 survives** — two new-mode files stamping the same
  `@namespace` + exporting the same name still collide; the 0.15.1-fixed namespace-scoped
  check is exactly right and keeps its (now rare, always legitimate) trigger. Grammar
  keeps `namespace` in the directive alternation; docs keep the escape-hatch sections.
- Consequence if REMOVED: 0113 + 2310-message + Config chain + CompanionFiles/Imports/
  Reference/README sections all change; hand-written partial `.cs` interop needs a new
  mechanism first.

**D2 — the un-migratable set.** Generic hooks and modules with nested types/properties
have NO plain-dialect form (`MIGRATION_GUIDE.md:419-421`; parser 2105 message even says
"keep the generic in a legacy 'hook' file"). Removal makes such files uncompilable.
Options: (a) ship with "extract to ambient `.cs`" as the documented path (this is how the
owner cleared PC-3 by hand: 21 members extracted, 18 files retired), optionally with a
codemod `--extract-blocking-members-to-cs` assist; (b) first land generic plain
declarations (family grammar decision F9) — a family-wide grammar addition, weeks not
days. **Recommendation: (a) for 0.16.0; F9 stays a family agenda item.**

**D3 — error-path design.** What a legacy file gets in 0.16:
- **Recommendation:** keep code **UITKX2320** but flip Warning → Error with new text:
  "legacy wrapper syntax was removed in 0.16.0 — run `UitkxMigrateImports --es-modules`".
  No family renumber (2328/2329 are reserved "do not allocate" slots; a new code is a
  STOP-AND-ASK family event per `DiagnosticCodes.cs:282`). The natural emission home is
  the existing wrapper-detection at `DirectiveParser.cs:2196-2244` (keep detection, drop
  the recovery parse). 2320's docs row flips from "deprecation" to "removed" wording.
  2104/2107/2109/2108/2200-2205/0211/0100 retire as reserved slots (docs rows moved to
  the Reserved section). Severity-bump-is-breaking is priced in: this IS the breaking
  release.

**D4 — family coordination for the corpus hash.** 16 of 17 `fileScan` corpus cases use
`component Foo {`. Any rewrite changes `Plans~/family-corpus.hash` (`917dd8cd…`), which is
frozen family-wide (CI gate + hard-coded `FrozenFamilyHash` in
`ImportCorpusManifestTests.cs:23` + byte-match with Unreal/Godot). 0.9.0 already deferred
this re-pin (G-13/R2). Options: (a) coordinate a family corpus-case modernization — all
three legs adopt the same rewritten cases + one new hash (clean, but requires
Unreal/Godot work in the same window); (b) move the 16 legacy cases from the `familyCore`
tier to the (currently empty) `perLeg` tier — still re-pins, but decouples future edits.
**This is a cross-repo STOP-AND-ASK either way; the Unity leg cannot re-pin unilaterally
without desynchronizing the family.** Note: Godot/Unreal presumably want the same removal
wave eventually — bundling the corpus modernization into a family removal campaign is the
clean version of (a).

**D5 — codemod parse-path survival (hard sequencing constraint).** The codemod
ProjectReferences the LIVE language-lib and classifies legacy files by PARSING them. If
0.16's parser deletes legacy parsing, the shipped codemod can no longer read the files it
must migrate. Options: (a) keep the legacy grammar as an internal parse-for-migration
entry point (`DirectiveParser.ParseLegacyForMigration`), used ONLY by the codemod — the
normal pipeline path emits the D3 error unconditionally; (b) pin the codemod to the 0.15
`Ruitk.Language` (PackageReference to a published/vendored copy). **Recommendation: (a)**
— one assembly, no version skew, and the internal entry point is also what the D3 error
path uses to find the declaration span to squiggle. It quarantines (not deletes) roughly
the four `ParseSingle*` methods; everything downstream of the parser still deletes.

**D6 — `UitkxTestFileDoNotTouch`: migrate (recommended), delete, or freeze.** Its only
LIVE consumers are the blanket formatter-idempotency theory and the samples corpus gate;
the 16 tests named for it are dead (stale path). It is byte-frozen by two CHANGELOG
promises, but those were scoped to the deprecation window this wave closes.
**Recommendation: migrate it to modern syntax** (its non-syntax coverage — 4-level
control flow, portals, Suspense, Router, commented-out JSX, CRLF beds — is genuinely
valuable), fix/delete the 16 dead tests, keep the `config.json` store-omit, and pin the
legacy ERROR path with small inline parser-test fixtures instead of sample files.

---

## 3. Prerequisite wave — codemod hardening (ships in or before 0.16.0)

The tool (`SourceGenerator~/Tools/UitkxMigrateImports`, `--es-modules`) already covers:
wrapper→typed-export for all three keywords, module-body explosion (Roslyn-parsed),
companion-set atomicity, importer rewrites incl. `import * as`, trivia preservation,
idempotence — 26 pinned facts. Gaps to close, in priority order:

1. **Namespace-move ledger + `.cs` fixer.** Migrated stamp-less files silently change
   namespace (folder-keyed → file-keyed); nothing updates hand-written `.cs` consumers or
   even reports old→new. Emit a per-file ledger; add a `.cs` rewriting pass reusing
   `RuitkMigrateBrand`'s `ScanRules`/`FileEncodings`/per-file-try-catch machinery.
2. **Non-public companion members** (the common `static readonly Style` shape) currently
   migrate with NO import and NO warning → CS0103. Auto-`export` + import them, or fail
   the set loudly. Highest-frequency real-world break.
3. **Namespace-import re-pointing** — `import "@Old.Folder"` aimed at pre-move namespaces
   is neither re-pointed nor deleted (spec §7.1 step 4, never implemented — PC-12b).
4. **Cross-asmdef implicit references** — export table is asmdef-local; references across
   asmdefs get no import, no warning. At minimum: report them.
5. **CLI hardening** — reject unknown flags (today `--es-module` [typo] silently runs the
   WRONG pass and stamps `@namespace` into every file), add `--help`, per-file try/catch,
   BOM/EOL round-trip (today CRLF repos get whole-tree EOL churn), `--report <file>`.
   Parity model: `RuitkMigrateBrand/Program.cs`.
6. **Post-migration verification gate** — re-run the SG pipeline over the tree, non-zero
   on new errors ("zero-diagnostics gate", spec §7.1 step 7, never implemented).
7. **Delivery UX** — `scripts/migrate-uitkx.ps1|mjs` wrapper + Unity MenuItem
   (Tools → Reactive UI Toolkit → Migrate…); today's UX is `dotnet run --project` inside
   `Library/PackageCache`. Fix the stale csproj header ("Not shipped to Unity" is wrong).
8. Optional escape hatch: `--stamp-legacy-namespace` for projects with hand-written
   partial `.cs` (with a same-folder `__Exports__UitkxHookRefresh` CS0101 pre-flight —
   it is emitted non-partial, `ExportsEmitter.cs:371`).
9. D2 assist (if ruled): `--extract-blocking-members-to-cs`.

Also: a repo/CI gate "no `.uitkx` parses legacy" (after D6 lands, the repo count is zero).

## 4. Removal work packages (per layer, with reported anchors)

**WP-A Parser / language-lib** (~110 sites, 9 whole methods — the §1 table). Sequencing:
StrictImports dead-branch commit → free-cleanup commit (IsFunctionStyle/DefaultKey/
Injects) → D5 quarantine of `ParseSingle*` → delete dispatch/tail/hook-module-file paths →
D3 error path at 2196-2244 → `EffectiveNamespace` collapse (`fileKeyed: true`; keep the
3-arg overload DELETION coordinated with HMR — it is REFLECTION-BOUND, see WP-C) →
`ImportScopeFacts` legacy block 201-234 + `DeriveHookContainerClassName` 2-arg →
`StrictImportDetector` 2109 gate 283-300 + 2110 unwrap → `VirtualDocumentGenerator`
GenerateHook/ModuleDocument + UsesLegacySyntax branches → `SemanticTokensProvider`
323-367 + 256-261 → `AstFormatter` dispatch 213-234, `FormatFunctionStyleComponent`,
`FormatHookModuleFile`, hook-header emitters (expect golden re-pins; legacy byte-identity
guardrail comment at :72 retires) → `DiagnosticsAnalyzer.CheckConstInModuleBodies`
(open sub-question: does the new-mode value/util body want an equivalent const-HMR
check?) → `ParseResult` member deletions LAST (ctor churn).

**WP-B Source generator.** Delete `ModuleEmitter.cs` + `PeerModuleInfo.cs`; strip
`HookEmitter`'s legacy container path (keep `EmitSingleHook`/`QualifyKeys` — ExportsEmitter
uses them); `UitkxPipeline` legacy short-circuit 168-252, mixed-decl module emit 464-475,
module-alias injection 912-940, `peerModules` threading (5 signatures); `CSharpEmitter`
file-top usings 214-224 → inside-namespace unconditional 259-265; `UitkxGenerator` legacy
regexes 381-413, ComponentName fallback 456-472, `{Stem}Hooks` branch 513-531, the
"legacy files never get PeerExportsInfo" disjunct :547 (→ every file becomes importable
via star/default/rename — 2109's reason to exist disappears), `CollectPeerModuleInfos`
771-783. Rebuild + re-commit `Analyzers/` DLLs (build-generator.ps1) at the end.

**WP-C HMR** (mirror WP-B; parity contract tests re-pin). `HmrCSharpEmitter` 309-349;
`HmrHookEmitter.EmitModules` (114 L) + legacy container path; `UitkxHmrCompiler`: legacy
route 1281-1310, companion engine 1359-1600, cache keys 1704-1714, `InjectUsings` param
3386-3418, `ComputeEffectiveNs`/`BuildHookFamilyKeyMap` legacy arms 3482-3608, and TWO
traps: (1) the reflection seam binds `EffectiveNamespace.Resolve` BY PARAMETER TYPES with
a loud MissingMethodException guard (1869-1899) — decide the 3-arg overload's fate on both
sides in ONE commit; (2) the fragment-parse fallback PREPENDS `@namespace __Tmp\n
@component __Tmp\n` (1970-1975) — breaks the moment those stop parsing; rewrite to the
modern prelude or make `UitkxParser.ParseFragment` mandatory. `UitkxHmrController`
companion→parent save redirect 558-601 (+ its regex; removes a per-save disk read; its doc
warns about pre-import CS0103 — confirm `FanOutToImporters` suffices, open question).
Also delete the reflective absent-property⇒legacy defaults (5 sites) — decide whether
`UsesLegacySyntax` stays on `ParseResult` as an always-false shim for older committed
`Ruitk.Language.dll` compat or is deleted outright (recommend delete; HMR and lib ship
together).

**WP-D LSP + grammar + schema** (~500 L). Per the 0.15.1 state: delete
`ComputeDuplicateComponentDiagnostics` ONLY IF D1=remove (else keep — it is now correctly
namespace-scoped); delete `ComputeCompanionMergeDiagnostics` + `PeerDeclaresComponent`'s
legacy fallback; `ResolveVisibleProps` namespace branch simplifies (own-ns seed can only
match self under pure file-keying; keep the `import "@Ns"` half); completion: delete
component/hook/module snippets 392-409 + `@namespace`/`@using` items per D1 (`@using`
item STAYS), module import-brace loop 1673-1674 (verify `HookDeclarations` new-mode
population before touching 1671-1672); Definition/References/Rename legacy regex
alternatives (AND fix the two §1 pre-existing References bugs); `WorkspaceIndex` legacy
regex alternatives + dead `s_uitkxComponentPattern` + module-export loop 721-723;
`ImportCodeActionHandler` convert-`@using` refactor (keep the bare C# `using X;` half?
minor call); `RoslynHost` 12 sites (container names → `__Exports`, peer-doc gates, 2316
message tail). Grammar: delete 3 wrapper rules + includes, split the directive
alternation (keep `namespace` per D1, keep `uss`/`backend`; drop `component|props|key|
inject`, keep `using`); the vscode `syntaxes/` copy is GENERATED by prebuild but
git-tracked — commit both. Schema: drop `component`/`props`/`key` directive entries (+
`using`? NO — stays; `namespace` per D1); `SchemaRegistryParityTests` gates elements
only, but verify. Hover directive-example arms follow the schema.

## 5. Test surface

- DELETE/rewrite dedicated legacy suites: `HmrModuleNamespaceParityContractTests` (whole
  file), `ComponentScopingDiagnosticTests` `@namespace` fact (per D1), 2107 facts in
  `EsModulesAuditFixTests`, the 8 module-trampoline facts in
  `HmrEmitterParityContractTests` (port to `export`-member fixtures — the trampoline
  machinery survives via ExportsEmitter), `ModuleStaticReadonlyStripTests` (port),
  `FileKeyedNamespaceTests` legacy facts, ~20 named legacy-assertion tests (list in the
  research output: 2320-tolerance asserts, `UsesLegacySyntax=true` fixtures, 2109 facts,
  legacy-formatter facts).
- CODEMOD TESTS STAY (the tool keeps working via D5) — they become the D5 entry point's
  primary coverage.
- MASS FIXTURE MODERNIZATION: ~700 wrapper occurrences in ~30 incidental files
  (FormatterSnapshotTests alone 434 wrapper + 92 `@ns/@using` occurrences across 644
  cases; then EmitterTests 96, DiagnosticsAnalyzerTests 39, ParserTests 36, LSP
  RoslynHostTests 20, …). Mechanical but the single largest line-count item; consider
  running the CODEMOD over extracted fixture strings as its own dogfood pass.
- Corpus: 16/17 `fileScan` cases rewrite + family hash re-pin per D4 (edit
  `Plans~/family-corpus.hash` + the `FrozenFamilyHash` const in lockstep; cross-repo
  mirror required).
- NEW tests: D3 error path (per wrapper keyword, per position), D5 migration-parse entry
  point, the References-regex fixes, no-legacy-left repo gate, DoNotTouch modern rewrite
  idempotency (it stays in the blanket theory automatically).

## 6. Content surface

- `Plans~/MIGRATION_GUIDE.md` — the biggest single rewrite: §wrapper-deprecation
  375-405 becomes §removed (keep the BEFORE/AFTER table — it is now the error's
  remediation doc), L425-426 ("removal comes in a later minor") replaced by the 0.16
  statement, un-migratable-set section per D2, `@using`-forever section UNCHANGED,
  `@namespace` sections per D1.
- Docs site (per-page list from research): Reference (major — delete
  `DIRECTIVE_HEADER_EXAMPLE`, 4-5 directive rows, wrapper-window sentence), Diagnostics
  (2320 row → removed-error; 2107/2109/0100/2108 rows → Reserved), Imports (keep the
  `@using` promise!), CompanionFiles + example (deprecated → removed; `@namespace` per
  D1; `EXAMPLE_TYPES` currently offers legacy modules as the nested-types escape — needs
  the D2 answer), Config (precedence chain per D1), FAQ (3 answers), GettingStarted,
  HMR page one-liner, Styling one-liner. EVERY rewritten page's `searchContent`/
  `keywords` blob in `docs.tsx` must be re-synced by hand (L89/104/119/212/260/395/437).
  Consider a dedicated Migration page (content currently split across 4 pages + README).
- Root `README.md`: stale Samples-layout row L24 (prerequisite fix), `@namespace` L76 per
  D1, "Migrating a pre-0.9.0 project?" block L81-90 gets the 0.16 framing.
- `ide-extensions~/README.md:26` feature-matrix cell; marketplace pages regenerate via
  the changelog skill (never hand-edit outputs); `changelog.json` gets the removal entry;
  `plans/DISCORD_CHANGELOG.md` gets the release post (prepend, ASCII, <=2000 chars).
- CHANGELOG 0.16.0: Breaking section with the exact removed-surface list, the codemod-
  first upgrade order, and the D2 path for un-migratable shapes.

## 7. Release mechanics + upgrade choreography

- Version: package 0.16.0 (breaking-by-policy pre-1.0 minor, same as 0.12.0 precedent).
  Extensions: grammar+schema+LSP all change → vscode/vs2022/rider bumps + changelog
  entries. Rebuild + commit `Analyzers/` DLLs. Docs build. All gates (SG suite, LSP
  suite, corpus hash per D4, machine-paths, changelog verify, discord verify).
- **Consumer upgrade order is load-bearing and must be the headline of every changelog
  surface:** run `UitkxMigrateImports --es-modules` (0.16's hardened one — works on
  legacy files via D5) → fix reported un-migratables per D2 → THEN the project compiles
  clean on 0.16. Upgrading first is safe but loud: every legacy file = one 2320 ERROR
  naming the codemod.
- Owner's own trees: JustStayOn + any pre-0.9 projects get the codemod pass;
  UnityComponents' embedded clone follows the branch automatically.
- Family: Godot/Unreal legs schedule their own removal waves; the corpus re-pin (D4) is
  the only hard cross-repo coupling for the Unity leg's release.

## 8. Suggested execution order

0. Prereq commits: dead-flag branches; free cleanup; fix/delete the 16 dead
   stale-path tests + README row. (Zero behavior change, shrinks every later diff.)
1. Codemod hardening wave (§3) + its tests. Dogfood: run it over the repo's own
   remaining legacy (DoNotTouch per D6) and over JustStayOn.
2. D5 parse-path quarantine + D3 error path (parser flips; everything still compiles
   because downstream legacy branches are now unreachable-but-present).
3. WP-B SG + WP-C HMR deletions (one PR — parity tests re-pin together).
4. WP-A parser deep deletions + WP-D LSP/grammar/schema (+ References-regex fixes).
5. Test-fixture modernization sweep + corpus/hash per D4.
6. Content surface (§6), version/changelogs, gates, release staging.

Estimated shape: ~2-4 focused days of execution after rulings, dominated by the test
fixture sweep and the docs rewrite; the code deletion itself is large but mechanical
(~1,000+ lines net-negative across the four layers).
