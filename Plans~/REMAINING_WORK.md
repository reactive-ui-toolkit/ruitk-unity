# Remaining Work — consolidated backlog

> Single source of truth for every outstanding item extracted from the archived plans
> (see `Plans~/archive/`). Assembled 2026-07-15 by verifying each plan against the code —
> item status is what the CODE shows, not what the plans claimed. When an item ships,
> delete its row here. Sources: `FINAL_AUDIT_UITKX_OPTIMIZATIONS` (perf audit),
> `V1_RELEASE_PLAN` (v1 tracker), `ASSET_STORE_PUBLISHING_PLAN`, `UITKX_PARITY_CLEANUP_PLAN`,
> `CUSTOM_RENDERING_PLAN`, `ROUTER_REACT_ROUTER_COMPARISON`, `VERSIONING_PROCESS` (still live),
> plus doc-staleness items found during triage.
>
> Living references that stay in `Plans~/` (not plans, no items unless listed below):
> `family-corpus.hash` (**load-bearing** — CI drift
> gate + `ImportCorpusManifestTests` resolve the repo root via it), `LATENCY_TARGETS.md`,
> `MIGRATION_GUIDE.md`, `UITKX_ARCHITECTURE_LANGUAGE_SERVER.md`, `VERSIONING_PROCESS.md`,
> `codebase-index.json` / `repository-atlas.md` (generated references; stale, refresh-worthy).
> The Discord changelog is NOT here: it lives at `plans/DISCORD_CHANGELOG.md` (lowercase,
> family-canonical path — `scripts/discord-changelog.mjs` and `publish.yml`'s discord job read it).
>
> 2026-08-04 sweep: every executed campaign plan moved to `Plans~/archive/` (ES-modules trio,
> rebrand + post-rename audit, family parity, extension listing, import unification, samples
> modernization [never executed — see SAMPLES-NS], uGUI proposal, Unity 6.5 plan). This file is
> the only live backlog.

---

## 0. Unity 6.5 support — carried over from Phase 1

Phase 1 (the three new controls) shipped in **0.14.0**. These are the items it did
**not** cover. Full detail: `Plans~/archive/UNITY_6_5_SUPPORT_PLAN.md`.

| Item | Why deferred | Trigger to revisit |
|---|---|---|
| **WA1/WA2 — PanelRenderer mount watchdog** (`Runtime/Core/PanelRendererRootSource.cs`, `TickMountWatchdog`; one mechanism, one flag `mount_watchdog`) | SHIPPED in 0.15.0 as a workaround, not a root-cause fix — the bugs are Unity's: **case IN-150082** (nested child never mounts, editor-only, filed 2026-08-01) and **UUM-147875** (disabled-in-`Awake()` never inserts its root; Unity's fix ships in 6000.5.7f1). Symptom-gated (no callback after enable), so it is inert on fixed editors | Remove when BOTH are fixed upstream AND `package.json`'s `unity` floor is past the fixes — procedure in plan §5.9.5 |
| **WA3 — nested-release prevention** (`PanelRendererRootSource.DisableNestedChildRenderers`, flag `nested_prevention`) | SHIPPED in 0.15.0 as a workaround for **UUM-148452** (open upstream): a parent rebuild's release cascade poisons nested child renderers. Prevention (the measured N2 pattern) acts before damage, so it cannot be symptom-gated; it runs only around rebuilds the library itself triggers | Remove when UUM-148452 is fixed AND the `unity` floor is past the fix |
| **WA4 — nested-renderer repair** (`PanelRendererRootSource.TickNestedRepair`, flag `nested_repair`) | SHIPPED in 0.15.0 as a workaround for **UUM-148452**: destroy + re-add only the nested child's renderer (measured N6), full settings copy, Undo-wrapped in edit mode. Symptom-gated on `resourcesReleased` persisting with no callback. Residual: serialized references TO the old component cannot survive (no replace-in-place API in Unity) — that case is what the opt-out covers | Remove when UUM-148452 is fixed AND the `unity` floor is past the fix |
| **Remount-path effect-cleanup scope** (`FiberReconciler.AbandonRoot`) | The 6.5 remount runs effect cleanups and disposes signal subscriptions but deliberately skips `OnHostRemoved` retention eviction for the abandoned tree's ROW POOLS: a poisoned virtualized view's pooled row renderers are dropped to GC without `Unmount()` (touching them would throw). Their own effect cleanups therefore do not run on this one path. Bounded: remount only, virtualized views only, and the sweep + weak tables reclaim the memory | A field report of leaked external resources (audio/timers) held by ROW components across a `.uxml`-save remount would justify a row-level abandon pass |
| **ATG measurement comparison** (§4.1) | Cannot be settled by reading — needs 6.4 and 6.5 side by side, rendering the text-heavy samples and diffing measured sizes and wrap points. The punctuation line-breaking divergence has **no UUM id at all** | Any report of layout shifting after a 6.5 upgrade, or before relying on precise text metrics. A clean repro would be new information for Unity |
| **`add-unity-version` skill / `VERSIONING_PROCESS` runbook defects** (§9) | Six gaps found while running this wave: `TypedPropsApplier` missing from every checklist, `Style.cs` described as one edit when it is six, `IStyleCoverageTests` needing a new array per IStyle-adding release, the four-emitter alias-parity layer absent, the release surface omitted, and `-FromDll`/`-ToDll` pinning not documented as mandatory | Before the next version-add wave — none of them bit this one because it had no IStyle changes |
| **Docs folder casing** — on disk `ReactiveUIToolKitDocs~` (capital K), tracked in git as `ReactiveUIToolkitDocs~` | Pre-existing; `core.ignorecase=true` hides it on Windows. New files were staged under the tracked casing so the tree did not split, but on a case-sensitive checkout the working dir and index disagree | Any CI docs-build oddity, or before someone adds files on Linux/macOS |

---

## 1. Correctness / Bugs

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| TXT-1 | `<Text>` silently drops every attribute except `text` — it compiles to the bare `V.Text(string, key)` primitive, so `style=`/`onClick=`/etc. vanish with no diagnostic (cost a demo-fix cycle: wrap styles on `<Text>` were no-ops). Add a UITKX warning for unsupported attributes on BuiltinText — needs the full parity sweep (SG diagnostic + HMR emitter + LSP/virtual doc) plus a docs note steering styled text to `<Label>` | `SourceGenerator~/Emitter/CSharpEmitter.cs:1316-1320` reads only `text` | demo sweep 2026-08-03 |
| GEN-4 | HMR secondary paths not yet verified for LOCAL (`file:`) packages: `NewCsFileDiscovery` roots at the project root (covers embedded, misses outside-root local installs) and the `Asset<T>` HMR import path joins `projectRoot + assetPath` (same limitation). The primary flow — watch roots, compile, swap, .uss cache refresh — is layout-aware as of the 2026-08-12 HMR wave; embedded packages fully covered and field-tested | `Editor/HMR/NewCsFileDiscovery.cs`, `Editor/HMR/UitkxHmrController.cs` (Asset import fallback) | HMR package-watch fix 2026-08-12 |
| U4 | Multi-root counting mismatch: `DiagnosticsAnalyzer` and `StructureValidator` count render roots with two separate implementations that can disagree — extract one shared root counter | two implementations remain (language-lib `DiagnosticsAnalyzer` vs SG `StructureValidator`) | PARITY U4 |
| P-2 | Format-on-save silently no-ops in some VS Code sessions — needs a live repro before any change (investigation item) | no repro recorded | V1 P-2 |
| LANG-2 | An APOSTROPHE inside a `//` comment that sits inside a JSX attribute expression (`style={new Style { // the body's line box …` ) makes the expression splicer swallow the rest of the file: the scanner treats `'` as a char-literal opener even in comment text, so the attribute never closes and the emitter produces garbage C# (`CS0116 a namespace cannot directly contain members` at the LAST markup lines, plus `CS0246` on the component's own props type). Zero parse diagnostics — `validate-uitkx.ps1` reports 0 and only the SG-backed csc smoke catches it. Fix: skip comment runs in the attribute-expression scanner (parity sweep: SG splicer, HMR emitter, language-lib, virtual doc). Workaround in tree: builder comments inside JSX expressions are written apostrophe-free | reproduced 2026-08-15 on `Builder/Editor/Canvas/CanvasView.uitkx` by adding `// 15px in the body's 1.45 line box` inside a `style={new Style { … }}` block; removing the apostrophe compiles | RUITK Builder pixel-parity round |
| LANG-1 | Bare JSX inside `@if`/`@for` directive blocks (no `return (...)`) parses clean (0 diagnostics) but the emitter produces MANGLED C#: raw markup left unlowered inside the IIFE, and a `@for` header containing `&&` + a `<` comparison gets a peer splice injected mid-header. Silent garbage, surfaced as misleading CS0246 at a `#line`-mapped location. Either support the bare form or diagnose it (new UITKX code) — full parity sweep (SG + HMR + vdoc + analyzer). Repro: builder CanvasView pre-rewrite, `git show 4cf5b535^..` vicinity; supported idiom (directive body wraps markup in `return (...)`) documented by every sample | found via the VE-09 SG-backed smoke compile (csc `-analyzer:` + `-additionalfile:` rsp), 2026-08-15 | RUITK Builder campaign |
| UB-05a | SUPERSEDED — the owner rejected this deferral on 2026-08-16; the work is ACTIVE again as `Plans~/UI_BUILDER_BUGS.md` UB-76 (single-line editors: LSP completion at the exact mapped position; multiline island editors: embed CodeField instead of bare TextFields). Row kept only so the ID resolves | `Plans~/UI_BUILDER_BUGS.md` §9 UB-76 | UI Builder campaign 2026-08-16 |
| UB-21a | `SemanticTokensProvider` threads `knownElements` through every Collect* overload but never dereferences it — its doc promises "names NOT in the set are skipped" and nothing implements that, and no distinct custom-tag token type exists. The Builder classifies custom tags client-side (CodeField schema-membership split); VS Code/VS still colour every tag as Element. Provider-level fix = new token type or the documented skip, with the 4-layer parity sweep | `ide-extensions~/language-lib/SemanticTokens/SemanticTokensProvider.cs` (`knownElements` pass-through only) | UI Builder campaign 2026-08-16, UB-21 |
| UB-REV | Adversarial-review tail deliberately deferred (all bounded/pre-existing): preview render exceptions from ASYNC scheduler ticks bypass the Mount try/catch (wrap `BuilderRenderScheduler`'s pump to close); `CollectStateNames` regex counts hook calls in comments/strings and pools one flat slot list across a MULTI-component file (both pre-existing imprecision — needs the parsed AST instead of a buffer regex); `RecompileWhenQuiet` status-write ordering when focus succeeds while a sibling fails; attribute menu opens at a stale pointer after the 1.5 s componentProps await; layer dropdown cannot re-trigger the CURRENT layer's preset (no value change event); drop-hint band line paints 2 world px (thin at L0, where rows are hidden anyway) | review workflow wf_9060148d-30b, findings 10/22/24/25/26/27/32; scratchpad review-findings.json | UI Builder campaign review 2026-08-16 |

## 2. HMR

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| H-5 | Per-swap non-collectible assembly leak: add optional `UITKX_HMR_AutoReloadAfterSwaps` threshold + surface `SessionMemoryDeltaMB` in the HMR window | no such pref anywhere in `Editor/HMR/` | AUDIT-OPT H-5 |
| O-03 | `GC.Collect(2, Optimized)` per HMR compile — measure a 10-save burst; gate it if it stutters | `Editor/HMR/UitkxHmrCompiler.cs` (~2498, ~2783) unconditional | AUDIT-OPT O-03 |
| O-04 / D-HMR-B8 | Shared-`.uss` edit fans out N separate Roslyn compiles (one per dependent) — batch into one union compile (defer until a real complaint; one-per-tick drain already landed) | no dependent batching in `UitkxHmrController` | AUDIT-OPT O-04, V1 D-HMR-B8 |
| D-HMR-B5 | Generic method overloads silently skip HMR swap (no overload-signature carrier) | no `[HmrOverloadSignature]`-style mechanism found | V1 D-HMR-B5 |
| D-HMR-B9 | `TryResolveMissingDependencies` lacks a visited-set/cycle guard (the visited-set in the import fan-out path is a different mechanism) | `Editor/HMR/UitkxHmrController.cs` (~1156–1181) | V1 D-HMR-B9 |
| D-HMR-B10 | `AssemblyReloadSuppressor` deferred refresh can fire after re-lock | not addressed | V1 D-HMR-B10 |
| D-OPT-1 | HMR dependency index over-links copy-rename near-clones (deferred pending telemetry) | deferred | V1 D-OPT-1 |
| HMR-MC | Multi-component files hot-swap only their FIRST component (HMR reads the singular `ComponentName`; `ComponentDeclarations` is never read in `Editor/HMR/`) — documented as a Known Issue; full support = per-declaration compile/swap | `Editor/HMR/UitkxHmrCompiler.cs` `Compile()` singular read | triage 2026-07-15 |
| HMR-ROSLYN-65 | In-process Roslyn is dead on Unity 6000.5: the NuGet-cache deps (`System.Reflection.Metadata` 5.0.0 etc.) are skipped when Unity's domain already carries same-named assemblies, and Roslyn 4.3.1 then binds Unity's copies → `MissingMethodException` (`MetadataReader.GetBlobContent`). Sessions now LATCH to the external csc after the first failure (fully functional since the 2026-08-13 define fix, just slower per save). Root fix = load the pinned deps into a dedicated context or ship a Roslyn version matched to the editor's BCL — measure the external path's per-save cost first; revisit if save latency complaints arrive or when bumping the pinned Roslyn | `Editor/HMR/UitkxHmrCompiler.cs` `TryLoadRoslyn` (alreadyLoaded skip), `CompileSources` latch | HMR 6.5 field session 2026-08-13 |
| HMR-FSW | If the member-file silence recurs WITH the 2026-07-18 trail in place (save produces neither an `[HMR] Save:` line nor, with Verbose watcher trace on, an `[HMR][trace] FSW` line), the drop is OS-level FSW non-delivery (Mono 8 KB buffer / AV hook) — next step is a bounded mtime-sweep fallback over the known `.uitkx` set in the watcher pump (the AssetPostprocessor net cannot help mid-session: `DisallowAutoRefresh` starves it). Trigger to revisit: one field report with the trail present | `Editor/HMR/UitkxHmrFileWatcher.cs` pump; fix wave `fix/hmr-field-wave` | field triage 2026-07-18 |

## 3. Performance

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| U-16 | LSP workspace scan walks `Library/`/`Temp/`/`obj/` with no exclusions (only `~` dirs skipped) — minutes-long initial scan on big projects | `ide-extensions~/lsp-server/WorkspaceIndex.cs` (~616–645) `EnumerateFiles(root, "*.cs", AllDirectories)` | AUDIT-OPT U-16 |
| U-17.2 | `CheckAssetPaths` does `File.Exists` per `Asset<>`/`@uss` occurrence per keystroke, no TTL cache; regex also matches inside comments | `ide-extensions~/language-lib/Diagnostics/DiagnosticsAnalyzer.cs` (~1334) | AUDIT-OPT U-17.2 |
| U-21 | Source generator is effectively non-incremental: reprocesses every `.uitkx` on any compilation change; per-file triple parse not merged; no `TrackIncrementalGeneratorSteps` cacheability test | `SourceGenerator~/UitkxGenerator.cs` single `RegisterSourceOutput` over combined providers | AUDIT-OPT U-21 |
| C-01 | Text vnodes allocate a `Dictionary<string,object>` per reconcile visit (largest steady-state alloc in the diff path) | `Shared/Core/Fiber/FiberChildReconciliation.cs` (~495) | AUDIT-OPT C-01 |
| O-01 | Scanner micro-costs: zero-alloc keyword compare (`TryReadKeywordAt` allocates a Substring per probe); thread line-starts/JSX ranges once per parse (partially done via `CSharpLexFacts.BuildLineStarts`) | `ReturnFinder.cs` (~255), `DirectiveParser.cs` (~2213) | AUDIT-OPT O-01 |
| O-05 | Formatter allocation profile — span-based line walking (opportunistic; its correctness gates all landed) | `AstFormatter` split/substring-heavy | AUDIT-OPT O-05 |

## 4. Diagnostics

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| U1 | Delete/retire never-emitted diagnostic constants + descriptors and add a reflection test enforcing "every registered code has an emission site" (0005/0012/0101/0102/0110 partially retired already — 0005/0103 SG descriptors deleted, 0101/0102/0103 marked RETIRED; finish the sweep + the guard test) | `DiagnosticCodes.cs` still lists codes with no emitter | PARITY U1 |
| U2 | Severity reconciliation per code across IDE/compile surfaces (0104, 0305, 0105/0109/0121) with a table-driven severity test (0106 already aligned by audit U-12) | no table-driven severity test | PARITY U2 |
| U3 | Diagnostics dropped at the Unity bridge: `Location.None` bridging semantics, `#error` strips the code prefix, discarded `jsxDiags`, unused 0025/0026 descriptors (partially improved by SurfaceLocationlessDiagnostics — audit the remainder) | `SourceGenerator~/UitkxPipeline.cs` | PARITY U3 |
| U5 | Silent tolerances to decide + enforce: duplicate attributes, expression-valued duplicate keys, `Key=`/`key` casing, camelCase `useEffect` variants, stray `>` | not addressed | PARITY U5 |

## 5. Testing

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| CR-T | Custom-rendering test matrix T1–T10 (SG emit shape, pool reset, diff/remove, `RedrawKey` `_hasEvents` gating, HMR parity marker) never landed as named tests | `grep OnGenerateVisualContent SourceGenerator~/Tests` → no matches | CUSTOM_RENDERING |
| RT-1 | Clean-clone → first-build validation (was flagged Blocker; never done — a CI job or a recorded manual run) | no CI job or record | V1 RT-1 |
| RT-2 | macOS sample validation (deferred — make the deferral an explicit recorded decision) | no macOS environment | V1 RT-2 |
| D-LAT | Automated latency-regression CI gate for `LATENCY_TARGETS.md` thresholds | no such CI job | V1 D-LAT |

## 6. Tooling / IDE

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| P-1a..e | TextMate layer-1 gaps: fragments `<>`, nested generics, verbatim/interpolated/raw strings, char literals, `?.` / `when` | `ide-extensions~/vscode/syntaxes/uitkx.tmLanguage.json` — no fragment/verbatim-string rules | V1 P-1 |
| LSP-P1 | Roslyn-formatter pass for `@code`/setup blocks (self-listed polish) | ARCHITECTURE doc §polish | LSP-ARCH |
| LSP-P2 | Semantic-token cross-IDE portability audit (self-listed polish) | ARCHITECTURE doc §polish | LSP-ARCH |
| LSP-P3 | TmLanguage minimal-fallback cleanup (overlaps P-1) | ARCHITECTURE doc §polish | LSP-ARCH |
| VP-7 | Add `sinceUnity` annotations to `uitkx-schema.json` for 6.3 additions | `VERSIONING_PROCESS.md` unchecked | VERSIONING VP-7 |
| VP-15 | Gap analyzer for the version-diff script (reads the compat matrix) | `VERSIONING_PROCESS.md` unchecked | VERSIONING VP-15 |
| Store-3 | OpenUPM registration (worth doing regardless of Asset Store) | no OpenUPM config | ASSET_STORE |
| MIG-UX | Unity MenuItem for the migration codemod (Tools → Reactive UI Toolkit → Migrate…): today's delivery UX is `node scripts/migrate-uitkx.mjs` from a repo clone, which UPM/Asset-Store users must discover; a MenuItem that shells the tool (probing dotnet per the standard chain) would make it one click. Also the plan's never-implemented "zero-diagnostics" post-migration gate (re-run the SG pipeline over the tree, non-zero on new errors) | `Plans~/LEGACY_SYNTAX_REMOVAL_PLAN.md` §5.7 | 0.16.0 removal wave |

## 7. Features / Design decisions

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| VP-27..33 | Unity 6.3 support wave: `#if` guards for 3 new `IStyle` props, `FilterFunction` CssHelpers, schema/styleKeyValues, docs manifest, matrix update, 6.2/6.3 test runs | `VERSIONING_PROCESS.md` Phase 4 unchecked | VERSIONING |
| RR-1 | Router: optional segments (`:lang?`) | re-homed from ROUTER comparison | ROUTER |
| RR-2 | Router: per-route `errorElement` | re-homed | ROUTER |
| RR-3 | Router: navigation-action tracking (POP/PUSH/REPLACE) | re-homed | ROUTER |
| RR-4 | Router: `UseNavigate` `relative:"path"` resolution | re-homed | ROUTER |
| TD11 | Design decision: hook ownership model | open decision | V1 D-DESIGN-TD11 |
| TD14 | Design decision: synthetic event dispatcher for portals | open decision | V1 D-DESIGN-TD14 |
| U7 | Decision: single-quote attribute strings | open decision | PARITY U7 |
| EXP-ENUM | `export enum` (and by extension exported type definitions) — parked on the FAMILY agenda per ruling D2 of the 0.16.0 removal wave: the plain dialect exports values/functions/hooks but does not define types; today's answer is a hand-written `.cs` beside the components. Revisit as a family-synchronized grammar addition if field demand appears | `Plans~/LEGACY_SYNTAX_REMOVAL_PLAN.md` §2 D2 | 0.16.0 removal wave |
| RT-6 | (Optional) runtime-only package variant | not done | V1 RT-6 |
| ~~NSIMP~~ | **DONE (v0.8.0)** — Namespace-import unification: `import "@Ns"`, UITKX2316 (editor error / build warning), UITKX2317 redundant-using Hint, quick-fixes, codemod `--tidy`, formatter round-trip. Deferred follow-ups: semantic unused-using 2317, per-segment namespace completion + hover, 2316 "did you mean" suggestion, bulk samples `--tidy` | `Plans~/archive/IMPORT_NAMESPACE_UNIFICATION_PLAN.md` | user request 2026-07-15 |
| SAMPLES-NS | **OPEN — plan stale, re-inventory before executing** — modernize Samples to zero `@namespace` via `namespacePrefix` config. As of 0.15.0 (2026-08-04) the samples still carry 104 `import "@…"` lines across 65 files. The archived plan's verbatim per-file appendices predate the ES-modules redesign (0.9.0) and the rebrand (0.12.0), so redo the inventory; its method (A: name→ns map, B: exact C# using edits, C: exact uitkx import DELETEs/ADDs, hard verification gates) still applies | `Plans~/archive/SAMPLES_NAMESPACE_MODERNIZATION_PLAN.md` | user request 2026-07-16 |

## 8. Cleanup / Tech-debt

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| C-02 | `ExtractProps` duplicated ×4 (drift risk): `FiberChildReconciliation` (~482), `FiberFactory` (~287), `FiberFunctionComponent` (~348), `FiberReconciler` (~1599) — extract one helper | four copies in `Shared/Core/Fiber/` | AUDIT-OPT C-02 |
| C-04 | `FiberReconciler.MetricsEmitted` static event can pin torn-down hosts in the editor — clear on unmount / domain reload | `Shared/Core/Fiber/FiberReconciler.cs` (~76) bare static event | AUDIT-OPT C-04 |
| C-06 | Add a reviewer note / analyzer rule that `Shared/Core/Fiber/**` stays LINQ-free | no guard exists | AUDIT-OPT C-06 |
| U6 | Docs truthfulness sweep: `{__children}`, bare-boolean attribute shorthand, prop-spread divergence, stale emitter comments | not addressed | PARITY U6 |
| VP-5 | `VERSIONING_PROCESS.md` still says "update TECH_DEBT.md" — retarget the pointer to this file | `VERSIONING_PROCESS.md` (~705) | VERSIONING VP-5 |
| ATLAS | `codebase-index.json` / `repository-atlas.md` are stale (May 3) — regenerate or mark generation date prominently | file dates | triage |

## 9. Docs

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| RT-3 | Per-editor feature-degradation matrix (what works in VS Code vs VS2022 vs Rider vs none) | not found | V1 RT-3 |
| RT-4 | Audit `THIRDPARTY.md` completeness | file exists, unaudited | V1 RT-4 |
| RT-5 | Publish compat/deprecation policy (beyond `VERSIONING.md`) | not published | V1 RT-5 |
| H-1..H-7 | Launch collateral: announcement, support channels, incident guide, onboarding/contribution guide, 2 how-to guides, metrics; residual H-5: diagnostics/docs issue templates (bug/feature/config exist) | not found | V1 H-* |
| A-1..A-7 | Product/process: scope lock, feature matrix, positioning, owners, severity policy | process work, no artifacts | V1 A-* |

## 10. Release / Process

| ID | Item | Evidence / anchor | Source |
|---|---|---|---|
| G-1..G-7 | Release engineering: RC branch, regression record, runbook dry-run, upgrade-path check, clean-env publish, release gates, post-release monitoring | process work, not done | V1 G-* |
| Store-1 | Asset Store: create package draft + first upload from a 6.2 editor + Submit (**paused by owner**; price switched to ~$5 → payout/tax setup required first) | no submission occurred | ASSET_STORE |
| RUNTIME-V | Unity-in-editor runtime verification of the 0.7.x import/export wave: the 5 HMR hook-family-key scenarios + F5 pass over migrated samples + JustStayOn | pending user | triage |
| CORPUS-DIV | Family-corpus hash DIVERGED by design (0.16.0 legacy removal, ruling D4): Unity re-froze `Plans~/family-corpus.hash` + `FrozenFamilyHash` at `f8c06ee6…` after modernizing the 16 wrapper-scaffolded `fileScan` cases (plain heads; the two module-subject cases became exported-value subjects with the new `values`/`value:Name` expect vocabulary). Unreal/Godot still pin `917dd8cd…` and their scanner tests still consume the legacy shapes | staged case edits: `Plans~/family-corpus-0.16-legacy-removal.patch` (reference diff — sibling prefixes differ, adopt-equivalent not raw-apply) | Trigger: each sibling's own legacy-removal wave adopts the case edits + re-pins; release-time TD-009 hash-match resumes once all three agree |

## 11. uGUI backend (adding-uGUI wave, 2026-07-25)

The gap-closure wave (same branch) CLOSED the original UG-1..UG-6: GameObject
pooling shipped (per-adapter, pristine-reset gated — stateless visuals pool,
stateful controls destroyed), deep LSP backend-awareness shipped (completion/
hover/diagnostics/props-type resolution keyed by @backend, virtual documents
included), UITKX2113 cross-backend import shipped (both directions pinned),
builtin default sprites ship via Ugui/Resources/UguiDefaultResources.asset
(menu-identical look in editor AND players), the U-vocabulary contract test
pins all 18 tags, and the docs site has the "uGUI Backend" page at /ugui.

| ID | Item | Evidence / anchor | Trigger to revisit |
|---|---|---|---|
| UG-3b | Compile-time markup diagnostics: driven-rect warning (rect props on a child of a childControl* LayoutGroup tag) and pointer-with-raycastTarget-false hint — the runtime editor hint ships (`UguiRectApplier.HintIfDriven`); the markup-level analysis needs parent-chain prop inspection in DiagnosticsAnalyzer | runtime hint only | ugui field wave shows the runtime hint is not early enough |
| UG-7 | `Animate` support for ugui hosts (tween adapter) — ON HOLD by owner; current guidance: Animator/DOTween via `Ref<RectTransform>` | `ResolveAnimationTarget` casts to VisualElement (null for ugui) | owner go |
| UG-8 | Prefab-bridge and island SCENE samples (need owner-side prefab/PanelSettings assets) + store omit-list review. Shipped: UguiDemo counter, RuntimeUguiGalleryDemo (slider/toggle/dropdown/input/scroll list), RuntimeUguiStressTestDemo (StressTest port), and the Ruitk.Ugui.Tests EditMode assembly (11 runtime tests incl. two stress-churn loops) | Samples/Showcase/Runtime + Ugui/Tests | owner F5 pass |
