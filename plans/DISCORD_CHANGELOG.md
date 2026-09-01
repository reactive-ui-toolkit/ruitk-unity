## [0.19.0] - 2026-08-31

### Component props, router hooks, and a builder that stops lying to you

**Props are an authoring gesture now.** The signature row was a label. Click it
to add, rename or remove a prop. A rename rewrites the declaration, its uses in
the body, and every call site's attribute as ONE undo.

**Breaking - a parameter with no default is REQUIRED.**

```
export VirtualNode Card(string label)               // required
export VirtualNode Card(string label = "Untitled")  // optional
```

`int x` and `int x = 0` were indistinguishable to the generator - both emit
`public int X { get; set; } = 0;` - so a forgotten prop silently rendered
`default(T)`. Omitting one is now `UITKX0115`.

**Router hooks are hooks.** All 16 joined the shared registry: hover,
completion, and the rules of hooks. `UseBlocker` composes `UseEffect`, so
calling one in an `@if` breaks effect ordering - and nothing said so.

**Builder fixes, all the same shape - the editor said one thing, did another:**

- A NEW tree rendered ANOTHER tree's components. Every hot swap stays loaded,
  so resolving a child by simple name reached whichever tree opened first. A
  child now resolves through the import that names it.
- The builder asks the TREE, not the disk. A module created, renamed, moved or
  deleted in the session could still answer as the stale file on disk said.
- A source edit could overwrite a DIFFERENT module: edit one, click another
  card, press Esc, and the first one's whole text landed in the second.
- A parent could not see a child's new props without saving.
- Every click rebuilt every component.
- The preview compiled at `latest` while Unity compiles at C# 9.
- Dropping a row into a self-closing tag did nothing, and said it worked.
- The library list kept exports the tree no longer had.
- Undo history died on every domain reload.

**Tests.** 1892/1892 SG, 185/185 LSP, plus out-of-Unity model checks.

VS Code **1.11.0 -> 1.12.0** | VS 2022 **1.11.0 -> 1.12.0**.

---

## [0.18.1] - 2026-08-27

### Patch - the store build compiles, and a refused drag says so

**The Asset Store build could not compile.** The store shell project's `manifest.json` was a
hand-written copy of the package's dependency set, and `com.unity.nuget.newtonsoft-json` - a
Builder dependency since 0.17.0 - was never added to it. Copied into `Assets/`, the package is no
longer a UPM package, so nothing resolves its own `package.json`, and the import died on `CS0246`
across every Builder file that touches JSON. The manifest is now GENERATED from `package.json`, so
a new dependency reaches the store build by construction rather than by remembering.

**A move that the builder refused said nothing.** Dropping a markup row onto another element had
five refusal paths that returned in silence, so a drop that did not land where you aimed was
indistinguishable from a drag that never armed - and a release BETWEEN rows quietly relocated the
row to the end of the root instead, which reads as the gesture doing something random. Every
outcome now reports itself, successes included ("Moved <LeftSide> into <VisualElement>"). The
insert path has always explained its refusals; the move path does too now.

**Docs.** Screenshots on the site were broken: an absolute `src="/builder/..."` written into a page
is emitted verbatim by Vite, so under the site's `/unity/` base it pointed at the domain root and
404'd. Images are imported now, which makes them hashed, base-correct, and a build error when
wrong; the sync gained a guard that fails on any un-prefixed asset URL. The UI Builder section also
gained figures for the empty state, Layer 1, card anatomy, both drop bands, the first-Save folder
prompt and the applies-on-Save toast - each cropped to the thing its caption names.

---
## [0.18.0] - 2026-08-26

### RUITK UI Builder - structure, menus, and a settled placement model

**The builder now has an opinion about where files go.** 0.17.0 shipped the canvas; this
release ships the other half - a folder tree in the left panel, and a placement convention
you can predict.

A component owns a folder. Its children live in `components/` inside it. Its companions -
`name.style.uitkx`, `useName.hooks.uitkx` - sit beside it. Right-click empty canvas to
create at the tree ROOT; right-click a component card to create a CHILD component or a
SIBLING companion. That replaces 0.17.0's focus-relative rule, under which three components
created in a row nested three deep - the structure recorded the order of your clicks rather
than your UI.

**Create states placement. Wiring states usage.** Creating a module never adds an import. It
cannot: an import with no usage is `UITKX2304`, which is error-tier, so a create that
imported would also have to guess where a style applies. The builder places the file and
stops; you wire it by dragging it in.

**Menus rebuilt** as a layer in the builder's own panel: real submenus, the builder's styling,
nothing to lose focus to, full keyboard drive. Long lists keep their search field.

Also: named zoom layers (Architecture / Cards / Edit), a History panel with click-to-jump, a
Trace toggle for the preview pipeline, and crash cover that offers unsaved work back.

**Fixes - about seventy tracked defects.** The preview bound to the SAVED copy of a style
module rather than your live buffer, so style edits never rendered. What USES a changed
module now rebuilds, not just the module. A save no longer rearranges the canvas: relocating
a module moves its folder and carries everything inside, and only the module that triggered
each move was told, so every card that rode along lost its place. Escape reaches an open
menu. A folder rearrangement can be saved.

**Docs.** A full UI Builder section on the site, with screenshots.

---
## [0.17.0] - 2026-08-15

### RUITK UI Builder - a visual editor inside Unity

**The builder lands.** Right-click any `.uitkx` -> **Open in RUITK UI Builder** and the whole component tree opens as one workspace: a pannable canvas with a kind-colored card per file and import edges drawn between them, a live preview mounted through the real reconciler, the file's code with semantic coloring and live diagnostics, and a searchable palette of every element, hook, directive and style key.

**Everything edits in memory.** Typing, palette inserts and outline reorders hit the buffer only; the preview hot-recompiles after a short debounce through the builder's own compiler instance - disk untouched, no reload, state preserved. **Save** writes every dirty buffer in one batch: one script reload total, or zero when HMR Mode is running. **Abort** discards. Undo/redo is per-file and never touches Unity's global undo. Files from immutable packages open read-only.

**Wired for navigation.** Ctrl+Click an element in the preview jumps to the component that rendered it; double-click a canvas card focuses its file; card layout and camera persist per tree in `UserSettings/`. Double-clicking assets still opens your external editor - the builder never steals that route.

**UXML import.** `Assets -> Convert UXML to UITKX` converts one-way, mapping inline USS to the typed `Style` initializer and warning about anything it had to drop.

**Under the hood:** `Label` gains `enableRichText`; the LSP server gains `ruitk/schema`, `ruitk/hooks`, `ruitk/componentProps` and `ruitk/workspaceGraph` plus an unsaved-buffer index overlay; the language lib gains a public AST printer and the UXML converter. Known limitation: `@uss`/`Asset<T>` refs added since the last Save resolve after saving.

**Tests.** 1878/1878 SG, 180/180 LSP.

VS Code **1.10.0 -> 1.11.0** | VS 2022 **1.10.0 -> 1.11.0**.

---

## [0.16.0] - 2026-08-10

### The wrapper keywords are gone - plain declarations only

**`component` / `hook` / `module` and the `@component`/`@props`/`@key`/`@inject` directives were removed** (deprecated since 0.9.0). A legacy file now fails with `UITKX2320`, whose message names the fix. Upgrade order: codemod first, then bump the package - the codemod runs from the repo against your project on any installed version:

```
node scripts/migrate-uitkx.mjs <YourProject>/Assets --es-modules
```

Companion sets migrate atomically, files whose generated namespace would move get an explicit `@namespace` stamp, and `--report` writes a ledger of every rewrite. The one shape the dialect will not express - type definitions - is reported with the fix (a hand-written .cs beside the components). `@using` keeps parsing forever; `import "@Ns"` stays the recommended spelling.

**Generic hooks are first-class now.** `export (T value, Action<T> set) useSel<T>(...)` parses, emits, hot-reloads, bridges across aliased imports, formats, and highlights - and the codemod migrates legacy generic hooks instead of skipping them.

**Fix - long declaration heads wrap again.** The legacy formatter wrapped over-width heads; the plain path silently did not. Parameters one per line, tuple returns ahead of the name.

**Under the hood**, the dead machinery went with the grammar: the SG's module emitter, HMR's legacy container emitter, the LSP's legacy generators, and diagnostics `UITKX2107`/`2108`/`2109`/`0211` (retired, IDs reserved). Docs gain a 0.16 migration page plus Mounting & Roots.

**Also shipped.** Underscore-marked params stay unused WITHOUT renaming their prop (`_onChanged` still exposes `onChanged`; `_x`+`x` = `UITKX0114`). Import specifiers accept single quotes. HMR runs on Unity 6000.5 and hot-swaps `.uitkx` inside embedded/local packages. `.uitkx` files wear the toolkit icon in VS Code.

**Tests.** 1855/1855 SG, 172/172 LSP.

VS Code **1.9.1 -> 1.10.0** | VS 2022 **1.9.1 -> 1.10.0**.

---

## [0.15.0] - 2026-08-02

### Unity 6.5 PanelRenderer host - sub-root mount, rebuild dispatch, workarounds

**`RootRenderer.Initialize(panelRenderer)` lands.** The component Unity's manual points new 6.5 projects at is now a first-class host; the `UIDocument` path is untouched. The mount is callback-driven, a `Render` issued before the panel exists is held and replayed, and the tree mounts into a library-owned sub-root so Unity's per-frame root rewrites never fight your UI. World-space config stays on the component. Five new samples, from hello to a router+signals+ListView dashboard.

**Rebuilds dispatch on the tree's own state.** Still parented -> reuse in place; orphaned but alive -> retarget, all hook/ref/animation state preserved; released -> remount: the old tree is dropped without touching it (cleanups still run via the new `AbandonRoot`) and the last UI replays fresh.

**Workarounds for open Unity 6000.5.x issues**, all symptom-gated so a fixed editor makes them inert: the mount watchdog (case IN-150082 + UUM-147875) forces the attach path when the reload callback never arrives; nested prevention and nested self-repair cover UUM-148452's release cascade into child renderers. Opt-outs `mount_watchdog`, `nested_prevention`, `nested_repair` in config.json; details on the Known Issues docs page.

**Fix - pre-existing leaks.** New `IsAlive` liveness on the host seam. User refs now detach on unmount, `Animator` stops ticking when its element leaves the tree, the four list/tree row pools finally unmount their pooled rows, and retarget no longer leaks style tracking. Also: a second `RootRenderer` used to DESTROY its own GameObject via a legacy singleton guard - multi-mount scenes work now. And the uGUI leg now compiles on 6.5, where `GetInstanceID` became an error-level obsolete.

**Tests.** The fiber core got its first CI suite: 76 tests over a mock backend (reconciler, retarget contract, hooks, signals). SG 1828 -> 1840 via the samples corpus.

---

## [0.14.1] - 2026-08-02

### Test coverage for Shared/ - and the first bug it found

**`RouterPath.ParseQuery("?a=1")` returned a dictionary keyed `"?a"`, not `"a"`.** The leading `?` was never stripped, so any caller passing a conventional query string found the first parameter silently unreachable. The router's own `Parse()` strips it first, which is why nobody hit it - but `ParseQuery` is public API. Fixed at the root; `"?a=1"`, `"a=1"` and a bare `"?"` all behave now. It was found by the first automated test ever run against that code, which is the real story here.

**`Shared/` had no CI coverage at all.** It is Unity C#, so only the Editor ever compiled it - CI runs the SourceGenerator and LSP suites, and the one Unity test assembly runs only inside the editor. `SharedTests~` is a net10.0 suite that links the host-agnostic parts of `Shared/Core` and runs them in CI: 39 tests over path normalisation, `Combine`/`StripBasename`/`WithBasename` round-tripping, query parse/build, and the `MemoryHistory` back/forward stack (forward-entry truncation, blockers, listener lifetime).

It does not reference Unity's assemblies, deliberately: those bind to native ECalls that exist only inside the Unity runtime - measured on 6000.5.6f1, `Debug.Log` and `new VisualElement()` throw `SecurityException` outside the editor. A shim supplies what the sources touch, and captures logs so tests can assert on them.

**A Unity compile check that needs no Editor.** `scripts/unity-compile-check.mjs` compiles `Shared/` and `Runtime/` against the real Unity assemblies twice: at the package floor with gates off, and with the 6.4/6.5 gates on. That is the manual "open it on the floor, then on 6.5" step, automated - and it catches a wrong `UnityEngine` API in an adapter, which the shimmed suite cannot see. Unity is discovered, never hardcoded.

**Tests.** 39/39 new Shared suite, 1828/1828 SG, 152/152 LSP.

---

## [0.14.0] - 2026-08-01

### Unity 6.5 and 6.4 controls - MaskField, Mask64Field, GUIDField

**Three controls Unity made runtime-available now work in UITKX.** `<MaskField>` and `<Mask64Field>` (Unity 6.5+) are multi-select bitmask dropdowns backed by `int` and `ulong`; `choices` gives the labels, and the optional `choicesMasks` overrides the default `1 << i` per entry so one choice can stand for a composite of bits. They existed before 6.5 but only in `UnityEditor.UIElements` as editor-only - 6.5 moved them into the runtime module.

`<GUIDField>` (Unity 6.4+) edits a `UnityEngine.GUID`. Despite the name it is a runtime control - the value type is `UnityEngine.GUID`, not `UnityEditor.GUID` - so it works in player builds. Takes a GUID or a hex string; inherits `readOnly`, `isDelayed`, `maxLength`, `selectAllOnFocus`.

**Masks: "Everything" is `~0`, not `(1 << n) - 1`.** The dropdown prefixes "Nothing" (`0`) and "Everything" (`~0`). Those differ from "every bit currently defined", so the props diff never normalises one into the other. Keep the distinction if you persist or compare masks yourself.

**Version-aware editors.** These are the first schema elements carrying a `sinceUnity` annotation, so completion and diagnostics understand tag availability now, not just style properties - targeting an older Unity reports the new tags instead of offering them. The docs version dropdown gains 6.4 and 6.5.

**Heads-up for 6.5.** The Advanced Text Generator is now the default. Unity claims feature parity but not measurement parity, so text sizes and wrap points can shift. The per-subtree opt-out `-unity-text-generator: standard` already works through the typed `Style` surface. See Known Issues.

**Compatibility.** Floor stays 6000.2; everything sits behind `UNITY_6000_4_OR_NEWER` / `UNITY_6000_5_OR_NEWER` and compiles out below it.

**Tests.** 1828/1828 SG, 152/152 LSP.

VS Code **1.8.0 -> 1.9.0** | VS 2022 **1.8.0 -> 1.9.0** | Rider **1.5.0 -> 1.6.0**.

---

## [0.13.0] - 2026-07-31

### Unified settings - one window, one JSON file, the family knob set

**Every setting now lives in Reactive UI Toolkit > Settings**, backed by a plain JSON file at `Assets/Resources/ReactiveUIToolkit/config.json` - created only when you click "Create settings file", and under Resources it ships into every player build by itself. The knob set is family-canonical (same semantics and defaults across the Reactive UI Toolkit legs; each leg spells the key names engine-natively): `environment`, `time_slicing`, `time_slice_ms`, `frame_budget_ms`, `host_node_pool`, `hook_validation`, `strict_diagnostics`, `strict_mode`, `trace_level`, `diff_tracing`, plus the Unity-only `diagnostics_output_folder`. Every default reproduces current behavior exactly - an untouched project behaves as before.

**Strict mode lands.** `strict_mode: true` double-invokes render functions (first result discarded, effects still run once, committed UI identical) so impure render bodies surface in dev. Forced off in release builds.

**The trace ladder is back.** `trace_level: basic` logs structural reconciler events again (placements, deletions, replacements, a commit summary) - lost in the fiber rewrite; `verbose` adds per-element/per-hook detail; `diff_tracing` is independent of both again (an AND-bug in three adapters silenced it without a trace level).

**Behavior changes:** hook validation now follows the environment (`auto` = off in release players - the per-render cost leaves shipped games; editor/dev keep it); strict diagnostics default on in the editor and dev builds, under the `[Hooks][Strict]` prefix; `exceptionControlFlow` is removed - it selected a legacy error-boundary strategy that no longer exists; a config.json still carrying the key is ignored.

**Fixes for UPM `file:`/git installs:** the source generator now finds package-resident `.uitkx` files, and HMR starts under PackageCache (Analyzers discovery is layout-agnostic).

## [0.12.0] - 2026-07-28

### Rebrand - the library is now Reactive UI Toolkit (Unity)

**New name, new home, same library.** The whole family (Godot, Unity, Unreal) now ships under one umbrella: Reactive UI Toolkit. This repo moved to https://github.com/reactive-ui-toolkit/ruitk-unity (old links redirect), and 0.12.0 is the rename release - nothing else changed.

What it means in code:
- Namespace/assembly root `ReactiveUITK` -> `Ruitk` (`using Ruitk;`, `global::Ruitk.Core.VirtualNode`). All asmdefs are `Ruitk.*`; the analyzer DLLs renamed with GUIDs preserved, so references survive.
- `ReactiveUITKConfig` -> `RuitkConfig`; the define `REACTIVEUITK_HAS_TEST_FRAMEWORK` -> `RUITK_HAS_TEST_FRAMEWORK`.
- Install folder is now `Assets/ReactiveUIToolkit` (lowercase k). Delete the old `Assets/ReactiveUIToolKit` before importing - on Linux (case-sensitive) you otherwise end up with both folders.
- The editor menu moved from `ReactiveUITK/` to `Reactive UI Toolkit/`.

Upgrading is two steps: delete the old folder and import 0.12.0, then run the bundled codemod over your own code - `dotnet run --project Assets/ReactiveUIToolkit/SourceGenerator~/Tools/RuitkMigrateBrand -- Assets` - it rewrites usings, namespaces, and asmdef references with per-file counts, is idempotent (`--check` gates CI), and never edits inside the package folder. Full guide: `MIGRATION-0.12.md` in the repo.

Unchanged on purpose: the UPM package id `com.reactiveuitoolkit`, the `.uitkx` language and the `UITKX` tooling brand, the extension marketplace identities (they update in place: VS Code 1.8.0, VS2022 1.8.0, Rider 1.5.0), and the docs site. License: the Reactive UI Toolkit Community License 1.1 - renamed and version-bumped family-wide, terms unchanged; everything you already downloaded keeps the license it shipped with.

## [0.11.0] - 2026-07-25

**uGUI support is here.** ReactiveUIToolKit now renders classic Unity UI (uGUI) with the same components, hooks, signals, router, and hot reload you use on UI Toolkit - and it speaks uGUI's native language. No CSS, no flexbox emulation: you position with RectTransform anchors/pivots (including an `anchors` preset prop that mirrors the Inspector widget), you style with sprites, colors, and materials, you stack with LayoutGroups. If you know uGUI, you already know this API.

**What ships:**
- `UguiRootRenderer` mounts a reactive tree under any RectTransform in YOUR existing Canvas - no scene takeover.
- Elements: Canvas, Panel, Image, RawImage, Text (TMP), Button, H/V/Grid LayoutGroups, Toggle(+Group), Slider, Scrollbar, ScrollRect, Dropdown, InputField - compound controls built on the exact GameObject > UI menu skeletons.
- `<Prefab source={...} bind={...}>` - mount your existing uGUI prefabs inside the reactive tree and migrate screen by screen.
- Prop groups = declarative Add Component: layoutElement, contentSizeFitter, canvasGroup, mask, rectMask2D, shadow, outline, pointer events on any element.
- Controlled components everywhere (no event echo), one-time event subscriptions, raycast hygiene by default, and an editor hint when a layout group is driving rect props you tried to set.
- `.uitkx` files: add `@backend ugui` and the whole toolchain (compiler, hot reload, schema, IDE completion) switches vocabulary.
- v2 islands: `UguiHost` puts a uGUI subtree inside a UITK tree; `UitkHost` puts a UITK panel (render texture + input forwarding) inside a uGUI tree.

UI Toolkit users: nothing changes - output is byte-identical when the directive is absent. Bonus: uGUI field testing surfaced three fiber-core bugs (keyed reorders, update starvation, stale-root replays) - all fixed, hardening BOTH backends.

## [0.10.0] - 2026-07-18

**License:** from 0.10.0 ReactiveUIToolKit ships under the **ReactiveUI Community License 1.0** (previously PolyForm Shield) — free to use and ship commercially for any team earning under **$250k/year**; above that, shipping takes a commercial license ($2,000/title or $2,500/studio/year — same mechanic on Godot, Unity, and Unreal). Development/evaluation stays free at any size; everything already downloaded keeps the license it shipped with. Ask of everyone: a "Made with ReactiveUI" credits line. Details: `LICENSE.md` + `LICENSE-COMMERCIAL.md` in the repo — full announcement posted separately.

### Patch - field-testing wave on the ES-modules surface

**Combined imports** - the last missing ES form:
```
import isSomethingEven, { getSomething } from "./SomeOtherName.utils"
import Fallback, * as Deck from "./Deck"
```
One line, every part binds: the default plus named/namespace imports. Parses, formats, lowers, and colors as a single declaration.

**Editor fixes** (found live, F5-testing the 0.9.0 release):
- Named **value** imports resolve in the editor now - `import { container }` + bare `container` squiggled a false CS0103 (the build was always clean) because member-only files never entered the editor's semantic workspace.
- No more false "unused import" on used value imports - bare references like `style={container}` now count.
- Ctrl+Space inside `import Def, {} from` completes the target's remaining exports.
- Import names **color by what they bind**: components like elements, hooks/utils like functions, values like variables - no more uniform type tint.

Unity package **0.10.0** + extensions **1.6.0** (VS Code/VS2022), **1.3.0** (Rider).

## [0.9.0] - 2026-07-18

### Minor - ES modules: a file IS a module

**The wrapper keywords are gone (deprecated).** No more `component X {}`, `hook useX {}`, `module M {}` - you write plain typed declarations and the signature says what it is:

```
import { FormatTime } from "../Shared/TimeUtils"

export Style container = new Style { Padding = 10f };
export int MaxItems = 5;

export string FormatScore(int s) { return $"Score: {s}"; }

export (int value, Action reset) useCountdown(int start) { ... }

export VirtualNode ScoreRow(string label) {
  return ( <Label text={label} style={container} /> );
}
export default ScoreRow;
```

A `VirtualNode` return = component (PascalCase enforced). A `use`-prefixed name = hook. `= initializer` = value. Anything else = util. No `export` = file-private, for real.

**Full ES import surface:** `import { a as b }`, `import * as X` (use `<X.Comp/>` in markup), default imports, and `export { a, b };` lists.

**Every file gets its own namespace** (folder + file stem) - two files in one folder no longer share one, and companion partial-merging is deprecated (`UITKX2107`): a companion's members are imported like anything else.

**Migration is one command:** `UitkxMigrateImports --es-modules` rewrites whole trees - wrappers to plain declarations, modules exploded member-by-member, `import { Module }` becomes `import * as Module` with call sites untouched, companion sets atomically, idempotent. Old syntax still parses this minor with `UITKX2320` warnings; removal comes later. Migrating a file resets its hot-reload state once.

New diagnostics `UITKX2320-2327` + `2107-2110`. Full editor support in all three IDEs (grammar, completions, rename/go-to-def across both syntaxes). Samples fully migrated.

Pre-release audit: four review agents swept the new surface; 50+ findings fixed (parser, hot reload, rename, codemod).

Unity package **0.9.0** + IDE extensions **1.5.0** (VS Code/VS2022), **1.2.0** (Rider).

## [0.8.3] - 2026-07-16

### Patch - a trailing `;` no longer wrecks an import

**If `import { X } from "./file";` blew up your whole file - this is the fix.** That trailing semicolon is the JS-canonical form, and pure muscle memory when the rest of the file is C# - but the file-import reader stopped right after the closing quote. The parser's cursor stalled on the `;`, the preamble loop bailed, and the entire file failed with a misleading `UITKX2105` ("no valid component declaration") plus phantom "single root element" errors on lines that were never markup.

The fix is parity, not a patch: `@using Ns;` and `import "@Ns";` both already consumed to end-of-line - the file-import reader was the one sibling missing that step. Now all three preamble forms tolerate the semicolon, and the formatter re-emits the canonical, semicolon-less line on save.

Bonus: with the parse meltdown gone, a genuinely wrong import gets its real diagnostic - e.g. importing a style-module field (`import { container } from "./X.style"`) now correctly reports `UITKX2301: not exported` instead of nuking the file. (Reminder: same-folder `X.style.uitkx` companions need **no import at all** - their module statics are visible bare, like `style={container}`.)

**HMR - style/module companions hot-reload again.** The module leg of hot reload still emitted under the RAW parsed namespace while everything else moved to the EFFECTIVE one - so in a stamp-less project, a companion's partial class never merged with its component (bare `container` refs failed `CS0103` under HMR while the build was clean) and `.style` edits silently swapped nothing (the static swapper matches types by `FullName`). All namespace threading now goes through the same effective-namespace seam as the build; even a companion file created mid-session hot-compiles - no domain reload. Contract tests pin the seam.

Unity package **0.8.3** + IDE extensions **1.4.4** (bundled LSP carries the same parser). SG suites green.

## [0.8.2] - 2026-07-16

### Patch — HMR root reload + import parity

**Root components hot-reload now.** Editing the root component of a mount never swapped: child tags go through Family-based `V.Func` calls the generator emits, but a hand-written root mount (`rootRenderer.Render(V.Func(App.Render))`) passes a raw delegate — leaving the root fiber with no Family, so the refresh walk skipped it silently. The plain `V.Func(delegate)` overloads now resolve the Family from the method group's declaring type (a family's id *is* the component's FQN — exact by construction). Editor-only, zero API change. Bonus: a zero-swap now logs *why* (key mismatch / not mounted / nothing dirty) instead of saying nothing.

**Hot reload now sees exactly what the build sees.** HMR injected `using static` for same-folder hook companions only — it never injected the module/component **type aliases** that imports imply. With path-derived namespaces, hot-editing a component whose C# body references an imported module (`SidebarItem`) or an imported component's type (`MetricDisplay.MetricType`) compiled fine in a full build but failed the hot-swap with CS0246. The injection rule now lives in one shared helper (`ImportScopeFacts`) consumed by the source generator's world, the editor's virtual document, and the HMR compiler — one rule, three consumers, contract-tested so it can't silently drift.

**Editor squiggle fix:** IntelliSense aliased imported modules/components with the target's *raw* parsed namespace — wrong for every stamp-less (path-derived) file, so `namespacePrefix` projects showed false red squiggles on cross-file references. Aliases now use the target's **effective** namespace (explicit `@namespace` wins, else path-derived + config).

Unity package **0.8.2** + IDE extensions **1.4.2** (bundled LSP). SG `1541/1541`, LSP `118/118`.

## [0.8.1] - 2026-07-16

### Patch — Router resolution fix for 0.8.0

**If `<Router>` / `<Routes>` / `<Route>` started erroring after 0.8.0 (`UITKX0008` "could not be found", `UITKX0109` unknown attribute `path`/`element`) — this is the fix.** 0.8.0 auto-injected `ReactiveUITK.Router`, so files stopped writing `import "@ReactiveUITK.Router"` — but the generator's tag/props resolver still searched only the file's own usings. The router tags then failed to resolve their props at build time even though the emitted C# was fine.

The resolver now searches the exact auto-injected baseline the emitters put in scope — one shared list, so the two can never drift again. Regression tests pin the direct (`<RouteFunc>`) and alias (`<Router>/<Routes>/<Route>`) paths.

**Also: file imports are now fully self-sufficient for components.** A cross-namespace `import { Widget } from "./file"` used to need an extra namespace-import for the tag's attributes to validate, and C# body references to the component's type (`Widget.WidgetProps`, `TableView.Column`) hit CS0246. The import now carries both: the target's namespace joins the resolver's scope, and a type alias is injected (like modules get) — mirrored in the editor virtual doc, shipped as extensions **1.4.1**.

**The 0.8.0 `dist` publish predates these fixes** — git-URL `#dist` consumers should update to **0.8.1**. SG `1532/1532`, LSP `118/118`.

## [0.8.0] - 2026-07-15

### Namespace imports + the diagnostics that make a typo visible

**One way to bring things in.** A C# namespace can now come into scope with `import "@ReactiveUITK.Router"` — exactly equivalent to `@using ReactiveUITK.Router` (same generated `using`). The distinction is the point: `import { X } from "./file"` (braces + `from`) imports a peer `.uitkx` file and is name-checked; a quoted `"@Namespace"` imports a C# namespace. Static/alias forms work too: `import "@static UnityEngine.Mathf"`. `@using` keeps working forever — the unified form is just the recommended spelling.

**UITKX2316 — unknown namespace.** A misspelled `@using` used to be completely silent in the editor (you'd only get a raw CS0246 buried in generated code). Now it's flagged like a bad import: an **error squiggle in the editor** on the namespace token, and a **build warning** (it never breaks an otherwise-valid build — the real CS0246 stays the gate). No false positives: validated against the compilation plus every peer `.uitkx` namespace.

**UITKX2317 — redundant using (Hint).** `@using UnityEngine` / `@using System` and friends are already auto-injected, so they're flagged as dead weight (faded) with a one-click "Remove redundant using".

**Tooling:** a "Convert to `import \"@…\"`" refactor on any `@using`; the codemod's new `--tidy` converts + strips redundant usings in bulk; the formatter round-trips both spellings.

**Plus:** the formatter now puts `@namespace` first and groups all imports under it; `RouterHooks` is auto-injected so `import "@ReactiveUITK.Router"` is never needed; and a new `uitkx.config.json` **`namespacePrefix`** lets a whole project carry its own namespace root — no per-file `@namespace` — falling back to the asmdef's `rootNamespace`.

Additive — existing files keep working. IDE extensions ship the same as **1.4.0**. SG `1527/1527`, LSP `118/118`.

## [0.7.1] - 2026-07-15

### Patch - strict import diagnostics, corrected

**If 0.7.0 flooded your project with `UITKX2307` errors on `useState` / `useEffect` / every built-in hook - this is the fix.** The strict-import exemption list only knew the PascalCase names (`UseState`) while real files call the camelCase aliases (`useState(`), so any project with exported hooks/modules errored on every built-in call site. Exemption now covers both spellings, in the Unity build and the editor.

**Heuristic findings are warnings now, not errors.** Bare hook calls (`useX(`) and module member access (`Name.member`) are scanned out of plain C# text - which ambient C# legitimately produces (hand-written hooks, nested enums via `@using static`, `Screen.width`). Those `UITKX2305`/`2307` matches no longer break the build; a genuinely missing import still fails with CS0103. Component tags (`<X>`) keep error severity - that syntax is uitkx-only, so the evidence is sound.

**Linux/macOS: import resolution fixed.** Rooted paths lost their leading `/` during specifier resolution, silently breaking go-to-definition on imports, `import { }` completion, the quick-fix, and live diagnostics on those platforms.

Update via Package Manager (git `#dist` consumers: refresh the package). IDE extensions carry the same fixes as **1.3.1** (VS Code / VS 2022). SG `1472/1472`, LSP `107/107` - plus a new corpus gate that compiles all bundled Samples through the real generator on every test run, so this class of regression is now caught before CI's floor-Unity import.

---

## [0.7.0] - 2026-07-12

### Imports & Exports - cross-file references are now explicit

**`.uitkx` gets ESM-style `import` / `export`.** A file declares what it exposes with `export component` / `export hook` / `export module`, and pulls in what it needs with `import { X } from "./path"` (relative `./`/`../` or the `~/` UI-root alias, extensionless). Referencing a peer name you didn't import is an error that names the exact `import` line to add. A file may declare any mix of components, hooks, and modules.

### Path-derived namespaces + accessibility

- A file's default namespace is now **derived from its path** relative to the owning `.asmdef` (`ReactiveUITK.Uitkx.<folders>`); `@namespace` becomes an optional override. Stable identity that no longer flips when you edit a companion `.cs`.
- **`export` drives accessibility** - exported = `public`, otherwise `internal` (file-private). Pure `.uitkx` usage inside one assembly is unaffected; hand-written cross-asmdef C# that referenced a now-`internal` type may need an `export`.
- **HMR hook identity is path-qualified** - hot-swap matches hooks by `{Ns}.{Container}::{name}`, so two same-named hooks in different files never cross-swap.
- One-component-per-file / filename==component are now **documented conventions**, not compiler warnings.

### Migration - one command

The bundled **`UitkxMigrateImports`** codemod rewrites a project in place: it adds `export` everywhere, inserts the imports each file needs, and stamps each file's current `@namespace` so identity is preserved. Idempotent and formatter-stable - run `--check` first for a dry run. The bundled Samples ship already migrated.

### Editor

Go-to-definition on imports, completion inside `import { }` and specifier strings, a one-click quick-fix for the "used but not imported" error, and live strict diagnostics (UITKX23xx) as you type.

Ships to Unity via the committed analyzer DLL - no IDE update required. SG `1465/1465`, LSP `107/107`.

---

## [0.6.5] - 2026-07-08

### Fix - `.uitkx` edits show up again on a plain recompile (Play + HMR off)

**The "edit, save, recompile, see the change" baseline is restored.** Since 0.5.10, saving a `.uitkx` in an `.asmdef` assembly (most real projects) left generated output **stale** with Play/HMR off - the component kept its old markup until a full Reimport / `Library` delete / any `.cs` edit. The change-watcher only dirtied Assembly-CSharp, never the assembly your components live in, so the generator never re-ran. (Regression from 0.5.10 dropping `CleanBuildCache`.) It now writes its recompile trigger into the **owning assembly's own folder** - one incremental recompile, analyzer stays warm, no 30-40s stall, skipped while HMR is active.

### HMR pipeline

- **`@if` / `@foreach` / `@for` / `@while` / `@switch` bodies hot-swap again** (the emitter had been reading the body off the wrong AST node).
- **`module` static-method edits refresh consumers** without a domain reload - and no more spurious `Could not find hook container` warning.
- **No hot-swap from a syntactically-broken file** - a parse error now leaves your running UI untouched instead of swapping the wrong UI in silently.
- Relative asset paths (`"./icon.png"`) resolve the same across editor, build, and HMR; a shared `.uss` edited across many components no longer freezes the editor.

### Parser, formatter, IDE

Formatter no longer corrupts commented-out code; `@switch (expr)` gets hover / diagnostics / go-to-def; six drifted mini-lexers consolidated into one; plus a batch of parser edge cases. All ship to Unity via the committed analyzer DLL - no IDE update required.

SG `1337/1337`, LSP `82/82`. VS Code / VS 2022 **1.2.18**, Rider **1.0.1**.

---

## [0.6.4] - 2026-06-19

### Fix - interrupted renders no longer duplicate UI, strand routes, or freeze the editor

**An update that lands mid-render is now safe.** When a time-sliced render parked between slices (across frames) and a state update or navigation arrived before the next commit, the reconciler restarted the work loop from the WIP root but did NOT clear the partially-built effect list -- that list lives on the persistent FiberRoot and was only ever cleared in `CommitRoot`. The interrupted pass's stale `Placement` effects then committed again, so freshly-mounted elements were appended twice (and once more per restart), and a route that should have unmounted could stay mounted beside the new one. Worst case: the re-appended chain spliced `NextEffect` into a cycle and `CommitRoot`'s walk spun forever, freezing the editor.

`FiberReconciler.ScheduleUpdateOnFiber` now discards `_root.FirstEffect` / `_root.LastEffect` and resets `_hasDeletions` when an update restarts the work loop; the restarted walk rebuilds the effect list from scratch. No API change, no player cost -- single-slice renders are untouched.

The window is narrow (an update must land after a slice yields but before the next commit), so it surfaced only under heavy pause-time churn: rapid route navigation while wall-clock `schedule.Execute().Every()` timers keep firing state at `Time.timeScale == 0`.

**Guidance.** Wrap bare `<Route>` siblings in `<Routes>` for atomic single-best-match selection -- without it, exclusivity is emergent (each unmatched route renders null) and two routes can briefly co-exist under the same stress.

Library-only release. IDE extensions unchanged at VS Code 1.2.17 / VS 2022 1.2.17.

---

## [0.6.3] - 2026-06-13

### Custom rendering - draw straight into any element with onGenerateVisualContent

**Declarative custom drawing landed.** Every element now takes an `onGenerateVisualContent` attribute that binds Unity UI Toolkit's `VisualElement.generateVisualContent` delegate (`Action<MeshGenerationContext>`). Draw vector shapes with `ctx.painter2D` or raw vertex/index meshes with `ctx.Allocate(...)` - charts, gauges, custom backgrounds - while the rest of your UI stays reactive. It is inherited from `BaseProps`, so it works on `VisualElement`, `Button`, and every built-in element.

```jsx
<VisualElement onGenerateVisualContent={ctx => DrawHelpers.Polygon(ctx, sides)} />
```

**Reactive repaints, your call.** The element repaints when the callback reference changes between renders, or when the new `redrawKey` (an int) changes. A fresh inline lambda redraws every render like any other prop; stabilize it with `useMemo` / `useStableCallback` and bump `redrawKey` for on-demand repaints without swapping the delegate. The callback runs at Unity paint time, so treat the element as read-only inside it.

**Player-safe.** `MeshGenerationContext`, `Painter2D`, and `Vertex` are runtime `UnityEngine.UIElements` types - no `#if UNITY_EDITOR` gating, identical in Editor and built games.

**Sample + docs.** New `CustomDrawDemoFunc` showcase - a Painter2D polygon by state, a raw `ctx.Allocate` quad, and a stable-callback + `redrawKey` scatter. Open it from the Unity menu under ReactiveUITK -> Demos -> Custom Drawing. New Custom Rendering guide added to the docs site. SG suite `1266/1266` passing.

VS Code **1.2.16 -> 1.2.17** | VS 2022 **1.2.16 -> 1.2.17** ship the schema and attribute-lambda typing.

---

## [0.6.2] - 2026-06-05

### Unity 6.3 panel-rebuild defense is now editor-only - zero player cost

**Unity fixed the bug, so the workaround is compiled out of builds.** 0.5.6 added a per-frame `UIDocument.rootVisualElement` poll to survive the Unity 6.3 regression where the panel is silently rebuilt on every `InspectorWindow.RedrawFromNative` (reported as `UUM-127851`). Unity shipped the fix in 6000.3.17f1 / 6000.4.9f1 / 6000.5.0b9 / 6000.6.0a6 - verified on real hardware: the root swaps about once per second while the UIDocument is selected in the Hierarchy on 6000.3.8f1, and is silent on 6000.3.17f1.

More importantly, every swap the poll defends against is editor-only: `RedrawFromNative` never runs in a player, and undo, asset-swap, disable/enable, and HMR are all editor mutations. In a built game the only root swaps are developer-initiated and already flow through the always-on reactive path, so the poll is dead weight in shipped builds. It is now wrapped in `#if UNITY_EDITOR` in both `RootRenderer.Initialize(UIDocument)` and `Hooks.UseUiDocumentRoot`.

**No API change, no player regression.** `Initialize(UIDocument)` still seeds the initial root in every build. `UseUiDocumentRoot` still does its initial capture plus one effect-time resync and still re-runs when the `doc` reference changes - only the per-frame poll for silent same-reference swaps is editor-gated. Consumers that swap the document through their own state (e.g. a UIDocument slot registry firing on enable/disable) behave identically in players.

Library-only release. IDE extensions unchanged at VS Code 1.2.15 / VS 2022 1.2.15.

---

## [0.6.1] - 2026-05-23

### Unified hook metadata registry - drift bugs cannot ship again, `useLayoutEffect` now diagnosed + documented

**Eight hand-maintained hook tables collapsed into one source of truth.** Every public hook had its name, signature regex, alias rewrite, validation pattern, hover doc, and Roslyn-only stub block copied across 5 files (SG `CSharpEmitter` / `HooksValidator`, both HMR emitters, `DiagnosticsAnalyzer`, `HoverHandler`, two `VirtualDocumentGenerator` stub blocks). Every release between 0.5.18 and 0.6.0 shipped at least one bug that was one table missing an entry the others had (7 camelCase aliases in 0.5.21; 10 hover docs in 0.5.22). Replaced with `ReactiveUITK.Core.HookRegistry` - `#if UNITY_EDITOR` static class with cached accessors all 5 consumers read from. Zero player cost. Linked into language-lib via `<Compile Include Link>`; SG inherits transitively via existing `ProjectReference`, avoiding `CS0436`.

**Fix - `useLayoutEffect` is now a real hook to the IDE.** Shipped runtime-side and SG-aliased several releases ago, but the validator and hover layers were never updated. Calling it inside an `if` silently passed `UITKX0013` (rules-of-hooks); hovering returned no docs. Registry adds the 3 missing validation patterns and 2 missing hover entries - pure additive coverage.

**Tests.** SG `1264/1264` passing. New `HookRegistryTests.cs` (16 tests) covers internal invariants (count, cardinality, accessor-reference caching for the per-keystroke hot path), runtime reflection parity against `Hooks`, and byte-for-byte golden equality against 8 snapshots captured at the 0.6.0 baseline. The validation-patterns golden asserts the diff is exactly the 3 `useLayoutEffect` entries - future drift fails a test naming the specific table.

VS Code **1.2.14 -> 1.2.15** | VS 2022 **1.2.14 -> 1.2.15** ship the new analyzer DLL.

---

## [0.6.0] - 2026-05-22

### HMR ported to React Fast Refresh - zero player cost, custom-hook edits invalidate consumers

**Cascade-drift bug gone.** The old trampoline compared `MethodInfo.DeclaringType` at the reconciler; after a cascade compile, parent and child delegates came from different DLLs, the reconciler saw a type mismatch and tore down state in components the user never edited (saving a leaf re-mounted the page root). Replaced with React Fast Refresh's Family indirection - a `Family` handle with a mutable `Current` slot. The SG emits one `__fam_<Child>` per child type plus a `[ModuleInitializer]` Register on a `*__UitkxRefresh` companion (keeps the component's `.cctor` cold on Mono). Reconciliation compares Family references - one identity per child type regardless of depth. Render crash -> `Family.Current` reverts to the previous body.

**Zero player cost.** `Family`, `RefreshRuntime`, the `V.Func(Family, ...)` overloads, and the identity/rollback paths are all wrapped in `#if UNITY_EDITOR`. The SG emits dual-shape `V.Func` per child site, so player builds compile down to direct delegate calls identical to 0.5.x. Mirrors React's `$RefreshReg$` model.

**Custom-hook edits invalidate consumers.** A hook in a separate `.uitkx` was a black box - adding or removing `useEffect` left the consumer's signature unchanged, so HMR re-rendered without resetting hook state. Ported `customHooks`: `[HookSignature]` now takes a `string[] customHookFamilyKeys`. Hook authors get `RefreshRuntime.RegisterHook(...)` on a `*__UitkxHookRefresh` companion. Every Register builds a `hookId -> consumerIds` reverse map; `PerformRefresh` fans out from dirty hooks (re-render, plus force-remount when the hook signature itself changed). Editor-only.

**Tests.** 1245/1245 SG passing. New invariant tests lock in the GetFamily fallback-factory and ModuleInitializer-on-companion-only contracts.

VS Code **1.2.13 -> 1.2.14** | VS 2022 **1.2.13 -> 1.2.14** ship separately.

---

## [0.5.22] - 2026-05-20

### IDE finally type-checks JSX inside attribute lambdas, plus UseTransition lands

**The silent IDE blind spot is gone.** The virtual document generator's `EmitMappedExpressionStrippingJsx` replaced JSX subtrees inside attribute values with `(VirtualNode)null!` - so `onClick={e => <Inner badProp={42} />}` and ternaries returning JSX from inline expressions produced zero diagnostics in VS Code / VS 2022, even though the SG and cold build flagged them correctly. The strip helper now records each stripped range; a new `EmitDeferredJsxAttributeChecks` re-parses the subtree and emits Pattern-B `dynamic __uitkx_jsxattr{pos}() { ... }` locals so Roslyn sees the full type-check graph without touching runtime emission. Thread-static `t_jsxAttrContext` carries source + directives to the 3 deferred sites.

**New runtime hook `Hooks.UseTransition()`.** Returns `(bool isPending, Action<Action> startTransition)`. UITKX has no concurrent renderer: `isPending` is always `false`, `startTransition(action)` runs synchronously, start delegate is `static readonly` (zero alloc). 0.5.21's `useTransition` alias rewrite now resolves at runtime instead of failing with `CS0117`.

**10 hooks now resolve in the IDE.** The VDG's stub blocks were missing shadow decls for `useReducer`, `useDeferredValue`, `useImperativeHandle`, `useStableFunc/Action/Callback`, `useTweenFloat`, `useAnimate`, `useSafeArea`, `useTransition`. Per-component scope shadowed the unqualified call sites so hover / go-to-def / signature-help silently failed. Both stub blocks now mirror the SG hook-shadow set 1:1.

**Validator + hover widened.** Pattern tables (SG + language-lib mirror) grew 30 -> 60 strings so `UITKX0013` now fires for 10 more hooks. Hover docs grew 16 -> 40 entries; `useTransition` calls out the synchronous UITKX semantics.

**Tests.** SG `1245/1245` (+1 alias parity); +2 LSP `RoslynHostTests`.

VS Code **1.2.12 -> 1.2.13** | VS 2022 **1.2.12 -> 1.2.13**.

---

## [0.5.21] - 2026-05-19

### HMR cascade-batch no longer re-fires mount effects, plus 7 hook aliases unlocked

**Scene duplication on save is gone.** When the 0.5.20 cascade walker pulled transitive ancestors into a batch, `NotifyMatchingFibers` invoked `FullResetComponentState` on every matched fiber - wiping `useEffect` cleanups and re-firing mount effects on the next render. In the wild: an additive `SceneManager.LoadSceneAsync` on a parent fired a second time when an unrelated sibling was edited. The cascade walker orders results dependents-first / originator-last, so the controller now threads `isOriginatingChange = (i == paths.Count - 1)` into `SwapAll` and gates the rude reset on it. Cascaded files still get the cheap trampoline-field swap (next render uses new IL), but fiber state and effect cleanups stay intact.

**Wrong-namespace warning spam fixed.** The hook-module branch resolved the assembly namespace via `LoadedAssembly.GetTypes().FirstOrDefault().Namespace`. Roslyn's embedded `EmbeddedAttribute` materialises first in metadata order, so the probe returned `Microsoft.CodeAnalysis` and the swapper logged "type not found" on every save. Declared `@namespace` is now threaded through `HmrCompileResult.Namespace` and read directly.

**7 more hooks usable in camelCase.** `useTweenFloat`, `useAnimate`, `useSafeArea`, `useStableFunc`, `useStableAction`, `useStableCallback`, `useImperativeHandle` were in every signature scanner but missing from every rewrite table - CS0103 in `.uitkx` setup blocks. Added 7 entries to `s_hookAliases` and 3 to `s_genericHookAliasRe` in lockstep across SG + 2 HMR layers; 7 new parity-contract tests added so future hook additions cannot silently drift. SG suite `1244/1244` green.

**Deferred.** `OPTIMIZATIONS.md` #1 (dep index over-links copy-rename files) is now an optimisation only after the cascade gate above.

No extension release. VS Code / VS 2022 unchanged.

---

## [0.5.20] - 2026-05-18

### HMR save cascade - one save propagates through the dependency graph

**The "save twice" tax is gone.** Editing a child component used to update only that file - parents kept the cold body until you saved them too. Module value edits (`Theme.Accent`) updated the module but not derived fields like `StatsPanel.Container.BorderColor`. Saving a `.uitkx` now enqueues every transitive consumer in the same asmdef via a real FIFO queue + cascade walker and recompiles in topological order through `EditorApplication.delayCall`. The prior single-slot queue was dead code; concurrent saves were silently dropped. Fixed.

**Per-SCC union compile (TD22B).** When a cascade pulls 2+ files in one asmdef, HMR compiles them as a single Roslyn union assembly so a refactored `ChildProps` shape resolves to one authoritative type across parent + child. Fenced by pre-compile uniqueness + post-compile assembly-identity checks; falls back to per-file compile on any failure so user-facing CS0117 / CS0246 / CS0433 stay loud. Telemetry: `[HMR] union: N files, M ms`.

**New `.cs` pickup.** Helper `.cs` files referenced from a `.uitkx` are now picked up before Unity has recompiled the project DLL. Asmdef-scoped, mtime-gated, AppDomain-deduped to avoid CS0101.

**Two new diagnostics.** `UITKX0113` flags duplicate `component` declarations in the same asmdef. `UITKX0211` flags `const` fields inside `module { }` bodies - consts inline at compile time and never propagate under HMR; use `static readonly` instead.

**Dev quality of life.** SG csproj now writes to `SourceGenerator~/bin/` and copies into `Analyzers/` separately. Local `dotnet build` no longer hits `MSB3027` when Unity holds the analyzer DLL lock. CI unchanged.

**Tests.** SG `1237/1237` passing. `+6` LSP `WorkspaceIndexDuplicateTests` pin the multi-valued index contract.

VS Code **1.2.10 -> 1.2.11** | VS 2022 **1.2.10 -> 1.2.11**.

---

## [0.5.19] - 2026-05-16

### Unity 6.3 style types reachable by short name from .uitkx

`UnityMaterial`, `AspectRatio` and `Filter` were added to `Style` in the Unity 6.3 batch, but none of the five emitters that produce per-component, per-hook, per-module and per-HMR .cs files included the new `StyleMaterialDefinition`, `MaterialDefinition`, `StyleRatio`, `Ratio` or `FilterFunction` types in their alias block. Result: an obvious user expression like `UnityMaterial = new StyleMaterialDefinition(new MaterialDefinition(mat))` failed at compile time with `CS0246`, even though the typed `Style.UnityMaterial` property inside the library compiled fine. Fixed by emitting all five types as preprocessor-guarded (`#if UNITY_6000_3_OR_NEWER`) aliases from CSharpEmitter, ModuleEmitter, HookEmitter, HmrCSharpEmitter, HmrHookEmitter and the IDE LSP virtual document.

### HMR component emitter brought back to alias parity with the SG

While investigating the Unity 6.3 bug above I found a long-standing latent parity gap: `HmrCSharpEmitter` only emitted `using Color = UnityEngine.Color;` while the source generator's `CSharpEmitter` emits 12 targeted UIElements aliases (EasingFunction, BackgroundRepeat, Length, StyleKeyword, TextAutoSizeMode, etc.). Any user file referencing those by short name compiled clean on a cold project build but failed on hot-reload with `CS0246`. HMR now emits the full alias block, and a new text-level parity test reads both emitters' source on disk to catch future drift.

### New typed helpers: MaterialDef + Ratio

Authoring `UnityMaterial = MaterialDef(myMat)` and `AspectRatio = Ratio(16f / 9f)` now work directly. Mirrors the existing `FontDef` and `Filter*` precedents. The untyped `PropsApplier.unityMaterial` setter also accepts a bare `Material` (matches `aspectRatio`'s multi-shape precedent).

Additive release. No API removals; existing verbose forms continue to compile.

VS Code 1.2.10 / VS 2022 1.2.10 ship alongside (LSP virtual document fix).

## [0.5.18] - 2026-05-15

### HMR - critical follow-up to 0.5.17 (UNITY_EDITOR define)

0.5.17's "swap on prior HMR DLL types" couldn't actually work in production. The HMR Roslyn compile used `CSharpParseOptions.Default` which defines no preprocessor symbols, so every HMR-compiled DLL had its `__hmr_Render` static field and the trampoline branch in `Render` stripped by the `#if UNITY_EDITOR` guards in `HmrCSharpEmitter` and the source generator. Symptom seen in the wild: `Component 'X' has no '__hmr_Render' field` warning followed by no swap, even though my new code correctly found prior HMR DLL types as swap targets.

Fix: the HMR compile now defines `UNITY_EDITOR` plus Unity's full editor define-set. Symbols are pulled from `CompilationPipeline.GetAssemblies(AssembliesType.Editor)` so version pragmas, scripting backend pragmas, and any user-defined symbols match the project's actual editor compile. Falls back to `UNITY_EDITOR`-only if the API is unavailable.

With this in place, HMR DLLs carry the trampoline field and the parent's `<NewComponent />` binding actually flows through it on every subsequent save - the original "create new component live, every edit hot-swaps without domain reload" workflow finally works end-to-end.

Secondary correctness fix: user companion .cs `#if UNITY_EDITOR` blocks now compile with the same semantics in HMR as in the project's editor build (previously they compiled with the opposite, a latent correctness trap).

If you have HMR DLLs from 0.5.17 or earlier in `%TEMP%/UitkxHmr/`, restart Unity once after upgrading. Subsequent sessions emit DLLs with the trampoline intact.

Library-only release. IDE extensions unchanged at VS Code 1.2.8 / VS 2022 1.2.8.

## [0.5.17] - 2026-05-15

### HMR - live-create new components without a domain reload

You can now create a new `.uitkx` component during a live HMR session and every save hot-swaps it just like an existing component. Previously the first save compiled fine but subsequent edits silently no-op'd: HMR's trampoline swap looked for the project-loaded type and a brand-new component has none (the source generator only runs on assembly recompile, which HMR holds locked).

The fix: `SwapAll` now finds every loaded type for the changed component, including types from prior `hmr_*` DLLs that earlier HMR cycles produced. As soon as a parent compiles with a reference to the new component, the parent's emitted IL binds via a compiler-cached method-group delegate to the HMR DLL's type. Subsequent saves write the new delegate into THAT DLL's `__hmr_Render` static field - the field the parent's binding actually reaches at render time - so the new body executes without any rebinding. Symmetric across multiple HMR generations: version N+1 updates every prior generation's trampoline.

Also fixed in the same release: the `_pendingRetryPaths` queue used to accumulate entries for files that no longer exist on disk (e.g. after a copy-rename-edit cycle while HMR was active), producing a `FileNotFoundException` cascade on every retry pass forever. The AssetPostprocessor now forwards `deletedAssets` and `movedFromAssetPaths` to the watcher, the watcher exposes a new `OnUitkxDeleted` event, and the controller evicts the stale path. Belt-and-braces: the compile-failure branch now checks `File.Exists` before adding to the retry queue.

Visible log when the first compile of a brand-new component has no consumer yet, so the journey reads correctly instead of looking dead.

Editor-only changes - runtime and built-game performance unaffected.

Library-only release. IDE extensions unchanged at VS Code 1.2.8 / VS 2022 1.2.8.

## [0.5.16] - 2026-05-15

### HMR file watcher - parallel AssetPostprocessor catches dropped FSW events

**The original bug, properly fixed.** Saves on deeply nested `.uitkx` files (e.g. `Assets/UI/Pages/GamePage/components/PlayerHud/components/StatsPanel.uitkx`) could land with no `[HMR]` log and no visual change, while saves on the parent worked. Root cause: Mono's `FileSystemWatcher` on Windows uses an 8 KB internal buffer that overflows under realistic Unity save bursts (every save also touches `.meta` files plus side-files). On overflow the OS silently drops events for arbitrary files.

0.5.14 and 0.5.15 tried to fix this by changing FSW configuration directly - raising `InternalBufferSize`, subscribing `Error`, reordering `EnableRaisingEvents`. Empirically that broke Mono 6.13.0 (Visual Studio built mono): the watcher silently stopped delivering events at all, killing HMR end-to-end. Both attempts have been reverted; the FSW config block in `UitkxHmrFileWatcher.cs` is byte-identical to 0.5.13 again.

The real fix in 0.5.16 is a parallel event source via Unity's `AssetPostprocessor.OnPostprocessAllAssets`. It runs on the main thread whenever Unity refreshes the asset database after a save, never drops events, and does not depend on Mono FSW. The watcher's existing `_pendingChanges` dictionary already dedupes by path, so redundant events from FSW + AssetPostprocessor coalesce into one swap via the 50 ms debounce window.

New file `Editor/HMR/UitkxHmrAssetPostprocessor.cs`. The watcher registers with it on `Start` and unregisters on `Stop`; while HMR is inactive the postprocessor is a no-op.

0.5.14 and 0.5.15 are superseded - jump straight to 0.5.16 if you installed either.

Library-only release. IDE extensions unchanged at VS Code 1.2.8 / VS 2022 1.2.8.


---

## [0.5.15] - 2026-05-15

### Hotfix - HMR watcher init order broke event delivery on Mono (regression in 0.5.14)

**HMR went completely silent after upgrading to 0.5.14.** Start logged normally, the assembly-reload lock engaged, but saving a `.uitkx` file produced no `[HMR]` output - and even with the new Verbose watcher trace toggle on, no `[HMR][trace] FSW ...` lines appeared. The watcher was alive but deaf.

Root cause: 0.5.14 set `InternalBufferSize = 64 * 1024` and `EnableRaisingEvents = true` inside the `FileSystemWatcher` object initializer. On .NET Framework that order is harmless, but on Unity's Mono runtime `EnableRaisingEvents` setter starts the native backend immediately - before event handlers are subscribed and before the buffer-size setter runs. On some Mono versions this leaves the watcher in a half-initialized state where it never raises any events for the lifetime of the instance.

Fix: configure properties in explicit order rather than via initializer.

- Configure `Path` / `IncludeSubdirectories` / `NotifyFilter` first.
- Subscribe `Changed` / `Created` / `Renamed` / `Error` handlers second.
- Set `InternalBufferSize` third, wrapped in `try/catch`. If Mono refuses the larger size, log a `LogWarning` and fall back to the default 8 KB instead of leaving the watcher dead.
- Set `EnableRaisingEvents = true` last, when the instance is fully wired.

If you ran 0.5.14, upgrade and Stop -> Start HMR once. The Verbose watcher trace toggle and 64 KB buffer behave as documented; if Mono ever refuses the buffer bump you will see the warning and HMR will keep working at 8 KB.

Library-only release. IDE extensions unchanged at VS Code 1.2.8 / VS 2022 1.2.8.

---

## [0.5.14] - 2026-05-15

### Hotfix - HMR file watcher dropped events under save bursts

**Saves on deep paths silently did nothing.** Editing a component like `Assets/UI/Pages/GamePage/components/PlayerHud/components/StatsPanel/StatsPanel.uitkx` could land with no `[HMR]` log and no visual change, while saves on the parent worked fine. Removing the component from its parent and re-adding it looked like a fix - but only because the fresh mount picked up the project-loaded type, not a real swap.

Root cause: `FileSystemWatcher` in `Editor/HMR/UitkxHmrFileWatcher.cs` ran with the default 8 KB internal buffer and had no `Error` subscription. Unity touches `.meta`, `Library/`, and other side-files on every save, so the FSW queue easily overflows when watching the full `Assets/` tree - and on overflow the OS silently drops events for arbitrary files. With no `Error` handler, the loss was invisible.

Two changes:

- Bumped `InternalBufferSize` to 64 KB (the documented maximum). Costs a few KB of pinned memory and removes the overflow under realistic Unity save bursts.
- Subscribed `FileSystemWatcher.Error` and surfaced it as a `Debug.LogError` so future overflows are loud instead of silent.

Restart HMR (Stop -> Start in the HMR window) once on this version so the new buffer size takes effect.

**Feature - Verbose watcher trace toggle.** New "Verbose watcher trace" setting in the HMR window logs every raw `.uitkx` / `.uss` / `.cs` event the OS delivers as `[HMR][trace] FSW <ChangeType> <path>`. Use it when a save appears to do nothing: no trace line means the event never reached the editor (overflow recurrence, antivirus hook, OneDrive/symlink path, file held by another process), and the fix is upstream of HMR. Off by default. Backed by `EditorPrefs` key `UITKX_HMR_VerboseWatcher`.

Library-only release. IDE extensions unchanged at VS Code 1.2.8 / VS 2022 1.2.8.

---

## [0.5.13] - 2026-05-15

### Hotfix - Editor/HMR namespace mismatch (regression in 0.5.12)

**Unity Editor compile broken in consumer projects.** The two new Editor-only files shipped in 0.5.12 - `HookContainerRegistry` and `AsmdefResolver` (HMR copy) - declared `namespace ReactiveUITK.Editor.HMR` while every other file under `Editor/HMR/` lives in `ReactiveUITK.EditorSupport.HMR`. Five `CS0103` errors at the call sites in `UitkxHmrController.cs` and `UitkxHmrCompiler.cs`, plus one cascade `CS0019` from the broken type binding.

```text
Editor/HMR/UitkxHmrController.cs(194,13): CS0103: The name 'HookContainerRegistry' does not exist in the current context
Editor/HMR/UitkxHmrCompiler.cs(495,34):  CS0103: The name 'AsmdefResolver' does not exist in the current context
```

Why the SG/LSP `dotnet test` suite did not catch it: the `Editor/` folder is excluded from the test projects, so only Unity's own csc invocation links these files together. Both new files now declare `namespace ReactiveUITK.EditorSupport.HMR` to match the rest of the folder. No behaviour change vs. 0.5.12 once compiling.

Library-only release. IDE extensions unchanged at VS Code 1.2.8 / VS 2022 1.2.8.

---

## [0.5.12] - 2026-05-14

### Hooks across folders and namespaces - work in build, IDE, and HMR

**Cross-directory + cross-namespace hook resolution.** A hook in `Assets/UI/Hooks/UseUiDocumentSlot.hooks.uitkx` (namespace `PrettyUi.UIHooks`) consumed by a component in `Assets/UI/Pages/MenuPage.uitkx` (namespace `PrettyUi.UI.Pages`) used to require a manual `using static` at every consumer, and HMR recompiles silently dropped the directive (CS0103 in the swap, even though the SG build was green).

```jsx
@namespace PrettyUi.UI.Pages
component MenuPage {
  var slot = useUiDocumentSlot("Main"); // hook lives 3 folders away
  return (<UIDocument source={slot}/>);
}
```

Three layers fixed in lockstep so squiggle, build, and HMR agree:

- **SG Stage 3d** drops the `phc.Namespace == directives.Namespace` strict check. Asmdef ownership is already enforced by `UitkxGenerator`'s pre-scan via `IsOwnedByCompilation`; injection is now unconditional within an asmdef and de-duplicated via a hash set.
- **LSP virtual document.** `RoslynHost.EnrichWithPeerHookUsings` mirrors the SG fix and gates by asmdef ownership via a new `AsmdefResolver`. `WorkspaceIndex` tracks every indexed `.cs` (`_allCsFiles`) so `FindCompanionFiles` unions same-folder + workspace-wide `.cs` filtered to the consumer's asmdef.
- **Editor HMR.** New `HookContainerRegistry` is seeded async from `UitkxHmrController.Start`, invalidated per-file by the watcher, reset on `Stop`. `EmitCompanionUitkxSources` adds a second pass pulling cross-directory FQNs from the registry, deduped against the inline set, with a 100 ms gate on the first recompile.

`AsmdefResolver` ships verbatim under `Editor/HMR/` and the LSP, mirroring the SG `IsOwnedByCompilation` no-asmdef fallback. `AsmdefResolverParityTests` pins the contract. Closes TECH_DEBT_V2 #18 and #19.

**Tests.** SG **1228/1228** (+6). LSP **63/63** (+1).

VS Code **1.2.7 -> 1.2.8** | VS 2022 **1.2.7 -> 1.2.8**.

---

## [0.5.11] - 2026-05-13

### LSP Go-To-Definition - resolves module and hook symbols across directories

**Cross-directory peer discovery.** Jumping to `Theme.SidebarWidth` from `Sidebar.style.uitkx` used to return nothing when `Theme.uitkx` lived in a different folder. `RoslynHost.FindPeerUitkxFiles` only enumerated same-directory `.uitkx` files, so Roslyn never saw the declaring document and had no symbol to resolve to.

`WorkspaceIndex` now tracks a workspace-wide set of `.uitkx` files that contain top-level `module` or `hook` declarations (matched by a `Multiline` regex `^(?:module\s+\w+\s*\{|hook\s+\w+\s*[<\(])`). The set is updated incrementally on every `IndexUitkxFile` and `Refresh` call under a brief write lock, exposed via `GetModuleAndHookFiles()`, and appended to the per-document peer list. Roslyn then loads these files as workspace documents alongside same-directory peers, and Go-To-Definition resolves the cross-directory symbol naturally.

The three downstream consumers - `EnrichWithPeerHookUsings`, `AddPeerUitkxDocuments`, `AddPeerUitkxDocumentsToSolution` - already filter peers by `HookDeclarations` / `ModuleDeclarations` not being default, so the wider candidate set is cost-free for files that declare neither. Module/hook files are typically single-digit per workspace.

Tooling-only release. No runtime, editor, source-generator, or shared changes; SG suite untouched. Pure extension fix with a library version bump for symmetry.

**Tests.** 62/62 LSP server tests passing.

VS Code **1.2.6 -> 1.2.7** | VS 2022 **1.2.6 -> 1.2.7**.

---

## [0.5.10] - 2026-05-13

### Component HMR rewritten as a static trampoline - no more stale renders on rapid saves

**Trampoline refactor.** Every `component` now compiles to a `static` triplet: an `internal static` delegate field `__hmr_Render` (initialized to `__Render_body`) and a `Render` entry that branches on `HmrState.IsActive`. HMR-side and project-side emit are byte-identical at the call site.

```csharp
component Counter(int initial = 0) {
    var (count, setCount) = useState(initial);
    return (<Button text={count.ToString()} />);
}
```

The new `UitkxHmrComponentTrampolineSwapper` swaps via one O(1) `FieldInfo.SetValue` per type, then notifies only fibers whose `TypedRender.Method.DeclaringType` matches. The legacy per-fiber closure-rollback path (`HmrPreviousRender` field, ~36 lines in `FiberReconciler`, two `IsCompatibleType` source-path fallbacks) is gone. A type-level rollback registry bridges via `HmrState.TryRollbackComponent` so a bad swap reverts cleanly. Roslyn's per-call-site method-group cache keeps `ReferenceEquals(fiber.TypedRender, vnode.TypedFunctionRender)` stable.

User-visible: 30+ Ctrl+S no longer leaks stale renders, navigate-away-then-back shows the new code, and incompatible hook-signature edits reset state in-place (React Fast Refresh) - no domain reload.

**Fix - rude-edit reloads Play-mode-safe.** `RequestScriptReload` in Play mode produced partial reloads that broke `MonoBehaviour` refs. Deferred to the next `EnteredEditMode`.

**Fix - HMR Stop no longer stalls 30-40s.** Dropped `CleanBuildCache` from the change-watcher trigger; Roslyn's `AdditionalText` cache picks up edits incrementally.

**Fix - LSP false "unused local" on hook pairs.** `AnalyzeUnusedLocals` treats `dataFlow.Captured` as reads, so `var (count, setCount) = useState(0);` consumed inside lambdas stops warning.

**Tests.** 1222/1222 SG passing (+2 trampoline parity tests).

VS Code **1.2.5 -> 1.2.6** | VS 2022 **1.2.5 -> 1.2.6**.

---

## [0.5.9] - 2026-05-12

### Module `static readonly` fields now hot-reload - no domain reload, no stale refs

**B28 closed.** Editing a module-scope `Style` or `Color` field initializer (e.g. `PaddingTop = 4` -> `16` in `Sidebar.style.uitkx`) used to report a successful HMR cycle but leave the UI on the cold value until you exited Play mode. Already-mounted components picked it up; newly-mounted ones kept reading the old ref.

Byte-level IL diagnostics confirmed: the Mono JIT inlines `ldsfld <static readonly>` into native code at first call-site emission. `FieldInfo.SetValue` updates the slot, but already-JIT'd methods keep reading the inlined cold ref.

```csharp
module Sidebar {
    public static readonly Style Wrapper = new Style {
        PaddingTop = 4,   // edit + save - visible next render
    };
}
```

Permanent IL fix (IL2CPP and Mono AOT player builds too - Editor and Player IL stay identical). The SG strips `readonly` from every top-level `static readonly` field inside a `module { ... }` body and decorates the rewrite with `[global::ReactiveUITK.UitkxHmrSwap]`. HMR mirrors the rewrite in `HmrHookEmitter.EmitModules`. Generator-managed module statics (`__sty_N` style hoists, `__uitkx_ussKeys`) get the same treatment. The hook-cache `static readonly ConcurrentDictionary` is intentionally left as `initonly` (ref is immutable; only contents are HMR-replaced) and the swapper still finds it via `IsInitOnly`.

New analyzer `UITKX0210` (Warning) flags writes to `[UitkxHmrSwap]` fields from non-cctor code - HMR will overwrite any external write on the next save.

**Known limitation.** Static auto-properties (`public static Style Root { get; } = ...`) lower to a private `static readonly` backing field the SG cannot rewrite. Prefer fields for HMR-able module values. Auto-property promotion is on the roadmap.

**Tests.** 1218/1218 SG passing (20 new: stripper, analyzer, end-to-end).

VS Code **1.2.4 -> 1.2.5** | VS 2022 **1.2.4 -> 1.2.5**.

---

## [0.5.8] - 2026-05-11

### `[OnOpenAsset]` migrated to `EntityId` callbacks on 6.3+ - no obsolete API touched

**Console navigation, warning-free on 6.3 / 6.4.** Unity 6.3 deprecated every `int<->EntityId` conversion on `EntityId` - including the implicit cast operator itself, which warns *"EntityId will not be representable by an int in the future. This casting operator will be removed in a future version."* A naive `(EntityId)instanceId` cast still emits `CS0618`.

A reflection probe of `UnityEngine.CoreModule.dll` on 6000.3 / 6000.4 showed the proper migration: `OnOpenAssetAttribute` accepts both the legacy `(int, int, int)` callback shape and a new `(EntityId, int, int)` shape on 6.3+. Let Unity hand us the `EntityId` directly - no conversion at all.

```csharp
#if UNITY_6000_3_OR_NEWER
    [OnOpenAsset]
    private static bool OnOpenAssetCompat(EntityId entityId, int line, int col)
        => HandleOnOpenAsset(entityId, line, col);

    private static bool HandleOnOpenAsset(EntityId id, int line, int col)
    {
        var path = AssetDatabase.GetAssetPath(id);
        if (string.IsNullOrEmpty(path))
            path = AssetDatabase.GetAssetPath(EditorUtility.EntityIdToObject(id));
        return ResolveAndDispatch(path, line, col);
    }
#else
    // int-typed callbacks retained for 6000.2 where EntityId doesn't exist
#endif
```

The four `[OnOpenAsset]` callbacks in `UitkxConsoleNavigation.cs` split by `#if UNITY_6000_3_OR_NEWER`. Common path-resolution and external-editor-launch logic moved into a shared `ResolveAndDispatch`. Zero obsolete APIs touched on 6.3+.

**Fix - `DoomTextures` sample CS8618.** Six lazy-init fields suffixed `= null!`; all getters route through `EnsureBuilt()`, no behaviour change.

**Docs.** Web API page picked up a note explaining why storm-time interactivity is non-restorable framework-side (per-panel event state is panel-owned).

VS Code **1.2.3 -> 1.2.4** | VS 2022 **1.2.3 -> 1.2.4**.

---

## [0.5.7] - 2026-05-11

### `<Portal>` survives Unity 6.3 panel rebuilds - completes the 0.5.6 defense

**Bug class 2 closed.** 0.5.6 fixed the main `RootRenderer` against Unity 6.3's silent `rootVisualElement` rebuilds, but world-space portals still vanished when their `UIDocument` was clicked in the Hierarchy. Logs showed the world root swapped repeatedly with `childCount=0`: `Hooks.UseUiDocumentRoot` correctly re-fired the consumer with the new root, but the Fiber commit phase had no path to physically move existing portal children from the old target to the new one. New mounts went to the new target; deletions cleared from the old; **stable** children stayed parented to the dead target.

**Fix lives in the commit phase**, mirroring 0.5.6's `RetargetContainer` at portal granularity:

- New `EffectFlags.PortalRetarget` (bit 6), set in `CompleteWork` when a `HostPortal` fiber's `PortalTarget` differs from its alternate's. One `ReferenceEquals` per portal per render - zero cost when stable.
- New `CommitWork` branch runs `CommitPortalRetarget`: bounded DFS descending only through non-host wrappers (`Fragment`, `FunctionComponent`, `ErrorBoundary`, `Suspense`) to the first host descendant on each branch. Reparenting one VisualElement carries its subtree along - no per-VE recursion. Nested portals skipped.
- `parent.Add(child)` removes from the old parent first, so retarget is safe even when Unity has already disposed the old target.

Combined with `UseUiDocumentRoot`, world-space portals now survive the rebuild storm: hook re-fires ? consumer renders `<Portal target={newRoot}>` ? reconciler reparents the subtree, all in one commit.

**Fix - lowercase `useUiDocumentRoot` alias.** 0.5.6 only registered the hook in the signature regex; SG/HMR rewrite tables and IDE Roslyn stubs were missed, so `useUiDocumentRoot(...)` in `.uitkx` produced `CS0103` except via `Hooks.UseUiDocumentRoot(...)`. All four sites synced.

VS Code **1.2.2 ? 1.2.3** | VS 2022 **1.2.2 ? 1.2.3**.

---

## [0.5.6] - 2026-05-10

### Unity 6.3 panel-rebuild defense - UIs no longer disappear on Inspector interaction

**Confirmed Unity 6.3 regression.** A standalone probe (`PanelLifecycleProbe`) showed `UIDocument.rootVisualElement` is silently recreated on every `InspectorWindow` redraw - selection changes, hovers, field focus. 6.2 fires zero events on the same project; 6.3 fires `DetachFromPanelEvent` ? fresh `VisualElement` instance, call stack ending at `UnityEditor.InspectorWindow.RedrawFromNative`. Reported to Unity. Distinct from UUM-47682 (closed "By Design" for UI Builder Live Reload only) - the documented workaround there ("create UI in `OnEnable`") doesn't apply because the rebuild fires repeatedly *after* `OnEnable`.

There is no public event for this rebuild, so polling is the only defense. The fix is additive - both old and new APIs remain valid.

**`RootRenderer.Initialize(UIDocument)` overload (new).** Subscribes to a per-frame poll. On swap, migrates the fiber tree to the new root via `RetargetHost` + `RetargetContainer`: host props re-applied, children re-added, all hook state and subscriptions preserved.

```csharp
rootRenderer.Initialize(uiDocument.rootVisualElement); // legacy, still valid
rootRenderer.Initialize(uiDocument);                   // 6.3-resilient
```

**`UseUiDocumentRoot` hook (new).** Stable `VisualElement` reference tracking the current root, with `ReferenceEquals` short-circuit.

```csharp
var root = Hooks.UseUiDocumentRoot(myUIDocument);
```

**Reparent-resilient adapters.** Video, MultiColumnListView, MultiColumnTreeView, TabView selection trackers route through new `PanelDetachGuard.Wire(ve, teardown)`, which defers teardown one frame via `MainThreadTimer.OneFrameLater` and cancels it on re-attach.

**Cost.** ~10 ns/frame per managed root. Zero hot-path allocations. O(N) retarget runs only on real rebuilds (Editor-only). HMR compatible.

VS Code **1.2.1 ? 1.2.2** | VS 2022 **1.2.1 ? 1.2.2**.

---

## [0.5.5] - 2026-05-09

The React idiom `{cond && <Tag/>}` now splices cleanly in markup expression positions. C# `&&` is bool-only - `bool && VirtualNode` is `CS0019` - so the splicer rewrites the expression at emit time to a ternary `((cond) ? V.Tag(...) : (VirtualNode?)null)`, reusing the already-tested Phase 1 ternary path. The `null` fallback is dropped at render time by `__C(params object[])` which filters nulls. No runtime change.

```jsx
// simple bool
<Box>{flag && <Label text="hi"/>}</Box>

// null check (the repro)
<Box>{icon != null && <Image texture={icon}/>}</Box>

// nested - LHS walker respects precedence
<Box>{a ? b : c && <Tag/>}</Box>     // LHS = c
<Box>{a || b && <Tag/>}</Box>        // LHS = b
```

A new shared `FindLhsStartForLogicalAnd` walker finds where the LHS of `&&` begins: forward pass, per-paren-depth boundary tracking, lexer-aware string/comment skipping, recognises `?`, `:`, `??`, `||`, `,`, `;` as boundaries at the same depth as the `&&`. Mirrored across SG, HMR (via reflection), and the IDE virtual document so the editor no longer flags CS0019.

Degenerate input (`{ && <X/>}`) emits a single `UITKX0026` diagnostic instead of cascading into raw-JSX compile errors. Setup-code and directive-body `&&` JSX (`var node = cond && <Tag/>;`) remain unsupported - tracked in TECH_DEBT_V2 item 15, workaround is the explicit ternary.

**Fix - `using UnityEngine;` is now injected into generated C#.** Six SG and HMR emit sites covered the namespace, partial-class, and function-component-overload positions. The IDE virtual document already had `UnityEngine` in scope, so code touching `Texture2D`, `Color`, `Vector2`, `Mathf`, etc. compiled green in the editor but red at build / HMR. Both pipelines now see the same surface area.

**Tests.** 8 new in `JsxInExpressionTests`, 3 in `UnityEngineImportTests`. **1198/1198 SG** passing. VS Code **1.2.0 ? 1.2.1** | VS 2022 **1.2.0 ? 1.2.1**.

---

## [0.5.4] - 2026-05-08

### Strict attribute validation on user components - fixed silent CS0117 cascade

Anything that isn't a declared parameter on a user component (or the universal `key` / `ref`) is now rejected at the IDE and build layers. Forwarding `style`, `name`, `className`, `onClick`, `extraProps`, etc. through a user component used to silently compile to `Style = x` against the generated `AppButtonProps` and explode at C# build time as `CS0117` with no pointer back to the `.uitkx` file. Now you get a proper `UITKX0109` error in the IDE and at build time with an actionable hint listing the available parameters.

```jsx
// before - silent slip-through, then CS0117
<AppButton text="Save" style={btnStyle}/>

// now - UITKX0109: Unknown attribute 'style' on 'AppButton'.
//                  Available on 'AppButton': text, onClick.
```

**Root cause.** The schema lumped `key`/`ref` together with the 58 `BaseProps` members under one `universalAttributes` list, so every tag appeared to accept the full intrinsic surface. The list is now split into two:

- `structuralAttributes` - `key`, `ref` - apply everywhere (`key` lives on `VirtualNode`; `ref` is routed `forwardRef`-style to the unique `Hooks.MutableRef<T>` parameter).
- `intrinsicElementAttributes` - the 58 `BaseProps` members - only valid on built-in `V.*` tags backing a `VisualElement`.

Built-ins are unchanged: `<Button style={...} extraProps={...}/>` still works.

**Migration.** Forwarding `style` through a user component? Declare it explicitly on the component:

```jsx
component AppButton(string text = "", IStyle? style = null) {
    return (<Button text={text} style={style}/>);
}
```

**Parity.** Editor (LSP) and build (source generator) now share the same attribute map - no more red-in-editor / yellow-at-build asymmetry. The bad attribute is skipped in the emitted C# so `UITKX0109` doesn't cascade into `CS0117`/`CS0246`.

**Tests.** 9 new regression tests covering both legal `key`/`ref` propagation and rejection of all 58 `BaseProps` members on user components. **1187/1187 SG** passing.

VS Code **1.1.15 ? 1.2.0** | VS 2022 **1.1.15 ? 1.2.0** ride the same release.

---

## [0.5.3] - 2026-05-08

### Hard cut of `@(expr)` markup syntax - `{expr}` is the only form

The only embed form for arbitrary C# expressions inside markup is now `{expr}` - same as JSX / Babel / React. The `@` prefix survives only as the directive marker (`@if`, `@else`, `@for`, `@foreach`, `@while`, `@switch`, `@case`, `@default`, `@using`, `@namespace`, `@component`, `@props`, `@key`, `@inject`, `@uss`).

```jsx
// before
<Box>@(items.Count)</Box>
<Tag attr=@(value) />

// now
<Box>{items.Count}</Box>
<Tag attr={value} />
```

Files containing legacy `@(expr)` raise hard parse error `UITKX0306`. Migration is mechanical: every `@(` ? `{`, matching `)` ? `}`. Unification touches the parser, formatter, analyzer, IntelliSense, VDG, HMR, source generator, TextMate grammar, all 12 shipped samples, and the tests.

**Fix - pool-rent decls no longer hide inside line comments.** The naive backward-scan picking the splice point for `var __p_N = __Rent<TProps>()` stopped at the first `;` or `}` - including `}` inside `// see {catBadge}` comments. The compiler ate the decls as comment text ? downstream `CS0103`. All four sites (SG x2, HMR x2) replaced with a shared `FindLastTopLevelStatementBoundary` lexer-aware scanner that skips comments, strings (regular / verbatim / interpolated with brace-depth in holes), and char literals. Pre-Phase 2 the comment had `@(catBadge)` so the bug was masked - unification exposed it.

**Fix - TextMate grammar was completely dead.** The JSON file picked up a UTF-8 BOM during the edit pass. `vscode-textmate` rejects BOM ? grammar fails to load ? every scoped token fell back to plain text (keywords, properties, operators rendering white in module bodies). Both grammar copies are now BOM-free; 37 rules load.

**Tests.** **1178/1178 SG** passing. HMR ? SG parity green.

VS Code **1.1.14 ? 1.1.15** | VS 2022 **1.1.14 ? 1.1.15** ride the same release.

---

## [0.5.2] - 2026-05-08

### JSX literals in any C# expression position - Phase 1 splicer

JSX literals now splice cleanly wherever a C# expression is allowed, matching React / Babel semantics. Patterns that previously emitted raw JSX and tripped the C# compiler now compile to `V.Tag(...)` calls:

```jsx
// ternary branches
<Box>{cond ? <A/> : <B/>}</Box>

// null-coalescing
<Box>{fallback ?? <Default/>}</Box>

// JSX in attribute expressions
<Box icon={active ? <Check/> : <X/>}/>

// JSX inside lambda bodies
attr={items.Select(x => <Item key={x.Id}/>)}
```

The scanner powering this (`FindBareJsxRanges` + `FindJsxBlockRanges`) was already proven on component preambles and directive bodies - it is now wired into the two remaining emit sites (`EmitExpressionNode` and the `CSharpExpressionValue` branch of attribute emission) and mirrored in HMR and the IDE virtual-document generator.

**No runtime change** | same `V.Tag(...)` factory + `__Rent<TProps>()` pooled shape. The whole splice is emit-time only and short-circuits when an expression contains no JSX.

**Fix - `Texture2D ? iconName = null` no longer drops the `?` on save.** Whitespace before `?` in a nullable component param made the tokenizer leave it unconsumed; format-on-save then re-emitted the parameter as non-nullable. Same pathology as the recent `@else` blank-line bug - formatter re-emit is lossy when the parser drops tokens. The tokenizer now peeks past whitespace, canonicalises the captured type, and emits a clean `Texture2D? name` regardless of input spacing.

**HMR + IDE parity.** HMR mirrors the splice end-to-end. The virtual-document generator strips embedded JSX to typed `(VirtualNode)null!` stubs so the IDE no longer shows phantom Roslyn errors on files that compile cleanly under SG.

**Tests.** 14 new (10 SG `JsxInExpressionTests`, 1 HMR parity tripwire, 3 VDG). **1178/1178** passing.

VS Code **1.1.13 ? 1.1.14** | VS 2022 **1.1.13 ? 1.1.14** ride the same release.

---

## [0.5.1] - 2026-05-07

### Generic `static` methods inside `module { - }` - fixed

Consumer (JustStayOn) upgraded 0.4.15 ? 0.5.0 and hit `CS0119: 'TProps' is a type, which is not valid in the given context` on every generic method inside `module Dialogs { Register<TProps,TResult>(...), Open<TProps,TResult>(...), - }`, plus `CS8625: Cannot convert null literal to non-nullable reference type` under `<Nullable>enable</Nullable>`. Both bugs sat dormant in `ModuleBodyRewriter` since 0.4.19 (the release that introduced `module { }` HMR). Non-generic methods worked fine - generic ones never ran through a real compiler in CI.

**Bug 1 - CS0119.** `AppendTypeArgs` emitted bare type-parameter names into the synthesized `MethodInfo.MakeGenericMethod(...)` call ? `MakeGenericMethod(TProps, TResult)`. `MakeGenericMethod` takes `params Type[]`. Fix: wrap each name in `typeof(...)` ? `MakeGenericMethod(typeof(TProps), typeof(TResult))`.

**Bug 2 - CS8625.** Generic-branch `MethodInfo` HMR field emitted as `= null;`. The field MUST start null - the trampoline checks `!= null` to fall through to the body method until `UitkxHmrModuleMethodSwapper` rebinds it via reflection. Fix: emit `= null!;`. Runtime value identical, nullable warning gone, swapper untouched.

**Why this only hit now.** `module { }` HMR landed in 0.4.19 with substring-only test coverage. Both broken outputs still contained the markers the existing test asserted on (`MakeGenericMethod`, `__hmr_<name>_h-`). Real consumer code with generic module methods was the first end-to-end compile of that code path.

**Test.** New `Sg_ModuleGenericMethod_GeneratedCodeCompiles_NoCS0119_NoCS8625` actually compiles the SG output through Roslyn (`UNITY_EDITOR` defined, nullable enabled) and asserts neither CS0119 nor CS8625 is raised. Bug class structurally impossible to recur silently. **1162/1162 SG** passing.

Runtime-only release - IDE extensions unchanged.

---

## [0.5.0] - 2026-05-06

### Media - `<Video>`, `<Audio>`, `useSfx()` + a fiber-unmount fix

Two declarative elements and one hook bring video/audio into UITKX, all rendered through one shared host.

**`<Video>`.** Pooled `VideoPlayer` + `RenderTexture` from a new `MediaHost`, fed into a UI Toolkit `Image` via `image = renderTexture` (`style.backgroundImage` caches stale samples). `frameReady` drives `MarkDirtyRepaint()`; an editor-only `QueuePlayerLoopUpdate()` pump advances the player when Unity isn't ticking. Props: `Clip`/`Loop`/`Autoplay`/`Muted`/`ScaleMode`/`Volume` + `VideoController` ref.

**`<Audio>` (Func-Component).** No visible content; rents an `AudioSource` via `UseEffect`, returns it on unmount. Same shape minus visuals + `Pitch`/`SpatialBlend`/`AudioMixerGroup`. `AudioController` ref.

**`useSfx()`.** Stable, allocation-free `Action<AudioClip, float>` backed by `MediaHost.Instance.SfxSource.PlayOneShot`. Same delegate ref across renders.

**`MediaHost`.** `HideAndDontSave` GameObject; reference-counted player/source pools; RT pool keyed by `(w, h, depth)`. Survives domain reloads.

**Fix - `FiberRenderer.Clear()` runs effect cleanups.** The audio leak surfaced it: closing the demo window left music playing forever. The stub cleared the container and nulled the root without invoking `CommitDeletion` - every `UseEffect` cleanup (timers, signal subs, RTs, audio) leaked on every editor mount/unmount. New `FiberReconciler.UnmountRoot()` runs depth-first `CommitDeletion`; `EditorRootRendererUtility.Unmount()` drains via `EditorRenderScheduler.PumpNow()`.

**IDE - `useSfx()` recognized in the editor.** Added to the LSP virtual-document hook-stub list (it was in SG/HMR alias regexes but missing here, so the editor reported `CS0103`).

**Demo.** `MediaPlaygroundDemoPage.uitkx` covers everything. Editor: `ReactiveUITK > Demos > Media Playground`.

VS Code **1.1.11 ? 1.1.12** | VS 2022 **1.1.11 ? 1.1.12** ride along.

---

## [0.4.19] - 2026-05-04

### Full HMR support for `module { - }` declarations

`module { - }` is now hot-reloadable end-to-end. Edit a `Style` field, a `static readonly Color`, or a 200-line `GameLogic.Tick(ref GameState st, -)` and the change takes effect on the next call - no Play-mode exit, no domain reload.

**Per-method hot-swap.** New SG pass (`ModuleBodyRewriter`) rewrites every top-level `static` method into a public trampoline bouncing through an `__hmr_<name>_h<sig>` delegate field to a private body method. `UitkxHmrModuleMethodSwapper` rebinds each field via `Delegate.CreateDelegate` after every HMR compile. Custom delegate types preserve `ref`/`out`/`in`/`params`; FNV-1a signature hash disambiguates overloads; generics use a `MethodInfo` + `ConcurrentDictionary` cache. `#if UNITY_EDITOR`-gated - zero player-build overhead. Trampoline visibility tracks the original, so `private static` methods using `private` nested types stay valid.

**Documented contract** per member kind: const ? re-baked at compile; `static readonly` ? re-initialised each cycle; mutable static ? preserved; static method ? hot-swapped; instance / nested / new field-or-method ? verbatim or rude-edit.

**Rude-edit detection.** Adding a new `static readonly` mid-session can't grow the project type's metadata - the CLR seals it at load. Instead of a silent `MissingFieldException` later, `UitkxHmrModuleStaticSwapper` logs a once-per-session warning. New `UitkxHmrController.AutoReloadOnRudeEdit` EditorPref (default `false`) automates the reload.

**12 HMR ? SG parity bugs fixed** in `HmrCSharpEmitter`: sibling-Props, JSX-as-attribute-value, duplicate-`key={}`, `ref={x}` on Func components, `Asset<T>(-)` in module bodies, deterministic Roslyn overload picking, FQN type comparison. New `UITKX0150` surfaces module-body parse failures with verbatim fallback. **1142/1142 SG** passing.

IDE extensions unchanged - runtime-only release.

---

## [0.4.18] - 2026-05-03

### HMR `CS0426` on function components with sibling top-level Props - fixed

After 0.4.17 unblocked HMR compilation of module/style/hook files, a follow-up surfaced: `[HMR] Compilation failed for AppRoot... CS0426: The type name 'RouterFuncProps' does not exist in the type 'RouterFunc'`. A long-standing convention divergence between SG and HMR that only became reachable once HMR could compile these files end-to-end.

**The bug.** Func-component Props classes ship in three legitimate shapes:

1. **Sibling top-level** | `RouterFunc` + `RouterFuncProps` both at namespace scope, neither nested. Used by `ReactiveUITK.Router`.
2. **Nested same-name** | `CompFunc.CompFuncProps` (the SG's default emission).
3. **Nested differently-named** | `ValuesBarFunc.Props` (legacy hand-written).

SG's `PropsResolver.TryGetFuncComponentPropsTypeName` walked all three. HMR's `FindPropsType` only walked nested types and shipped `{Type}.{Type}Props` unconditionally - shape (1) compiled fine through SG but failed CS0426 through HMR.

**The fix.** `FindPropsType` now mirrors `PropsResolver` verbatim - sibling top-level first, then nested same-name, then any nested `IProps`, then a convention fallback. One canonical answer per component.

**Tests.** Two layers on every push/PR/publish:
- **SG-side parity test** drives the generator with the real `RouterFunc` / `RouterFuncProps` shape and asserts `V.Func<global::Ns.RouterFuncProps>` is emitted.
- **HMR algorithm contract tests** | five Roslyn-in-memory cases (sibling / nested-named / nested-legacy / sibling-wins-priority / negative fallback) mirror `FindPropsType` verbatim, since the Editor assembly's `UnityEditor` deps block direct loading by the standalone test runner.

**1070/1070 SG** passing.

VS Code **1.1.10 ? 1.1.11** | VS 2022 **1.1.10 ? 1.1.11** ride the same release.

---

## [0.4.17] - 2026-05-03

### Two converging bugs around `.style.uitkx` / `.hooks.uitkx` - both fixed

Consumer hit `[UITKX] Asset not found in registry: "../Resources/background-01.png"`. Two independent latent bugs converged in module/style/hook files.

**Bug 1 - HMR `ArgumentException` on every save.** `UitkxHmrCompiler.InvokeWithDefaults` had two `params object[]` overloads; C# resolution preferred `string ? object target`, shifting all args left and landing a `List<ParseDiagnostic>` in `DirectiveParser.Parse`'s `filePath` slot. Collapsed to a single canonical `(MethodInfo, object target, params object[])` with mandatory `target` - bug class structurally impossible to recur. All 11 call sites updated.

**Bug 2 - `Asset<T>(...)` not rewritten in `module` / `hook` bodies.** `UitkxAssetRegistry` is keyed by resolved paths; the emitter rewrite covered setup code + JSX + `@if`/`@foreach`/`@switch` but skipped module/hook bodies, so `Asset<Texture2D>("../Resources/bg.png")` shipped as the raw literal while registry sync wrote the resolved key - runtime miss. `ResolveAssetPaths` promoted to `internal static` shared by `CSharpEmitter`/`HookEmitter`/`ModuleEmitter`; HMR's `HmrCSharpEmitter.ResolveAssetPaths` visibility lifted so `HmrHookEmitter` routes module+hook bodies through it. Source-gen and HMR now emit literal-identical asset strings.

**Tests.** 4 new `EmitterTests` regressions (module `./`, module `../`, module absolute, hook `./`) run on every push/PR/publish. **1064/1064 SG** passing.

VS Code **1.1.9 ? 1.1.10** | VS 2022 **1.1.9 ? 1.1.10** ride the same release.

---

## [0.4.16] - 2026-05-03

### HMR - fix `TargetParameterCountException` + production-grade hardening

A reflection signature drift between the editor-only HMR compiler and `ReactiveUITK.Language.dll` (`UitkxParser.Parse` gained an optional `lineOffset` in 0.4.7) was firing `TargetParameterCountException` on every `.uitkx` save during play mode, swallowed into a silent warning + infinite retry storm.

**Layer 1 - fix.** Both `_uitkxParse.Invoke` sites now pass the trailing `lineOffset = 0`. Hot reload of components, hooks, and modules works again.

**Layer 2 - `InvokeWithDefaults` helper.** All six reflective calls into the language library now route through a helper that pads short argument arrays with each parameter's compile-time `DefaultValue`. One-time `Debug.LogWarning` per `MethodInfo` surfaces silent API drift the next time it happens, instead of failing.

**Layer 3 - infrastructure-error classifier + self-disable.** `HmrCompileResult.IsInfrastructureError` is now set when the inner exception is `TargetParameterCountException | MissingMethodException | MissingFieldException | TypeLoadException | ReflectionTypeLoadException | BadImageFormatException`. The controller emits one `Debug.LogError` with actionable text and calls `Stop()` (the only safe disable path - unhooks events, stops the watcher, unlocks the assembly-reload suppressor, restores `runInBackground`, clears retry queues). User-authored compile errors (CS0103, CS1xxx, syntax) keep the existing warn + retry cascade.

VS Code **1.1.8 ? 1.1.9** | VS2022 **1.1.8 ? 1.1.9** | IDE virtual-document generator now injects `using static ReactiveUITK.AssetHelpers;` so `Asset<T>("...")` and `Ast<T>("...")` no longer report CS0103 in component setup blocks, hook bodies, and module/style initializers (e.g. `AppRoot.style.uitkx`).

**1060/1060 SG** passing. Source generator, runtime, build, and IDE extension surfaces are otherwise untouched.

---

## [0.4.14] - 2026-05-03

### Router - React-Router-v6 parity (additive, no breaking changes)

New primitives: **`<Outlet/>`**, **`<Routes>`** (ranked first-match-wins), **`<NavLink>`** (active styling), **`<Navigate to>`** (declarative redirect).

`<Route>` gains `index`, `caseSensitive`, and layout-route composition (`element` + child `<Route>`s feed `<Outlet/>`). `<Router>` gains `basename`; nested `<Router>` is now a hard error.

New `RouterHooks`: `UseOutletContext<T>`, `UseMatches`, `UseResolvedPath`, `UseSearchParams`, `UsePrompt`, `UseNavigate(NavigateOptions)`. Old signatures preserved.

Internals: shared `RouteRanker` (port of RR's `rankRouteBranches`/`computeScore`); single-source-of-truth tag-alias map shared by source-gen + HMR.

**1063/1063 SG** passing (6 new emission tests). VS Code **1.1.7 ? 1.1.8** | VS2022 **1.1.7 ? 1.1.8** ship schema entries for the new tags/attributes. Docs site router page rewritten to cover the new surface.

---

## [0.4.13] - 2026-05-02

### Style coverage - 13 missing IStyle properties wired end-to-end
Closes the long-standing UITKX vs `UnityEngine.UIElements.IStyle` wiring gap. Every IStyle property is now reachable via the typed `Style` API, the tuple `(StyleKeys.X, value)` form, and the IDE autocomplete schema.

### New typed properties (Unity 6.2 floor)
- **9-slice** | `UnitySliceLeft/Right/Top/Bottom`, `UnitySliceScale`, `UnitySliceType` (`Sliced`/`Tiled`).
- **Clipping** | `UnityOverflowClipBox` (`PaddingBox`/`ContentBox`).
- **Text** | `WordSpacing`, `UnityParagraphSpacing`, `TextShadow`, `UnityTextGenerator` (`Standard`/`Advanced`), `UnityEditorTextRenderingMode` (`SDF`/`Bitmap`, editor-only).
- **Fonts** | `UnityFontDefinition` (legacy `Font` or TextCore `FontAsset`).

New `CssHelpers`: `SliceFill`/`SliceTile`, `ClipPaddingBox`/`ClipContentBox`, `TextGenStandard`/`TextGenAdvanced`, `EditorTextSDF`/`EditorTextBitmap`, `Shadow(dx,dy,blur,color)`, `FontDef(font)`.

### Fix - 19 missing `styleResetters` (silent leak)
Setter/resetter audit found 19 `IStyle` properties with a setter but no resetter - removing them from a `style={}` block silently leaked the previous value. Now all reset to `StyleKeyword.Null` (`alignContent/Items/Self`, `backgroundPositionX/Y`, `backgroundRepeat/Size`, `flexDirection/Wrap`, `fontFamily/Size`, `justifyContent`, `position`, `rotate`, `scale`, `textAlign`, `transformOrigin`, `translate`, `unityFontStyle`).

### Tests
New `IStyleCoverageTests` (7 facts) regex-asserts every IStyle property is wired through every layer. Future Unity versions can no longer add an unwired property without a red CI. **1051/1051 SG - 61/61 LSP** passing.

### Extensions
VS Code **1.1.6 ? 1.1.7** | VS2022 **1.1.6 ? 1.1.7** | autocomplete for the 4 new enum-valued IStyle properties via embedded `uitkx-schema.json`.

---

## [0.4.12] - 2026-05-01

### Doom demo - Phase 9 sector-engine release
No runtime / source-generator / IDE changes this cycle. The `Samples/Components/DoomGame/` demo went from a flat raycaster to a real sector-portal engine - stacked floors, key-chain progression, minimap, and a full status bar.

### Renderer
- **Sector / portal raycaster (Phase 1-3)** | tile map compiles to `MapData` (sectors + linedefs); rendering walks portals via per-ray cliprange (`winTop`/`winBot` screen-Y window). Variable floor/ceiling heights, upper/lower wall segs, and sky cells render correctly.
- **ExtraFloor stacked slabs (Phase 9)** | sectors carry any number of slabs; column rasterizer emits front + back TOP/BOTTOM/SIDE planes per slab and tightens the cliprange so taller slabs further along the ray stay visible. Fixes the staircase upper-treads-vanish bug; powers Level 6-s 7-step interior staircase.
- **Z-aware collision (Phase 7)** | `BlocksMovementZ(footZ, headZ, STEP_HEIGHT)` replaces binary `BlocksMovement` for slab-aware step-up, jump, and crouch.

### Gameplay
- **6 hand-built levels** | Hangar, Toxin Refinery, Containment Area, Outpost, Phobos Anomaly, and a boss-only finale.
- **Level 1 progression rebuild** | hub gates side wings behind colored doors: yellow key in hub ? east wing (red key) ? west wing (blue key + shotgun) ? north boss room (Baron + Cacodemon). Walls flank every door so they can-t be sidestepped.
- **Boss-gated exits** | new `LevelStart.BossExitGated` + `GameLogic.AnyBossAlive` blocks level-end until every Baron / Cacodemon is dead, with a HUD message on attempt.
- **Walkable exit pads** | new `MapBuilder.ExitPad(x, y)` creates an Exit-kind cell with no wall texture and a deep-blue floor (`F_BLUE`); back wall painted with new `W_BRICK_BLUE` so the end-zone reads visually.
- **Boss balance** | Baron HP 800 ? 200, Cacodemon 400 ? 120; the Level 1 boss now drops in a few shotgun blasts.

### UI
- **Status bar rewrite** | 8-panel `FlexGrow`-ratio layout (AMMO / HEALTH / ARMS / FACE / ARMOR / KEYS / BREAKDOWN / INFO) filling the full 800-90 region; consistent title spacing, `WhiteSpace.NoWrap`, ARMS as a 3-column 7-weapon button grid.
- **Live minimap** | top-right overlay, auto-scales to fit any map into 160px. Walls, color-keyed doors, exit pad, player (yellow + heading), and every live mobj (red enemies, cyan pickups, key-color keys).

---

## [0.4.11] - 2026-04-28

### Performance
- **JSX children fast-path (OPT-V2-1)** | source generator emits children directly into `params VirtualNode[]` instead of allocating a transient `__C(...)` wrapper when the children list is statically simple. One allocation per element gone.
- **Static-style hoisting (OPT-V2-2)** | `style={new Style{...}}` literals with all-constant initializers are hoisted to class-level `static readonly Style` fields. The reconciler's `SameInstance` check makes the diff walk a no-op when the same instance is reused across renders. Falls back to the pool-rent path for any non-literal value.

### Extensions
VS Code **1.1.5 ? 1.1.6** | VS2022 **1.1.5 ? 1.1.6**
- Source-generator parity - both LSPs ship the new hoisting + children fast-path emit.
- **Cross-document `Ref<T>` unification** | `useRef<T>()` returns canonical `global::ReactiveUITK.Core.Ref<T>` in virtual docs, killing false `CS1503` when passing refs to peer hooks.
- **Polyfill stubs load correctly** in workspaces where Unity hasn't compiled yet.
- **Formatter idempotency for `&&` / `||` chains** inside nested blocks - no more indent drift on save.

---

## [0.4.10] - 2026-04-27

### Performance
- **Major reconciler & props pipeline rewrite** | UITKX went from ~1.7- overhead vs. native UIToolkit (28 FPS at the 3000-box stress benchmark) to ~78% of native (36-38 FPS).
- **Typed Props + Style pipelines** | eliminated ~27,000 dictionary/boxing allocations/frame; flat backing-field structs replace `Dictionary<string, object>`.
- **Object pooling + IIFE closure elimination** | removed ~6,000 `Style`/`BaseProps` allocations and ~3,000 loop-body closures per frame.

### Added
- **Doom-style game demo** (`Samples/Components/DoomGame/`). Window: `ReactiveUITK/Demos/Doom Game`.
- **Typed Props for editor field types** | `BoundsField`, `ColorField`, `DropdownField`, `EnumField`, `MinMaxSlider`, `ObjectField`, `Slider(Int)`, `Tab(View)`, `Toggle(ButtonGroup)`, `Vector2/3/4Field`, and more.

### Changed
- **SG diagnostic IDs unified with live analyzer** | seven codes renumbered so the same issue surfaces with the same ID in both Unity Console and IDE Problems pane (`UITKX0006/0002/0009/0010/0017/0022/0023 ? 0103/0109/0106/0104/0108/0120/0121`).
- **Initial `CreateRoot` render is now synchronous** | no empty-frame flicker between `Clear()` and the first commit.

### Fixed
- **Cross-wired Style / BaseProps "disco" bug** | pooled instance could be returned twice in one flush window and re-rented to two fibers. Fixed via idempotent `_isPendingReturn` guard.
- **`<ErrorBoundary>` stuck on fallback after `resetKey` change**, **`<Portal target={x}>` ignored target changes** | bailout clones now refresh the relevant fields from the new VNode.
- **VNode pooling reverted** | VNode refs can live inside opaque `IProps` payloads (e.g. `Route.Element`); pool returns produced cross-wired trees.

---

## [0.3.2] - 2026-04-07

### Breaking
- **Comment syntax normalized** | `{/* */}` JSX comments replaced with standard `//` (line) and `/* */` (block) in markup. Same syntax everywhere - setup code and JSX.

### Added
- **UITKX0025 for var assignments** | `var x = (<A/><B/>)` now flagged as single-root violation in IDE
- **Block comments in markup** | `/* */` supported in JSX markup for multi-line comments

### Fixed
- **`@(expr)` type enforcement** | inline `@(expr)` now type-checked as `VirtualNode` in IDE diagnostics. Non-VirtualNode expressions (e.g. `VirtualNode[]`) show errors early.
- **Formatter block diff** | single block TextEdit instead of per-line diffs, eliminates corruption on blank-line variations
- **Formatter idempotency** | bare-return formatting matches canonical form on first pass
- **Formatter preserves empty containers** | `<Box></Box>` no longer collapsed to `<Box />`
- **HMR dangling comma** | fixed pre-existing bug in `EmitChildArgs` when comment nodes appear between children

### IDE
- **VS Code** | removed custom `toggleBlockComment` command. `Ctrl+/` ? `//`, `Shift+Alt+A` ? `/* */`
- **VS2022** | simplified comment handler, always uses `//` line comments

### Extensions
VS Code **1.0.306** | VS2022 **1.0.82**

---

## Intro post (Discord server description, Unity leg) - 2026-08-04

Building UI in Unity shouldn't mean hand-wiring UXML, USS and C# event plumbing for every screen. **Reactive UI Toolkit - Unity** brings the React component model to Unity UI Toolkit, in plain C# - function components, hooks, a fiber reconciler, and a JSX-like markup language called **UITKX**. Write your UI in `.uitkx` files, hit save, and your running game updates in place.

**What it does:**
- Function components with hooks - useState, useEffect, useMemo, useContext, useSignal, useTweenFloat, and more
- `.uitkx` markup compiles to plain C# by a Roslyn source generator at build time - zero runtime overhead, no custom packages pipeline
- Hot reload - save while the game runs in the editor and the live UI hot-swaps in ~50-200 ms with hook state preserved, no domain reload
- ~60 host elements mapping to UI Toolkit controls, typed compile-checked styling, a router, signals, portals, suspense, error boundaries
- A second **uGUI backend** - the same components, hooks and router render into classic Canvas UI
- Tooling on a shared language server: **VS Code**, **Visual Studio 2022** and **Rider** extensions - highlighting, live diagnostics, completion, go-to-definition, rename, formatting

**A quick taste:**
```jsx
export VirtualNode Counter() {
  var (count, setCount) = useState(0);
  return (
    <VisualElement>
      <Label text={$"Count: {count}"} />
      <Button text="+1" onClick={_ => setCount(count + 1)} />
    </VisualElement>
  );
}
```

:book: **Repo & releases**
https://github.com/reactive-ui-toolkit/ruitk-unity
:book: **Documentation:**
https://reactiveuitoolkit.info/unity/

A Doom-style demo (and 40+ other samples) ships in the package - import and play.

Happy to answer questions about the approach or the architecture - more to come on the road to 1.0!