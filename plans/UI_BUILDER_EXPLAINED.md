# The RUITK Builder, explained

**Audience:** someone — human or model — who has to work on this and has not
lived through its construction. It is written to be read top to bottom once,
then used as a reference. Where a rule exists because something went wrong, the
failure is named, because the rule is not memorable without it.

**Companion documents.** `plans/UI_BUILDER_CAPABILITIES.md` is the *catalogue* —
what the builder can do, stated flatly. This document is the *explanation* — why
it is shaped this way and how the parts relate. `Plans~/UI_BUILDER_BUGS.md` is
the defect register (UB-## ids referenced throughout).
`ruitkUiBuiler/index.html` is the browser prototype the interaction design came
from; hundreds of code comments cite it as "POC ...", and §2.1 explains what it
is authoritative for and what it is not.

---

## 1. What it is

The RUITK Builder is a **visual editor for `.uitkx` files that runs inside the
Unity Editor**. It shows a folder of components as cards on an infinite zoomable
canvas, lets you edit them by direct manipulation — drag an element in, click an
attribute, add a prop — and renders the focused component live, from the real
compiled component, while you work.

Open it from the Unity menu; it is an `EditorWindow` (`BuilderWindow`).

### What it is NOT

- **Not a UXML/UI-Builder replacement.** Unity's UI Builder edits UXML assets.
  This edits `.uitkx` — a JSX-like language that compiles to C#. Different
  artifact, different model.
- **Not a code generator you run once.** It is a bidirectional editor: it reads
  the files you already have and writes them back in place.
- **Not a scene tool.** It never touches scenes, prefabs or GameObjects.
- **Not a preview of a *file*.** It previews a **component**, mounted and
  running, with its hooks live.

---

## 2. The intention

`.uitkx` gives Unity a React-shaped component model. That solved authoring in a
text editor. It did not solve three things:

1. **Seeing the shape of a UI.** A screen made of fifteen components spread over
   fifteen files has a structure that exists only in the author's head. The
   import graph is the structure, and nothing drew it.
2. **The edit/see loop.** Change a label, enter Play mode, navigate to the
   screen, look. Thirty seconds per glance, which is enough to stop you
   iterating on the way something *looks*.
3. **Discoverability.** 79 elements, 21 hooks, ~86 style keys, and typed props
   per component. All correct, all invisible until you already know the name.

The builder is the answer to those three, in that order of importance: **make the
structure visible, make the loop instant, make the vocabulary reachable.**

A fourth intention emerged during construction and now shapes everything: **the
editor must never lie.** A card that shows stale content, a preview that renders
an old build, a diagnostic that points at the wrong line — each of those costs
more trust than the feature earns. Most of the hard-won rules below exist to
enforce that.

---

## 2.1 The POC — where the design came from, and how to use it

Nearly every interaction in the builder was designed first as a **browser
prototype**, and hundreds of code comments cite it. If you see

```csharp
// POC selectNode(): every route into a file moves the gold ring too.
// POC ".knobs label { width: 70px }" is a FLEX ITEM with the default...
// POC cardHtml: the props-row section exists ONLY for component and hook cards
```

those are references to that prototype's own function names and CSS selectors.
Without it those comments are unreadable; with it they are precise.

### Where it is

| Path | Lines | What it is |
|---|---|---|
| `ruitkUiBuiler/index.html` | ~2970 | **The live one.** Newest; kept in step as the design evolved. (The folder name is misspelled - "Builer" - and it sits at the package root, so it currently ships inside the `.unitypackage`. Both are known and neither is load-bearing.) |
| `Plans~/ruitk-ui-builder-poc/index.html` | ~2050 | The original snapshot, moved under `Plans~/` at campaign start (VE-R7) so Unity and the dist ignore it. Ships with a `README.md` describing the interaction model. |

Open either by double-clicking. **No server, no build, no dependencies** - a
single self-contained HTML file with inline CSS and JS.

### What it contains

A mock of the whole editor, driven by a hardcoded model rather than real files:

- One canvas with **semantic zoom** and the three LOD bands.
- Cards with title, props row, hook chips and a collapsed JSX outline.
- **Edges that anchor at usage rows**, not card borders.
- Hovering a state chip lights every `{gold}` usage in the rows and the source.
- A **live preview with prop knobs**, plus the generated `.uitkx` source.
- `ShopScreen` is a scripted demo (working cart); every other component renders
  generically from the model - attributes, `@if`/`@foreach`, hook state, `var`
  body lines and imported styles are all evaluated.

### How it is used

**As the interaction spec, not as a specification of behaviour.**

- **Use it for**: layout, spacing, colour, what a gesture should feel like, what
  a menu contains, what a card shows at each zoom level. When a comment says
  "POC `.ctx .sep`", the prototype is the authority on what that row looks like.
- **Do NOT use it for**: correctness. It has no parser, no compiler, no tree, no
  save. Its model is a JS object literal. Every hard rule in this document -
  isolation, the save-only contract, one-assembly-per-group, undo scoping -
  exists in the real builder and has no counterpart in the prototype.

Where the two disagree about behaviour, **the real builder is right and the
prototype is stale**. The one deliberate divergence recorded in the code is the
source pane: the prototype renders a read-only listing, while the real pane
re-parses live and uses edit/apply as the commit gesture.

It is a design reference and a shared vocabulary. It is not a test, and nothing
verifies the two stay in step.

---

## 3. The one idea everything follows from

> **The TREE is the model. Disk is a projection of it, written only on Save.**

When you open a folder, the builder reads every `.uitkx` in it **once** and
builds an in-memory tree. From that moment:

- Every card, every edge, every preview render, every diagnostic is computed
  **from the tree**, never from the files behind it.
- Editing changes buffers in memory. Nothing is written.
- **Save** projects the whole tree to disk in one pass: creates, renames, moves,
  overwrites, deletes.
- **Abort** throws the tree away and reloads from disk.

Two consequences that are easy to get wrong:

- **Deletion is absence.** A deleted module is not flagged deleted; it is simply
  no longer in the tree. Save notices its file is orphaned and removes it. There
  is no pending-deletion list to keep in step, and therefore no way for one to
  drift.
- **A module that has been created, renamed, moved or edited answers as the TREE
  says it is, not as disk says.** Asking the filesystem about a module the tree
  owns is a bug, not an optimisation. The filesystem is consulted only for
  modules the open tree does not own — a hand-written file elsewhere in the
  project that this tree imports — and every such fall-through is reported
  (see §9.4).

This is the single most misunderstood thing about the builder. Three separate
defect waves (UB-194, UB-198, UB-223, and the whole ISO-A..G isolation campaign)
were all the same root cause: **something asked the disk, or the loaded assembly
table, about a tree that only exists in memory.**

---

## 4. Anatomy — where the code lives

~24,400 lines under `Builder/` (Editor-only assembly `Ruitk.Builder.Editor`).

| Area | Lines | What it owns |
|---|---|---|
| `Editor/` | 7,254 | `BuilderWindow` — the shell, all gestures, save/abort, keyboard |
| `Editor/Canvas/` | 6,123 | The canvas: graph model, graph builder, drawing, and `CanvasView.uitkx` (the canvas is itself written in `.uitkx`) |
| `Editor/Document/` | 3,038 | The tree, modules, naming, specifiers, undo ledger, reload journal, edit sessions, signature editing |
| `Editor/Controls/` | 2,721 | `CodeField` (the source pane), search menu, inline editor overlay, context menu, cursor |
| `Editor/Preview/` | 1,552 | The live preview pane and its render scheduler |
| `Editor/Library/` | 1,457 | Library pane, folder pane, drag service |
| `Editor/Lsp/` | 1,000 | Language-server client, schema cache, dotnet locator |
| `Editor/Compile/` | 925 | The preview compiler and the tree-backed module source |
| `Editor/Lang/` | 357 | Thin wrappers over the shared language library |

**The canvas is dogfood.** `CanvasView.uitkx` is a RUITK component compiled by the
same source generator the builder edits files for. Editing the canvas means
editing `.uitkx`, and a parse error there breaks the builder itself.

---

## 5. The window — five panes

```
┌─ toolbar: Layer ▾ | Import .uxml | History | Trace | How to drive it | Save | Abort
├──────────┬───────────────────────────────────┬──────────────────────┐
│ FOLDERS  │                                   │  LIVE PREVIEW        │
│  (tree)  │                                   │  (focused component) │
├──────────┤            CANVAS                 ├──────────────────────┤
│ LIBRARY  │        (cards + edges)            │  STATE / knobs       │
│ (search) │                                   ├──────────────────────┤
│          │                                   │  SOURCE — .uitkx     │
└──────────┴───────────────────────────────────┴──────────────────────┘
                     status bar: gesture hints
```

Splitters between panes are draggable and persisted.

### 5.1 Folders pane
The tree's own folder structure. Selecting a file focuses it. Reflects the tree,
so a renamed or created module appears here before it exists on disk.

### 5.2 Library pane
Searchable palette, in sections: **Native elements** (from the LSP schema, ~79),
**Custom components** (the open tree's own exports), **Hooks** (the 21 from
`HookRegistry`, plus hook modules in the tree), **Style modules**, **Util
modules**. Items are dragged onto the canvas.

Every section except Native elements is a **projection of the graph**, refreshed
whenever the graph changes (UB-225 — it used to refresh only on mount, so
renaming an export left the old name in the list).

### 5.3 Canvas
See §6.

### 5.4 Preview pane
See §8.

### 5.5 Source pane
The focused module's `.uitkx` text, syntax-coloured, with a diagnostics strip
underneath. Read-only until **edit**; then **apply** re-parses, **cancel**
restores. See §7.7 for why that session is more delicate than it looks.

---

## 6. The canvas

### 6.1 Semantic zoom (LOD)
Three levels, selectable by name from the toolbar or reached by zooming:

| Layer | Zoom | What a card shows |
|---|---|---|
| **L0 Architecture** | < 0.45 | A pill: name + kind colour. Edges only. |
| **L1 Cards** | 0.45–0.80 | Title, signature, imports, hook chips, collapsed markup outline. |
| **L2/L3 Edit** | > 0.80 | Everything: attributes, code islands, directive badges, per-export style entries. |

The zoom floor is 0.30 (raised from 0.18, which produced unreadable cards).

### 6.2 A card
One card per **module**. Sections, top to bottom:

1. **Title bar** — kind badge + name. Draggable (moves the card). Right-click
   opens the card menu: Props…, Rename…, Delete, and a create submenu.
2. **Signature row** — `Card(string label, int count = 3)`, syntax-coloured.
   **Clickable**: opens the props gestures (§7.6).
3. **IMPORTS** — one row per import, showing the specifier. Right-clickable.
4. **BODY — HOOKS & STATE** — a chip per hook call (`useState → value,
   setValue`), then the *code island*: every setup statement that is not a hook,
   shown as an editable block. `+ hook` and `+ code` chips add to it.
5. **RETURN — MARKUP** — one row per element, indented by nesting, with
   attributes at L2. Directive heads (`@if`, `@foreach`, …) get their own badged
   rows.
6. **EXPORTS detail** — style and util modules list their exports and entries
   here instead of markup.

Card position is user-draggable and **persisted per tree** (`BuilderCanvasConfig`,
keyed by tree membership rather than by a derived root — UB-221, because a
re-filed folder elects a different head and the layout looked brand new).

The **selected** card is painted last so it is never covered — UI Toolkit has no
z-index, so paint order is document order.

### 6.3 Edges
An import produces an edge. Crucially, edges **anchor at the usage row**, not the
card border: the arrow leaves the exact markup row that instantiates the target.
Broken edges (unresolved import) are drawn distinctly.

### 6.4 Viewport culling
Only cards within roughly one viewport build their full subtree; the rest render
as a sized empty box (UB-81). The box must keep its size because the edge painter
measures `card-{index}` to place arrows.

---

## 7. Editing — every gesture

Everything here mutates **buffers**. Nothing writes to disk.

### 7.1 Inline editors
Click an attribute value, a directive header, a hook chip, or a style entry at
L2 and a floating editor opens over it, seeded with the current text.

### 7.2 Attributes
- **Add** — searchable menu, typed from the schema for native elements and from
  *declared props* for components, with a free-text fallback.
- **Remove** — by name, or by emptying the value.
- Values are edited inline.

### 7.3 Structure
- **Add child element** — searchable element list.
- **Wrap in…** — the five directives, seeded compile-clean (`@if (true)`,
  `@for (int i = 0; i < 1; i++)`, …), then the header editor opens.
- **Clause management** — add `@else`/`@else if` to an `@if`; `@case`/`@default`
  to a `@switch`. New cases insert above `@default`.
- **Unwrap** a single-clause directive, keeping its children.
- **Delete** a row (a directive head takes its whole block).

### 7.4 Drag and drop
- **Library → markup row**: top edge inserts *before*, bottom edge *after*,
  middle nests *inside*.
- **Library hook → BODY**, **style/util module → card** (adds the import).
- **Existing row → another row**: reorder **within the same file only**.
  Cross-component moves are refused with a toast; the payload is
  `move:<path>:<rowIdx>` and the source path is compared against the target.
- Drop targets are resolved by a **fresh hit-test on pointer-up**, never from
  render-state closures (UB-31), so the first drop is as accurate as the last.

### 7.5 Modules
- **Create** — component / style / hooks / utils, from the card menu or `+ new`.
- **Rename** — one undoable action covering four edits: the export it declares,
  the file name, the folder when the module owns one, and **every importer's
  specifier and binding**. Import specifiers are then re-derived from where
  modules actually ended up, so the string surgery only has to be right about
  names, not paths.
- **Delete** — refused while another module imports it, naming the referrers.

### 7.6 Props (the signature row)
Added in 0.19.0. Click the signature or use **Props…**:

- **Add** — searchable type menu (types the tree already uses, then common ones,
  then free text) → name → required or default. A required prop is inserted
  **before** the first optional one, because C# rejects the other order.
- **Rename** — declaration, its uses in the component's own body, and the
  attribute at every call site in the tree, as ONE undo.
- **Remove** — strips the attribute at every call site the tree knows about,
  reports how many callers it touched, and warns if the body still uses it.
- **Make required / optional** — "required" is not a stored flag; it IS the
  absence of a written default, so the toggle writes one or removes it.

The text surgery lives in `Builder/Editor/Document/BuilderSignatureEdit.cs` —
pure, Unity-free, and checked outside Unity. It exists to survive: generic
argument lists (`Dictionary<string, int>` is one parameter, not two), lambda
defaults (`Action<int> f = i => { }` — the arrow is not the default's `=`),
braces inside attribute values, braces inside *strings*, an attribute whose name
prefixes another, and markup inside a C# string literal.

### 7.7 The source-pane edit session
`edit` snapshots the buffer; `apply` re-parses; `cancel` restores the snapshot.

**The snapshot carries its file** (`BuilderSourceEditSession`). It used to be a
bare string, and every consumer assumed it belonged to whatever was focused *now*
— so editing one component, clicking another card, and pressing Esc restored the
first module's entire text into the second's buffer (UB-224). Changing focus now
ends the session; nothing is lost, because typing is applied to the buffer live
and the ledger still holds it.

### 7.8 Undo
`BuilderActionLedger`. One entry per **gesture**, holding every `(file, before,
after)` triple it produced, so one Ctrl+Z reverts all of them or none. Typing
coalesces into a single entry per file within 1.5 s. Redo is the tail past the
cursor; recording a new action truncates it.

The ledger **survives a domain reload**, because the tree does. What does not:
an entry left open mid-gesture, and entries naming files the restored tree no
longer has (dropped whole, never half).

---

## 8. The preview

### 8.1 What it renders
The **focused component**, mounted for real: `RootRenderer`-equivalent host,
fiber reconciler, live hooks. Not a picture — you can click its buttons and its
`useState` updates.

### 8.2 Knobs
Primitive props of the component's generated props class, **reflected** off the
compiled type. Defaults are taken from the component's first usage in the tree,
so a preview of a child shows values its parent actually passes.

### 8.3 Failure behaviour
A failed compile **keeps the last good render** and reports the error above it.
The preview never goes blank because of a transient parse error.

### 8.4 The known hole
The component is mounted **bare** — no ancestors, no context. Any component
depending on an ancestor (`Router`, `provideContext`, a portal target, a signal
scope) gets null from `UseContext` and renders nothing, **silently**. Fixing this
generally is Part 3 of `Plans~/BUILDER_ISOLATION_PLAN.md` (RTR-2a/2b/2c).

---

## 9. The compile pipeline — the subtle part

This is where most misunderstandings live.

### 9.1 Two compilers
Unity's own compile handles saved files. The builder has its **own instance** of
the HMR compiler (`BuilderPreviewCompiler` → `UitkxHmrCompiler`) which compiles
**buffers**. They coexist; families converge after Save because registration is
global last-write-wins.

### 9.2 Where source comes from
- `SourceOverlay` — a read fast-path returning buffer text for a path.
- `IModuleSource` (`BuilderModuleSource`) — answers *existence*, *reads*, and
  *sibling discovery* **from the tree**, which the overlay could not. This is
  what makes the compile isolated.

### 9.3 What gets rebuilt
- **Dirty** = differs from what it was last **BUILT** from — *not* "unsaved".
  Under save-only everything is unsaved forever, so keying on that rebuilt
  everything on every keystroke. Keying on *dirty-vs-built* also fixes the
  reverse case: type a label and type it back, and the module is clean again
  (UB-194).
- **Close upward** — importers of anything changed are added (UB-198: editing a
  style rebuilt the style and nothing that used it).
- **Restrict to the focus closure** — only the previewed component and what it
  imports can affect what is on screen.

### 9.4 One assembly per group
Components in the batch compile into **one assembly** (`CompileBatch`).

This is not an optimisation, it is a correctness requirement. A generated body
reads its props with `__rawProps as FooProps` — an `as`, not a cast. Two
same-named props types in two assemblies do not throw; the match fails, the
null-coalesce substitutes a fresh instance, and the component renders with
**every prop defaulted, silently**. One assembly means one type. It is also
parity: a real Unity compile puts every component of an asmdef in one DLL.

Style, hook and util modules are **not** union-eligible and compile one assembly
each, first, in import order. They must therefore be excluded from the
same-assembly invariant check, or the closure looks permanently split and every
round forces a full rebuild.

If the union declines (parse failure, duplicate namespace+name, Roslyn error) the
per-file path runs instead, because that is what surfaces the real error.

### 9.5 Language version
The hot compile pins to the version Unity reports via `CompilationPipeline`
(C# 9.0 on Unity 6), not `latest`. A preview that accepts more than the compiler
is not a preview — `var f = () => { }` compiled in the preview and failed the
next real build.

### 9.6 Instrumentation
With **Trace** on:
- `[RUITK Builder] compile round:` — module count, component count, whether the
  union was eligible, the focus.
- `union compile DECLINED … Reason:` — a warning when the union bails.
- `[RUITK Builder] perf (last second):` — canvas render / compile round /
  diagnostics pass, each with count, total and worst.
- `buffer write:` — every buffer write, with size delta and **what the text now
  declares**; a warning when a file's name and its declared export disagree.
- Fall-through report — every module path the tree could not answer and the
  filesystem was asked about instead.

---

## 10. Diagnostics

Three tiers, merged in the source pane:

| Tier | Source | Examples |
|---|---|---|
| **T1** | The parser | unclosed tag, bad expression |
| **T2** | The shared analyzer | UITKX0105 unknown element, 0109 unknown attribute, 0111 unused parameter, **0115 missing required prop** |
| **T3** | Roslyn, via the language server | CS errors in C# expressions |

T2 needs to know what exists: the **known-element set** and the **attribute
contracts** are built from the tree and refreshed whenever the graph changes.

A contract's `Known` set may be **null**, meaning "I don't know the full accepted
set — skip the unknown-attribute check for this element". Null is not empty. The
builder knows what a component *declares* (hence what is required) without
necessarily holding the schema for what it *accepts*.

---

## 11. Save and Abort

**Save** projects the tree to disk in one pass, in dependency order: create new
folders and files, apply renames and moves, write changed buffers, delete
orphans. A tree begun from the empty state has no folder to infer, so Save asks
once, then moves the pending modules there before writing.

**Abort** discards the tree and reloads from disk.

**Domain reloads** are survived: the tree serializes with the window
(`BuilderReloadJournal` covers the unsaved case and asks before restoring).

---

## 12. Invariants — the rules that must not be broken

1. **Never ask disk about a module the tree owns.**
2. **Nothing is written outside Save.**
3. **One compile unit per connected component group** — a component's props type
   must exist in exactly one assembly at a time.
4. **A snapshot, an edit, a diagnostic and a rewrite name their own file** —
   never "the focused one".
5. **A projection of the graph refreshes when the graph does** — library,
   known-element set, preview notes.
6. **A gesture is one undo entry** — never half.
7. **Silence is a bug.** A refusal, a fall-through, a declined union, a skipped
   module: each says so.

---

## 13. Failure modes learned the hard way

Useful because they recur, and because they are what a newcomer reinvents.

| Symptom | Root cause |
|---|---|
| A new tree renders another tree's components (UB-223) | Resolved a child by *name* across loaded assemblies; every previously opened tree leaves its swap assemblies loaded. Resolve through the **import**. |
| Editing a style updated nothing (UB-198) | The batch was not closed upward to importers. |
| An edit taken back kept showing (UB-194) | Batch keyed on "unsaved" instead of "differs from last built". |
| Canvas layout scrambled on Save (UB-220/221) | Layout keyed on a derived root, which a folder move re-elects. Key on membership. |
| One card holding another's text (UB-224) | An edit session that could not name its own file. |
| Library listing a renamed-away export (UB-225) | A graph projection refreshed only on mount. |
| Parent won't compile against a child's new prop (UB-226) | The compile asked "is this child new?" when the question was "has its shape changed?" |
| Preview accepted code Unity rejected | Hot compile at `-langversion:latest`. |
| Every click rebuilt everything | Non-component modules counted in the same-assembly check, so the closure looked permanently split. |

---

## 14. Deliberate non-capabilities

- Cannot move a markup row between components (delete and re-add).
- Two trees in the same project may share a component name; they are kept apart
  by **where the file is**, not what it is called.
- No object/config route definitions — routes are `<Route>` children only.
- The `element={…}` attribute is displayed but never authored by the builder.
- Read-only modules (immutable packages) render but cannot be edited.

---

## 15. Where to look

| Question | File |
|---|---|
| How is the tree modelled? | `Document/BuilderTree.cs`, `Document/BuilderModule.cs` |
| How does a card get built from text? | `Canvas/BuilderGraphService.cs` (`PopulateCardDetail`) |
| What does the canvas actually draw? | `Canvas/CanvasView.uitkx` |
| What happens on a gesture? | `BuilderWindow.cs` |
| What gets recompiled and why? | `Compile/BuilderPreviewCompiler.cs` |
| Where does compile source come from? | `Compile/BuilderModuleSource.cs` |
| How is a prop added/renamed/removed? | `Document/BuilderSignatureEdit.cs` |
| How does undo work? | `Document/BuilderActionLedger.cs` |
| How is the preview mounted? | `Preview/BuilderPreviewPane.cs` |
| What does the LSP provide? | `Lsp/BuilderLspClient.cs` |
