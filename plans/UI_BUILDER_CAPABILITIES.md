# RUITK Builder — capability reference

What the in-Unity visual builder can do, as shipped. This file exists so the
builder can be rebuilt or ported to another engine's toolkit (Godot, Unreal)
from a description of BEHAVIOUR rather than by reading the Unity implementation.

**This file is a contract: every capability added, changed or removed must be
recorded here in the same commit.** See `.claude/skills/uibuilder-capabilities/`.

Defect history and per-item root causes live in `Plans~/UI_BUILDER_BUGS.md`
(UB-## ids). This file describes what works; that file describes what broke.

---

## 1. What the builder is

A canvas of CARDS, one per `.uitkx` module in the open tree, wired by IMPORT
EDGES, with three side panes: a searchable library, a live preview, and a
bidirectional source editor. Editing happens on the canvas, in the source pane,
or by dragging from the library — all three land as text edits on the same
document buffers.

**Disk contract (load-bearing):** nothing reaches disk until Save. Every edit,
including deletion, is a pending change on an in-memory buffer. Abort discards
everything; closing without saving changes nothing on disk.

## 2. The canvas

### Cards
- One card per module. Kinds: component, style module, hook module, util
  module — each with its own accent colour and badge.
- Sections, top to bottom: title bar, signature (name + full props signature,
  syntax-coloured), IMPORTS, BODY (hooks & state), RETURN (markup rows), and
  for style/util modules, per-export detail entries.
- Card position is user-draggable by the title bar and persisted per tree.
- A card can be deleted (see Deletion). Deleting is refused while another
  module still imports it, naming the referrers.

### Levels of detail (LOD)
Driven by zoom, three bands:
- **L0** (< 0.45) — pill: name + kind only. The architecture diagram.
- **L1** (< 1.05) — signature, imports, hook chips, markup rows.
- **L2** (>= 1.05) — adds per-row attributes, code islands, directive badges,
  style entry lines.

### Camera
- Wheel zooms about the cursor; drag on empty canvas pans.
- Zoom range 0.10–2.2. A tree with no saved layout opens at 1.0 (L1).
- Toolbar presets jump to fixed zooms.
- Camera and zoom persist per tree.
- Cards more than one viewport outside the visible rect render as a sized empty
  box (their sections are not built) — a pure performance behaviour, invisible
  to the user except as responsiveness at high zoom.

### Edges
Bezier import edges, one per import row plus one per markup row that
instantiates another module. Anchor dots sit in a column ON the card's right
border, one per referencing row, and each edge leaves from its own dot and
arrives at the target card's top-left — so a curve never starts over the card's
own content, and every dot has a visible line. Edges are painted in a
screen-space overlay so stroke weight is constant at every zoom.

## 3. Reading a tree

- The tree is discovered from the focus file by walking imports through the
  language server's module graph.
- Every card's content is parsed from the real language AST, not by regex: the
  signature, exports, imports, hook calls, markup structure and all five
  directives (`@if`/`@else if`/`@else`, `@foreach`, `@for`, `@while`,
  `@switch`/`@case`/`@default`).
- Directive heads and clauses render as their own badged rows with their
  children indented beneath them.
- Hook chips show the hook name and the state names it returns; hovering one
  highlights every usage of those names in the markup rows and the source pane.

## 4. Editing on the canvas

All canvas edits are text edits on the buffer, committed through one funnel
that also re-parses the card, re-syncs the language server and records an undo
entry.

### Inline editors
A single floating editor serves every surface. It carries syntax colouring,
Ctrl+Space completion mapped to the exact file position, and overlay
diagnostics. Enter commits, Escape cancels, clicking away commits. Escape on an
editor the builder SEEDED (a fresh wrap or clause) also undoes the seeding.

Editable in place: attribute values, directive headers, hook chips, style entry
lines, code islands (multiline), and element rows. The editor takes the size and
position of the thing it edits - a code island editor replaces its island
exactly, and a single-line editor matches its row's height and glyph size at any
zoom. Focus never selects the whole text, so the first keystroke cannot wipe it.

### Structural operations
- **Add attribute** — searchable, typed from the schema for native elements and
  from declared props for components; free-text fallback.
- **Remove attribute** — by name, or by emptying its value.
- **Add child element** — searchable element list.
- **Wrap in…** — submenu of the five directives. Seeds compile-clean headers
  (`@if (true)`, `@for (int i = 0; i < 1; i++)`, `@while (false)`,
  `@switch (0)` + `@case 0:`), then opens the header editor.
- **Clause management** — add `@else`/`@else if` to an `@if`; add
  `@case`/`@default` to a `@switch`. New cases insert above `@default`; new
  case labels are the next unused integer.
- **Unwrap** a single-clause directive, keeping its children.
- **Add hook** — seeds a `useState` before the return.
- **Add style/util export**, and **add style entry** with searchable keys and
  value helpers (Px/Pct/Hex/Rgba/flex/justify/align/font/text/display/position).
- **Create module** — component / style / hook / util, from the canvas
  right-click or the library's "+ new", named through a validating prompt.

### Drag and drop
- Library rows drag onto markup rows: the drop band (top 30% / bottom 30% /
  middle) inserts before, after, or nests inside. The hint distinguishes the
  two outcomes: a thick dashed caret with end caps marks the exact line a
  SIBLING will land on, while a tinted outlined box means it will NEST inside
  the target.
- Existing markup rows drag to reorder or re-parent, moving their whole line
  range with re-indentation. Directive heads move their entire block.
- Hooks drop onto BODY; style/util modules drop onto a card and add the import.

### Selection and the keyboard
- Exactly one thing is selected at a time: a card, a markup row, or a
  line-backed item (hook chip, import row, code island, style entry). Selection
  is always visible - a warm band and accent outline on whatever is selected.
- **Delete** removes the selection: an element row, a directive clause, a whole
  directive block, a hook/import/island/entry line range, or — falling through
  to the card — the module itself.
- **Escape** cancels the innermost active edit, then clears the selection.
- Both are inert while a text surface holds focus, so Delete still deletes
  characters inside an editor.
- **Ctrl+S** save, **Ctrl+Z** undo, **Ctrl+Shift+Z** / **Ctrl+Y** redo. All
  builder shortcuts are consumed by the window and never reach Unity's globals.

### Deletion
Deleting a module marks it pending: the card leaves the canvas, the file stays
on disk. Save performs the deletion (to the OS trash, not an erase) in the same
batch as the writes; Abort discards it; undo un-marks it. No asset is ever
re-created, so file identity is never churned.

## 5. Undo / redo

An action ledger records every builder action as one entry holding every
`(file, before, after)` triple the gesture produced — so a change touching two
files undoes as a single step, from whichever file is in focus. A History panel
lists all actions with the live cursor; clicking any row walks the buffers to
that point. Redo is truncated by new work.

## 6. Source pane

- Full file with syntax colouring, line banding, and a diagnostics console.
- Bidirectional: editing re-parses into the model and updates the card;
  canvas edits regenerate the source.
- Colouring comes from language-server semantic tokens (markup structure plus
  merged C# classification) with a lexical fallback so identifiers, types,
  members, calls and numbers are always coloured, tokens or not.
- Edit mode keeps the coloured listing visible under a transparent-ink input,
  so text stays coloured while being typed.
- Ctrl+Space completion; Ctrl+Enter applies.
- Clicking a markup row scrolls the pane to that line (vertically only).
- The diagnostics console is scrollable and selectable, holds every diagnostic,
  and supports Ctrl+A / Ctrl+C plus a copy-all menu.

## 7. Preview pane

Live-renders the selected component by compiling the current buffers in-process
and mounting the result. Compile failures are reported loudly rather than
silently showing a stale tree. Clicking an element in the preview selects the
markup row that produced it, and vice versa. Hook modules show their signature
and consumers instead of a preview.

## 8. Library pane

Searchable palette in sections: native elements (from the schema), hooks,
custom components, style modules, util modules, hook modules. Entries drag onto
the canvas. Double-clicking a workspace entry FRAMES its card — solving the
zoom so the card fills the viewport, then centring on it.

## 9. Diagnostics

Three tiers, merged into the source console and the card overlays:
1. Structural parse/validation diagnostics (`UITKX####`).
2. Unknown element / unknown attribute checks, resolved against the schema
   UNIONED with the runtime element registry, so a registered element is never
   reported unknown because the schema lags.
3. C# compile errors (`CS####`) from the preview compile.

## 10. Persistence

- Card positions, camera and zoom per tree, in a local layout file.
- Document buffers and pending deletions survive a domain reload.
- Nothing else is written outside Save.

---

## Known non-capabilities

Recorded so a port does not go looking for them:
- The canvas itself is a real `.uitkx` component; the surrounding chrome
  (window shell, panes, source field, menus, overlays) is hand-built UI Toolkit
  C#. Full dogfooding is a planned future pass.
- No multi-select. Exactly one thing is selected at a time.
- No automatic graph layout ("tidy"); card positions are manual and persisted.
- No rename-across-files refactor from the canvas.
