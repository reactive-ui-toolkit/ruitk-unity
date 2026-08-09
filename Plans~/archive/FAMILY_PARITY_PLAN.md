# Family Settings Parity — UNITY EXECUTION PLAN

**Status: EXECUTED — archived 2026-08-04.** The campaign completed on `feat/family-parity`
(0.13.0 shipped; conformance waves C1–C8 ruled and fixed; gates green — see the execution log at
the bottom of this file).

Original status: **EXECUTING** (campaign started 2026-07-31 on `feat/family-parity`; execution log at the
bottom of this file). Originally written 2026-07-31; every anchor below verified against the working
tree on `master` HEAD `758b1588` **plus the uncommitted unified-settings pile** — see §1.1, that
pile is the baseline, not noise. **BASELINE DRIFT (M0 finding):** the §1.1 pile has since been
COMMITTED (`ab46dc53`) and release-staged as `0.13.0` (`f2728b43`, merged to dev+master via
PRs #224–#226) but **NOT published** (latest GitHub release: v0.11.0; no package tag; no Publish
run since 2026-07-25). Consequences: §1.1's "uncommitted" framing is history; §5 row 15's
`[Unreleased]` section is now the committed-but-unshipped `[0.13.0]` body; U-01's "never shipped
or committed" is now "committed, never shipped"; M6 must resolve with the owner whether the
campaign reshapes the staged 0.13.0 in place or targets 0.14.0.
**Family contract:** §0 below is the normative embed of the family parity contract (owner rulings
2026-07-31). Sibling legs carry the same section; substance is family-frozen. This plan MAY add
Unity detail; it MAY NOT contradict §0. Any conflict = STOP AND ASK the owner.
**Reference-implementation rule:** THIS repo is the family reference. The campaign's job is
**exposure + cleanup** — making the reconciler's existing behavior configurable and restoring
lost diagnostics semantics — **never behavior change to the reconciler itself**. If a task seems
to require changing what `FiberReconciler` *does* (ordering, effects, bailout, commit), stop: you
misread the task.
**Release target:** Unity package **0.12.0 → 0.13.0** (`package.json:4`), one minor, folding in
the already-implemented (uncommitted) unified-settings work it reshapes.
**Branch:** create `feat/family-parity` off the current branch. Never push, never tag, never add a
`Co-Authored-By` trailer, never commit unless the milestone says so and the owner has not said
otherwise.

Every decision is pre-made below. All verification gates are HARD — do not close a milestone with
any gate red.

---

## 0. FAMILY PARITY CONTRACT (normative — owner rulings 2026-07-31; do NOT re-litigate)

Reference implementation: **this repo (ruitk-unity)**. Sibling legs port these semantics; this leg
defines them. Rulings:

- **All settings ship into builds.** Defaults are off/production — an untouched build behaves as
  today (the two sanctioned exceptions are recorded in the decision log, §10: the hook-validation
  release flip and the no-store environment default).
- **`strict_mode` on every leg defaults OFF**, is opt-in, and is **force-off in release builds**.
- **NO UI Toolkit pooling.** `Shared/Elements/Pools/GlobalVisualElementPool.cs` was removed
  deliberately — commit `a05f5c07` (2025-11-17, "removed cache and pool GlobalVisualElementPool";
  landed via merge `e54ccf54` 2025-10-24, removed from mainline via merge `baf2797b`): generic
  `VisualElement` reset = state bleed. Only **adapter-gated** pooling — the uGUI pattern where the
  element joins the pool only if its adapter's `TryResetForPool` provably restores the pristine
  `Create()` state (`Ugui/Core/UguiHostConfig.cs:21-26`) — is sanctioned.
- **`exceptionControlFlow` stays removed.** It was the selector for a legacy error-boundary
  strategy; the feature itself survived unconditionally (verified: `DiagnosticsConfig.
  UseExceptionBoundaryFlow` is written at three mount sites and **read by nothing** — §5). The
  knob is a lie today; it dies in this campaign (§5) and is never revived.
- **`Basic` trace level is RESTORED to its legacy meaning: structural events.** It was lost by
  accident in the fiber rewrite. Evidence: legacy `Shared/Core/Reconciler.cs:1811` gated
  `EnableDiffTracing || TraceLevel != DiffTraceLevel.None` on the `[ReplaceNode]` structural log,
  vs `EnableDiffTracing || TraceLevel == DiffTraceLevel.Verbose` on the `[Diff]` detail logs
  (`:1669, :1922, :2142, :2161`) — file deleted whole in commit `2d8b50a7` ("removed all legacy
  code", 2025-12-14). Today `TraceLevel.Basic` parses (`Shared/Core/Config/RuitkConfig.cs:115-117`)
  but **no code path anywhere checks for it** — every gate is `== Verbose` or `!= None`.
- **Pool caps stay per-leg constants** (`UguiHostConfig.cs:25` `PoolCapacityPerType = 128`) — the
  on/off knob is canonical, the capacity is not.

### 0.1 Canonical knobs (identical names/semantics/defaults across all legs)

| # | Knob | Type | Default | Unity anchor today | What changes here |
|---|---|---|---|---|---|
| 1 | `time_slicing` | bool | `true` | `Shared/Core/Fiber/FiberReconciler.cs:363-373` — "no scheduler installed" is currently the ONLY synchronous path | `false` = explicit scheduler bypass → synchronous `WorkLoop()` even when a scheduler exists (U-04) |
| 2 | `time_slice_ms` | float | `2.0` | `private const float TimeSliceMs = 2.0f;` `FiberReconciler.cs:31`, consumed `:450` | const → setting (U-04) |
| 3 | `frame_budget_ms` | float | `4.0` | `Runtime/Core/RenderScheduler.cs:19-20` `[SerializeField] frameBudgetMs = 4.0f`, read `:152, :177` — serialized-but-unreachable (the component is only ever `AddComponent`-created at runtime: `Runtime/Core/RootRenderer.cs:52-56`, `Ugui/Core/UguiRootRenderer.cs:44-48`) | field ← setting at `Awake` (U-04) |
| 4 | `host_node_pool` | bool | `true` | uGUI pool always-on: acquire `UguiHostConfig.cs:55-63`, release `:221-235` | gates the uGUI pool; the UITK path stays unpooled — this knob does NOT create a UITK pool (U-05) |
| 5 | `hook_validation` | tri-state | `auto` | `Shared/Core/Hooks.cs:21` `EnableHookValidation = true` in ALL builds | **the flip**: auto = editor/dev ON, release OFF. `EnableHookAutoRealign` (`Hooks.cs:24`) stays as-is, internal, untouched (U-06) |
| 6 | `strict_diagnostics` | tri-state | `auto` | `Hooks.cs:22` `EnableStrictDiagnostics = false` | becomes auto (same mapping as #5). Warnings already implemented: state-update-during-render `Hooks.cs:156-161` + `:603-608`, missing-deps `:551-575`, funnel `WarnStrict` `:523-549`. FIX the misleading `[Hooks][StrictMode]` prefix → `[Hooks][Strict]` (3 sites: `:160, :573, :607`) (U-06) |
| 7 | `strict_mode` | bool | `false`, force-off in release | does not exist | ADD: double-invoke render functions, first result discarded, effects NOT double-invoked, diagnostics count the render once. Semantics reference: `ruitk-unreal/Plugins/ReactiveUIToolkit/Source/RuitkCore/Private/RuitkReconciler.cpp:565-570` (`RunOnce(); if (IsStrictModeEnabled()) Result = RunOnce();`). Unity insertion point: the single render call `Shared/Core/Fiber/FiberFunctionComponent.cs:160-163` (U-07) |
| 8 | `trace_level` | enum `none/basic/verbose` | `none` | enum exists `Shared/Core/Diagnostics/DiagnosticsConfig.cs:11-16`; `Basic` is dead (see §0 ruling) | RESTORE Basic = structural events, mapped to the fiber reconciler's placement/deletion/commit sites (the `FiberConfig.EnableFiberLogging` sites `FiberReconciler.cs:1172-1216` are the natural structural-log locations). Verbose = Basic + per-element/per-hook detail (existing Verbose sites). Full mapping table §6 (U-08) |
| 9 | `diff_tracing` | bool | `false`, **INDEPENDENT** | wrongly AND-ed with trace level in 3 element adapters: `Shared/Elements/RadioButtonElementAdapter.cs:78-80`, `RadioButtonGroupElementAdapter.cs:130-132`, `ToggleElementAdapter.cs:80-82` (`EnableDiffTracing && CurrentTraceLevel != None`; legacy semantics were OR — `Reconciler.cs:1669` et al.) | restore independence; wire to the real fiber diff layer, absorbing `FiberConfig.EnableFiberLogging` (`Shared/Core/Fiber/FiberConfig.cs:11` — verified set by NOTHING anywhere), which becomes internal to this knob (U-08) |
| 10 | `environment` | enum `auto/development/production` | `auto` | `Shared/Core/Config/RuitkSettings.cs:11-16, :38, :80-93` — already correct | keep; storage moves with the rest (U-01) |

Leg-specific extras MUST be marked **"(Unity-only)"** wherever they surface (schema, window, docs).
Unity's one extra: `diagnostics_output_folder` (consumed by
`Shared/Core/Config/RuitkDiagnosticsPaths.cs:34`).

---

## 1. Where Unity starts (verified 2026-07-31 — trust it, re-verify only what you touch)

### 1.1 The baseline INCLUDES the uncommitted unified-settings pile

The working tree carries the settings campaign of 2026-07-30, implemented and green but
**uncommitted**. It is the floor this plan builds on — do not revert it, do not commit it
separately, do not "clean it up" first. The relevant untracked/modified files:

- `Shared/Core/Config/RuitkSettings.cs` (untracked) — the ScriptableObject store this plan
  **replaces** (U-01). Fields: `environment :38`, `traceLevel :41`, `diffTracing :44`,
  `exceptionControlFlow :46-47`, `diagnosticsOutputFolder :55`; `ActiveOrNull :63`;
  `ResolveEnvironmentLabel :80-93`.
- `Editor/RuitkSettingsBootstrap.cs` (untracked) — asset discovery + `CreateSettingsAsset`; **to
  be deleted** (U-01).
- `Editor/RuitkSettingsBuildInjection.cs` (untracked) — Preloaded-Assets build hook; **to be
  deleted** (U-01; the JSON store needs no injection — `Resources/` ships by itself).
- `Editor/RuitkSettingsWindow.cs` (untracked) — the unified window; **kept and retyped** over the
  JSON (U-02). Sections: Configuration (`:62-219`), HMR (`:241-306`), Console navigation
  (`:382-403`); read-only no-store block `:86-121`; create-on-demand button `:116-120`; Browse
  picker `:170-195`.
- `Shared/Core/BuildDefinesConfig.cs` (modified) — the four bootstrap resolvers (`:15-53`), each
  `RuitkSettings.ActiveOrNull → RuitkConfig.Current → compiled default`. This resolver SHAPE is
  kept; the first hop changes store (U-01) and four new resolvers join it (U-04..U-07).
- `Shared/Core/Config/RuitkConfig.cs` (modified) — the LEGACY `Assets/ReactiveUIToolkit/config.json`
  `envVariables` fallback reader (path derivation `:102-106`). Stays, as fallback hop 2, minus the
  `exceptionControlFlow` field (§5).
- `Shared/Core/Config/RuitkDiagnosticsPaths.cs` (untracked) — output-root resolution; consumer of
  `RuitkSettings.ActiveOrNull` (`:34`); keeps working through the storage swap.
- `CHANGELOG.md` (modified) — an `[Unreleased]` section describing the ScriptableObject design.
  **Rewritten in M6** (the asset never shipped; describe only what 0.13.0 actually ships).
- Also in the pile, NOT this campaign's to touch beyond what §5/§7 name: the generator disk-scan
  fix + `SourceGenerator~/Tests/PackageLayoutDiscoveryTests.cs`, machine-path gate files, `.vscode/`,
  publish-menu removal, docs page edits.

### 1.2 Bootstrap seams (where resolved settings are APPLIED — all three mount surfaces)

| Surface | File | Resolver-apply block today |
|---|---|---|
| UITK runtime (`MonoBehaviour`) | `Runtime/Core/RootRenderer.cs` | `EnsureSetup` `:44-75` (env `:63`, trace `:66`, diff `:67`, exception `:68-69`, internal-logs-from-Verbose `:72-73`) |
| uGUI runtime | `Ugui/Core/UguiRootRenderer.cs` | `EnsureSetup` `:40-69` (same shape; exception `:64-65`, internal-logs `:67-68`) |
| Editor mounts | `Editor/EditorRootRendererUtility.cs` | `Mount` `:35-70` (exception `:57-58`, internal-logs `:60-61`) |

New knobs are read at these same seams, bootstrap-style, like the existing keys. Note the editor
surface uses `EditorRenderScheduler` (`Editor/EditorRenderScheduler.cs`), which has **no frame
budget at all** — `ExecuteQueue` (`:159-173`) drains every queue fully each editor update. See
U-04 for the decision.

### 1.3 Trace/diagnostics sites (complete inventory — §6 maps each to its new gate)

- `FiberConfig.EnableFiberLogging` consumers, ALL in `Shared/Core/Fiber/FiberReconciler.cs`:
  `:1136` (apply typed props), `:1154` (apply props), `:1172` (no props), `:1187` (InsertBefore),
  `:1199` (AppendChild), `:1209` (no host parent), `:1291` (CommitUpdate Label old/new text dump).
  The flag is declared `FiberConfig.cs:11` and **never assigned anywhere** — a compile-time-only
  debug knob.
- Commit-phase methods (structural-event homes): `CommitRoot :784`, `CommitDeletions :916`,
  `CommitWork :943`, `CommitPlacement :1094`, `CommitDeletion :1370` (all `FiberReconciler.cs`).
- `InternalLogOptions.EnableInternalLogs` (`Shared/Core/Diagnostics/InternalLogOptions.cs:12`) —
  set from `CurrentTraceLevel == Verbose` at the three mount seams (§1.2); consumers:
  `Shared/Elements/BaseElementAdapter.cs:112`, `Shared/Core/Hooks.cs:219, :247, :638, :665`.
- Direct Verbose checks: `Hooks.cs:1241` (UseEffect capture log),
  `Editor/EditorRenderScheduler.cs:111-114` (queue depths), `:133-136` (effect flush).
- The three AND-bugged adapter sites (§0.1 row 9).

### 1.4 Verification infrastructure (what "green" means here)

- **Engine-free gates** (run from the repo root, no Unity needed):
  ```bash
  node scripts/check-machine-paths.mjs      # machine-local path gate (CI: a step of test.yml's `gates` job)
  node scripts/corpus-hash.mjs --check      # family corpus (untouched by this campaign — must STAY green)
  ```
- **Compile harness — the host Unity project.** This package is consumed as a UPM `file:`
  dependency by a host project whose `Packages/manifest.json` contains
  `"com.reactiveuitoolkit": "file:…/ruitk-unity"` (+ `testables`). The host project's location is
  a MACHINE FACT — derive it (find the manifest naming this checkout among the owner's project
  roots; if not found, ask the owner), never write it into a tracked file. All Unity compile
  gates are `dotnet build` runs against the host project's generated csprojs — **VERIFY-UNITY**:
  ```bash
  # run from the HOST PROJECT root; ls *.csproj first — Unity regenerates these
  dotnet build Ruitk.Shared.csproj -v q --nologo      # engine core — 0 errors
  dotnet build Ruitk.Runtime.csproj -v q --nologo
  dotnet build Ruitk.Ugui.csproj -v q --nologo
  dotnet build Ruitk.Editor.csproj -v q --nologo
  dotnet build Ruitk.Samples.csproj -v q --nologo
  dotnet build Ruitk.Diagnostics.csproj -v q --nologo
  ```
  Verified 2026-07-31: the `Ruitk.*` csproj set exists in the host project alongside a STALE
  `ReactiveUITK.*` set from before the 0.12 rename — never build the stale set.
- **Player-assembly proof** (required for every milestone that touches `Shared/`, `Runtime/`, or
  `Ugui/`): build the `.Player` variant of each touched runtime assembly (e.g.
  `Ruitk.Shared.Player.csproj`). Verified 2026-07-31: **`Ruitk.*.Player.csproj` do not currently
  exist** (only stale `ReactiveUITK.*.Player.csproj`). At M0, ask the owner to enable player-csproj
  generation (Edit ▸ Preferences ▸ External Tools) and regenerate, or to run a regeneration
  headlessly; if neither is possible, STOP AND ASK — do not substitute the stale set, and do not
  skip the proof (a stray `UnityEditor` reference outside `#if UNITY_EDITOR` is exactly what this
  gate exists to catch).
- **THE OWNER MAY HAVE THE UNITY EDITOR OPEN.** Never launch Unity in batchmode against the host
  project (single-instance lock; `CICD/Editor/AssetStoreExport.cs:17-23` documents the batchmode
  pattern for CI, not for this). The `dotnet build` harness above is the whole point: it compiles
  the same csprojs without touching the running editor. In-editor verification is the owner's
  (M8).
- **SG/LSP suites** (`dotnet test SourceGenerator~/Tests/…`, `ide-extensions~/lsp-server/Tests/…`)
  are UNTOUCHED by this campaign unless a milestone touches `SourceGenerator~`/`language-lib`
  (none does). Run them once at M0 for a green floor and once at M7; anything red that this
  campaign didn't cause = pre-existing, record and continue.
- **Functional smoke — the settings-campaign pattern**: resolution-order proof by seeding each
  layer and asserting the resolved value (§4 M1 test spec), plus the owner-run window smoke (M8).

---

## 2. Engine-local decisions (U-01..U-09) — pre-made; do not improvise

**U-01 — STORAGE REWORK: plain JSON in `Resources`, ScriptableObject stack deleted.**
The store becomes a project-owned JSON file at **`Assets/Resources/ReactiveUIToolkit/config.json`**
in the CONSUMER project (never inside this package): under `Resources/` it ships into every player
build automatically, and `Resources.Load<TextAsset>("ReactiveUIToolkit/config")` is synchronous on
all platforms. **Created on demand only** by the settings window's create button — never
auto-dropped into a user's project (the TMP/DOTween lesson; same rule the SO campaign followed,
`RuitkSettingsWindow.cs:86-121`).
- **DELETE:** `Editor/RuitkSettingsBootstrap.cs` (+`.meta`), `Editor/RuitkSettingsBuildInjection.cs`
  (+`.meta`) — no discovery (fixed path), no Preloaded-Assets injection (Resources ships itself).
- **REWRITE `Shared/Core/Config/RuitkSettings.cs` in place** (same file, same class name, same
  assembly): from `ScriptableObject` to a plain serializable settings model + static loader.
  Keep the consumer-facing surface so `BuildDefinesConfig` and `RuitkDiagnosticsPaths.cs:34`
  need only mechanical edits: `RuitkSettings.ActiveOrNull` remains the "store or null" accessor
  (null = no JSON file ⇒ fall through to legacy config ⇒ compiled defaults), now backed by a
  cached `Resources.Load` + parse, with an explicit `Invalidate()` for the editor window to call
  after writes. No `UnityEditor` references (player assembly — the proof gate catches this).
- **Resolution order (unchanged shape, new first hop):** JSON store → legacy
  `Assets/ReactiveUIToolkit/config.json` `envVariables` (`RuitkConfig`, `:102-106`) → compiled
  defaults. All resolvers stay on `BuildDefinesConfig`.
- **Parsing:** `JsonUtility.FromJson` into a DTO whose fields are INITIALIZED to the §3 defaults —
  JsonUtility leaves absent fields at their initializers, which is exactly missing-key = default.
  Tri-states and enums are lowercase strings (`""` ⇒ default); unknown keys are ignored by
  JsonUtility (forward compat). The parse core takes a `string` (not a path) so it is unit-testable
  without asset plumbing.
- **The window (U-02) is the only writer.** It writes the FULL canonical schema (§3, all keys
  explicit, 2-space indent, trailing newline) via `File.WriteAllText` +
  `AssetDatabase.ImportAsset`, then `RuitkSettings.Invalidate()`.
- **Migration story:** the ScriptableObject asset was never shipped or committed — drop silently;
  one changelog sentence (M6). The owner's host project holds a smoke-test
  `Assets/ReactiveUIToolkitSettings.asset` — flag it for deletion during the M8 smoke. The legacy
  `config.json` fallback keeps store customers with an edited file working, unchanged.

**U-02 — The settings window becomes a typed editor over the JSON.** `Editor/RuitkSettingsWindow.cs`
keeps its shell: three sections, the no-store read-only "effective values" view (`:86-121`
pattern), "Create settings file" (writes the full §3 schema at the U-01 path, creating
`Assets/Resources/ReactiveUIToolkit/` as needed), the Browse picker with project-relative
normalization (`:170-195`), HMR + Console sections untouched. The `SerializedObject` plumbing
(`:123-197`) is replaced by: parse JSON → typed controls (`EnumPopup` for environment/trace_level,
`Popup` for the two tri-states, `Toggle`/`FloatField` for the rest, each labeled with its §0.1
semantics; Unity-only keys suffixed "(Unity-only)") → on change, rewrite the file (U-01 writer).
Show the file path + a Select button (ping the TextAsset). The exceptionControlFlow rows
(`:105-108`, `:147-153`) die in §5.

**U-03 — `BuildDefinesConfig` grows one resolver per knob** (same shape as `:15-53`):
`ResolveTimeSlicing`, `ResolveTimeSliceMs`, `ResolveFrameBudgetMs`, `ResolveHostNodePool`,
`ResolveHookValidation`, `ResolveStrictDiagnostics`, `ResolveStrictMode`; existing
`ResolveEnvironment`/`ResolveTraceLevel`/`ResolveEnableDiffTracing` re-point their first hop to the
JSON store; `ResolveExceptionBoundaryFlow` is deleted (§5). Legacy-fallback note: `RuitkConfig`
only ever carried `env/traceLevel/diffTracing` (+ the dying key) — the NEW knobs have no legacy
hop; their chain is JSON → compiled default. Tri-state mapping (`auto`):
`Application.isEditor || Debug.isDebugBuild ? on : off`. `strict_mode` force-off:
`ResolveStrictMode()` returns `false` whenever `!Application.isEditor && !Debug.isDebugBuild`,
regardless of the stored value — release players cannot opt in.

**U-04 — Reconciler knob exposure (no behavior change at defaults).**
- `time_slice_ms`: delete the const `FiberReconciler.cs:31`; add
  `public static float TimeSliceMs = 2.0f;` to `FiberConfig` (`Shared/Core/Fiber/FiberConfig.cs`),
  consume at `FiberReconciler.cs:450`. Set from `ResolveTimeSliceMs()` at the three §1.2 seams.
- `time_slicing`: add `public static bool TimeSlicingEnabled = true;` to `FiberConfig`. At
  `FiberReconciler.cs:363-373` the dispatch becomes: scheduler present AND `TimeSlicingEnabled` →
  `ScheduleRootWork` (sliced, unchanged); otherwise → `WorkLoop()` (the existing synchronous
  path, `:380-400`). This is the contract's "explicit bypass": today "no scheduler installed" is
  the only sync route; the knob makes the bypass first-class without touching either loop's body.
- `frame_budget_ms`: in `RenderScheduler.Awake` (`Runtime/Core/RenderScheduler.cs:33-43`), after
  the singleton guard, `frameBudgetMs = BuildDefinesConfig.ResolveFrameBudgetMs();`. Keep the
  `[SerializeField]` (harmless; the resolver wins for the runtime-created instance).
- **Editor scheduler stays unbudgeted BY DESIGN** (investigated): `EditorRenderScheduler` has no
  budget field and drains fully every `EditorApplication.update` (`:159-173`) — editor preview
  favors immediacy, HMR depends on prompt flushes, and no owner ask exists to change it.
  `frame_budget_ms` therefore applies to play-mode/player `RenderScheduler` only; `time_slicing` /
  `time_slice_ms` apply everywhere a scheduler slices (`ProcessWorkUntilDeadline`,
  `FiberReconciler.cs:429-472`). Document this Unity-only note in the window tooltip + docs (§7).
- Defaults leave every path byte-equivalent to today: `true/2.0/4.0` reproduce current behavior
  exactly.

**U-05 — `host_node_pool` gates the uGUI pool only.** `UguiHostConfig` reads
`BuildDefinesConfig.ResolveHostNodePool()` ONCE in its constructor into a `readonly bool
_poolEnabled` (bootstrap-read discipline — no per-frame resolver calls). Gate the acquire
(`UguiHostConfig.cs:55-63` — skip pool lookup, go straight to `adapter.Create()`) and the release
(`:221-235` — skip `TryResetForPool`, `DestroySafely` directly). `PoolCapacityPerType` (`:25`)
stays a per-leg constant per §0. The UITK host path gains NOTHING — no pool, no flag, per the §0
ruling.

**U-06 — Hook validation + strict diagnostics.** At the three §1.2 seams:
`Hooks.EnableHookValidation = BuildDefinesConfig.ResolveHookValidation();` and
`Hooks.EnableStrictDiagnostics = BuildDefinesConfig.ResolveStrictDiagnostics();`. The compiled
initializers (`Hooks.cs:21-22`) stay as-is (`true`/`false`) — they only matter before first mount,
and pre-mount there are no hooks; the resolver overwrites at every mount. Net effect = the
contract's flip: release players resolve `auto` → OFF. `EnableHookAutoRealign` (`:24`) is
untouched, internal, and NOT in the schema. Prefix fix: the three `[Hooks][StrictMode]` message
sites (`:160, :573, :607`) become `[Hooks][Strict]` — these are strict-DIAGNOSTICS warnings and
the old prefix collides with knob #7's name.

**U-07 — strict_mode double-invoke.** Insertion: `FiberFunctionComponent.cs:160-163`, the single
`wipFiber.TypedRender(...)` call. Shape (mirroring
`ruitk-unreal/Plugins/ReactiveUIToolkit/Source/RuitkCore/Private/RuitkReconciler.cpp:565-570`):
when `FiberConfig.StrictModeEnabled` (new static, set from `ResolveStrictMode()` at the §1.2
seams), invoke the render function twice; the FIRST result is discarded, the SECOND is the one
reconciled. Rules, all load-bearing:
- **Per-render state must be re-prepared between invokes**: hook cursors and the context-dep clear
  (`FiberFunctionComponent.cs:130-136` and the state-reset code immediately above the render call)
  run before EACH invoke — extract the existing prep into a local and call it twice; do not
  duplicate the code.
- **Effects are NOT double-invoked**: hook effect registration is index-keyed and overwrites in
  place (`Hooks.cs:1230-1239` pattern), so the second pass replaces the first's captures — verify,
  don't assume, for EVERY hook family (state/effect/layout-effect/memo/callback/ref/context) in
  the M4 tests. Effects run at commit, which happens once.
- **Diagnostics count the render once**: any per-render counter/metric/trace incremented inside
  the render path must reflect one logical render — audit `_workUnitCount`/metrics
  (`FiberReconciler.cs:33-39`), hook-order priming (`FiberFunctionComponent.cs:172-174` — priming
  after the second invoke is correct and unchanged), and the §6 trace sites (log once, on the
  counted invoke).
- **The discarded tree**: `VirtualNode` is pooled (`__Rent`). Investigate the existing recycle
  path at execution time; if a safe explicit release exists, release the discarded tree, else
  document the per-render garbage as strict-mode-only cost. Acceptance: the Ugui stress suite
  (`Ugui/Tests/UguiStressChurnTests.cs`) green with strict_mode forced on (M4 test spec).
- **Force-off in release** is U-03's resolver job, not a `#if` — the code path compiles into
  players but cannot activate.
- MaxRenderDepth guard (`:144-155`): the double-invoke must not double-count depth — one logical
  render increments `s_renderDepth` once (the two invokes happen within it).

**U-08 — Trace ladder + diff_tracing rewire.** Full site mapping in §6. Principles:
- `trace_level` drives TWO derived flags at the §1.2 seams (plus everywhere §6 says inline):
  `Basic` ⇒ structural logging on; `Verbose` ⇒ structural + detail
  (`InternalLogOptions.EnableInternalLogs` becomes `>= Basic`? NO — it is per-hook/per-element
  DETAIL, so it stays `== Verbose`; §6 rows are authoritative).
- Structural events (Basic): placement (`InsertBefore :1187-1196`, `AppendChild :1199-1204`),
  deletion (`CommitDeletion :1370` — add the log, none exists), the no-host-parent anomaly
  (`:1209-1215`), and a one-line commit summary in `CommitRoot :784` (counts already tracked:
  `_commitCount`, `_effectsCommitted`). Gate: `DiagnosticsConfig.CurrentTraceLevel != None`.
  Replacement is deletion+placement in the fiber model — the two logs above cover the legacy
  `[ReplaceNode]` semantics.
- Diff detail (`diff_tracing`, independent): props application (`:1136-1141, :1154-1160,
  :1172-1177`), `CommitUpdate` old/new dump (`:1291-1320+` — and DROP the `== "Label"` filter?
  NO: keep the site's existing behavior, just re-gate it; widening the dump is behavior change),
  and the three element adapters (fix `&&` → drop the trace-level term entirely: gate on
  `EnableDiffTracing` alone — that is the "independent" ruling; the legacy OR also let Verbose
  alone light these, which §6 preserves by ALSO gating them on `== Verbose`, i.e.
  `EnableDiffTracing || CurrentTraceLevel == Verbose`, the exact legacy expression).
- `FiberConfig.EnableFiberLogging` DIES as public API: delete the property (`FiberConfig.cs:11`),
  replace every consumer per §6. Nothing sets it today (verified), so no caller breaks.
  `ShowReconcilerInfo` (`FiberConfig.cs:16`) is dead too but is a §9 decision item, not this
  campaign's.
- `DiagnosticsConfig.EnableDiffTracing` (`DiagnosticsConfig.cs:26`) remains the runtime flag;
  `RuitkConfig` legacy parsing (`:73`) and the resolver chain keep feeding it.

**U-09 — Schema/docs/changelog discipline.** §3 is the schema; §7 the sync surface. The
`[Unreleased]` changelog section is REWRITTEN, not appended to (it describes unshipped work this
campaign reshapes). House changelog style per `CHANGELOG.md` top entry + `scripts/changelog.mjs
verify` if touched-lanes require; Discord entry per the `discord-changelog` skill (ASCII, ≤2000
chars) staged in `plans/DISCORD_CHANGELOG.md` at M7, shipped by the owner at release.

---

## 3. The JSON schema (canonical; the window always writes ALL keys)

```json
{
  "environment": "auto",
  "time_slicing": true,
  "time_slice_ms": 2.0,
  "frame_budget_ms": 4.0,
  "host_node_pool": true,
  "hook_validation": "auto",
  "strict_diagnostics": "auto",
  "strict_mode": false,
  "trace_level": "none",
  "diff_tracing": false,
  "diagnostics_output_folder": ""
}
```

- Keys are the §0.1 canonical snake_case names, identical across legs;
  `diagnostics_output_folder` is **(Unity-only)** and must be labeled so in window + docs.
- Enum/tri-state values are lowercase strings: `environment` ∈ `auto|development|production`;
  `hook_validation`/`strict_diagnostics` ∈ `auto|on|off`; `trace_level` ∈ `none|basic|verbose`.
  Parsing is case-insensitive, unknown value ⇒ default + one editor-only warning.
- Missing key ⇒ default (DTO initializers, U-01). Unknown keys ⇒ ignored.
- File absent ⇒ `RuitkSettings.ActiveOrNull == null` ⇒ legacy `config.json` hop ⇒ compiled
  defaults. An untouched project has NO file and behaves per the defaults column.
- Platform notes: `Resources.Load<TextAsset>` is synchronous everywhere Unity runs (the `.json`
  extension imports as TextAsset); no streaming-assets async, no per-platform path logic, WebGL
  included. The load happens once, cached, at first resolver call per domain.

---

## 4. Milestones

House rules for EVERY milestone: re-verify the anchors you are about to edit (the tree moves);
develop; extend tests IN the milestone; run the milestone's verify block; NEVER weaken an existing
test/gate to get green (if a gate seems wrong — STOP AND ASK); commit at milestone end with
`feat(parity): M<n> — <summary>` ONLY if the owner's no-auto-commit standing rule has been lifted
for this campaign — otherwise leave the work uncommitted and note milestone completion in the
final report. No push, ever.

**VERIFY-GATES** (every milestone, engine-free, repo root):
```bash
node scripts/check-machine-paths.mjs
node scripts/corpus-hash.mjs --check
```

### M0 — Baseline audit (no product code)
1. `git status --short` — expect exactly the §1.1 pile (plus this plan file). Anything else dirty:
   STOP AND ASK.
2. Locate the host project (§1.4). `ls *.csproj` there; confirm the `Ruitk.*` set. Ask the owner
   to produce `Ruitk.*.Player.csproj` (player-csproj generation) — record the answer; if
   unavailable this session, the player proof runs on whatever milestone first touches `Shared/`
   and MUST be resolved by then.
3. Run VERIFY-GATES + VERIFY-UNITY + both `~`-world suites (SG, LSP) for the green floor. Record
   totals. Any red — STOP AND ASK (do not build on a red base).
4. Create branch `feat/family-parity`.

Gate: everything green; findings recorded at the top of the working notes.

### M1 — exceptionControlFlow removal (small, self-contained, shrinks every later surface)
Execute the §5 table top to bottom. Definition of done: `grep -ri "exceptionControlFlow\|
UseExceptionBoundaryFlow\|ResolveExceptionBoundaryFlow" --include="*.cs"` over `Shared/ Runtime/
Ugui/ Editor/ Diagnostics/ Samples/ CICD/` returns ZERO hits; the docs/changelog rows are edited
per their table verdicts.

Gate: VERIFY-GATES + VERIFY-UNITY green; player proof for `Ruitk.Shared.Player` +
`Ruitk.Runtime.Player` + `Ruitk.Ugui.Player` (first `Shared/` touch — M0 step 2 must be resolved).

### M2 — Storage rework (U-01, U-02, U-03 for the EXISTING keys)
1. Rewrite `Shared/Core/Config/RuitkSettings.cs` per U-01 (model + loader + `Invalidate`).
2. Delete `Editor/RuitkSettingsBootstrap.cs`, `Editor/RuitkSettingsBuildInjection.cs` (+metas).
3. Retype `Editor/RuitkSettingsWindow.cs` per U-02 (existing keys only: environment, trace_level,
   diff_tracing, diagnostics_output_folder — the new knobs join in their own milestones so each
   lands with its plumbing).
4. Re-point `BuildDefinesConfig` first hop; `RuitkDiagnosticsPaths` mechanical fix.
5. Tests (new file `Ugui/Tests/RuitkSettingsJsonTests.cs`, EditMode-safe, in the existing
   `Ruitk.Ugui.Tests` asmdef — no new asmdef): parse-string cases — empty JSON ⇒ all defaults;
   full §3 schema round-trip; unknown key ignored; bad enum value ⇒ default; tri-state mapping
   table (`auto` editor-context = on); resolution-order proof seeding each hop (JSON model
   injected → legacy `RuitkConfig` fixture string → defaults) — the settings-campaign functional
   smoke pattern, now pinned as a real test.

Gate: VERIFY-GATES + VERIFY-UNITY + player proof (`Shared/` touched); `Ruitk.Ugui.Tests` compiles
(owner runs it in-editor at M8; the compile IS this session's gate — §1.4 locked-editor rule).

### M3 — Reconciler knobs (U-04, U-05)
1. `FiberConfig`: add `TimeSliceMs`, `TimeSlicingEnabled`; `FiberReconciler.cs:31` const deleted,
   `:450` re-pointed, `:363-373` bypass added.
2. `RenderScheduler.Awake` budget read; `UguiHostConfig` `_poolEnabled` gates (U-05).
3. Resolvers + seam application (all three §1.2 seams); window rows + §3 keys for
   `time_slicing/time_slice_ms/frame_budget_ms/host_node_pool` with the U-04 editor-unbudgeted
   tooltip note.
4. Tests: extend `RuitkSettingsJsonTests` for the four new keys; stress suites
   (`UguiStressChurnTests`) must be re-read to confirm they do not assume pooling — if one does,
   parameterize it, never delete the assertion.

Gate: VERIFY-GATES + VERIFY-UNITY + player proof. Acceptance: with no JSON file present, a
diff of runtime behavior is IMPOSSIBLE by construction (defaults reproduce the constants —
re-read the three edited decision points and confirm each default short-circuits to the old code
path).

### M4 — hook_validation flip + strict_diagnostics + strict_mode (U-06, U-07)
1. U-06 seam wiring + the three-site prefix fix.
2. U-07 double-invoke at `FiberFunctionComponent.cs:160-163` + `FiberConfig.StrictModeEnabled` +
   force-off resolver.
3. Window rows + §3 keys (`hook_validation`, `strict_diagnostics`, `strict_mode` — the last with a
   "double-invokes renders in dev; forced off in release builds" tooltip).
4. Tests: `Ugui/Tests` additions — strict_mode on: a counting component proves render body runs
   2×, effect runs 1×, cleanup runs 1×, committed UI identical to strict-off; hook-order
   validation still primes correctly; `UguiStressChurnTests` green with strict on (pool
   interaction, U-07 discarded-tree rule); state-update-during-render warning fires once per
   offending render (dedup via `StrictDiagnosticsKeys`, `Hooks.cs:539-543`, unchanged).

Gate: VERIFY-GATES + VERIFY-UNITY + player proof. Acceptance: message prefix grep —
`grep -rn "StrictMode" Shared/Core/Hooks.cs` returns zero hits.

### M5 — Trace ladder restoration + diff_tracing independence (U-08, §6)
Execute the §6 table row by row; delete `FiberConfig.EnableFiberLogging`; fix the three adapters.
Tests: a gate-matrix test (pure logic, `Ugui/Tests`): for each (trace_level × diff_tracing)
combination assert the derived flags (`structural`, `detail`, `diff`) match §6's truth table —
this pins Basic's restoration and diff independence against regression.

Gate: VERIFY-GATES + VERIFY-UNITY + player proof. Acceptance grep:
`grep -rn "EnableFiberLogging" --include="*.cs" .` → zero hits outside `Plans~/`.

### M6 — Changelog + version
1. REWRITE `CHANGELOG.md` `[Unreleased]` → `## [0.13.0] - <date>`: unified settings (JSON store,
   window), the canonical knob set with the §0.1 defaults table, the hook-validation release
   flip (BEHAVIOR CHANGE callout, house style precedent: the config.json demotion entry), Basic
   trace restoration, diff_tracing independence, strict_mode, exceptionControlFlow removal, the
   generator disk-scan fix (already drafted — keep), publish-menu removal (already drafted —
   keep). One sentence: the interim ScriptableObject store existed only unreleased and was
   replaced before shipping.
2. `package.json:4` → `0.13.0`.
3. `plans/DISCORD_CHANGELOG.md` entry per the `discord-changelog` skill (ASCII, ≤2000 chars).

Gate: VERIFY-GATES; `node scripts/changelog.mjs verify` if the tooling lane was touched (it was
not — extensions unchanged; run it anyway, it must stay green).

### M7 — Docs site
Per §7. Gate: `cd ReactiveUIToolkitDocs~ && npm run build` → 0 errors; VERIFY-GATES.

### M8 — Owner smoke (manual; do not skip silently)
Ask the owner (editor open is fine — this is IN the editor): open Reactive UI Toolkit ▸ Settings;
no-store view shows effective defaults; Create writes `Assets/Resources/ReactiveUIToolkit/
config.json` with the full §3 body; toggling trace_level to `basic` produces structural `[Fiber]`
logs on a Samples interaction and NO per-hook detail; `verbose` adds detail; `diff_tracing` alone
(trace `none`) produces diff logs (independence proven live); strict_mode on shows double render
counts in a dev build and is inert in a release build; delete the stale
`Assets/ReactiveUIToolkitSettings.asset` from the host project; run `Ruitk.Ugui.Tests` in the
Test Runner. Record results; if the owner defers, record THAT in the changelog entry
("editor smoke pending") — never silently.

---

## 5. exceptionControlFlow — full touchpoint table (M1; verdict per row)

| # | Location | What is there | Action |
|---|---|---|---|
| 1 | `Shared/Core/Config/RuitkSettings.cs:46-47` | `exceptionControlFlow` field + tooltip | delete (file is rewritten in M2 anyway; M1 deletes the field so the M2 rewrite never carries it) |
| 2 | `Shared/Core/Config/RuitkConfig.cs:25` | legacy DTO field | delete — an old user `config.json` carrying the key is silently ignored by JsonUtility, which is the intended migration |
| 3 | `RuitkConfig.cs:38` | `UseExceptionBoundaryFlow` property | delete |
| 4 | `RuitkConfig.cs:74` | fallback assignment | delete |
| 5 | `Shared/Core/BuildDefinesConfig.cs:45-53` | `ResolveExceptionBoundaryFlow()` | delete |
| 6 | `Shared/Core/Diagnostics/DiagnosticsConfig.cs:28-32` | `UseExceptionBoundaryFlow` static (write-only — zero readers, verified 2026-07-31) | delete |
| 7 | `Runtime/Core/RootRenderer.cs:68-69` | seam assignment | delete |
| 8 | `Ugui/Core/UguiRootRenderer.cs:64-65` | seam assignment | delete |
| 9 | `Editor/EditorRootRendererUtility.cs:57-58` | seam assignment | delete |
| 10 | `Editor/RuitkSettingsWindow.cs:105-108` | read-only "Effective values" row | delete |
| 11 | `Editor/RuitkSettingsWindow.cs:147-153` | PropertyField row | delete |
| 12 | `ReactiveUIToolkitDocs~/src/pages/UITKX/Concepts/UitkxConceptsPage.tsx:117` | settings bullet claiming the toggle "routes render exceptions through the exception-boundary flow" — **currently false** (the flag reads nothing) | delete the bullet (the section is updated wholesale in M7 anyway; M1 may fold this into M7 — either is fine, it must be gone by M7's gate) |
| 13 | `ReactiveUIToolkitDocs~/src/pages/Migration/MigrationPage.tsx:102-111` (`:106`) | 0.12-migration backup warning listing the legacy key | annotate: append "(`exceptionControlFlow` was removed in 0.13.0; the legacy key is ignored)" — the backup advice itself stays, it is about a real old file |
| 14 | `MIGRATION-0.12.md:51-54` (`:53`) | same list, shipped doc | **leave as history** — shipped migration docs are a frozen record (the machine-path gate's own frozen-tier principle); it accurately describes 0.12-era files |
| 15 | `CHANGELOG.md` `[Unreleased]` (uncommitted; two mentions: the Added section's window row, the Changed section's shipped-block note) | describes the knob as live | rewritten at M6 — M1 just leaves a `TODO(M6)` marker; SHIPPED changelog bodies mentioning the key are frozen, untouched |

Rationale lock (echo of §0): the knob selected between error-boundary strategies in the legacy
reconciler; the strategy selector died with `Shared/Core/Reconciler.cs` (commit `2d8b50a7`) and
the surviving boundary behavior is unconditional. Do not resurrect the knob "for compatibility" —
there is nothing for it to select.

---

## 6. Trace-site mapping (M5 executes this table; §0.1 rows 8-9 are the law)

Derived gates after M5 — spell them exactly like this in code (no new abstraction layer; these are
inline conditions or the existing `InternalLogOptions` bridge):
- **structural** ⇒ `DiagnosticsConfig.CurrentTraceLevel != TraceLevel.None` (Basic and Verbose)
- **detail** ⇒ `DiagnosticsConfig.CurrentTraceLevel == TraceLevel.Verbose`
  (`InternalLogOptions.EnableInternalLogs` keeps this meaning — assignment at the three seams
  unchanged)
- **diff** ⇒ `DiagnosticsConfig.EnableDiffTracing || CurrentTraceLevel == TraceLevel.Verbose`
  (the exact legacy OR expression, `Reconciler.cs:1669` at `2d8b50a7~1`)

| Site (today) | Today's gate | Becomes |
|---|---|---|
| `FiberReconciler.cs:1187-1196` InsertBefore log | `EnableFiberLogging` | **structural** |
| `FiberReconciler.cs:1199-1204` AppendChild log | `EnableFiberLogging` | **structural** |
| `FiberReconciler.cs:1209-1215` no-host-parent warning | `EnableFiberLogging` | **structural** |
| `FiberReconciler.cs:1370` `CommitDeletion` (no log exists) | — | ADD one **structural** log: `[Fiber] Delete {ElementType}` at method entry (top-level per deleted subtree — inside `CommitDeletions :916`'s loop, not per recursive child; one line per removed subtree, matching legacy `[ReplaceNode]` granularity) |
| `FiberReconciler.cs:784` `CommitRoot` (no log exists) | — | ADD one **structural** summary at commit end: `[Fiber] Commit #{_commitCount} effects={_effectsCommitted}` |
| `FiberReconciler.cs:1136-1141` apply typed props | `EnableFiberLogging` | **diff** |
| `FiberReconciler.cs:1154-1160` apply props (+key list) | `EnableFiberLogging` | **diff** |
| `FiberReconciler.cs:1172-1177` NO-props warning | `EnableFiberLogging` | **diff** |
| `FiberReconciler.cs:1291+` CommitUpdate Label old/new dump | `EnableFiberLogging && == "Label"` | **diff** (keep the Label filter — re-gating, not widening) |
| `Shared/Elements/RadioButtonElementAdapter.cs:78-80` | `EnableDiffTracing && != None` (BUG) | **diff** |
| `Shared/Elements/RadioButtonGroupElementAdapter.cs:130-132` | same BUG | **diff** |
| `Shared/Elements/ToggleElementAdapter.cs:80-82` | same BUG | **diff** |
| `Shared/Elements/BaseElementAdapter.cs:112` | `EnableInternalLogs` | **detail** (unchanged) |
| `Hooks.cs:219, :247, :638, :665` | `EnableInternalLogs` | **detail** (unchanged) |
| `Hooks.cs:1241-1253` UseEffect capture log | `== Verbose` inline | **detail** (mechanically: leave as-is or route through `InternalLogOptions` — pick the file's existing majority style, which is `InternalLogOptions`) |
| `Editor/EditorRenderScheduler.cs:111-129` queue-depth log | `== Verbose` inline | **detail** (unchanged gate; editor-side) |
| `Editor/EditorRenderScheduler.cs:133-143` effect-flush log | `== Verbose` inline | **detail** (unchanged gate) |
| `FiberConfig.EnableFiberLogging` (`FiberConfig.cs:11`) | set by nothing | DELETED (absorbed; §0.1 row 9) |

Strict-mode interaction (U-07): structural/diff logs fire on the COUNTED (second) invoke only —
placement/commit sites are commit-phase so they are naturally single; nothing in the render phase
above logs per-invoke except hook detail (`Hooks.cs:1241`), which under strict double-invoke will
log twice at Verbose — accepted, it is truthful (two captures happened), note it in docs.

---

## 7. Docs + changelog sync surface (M6/M7 checklist)

- [ ] `ReactiveUIToolkitDocs~/src/pages/UITKX/Concepts/UitkxConceptsPage.tsx:102-120` — the
      settings bullets: rewrite to the §3 schema (all 10 canonical knobs + the Unity-only folder
      key, marked), the JSON path + create-on-demand flow replacing the asset story, the
      `auto` tri-state semantics, the trace ladder (`basic` = structural, `verbose` = +detail,
      `diff_tracing` independent), strict_mode (dev-only, double-invoke, release force-off), and
      the U-04 editor-unbudgeted note. Delete row 12 of §5 if M1 left it.
- [ ] `ReactiveUIToolkitDocs~/src/pages/Migration/MigrationPage.tsx:106` — §5 row 13 annotation.
- [ ] `CHANGELOG.md` — M6 rewrite (see milestone).
- [ ] `package.json:4` — `0.13.0`.
- [ ] `plans/DISCORD_CHANGELOG.md` — M6 entry.
- [ ] `CLAUDE.md` — if it gains/keeps any sentence about settings storage, it must say JSON store,
      not ScriptableObject (currently it says neither — only add if something there becomes wrong).
- [ ] Extension lanes (`ide-extensions~/changelog.json`, marketplace pages): UNTOUCHED — no
      extension change in this campaign; `node scripts/changelog.mjs verify` must remain green.
- [ ] `ReactiveUIToolkitDocs~` build: `npm run build` → 0 errors.

---

## 8. DO-NOT list (violating any = stop and ask)

1. **NO UI Toolkit pooling — not even "while we're in there".** History, quoted (§0): the one
   attempt, `Shared/Elements/Pools/GlobalVisualElementPool.cs`, was deliberately removed in
   `a05f5c07` ("removed cache and pool GlobalVisualElementPool", 2025-11-17) because resetting a
   generic `VisualElement` cannot be proven complete — leftover state bleeds into the next mount.
   Only adapter-gated pooling (uGUI `TryResetForPool`, where each adapter owns its reset proof)
   is sanctioned, and `host_node_pool` only GATES the existing uGUI pool — it creates nothing.
2. **NO exceptionControlFlow revival.** It was a strategy selector for a legacy reconciler path
   that no longer exists; the feature it "selected" runs unconditionally. A config key that
   selects nothing is worse than none (§5 rationale lock).
3. **Reconciler BEHAVIOR unchanged — this leg is the reference.** Every default must reproduce
   today's execution byte-for-byte (M3 acceptance). If parity with a sibling seems to require a
   Unity reconciler change, the sibling is wrong or the contract is — STOP AND ASK; do not edit
   `FiberReconciler`'s algorithm, effect ordering, bailout, or commit sequencing.
4. **Mount stays synchronous.** `time_slicing=false` routes through the EXISTING `WorkLoop()`;
   do not introduce async mount, coroutines, or deferred first paint anywhere.
5. **Never auto-create the settings file.** Window button only (TMP/DOTween lesson). Opening the
   window must not dirty the user's project.
6. **Never launch the Unity editor from automation while the owner may have it open** (§1.4) —
   the dotnet compile harness is the only sanctioned compile check; in-editor steps are M8, owner-run.
7. **Do not commit the uncommitted settings pile "to clean up" before M0** — it is the baseline;
   the owner's no-auto-commit rule stands unless explicitly lifted.
8. **Never weaken/delete an existing test assertion to get green** (parameterizing a
   pooling-assuming stress test per M3 is the sanctioned pattern; deleting its assert is not).
9. **No new asmdef, no SourceGenerator~/language-lib edits, no corpus/hash writes** — this
   campaign has zero `.uitkx`-language surface; `corpus-hash.mjs --check` green throughout.
10. **No machine-local paths in anything tracked** — host-project location and Unity editor
    location are derived or live in `.ruitk-local.json` (§1.4); `check-machine-paths.mjs` gates
    every milestone.
11. **Pool capacity stays a constant** (`PoolCapacityPerType = 128`) — do not promote it to a
    setting "for symmetry"; §0 pins caps per-leg.

---

## 9. Dead-code decision items (LISTED for the owner — recommendation only, NOT part of this campaign)

| Item | Evidence | Recommendation |
|---|---|---|
| `PropTypeValidator` subsystem | `Shared/Core/PropTypes.cs:131-180`; `internal static class` with `Enabled=true` and `Validate(...)` — **zero call sites repo-wide** (verified 2026-07-31: the only grep hit is its own declaration). The public `PropTypes`/`WithPropTypes` surface (`:182+`) attaches definitions that nothing ever validates. | Owner-gated ticket: **remove-or-wire**. If wired, it belongs behind `strict_diagnostics`; if removed, the public `WithPropTypes` API needs a deprecation minor first. Do NOT fold into this campaign. |
| `FiberConfig.ShowReconcilerInfo` | `Shared/Core/Fiber/FiberConfig.cs:16` — declared, never read, never set (verified). | Remove in the same future ticket; public static, so deprecation note in changelog. |

---

## 10. Decision log (campaign-local; §0 decisions are the family's, these are Unity's)

| # | Decision | Why |
|---|---|---|
| D-1 | Storage = `Assets/Resources/ReactiveUIToolkit/config.json` TextAsset in the CONSUMER project; package never carries one | Resources ships into every build with zero build hooks; synchronous load everywhere; project-owned = writable + upgrade-stable in all install layouts (the UPM PackageCache problem that killed the in-package file, documented at `RuitkConfig.cs:92-100`) |
| D-2 | `RuitkSettings` class name + `ActiveOrNull` accessor survive the SO→JSON rewrite | Minimizes churn at `BuildDefinesConfig` + `RuitkDiagnosticsPaths`; the type was never shipped, so no public-API compat concern |
| D-3 | Bootstrap + BuildInjection deleted rather than adapted | Both exist solely to solve SO problems (asset discovery, preloaded-assets injection) that JSON-in-Resources does not have |
| D-4 | JsonUtility DTO-with-initialized-defaults as the parser | No new dependency; absent-field = initializer is exactly missing-key = default; tri-states as strings sidestep JsonUtility's absent-bool blindness |
| D-5 | Editor scheduler stays unbudgeted (U-04) | Investigated: no budget exists today, editor preview + HMR favor immediate drain; aligning it would be a behavior change with no ask — documented instead |
| D-6 | Sanctioned untouched-build changes, exactly two | (a) hook_validation release flip — §0 ruling item 5 explicitly sanctions it; (b) no-store editor environment: `production` (legacy compiled default) → `auto`→`development` — this is §0.1 row 10's canonical default doing its job, and the (uncommitted) changelog already carries the BEHAVIOR CHANGE callout pattern to extend |
| D-7 | `[Hooks][StrictMode]` → `[Hooks][Strict]` rather than renaming the strict_diagnostics knob | The messages belong to strict_diagnostics; `strict_mode` (knob 7) now owns the "StrictMode" name family-wide — prefix must stop squatting it |
| D-8 | Legacy trace evidence pinned to `2d8b50a7~1:Shared/Core/Reconciler.cs` | Executors can re-derive every §6 "legacy" claim with `git show` — no trust required |
| D-9 | strict_mode gate is a resolver-level runtime force-off, not `#if` | Contract says settings ship into builds; the CODE ships, the ACTIVATION is denied in release — simplest proof of "cannot opt in" |
| D-10 | MIGRATION-0.12.md untouched (§5 row 14) | Shipped migration docs are frozen history — same principle the machine-path gate encodes for archived tiers |

---

## 11. Reference reading list (the files that DEFINE the family semantics — protect them)

Sibling-leg executors port FROM these; Unity executors must not casually reshape them. Read before
touching, cite line-exactly in commits:

- `Shared/Core/Fiber/FiberReconciler.cs` — work loop (`:360-472`), commit phase (`:784+`),
  placement (`:1094+`). THE reference reconciler. This campaign only re-gates its logs and
  parameterizes two constants.
- `Runtime/Core/RenderScheduler.cs` — the budgeted frame pump (`:150-180`), priority queues,
  batching. `frame_budget_ms` semantics live here.
- `Shared/Core/Fiber/FiberFunctionComponent.cs` — the render call (`:130-175`), hook-order
  priming, effect flag propagation. strict_mode's insertion point.
- `Shared/Core/Hooks.cs` — validation (`:21`), strict diagnostics (`:22, :156-161, :523-575,
  :603-608`), the 20+ hook implementations whose index-keyed re-registration makes double-invoke
  safe.
- `Shared/Core/BuildDefinesConfig.cs` + `Shared/Core/Config/RuitkConfig.cs` +
  `Shared/Core/Config/RuitkSettings.cs` — the three-hop resolution chain every leg mirrors.
- `Ugui/Core/UguiHostConfig.cs` — the sanctioned pooling pattern (`TryResetForPool`).
- `ruitk-unreal/Plugins/ReactiveUIToolkit/Source/RuitkCore/Private/RuitkReconciler.cpp:565-570` —
  the strict_mode double-invoke shape this leg adopts (sibling reference, read-only).
- History: `git show a05f5c07` (pool removal), `git show 2d8b50a7` (legacy reconciler deletion —
  the Basic-trace evidence base).

---

## 12. Error signatures / risks

| Signature | Meaning → action |
|---|---|
| CS0246 `UnityEditor` in a `.Player` build | An editor API leaked into `Shared/`/`Runtime/`/`Ugui/` — wrap in `#if UNITY_EDITOR` or move to `Editor/`; this is the player-proof gate doing its job |
| `Ruitk.*.Player.csproj` absent | M0 step 2 unresolved — STOP AND ASK; do not fake the proof with the stale `ReactiveUITK.*` set |
| Settings window edits do nothing at runtime | `RuitkSettings.Invalidate()` not called after write, or the TextAsset wasn't re-imported — U-01 writer contract |
| A default-config run behaves differently from `master` | M3 acceptance violated — a knob's default doesn't short-circuit to the old path; diff the decision point, not the symptom |
| Strict-mode double effects | U-07 rule 2 broken for some hook family — the index-keyed overwrite assumption failed; fix the prep-reset, never skip the second invoke |
| `check-machine-paths.mjs` red on the plan or code | A drive-absolute or personal-root path got written — derive it or move it to `.ruitk-local.json`; NEVER extend the allow-list to pass |
| `corpus-hash.mjs` red | Something touched the `.uitkx` corpus — this campaign must not; revert the touch |
| Unity editor "assembly locked" during a build attempt | You launched Unity or copied into `Analyzers/` against §1.4/DO-NOT 6 — stop, use the dotnet harness |
| Verbose logs appear at `basic` | A §6 row mis-gated (structural vs detail) — re-check against the truth-table test from M5 |

---

*End of plan. Companion documents: `Plans~/ES_MODULES_EXECUTION_PLAN.md` (house plan-style
precedent), `CHANGELOG.md` `[Unreleased]` (the uncommitted settings-campaign record this plan
absorbs), the family parity contract as embedded in §0 (sibling legs carry the same section).*

---

## EXECUTION LOG (running; newest milestone last)

### M0 — Baseline audit — DONE 2026-07-31
- **Tree state:** clean; the §1.1 pile is committed (`ab46dc53` settings, `f2728b43` 0.13.0
  release staging, `ca31886e` this plan) — see the BASELINE DRIFT note in the header. Branch
  `feat/family-parity` checked out at `392154d4` (== origin/dev; origin/master identical).
- **Host project:** located via `Packages/manifest.json` naming this checkout (`file:` dependency
  + `testables`). The **Unity editor IS OPEN on it** (editor process + `Temp/UnityLockfile`
  verified) → locked-editor mode for the whole round: dotnet harness only, no Unity launches.
- **Csproj sets:** current `Ruitk.*` set present (regenerated 2026-07-30 by the running editor);
  `Ruitk.*.Player.csproj` **absent** (only the stale pre-rename `ReactiveUITK.*.Player` set,
  from a months-old generation — never used, per plan). **Workaround executed:** the three
  Player csprojs are SYNTHESIZED outside the repo (scratchpad) from the current `Ruitk.*` set by
  the exact generator delta (drop `UNITY_EDITOR*` defines, drop `UnityEditor*` reference blocks,
  Player output dir, `.Player` project-reference chain, paths absolutized so nothing is written
  into the host project). Baseline proof: Shared/Runtime/Ugui Player builds **0 errors**.
  OWNER TOUCHPOINT (deferred): enable player-csproj generation (Edit ▸ Preferences ▸ External
  Tools) and regenerate, so later milestones can run the real artifact.
- **Green floor:** machine-paths gate ✓; corpus-hash ✓ (`917dd8cd…`); VERIFY-UNITY 6/6 csprojs
  0 errors (warnings pre-existing: Shared 5, Editor 1, Samples 11); SG suite **1828/1828**; LSP
  suite **152/152**. `dotnet test` churned `Analyzers/*.dll` — reverted via targeted checkout
  (watch this before every commit).

### M1 — exceptionControlFlow removal — DONE 2026-07-31
- §5 table executed top to bottom; every anchor re-verified against the tree before editing
  (all line anchors held). Rows 1–11 deleted as specified; row 12 (Concepts bullet) done NOW
  rather than M7 (plan allows either); row 13 annotated verbatim; row 14 left frozen.
- **Row 15 deviation (drift-adjusted):** the changelog section is no longer an uncommitted
  `[Unreleased]` but the committed, release-staged `[0.13.0]` body (see header drift note) — so
  no `TODO(M6)` marker was injected into `CHANGELOG.md` itself. The two live mentions (the Added
  window row at `:18`, the Changed shipped-block note at `:44`) are recorded HERE instead:
  **TODO(M6): rewrite both when the owner resolves reshape-0.13.0 vs 0.14.0.** Row 13's
  "removed in 0.13.0" annotation must be re-versioned then too if 0.14.0 wins.
- **DoD grep:** `exceptionControlFlow|UseExceptionBoundaryFlow|ResolveExceptionBoundaryFlow`
  over `Shared/ Runtime/ Ugui/ Editor/ Diagnostics/ Samples/ CICD/` `*.cs` = **ZERO hits**.
  Repo-wide sweep: remaining hits only in frozen history (`MIGRATION-0.12.md`,
  `Plans~/BUGS_FOUND_AFTER_RENAME.md`), the stale-by-design `Plans~/codebase-index.json`
  snapshot, this plan, and untracked build output — all correct to leave.
- **Gates:** machine-paths ✓, corpus-hash ✓; VERIFY-UNITY 6/6 csprojs 0 errors; player proof
  Shared/Runtime/Ugui `.Player` 0 errors (synthesized-csproj harness, M0); docs `npm run build`
  0 errors (pre-existing chunk-size warning only).

### M2 — Storage rework (U-01/U-02/U-03 existing keys) — DONE 2026-07-31
- **U-01:** `RuitkSettings.cs` rewritten in place — SO → plain model + static loader
  (`Resources.Load<TextAsset>("ReactiveUIToolkit/config")`, cached, `Invalidate()`), string-in
  `Parse` core, case-insensitive lowercase enum/tri-state strings (unknown value ⇒ default + one
  editor-only warning), `ToCanonicalJson()` (full §3 body: canonical order, 2-space indent, LF,
  trailing newline). `SetActive` kept as the explicit override/test seam; `Instance` dropped
  (zero consumers, verified). **Schema-scope interpretation:** the MODEL speaks the full §3
  schema from M2 (DTO initializers = §3 defaults; the writer always emits all keys — U-01's
  writer contract); the WINDOW rows and RESOLVERS for the new knobs join in M3/M4 with their
  plumbing, exactly as the milestone text orders them. `RuitkTriState` added;
  `BuildDefinesConfig.MapTriState` (the U-03 auto-mapping) added now so the M2 tri-state tests
  pin it.
- **Deletions:** `Editor/RuitkSettingsBootstrap.cs`, `Editor/RuitkSettingsBuildInjection.cs`
  (+metas) — discovery and Preloaded-Assets injection have no JSON-store equivalent (D-3).
- **U-02:** window Configuration section retyped over the file (fixed path
  `Assets/Resources/ReactiveUIToolkit/config.json`, mtime-cached parse, change-check → full
  canonical rewrite → `ImportAsset` → `Invalidate`), "Create settings file" button (dir created
  on demand), File row + Select (pings the TextAsset), diagnostics folder labeled
  "(Unity-only)", Browse keeps project-relative normalization. HMR + Console sections untouched.
- **U-03 (existing keys):** `BuildDefinesConfig` unchanged mechanically (the ActiveOrNull
  surface survived by design, D-2) — doc headers re-worded to the JSON story; `RuitkConfig`
  gained the `Parse(string)` core + `SetCurrentForTests` seams; `InternalsVisibleTo
  ("Ruitk.Ugui.Tests")` added (editor-only block).
- **Tests:** `Ugui/Tests/RuitkSettingsJsonTests.cs` (+meta, fresh GUID) in `Ruitk.Ugui.Tests` —
  empty/null ⇒ defaults; canonical default body ⇒ defaults; **writer-emits-§3-byte-for-byte
  pin**; non-default round-trip; unknown key ignored; missing keys keep defaults; bad enum ⇒
  default; case-insensitivity; tri-state mapping table (auto = on in editor); three-hop
  resolution-order proof (JSON injected → legacy fixture string → compiled defaults) + legacy
  parse never throws.
- **Bughunt findings:** (1) missing `using Ruitk.Core.Diagnostics;` in the retyped window —
  caught by the harness, fixed; (2) the generated host csprojs cannot compile mid-milestone
  file adds/deletes — added a `sync-csproj` harness step (scratchpad copies: paths absolutized,
  deleted Compile entries pruned, new files added, `ProjectReference` re-mapped to synced
  copies; `Ruitk.Diagnostics` needed BOTH `Ruitk.Editor` and transitive `Ruitk.Samples`
  remapped). Real-generation csprojs regenerate when the owner's editor refocuses.
- **Owner-visible migration note (M8):** the host project's stale
  `Assets/ReactiveUIToolkitSettings.asset` now loses its script (SO class gone) — already
  flagged for deletion in the M8 smoke.
- **Gates:** machine-paths ✓, corpus-hash ✓; VERIFY-UNITY — Shared/Runtime/Ugui direct 0
  errors, Editor/Samples/Diagnostics 0 errors via synced csprojs, `Ruitk.Ugui.Tests` (with the
  new file) 0 errors — the compile IS this session's test gate (locked editor; owner runs the
  suite in-editor at M8); player proof Shared/Runtime/Ugui `.Player` 0 errors.

### M3 — Reconciler knobs (U-04/U-05) — DONE 2026-07-31 (round 2; editor still open — locked-editor mode re-verified: lockfile + 3 Unity processes)
- **U-04:** `FiberConfig` gained `TimeSlicingEnabled` (true) + `TimeSliceMs` (2.0f) as auto-
  properties (file's existing style; the plan's field snippet is shape, not letter).
  `FiberReconciler`: the `:31` const deleted; `:450` re-pointed to `FiberConfig.TimeSliceMs`;
  the `:363-373` dispatch is now `scheduler != null && TimeSlicingEnabled → ScheduleRootWork,
  else WorkLoop()`. **Bughunt find (deviation, load-bearing):** the deferred-update replay in
  `CommitRoot`'s finally (`:901`) also had to learn the bypass — its condition is now
  `_scheduler == null || !TimeSlicingEnabled`, because under the bypass a commit is reached
  from `WorkLoop`, so NO Slice callback exists to pick replayed work up: without this, a
  setState during a bypassed commit (e.g. from a layout effect) stalls forever. Default-true
  reduces to the old `_scheduler == null` exactly. Pinned by
  `ReconcilerKnobTests.TimeSlicingBypass_StateUpdateDuringCommit_ReplaysSynchronously`.
- **frame_budget_ms:** read in `RenderScheduler.Awake` (singleton-winning branch) from
  `ResolveFrameBudgetMs()`; `[SerializeField]` kept. Editor scheduler untouched (D-5).
- **U-05:** `UguiHostConfig` reads `ResolveHostNodePool()` ONCE in the constructor into
  `readonly bool _poolEnabled`; gates the acquire pool-lookup and the release
  `TryResetForPool` branch. `PoolCapacityPerType` untouched. UITK path untouched.
- **U-03 (new keys):** four resolvers added to `BuildDefinesConfig`
  (`ResolveTimeSlicing/ResolveTimeSliceMs/ResolveFrameBudgetMs/ResolveHostNodePool`), chain
  JSON → compiled default (no legacy hop — documented in-code). Seam application at all three
  §1.2 seams: `FiberConfig.TimeSlicingEnabled/TimeSliceMs` set bootstrap-style (the editor
  seam comments that frame_budget_ms deliberately does NOT apply there).
- **U-02 (new rows):** window store editor gained the four typed rows in §3 order with §0.1
  tooltips (frame_budget_ms row carries the U-04 editor-unbudgeted Unity note); the no-store
  "Effective values" view gained the same four via the resolvers.
- **Tests:** `RuitkSettingsJsonTests` +3 (partial-doc key spellings; JSON-store-wins for the
  four knobs; no-legacy-hop proof — legacy doc seeded, new knobs still compiled defaults).
  `UguiStressChurnTests` parameterized per the plan: `CreateRenderer(bool hostNodePool)`
  seeds the store BEFORE `UguiHostConfig` construction (ctor-read discipline); the reuse test
  now explicitly runs pool-ON (assertion untouched), new pool-OFF companion asserts per-cycle
  structure coherence AND ≥ BoxCount+900 distinct instances (reuse provably off). New
  `Ugui/Tests/ReconcilerKnobTests.cs` (+meta, fresh GUID): defaults pin (2.0/true), sliced
  default routes updates through a recording scheduler (stale until slice, commit after
  drain; mount enqueues nothing), bypass commits synchronously with ZERO scheduler enqueues,
  `TimeSliceMs=0` forces multi-slice vs `100000` single-slice (yield plumbing at `:450`), and
  the deferred-replay pin above. Assumption audit: the only render-work `Enqueue` in
  `FiberReconciler` is the Slice (`:424`) and layout effects run inside commit
  (`CommitWork :979 → CommitLayoutEffects`) — both verified by grep before the assertions
  were written.
- **M3 acceptance re-read (no JSON file ⇒ byte-equivalence):** dispatch `true &&` ⇒ old
  `_scheduler != null`; `:450` reads 2.0f (the old const's value); `:901` `|| !true` ⇒ old
  `_scheduler == null`; `Awake` resolver returns 4.0f (the serialized default); pool gates
  `true &&` ⇒ old conditions. All five short-circuit to the old code paths.
- **Bughunt fixes:** (1) the `:901` replay condition (above); (2) CS0104 `Object` ambiguity
  in the new test file (`using System;` vs `UnityEngine.Object`) — qualified the two
  `DestroyImmediate` calls; caught by the harness.
- **Gates:** machine-paths ✓ (with `add -N` for the two new files), corpus-hash ✓
  (`917dd8cd…`); VERIFY-UNITY — Shared/Runtime/Ugui direct 0 errors,
  Editor/Samples/Diagnostics/Ugui.Tests 0 errors via freshly re-synced csprojs (Editor prune
  2, Ugui.Tests add 2); player proof Shared/Runtime/Ugui `.Player` 0 errors (synthesized
  harness, M0). No `Analyzers/*.dll` churn this round (only `dotnet build`, no `dotnet
  test`).

### M4 — hook_validation flip + strict_diagnostics + strict_mode (U-06/U-07) — DONE 2026-07-31 (round 3; editor OPEN again — locked-editor mode re-verified: host `Temp/UnityLockfile` + 3 Unity processes; compile IS this session's test gate, owner runs the suites at M8)
- **U-06 wiring:** `BuildDefinesConfig` gained `ResolveHookValidation` / `ResolveStrictDiagnostics`
  (both `MapTriState(store-or-Auto)`, chain JSON → compiled default, no legacy hop) and
  `ResolveStrictMode()` delegating to an **internal `ResolveStrictMode(bool developmentContext)`
  core** (the D-9 resolver-level force-off, made testable: `false` context ⇒ `false` regardless of
  the stored value). Seam application at all three §1.2 seams:
  `Hooks.EnableHookValidation` / `Hooks.EnableStrictDiagnostics` / `FiberConfig.StrictModeEnabled`
  set bootstrap-style after the M3 knobs. Compiled initializers (`Hooks.cs:21-22`) untouched per
  U-06; `EnableHookAutoRealign` untouched, not in the schema.
- **Prefix fix:** the three `[Hooks][StrictMode]` sites (`:160, :573, :607`) → `[Hooks][Strict]`
  via one replace-all. **Acceptance grep: `grep -rn "StrictMode" Shared/Core/Hooks.cs` = ZERO
  hits** (`[Hooks][Strict]` = 3).
- **U-07 double-invoke:** `FiberConfig.StrictModeEnabled` (default false) + the insertion at the
  render call. The per-pass prep (hook cursor resets, formerly `:44-46` pre-bailout + the
  context-dep clear, formerly `:132-136`) is EXTRACTED into a `RunRenderPass()` local function
  (direct-called ⇒ struct closure, no allocation) called once normally, twice under strict —
  `childVNode = RunRenderPass(); if (StrictModeEnabled) childVNode = RunRenderPass();`, the
  sibling legs' `RunOnce(); if strict Result = RunOnce()` shape. Moving the cursor resets
  post-bailout is safe: grep-verified the only cursor writes/reads outside `Hooks.cs` were the
  `:44-46` resets themselves, and the context-dep clear must NOT move earlier (the bailout's
  `HasContextChanged` reads the deps). Depth guard counts one logical render (both invokes inside
  one `s_renderDepth` increment); `FlushQueuedStateUpdates` stays pre-bailout (once); hook-order
  priming stays after the second invoke per plan. `_workUnitCount` is per-fiber
  (`PerformUnitOfWork`), so metrics count the render once by construction.
- **Discarded-tree verdict (U-07 rule 4, investigated):** NO safe explicit release exists —
  documented as strict-mode-only per-render garbage. Two independent reasons:
  (1) `VirtualNode.__ScheduleReturn`/`__FlushReturns` have **zero callers anywhere** (the vnode
  pool rents but never returns — dormant recycle path, pre-existing); (2) memoized subtrees
  (UseMemo-cached vnodes whose deps are unchanged between the invokes) are SHARED between the
  first and second results, so force-releasing the first tree (vnodes or their rented host props)
  would corrupt the reconciled second tree. The discarded tree's `__Rent`ed family props are
  never scheduled for return (they never enter a fiber) — bounded GC garbage, pool unaffected.
- **Per-hook-family index-keyed-overwrite audit (every family in `Hooks.cs`, line-verified):**
  | Family | Slot mechanism | Second-invoke behavior | Verdict |
  |---|---|---|---|
  | UseState | `HookStates[i]` Add-if-fresh, read-only after | reads same slot; setter delegates cached per `(index, kind)` | overwrite-safe |
  | UseReducer | `ReducerHookState` object reused via `is` check; `Dispatch` allocated once | same object, reducer ref refreshed | overwrite-safe |
  | UseMemo | `(value, deps)` tuple; recompute only on `DepsChanged` | invoke-2 deps value-equal invoke-1's ⇒ NO recompute (factory once per logical change) | overwrite-safe |
  | UseCallback | same tuple shape | same — cached callback returned | overwrite-safe |
  | UseImperativeHandle | same tuple shape | factory once | overwrite-safe |
  | UseDeferredValue | `(val, deps)` tuple; slot write direct or via `EnqueueBatchedEffect` | schedulerless: invoke 1 already wrote ⇒ invoke 2 sees equal ⇒ no-op. WITH scheduler: both invokes can enqueue the slot-write batched effect — the two writes are IDENTICAL (same value, same slot), idempotent; accepted + noted | safe (idempotent double-enqueue) |
  | UseTransition | pure cursor bump, constants returned | trivially safe | safe |
  | UseEffect | `FunctionEffects[EffectIndex]` Add-or-overwrite factory+deps, preserving lastDeps+cleanup (`:1230-1239` — the plan's canonical pattern) | second registration replaces first's captures; runs once at commit | overwrite-safe (pinned: committed effect observes invoke-2's capture) |
  | UseLayoutEffect | identical pattern on `FunctionLayoutEffects[LayoutEffectIndex]` | same | overwrite-safe |
  | UseRef&lt;T&gt; | Add-if-fresh, instance returned | same `Ref<T>` instance both invokes | overwrite-safe (pinned) |
  | UseContext | NO slot — appends to `ContextDependencies` | list CLEARED by the per-pass prep before EACH invoke ⇒ rebuilt, never doubled (this is exactly why the prep must re-run) | safe via prep |
  | UseSignal | `SignalSubscriptionState` object reused; subscribe only on signal-instance change | invoke 2: same signal ⇒ no re-subscribe; selector overload re-evaluates `lastValue` (idempotent). Per-render new-signal anti-pattern: dispose-then-resubscribe per invoke, no leak, no double-subscription | overwrite-safe |
  | UseSfx | `(mixer, action)` tuple, rebuilt only on mixer change | same delegate returned | overwrite-safe |
  | UseAnimate / UseTweenFloat | one passive slot (written by the EFFECT, not render) + delegation to UseEffect | render pass touches nothing side-effectful; effect overwrite rules apply | overwrite-safe |
  | UseSafeArea / UseStableFunc / UseStableAction / UseStableCallback / element `UseRef()` | metadata-gated: on the pure fiber path (`FunctionComponentState.Owner` is ctor-null, get-only) they early-return WITHOUT consuming a slot | slot-neutral, invoke-symmetric | inert on fiber path (pre-existing) |
  | ProvideContext | writes `fiber.ProvidedContext[key]`; `PropagateContextChange` compares vs the COMMITTED alternate | invoke 2 recomputes the same verdict and re-marks the same flags (bool sets, idempotent); double tree-walk cost only | safe (idempotent) |
  | RecordHook (order validation) | signature list uses overwrite-or-append (`Count > index`) while unprimed | invoke 2 overwrites the same signature slots; priming fires once, post-invoke-2. NOTE: metadata-gated ⇒ inert on the pure fiber path (Owner null, pre-existing — validation lives on the legacy/metadata path) | safe |
  | WarnStrict (diagnostics) | `StrictDiagnosticsKeys` HashSet dedup | key added on invoke 1 blocks invoke 2 AND the replay pass ⇒ one warning per logical render (pinned) | safe |
- **U-02 (new rows):** window store editor gained the three typed rows in §3 order (two
  tri-state `Popup`s over a shared `TriStateOptions` — indices match `RuitkTriState` — and the
  strict_mode Toggle with the "double-invokes renders in dev; forced off in release builds"
  tooltip); the no-store "Effective values" view gained the same three via the resolvers.
- **Tests:** `RuitkSettingsJsonTests` +4 (strict-knob key spellings; JSON-store-wins incl.
  editor-context strict_mode opt-in; no-legacy-hop for the three; **force-off-in-release proof at
  resolver level** — stored `true` + `developmentContext:false` ⇒ `false`, both contexts pinned,
  no-store ⇒ `false` everywhere). New `Ugui/Tests/StrictModeTests.cs` (+meta, fresh GUID):
  strict-off baseline (render 1×); strict mount pin (**render body 2×, effect 1×, layout effect
  1×, cleanup 1× on unmount, memo/imperative factories 1×, one Ref instance, committed effect
  holds the SECOND invoke's capture**); strict update pin (**4 invokes for mount+setState vs 2
  strict-off, committed UI byte-identical to strict-off, effect/cleanup/memo counts equal**);
  state-update-during-render warning **once** under the `[Hooks][Strict]` prefix
  (LogAssert.Expect + NoUnexpectedReceived; 4 invokes = mount 2 + deferred-replay 2 — the replay
  machinery is the M3-pinned `:901` path); compiled-default-off pin. The kitchen-sink component
  exercises every fiber-path-live family (state/reducer/memo/callback/ref/context/deferred/
  transition/imperative/signal/animate/layout-effect/2×effect). `UguiStressChurnTests` +1:
  **strict ON** function-component churn over the full box field (typed `CycleProps`, structural
  equality) — structure + status text coherent every cycle AND the pooled host-reuse bound
  (≤ BoxCount+50) holds with the double-invoke discarding a full rented tree per pass; TearDown
  now restores `StrictModeEnabled`.
- **Behavior notes (contract-mandated, recorded honestly):** (1) `strict_diagnostics` default
  `auto` = editor/dev-build ON where the compiled initializer was `false` — §0.1 row 6 orders
  this ("becomes auto, same mapping as #5"); release stays OFF (no release change). (2) At
  Verbose trace, `Hooks.cs:1241`'s UseEffect capture log fires per invoke under strict (twice) —
  §6 already documents this as accepted/truthful. (3) Under strict + scheduler,
  UseDeferredValue's deferred slot-write may be enqueued twice (identical idempotent writes) —
  audit table above.
- **Byte-equivalence re-read (strict OFF default):** single `RunRenderPass()` call ≡ the old
  inline sequence exactly (same statements, same order, post-bailout as before for the dep clear;
  the cursor resets moved from pre-bailout to pre-render — no reader in between, grep-proven);
  `StrictModeEnabled` compiled false + no-store resolver false ⇒ the second invoke is unreachable
  in an untouched project. Hooks initializers unchanged; seam writes at defaults reproduce
  today's editor values (`true`/editor-ON) — the only default-flip is release-player
  hook_validation OFF + editor strict_diagnostics ON, both §0-sanctioned.
- **Gates:** machine-paths ✓ (`add -N` for the two new files), corpus-hash ✓ (`917dd8cd…`);
  VERIFY-UNITY — Shared/Runtime/Ugui direct 0 errors (Shared warnings now 4, was 5 at M0: the
  delta is M1's deleted `exceptionControlFlow` field's CS0649, remaining 4 are the pre-existing
  `RuitkConfig.EnvVariables` CS0649s), Editor/Samples/Diagnostics/Ugui.Tests 0 errors via
  re-synced csprojs (Editor prune 2 again — host set still stale from 2026-07-30, regenerates on
  editor refocus; Ugui.Tests add 3: both M2/M3 test files + StrictModeTests.cs); player proof
  Shared/Runtime/Ugui `.Player` 0 errors (re-synthesized). No Analyzers churn (build only).

### M5 — Trace ladder restoration + diff_tracing independence (U-08/§6) — DONE 2026-07-31 (round 3 continued; same locked-editor mode)
- **§6 table executed row by row**, every gate spelled inline exactly as §6 dictates
  (`using Ruitk.Core.Diagnostics;` added to `FiberReconciler` — the file's namespace can't see
  the child namespace unqualified):
  - InsertBefore / AppendChild / no-host-parent (`FiberReconciler`) → **structural**
    (`CurrentTraceLevel != None`) — Basic RESTORED to structural events.
  - ADDED the two missing structural logs: `[Fiber] Delete {ElementType}` in `CommitDeletions`'
    loop (top-level per removed subtree, NOT per recursive child — legacy `[ReplaceNode]`
    granularity; `ElementType ?? Tag` for non-host subtree roots) and the commit-end summary
    `[Fiber] Commit #{_commitCount} effects={_effectsCommitted}` in `CommitRoot` (placed before
    `EmitMetrics()`, after the passive-effect flush).
  - Apply-typed-props / apply-props(+keys) / NO-props warning / CommitUpdate Label dump →
    **diff** (`EnableDiffTracing || == Verbose`, the exact legacy OR); the Label filter KEPT
    (re-gating, not widening).
  - The three AND-bugged adapters (`RadioButtonElementAdapter:78`,
    `RadioButtonGroupElementAdapter:130`, `ToggleElementAdapter:80`): `&& != None` → the legacy
    OR (`|| == Verbose`) — independence restored, Verbose-alone still lights them.
  - `Hooks.cs:1241` UseEffect capture log: inline `== Verbose` → `InternalLogOptions.
    EnableInternalLogs` (the file's majority style per the §6 row; meaning unchanged — the
    bridge is set from `== Verbose` at the seams). Strict-mode double-log at Verbose noted
    in-code (truthful; §6 accepted it).
  - `BaseElementAdapter:112`, Hooks detail sites, EditorRenderScheduler sites: untouched per §6.
  - **`FiberConfig.EnableFiberLogging` DELETED** (`ShowReconcilerInfo` stays — §9 owner item).
    **Acceptance: `git grep EnableFiberLogging -- "*.cs"` = ZERO hits**; remaining mentions only
    in `Plans~/` (this plan + two archived docs — allowed by the criterion).
- **Placement-log granularity verified before asserting:** every new host fiber carries
  `EffectFlags.Placement` (`FiberFactory.cs:30`, host-creation `:648`) ⇒ CommitPlacement (and
  its structural log) fires per host node on mount — the behavioral test's per-node counts are
  grounded, not assumed.
- **Tests — new `Ugui/Tests/TraceGateTests.cs` (+meta, fresh GUID):** the §6 gate-matrix truth
  table in executable form (all 6 `(trace_level × diff_tracing)` rows, structural/detail/diff
  asserted per row — pins Basic's restoration and diff independence), PLUS behavioral pins
  capturing real reconciler output via `Application.logMessageReceived`: (None,off) fully
  silent; **Basic ⇒ placements + commit summary + exactly-2 `[Fiber] Delete` on a 3→1 churn and
  ZERO diff detail**; **diff alone (trace none) ⇒ `[Fiber] Applying` present and ZERO
  structural** (the M8 smoke's independence proof, automated); **Verbose ⇒ both** (the legacy
  OR). Bughunt fix: missing `using Ruitk.Core.Fiber;` in the new test file (CS0246, caught by
  the harness).
- **Byte-equivalence at defaults (None/false):** every rewritten gate evaluates false exactly
  where `EnableFiberLogging`(=false, set by nothing) did; the two ADDED logs are
  structural-gated ⇒ silent; the adapter OR at defaults = `false || false` ≡ old
  `false && …`. Turning Verbose now lights the former EnableFiberLogging sites — previously
  unreachable dead code, which IS the restoration the contract orders.
- **Gates:** machine-paths ✓, corpus-hash ✓ (`917dd8cd…`); VERIFY-UNITY — Shared/Runtime/Ugui
  direct 0 errors, Editor/Samples/Diagnostics 0 errors (synced), Ugui.Tests 0 errors (re-synced,
  add 4: all campaign test files incl. TraceGateTests.cs); player proof Shared/Runtime/Ugui
  `.Player` 0 errors. No Analyzers churn.

### M6 — Changelog + version, under the FOLD ruling — DONE 2026-07-31 (round 4)
- **Owner ruling recorded: FOLD.** The campaign reshapes the staged-unpublished `[0.13.0]`
  in place (committed this morning, never published, no tags) — NO 0.14.0. This resolves the
  header drift question and the M1 row-15 TODO(M6); §5 row 13's "removed in 0.13.0"
  annotation is CORRECT as written (re-verified at `MigrationPage.tsx:109`).
- **`CHANGELOG.md` `[0.13.0]` reshaped in place** (header + date kept `2026-07-31`):
  - Added section evolved SO → JSON: the window + `Assets/Resources/ReactiveUIToolkit/
    config.json` create-on-demand story (no build hooks / no Preloaded Assets), parse
    semantics (missing key → default, unknown ignored, case-insensitive), the one-sentence
    interim-ScriptableObject note the milestone orders; the §0.1 canonical knob defaults
    TABLE (all 10 + Unity-only folder key, marked); a strict_mode bullet (double-invoke,
    effects once, resolver-level release force-off); a reconciler-knob API bullet
    (`FiberConfig.TimeSlicingEnabled/TimeSliceMs/StrictModeEnabled`, incl. the M3
    bypassed-commit replay); per-developer sections + diagnostics-paths bullets kept, the
    latter re-worded (settings-asset → `diagnostics_output_folder` override).
  - NEW `### Changed — the trace ladder is restored; dev diagnostics default on`: Basic =
    structural events (with the exact log names), diff_tracing independence (legacy OR, the
    three AND-bugged adapters named), and the two BEHAVIOR CHANGE callouts in the house
    loud style — hook_validation release flip; strict_diagnostics editor/dev ON + the
    `[Hooks][Strict]` prefix change.
  - config.json-demotion section evolved: resolution order now names the JSON file first;
    no-legacy-hop note for the new knobs; the loud store-defaults BEHAVIOR CHANGE note KEPT
    with its `:44`-era exception-control-flow mention reworded to "(and the now-removed
    exception-control-flow flag)"; Publish-menu bullet kept verbatim.
  - NEW `### Removed — exceptionControlFlow (the knob selected nothing)` carrying the §5
    rationale lock + the silently-ignored-legacy-key migration sentence, plus
    `FiberConfig.EnableFiberLogging` (M5 deletion, was public API).
  - Both Fixed sections (generator disk-scan, HMR PackageCache) kept verbatim. Both
    TODO(M6) mentions (`:18` window row, `:44` shipped-block note) resolved.
  - Housekeeping: the file-header mojibake (`â€”`, preamble line 7 — non-frozen) fixed;
    the same pre-existing mojibake inside old SHIPPED bodies left frozen (owner item).
- **Version:** NO numbers changed — `package.json` re-verified `0.13.0`, untouched.
- **Discord:** `[0.13.0]` entry PREPENDED to `plans/DISCORD_CHANGELOG.md` — 1962 chars
  (cap 2000, counted by script), zero non-ASCII (verified), shape matched to the 0.12.0
  entry (no trailing `---`, which the file does not actually use between entries).
- **Gates:** machine-paths ✓, corpus-hash ✓ (`917dd8cd…`); `changelog.mjs verify` —
  **green on the committed bytes, red only in this checkout**: the two generated
  marketplace pages are LF in the index but CRLF in the worktree (`core.autocrlf=true`)
  and verify byte-compares the worktree. Proven environmental, pre-existing, and
  campaign-independent via a detached `core.autocrlf=false` worktree at HEAD → `OK -- 2
  generated marketplace page(s) match their templates` (exit 0); extensions lane untouched
  by this campaign; CI's extensions context re-proves it on every push. (Noted while
  verifying: `changelog.mjs verify` is not in `test.yml` at all — extract-only in
  `publish.yml` — so the LF-worktree proof is the gate's green evidence.)

### M7 — Docs sweep — DONE 2026-07-31 (round 4)
- **§7 checklist closed:**
  - `UitkxConceptsPage.tsx` — the settings section rewritten wholesale (retitled
    "Environment & tracing configuration" → "Settings"): the JSON path +
    create-on-demand flow replaces the asset story (the Player-builds/Preloaded-Assets
    paragraph deleted as obsolete — Resources ships itself); parse semantics
    (missing/unknown/case-insensitive); ALL 11 §3 keys as bullets with the §0.1
    defaults + semantics (folder key marked *(Unity-only)*); the `auto` tri-state
    mapping; the trace ladder (`basic` structural / `verbose` +detail) +
    `diff_tracing` independence; `strict_mode` incl. the release force-off; the U-04
    editor-unbudgeted note on `frame_budget_ms`; and the §6 strict×verbose double-log
    note (§6 said "note it in docs" — done here). Legacy-fallback paragraph now
    spells the three-hop order + the no-legacy-hop rule for the new knobs. §5 row 12
    re-verified gone (M1 deleted it).
  - `MigrationPage.tsx:109` — row-13 "removed in 0.13.0" annotation re-verified
    correct under the FOLD ruling; untouched.
  - README "Settings" section rewritten to the JSON store + full knob list +
    create-on-demand + release-player resolution note. This also killed a stale
    "exception control flow" mention M1's DoD grep could not see (its scope was
    `*.cs`) — caught by this round's tracked-file sweep.
  - Grep-sweep finds beyond the checklist: `docs.tsx` Concepts `searchContent`
    refreshed (dead define-symbol keywords `env_dev`/`ruitk_trace_*` → the canonical
    knob keys); `HooksGuidePage.tsx` hook-configuration section + `HooksAPIPage.tsx`
    configuration block now state that `EnableHookValidation`/`EnableStrictDiagnostics`
    are overwritten at every mount from the settings file (`auto` mapping spelled;
    `EnableHookAutoRealign` marked internal/not settings-backed) — the M4 flip made
    the old "true by default in Editor" framing stale.
  - `CLAUDE.md`: zero settings-storage sentences (grep) — §7 says add only if wrong;
    nothing added. Extension lanes untouched. Window tooltips VERIFIED only (all 11
    rows present with §0.1 semantics from M3–M5; this round's scope).
- Residual sweep hits all verified legitimate: the CHANGELOG interim-SO sentence +
  Removed header (deliberate), frozen shipped changelog bodies, the asset-registry
  ScriptableObject docs (unrelated subsystem), MigrationPage's annotated historical
  key list.
- **Gates:** docs `npm run build` ✓ 0 errors — and the script runs `tsc -b` first, so
  the TSX edits are typechecked (pre-existing chunk-size warning only). Bughunt extra:
  `npm run lint` — red on 2 PRE-EXISTING errors in files this campaign never touched
  (`contexts/VersionContext.tsx` react-refresh/only-export-components, a dialog
  react-hooks/set-state-in-effect); recorded, not caused; CI's docs context runs
  build only (verified in `test.yml`), so this was never a gate — owner item.
  machine-paths ✓; corpus-hash ✓ (`917dd8cd…`).

### M8 pre-work — in-editor suite fix round (owner's first real run: 47/50) — DONE 2026-07-31
- **The owner ran `Ruitk.Ugui.Tests` in-editor** (the campaign's first real execution —
  every prior gate was compile-only): 47/50 green, 3 red, all in `StrictModeTests`:
  `StrictOff_Mount_RendersOnce_Baseline`, `StrictMode_Mount_RenderTwice_EffectsOnce_
  SlotsOverwritten`, `StrictMode_Update_RendersTwicePerPass_CommittedUiMatchesStrictOff`.
  The two passing strict tests (`CompiledDefault`, `StateUpdateDuringRender` — which
  pins renderCount==4 ABSOLUTE and passed) isolate the failure to the kitchen-sink
  component's hook set, not the double-invoke machinery.
- **Root cause (PRODUCT BUG, pre-existing since 0.5.22, Unity-only):**
  `Hooks.UseTransition` (`Shared/Core/Hooks.cs:1057`) did `state.HookIndex++` WITHOUT
  materializing a slot in `HookStates` — the M4 audit's "pure cursor bump, trivially
  safe" verdict was wrong: it is safe only when no slot-backed hook follows. Kitchen-sink
  slot walk: state(0) reducer(1) memo(2) callback(3) ref(4) context(slotless)
  deferred(5) → transition bumps 6→7 with Count 6 → `UseImperativeHandle`'s
  Add-if-fresh appends ONE element (Count 7) then reads `HookStates[7]` →
  `ArgumentOutOfRangeException` on the FIRST render, strict on or off. All three red
  tests mount the kitchen-sink → identical crash; NUnit captures the exception into
  the result (nothing in Editor.log — verified: the log holds both suite runs and the
  StrictModeTests block emitted only the expected `[Hooks][Strict]` warning). Latent
  because no fiber-path caller ever put another slot hook after `UseTransition` —
  the M4 kitchen-sink is the first. The in-editor gate caught a real product bug.
- **Fix:** `UseTransition` now seeds a null placeholder via the same Add-if-fresh
  pattern as every other slot hook (cursor and `HookStates.Count` stay in lockstep;
  strict double-invoke reuses the slot). Re-simulated all five `StrictModeTests`
  against the fixed slot walk: every assertion (render counts, effect/memo/imperative
  factory counts, captured-render, ref identity, committed text, cleanups) checks out.
- **Bughunt (siblings of the mistake):** every `HookIndex++` site in the repo audited —
  all others materialize their slot first (`UseSfx`/`UseTweenFloat`/`UseAnimate` seed
  null placeholders; the rest Add-or-write). `FiberFunctionComponent.cs:140` is the
  only other cursor write (the per-pass reset). Sibling legs verified clean read-only:
  Godot `hooks.gd useTransition` appends `{ "kind": "transition" }`; Unreal
  `RuitkContext.h:372` emplaces `FRuitkTransitionCell`. Unity was the odd one out.
- **Verification (editor LOCKED throughout — lockfile + 3 processes, no Unity
  launches):** VERIFY-UNITY — Shared 0 errors (4 pre-existing CS0649 warnings,
  unchanged floor), Runtime/Ugui/Editor/Samples/Diagnostics/Ugui.Tests all 0 errors;
  player proof re-synthesized (`Ruitk.Shared` sans `UNITY_EDITOR` defines/refs) 0
  errors; machine-paths ✓; corpus-hash ✓ (`917dd8cd…`). Headless 50/50 rerun still
  owed — blocked on the editor; the owner's in-editor rerun is the M8 confirmation.
- **Changelog:** new `### Fixed — UseTransition crashed any component that called
  another hook after it` folded into the staged `[0.13.0]` (FOLD ruling; no version
  bump).
- **Deferred cleanups closed (separate commit):** (a) the M6-recorded owner item —
  mojibake inside old SHIPPED changelog bodies — repaired mechanically (447
  UTF-8-read-as-cp1252 sequences across 379 lines; reverse transform only where the
  depicted cp1252 bytes form valid UTF-8, so correct text is untouchable by
  construction; 379 insertions == 379 deletions). (b) The two M7-recorded
  pre-existing docs lint errors: `VersionContext.tsx` now exports only the provider
  (context + `useSelectedVersion` moved to `contexts/useSelectedVersion.ts`, 6
  importers repointed) and `SearchModal.tsx`'s selection-reset effect replaced with
  the render-time adjust-state pattern. `npm run lint` clean; `npm run build` green.

### Cross-leg conformance (2026-07-31) — fix round (editor still LOCKED: host lockfile present; compile harness only)
- **Defaults verified identical ×10×3:** all ten family-canonical knobs carry the
  same default on all three legs (Unity JSON store, Godot Project Settings bridge,
  Unreal engine-native settings) — the conformance pass's table check, no deltas.
- **Blessed items touching this leg (supervisor rulings — reference/engine-native,
  NOT divergences):** JSON snake_case key naming (each leg spells the key names
  engine-natively — contract §1); the `auto`/`on`/`off` tri-state vocabulary;
  B8 editor-scheduler-unbudgeted (D-5 re-affirmed); the budgeted
  PumpNow-with-stale-comment path is reference behavior; the live-list
  batched-effects `foreach` is reference behavior. Strict-prefix blessing:
  `[Hooks][Strict]` here vs `[Ruitk][strict]` on Unreal — engine-native spelling
  per contract §1.
- **C2 (ruled: fix) — Basic trace set gains the missing Replace event.** The fiber
  reconciler had folded node replacement into the `[Fiber] Delete` line (the
  "legacy [ReplaceNode] granularity" comment in `CommitDeletions`). A distinct
  structural `[Fiber] Replace {old} -> {new}` now fires at the replacement
  DECISION sites, gated `!= TraceLevel.None` (§6 structural), matching the family
  Basic set (placements/deletions/replacements/commit summary; sibling shapes:
  Godot `reconciler.gd` `[Fiber] Replace`, Unreal `RuitkReconciler.cpp`
  `[Ruitk][trace] Replace`): `FiberChildReconciliation.TraceReplace` (new
  internal helper) called from the keyed same-key type change, the by-index
  occupied-slot type change, and `FiberFunctionComponent.ReconcileSingleChild`'s
  component-root type change. The torn-down subtree still logs its own Delete at
  commit — same additive shape as the siblings. Reconciler ALGORITHM untouched
  (DO-NOT #3): trace emission only. TraceGateTests extended: keyed + unkeyed
  Basic pins (exactly one Replace + the Delete), None stays silent across a
  replacement render, diff_tracing-alone shows NO Replace (structural, not
  diff), Verbose includes it; gate matrix untouched (it enumerates gates, not
  events). Staged-0.13.0 texts updated where they enumerate the basic set:
  CHANGELOG knob row + trace-ladder bullet, `DISCORD_CHANGELOG.md`, docs
  Concepts page `trace_level` bullet.
- **C8 (ruled: fix) — changelog wording.** The knob-table intro no longer claims
  "same names": now "same semantics and defaults on every Reactive UI Toolkit
  leg (each spells the key names engine-natively)"; the Discord entry's
  equivalent sentence softened the same way.
- **Gates (locked-editor):** VERIFY-UNITY — Shared/Runtime/Ugui direct 0 errors
  (Shared's pre-existing 4-warning floor unchanged), Editor/Samples/Diagnostics/
  Ugui.Tests 0 errors via the synced csprojs (file set unchanged — no re-sync
  needed); player proof Shared/Runtime/Ugui `.Player` 0 errors; machine-paths ✓;
  corpus-hash ✓ (`917dd8cd…`); docs `npm run build` ✓ (pre-existing chunk-size
  warning only). The owner's in-editor rerun expectation is unchanged: 50/50.
