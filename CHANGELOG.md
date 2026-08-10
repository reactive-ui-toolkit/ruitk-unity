# Changelog

All notable changes to the Reactive UI Toolkit — Unity package are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/).

For IDE extension changelogs (VS Code, Visual Studio 2022), see
`ide-extensions~/changelog.json` — the single source of truth for extension releases.

## [0.16.0] - 2026-08-10

### Removed — the legacy wrapper grammar (BREAKING)

The `component` / `hook` / `module` wrapper keywords and the `@component` / `@props` /
`@key` / `@inject` directive-header form — deprecated since 0.9.0 — are removed. A file
that still uses them fails to compile with **UITKX2320** (now an Error), whose message
names the fix. **Upgrade order: codemod first, then bump the package** — the codemod runs
from the repository against your project regardless of the installed package version:

```bash
node scripts/migrate-uitkx.mjs <YourProject>/Assets --es-modules          # rewrite
node scripts/migrate-uitkx.mjs <YourProject>/Assets --es-modules --check  # dry run / CI gate
```

With the grammar gone, the mechanisms built on it are gone too: the companion
partial-class merge (a companion file is an ordinary module reached via `import`), the
folder-keyed namespace mode (every file is file-keyed; `@namespace` stays as the interop
escape hatch), and the blanket peer-hook-container injection in HMR and the LSP
(cross-file scope is import-driven everywhere). Diagnostics **UITKX2107** (companion
merge), **UITKX2108** (mixed styles), **UITKX2109** (migrate-target-first import gate)
and **UITKX0211** (const in module body) retired with their subjects; the IDs stay
reserved. `@using` keeps parsing indefinitely — the unified `import "@Ns"` spelling
remains the recommended form.

### Added — generic hooks as first-class plain declarations

`export (T value, Action<T> set) useSel<T>(...) { ... }` — generic declaration heads
parse, emit, hot-reload (the existing `MethodInfo`-cache trampoline), bridge across
aliased imports, format, and highlight. The codemod migrates legacy generic hooks
instead of skipping them; where-constrained generics are rejected with a message (move
the constrained helper to ambient C#, like type definitions — the one shape the plain
dialect deliberately does not express).

### Added — codemod hardening for the removal wave

Companion auto-export (dotted-stem files export their members so the parent's imports
resolve), a namespace-move ledger (`--report` records every rewrite and every generated
namespace that moved), a strict CLI (unknown flags are fatal, `--help`, distinct exit
codes, BOM + line-ending round-trip), and the `scripts/migrate-uitkx.mjs` wrapper that
resolves `dotnet` via `$RUITK_DOTNET` → `.ruitk-local.json` → PATH. The legacy grammar
survives in exactly one quarantined entry point (`ParseLegacyForMigration`) that only
the codemod uses — everything else parses legacy files straight to the 2320 error.

### Fixed — package-embedded `.uitkx` edits recompile their owning assembly

Editing a `.uitkx` inside an embedded (or local `file:`) package changed nothing in
Unity: the change-watcher's ancestor-asmdef walk covered `Assets/` only, classified
every package-resident file as Assembly-CSharp, and dirtied the wrong assembly — the
package assembly kept running stale generated code with zero warning (GEN-1, the
2026-08-04 "wrap hunt" false trail). The walk now resolves `Packages/…` paths through
`PackageInfo` (embedded and local layouts map to their real location; immutable
packages are excluded — read-only content cannot change and the cache is never written
to) and drops the recompile trigger into the owning package folder. Remaining
`Assets/`-only siblings (asset-registry scan, csproj AdditionalFiles injection) are
tracked as GEN-2/GEN-3 in `Plans~/REMAINING_WORK.md`.

### Fixed — the formatter's long-head wrapping, restored

Plain-declaration heads longer than the print width wrap again (parameters one per
line; tuple returns wrap ahead of the name) — the legacy formatter had this contract
and the plain path silently lost it. 59 sample files reformatted to the restored canon.

### Changed — internals and record-keeping

The dead legacy emission machinery is deleted wholesale: the SG's ModuleEmitter/peer
module tables and every legacy pipeline branch behind the new early gate; HMR's legacy
container emitter, `EmitModules`, the companion→parent save redirect (every file
compiles itself; parents recompile via the import fan-out), the workspace hook-container
registry, and the static-readonly stripper; the LSP's legacy document generators,
formatter paths, and reference/rename keyword regexes (retaught the modern heads, with
generic-call tolerance). The mirrored family corpus modernized its 16
wrapper-scaffolded cases and re-froze the family hash — the cross-repo match diverges
by design until the Unreal/Godot legs run their own removal waves (staged reference
patch + trigger recorded in `Plans~/REMAINING_WORK.md`).

Docs: full removal follow-through (Reference, Diagnostics, Companion Files, Imports,
FAQ, Getting Started, HMR), a dedicated **Migrating to 0.16** page, and the previously
missing **Mounting & Roots** page (all three `RootRenderer.Initialize` hosts including
the Unity 6.5 `PanelRenderer` path, `env` seeding, editor mounting).

Samples: the game samples' style companions moved from ambient `.cs` static classes to
`.style.uitkx` modules (11 files, 50 style exports across Galaga, Mario, Snake) — each
consumer star-imports the module under the old class name, so every call site is
unchanged; the one non-style member (`GalagaScreen`) moved to `GalagaTypes.cs`. The
Galaga simulation followed (`GameLogic.cs` → a `GameLogic.uitkx` module of 2 exported +
9 internal utils, the DoomGame pattern; the consumer star-imports it as `Tick`, call
sites unchanged). What deliberately stays ambient C#: type definitions
(`GalagaTypes.cs`, `MarioGameTypes.cs`, `SpriteAtlas.cs`'s `SpriteFrame`) — the one
shape the plain dialect does not express — plus their `const` tables and
`ref`-parameter helpers, which demonstrate the intended interop split.

Tests: SG suite 1844, LSP suite 164, shared fiber suite unchanged; family corpus hash
gate green on the re-frozen value.

## [0.15.0] - 2026-08-02

### Added — Unity 6.5 `PanelRenderer` host

`RootRenderer.Initialize(PanelRenderer)` (gated `UNITY_6000_5_OR_NEWER`) hosts the component
Unity's manual points new 6.5 projects at; the `UIDocument` path is untouched and not deprecated.
The mount is callback-driven (`PanelRenderer`'s root is internal), renders issued before the panel
exists are held and replayed automatically, and the fiber tree mounts into a library-owned sub-root
(`__ruitk_root`) so Unity's per-frame root rewrites and nested-renderer front-insertion never
collide with the mounted UI. `V.Host` props land on the sub-root. World-space configuration
(`worldSpaceSizeMode`, `worldSpaceSize`, `position`, `pivot`, `pivotReferenceSize`) stays on the
`PanelRenderer` component, where Unity owns it.

Panel rebuilds dispatch three ways on the sub-root's own state (never the version counter): still
parented → **reuse in place** (also the double-fire guard); orphaned but alive → **retarget**, all
hook/ref/animation state preserved; released (`resourcesReleased`) → **remount** — the old tree is
dropped without touching it via the new `FiberReconciler.AbandonRoot` (effect cleanups and signal
disposals still run; a released element is never mutated), and the last UI replays fresh.

### Added — workarounds for open Unity 6000.5.x `PanelRenderer` issues (all symptom-gated)

- **Mount watchdog** (case **IN-150082**, editor-only; **UUM-147875**, fixed upstream in
  6000.5.7f1): an enabled, configured renderer that never delivers its UI reload callback gets the
  attach path forced — a `panelSettings` round-trip escalating to `PerformUpdate` /
  `PerformValidation(true)` / a component toggle, judged by outcome. Opt-out `mount_watchdog`.
- **Nested-release prevention** (**UUM-148452**, open; active in editor AND players): nested child
  renderers are disabled around rebuild levers the library itself pulls, so the parent's release
  cascade cannot poison them. Opt-out `nested_prevention`.
- **Nested repair** (**UUM-148452**): a nested renderer whose tree Unity released with no
  follow-up callback destroys + re-adds its own `PanelRenderer` with every setting copied
  (Undo-wrapped in edit mode). Opt-out `nested_repair`.

Each site carries a greppable `WORKAROUND(...)` header, an entry in
`Plans~/REMAINING_WORK.md`, and a section on the docs site's Known Issues page; all three
opt-outs live in `config.json` and the settings window (default on; symptom gating makes them
inert on fixed editors). One-time mount warnings fire for `visualTreeAsset != null` (saving that
`.uxml` remounts and drops transient state) and `parentUI != null` (nested limitations).

### Added — `FiberHostConfig.IsAlive` + retention-site cleanup

`bool IsAlive(object)` is the new liveness primitive (UITK: `!resourcesReleased` under 6.5;
uGUI: the destroyed-`GameObject` fake-null check). The retention sites that would have held
poisoned references — several of them pre-existing leaks on the `UIDocument` path — are fixed
outright: user refs now detach on unmount (parity with uGUI), `Animator` stops tick lambdas when
their element leaves the tree (and self-terminates on released elements), the four virtualized
list/tree row pools unmount their pooled row renderers on host removal (their effect cleanups
never ran before), `VNodeHostRenderer.RetargetHost` no longer leaks one style-tracking entry per
retarget, and `PropsApplier.EvictReleasedElements()` sweeps wholesale-released subtrees.

### Added — the A3 root-source seam, one bootstrap, one HMR registry

`IRootSource` (internal; public entry points per host) abstracts where a mount's root comes from:
`StaticRootSource`, `UIDocumentRootSource` (the editor poll moved out of `RootRenderer`,
behaviour-neutral) and `PanelRendererRootSource`. `RuitkBootstrap` replaces the three
near-identical bootstrap copies in `RootRenderer` / `UguiRootRenderer` /
`EditorRootRendererUtility`. `MountRegistry` (editor-only) replaces both hard-coded HMR host
lists: every `FiberRenderer` self-registers, so ALL hosts — including pooled row renderers, which
were previously invisible to HMR — are hot-reload-reachable with no per-host list.
`VNodeHostRenderer.RetargetHost` widened to public + `object`-typed.

### Added — CI test coverage for the fiber core

`SharedTests~` now links and tests the reconciler and renderer through a mock host backend
(76 tests): mount order, Text→Label contract, fragment flattening, reuse-in-place,
different-type remount, keyed reorders without recreation, conditional children, `UseState`
synchronous re-render + same-value bailout, `UseEffect` setup/cleanup + dependency gating,
`UseRef` persistence, `UnmountRoot` cleanup, the retarget contract (children move, updates follow,
hook state survives), signals (registry semantics, subscribe/dispose, throwing-subscriber
isolation, `UseSignal` re-render + selector slicing + unmount unsubscription), and the `IsAlive`
seam. Enabled by splitting the UITK-flavoured halves of `Hooks`, `V`, `HostContext`,
`FiberRenderer` and `IElementAdapter` into `*.Uitk.cs` / `V.Core.cs` partials — public surface
unchanged.

### Added — five `PanelRenderer` samples (simple → complex)

Hello PanelRenderer (deferred mount), a diegetic world-space panel, nested renderers (the live
workaround regression), mixed `UIDocument` + `PanelRenderer` hosts sharing a signal, and a ship
dashboard (router + signals + `ListView`). The `.uitkx` components join the samples corpus gate —
the SG suite grew 1828 → 1840.

### Fixed — Unity 6.5 upgrade fallout (found opening a real 6.3 project on 6.5)

- **uGUI backend failed to compile on 6.5**: `GetInstanceID()` became an error-level obsolete
  (CS0619, `GetEntityId` replaces it) — and `EntityId`'s implicit `int` conversion is error-level
  obsolete too. `UguiRectApplier` and the stress tests now call `GetEntityId()` under
  `UNITY_6000_5_OR_NEWER` and key their dedup sets by the version's native id type; the floor
  keeps `GetInstanceID` and `int`. This lived outside `unity-compile-check`'s reach — the uGUI
  package assemblies aren't in Unity's `Managed/` dir.
- **`UAC1001` serialization-analyzer warnings** (new in 6.5): `BenchEnvOverrides` carried a
  leftover `[Serializable]` while being a purely in-memory overrides carrier whose nullable
  fields Unity's serializer cannot represent (the resolved values land on `BenchEnv`, which is
  what reaches disk). The false attribute is removed and the contract documented.
- **Endless `DirectoryNotFoundException` spam from the Package Manager** in development/embedded
  checkouts: `package.json` declares the samples at `Samples~` (the shipped layout — the release
  pipeline renames `Samples/` on publish), and 6.5's Package Manager now computes sample sizes on
  every package event, throwing forever when the path is missing. A placeholder `Samples~/README.md`
  makes the path resolve in dev; both release paths delete it before moving the real demos in.

### Fixed — the sub-root swallowed clicks under layered panels

The library-owned sub-root stretches over the whole panel and had default picking, so any UI
beneath a `PanelRenderer` panel (a `UIDocument` overlay in the same scene, for instance) was
unclickable. The sub-root is structural and now has `pickingMode: Ignore` — content inside it
still picks, and `V.Host` props can override.

### Fixed — multi-mount scenes self-deleted

A second `RootRenderer` in a scene had its **whole GameObject destroyed** by a legacy singleton
guard in `Awake()` (`.Instance` has zero consumers). The first renderer still claims the legacy
`Instance` slot; additional renderers are now independent mounts.

### Notes

Verification: SharedTests~ 76/76, SG 1840/1840, `unity-compile-check` green at the floor AND with
the 6.4+6.5 gates on (the whole callback/watchdog/repair surface compiles against the real
6000.5.6f1 assemblies), docs build green. IDE extensions are unchanged (no `.uitkx` schema
change). The workaround registry's single authority is `Plans~/archive/UNITY_6_5_SUPPORT_PLAN.md` §5.9,
including the removal procedure.

## [0.14.1] - 2026-08-02

### Fixed — `RouterPath.ParseQuery` dropped the leading `?`

- **`RouterPath.ParseQuery("?a=1")` returned a dictionary keyed `"?a"`, not `"a"`.** Any
  caller passing a conventional query string — the web idiom — got a dictionary in which
  the first parameter was silently unreachable. The router's own `Parse()` strips the `?`
  before calling in, which is why it went unnoticed, but `ParseQuery` is public API.
  It now accepts both `"?a=1"` and `"a=1"`, and `"?"` alone yields an empty result.
  Found by the first automated test ever run against this code (see below).

### Added — CI coverage for `Shared/`, which previously had none

- **`SharedTests~` — a `net10.0` suite that links the host-agnostic parts of `Shared/Core`
  and runs them in CI.** `Shared/` is Unity C# and was otherwise only ever compiled by the
  Editor, so the router and signals runtime had no automated coverage at all. 39 tests
  covering path normalisation, `Combine`/`StripBasename`/`WithBasename` round-tripping,
  query parse/build, and the `MemoryHistory` back/forward stack including forward-entry
  truncation, blockers and listener lifetime.
- It deliberately does not reference Unity's assemblies. Those bind to native ECalls that
  exist only inside the Unity runtime — measured on 6000.5.6f1, `Debug.Log`,
  `Time.realtimeSinceStartup`, `GUID.Generate` and `new VisualElement()` all throw
  `SecurityException` outside the Editor. A small shim supplies what the linked sources
  touch, and captures logs so tests can assert on them.

### Added — a Unity compile check that does not need the Editor

- **`node scripts/unity-compile-check.mjs`** compiles `Shared/` and `Runtime/` against the
  **real** Unity assemblies — twice: once at the package floor with the version gates off,
  and once with `UNITY_6000_4_OR_NEWER` + `UNITY_6000_5_OR_NEWER` on. That is the manual
  "open it on the floor, then open it on 6.5" step from the release checklist, automated,
  and it catches a wrong `UnityEngine` API in an element adapter — the one class of error
  the shimmed suite above cannot see.
- Compile only; it never executes, for the ECall reason above. Unity is **discovered**, never
  hardcoded: `$RUITK_UNITY_EDITOR` → `.ruitk-local.json` `"unityEditor"` → the standard Hub
  roots. Wired into CI with `--allow-missing`, where it skips loudly rather than pretending
  to pass, since GitHub runners carry no licensed Editor.

### Changed — `IRouterHistory.Listen` now documents its contract

- Implementations must invoke the listener once with the current location before returning.
  `MemoryHistory` always did; nothing said so, so an alternative implementation could
  reasonably have omitted it and broken every subscriber that relies on being seeded.

## [0.14.0] - 2026-08-01

### Added — Unity 6.5 and 6.4 controls: `MaskField`, `Mask64Field`, `GUIDField`

- **`V.MaskField` / `<MaskField>` (Unity 6.5+)** — a multi-select bitmask dropdown
  backed by an `int`. `Choices` supplies the option labels; the optional
  `ChoicesMasks` overrides the default `1 << i` per entry so a single choice can
  stand for a composite of several bits. These controls existed before 6.5, but
  only in `UnityEditor.UIElements` as editor-only; Unity 6.5 moved them into the
  runtime module, which is what makes them usable here.
- **`V.Mask64Field` / `<Mask64Field>` (Unity 6.5+)** — the 64-bit sibling, backed
  by a `ulong` with `List<ulong>` masks, for when 32 flags are not enough. In
  markup a mask literal may parse as `int` or `long`; those are widened rather
  than rejected.
- **`V.GUIDField` / `<GUIDField>` (Unity 6.4+)** — a text field for a
  `UnityEngine.GUID`. Despite the name this is a **runtime** control — the value
  type is `UnityEngine.GUID`, not `UnityEditor.GUID` — so it works in player
  builds. Accepts a `GUID` or a dashed/undashed hex string, matching how UXML
  round-trips it, and inherits `ReadOnly`, `IsDelayed`, `MaxLength` and
  `SelectAllOnFocus` from the text-input base.

> **Masks: `Everything` is `~0`, not `(1 << n) - 1`.** The dropdown always prefixes
> two synthetic entries — `Nothing` (`0`) and `Everything` (`~0`, i.e. `-1`). Those
> are *different values* from "every bit currently defined", so the props diff never
> normalises one into the other. If your own code persists or compares a mask, keep
> that distinction too, or `Everything` silently degrades into whatever bits happened
> to exist at the time.

### Added — version-aware editor support for elements

- The UITKX schema entries for the three controls carry the first **`sinceUnity`**
  annotations on *elements* (previously only style properties had them). Completion
  and diagnostics are therefore version-aware for tags as well: on a project
  targeting an older Unity the new tags are reported rather than silently offered.
  No LSP change was needed — that path already existed and is now exercised.
- The documentation site's version dropdown gains **6.4** and **6.5**, so there is
  no longer a hole between the floor and the newest supported release.

### Added — documentation

- Component pages for all three controls, with the mask sentinel rule called out.
- A **Known Issues** section for Unity 6.5 making the **Advanced Text Generator**
  the default: Unity claims feature parity but not *measurement* parity, so text
  sizes and wrap points can shift. Documents the per-subtree opt-out
  (`-unity-text-generator: standard`, already supported in the typed `Style`
  surface), plus the static-font-asset and rich-text breaks.

### Fixed — internal tooling

- `automation~/unity-api-diff.ps1` keyed its element/enum/struct collections by
  simple type name, so a pre-existing **nested** type masked a **new top-level**
  type of the same name and the addition was dropped from the diff entirely.
  Unity 6.5's top-level `WorldSpaceSizeMode` went missing this way. Nested types
  are now qualified `Outer.Nested`; top-level types keep their simple name, which
  `apply-diff-to-schema.mjs` requires.

### Compatibility

- The package floor is unchanged at **6000.2**. Everything above is behind
  `UNITY_6000_4_OR_NEWER` / `UNITY_6000_5_OR_NEWER`, which Unity defines
  automatically — no asmdef `versionDefines` needed — and compiles out entirely
  below the gate. Existing projects are unaffected.

## [0.13.0] - 2026-07-31

### Added — unified settings: one window, one JSON file, the family knob set

- **A unified settings window** (*Reactive UI Toolkit ▸ Settings*): one scrollable
  window holding every setting, in labeled sections. **Configuration** is
  project-scoped and lives in a plain JSON file,
  `Assets/Resources/ReactiveUIToolkit/config.json` — the section is read-only
  (showing the values in effect) until you click **Create settings file**;
  nothing is written to your project until you do. Under `Resources/` the file
  ships into every player build by itself — no build hooks, no Preloaded
  Assets — and loads synchronously on every platform. Missing file or key →
  compiled default; unknown keys are ignored; enum values are lowercase
  strings, parsed case-insensitively. (An interim `RuitkSettings`
  ScriptableObject store existed only in unpublished staging of this release
  and was replaced by the JSON file before shipping — nothing to migrate.)
- **The family-canonical knob set** — same semantics and defaults on every
  Reactive UI Toolkit leg (each spells the key names engine-natively); every
  default reproduces the previous behavior exactly, so an untouched project
  behaves as before:

  | Key | Default | Meaning |
  |---|---|---|
  | `environment` | `auto` | `auto` → `development` in the editor and development builds, `production` otherwise; exposed as `HostContext.Environment["env"]` |
  | `time_slicing` | `true` | `false` is an explicit scheduler bypass — every render runs the synchronous work loop even when a scheduler is installed |
  | `time_slice_ms` | `2.0` | render-phase budget per slice (previously a compiled-in constant); applies wherever a scheduler slices, the editor included |
  | `frame_budget_ms` | `4.0` | per-frame budget of the play-mode/player `RenderScheduler`; editor mounts use the editor scheduler, which is unbudgeted by design |
  | `host_node_pool` | `true` | gates the uGUI host-element pool (only adapters that provably reset, via `TryResetForPool`, ever pool); off destroys instead. The UI Toolkit path is never pooled |
  | `hook_validation` | `auto` | hook order/count validation; `auto` = on in the editor and development builds, off in release players |
  | `strict_diagnostics` | `auto` | development warnings for suspect hook usage (state updates during render, missing dependency arrays), deduplicated; `auto` = on in the editor and development builds, off in release |
  | `strict_mode` | `false` | double-invokes render functions so impure render bodies surface; **forced off in release builds** |
  | `trace_level` | `none` | `basic` = structural reconciler events (placements, deletions, replacements, a commit summary); `verbose` = structural + per-element/per-hook detail |
  | `diff_tracing` | `false` | detailed Fiber diff logs, **independent** of `trace_level` |
  | `diagnostics_output_folder` | `""` | **(Unity-only)** where benchmark results and log captures are written |

- **`strict_mode`** — the React StrictMode idea: render functions run twice, the
  first result is discarded, the second is reconciled. Effects, layout effects,
  memo/callback factories, and cleanups still run once, and the committed UI is
  identical to strict-off — the double-invoke exists to make impure render
  bodies visible. Costs one extra render (plus its discarded tree) per pass;
  the release force-off happens in the resolver, so a shipped player can never
  activate it.
- **Reconciler knobs are real API now**: `FiberConfig.TimeSlicingEnabled`,
  `FiberConfig.TimeSliceMs`, and `FiberConfig.StrictModeEnabled` — public
  statics set from the resolved settings at every mount. The `time_slicing`
  bypass also covers state updates raised during a bypassed commit (replayed
  synchronously rather than stranded on a scheduler that never gets a slice).
- **Per-developer sections in the same window** (EditorPrefs): **Hot Reload
  (HMR)** — the four HMR toggles (same pref keys as before) plus the two keybind
  recorders, both moved out of the HMR window, which keeps Start/Stop, status,
  and stats and now links to the settings window instead — and **Console
  navigation**, the `.uitkx` verbose toggle, now live-reading, so it takes
  effect without a domain reload.
- **`RuitkDiagnosticsPaths.GetOutputRoot()`** — benchmark results and log captures
  now write to `<project>/Logs/ReactiveUIToolkit` in the editor and
  `<persistentDataPath>/ReactiveUIToolkit` in players (or the
  `diagnostics_output_folder` override; absolute used as-is, relative resolved
  per context), never into the package folder — which a UPM PackageCache
  install cannot write anyway. The Bench Results Viewer seeds its picker at the
  new location and falls back to the old in-package `Results/` for
  pre-existing runs.

### Changed — the trace ladder is restored; dev diagnostics default on

- **`TraceLevel.Basic` means something again — structural events.** Its legacy
  meaning was lost in the fiber rewrite: the value still parsed, but every gate
  in the codebase checked `Verbose` or `None`, so `Basic` behaved like `None`.
  `basic` now logs placements (`[Fiber] InsertBefore` / `[Fiber] AppendChild`),
  one `[Fiber] Delete` per removed subtree, one `[Fiber] Replace old -> new`
  per node-replacement decision, and a per-commit summary
  (`[Fiber] Commit #n effects=…`); `verbose` adds the per-element/per-hook
  detail it always had.
- **`diff_tracing` is independent of the trace level again.** Every diff-detail
  site (props application, the `CommitUpdate` dump, and the RadioButton /
  RadioButtonGroup / Toggle adapters) now uses the legacy OR gate —
  `diff_tracing` on **or** `trace_level` = `verbose` — so diff tracing alone
  lights them. The three adapters had AND-ed the two flags, so `diff_tracing`
  without a trace level logged nothing there.
- **BEHAVIOR CHANGE — hook validation now follows the environment.** It was
  compiled on in every build, release players included; with
  `hook_validation: auto` release players skip it (the per-render validation
  cost leaves shipped games) while the editor and development builds keep it.
- **BEHAVIOR CHANGE — strict diagnostics default on in the editor and
  development builds** (`strict_diagnostics: auto`; the compiled default was
  off, so these warnings were effectively unreachable). Their prefix is now
  `[Hooks][Strict]` — the old `[Hooks][StrictMode]` collided with the new
  `strict_mode` knob, which owns that name family-wide. Release players are
  unchanged (off).

### Changed — config.json demoted to legacy fallback

- Resolution order for every setting is now: the JSON settings file → legacy
  `Assets/ReactiveUIToolkit/config.json` (`envVariables`) → compiled defaults. A
  user-edited legacy `config.json` keeps working when no settings file exists
  (it only ever spoke `env`/`traceLevel`/`diffTracing` — the knobs new in
  0.13.0 have no legacy spelling and resolve settings file → compiled default).
- **The shipped `config.json` no longer contains an `envVariables` block** (the
  publish omit-lists stay). BEHAVIOR CHANGE for store installs: the shipped block
  used to force `development` + `Basic` tracing (and the now-removed
  exception-control-flow flag) onto every customer; everyone now starts from
  compiled defaults (`environment` `auto` → by build type, `trace_level`
  `none`, `diff_tracing` off) unless they create the settings file or carry
  their own edited `config.json`.
- The four *Reactive UI Toolkit ▸ Publish* menu items were removed — publishing is
  CI-driven (`.github/workflows/publish.yml`); the methods remain callable
  programmatically and `AssetStoreExport.Run` is unchanged.

### Removed — `exceptionControlFlow` (the knob selected nothing)

- The key selected between error-boundary strategies in the **legacy**
  reconciler; that selector died with the legacy reconciler's removal, and the
  surviving boundary behavior runs unconditionally — by 0.12.0 the flag was
  written at every mount and read by nothing. Gone from the entire config
  surface (`RuitkConfig`, the resolver, the mount seams, the settings window);
  a legacy `config.json` still carrying the key is silently ignored, which is
  the whole migration.
- `FiberConfig.EnableFiberLogging` (public static, assigned by nothing since
  the fiber rewrite) is gone too — its log sites are re-gated under
  `trace_level` and `diff_tracing` per the ladder above.

### Fixed — source generator finds package-resident `.uitkx` (UPM `file:`/git install)

- The generator's fallback disk scan (the path Unity's in-editor compile always
  takes — no `.uitkx` AdditionalTexts exist there) was `{projectRoot}/Assets`-rooted,
  so consuming this repo as a UPM `file:` package left every Samples component
  ungenerated ("Found 0 .uitkx file(s) via disk scan" → 294× CS0234 in the Samples
  assembly). The scan now adds a second root derived from the compilation's own
  syntax trees: the directory of the `.asmdef` whose `name` matches the compiling
  assembly (`UitkxPipeline.FindCompilationAsmdefRoot`). For Assets-resident layouts
  that root is a subset of the Assets scan (case-insensitively deduped), so
  discovery — and every generated namespace — stays byte-identical for existing
  customer projects; namespace derivation itself was already location-independent
  (asmdef-anchored, `uitkx.config.json` prefix). Store/`Samples~` install channels
  were never affected. Pinned by `SourceGenerator~/Tests/PackageLayoutDiscoveryTests.cs`.

### Fixed — Hot Module Reload works under UPM git/`file:` installs

- Locating the bundled `Analyzers/` directory used to probe only under `Assets/`,
  so a PackageCache install (the UPM git-URL channel) exhausted every probe and
  threw `DirectoryNotFoundException` — HMR could not start at all. Package-root
  discovery is now layout-agnostic (`RuitkPackagePaths`: `PackageInfo` →
  asset-database sentinel walk-up → legacy `Assets/ReactiveUIToolkit`), shared by
  HMR, the `.uitkx` test-runner window (which now reports a missing
  `SourceGenerator~` instead of failing — it is dev-repo content, absent from
  store installs), and the Bench Results Viewer's legacy fallback.

### Fixed — `UseTransition` crashed any component that called another hook after it

- `Hooks.UseTransition` advanced the hook cursor without materializing its slot
  in the hook-state list, leaving the cursor one past the list's end. The next
  slot-backed hook in the same component (`UseState`, `UseMemo`,
  `UseImperativeHandle`, …) then appended one element and indexed one past it —
  `ArgumentOutOfRangeException` on the component's very first render, strict
  mode on or off. Latent since the hook shipped in 0.5.22 (nothing on the fiber
  path ever called `UseTransition` followed by another slot hook); exposed by
  this release's strict-mode kitchen-sink tests, the first real execution of
  that hook order. `UseTransition` now seeds a placeholder slot exactly like
  every other slot-backed hook, keeping the cursor and the slot list in
  lockstep — components where it was the last (or only) hook are unaffected.
  The Godot and Unreal legs already materialize the slot; this was Unity-only.

## [0.12.0] - 2026-07-28

### Changed — BREAKING: the "Reactive UI Toolkit" family rebrand

The library is now **Reactive UI Toolkit — Unity**, in the `reactive-ui-toolkit`
GitHub org as `ruitk-unity`. Nothing changed functionally — this release is the
rename, nothing else. See `MIGRATION-0.12.md` for the two-step upgrade
(delete-old-folder + codemod).

- **Namespace/assembly root `ReactiveUITK` → `Ruitk`**: every `namespace`, `using`,
  and `global::` reference; all 10 asmdefs `ReactiveUITK.*` → `Ruitk.*` (the
  `ReactiveUITK.Examples.asmdef` file is now `Ruitk.Samples.asmdef`, matching the
  assembly name it always declared); analyzers renamed to `Analyzers/Ruitk.Language.dll`
  + `Analyzers/Ruitk.SourceGenerator.dll` with their GUIDs preserved (references
  survive); generator emits `global::Ruitk.*`.
- **`ReactiveUITKConfig` → `RuitkConfig`** (type + file); the hidden media-host
  object names `__ReactiveUITK_*` → `__Ruitk_*`; editor-prefs key
  `ReactiveUITK.UitkxNavVerbose` → `Ruitk.UitkxNavVerbose` (saved value resets once).
- **Define `REACTIVEUITK_HAS_TEST_FRAMEWORK` → `RUITK_HAS_TEST_FRAMEWORK`.** If you also
  carry this define in *Project Settings ▸ Player ▸ Scripting Define Symbols* or in a
  `csc.rsp`/`mcs.rsp`, rename it there too — a stale define fails **silently** (the
  guarded block simply stops compiling in, with no diagnostic).
- **BREAKING for reflection — the default generated namespace for *your* `.uitkx`
  components changed:** `ReactiveUITK.Uitkx` → `Ruitk.Uitkx`, and
  `ReactiveUITK.FunctionStyle` → `Ruitk.FunctionStyle`. Every component **without** an
  explicit `@namespace`/`namespacePrefix` moves namespace. Compile-time references are
  handled by the codemod, but assembly-qualified **strings** are not reliably fixable:
  `Type.GetType("ReactiveUITK.Uitkx.UI.HelloWorld, Assembly-CSharp")` and serialized
  type names break at **runtime**, not at compile time. Grep for `"ReactiveUITK.Uitkx`
  and `"ReactiveUITK.FunctionStyle` before shipping, or pin the old value with an
  explicit `@namespace`.
- **Install folder `Assets/ReactiveUIToolKit` → `Assets/ReactiveUIToolkit`**
  (lowercase k; Linux upgraders: delete the old capital-K folder — case-sensitive
  filesystems keep both).
- **Editor menu root `ReactiveUITK/…` → `Reactive UI Toolkit/…`** (53 items) and the
  Bench window titles follow the display name.
- **License: Reactive UI Toolkit Community License 1.1** — same terms, renamed and
  version-bumped family-wide; the credit line is now "Made with Reactive UI Toolkit".
  Licensees under 1.0 keep their 1.0 terms.
- **New codemod `SourceGenerator~/Tools/RuitkMigrateBrand`** rewrites user projects
  (usings/namespaces/global:: refs, asmdef references, menu-path strings, the define,
  install-path strings; scans `.cs`/`.uitkx`/`.asmdef`/`.asmref`/`.rsp`; idempotent,
  `--check` gate; never edits inside the package folder; preserves each file's BOM and
  refuses rather than corrupts non-UTF-8 files; frozen extension marketplace IDs are
  masked so they survive verbatim). It lives in the **repository**, not in the shipped
  package — clone `ruitk-unity` and run it against your project path. Requires the
  .NET 8 SDK (Unity ships none).
- **After migrating, delete `Assets/ReactiveUITK/`** — the generated trigger+registry
  folder, which 0.12.0 moved to `Assets/Ruitk/`. It is *not* the package folder and
  nothing cleans it up. A leftover `Resources/__uitkx_registry.asset` competes with the
  new registry (`Resources.Load` resolves it by name) and can make `Asset<T>()`/`Ast<T>()`
  and `uss=` lookups return null in editor **and** player builds; the editor now logs a
  warning when it detects one.
- Unchanged on purpose: UPM package id `com.reactiveuitoolkit`, the `.uitkx` language
  and `UITKX` tooling brand, extension marketplace identities, the docs domain, and
  `RUITK`-prefixed titles/pref keys.
- Housekeeping: the broken UPM `displayName` ("Reactive UIToolKit") is fixed; stale
  tracked `Analyzers/ReactiveUITK.SourceGenerator.dll.old` removed; wrong-org and
  placeholder repo URLs corrected everywhere.

## [0.11.0] - 2026-07-25

### Added — uGUI render backend (`@backend ugui`)

The library now renders **Unity UI (uGUI)** through the same fiber reconciler,
hooks, signals, context, router, and Fast Refresh as UI Toolkit. The design
principle: uGUI keeps its own mental model — RectTransform anchors/pivots for
positioning (with an `anchors` preset prop mirroring the Inspector widget),
sprites/colors/materials for styling, LayoutGroups for stacking. There is
deliberately no `Style`/USS surface on uGUI elements.

- **New `ReactiveUITK.Ugui` assembly** (`Ugui/`): `UguiRootRenderer` mounts a
  tree under any RectTransform in an existing Canvas; `U.*` factories and a
  per-element adapter registry cover Canvas, Panel, Image, RawImage, Text
  (TMP), Button, Horizontal/Vertical/GridLayoutGroup, Toggle(+ToggleGroup),
  Slider, Scrollbar, ScrollRect, Dropdown (TMP), InputField (TMP), and
  `<Prefab>` — the migration bridge that mounts existing uGUI prefabs with
  `IReactivePrefab` prop binding.
- **Prop groups** = declarative "Add Component": `layoutElement`,
  `contentSizeFitter`, `aspectRatioFitter`, `canvasGroup`, `mask`,
  `rectMask2D`, `shadow`, `outline`, and `pointer` (IPointer*/drag/scroll
  bridge) on any element; non-null adds/updates, null-transition removes.
- **Controlled components**: all value writes go through SetValueWithoutNotify
  / SetTextWithoutNotify (no event echo); UnityEvents are subscribed exactly
  once with delegate-field diffs. Raycast hygiene: visual elements default
  `raycastTarget=false`, interactive ones true. Editor-only hint when rect
  props are written on a RectTransform driven by a layout component.
- **`.uitkx` support**: the `@backend ugui` file directive selects the ugui
  element vocabulary across the source generator, HMR emitter, schema, and
  IDE tooling (UITKX2111 invalid/duplicate backend, UITKX2112 `@uss` in a
  ugui file). UI Toolkit emission is byte-identical when the directive is
  absent.
- **Cross-backend islands (v2)**: `UguiHost` embeds a uGUI subtree inside a
  UI Toolkit tree (screen-synced overlay canvas, native input); `UitkHost`
  embeds a UI Toolkit panel inside a uGUI tree (PanelSettings render texture
  + forwarded pointer input).
- **Host-agnostic fiber core**: host operations now route through an abstract
  `FiberHostConfig` with opaque host handles, and typed props generalize
  through `HostPropsBase` — no behavior change for UI Toolkit mounts
  (all 1726 pre-existing generator pins unchanged).

- **Complete Inspector prop coverage**: Selectable animation triggers,
  toggle transition, TMP gradients/outline/style sheets/sprite assets,
  dropdown option icons + arrow/template/item styling, input-field
  inputType/keyboardType/characterValidation/caret control +
  onSelect/onDeselect, canvas sorting layer/target display/shader channels,
  full 3D rotation + localPosition.z + GameObject tag, image alpha hit
  test, scrollbar spacing.
- **GameObject pooling**: per-adapter pool for stateless visuals (Panel,
  Image, RawImage, Text), gated on a pristine-state reset — stateful
  controls are never pooled, so reuse cannot leak toggle/input state.
- **Menu-identical visuals everywhere**: a shipped resources asset
  references Unity's builtin UI sprites, so runtime-created controls look
  like the GameObject > UI menu ones in the editor AND player builds.
- **Backend-aware IDE intelligence**: completions, hover, diagnostics, and
  virtual-document props resolution follow `@backend`; `UITKX2113` flags
  cross-backend imports at compile time (both directions); an 18-tag
  vocabulary contract test pins the ugui tag surface. Docs site gains the
  "uGUI Backend" page.

### Added — null-only components (React case 2)

A component may now end in an explicit top-level `return null;` with no
markup return — the React shape for a component that always renders
nothing (effect-only components: register something, play a sound,
portal-less side effects). The whole body emits as setup code with full
hook support and the runtime renders nothing. Diagnostics stay strict: a
body with no top-level return at all is still UITKX2101 (its message now
mentions the null form), and `return expr;` is still UITKX2102. Uniform
across the source generator, HMR emitter, formatter (no synthesized
markup return on format), IDE virtual document, and both element
vocabularies (uitk and `@backend ugui`).

### Fixed — fiber core hardening (found by uGUI field testing, benefits both backends)

- **Render-restart starvation**: state updates arriving while a large render
  pass was in flight restarted the pass from scratch, forever, under
  sustained update pressure — the tree never committed. In-flight passes now
  finish and the queued updates replay after commit.
- **Keyed reorders now physically move hosts**: reusing a keyed child never
  moved its host element, so reorders silently rendered in stale order.
  React-style lastPlacedIndex tracking plus a move branch in CommitPlacement,
  with DOM-semantics same-parent InsertBefore in both host configs.
- **Deferred updates on a swapped root**: updates deferred across a commit
  could target the stale alternate tree and die silently (frozen subtrees);
  they now redirect to the live fiber and re-mark the parent path.
- **Stress-sample Restart**: the auto-stop was level-triggered on stale
  `finished` state and re-cancelled a restart one render later; now
  edge-triggered on the finish transition (UITK and uGUI samples alike).
- **Internal diagnostics logs off by default**:
  `InternalLogOptions.EnableInternalLogs` defaulted to true and only the
  UI Toolkit mount path overwrote it from the trace level, so uGUI mounts
  logged every state set. The default is now false and `UguiRootRenderer`
  mirrors `RootRenderer`'s diagnostics wiring (verbose trace re-enables).
- **uGUI test assembly gated on the Test Framework package**: `Ugui/Tests`
  is the package's first Unity-visible test asmdef, and `UNITY_INCLUDE_TESTS`
  is defined in every editor compile — so any project WITHOUT
  `com.unity.test-framework` (including the Asset Store export shell, which
  failed the 0.11.0 store build with a CS0246 NUnit storm) tried to compile
  the tests without NUnit. Two layers: the asmdef now adds a
  `versionDefines` gate so the assembly compiles only when the Test
  Framework package is present, and `Ugui/Tests` joins
  `pathsToOmitFromStore` so the Asset Store `.unitypackage` ships with no
  test files at all (the dist/git channels keep them, inert without the
  package).

### Changed — performance & API

- **Typed component cache on the uGUI diff path**: adapters cache
  Graphic/Image/Selectable/control references on the node tag at create
  time; per-frame prop writes are a field read plus a property set — the
  same cost model as UI Toolkit's typed VisualElement writes. GetComponent
  survives only as cache-miss fallback and on cold paths.
- **`AnimationTicker` is now public** (`ReactiveUITK.Core.Animation`): the
  host-agnostic per-frame tick source (editor and player) for backends with
  no per-element scheduler; `Subscribe(Action)` returns the disposer.

### Samples & docs

- **`UguiStressTest`** — the stress test as a `.uitkx` `@backend ugui`
  component mirroring the UI Toolkit `StressTest` hook-for-hook (same
  seed-42 physics, keyed box field, `AnimationTicker` in place of
  `schedule.Every(16)`), so backend throughput compares like for like.
  Mounted by `RuntimeUguiStressTestDemo`; `RuntimeUguiGalleryDemo` walks the
  element vocabulary; an EditMode suite (`Ugui/Tests`) pins mount, diff,
  keyed-reorder, and pool-churn behavior.
- Root README and Samples README now cover the uGUI backend (directory map,
  `@backend ugui` example, element vocabulary, islands, docs link).

Remaining follow-ups in `Plans~/REMAINING_WORK.md` §11 (markup-level
driven-rect diagnostic, ugui Animate adapter, sample gallery).

## [0.10.0] - 2026-07-18

### Changed — License: PolyForm Shield 1.0.0 → ReactiveUI Community License 1.0

From this release, ReactiveUIToolKit ships under the **ReactiveUI Community
License 1.0** (`LICENSE.md`): free to use, modify, and ship commercially for any company
under **US $250,000** gross revenue plus funding in the trailing 12 months; above that,
shipping a product requires a commercial license — **$2,000 per title** (perpetual) or
**$2,500 per studio per year** — see `LICENSE-COMMERCIAL.md` (new). Development,
evaluation, and education stay free at any company size; the obligation attaches only
to shipping. The license asks every shipped product for a "Made with ReactiveUI"
credits line, and carries the noncompete forward (reselling the library as a competing
product remains prohibited — a game built with it is never a competing product). Every
previously released version keeps the license it shipped with (v0.8.2 and earlier: MIT
in the IDE-extension files; v0.9.0: PolyForm Shield). Also new: `CLA.md` (contributor
license agreement, required for outside contributions), and the two remaining MIT SPDX
headers in source were aligned. No code or feature changes in this entry — same
library, all features, for everyone.

### Added

- **ES combined import forms**: `import Def, { a, b as c } from "./file"` and
  `import Def, * as X from "./file"` — one declaration carrying the default binding plus
  the named/namespace surface, exactly as in ES. Parsed, formatted (single canonical
  line), lowered (default alias/bridge + named payloads + star alias from the one
  declaration), colored, and duplicate-checked across all parts.

### Changed — UITKX2304 (unused import) is now an ERROR

Severity bump (BREAKING for builds that carried unused imports as warnings): an unused
imported binding — named, `* as`, or default — now fails the build like the other import
diagnostics, in the editor and the generator alike. The promotion is safe from false
positives: the reference scan over-approximates "used" (every identifier outside the
import lines counts), so a firing 2304 means the binding truly appears nowhere. The
squiggle now also spans the whole binding token instead of one character.

### Fixed — 0.9.0 F5 field battery

- **Same-name default imports no longer emit an ambiguous binding.** `import
  isSomethingEven, { getSomething } from …` produced CS0121: the default lowered to a
  bridge on the consumer's `__Exports` while the named part's container using ALSO exposed
  the (default-exported, hence public) member — the same name visible twice. A member
  default whose binding keeps the exported name now lowers to the container using, exactly
  like a named import; only RENAMED defaults bridge.
- **Unused star/default bindings are flagged.** `UITKX2304` only ever checked named
  entries — an unused `* as X` or default binding (including the default part of a
  combined import) was never reported. All binding forms are checked now, and the
  reference scan excludes the import lines themselves so a binding can no longer count
  itself as its own use.
- **Import-brace completion works in the combined form.** Ctrl+Space inside the braces
  of `import Def, {} from "./file"` returned nothing (the context check required a bare
  `import` before `{`); it now recognizes the default-binding prefix and excludes both
  the default alias and the target's default export from the suggestions.
- **Imported bindings color by what they bind.** Import-list names all rendered with the
  grammar's uniform type tint; the semantic-token layer now resolves each import's target
  and colors every binding by its export kind — components as elements, hooks/utils as
  functions, values as variables (star aliases as variables; a default binding takes the
  kind of the default-exported declaration).
- **Named VALUE imports resolve in the editor.** The LSP's Roslyn workspace only loaded
  peer `.uitkx` files with legacy hooks/modules — a new-mode member file
  (`export Style container = …`) never entered the compilation, so its `__Exports` didn't
  exist and every bare member reference in an importer squiggled CS0103 while the build
  was clean. Peer inclusion now covers new-mode member files.
- **False `unused import` (UITKX2304) on used value imports.** The unused-import scan
  only recognized references shaped like tags, hook calls, or dotted access — a bare
  value reference (`style={container}`) was invisible. The scan now counts every
  identifier.

SG suite 1705/1705, LSP suite 152/152. Unity package **0.10.0**; VS Code + VS2022
extensions **1.6.0**; Rider **1.3.0**.

## [0.9.0] - 2026-07-18

### Changed — License: MIT → PolyForm Shield 1.0.0

From 0.9.0 the package (and the IDE extensions) are licensed under the
**PolyForm Shield License 1.0.0**: you may use, modify, and ship it inside your
own projects and commercial games exactly as before, royalty-free — but you may
not redistribute or sell it as a product that competes with ReactiveUIToolKit
(e.g. reselling the library on an asset store). Versions released up to 0.8.3
remain under their original MIT terms. Full text: `LICENSE.md`
(<https://polyformproject.org/licenses/shield/1.0.0>).

### Changed — ES-modules redesign (family campaign; owner-approved plan `Plans~/ES_MODULES_GENERAL_PLAN.md`)

- **A `.uitkx` file IS a module.** Plain typed `export` declarations replace the
  `component` / `hook` / `module` wrapper keywords. Classification is read from the
  signature alone (G-03): a `VirtualNode` return type is a **component** (PascalCase
  enforced), a `use`-prefixed name is a **hook**, `= initializer` after the name is a
  **value** (`export Style container = new Style {…}`, with `export theme = new T {…}`
  inference sugar), anything else is a **util**. Non-exported declarations are
  file-private (`internal` on the per-file `__Exports` container / `internal` component
  partials).
- **Full ES import surface** (G-05): rename-on-import `import { a as b }`, namespace
  imports `import * as X` (markup access via dotted tags `<X.Comp/>`), default imports
  `import X from "./file"` + `export default Name;`, and deferred `export { a, b };`
  lists. Renamed/default member imports lower to typed bridges on the consumer's
  `__Exports`.
- **File-keyed namespaces** (G-01/U-01): a new-syntax file's namespace is its folder path
  PLUS its file stem — every file is its own module; two files in one folder no longer
  share a namespace. The explicit `@namespace` stamp still wins (escape hatch). Legacy
  files keep folder-keyed derivation for the deprecation window.
- **Companion partial-class merging is deprecated** (U-07): new-syntax files never merge
  partials across files — a companion's members are imported like any other module's.
  The legacy merge still works for legacy files and now warns (`UITKX2107`).
- **Deprecation window** (G-10): wrapper keywords keep parsing this minor with
  `UITKX2320` warnings; removal comes in a later minor. The
  `UitkxMigrateImports --es-modules` codemod migrates whole trees — tidy, export-
  normalize, wrapper rewrite (modules are exploded member-by-member via Roslyn), importer
  rewrite (`import { Module }` becomes `import * as Module`, call sites untouched),
  companion sets atomically, formatter last; idempotent, with a `--check` gate. Sets the
  plain dialect cannot express (generic hooks; modules with nested types/properties) stay
  legacy with a report.
- **New diagnostics.** Family band `UITKX2320-2327` (wrapper deprecation, hook-returns-
  markup, value-inference failure, undeclared/duplicate exports, alias collisions,
  default-import mismatches) + Unity-local `UITKX2107-2110` (companion-merge deprecation,
  mixed-declaration-styles error, import-form-needs-migrated-target, hook-rename-drops-
  use-prefix).
- **Hot reload parity** (U-06/G-09): new-mode members hot-swap through the same machinery
  (values re-initialize via `[UitkxHmrSwap]`, utils re-bind by name, hooks re-register
  under `{ns}.__Exports::{hook}` family keys). **Migrating a file resets its hot-reload
  state once** (family keys change with the namespace); steady-state behavior is
  unchanged. In-editor smoke run: **pending** (owner-run milestone M8).
- **Editor surface** across VS Code / VS2022 / Rider: grammar, schema 1.2, completions
  (plain-declaration snippets; wrapper snippets marked deprecated), definition / rename
  (alias-aware) / references, semantic tokens, and Roslyn virtual documents mirroring the
  `__Exports` emission — including the C# name-resolution subtlety file-keyed namespaces
  introduce (usings emitted inside the namespace block, `global::`-qualified, in ALL four
  emit layers so editor and build resolve identically).
- **Samples fully migrated.** First pass converted 130 files; the pre-release audit wave
  finished the flip — the remaining 21 companion sets (modules hosting structs/enums/
  properties, which have no plain-dialect form) moved those members into ambient C# files
  beside the components (the documented pattern) and migrated the rest. Zero legacy
  wrapper syntax remains outside the byte-frozen
  `Samples/Components/UitkxTestFileDoNotTouch/` fixture.
- Family corpus hash re-pin deferred pending the family's shared ES-modules corpus cases
  (G-13/R2; recorded).

### Fixed — four-agent audit wave (pre-release; findings ledger `Plans~/ES_MODULES_AUDIT_FINDINGS.md`)

- **Parser**: line counter resyncs after component bodies (every later declaration carried
  a wrong line — wrong diagnostic anchors, wrong codemod edit targets); empty/imports-only
  files keep their baseline diagnostics instead of becoming empty new-mode sets the
  formatter then "reconstructed" into a fabricated `component Component {}`; dotted tags
  with children (`<X.Comp>…</X.Comp>`) no longer false-error on the closing tag; generic
  declarations get a precise error instead of a whole-file line-1 failure; multi-line
  typed heads keep line tracking; `ref`/`out`/`in`/`params` parameter modifiers parse into
  the typed view and forward correctly through import bridges; `hook 123 {}` no longer
  crashes the parser; UITKX2108 (mixed styles) now fires in BOTH directions; alias-vs-
  import collisions carry family UITKX2325 ("another import" arm) instead of 2303.
- **Strict imports**: a file's own new-mode hooks/members no longer false-flag 2305/2307;
  references are satisfied by the BOUND name (`import { Widget as W }` satisfies `<W/>`,
  flags `<Widget/>`); renamed-but-used imports no longer report 2304; `as`-renames against
  a legacy target now hit UITKX2109 (previously: silence, then raw CS0103) and emit no
  original-name payloads; 2326 no longer stacks on 2109; 2325/2326/2109/2110 messages are
  family-verbatim.
- **Build/editor parity**: legacy hook/module files importing new-mode members now get
  their payloads (was: clean editor, CS0103 build); the SG no longer injects whole-
  container/original-name payloads for new-mode targets the editor deliberately withholds;
  UITKX2306 value-cycle detection covers new-mode values/utils and star/default edges;
  `import * as X` of a component-only file no longer emits an alias to a never-emitted
  `__Exports` (file-breaking CS0246); UITKX2316 seeding covers member-only files' namespaces;
  the virtual doc moves a new-mode file's own usings inside the namespace (globalized) and
  `global::`-qualifies the own-exports using — editor and build resolve identically.
- **Hot reload**: a new-mode component's hot unit now carries its own `__Exports` (fresh
  member bodies + internal members resolve — previously every hot edit of the mainline
  component-plus-private-value pattern failed CS0122, and same-save member edits silently
  stayed stale); the member-only route injects import payloads; aliased/default member
  imports get real forwarding bridges in hot units (shared renderer, SG-shape parity);
  dotted and renamed component tags hot-compile via shared tag maps with an FQN-aware
  props-type scan; the hook family-key map gates on exported hooks and binds renamed
  imports to original-name keys (Fast-Refresh reset survives renames); body text like
  `module.Init();` can no longer misclassify a member file as legacy and misroute its
  save; every `Editor/HMR` source is parse-gated by tests.
- **LSP**: go-to-definition requires a declaration shape (was: jumped to a call site above
  the declaration); rename respects comments/strings and applies exactly one
  interpretation of an aliased name from the rename origin (`import { a as b, c as a }`
  no longer corrupts the unrelated binding); alias-aware brace completion; comment-
  scrubbed workspace indexing; UITKX2107 (deprecated companion merge) now surfaces live in
  the editor; default-import snippet.
- **Codemod**: a close brace sharing the last statement's line no longer silently deletes
  the NEXT declaration (spans derive from parse-record offsets); same-line adjacent
  declarations fail loudly instead of corrupting; member comments/XML docs are preserved,
  attributed members reject the set loudly, `const` migrates with a named warning;
  modifier-less module members stay private (C# default) instead of gaining `export`; all
  three companion-set import directions resolve; importers in failed sets get their star-
  import rewrite (previously left broken); `~/` specifiers resolve against the real root;
  files with parse errors are never half-rewritten.
- **Semantic tokens**: dotted tags color as binding + element (U-05).

SG suite 1730/1730, LSP suite 151/151, family corpus hash green; committed generator DLLs
rebuilt. Unity package **0.9.0**; VS Code + VS2022 extensions **1.5.0**; Rider **1.2.0**.

## [0.8.3] - 2026-07-16

### Fixed

- **HMR: companion style/module edits work in the path-derived world.** The module leg of
  the hot-reload pipeline still emitted under the RAW parsed namespace (a parser default
  for every stamp-less file) while the component/hook legs and the real build use the
  EFFECTIVE namespace. Consequences, all fixed at the one seam (`EmitModules` +
  `ComputeEffectiveNs` threading, mirroring the hook leg's 0.8.2 conversion): a companion
  module's partial class landed in a foreign namespace and never merged with its component
  in the hot unit (bare style refs like `container` failed CS0103 under HMR while the full
  build was clean — even a companion created MID-session now works, no domain reload); the
  module static swapper matched hot↔project types by `Type.FullName` and silently swapped
  nothing on `.style` edits; and the batch path's `FullyQualifiedName` aimed cascade
  trampoline swaps at a nonexistent type. Three source-text contract tests pin the
  threading so the raw-namespace shapes cannot reappear.

- **A trailing `;` on a file import no longer wrecks the whole file.** `import { X } from
  "./file";` — the JS-canonical form, and pure muscle memory in a file whose body is C# —
  left the parser's cursor stalled on the `;`, so the preamble loop bailed and the file
  failed with a misleading `UITKX2105` ("no valid component declaration") plus cascading
  phantom markup errors in the editor. The file-import reader was the only preamble reader
  missing the family's consume-to-end-of-line step (`@using` and `import "@Ns"` both had
  it); it now has parity, the `;` is tolerated everywhere, and the formatter re-emits the
  canonical semicolon-less form. Found live: the very first hand-written import in an HMR
  test session tripped it.

SG suite 1550/1550, LSP suite 118/118.

## [0.8.2] - 2026-07-16

### Fixed

- **HMR: hot-swapped code now sees the same import scope as the real build.** The HMR compiler
  injected `using static` only for same-folder hook companions — it never injected the module and
  component **type aliases** that imports imply. With path-derived namespaces (where a cross-folder
  peer always lives in another namespace), hot-editing a component whose C# body references an
  imported module (`SidebarItem`) or an imported component's type (`MetricDisplay.MetricType`)
  compiled fine in the full build but failed the hot-reload compile with CS0246. Extracted the
  injection rule into a shared language-lib helper (`ImportScopeFacts` — hook containers +
  module/component aliases, same-namespace/reserved-alias guards) consumed by the LSP virtual doc
  (typed) and the HMR compiler (via reflection, tolerant of older DLLs); a contract test pins the
  reflection seam by name on both sides.
- **Editor IntelliSense used the target's RAW namespace for import aliases.** The virtual-document
  generator aliased imported modules/components with the namespace as parsed — wrong for every
  stamp-less (path-derived) file, so cross-file references in a `namespacePrefix` project showed
  false squiggles. Aliases now use the target's EFFECTIVE namespace (explicit `@namespace` wins,
  else path-derived + config), and the same-namespace guard now matches the source generator's.
- **HMR crashed on the first hot-reload of any plain component** (`InvalidOperationException:
  default instance of ImmutableArray<T>` in `BuildHookFamilyKeyMap`). The reflective `GetItems`
  enumerated `DirectiveSet.HookDeclarations`, which is a DEFAULT `ImmutableArray` on every
  component-only file — typed code guards with `IsDefaultOrEmpty`; the mirror now checks
  `IsDefault` too. Latent since the §7 hook-family-key work; surfaced by the first RUNTIME-V
  in-editor test.
- **HMR: root components hot-reload.** Editing the ROOT component of a mount never swapped —
  the SG rewrites *child* tags to family-based `V.Func(__fam_X, …)` calls, but a hand-written
  root mount (`rootRenderer.Render(V.Func(App.Render))`) passes a raw method group, leaving the
  root fiber with no Family; `PerformRefresh` then walks right past it ("compiled OK but 0 fibers
  refreshed"). The plain `V.Func(delegate)` overloads now resolve the Family from the method
  group's declaring type (a family's id IS the component type's FQN — exact by construction),
  editor-only, one dictionary lookup; lambda mounts miss gracefully. Every root mount — samples
  and games alike — is now hot-reloadable.
- **HMR: a zero-swap now explains itself.** A compile that succeeded but refreshed nothing logged
  NOTHING — undiagnosable. The controller now reports exactly which leg returned zero (family key
  never registered / hot assembly registered a FRESH key ⇒ build↔HMR key mismatch / nothing dirty /
  no live roots / component not mounted), backed by `RefreshRuntime.LastRefreshStats` +
  `TryGetFamilyInfo` and the emitted family key carried on the compile result.
- **HMR: hooks companions no longer false-warn.** The "caught mid-write" companion warning fired
  for every `.hooks` companion (it only counted module declarations); it now fires only when a
  companion has neither modules nor hooks.

### Changed

- **Samples modernized to zero `@namespace`.** `Samples/uitkx.config.json` now carries
  `namespacePrefix: "ReactiveUITK.Samples"`; all sample namespaces are path-derived
  (`ReactiveUITK.Samples.<Folders>`), demo bootstraps/windows updated accordingly. The
  UitkxTestFileDoNotTouch fixture intentionally keeps the legacy form.

IDE extensions 1.4.2 ship the virtual-document half. SG suite 1541/1541, LSP suite 118/118.

## [0.8.1] - 2026-07-16

### Fixed

- **Router tags broke without an explicit import (UITKX0008 / false UITKX0109).** 0.8.0 made
  `ReactiveUITK.Router` part of the auto-injected baseline, so files (including the bundled
  samples) stopped writing `import "@ReactiveUITK.Router"` — but the generator's tag/props
  resolver still searched only the file's own usings. `<Router>`/`<Routes>`/`<Route path
  element>`/`<Outlet>` then failed to resolve their props at generation time (UITKX0008
  warnings + false UITKX0109 errors) even though the emitted C# was fine — caught by the
  floor-Unity store gate. The resolver now searches the same auto-injected baseline the
  emitters put in scope (one shared list, so they can never drift again), with two regression
  tests pinning the direct and alias-tag paths.

- **File imports are now fully self-sufficient for components.** Surfaced by path-derived
  namespaces (companions in other folders always land in different namespaces):
  - A cross-namespace file-imported component's **tag attributes** failed to validate (false
    UITKX0109 "declares no parameters") unless the file *also* carried a namespace-import of the
    target's namespace. The resolver's search list now includes the namespaces of the file's
    imported components — the import names the exact target file, so it is enough by itself.
  - **C# body references** to an imported component's type (`Preset.PresetProps`,
    `TableView.Column`) hit CS0246 in generated code — component imports only FQN-qualified tags.
    Imported components now get a type alias injected (`using Widget = My.Widgets.Widget;`),
    exactly like modules, with the same same-namespace/reserved-alias guards. Mirrored in the
    editor virtual document so IntelliSense matches the build.

  The 0.8.0 `dist` publish predates these fixes — git-URL `#dist` consumers should update to
  0.8.1. IDE extensions 1.4.1 ship the virtual-document parity fix (see
  `ide-extensions~/changelog.json`).

## [0.8.0] - 2026-07-15

### Added

Namespace-import unification — a single, consistent way to bring things into a `.uitkx` file,
plus the diagnostics that make a misspelled namespace visible instead of silent.

- **`import "@Namespace"` — the unified spelling of `@using`.** A C# namespace can now be brought
  into scope with `import "@ReactiveUITK.Router"`, exactly equivalent to `@using ReactiveUITK.Router`
  (same generated `using`, byte-identical output). It also accepts the full using grammar:
  `import "@static UnityEngine.Mathf"`, `import "@V = UnityEngine.Vector2"`. The distinction is the
  point: `import { X } from "./file"` (braces + `from`) imports a **peer `.uitkx` file** and is
  name-checked; a quoted `"@Namespace"` imports a **C# namespace**. `@using` keeps working forever —
  the unified form is the recommended spelling for new code.
- **UITKX2316 — unknown namespace.** A `@using` / `import "@Ns"` whose namespace resolves nowhere in
  the assembly or its references is now flagged (the namespace analogue of UITKX2300). Previously a
  misspelled `@using` produced no editor feedback and only a raw CS0246 buried in generated code.
  Now it's an **error in the editor** (a squiggle anchored on the namespace token) and a **warning at
  build** — it never breaks an otherwise-valid build; the emitted `using`'s CS0246 remains the gate.
  False positives are avoided by construction (validated against the compilation's global namespace
  unioned with every peer `.uitkx` namespace, so generated namespaces are never flagged).
- **UITKX2317 — redundant using (Hint).** `@using UnityEngine`, `@using System`, and the other
  namespaces auto-injected into every generated file are flagged as redundant (faded, editor-only)
  with a "Remove redundant using" quick-fix.
- **Quick-fixes + tooling:** a "Convert to `import \"@…\"`" refactor on any `@using` line; the
  codemod gains `--tidy` (convert `@using X` → `import "@X"` and strip redundant baseline usings in
  bulk, idempotent, `--check` for CI) and a `--format` batch mode; the formatter round-trips both
  spellings without rewriting one to the other; a discoverable `import "@…"` completion snippet.

Follow-up polish (same release):

- **Preamble ordering.** The formatter now emits `@namespace` first, then all `import` lines
  grouped together (previously `@namespace` sat between file imports and namespace imports, splitting
  them). One clean import block under the identity line.
- **Router is auto-injected.** `RouterHooks.UseNavigate()` and friends now resolve with **no
  import at all** — `ReactiveUITK.Router` joined the baseline usings every generated file receives.
  An explicit `import "@ReactiveUITK.Router"` is now flagged redundant (2317) and stripped by `--tidy`.
- **Project-wide namespace root — `namespacePrefix`.** A new `uitkx.config.json` key sets the root
  of every path-derived namespace, so a whole project can drop per-file `@namespace`. Precedence:
  `@namespace` → `namespacePrefix` → the owning `.asmdef`'s `rootNamespace` (Unity's own field) →
  the `ReactiveUITK.Uitkx` default. Every step is opt-in — projects that set none keep the exact
  legacy derivation.
- **Consistent import colour.** `import "@Ns"` is highlighted identically to a file import (same
  keyword + string scopes) so every import line reads as one colour.

### Fixed

- **Strict-import squiggles now land on the offending token.** Every 23xx diagnostic used to
  render as a one-character squiggle at column 0 (on `import`). Now 2300/2308/2314 underline the
  quoted specifier, 2301/2303/2304 the offending imported name, and 2305/2307 the referenced
  identifier — in the editor and with matching columns at build.

Additive: existing `@using`/`@namespace` files keep working unchanged. SG suite 1527/1527, LSP suite 118/118.

## [0.7.1] - 2026-07-15

### Fixed

Correctness fixes for 0.7.0's strict import diagnostics. These landed on `master` right
after the 0.7.0 dist publish, so the `dist` branch (git-URL consumers) never received
them — this patch re-publishes with the fixed analyzer DLLs.

- **False UITKX2307 errors on built-in hooks.** The strict-import exemption only matched
  the canonical PascalCase names (`UseState`, …) while real files call the camelCase
  aliases (`useState(`, …) — so any project with exported peer hooks/modules got an
  error on every built-in hook call. The exemption now covers both spellings from the
  single-source `HookRegistry.AmbientHookNames` (build + editor).
- **Heuristic strict findings are warnings, not errors.** Bare hook-call (`useX(`) and
  module member-access (`Name.member`) matches are scanned from plain C# expression
  text, which ambient C# legitimately produces (hand-written hooks, nested enums via
  `@using static`, `Screen.width`). Those UITKX2305/2307 findings are warning-tier now;
  a truly missing import still fails the compile with CS0103. Component-tag `<X>`
  findings stay errors (uitkx-only syntax, sound evidence).
- **Rooted-path import resolution on Linux/macOS.** Specifier resolution dropped the
  leading `/` of absolute Unix paths, breaking `File.Exists`/path comparisons downstream
  (LSP go-to-definition, import completion, live 23xx diagnostics on those platforms).
- **Migration codemod: two reference-scan bugs** (found migrating a real project):
  a generic type argument (`List<Dialogs.Item>`) matched the component-tag regex first
  and poisoned the shared dedup set, so the later module-member scan never added the
  `import { Dialogs }` line; and the parser's line counter was not advanced across
  hook/module bodies, so the SECOND+ declaration in a file had a stale
  `DeclarationLine` and the codemod's `export`-prepend silently missed it (consumers
  then hit UITKX2301). Both fixed with regression tests; the parser fix also corrects
  diagnostic line-anchoring for later declarations in multi-declaration files.

### Notes

- Library-only patch; IDE extensions shipped the same fixes as 1.3.1.
- SG suite 1472/1472, LSP suite 107/107. New `SamplesCorpusGateTests` runs the real
  generator over the bundled Samples (the local mirror of the Asset-Store CI's floor-Unity
  import) so this failure class is caught by `dotnet test` before reaching CI.

## [0.7.0] - 2026-07-12

### Added

ESM-style **`import` / `export`** for `.uitkx` — cross-file components, hooks, and
modules are now referenced explicitly instead of resolving implicitly across a whole
assembly. This is the Unity leg of the three-engine family feature (shared grammar with
the Unreal `.uetkx` and Godot `.guitkx` ports).

- **Grammar.** A `.uitkx` file is a preamble of `import { A, B } from "./path"` lines
  (relative `./`/`../` or the root alias `~/`, extensionless — `.uitkx` implied),
  followed by a sequence of declarations, each optionally `export`-prefixed:
  `export component`, `export hook`, `export module`. Multiple declarations of any kind
  may appear in one file, in any order (mixed-decl).
- **Strict resolution.** Only `export`ed names are visible across files, and only when
  imported. Referencing a peer-exported name without importing it is `UITKX2305` (with
  the exact `import { … } from "…"` line to add); a `useX()` call that no file exports
  and that is not a builtin/ambient hook is `UITKX2307`. New diagnostics occupy the
  family-reserved band `UITKX2300–2315`.
- **Path-derived namespaces.** A file's default namespace is derived from its path
  relative to the owning `.asmdef` (`ReactiveUITK.Uitkx.<dir segments>`); with no asmdef
  anywhere (default Assembly-CSharp) it anchors at the configured UI source root
  (`uitkx.config.json` `"root"`, default `Assets`) so every in-project file always derives
  one. `@namespace` becomes an optional interop override, and the generator **no longer
  reads a companion `.cs` to infer a namespace** — a file's identity never flips on a `.cs`
  edit. Existing files keep their identity — the migration below stamps their current
  namespace explicitly.
- **`~/` in asset references.** `Asset<T>` strings and `@uss` paths accept the `~/`
  root alias (UI source root, engine default `Assets/`).
- **Path-qualified hook Fast-Refresh keys.** HMR matches an edited hook to its consumer
  components by `{Namespace}.{Container}::{hookName}` instead of the bare hook name, so two
  identically-named hooks in different files no longer cross-swap during hot reload.
- **File-layout conventions are documentation-only.** One-component-per-file,
  hooks-in-`.hooks`-files, and filename==component are recommended conventions (see the
  docs' "Conventions & best practices"), no longer compiler-enforced: `UITKX0103`
  (filename mismatch) is retired and no longer emitted; `UITKX2313` stays reserved, never
  emitted.

### Migration

- A codemod, **`UitkxMigrateImports`**, rewrites existing `.uitkx` sources in place:
  `dotnet run --project SourceGenerator~/Tools/UitkxMigrateImports -- Assets`. It adds
  `export` to every declaration (export-everything default), inserts the imports each
  file needs (directory-proximity disambiguation for same-named peers), and stamps the
  current effective `@namespace`. Idempotent and formatter-stable. The bundled Samples
  are already migrated.
- One-time editor-only note: HMR component Register ids AND hook family keys are
  namespace-qualified, so any in-flight Fast Refresh session is invalidated once on
  upgrade (a remount, no data loss). Moving a file changes its path-derived identity
  (a documented one-time remount).

### Changed (potentially breaking)

- **`export` now drives accessibility.** An `export`ed component/module emits a
  `public partial class`; a non-`export`ed one emits `internal`. Previously the generator
  always emitted `public`. Pure `.uitkx`-to-`.uitkx` usage within one assembly is
  unaffected, but **hand-written C#** that references a now-`internal` generated type —
  a `public partial class Foo` companion `.cs`, or a cross-asmdef consumer — will fail to
  compile (CS0262 / CS0122) until the declaration is `export`ed. The migration codemod's
  export-everything default keeps migrated projects compiling; classified minor because the
  codemod covers the common case, but the accessibility change is behaviorally breaking for
  un-migrated hand-written interop.

### Notes

- New syntax is additive; the codemod keeps existing projects compiling.
- SG suite 1465/1465, LSP suite 107/107 (includes the pre-release correctness pass over the
  emit pipeline: mixed-decl multi-source emit, strict-diagnostic and namespace-derivation
  edge cases, HMR import reverse-edge invalidation, and the hook-family-key SG↔HMR parity
  tests).

## [0.6.5] - 2026-07-08

### Fixed

Correctness sweep across the `.uitkx` parser, formatter, source generator, and
HMR pipeline (all shared by the committed `Analyzers/*.dll`, so every fix here
ships to Unity even without an IDE-extension update). No syntax, API, or
diagnostic-code removal — only bug fixes to existing behavior.

#### Build & Hot-Module-Reload

- **`.uitkx` edits are reflected again on a normal recompile (Play mode and HMR
  off).** Since 0.5.10, saving a `.uitkx` that belongs to an `.asmdef` assembly
  (anything but the default Assembly-CSharp) produced **stale** generated output
  until a full Reimport / `Library` delete / any `.cs` edit — the change-watcher
  only wrote its recompile trigger into Assembly-CSharp, which never dirties the
  assembly the component actually lives in. (Regression from removing
  `RequestScriptCompilationOptions.CleanBuildCache`, which had masked it by
  force-rebuilding everything at a 30-40s cold-analyzer cost.) The watcher now
  writes the trigger into the **owning assembly's** own folder, so only that
  assembly recompiles — incrementally, analyzer stays warm, no stall — and is
  skipped entirely while HMR is active.
- **HMR of `@if` / `@foreach` / `@for` / `@while` / `@switch` bodies works
  again.** The HMR emitter read the control-flow body fields off the wrong AST
  node after they moved onto a shared `ControlBlockPayload`, so every
  control-flow body silently vanished on hot-swap. It now dereferences
  `.Payload` first (with a null-fallback for older language DLLs).
- **HMR no longer hot-swaps UI from a syntactically-broken `.uitkx`.** The HMR
  compiler filled a parse-diagnostic list but never read it, so a save with a
  syntax error could emit valid C# from an error-recovered AST and hot-swap the
  *wrong* UI silently. It now surfaces the parse error and leaves the running UI
  untouched (matching the source generator's behavior).
- **Editing a `module`'s static method now hot-swaps and refreshes its
  consumers** without a domain reload, and no longer logs a spurious
  `Could not find hook container` warning. The module-method swap itself worked
  but its consumer re-render was coupled to the hook path, so a module-only edit
  left static consumers showing stale output until an unrelated re-render; and
  the hook swapper ran for module-only files that legitimately have no hooks.
- **HMR resolves relative asset paths (`"./icon.png"`, `"../ui/theme.uss"`)
  identically to the source generator.** Four consumers previously disagreed on
  how a bare/relative asset path resolves (uitkx-dir-relative vs project-root vs
  as-is), so the editor could show no error while the build emitted an
  unresolvable path and HMR's USS map missed the file (theme edits stopped
  hot-reloading). Unified into one rule in the shared language-lib
  (`AssetPathUtil`), mirrored byte-for-byte by `HmrAssetPathUtil`.
- **A shared `.uss` edited across many components no longer freezes the
  editor.** The HMR compile queue drained the entire fan-out synchronously in a
  single editor tick (N sequential Roslyn compiles in one frame); it now
  compiles one item per tick.
- HMR markup-fragment parsing hardened: the fragile synthetic-header parse
  (three chained accidental behaviors) was replaced with a dedicated
  `ParseMarkupFragment` path, so unrelated parser work can't silently break HMR
  splicing.

#### Parser, formatter & tooling

- **Formatter no longer corrupts commented-out code.** `DirectiveParser.FindJsxBlockRanges`
  and the formatter's `ScanJsxParenBlocks` had no `/* ... */` block-comment skip
  (their sibling `FindBareJsxRanges` already did), so a `/* old UI: (<Label/>)
  was removed */`-style comment was misdetected as live JSX: it could produce a
  false `CS1002` in the IDE's virtual document, and the formatter re-indented
  the comment's interior as if it were real code on every save. Fixed at the
  root in both detectors, plus a new comment-interior line mask (mirroring the
  existing multi-line-string mask) so comment content is now re-emitted
  byte-verbatim, matching author-chosen indentation.
- **Six independently-drifted mini-lexers consolidated** into one
  shared, tested `CSharpLexFacts` class. Fixes a real lexing bug along the way:
  `{ $@"{a("}")}" }` (a quoted `}` inside an interpolation hole) closed the
  outer brace early and truncated the extracted expression.
- **`x = cond && <Tag/>` and other logical-AND-before-JSX shapes** now desugar
  correctly regardless of LHS shape (parenthesized, method-call, or preceded
  by a real `==`/`!=`/`<=`/`>=` comparison instead of `=`).
- **Literal `@` in text content** (e.g. `contact me @ home`) no longer
  misparses as a directive attempt; genuine misspelled directives
  (`@foreech (...)`) still error.
- **Duplicate `@namespace`** now reports one clear diagnostic at the correct
  line instead of a misleading whole-file error.
- **`@switch`/`@else`/control-block edge cases**: Allman-style `@else`, `::`
  namespace-qualified `@case` values, an unwrapped `return expr;` inside a
  control block, and an unclosed `{`/`{expr}` in a snippet mini-parse all now
  produce a correct diagnostic instead of silently mis-parsing.
- **`attr={(<Tag/>)}`** (a paren-wrapped inline JSX attribute value) is now
  recognized as JSX instead of being typed as opaque C#.
- **Mismatched closing tag diagnostic** now anchors at the actual mismatched
  tag (with a tracked column) instead of a stale line, and no longer claims a
  fabricated "opened at line N" when the opening location isn't tracked.
- **`WorkspaceIndex` prop indexing** now recognizes expression-bodied
  properties (`public string Foo => ...;`), not just brace-bodied ones.
- **`@switch` expression** is now mapped into the IDE's virtual document with
  its own tracked offset, so it gets the same hover/diagnostics/go-to-def
  support as `@if`/`@foreach`/`@for`/`@while` conditions already had.
- Dead code removed: an unreachable inline-effect-scheduling path in the fiber
  reconciler (`FiberFunctionComponent.ScheduleEffect` and its unused callers —
  all committed passive effects already flow through the real two-pass
  `_pendingPassiveEffects` scheduler), plus several already-unreferenced
  parser/semantic-token helpers.
- `cursor` (the one permanently-unsupported inline style key) is now
  documented as such in the shared schema (`uitkx-schema.json`) instead of
  only a code comment, so tooling has a single source of truth for it.

### Notes

- Full test suites green: SourceGenerator `1337/1337`, LSP server `82/82`
  (includes a new `StaleAdditionalTextTests` regression guard: the generator
  now reads `.uitkx` content disk-authoritative, so a stale `AdditionalText`
  handed to the driver still yields fresh output).
- IDE extensions ship in lockstep with the same underlying fixes (LSP server,
  formatter, diagnostics) — see `ide-extensions~/changelog.json` for VS Code
  1.2.18 / VS 2022 1.2.18, and the Rider plugin bumped to 1.0.1.

## [0.6.4] - 2026-06-19

### Fixed

- **Interrupted time-sliced renders no longer duplicate UI, strand old
  routes, or freeze the editor.** When a state update or navigation arrived
  while a time-sliced render was parked mid-tree (between slices, across
  frames), `FiberReconciler` restarted the work loop from the work-in-progress
  root but did not clear the partially-built effect list. That list lives on
  the persistent `FiberRoot` and was only ever cleared in `CommitRoot`, so the
  interrupted pass's stale `Placement` effects were committed a second time on
  the next commit: freshly-mounted host elements were appended twice (and once
  more per restart), and a route that should have unmounted could stay mounted
  alongside the new one. In the worst case the re-appended effect chain spliced
  `NextEffect` into a cycle and `CommitRoot`'s `while (effect != null)` walk
  spun forever, freezing the editor.
  - **Fix:** `FiberReconciler.ScheduleUpdateOnFiber` now discards
    `_root.FirstEffect` / `_root.LastEffect` and resets `_hasDeletions`
    whenever an update restarts the work loop; the restarted walk rebuilds the
    effect list (and re-derives deletions) from scratch.
  - **No API change, no player cost.** Renders that complete within a single
    slice are unaffected -- only the interrupted-and-restarted path changed.
  - **Why it was rare:** the update must land in the narrow window after a
    slice yields but before the next commit. It surfaced under heavy
    pause-time UI churn -- e.g. rapid menu/route navigation while wall-clock
    `schedule.Execute().Every()` timers keep firing state updates at
    `Time.timeScale == 0`.

### Notes

- Mount bare `<Route>` siblings under a `<Routes>` selector for atomic
  single-best-match selection. Without it, route exclusivity is emergent (each
  unmatched route independently renders null), which can briefly show two
  routes at once under the same interrupted-render stress; `<Routes>` collapses
  the choice to a single committed node.

Library-only release. IDE extensions unchanged at VS Code 1.2.17 / VS 2022 1.2.17.

## [0.6.3] - 2026-06-13

### Added

- **Declarative custom rendering via `onGenerateVisualContent` (plus
  `redrawKey`).** Every element now accepts an `onGenerateVisualContent`
  attribute that binds directly to Unity UI Toolkit's
  `VisualElement.generateVisualContent` delegate
  (`Action<MeshGenerationContext>`). Draw vector shapes with `ctx.painter2D`
  or raw vertex/index meshes with `ctx.Allocate(...)`, while the rest of your
  UI stays reactive. The attribute is inherited from `BaseProps`, so it is
  available on `VisualElement`, `Button`, and every other built-in element.
  - **Reactive repaints.** The element repaints automatically when the
    callback reference changes between renders, or when the new `redrawKey`
    (an `int`) changes. A fresh inline lambda redraws each render — the same
    model as any other prop. Stabilise the callback with `useMemo` /
    `useStableCallback` and bump `redrawKey` for on-demand repaints without
    changing the delegate.
  - **Player-safe.** `MeshGenerationContext`, `Painter2D`, and `Vertex` are
    runtime `UnityEngine.UIElements` types, so the feature ships in player
    builds with no `#if UNITY_EDITOR` gating and behaves identically in the
    Editor and in a built game.
  - **Read-only callback.** The callback runs during Unity's paint phase, not
    during render — treat the element as read-only inside it.

- **Sample: `CustomDrawDemoFunc`.** A new showcase component demonstrating all
  three techniques — a Painter2D polygon driven by component state, a raw
  `ctx.Allocate` quad, and a stable callback forced to repaint via `redrawKey`.
  Open it from the Unity menu under **ReactiveUITK → Demos → Custom Drawing**.

### Documentation

- New **Custom Rendering** guide on the documentation site covering the
  attribute reference, repaint semantics, Painter2D vs. raw-mesh drawing, the
  companion-file pattern for keeping draw bodies out of the markup, and
  `redrawKey`.

IDE extensions ship the matching schema and attribute-lambda typing
(`MeshGenerationContext`) at VS Code 1.2.17 / VS 2022 1.2.17.

## [0.6.2] - 2026-06-05


### Changed

- **Unity 6.3 panel-rebuild defense is now editor-only (`#if UNITY_EDITOR`).**
  The per-frame `UIDocument.rootVisualElement` poll added in 0.5.6 to work
  around the Unity 6.3 regression — the panel being silently rebuilt on every
  `InspectorWindow.RedrawFromNative`, reported to Unity as `UUM-127851` — is
  now compiled out of player builds entirely. Two facts drove the change:
  - **Unity shipped the fix.** `UUM-127851` is resolved in 6000.3.17f1,
    6000.4.9f1, 6000.5.0b9, and 6000.6.0a6. Verified on real hardware: the
    root swaps roughly once per second while the `UIDocument` is selected in
    the Hierarchy on 6000.3.8f1, and is silent on 6000.3.17f1.
  - **Every swap the poll defends against is editor-only.**
    `InspectorWindow.RedrawFromNative` never runs in a player, and undo,
    source-asset reassignment, disable/enable, and HMR are all editor
    mutations. In a built game the only `rootVisualElement` swaps are
    developer-initiated and already flow through the always-on reactive
    path, so the poll is dead weight in shipped builds.

  Public API is unchanged — both `RootRenderer.Initialize` overloads and both
  `Hooks.UseUiDocumentRoot` overloads keep their signatures and their full
  editor behaviour:
  - `RootRenderer.Initialize(UIDocument)` still seeds the initial root from
    the document in every build (identical to `Initialize(VisualElement)`).
    Only the per-frame poll, `PollHostDocument`, the `AnimationTicker`
    subscription, and the `RetargetHost` call path are editor-gated.
  - `Hooks.UseUiDocumentRoot` still performs its initial `UseState` capture
    plus one effect-time resync in every build, and still re-runs whenever the
    `doc` argument reference changes (its `UseEffect` dependency). Only the
    per-frame poll that catches *silent same-reference* root swaps is
    editor-only. Consumers that swap the document through their own state —
    e.g. a `UIDocument` slot registry firing on enable/disable — behave
    identically in players.

### Performance

- Player builds no longer execute or contain the per-frame
  `UIDocument.rootVisualElement` poll (one property read plus a
  `ReferenceEquals` per `RootRenderer` and per `UseUiDocumentRoot` consumer,
  per frame). The editor-only machinery and the `hostDocument` tracking fields
  on `RootRenderer` are stripped from player IL. Editor cost is unchanged.

Library-only release. IDE extensions unchanged at VS Code 1.2.15 / VS 2022 1.2.15.

## [0.6.1] - 2026-05-23

### Changed

- **Unified hook metadata into a single source of truth
  (`ReactiveUITK.Core.HookRegistry`).** Eight hand-maintained tables
  describing the 20 public hooks lived in parallel across five files
  and consistently drifted from each other after every hook
  addition: `CSharpEmitter.s_hookAliases` /
  `s_genericHookAliasRe` / `s_hookSignatureRe` (SG),
  `HooksValidator.s_hookPatterns` (SG), the two HMR mirrors in
  `HmrCSharpEmitter` + `HmrHookEmitter`, `DiagnosticsAnalyzer.s_hookPatterns`
  (IDE live diagnostics), `HoverHandler.s_hookDocs` (IDE hover), and
  the two verbatim Roslyn-only stub blocks in
  `VirtualDocumentGenerator` (static + instance forms). Every
  release between 0.5.18 and 0.6.0 shipped at least one bug that was
  a `s_hook*` table missing an entry the other tables already had
  (camelCase aliases for 7 hooks in 0.5.21, hover docs for 10 hooks
  in 0.5.22, etc.). Consolidated into a new `public static class
  HookRegistry` (`Shared/Core/HookRegistry.cs`, wrapped in
  `#if UNITY_EDITOR` so it adds zero player-build cost) with three
  private orderings (`s_signatureOrder`, `s_aliasOrder`,
  `s_genericOrder`), cached accessors (`GetAliasTable`,
  `GetSignatureRegexPattern`, `GetGenericHookPattern`,
  `GetValidationPatterns`, `GetDocMap`,
  `GenerateVirtualDocStubs(bool staticForm)`), and adds-once
  semantics: every accessor returns the same cached reference so
  per-keystroke hot paths in `DiagnosticsAnalyzer` retain their
  zero-allocation contract. The registry is linked into
  `UitkxLanguage.csproj` (`<Compile Include Link>`); the SG inherits
  it transitively via its existing `ProjectReference` to language-
  lib, avoiding CS0436 duplicate-type. All five consumer files now
  read from the registry instead of declaring their own copy. Net
  diff: ~470 LOC added (registry + 16 golden-snapshot tests + 8
  golden files), ~290 LOC of duplicated tables removed.

### Fixed

- **`useLayoutEffect` is now covered by rules-of-hooks diagnostics
  and IDE hover docs.** The hook shipped in the runtime several
  releases ago and had a SG alias-rewrite entry, but the validator
  and hover layers' `s_hookPatterns` / `s_hookDocs` tables were
  never updated, so calling `useLayoutEffect` inside an `if` block
  silently passed `UITKX0013` (rules-of-hooks) and hovering over the
  call site returned no documentation. The registry adds the missing
  three validation patterns (`Hooks.UseLayoutEffect(`,
  `UseLayoutEffect(`, `useLayoutEffect(`) and the two hover entries
  (camelCase + qualified) — pure additive coverage, no existing
  diagnostic behaviour changes.

### Tests

- SG `1264/1264` passing. New `SourceGenerator~/Tests/HookRegistryTests.cs`
  adds 16 tests across three layers: internal invariants
  (`Registry_HookCount_IsExpected`, `Registry_AliasTable_HasOneEntryPerHook`,
  `Registry_ValidationPatterns_HaveThreeFormsPerHook`,
  `Registry_Accessors_ReturnSameReferenceOnRepeatedCalls`), runtime
  reflection parity (`Registry_CanonicalNames_MatchHooksType`,
  `Registry_SignaturePattern_MatchesAllFormsOfEveryHook`,
  `Registry_GenericPattern_MatchesGenericFormsOnly`), and byte-for-
  byte golden-file equality against `SourceGenerator~/Tests/Golden/HookRegistry/`
  (alias table, signature regex, generic alias regex, validation
  patterns, hover docs, both VDG stub blocks). The validation-
  patterns golden test explicitly asserts the diff between registry
  output and the pre-refactor baseline is exactly the three
  `useLayoutEffect` entries, so any future drift surfaces as a test
  failure naming the specific table that drifted.

## [0.6.0] - 2026-05-22

### Changed

- **HMR: Fast-Refresh-style component identity via Family handles
  (no backward compatibility).** The previous trampoline-swap design
  rebound a per-component `__hmr_Render` field on every save and used
  `MethodInfo.DeclaringType` equality at the reconciler to decide
  whether a fiber could be reused. Cross-DLL identity drift after a
  cascade compile produced false negatives, tearing down state in
  components the user had not edited (e.g. saving a leaf re-mounted
  the entire page root). Replaced with a port of React Fast Refresh's
  Family indirection:
  - New `ReactiveUITK.Refresh.Family` handle with a mutable `Current`
    delegate slot and a previous-body rollback slot.
  - New `ReactiveUITK.Refresh.RefreshRuntime` provides `Register`
    (called from each component's `[ModuleInitializer]` polyfill),
    `GetFamily` (called from parents that mention the child),
    `PerformRefresh` (Editor-only, walks all live root fibers and
    notifies state for hook-signature changes), and `TryRollback`.
  - Source generator emits a single static `__fam_<Child>` field per
    distinct child type plus a `[ModuleInitializer]` that publishes
    the component's render body and hook signature.
  - `V.Func(family, ...)` overloads thread the Family handle into the
    `VNode`/`FiberNode`; reconciliation compares Family references
    instead of delegate-DeclaringType equality, eliminating the cross-
    DLL identity bug regardless of how many cascade compiles preceded
    the swap.
  - On render-crash rollback, the runtime reverts the Family delegate
    to its previous body so the retry path executes the last-known-
    good IL.
  - Removed: `UitkxHmrComponentTrampolineSwapper`,
    `UitkxFileDependencyIndex`, `HmrCompiler.CompileBatch`/
    `HmrBatchCompileResult`, controller cascade batch path,
    `HmrState.TryRollbackComponent`. The per-SCC union compile is no
    longer needed because each consumer's Family field points at the
    single canonical Family object the new assembly populates.
  - **React-style dev/prod split — zero player overhead.** The entire
    Family / `RefreshRuntime` machinery is wrapped in
    `#if UNITY_EDITOR`: `Refresh.Family`, `RefreshRuntime`, `HmrState`,
    the `VNode._family` field, the `FiberNode.Family` field, the
    `V.Func(Family, ...)` overloads, the reconciler's Family-identity
    branch, and the render-crash rollback path. The source generator
    emits dual-shape `V.Func` calls per child site —
    `#if UNITY_EDITOR V.Func<P>(__fam_X, ...) #else V.Func<P>(global::Foo.X.Render, ...) #endif` —
    so player builds compile down to direct delegate calls identical
    in shape to 0.5.x (zero Family allocations, zero `RefreshRuntime`
    types, zero `[ModuleInitializer]` Register calls, zero indirection
    on the hot path). The `ModuleInitializerAttribute` polyfill is
    likewise editor-only. This mirrors React Fast Refresh's
    `$RefreshReg$` injection model: dev tooling injects identity
    tracking; production ships with none of it.
  - Editor cost: same as before plus a Family lookup; the reconciler
    hot path is unchanged (one reference comparison).
  - `ModuleInitializerAttribute` is emitted as an internal polyfill in
    user assemblies under `#if UNITY_EDITOR && !NET5_0_OR_GREATER` so
    Unity's mono runtime invokes the per-component registrations on
    Editor assembly load (player builds need no polyfill since no
    `[ModuleInitializer]` consumer is emitted).
  - **Lazy-factory Register to avoid asset-registry race in Editor.**
    The SG-emitted `[ModuleInitializer]` now passes `() => __Render_body`
    to `RefreshRuntime.Register` instead of the field directly. Reading
    `__Render_body` from the ModuleInitializer would trigger the
    component type's `.cctor`, which runs user static field initializers
    such as `static readonly Texture2D bg = AssetHelpers.Asset<Texture2D>(...)`
    BEFORE Unity's `[InitializeOnLoadMethod]` hooks have populated
    `UitkxAssetRegistry`. The lambda defers the field read until first
    render -- by which point the editor's load-time hooks have all
    completed and the registry is fully populated. A new
    `RefreshRuntime.Register(string, Func<Func<...>>, string)` overload
    stores the factory on the `Family`; `Family.Current` resolves it
    lazily on first read.
  - **Companion class hosts `[ModuleInitializer]` (Mono cctor fix).**
    Per ECMA-335 §I.8.9.5, a `beforefieldinit` type's `.cctor` is only
    required before first static **field** access — calling a static
    method on the type should NOT trigger it. Mono diverges: the
    `call MenuPage::__UitkxRegisterFamily()` instruction emitted into
    `<Module>::.cctor` triggers `MenuPage.cctor` anyway, which runs
    user static initializers before any editor load-time hook (the
    very race the lazy factory was meant to defer). Verified
    end-to-end with Mono.Cecil on an emitted PrettyUi assembly. The
    SG and HMR emitters now emit the `[ModuleInitializer]` Register
    call on a separate companion type, `{ComponentName}__UitkxRefresh`,
    living in the same namespace as the component. Calling
    `MenuPage__UitkxRefresh.__Register()` from the module .cctor never
    touches `MenuPage`; the `() => MenuPage.__Render_body` factory
    uses `ldftn` (also non-triggering per ECMA) so the component
    .cctor only runs at first `Family.Current` read — i.e. first
    render, after `UitkxAssetRegistry` is fully populated.
    `__Render_body` visibility relaxed from `private` to `internal`
    so the companion can take its address. Player builds remain
    unaffected (the entire companion is wrapped in `#if UNITY_EDITOR`).
  - **Parent-supplied fallback factory for non-SG children.** Components
    that mention a hand-written child (e.g. `<Router>`, `<Route>`,
    `<Routes>`, `<Outlet>` from `ReactiveUITK.Router`) have no SG
    companion → no `[ModuleInitializer]` Register call → the
    parent-side `GetFamily("ReactiveUITK.Router.RouteFunc")` returned
    a Family with no body, and the very first render against it
    threw `InvalidOperationException: Family placeholder body invoked`.
    The SG/HMR emitters now pass a fallback factory to GetFamily:
    `RefreshRuntime.GetFamily("X", () => global::X.Render)`. The
    `ldftn X.Render` inside the lambda does NOT trigger `X.cctor`
    (per ECMA-335 §I.8.9.5; Mono honors `ldftn` -- the
    beforefieldinit divergence is on `call`, not `ldftn`), so the
    fallback is safe to install at parent cctor time and only
    resolves at first render. SG-emitted children remain unaffected
    -- their companion's Register runs first and the fallback is
    ignored. New overload
    `RefreshRuntime.GetFamily(string, Func<Func<...>>)` and
    `Family.TrySetFallbackFactory` support this.
  - **Diagnostics: one-shot Console error per unresolved Family.** When
    `Family.Current` resolves with neither a Register call nor a
    fallback factory, the runtime now emits a single
    `Debug.LogError` per `Family.Id` via
    `RefreshRuntime.WarnUnresolvedFamilyOnce`, naming the missing
    component and pointing at the two likely causes (non-SG child
    whose parent was generated by a pre-0.6.0 SG, or a missing
    companion class). The thrown `InvalidOperationException` now
    also includes the `Family.Id` in its message instead of a
    generic placeholder text. This eliminates "which Family threw?"
    decompilation rounds when a parent is mis-emitted.
  - **Test guards (architectural invariants).** Added two regression
    tests in `SourceGenerator~/Tests/EmitterTests.cs` that lock in
    the two HMR fixes above:
    `ChildFamily_GetFamilyCall_AlwaysIncludesFallbackFactory`
    asserts that every `GetFamily(id, ...)` emission includes the
    `() => {Child}.Render` fallback factory (prevents future SG
    "optimisations" from regressing the Router crash).
    `ModuleInitializer_OnlyEmittedOnCompanionClass_NeverOnComponentItself`
    parses generated source as a Roslyn syntax tree and asserts
    every `[ModuleInitializer]` method lives on a `*__UitkxRefresh`
    type (prevents future refactors from moving Register back onto
    the component class and re-introducing the Mono BeforeFieldInit
    cold-open crash).
  - Family registry keys use the component's fully qualified name
    (`Namespace.ComponentName`) and the per-parent `__fam_*` field is
    derived from the resolved peer FQN with non-identifier characters
    replaced by `_`. Two components sharing a simple name in different
    namespaces (e.g. `MyApp.Buttons.Button` vs `Vendor.Button`) get
    distinct Family handles, preventing silent registry collisions.

- **HMR: hook signature redesign — custom-hook edits now invalidate
  consumer components.** The Family port above wired hook-call-shape
  signatures (`[HookSignature("UseState,UseEffect")]`) to the
  force-remount path, but only for hooks inlined in the component
  setup code. A hook defined in a separate `.uitkx` file
  (`hook useFoo(...) { ... }`) was opaque — adding or removing
  `useEffect` inside the hook left the consumer's `Signature` string
  unchanged, so HMR re-rendered the consumer WITHOUT resetting its
  hook state. Effects accumulated, refs went stale, state shifted by
  one slot. The fix ports React Fast Refresh's `customHooks` array:
  - `HookSignatureAttribute` gained a second constructor arg
    `string[] customHookFamilyKeys` listing every first-level custom
    hook called by the component (bare hook names, React-style
    Fast-Refresh family keys).
  - `RefreshRuntime.RegisterHook(id, signature, customHookFamilyKeys)`
    is the new entry point for hook authors. Both the SG
    (`HookEmitter`) and the HMR `HmrHookEmitter` now emit a
    `{ContainerClass}__UitkxHookRefresh` companion with a
    `[ModuleInitializer]` that calls it.
  - `Register` for components has matching 4-arg overloads that wire
    a `hookId -> consumerIds` reverse-edge map on every call.
  - `PerformRefresh` now begins with `PropagateHookSignatureChanges`:
    a BFS walk that fans out from every dirty hook Family through the
    reverse map, adding consumers to `s_dirty` (re-render) plus
    `s_forceRemount` when the hook itself force-remounted. Hook-calls-
    hook chains and cycles are bounded by a visited set.
  - Both SG and HMR component emitters scan setup code with a
    hook-shape regex matching `use[A-Z]` / `Use[A-Z]` / `provide[A-Z]`
    / `Provide[A-Z]` identifier prefixes, filter against the 21-name
    built-in allowlist (`UseState`, `UseEffect`, `UseRef`, etc.), and
    emit the surviving identifiers as the `customHookFamilyKeys` list.
  - Family keys are bare hook names. The alternative
    `{ContainerFQN}::{HookName}` scheme would require the SG to
    resolve cross-asmdef container FQNs from hook names — invasive
    and outside the SG's incremental boundary. React's runtime uses
    the same simple convention; collisions across packages become a
    naming-discipline issue rather than a build-system issue.
  - **Cost.** One `HashSet<string>` per registered hook id (only when
    consumers exist), one BFS per `PerformRefresh` capped at the live
    hook subgraph. Zero player-build impact — the entire reverse-edge
    map and propagator are inside `#if UNITY_EDITOR`.

### Added

- `Family.CustomHookFamilyKeys` (string[]) — first-level hook family
  keys referenced by this Family. Maintained on every Register /
  RegisterHook call.
- `Family.IsHook` (bool) — distinguishes hook families (body null,
  invoked via static trampoline) from component families.
- `RefreshRuntime.RegisterHook(id, signature, customHookFamilyKeys = null)`.

### Fixed

- **HMR: cross-folder peer-hook resolution.** `HookContainerRegistry`
  (and the parallel `UitkxFileDependencyIndex`) used a regex that
  required a trailing `;` after `@namespace Foo.Bar` in `.uitkx`
  files. `DirectiveParser.TryReadFunctionStyleNamespaceDirective`
  treats the semicolon as optional, and no `.uitkx` in the codebase
  (or in the samples / IDE tests) actually writes one — so the
  registry indexed zero files and silently returned an empty list
  from `GetForAsmdef`. Consumers that lived in a different folder
  from their hook file (anything outside the
  `{ComponentName}.*.uitkx` sibling glob handled inline by
  `UitkxHmrCompiler.EmitCompanionUitkxSources`) compiled with no
  `using static {HookNs}.{HookContainer};` injected and failed with
  `CS0103: The name 'useFoo' does not exist in the current context`.
  Fix: relax both registry regexes to `\s*;?\s*$` so the trailing
  semicolon is optional, matching the parser. TicTacToe-style same-
  folder layouts were already covered by the companion glob and are
  unaffected.

- **HMR: hook-file edits never drained the Phase 1/3 propagation
  queues.** `UitkxHmrController` split its post-compile path in two:
  component files called `RefreshRuntime.PerformRefresh()`, but hook
  files called only `UitkxHmrDelegateSwapper.SwapHooks(...)`. The
  swapper's own `TriggerGlobalReRender()` walked every fiber and
  invoked `OnStateUpdated`, which is enough to pick up a body-only
  change (the new delegate runs on the next render and state is
  preserved). It is NOT enough for a signature change: the hook's
  `RegisterHook` ModuleInitializer correctly added the hook Family
  to `s_dirty` + `s_forceRemount`, but with no `PerformRefresh` call
  to drive `PropagateHookSignatureChanges`, the reverse-edge walk
  from the hook to its consumers never ran and the consumers were
  re-rendered without being remounted (so `useRef` / `useState` from
  before the edit lingered across a real signature change — the
  exact regression the hook-signature redesign was built to fix).
  Fix: after `SwapHooks` returns, also call
  `RefreshRuntime.PerformRefresh()` and report the larger of the two
  instance counts. End-to-end validation: a custom hook gains a
  `useEffect` → consumer's `useRef` resets to a fresh GUID
  (force-remount); same hook edited to change only an interior
  `Debug.Log` text → the consumer's GUID is preserved (re-render
  only); same hook with the `useEffect` removed → consumer remounts
  again.

- **HMR: `ModuleInitializerAttribute` CS0122 after copy-rename of a
  component into a new folder.** The source generator emits a
  `ModuleInitializerAttribute` polyfill as `internal` to the main
  asmdef. The HMR-compiled DLL references that asmdef and emits
  `[ModuleInitializer]` on the per-component companion class, but
  `internal` is not visible across assemblies, so every HMR compile
  failed with CS0122 "ModuleInitializerAttribute is inaccessible due
  to its protection level". Fix: `UitkxHmrCompiler.CompileSources`
  now prepends a local copy of the polyfill (guarded by
  `#if !NET5_0_OR_GREATER`) to every HMR compilation unit so Roslyn
  binds the attribute to the in-compilation type before consulting
  references. The main-asmdef polyfill is left `internal` so a future
  Unity TFM shipping the real attribute can supersede it without
  ambiguity. Affects both single-file and batch cascade compiles, and
  both in-process and external csc paths.

- **HMR: defensive diagnostic when a companion `.uitkx` matches the
  `ComponentName.*.uitkx` glob but contributes no module body.** When
  a companion file is caught mid-write by the change watcher (common
  when copy-pasting a component folder with multiple files), the
  directive parser may successfully parse the file yet find zero
  module declarations -- silently dropping the partial-class fragment
  the parent depends on for static member resolution (e.g. style
  identifiers like `Container`). The component then fails to compile
  with CS0103 on each missing static, with no indication that a
  sibling file was the cause. `EmitCompanionUitkxSources` now logs a
  single warning naming the file and instructing the user to re-save
  the `.uitkx` to retry.

- **HMR: newly-added component renders as a placeholder until full
  reload (Family.Current never published).** After fixing the CS0122
  polyfill issue, a freshly added component (e.g. copy-rename a
  folder, reference the new name from the parent) compiled cleanly
  but the parent rendered nothing where the new child should appear.
  Root cause: `UitkxHmrCompiler` relied on the CLR firing
  `<Module>.cctor` automatically when `Assembly.LoadFrom` returns,
  but the CLR fires it lazily on first member access of any type in
  the module. The downstream HMR swap pipeline only touches module
  types that declare instance/static members (e.g. `Style Container`)
  -- it never touches the synthetic `__UitkxRefresh` companion class
  whose sole job is to carry `[ModuleInitializer]` for the
  `RefreshRuntime.Register(fqn, renderDelegate, hookSignature)` call.
  Without that Register call the child's `Family.Current` stayed at
  the fallback factory (a placeholder warning render), so the parent
  drew nothing. Components without a companion file happened to work
  because their swap path accidentally touched a member of the
  companion type via reflection enumeration, latently firing the
  module cctor. Fix: `UitkxHmrCompiler` now calls
  `RuntimeHelpers.RunModuleConstructor` on every module of the loaded
  HMR assembly immediately after `Assembly.LoadFrom`, on both the
  in-process Roslyn and external csc paths. The CLR de-duplicates
  internally so subsequent automatic invocations are no-ops. The
  controller's post-compile comment that previously asserted "the
  module initializer has already run during Assembly.Load" has been
  updated to document the deterministic kick.

- **HMR: a parent that was already visible when a new child component
  was added did not pick up the change until unmount + remount.**
  After `RefreshRuntime.Register` updated the child's `Family.Current`
  and added the parent's family to the dirty set, `PerformRefresh`
  walked the live fiber tree and scheduled an `OnStateUpdated`
  re-render on every fiber whose Family was dirty -- but the next
  render pass still invoked `fiber.TypedRender`, the delegate that
  was captured at mount time (or from the previous V.Func vnode).
  That delegate is the OLD compiled body, so the re-render produced
  the same VDOM as before and the new child never appeared. The only
  way to see the new child was to unmount + remount the parent (e.g.
  close and reopen a dialog), because remount routes through
  `V.Func(family, ...)` which reads `Family.Current` fresh. Fix:
  `RefreshFiberTree` now assigns `fiber.TypedRender = fiber.Family.Current`
  immediately before invoking `OnStateUpdated`, mirroring the same
  refresh that the render-crash rollback path at `FiberReconciler`
  L514 already performs.

- **HMR: edits to a freshly-added child `.uitkx` silently never
  propagated while edits to the parent worked.** When the user pressed
  Save in their editor, the editor briefly held the file with an
  exclusive write lock; the HMR `FileSystemWatcher` fired during that
  window and `File.ReadAllText(uitkxPath)` in `UitkxHmrCompiler.Compile`
  threw `IOException: Sharing violation`. The exception bubbled to
  `HandleCompileFailure` which logged a warning and dropped the change
  — the FileWatcher's de-duplication then suppressed retries, so the
  save was lost. Parent saves only worked because their lock release
  happened to land before the watcher's debounce. Fix: all six raw
  `File.ReadAllText` call sites in `UitkxHmrCompiler` now route through
  a new `ReadTextWithRetry` helper that opens with
  `FileShare.ReadWrite | FileShare.Delete` (cooperative with the editor
  instead of fighting it) and retries up to 8 times with exponential
  backoff capped at 120ms (worst case ~480ms total, well under the
  watcher debounce window). Successful retries are logged so future
  lock-contention regressions are visible.

- **HMR: edits to a freshly-added child component silently never
  propagated (Family-key namespace mismatch).** `[HMR-DIAG]` tracing
  pinpointed two distinct `Family` instances existing for the same
  component: the producer-side companion `[ModuleInitializer]` emitted
  by `HmrCSharpEmitter` published against the FQN key
  (e.g. `"PrettyUi.App.Pages.TextOne"`), while the consumer-side
  `__fam_TextOne` static field — emitted into the parent component's
  HMR-compiled body — called `GetFamily(...)` with the raw JSX tag
  (`"TextOne"`) because the emitter only had the simple name at JSX
  walk time. Result: two `Family` instances in `s_families` (e.g.
  `Family#-1783213528` vs `Family#-773693448`); the live fiber tree
  pointed at the simple-name family, every `Register` call updated the
  FQN family, and `PerformRefresh`'s walk never matched (always
  `dirty=False` for the TextOne fiber). Cold-built SG output was
  unaffected because the SG resolves to FQN at compile time. Fix:
  added `ResolveComponentFqn(typeName)` to `HmrCSharpEmitter` (mirrors
  the existing `FindPropsTypeAndRefSlot` reflection scan) and routed
  the consumer-side `famKey` through it before emitting the
  `GetFamily(...)` call, so the consumer-side key matches the
  producer-side `Register` key exactly. Falls back to the bare name
  when the FQN can't be resolved (hand-written components without a
  Register call), preserving prior behaviour for those cases.

### Tests

- SG `1245/1245` passing — no regressions. Two new architectural-
  invariant tests in `SourceGenerator~/Tests/EmitterTests.cs` lock in
  the GetFamily-fallback-factory and ModuleInitializer-on-companion-
  only contracts. The new hook-signature reverse-edge propagation
  path is exercised end-to-end through the existing
  `UitkxHmrController` integration on every editor save.

## [0.5.22] - 2026-05-20

### Added

- **`Hooks.UseTransition()` — React-compatible no-op transition hook.**
  Returns `(bool isPending, Action<Action> startTransition)`. UITKX has
  no concurrent renderer, so `isPending` is always `false` and
  `startTransition(action)` runs `action` synchronously; the start
  delegate is a cached `static readonly` field so per-call allocation
  is zero. Provided for source compatibility with React-targeted code
  being ported to UITKX, and so the SG / HMR alias rewrite from
  `useTransition(...)` to `Hooks.UseTransition(...)` (added in 0.5.21's
  `s_hookAliases`) now resolves at runtime instead of failing with
  `CS0117: 'Hooks' does not contain a definition for 'UseTransition'`.

### Fixed

- **IDE: JSX inside attribute-lambda bodies is now type-checked.** The
  virtual document generator's `EmitMappedExpressionStrippingJsx`
  (`ide-extensions~/language-lib/Roslyn/VirtualDocumentGenerator.cs`
  L736) replaced JSX subtrees inside attribute values — e.g.
  `onClick={e => <Inner badProp={42} />}` or ternaries returning JSX
  from an inline expression — with `(VirtualNode)null!`, dropping the
  entire subtree from Roslyn's view. Result: bad props, undefined
  components, and type mismatches inside attribute-lambda JSX produced
  zero diagnostics in VS Code / VS 2022 even though the SG and cold
  build flagged them correctly. Fix: extended the strip helper with an
  optional `deferredJsxRanges` list that records every stripped
  subtree position; a new `EmitDeferredJsxAttributeChecks` helper
  parses each subtree with `UitkxParser.Parse` and wraps the
  node-check code in a Pattern-B
  `dynamic __uitkx_jsxattr{pos}() { ... return default!; }` local
  function so Roslyn sees the full type-check graph without affecting
  the runtime emission. Thread-static `t_jsxAttrContext` (set in a
  try/finally around `EmitFunctionStyleBodyCore`) publishes the
  source + directives context to the three deferred sites
  (`EmitExpressionStatement`, `EmitTypedPropsCheck`,
  `EmitBodyWithReturnFix`) without threading two nullable parameters
  through six method signatures. Locked in by
  `AttributeLambda_JsxBody_DeferredPatternBEmitted` +
  `AttributeLambda_WithJsxSubtree_GeneratesVirtualDoc` in
  `RoslynHostTests`.

- **IDE: 10 more hooks resolve in the virtual document.** The VDG's
  static + component stub blocks were missing per-component shadow
  declarations for `useReducer`, `useDeferredValue`,
  `useImperativeHandle`, `useStableFunc`, `useStableAction`,
  `useStableCallback`, `useTweenFloat`, `useAnimate`, `useSafeArea`,
  and `useTransition`. The SG alias-rewrite layer in 0.5.21 sent the
  camelCase forms through to `Hooks.UseXxx(...)`, but the IDE's
  per-component scope shadowed the unqualified call sites so
  hovering / go-to-definition / signature-help silently failed. Both
  stub blocks now mirror the SG's hook-shadow set 1:1 (20 hooks × both
  invocation forms = 40 stubs each side).

- **`UITKX0013` (hook-in-conditional) now covers 10 additional hooks.**
  `HooksValidator.s_hookPatterns` (SG) and
  `DiagnosticsAnalyzer.s_hookPatterns` (language-lib mirror for live
  diagnostics) grew from 30 to 60 strings — 20 public hooks × 3 forms
  (`Hooks.UseX(`, `UseX(`, `useX(`) — so the diagnostic now fires for
  `UseImperativeHandle`, `UseSafeArea`, `UseStableFunc`,
  `UseStableAction`, `UseStableCallback`, `UseAnimate`,
  `UseTweenFloat`, `UseSfx`, `UseUiDocumentRoot`, and `ProvideContext`
  (previously these 10 silently broke React's rules-of-hooks
  contract). `HoverHandler.s_hookDocs` grew from 16 to 40 entries so
  hover over the 12 newer hooks produces the documented signature,
  params, and a UITKX-specific note where the runtime semantics
  diverge from React (e.g. `useTransition`'s synchronous behaviour).

### Tests

- SG suite `1245/1245` passing
  (+1 `Sg_UseTransitionHook_LowercaseAliasRewritten` parity test).
- LSP suite gains 2 new `RoslynHostTests` for the attribute-lambda
  fix (`AttributeLambda_WithJsxSubtree_GeneratesVirtualDoc`,
  `AttributeLambda_JsxBody_DeferredPatternBEmitted`).

## [0.5.21] - 2026-05-19

### Fixed

- **HMR: cascade-batch no longer re-fires mount effects on non-originating
  files (Issue 14.1).** When a `.uitkx` save pulled transitive
  dependents into a batch compile, `ApplySuccessfulCompileResult` ran
  per-file and `UitkxHmrComponentTrampolineSwapper.SwapAll` →
  `NotifyMatchingFibers` unconditionally invoked `FullResetComponentState`
  on every matched fiber whenever the `[HookSignature]` byte-differed
  between SG-emitted and HMR-emitted attributes. For cascade-pulled
  ancestors this wiped `useEffect` cleanups and re-fired mount effects
  on the next render — observed in the wild as additive scenes being
  loaded twice when an unrelated sibling component was edited. Fix:
  `SwapAll` now takes an `allowFullStateReset` parameter (default
  `true`, preserving single-file semantics); the controller threads
  `isOriginatingChange` to it, computed from the cascade walker's
  dependents-first / originator-last ordering
  (`i == paths.Count - 1`). Cascade-pulled siblings/ancestors still
  get the cheap trampoline-field swap (so their next render uses the
  new IL) but retain their fiber state and effect cleanups intact.
  See `Plans~/ISSUE_14_FIX_PLAN.md` Patch 14.1.

- **HMR: wrong-namespace warning spam eliminated (Issue 14.4).**
  `ApplySuccessfulCompileResult` resolved the assembly's namespace via
  `LoadedAssembly.GetTypes().FirstOrDefault().Namespace` — Roslyn's
  embedded `Microsoft.CodeAnalysis.EmbeddedAttribute` materialises first
  in metadata order, so the swapper probed for hook containers under
  `Microsoft.CodeAnalysis.<ContainerClass>` and logged "type not found"
  warnings on every save. Fix: thread the real `@namespace` directive
  value through `HmrCompileResult.Namespace` (set in `Compile`,
  populated in `CompileBatch` per-file results from
  `ComponentBuildArtifacts.Namespace`); the controller reads it
  directly with a name-filtered `GetTypes()` fallback for defensive
  resolution. See `Plans~/ISSUE_14_FIX_PLAN.md` Patch 14.4.

- **camelCase hook aliases work for 7 more public hooks (Issue 15).**
  `useTweenFloat`, `useAnimate`, `useSafeArea`, `useStableFunc`,
  `useStableAction`, `useStableCallback`, and `useImperativeHandle`
  were listed in every layer's `s_hookSignatureRe` (signature
  scanner) but missing from every layer's `s_hookAliases` rewrite
  table and from `s_genericHookAliasRe` for the 3 generic ones.
  Result: writing them in their natural camelCase form in a `.uitkx`
  setup block produced `CS0103: The name 'useTweenFloat' does not
  exist in the current context`, forcing consumers into manual
  `Hooks.UseXxx(...)` qualification with no compiler help. Fix: 7
  alias entries + 3 generic-regex entries added in lockstep to
  `SourceGenerator~/Emitter/CSharpEmitter.cs`,
  `Editor/HMR/HmrCSharpEmitter.cs`, and
  `Editor/HMR/HmrHookEmitter.cs`. Locked in by 7 new
  `Sg_Use*_AliasRewritten` parity-contract tests; full SG suite
  `1244/1244` green.

### Deferred

- **Optimization #1 — HMR dependency index over-links near-clones
  via leftover module tokens.** Copy-renamed files with leftover
  `Marker.X` tokens register as referrers of unrelated modules,
  causing one extra compile per save. No user-visible symptom after
  the Issue 14.1 fix lands. See `Plans~/OPTIMIZATIONS.md` #1.

## [0.5.20] - 2026-05-18

### Added

- **LSP: multi-valued component index (Tech Debt #21, Rank 1).** The
  workspace element index (`WorkspaceIndex._elementInfo`) now stores
  every declarant of a duplicated `component <Name>` instead of
  silently overwriting on the second commit. Existing single-result
  callers continue to use `TryGetElementInfo` (deterministic first
  match); new `GetAllElementInfo` / `GetDuplicateDeclarations` accessors
  expose the full set for diagnostics and rename. `EvictElementsFromFile`
  now removes only the per-file entry, so editing one duplicate no
  longer evicts the entire name globally.

- **`UITKX0113` — duplicate component declaration in same asmdef.** Warning
  fired by `DiagnosticsPublisher` against each duplicate's
  `component <Name>` line. Asmdef-scoped (cross-asmdef collisions are
  legal). Suppressed until the workspace scan completes to avoid
  transient false positives during indexing.

- **`UITKX0211` — `const` inside `module { }` body.** Warning fired by
  the language-lib `DiagnosticsAnalyzer`. Const fields are inlined into
  every consumer's IL at compile time and never propagate under HMR.
  Recommends `static readonly` (which the SG strips and the HMR
  static-swapper refreshes on every edit-save cycle).

- **HMR: workspace-wide dependency index (`UitkxFileDependencyIndex`).**
  New asmdef-aware reverse-edge graph of `.uitkx` modules and JSX
  component consumers. Same async-seed / per-file-invalidate lifecycle
  as `HookContainerRegistry`. Used by the cascade walker (below) to
  enqueue every transitive dependent of a saved file in topological
  order, in the same asmdef.

- **HMR: real FIFO compile queue + cascade walker (Tech Debt #20, Ranks 3 + 4).**
  Replaces the prior single-slot `_compilationQueued` / `_queuedPath`
  fields (which were effectively dead code — `_compilationQueued` was
  never set to `true`, so concurrent saves were dropped). Saving a
  `.uitkx` now enqueues the file plus every transitive module-consumer
  and JSX-consumer in the same asmdef. The queue drains via
  `EditorApplication.delayCall` so the editor stays responsive between
  compiles. Body-only edits to a child component now propagate to
  parent renderers without a manual second save. Module value edits
  (e.g. `Theme.Accent`) propagate through derived module fields
  (`StatsPanel.Container.BorderColor`) automatically.

- **HMR: new-`.cs` pickup (`NewCsFileDiscovery`, Tech Debt #22A, Rank 2).**
  When a new helper `.cs` file is referenced from a `.uitkx` before
  Unity has recompiled the project DLL, HMR now finds and includes
  the new file as an additional Roslyn syntax tree. Asmdef-scoped,
  mtime-gated against `Library/ScriptAssemblies/<asmdef>.dll`, and
  AppDomain type-name deduped to prevent CS0101 against types already
  loaded.

- **HMR: per-SCC union compile (Tech Debt #22B, Rank 5).** When a save
  cascade pulls in 2+ files within one asmdef, the controller now
  drains the whole queue into a single Roslyn compile via the new
  `UitkxHmrCompiler.CompileBatch` entry point. The resulting union
  assembly carries the new shape of every type in the batch, so when
  a parent's render delegate is swapped to its union-DLL version, the
  new `ChildProps` shape resolves to a SINGLE authoritative type across
  parent + child within the same assembly — closing failure mode #22B
  (cascading prop signatures across parent+child saves). Type identity
  is preserved across the HMR boundary via the `IProps` interface,
  matching the existing trampoline contract. Two guards fence the
  union path: a pre-compile `(Namespace, ComponentName)` uniqueness
  check, and a post-compile assembly-identity check confirming every
  batch FQN resolves to the union assembly. On guard or compile
  failure the controller falls back to per-file `Compile` so the
  user-facing error surface (CS0117 / CS0246 / CS0433) is preserved
  ("loud regression over silent wrong-IL", §5.2.1 of the resolution
  plan). Telemetry: `[HMR] union: N files, M ms` on success.

### Tests

- **`WorkspaceIndexDuplicateTests`** (xUnit, `ide-extensions~/lsp-server/Tests/`).
  Six tests pin the multi-valued `WorkspaceIndex` contract against the
  live `Refresh` API with real temp files: two-file duplicate visibility
  via `GetAllElementInfo`, `GetDuplicateDeclarations` reports conflicts,
  single-delete preserves survivor (Wave-1 latent-build regression guard
  for the `EvictElementsFromFile` shape fix), rename-one preserves the
  other declarant's original name, sole-declarant delete clears the
  name entirely, and non-duplicate is omitted from `GetDuplicateDeclarations`.

- **UITKX0211 analyzer tests** (xUnit, `SourceGenerator~/Tests/DiagnosticsAnalyzerTests.cs`).
  Five tests cover the const-in-module warning: fires for `public const`,
  severity is `Warning`, NOT fired for `static readonly`, NOT fired for
  a line-commented const (regex line-awareness), fires per-decl for
  multiple consts in one module body.

### Changed

- **SourceGenerator csproj output split.** The generator now writes its
  build output to `SourceGenerator~/bin/<Configuration>/` and a new
  `PublishGeneratorToAnalyzers` MSBuild target copies the result into
  `Analyzers/` afterwards. Previously `<OutputPath>` pointed directly
  at `Analyzers/`, which collided with Unity Editor's exclusive file
  lock on the live `RoslynAnalyzer` DLL — local `dotnet build` would
  fail with `MSB3027` while Unity was running, the test project
  consumed a stale `TestIso` copy of the DLL, and recent generator
  changes silently failed to land in tests. The new publish target is
  `WarnAndContinue` in Debug (Unity-lock collisions are advisory) and
  `ErrorAndStop` in Release (the publish.yml path, where any failure
  is a real runner problem). Retries 3× at 200ms cover transient
  antivirus / indexer locks. Zero CI behaviour change — `publish.yml`
  already rebuilds the generator fresh before pushing to the dist
  branch, so shipped DLLs remain authoritative.

- **`.github/workflows/test.yml` advisory drift check.** New step
  rebuilds the generator and `cmp`s the result against
  `Analyzers/ReactiveUITK.SourceGenerator.dll` /
  `ReactiveUITK.Language.dll`. Drift produces a GitHub `::warning::`
  annotation (never fails the job) so PR reviewers can spot
  "forgot to commit a rebuilt generator" without changing release
  semantics.

### Notes

- Tech Debt #22B (cross-`.uitkx` prop-signature cascade, Rank 5 in
  `Plans~/TECH_DEBT_20_21_22_RESOLUTION_PLAN.md`) is now shipped in
  this release via the per-SCC union compile path described above.
  Prop-signature refactors across parent + child no longer require a
  manual second save or Stop/Restart of HMR.

## [0.5.19] - 2026-05-15

### Fixed

- **Unity 6.3 style types are now reachable by short name from `.uitkx` source.**
  `StyleMaterialDefinition`, `MaterialDefinition`, `StyleRatio`, `Ratio` and
  `FilterFunction` are now emitted as preprocessor-guarded
  (`#if UNITY_6000_3_OR_NEWER`) `using` aliases in every component, hook,
  module and HMR-recompiled file. Before this fix, expressions such as
  `UnityMaterial = new StyleMaterialDefinition(new MaterialDefinition(mat))`
  failed at compile time with `CS0246: The type or namespace name
  'StyleMaterialDefinition' could not be found` even though the typed
  `Style.UnityMaterial` property compiled fine inside the library itself.
  Affected emitters: `CSharpEmitter`, `ModuleEmitter`, `HookEmitter`,
  `HmrCSharpEmitter`, `HmrHookEmitter`, and the IDE LSP virtual document.

- **HMR's component emitter (`HmrCSharpEmitter`) now emits the same
  targeted `UnityEngine.UIElements` alias block as the source generator.**
  Previously HMR only emitted `using Color = UnityEngine.Color;`, so any
  user expression referencing `EasingFunction`, `BackgroundRepeat`,
  `Length`, `StyleKeyword`, `TextAutoSizeMode`, etc. compiled cleanly on
  cold builds but failed on hot reload with `CS0246`. A new text-level
  parity test (`HmrCSharpEmitterAliasParityTests`) acts as a drift tripwire.

### Added

- **`CssHelpers.MaterialDef(Material)`** returns a ready-to-assign
  `StyleMaterialDefinition` (mirrors the existing `FontDef` precedent).
- **`CssHelpers.Ratio(float value)`** returns a `StyleRatio` so
  `AspectRatio` can be authored without manually wrapping
  `new StyleRatio(new Ratio(...))`.
- **`PropsApplier.unityMaterial`** now accepts a bare `Material` in
  addition to `StyleMaterialDefinition`/`MaterialDefinition` (matches the
  multi-shape precedent already used by `aspectRatio`).

### Notes

- Pure additive release — no API removals, no behavior change for code
  that already uses the verbose `new StyleMaterialDefinition(new
  MaterialDefinition(mat))` form. Both forms continue to compile.

## [0.5.18] - 2026-05-15

### Fixed

- **Critical follow-up to 0.5.17: HMR's Roslyn compile now defines
  `UNITY_EDITOR` (and the full Unity editor define-set when available
  via `CompilationPipeline.GetAssemblies(AssembliesType.Editor)`).**
  Without this, every HMR-compiled DLL had its `__hmr_Render` static
  field AND the `if (HmrState.IsActive) return __hmr_Render(...)`
  trampoline branch in `Render` stripped by the `#if UNITY_EDITOR`
  guards in `HmrCSharpEmitter` and the SG. Net effect:
  - The 0.5.17 "swap on prior HMR DLL types" feature couldn't actually
    work — prior HMR DLLs had no field to write into. Brand-new
    components created live still silently no-op'd after the first
    save. (Symptom in user logs:
    `Component 'X' has no '__hmr_Render' field` warning followed by no
    swap.)
  - User companion .cs `#if UNITY_EDITOR` blocks compiled with opposite
    semantics to the project's actual editor build, a latent
    correctness bug for any user code gated on it.
  Now resolved: HMR DLLs carry the trampoline field and the parent's
  `<NewComponent />` binding actually flows through it on every
  subsequent save. Symbols are pulled from
  `CompilationPipeline.GetAssemblies(AssembliesType.Editor)` so
  version pragmas, scripting backend pragmas, and any user-defined
  symbols match the project's editor compile. Falls back to
  `UNITY_EDITOR`-only if the API is unavailable.

- **Reworded the `no '__hmr_Render' field` warning** in
  `UitkxHmrComponentTrampolineSwapper` to reflect both possible causes
  (pre-trampoline-refactor SG output OR a stale HMR DLL from a
  pre-0.5.18 session) and to point at the correct remediation
  (restart Unity).

### Notes

- If you have a Unity session running with HMR DLLs from 0.5.17 or
  earlier in `%TEMP%/UitkxHmr/`, restart Unity once after upgrading.
  Subsequent sessions emit HMR DLLs with the trampoline field intact.

## [0.5.17] - 2026-05-15

### Added

- **HMR support for components created live (no domain reload required).**
  The trampoline swap now targets every loaded type representing the
  changed component — both the project-loaded type (post domain reload)
  AND every prior `hmr_*.dll` type from earlier HMR cycles. Brand-new
  components created during a session previously had no project-side type
  for `SwapAll` to find, so saves silently no-op'd after the first compile.
  Now the first parent that references the new component binds (via
  compiler-cached method-group delegate) to the HMR DLL's type, and every
  subsequent edit writes the fresh delegate into that DLL's
  `__hmr_Render` static field — the parent's binding hits the new body on
  the next render. Symmetric across multiple HMR generations: version N+1
  updates every prior generation's trampoline so all live consumer
  bindings stay current. See `Plans~/HMR_NEW_COMPONENT_LIVE_SWAP_PLAN.md`.

- **AssetPostprocessor now forwards `deletedAssets` and `movedFromAssetPaths`**
  to the file watcher via a new `OnUitkxDeleted` event. The controller
  uses this to evict stale entries from `_pendingRetryPaths` so a
  rename / delete during HMR no longer leaves a permanent retry-loop
  pointing at a non-existent file.

### Fixed

- **`_pendingRetryPaths` no longer accumulates entries for files that no
  longer exist on disk.** Two complementary mechanisms: (a) the new
  `OnUitkxDeleted` postprocessor path evicts on file delete / rename;
  (b) the controller's compile-failure branch now does a `File.Exists`
  check before adding to the retry queue and evicts on miss
  (belt-and-braces against deletions performed outside the Project
  window). Eliminates the `FileNotFoundException` cascade observed when
  cloning components via copy-rename-edit while HMR is active.

- **Visible log when the first compile of a brand-new component has no
  consumer yet.** Previously `SwapAll` returned 0 silently in this case,
  giving the impression HMR was dead. Now it logs `[HMR] Compiled X — no
  live consumer types yet (component is brand-new; subsequent edits will
  hot-swap once a parent compiles against it)` so the journey is visible.

### Notes

- Editor-only changes; runtime / built-game performance is unaffected.
  The trampoline field `__hmr_Render` and its single `if (HmrState.IsActive)`
  branch in generated `Render` were already present in 0.5.16 and are
  unchanged. Player builds strip the entire HMR editor assembly.

## [0.5.16] - 2026-05-15

### Fixed

- **HMR silently dropped file-change events under save bursts on a deep
  Assets/ tree** (the original bug 0.5.14 and 0.5.15 tried and failed to fix).
  Mono's `FileSystemWatcher` on Windows uses an 8 KB internal buffer; under
  realistic Unity save bursts (every save touches `.meta` files plus assorted
  side-files) the buffer overflows and the OS silently drops events for
  arbitrary files. Symptom: parent component saves hot-reloaded, but a
  deeply-nested child component (e.g.
  `Assets/UI/Pages/GamePage/components/PlayerHud/components/StatsPanel/StatsPanel.uitkx`)
  saved repeatedly with no `[HMR]` console output and no visual change.

  The 0.5.14/0.5.15 attempts modified `FileSystemWatcher` directly
  (`InternalBufferSize`, `Error` handler, `EnableRaisingEvents` ordering).
  Empirically that proved fragile on Mono 6.13.0 (Visual Studio built mono):
  the watcher silently fell into a state where it stopped delivering events
  entirely, killing HMR end-to-end. Both fixes have been reverted; the FSW
  configuration block in `Editor/HMR/UitkxHmrFileWatcher.cs` is byte-identical
  to the 0.5.13 known-good state.

  The actual fix this release ships is a **parallel event source** via
  Unity's `AssetPostprocessor.OnPostprocessAllAssets`. It fires on the main
  thread whenever Unity refreshes the asset database after a save, never
  drops events, and does not depend on Mono FSW. The watcher's existing
  `_pendingChanges` dictionary already deduplicates by path, so redundant
  events from FSW + AssetPostprocessor are coalesced into a single swap by
  the existing 50 ms debounce window. New file:
  `Editor/HMR/UitkxHmrAssetPostprocessor.cs`. Registration is gated by
  `UitkxHmrFileWatcher.Start` / `Stop` so the postprocessor is a no-op when
  HMR is not active.

### Notes

- 0.5.14 and 0.5.15 are superseded. If you upgraded to either, you should
  upgrade straight to 0.5.16 — do not stay on 0.5.14/0.5.15.
- The "Verbose watcher trace" toggle added in 0.5.14 is still present in
  the HMR window. It is harmless leftover; full wire-up is deferred until
  there is a concrete next-debugging need.

## [0.5.15] - 2026-05-15

### Fixed

- **Hotfix: HMR file watcher initialization order broke event delivery on Mono
  (regression in 0.5.14).** The 0.5.14 watcher hardening set
  `InternalBufferSize = 64*1024` and `EnableRaisingEvents = true` inside the
  `FileSystemWatcher` object initializer, which on Unity's Mono runtime
  enables the watcher BEFORE event handlers are wired and BEFORE the buffer
  size is applied. On some Mono versions this combination leaves the
  watcher in a half-initialized state where it never raises any events.
  Symptom: HMR Start logs normally but no `[HMR]` messages appear on save
  and no `[HMR][trace] FSW ...` lines appear with verbose-trace enabled.
  The watcher is now configured property-by-property in the correct order:
  configure paths/filters first, subscribe handlers second, set
  `InternalBufferSize` (wrapped in try/catch with a fall-back warning if
  Mono refuses) third, and `EnableRaisingEvents = true` last. If
  `InternalBufferSize` cannot be raised, HMR continues to run with the
  default 8 KB buffer instead of going dark.

## [0.5.14] - 2026-05-15

### Fixed

- **HMR silently dropped file-change events under save bursts.** The
  `FileSystemWatcher` in `Editor/HMR/UitkxHmrFileWatcher.cs` ran with the
  default 8 KB internal buffer and had no `Error` subscription. Watching the
  full `Assets/` tree easily overflows that buffer because Unity touches
  `.meta`, `Library/`, and other side-files on every save, and on overflow
  the OS silently drops events for arbitrary files. Symptom: parent
  component saves hot-reloaded normally, but a deeply-nested child
  component (e.g.
  `Assets/UI/Pages/GamePage/components/PlayerHud/components/StatsPanel/StatsPanel.uitkx`)
  saved repeatedly with no `[HMR]` console output and no visual change —
  removing the component from its parent and re-adding it appeared to
  "fix" things (because that triggered a fresh fiber mount that picked up
  the project-loaded type). The buffer is now bumped to 64 KB (the
  documented maximum) and `FileSystemWatcher.Error` is logged at error
  level so future overflows surface instead of vanishing. Restart HMR
  (Stop -> Start in the HMR window) for the new buffer size to take effect.

### Added

- **HMR window: "Verbose watcher trace" toggle.** When enabled, every raw
  `.uitkx` / `.uss` / `.cs` file event the OS delivers is logged as
  `[HMR][trace] FSW <ChangeType> <path>`. Use this when a save appears to
  do nothing — if no trace line appears for your file, the OS itself
  isn't delivering the event (FSW buffer overflow recurrence, antivirus
  hook, OneDrive/symlink path, file held by another process) and the
  problem is upstream of HMR. Backed by `EditorPrefs` key
  `UITKX_HMR_VerboseWatcher`; off by default. Visible in
  ReactiveUITK -> HMR Mode under Settings, alongside the existing
  Auto-stop and Auto-reload toggles.

## [0.5.13] - 2026-05-15

### Fixed

- **Editor/HMR namespace mismatch on `HookContainerRegistry` and `AsmdefResolver`
  (regression in 0.5.12).** The two new Editor-only files shipped under
  `namespace ReactiveUITK.Editor.HMR` while every other file in `Editor/HMR/`
  uses `namespace ReactiveUITK.EditorSupport.HMR`. Unity Editor compile of
  consumer projects failed with `CS0103: The name 'HookContainerRegistry' does
  not exist in the current context` (and the same for `AsmdefResolver`) at the
  five call sites in `UitkxHmrController.cs` and `UitkxHmrCompiler.cs`, plus
  one cascade `CS0019` from the broken type binding. The dotnet test suite did
  not catch this because the Editor folder is not exercised by the SG/LSP
  test projects — only Unity's own csc invocation links these files together.
  Both new files now declare `namespace ReactiveUITK.EditorSupport.HMR` to
  match the rest of the folder. No behaviour change vs. 0.5.12 once compiling.

## [0.5.12] - 2026-05-14

### Fixed

- **Cross-directory and cross-namespace hook resolution across SG, HMR, and IDE.**
  Previously, a `.uitkx` component could only see hooks declared in a peer
  `.uitkx` file when that peer lived in the same folder AND in the same
  `@namespace`. Hook files like `Assets/UI/Hooks/UseUiDocumentSlot.hooks.uitkx`
  in namespace `PrettyUi.UIHooks` consumed by components in
  `Assets/UI/Pages/...` in namespace `PrettyUi.UI.Pages` required a manual
  `using static` directive at every consumer, and HMR recompiles silently
  dropped the directive entirely. Three layers were fixed in lockstep:

  - **Source generator (Stage 3d).** `UitkxPipeline` no longer requires the
    consumer's `@namespace` to match the hook file's. Asmdef ownership is
    already enforced by `UitkxGenerator`'s pre-scan via `IsOwnedByCompilation`,
    so injection is now unconditional within an asmdef and de-duplicated via
    a hash set against the existing `@using` set.
  - **LSP virtual document.** `RoslynHost.EnrichWithPeerHookUsings` mirrors the
    SG fix: drops the strict-namespace check, switches the FQN to the hook
    file's own namespace, and gates injection by asmdef ownership via a new
    `AsmdefResolver` helper. `WorkspaceIndex` now tracks every indexed `.cs`
    file (`_allCsFiles` / `GetAllCsFiles()`) so `FindCompanionFiles` unions
    same-folder `.cs` with workspace-wide `.cs` filtered to the consumer's
    asmdef.
  - **Editor HMR.** New `HookContainerRegistry` (seeded asynchronously from
    `UitkxHmrController.Start`, invalidated per-file by the watcher, reset on
    `Stop`) gives `UitkxHmrCompiler.EmitCompanionUitkxSources` the cross-
    directory hook FQNs without scanning the workspace per recompile. The
    same-folder companion scan still emits inline source for module/hook
    partials; the registry only contributes `using static` lines for hook
    classes already compiled into the loaded assembly.

  `AsmdefResolver` is mirrored verbatim across `Editor/HMR/` and the LSP
  server; SG keeps the original implementation in `UitkxPipeline`. A new
  `AsmdefResolverParityTests` set pins the no-asmdef Editor / non-Editor
  fallback contract. Closes TECH_DEBT_V2 #18 and #19.

### Tests

- SG: **1228/1228 passing** (+6: cross-namespace, no-namespace, asmdef-boundary
  injection coverage; asmdef-resolver parity).
- LSP: **63/63 passing** (+1: cross-namespace virtual-doc enrichment).

VS Code **1.2.7 → 1.2.8** | VS 2022 **1.2.7 → 1.2.8**.

## [0.5.11] - 2026-05-13

### Fixed

- **LSP Go-To-Definition now resolves module/hook symbols across directories.**
  Jumping to a `Theme.SidebarWidth` reference from a `.uitkx` file in one
  folder used to return nothing when the declaring `Theme.uitkx` lived in a
  different folder, because `RoslynHost.FindPeerUitkxFiles` only enumerated
  same-directory peers. `WorkspaceIndex` now tracks a workspace-wide set of
  `.uitkx` files containing top-level `module` or `hook` declarations
  (`_moduleHookFiles`), exposed via `GetModuleAndHookFiles()` and appended to
  the per-document peer set. Roslyn then loads them as workspace documents
  and resolves the cross-directory symbol naturally. The three downstream
  consumers (`EnrichWithPeerHookUsings`, `AddPeerUitkxDocuments`,
  `AddPeerUitkxDocumentsToSolution`) already filter peers by
  `HookDeclarations`/`ModuleDeclarations` so the wider candidate set is
  cost-free for non-module/hook files. Tooling-only release; no runtime,
  editor, source-generator, or shared changes.

VS Code **1.2.6 → 1.2.7** | VS 2022 **1.2.6 → 1.2.7**.

## [0.5.10] - 2026-05-13

### Changed

- **Component HMR rewritten as a static trampoline field — eliminates per-fiber
  rollback closures and stale renders on rapid saves.** The source generator now
  emits every `component` as a `static` trampoline triplet: an `internal static`
  delegate field `__hmr_Render` (initialized to a private static body method
  `__Render_body`), a public `Render` entry point that branches on
  `HmrState.IsActive` and forwards to either the field or the body directly,
  and the body method itself. The HMR-side emitter (`HmrCSharpEmitter`) mirrors
  the same shape so HMR-compiled and project-compiled assemblies are
  byte-identical at the call site. This collapses the legacy "wrap each fiber's
  `TypedRender` in a per-instance closure that captures the new delegate, then
  walk the entire tree replacing closures on every save" model into a single
  `FieldInfo.SetValue` per component type.

  The new `UitkxHmrComponentTrampolineSwapper` (replaces the component branch
  of `UitkxHmrDelegateSwapper.SwapAll`) does the swap in one O(1) field write,
  then notifies fibers of the changed component type via a bounded walk that
  only touches fibers whose `TypedRender.Method.DeclaringType == oldType` —
  much cheaper than the previous global tree pass. A `ConcurrentDictionary<Type,
  Delegate>` rollback registry is bridged to the runtime via
  `HmrState.TryRollbackComponent` (a `Func<Type, bool>` wired by
  `[InitializeOnLoadMethod]`), so the reconciler can revert a single bad swap
  on an exception without per-fiber bookkeeping. The previous per-fiber
  `HmrPreviousRender` field on `FiberNode` and its ~36 lines of rollback
  bookkeeping in `FiberReconciler` have been deleted; the `IsCompatibleType`
  HMR-only fallbacks in both `FiberFunctionComponent.IsCompatibleType` and
  `FiberChildReconciliation` (the source-path attribute fallback) have been
  deleted too — Roslyn's per-call-site method-group cache now naturally keeps
  `ReferenceEquals(fiber.TypedRender, vnode.TypedFunctionRender)` stable across
  HMR cycles once the trampoline stabilizes the underlying method group.

  User-visible effect: rapid saves (30+ Ctrl+S in a few seconds) no longer
  leak stale renders, navigating away from and back to a hot-edited component
  shows the new code immediately, and incompatible hook-signature edits reset
  state in-place without a domain reload (React Fast Refresh semantics — see
  HMR docs page).

### Fixed

- **HMR rude-edit domain reloads now Play-mode-safe.** `UitkxHmrController`
  routes rude-edit-triggered domain reloads through a Play-mode guard. Calling
  `EditorUtility.RequestScriptReload()` (or `CompilationPipeline.RequestScriptCompilation`)
  while in Play mode produces partial reloads that leave `MonoBehaviour`
  instances with broken script references; the reload is now deferred until
  the next `EnteredEditMode` transition. `UitkxHmrModuleStaticSwapper` warning
  text updated to mention the deferral.

- **HMR Stop no longer stalls 30-40s on tiny projects.** `UitkxChangeWatcher`
  no longer passes `RequestScriptCompilationOptions.CleanBuildCache` on HMR
  triggers. Cold-restarting every analyzer and source generator (Roslyn,
  ReactiveUITK.SourceGenerator, every other analyzer in the project) was
  costing tens of seconds per stop; the trigger-file rewrite already
  invalidates the .cs side, and Roslyn's content-hashed `AdditionalText`
  cache picks up modified `.uitkx` files via normal incremental compilation
  without a clean rebuild.

- **LSP: false "unused local" diagnostic on hook setter/value pairs consumed
  inside lambdas.** `RoslynHost.AnalyzeUnusedLocals` now includes
  `dataFlow.Captured` in the "read set" when computing unused-locals
  diagnostics. Captures by nested lambdas / local functions are by definition
  a future use; flagging them as unused was a false positive on hook setter
  pairs (`var (count, setCount) = useState<int>(0);`) that are only consumed
  inside event-handler lambdas (e.g. `<Button onClick={() => setCount(count + 1)} />`)
  or JSX fragments lowered to render lambdas. Adds a regression test in
  `RoslynHostTests`.

### Documentation

- HMR docs page updated: incompatible hook-signature edits (changed hook count,
  order, or types) are now described as in-place state resets with the correct
  console message (`[HMR] Hook signature changed in <Component> — resetting
  state on all instances.`) and an explicit note that the reset happens without
  a domain reload (React Fast Refresh semantics).

### Internal

- `Editor/HMR/UitkxHmrComponentTrampolineSwapper.cs` (new file).
- `Editor/HMR/UitkxHmrDelegateSwapper.cs` trimmed to `SwapHooks`,
  `TriggerGlobalReRender`, `ScheduleFullTreeUpdate`; component branch
  retired.
- `Shared/HmrState.cs` exports `TryRollbackComponent` bridge.
- `Shared/Core/Fiber/FiberNode.HmrPreviousRender`, the corresponding
  `FiberFactory` clone line, the post-commit clear in `FiberReconciler`, and
  both `IsCompatibleType` HMR fallbacks deleted.
- 1222 SG tests passing (+2 new `HmrEmitterParityContractTests`:
  `Sg_FunctionComponent_GeneratesRenderTrampoline`,
  `Sg_FunctionComponent_BodyContainsHooksAndSetup_TrampolineStaysThin`).

## [0.5.9] - 2026-05-12

### Fixed

- **B28 — HMR now refreshes `module` `static readonly` fields without a
  domain reload.** Editing a module-scope `Style`, `Color`, or any other
  `static readonly` field initializer in a `.uitkx` file (for example
  changing `PaddingTop = 4` to `16` in a `Sidebar` style module) used
  to report a successful HMR cycle but the rendered UI kept showing
  the cold-build value until you exited Play mode. Already-mounted
  components picked up the change (because their `Render` delegate
  was hot-swapped to the freshly compiled body); newly-mounted
  components on subsequent navigation kept reading the **old**
  reference. Root cause confirmed via byte-level IL diagnostics: the
  Mono JIT inlines the object reference for `ldsfld <static readonly>`
  into native code at first call-site emission. The HMR swapper's
  `FieldInfo.SetValue` correctly writes the new instance into the
  field slot, but already-JIT'd methods continue to read the inlined
  cold reference.

  The fix is permanent and applies in IL2CPP and Mono AOT player
  builds too (we deliberately keep Editor and Player IL identical so
  HMR remains a faithful Player preview). The source generator now
  strips the `readonly` modifier from every top-level `static readonly`
  field in a `module { … }` body and decorates the rewritten field
  with `[global::ReactiveUITK.UitkxHmrSwap]`. The HMR pipeline mirrors
  the rewrite in `HmrHookEmitter.EmitModules` via a hand-written
  tokenizer (`HmrStaticReadonlyStripper`) so the Editor assembly does
  not need to take a direct dependency on Microsoft.CodeAnalysis. The
  same transformation is applied to the two generator-managed module
  statics: the `__sty_N` style-hoist fields and the `__uitkx_ussKeys`
  array. The hook-cache `static readonly ConcurrentDictionary` field
  (whose reference is genuinely immutable; only its contents are
  HMR-replaced) is deliberately left as `initonly` and continues to
  be matched by the swapper via `FieldInfo.IsInitOnly`.

  `UitkxHmrModuleStaticSwapper`'s eligibility predicate now accepts
  `HasUitkxHmrSwapAttribute(f) || f.IsInitOnly` so both the new
  `[UitkxHmrSwap]` mutable statics and legacy `initonly` fields are
  refreshed in one pass. Per-access cost is one extra static-slot
  load (~1 ns, L1-cached, single `mov`); a 50-button Sidebar pays
  ~50 ns/frame, far below noise.

### Added

- **`ReactiveUITK.UitkxHmrSwap` attribute** (under `Shared/Core/`) — the
  source-generator-emitted marker that opts a field into HMR-managed
  re-initialization. The attribute is the live semantic distinction
  between user-immutable module statics (where writes overwrite an
  initializer the HMR pipeline owns) and ordinary mutable statics
  (lazy caches, counters) whose value should carry across HMR cycles.
- **`UITKX0210` analyzer warning** (Roslyn). Flags writes to
  `[UitkxHmrSwap]` fields from anywhere other than the containing
  type's static constructor. The HMR pipeline will overwrite any
  external write on the next save, so the rule surfaces the bug
  ahead of time. Categories: `SimpleAssignment`, `CompoundAssignment`,
  `Increment`, `Decrement`. Allowed when the containing symbol is
  `MethodKind.StaticConstructor`. Suppress with
  `#pragma warning disable UITKX0210` if intentional.

### Documentation

- HMR docs page corrected: module saves no longer claim to trigger a
  domain reload. The new contract (re-init `static readonly` fields,
  hot-swap `static` methods, preserve mutable `static` fields) is
  spelled out alongside the rude-edit and field-vs-static-auto-property
  caveats.
- Diagnostics reference page picked up the `UITKX0210` row.

### Known limitations

- **Static auto-properties** (`public static Style Root { get; } = …`).
  The C# compiler lowers these to a private `static readonly` backing
  field that the source generator cannot see during emission, so the
  JIT inlines the cold reference and HMR cannot refresh it. For
  HMR-able module values prefer fields:
  `public static readonly Style Root = new Style { … }`. Promotion of
  static auto-properties into HMR-able backing fields is on the
  roadmap.
- **Newly added** `static readonly` fields mid-session remain a CLR
  rude edit; the project type's metadata cannot grow at runtime. The
  existing once-per-session warning is unchanged.

### Tests

- 1218/1218 SG passing (1198 pre-existing plus 20 new): 9 stripper
  unit tests (multi-declarator, generic type, attributes, XML doc,
  const-untouched, mutable-untouched, instance-readonly-untouched),
  6 analyzer tests (write outside cctor flagged, write inside cctor
  allowed, no false positives, compound/increment flagged, field
  initializer allowed), 5 end-to-end module-strip tests (single
  field, multi-field, const-untouched, mutable-untouched,
  attribute-preservation).

## [0.5.8] - 2026-05-11

### Fixed

- **`[OnOpenAsset]` migration to `EntityId` callbacks on Unity 6.3+.** The
  Console hyperlink navigation hook in `UitkxConsoleNavigation.cs`
  surfaced `CS0618` warnings on 6.3 / 6.4 because every `int↔EntityId`
  conversion on `EntityId` is `[Obsolete]` — including the implicit cast
  operator itself, which carries the deprecation message *"EntityId will
  not be representable by an int in the future. This casting operator
  will be removed in a future version."* A reflection probe of
  `UnityEngine.CoreModule.dll` on 6000.3.8f1 and 6000.4.6f1 revealed
  that the clean migration is not to convert `int → EntityId` at the
  call site but to let `[OnOpenAsset]` hand us an `EntityId` directly:
  Unity's `OnOpenAssetAttribute` accepts both the legacy
  `(int, int, int)` callback shape and a new `(EntityId, int, int)`
  shape on 6.3+. The four registered callbacks (`OnOpenAssetPriority`,
  `OnOpenAssetPriorityCompat`, `OnOpenAssetCompat`, `OnOpenAssetCompat2`)
  are now split by `#if UNITY_6000_3_OR_NEWER`:
  - 6.3+ branch: callbacks take `EntityId entityId` and forward to an
    `EntityId`-typed `HandleOnOpenAsset` overload that calls
    `AssetDatabase.GetAssetPath(entityId)` and
    `EditorUtility.EntityIdToObject(entityId)` directly. Zero
    `int↔EntityId` conversions, zero obsolete APIs touched.
  - Pre-6.3 branch: retains the original `int`-typed callbacks verbatim,
    since `EntityId` does not exist on the package's minimum supported
    Unity 6000.2.

  The version-independent path-resolution and external-editor-launch
  logic was extracted into a shared `ResolveAndDispatch(assetPath, line,
  column)` so neither branch duplicates the resolver. Tech-debt item 16
  closed with full reasoning so the dead-end cast approach is not
  re-attempted.

- **`DoomTextures` sample — `CS8618` on non-nullable lazy fields.** Six
  fields (`_walls`, `_floors`, `_sprites`, `_sky`, `_faces`, `_weapons`)
  were declared as non-nullable `Texture2D` / `Texture2D[]` but populated
  lazily by `EnsureBuilt()` after first read. The compiler flagged each
  with `CS8618` ("Non-nullable field must contain a non-null value when
  exiting constructor"). Suffixed each declaration with `= null!`,
  idiomatic for framework-initialized-later state. Every public getter
  routes through `EnsureBuilt()` so consumers never observe the null
  state — zero behaviour change. Tech-debt item 17 closed.

VS Code **1.2.3 → 1.2.4** · VS 2022 **1.2.3 → 1.2.4**.

## [0.5.7] - 2026-05-11

### Fixed

- **`<Portal>` survives Unity 6.3 panel rebuilds.** When a `<Portal target={x}>`
  was rendering into a world-space `UIDocument`, clicking that document in
  the Hierarchy (or any other action that triggered Unity 6.3's silent
  `rootVisualElement` rebuild — see 0.5.6) caused the portal contents to
  disappear. Diagnostic logs confirmed the world panel's root was being
  swapped repeatedly with `childCount=0`: 0.5.6's `Hooks.UseUiDocumentRoot`
  correctly re-fired the consumer with the new root reference, but the
  Fiber commit phase had no path for moving an existing portal's children
  from the old target VisualElement to the new one. The Portal HostFiber's
  `PortalTarget` and `HostElement` were refreshed by `FiberFactory.CloneFiber`,
  newly-mounted children were placed into the new target, deleted children
  were removed from the old — but **stable** children (the common case)
  remained parented to the dead target.

  Fixed at the right architectural layer — the commit phase, mirroring the
  shape of 0.5.6's `RetargetContainer`:

  - New `EffectFlags.PortalRetarget` (bit 6) — set in `CompleteWork` for any
    `HostPortal` fiber whose `PortalTarget` reference no longer matches its
    alternate's. One `ReferenceEquals` per portal fiber per render; no cost
    when the target is stable.
  - New `CommitWork` branch invokes `CommitPortalRetarget`, which performs a
    bounded depth-first walk of the portal's fiber subtree, descending only
    through non-host wrappers (`Fragment`, `FunctionComponent`, `ErrorBoundary`,
    `Suspense`) to the first host descendant on each branch. Reparenting one
    `VisualElement` carries its full UI Toolkit subtree along, so no per-VE
    recursion is needed. Nested `HostPortal` fibers are skipped — they own
    their own targets.
  - `_hostConfig.AppendChild` (which calls `parent.Add(child)`) transparently
    removes the child from its previous parent first, so the retarget is
    safe even when the old target VisualElement has already been disposed
    by Unity (the 6.3 rebuild scenario this fix exists for).
  - Null-target case detaches the portal's host descendants cleanly so they
    do not linger as orphans of a dead panel root.

  Combined with 0.5.6's `Hooks.UseUiDocumentRoot`, world-space portals are
  now resilient to the full 6.3 rebuild storm: the hook re-fires with the
  new root, the consumer renders `<Portal target={newRoot}>`, and the
  reconciler reparents the existing portal subtree into the new root in
  the same commit. Steady-state cost is one `ReferenceEquals` per portal
  per render; retarget cost is `O(top-level host descendants)` and only
  runs on actual rebuild events.

- **Source generator and HMR emitter — lowercase `useUiDocumentRoot` alias
  and IDE virtual-document stubs.** 0.5.6 added `Hooks.UseUiDocumentRoot`
  to the hook signature regex (so it counted as a hook for ordering
  diagnostics) but missed three downstream sites: the `s_hookAliases`
  rewrite table in both `SourceGenerator~/Emitter/CSharpEmitter.cs` and
  `Editor/HMR/HmrCSharpEmitter.cs` plus `HmrHookEmitter.cs`, and the
  Roslyn virtual-document stubs in
  `ide-extensions~/language-lib/Roslyn/VirtualDocumentGenerator.cs`.
  The result was that lowercase `useUiDocumentRoot(...)` in `.uitkx`
  produced `CS0103: The name 'useUiDocumentRoot' does not exist in the
  current context` both in Unity build output and in the IDE LSP
  preview — only the fully-qualified `Hooks.UseUiDocumentRoot(...)` form
  worked. All four sites are now in sync; the lowercase form participates
  on identical terms with `useState` / `useEffect` / `useContext` / etc.

### Changed

- **`AppBootstrap` portal-stage seeding** in samples now stashes the
  `UIDocument` itself (not its `rootVisualElement`) into the
  `HostContext.Environment` slot, and the consuming component reads it
  via `Hooks.UseUiDocumentRoot(contextKey)`. The previous pattern (seed
  the root directly) is still supported but does not survive panel
  rebuilds. Updated example: `MenuPage.uitkx` in the Pretty UI sample.

### Known limitations

- **Editor-only: portal contents are non-interactive *while* a Unity 6.3
  panel-rebuild storm is in progress.** Selecting a world-space
  `UIDocument` in the Hierarchy triggers Unity 6.3's
  `InspectorWindow.RedrawFromNative` to rebuild `rootVisualElement`
  *every frame* for as long as the document is selected. Our retarget
  keeps the chrome painted, but UI Toolkit's per-panel event-dispatcher
  state (`FocusController`, pointer capture, hover tracking) is owned by
  the panel and is destroyed and recreated each frame alongside the
  root. `PointerDownEvent` may fire on a child of root R<sub>n</sub> and
  the matching `PointerUpEvent` arrives after R<sub>n+1</sub> already
  replaced it, so clicks, hover, and focus traversal do not land. The
  same applies to non-portal Reactive trees attached to the affected
  document. Deselecting the document (or selecting any other Hierarchy
  object) stops the storm immediately and full interactivity returns
  within one frame. The behaviour does not exist in Player builds —
  `RedrawFromNative` is an Editor-only path. There is no framework-side
  fix: any attempt would require synthesising panel-internal event state
  across rebuilds, which is brittle against Unity's private surface and
  would be a poor trade for an Editor-only upstream regression that has
  already been reported. Once Unity ships the fix, occasional one-shot
  rebuilds keep working transparently through the existing 0.5.6/0.5.7
  plumbing.


### Fixed

- **Unity 6.3 panel-rebuild defense — UIs no longer disappear on Inspector
  interaction.** Unity 6.3 silently recreates `UIDocument.rootVisualElement`
  on `InspectorWindow` redraws (selection change, hover over fields, focus,
  property edits). Confirmed via standalone repro probe — fires zero events
  on 6.2, repeated `DetachFromPanelEvent` → new `VisualElement` instance on
  6.3 with call stack ending at `UnityEditor.InspectorWindow.RedrawFromNative`.
  Reported to Unity; distinct from UUM-47682 (closed "By Design" for the UI
  Builder Live Reload trigger). There is no public API to detect the rebuild,
  so the only viable defense for a reactive UI framework is to poll
  `rootVisualElement` and migrate the rendered tree on swap. The fix is
  layered across three plans:

  1. **`RootRenderer.Initialize(UIDocument hostDoc, ...)` overload** (new).
     When you construct a `RootRenderer` against a `UIDocument` (instead of
     a raw `VisualElement`) it subscribes to a per-frame poll via
     `AnimationTicker` and, on swap, calls `VNodeHostRenderer.RetargetHost`
     which re-applies the captured host props and forwards to
     `FiberRenderer.RetargetContainer`. `RetargetContainer` snapshots the
     existing child array and re-adds each child to the new root container,
     preserving the entire fiber tree, all hook state, and all
     `VisualElement` subscriptions through Unity's rebuild. The original
     `Initialize(VisualElement)` overload is unchanged — opt-in only.

     ```csharp
     // before — vulnerable to Unity 6.3 rebuilds
     rootRenderer.Initialize(uiDocument.rootVisualElement);

     // after — survives rebuilds via poll + retarget
     rootRenderer.Initialize(uiDocument);
     ```

  2. **`UseUiDocumentRoot` hook** (new). Returns a stable
     `VisualElement` reference that always tracks the current
     `UIDocument.rootVisualElement`. Polls via `AnimationTicker` and updates
     state with a structural `ReferenceEquals` short-circuit (no re-render
     unless the root actually changed). Two overloads:

     ```csharp
     // by UIDocument instance
     var root = Hooks.UseUiDocumentRoot(myUIDocument);

     // by HostContext key (when bootstrap injects the document)
     var root = Hooks.UseUiDocumentRoot("uiDocument");
     ```

  3. **Reparent-resilient element adapters.** `VideoElementAdapter`,
     `MultiColumnListViewElementAdapter`, `MultiColumnTreeViewElementAdapter`,
     and `TabViewSelectionTracker` previously tore down their underlying
     state on `DetachFromPanelEvent`. Under the 6.3 rebuild pattern
     (detach → reattach in same frame) this destroyed state that was about
     to be reused. They now route through a new `PanelDetachGuard.Wire`
     helper which defers teardown one frame via `MainThreadTimer.OneFrameLater`
     and cancels it if the element re-attaches before the deferred frame
     runs. `VideoElementAdapter` additionally calls `Setup` only on the
     true first attach so reattach is a no-op.

### Added

- **`Shared/Core/Animation/AnimationTicker.cs`** — panel-independent shared
  ticker for animation/poll subscribers. One `Action onTick` per
  subscriber, internally hooked once via `EditorApplication.update` (Editor)
  or `MediaHost.SubscribeTick` (Player). `Subscribe(Action) => Action unsubscribe`.
  `Animator.PlayTrack` migrated off panel-attached scheduling onto this
  ticker so animation clocks advance regardless of attach/detach state;
  style writes are gated on `ve.panel != null` so detached elements do not
  paint stale frames. Used by `RootRenderer.SubscribeToHostDocument` and
  `UseUiDocumentRoot` for the rebuild poll. ~3–5 ns per subscriber per
  frame; zero allocations on the hot path.

- **`Shared/Core/MainThreadTimer.cs`** — `OneFrameLater(Action callback) =>
  Action cancel`. One-shot main-thread continuation backed by
  `EditorApplication.update` (Editor) or `MediaHost.SubscribeTick`
  (Player). Self-removes after firing; `cancel()` is idempotent. Used by
  `PanelDetachGuard` for deferred adapter teardown.

- **`Shared/Core/PanelDetachGuard.cs`** — `Wire(VisualElement ve, Action
  teardown)`. Registers a `DetachFromPanelEvent` listener on `ve` that
  schedules `teardown` one frame later. If the element re-attaches before
  the deferred frame runs, the pending teardown is cancelled. Centralises
  the reparent-resilient pattern used by the three column/tab/video
  adapters.

### Changed

- **`VNodeHostRenderer.hostElement` is now mutable** so `RetargetHost(nextHost)`
  can swap it on Unity-induced rebuilds. Last-applied host props are
  captured per render so the new host receives the same prop pass.
  `internal` API — not a public-surface change.

- **`FiberRenderer.RetargetContainer(VisualElement nextContainer)`** added
  (internal). Snapshots `_container.Children().ToArray()` and re-adds each
  to `nextContainer`, then updates `_container`, `_root.ContainerElement`,
  and `_root.Current.HostElement`. O(N) in number of direct children of
  the renderer's container — runs once per Unity-induced rebuild, not
  per frame.

- **`Shared/AssemblyInfo.cs`** — `[InternalsVisibleTo("ReactiveUITK.Runtime")]`
  is now always-on (previously editor-gated) so `RootRenderer` can call
  the new internal retarget API and subscribe to `AnimationTicker`.

### Source generator / HMR

- **Hook regex whitelist extended** with `useUiDocumentRoot` /
  `UseUiDocumentRoot` in both `SourceGenerator~/Emitter/CSharpEmitter.cs`
  and `Editor/HMR/HmrCSharpEmitter.cs` so the new hook participates in
  hook-signature detection on identical terms with the rest of the suite.

### Performance

- Steady-state cost of the rebuild defense is ~10 ns/frame per
  `RootRenderer` initialised against a `UIDocument` (one property read +
  one `ReferenceEquals`). `UseUiDocumentRoot` consumers cost the same per
  hook instance. No allocations on the poll hot path.
- The retarget path (O(N) re-add of direct children) executes only on
  actual Unity rebuilds, which happen exclusively in the Editor when the
  Inspector window is repainted. Built players never trigger the bug, so
  the retarget code is dead weight in production builds (one ReferenceEquals
  per frame).
- `Animator` migration to `AnimationTicker` is performance-neutral or
  better: the per-tick body skips style writes when the element is
  detached, where previously it always wrote.

### Compatibility

- HMR cooperates with all three plans. `UitkxHmrDelegateSwapper` walks
  `RootRenderer.AllInstances` and `FiberRenderer.Root` independently of
  the container, so swapping delegate pointers / triggering re-renders
  remains valid through Unity rebuilds — the next poll tick re-targets
  the host and the freshly re-rendered tree comes with it.
- Editor renderers (`UitkxWindow`) and the legacy `Initialize(VisualElement)`
  overload do not subscribe to the rebuild poll; only the
  `Initialize(UIDocument)` path activates Plan 3.

## [0.5.5] - 2026-05-09

### Added

- **JSX `&&` short-circuit splice in markup expression positions.** The React
  idiom `{cond && <Tag/>}` is impossible to emit verbatim because C# `&&` is
  bool-only and `bool && VirtualNode` is **CS0019**. The splicer now detects
  a trailing `&&` operator at the end of the prefix preceding any JSX literal
  in `{expr}` or `attr={expr}` positions and rewrites the expression to
  ternary form

  ```csharp
  ((cond) ? V.Tag(...) : (global::ReactiveUITK.Core.VirtualNode?)null)
  ```

  reusing the already-tested Phase 1 ternary path. The `null` fallback is
  dropped at render time by `__C(params object[])` which filters nulls — no
  runtime change required.

  A new shared precedence-aware walker
  `DirectiveParser.FindLhsStartForLogicalAnd` locates where the LHS of the
  `&&` begins inside the surrounding expression: single forward pass with
  per-paren-depth boundary tracking, lexer-aware string/comment skipping,
  and recognition of `?`, `:`, `??`, `||`, `,`, `;` as boundary tokens at
  the same paren depth as the `&&`. Examples:

  ```jsx
  // simple bool
  <Box>{flag && <Label text="hi"/>}</Box>

  // null check (the user-reported repro)
  <Box>{icon != null && <Image texture={icon}/>}</Box>

  // parenthesised LHS preserved
  <Box>{(x.Count > 0) && <Label text="non-empty"/>}</Box>

  // method-call LHS preserved (parens balanced)
  <Box>{IsActive(item) && <Label text="on"/>}</Box>

  // nested in ternary — LHS walker stops at `:` boundary
  <Box>{(a ? b : c && <Label text="x"/>)}</Box>   // LHS = c

  // nested in `||` — LHS walker stops at `||` boundary
  <Box>{a || b && <Label text="x"/>}</Box>        // LHS = b

  // bitwise `&` is NOT mistaken for logical
  <Box>{((a & b) > 0 ? <Label text="on"/> : <Label text="off"/>)}</Box>
  ```

  Mirrored across all four code layers: shared scanner adds the `&&` trigger
  in `FindBareJsxRanges` and the LHS walker; SG `CSharpEmitter` and HMR
  `HmrCSharpEmitter` emit the ternary desugar (HMR via a new reflection
  delegate `FindLhsStartFunc` plumbed through `UitkxHmrCompiler`); the IDE
  `VirtualDocumentGenerator` rewrites the same shape to a typed-null ternary
  placeholder so Roslyn does not show a permanent CS0019 squiggle on the
  `&&` line.

  When the LHS walker fails on degenerate input (e.g. `{ && <X/>}`) the
  splicer emits a single `#error UITKX0026: Could not desugar \`&&\` JSX
  expression. Use \`cond ? <Tag/> : null\` instead.` directive instead of
  cascading into raw-JSX compile errors.

  Setup-code and directive-body `&&` JSX positions (e.g. `var node = cond &&
  <Tag/>;` inside a component setup block) remain unsupported and are
  tracked in `Plans~/TECH_DEBT_V2.md` item 15. The workaround is identical
  to before: rewrite as an explicit ternary `var node = cond ? <Tag/> :
  null;`.

### Fixed

- **Source generator and HMR emitter now inject `using UnityEngine;` into
  the generated component compilation unit.** Six emit sites covered the
  namespace block, the partial-class body, and the function-component
  overload across both pipelines (three in SG `CSharpEmitter`, three in HMR
  `HmrCSharpEmitter`). The IDE virtual document already pulled
  `UnityEngine` into scope via its Roslyn workspace, so user code
  referencing types like `Texture2D`, `Color`, `Vector2`, `Mathf`, etc.
  without an explicit `@using UnityEngine` directive compiled green in the
  editor but red at build/HMR time. Both pipelines now see the same surface
  area and the editor-vs-build asymmetry on `UnityEngine.*` symbols is
  gone.

### Tests

- 8 new regression tests in `JsxInExpressionTests` cover the `&&` desugar:
  simple bool, null comparison, parenthesised LHS, method-call LHS,
  nested-in-`?:`, nested-in-`||`, bitwise-`&` non-trigger, and the
  UITKX0026 diagnostic path.
- 3 new tests in `UnityEngineImportTests` cover the namespace-scope,
  class-scope, and function-component-overload `using UnityEngine;`
  injection sites.
- **1198/1198 SG** passing. LSP server build clean.

## [0.5.4] - 2026-05-08

### Changed

- **Breaking: User components now reject any attribute that isn't a declared
  parameter (or `key`/`ref`).** Previously the schema treated all 60 BaseProps
  members — `style`, `name`, `className`, `onClick`, `extraProps`,
  `enabledInHierarchy`, etc. — as universal across every tag, so a typo or
  stale attribute on a user component (`<AppButton style={x}/>` when
  `AppButton` doesn't declare a `style` parameter) silently produced
  `Style = x` against the generated `AppButtonProps` class and exploded at C#
  compile time as **CS0117** with no useful pointer back to the `.uitkx`
  source. The schema is now split into two semantic groups:

  - **`structuralAttributes`** — just `key` and `ref`. These apply everywhere
    because `key` is a VirtualNode reconciliation slot (lives on the node, not
    on Props) and `ref` is routed to the unique `Hooks.MutableRef<T>`
    parameter on the target component via `forwardRef`-style semantics.
  - **`intrinsicElementAttributes`** — the 58 BaseProps members. These only
    apply to built-in `V.*` tags that actually back a `VisualElement`. User
    components do **not** inherit them.

  Unknown attributes on user components now raise **UITKX0109** at **Error**
  severity (was Warning) with an actionable hint — `did you mean 'X'?` for
  close matches, otherwise
  `Available on '<Comp>': a, b, c. Add a parameter to the component or remove
  the attribute.` The bad attribute is also **skipped in the generated C#**
  so a single UITKX0109 doesn't cascade into CS0117/CS0246 against the
  synthesized props class.

  **Migration:** if you were forwarding `style`/`name`/`className`/etc.
  through a user component, declare them as explicit parameters and forward
  them yourself in the body — e.g.
  `component AppButton(IStyle? style = null) { return (<Button style={style}/>); }`.
  Built-ins are unchanged: `<Button style={...} extraProps={...}/>` still
  works exactly as before.

### Fixed

- **Editor and build-time diagnostics paths now share the same
  element-class-aware attribute map.** The LSP analyzer
  (`DiagnosticsPublisher.BuildKnownAttributes`) and the source generator
  (`CSharpEmitter.EmitFuncComponent`) previously diverged: the LSP raised
  UITKX0109 (Error) for the user-component path while the source generator
  raised nothing, leaving the IDE red but the build only yellow (or worse,
  silent until the C# compiler exploded with CS0117). Both now query the
  same split schema and produce identical diagnostics.
- **`PropsResolver.GetPublicPropertyNamesByQualifiedName` gained a same-pass
  peer fallback** so cross-file user-component attribute validation works on
  a clean build — before the generated `*Props` symbol exists as compiled
  metadata, the resolver now consults `PeerComponentInfo.FunctionParams`
  collected during the same generator pass.

### Tests

- 9 new regression tests (5 analyzer-level in `DiagnosticsAnalyzerTests`,
  4 source-generator end-to-end in `EmitterTests`) covering: style rejected
  on user component, `key`/`ref` always exempt, `extraProps` rejected on
  user component, declared attributes pass through cleanly, no-params
  components reject every non-structural attribute, and built-ins remain
  unaffected. **1187 / 1187 SG tests passing.**

## [0.5.3] - 2026-05-08

### Changed

- **Breaking: `@(expr)` markup-embed syntax has been removed.** The canonical
  and only embed form for arbitrary C# expressions inside markup is now
  `{expr}` — matching JSX/Babel and React. The `@` prefix continues to mark
  directives only: `@if`, `@else`, `@for`, `@foreach`, `@while`, `@switch`,
  `@case`, `@default`, `@using`, `@namespace`, `@component`, `@props`, `@key`,
  `@inject`, `@uss`. Files containing legacy `@(expr)` in markup now raise a
  hard parse error **UITKX0306** (`@(expr) is no longer supported — use
  {expr}`). Migration is mechanical: every `@(` becomes `{` and the matching
  `)` becomes `}`. The unification removes one of two competing embed forms
  end-to-end across the parser, formatter, analyzer, IntelliSense cursor
  context, virtual-document generator, HMR emitter, source generator,
  TextMate grammar, all 12 shipped sample files, and the test suite (every
  fixture inverted; 3 new UITKX0306 diagnostic tests added).

### Fixed

- **Source generator: pool-rent declarations no longer end up inside line
  comments.** The naive backward-scan that picked the splice point for
  `var __p_N = __Rent<TProps>();` statements stopped at the first `;` or `}`
  it encountered — including `}` characters living inside `// see {catBadge}`
  line comments. The compiler then read the rent statements as part of the
  comment text, leaving `__p_N` references downstream tripping CS0103
  (`The name '__p_12' does not exist in the current context`). Replaced all
  four sites (two in `CSharpEmitter`, two in `HmrCSharpEmitter` for HMR
  parity) with a shared `FindLastTopLevelStatementBoundary` lexer-aware
  forward scanner. The scanner correctly skips `//` line comments,
  `/* */` block comments, regular `"..."`, interpolated `$"..."` (with
  `{{`/`}}` escape and brace-depth tracking inside interpolation holes),
  verbatim `@"..."`, dollar-verbatim `$@"..."`, and `'...'` char literals —
  only `;` or `}` outside any of these counts as a statement boundary.
  Pre-Phase-2 the bug was masked because the comment text contained
  `@(catBadge)` (no `}` to trip on); the Phase 2 unification of `@(...)` →
  `{...}` exposed the latent flaw.
- **Function-style component discriminator now accepts a bare `{` opener.**
  `LooksLikeMarkupRoot` (used to distinguish setup code from a return-value
  markup expression) recognised `(` and `<` only, missing the new `{`-opened
  embed form introduced by Phase 2. Files using a top-level `{expr}` return
  were misclassified as setup code, producing CS-cascade errors. Acceptance
  set expanded to `(`, `<`, and `{`.
- **`AstCursorContext` block-2a recognises `{` as a markup-embed opener.**
  IntelliSense post-`{` cursor classification was anchored on the legacy
  `@(` opener; with Phase 2 the cursor inside `<Tag attr={cursor}/>` and
  `<Box>{cursor}</Box>` now resolves under the correct C#-expression scope
  rather than as an unrelated brace context.

### Tests

- 1178/1178 source-generator tests passing after the Phase 2 cut and the
  splice-helper rewrite.
- All 12 sample `.uitkx` files converted in-place with byte-safe UTF-8
  preservation (no encoding regressions).
- HMR↔SG parity contract tests still green (verifying both emitters share
  the same splice semantics).

## [0.5.2] - 2026-05-08

### Added

- **JSX literals are now allowed in any C# expression position** — matching
  React/Babel semantics. Previously the source generator only recognised JSX
  in three places: top-level markup, component preamble (`var x = <Tag/>;`
  before `return`), and directive bodies (inside `@if`/`@foreach`/etc.). JSX
  inside an inline expression — ternary branches, lambda bodies, attribute
  expressions, `?? <Tag/>`, child `{...}` or `@(...)` — was emitted verbatim
  and rejected by Roslyn. The existing scanner
  (`DirectiveParser.FindBareJsxRanges` + `FindJsxBlockRanges`) is now wired
  into the two remaining emit sites (`EmitExpressionNode` and the
  `CSharpExpressionValue` branch of attribute emission), so all six positions
  splice JSX uniformly. New `SpliceExpressionMarkup` helper in
  `CSharpEmitter.cs` mirrors `SpliceBodyCodeMarkup` 1:1; pool-rent statements
  flow into the shared `_rentBuffer` so the parent emit context hoists them
  above the surrounding expression. Patterns now supported:
  - `<Box>{cond ? <A/> : <B/>}</Box>` — ternary with JSX branches
  - `<Box>{fallback ?? <Default/>}</Box>` — null-coalescing with JSX
  - `<Box icon={active ? <Check/> : <X/>}/>` — JSX in attribute ternary
  - `attr={items.Select(x => <Item key={x.Id}/>)}` — JSX in lambda body
  - `var renderItem = i => <Label text={i}/>;` in preamble (already worked,
    now also works through attribute lambda flows)

  No runtime change — the emitter still produces the same `V.Tag(...)` factory
  calls and pooled `__Rent<TProps>()` shape; the splice runs purely at emit
  time. Compile-time cost is one O(n) scanner pass per expression; for
  expressions without embedded JSX (the common case) the helper returns the
  input unchanged.

### Fixed

- **`Texture2D ? iconName = null` (whitespace before `?`) is no longer
  silently dropped on save.** The `DirectiveParser.TryReadTypeName` tokenizer
  required `?` to immediately follow the type name with no intervening
  whitespace; with whitespace, the trailing `?` was left unconsumed and the
  formatter re-emitted the type without nullability — turning a nullable
  parameter into a non-nullable one across format-on-save cycles. Same
  pathology as the 0.5.x `@else` blank-line bug — formatter re-emit-from-AST
  is lossy when the parser drops tokens. Fix: `TryReadTypeName` now peeks
  past whitespace, consumes a trailing `?`, and canonicalises the captured
  type name as `<base>?` so the formatter re-emits a clean `Texture2D? name`
  regardless of the user's spacing. New regression test
  `ComponentParam_NullableType_WhitespaceBeforeQuestionMark_Preserved` in
  `FormatterSnapshotTests` covers `Action ? onClick`, `Texture2D ? iconName`,
  and `List<int>  ?  items`, asserting both idempotency across three format
  passes and canonical re-emit.

### IDE / HMR parity

- **HMR `HmrCSharpEmitter` mirrors `SpliceExpressionMarkup`** end-to-end.
  Reflection delegates for the two scanner methods are piped through
  `UitkxHmrCompiler` (graceful fallback if an older `Language.dll` lacks the
  newly-public APIs). Hot-reload of components using JSX-in-expression now
  produces identical C# to the source generator.
- **Virtual document generator (IDE Roslyn analysis)** now strips embedded
  JSX literals to typed-`(VirtualNode)null!` stubs when wrapping expressions
  for type-checking. Without this update, files using the new patterns would
  compile cleanly under the source generator but show phantom Roslyn errors
  in the editor on the JSX literals. Surrounding C# stays source-mapped so
  completions and squiggles still work outside the JSX.

### Tests

- 14 new tests, 1178/1178 passing (1164 baseline + 10 SG `JsxInExpressionTests`
  + 1 HMR parity tripwire + 3 VDG `VirtualDocumentTests`). Coverage matrix
  includes ternary-with-jsx (both branches, single branch, `@(...)` form),
  attribute-with-jsx, lambda-with-jsx, generic-LT-not-confused-with-jsx,
  string-with-tag-like-text-not-spliced, null-coalesce-with-jsx,
  no-op-fast-path, and `JsxExpressionValue` non-regression.

### Notes

- `DirectiveParser.FindBareJsxRanges` and `FindJsxBlockRanges` widened from
  `internal` to `public` so SG, HMR, and VDG share the single proven scanner
  implementation. Binary-additive change; no breaking impact on consumers.
- Phase 2 (soft-deprecate `@(expr)` in JSX child position in favour of
  `{expr}` to match React semantics) is planned as a separate follow-up;
  both syntaxes continue to work and emit identical AST in this release.

## [0.5.1] - 2026-05-07

### Fixed

- **Generic `static` methods inside `module { … }` blocks now compile.** The
  HMR trampoline rewriter (`SourceGenerator~/Emitter/ModuleBodyRewriter.cs`),
  introduced in 0.4.19, emitted two pieces of invalid C# on the generic-method
  branch — the bug was inert until a consumer authored a generic method inside
  a `module { … }` body.
  - **CS0119** (`'TProps' is a type, which is not valid in the given context`)
    — `AppendTypeArgs` emitted bare type-parameter names into the synthesized
    `MethodInfo.MakeGenericMethod(...)` call, e.g.
    `MakeGenericMethod(TProps, TResult)`. `MakeGenericMethod` takes
    `params Type[]`, so each name must be wrapped in `typeof(...)`. Fix:
    `AppendTypeArgs` now emits `typeof(TProps), typeof(TResult)`.
  - **CS8625** (`Cannot convert null literal to non-nullable reference type`)
    — the synthesized `MethodInfo` HMR field was emitted as
    `static MethodInfo __hmr_<name>_h<sig> = null;`. The field MUST start
    `null` (the trampoline checks `!= null` to fall through to the body method
    until `UitkxHmrModuleMethodSwapper` fills it via reflection), but consumer
    projects with `<Nullable>enable</Nullable>` or
    `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` failed compilation.
    Fix: emit `= null!;` — runtime value identical, warning suppressed.
- **Non-generic module methods, player builds, and the HMR swapper are
  unaffected** — both fixes are purely emit-side, behind `#if UNITY_EDITOR`,
  and `null!` is a compile-time null-forgiving annotation only.

### Tests

- New regression test
  `Sg_ModuleGenericMethod_GeneratedCodeCompiles_NoCS0119_NoCS8625` in
  `HmrEmitterParityContractTests` actually **compiles** the generated module
  output through Roslyn (with `UNITY_EDITOR` defined and nullable enabled) and
  asserts neither CS0119 nor CS8625 is raised. The pre-existing
  `Sg_ModuleGenericMethod_UsesMethodInfoCache` test only did substring checks
  and could not detect either bug. **1162/1162 SG** passing.

### Notes

- Runtime-only release. IDE extensions (VS Code, VS 2022) unchanged — the
  rewriter pass is SG-only and never runs in the LSP.

## [0.5.0] - 2026-05-06

### Added

- **`<Video>` element** (Pattern A — element adapter). Wraps a pooled
  `VideoPlayer` + `RenderTexture` rented from the new `MediaHost` peer pool and
  feeds the decoded RT into a UI Toolkit `Image` sink via
  `Image.image = renderTexture`. Repaints are driven by
  `VideoPlayer.frameReady` (no polling). An editor-only
  `EditorApplication.QueuePlayerLoopUpdate()` pump advances the player when
  Unity isn't ticking. Declarative props: `Clip`, `Loop`, `Autoplay`, `Muted`,
  `ScaleMode`, `Volume`. Imperative `VideoController` ref:
  `Play`/`Pause`/`Seek`/`StepForward`.
- **`<Audio>` element** (Pattern B — Func-Component). Renders no visible
  content; rents an `AudioSource` from `MediaHost` via `UseEffect` and returns
  it on unmount. Props: `Clip`, `Loop`, `Autoplay`, `Volume`, `Pitch`,
  `SpatialBlend`, optional `AudioMixerGroup`. Imperative `AudioController` ref.
- **`useSfx()` hook** — returns a stable `Action<AudioClip, float>` that calls
  `MediaHost.Instance.SfxSource.PlayOneShot(clip, volumeScale)`. Zero per-call
  allocation, identical delegate reference across renders so it composes
  cleanly inside `UseEffect` dependency lists. Optional `AudioMixerGroup`
  parameter is captured at hook-call time.
- **`MediaHost` peer pool** — `HideAndDontSave` GameObject hosting all
  `VideoPlayer` and `AudioSource` instances plus a stable `SfxSource`. Pool
  rent/return is reference-counted; `RenderTexture`s pooled by
  `(width, height, depth)` tuple. Survives domain reloads via lazy
  resurrection.
- **MediaPlayground demo** — `Samples/Shared/MediaPlaygroundDemoPage.uitkx`
  exercises every media surface end-to-end. Editor window at
  `ReactiveUITK > Demos > Media Playground`; runtime bootstrap
  (`MediaPlaygroundRuntimeBootstrap.cs`) for play-mode testing.

### Fixed

- **`FiberRenderer.Clear()` now runs effect cleanups before dropping the
  tree.** The previous implementation cleared the container and nulled the
  root in a single call, never invoking the depth-first `CommitDeletion` path
  that fires `UseEffect` cleanup callbacks and disposes signals. Any
  `UseEffect`-owned resource (audio sources, timers, signal subscriptions,
  `RenderTexture`s, animation handles) leaked across editor-window close /
  reopen cycles. New `FiberReconciler.UnmountRoot()` walks
  `_root.Current.Child` and runs `CommitDeletion` on each child before the
  root is nulled. `EditorRootRendererUtility.Unmount()` now calls
  `EditorRenderScheduler.Instance.PumpNow()` after `Unmount()` so cleanups
  drain synchronously before the editor window closes. The leak only became
  visible with `<Audio>` (background music kept playing forever) but the same
  code path affected every Func-Component using `UseEffect` cleanup.
- **IDE — `useSfx()` no longer reports `CS0103` in `.uitkx` files.** The LSP
  scaffolds private hook stubs into a virtual document so Roslyn can
  type-check setup code; `useSfx` had been added to the source generator and
  HMR alias regexes when it shipped but was never added to the LSP's stub
  list. Stubs added to both function-style component and hook-document
  scaffolds in `VirtualDocumentGenerator`.

### IDE extensions

- VS Code **1.1.11 → 1.1.12**
- Visual Studio 2022 **1.1.11 → 1.1.12**

---

## [0.4.19] - 2026-05-04

Full HMR support for `module { … }` declarations. The contract for what is and
is not preserved across a hot-reload cycle is now explicit and matches the
conventions used by React Fast Refresh and .NET Hot Reload.

### HMR contract for `module { … }` bodies

| Member kind | Behaviour on save |
|---|---|
| `public const X` | Re-baked into the call sites at compile time; new value visible after the next HMR swap (constants are folded by the C# compiler, so existing already-loaded code keeps the old value until that code is itself re-emitted). |
| `public static readonly X` | **Re-initialised every HMR cycle** — the new initializer expression runs in the HMR-compiled assembly and the result is copied into the project type via `UitkxHmrModuleStaticSwapper`. |
| `public static X` (mutable) | **Preserved** — runtime value carries across HMR cycles. Matches React Fast Refresh, .NET Hot Reload, and JS HMR. Use cases: lazy caches (`_textures`, `_built`), session counters, accumulated state. To reset, exit Play mode (or enable the opt-in auto-reload setting). |
| `public static T Foo(…)` (method) | **Hot-swapped via per-method delegate trampolines** — supports `ref`/`out`/`in`/`params`, default values, generics, and overloads. Behind `#if UNITY_EDITOR`, zero overhead in player builds. |
| Newly-added `static readonly` field | **CLR rude edit** — the project type's metadata is sealed by the runtime and cannot grow new fields. By default HMR schedules a domain reload so the new field materialises everywhere; disable via the HMR window's *Auto-reload on rude edit* toggle (EditorPref `UITKX_HMR_AutoReloadOnRudeEdit`) if you want manual control. A once-per-session warning is logged either way. |
| Newly-added method | The new method exists only on the HMR-compiled type. Calls from already-loaded (non-HMR'd) code throw `MissingMethodException`. Same workaround as new fields. |
| Instance methods, properties, operators, nested-type members | Emitted verbatim — not hot-reloaded. Edit them and trigger a full domain reload to see changes. |

### Added — HMR for module statics & methods

- **Module `static readonly` field re-init.** New `UitkxHmrModuleStaticSwapper` copies static-readonly field values from the freshly HMR-compiled assembly into matching project types. Fixes the case where editing a `Style`/`Color` module field initializer reported a successful HMR cycle but the rendered UI kept showing the cold-build value until you exited Play mode.
- **Module `static` method hot-swap.** New source-generator pass (`ModuleBodyRewriter`) rewrites every top-level `public static` method inside a `module { … }` body into a trampoline triplet: a public surface method that bounces through an `__hmr_<name>_h<sig>` delegate field to a private `__<name>_body_h<sig>` body method (all `#if UNITY_EDITOR`-gated). After each HMR compile, the new `UitkxHmrModuleMethodSwapper` rebinds every delegate field to the freshly compiled method via `Delegate.CreateDelegate`. Custom delegate types support `ref`/`out`/`in`/`params` (previously impossible with framework `Func<>`/`Action<>`); FNV-1a 32-bit signature hash disambiguates overloads; generic methods use a `MethodInfo` + `ConcurrentDictionary<Type, Delegate>` cache pattern. Trampolines preserve the original method's visibility so `private static` methods using `private` nested types stay valid (no CS0050/CS0051/CS0052/CS0058/CS0059).
- **Rude-edit detection.** When you add a new `static readonly` field to a module mid-session, the CLR can't grow the project type's metadata — `UitkxHmrModuleStaticSwapper` now detects the mismatch and logs a once-per-session warning naming each affected field, the runtime constraint, and the available remediations.
- **Auto-reload on rude edit (default on).** New `UitkxHmrController.AutoReloadOnRudeEdit` setting (EditorPref `UITKX_HMR_AutoReloadOnRudeEdit`, default `true`) surfaced as the *Auto-reload on rude edit* toggle in the HMR window. When a rude edit lands (newly-added field/method), HMR schedules `EditorUtility.RequestScriptReload()` via `EditorApplication.delayCall` so the new member materialises everywhere with one extra round-trip. Disable for manual control — a warning is still logged either way.
- **`UITKX0150` Info diagnostic.** Emitted when the source generator cannot Roslyn-parse a module body for trampoline rewriting; falls back to verbatim emission so the module still compiles (only per-method HMR for that module is unavailable).

### Fixed — 12 HMR ↔ source-generator parity bugs in `HmrCSharpEmitter`

The HMR pipeline emits C# from a hand-written transpiler that must match the
Roslyn-based source generator's output for any given `.uitkx` input. A round
of cross-checking surfaced 12 long-standing divergences:

- `ref={x}` on function components is now resolved to the props' `Ref<T>`/`MutableRef<T>` slot via the new `FindPropsTypeAndRefSlot` + `FindRefSlotName` helpers, instead of being treated as a literal `Ref` prop assignment (which silently dropped the binding).
- JSX-as-attribute-value (e.g. React-Router `element={<X/>}`) now emits a real nested element via the `JsxExpressionValue` `_sb`-capture path instead of collapsing to `null`.
- Sibling duplicate `key={…}` warnings are now raised at HMR-compile time via `CheckDuplicateKeys` from `EmitChildArgs`, matching SG's `UITKX0104`.
- Sibling top-level Props classes (`RouterFunc` / `RouterFuncProps` at namespace scope) resolve correctly — three resolution paths now mirror the SG's `PropsResolver.TryGetFuncComponentPropsTypeName`.
- `HmrCSharpEmitter.FindPropsType` no longer over-eagerly returns `{Type}.{Type}Props` and now walks all three legitimate Props shapes (sibling top-level, nested same-name, nested differently-named).
- Function-component invocations correctly use `new …Props { … }` (not `BaseProps.__Rent`) — function-component Props derive from `IProps`, not `BaseProps`, and cannot be pooled.
- `Asset<T>("./x")` / `Ast<T>("../x")` relative paths are resolved to absolute Unity-registry keys before HMR emit, so HMR-compiled and SG-compiled code produce identical literal strings (parity with `UitkxAssetRegistry`).
- `UitkxHmrCompiler` adds a silent-drift list for 4 reflection-bound Roslyn methods, a deterministic `PickAllOptionalTailOverload` helper (overload picking is no longer order-sensitive across Roslyn versions), and an explicit `lineOffset:0` on `_uitkxParse`.
- `CheckIfGenuinelyNew` uses fully-qualified type names so two unrelated modules with the same short name no longer fight over the swap slot.
- `CompileHookModuleFile` correctly dispatches `HmrHookEmitter.EmitModules` so module bodies emitted by HMR compile end-to-end (exposed by Bug 1 from 0.4.17).

### Changed

- `UitkxHmrController.ProcessFileChange` extends its success log with `| Module statics re-init: N` and `| Module methods re-init: K` so the editor console makes it obvious which kind of HMR work happened on each save.
- `UitkxHmrModuleStaticSwapper.SwapModuleStatics` returns a richer `ModuleStaticSwapResult { Copied, AddedFieldsDetected }` instead of a bare `int`.

### Tests

- 12 SG ↔ HMR emitter parity contract tests in `HmrEmitterParityContractTests` (5 from the parity-bugs round + 7 for the new module-method trampoline shape: trampoline-triplet shape, `ref` parameter custom-delegate, distinct overload hashes, generic-method `MethodInfo`-cache, non-method members emitted verbatim, instance-method untouched, default-parameter behaviour). 1142/1142 tests passing.

## [0.4.18] - 2026-05-03

### Fixed — HMR `CS0426` on function components with sibling top-level Props

A consumer hit `[HMR] Compilation failed for AppRoot... CS0426: The type name
'RouterFuncProps' does not exist in the type 'RouterFunc'` immediately after
shipping 0.4.17. Root cause was a long-standing convention divergence between
the source generator and the HMR compiler that only surfaced once HMR could
actually compile module/style/hook files end-to-end (Bugs 1 & 2 from 0.4.17).

#### The bug

Function-component Props classes are emitted in three legitimate shapes:

1. **Sibling top-level** — `RouterFunc` and `RouterFuncProps` both at namespace
   scope, neither nested. Used by `ReactiveUITK.Router`.
2. **Nested same-name** — `CompFunc.CompFuncProps` (the source generator's own
   default emission shape).
3. **Nested differently-named** — `ValuesBarFunc.Props` (legacy hand-written
   pattern still in use).

The source generator's `PropsResolver.TryGetFuncComponentPropsTypeName` already
walked all three. HMR's `HmrCSharpEmitter.FindPropsType` only walked nested
types and shipped `{Type}.{Type}Props` unconditionally — so any component using
shape (1) compiled fine through source-gen but failed CS0426 through HMR.

#### The fix

`FindPropsType` now mirrors `PropsResolver` lookup order verbatim:

1. Sibling top-level `{typeName}Props` in same namespace as the located
   component type → returns `"global::" + siblingFullName` (typed Props).
2. Nested `{typeName}.{typeName}Props` implementing `IProps` → returns
   `"{typeName}.{siblingName}"`.
3. Any nested `IProps` (legacy fallback) → returns `"{typeName}.{nested.Name}"`.
4. Convention fallback string (preserves prior behavior for genuinely missing
   types so the resulting CS error points at a recognizable location).

#### Tests

Two complementary layers, both running on every push, every PR, and before
every package publish via the existing GitHub Actions workflows:

- **SG-side parity test** (`FuncComponent_WithSiblingTopLevelPropsClass_EmitsTypedVFunc`)
  — drives the generator with the real `RouterFunc` / `RouterFuncProps` shape
  and asserts it emits `V.Func<global::Ns.RouterFuncProps>` rather than the
  broken nested form. Pins the contract HMR mirrors.
- **HMR algorithm contract tests** (`HmrFindPropsTypeContractTests`) — five
  cases exercising the algorithm against in-memory Roslyn-compiled assemblies
  (sibling / nested-named / nested-legacy / sibling-wins-priority / negative
  fallback). Mirrors `FindPropsType` verbatim because the Editor assembly
  (`UnityEditor` deps) cannot be loaded by the standalone .NET test runner.

**1070/1070 SG** passing.

VS Code **1.1.10 → 1.1.11** · VS 2022 **1.1.10 → 1.1.11** ride the same release.

---

## [0.4.17] - 2026-05-03

### Fixed — HMR overload-resolution bug + asset-path rewrite gap in `module` / `hook` bodies

Two related production-grade fixes converging on `.style.uitkx` / `.hooks.uitkx`
files. Both were silent until they met in a real consumer project (the
`AppRoot.style.uitkx` / `Asset<Texture2D>("../Resources/background-01.png")`
case), so this release also adds CI coverage so neither can recur.

#### Bug 1 — HMR `ArgumentException` on every `.uitkx` save

`UitkxHmrCompiler.InvokeWithDefaults` had two `params object[]` overloads:

- `InvokeWithDefaults(MethodInfo, object target, params object[])` (instance/static aware)
- `InvokeWithDefaults(MethodInfo, params object[])` (static-only, with API-drift padding)

C# overload resolution prefers `string → object target` over `string →
params object[]`, so calls like `InvokeWithDefaults(_directiveParse, source,
uitkxPath, diagList, true)` silently bound to the **first** overload —
`source` became the (ignored) target receiver and every subsequent argument
shifted left by one position, dropping a `List<ParseDiagnostic>` into
`DirectiveParser.Parse(string source, string filePath, ...)`'s `filePath`
slot. Result on every `.uitkx` save during play mode:

```
ArgumentException: Object of type
'System.Collections.Generic.List`1[ReactiveUITK.Language.Parser.ParseDiagnostic]'
cannot be converted to type 'System.String'.
```

The two overloads were collapsed into a single canonical signature
`InvokeWithDefaults(MethodInfo method, object target, params object[] args)`
where `target` is **mandatory** (not defaulted) — this makes the entire
class of "string arg accidentally captured as receiver" bug structurally
impossible to recur. All eleven call sites updated to pass an explicit
`null` (static methods) or the actual receiver (instance methods like
`Compilation.Emit(stream)`).

#### Bug 2 — `Asset<T>("./x")` / `Asset<T>("../x")` not rewritten in `module` / `hook` bodies

The runtime `UitkxAssetRegistry` is a flat dictionary keyed by **resolved**
Unity asset paths (e.g. `Assets/Resources/background-01.png`). The compile-
time emitters are responsible for rewriting every `Asset<T>("./relative")`
literal in the generated C# from the relative form to that resolved key,
so that runtime `Get<T>(string key)` finds the entry.

That rewrite (`ResolveAssetPaths`) was applied to component setup code,
JSX attribute expressions, and directive (`@if` / `@foreach` / `@switch`)
bodies — but **not** to `module { ... }` or `hook { ... }` bodies. So:

```uitkx
module AppRoot {
  public static readonly Style Root = new Style {
    BackgroundImage = Asset<Texture2D>("../Resources/background-01.png"),
  };
}
```

…shipped the literal `"../Resources/background-01.png"` to runtime, while
the editor-side `UitkxAssetRegistrySync` (which scans the same source
independently) wrote the entry under the resolved key
`Assets/Resources/background-01.png`. The two halves no longer agreed,
so `Asset<T>("…")` returned `null` with a warning:

```
[UITKX] Asset not found in registry: "../Resources/background-01.png"
```

Both emitter pipelines were widened to apply `ResolveAssetPaths` to
module/hook bodies:

- **Source generator** — `ModuleEmitter.Emit` and `HookEmitter.EmitSingleHook`
  now call the same shared `EmitContext.ResolveAssetPaths` that powers
  setup code and JSX attributes. The helper was promoted from a private
  instance method to an `internal static` so all three emitters share a
  single implementation (no semantic drift).
- **HMR** — `HmrHookEmitter.EmitModules` and `HmrHookEmitter.EmitSingleHookBody`
  now route bodies through `HmrCSharpEmitter.ResolveAssetPaths` (visibility
  promoted from `private` to `internal`). HMR-recompiled assemblies now
  produce literal-identical asset strings to source-generated ones.

#### Why these two are related

`.style.uitkx` and `.hooks.uitkx` files are the convergence point. Bug 1
prevented HMR from ever compiling those files (Parse blew up). Bug 2 meant
that even when source-gen ran cleanly, the registry lookup still missed
because the literal stayed unrewritten. Both bugs needed to be fixed for
`Asset<T>` inside `module` / `hook` blocks to work end-to-end.

#### Tests

Four new regression tests in `SourceGenerator~/Tests/EmitterTests.cs`:

- `Module_AssetCall_RelativePath_IsRewritten` — `./bg.png` → `Assets/UI/bg.png`
- `Module_AssetCall_DotDotPath_IsRewritten` — the exact failing case
  (`../Resources/bg.png` → `Assets/Resources/bg.png`)
- `Module_AssetCall_AbsolutePath_Unchanged` — negative test, no double-prefix
- `Hook_AssetCall_RelativePath_IsRewritten` — parity for `HookEmitter`

These run on every push, every PR, and before every package publish via
`.github/workflows/test.yml` and `.github/workflows/publish.yml`, so the
bug class cannot ship again. **1064/1064 SG** passing.

#### Files touched

- `Editor/HMR/UitkxHmrCompiler.cs` — overload collapse + 11 call-site updates
- `Editor/HMR/HmrCSharpEmitter.cs` — `ResolveAssetPaths` visibility
- `Editor/HMR/HmrHookEmitter.cs` — apply asset-path rewrite to hook + module bodies
- `SourceGenerator~/Emitter/CSharpEmitter.cs` — `ResolveAssetPaths` (and helpers
  `ResolveRelativePath` / `GetUitkxAssetDir` / `GetProjectRoot`) promoted to
  pure statics taking `(filePath, diagnostics)` parameters
- `SourceGenerator~/Emitter/HookEmitter.cs` — wire asset-path rewrite after
  hook-alias substitution
- `SourceGenerator~/Emitter/ModuleEmitter.cs` — wire asset-path rewrite for
  every module body
- `SourceGenerator~/Tests/EmitterTests.cs` — 4 new regression tests

VS Code **1.1.9 → 1.1.10** · VS 2022 **1.1.9 → 1.1.10** ride the same release.

## [0.4.16] - 2026-05-03

### Fixed — HMR `TargetParameterCountException` + production-grade hardening

A reflection signature drift between the editor-only HMR compiler and the
loaded `ReactiveUITK.Language.dll` (`UitkxParser.Parse` gained an optional
`lineOffset` parameter in 0.4.7) caused `TargetParameterCountException` to
fire on every `.uitkx` save during play mode, swallowed silently into a
`Debug.LogWarning` and an infinite retry storm. This release fixes the
immediate symptom and adds two layers of defense so the same class of
plumbing failure cannot recur silently.

#### Layer 1 — immediate fix

`UitkxHmrCompiler` now passes the trailing `lineOffset = 0` argument to
both `_uitkxParse.Invoke` sites in `Compile()` and the `parseMarkup`
delegate. Hot reload of components, hooks, and modules works again
during play mode.

#### Layer 2 — defensive `InvokeWithDefaults` helper

All six reflective invocations into the language library
(`DirectiveParser.Parse`, `UitkxParser.Parse`, `CanonicalLowering.LowerToRenderRoots`)
now route through a new `InvokeWithDefaults(MethodInfo, params object[])`
helper that pads short argument arrays with each parameter's compile-time
`DefaultValue`. When padding is actually triggered, a one-time
`Debug.LogWarning` per `MethodInfo` surfaces silent API drift the next
time it happens — instead of failing, HMR keeps working with sensible
defaults and tells you to update the call site.

#### Layer 3 — infrastructure-error classifier + self-disable

`HmrCompileResult` gained a `bool IsInfrastructureError` flag. The
compiler's catch blocks classify the inner exception type
(`TargetParameterCountException | MissingMethodException |
MissingFieldException | TypeLoadException | ReflectionTypeLoadException |
BadImageFormatException`) and set the flag. `UitkxHmrController` checks
the flag before its existing CS0103 retry cascade: on the first
infrastructure failure it emits a single `Debug.LogError` with
actionable text, then calls `Stop()` (the only safe disable path —
unhooks events, stops the file watcher, unlocks the assembly-reload
suppressor, restores `Application.runInBackground`, clears retry
queues). The user can re-`Start` from the HMR window after rebuilding
the language library; a `_loggedInfrastructureFailure` gate is reset on
`Start()` so future sessions get a fresh shot.

User-authored compile errors (`CS0103`, `CS1xxx`, syntax errors) are
still returned as strings on `result.Error` and follow the existing
warn + retry cascade — only true infrastructure plumbing failures
self-disable.

Files changed: [Editor/HMR/UitkxHmrCompiler.cs](Editor/HMR/UitkxHmrCompiler.cs),
[Editor/HMR/UitkxHmrController.cs](Editor/HMR/UitkxHmrController.cs).
Source generator, runtime, build, and IDE extension surfaces are
untouched. All 1060 source-generator tests pass.

## [0.4.15] - 2026-05-03

### Fixed

- **Source generator (CS8323):** the no-props `V.Func` emit branch produced
  `V.Func(Type.Render, key: "k", child)` when a parameterless user component
  wrapped element children (e.g. `<MenuPage><HomePage/></MenuPage>`). The
  named `key:` argument landed at call slot 2 while its natural slot is 3,
  triggering CS8323 ("Named argument used out-of-position but is followed by
  an unnamed argument"). Emit now inserts a positional `null` for the IProps
  `props` slot — `V.Func(Type.Render, null, key: "k", child)` — mirroring the
  shape already used by the typed-props branch. Zero runtime / IL change
  (`null` flows through `?? EmptyProps.Instance` exactly as the implicit
  default did). Patch applied to both the cold-build emitter and the HMR
  emitter so hot-reload behaves identically. Regression test added
  ([NoPropsFuncWithChildrenRegressionTest.cs](SourceGenerator~/Tests/NoPropsFuncWithChildrenRegressionTest.cs))
  recompiles the generated source against a real-shape `V.Func` stub and
  asserts no CS8323.

## [0.4.14] - 2026-05-03

### Router — React-Router-v6 parity for layout routes, ranking, and DX hooks

This release closes the structural gap between the UITKX router and React Router v6.
Existing apps continue to work unchanged — every change is additive — but new apps
can now compose layout routes with `<Outlet/>`, rely on deterministic
ranking via `<Routes>`, and use the same DX hooks RR users expect.

#### New components

- **`<Outlet/>`** — render-slot for nested routes. A parent `<Route element=...>`
  with child `<Route>`s now publishes the matched child into context; the
  descendant `<Outlet/>` renders it. Optional `context` prop is exposed to
  descendants via `RouterHooks.UseOutletContext<T>()`.
- **`<Routes>`** — first-match-wins selector. Walks child `<Route>` declarations,
  ranks them with a port of RR's `rankRouteBranches` / `computeScore` (constants
  unchanged: `staticSegmentValue=10`, `dynamicSegmentValue=3`, `splatPenalty=-2`,
  `indexRouteValue=2`, `emptySegmentValue=1`), and renders only the highest-ranked
  match. Replaces ad-hoc "two routes both matched" foot-guns.
- **`<NavLink>`** — built-in navigation link with active styling (`activeStyle`,
  `end`, `caseSensitive`). Activation rules mirror RR exactly, including the
  `to="/"` special case.
- **`<Navigate to>`** — declarative redirect. Defaults to `replace=true` so
  redirects don't grow history. Useful for `<Route path="/" element={<Navigate to="/welcome"/>}/>`.

#### `<Route>` upgrades

- `index="true"` — index routes match the parent pattern exactly (no extra segment).
  Setting both `index` and `path` now throws an actionable
  `InvalidOperationException`.
- `caseSensitive="true"` — opt-in to case-sensitive segment matching for that
  Route only (default remains case-insensitive for back-compat).
- **Layout routes** — when both `element=...` and child `<Route>`s are present,
  `<Route>` now acts as a layout: it ranks the children, publishes the matched
  child to the descendant `<Outlet/>`, and renders its element wrapper. When no
  nested `<Route>`s are present, behavior is byte-identical to today.

#### `<Router>` upgrades

- `basename="/app"` — URL prefix the router treats as the application root.
  Locations are stripped of the prefix on the way in and re-attached on the
  way out (push/replace).
- Nested `<Router>` is now a hard error
  (`InvalidOperationException("UITKX <Router> cannot be nested ...")`)
  instead of silently shadowing context — mirrors RR's `invariant(!useInRouterContext())`.

#### New hooks (`RouterHooks`)

- `UseOutletContext<T>()` — typed accessor for the value passed via
  `<Outlet context=...>`.
- `UseMatches()` — ordered chain of `RouteMatch` from root → current route
  (breadcrumbs / debug overlays / analytics).
- `UseResolvedPath(string to)` — pure path resolver against the current
  navigation base.
- `UseSearchParams()` — `(IReadOnlyDictionary<string,string> Query, Action<…,bool> Set)`
  tuple. The setter preserves the path component and replaces only the query.
- `UsePrompt(bool when, string message = null)` — convenience over `UseBlocker`.
- `UseNavigate(NavigateOptions options)` — overload returning a path-only
  navigator pre-bound to `Replace`/`State`. Old `UseNavigate(bool replace = false)`
  remains for back-compat.

#### Ranker

- New internal `RouteRanker` (port of RR `flattenRoutes` + `rankRouteBranches` +
  `computeScore`) shared by `<Routes>` and the layout-route flow on `<Route>`.
  Higher-score routes win; ties break by declaration order.

#### Source generator + HMR

- `Router/Route/Link` alias map de-duplicated. Single source of truth lives at
  `Shared/Core/Router/RouterTagAliases.cs` and is linked into the source generator
  via `<Compile Include>`. Adding a new router primitive now touches **one**
  dictionary entry instead of two. New entries: `Outlet → OutletFunc`,
  `Routes → RoutesFunc`, `NavLink → NavLinkFunc`, `Navigate → NavigateFunc`.

#### IDE schema

- `ide-extensions~/grammar/uitkx-schema.json` updated with full attribute
  metadata for the new components. VS Code, Rider, and Visual Studio extensions
  inherit autocompletion and inline documentation automatically.

#### Tests

- 6 new emission tests in `SourceGenerator~/Tests/EmitterTests.cs` lock the
  alias map to its expected codegen for every router primitive.
- 1063 total source-generator tests (was 1057); same 2 pre-existing snapshot
  failures (PortalsPlayground.uitkx) unrelated to this change.

#### Internals

- `RouteMatcher.Match` now accepts an optional `caseSensitive` parameter
  (overload preserves the old default-case-insensitive behavior).
- `RouterPath` gained `BuildQuery`, `StripBasename`, `WithBasename` helpers.
- `RouterContextKeys` gained `OutletElement`, `OutletContext`, and `MatchChain`.
- `RouterState` gained `Basename` plus a constructor parameter (default `"/"`).
- `RouteFuncProps` gained `Index` and `CaseSensitive`. `RouterFuncProps` gained
  `Basename`. All defaults preserve current behavior.

#### Backward compatibility

Every change is additive. Existing samples (`RouterDemoFunc`, `MainMenuRouterDemoFunc`,
and downstream user apps) compile and render byte-identically. The only
behavioral change is:
- Nested `<Router>` now throws (previously silently shadowed).
- `<Route index>` with a path now throws (previously silently ignored both).

Both throws replace **silently broken** behavior with **loudly broken** behavior
and catch real bugs at startup instead of in production.

#### Deferred (intentional)

- **Optional segments (`:lang?`)** — Phase 3.7 in
  `Plans~/ROUTER_GAP_CLOSURE_PLAN.md`. Requires porting `explodeOptionalSegments`
  and reworking the ranker's stability ordering; safe to add later as it's purely
  additive in `RouteMatcher`/`RouteRanker`.
- **Static analyzer for ambiguous sibling `<Route>` patterns** — Phase 4.2.
  Best implemented as an AST pass in
  `ide-extensions~/language-lib/Diagnostics/DiagnosticsAnalyzer.cs` once user
  reports validate the noise/signal ratio. Until then, wrap competing routes in
  `<Routes>` to get deterministic first-match-wins behavior.

See `Plans~/ROUTER_GAP_CLOSURE_PLAN.md` and `Plans~/ROUTER_REACT_ROUTER_COMPARISON.md`
for the full design analysis.

## [0.4.13] - 2026-05-02

### IStyle coverage — 13 missing properties wired end-to-end

Closes the long-standing gap between `UnityEngine.UIElements.IStyle` (Unity
6.2 floor: 84 properties) and the UITKX style pipeline. All 13 properties
listed below are now first-class typed setters with full bitmask diffing,
SetByKey/GetByKey support, pool reset, source-generator literal hoisting,
HMR mirror, and IDE schema entries. A new xUnit coverage test
(`IStyleCoverageTests`, 7 facts) locks parity in CI so future Unity
versions cannot land an unwired property.

#### New typed `Style` properties

- **9-slice (6 props):** `UnitySliceLeft`, `UnitySliceRight`, `UnitySliceTop`,
  `UnitySliceBottom` (each `StyleInt`), `UnitySliceScale` (`StyleFloat`),
  `UnitySliceType` (`SliceType` — `Sliced` / `Tiled`).
- **Clipping:** `UnityOverflowClipBox` (`OverflowClipBox` —
  `PaddingBox` / `ContentBox`).
- **Text spacing:** `UnityParagraphSpacing` (`StyleLength`),
  `WordSpacing` (`StyleLength`).
- **Text shadow:** `TextShadow` (`TextShadow` struct — offset, blur, color).
- **Advanced font:** `UnityFontDefinition` (`FontDefinition` — wraps a
  legacy `Font` or a TextCore `FontAsset`).
- **Text generator:** `UnityTextGenerator` (`TextGeneratorType` —
  `Standard` / `Advanced`).
- **Editor text rendering:** `UnityEditorTextRenderingMode`
  (`EditorTextRenderingMode` — `SDF` / `Bitmap`; editor-only behaviour).

#### New `CssHelpers` shortcuts

- `SliceFill`, `SliceTile` (SliceType)
- `ClipPaddingBox`, `ClipContentBox` (OverflowClipBox)
- `TextGenStandard`, `TextGenAdvanced` (TextGeneratorType)
- `EditorTextSDF`, `EditorTextBitmap` (EditorTextRenderingMode)
- `Shadow(dx, dy, blur, color)` → `TextShadow`
- `FontDef(font)` → `FontDefinition`

#### Fix — 19 pre-existing missing `styleResetters`

While auditing setter/resetter parity, surfaced 19 `IStyle` properties
that had a `styleSetters` entry but no matching `styleResetters` entry
(silently leaked previous values when removed from a style block):
`alignContent`, `alignItems`, `alignSelf`, `backgroundPositionX`,
`backgroundPositionY`, `backgroundRepeat`, `backgroundSize`,
`flexDirection`, `flexWrap`, `fontFamily`, `fontSize`, `justifyContent`,
`position`, `rotate`, `scale`, `textAlign`, `transformOrigin`,
`translate`, `unityFontStyle`. All now reset to `StyleKeyword.Null`.

#### Internals

- `Style` bit budget extended from 79 to 92 (`_setBits1` bits 15–27;
  total 128 still in budget).
- `Style.__Rent()` pool reset now clears `_textShadow` and
  `_unityFontDefinition` (reference-bearing structs).
- Source-generator hoisting whitelist (`s_literalCtorTypes` in
  `CSharpEmitter` and HMR mirror) now accepts `TextShadow` and
  `FontDefinition` literal initializers — all-literal `Style` blocks
  with `Css.Shadow(...)` or `Css.FontDef(...)` get lifted to a
  `private static readonly Style __sty_N` and reused across renders.
- IDE schema (`uitkx-schema.json`) gained 4 enum value lists:
  `unitySliceType`, `unityOverflowClipBox`, `unityTextGenerator`,
  `unityEditorTextRenderingMode`.

#### Documentation

- Styling page property catalog: 13 new property cards across the Text,
  Enum Styles, Background, and Assets categories.
- Styling page enum-shortcuts table: 4 new rows (SliceType,
  OverflowClipBox, TextGeneratorType, EditorTextRenderingMode).
- Styling page compound-helpers table: 2 new rows (TextShadow,
  FontDefinition).
- CssHelpers Reference page: 6 new helper groups.
- Search index extended with the new property and helper names.

## [0.4.12] - 2026-05-01

### Doom demo — Phase 9 sector-engine release

This release is a non-library update: no UITKX runtime / source-generator /
IDE changes. Everything below is the `Samples/Components/DoomGame/` demo,
promoted from a flat 2.5D raycaster to a full sector-portal engine with
stacked floors, a key-chain progression, a minimap, and a polished status
bar. Pulled in to demonstrate that UITKX can host a real interactive game
on top of the typed-props / hoisted-style render pipeline shipped in 0.4.10
/ 0.4.11.

#### Renderer

- **Sector / portal raycaster (Phase 1–3).** Tile map is converted to a
  `MapData` of sectors + linedefs at level start; rendering walks portals
  via a per-ray cliprange (Plan C `winTop`/`winBot` screen-Y window) instead
  of the old single-cell DDA. Variable floor / ceiling heights, upper /
  lower wall segments, and sky cells render correctly.
- **ExtraFloor stacked slabs (Phase 9).** Sectors can carry any number of
  `ExtraFloor` slabs; the column rasterizer emits front-side and back-side
  TOP / BOTTOM / SIDE planes per slab and tightens `winTop` / `winBot` per
  ray so taller slabs further along the ray stay visible. Fixes the
  long-standing “staircase upper treads disappear behind the lower one”
  bug — used by Level 6’s 7-step interior staircase.
- **Z-aware collision (Phase 7).** `MapDef.BlocksMovementZ(footZ, headZ,
  STEP_HEIGHT)` replaces the binary `BlocksMovement` for slab-aware step-up,
  jump, and crouch. Player is anchored to the current sector floor unless
  airborne.

#### Gameplay

- **6 hand-built levels** (`Level1`..`Level6`) in `DoomMaps.uitkx` covering
  Hangar, Toxin Refinery, Containment Area, Outpost, Phobos Anomaly, and
  the boss-only finale.
- **Level 1 progression rebuild.** Hub now gates side wings behind colored
  doors: pick up the yellow key in the hub center → east wing (red key) →
  west wing (blue key + shotgun) → north boss room (Baron + Cacodemon).
  Walls flank every door so they can’t be sidestepped.
- **Boss-gated exits.** New `LevelStart.BossExitGated` flag plus
  `GameLogic.AnyBossAlive(ref st)` blocks the level-end trigger until every
  Baron / Cacodemon is dead, with a “Kill the boss first.” HUD message on
  attempt.
- **Walkable exit pads.** New `MapBuilder.ExitPad(x, y)` creates an
  `Exit`-kind cell with no wall texture and a deep-blue floor (`F_BLUE`),
  so the back of the boss room reads as a clear visual end-zone instead of
  the legacy “EXIT” sign block.
- **Blue-brick back wall** (`W_BRICK_BLUE`) paints the wall behind the
  Level 1 exit pads to reinforce the end-zone signal.

#### UI

- **Status bar rewrite** (`DoomHUD.uitkx`). 8-panel `FlexGrow`-ratio layout
  (AMMO / HEALTH / ARMS / FACE / ARMOR / KEYS / BREAKDOWN / INFO) that
  fills the full 800×90 viewport-bottom region. Per-panel title labels
  with consistent vertical spacing and `WhiteSpace.NoWrap`. ARMS button
  group renders 7 weapons in 3 columns with centered justification.
- **Live minimap** (`DoomMinimap.uitkx`). Top-right overlay, auto-scales to
  fit the largest map dimension into 160px. Renders walls, color-keyed
  doors, the exit pad, the player (yellow dot + heading indicator), and
  every live mobj (red enemies, cyan pickups, key-color keys).
- **Boss / pickup balance.** Baron HP 800 → 200, Cacodemon HP 400 → 120 so
  the Level 1 boss can be cleared with a few shotgun blasts.

## [0.4.11] - 2026-04-28

### Performance

- **OPT-V2-1 — JSX children fast-path.** Source generator now emits child
  arguments directly into `params VirtualNode[]` instead of allocating a
  transient `__C(...)` wrapper array when the children list is statically
  simple (no spreads, no conditional fragments, no `@foreach`/`@for`/`@while`
  collectors). Eliminates one allocation per element on the hot render path.
- **OPT-V2-2 — Static-style hoisting.** Source generator now hoists
  `style={new Style{...}}` literals to class-level `static readonly Style`
  fields whenever every initializer value is a compile-time constant. Handles
  both setter form (`Width = 5f`) and tuple form (`(StyleKeys.Width, 5f)`).
  Whitelist covers literals, named-static dotted refs (`StyleKeys.X`,
  `Color.red`, `Position.Absolute`), and `new T(literal-args)` for
  `Color`/`Color32`/`Vector*`/`Length`/`TimeValue`/`Rect`/`Quaternion`. The
  reconciler's existing `SameInstance` check makes the diff walk a no-op when
  the same hoisted instance is supplied across renders. Falls back to the
  existing pool-rent path for any non-literal value (state-derived, captures,
  method calls, instance-member access on locals).

## [0.4.10] - 2026-04-27

### Performance

- **Major reconciler & props pipeline optimization pass.** Brought UITKX from
  ~1.7× overhead vs. native UIToolkit (28 FPS / 47 FPS at the 3000-box stress
  benchmark) up to ~78% of native (36–38 FPS). Real apps with partial updates
  will be much closer to native still. Notable items:
  - **Typed Props Pipeline** — eliminated ~6,000 dictionary allocations/frame
    on the props plumbing path (component → reconciler → element adapter).
  - **Typed Style Pipeline** — eliminated ~21,000 boxing + dictionary
    allocations/frame; styles now flow through a flat backing-field struct
    instead of `Dictionary<string, object>`.
  - **Style & BaseProps object pooling (OPT-16)** — removed ~6,000 object
    allocations/frame; pool runs at ~99% hit rate at steady state.
  - **`@foreach` / `@for` / `@while` IIFE closure elimination (OPT-10)** —
    `return` inside loop bodies rewritten to `__r.Add(...); continue;` so each
    iteration no longer allocates a delegate closure (~3,000 closures/frame
    eliminated). Also fixes a pre-existing `break`/`continue` semantics bug in
    `@for`/`@while` bodies.
  - **Event handler diff fast-path (OPT-22)** — `_hasEvents` flag on `BaseProps`
    skips ~43 `DiffEvent` calls per element when neither the previous nor next
    props carry any handler. ~+2 FPS at 3000 boxes.
  - **Quick-wins batch (OPT-4/5/7/11/23/24/25/26)** — small per-element wins
    across BaseProps equality, fragment fast-paths, fiber bailout, deletion
    tracking, and adapter dispatch.

### Added

- **Doom-style game demo sample** (`Samples/Components/DoomGame/`) — full
  demo built in UITKX: types, maps, game loop, hooks, styles, and a
  `DoomGameScreen` / `DoomHUD` / `DoomMainMenu` component split. Editor
  window: `ReactiveUITK/Demos/Doom Game`.
- **Pure UI Toolkit comparison harness** — `PureUIToolkitStressTestBootstrap`
  + editor window for measuring native UIToolkit alongside the UITKX stress
  test under identical conditions.
- **`ScrollView` `contentContainer` typed-path styling** — `contentContainer`
  prop now applies on both `ApplyTypedFull` and `ApplyTypedDiff` paths
  (previously only the untyped slot path applied it).
- **Typed Props for editor field types** — `BoundsField`, `BoundsIntField`,
  `ColorField`, `DoubleField`, `DropdownField`, `EnumField`, `EnumFlagsField`,
  `FloatField`, `Foldout`, `GroupBox`, `Hash128Field`, `HelpBox`, `Image`,
  `IntegerField`, `LongField`, `MinMaxSlider`, `MultiColumnListView`,
  `MultiColumnTreeView`, `ObjectField`, `ProgressBar`, `PropertyInspector`,
  `RadioButton`/`RadioButtonGroup`, `RectField`/`RectIntField`,
  `RepeatButton`, `Scroller`, `Slider`/`SliderInt`, `Tab`/`TabView`,
  `TemplateContainer`, `TextElement`, `TextField`, `ToggleButtonGroup`,
  `Toggle`, `Toolbar`, `TreeView`, `UnsignedIntegerField`/`UnsignedLongField`,
  `Vector2Field`/`Vector2IntField`/`Vector3Field`/`Vector3IntField`/`Vector4Field`,
  `IMGUIContainer`. All wired through `TypedPropsApplier` with full diff
  support.

### Changed

- **Source generator diagnostic IDs unified with live analyzer.** Seven
  diagnostics now use the analyzer's canonical IDs so the same logical issue
  surfaces with the same code in both the Unity Console (source generator) and
  the VS Code Problems pane (live analyzer):

  | Concept | Old (source-gen) | New (aligned) |
  |---|---|---|
  | `@component` name ≠ filename | `UITKX0006` | `UITKX0103` |
  | Unknown attribute on element | `UITKX0002` | `UITKX0109` |
  | Element inside loop missing `key` | `UITKX0009` | `UITKX0106` |
  | Duplicate sibling key | `UITKX0010` | `UITKX0104` |
  | Multiple root elements | `UITKX0017` | `UITKX0108` |
  | Asset path not found | `UITKX0022` | `UITKX0120` |
  | Asset type mismatch | `UITKX0023` | `UITKX0121` |

  Diagnostic text and severity are unchanged. Migrate any explicit ID
  references (e.g. CI grep rules) to the new codes.
- **Initial `CreateRoot` render is now synchronous.** The first render +
  commit phase runs to completion before `CreateRoot` returns, so the host
  container never appears empty for one frame between `Clear()` and the
  first commit. Mirrors React 18's `createRoot().render()` behaviour:
  initial mount is always synchronous; time-slicing is reserved for
  subsequent state-driven updates. Passive effects are still scheduled
  asynchronously.

### Removed

- **Dead `FiberNode.ContextProviderId` field.** The field had no production
  reads — it was only assigned in `CloneForReuse` and ignored by every
  consumer. Removing it slightly reduces the per-fiber memory footprint.
- **`VirtualNode` object pooling fully reverted.** VNode references can
  appear inside opaque `IProps` payloads (e.g. `Route.Element` and any
  slot-like prop), so pool returns produced dangling pointers and
  cross-wired component trees. VNodes are now plain GC heap objects.

### Fixed

- **Cross-wired Style / BaseProps "disco" bug.** A pooled `Style` or
  `BaseProps` instance could be scheduled for return twice in the same
  flush window — once during render-phase bailout and again from the
  commit-phase update — causing it to be pushed into the pool twice and
  then re-rented to two different fibers, which then mutated each other's
  styles. Fixed by adding an idempotent `_isPendingReturn` guard on both
  pools and by removing the render-phase pool-return entirely (the leak
  is bounded — the unused instance is collected when the owning component
  re-renders).
- **`<ErrorBoundary>` stuck on its fallback after `resetKey` change.**
  `CloneFromCurrent` was copying `ErrorBoundaryResetKey` from the previous
  fiber, so the clone always equalled the current and the change was never
  observed. The reset key is now refreshed from the new VNode and marked
  consumed against the alternate inside `UpdateErrorBoundary`.
- **`<Portal target={x}>` ignored target-prop changes.** When a portal's
  `target` prop pointed at a new container between renders, the bailout
  clone kept the previous `PortalTarget` / `HostElement`. Both now refresh
  from the new VNode.
- **Universal deletion tracking in `BeginWork`.** Function components
  (`ReconcileSingleChild`, null-return deletion) and fragments call into
  the reconciliation path directly, bypassing the wrapper that set
  `_hasDeletions`. Tracking now lives at the single universal `BeginWork`
  exit, covering every code path.
- **Diagnostic-test IDs realigned with renumbered codes.** The
  `UITKX0009_ForeachMissingKey` / `UITKX0010_DuplicateSiblingKey` /
  `UITKX0010_NotFiredForUniqueKeys` source-generator tests asserted the
  pre-renumber IDs and silently failed; now assert the canonical
  `UITKX0106` / `UITKX0104` codes.
- **Stray VS Code extension activation logging.** The extension previously
  logged `chatHistory` / `globalState.keys()` on every activation — leftover
  scaffolding from an unrelated experiment. Removed.
- **`RS2008` build warning in the language server.** Suppressed the
  "enable analyzer release tracking" warning, which targets analyzer NuGet
  packages, not the LSP server EXE.
- **Galaga sample dead code.** Removed unused `int beamH` local in
  `GameScreen.uitkx`.

## [0.4.9] - 2026-04-18

### Added
- **Galaga game demo** — full arcade-style Galaga game sample built entirely in UITKX. Features sprite-sheet rendering, entry wave formations with configurable delays, dive attacks with enemy shooting, tractor beam capture/release mechanics, dual-ship mode, multi-wave progression, and game-over/restart flow

## [0.4.8] - 2026-04-18

### Added
- **HMR delegate rollback guard** — if a hot-reloaded delegate crashes during render, the reconciler automatically rolls back to the previous working version, resets hook/effect state, and retries before falling through to the ErrorBoundary

## [0.4.7] - 2026-04-17

### Added
- **Children slot re-render detection** — components receiving `@(__children)` now correctly re-render when their children change, using reference-equality comparison on the children list

### Fixed
- **Directive body scoping** — `@if`, `@foreach`, `@for`, `@while`, and `@switch` bodies now emit as C# local functions, preventing variable scoping leaks and early-return issues between branches
- **UITKX0009 coverage** — "loop element missing key" diagnostic now fires for `@for` and `@while` loops, not just `@foreach`
- **Setup code JSX validation** — source generator validates JSX placement inside directive body setup code
- **Hook alias runtime wrappers** — source generator emits correct wrapper methods for hook aliases
- **Source map accuracy** — improved diagnostic line mapping for UITKX0014, UITKX0013, and CS0219
- **HMR directive body support** — HMR emitter updated to match source generator's directive-body-as-function approach, including JSX splicing inside directive bodies

## [0.4.6] - 2026-04-13

### Added
- **Procedurally generated Mario levels** — `LevelGenerator` produces 35-screen levels with 6 screen types (Flat, Pit, Pipes, Staircase, Floating, Final/Flagpole). Difficulty scales with progression. Smart block cluster placement avoids pipe/ground overlap and guarantees mushrooms in question blocks.
- **Camera scrolling** — one-way horizontal camera follows Mario, clamped at level edges. Player cannot walk left past the camera (classic Mario behavior). Frustum culling skips rendering and collision for off-screen tiles.
- **Pipe tiles** — 2-wide green solid pipe obstacles with varying heights (2–4 tiles)
- **Flagpole win condition** — final screen has a staircase, flagpole, and castle. Touching the flagpole triggers "YOU WIN!" overlay with final score.
- **Damage shield** — Big Mario hit by enemy shrinks instead of dying, with 3-second invincibility grace period. Mario blinks (opacity toggle) during invincibility.
- **Coin blocks** — multi-hit blocks that give 50 points per hit (up to 5 hits)
- **Block bump animation** — blocks nudge upward briefly when hit from below
- **Mushroom power-up** — collecting a mushroom makes Mario grow (96px tall, 48px wide) for 10 seconds
- **Ducking slide** — ducking on the ground applies friction-based deceleration instead of instant stop, creating a slide effect
- **Multi-row block clusters** — 30% of generated block clusters have a second row 3 tiles above the first

### Fixed
- **HMR hook trampoline + using-static injection** — companion `.hooks.uitkx` files created during HMR sessions now emit public trampoline methods and inject `using static` into the component source
- **Brick destruction** — bricks now break when hit from below and disappear from the level
- **Mushroom physics** — mushrooms slide horizontally, fall with gravity, and bounce off walls
- **Jump height** — increased `JUMP_VEL` from -500 to -620 so Mario can clear gaps and reach blocks
- **Ducking mid-air** — ducking now works in the air (not grounded-only) and correctly reduces collision box
- **Duck position snapping** — transitioning between duck/stand adjusts Y position to keep feet in place, preventing underground clipping and forward teleporting
- **Side-hit brick breaking removed** — bricks only break from head-hits underneath, not side collisions. Center-of-head check prevents angled corner-clip breaks.
- **Mushroom Big flag ordering** — Big/BigTimer now applied after Items loop so mushroom collection actually persists to player state
- **Game start grounding** — player initial Y slightly overlaps ground so `grounded=true` on first frame (enables jumping immediately)
- **Restart keyboard focus** — clicking "Try Again" re-focuses the game board so keyboard input works immediately

## [0.4.5] - 2026-04-12

### Fixed
- **HMR hook companion trampoline** — companion `.hooks.uitkx` files discovered during HMR now emit public trampoline methods (e.g. `useXxx()`) in addition to the private body, and inject `using static Ns.XxxHooks;` into the component source. Previously only the private `__useXxx_body` was emitted, causing `CS0103` when a hook file was created during an HMR session.

## [0.4.4] - 2026-04-12

### Fixed
- **HMR companion `.uitkx` discovery** — HMR now discovers and compiles companion `.uitkx` files (`.style.uitkx`, `.hooks.uitkx`, `.utils.uitkx`) alongside the parent component, so module/hook members are available in the compilation unit. Previously only companion `.cs` files were included, causing `CS0103` errors for module-defined symbols like style constants.
- **HMR companion change redirection** — saving a companion `.uitkx` file now triggers recompilation of the parent component file, ensuring changes to styles/hooks/utils are immediately hot-reloaded.

## [0.4.3] - 2026-04-12

### Fixed
- **`onInput` event handler dispatch** — `onInput` handlers with `Action<string>` signature now correctly receive the field's text (`InputEvent.newData`) instead of `null`. Added `Action<InputEvent>` fast-path dispatch to avoid `DynamicInvoke` fallback.

### Added
- **Editor demo windows** — added `ReactiveUITK/Demos/Stress Test`, `Snake Game`, and `Tic Tac Toe` menu items for launching sample games in editor windows
- **Stress Test sample** — moved stress test to its own `Samples/Components/StressTest/` folder with configurable box count via UI input

## [0.4.0] - 2026-04-10

### Added
- **Hook companion files** (`.hooks.uitkx`) — extract reusable hooks into dedicated companion files using the `hook` keyword with `-> ReturnType` syntax. Hooks are parsed, validated, and code-generated alongside the parent component.
- **Module companion files** (`.style.uitkx`, `.utils.uitkx`) — extract styles, constants, and utilities into companion files using the `module` keyword. Generates partial class members on the parent component.
- **`@namespace` directive** — components, hooks, and modules declare their namespace via `@namespace` instead of requiring a companion `.cs` partial class.
- **Cross-file peer resolution** — LSP server and source generator resolve hooks and modules from sibling `.uitkx` files, providing full IntelliSense, diagnostics, and navigation across companion files.

### Fixed
- **Cross-file diagnostic staleness** — peer `.uitkx` content now read from editor buffers (not disk) during Roslyn rebuilds, eliminating stale diagnostics when editing companion files
- **Hover for declarations** — hover now shows type info for local variables, parameters, and fields via `GetDeclaredSymbol` fallback
- **Hover for delegate types** — delegate-typed symbols show invoke signature (e.g. `void Action(int value)`) instead of raw enum name
- **CS1662 lambda cascade** — suppressed cascading lambda conversion errors caused by state-setter type mismatches
- **Log spam cleanup** — removed 7 hot-path log calls that fired on every keystroke, rebuild, or hover

### Changed
- **Documentation rewritten** — all docs updated to reflect hook/module `.uitkx` companion approach; no more `.cs` companion file references

## [0.3.3] - 2026-04-07

### Fixed
- **VS2022 CI build** — pipeline now correctly packages LSP server binaries in VSIX; clean marketplace installs no longer fail with "no launch strategy succeeded"

### Added
- **HMR hook signature detection** — both emitters now emit `[HookSignature]` attribute with ordered hook call list. `UitkxHmrDelegateSwapper` compares old/new signatures before render and proactively resets all component state on mismatch, preventing silent hook corruption.

### Fixed
- **HMR state reset now comprehensive** — `FullResetComponentState` runs effect cleanups, disposes signal subscriptions, and clears hook states, queued updates, setter caches, context dependencies (previously only `HookStates` was cleared)
- **Hook order validation activated** — `HookOrderPrimed` now set to `true` after first render, enabling the previously dead runtime hook-order validation code path
- **Formatter snapshot tests stabilised** — Replace target updated to match current sample file content, fixing 32 spurious CI failures

## [0.3.2] - 2026-04-07

### Breaking
- **Comment syntax changed** — `{/* */}` JSX comments replaced with standard `//` (line) and `/* */` (block) comments in markup. Existing `{/* */}` comments in JSX return blocks must be converted.

### Added
- **UITKX0025 for variable assignments** — `var x = (<A/><B/>)` now correctly flagged as single-root violation in IDE diagnostics
- **Block comments in markup** — `/* */` now supported in JSX markup for multi-line comments

### Fixed
- **`@(expr)` type enforcement** — VDG now emits `VirtualNode` (not `object`) for inline `@(expr)`, matching the SG's cast. IDE shows errors for non-VirtualNode expressions early.
- **Formatter block diff** — formatter now uses a single block TextEdit instead of per-line diffs, eliminating corruption on files with blank-line variations
- **Formatter idempotency** — bare-return formatting now matches canonical form on first pass
- **Formatter preserves empty containers** — `<Box></Box>` no longer collapsed to self-closing by the formatter
- **HMR comment node handling** — fixed pre-existing dangling comma bug in `EmitChildArgs` when comment nodes appear between children

## [0.3.1] - 2026-04-05

### Added
- **Rules of Hooks validation in SG** — `HooksValidator` now scans SetupCode in all control blocks (`@if`, `@foreach`, `@for`, `@while`, `@switch`) for hook calls (UITKX0013–0016)
- **UseEffect missing-deps in SetupCode** — `StructureValidator` now scans control-block SetupCode for `UseEffect` without dependency arrays (UITKX0018)
- **StyledAssetDemoFunc sample** — new sample component demonstrating `@uss` directive with className-based USS styling

### Fixed
- **`@foreach` emitter double-brace bug** — `EmitForeachNode` produced invalid C# when SetupCode was present (`}}` in plain string instead of `}` in the IIFE closing)

## [0.3.0] - 2026-04-05

### Breaking
- **Control block bodies require `return (...)`** — all `@if`, `@for`, `@foreach`, `@while`, and `@switch` `@case`/`@default` bodies must now wrap their markup in `return (...)`. This enables C# setup code before the return statement (var declarations, lambdas, local computation). Existing control blocks with bare markup must be migrated.
- **CssHelpers renamed all shortcuts** — every member now has a consistent prefix for autocomplete discoverability (e.g. `Row` → `FlexRow`, `Column` → `FlexColumn`, `JustifyCenter` → `JustifyCenter`, `SpaceBetween` → `JustifySpaceBetween`, `AlignCenter` → `AlignCenter`, `Stretch` → `AlignStretch`, `Auto` → `StyleAuto`, `None` → `StyleNone`, `Initial` → `StyleInitial`, `WrapOn` → `WrapOn`, `NoWrap` → `WrapOff`, `WrapRev` → `WrapReverse`, `Relative` → `PosRelative`, `Absolute` → `PosAbsolute`, `Flex` → `DisplayFlex`, `DisplayNone` → `DisplayNone`, `Visible` → `VisVisible`, `Hidden` → `VisHidden`, `OverflowVisible` → `OverflowVisible`, `OverflowHidden` → `OverflowHidden`, `Normal` → `WsNormal`, `Nowrap` → `WsNowrap`, `Clip` → `TextClip`, `Ellipsis` → `TextEllipsis`, `Bold` → `FontBold`, `Italic` → `FontItalic`, `BoldItalic` → `FontBoldItalic`, `FontNormal` → `FontNormal`, `White`/`Black`/etc. → `ColorWhite`/`ColorBlack`/etc., `Transparent` → `ColorTransparent`, `OverflowStart` → `TextOverflowStart`, `OverflowMiddle` → `TextOverflowMiddle`, `OverflowEnd` → `TextOverflowEnd`)

### Added
- **Control block setup code** — `@if`, `@for`, `@foreach`, `@while`, `@switch` bodies can now contain C# statements (variable declarations, method calls, lambda captures) before `return (...)`, mirroring the component-level setup code pattern
- **Switch fallthrough** — adjacent `@case` labels with no body share the same branch (emits stacked `case X: case Y:` in statement mode, `X or Y =>` in expression mode)
- **UITKX0024 diagnostic** — parser emits an error when a control block body is missing `return (...);`
- **Compound struct factories** — `CssHelpers` now provides factory methods and presets for all compound struct style types:
  - Background: `BgRepeat(x, y)`, `BgRepeatNone`, `BgRepeatBoth`, `BgRepeatX`, `BgRepeatY`, `BgRepeatSpace`, `BgRepeatRound`; `BgPos(keyword)`, `BgPos(keyword, offset)`, `BgPosCenter`, `BgPosTop`, `BgPosBottom`, `BgPosLeft`, `BgPosRight`; `BgSize(x, y)`, `BgSizeCover`, `BgSizeContain`
  - Transforms: `Origin(x, y)`, `OriginCenter`, `Xlate(x, y)`
  - Easing: `Easing(mode)`, `EaseDefault`, `EaseLinear`, `EaseIn`, `EaseOut`, `EaseInOut`, + sine/cubic/circ/elastic/back/bounce variants (24 presets total)
- **TextAutoSizeMode** — full support for `unityTextAutoSize` across every layer: `StyleKeys`, `Style`, `CssHelpers` (`AutoSizeNone`, `AutoSizeBestFit`), `PropsApplier` (typed + string), schema, LSP completions
- **PropsApplier string parsing** — compound style properties (`backgroundRepeat`, `backgroundPositionX/Y`, `backgroundSize`, `transitionTimingFunction`) now accept CSS string values in the untyped API
- **LSP style value completions** — `backgroundRepeat`, `backgroundPositionX/Y`, `backgroundSize`, `transitionTimingFunction` now auto-complete CSS keyword values in `.uitkx` files
- **`JustifySpaceEvenly`** — added missing `Justify.SpaceEvenly` shortcut
- **`WhiteSpace.Pre`/`PreWrap`** — added `WsPre` and `WsPreWrap` shortcuts

### Docs
- **Documentation audit complete** — all 67 identified gaps now addressed: expanded guides for hooks, context, events, refs, keys, HMR, styling, advanced API, known issues, and more
- **CodeBlock syntax highlighting** — fixed non-functional C# highlighting in docs site by switching all code blocks to JSX (prism-react-renderer compatible)
- **`onChange` event documented** — added `ChangeEventHandler<T>` / `ChangeEvent<T>` to the Events page reference table

## [0.2.45] - 2026-03-29

### Added
- **CssHelpers auto-import** — `using static CssHelpers` is now auto-injected by the source generator and HMR emitter, no `@using` directive needed in `.uitkx` files
- **CssHelpers enum shortcuts** — full zero-exception coverage of all UIElements enums used in typed props: `PickPosition`/`PickIgnore` (PickingMode), `SelectNone`/`SelectSingle`/`SelectMultiple` (SelectionType), `ScrollerAuto`/`ScrollerVisible`/`ScrollerHidden` (ScrollerVisibility), `DirInherit`/`DirLTR`/`DirRTL` (LanguageDirection), `SliderHorizontal`/`SliderVertical`, `ScrollVertical`/`ScrollHorizontal`/`ScrollBoth`, `ScaleStretch`/`ScaleFit`/`ScaleCrop`, `OrientHorizontal`/`OrientVertical`, `SortNone`/`SortDefault`/`SortCustom`
- **LSP enum value completions** — attribute value completions now suggest CssHelpers shortcuts for enum-typed and string-enum props

### Fixed
- **ScrollView adapter** — `VerticalAndHorizontal` mode now accepted via string `"verticalandhorizontal"` or `"both"`
- **TwoPaneSplitView adapter** — orientation string comparison is now case-insensitive

### Improved
- **Plan status audit** — updated USS_LOADING_PLAN (15% → 95% complete), ASSET_REGISTRY_PLAN (D2 status), and V1 Road Map (checked 6 items previously marked incomplete that are covered by existing docs site pages)
- **Sample cleanup** — removed redundant `@using static StyleKeys` (17 files), `@using static CssHelpers` (1 file), and `@using UnityEngine.UIElements` (4 files) from sample `.uitkx` files; replaced `SelectionType.None`/`ColumnSortingMode.Custom` with CssHelpers shortcuts

## [0.2.44] - 2026-03-29

### Fixed
- **Formatter empty-element regression** — `<Box></Box>` no longer expands to multi-line; empty elements with explicit close tags stay on one line
- **LSP attribute version filtering** — completion items for attributes requiring a newer Unity version now show ⚠️ warning and sort lower; removed attributes are hidden entirely
- **LSP attribute version diagnostics** — UITKX0200 warnings for attributes with `sinceUnity` or `removedIn` mismatches against the detected Unity version

### Improved
- **Docs Unity links** — component reference pages now show an inline "Unity docs" link next to the title, pointing to the versioned Unity manual page
- **Documentation updates** — updated architecture docs reflecting completed Roslyn integration; updated versioning process docs; documented `apply-diff-to-schema.mjs` automation script

## [0.2.43] - 2026-03-29

### Fixed
- **Formatter preserves empty elements** — `<Box></Box>` no longer collapsed to `<Box />` by the formatter; explicit close tags are preserved
- **Tag completion** — autocomplete no longer inserts closing tag for elements accepting children; inserts tag name + trailing space instead

## [0.2.42] - 2026-03-28

### Added
- **Find All References** (Shift+F12) — resolves symbol via `SymbolFinder.FindReferencesAsync()` across all per-file workspaces; results mapped back to `.uitkx` via SourceMap
- **JSX-style fallback** — improved fallback for JSX-style syntax in completions

### Fixed
- **VS2022 native LSP routing** — removed 3 custom GoToDefinition handlers; VS2022 now routes through `CodeRemoteContentTypeName`

## [0.2.41] - 2026-03-28

### Improved
- **HMR background reload** — HMR now sets `Application.runInBackground = true` while active, so file-save hot-reloads trigger immediately even when VS Code (or another editor) has focus. Original setting restored on stop.

## [0.2.40] - 2026-03-28

### Added
- **Transition style support** — `transitionDelay`, `transitionDuration`, `transitionProperty`, and `transitionTimingFunction` setters/resetters in PropsApplier, typed properties in Style, and StyleKeys constants

### Fixed
- **Tag completion** — autocomplete no longer inserts a closing tag snippet when editing an existing tag name (e.g., replacing `VisualElement` with `Box` inside `<VisualElement style={...}>`)

## [0.2.39] - 2026-03-28

### Fixed
- **HMR CS0433** — companion file discovery now filters by component prefix, preventing duplicate type errors when multiple `.uitkx` files share a directory
- **HMR memory leak** — controller and compiler reused across start/stop cycles, eliminating ~200MB Roslyn re-init per cycle
- **HMR per-cycle growth** — eliminated `ms.ToArray()` byte[] copy (direct `ms.CopyTo(fs)`), cached USS dependency map across cycles, switched to normal `AssetDatabase.Refresh()`

### Added
- **HMR memory tracking** — HMR window shows live RAM (working set via Win32 P/Invoke), delta since window open, and delta since session start; refreshes every 2 seconds

### Improved
- **HMR compilation** — incremental Roslyn compilation cache, cross-reference MetadataReference cache, `Assembly.LoadFrom()` instead of `Assembly.Load(byte[])`, `GC.Collect(2)` after each compilation
- **HMR window** — Repaint only on state change (swap count, error count, active toggle) instead of every frame

## [0.2.38] - 2026-03-28

### Improved
- **Documentation site** — Asset\<T\> page: replaced plain-text sections (Texture Import, Diagnostics, Supported Types, Registry) with rich MUI tables, colored Chips, and Alerts
- **Documentation site** — Diagnostics page: all diagnostic codes and severities rendered as colored MUI Chips (red=Error, orange=Warning, blue=Hint)
- **Documentation site** — Fixed Image (`texture=`) and HelpBox (`messageType=`) prop names in component examples
- **Documentation site** — Component props displayed as table with collapsible BaseProps accordion
- **Documentation site** — Added Asset\<T\> docs page with 8 sections (basic usage, relative paths, shorthand, inline, @uss, auto-import, diagnostics, supported types, registry)
- **Documentation site** — Added @uss section to Styling guide (basic usage, .uss file, multiple sheets, combining USS+Style, HMR info)

## [0.2.37] - 2026-03-28

### Added
- **@uss directive** — attach USS stylesheets to components via `@uss "./path.uss"`, parsed at compile time with `__uitkx_ussKeys` static array
- **@uss SG diagnostics** — UITKX0022 (file not found) and UITKX0023 (type mismatch) validate @uss paths at compile time
- **@uss HMR** — `.uss` file changes trigger hot-reload of dependent `.uitkx` components; USS→UITKX dependency tracking
- **@uss formatter** — `@uss` directives preserved on save (formatter preamble emission)
- **@uss syntax highlighting** — `@uss` keyword colored as directive, path colored as string

## [0.2.36] - 2026-03-28

### Added
- **Asset Registry** — `UitkxAssetRegistry` ScriptableObject with `Asset<T>()`/`Ast<T>()` helpers for loading assets from `.uitkx` files
- **Editor asset sync** — `UitkxAssetRegistrySync` auto-populates the registry on `.uitkx` save and domain reload
- **HMR asset injection** — Hot reload injects asset cache entries; on-demand `ImportAsset` for files copied during HMR
- **Type-aware auto-import** — `Asset<Sprite>("./img.png")` auto-configures `TextureImporter` to Sprite mode; `Asset<Texture2D>()` ensures Default import
- **UITKX0022** (Source Generator) — Error when `Asset<T>()`/`Ast<T>()` references a file that doesn't exist on disk
- **UITKX0023** (Source Generator) — Error when `Asset<T>()` type parameter is incompatible with file extension (e.g. `Asset<AudioClip>("./bg.png")`)

### Changed
- `Style.TextColor` renamed to `Style.Color` to match `StyleKeys` and Unity `IStyle` naming
- Classic directive mode removed — function-style only

## [0.2.35] - 2026-03-27

### Added
- Centralized changelog system for IDE extensions (`ide-extensions~/changelog.json`)

### Removed
- Classic mode code paths (~835 lines across 15+ files)