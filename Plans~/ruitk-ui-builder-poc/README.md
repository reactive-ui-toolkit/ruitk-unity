# RUITK Visual Editor — interaction POC

Open `index.html` in any browser (double-click; no server, no dependencies).

A mock of the proposed visual editor for `.uitkx`:

- **One canvas, semantic zoom.** Mouse-wheel to zoom, drag the background to pan,
  drag a card's title bar to move it. Zoom changes the level of detail:
  - **L0 Architecture** (zoomed out) — components/hooks/styles as pills + edges.
  - **L1 Cards** (mid) — title, props, hook chips, collapsed JSX outline.
  - **L2 Edit** (close) — attributes, body code islands, directive badges.
- **Edges anchor at usage rows** — the arrow leaves the exact JSX row (or import
  row) that references the target, not the card border.
- **Hover a state chip** (e.g. `gold`) — every `{gold}` usage lights up in the
  JSX rows and in the source pane.
- **Click any card** — the right side shows a **live preview** with **prop
  knobs**, and the generated `.uitkx` source. ShopScreen is a scripted app
  demo (buy items, working cart); **every other component renders generically
  from the model**: attrs, `@if`/`@foreach`, hook state, `var` body lines and
  imported styles are evaluated, object-typed props get knobs synthesized
  from the member paths the markup uses, and `setX(x + 1)`-style onClick
  handlers actually increment. Any canvas or source edit — including
  reordering — re-renders it. Clicking a nested custom component in the
  preview jumps to it.
- **Naming rules** — components are PascalCase; style, util, and hook modules
  are camelCase (hooks start with `use`).
- **Util modules export values too** — "+ export" offers function or value
  (`export int MaxHealth = 100;`); both import by name.
- **Directive badges** — `@foreach` on the ItemCard row, `@if` on the Counter
  row; flip the *stock* knob on ItemCard to watch the `@if` branch switch live.
- **Library sidebar** (left, searchable) — native elements, custom components
  (including ones you create), hooks, and style modules. Drag onto a JSX row:
  top edge inserts *before*, bottom edge *after*, middle nests *inside*
  (imports auto-added). Drag a hook onto a card's BODY section; drag a style
  module onto a card to add its import. Existing rows drag to reorder.
- **Typed attributes with autocomplete** — "Add attribute" is a searchable
  menu: native elements from a schema table (common VisualElement surface +
  per-element extras), custom components from their props signature (with the
  undeclared native attributes shown separately — they'd need a matching
  prop), and a freeform "untyped" fallback in case something is missing.
- **Create menu (Shader-Graph style)** — right-click empty canvas or the
  Library's "+ new": component / style module / hook module / **util module**
  (plain exported functions, imported by name — drag the module onto a
  component to add the import), named via an in-menu input (no browser
  prompts anywhere). Right-click a card to delete it (blocked while
  referenced).
- **Attribute removal** — right-click a row → "Remove attribute…", or simply
  empty an attribute's value in the inline editor.
- **Wrapped attributes at L2** — rows with many attributes wrap across lines
  (VS Code style) instead of ellipsizing.
- **Typed style authoring** — on a style card, "+ entry" opens a searchable
  key list (FlexGrow, BackgroundColor, …) then value templates built on the
  CssHelpers (`Px()`, `Pct()`, `Hex()`, `Rgba()`, `FlexRow`, …); "+ style"
  adds another export. Freeform fallback on both.
- **Bidirectional source pane** — click *edit* (or double-click the source),
  change the text, *apply* (Ctrl+Enter). A mini-parser for the dialect
  re-parses it into the model: the card, edges, and preview update, and the
  source re-renders canonically formatted. Parse errors toast and keep your
  text. In the real tool this parser is `Ruitk.Language` itself.
- **Editing happens on the canvas** too. At L2:
  - **click** an attribute value, a directive badge, or a ShopStyles entry →
    inline edit (Enter commits, Esc cancels); `{}` / quote wrappers stay
    outside the field — you edit only the value. The model updates and the
    source regenerates, mimicking visual-edit → AST → formatter → file;
  - **double-click** a code island or a hook chip to edit body code;
  - **right-click a JSX row** → add attribute, add child element (palette of
    native elements + every custom component; imports are auto-added), wrap
    in `@if`/`@foreach`, remove directive, delete element;
  - **right-click empty canvas** → create a new component (a new card +
    generated source); the **+ hook** chip on any card adds a hook;
  - select ShopScreen, then edit `root`'s `BackgroundColor` hex in the
    ShopStyles card — the live preview repaints.
- **Resizable panes** — drag the vertical splitter (canvas ↔ right panel) or
  the horizontal one (preview ↔ source).
- **Import .uxml** button — mocks the one-way UI Builder import path.

Everything is hand-mocked in one HTML file; no part of the real toolkit runs here.
The point is the interaction model, not the implementation.
