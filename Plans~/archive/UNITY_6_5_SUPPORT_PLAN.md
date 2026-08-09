# Unity 6.5 support plan (with 6.3 / 6.4 backfill)

Status: **EXECUTED — archived 2026-08-04.** Phase 1 shipped in 0.14.0/0.14.1; Phase 2 (the
PanelRenderer host, A3 root sources, WA1–WA4) shipped in 0.15.0 on `feat/phase2-panelrenderer-host`,
demo-verified on 6000.5.6f1. The workaround registry §5.9 remains the WA lifecycle authority
(referenced from `Plans~/REMAINING_WORK.md` §0 and the 0.15.0 changelog). Deferred items live in
`Plans~/REMAINING_WORK.md`; the header below is the original research-time status, kept verbatim.

Original status: **RESEARCH COMPLETE — implementation not started.** Written 2026-08-01 against
`feat/family-parity` @ `857e495a`, package `0.13.0`, Unity floor `6000.2`.

All empirical spikes (§5.8) are **done**, measured on `6000.5.6f1`. The Phase-2 gate is **green**.
Architecture decisions are settled: **A3** root-source abstraction (§5.5), **simple remount** on the
release path (§5.8.5), world-space UI in scope (§4.4). Findings 1–13 in §5.8.2–5.8.5 supersede any
earlier inference in this document; where they conflict, the measurement wins and the earlier text
has been corrected in place.

Every API fact below is **Cecil-verified against the installed DLLs**
(`6000.2.6f2`, `6000.3.17f1`, `6000.4.10f1`, `6000.5.6f1`), not read off release notes.
Where only web sources support a claim it is marked **[web]**. Unity's own release notes were
found to be unreliable for version attribution (they list backported items under the newest
stream), so they are used for *semantics* only, never for *versioning*.

---

## 0. Prerequisite — DONE

**The discovery tool was silently under-reporting.** `automation~/unity-api-diff.ps1` keyed its
element/enum/struct collections by simple `.Name`, and nested types share that key space with
top-level ones. Unity 6.5 added a top-level `UnityEngine.UIElements.WorldSpaceSizeMode` while
`UIDocument/WorldSpaceSizeMode` already existed in 6.4 — the addition was dropped from the diff.

Fixed (+22/−3): a `Get-TypeKey` helper applied at all three collection sites. Top-level types keep
their simple name (**required** — `automation~/apply-diff-to-schema.mjs` matches
`schema.elements[name]` by simple name); nested types become `Outer.Nested`.

Verified in both directions:

| Check | Result |
|---|---|
| 6.4→6.5 enums | +2 → **+3** (`WorldSpaceSizeMode` now surfaced) |
| False positive removed | phantom `Enumerator` "change" was colliding nested types → now `VisualElement.Hierarchy` |
| Regression 6.2→6.3 | still exactly `aspectRatio`, `filter`, `unityMaterial`; 0 elements; +6 enums; +12 structs |
| `check-machine-paths` | clean |

**Consequence — a previously reported finding was wrong.** With the buggy tool I reported that 6.4
added `PanelRenderMode` and `TextureOptions`. It did not: both are **6.3** types, present in
`6000.3.8f1`. Corrected in §1.

**Second methodology fix: pin exact patch versions.** `6000.3.8f1` and `6000.3.17f1` do not have the
same API surface. Prefix matching (`-From 6000.4`) silently takes the *first* Hub match. All future
runs must use `-FromDll`/`-ToDll` with explicit paths, and the report's `meta` block records them.

---

## 1. What actually changed — the authoritative matrix

Public **top-level** types, by the version that introduced them. Zero types were removed anywhere in
6.3→6.5.

| Type | Kind | Introduced | Library action |
|---|---|---|---|
| `PanelRenderMode` | enum | **6.3** | none — §2.3 |
| `TextureOptions` | enum | **6.3** | none — §2.3 |
| `GUIDField` | VisualElement | **6.4** | **wrap** — §3 |
| `MaskField` | VisualElement | **6.5** | **wrap** — §3 |
| `Mask64Field` | VisualElement | **6.5** | **wrap** — §3 |
| `BaseMaskField<T>`, `BaseMask64Field` | abstract bases | 6.5 | none (bases only) |
| `PanelRenderer` | `: UnityEngine.Renderer` | 6.5 | **Phase 2 — in scope** — §5 |
| `IPanelComponent` | interface | 6.5 | **the migration seam** — §5 |
| `WorldSpaceSizeMode` | enum | 6.5 | with `PanelRenderer` |
| `VisualElementClearOptions` | enum | 6.5 | **reconciler risk** — §4.2 |
| `AuthoringIdPath` | struct | 6.5 | none — authoring plumbing |
| `VisualElementReference`, `VisualElementReference<T>` | class | 6.5 | none — §2.3 |
| `VisualElementAssetReferenceTable` | class | 6.5 | none — §2.3 |
| `TextElement.GlyphKind` | nested enum | 6.5 | none — read-only introspection |

### 1.1 IStyle: nothing to do, verified twice

`IStyle` has **89 properties in both 6.4 and 6.5** — zero added, removed, or changed. Independently
corroborated by a docs diff of the 6.4 vs 6.5 ScriptReference pages, controlled against 6.2 (which
correctly lacks the three 6.3 additions).

`IStyle` also declares exactly one **method**, `Clear`, in both versions — the property-only diff
would have missed a method change, so this was checked explicitly.

**Coverage audit:** all 89 Unity 6.5 IStyle properties are already present in the library's
coverage arrays (`SourceGenerator~/Tests/IStyleCoverageTests.cs`). **No gaps.** Notably
`unityTextGenerator` — the ATG opt-out (§4.1) — is already wrapped.

**Therefore the entire style pipeline is no-op for this wave:** `Shared/Props/Typed/Style.cs` (all
six edit sites), `StyleKeys.cs`, `CssHelpers.cs`, `PropsApplier.cs`, `TypedPropsApplier.cs`, the
schema `styleVersions` block, `stylePropertyCatalog.ts`, and both `IStyleProperties_*` test arrays
are untouched. This is the expensive half of the normal version-add runbook and it does not apply.

---

## 2. Scope decisions

### 2.1 In scope — two phases, each independently shippable

The wave is split into **two phases**. Each is a complete, releasable unit: implementation +
verification + full release surface (changelog, Discord, version bumps). Phase 1 ships and is
published; Phase 2 begins immediately after.

**PHASE 1 — mechanical version support.** The normal `add-unity-version` runbook. No architectural
risk; nothing here touches the reconciler.
1. **Three new controls** — `MaskField`, `Mask64Field` (6.5), `GUIDField` (6.4). §3.
2. **Version gating** for all three, plus the docs version manifest. §6.
3. **Documentation** — component pages, version dropdown, Unity doc links, the ATG note. §7.
4. **Release surface** — §8. Ships as a **minor** bump (additive controls).

**PHASE 2 — the `PanelRenderer` host.** Everything the spikes uncovered. §5.
1. **A3 root-source abstraction** + `UIDocument`/`PanelRenderer` implementations. §5.5.
2. **Sub-root mount, deferred mount + replay, idempotent callback, three-way rebuild branch.** §5.3.
3. **Retention-site cleanup — a hard blocker**, promoted by finding 13. §5.4.
3b. **Nested-renderer support** — N2 prevention (always on) + N6 repair (default-on, opt-out) with a
   full settings copy and editor `Undo` integration. §5.8.7 decisions D1–D5. Includes the docs page
   and the upstream bug report.
4. **World-space UI parity** on the new host. §4.4.
5. **Release surface** — §8. Ships as a **minor** bump (additive host).

### 2.2 Sequencing note

Phase 1 has **no dependency** on Phase 2 and ships first so `MaskField`/`GUIDField` reach users
without waiting on the host work. Phase 2's gate (§5.8) is already green, so nothing blocks it
either — the split is about shipping value early, not about risk hedging.

Within Phase 2, land the A3 abstraction and the two UITK hosts first (that is what 6.5 needs), then
migrate the remaining four mount sites (EditorWindow, uGUI, both islands).

### 2.3 Explicitly out of scope, with reasons

| Item | Why no work |
|---|---|
| `PanelRenderMode` (6.3) | A `PanelSettings` **asset** property (`PanelSettings.renderMode`). The library does not wrap `PanelSettings` — users configure it in the Inspector. Reachable today with zero changes. |
| `TextureOptions` (6.3) | A per-draw-call flag on `MeshGenerationContext.DrawMesh`. The library already exposes `generateVisualContent`, so a user's callback receives the context and passes the flag directly. Nothing to wrap. |
| `AuthoringIdPath`, `VisualElementReference(<T>)`, `VisualElementAssetReferenceTable` | Inspector-authoring plumbing for `PanelRenderer`, and **runtime-UI + PanelRenderer only** [web]. Its purpose — replacing `root.Q<T>("name")` string lookups with serialized references — is orthogonal to a code-first component model that never queries by name. |
| `TextElement.GlyphKind` | Read-only per-glyph introspection on `TextElement.Glyph`. ATG-only (the legacy generator always reports `Character`). No prop surface. |
| `BaseMaskField<T>`, `BaseMask64Field` | Abstract bases of the wrapped controls. |

---

## 3. The three controls

### 3.1 Facts (Cecil + official docs)

**`MaskField`** — `: BaseMaskField<int> : BasePopupField<int,string> : BaseField<int>`.
Multi-select bitmask dropdown.

- Own members: `formatSelectedValueCallback`, `formatListItemCallback` (both `Func<string,string>`).
- Inherited: `choices` (`List<string>`), `choicesMasks` (`List<int>`), `value` (`int`),
  `SetValueWithoutNotify`, `label`, `showMixedValue`, `text`.
- USS: `.unity-mask-field`, `__label`, `__input` (+ `.unity-base-popup-field*` from the base).
- **UXML attributes are `choices` and `value` only — `choicesMasks` is C#-only.** [web]

**`Mask64Field`** — same shape, `value` is `ulong`, `choicesMasks` is `List<ulong>`.
USS `.unity-mask64-field*`.

**Mask semantics** (matters for the reconciler's diffing) [web]:
the dropdown is `choices` prefixed by two synthetic entries, `"Nothing"` (index 0) and
`"Everything"` (index 1); user choices start at displayed index 2. Default mask per user choice is
`1 << i`. `"Nothing"` → `0`; **`"Everything"` → `~0` (i.e. `-1`), *not* `(1 << n) - 1`.**
→ **The props differ must not normalize `~0` and "all defined bits" to each other.**
`choicesMasks` overrides the `1 << i` default per index, enabling composite flags.

**`GUIDField`** (6.4) — `: TextInputBaseField<UnityEngine.GUID>`.

- **`GUID` resolves to `UnityEngine.GUID`, not `UnityEditor.GUID`** — Cecil-confirmed from the base
  type's generic argument. So the control is genuinely runtime-usable with no editor dependency;
  the props class can name `UnityEngine.GUID` safely. (Web research could not resolve this; the DLL
  did.)
- USS: `.unity-guid-field`, `__label`, `__input`.
- UXML `value` round-trips as a **delayed string** (`[UxmlAttribute("value"), Delayed]` on a
  `valueAsString` property) — accepts dashed and undashed hex.
- Inherited from `TextInputBaseField`: `isReadOnly`, `isDelayed`, `maxLength`, `placeholder-text`,
  `select-all-on-focus`, `SelectAll()`, `SetValueWithoutNotify()`.

### 3.2 Framing correction — these are *relocations*, not inventions

`MaskField`, `Mask64Field`, `BaseMaskField<T>` and `BaseMask64Field` existed before 6.5 in
**`UnityEditor.UIElements`** and were documented as *Editor-only* controls; 6.5 moved them into the
**runtime** `UnityEngine.UIElements` module [web]. From this library's perspective the effect is
identical to a new control — they become available at runtime for the first time — but the framing
matters for the changelog wording and for the docs' "since" note.

Related — **checked during implementation, no action needed.** `EnumFlagsField` keeps its
`UnityEditor.UIElements` **namespace** but moved **DLL**: it is now in `UnityEditor.CoreModule.dll`,
not `UnityEditor.UIElementsModule.dll`, and its base is now
`UnityEngine.UIElements.BaseMaskField<System.Enum>` (the base did cross to the runtime module).
The move predates 6.5 — it is already out of `UnityEditor.UIElementsModule` on 6.4 — so the existing
wrapper compiles unchanged and this is **not** a 6.5 regression. Editor asmdefs auto-reference all
editor modules, so the DLL move is invisible to us.

### 3.3 Implementation — the 12-file template

`EnumFlagsField` is the closest precedent (same `BasePopupField` lineage, same mask concept). Per
control:

| # | File | Note |
|---|---|---|
| 1 | `Shared/Props/Typed/{X}Props.cs` (+ `.meta`) | typed props; gate the whole file body |
| 2 | `Shared/Elements/{X}ElementAdapter.cs` (+ `.meta`) | create/apply/diff |
| 3 | `Shared/Core/V.cs` | `V.{X}(props, key)` → `RentElement("{X}", key, props)` (pattern at ~L211) |
| 4 | `Shared/Elements/ElementRegistryProvider.cs` | **three** sites — `RegisterIfAllowed` (~L67), allow-set (~L139), switch + `Register` (~L307) |
| 5 | `ide-extensions~/grammar/uitkx-schema.json` | element entry **+ `sinceUnity`** (see §6.2) |
| 6 | `ReactiveUIToolkitDocs~/src/pages/Components/{X}/{X}Page.tsx` | new page |
| 7 | `…/pages/Components/{X}/{X}Page.example.ts` | examples |
| 8 | `…/src/pages.tsx` | route registration |
| 9 | `…/src/components/UnityDocsSection/unityDocLinks.ts` | Unity doc URL |
| 10 | `…/src/pages/UITKX/Components/UitkxComponentReferencePage.tsx` | reference table row |
| 11 | `…/src/pages/UITKX/Components/UitkxComponentsPage.tsx` | index listing |
| 12 | `…/src/versionManifest.ts` | `ELEMENT_VERSIONS` entry (§6.2) |

**✅ `unityDocLinks.ts` caveat — AUDITED, no action needed.** The concern was that uGUI types no
longer have main-ScriptReference pages (`ScriptReference/UI.*.html` 404s on 6.4/6.5; uGUI docs moved
under `docs.unity3d.com/Packages/com.unity.ugui@<ver>/api/`) [web]. Checked during implementation:
`unityDocLinks.ts` builds **Manual** URLs only
(`Documentation/Manual/UIE-uxml-element-{name}.html`) and contains **no uGUI entries at all** — every
entry is a UI Toolkit element. Nothing to fix.

---

## 4. Risks — the parts that are not mechanical

### 4.1 🔴 ATG becomes the default text system in 6.5

Unity made the Advanced Text Generator the default for UI Toolkit in 6.5 (opt-in 6.0 → Editor
default 6.3 → IMGUI 6.4 → **runtime default 6.5**) [web].

**Unity claims *feature* parity — never *measurement* parity.** ATG shapes through HarfBuzz + ICU +
FreeType; real shaping changes advance widths (kerning, ligatures) and ICU changes line-break
opportunities. Unity documents one divergence itself (`GlyphKind` is ATG-only; legacy always reports
`Character`).

**Unity has conceded measurement drift explicitly.** Release note, 6000.4.0b4 [web]:
> *"Editor: Fixed Advanced Text Generator `fontSize` to match TextCore. (UUM-114780)"*

That is Unity stating ATG's `fontSize` did not match TextCore until 6.4.0b4.

**Measurement/layout defects, with landing versions** [web]:

| ID | Issue | Fixed in |
|---|---|---|
| UUM-114780 | ATG `fontSize` didn't match TextCore | 6000.4.0b4 |
| UUM-137212 | Synthetic bold + wrap + ellipsis overflows ("completely broke all UI" — staff-confirmed) | **6000.5.0b11** |
| UUM-122919 | Auto-Size fluctuates wildly on resize | 6000.5.0a2 |
| UUM-130521 / UUM-128667 | Ellipsis width / placement | 6000.5.0a3–a5 |
| UUM-128666 | BIDI wrapping overflow | 6000.5.0a5 |
| UUM-135887 | CJK wraps outside Label bounds | 6000.5.0a9 · **Won't Fix on 6.4.X** |
| UUM-136362 | Static font asset throws under ATG | 6000.5.0b1 · **Won't Fix on 6.4.X** |
| UUM-147305 | `PostProcessTextVertices` throws OOR | **6.6/6.7 only — still broken on 6.5** |

Good news for us: **the install in hand (`6000.5.6f1`) is past every 6.5 fix above.**
Bad news: **6.4.X is the worst branch for ATG parity** — two defects are Won't-Fix there.

**🔴 The one unacknowledged divergence — line breaking at punctuation.** Community reports (English,
Chinese, Hebrew) that ATG aggressively breaks lines at commas/periods where TextCore did not;
`U+00A0`, `U+FEFF`, `U+2060` all fail to prevent it; **no UUM ID, no staff reply**, and the only
working fix is disabling ATG. If our layout depends on wrap points, this is the real risk — it is
open, undiagnosed, and invisible to any API diff.

**Hard breaks worth documenting for consumers:**
- **Static font assets are unsupported** — `"Advanced text system cannot render using static font
  asset"`. Any consumer shipping static font assets must migrate to dynamic.
- Rich-text parsing is not byte-identical: `<style=blue>` must become `<style="blue">`;
  `<align=flush>` is unsupported.

**The opt-out is a cascading USS property, not a project setting** [web]:

```css
:root { -unity-text-generator: standard; }
```
```csharp
element.style.unityTextGenerator = new StyleEnum<TextGeneratorType>(TextGeneratorType.Standard);
```

Inherited, non-animatable — **a consumer can flip generators per subtree**, so the library must not
hold a global assumption about text metrics. `unityTextGenerator` is already in the typed `Style`
surface (§1.1), so no code change is needed. Note Unity intends to **remove the opt-out eventually**
("we eventually plan on removing the ability to opt-out"), so this is a migration runway, not a
permanent escape hatch.

**Action: empirical, not documentary.** Render the text-heavy samples on 6.4 and 6.5 and compare
measured sizes and wrap points — with a deliberate case for punctuation-heavy wrapped text. This is
the one item that cannot be settled by reading.

### 4.2 🔴 6.5 resource-release semantics vs. a pooling reconciler

Cecil-confirmed **new in 6.5** (absent in 6.4):

```csharp
public void ReleaseResources();                       // VisualElement
public void Clear(VisualElementClearOptions options); // VisualElement
```

`VisualElementClearOptions` = `None` (direct children only — *not* a no-op), `Recursive`,
`RecursiveReleaseResources`.

`ReleaseResources()` requires the element to be detached and childless, and **leaves it permanently
unusable** — touching or re-adding it throws.

**Why this matters here:** the fiber reconciler pools and retains `VisualElement` references across
renders. Two 6.5 behaviors interact badly:

- `PanelRenderer` **auto-calls recursive release** on its root, and it **descends into our
  programmatically-added children** — measured, §5.8.4 finding 10.
- `PanelRenderer` **preserves** UI content across disable/enable, where `UIDocument` destroyed and
  recreated it — measured, §5.8.2 finding 3.

So teardown logic keyed on "disable destroys the tree" would leak or double-mount under
`PanelRenderer`, and any retained element could be released out from under the pool. **This is the
single most dangerous silent change in 6.5** and it is a prerequisite risk for §5, not for §3.

**Now measured, not inferred.** §5.8.2–5.8.5 settle the exact preconditions, triggers, and detection
mechanism; §5.8.5 carries the closed trigger table. Two facts reshape this risk:
`ReleaseResources()` is **detached-only and leaf-only** (so it cannot release a subtree at all — only
`Clear(RecursiveReleaseResources)` can), and the editor triggers are the frequent ones (live reload
does not exist in a player build).

### 4.3 uGUI backend — 6.5 changes worth a look [web]

The library ships a uGUI backend (`Ugui/`), so these matter even though they are outside UI Toolkit.
uGUI package went `2.0.0` (6.0–**6.4**) → `2.5.0` (6.5) — so **6.4 has no uGUI API additions at all**.

| Change | Relevance |
|---|---|
| **`RaycastReceiver`** — `: Graphic`, adds zero members, `OnPopulateMesh` clears. The transparent-Image raycast trick formalized. | Candidate element for the uGUI vocabulary; cheaper than the current transparent-Image idiom. **Optional.** |
| **`Graphic.Raycast()` substantially rewritten** — ancestors with `raycastTarget == false` are skipped as filters; failed `MaskableGraphic` parent results cached. | **Changes hit-testing results** in nested hierarchies. Worth a regression pass on the uGUI samples. |
| **`LayoutGroup` now sets `sendChildDimensionsChange`** and adds `OnChildRectTransformDimensionsChange()` → self-dirties on child dimension changes. | Real invalidation-behavior change under our layout-group adapters. |
| `GridLayoutGroup.generatedRowCount` / `generatedColumnCount` | New read-only, valid only after a layout pass. Optional props. |
| `Selectable.IsHighlighted()` / `IsPressed()` `protected` → **`public`** | Source-compatible; binary-breaking for prebuilt DLLs. |
| 3 new **static** `TMP_Text` events: `OnCharacterRequest`, `OnFontMaterialRequest`, `OnColorGradientAssetRequest` | Asset-loading hooks; not element props. |
| **`AddComponentMenu` paths renamed `"UI/…"` → `"UI (Canvas)/…"`** across ~20 components | Breaks any hardcoded menu path — audit ours. |
| **`com.unity.render-pipelines.core` no longer depends on `com.unity.ugui`** | **Do not assume uGUI is present via URP/HDRP** — our package manifest must declare it explicitly. |

Ahead (6.6 / uGUI 2.6.0): `ILayoutElement` gains Maximum Width/Height — an **interface change, breaking
for custom `ILayoutElement` implementers**. Check whether we implement it.

### 4.4 `UIDocument` — still fully usable in 6.5 (verified, corrects an earlier claim)

Cecil-verified on 6000.5.6f1:

```
UIDocument     [AddComponentMenu] "UI Toolkit/Legacy/UI Document (UI Toolkit)"
PanelRenderer  [AddComponentMenu] "UI Toolkit/Panel Renderer (UI Toolkit)"
```

- **`UIDocument` has no `[Obsolete]` attribute** → compiles with zero warnings.
- **It is still in the Add Component menu**, under a `Legacy/` submenu. An earlier research pass
  claimed new users "cannot add UIDocument from Add Component" — **that is false.** It is
  discoverable and addable; it is merely demoted.
- `UIDocument.rootVisualElement` **remains public** in 6.5 (`PanelRenderer`'s is `internal`).
- Unity staff on the record: no removal plans.

**Therefore `PanelRenderer` adoption is not forced on us by *compatibility*** — nothing breaks on
6.5 without it. The existing `RootRenderer`/`UIDocument` path is not deprecated, not warned, and not
broken.

**But adoption is mandatory on coverage grounds. Owner directive (2026-08-01):** *"we simply must
support anything unity throws at us and not limit the user to a subset of features we provide."* If
a user reaches for the component Unity's own manual points new projects at, our library must host
it. Two consequences for this plan:

- **World-space UI is required scope, not a stretch goal.** We already ship world-space UI on the
  `UIDocument` path, so this is about full parity on the new host — including the per-frame
  transform writes and `WorldSpaceSizeMode` — not a first attempt.
- **Optionality flows to the *user*, not to us.** Both hosts must work. The user picks.

Consequence: `RootRenderer` keeps working on 6.5, but the documented on-ramp Unity's manual points a
*new* 6.5 project at — it calls `UIDocument` *"obsolete and superseded by the Panel Renderer
component"* — is a component the library does not yet support. That is the argument for §5.

---

## 5. `PanelRenderer` + `IPanelComponent` — IN SCOPE (Phase 2)

Owner directive: both are in scope, core changes acceptable.

**What it is.** `class PanelRenderer : Renderer, IPanelComponent` — the 6.5 successor to
`UIDocument`, built for world-space UI. Members: `panelSettings`, `visualTreeAsset`,
`worldSpaceSizeMode`, `worldSpaceSize` (`Vector2`), `position`, `pivot`, `pivotReferenceSize`,
`parentUI`, plus `Register/UnregisterUIReloadCallback`. Note `pivot`/`pivotReferenceSize`/`position`
are **enums**, not vectors.

### 5.1 Source-verified lifecycle facts

Read from `PanelRenderer.bindings.cs` @6000.5 — these are quotes, not inference.

| Fact | Consequence |
|---|---|
| **`rootVisualElement` is `internal`; `IPanelComponent.GetRootVisualElement()` is NOT public** (Cecil: `IsPublic=False`) | **The callback is the only way in.** No pull model. `UIDocument.rootVisualElement` stays public, so the ≤6.4 path is untouched. |
| `RegisterUIReloadCallback` **fires immediately at registration** iff `root != null && root.panel != null`; multicast | Registration order is forgiving; double-registration double-fires. |
| ~~**`m_UIVersion` bumps only when a new root instance is constructed**~~ — **partly wrong, corrected by measurement** | The biconditional does **not** hold: run 1 saw a **NEW root with `version=1`** (domain reload resets the counter), and run 1 also saw a **SAME root with `version=1`**. `version` counts UI reloads *within a session* and resets on domain reload (§5.8.4 finding 11). **Do not use it as the stale-detector.** The reliable probe is `resourcesReleased` + our sub-root's `parent` (§5.8.4 finding 9). |
| **Release happens BEFORE the callback** — synchronously at the top of `InitRootVisualElement`, before the new root exists | We are never given a chance to detach first. Scenario **S4** (§5.2). |
| **`Clear(RecursiveReleaseResources)` is provenance-blind** — `GatherAllChildren` walks the live hierarchy | It **does** descend into our programmatically-added children. They are poisoned. |
| **`public bool resourcesReleased`** exists | We can detect poisoning **without throwing**. |
| Disable does **not** release — **measured: the sub-root stays `parented=True` under the SAME root**, children and state intact; re-enable fires the callback with unchanged version | Disable/enable is cheap and safe. **Correction:** the tree is *not* detached, it is left in place — so the callback must **reuse in place**, not rebuild (§5.8.2 finding 3). |
| With **`visualTreeAsset == null` (our case)** the per-frame `RefreshAssets()` path **never releases** | Confirmed empirically: an unrelated `.uxml` save produces literally zero output (§5.8.3 finding 6). See §5.8.5 for the **closed** trigger table — the one frequent trigger is saving a `.uxml` that IS this component's Source Asset. |
| Nested renderers insert at **`firstChildInsertIndex = 0`** for a null-VTA root | 🔴 **Unity inserts and removes children of our root behind our back.** |
| `SetupRootClassList`/`SetupWorldSpaceSize`/`SetTransform` write `position`, `width`, `height`, `transformOrigin`, `translate`, `rotate`, `scale` on the root **every frame** | 🔴 **The root's layout/transform styles are Unity-owned.** |

### 5.2 The release matrix — resolved

The four scenarios we planned for, and where reality landed:

| | Release before callback | Release after callback |
|---|---|---|
| **Doesn't descend into our children** | S3 | S1 |
| **Descends into our children** | **S4 ← WE ARE HERE** | S2 |

**S4 is the worst cell, and measurement confirmed both halves of it** — release happens before the
callback (finding 10: the sub-root is already `resourcesReleased=True` at callback entry) and it does
descend into our programmatically-added children (finding 10: the child Label was released too).

So on the **release** path the old tree is dead before we hear about it and retargeting is impossible
— there is nothing safe to move. That is *not* the only path, though: a sub-root can also be orphaned
**without** being released, and there retargeting is both legal and correct. Hence the three-way
branch below rather than a binary one.

Mitigating facts, both measured: with `visualTreeAsset == null` the frequent triggers never release
(finding 6). NOTE: the release path ALSO exists in a player build via the public `panelSettings` setter — §5.8.8 finding 28.

**Therefore the strategy is: detect, don't salvage.**

**The dispatch is keyed on our sub-root's own state, NOT on `version`** — finding 11 proved `version`
is not a reliable discriminator (it resets on domain reload, and a new root can arrive with an
unchanged value). The reliable signals are `subRoot.parent` and `subRoot.resourcesReleased`, both
cheap and both proven in §5.8.4 finding 9:

```
on UIReloadCallback(renderer, root, version):
    if (subRoot != null && subRoot.parent == root)   -> REUSE IN PLACE
                                                        no retarget, no rebuild, nothing to do
                                                        (measured: this is what disable/enable gives us)
    else if (subRoot != null && !subRoot.resourcesReleased)
                                                     -> RETARGET onto the new root
                                                        fiber + hook + ref + animation state preserved
    else                                             -> REMOUNT
                                                        drop the old tree WITHOUT touching it,
                                                        invalidate every retention site (5.4),
                                                        mount fresh into a new sub-root
```

The first branch is not an optimization — it is required. A blind rebuild on every callback visibly
stacks duplicate trees (§5.8.2 finding 3, observed as generations 1..4 in the Game view).

`RetargetContainer` is **only** legal in the second branch. It works by moving the *existing* host
elements into the new container, and re-inserting a released element throws
`InvalidOperationException` (finding 9) — so it must never be reached with a released sub-root.

**Remount loses hook state. DECIDED (owner, 2026-08-01): that is acceptable — ship the simple
remount.** A UXML save means the user changed their layout, so a full rebuild is the semantically
expected outcome and matches Unity's own live-reload model. A *rehydrate* path (rebuild host elements
under a preserved fiber tree) stays a possible follow-up, justified only if the mixed UXML+code
workflow turns out to be common.

### 5.3 Design decisions that follow

1. **🔴 Mount under our own sub-root, never directly on the PanelRenderer root.** On the callback we
   create one child (`__ruitk_root`) and mount the fiber tree into *that*. This solves three problems
   at once: Unity's front-insertion of nested-renderer roots (§5.1) no longer collides with our child
   list; Unity's per-frame style writes on the root no longer fight our `Style` system; and we get a
   single element to detach. **This is a hard requirement, not a preference.**
2. **Never read the old container during rebuild.** `FiberRenderer.RetargetContainer:116-123` calls
   `GetChildCount`/`GetChildAt` on the *old* container — under S4 that container is released.
   The rebuild path must not touch it at all.
3. **Deferred mount + replay.** `RootRenderer.Render:218-221` currently early-returns and **discards**
   the vnode when the root is null. With a callback host that is the normal case. Add a pending-vnode
   slot and replay it when the root arrives. (Also fixes a latent bug on the UIDocument path.)
4. **Idempotent callback.** UUM-139973 (double-fire entering play mode, fixed 6000.5.0b9) and
   UUM-142211 (`root.panel` null at callback, fixed 6000.5.0b11) both produce duplicate invocations.
   The **reuse-in-place** branch of §5.2 is itself the double-fire guard — a second callback with the
   same root finds the sub-root still parented and does nothing. Do **not** guard on `version`
   (finding 11).
5. **`bool IsAlive(object)` on `FiberHostConfig`** (virtual, default `true`). UITK implements it as
   `!((VisualElement)h).resourcesReleased`; uGUI implements it as the Unity fake-null check it already
   does ad hoc. This is the missing liveness concept — see §5.4.
6. **Invalidate retention sites on rebuild** (§5.4).

### 5.4 🔴 The invariant this breaks, and the retention sites it exposes

**Today the entire codebase assumes a `VisualElement` never dies.** It is a plain managed object —
no fake-null, never throws; an orphan stays readable and writable forever. **6.5's
`ReleaseResources()` breaks that invariant for the first time**: a released element throws on any
hierarchy mutation, its callback registry is cleared, and its layout node is recycled into Unity's
shared pool.

Sites that would hold poisoned references after a rebuild, and what each needs:

| Site | Today | Needed |
|---|---|---|
| `Shared/Props/PropsApplier.cs:13-16` — static `Dictionary<VisualElement, styles>` | cleared only via `OnHostRemoved` on fiber deletion; **already leaks the old root on every retarget** | evict on rebuild |
| `Editor/EditorRootRendererUtility.cs:15` — registry keyed by element identity | stale entry + duplicate mount if the root is replaced | must not be copied by the new host |
| `ListViewElementAdapter.cs:21-23` (+ TreeView `:21`, MCLV `:25`, MCTV `:35`) — row pools | **never evicted**; `unbindItem` only detaches | evict on rebuild |
| `FiberNode.HostElement` + `Alternate` double-buffer | never nulled in `CommitDeletion`; retained one extra generation | dropped wholesale by rebuild |
| `Animator.cs:116-186` — tick lambda captures `ve`; `AnimationTicker.cs:31` static event | a looping animation on an orphan **ticks forever** (gated on `panel != null`, so silent) | stop on rebuild |
| user `Ref<VisualElement>` | **never detached on unmount** (uGUI *does* detach — `UguiHostConfig.cs:218-223`) | detach on rebuild |
| `VNode` pool `_portalTarget`, `BaseProps` pool `Ref` | reset on *rent*, not on return | acceptable; nulled before reuse |

🔴 **A released element must never enter our pools.** Unity has already recycled its layout node into
`LayoutManager.SharedManager`; reusing it would corrupt another element's layout.

Several of these are **pre-existing leaks** that this work fixes as a side effect.

### 5.5 Host architecture — the options

| | Approach | Cost | Verdict |
|---|---|---|---|
| **A1** | New `PanelRendererRootRenderer`, parallel to `RootRenderer` | Low | 4th copy of the triplicated `EnsureSetup`; 4th HMR leg in **two** hard-coded lists; users choose a component per Unity version |
| **A2** | **Extend `RootRenderer`** with a 6.5-gated `Initialize(PanelRenderer, …)` + callback path | Medium | One component for users; reuses `UitkHostConfig` **verbatim** (it has zero `UIDocument` references); existing overloads untouched |
| **A3** ⭐ | Full `IRootSource` abstraction over all 6 mount sites | High | **CHOSEN.** The right eventual shape; also fixes the triplicated bootstrap and the element-keyed registry |

**DECIDED (owner, 2026-08-01): A3.** Size is explicitly not a constraint; A3 is the better end
state. Build the root-source abstraction now, with `UIDocument` and `PanelRenderer` as its first two
implementations, then migrate the remaining four mount sites (EditorWindow, uGUI, both islands) onto
it. What A3 buys beyond PanelRenderer support — all of it pre-existing debt this exposes:

- one bootstrap instead of three near-identical copies of `EnsureSetup`
- retarget available to **every** mount site, not just `RootRenderer` (and public + `object`-typed
  instead of `internal` + `VisualElement`-typed, with the first tests it has ever had)
- kills the element-keyed registry in `EditorRootRendererUtility.cs:15`
- one place for the HMR host list instead of two hard-coded duplicates
- the nested list/tree row renderers stop sharing a static UITK `HostContext`

**Sequencing inside A3:** land the abstraction + the two UITK hosts first (that is what 6.5 needs),
then migrate the other four sites. Phase 1 (§3) still ships independently of all of it.

### 5.5b Root-element ownership — what we can and cannot set

Unity writes these on **its** root every frame (`SetupRootClassList`, `SetupWorldSpaceSize`,
`SetTransform`): `position`, `width`, `height`, `transformOrigin`, `translate`, `rotate`, `scale` —
plus it constructs the root as an internal `PanelRendererRootElement : TemplateContainer` with
`pickingMode = Ignore`. Fighting it is pointless: it re-asserts them per frame.

**This costs us nothing, because of the sub-root (§5.3.1).** We own our sub-root completely — width,
height, `pickingMode`, picking, classes, styles, everything. So:

- **`V.Host(props, …)` retargets from Unity's root to our sub-root.** Root-level host props keep
  working exactly as they do today under `UIDocument`; they just land on an element we control
  rather than one Unity rewrites.
- Full-screen behaviour is expressed on the sub-root (stretch/`flex-grow`) instead of relying on
  Unity's root class — same result, and it works identically in world-space mode.
- `pickingMode = Ignore` on Unity's root does **not** block our children; `Ignore` excludes only the
  element itself from picking.

Net: the sub-root is not a workaround, it is the thing that *restores* full root control under
PanelRenderer.

### 5.5c Mixing `UIDocument` and `PanelRenderer` (verified)

| Question | Answer |
|---|---|
| Both hosts in the same scene, separate trees? | **Yes** — independent components. |
| Both attached to the same `PanelSettings` / same panel? | **Yes** — `PanelSettings.AttachAndInsertPanelComponentToVisualTree(IPanelComponent)` takes the interface, so it accepts either. |
| **Nesting across types** (a `PanelRenderer` under a `UIDocument`, or vice versa)? | **No.** `UIDocument.parentUI` is typed `UIDocument`; `PanelRenderer.parentUI` is typed `PanelRenderer`. Each only adopts its own kind. A mixed pair does not nest — both attach to the panel independently. |
| Portal from one host's tree into the other's? | Mechanically yes (a portal is just a re-parent), provided the target element is reachable. Cross-panel portaling renders in the *target's* panel — surprising, worth a docs note, not blocked. |
| Islands (`Ugui/Islands/UitkHostElement.cs`) | Create a `UIDocument` at runtime; still valid on 6.5 since `UIDocument` is fully supported. A PanelRenderer island would be a separate implementation, same sub-root pattern. |

**What `IPanelComponent` is still good for** — even though `GetRootVisualElement()` is inaccessible,
the interface is implemented by **both** `UIDocument` and `PanelRenderer` and exposes
`panelSettings`, `parentUI`, `sortingOrder` (**as `float`**, normalizing `Renderer`'s `int`),
`visualTreeAsset`, and the whole world-space property set. Use it for **configuration** (one code
path for panel settings / sorting / world-space props across both hosts); use the **callback** for
root acquisition. Gated `UNITY_6000_5_OR_NEWER`.

### 5.6 What else must change

- **HMR:** add a third leg to the host list — duplicated at `Editor/HMR/UitkxHmrController.cs:484-504`
  **and** `Editor/HMR/UitkxHmrDelegateSwapper.cs:141-166`. The HMR contract itself is a single
  `Func<IEnumerable<FiberNode>>` (`RefreshRuntime.cs:860`) that only needs `FiberRoot`/root-`FiberNode`
  **object identity** to stay stable — which rebuild breaks by design, so HMR must tolerate a
  re-registered root.
- **Promote retarget to a first-class primitive:** `VNodeHostRenderer.RetargetHost` is `internal` and
  `VisualElement`-typed (`:55`), so no other assembly can call it. Widen to `object` + public.
  **It has zero test coverage today** — add tests.
- **`Ugui/Islands/UitkHostElement.cs:95-101,137,143`** creates a `UIDocument` at runtime; decide
  whether the island follows to `PanelRenderer` on 6.5.
- **`Hooks.UseUiDocumentRoot`** (`Shared/Core/Hooks.cs:1377`) — a second independent poll of the same
  property; needs a `PanelRenderer` sibling for portal targeting.

### 5.7 Known 6.5 bugs to design around

| ID | Issue | Status |
|---|---|---|
| UUM-146174 | Callback not triggered when re-enabling the owning GameObject | fixed **6000.5.4f1** (our 6.5.6f1 is clear) |
| UUM-142211 | `root.panel` may be null when the callback triggers | fixed 6000.5.0b11 |
| UUM-139973 / UUM-139975 | Callback fires twice entering play mode / accumulates code-created content | fixed 6000.5.0b9 — **note 139975 is Unity endorsing code-built trees by fixing it** |
| **UUM-147875** 🔴 | **Root not inserted if the component is enabled after being disabled in `Awake()`** — i.e. the deferred-mount pattern | **fix in 6000.5.7f1, UNSHIPPED.** Workaround: toggle the *component*, not the GameObject |
| UUM-148452 | Nested-renderer release cascade → "can't modify after resources released" | **OPEN** |

### 5.7.1 What we can actually do about the two live ones

**Reachability first (Cecil, 6000.5.6f1).** `IPanelComponent` is public, but its useful recovery
methods are **`internal`**, and `[InternalsVisibleTo]` on `UnityEngine.UIElementsModule` grants only
Unity's own modules and test assemblies:

| Member | Accessibility | Usable by us |
|---|---|---|
| `HandleLiveReload()` | **internal** | ❌ |
| `SetComponentEnabled(bool)` | **internal** | ❌ |
| `GetRoot()` / `GetRootVisualElement()` | **internal** | ❌ |
| `panelSettings` **get + set** | **public** | ✅ |
| `PerformUpdate()` | **public** | ✅ |
| `PerformValidation(bool)` | **public** | ✅ |

`PanelRenderer.InitRootVisualElement(bool)` and `AddRootVisualElementToTree()` are private. So the
direct "just force a reload" fix is closed — but the public surface still gives us a lever.

**UUM-147875 — workaround: symptom-gated self-heal.** The failure is *silently no root*, and our host
is the one component that can tell: it knows it is enabled, has a `panelSettings`, and has received
no callback. That is a detectable state, not a guess.

```
on enable, if panelSettings != null and no callback has arrived after N frames:
    // the panelSettings setter is one of the four attach/release paths (5.1),
    // so round-tripping it forces a detach + re-attach and inserts the root.
    var ps = panelRenderer.panelSettings;
    panelRenderer.panelSettings = null;
    panelRenderer.panelSettings = ps;
```

Three properties make this safe:
- **It costs nothing when the bug is absent** — the recovery only runs when no callback arrived, so
  on 6000.5.7f1+ it never fires. **Gate on the symptom, not the version** — no `Application.unityVersion`
  parsing, and it stays correct if Unity backports the fix.
- **It cannot destroy anything.** The `panelSettings` setter releases the current tree — but in this
  scenario there is no tree; that is the bug. Nothing to lose.
- Fallback ladder if the round-trip proves insufficient: `PerformUpdate()`, then
  `PerformValidation(true)`, then toggling `panelRenderer.enabled`.

**✅ VERIFIED END-TO-END (2026-08-01, 6000.5.6f1)** with `PanelRendererDeferredEnableTest.cs`, which
disables the renderer in `Awake()` and re-enables it a second later — the real-world
"disable all screens at startup, enable one on demand" pattern:

```
[147875] Awake: disabled the PanelRenderer (the pattern under test)
[147875] re-enabling the PanelRenderer now
[147875] ===== REPRODUCED: no callback 2s after enabling. The UI would silently never appear. =====
[147875] applying workaround: panelSettings round-trip
[147875] CALLBACK #1 version=3 - content added
[147875] ===== WORKAROUND WORKS: callback arrived after the round-trip =====
```

So the bug is **real on 6000.5.6f1**, it silently produces no UI, and the symptom-gated round-trip
**does** fix this specific bug — not merely the mechanism in general, which was the earlier gap.
Ship the self-heal; it becomes inert on 6000.5.7f1+ because the callback arrives and the symptom
gate never fires.

**UUM-148452 — REPRODUCED, and we now have both a prevention and a repair.** See §5.8.7 for the
measured ladder and the decisions (N2 prevention always on; N6 repair on by default, opt-out). The
storm itself is confirmed in §5.8.6 finding 22. Still **OPEN upstream** — no fix in any version — so
the workaround ships and is documented as version-scoped. The rest of this entry predates that work:

1. **Never create a nested `PanelRenderer` ourselves.** `parentUI` is auto-discovered via
   `GetComponentsInParent<PanelRenderer>` — nesting is purely a *GameObject hierarchy* property.
   A `PanelRenderer` island placed on a GameObject that is not under another `PanelRenderer` cannot
   be adopted, which defeats the cascade entirely. Fully in our control.
2. **Warn when a user nests them** — `parentUI != null` is public and cheap to check at mount.
3. **Degrade to remount, not to a broken tree.** Whatever Unity throws mid-cascade, our next callback
   still arrives and the three-way branch (§5.2) sees `resourcesReleased == true` and remounts. This
   holds **only if our own code never touches a released element** — which is exactly what `IsAlive`
   (step 8) guarantees. The cleanup is therefore the mitigation for this bug too.

**MEASURED since this was written** - reproduced, root-caused and worked around; see 5.8.6 f22, 5.8.7 and the WA3/WA4 registry rows in 5.9. The original note read: reproduce it in
Phase 2 before relying on the reasoning above.

**Upstream: report, don't patch.** We cannot contribute a fix. `UnityCsReference` is **reference-only**
— Unity does not accept pull requests, and the licence forbids modifying or redistributing the code;
UI Toolkit is a built-in engine module, not an open UPM package. The supported channel is the Unity
Bug Reporter, where **repro quality drives prioritisation**. Since Phase 2 step 13 reproduces this
bug anyway to validate the mitigation, extend `PanelRendererReloadProbe.cs` to two **nested**
PanelRenderers and, if it reproduces, submit that project against UUM-148452 and vote on the tracker
entry. The Asset Store publisher angle ("blocks a published package") is legitimate leverage, and
UITK staff were demonstrably responsive during the 6.5 beta (UUM-139973 / 142211 / 146174 all fixed
in-cycle). **This never replaces the mitigation** — any fix lands in a future patch, and every user
on an earlier one still needs `IsAlive` + no-self-nesting.

**Both bugs are editor-triggered in practice.** The mount bug is confirmed editor-only (§5.8.8 finding 27); the release path, however, DOES occur in a player build via the public `panelSettings` setter (finding 28).

### 5.8 Empirical spikes — ✅ **COMPLETE. Gate is GREEN; Phase 2 is unblocked.**

All spikes were run on `6000.5.6f1` on 2026-08-01 with the harness in §5.8.1. Results and the
findings they produced are in §5.8.2 (run 1), §5.8.3 (run 2), §5.8.4 (run 3), §5.8.5 (run 4).
Findings 1–13 are authoritative and supersede any inference elsewhere in this document.

**Spike 1 — 🟢 RESOLVED. Answer: NO — an unrelated `.uxml` save cannot reach us.**
Established from IL (below) and **confirmed in-editor on 6000.5.6f1, run twice, zero output**
(§5.8.3 finding 6). Phase 2 is unblocked.

The scare was real but stops one layer down. `PanelRenderer.SetupVisualTreeAssetTracker()` registers
the tracker **unconditionally** — it checks `rootVisualElement != null` and `panelSettings != null`,
never `visualTreeAsset != null`. However the tracker it registers is
`UnityEditor.UIElements.PanelComponentVisualTreeAssetTracker`, whose base
`BaseLiveReloadAssetTracker<VisualTreeAsset>` gates on its own tracked set:

```csharp
bool OnAssetsImported(HashSet<VisualTreeAsset> changed, HashSet<string> deleted) {
    if (m_TrackedAssets.Count == 0) return false;          // first instruction
    ...
}
bool ProcessChangedAssets(HashSet<VisualTreeAsset> changed) {
    foreach (var a in changed)
        if (m_TrackedAssets.ContainsKey(a.GetEntityId())) return true;
    return false;                                          // not mine -> ignored
}
```

`m_TrackedAssets` is populated only by tracking a real VTA. **Null VTA → empty dict → early return →
`OnVisualTreeAssetChanged` never fires → `HandleLiveReload` never runs.** And it is keyed by entity
ID, so even a PanelRenderer *with* a VTA ignores every other `.uxml` in the project. Neither
mitigation candidate (dummy VTA / detach-on-entry) is needed.

**Residual risk, unchanged:** `HandleLiveReload` is unconditional *once called* —
`if (rootVisualElement == null) return; InitRootVisualElement(true); AddRootVisualElementToTree();` —
so any other caller (live-reload toggle, domain reload, disable/enable, native paths) still replaces
the root. That is the "recompile + two editor toggles" tier, which the design already handles via
deferred mount + `RetargetContainer`.

**Still confirm in-editor** (IL cannot see native callers, and cannot tell whether children come
back merely detached or *released*): drop `PanelRendererReloadProbe.cs` (spike scratch file) on a
`PanelRenderer` with an empty Source Asset and run scenarios A–E in §5.8.1.
2. ✅ **Does toggling Live Reload release a null-VTA tree?** **YES** — finding 10.
3. ✅ **`m_UIVersion` across domain reload** — **resets**; counts reloads within a session only.
   Not usable as a stale-detector — finding 11.
4. ⬜ **Ordering of the first callback vs `Start`/first `Update`** — not measured. Low risk: the
   deferred-mount + replay design (§5.3.3) is order-independent by construction.
5. ⬜ **Nested-renderer front-insertion** — not measured. Does not gate anything: §5.3.1 mandates the
   sub-root regardless, which makes insertion order moot.
6. ✅ **Is a released element safe to merely *read*?** **NO** — reading `resolvedStyle` throws
   `NullReferenceException`; re-inserting throws `InvalidOperationException` — finding 9.
7. ⬜ **UUM-147875 blast radius** for the deferred-enable pattern — not measured; fix lands in
   6000.5.7f1. Revisit during Phase 2 implementation, workaround is to toggle the component rather
   than the GameObject (§5.7).

Items 4, 5 and 7 are the only unmeasured questions left. None of them gate Phase 2 — 4 and 5 are
neutralized by design choices already made, and 7 has a known workaround and an upstream fix.

### 5.8.1 Spike procedure (probe harness)

Scratch file `PanelRendererReloadProbe.cs` — deliberately depends on **nothing from this library**,
so it measures Unity's behaviour rather than confounding it with our reconciler. It registers the
versioned reload callback, builds a sub-root with a state-carrying marker child, reports the
*previous* sub-root's `parent`/`resourcesReleased`/touch-throws on every callback, and runs a 4 Hz
watchdog that fires only on state change — which is what catches destruction that never routes
through the callback at all.

**Setup.** 6000.5.6f1 project → drop the file in `Assets/` → `PanelSettings` asset (Create > UI
Toolkit > Panel Settings Asset) → empty GameObject → Add Component > UI Toolkit > Panel Renderer →
assign the PanelSettings and **leave Source Asset EMPTY** → add the probe. Enter play mode; expect
`CALLBACK #1` then `built sub-root generation=1`.

| # | Scenario | What it settles | Predicted |
|---|---|---|---|
| **A** | Create `Assets/Unrelated.uxml`, reference it from nothing, then edit+save / Reimport / delete it — in play mode and in edit mode | **The gate.** Any `CALLBACK` or `WATCHDOG` line = the tracker is global and we need a mitigation | **No output** (per the IL above) |
| **B** | Toggle the editor's UI Toolkit Live Reload option | Whether the toggle path releases a null-VTA tree; whether the previous sub-root returns `resourcesReleased=true` | Callback fires, NEW root object |
| **C** | Edit any `.cs` and let it recompile while in play mode | Domain-reload behaviour; whether `version` resets (spike 3) | Callback fires, `version` back to 1 |
| **D** | Disable then re-enable the PanelRenderer component | Enable-cycle release semantics | Callback fires |
| **E** | Context menu → *force ReleaseResources on sub-root* | **Calibration.** Establishes what a poisoned element actually reads like, so B/C/D readings are interpretable | `touchThrows=YES` |

Run **E first** — without it, a `released=false` in B/C/D is ambiguous between "not released" and
"the probe cannot detect release."

**Reading the results.** A clean = gate green, proceed. B/C/D showing `released=true` on the previous
sub-root promotes the retention sites in the todo map (`PropsApplier`'s static element dictionary,
the four row pools, `Animator`'s captured `ve`) from *memory leak* to *throws on next touch* — which
is what §5.4 has to neutralize before Phase 2 ships.

### 5.8.2 Spike results — measured on 6000.5.6f1 (2026-08-01, run 1)

Empty URP project, `PanelRenderer` with **no** Source Asset, probe harness attached.

1. **Edit mode works — corrects a prediction in this plan.** `CALLBACK #1 [EDIT] version=1`,
   dispatched from `UIElementsRuntimeUtilityNative.UpdatePanels()`. `PanelRenderer` has no
   `[ExecuteAlways]` (unlike `UIDocument`), but the *native* panel updater drives it in the editor
   anyway, and the reload callback is delivered. A PanelRenderer-hosted tree therefore renders
   outside play mode, so the editor/HMR story is not degraded relative to `UIDocument`.

2. **`ReleaseResources()` has an undocumented precondition:**
   `InvalidOperationException: Cannot release resources while the VisualElement is still in the
   hierarchy`. The element must be detached first. Consequence for §5.4: **Unity cannot release an
   element out from under us while it is still parented** — release is necessarily preceded by a
   detach we can observe. That is a meaningfully weaker hazard than assumed.

3. **Disable → re-enable does NOT tear down our subtree.**
   `CALLBACK #3 SAME root object rootChildCount=1`, previous sub-root `parented=True
   resourcesReleased=False markerReleased=False markerUserData=gen2 touchThrows=no`. Unity reused
   the same root and left our programmatic children untouched, with state intact.
   → **The host must be idempotent on callback**: if our sub-root is still parented to the incoming
   root, reuse it (no rebuild, no `RetargetContainer`); only rebuild when it is genuinely orphaned.
   The first probe revision rebuilt blindly and visibly stacked generations 1..4 in the Game view —
   that stacking is exactly the bug a naive host implementation would ship.

4. **`version` is always 1**, including on callbacks #2 and #3 with a NEW root object. It is not a
   usable staleness discriminator; the design must not depend on it. (Spike 3 answered: not
   comparable, and not even monotonic within a session.)

5. **Root object identity is not a reliable rebuild signal either** — #2 reported NEW root, #3
   reported SAME root. Attachment state of our own sub-root is the only trustworthy check.

### 5.8.3 Spike results — run 2. **🟢 THE GATE IS GREEN (confirmed in-editor).**

6. **Scenario A produced zero log output, run twice.** Creating `Unrelated.uxml`, editing it in UI
   Builder and saving, reimporting it, and deleting it — all while a null-VTA `PanelRenderer` tree
   was live — produced **no callback and no watchdog line**. The IL analysis in §5.8 is confirmed
   empirically: **an unrelated `.uxml` save cannot reach a code-only PanelRenderer tree.** No
   mitigation needed; Phase 2 is unblocked.

7. **`ReleaseResources()` has a SECOND undocumented precondition:**
   `InvalidOperationException: Cannot release resources while the VisualElement has children`.
   Combined with finding 2, the API is **detached-only AND leaf-only**. It cannot release a subtree.

8. **The only subtree-release path is `Clear(VisualElementClearOptions)`** —
   `None = 0, Recursive = 1, RecursiveReleaseResources = 3`. So poisoning a whole tree requires an
   explicit, opt-in `Clear(RecursiveReleaseResources)` call. This narrows §5.4 substantially: the
   hazard is one specific call with one specific argument, not an ambient property of 6.5. The
   remaining question is whether Unity itself ever issues it on a path we sit on — and since the
   live-reload path provably never fires for a null VTA (finding 6), the frequent path is clear.

### 5.8.4 Spike results — run 3. **Calibration valid. The hazard is REAL.**

9. **Calibration succeeded — a released element is detectable and genuinely poisoned:**
   ```
   LEAF before:        released=False touchThrows=no
   LEAF after release: released=True  touchThrows=YES (NullReferenceException) userData=gen1
   re-adding a RELEASED leaf THREW: InvalidOperationException:
     "You can't insert a VisualElement after its resources are released. This usually happens
      when PanelRenderer releases elements during UI reload or cleanup. Make sure that you don't
      hold stale references to elements."
   ```
   `resourcesReleased` is a **reliable, cheap, public** liveness probe. Reading `resolvedStyle`
   throws `NullReferenceException`; re-inserting throws `InvalidOperationException`. Note Unity's own
   message names this exact scenario and prescribes exactly our mitigation.

10. **🔴 The live-reload TOGGLE releases our whole programmatic subtree.** Toggling
    *Console/Game ⋮ → UI Toolkit Live Reload* off and on, on a **null-VTA** PanelRenderer:
    ```
    CALLBACK #2 [PLAY] version=2 NEW root object      <- UnityEditor.GenericMenu:CatchMenu
      previous sub-root: parented=False resourcesReleased=True markerReleased=True
                         touchThrows=YES (NullReferenceException)
      ORPHANED - sub-root generation=2 is not under this root; rebuilding
    ```
    Both the sub-root **and its child** were released. So:
    - **Release DOES descend into programmatically-added children** — the open question from the
      retention map is answered YES.
    - The asset-import gate (finding 6) does **not** cover this path. `OnLiveReloadOptionChanged`
      is a separate, unconditional entry point that reaches null-VTA components.
    - **This trigger is editor-only** (live reload does not exist in a build), but the release path itself is NOT — see §5.8.8 finding 28. Via this trigger it is a DX concern,
      not a shipped-runtime correctness concern.

11. **Correction to finding 4 — `version` IS meaningful, within a session.** It incremented 1→2→3
    across genuine reloads in run 3, while staying 1 across the disable/enable and domain-reload
    events of run 1. So `version` counts *UI reloads*, resets on domain reload, and is usable as a
    same-session generation signal — but never comparable across a domain reload.

12. **`Clear(RecursiveReleaseResources)` does not release the element it is called on**, only its
    children (`subRootReleased=False` after the call). Our own `Clear` usage is therefore safe for
    the container; only the children it evicts are poisoned.

**Design consequence — `RetargetContainer` cannot be used on the release path.** Retarget works by
moving the *existing* host elements into the new container, and re-inserting a released element
throws. So the two rebuild paths are genuinely different:

| Trigger | Old children state | Correct response |
|---|---|---|
| Sub-root survived, still parented | live | **reuse in place** — no retarget, no rebuild (finding 3) |
| Sub-root orphaned but **not** released | live | `RetargetContainer` — preserves fiber/hook state |
| Sub-root **released** (live-reload toggle in-editor; `panelSettings` reassignment anywhere) | poisoned | **full remount** — host elements must be recreated |

Deciding between them is a single `resourcesReleased` check on our retained sub-root, which finding 9
proves is reliable.

### 5.8.5 Spike results — run 4. **How often the release path actually fires.**

13. **With a Source Asset assigned, EVERY save of that `.uxml` releases our subtree.** Measured:
    three consecutive saves produced `CALLBACK #2/#3/#4` (`version=2/3/4`), each reporting the
    previous sub-root as `resourcesReleased=True markerReleased=True touchThrows=YES`. The new root
    arrives with `rootChildCount=1` — Unity has already instantiated the VTA into it.

**Trigger list for the release path — EDITOR triggers only, NOT exhaustive (see §5.8.8 finding 28):**

| Trigger | Releases? | Frequency | Basis |
|---|---|---|---|
| Save a `.uxml` assigned as that PanelRenderer's **Source Asset** | **Yes** | **every save** | measured, run 4 |
| Toggle *UI Toolkit Live Reload* | **Yes** | rare, manual | measured, run 3 |
| Save an **unrelated** `.uxml` | No | — | measured twice, run 2 |
| Disable / re-enable the component | No | — | measured, run 1 |
| Domain reload / recompile | Moot | — | managed refs are wiped too; nothing stale survives to be touched |
| Assigning `panelSettings` at **runtime in a player build** | **Yes** | user-code dependent | ⚠️ measured in a build, §5.8.8 finding 28 |
| Live-reload / uxml-save paths in a player build | Never | — | live reload does not exist outside the editor |

> ⚠️ **This table lists *editor* triggers and is NOT exhaustive.** `panelSettings` is a public runtime
> setter, so the release path exists in shipped builds too — see §5.8.8 finding 28, which corrects an
> earlier "neither can affect a player build" claim made here.

**Revised recommendation.** Ship the **simple remount**, for two reasons: a UXML save means the user
changed their layout, so a full rebuild is the semantically expected outcome and matches Unity's own
live-reload model; and the mixed UXML+code setup is opt-in, not the mainstream path for a code-first
library. Rehydrate stays a follow-up.

**But finding 13 promotes §5.4 from optional hardening to a Phase-2 blocker.** The remount path is
now known to be reachable on a tight loop, not once in a blue moon, so every retained reference must
be dropped on remount or the mixed workflow throws within a few saves. Specifically required before
Phase 2 ships: `PropsApplier`'s static element-keyed dictionary, the four row pools, `Animator`'s
captured element in the tick lambda, and user `Ref`s (which, unlike uGUI, are never detached on
unmount today). Additionally, the host should detect `visualTreeAsset != null` at mount and emit a
one-time explanatory warning that editing that asset will remount the tree and drop transient state.

---

### 5.8.6 Spike results — run 6. ✅ **VERIFIED** under a clean one-question-per-test protocol.

Four isolated tests, console cleared between each, on 6000.5.6f1. Setup: `UIParent` (PanelRenderer +
probe, PanelSettings assigned, Source Asset empty) with `UIChild` as a GameObject child (same, and
`parentUI` confirmed resolving to `UIParent`).

14. **✅ The UUM-147875 workaround is CONFIRMED on a top-level renderer.** A `panelSettings`
    round-trip forces a genuine rebuild: old tree released, new root, callback delivered, remount.
    ```
    >>> round-trip completed without throwing
    >>> immediately after: subRootParented=False subRootReleased=True
    CALLBACK #2 version=3 NEW root object  ->  ORPHANED  ->  generation=2
    ```
    Detail: `version` jumps **1 → 3**, so the round-trip costs *two* rebuilds (set-null, set-back).
    Acceptable for a recovery that only runs when nothing is mounted.

15. **🔴 CONFIRMED (after being wrongly retracted) — a nested child `PanelRenderer` never receives
    its reload callback in play mode.** Reproduced in a **clean minimal project** with setup verified
    in-log:
    ```
    [REPRO:UIChild]  OnEnable | panelSettings=New Panel Settings | sourceAsset=empty | parentUI=NESTED under 'UIParent'
    [REPRO:UIParent] callback #1 - added content
    [REPRO:UIChild]  NO CALLBACK 3s after start - this renderer never mounted. parentUI=NESTED
    [REPRO:UIParent] mounted OK after 1 callback(s)
    ```
    **Why it was retracted, and why the retraction was wrong:** the probe harness carries
    `[ExecuteAlways]`, so its child registered and mounted in **edit** mode first, which primes it and
    makes the play-mode callback arrive. The minimal repro has no `[ExecuteAlways]`, so play mode is
    the child's first chance — and it never gets one. Tests 3 and 5 were right all along; the
    "contradicting" evidence (Test 7, Recovery) came from a confounded harness.

    **The workaround is verified for this case too** — the `panelSettings` round-trip mounts it:
    ```
    [REPRO:UIChild] NO CALLBACK 3s after start - this renderer never mounted
    [REPRO:UIChild] round-trip issued (callbacks before=0)
    [REPRO:UIChild] callback #1 - added content   ->   callbacks=1 content=alive
    ```

    **Design consequence: the mount watchdog IS required**, not merely nice-to-have. A callback-only
    host on a nested child never mounts at all in a player-facing scenario. This is separate from,
    and more basic than, the release/cascade problem in §5.8.7 — it happens on a first, clean mount
    with nothing released. The same round-trip primitive serves all three cases (this, UUM-147875,
    and the unmounted branch of §5.8.7), so it is one mechanism, gated on the symptom.

16. **⚠️ CORRECTED by finding 20 — the recovery IS available on nested children.** Test 4 showed
    `panelSettings round-trip -> setterTookEffect=False`: the child's `panelSettings` is parent-owned
    (*"The PanelSettings asset is set by the Parent and cannot be directly changed"*) and the assigned
    value does **not** change. I read that as "recovery unavailable." **That was wrong** — see
    finding 20. The value is ignored, but the setter still runs its attach/detach side effect.
    `setterTookEffect` measures the wrong thing; only the resulting callback counts.

17. **🔴🔴 THE BIG ONE — a parent rebuild silently kills a nested child's tree.** Test 2, edit mode,
    parent `panelSettings` round-trip:
    ```
    [UIChild]  WATCHDOG state change WITHOUT a callback: parented True -> False, released False -> True
    [UIParent] CALLBACK #2 -> ORPHANED -> rebuilt generation=2
    [UIChild]  manual probe: callbacks=1 (UNCHANGED) parented=False resourcesReleased=True
               markerReleased=True touchThrows=YES
    ```
    The child's programmatic subtree **was released**, and **the child received no callback** —
    `callbacks` stayed at 1. The parent recovered; the child is permanently, silently dead.
    `cascadeErrorsSoFar=0`: **no exception was thrown.** So this is *not* UUM-148452's signature —
    it is quieter and worse. UUM-148452 at least announces itself; this fails silently.

18. **Recovery IS possible — the lever is not yet isolated.** Test 4's
    `### NO LEVER RECOVERED IT` was a **false negative in the harness**: the reload callback is
    delivered asynchronously on a later `UpdatePanels`, and the ladder judged each lever
    synchronously. Immediately after it gave up, `CALLBACK #1 version=3` arrived and the child
    mounted (confirmed visually). The ladder is now frame-aware; **which** lever works is the one
    open question.

19. **Nested roots insert at the FRONT, and ordering is unstable.** Observed across the screenshots:
    in edit mode the parent's content renders above the child's; after play-mode recovery the child's
    content renders **above** the parent's. This confirms the previously-unmeasured front-insertion
    behaviour (`firstChildInsertIndex = 0`) and shows the resulting z/document order depends on
    *when* each renderer mounted. Our sub-root does not control where Unity places a nested child's
    root — a docs note at minimum.

20. **✅ THE FIX — the `panelSettings` round-trip recovers a nested child too.** Test 5, frame-aware
    ladder, first lever:
    ```
    ###   trying panelSettings round-trip -> setterTookEffect=False
    CALLBACK #1 [PLAY] version=3 rootHash=788638308 (first)
    built sub-root generation=1
    ### >>> RECOVERED BY: panelSettings round-trip <<<  (after 1 callback(s))
    ```
    Confirmed visually — the child reappeared. So **one lever recovers both configurations**:
    top-level (finding 14) and nested (here). The other three levers are never needed. Note the
    mechanism: assigning the property is rejected, but the setter still executes the attach path,
    which constructs and inserts the root and fires the callback. We use a public API in a legal way
    (assigning a property its current value) and rely on Unity's side effect — worth a comment at the
    call site so it is not "optimised away" later as a no-op.

21. **🔴 Death RECURS — the watchdog must be permanent, not one-shot.** Test 6: after the child had
    been recovered, forcing another parent rebuild killed it again —
    `WATCHDOG parented True -> False, released False -> True`, and the manual probe reported
    `callbacks=1` **unchanged**, `resourcesReleased=True`, `touchThrows=YES`. It did not come back on
    screen. Every parent rebuild re-kills a nested child, and the child never self-recovers.

22. **🔴 UUM-148452 REPRODUCED — an infinite per-frame exception storm inside Unity.** After a parent
    rebuild releases a nested child's root, Unity throws this **every frame, forever** (670 copies in
    one 11-second capture, 86 in another):
    ```
    InvalidOperationException: You can't modify a VisualElement after its resources are released
      InlineStyleAccess.IStyle.set_position
      PanelRenderer.SetupRootClassList()
      PanelRenderer.InitRootVisualElement(bool visualTreeAssetChanged)
      PanelRenderer.RefreshAssets()
      UIElementsRuntimeUtility.PreUpdatePanelRenderers()
      UIElementsRuntimeUtility.UpdatePanels()
    ```
    **No user code appears in the stack.** The mechanism is the per-frame style write documented in
    §5.1: Unity releases the child's root, then its own `SetupRootClassList` writes `style.position`
    to that released root on the next frame and every frame after. Only a **domain reload** was
    observed to clear it.

23. **The heal works — but only for the unmounted state, not the released state.** Two distinct
    conditions were conflated by a single `IsDead()` predicate:

    | Child state | `panelSettings` round-trip | Evidence |
    |---|---|---|
    | Unmounted / detached, **not** released | ✅ **recovers** | `Recovery`: attempt 13 → `AUTO-HEAL SUCCEEDED`, **zero** storm errors in that log |
    | **Released**, storm active | ❌ throws every attempt | `Test8` + `Errors`: 13 attempts, 13 throws, storm present |

    An earlier write-up here claimed the heal "never works." **That was wrong** — it was based on
    grepping partial captures that happened to contain only storm-state attempts.

### 5.8.7 Nested renderers — the recovery ladder. **FINAL, and the basis for the design.**

Seven candidate recoveries, each tested from a clean state (a known-good reset between levers).
**Reproduced identically across three runs** on 6000.5.6f1.

| Lever | What it does | Result |
|---|---|---|
| N1 | slow `panelSettings` round-trip (null, wait 30 frames, restore) | fails |
| **N2** | **PREVENTION: disable child renderer → rebuild parent → re-enable** | **✅ WORKS — destroys nothing** |
| N3 | re-parent the child out of the nest and back | fails |
| N4 | destroy + re-add **only the parent's** renderer | fails |
| N5 | the N2 pattern applied as a *cure* after the damage | fails |
| **N6** | **destroy + re-add only the CHILD's renderer** | **✅ WORKS — minimal repair** |
| W3 | destroy + re-add **both** renderers | works, but **obsolete** — N6 is strictly less invasive |

24. **One model explains every result: the stuck state is in the CHILD's renderer, and it must be
    REMOVED, not supplemented.** N4 replaced the parent but left the poisoned child registered →
    fails. The earlier L4 added a *brand-new* nested renderer while the poisoned one was still
    attached → fails. W3 removed it (plus the parent) → works. N6 removes just it → works.
    **This retracts the earlier "the poison is in the parent" claim, which was exactly backwards.**

25. **Timing beats technique.** N5 applied N2's exact pattern *after* the damage and failed. Once a
    child is released, gentleness no longer works — only removing the component does.

26. **W3 only ever looked necessary because of a harness bug.** The child-only lever (originally L3)
    was silently blocked by `[RequireComponent(typeof(PanelRenderer))]` on the probe, so we climbed
    to the heaviest rung without testing the one below it. Removing the attribute exposed N6.

**DECISIONS (owner, 2026-08-01):**

- **D1 — Prevention (N2) is always on.** Every rebuild the library itself triggers disables nested
  child renderers first, rebuilds, then re-enables. Non-destructive; closes the entire class of
  failure we cause.
- **D2 — Repair (N6) is ON BY DEFAULT, opt-out.** Not opt-in. For rebuilds *Unity* triggers we get
  no warning, so the host repairs on the next parent callback by destroying and re-adding **only the
  nested child's** renderer. The parent — the component users actually wire into Inspector fields —
  is never touched. **W3 is not implemented.**
- **D3 — Copy everything we can.** The N6 *test* copied nothing, which is why it mangled the scratch
  scene; the implementation must carry over every setting: `visualTreeAsset`, `sortingOrder`,
  `position`, `pivot`, `pivotReferenceSize`, `worldSpaceSizeMode`, `worldSpaceSize`. (`panelSettings`
  does not apply — on a nested child it is parent-owned and read-only.) In the editor prefer a full
  serialized copy (`ComponentUtility.CopyComponent`/`PasteComponentValues` or a `SerializedObject`
  walk) so private serialized fields survive too. Use `Undo.DestroyObjectImmediate` /
  `Undo.AddComponent` in edit mode so the repair is a single Ctrl+Z and not a silent scene rewrite.
- **D4 — Document it as a temporary, version-scoped workaround** on the docs site and in the
  changelog: what it does, why, that it is specific to Unity 6000.5.x, and how to opt out.
- **D5 — Push upstream.** File/vote on the Unity issue; remove the workaround when it is fixed.

**What survives a repair, and what cannot:**

| | Preserved |
|---|---|
| All renderer settings | ✅ with D3's copy |
| Rendering/behaviour after repair | ✅ measured |
| Serialized references **to** the child's renderer (`[SerializeField]`, UnityEvent targets) | ❌ **unavoidable** — Unity has no replace-in-place API; the new component is a new object |
| Component order in the Inspector | ❌ cosmetic |

Residual exposure: a user who **both** nests PanelRenderers **and** holds a serialized reference to
the nested child specifically. Narrow enough to justify default-on; D2's opt-out covers it.

### 5.8.8 Built-player results — **ANSWERED, and it corrects an earlier claim.**

Ran `BuildModeSelfTest.cs` (self-driving, IMGUI overlay + `Player.log`) in a **Windows standalone
build** of 6000.5.6f1. Five tests, all conclusive:

| | Question | Result |
|---|---|---|
| **T1** | Does a nested child mount unaided in a build? | **YES** — `parent=1 child=1 callbacks` |
| T2 | (workaround needed?) | skipped — child mounted unaided |
| **T3** | Does N2 prevention hold in a build? | **YES** — `parent=alive child=alive` |
| **T4** | Can an unprotected parent rebuild kill the child in a build? | **YES — `released=True`** |
| **T5** | Does N6 repair work in a build? | **YES** |

27. **✅ The mount bug (case IN-150082) is EDITOR-ONLY.** T1: a nested child mounts unaided in a
    built player. Shipped games are unaffected, which retroactively validates filing it as
    "A problem with the Editor".

28. **🔴 CORRECTION — the release/poisoning is NOT editor-only.** T4 reproduces it in a build.
    §5.8.5's trigger table listed only editor events (uxml save, live-reload toggle) and concluded
    "every release trigger is editor-only... neither can affect a player build." **That was wrong.**
    `panelSettings` is a **public runtime setter**, so any code that reassigns it at runtime — a
    plausible thing to do when switching panel configurations — releases nested children in a
    shipped game. The trigger table was a list of *editor* triggers, not an exhaustive one.

29. **✅ Both of our strategies work in builds.** T3 (N2 prevention) and T5 (N6 repair) both succeed
    in a standalone player. Only 1 release exception appeared in the whole run, because the repair
    landed promptly rather than leaving the tree wedged.

**Design consequence:** the prevention and repair must be **active in builds, not gated to the
editor**. Only the mount watchdog is editor-relevant, and since it is symptom-gated it costs nothing
to leave enabled everywhere.

**We already have this exact pattern in-house.** `RootRenderer.cs:21-43` runs an editor-only poll of
`UIDocument.rootVisualElement` for UUM-127851 and calls `RetargetHost` on change. The PanelRenderer
watchdog is the same shape with a different probe and a wider run condition — so this is an extension
of an existing, proven defence rather than a new mechanism.

---

## 5.9 Workaround registry — what ships, how it is gated, when it comes out

Four workarounds ship for Unity bugs. They are **temporary by construction** and must not outlive
the bugs. This section is the single authority on their lifecycle.

### 5.9.1 The gating principle: gate on the SYMPTOM, not the version

**Do not use `#if UNITY_6000_5_OR_NEWER`-style version gates for these.** Three reasons, all learned
the hard way in this wave:

1. **Fixes land in patch releases, not minors.** UUM-147875 is fixed in **6000.5.7f1** — a version
   define cannot express "6000.5.0 through 6000.5.6".
2. **Unity backports.** A fix can appear in an older stream at any time; a version range hard-coded
   today is wrong tomorrow.
3. **Symptom gating self-retires.** If the callback arrives, the mount watchdog never fires. If the
   sub-root is never released, the repair never runs. On a fixed editor the code is **inert with no
   change on our side** — no version parsing, no release needed to turn it off.

Symptom gating is also what we *measured*: the deferred-enable self-heal was verified by observing
the symptom (no callback in 2s) and then confirming recovery — §5.7.1.

**The one exception is N2 prevention**, which by definition acts *before* the damage, so there is no
symptom to observe. It is cheap (one frame with the child renderer disabled, only around rebuilds we
ourselves trigger) and is therefore **always on behind its opt-out flag**. If we later want it to
self-retire too, the mechanism is a one-shot calibration: allow the first rebuild to run unprotected,
observe whether the child is released, and enable prevention only if it was.

### 5.9.2 The registry

| # | Workaround | Fixes | Upstream | Affected | Gate | Opt-out flag | Remove when |
|---|---|---|---|---|---|---|---|
| WA1 | **Mount watchdog** — `panelSettings` round-trip when no callback arrives | nested child never mounts | **case IN-150082** (filed 2026-08-01) | 6000.5.x **editor only** (§5.8.8 f27) | symptom: no callback N frames after enable | `enableMountWatchdog` | fixed upstream **and** floor is past the fix |
| WA2 | **Deferred-enable self-heal** — *same round-trip, same code path as WA1* | `PanelRenderer` disabled in `Awake()` never inserts its root | **UUM-147875** | 6000.5.0 – 6000.5.6 (fix in **6000.5.7f1**) | symptom: identical to WA1 | `enableMountWatchdog` | floor ≥ 6000.5.7f1 |
| WA3 | **N2 prevention** — disable nested child renderers around rebuilds we trigger | nested release cascade | **UUM-148452** (open) | all 6.5, **editor AND player** (§5.8.8 f28) | none — always on (see 5.9.1) | `enableNestedPrevention` | fixed upstream and floor is past the fix |
| WA4 | **N6 repair** — destroy + re-add only the nested child's renderer, copying all settings | nested release cascade | **UUM-148452** (open) | all 6.5, **editor AND player** | symptom: `subRoot.resourcesReleased` | `enableNestedRepair` | fixed upstream and floor is past the fix |

**WA1 and WA2 are one mechanism with one flag.** Both are "the callback never arrived, force the attach
path". Do not implement them twice.

### 5.9.3 Code convention — every workaround site is greppable

Each site carries this header, so `grep -rn "WORKAROUND(" ` enumerates every one:

```csharp
// WORKAROUND(WA4, UUM-148452): destroy + re-add the nested child's PanelRenderer.
// AFFECTS:    Unity 6000.5.x, editor and player. Open upstream as of 2026-08-01.
// GATED BY:   subRoot.resourcesReleased - inert when the bug is absent.
// REMOVE WHEN: UUM-148452 is fixed AND package.json "unity" floor is past the fix.
// EVIDENCE:   Plans~/UNITY_6_5_SUPPORT_PLAN.md 5.8.7 (recovery ladder, N1-N6 measured).
// NOTE:       the panelSettings self-assignment is the MECHANISM, not dead code - see 5.8.7 f20.
```

### 5.9.4 Where each one must be recorded

- **`Plans~/REMAINING_WORK.md`** — one entry per workaround with what/why/trigger-to-revisit. Repo
  policy already requires this for any workaround that does not reach root cause.
- **Docs site** — a "Unity 6.5 known issues" page: what breaks, what we do about it, how to opt out,
  and the Unity case/issue ids so users can vote. Scoped to 6.5 so it can be deleted wholesale.
- **`CHANGELOG.md`** — the workarounds named, with issue ids.
- **This section** — the authority; keep the registry current when a fix ships.

### 5.9.5 Removal procedure

When a fix ships upstream: confirm the symptom no longer reproduces on the fixed version (the spike
harnesses in §5.8.1 and the build self-test still exist for exactly this), then remove the workaround
**only once `package.json`'s `unity` floor is past the fix** — not before, or users on older patches
lose it. Delete the docs page section, the `REMAINING_WORK.md` entry, and the registry row together.

---

## 6. Gating

### 6.1 Defines

Unity's documented rule: for version `X.Y.Z` it defines `UNITY_X`, `UNITY_X_Y`, `UNITY_X_Y_Z`, plus
`UNITY_X_Y_OR_NEWER` [web, [scripting symbol reference](https://docs.unity3d.com/6000.5/Documentation/Manual/scripting-symbol-reference.html)].
So `UNITY_6000_4_OR_NEWER` and `UNITY_6000_5_OR_NEWER` are correct and **auto-defined — no asmdef
`versionDefines` entry is needed** (all asmdefs currently have `"versionDefines": []` except
`Ugui/Tests`). `UNITY_6000_4_OR_NEWER` is confirmed in first-party Unity package source.

| Item | Gate |
|---|---|
| `GUIDField` props / adapter / factory / registry | `#if UNITY_6000_4_OR_NEWER` |
| `MaskField`, `Mask64Field` props / adapter / factory / registry | `#if UNITY_6000_5_OR_NEWER` |

Precedent: `UNITY_6000_3_OR_NEWER` is used across 15 files today. **No `6000_4` or `6000_5` gate
exists yet — this wave introduces the first of each.**

### 6.2 Data-driven gating (no `#if`)

- **Schema** — `ide-extensions~/grammar/uitkx-schema.json`: each new element gets
  `"sinceUnity": "6000.4"` / `"6000.5"`. **No element currently carries `sinceUnity`** (all predate
  the floor), so these are the first; verify the LSP's version-aware completion/diagnostic filtering
  actually honours it for *elements* (it is proven for `styleVersions`).
- **Docs** — `ReactiveUIToolkitDocs~/src/versionManifest.ts`, `ELEMENT_VERSIONS`. The file already
  ships the exact template in a comment: `CalendarPicker: { sinceUnity: '6000.5' },`.

### 6.3 Floor is unchanged

`package.json` stays `"unity": "6000.2"`. Everything here is additive behind gates. Do **not** touch
`publish.yml`'s `UNITY_VERSION: 6000.2.6f1` — that is the Asset Store compatibility floor and the
editor reviewers test on; `unity-license-check.yml` must stay in lockstep with it.

---

## 7. Documentation

1. **`versionManifest.ts` — `SUPPORTED_VERSIONS` currently lists only `6000.2` and `6000.3`.** Add
   **both** `6000.4` and `6000.5`, otherwise the site's version dropdown shows a hole. 6.4 has no
   UI-Toolkit-facing library changes beyond `GUIDField`, so its entry is a one-liner.
2. Three new component pages (§3.3, files 6–11).
3. `ELEMENT_VERSIONS` entries for all three controls.
4. **A short "Text generation in 6.5" note** — ATG is the default; `unityTextGenerator` /
   `-unity-text-generator` is the per-subtree opt-out; it is already supported in the typed `Style`
   surface. This is the highest-value doc addition in the wave because it is the change most likely
   to surprise a user upgrading.
5. Optional: a "Unity version support" page stating the floor, the supported ceiling, and which
   elements are gated.
6. **(Phase 2) A "Unity 6.5 known issues" page — required, not optional.** Scoped to 6.5 so it can be
   deleted wholesale when the fixes ship. Must cover, per §5.9:
   - **Nested `PanelRenderer`s**: what breaks, that we detect and repair it automatically, that the
     repair replaces the nested child's `PanelRenderer` component (so a serialized reference *to that
     child renderer* will not survive), and the opt-out flags.
   - **The Unity case/issue ids** (case **IN-150082**, **UUM-148452**, **UUM-147875**) with a link to
     the issue tracker so users can vote — that is the fastest route to these being fixed.
   - Which versions are affected, and that the workarounds go inert automatically on fixed versions
     because they are symptom-gated (§5.9.1) — users do not have to do anything.

---

## 8. Verification and release

**This section runs TWICE — once per phase.** Each phase is a complete release: gates, manual
verification, changelog, Discord entry, version bumps. Phase-specific notes are called out inline.

**Gates** (all must pass):

```powershell
dotnet test SourceGenerator~/Tests/Ruitk.SourceGenerator.Tests.csproj      # net10
git restore Analyzers/                                                     # SG tests clobber the committed DLLs
dotnet test "ide-extensions~/lsp-server/Tests/UitkxLanguageServer.Tests.csproj"  # net8
node scripts/check-machine-paths.mjs
node scripts/corpus-hash.mjs --check
cd ReactiveUIToolkitDocs~ && npm run build
```

**Manual, on a consuming project:**

- Open on **6000.2** (floor) — gated code compiles out, no new elements offered.
- Open on **6000.5.6f1** — *(Phase 1)* the three controls render, bind, and diff correctly.
- *(Phase 2 only)* Re-run the §5.8.1 spike scenarios against the **real host** rather than the probe:
  disable/enable must reuse in place; a Source-Asset save must remount cleanly with no throw; an
  unrelated `.uxml` save must still be a no-op; a live-reload toggle must remount cleanly. Watch
  specifically for `InvalidOperationException: You can't insert a VisualElement after its resources
  are released` — that exception is the signal that a retention site in §5.4 was missed.
- *(Phase 2 only)* **Nested-renderer matrix, in a BUILD as well as the editor** — §5.8.8 proved the
  release path exists in players, so editor-only verification is insufficient. The
  `BuildModeSelfTest.cs` harness already covers it: nested child mounts; N2 prevention holds; an
  unprotected rebuild kills the child; N6 repair restores it. All four must pass in a standalone
  build.
- *(Phase 2 only)* **Workaround inertness** — with each opt-out flag off, and on a Unity version where
  the symptom does not occur, confirm none of WA1–WA4 ever fires (§5.9).
- ATG comparison (§4.1) on 6.4 vs 6.5.

**If any emitter changed** (not expected in this wave): `scripts/build-generator.ps1` and re-commit
`Analyzers/*.dll`.

**Release surface** — required by repo policy and CI gates, and omitted from the
`add-unity-version` skill:

- `CHANGELOG.md` — the wave, framed as *"MaskField/Mask64Field became runtime-available in 6.5"*
  rather than "new controls" (§3.2).
- `ide-extensions~/changelog.json` via the `changelog` skill (+ regenerate + `verify`) **only if**
  the schema ships — the schema is an **embedded resource** in the LSP, so a schema change requires
  an IDE-extension rebuild and release to reach users.
- `plans/DISCORD_CHANGELOG.md` via the `discord-changelog` skill (ASCII, ≤2000 chars, prepend-only).
- Version bumps: `package.json`, `ide-extensions~/vscode/package.json`,
  `source.extension.vsixmanifest`, `ide-extensions~/rider/gradle.properties`.

**Record-keeping:** `Plans~/VERSIONING_PROCESS.md` §3.1 table / §3.3 tracker / §3.4 audit log, and
`Plans~/REMAINING_WORK.md`.

---

## 9. Runbook defects found while researching (fix separately)

The `add-unity-version` skill and `VERSIONING_PROCESS.md` understate the work. None of these bite
*this* wave (no IStyle changes), but all will bite the next one that has them:

1. **`TypedPropsApplier.cs` is missing from every checklist** — it is the hot typed path. Omitting it
   means a style property compiles, type-checks, and silently never applies.
2. **`Style.cs` is described as one edit; it is six** (backing field, pooled reset, `BIT_*` const,
   typed setter, `SetByKey`, `KeyToBit`).
3. **`IStyleCoverageTests.cs` needs a new version array** per IStyle-adding release, or six tests
   hard-fail in the same commit.
4. **The four-emitter alias-parity layer is absent from the skill** — SG (`CSharpEmitter`,
   `ExportsEmitter`, `HookEmitter`, `ModuleEmitter`), HMR (`HmrCSharpEmitter`, `HmrHookEmitter`), and
   `VirtualDocumentGenerator`. This shipped as a separate follow-up commit ~7 weeks after the 6.3
   wave because the first pass missed it. `SourceGenerator~/Tests/Unity63AliasEmissionTests.cs` is
   the template for pinning it.
5. **Phase 6 omits the entire release surface** (§8).
6. **The diff tool needs `-FromDll`/`-ToDll` pinning documented as mandatory**, and the runbook
   should state that release notes are unreliable for version attribution.

---

## 10. Order of work

**Done up front:**
- ~~Fix the diff tool~~ — §0.
- ~~Phase 2 spikes~~ — §5.8, gate green, findings 1–13.

### PHASE 1 — mechanical version support → **ship + publish**

1. `GUIDField` (6.4) — smallest; exercises the 12-file path and the first `UNITY_6000_4_OR_NEWER`
   gate end to end.
2. `MaskField` (6.5) — the `~0` "Everything" diffing subtlety lands here (§3.1).
3. `Mask64Field` (6.5) — mechanical after `MaskField`.
4. Confirm the existing `EnumFlagsField` wrapper still compiles on 6.5 (its base crossed
   assemblies — §3.2).
5. Docs: `SUPPORTED_VERSIONS` + 6.4 + 6.5, `ELEMENT_VERSIONS`, three component pages, the ATG note
   (§7). Audit the `unityDocLinks.ts` uGUI 404s (§3.3).
6. Gates (§8) + manual verification on the 6000.2 floor and on 6000.5.6f1.
7. Release surface (§8) — minor bump. **Push → owner PRs → CI → merge → fast-forward master.**

### PHASE 0 — a testable core → **ship + publish (0.14.1, patch)**

**Inserted after Phase 1 shipped (owner decision, 2026-08-02).** Its own branch and PR, landed and
verified *before* Phase 2 starts.

**Why it exists.** CI runs the SourceGenerator suite and the LSP suite. Neither compiles `Shared/`.
The only Unity test assembly is `Ugui/Tests`, which runs only inside the editor. So
`FiberReconciler`, `RetargetContainer`, `PropsApplier`, the row pools and `Animator` — **the entire
surface Phase 2 rewrites** — has no CI coverage at all. Phase 1 was safe because it was additive and
`SchemaRegistryParityTests` caught the one cross-layer requirement; Phase 2 is the opposite.

**Why it is possible.** The core is already host-agnostic: `FiberHostConfig` is 12 members over
opaque `object` handles, and `Shared/Core/Fiber/` has no real `UnityEngine` dependency (residual
references are comments, except the legacy `VisualElement`-typed ctor at `FiberRenderer.cs:4,24` —
which A3 removes anyway when retarget widens to `object`).

0.1 **A `net10.0` test project that links `Shared/Core/**`** and drives the reconciler through a
    **mock `FiberHostConfig`** built on plain POCOs. First task is confirming exactly which files are
    `UnityEngine`-free; the legacy ctor is the one known blocker.
0.2 **Tests for the behaviour Phase 2 depends on**: reuse-in-place when the sub-root is still
    attached; `RetargetContainer` preserving fiber/hook/ref/animation state; full remount dropping
    every retained reference; keyed reorders; deletion cleanup.
0.3 **Retention-site cleanup** (§5.4) with an **internal** liveness predicate — `PropsApplier`'s
    static element dictionary, the four row pools, `Animator`'s captured element, user `Ref`s — each
    with a test proving the reference is actually dropped.
0.4 Gates + release surface. **Patch bump, 0.14.1**: no new public API.

> **`IsAlive` on `FiberHostConfig` deliberately moves to Phase 2.** It is a new public virtual, i.e.
> additive, i.e. minor-worthy — keeping it out is what lets Phase 0 stay a clean patch. Phase 0 uses
> an internal predicate; A3 promotes it.

### PHASE 2 — the `PanelRenderer` host → **ship + publish (0.15.0, minor)**

Begins after Phase 0 publishes. **One branch, one PR** (owner decision) — Phase 0's separate PR is
the proof that the test harness works; everything after it lands together.

8. **🔴 Retention-site cleanup + `IsAlive`** (§5.4) — **first, deliberately.** Every site listed
   exists in today's code and has **zero dependency on the new host**: `PropsApplier`'s static
   element dictionary, the four row pools, `Animator`'s captured element in the tick lambda, user
   `Ref`s (uGUI already detaches these; UITK does not). Add `bool IsAlive(object)` to
   `FiberHostConfig` (virtual, default `true`; UITK → `!resourcesReleased`, uGUI → its existing
   fake-null check) as the liveness primitive the cleanup keys on.

   **Why first:** these are pre-existing leaks, fixable and unit-testable against the *current*
   `UIDocument` path where the variables are known. Doing it after the host means the first time
   remount runs you are debugging a new host and poisoned references simultaneously — and cannot
   tell which one failed. Doing it first means any later remount failure points at the host alone.
   End-to-end proof still waits for step 11 (nothing releases on the UIDocument path), so land this
   with unit tests now and confirm it end-to-end at step 14.

9. **A3 root-source abstraction** (§5.5) — `IRootSource`, one bootstrap replacing the triplicated
   `EnsureSetup`, retarget promoted to public + `object`-typed **with its first tests**.
10. **`UIDocument` root source** — port the existing path onto the abstraction; behaviour-neutral,
    verifies the seam before anything new rides on it.
11. **`PanelRenderer` root source** (§5.3) — sub-root mount, deferred mount + replay, the three-way
    reuse/retarget/remount branch (§5.2).
11b. **🔴 Nested-renderer workarounds WA1–WA4** (§5.9 registry). All four carry the greppable
    `WORKAROUND(...)` header of §5.9.3 and a `REMAINING_WORK.md` entry:
    - **WA1/WA2 mount watchdog** — one mechanism: no callback N frames after enable → `panelSettings`
      round-trip. Judge success by **outcome**, never by whether the call threw (§5.8.6 f18/f23).
    - **WA3 N2 prevention** — disable nested child renderers around rebuilds we trigger. Always on.
    - **WA4 N6 repair** — on `subRoot.resourcesReleased`, destroy + re-add **only the nested child's**
      renderer, **copying every setting across** (`visualTreeAsset`, `sortingOrder`, `position`,
      `pivot`, `pivotReferenceSize`, `worldSpaceSizeMode`, `worldSpaceSize`; prefer a full serialized
      copy in the editor) and using `Undo.DestroyObjectImmediate`/`Undo.AddComponent` in edit mode.
    - **All four active in builds, not `#if UNITY_EDITOR`-gated** — §5.8.8 f28.
    - Ship an **inertness test** per workaround: with the symptom absent, it must never fire.
12. **World-space parity** (§4.4) — `worldSpaceSizeMode`, `worldSpaceSize`, `position`, `pivot`,
    `pivotReferenceSize` surfaced on the host.
13. **Warn when `visualTreeAsset != null`** at mount — one-time, explains that editing that asset
    remounts and drops transient state (§5.8.5). Also **warn when `parentUI != null`**, naming the
    nested-renderer limitation and pointing at the known-issues page.
14. Migrate the remaining four mount sites (EditorWindow, uGUI, both islands) onto `IRootSource`;
    HMR third leg in **both** duplicated host lists (§5.6).
14b. **🔴 Five new samples on the new renderer** (owner request, 2026-08-02), simple → complex. These
    are the acceptance test a user actually sees, and each is chosen to exercise a distinct risk
    surface rather than to look pretty:

    | # | Sample | Exercises |
    |---|---|---|
    | 1 | **Hello PanelRenderer** — one screen-space panel, a label and a button | the plain mount path: deferred mount + replay, sub-root, `V.Host` props landing on our sub-root (§5.5b) |
    | 2 | **World-space panel in a 3D scene** — a diegetic UI on a surface | `worldSpaceSizeMode`, `worldSpaceSize`, `position`, `pivot`, `pivotReferenceSize`; the per-frame transform writes Unity owns |
    | 3 | **Nested renderers** — a child `PanelRenderer` under a parent | WA1–WA4: the mount watchdog, N2 prevention, N6 repair, and the `parentUI` warning. Should survive a parent rebuild in front of the user |
    | 4 | **Mixed hosts** — `UIDocument` and `PanelRenderer` in one scene, plus a portal and an island | §5.5c: both hosts coexisting, cross-panel portal behaviour, islands unaffected |
    | 5 | **A real screen** — router + signals + a list, on `PanelRenderer`, in both screen and world space | hook state across reuse/retarget/remount, HMR through the new host, and that nothing about the app-level API changed |

    Samples 3 and 4 double as living regression tests for the two Unity bugs we filed
    (case IN-150082, UUM-148452) — if a future Unity fixes them, these are where it shows.

15. Gates + manual verification, including the spike scenarios re-run against the real host — this
    is where step 8's cleanup gets its end-to-end proof.
16. Release surface (§8) — minor bump. Same push/PR/merge flow.

### Separately, not in either phase

17. The **ATG measurement comparison** (§4.1) — the one item that cannot be settled by reading.
18. The **runbook defects** (§9) — fix `add-unity-version` and `VERSIONING_PROCESS.md`.
