# RUITK Builder — capability reference

What the in-Unity visual builder can do, as shipped. This file exists so the
builder can be rebuilt or ported to another engine's toolkit (Godot, Unreal)
from a description of BEHAVIOUR rather than by reading the Unity implementation.

**This file is a contract: every capability added, changed or removed must be
recorded here in the same commit.** See `.claude/skills/uibuilder-capabilities/`.

Defect history and per-item root causes live in `Plans~/UI_BUILDER_BUGS.md`
(UB-## ids). This file describes what works; that file describes what broke.

---

## 0. Getting in

Three entry points:
- **Right-click a `.uitkx` asset → "Open in RUITK UI Builder"** opens the tree
  that file belongs to — the whole connected tree, not just the clicked file.
- **The menu item** opens the builder with no tree, on a start screen offering
  the four module kinds. A tree begun this way lives entirely in memory; the
  first Save asks which folder it belongs in (refusing anywhere outside the
  Unity project), moves the pending modules there, and writes. Cancelling that
  prompt cancels the save and writes nothing.
- **Right-click a `.uxml` asset → "Convert UXML to UITKX"** — one-way import.

Double-clicking a `.uitkx` asset still opens the external editor; the builder
never takes that route over.

## 1. What the builder is

A canvas of CARDS, one per `.uitkx` module in the open tree, wired by IMPORT
EDGES, with a folder tree and a searchable library down the left and a live
preview over a bidirectional source editor on the right. Editing happens on the
canvas, in the source pane, or by dragging from the library — all three land as
text edits on the same document buffers.

**Disk contract (load-bearing):** nothing reaches disk until Save. Every edit,
including deletion, renaming and folder moves, is a pending change on an
in-memory tree. Abort discards everything; closing without saving changes
nothing on disk.

## 2. The canvas

### Cards
- One card per module. Kinds: component, style module, hook module, util
  module — each with its own accent colour and badge.
- Sections, top to bottom: title bar, signature (name + full props signature,
  syntax-coloured, and clickable - see Props), IMPORTS, BODY (hooks & state),
  RETURN (markup rows), and for style/util modules, per-export detail entries.
- Card position is user-draggable by the title bar and persisted per tree.
- A card can be deleted (see Deletion). Deleting is refused while another
  module still imports it, naming the referrers.

### Levels of detail (LOD)
Driven by zoom, three bands, each selectable by name from a labelled toolbar
dropdown:

| Layer | Zoom preset | Applies at | Shows |
|---|---|---|---|
| **Layer 1 — Architecture** | 0.30 | zoom < 0.32 | Pill: name + kind only, and the edges between them. |
| **Layer 2 — Cards** | 0.75 | zoom < 0.80 | Signature, imports, hook chips, markup rows. |
| **Layer 3 — Edit** | 1.25 | zoom >= 0.80 | Adds per-row attributes, code islands, directive badges and style entry lines — and this is the layer at which those become clickable to edit. |

### Camera
- Wheel zooms about the cursor; drag on empty canvas pans. Over a section that
  scrolls on its own, Ctrl+wheel zooms the canvas instead of scrolling it.
- Zoom range 0.10–2.2. A tree with no saved layout opens at Layer 2.
- The layer dropdown jumps straight to a layer's preset zoom.
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

## 3. The folder pane

The top-left pane shows the tree as FOLDERS — a second projection of the same
modules the canvas draws, showing where each one lives rather than what imports
it. Expanded by default, collapsible from its header when the library needs the
height.

- Clicking a file focuses it: the canvas selects its card, the preview switches
  to it, and the source pane shows its buffer.
- Dragging a file or a folder onto another folder re-files it. This is the only
  gesture that moves anything.
- What it shows is PENDING until Save — a move made here is a plan, not a
  filesystem operation.

### The folder convention
A component owns a folder named after it; its children live in a `components/`
folder inside that folder; its companions sit beside it.

```
Assets/UI/NewComponent/
  NewComponent.uitkx            <- the tree root
  newComponent.style.uitkx      <- companion
  useNewComponent.hooks.uitkx   <- companion
  components/
    LeftSide/
      LeftSide.uitkx
      leftSide.style.uitkx
    RightSide/
      RightSide.uitkx
      components/
        Badge/Badge.uitkx       <- children nest the same way at any depth
```

| Kind | File name | Where it goes |
|---|---|---|
| Component | `PascalCase.uitkx` | its own folder, under the parent's `components/` |
| Style module | `camelCase.style.uitkx` | beside the component it belongs to |
| Hook module | `useSomething.hooks.uitkx` | beside the component it belongs to |
| Util module | `camelCase.uitkx` | beside, and at the tree root by default |

**Families.** A component and the style and hook modules named after it are one
family — `NewComponent.uitkx`, `newComponent.style.uitkx`,
`useNewComponent.hooks.uitkx`. A new companion whose name matches a family
lands in that component's folder, wherever it lives. Because companions are
siblings rather than pooled, `Card/button.style.uitkx` and
`Panel/button.style.uitkx` coexist.

**What moves a module.** Nothing, unless a gesture says so. Removing an import
does not move a file, and nothing re-places itself. A drag in the folder tree
re-files by TYPE — a component into `Target/components/Name/`, a companion into
`Target/` — and rewrites the specifiers of everything that already imports it,
from each importer's own position. It adds and removes no imports: dragging X
onto Y does not make Y use X, and the old parent keeps its import because its
markup still references X.

## 4. Reading a tree

- The tree is discovered from the focus file by walking imports. The language
  server supplies the inventory of modules on disk and, for a module the builder
  has not touched, what it imports — its answer is derived from the same text,
  so it is a cache and a cheap one. For anything the builder holds differently
  from disk — created, renamed, or merely edited — the server is stale by
  definition and that module's own buffer is parsed instead. A module is
  therefore wired into the tree the moment its import is typed, with no file
  behind it.
- Every card's content is parsed from the real language AST, not by regex: the
  signature, exports, imports, hook calls, markup structure and all five
  directives (`@if`/`@else if`/`@else`, `@foreach`, `@for`, `@while`,
  `@switch`/`@case`/`@default`).
- Directive heads and clauses render as their own badged rows with their
  children indented beneath them.
- Hook chips show the hook name and the state names it returns; hovering one
  highlights every usage of those names in the markup rows and the source pane.

## 5. Editing on the canvas

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

### Menus
Context menus are drawn as a layer in the builder's own panel rather than as a
separate window: they carry the builder's styling, nothing can lose focus to
them, and they have real SUBMENUS rather than a flattened list. Rows, cards,
import rows and the empty canvas each have their own menu.

They are fully keyboard-drivable: up/down moves, Right or Enter opens a
submenu, Left or Escape backs out one level, Escape again closes. Menus with a
long vocabulary — style keys, elements, attributes — open with a search field
instead, and keep the freeform fallback entry.

### Props (the signature row)
The signature row is a gesture, not a label. Clicking it — or **Props…** on the
card's own menu — opens the props of a component or hook module:

- **Add a prop** — a searchable type menu offering the types this tree already
  uses ahead of the handful every UI needs, with a free-text row for everything
  else, because prop types are ordinary C# and no menu can be exhaustive. Then
  the name, then whether it is required or carries a default. A required prop is
  inserted BEFORE the first optional one, since C# rejects the other order.
- **Rename a prop** — the declaration, its uses inside the component's own body,
  and the attribute at every call site in the tree, as ONE undoable action.
- **Remove a prop** — strips the attribute from every call site the tree knows
  about, also one undo, and reports how many callers it touched. If the
  component's own body still refers to the prop, the toast says so rather than
  leaving the compile error to be discovered.
- **Make required / make optional** — "required" is not a flag stored anywhere;
  it IS the absence of a written default, so the toggle writes one or takes it
  away.

A parameter written without a default is REQUIRED: a call site that omits it is
an error (`UITKX0115`) — in Unity's console at build time, in the IDE from the
language server, and in the builder's own source pane while you type, where the
required set is read from the open tree rather than from disk. `MutableRef<T>`
parameters are exempt — `ref={x}` fills them, they are not an input the caller
supplies.

Every one of these gestures reads and writes the tree's in-memory buffers. A
call site in a module the user never opened is still a call site, and the tree
knows about it; nothing reaches disk before Save. Call sites OUTSIDE the open
tree are not rewritten — they get the diagnostic instead, which is the honest
limit of what the builder can see.

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
- **Add hook** / **Add code** — the BODY section carries a "+ hook" chip that
  seeds a `useState` and a "+ code" chip that seeds a plain statement; both
  open the inline editor on the new line, so custom body logic never requires
  the source pane.
- **Add style/util export**, and **add style entry** with searchable keys and
  value helpers (Px/Pct/Hex/Rgba/flex/justify/align/font/text/display/position).
  The key vocabulary is reflected from the real typed style surface, so a menu
  can never offer a key the type does not have.
- **Apply a style module** by dragging it onto an ELEMENT row: the element's
  `style` attribute is set to the chosen export and the import is added if the
  file lacks it, as one undoable action. A module with several exports asks
  which. Dropped on the card rather than a row it adds the import alone.
- **Rename module** — from the card menu. Renames the export, the file, the
  folder when the module owns one, and every importer's specifier and
  binding across the tree — including importers the user has never opened.
  Like every edit it is pending: Save applies it, Abort drops it, and one
  undo reverses the whole rename. The module keeps its identity across the
  rename, so its buffer, its undo history and the line-ending flavour of the
  file it came from all survive; Save projects the move as one operation
  rather than a new file plus a deletion.
  A component that owns its folder takes the **whole folder** with it —
  sub-components, companion modules, and files the builder does not manage —
  as a single move, so child GUIDs survive and nothing is left behind. The
  saved card layout follows the rename, out and back again through undo.
- **Import .uxml** — one-way conversion to a `.uitkx` module, from the
  toolbar or from a `.uxml` asset's context menu. The result arrives as a
  pending module like anything else the builder creates: Save writes it,
  Abort drops it, Ctrl+Z takes it back.
- **Create module** — component / style / hook / util, from a right-click or
  the library's "+ new", named through a validating prompt (PascalCase
  components, camelCase style/util, `use…` hooks). A name is taken only when the
  FILE it would produce is taken, so `SomeComponent` the component and
  `someComponent` the style module coexist. A component and a hook start with
  exactly the export just named and the smallest legal body; a style and a util
  module start EMPTY. Like every other edit, the file is a pending buffer with a
  real card on the canvas.

  **Where it is born follows from where you right-clicked**, never from what is
  focused:

  | Right-click on | Component | Style / hook / util |
  |---|---|---|
  | Empty canvas | at the tree root, `Root/components/Name/` | at the tree root, unless the name matches a family |
  | A component card | a CHILD, `Parent/components/Name/` | a SIBLING, `Parent/` |
  | A companion card | no create menu — a style module has no children | — |

  The name prompt names the parent it is creating under, and the new card is
  placed under its parent on the canvas.

  **Create states placement; wiring states usage.** Creating a module never adds
  an import. An import with no usage is an ERROR, so a create that imported
  would also have to invent a usage — which means guessing where a style applies
  or which element a hook belongs to. The builder places the file and stops; the
  user wires it by dragging it in.

### Drag and drop
- Library rows drag onto markup rows. The canvas lists markup flattened with
  indentation, and a drop lands exactly where the hint is drawn:
  - **middle band** — a tinted outlined box over the row: appended INSIDE it,
    as its last child.
  - **bottom band** — a dashed caret in the gap under the row: if the next
    listed row is deeper, the element becomes that row's FIRST child;
    otherwise it lands after the row's whole block. Both are the same point
    on screen.
  - **top band** — a dashed caret above the row: inserted before it, as a
    sibling.
- Existing markup rows drag to reorder or re-parent, moving their whole line
  range with re-indentation. Directive heads move their entire block. Every
  outcome is reported, including refusals and a drop that landed between rows
  rather than on one.
- Hooks drop onto BODY; style/util modules drop onto a card and add the import.

### Selection and the keyboard
- Exactly one thing is selected at a time: a card, a markup row, or a
  line-backed item (hook chip, import row, code island, style entry). Selection
  is always visible - a warm band and accent outline on whatever is selected.
- **Delete** removes the selection: an element row, a directive clause, a whole
  directive block, a hook/import/island/entry line range, or — falling through
  to the card — the module itself.
- **Escape** cancels the innermost active edit, then clears the selection. An
  open menu is innermost of all, so Escape closes it first.
- Both are inert while a text surface holds focus, so Delete still deletes
  characters inside an editor.
- **Ctrl+S** save, **Ctrl+Z** undo, **Ctrl+Shift+Z** / **Ctrl+Y** redo. All
  builder shortcuts are consumed by the window and never reach Unity's globals.

### Deletion
Deleting a module marks it pending: the card leaves the canvas, the file stays
on disk. Save performs the deletion (to the OS trash, not an erase) in the same
batch as the writes; Abort discards it; undo un-marks it. No asset is ever
re-created, so file identity is never churned.

## 6. Save and abort

Save formats every dirty buffer, then writes them in ONE batch — one script
reload for the whole batch instead of one per file, and no reload at all while
HMR Mode is active. It performs the moves planned in the session (renames,
folder re-filings) together with the import-specifier rewrites that keep them
consistent.

Save asks before anything irreversible:
- **Deletion** — it names every file the save would delete, and they go to the
  trash rather than being erased.
- **An empty module** — an empty `.uitkx` is not an empty file but a broken one,
  because the language requires a top-level declaration. Clearing a module while
  working is legitimate; writing it is where the builder stops and asks.

A tree begun from the start screen has no folder yet, so the first Save asks for
one, once, and moves the whole pending tree there before writing. Until then its
modules live at a provisional location the Asset Database cannot see, so a
half-finished tree can never be picked up by a compile. The relocation is
planned in full first, so a name collision cancels the whole move instead of
leaving half the tree in the new folder.

Abort discards every unsaved buffer and puts PATHS back as well as text: a
renamed module returns to its old name, and a module that rode along inside a
renamed folder returns with it.

## 7. Undo / redo

An action ledger records every builder action as one entry holding every change
the gesture produced — text edits, and the structural ones: a creation, a
deletion, a module move, a folder move. A change touching two files undoes as a
single step, from whichever file is in focus, and each change remembers which
MODULE it belongs to rather than only which path, so a replay still finds it
after a rename has moved that path.

A History panel lists all actions with the live cursor; clicking any row replays
whole entries to that point — the same path undo and redo use, so a jump across
a rename or a delete moves the tree and not just the text. Redo is truncated by
new work.

## 8. Source pane

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

## 9. Preview pane

Live-renders the selected component by compiling the current buffers in-process
and mounting the result through the real reconciler, on its own frame-budgeted
scheduler. Compile failures are reported loudly rather than silently showing a
stale tree, and the last good preview is kept. Primitive props on the focused
component appear as knobs. Clicking an element in the preview selects the markup
row that produced it, and vice versa. Hook modules show their signature and
consumers instead of a preview.

## 10. Library pane

Searchable palette in sections: native elements (from the schema), hooks,
custom components, style modules, util modules, hook modules. Entries drag onto
the canvas. Double-clicking a workspace entry FRAMES its card — solving the
zoom so the card fills the viewport, then centring on it. "+ new" creates a
module at the tree root.

## 11. Diagnostics

Three tiers, merged into the source console and the card overlays:
1. Structural parse/validation diagnostics (`UITKX####`).
2. Unknown element / unknown attribute checks, resolved against the schema
   UNIONED with the runtime element registry, so a registered element is never
   reported unknown because the schema lags.
3. C# compile errors (`CS####`) from the preview compile.

A **Trace** toggle in the toolbar turns on a running log of what the preview
pipeline decided. Off by default; it exists for when the preview is not showing
what the user expects, and it reports four things:

- which modules were considered for a rebuild, which were rebuilt, and why;
- which component each child tag resolved to, through the importing file rather
  than by name — and, explicitly, when nothing resolved;
- which body each child reference actually received at render time, naming the
  type and the assembly it came from, so a component rendering as something else
  is one line rather than an inference;
- any module path the builder could not answer from its own tree and asked the
  filesystem about instead.

The last one is worth understanding: the builder holds the open tree in memory,
and everything it shows is computed from that tree. Falling through to the
filesystem is legitimate for a module the tree does not own — a hand-written
file elsewhere in the project that this tree imports — and a mistake for one it
does. Naming them makes the difference visible.

## 12. Formatting

Save reprints every dirty buffer through the AST formatter, so text spliced in
by canvas edits ends up in the canonical shape. It is deliberately a SAVE-time
pass, not per keystroke. A buffer that does not format cleanly is written
exactly as it stands.

## 13. Persistence

- Card positions, camera and zoom per tree, in a local layout file outside the
  project's assets — per-user preference, not project content, so it is written
  as soon as a card is dragged rather than waiting for Save.
- A card's slot is decided once and then remembered, so adding a module never
  reshuffles cards the user has already placed.
- Renaming, moving or re-filing carries the layout with the files, and a tree is
  recognised by its MEMBERSHIP, so a re-filed tree still finds its own layout
  even when the re-filing changes which module is its root.
- The tree is journalled while work is unsaved and dumped in full before a
  domain reload, outside the project's assets. If the builder ever comes up
  empty beside a journal it offers the unsaved work back; a session that ended
  cleanly leaves no journal.
- Nothing else is written outside Save.

The same rule governs READS. From the moment a tree is open, everything the
builder shows — every card, every edge, every preview render — is computed from
the in-memory tree, never from the files behind it. A module that has been
created, edited, renamed, moved or deleted in the session answers as the tree
says it is, not as the file still on disk says. The filesystem is consulted only
for modules the open tree does not own.

## 14. Read-only sources

Modules that live in immutable packages open read-only: they render on the
canvas and in the preview, and their cards can be inspected, but they cannot be
edited or saved.

---

## Known non-capabilities

Recorded so a port does not go looking for them:
- The canvas itself is a real `.uitkx` component; the surrounding chrome
  (window shell, panes, source field, menus, overlays) is hand-built UI Toolkit
  C#. Full dogfooding is a planned future pass.
- No multi-select. Exactly one thing is selected at a time.
- No automatic graph layout ("tidy"); card positions are manual and persisted.
- No rename-across-files refactor from the canvas beyond the module rename.
- `@uss` and `Asset<T>` references added since the last Save resolve only after
  saving — the asset cache is disk-gated.
- Two `.uitkx` trees in the same project may share a component name; the builder
  keeps them apart by where the file IS, not what it is called. (This was not
  always true — a tree opened second could render the first one's components.)
- Moving an EXISTING markup row into another element is unreliable: the gesture
  is armed and every band resolves, but landing the drop is difficult in
  practice. Adding the same element from the library works normally. Every
  outcome now reports itself, so a failed attempt says which one it took.
