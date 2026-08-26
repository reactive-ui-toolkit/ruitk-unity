# POC parity spec — extracted from ruitkUiBuiler/index.html

> The parity contract for the RUITK Builder (owner mandate 2026-08-15: the Unity
> builder must match this POC top to bottom). Extracted by exhaustive read of the
> POC source (CSS 7-1060, DOM 1063-1143, JS 1145-2967).

## 0. Palette (parity contract)

`--bg #1b1b1f` app/canvas bg · `--panel #232329` toolbar/library/right/cards ·
`--panel2 #2a2a31` card titles/buttons/chips/menus · `--line #3a3a44` all borders ·
`--text #d6d6dc` · `--dim #8b8b96` section labels/hints/imports · `--accent #4fc3f7`
logo/active/focus/builtin tags/drop hints · `--comp #4fc3f7` · `--hook #81c784` ·
`--style #ce93d8` · `--warn #ffb74d` util kind/expressions/state names/@if ·
`--sel #ffd54f` selected card/row · `--edge #5c8bb0` · `--edgehook #6da86f` ·
`--edgestyle #a577b3`.

Literals: `#17171b` code islands/source/inputs · `#7fdbca` custom tags · `#f06292`
@else/errors/SOLD OUT · `#c792ea` keywords · `#c3e88d` strings · `#616e7a` comments ·
`#bdbdc7` code-island text · `#cfcfda` style-entry text · `#9d9dab` jsx-attrs ·
`#10222c` text-on-accent · `#101014` mock root · `#2c2c33` canvas grid dot (26px grid
on the WRAPPER — does not scale with zoom) · `#26262e` mock header · `#1d2b1d` mock
counter · `#221d12` hl island bg.

Tints: card shadow `0 6px 18px rgba(0,0,0,.35)`; selected ring `0 0 0 2px
rgba(255,213,79,.25)`; row hover `rgba(79,195,247,.08)`; row .hl
`rgba(255,183,77,.18)` + outline `rgba(255,183,77,.5)`; .rowsel
`rgba(255,213,79,.15)` + outline .5; drop hint 2px dashed accent + tint; kind badge
bgs `.16` alpha of kind color; directive badge bgs `.2` alpha; anchor-dot glow `.25`.
Fonts: UI `13px Segoe UI`; code `Consolas`.

## 1. LAYOUT

**Toolbar**: logo `RUITK Visual Editor` (accent, 700) · `interaction POC` (dim 11px) ·
sep · `L0 Architecture` / `L1 Cards` (active at boot) / `L2 Edit` · sep ·
`Import .uxml…` · `? How to drive it` · legend (margin-left auto). Buttons: bg panel2,
1px line, r4, 4x10px, 12px; hover border accent; active bg accent text `#10222c` w600.
Zoom presets L0 0.30 / L1 0.75 / L2 1.25, each resets pan to (60,30).
**Legend** (11px dim, 9px dots): `component` #4fc3f7 · `hook module` #81c784 ·
`style module` #ce93d8 · `usage edge` #5c8bb0.

**Main row**: library `0 0 205px` (right border) · canvasWrap `1 1 auto` (cursor
grab/grabbing; 26px dot grid bg) · vsplit `0 0 6px` col-resize hover accent · right
`0 0 440px`. Edges SVG overlay: absolute inset 0, pointer-events none, **z above
cards**. Right pane: pane-title `Live preview` + [mode btn][component chip accent] →
`#preview` `0 0 380px` (boot text `Select a card…` italic dim) → hsplit 6px
row-resize → pane-title `Source — .uitkx` + [edit][apply (Ctrl+Enter)][cancel
(Esc)][file chip] → srcpane flex 1, Consolas 12, bg #17171b. pane-title: 11px
uppercase ls .08em dim, bg panel2. Splitter clamps: right [280, win-300]; preview
[120, win-200]; edges redraw during drag.

**Footer** (11px dim): `Wheel: zoom • Drag Library items onto rows (top=before,
bottom=after, middle=inside) or BODY (hooks); drag rows to reorder • Right-click
rows / cards / canvas for typed attributes, directives, delete, create • L2: click
attrs / badges / style entries to edit • Source pane: edit → apply re-parses • Drag
splitters to resize`.

**Toast**: fixed bottom-center 44px, bg panel2, 1px accent border, r6, fades .25s,
auto-hide 3200ms. **Help** `Drive it like this`: 13-step ol (330px card, top-left).

## 2. LIBRARY PANE

pane-title `Library` + mini `+ new` (create menu at click, node at visible-canvas
center). Body (scroll, p8): search input placeholder `search library…` (bg #17171b,
focus accent) → sections (10px uppercase dim):
`native elements` <VisualElement> <Label> <Button> <ScrollView> <TextField> <Toggle>
<Slider> (accent) · `custom components` (all component nodes, #7fdbca) · `hooks`
(useState/useEffect/useMemo/useRef templates + hook modules as `useX (module)`,
green) · `style modules` (purple) · `util modules` (warn). Items: Consolas 12px,
4x9px, 1px line, r5, mb4, bg panel2, cursor grab, hover border accent; native/custom
render angle brackets. All draggable (`lib:<name>`, copy). Hint text at bottom
(10.5px dim): drag semantics. Search: substring; section header hides when all its
items hide. Library rebuilds preserve search text.

## 3. MODEL

Node `{id, kind, x, y, propsSig, exportsSig?, imports[], hooks[], body[], jsx|null,
styles?, utils?}`; JSX `{id, tag, attrs[{n,e}], children[], ref, directive{kind,
text}|null}`; Import `{to, kind: usage|hook|style, text}`; Hook `{decl, names[],
hook}`; Style `{name, entries[]}`; Util `{sig, body|null}`. File names: hook →
`X.hooks.uitkx`, style → `X.style.uitkx`, else `X.uitkx`.

## 4. CANVAS CARDS

**4.1 Container**: absolute, width 340 (L0 300 / L2 430), bg panel, border **1.5px**
line, **radius 10**, shadow; `.selected` border `--sel #ffd54f` + yellow ring.
Exactly one selected.

**4.2 Anatomy** (DOM order):
- `.pill` L0 ONLY: flex 16x20px pad, **26px w700** title + enlarged kind badge (15px,
  3x12px, r12). Drag handle.
- `.card-title` (hidden L0): flex g8, 8x12px, bottom border, bg panel2, cursor move —
  THE drag handle. Kind badge (10px, 1x7px, r8, w600: `component`/`hook`/`styles`/
  `utils`, bg = kind color at .16 alpha, text = kind color) + name (w600 14px).
- Signature section (component+hook): Consolas 12 dim, `<b>Name</b>` in text-color
  w600 + dimmed propsSig, e.g. **Header**`(string title, int gold)`.
- Sections: `padding 7px 12px`, bottom border `rgba(58,58,68,.6)`, last no border.
  `.sec-label`: 10px uppercase ls .08em dim mb5.
- `imports` section: per import a row (flex, Consolas 11.5 dim, 1.5x6px) with
  ellipsised full import text + trailing **anchor dot** 8x8 round (usage #5c8bb0 /
  hook #6da86f / style #a577b3, glow .25) — the edge source.
- `body — hooks & state` (component+hook): `.chips` wrap g5. Chip: bg panel2 1px
  line r10 2x9px Consolas 11.5, hover border warn; content `useState` (green) ` → `
  `gold, setGold` (warn); data-states for hover-trace; title = decl + "(double-click
  to edit)". Trailing dashed `+ hook` chip (dim → accent). Body lines → `.code-island`
  (L2 only): bg #17171b 1px line r6 6x9px Consolas 11.5 #bdbdc7 pre, hover accent.
- `return — markup` section (jsx): recursive rows (4.3).
- Style card `exports`: per export `name = new Style {` (name purple bold), entries
  (L2 only, indented 24px, trailing comma, click-to-edit, hover purple tint),
  `+ entry` (L2), `}`; after all: `+ style` (visible at L1 too).
- Util card `exports`: value sigs as-is; functions `sig {` + body island (L2) + `}`;
  trailing `+ export`.

**4.3 JSX row**: flex g6, 2.5x6px, r5, Consolas 12, pointer, draggable, padding-left
`8 + depth*14`. Order: directive badge (`@if` warn tint / `@foreach` green tint /
`@else` pink tint; 10px w700, title = full text) · tag WITH angle brackets (builtin
accent; custom #7fdbca w600) · attrs (L2 only, base #9d9dab; per attr `name=` +
editable `{expr}` value in warn, hover dotted underline) · anchor dot (only rows
instantiating a custom component; edge source, margin-left auto). States: hover
accent tint; .hl warn tint + outline; .rowsel yellow tint + outline; drop-before/
after 2px accent inset top/bottom; drop-hint dashed accent outline.

## 4.4 LOD (zoom) behavior

`lod = s < 0.45 ? 0 : s < 1.05 ? 1 : 2`; body class `lod0/1/2`; matching toolbar button `.active`.

| | L0 (`s < 0.45`) | L1 (`0.45 ≤ s < 1.05`) | L2 (`s ≥ 1.05`) |
|---|---|---|---|
| Card width | 300px | 340px | 430px |
| `.pill` (26px title + big kind badge) | shown | hidden | hidden |
| `.card-title`, all `.card-section` | hidden | shown | shown |
| `.lod2only` (jsx attrs, code islands, util bodies, style entries, `+ entry`) | hidden | hidden | shown |
| `.jsx-attrs` | — | `max-width:130px` ellipsised | wraps fully |
| Edges anchor | card-to-card | per-anchor-dot | per-anchor-dot |

Toolbar zoom presets (buttons SET ZOOM; lod derives): **L0 → 0.30**, **L1 → 0.75**, **L2 → 1.25**; preset also resets pan to `x:60, y:30`. Initial view `{x:40, y:20, s:0.75}`.

## 5. EDGES

- Screen-space overlay recomputed on: view change, card drag, rebuild, selection, splitter drag, resize.
- Edge list: one per import (anchor = that import row's dot `a-imp-<node>-<to>`), plus one per JSX row instantiating a custom component (anchor dot on the row, kind `usage`).
- Anchoring L1/L2: from the anchor dot (`right-4`, vertical center) to target card `left`, `top+18`. L0: card center to card center-left.
- Curve: cubic Bézier, `dx = max(40, |x2-x1| * 0.45)`, horizontal handles.
- Stroke width 2, opacity 0.85, no arrowhead — a **filled circle r=4** in edge color at the target endpoint.

| kind | color | dash |
|---|---|---|
| usage (component + util imports) | `#5c8bb0` | solid |
| hook import | `#6da86f` | `6 4` |
| style import | `#a577b3` | `6 4` |

## 6. INTERACTIONS

### 6.1 Pan/zoom/card drag
- Card drag ONLY from `.card-title` / `.pill`; elsewhere on card = nothing; empty canvas = pan (cursor grabbing).
- Wheel zoom-to-cursor: factor 1.12, clamp s ∈ [0.18, 2.2].

### 6.2 Selection & source sync
- Card click → select (single), render source+preview, redraw edges.
- Row click → select + scroll matching generated source line into view (`.srcline.sel`); row gets `.rowsel`.
- Double-click row with custom tag → navigate to that component.
- Hover a hook chip → highlight (`.hl`) every row/code-island (and source line) whose expressions reference the hook's state names (regex `\b(name1|name2)\b`).

### 6.3 Inline editing (L2)
`inlineEdit`: input `.inline-edit` (bg #17171b, 1px accent, radius 4, Consolas 11.5px, size = clamp(len+2,10,34)); multiline textarea variant (min-height 84px). `{`/`}` or quotes stay OUTSIDE the field as pre/post text. Enter commits (Ctrl+Enter multiline), Esc cancels, blur commits.

| target | trigger | notes |
|---|---|---|
| attr value `.expr` | single click | empty value ⇒ attribute removed |
| directive badge | single click | text re-kinded by prefix |
| style entry | single click | raw text |
| util signature | single click | trailing ` {` stripped |
| code island (component/hook/util body) | double click | multiline |
| hook chip | double click | re-derives names + hook |
| `+ hook` chip | single click | seeds `var (value, setValue) = useState(0);` then opens editor |

Commit toast: `Committed <what> → AST → formatter → <file>.uitkx (mock write)`.

### 6.4 Context menus
Chrome: fixed, bg panel2, 1px line, radius 6, min-width 195, item hover rgba(79,195,247,.14); searchable lists (`.ctx-search`), freeform fallback item in `--warn`, `(no matches)` dim item; Enter = first item; clamped to window; Esc/outside closes.

**Row menu** (title `<Tag>`): Add attribute (typed)… / Add child element… / Remove attribute… (searchable submenu `name = expr`) / — / Edit-Remove directive OR Wrap in @if / Wrap in @foreach (then inline-edit the badge) / — / Delete element (not on root).

**Attribute menu**: custom component → parsed propsSig params (`name : type`) + synthetic `key : list key`, then header `not declared on <Tag> — needs a matching prop` + COMMON_ATTRS. Native → ELEMENT_ATTRS[tag] + COMMON_ATTRS; present attrs filtered out.
- COMMON_ATTRS: style:Style, name:string, tooltip:string, focusable:bool, pickingMode:PickingMode, viewDataKey:string, usageHints:UsageHints, onClick:Action, onMouseEnter:Action, onMouseLeave:Action, onGeometryChanged:Action.
- defaultAttrValue: on[A-Z]/Action → `{handler}`; style → `{styleName}`; string/text/label → `"text"`; else `{value}`.
- After add: if zoom < 1.05 force-zoom 1.25 then inline-edit the new value.

**Add-child menu**: header `native elements` (7 native tags) + header `custom components` (all components except self). Adds at end. Seeds: Label text="New label", Button text="Click"; custom tag ⇒ ref set + auto import.

**Card menu**: single item `Delete <file>.uitkx`; guarded by reference scan (toast `Can't delete: still referenced by A, B.`).

**Canvas menu** (`create`): New component (.uitkx, PascalCase) / New style module (.style.uitkx, camelCase) / New hook module (.hooks.uitkx, `^use[A-Z]`) / New util module (camelCase); name menu w/ inline validation errors; world pos at cursor. `+ new` toolbar button = same menu at visible-canvas center.

### 6.5 Style/util authoring menus
`+ entry` → style-keys menu (26 keys with types) → value/helper menu per type (VALUE_TEMPLATES: Px(8)/Px(16)/Pct(100)/Pct(50); Hex/Rgba presets; FlexRow…; JustifyCenter…; AlignCenter…; FontBold…; TextMiddleCenter…; DisplayFlex/None; PositionRelative/Absolute; fallback `0, Px(8), Pct(100), Hex("#ffffff")`).
`+ style` → name menu (camelCase, dup check) seeds `FlexGrow = 1`.
`+ export` (util) → New function… (PascalCase, seeds `export int V(int value)`/`return value;`) / New value… (seeds `export int V = 0;`).

### 6.6 Drag & drop
Sources: library items (`lib:<name>`, copy) and JSX rows (`move:<node>:<row>`, move; root row NOT draggable).
Row bands: rel < 0.3 before / > 0.7 after / else inside; root forces inside. Hints: `.drop-before` inset top 2px accent; `.drop-after` inset bottom; `.drop-hint` dashed accent outline + tint; card-not-row → nearest section hint, inside.
Drops: hook → push hook (HOOK_TEMPLATES; style card ⇒ toast `Style modules have no hooks.`); stylemod → `import * as Styles from "./x.style"` (component only; dup toast); utilmod → `import { names } from "./x"` (components + hook modules only); element/component → insert child at parent+index (no jsx ⇒ toast; self-nesting ⇒ `A component can't contain itself.`); move → reorder/reparent (cross-component ⇒ toast `Moving across components isn't in the POC — delete and re-add.`; own-subtree guard; forward-index decrement).
HOOK_TEMPLATES: useState/useEffect/useMemo/useRef seeds.

### 6.7 Source pane (bidirectional)
Render: per-line divs, tokenized (.k #c792ea keywords+directives, .t accent builtin tags, .cu #7fdbca custom tags, .s #c3e88d strings, .e warn {expr}). Edit: `edit` button or double-click → textarea; apply (Ctrl+Enter) re-parses via per-kind parsers; throw ⇒ `.err` inset + toast `Parse failed: <msg>`, stays open; success ⇒ commit + canonical reformat. Rename rejected: `Rename to 'X' ignored — file identity stays <Id>`. Row↔line jump map via anchor keys (imp-/head-/hk-/row-/sty-/util-).

### 6.8 Splitters
Vertical: right pane flexBasis clamp(280, win-300). Horizontal: preview flexBasis clamp(120, win-200). Redraw edges during drag.

### 6.9 Toolbar misc
`? How to drive it` toggles help. `Import .uxml…` (real in Unity build).

## 7. LIVE PREVIEW + STATE
- Non-visual kinds show exact `.nopreview` texts (hook/style/util).
- Scripted vs model toggle for the demo component; generic model render is default.
- Generic renderer: AST → controls (Label/Button/TextField/Toggle/Slider/else container), custom components recursive with depth cap 2 (`<Tag /> (depth cap)` stub), `data-jump` regions navigate on click.
- Expression eval: literals, dotted paths, $-interpolation (unresolved → `…`), comparisons default true, @foreach unresolved → two dummy items, `set<X>(<y>+1)` onClick works.
- Style application maps Px/Pct/Hex + the 26 keys onto the DOM/USS.
- Knobs: `props — auto-generated knobs` (skip Action/Func; int/float→number, bool→checkbox, string→text; object props expand via `item.Member` scans, member type by name regex; defaults seeded from first usage elsewhere + note `knob defaults taken from its usage in <Owner>`).
- `state — live hook values`: EDITABLE fields (number/text/checkbox by runtime type) writing straight into the live state store; non-focused inputs synced back on re-render.
- Trailing note: `rendered generically from the model — every edit re-renders; handlers with C# bodies (e.g. cart.Buy) are no-ops here, the real renderer runs them`.
- useState init: literals; identifiers via util value exports; `Name()` via mocked builtins; custom hooks stubbed from exportsSig tuple.

## 8. Boot
buildLibrary → buildCards → applyView (lod1) → select first component → deferred edge draw; resize redraws.

## 9. Unity porting notes
- LOD via zoom thresholds .45 / 1.05; toolbar buttons are ZOOM PRESETS (0.30 / 0.75 / 1.25) not modes.
- Edges: MeshGenerationContext overlay, dirty on every card move/zoom/splitter/rebuild.
- No box-shadow/outline in USS — borders/overlays instead.
- DnD via pointer capture; 30/70 band math carries over.
- Inline editor width = measured clamp(len+2, 10, 34) chars.
