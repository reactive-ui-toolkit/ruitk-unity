# uGUI Backend Support Plan ("Option B" — full second render target)

Status: **EXECUTED — archived 2026-08-04.** The backend this proposal designed shipped starting
0.11.0 (`Ugui/` — adapters incl. compound Dropdown/InputField, islands both directions, prefab
bridge, prop groups, layout groups) and is maintained through the 6.5 wave. Deferred items live in
`Plans~/REMAINING_WORK.md`.

Original status: RESEARCH / PROPOSAL — not scheduled. Written 2026-07-20 after owner direction.
Owner's framing (the design north star): **do not make uGUI something it is not.** No CSS,
no flexbox emulation, no world-space chasing. uGUI users get the declarative data flow —
components, hooks, signals, the reconciler — while keeping every habit they already have:
RectTransform anchors/pivots for positioning, LayoutGroups for stacking, sprites/materials
for styling, prefabs for reuse, the Inspector vocabulary for everything.

This inverts the approach taken by prior art (ReactUnity imposes CSS/Yoga onto
RectTransforms). Here the native uGUI model IS the props surface. That choice removes the
single largest engineering item (a custom layout engine) and is what makes this plan
tractable.

---

## 1. Goals and non-goals

### Goals
- A uGUI render backend driven by the SAME fiber reconciler, hooks, signals, context,
  suspense, and Fast-Refresh machinery as the UI Toolkit backend.
- Element vocabulary and prop names that mirror the Unity Inspector / uGUI API one-to-one
  (`anchorMin`, `pivot`, `sizeDelta`, `spacing`, `childAlignment`, `preserveAspect`,
  `raycastTarget`, ...). A uGUI developer should be able to write their first component
  from memory.
- First-class prefab integration (the core uGUI reuse habit).
- `.uitkx` support for uGUI-targeted files with full editor intelligence.
- HMR for uGUI trees at parity with the UI Toolkit backend.

### Non-goals (explicit)
- NO `Style` / USS / typed-CSS mapping onto uGUI. The typed `Style` system stays a UI
  Toolkit concept. uGUI styling = sprites, colors, materials, vertex effects — as props.
- NO custom layout engine (no Yoga-on-RectTransform). uGUI's own layout (anchors +
  LayoutGroup/ContentSizeFitter/LayoutElement) does all layout.
- NO world-space positioning features beyond what a `Canvas` element naturally exposes
  (UI Toolkit has native world-space since 6.2; that is not what this backend is for).
- NO mixed-backend trees in v1: one mount = one backend. (Cross-backend embedding is the
  separate `<UguiHost>` island item, out of scope here.)
- NO IMGUI, no legacy `UnityEngine.UI.Text` (TMP only — TMP is part of com.unity.ugui 2.0
  in Unity 6; no extra package dependency).

---

## 2. uGUI domain inventory (what the backend must model)

This section is the reference map of the surface area. Everything here was inventoried
against com.unity.ugui 2.x as shipped with Unity 6000.2+.

### 2.1 RectTransform — the positioning model
The prop surface every element shares:
- `anchorMin`/`anchorMax` (Vector2, 0..1 in parent space). Point anchors (min==max) mean
  `sizeDelta` IS the absolute size; stretch anchors (min!=max on an axis) mean `sizeDelta`
  is the OFFSET from the anchor rect (usually negative padding), and `offsetMin`/`offsetMax`
  become the natural way to express margins.
- `anchoredPosition` (position of the pivot relative to the anchor reference point).
- `pivot` (Vector2 0..1), affects rotation/scale center and anchoredPosition semantics.
- `sizeDelta`, `offsetMin`, `offsetMax` — redundant encodings; the adapter accepts EITHER
  (`sizeDelta`+`anchoredPosition`) or (`offsetMin`+`offsetMax`) and diagnoses conflicts.
- `localRotation`, `localScale`, `localPosition.z` (depth for nested-canvas tricks).
- **Anchor presets sugar** — the habit-preservation centerpiece. The Inspector's preset
  widget becomes a string prop: `anchors="top-left" | "top-stretch" | "stretch" |
  "middle-center" | "bottom-right" | ...` expanding to the exact min/max/pivot triple the
  preset widget sets. A uGUI dev thinks in presets; give them presets.

### 2.2 Driven rects — the one place declarative meets uGUI reality
LayoutGroups and ContentSizeFitter DRIVE child/self RectTransform properties via
`DrivenRectTransformTracker` (the Inspector greys them out). Writing a driven property is
ignored/warned at runtime. The backend must model this:
- The differ consults the PARENT element's type+props: under a `<VerticalLayoutGroup
  childControlWidth childControlHeight>`, a child's `sizeDelta`/`anchoredPosition` writes
  are skipped and a **build-time diagnostic** fires (new Unity-local code, see §7):
  "`sizeDelta` is driven by the parent VerticalLayoutGroup — value ignored; use
  `layoutElement.preferredHeight` instead". This turns uGUI's most notorious silent
  gotcha into a compile-time message — better DX than hand-written uGUI.
- Same rule for `ContentSizeFitter` (drives self size) and `AspectRatioFitter`.

### 2.3 Canvas stack
- `<Canvas>` element: `renderMode` (ScreenSpaceOverlay default / ScreenSpaceCamera /
  WorldSpace passthrough), `sortingOrder`, `pixelPerfect`, `overrideSorting` (nested),
  plus `CanvasScaler` props (`uiScaleMode`, `referenceResolution`, `matchWidthOrHeight`,
  `scaleFactor`) and `GraphicRaycaster` props (`blockingObjects`, `ignoreReversedGraphics`)
  folded in as prop groups — one element, three components, exactly how a designer thinks
  of "a canvas".
- Nested `<Canvas>` = rebuild isolation (the standard uGUI perf tool) — supported as a
  plain child element with `overrideSorting`.
- A mount does NOT require owning the Canvas: `UguiRootRenderer` (see §4) mounts onto any
  existing `RectTransform`, so trees can live inside user-managed canvases/prefabs.

### 2.4 Graphics
- `<Image>`: `sprite`, `type` (Simple/Sliced/Tiled/Filled), `fillMethod`, `fillAmount`,
  `fillOrigin`, `fillClockwise`, `preserveAspect`, `useSpriteMesh`, `pixelsPerUnitMultiplier`,
  `color`, `material`, `raycastTarget`, `raycastPadding`, `maskable`.
- `<RawImage>`: `texture`, `uvRect`, `color`, `material`.
- `<Text>` (TMP_TextUGUI): `text`, `font` (TMP_FontAsset), `fontSize`, `autoSize`
  (+min/max), `fontStyle`, `color`, `alignment` (TextAlignmentOptions), `wrapping`,
  `overflow`, `richText`, `characterSpacing`/`wordSpacing`/`lineSpacing`/`paragraphSpacing`,
  `margin`, `raycastTarget`. `V.Text("...")` runs lower to a TMP-backed element.
- Vertex effects as prop groups on any Graphic element: `shadow={...}`, `outline={...}`
  (Shadow/Outline components, added/removed by the differ).
- `material` passthrough is the styling escape hatch uGUI people rely on (custom UI
  shaders, dissolves, etc.) — first-class, never wrapped.

### 2.5 Interaction set (Selectable family)
Common Selectable prop surface on all of these: `interactable`, `transition`
(ColorTint/SpriteSwap/Animation) + the per-mode blocks (`colors={ColorBlock}`,
`spriteState`, `animationTriggers`), and **`navigation`** (None/Horizontal/Vertical/
Automatic/Explicit + explicit up/down/left/right refs) — gamepad/keyboard nav is a uGUI
habit the backend must not lose; explicit nav targets accept element refs.
- `<Button>`: `onClick`.
- `<Toggle>`: `isOn`, `onValueChanged`, `graphic` (checkmark ref), `group` (ToggleGroup).
  `<ToggleGroup>` container element: `allowSwitchOff`.
- `<Slider>`: `minValue`, `maxValue`, `wholeNumbers`, `value`, `direction`,
  `onValueChanged` + the fillRect/handleRect wiring built by the element's internal
  structure (see §3.4 compound elements).
- `<Scrollbar>`: `value`, `size`, `numberOfSteps`, `direction`, `onValueChanged`.
- `<ScrollRect>` (compound): `horizontal`, `vertical`, `movementType` (+`elasticity`),
  `inertia` (+`decelerationRate`), `scrollSensitivity`, `onValueChanged`, optional
  `horizontalScrollbar`/`verticalScrollbar` (+visibility modes). Internal structure
  (viewport + RectMask2D + content) is generated; children mount into content.
- `<Dropdown>` (TMP_Dropdown, compound): `options` (list of {text, image}), `value`,
  `onValueChanged`; template subtree generated with override slots.
- `<InputField>` (TMP_InputField, compound): `text`, `placeholder`, `characterLimit`,
  `contentType` (+lineType, inputType, keyboardType, validation), `readOnly`,
  `caretBlinkRate`, `selectionColor`, `onValueChanged`, `onEndEdit`, `onSubmit`,
  `onSelect`/`onDeselect`.

### 2.6 Layout components
- Container elements: `<HorizontalLayoutGroup>`, `<VerticalLayoutGroup>`,
  `<GridLayoutGroup>` — a plain GO + the layout component; props exactly as Inspector:
  `padding` (RectOffset), `spacing`, `childAlignment`, `reverseArrangement`,
  `childControlWidth/Height`, `childScaleWidth/Height`, `childForceExpandWidth/Height`;
  Grid: `cellSize`, `startCorner`, `startAxis`, `constraint` (+count).
- Per-child props on ANY element: `layoutElement={ minWidth, minHeight, preferredWidth,
  preferredHeight, flexibleWidth, flexibleHeight, layoutPriority, ignoreLayout }` —
  adds/removes a LayoutElement component.
- Self-sizing props on any element: `contentSizeFitter={ horizontalFit, verticalFit }`,
  `aspectRatioFitter={ aspectMode, aspectRatio }` (with the §2.2 driven-rect rules).
- Rebuild protocol: adapters batch property writes per commit and call
  `LayoutRebuilder.MarkLayoutForRebuild` once per dirtied subtree root — never per prop.

### 2.7 Masking and grouping
- `mask={true}` prop on a Graphic element → Mask component (stencil, needs the Graphic);
  `rectMask2D={ padding?, softness? }` on any element (cheaper rect clip).
- `canvasGroup={ alpha, interactable, blocksRaycasts, ignoreParentGroups }` on any
  element — THE uGUI idiom for fading/disabling whole subtrees declaratively; maps
  perfectly to state-driven props.

### 2.8 Prefabs — the reuse habit
`<Prefab source={GameObject} />`:
- Instantiates under the tree position, reconciled as a keyed leaf (source change =
  destroy + re-instantiate; prop changes = rebind).
- Prop binding contract, two tiers: (a) `bind={obj}` — if the prefab root has a component
  implementing `IReactivePrefab { void Bind(object props); }`, call it on every prop
  change; (b) `overrides={ "path/to/child.ComponentType.member": value }` weak-typed
  fallback for prefabs the user cannot edit.
- `onInstantiated={go => ...}` callback + `Ref<GameObject>` support for tween/Animator
  habits.
- This element is the migration bridge: an existing uGUI project can mount a Reactive
  tree that is 90% prefabs on day one and convert leaf-by-leaf.

### 2.9 Events and the EventSystem
- uGUI input requires an `EventSystem` + input module in the scene. The backend does NOT
  auto-create one silently; `UguiRootRenderer` validates at mount and logs a single
  actionable error (auto-create behind an opt-in bool, default off — respect the user's
  scene ownership).
- High-level events are the components' own UnityEvents (`onClick`, `onValueChanged`,
  ...): adapters subscribe/unsubscribe delegate wrappers on diff — no reflection, no
  persistent-listener API (that is editor-serialization machinery, wrong tool at runtime).
- Low-level pointer events on any element (`onPointerEnter/Exit/Down/Up/Click`,
  `onBeginDrag/onDrag/onEndDrag`, `onScroll`) via ONE internal listener MonoBehaviour
  implementing the corresponding `IPointerXxxHandler`/`IDragHandler` interfaces, attached
  lazily only when a handler prop is present.
- `raycastTarget` policy — a deliberate DX improvement over hand uGUI: default FALSE for
  purely visual elements, auto-true when any pointer handler or Selectable is present,
  always explicitly overridable. (Stray raycast targets are uGUI's #1 perf smell.)

### 2.10 Performance characteristics the backend must respect
- Canvas rebuild: any Graphic vertex/property dirty re-batches its canvas. Mitigations:
  the differ's minimal writes (already core to the fiber), per-commit batched writes,
  nested `<Canvas>` isolation as the documented recipe, raycastTarget hygiene (above).
- GameObject churn is 10-100x more expensive than VisualElement churn: adapter-level
  pooling (per element type) for unmount/remount, `SetActive(false)` parking, pool
  invalidation on HMR swap and scene teardown.
- TMP text changes regenerate mesh — coalesce `text` writes per commit (differ already
  guarantees single write per prop per commit).
- Reorder = `SetSiblingIndex` (sibling order IS paint order in a canvas — maps 1:1 to
  the reconciler's child order semantics; no z-index concept to emulate).

---

## 3. Architecture

### 3.1 Where the seam already is
`FiberHostConfig` mediates ALL element creation/property application through
`ElementRegistry` → `IElementAdapter`. The fiber's structural ops (append/insert/remove/
reorder child) and the adapter interface are the only places that need to become
host-generic. Survey result: 92 files in `Shared/` reference `VisualElement`; ~70 are the
per-element ADAPTERS (correctly backend-specific — they simply stay in the UITK backend),
leaving **~22 core files** to refactor: `Core/Fiber/*` (6), `Hooks.cs`, `HookRegistry.cs`,
`RefUtility.cs`, `SyntheticEvents.cs`, `ReactiveTypes.cs`, `V.cs`, `VNode.cs`,
`VNodeHostRenderer.cs`, `NodeMetadata.cs`, `PortalContextKeys.cs`, `PanelDetachGuard.cs`,
`MainThreadTimer.cs`, `Core/Animation/*` (3).

### 3.2 Host abstraction choice
Three candidates, one recommendation:
- (a) Generic fiber (`FiberReconciler<THost>`): viral generics through hooks/refs/context;
  rejected — infects every public signature and user-visible type.
- (b) `IHostElement` interface implemented by a wrapper around VisualElement/GameObject:
  allocation per node + interface dispatch on the hottest paths; workable but pays
  overhead precisely where the UITK backend is today zero-cost.
- **(c) React's own answer — untyped host instances (RECOMMENDED)**: the fiber stores
  `object HostInstance`; ALL host-typed operations live behind an abstract
  `HostBackend` (`CreateElement`, `ApplyProps`, `AppendChild`, `InsertAt`, `RemoveChild`,
  `SetIndex`, `CreateTextNode`, `SetText`, event attach/detach, ref resolution). The
  existing `FiberHostConfig` becomes `UitkBackend : HostBackend` (casts once per op —
  measured-negligible); `UguiBackend : HostBackend` is the new sibling. UITK-only
  services (`PanelDetachGuard`, panel polling) move behind optional backend capabilities.
  Public API compat: `Ref<VisualElement>` keeps working (refs are already generic —
  `Ref<T>`); uGUI trees use `Ref<GameObject>`/`Ref<RectTransform>`/`Ref<Button>` etc.
  Synthetic events split into a small backend-neutral core + per-backend adapters.
- Gate for this stage: **byte-identical behavior for the UITK backend** — full suites +
  golden emissions unchanged + a perf micro-benchmark showing no commit-path regression.

### 3.3 Backend selection and vocabularies
- One mount = one backend. `RootRenderer`/`EditorRootRendererUtility` = UITK (unchanged);
  new `UguiRootRenderer : MonoBehaviour` (target `RectTransform`, mounts/reconciles under
  it) = uGUI.
- TWO element vocabularies, two registries. Tag names may overlap (`Image` exists in
  both) — resolution is per-mount registry, so no ambiguity at runtime.
- `.uitkx` files declare their vocabulary with a file-level directive: `@backend ugui`
  (absent = uitk). The directive selects: SG tag resolution table, schema for editor
  intelligence, applicable diagnostics (e.g. `@uss` in a ugui file = error; `Style`-typed
  props = error with a "use uGUI props" message). Components remain host-agnostic at the
  function level, but a component authored against ugui tags is a ugui component — the
  diagnostic layer enforces that imports across backends are errors (same clean rule as
  one-mount-one-backend).

### 3.4 Element/adapter model for uGUI
- `UguiElementAdapter` counterpart of `BaseElementAdapter`: `Create()` returns a
  configured `GameObject` (with RectTransform + primary components), `ApplyTypedDiff`
  writes only changes, `ResolveChildHost` returns the child attach point (content rect
  for ScrollRect, template slots for compound elements).
- **Primary-component principle**: each element = one GO + one primary component
  (Graphic or Selectable); every secondary uGUI component (LayoutElement, CanvasGroup,
  Mask, Shadow, Outline, ContentSizeFitter) is a PROP GROUP that the differ
  adds/removes/updates — mirroring "Add Component" in the Inspector.
- **Compound elements** (ScrollRect/Dropdown/InputField/Slider) generate their internal
  skeletons exactly like the GameObject > UI menu templates do, with named override
  slots for the parts users habitually restyle (handle, checkmark, placeholder, caret,
  viewport).
- Typed props: per-element `*Props : UguiBaseProps` classes (the typed pipeline already
  exists — `ITypedElementAdapter`); `UguiBaseProps` carries the RectTransform block,
  the shared prop groups (§2.7), and `name`/`layer`/`tag`.

### 3.5 What carries over with zero or near-zero work
Hooks (state/reducer/memo/effect/layoutEffect/context/deferred/imperative-handle),
signals, suspense, keys/reorder semantics, the time-sliced work loop, Fast-Refresh
families (keyed by component FQN — backend-irrelevant), the `.uitkx` parser/formatter/
importer machinery, and component-level SG emission (`V.Func<TProps>`, `V.Suspense`,
`V.Portal`, `V.Fragment`, `V.Text` — backend-neutral by construction), HMR compile/swap
plumbing. ELEMENT emission is NOT free (verified against CSharpEmitter): elements emit
TYPED factories (`V.Box(new BoxProps {...})`) resolved by PropsResolver against the
factory surface — the ugui vocabulary needs its own factory class + props classes; see
section 4. Portals: re-target to any `RectTransform`
(the GO reparent op is native). The `Animate` layer needs the backend split (it writes
style properties today) — uGUI animation habit is Animator/tweens via refs; `Animate`
support can trail.

---

## 4. Language & tooling parity surface (the four layers)
Facts below verified against the code 2026-07-25.
- Parser/language-lib: `@backend` directive (trivial preamble addition); no grammar
  change otherwise.
- SG: elements emit typed factory calls (`V.{MethodName}(new {X}Props {...})`), so the
  ugui vocabulary is a NEW factory surface — a sibling static factory class (working
  name `U.*`; final name = open question 1b) + ugui props classes in a new
  `Shared/Ugui/` area, with PropsResolver selecting the factory surface by `@backend`.
  Markup tags stay bare Inspector names; only generated code differs. This is also how
  the tag-name overlap with UITK (`Image`, `Button`) is resolved at compile time; the
  per-mount registry resolves the same names at runtime. Diagnostics per section 7.
- Emitter parity: the new element table mirrors across SG (CSharpEmitter), the HMR
  emitters, and the IDE virtual document — `HmrEmitterParityContractTests` grows a ugui
  fixture set, and the VDG (not covered by those tests) gets explicit pins.
- HookRegistry (single source of truth, Compile-linked into the language-lib under
  `UNITY_EDITOR`): its virtual-document hook stubs are UITK-typed today (`useRef()`
  returns `VisualElement`; `useUiDocumentRoot` is UITK-only). It gains per-backend stub
  sets keyed by `@backend` (`useRef` typed to the ugui host in ugui files;
  `useUiDocumentRoot` in a ugui file = diagnostic). M1 must keep the language-lib
  link-compile of this file intact.
- LSP: `uitkx-schema-ugui.json` — a second MAINTAINED schema next to
  `ide-extensions~/grammar/uitkx-schema.json`, kept current via the same `automation~`
  diff/patch flow (the UITK schema is not generated from props classes; neither is
  this one). Completion/hover/diagnostics keyed off `@backend`; defs, rename,
  formatting, semantic tokens are tag-agnostic already.
- HMR: compile/swap plumbing unchanged; additions are the mirrored element table
  (above), pool invalidation on swap, and `UguiRootRenderer` in the two root
  enumeration sites (`UitkxHmrController` + `UitkxHmrDelegateSwapper`, which today walk
  `RootRenderer.AllInstances` + `EditorRootRendererUtility.GetAllRenderers()`).

---

## 5. Milestones

| M | Deliverable | Gate |
|---|---|---|
| M0 | This plan reviewed; owner decisions on §8 open questions | owner sign-off |
| M1 | Host abstraction (§3.2c): fiber on `object` host + `HostBackend`; UITK backend re-seated | ALL suites green, golden emissions byte-identical, commit-path micro-bench flat, language-lib link-compile of HookRegistry.cs intact |
| M2 | `UguiRootRenderer` + core elements: Canvas, Panel, Image, RawImage, Text(TMP), Button + RectTransform props + anchor presets + events | playmode smoke scene renders + counter demo works |
| M3 | Layout: LayoutGroups, LayoutElement/ContentSizeFitter/AspectRatioFitter prop groups, driven-rect diagnostics, rebuild batching | layout parity scene vs hand-built duplicate; driven-prop diagnostics fire |
| M4 | Full interaction set: Toggle(+Group), Slider, Scrollbar, ScrollRect, Dropdown, InputField, Selectable nav/transitions | interactive gallery sample |
| M5 | Prefab element + refs + pooling + CanvasGroup/masking prop groups | migration-bridge sample (prefab-heavy screen) |
| M6 | `@backend ugui` + schema + LSP + diagnostics across the four layers | editor intelligence parity checklist; suites green |
| M7 | HMR verification wave (member files, combined imports, the whole field-battery re-run against a ugui mount) | owner in-editor battery |
| M8 | Docs (dedicated section: "uGUI backend — same data flow, your layout"), samples, changelogs | docs build + drift checks |
| M9 | Release (minor version; additive) | owner publish |

Honest effort: M1 is the risk concentrate (touching the hottest code in the library);
M2-M5 are wide but mechanical; total is a multi-week, Godot-port-scale effort, plus a
permanent second maintenance leg (every future element/prop/Unity-version wave gains a
uGUI column — `add-unity-version` skill must grow a step).

### M8 docs & changelog deliverables (the repo's specific machinery, not generic "docs")
- Docs site (`ReactiveUIToolkitDocs~`): new "uGUI backend" section — getting started
  for uGUI (mount, first component, anchor presets), full element/prop reference
  (generated-page style matching the UITK reference), the prefab migration-bridge
  guide, and a "which backend when" page; getting-started updated to present the
  backend choice. Redeploy = the owner's republish flow.
- `CHANGELOG.md` (source of truth): minor-version entry via `scripts/changelog.mjs`
  assist, per VERSIONING.md (additive = minor).
- `plans/DISCORD_CHANGELOG.md`: release post under the hard 2000-char-per-entry cap
  (discord-changelog skill rules: ASCII-only, prepend-only).
- IDE extensions: entries added to `ide-extensions~/changelog.json` (`@backend`
  directive, ugui schema completions/hover/diagnostics) and marketplace pages
  REGENERATED via the changelog system — README.md/overview.md are never hand-edited
  (changelog skill). Extensions version independently (0.x line).
- Samples: the M4 interactive gallery + M5 migration-bridge scene ship under
  `Samples/`, store-shape-checked (the store omit-list must not leak dev fixtures).
- Drift checks: docs-vs-code sweep for the new pages joins the existing docs accuracy
  audit routine.

---

## 6. Performance plan
Pooling per element type; per-commit write batching + single MarkLayoutForRebuild per
subtree; raycastTarget hygiene by default; nested-canvas guidance in docs; benchmark
scene comparing hand-written uGUI vs reactive-uGUI for: 500-row scroll list (pooled),
per-frame text updates, full-screen re-render. Acceptance: within 15% of hand-written
uGUI on rebuild-heavy scenarios, better on raycast/idle (because of hygiene defaults).

---

## 7. Diagnostics (new, Unity-local band continuation 2111+)
- 2111 (Error): Style/USS/`@uss` surface used in a `@backend ugui` file.
- 2112 (Warning): RectTransform prop written while driven by parent layout component —
  names the driver and the `layoutElement.*` alternative.
- 2113 (Error): cross-backend import (a ugui file importing a uitk component or vice
  versa).
- 2114 (Warning): pointer handler on an element with explicit `raycastTarget={false}`.
- 2115 (Info/Hint): `EventSystem` missing at mount (runtime log, not compile-time).
Verified free 2026-07-25: 2100-2110 are occupied; 2111+ is the next open range.
Re-verify at implementation time; family band untouched (these are Unity-local by
nature).

---

## 8. Open questions for the owner
1. Tag naming: bare Inspector names (`<Image>`, `<Button>`) with per-mount resolution, or
   a `u:` prefix? (Plan assumes bare names + `@backend`; cleanest for habit preservation.)
   1b. Generated-code factory class name for the ugui surface (`U`, `Vu`, `V.Ugui`) —
   users never type it in markup, but it appears in generated partials and stack traces.
2. Should `UguiRootRenderer` optionally own/create its Canvas (prefab-less quick start)?
3. Pool by default or opt-in per mount?
4. Does `Animate` ship in v1 for uGUI (tween adapter) or defer to refs+DOTween habits?
5. Editor-window preview: uGUI cannot render in EditorWindow without a scene — accept
   "play-mode/scene-view only" for the uGUI backend? (UITK keeps editor-window support.)

## 9. What is NOT possible / accepted losses
- Typed `Style`, USS files, style transitions — by design (non-goal).
- UITK-only controls with no uGUI counterpart (MultiColumnListView, TreeView, editor
  fields like ObjectField/ColorField outside editor contexts) — absent from the ugui
  vocabulary; the schema makes this visible at authoring time.
- Built-in list virtualization — uGUI has none; v1 ships pooling + a documented
  windowed-list recipe; a `<VirtualScrollList>` helper is a candidate follow-up.
- Editor-window hosting of uGUI trees (engine limitation).
- Mixing backends inside one tree (v1) — islands remain the interop story.
- Vertex-effect parity with UITK visuals (rounded corners/borders are UITK/USS concepts;
  in uGUI land users reach for sprites/9-slice/materials — their habit, preserved).

## 10. Risks
- R1: M1 regression risk on the hottest paths — mitigated by the byte-identical gate +
  micro-bench; do M1 alone, ship nothing else in that wave.
- R2: compound elements (InputField/Dropdown) have deep internal state interplay with
  reconciliation (IME, caret, focus) — prototype early in M4, budget field-testing.
- R3: driven-rect detection completeness (third-party ILayoutController components can
  drive rects too) — v1 detects first-party drivers; escape hatch prop
  `suppressDrivenRectCheck`.
- R4: maintenance doubling — every future feature asks "and on uGUI?"; priced in above,
  and the `@backend` split keeps "not supported on ugui" expressible in the schema
  rather than as runtime surprises.
- R5: demand uncertainty — recommendation stands to gauge store-listing feedback before
  green-lighting M1.
