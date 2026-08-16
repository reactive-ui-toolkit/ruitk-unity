# RUITK Builder — Bug and Gap Register

Companion to `VISUAL_EDITOR_PLAN.md` (what to build) and `POC_PARITY_SPEC.md`
(what the web POC does). This file is the **defect register**: everything known
to be wrong, missing, fake, or undecided in the shipped builder.

Branch: `feat/ruitk-builder`. Every item has a stable `UB-##` id — reference
these in commits, never the line numbers, which move.

## How to use this file

- **Test fixture** for every item is the same tree the campaign has used
  throughout: `UitkxTestFileDoNotTouch.uitkx` and its 3 imported peers
  (4 files, ~11 graph nodes). "Reproduce" means: open that file in the
  builder and do the named gesture.
- An item is **CLOSED** only when it has been seen working on screen. "Code
  committed" is not closed — the campaign already burned a round on fixes that
  were committed, gated, and never rendered.

### Status vocabulary

| Status | Meaning |
|---|---|
| `OPEN` | Confirmed defect, not fixed. |
| `UNVERIFIED` | Fix committed, never confirmed on screen. |
| `DESIGN` | Blocked on a decision, not on code. See §8. |
| `CLOSED` | Seen working on screen. |
| `ACCEPTED` | Deliberate divergence, do not re-flag. |

## Campaign ledger — 2026-08-16 execution run

The whole register was implemented in eight waves on `feat/ruitk-builder`:
W1 `bf12c1d2` data truth · W2 `412392c1` directive head rows · W3 `7f591852`
canvas visuals · W4 `fa96ce16` drag machine · W5 `0a261394` editor
intelligence · W6 `777a9c3d` hook truth + loud preview · W7 `2ce57600` IDE
directive drift · W8 `580c32d9` the 41-agent adversarial review's 18 confirmed
fixes (its deferred tail is REMAINING_WORK UB-REV) · W9 `91182808` the owner's
field report: the Builder assembly had been CS0433-dead in Unity since W6 (the
editor-variant language DLL exported a second public HookRegistry — variant
now internal; the rsp smoke gate is blind to this class because it compiles
Shared from source, noted), five samples carried committed cp1252 mojibake
since the samples flatten (repaired), the source pane's C# body colours via
the LSP's Roslyn-merged semanticTokens/full with the local tokens as the
between-edits fallback, code islands share the same colouring passes, plus
the window-close NullReferenceException and the reload/mount log spam.
Gates on every wave:
validate-uitkx, SG-backed csc smoke, machine-path; W6/W7/W8 also SG 1879/1879
+ LSP 180/180 and committed-DLL rebuilds. Round-2 and round-3 captures
verified the visible surface on screen (round 3's style-card/panel-l2 frames
were invalid — the owner's browser held focus). The item texts below describe
the defects AS FOUND; current state lives here.

| Item | State | Note |
|---|---|---|
| UB-01/02/03 | `CLOSED`(render) | round 3: `@if`/`@else`/`@foreach` head rows with indented children at L1 AND L2 (GalagaGame/GameScreen), badge colours per keyword; wrap/clause MENUS + moves remain interactive-unverified |
| UB-04 | `CLOSED` | suites pin the behavior; grammar/VSIX halves are presentation-only and ride the owner's next F5 |
| UB-05 | `PARTIAL` | source pane fully treated; the six inline canvas editors are deferred as REMAINING_WORK UB-05a |
| UB-06 | `UNVERIFIED` | T3 relay wired; needs a CS error seen in the pane |
| UB-07 | `CLOSED` | knownElements live — custom tags classify teal on screen (round 3), UITKX0105 armed |
| UB-08/09/10/11/13/14 | `UNVERIFIED` | menu/insertion content — popup menus are not capturable; verify by driving |
| UB-12 | `UNVERIFIED` | registry arity + truncation note |
| UB-15 | `UNVERIFIED` | loud preview failures (hook-module copy with real signature/consumers seen in round 3) |
| UB-20 | `CLOSED` | round-2: L0 leaves the pill's right edge, terminal dots on targets |
| UB-21 | `CLOSED` | premise corrected — palette already matched the POC; the custom-tag teal shipped in W5, seen in round 3 |
| UB-22 | `CLOSED` | round-2/3: dots paint in the overlay, no occlusion |
| UB-23 | `CLOSED` | round 3: GameScreen's long markup scrolls inside the capped section, card bounded |
| UB-24 | `UNVERIFIED` | typography was already POC-correct; the real fix was the 0.18→0.30 zoom floor (+ persisted-zoom clamp, W8) |
| UB-25 | `UNVERIFIED` | coloured edit overlay with auto-degrade (W8 fixed the restore-ordering bug); interactive check |
| UB-26 | `OPEN` | layout crowding shrank with the caps but long swoops remain; layout follow-up if the owner still minds |
| UB-30/31/32 | `UNVERIFIED` | drag machine is gesture-driven; owner drive-through (W8 added button filter, root backstop, canvas-scoped hit-test) |
| UB-40 | `CLOSED` | layer dropdown seen in rounds 2 and 3 |
| UB-50/51 | `UNVERIFIED` | carried from round 1; round 3's style-card frame was invalid |
| UB-60..66 | `UNVERIFIED` | interactive surfaces, review-only as before |

---

## 1. Coherence — is what the builder shows actually real?

The question this section answers: when the builder tells you something, is it
reading the real language services, or approximating? Anything approximate is a
lie the user will eventually act on.

### Ground truth: what the language actually implements

Established by reading `ide-extensions~/language-lib/Parser/UitkxParser.cs`
and `SourceGenerator~/Emitter/CSharpEmitter.cs`. Record it here because the
register below depends on it and because two IDE layers already disagree with
it (UB-04).

A directive is a **child block in markup**, and its body is **raw C# containing
`return (…)` statements** — not bare JSX:

```
@if (count >= 0) {
  return ( <Label text={$"ok {count}"} /> );
} @else {
  return ( <Label text="no" /> );
}
```

**Five constructs are implemented**, each lowered to an IIFE:

| Directive | Clauses | Binds | Lowers to | Yields |
|---|---|---|---|---|
| `@if` | `@else if` (unlimited), `@else` | — | `Func<VirtualNode>` with real `return`s | one node or `null` |
| `@for` | — | init var(s) | `Func<VirtualNode[]>`, returns rewritten to `__r.Add` | array |
| `@foreach` | — | loop var (header split on first `" in "`) | same | array |
| `@while` | — | — | same | array |
| `@switch` | `@case`, `@default` | — | `Func<VirtualNode>`, per-case brace scope | one node or `null` |

`@case` labels end at a single `:` (`::` preserved for `global::`).
`@break` is parsed but has **no AST node** — it is silently swallowed as an
optional `@switch` case terminator and is an error anywhere else.
`@continue` and `@code` are always errors. Unknown directives raise
**UITKX0305** (Error).

### UB-01 — Builder exposes 2 of the 5 real directives `OPEN` `HIGH`

The row context menu offers exactly `Wrap in @if` and `Wrap in @foreach`
(`BuilderWindow.cs:695-705`). Missing: **`@for`, `@while`, `@switch`**, and
`@if`'s `@else if` / `@else` chain.

This is **not** a POC-parity gap. The POC only ever knew `@if` and `@foreach`
(`ruitkUiBuiler/index.html` contains those two strings and no others). Copying
the POC here copies a mock's toy vocabulary into a real editor. A file using
`@switch` opens in the builder today and the builder cannot describe, edit, or
preserve that construct through a move.

Blocked on the model question in §8.1.

### UB-02 — One badge slot per row: nested directives display WRONG today `OPEN` `HIGH`

Better news and worse news than first assessed. The graph service is **not**
regex-guessing markup — `WalkMarkup` (`BuilderGraphService.cs:832-919`) walks
the real AST and already visits all five constructs, including every
`@else if` branch and every `@case`. The bottleneck is purely the
representation: a directive is stamped onto the **first element row its clause
produced** via `Attach` (`:963-974`), and `BuilderCardLine` has **one badge
slot** (`BadgeText`/`DirectiveText`/`DirectiveLine`).

Confirmed consequences, each traceable to a line:

- **Nested directives show the wrong badge.** `Attach` bails on collision
  (`:969-970` — `if (row.BadgeKind != 0) return`). When a clause's first child
  is itself a directive, the inner one attaches first and the **outer construct
  silently vanishes from the card**. `Samples/.../NestedSection.uitkx`
  (`@if` inside `@foreach`) renders as a bare `@if` — the loop is invisible.
- **Empty clauses vanish.** `:966-967` — a branch whose body produced no rows
  (an empty `@else {}`) has nothing to attach to and disappears entirely.
- **Clause membership is invisible.** Directive bodies are walked at the SAME
  depth (`:874` passes `depth`, not `depth + 1`), so rows 2..N of a clause are
  indistinguishable from rows outside it.
- **An `@else` clause can be dragged away from its `@if`.** Every row with
  `rowIdx > 0` arms a move payload (`CanvasView.uitkx:1027-1028`), and a
  badge row's move carries `DirectiveLine..MatchingCloseLine`
  (`BuilderWindow.cs:991-995`) — for an `@else` badge that is the clause alone.
  Dropping it elsewhere strands an orphan `@else` and the file stops parsing.
- **`@for`, `@while`, `@case` and `@default` share BadgeKind 4**
  (`:895, :903, :913-914`) — the model cannot tell them apart except by
  display string.

Resolution designed and adopted — see §8.1.

### UB-03 — `Wrap in @foreach` emits code that cannot compile `OPEN` `HIGH`

Two independent failures, both of them certain:

1. **Unbound identifiers.** `WrapRowInDirective(…, "@foreach (var item in
   items)", …)` emits that header literally. `items` is not in scope in an
   arbitrary component and `item` is never referenced by the wrapped row. A
   correct wrap must offer in-scope enumerable symbols for the collection,
   bind the loop variable, and let the wrapped markup reference it.

2. **Loops yield an array, so they are illegal as a single root.** `@foreach`
   lowers to `Func<VirtualNode[]>`; `CSharpEmitter.cs:2600` raises
   **UITKX0025** when a loop directive is the single root of an inline `{…}`
   expression. Wrapping the only root row in `@foreach` therefore produces a
   hard compile error, where wrapping it in `@if` does not. The builder has no
   idea this asymmetry exists.

The second point generalises: **`@if`/`@switch` are node-valued and `@for`/
`@foreach`/`@while` are array-valued**, and the builder must know which it is
inserting and where arrays are legal.

### UB-04 — IDE layers offer directive syntax the parser rejects `OPEN` `MED`

Not a builder bug, but the builder consumes the same LSP, so it inherits every
one of these. Found while establishing the ground truth above:

- `lsp-server/CompletionHandler.cs:1216-1218` and `HoverHandler.cs:591-593`
  offer an **arrow** switch form (`@case $1 => $0`, `@default => …`). The
  parser only accepts `:` (`UitkxParser.cs:1300-1317`). Accepting the
  completion produces code that does not parse.
- The same arrow form is in `grammar/uitkx.tmLanguage.json:286`,
  `vscode/syntaxes/uitkx.tmLanguage.json:286`, and
  `visual-studio/UitkxVsix/UitkxClassifier.cs:1162` as a `switch-arm-label`
  rule.
- `CompletionHandler.cs:475-482` completes `break` / `continue` inside loop and
  switch blocks. `@continue` is **always** an error; `@break` has no AST node.
- `UITKX0110` (`UnreachableAfterBreakOrContinue`) is declared
  (`DiagnosticCodes.cs:99-104`) and never emitted — its only consumer is a
  suppression filter in `DiagnosticsPublisher.cs:632-636`.
- `SourceGenerator~/Diagnostics/UitkxDiagnostics.cs:124-132` holds a dead
  `UnknownDirective` descriptor whose message still lists a valid `code`
  directive and whose severity is `Warning` against the parser's `Error`.
  Nothing in `SourceGenerator~` references it.
- `uitkx-schema.json -> controlFlow` descriptions are **stale**: they claim
  `@if` "generates a ternary", `@foreach` "generates .Select().ToArray()", and
  `@switch` "generates a C# switch expression". The emitter generates IIFEs
  for all five (`CSharpEmitter.cs:1983-2128`). The builder's directive menu
  (§8.1) must consume the NAMES, never these descriptions, until the schema is
  fixed.

### UB-05 — Editable surfaces have no colouring, no completion, no diagnostics `OPEN` `HIGH`

Every place the builder lets you type should behave like an editor. Nine of the
ten do not. Audited surface by surface:

| Surface | Colour | Completion | Diagnostics |
|---|---|---|---|
| Source pane, **read** mode | yes | n/a | yes |
| Source pane, **edit** mode | **no** | Ctrl+Space only | yes |
| Canvas attribute-value editor (`CanvasView.uitkx:1152-1180`) | no | no | no |
| Canvas directive/badge editor (`:1039-1067`) | no | no | no |
| Canvas hook-chip editor (`:494-522`) | no | no | no |
| Canvas style-entry editor (`:829-859`) | no | no | no |
| Canvas code-island editor (`:636-666`) | no | no | no |
| Canvas line-rewrite (`:798-827`) | no | no | no |
| `BuilderSearchMenu` field (`BuilderSearchMenu.cs:195-224`) | no | substring filter over caller-supplied items — not LSP | no |
| Library search (`BuilderLibraryPane.cs:140-165`) | no | substring filter | no |

Even the one working completion is thin: Ctrl+Space only
(`CodeField.cs:361-364`), unreachable outside edit mode because `_input` is
`display:none` (`:228`); the popup is pinned to a fixed corner
(`top:22, right:8`, `:404-405`) rather than the caret; capped at 25 items
(`:420`); no filter-as-you-type, no docs, and it ignores the LSP's `textEdit`
range, just calling `InsertAtCaret(insertText)` (`:431`).

### UB-06 — Half the LSP client is dead code `OPEN` `HIGH`

`Lsp/BuilderLspClient.cs` implements the requests and nothing calls them:

| Method | Line | Called? |
|---|---|---|
| `RequestCompletion` | `:508` | yes — `BuilderWindow.cs:585`, the only caller |
| `RequestSchema` / `RequestHooks` | `:499-500` | yes |
| `RequestWorkspaceGraph` | `:505` | yes |
| `RequestHover` | `:515` | **never** |
| `RequestSemanticTokens` | `:529` | **never** |
| `RequestFormatting` | `:522` | **never** |
| `RequestComponentProps` | `:502` | **never** (see UB-13) |
| `DiagnosticsPublished` event | `:48`, fired `:395` | **no subscriber** |

The last one has real consequences: the server publishes tier-3 / Roslyn
diagnostics (`lsp-server/DiagnosticsPublisher.cs`), the client marshals them to
the main thread (`:383-399`), and nothing listens. `BuilderLanguage.cs:80-82`
even documents that T3 "is overlaid by the caller" — no caller does. **The
builder can never show a type error.**

Colouring and diagnostics come instead from the in-process facade
`Lang/BuilderLanguage.cs` (`Parse` `:30`, `Tokens` `:103`, `Diagnose` `:83`),
which is legitimate and fast — but it is tier-1 + tier-2 only.

### UB-07 — `knownElements` is null everywhere, so custom tags are never classified `OPEN` `HIGH`

**This is the root cause of the "syntax/style component colouring is off"
observation, and it is a one-line class of fix.**

`CodeField.SetContent(..., knownElements)` is passed **`null` at all four call
sites** (`BuilderWindow.cs:464, 534, 606, 1730`). That null flows into both
`BuilderLanguage.Tokens` and `BuilderLanguage.Diagnose`, so:

- custom-component tags are never classified and therefore never get the
  custom-tag colour — they fall back to whatever the default is;
- the tier-2 unknown-element check
  (`language-lib/Diagnostics/DiagnosticsAnalyzer.cs:669`) is guarded by
  `projectElements != null` and so **can never fire in this window**.

The builder already holds both the schema and the graph. It just never passes
them.

Separately, the palette itself may also be wrong — see UB-21.

### UB-08 — Style-key menu emits non-compiling code `OPEN` `CRITICAL`

`BuilderWindow.cs:1293-1304` hardcodes 26 `(Key, Type)` tuples. The real
surface is `Shared/Props/Typed/StyleKeys.cs` — **92** keys, with 92 matching
properties on `Style` (`Style.cs:384-1269`).

**Two of the 26 offered keys do not exist and produce a compile error when
picked:**

- `("Gap", "length")` (line 1299) — `Gap` appears nowhere in `Shared/Props/`.
- `("UnityTextAlign", "text-align")` (line 1302) — the RUITK member is
  `TextAlign` (`Style.cs:943`). `UnityTextAlign` is the IMGUI name.

Picking either writes `Gap = Px(8),` or `UnityTextAlign = TextMiddleCenter,`
into a `.style.uitkx` export, which then fails to compile. This is the single
most damaging item in the register: the builder actively breaks the user's
file.

The other **66 of 92** keys are simply unreachable — every `Border*Color`,
`BorderTopLeftRadius`, `Left/Top/Right/Bottom`, `Rotate/Scale/Translate`,
`Transition*`, `Filter`, `AspectRatio`, `UnitySlice*`, `TextShadow`,
`WordSpacing`, `Visibility`, `Overflow`, `WhiteSpace`.

Related: `ValueTemplatesFor` (`:1306-1319`) hardcodes ~35 value literals keyed
on pseudo-types (`"length"`, `"color"`, `"justify"`) that are themselves
invented by `s_styleKeys` and correspond to nothing. The real sources are
`uitkx-schema.json -> styleKeyValues` (25 keys with their true enum value sets)
and `Shared/Props/Typed/CssHelpers.cs` for helper tokens.

### UB-09 — Attribute menu falls back to an 11-entry list, 3 of them fake `OPEN` `HIGH`

`Lsp/BuilderSchemaCache.cs:29-42` holds `s_common`, 11 hardcoded `AttrInfo`.
The schema the builder **already downloads** carries
`intrinsicElementAttributes` (60) + `structuralAttributes` (2: `key`, `ref`)
and the builder throws both arrays away, reading only `elements[*].attributes`.

- **3 of the 11 do not exist**: `usageHints`, `onMouseEnter`, `onMouseLeave`
  (the real events are `onPointerEnter` / `onPointerLeave`).
- Missing entirely: `key`, `ref`, `className`, `visible`, `enabled`,
  `tabIndex`, `extraProps`, and all 20 `*Capture` handlers.

Worse, the fallback is **silent**. `BuilderSchemaCache.Register` has exactly
one call site (`BuilderLibraryPane.cs:240`), inside `Attach`. If the LSP throws
— the `catch` at `BuilderLibraryPane.cs:276` returns early — `s_byElement`
stays empty and every element degrades to those 11 entries with no warning. The
user cannot tell a real schema from a dead one.

`AttrInfo` (`:16-24`) also drops the schema's per-attribute `description`, so
the menu can never show docs.

### UB-10 — "Add child element" offers 7 of 72 elements `OPEN` `MED`

`ShowAddChildMenu` (`BuilderWindow.cs:739-773`) uses `NativeTagOrder`
(`BuilderLibraryPane.cs:35-38`) as its entire native list. That constant is
correct where it was written — it is a **curation order** for the library pane,
which then renders all 72 schema elements after it. Reusing it as a source list
silently truncates to 7.

`SeededTag` (`:776-779`) seeds default attributes for exactly 2 tags (`Label`,
`Button`); every other element inserts bare.

### UB-11 — Hook insertion is hardcoded for 4 of 21 hooks `OPEN` `HIGH`

The LIBRARY pane is correct — it merges the live `ruitk/hooks` response
(`HookRegistry.AmbientHookNames` + `GetDocMap()`, 21 hooks) at
`BuilderLibraryPane.cs:253-274`. My earlier note that it showed only 4 was
wrong; the 4 in `HookTemplates` (`:43-46`) are an offline seed and an ordering
hint.

What is actually broken is **insertion**. `BuilderWindow.cs:917-921` hardcodes
declaration text for `useState` / `useEffect` / `useMemo` / `useRef` and falls
back to `"var value = " + name + "();"` for everything else — wrong for most of
the remaining 17. `HookRegistry` already emits real stubs
(`HookStubsStaticForm` / `HookStubsInstanceForm`), so the correct declaration
is available and unused.

Second-order: the offline seed means a dead LSP shows a plausible 4-hook list
instead of an error.

### UB-12 — Preview STATE panel silently truncates `OPEN` `MED`

`Preview/BuilderPreviewPane.cs:453-462` hand-partitions hooks into
`s_slotHooks` (7) and `s_noSlotHooks` (2) to index `HookStates` positionally.
That covers 9 of the 21 registry hooks. For a component using `useSignal`,
`useDeferredValue`, `useTransition`, `useAnimate`, `useTweenFloat`, `useSfx`,
`useUiDocumentRoot`, `useStableCallback`, `useImperativeHandle` or
`provideContext`, `CollectStateNames` bails at `:489` and the STATE panel
truncates **with no indication that it did**.

Proper fix is at the source: slot arity belongs on `HookRegistry` as a field,
not in a hand-maintained list in the preview pane.

### UB-13 — Component props are re-derived by string-splitting `OPEN` `MED`

`PropsOf` (`BuilderWindow.cs:1265-1291`) splits `BuilderCanvasNode.Signature`
on `(`, `,`, `=` and last-space to recover `(Name, Type)` pairs, then appends a
synthetic `("key", "list key")`.

The real thing already exists on both ends: `ruitk/componentProps` ->
`RuitkComponentPropsHandler` (`lsp-server/RuitkBuilderRequests.cs:145-179`)
returns `Name`, `Type`, `Doc`, `Line` from the real `WorkspaceIndex`, and the
client method `BuilderLspClient.RequestComponentProps` (`:502-503`) is
implemented. It has **zero call sites in the repo**. A live real source sitting
next to a fake parser that is used instead.

### UB-14 — Attribute insertion emits `{value}` for every non-string type `OPEN` `HIGH`

`DefaultValueFor` (`BuilderSchemaCache.cs:61-72`) is a name heuristic that ends
in a catch-all:

```csharp
if (on*-handler || type.Contains("Action")) return "{handler}";
if (name == "style" || type == "Style")     return "{styleName}";
if (type == "string" || name is "text"/"label") return "\"text\"";
return "{value}";   // bool, int, float, enum, Color ALL land here
```

Used at `BuilderWindow.cs:1192`, written into the tag at `:1205`. So
`focusable` becomes `focusable={value}`, `pickingMode` becomes
`pickingMode={value}`, a `Color` prop becomes `{value}` — **none of which
compile**. Together with UB-08 this is the second way the builder writes broken
code into the user's file.

The type is known and is even displayed in the menu label
(`BuilderWindow.cs:1249`) before `DefaultValueFor` discards it. Some invention
is unavoidable — the schema carries no defaults — but it must at least be
type-driven.

### UB-15 — Live preview is real; its failure modes are silent `OPEN` `MED`

The preview is genuinely real, and this is worth recording because the campaign
kept re-litigating it. `BuilderPreviewPane.Mount()` (`:831-842`) builds a real
`HostContext` via `RuitkBootstrap.CreateHostContext`, with its own budgeted
scheduler (`BuilderRenderScheduler`, 4 ms/tick), and renders the generated
component's real `Render`. Clicking a Button in the preview runs the real
handler, mutates real fiber state, and re-renders; interactive controls are
exempted from the click-to-select gesture (`:793-796`). The STATE strip reads
`fiber.ComponentState.HookStates` twice a second (`:366-409`) and writes back
(`:502-602`). Edits re-render after a 300 ms debounce, and **no Save is
needed** — `SourceOverlay` (`BuilderPreviewCompiler.cs:44`) makes
`UitkxHmrCompiler` read the unsaved buffer.

What is wrong is that **every failure path is silent**:

- `Mount()` has **no try/catch** (`:831-842`), and `CreateDefaultProps` is a
  bare `Activator.CreateInstance` (`:991-1004`), so reference props are `null`.
  A component that dereferences a required prop throws out of the render into
  `EditorApplication.update`.
- `CompileDirty` **breaks on the first failure** (`BuilderPreviewCompiler.cs:75-77`),
  so one broken sibling stops the loop before the focus file is ever reached;
  `focusResult` stays null and the pane keeps the stale render with no message.
- Only **dirty** sessions compile (`:59-67`). A freshly opened file depends on
  the assembly Unity's generator already produced — if the project does not
  compile there is no preview and no explanation.
- Import ordering resolves **relative specifiers only** (`:130-135`); `~/`
  cross-tree and `@` package imports are skipped, so a dirty peer reached via
  `~/` can compile out of order.
- On compile failure the pane shows one generic line
  (`BuilderWindow.cs:1876-1880`); the real error only reaches the Unity console.
- Knob seeding is heuristic: literals are mined from the *first* usage found on
  the canvas (`:1120-1137`), and object props get knobs regex-mined from card
  text and seeded with fabricated values (`:1142-1234`).

### Correctly reading a real source today — do not "fix" these

Library native elements (all 72, `BuilderLibraryPane.cs:226-251`); library
hooks (`:253-274`); library components / style / hook / util modules from the
live graph (`:292-355`); the workspace graph
(`BuilderGraphService.cs:152`, with `-32801` retry at `:145-165`, card detail
re-parsed from the live buffer at `:203-221`); `UsedStyleKeys`
(`BuilderWindow.cs:1350-1377`); remove-attribute (`:1150-1173`); and
`CheckSchemaDrift` (`BuilderCanvasHost.cs:408-434`), which cross-checks the
schema against the live `ElementRegistry` and warns on drift.

One scoping caveat worth knowing: the graph is only the **connected component
of the focus file** (`BuilderGraphService.cs:80-84`), not the whole project, so
components in an unrelated tree never appear in the library.

---

## 2. Visual defects the owner observed

These came from looking at the window, which is the only source that has caught
appearance bugs reliably.

### UB-20 — Edges have no terminal point and attach on the wrong side `OPEN` `HIGH`

Every curve should end in a visible point, and the attachment should be the
**right-most** edge of the component, at every layer.

Today (`Canvas/BuilderCanvasDrawing.cs:676-686`, `DrawEdges`):
- the **target** end is `rect.xMin` — the card's **left** edge — and paints no
  terminal dot at all;
- the **source** end at L1/L2 is the measured anchor dot on the import/markup
  row (correct-ish), but at **L0** it is `CardRect(...).center`, so the curve
  starts inside the card body and crosses over it.

Both are faithful ports of the POC (`x1 = r1.right - r1.width/2`,
`x2 = r2.left`). Changing them is a **deliberate divergence from the POC** in
favour of legibility. See §8.3 — the exact geometry is a decision, not a
lookup.

### UB-21 — Source pane token colours are the wrong palette `OPEN` `MED`

Two separate causes, and **UB-07 is the bigger one** — custom-component tags
are never classified at all, because `knownElements` is `null` at every
`SetContent` call site. Fix that first, then re-look.

What may remain after UB-07: the palette itself. Hypothesis to confirm in
`Controls/CodeField.cs` — tokens mapped to VS Code Dark+ (`#569CD6` keyword
blue, `#CE9178` string orange) instead of the POC palette (`#c792ea` keyword
purple, `#c3e88d` string green, `#7fdbca` custom tag).

Note that canvas-side "colouring" is not tokenised at all — it is ad-hoc rich
text (`BuilderCanvasDrawing.cs:198` signature, `:213` hook chip, `:255` style
export head), so it cannot track the palette automatically.

### UB-22 — Anchor dots are occluded by overlapping cards `OPEN` `HIGH`

Root cause: **the dots and the curves are not coplanar.** Anchor dots are
child elements inside each card's subtree (`a-imp-<i>-<r>`, `a-row-<i>-<r>`),
but edges paint into a single overlay layer that is the **last sibling** of the
world (`CanvasView.uitkx:1299-1304`). So an overlapping card draws over the
underlying card's dots while the curves sail over the top of everything — the
exact split visible in the L1 screenshot, where the `UitkxTestFileDoNotTouch`
import dots vanish under `MultiColumnListViewStatefulDemoFunc`.

Fix at the layer where the bug lives: paint the dots in the edge overlay (their
positions are already measured there by `AnchorOf`), rather than bumping card
z-order, which only moves the occlusion to a different pair of cards.

Interacts with UB-23 and §8.2.

### UB-23 — Cards have unbounded height `OPEN` `MED` `DESIGN`

No card section is height-capped; the only `ScrollView` on a card is the
horizontal island scroller (`ruitk-island-scroll`). A component with a long
markup body grows a card until it overlaps its neighbours — which is what
*produces* UB-22 in the first place, and what makes the L1 canvas unreadable on
the fixture.

The fix (cap section height, scroll inside) has a hard consequence for anchors
and edges: a dot scrolled out of its section still reports a `worldBound`, so
the curve would attach to a point outside the visible region. Blocked on §8.2.

### UB-24 — L0 pill cards are unreadably small `OPEN` `MED`

The POC compensates for the 0.30 L0 zoom with enlarged pill typography (26px
title) so cards stay legible. Ours keeps L1 type sizes and shrinks to
unreadable chips — visible in the L0 screenshot, where every card title is a
smear.

### UB-25 — Source pane turns white in edit mode `OPEN` `MED`

Root cause confirmed, and it is not a shade problem. `CodeField` keeps **two**
views: a coloured `.srcline` listing (`:503-567`, `:701-752`) and a plain
`TextField` (`#src-edit`, `:209-234`). `SetEditing` (`:258-268`) hides the
listing and shows the field, and the field has **no colouring at all** — so
edit mode is not a lighter shade of the listing, it is a different, unlit
control.

The POC also drops to a textarea, so this is technically parity. It is still
wrong for a real editor, and it is the same defect as UB-05: the answer is that
edit mode should stay coloured, which means the listing and the editable
surface have to become one control rather than two.

### UB-26 — Edges cross card bodies `OPEN` `LOW`

Partly z-order (UB-22), mostly layout: the auto-placement crowds cards so
curves have no clear channel. Likely resolves as a side effect of UB-23 +
UB-20; re-check after those.

---

## 3. Drag and drop

### UB-30 — Picking up a library item gives no feedback `OPEN` `HIGH`

`BuilderDragService` (`Library/BuilderDragService.cs`) is a single `static
string Payload`. Arming it changes nothing on screen: no drag ghost, no
"picked" styling on the source row, no cursor change. `DragActive` is read only
by canvas rows, to decide whether to band-hint **once the pointer is already
over one**. Between press and arrival at a target row the user has no evidence
anything is happening — which is exactly the reported symptom.

Needs a real drag visual: a ghost element following the pointer carrying the
payload's label, plus a held-down state on the source row.

### UB-31 — The drop band is read from a stale render closure `OPEN` `HIGH`

Root cause, and it explains both "not always accurate" and "the first drop
doesn't even add the component".

`CanvasView.uitkx:997-1010`: `onPointerMove` calls `setDragBand(...)` — fiber
component state. `onPointerUp` then reads `dragBand`. But the handler closes
over the value from **the render that created it**, so a pointer-up that
arrives before the state-driven re-render commits reads the *previous* band.
On the first drop of a session that previous value is the initial state, which
is why the first drop misbehaves the most.

Compounding: `setDragRowKey`/`setDragBand` fire on **every** pointer move, so
the tree re-renders per move and the band lags the cursor by a frame.

Fix: the band and target row are drag-machine state, not render state — hold
them in `BuilderDragService` (or a ref) and read them at drop time. Keep the
`setState` only for the hint's visual.

### UB-32 — No pointer capture; a drop that misses a row is silently swallowed `OPEN` `MED`

Nothing calls `CapturePointer` on drag start. Pointer-up is delivered to
whatever element is under the cursor, so releasing over a card gap, a section
header, or the canvas background never reaches a row's drop handler — and the
world's own `onPointerUp` (`CanvasView.uitkx:123-124`) calls
`BuilderDragService.Cancel()`. The drag evaporates with no message.

The library row is worse: it registers `PointerUpEvent -> Cancel()` on itself
(`Library/BuilderLibraryPane.cs:522-524`) to preserve click-to-insert, so a
press-and-release without travel is indistinguishable from an aborted drag.

Fix: capture the pointer on arm, resolve the drop from the captured stream's
final position by hit-test, and surface an explicit "no valid drop target"
cancel rather than a silent one.

---

## 4. Chrome and controls

### UB-40 — Layer control should be a labelled dropdown `OPEN` `LOW`

`BuilderWindow.cs:131-140` builds three toggle buttons labelled
`L0 Architecture` / `L1 Cards` / `L2 Edit`. Wanted: a single select, with
human labels — `Layer 1 …`, `Layer 2 …`, `Layer 3 …`. Note the renumbering:
the internal presets stay 0/1/2 with zooms 0.30 / 0.75 / 1.25, but the user
sees 1-based names. Keep one mapping table so the label set and the preset
array cannot drift.

---

## 5. Committed but never seen on screen

Carried from parity round 1 (commit `4191c90a`). All gated, none watched.

| Id | Item | Status |
|---|---|---|
| UB-50 | Util module multi-line function signatures render as one collapsed head | `UNVERIFIED` |
| UB-51 | `+ style` / `+ export` affordance shows when a module has no parsed exports | `UNVERIFIED` |

Closed from the same round, confirmed on screen: canvas dot grid; L2 code
island for wrapped export headers; anchor dot on every IMPORTS row; full
native-elements list.

---

## 6. Reviewed from source only — never captured

No screenshot harness state exists for any of these, so their parity is a code
reading, not an observation.

| Id | Surface |
|---|---|
| UB-60 | Context menus — row, card, canvas |
| UB-61 | Searchable menu chrome (`BuilderSearchMenu`) |
| UB-62 | Drag bands and drop hints (see also UB-30..32) |
| UB-63 | Hover states |
| UB-64 | New File dialog |
| UB-65 | Source pane edit -> apply cycle |
| UB-66 | Help overlay |

---

## 7. Accepted divergences — do not re-flag

- **Text rasterisation.** Unity's text engine will never match a browser's.
- **Live preview content.** Ours mounts the real component; the POC scripts a
  fake shop screen. Ours is correct.
- **Save / Abort buttons.** The POC never writes a file. Owner-decided.
- **Library column is fixed-width.** The POC wires only two splitters.

---

## 8. Design decisions (resolved 2026-08-16 with the owner)

All three questions are now decided. This section is the implementation spec;
the POC is explicitly NOT the reference for any of it (its directive knowledge
was two keywords, its edge geometry is what UB-20 exists to replace).

### 8.1 Directive model — DECIDED: clause head rows, schema-named menu

**A directive clause becomes a card row of its own** (`BuilderCardLineKind.
Directive` — the enum member already exists), replacing the `Attach`
badge-stamping. Nesting falls out for free because `WalkMarkup` already
recurses over the real AST; the samples' nested forms (`NestedSection.uitkx`)
become representable exactly, where today they display wrongly (UB-02).

**Model** (`BuilderGraphModel.cs`): head rows reuse `BadgeText` (keyword),
`DirectiveText` (full header — `FillDirectiveText` unchanged), `DirectiveLine`
(clause head line). Add two ints: `CloseLine` (clause close brace) and
`ClauseIndex` (0 = construct head; >0 = structurally bound continuation:
`@else if`, `@else`, `@case`, `@default`). `CloseLine` is computed builder-side
from `ControlBlockPayload.BodyCodeLine` + the newline count of `BodyCode`
(verified: the AST carries per-clause `SourceLine` but no end line — no
language-lib change, no DLL rebuild, no parity ripple).

**Walker** (`BuilderGraphService.WalkMarkup`): each clause emits its head row,
then walks its body at `depth + 1` (switch: `@switch` head, `@case` heads at
`depth + 1`, bodies at `depth + 2`). `Attach` and its collision/empty-clause
bugs are deleted. A clause with setup code before its `return` renders that as
a code island under the head, same as the L2 body island.

**Renderer** (`CanvasView.uitkx`): a Directive row renders as a badge-chip
row. Click = the existing inline header editor (`OnDirectiveEdit` path is
line-based and survives unchanged). Directive rows carry no anchor dot.

**Move/drag rules** (`BuilderWindow`): a `ClauseIndex == 0` head drags the
WHOLE construct — first clause's `DirectiveLine` through the last clause's
`CloseLine`. A `ClauseIndex > 0` head is **not draggable** (an `@else` cannot
live anywhere else — today it can be dragged into garbage, UB-02); its menu
offers edit-condition / delete-clause instead. Element rows inside a clause
drag themselves, as today. Drop band "inside" on a head row inserts at the top
of that clause's body.

**Menu** (`BuilderWindow`): the wrap list is built from
`uitkx-schema.json -> controlFlow` **names** (never the descriptions — they are
stale, UB-04), intersected with a builder-side capability table keyed by name:

| name | role | wrap? | yields |
|---|---|---|---|
| `if` | construct | yes | node |
| `foreach`, `for`, `while` | construct | where an array is legal | array |
| `switch` | construct | yes | node |
| `else`, `case`, `default` | clause-add (head-row menu) | no | — |

A `CheckSchemaDrift`-style startup check warns when the schema names a
directive the table does not cover — the builder can never silently trail the
language again. `break`/`continue` are never offered (no AST node / always an
error).

**Clause adds**: `@if` head menu — "Add @else if" / "Add @else" (disabled when
an `@else` exists); `@switch` head — "Add @case…" / "Add @default" (disabled
when one exists). Each is a small text edit at the clause boundary
(`} @else {`) through `ApplyProgrammaticEdit`.

**Loop wraps bind their collection** (UB-03): the `@foreach` wrap opens a
searchable menu of in-scope enumerables — component props with
collection-shaped types (via `ruitk/componentProps`, which UB-13 wires up —
dependency), hook lhs vars from the card's BODY rows, then the warn-orange
freeform fallback. The loop variable is singularised from the collection name
and collision-checked. Loop wraps are disabled (with a tooltip citing
UITKX0025) where a single node is required.

### 8.2 Card section heights — DECIDED: max-height + scroll, clamped anchors

Sections cap their height and scroll (owner's original preference; the fold
alternative is dropped). The anchor problem the owner spotted is real but
cheap to solve — three mechanics, all riding existing machinery:

1. **Position is already live.** `DrawEdges` re-measures every dot's
   `worldBound` per repaint (`AnchorOf`, `BuilderCanvasDrawing.cs:646-661`).
   A dot inside a scrolled `ScrollView` keeps reporting its true, moved
   worldBound (clipping does not affect bounds), so "where is the row now"
   costs nothing new.
2. **Visibility is one rect test.** Compare the dot's centre against the
   section viewport's `worldBound` y-range. Outside → the row is scrolled out.
3. **Scrolled-out anchors clamp to the viewport edge.** The curve terminates
   at the section's top or bottom edge, in the dot's own colour — never at the
   true-but-clipped position (without the clamp the curve would dive under the
   card chrome, the exact "stick to the hidden part" failure). The clamp edge
   also tells you which way to scroll, and multiple hidden rows bundle there
   naturally. Since dots are painted in the overlay (8.3), the clamped dot is
   painted at the clamp point in the same pass.

   (Variant, owner's suggestion: collapse to a section-header dot instead.
   Equally cheap, but sections have no header dot today, so it adds an
   element; edge-clamp adds none. Either is a one-enum swap later.)

Plus one wire: each section `ScrollView`'s scroller `valueChanged` →
`edgeLayer.MarkDirtyRepaint()`, so edges track the scroll live instead of
lagging to the next repaint.

### 8.3 Edge anchors — DECIDED: dots into the overlay; right-edge source, terminal point

- **Anchor dots move into the edge overlay layer** (owner-approved). The row
  elements stay as invisible measurement markers; the visible glyphs are
  painted by `DrawEdges` in the overlay, so no card can ever occlude them
  (UB-22) and the terminal cap is painted in the same pass.
- **Source attaches at the card's right edge at every layer** — including L0,
  which today uses the card centre and draws curves out of the card's body.
- **Every curve ends in a visible terminal point** at the target end, matching
  the source dot's colour per edge kind.

Default interpretation of "always to the right most": source side. If the
owner instead wants BOTH ends on the right (rail-diagram routing, curves loop
back into the target's right edge), that is a routing change on top of this —
say so and it becomes its own item.

---

## 9. Field reports — owner drive-through, 2026-08-16

The create menu and drag-and-drop passed the owner's hands-on. These are the
defects and feature asks from that session, plus one the screenshots caught
that the owner did not name. All `OPEN` unless marked.

### UB-70 — Enter in a freshly added @case badge editor opens the file in VS2022 `OPEN` `HIGH`

Only on NEW cases (Add @case…), never on existing badges. Hypothesis: the
new-clause editor is pushed via `BeginEditOnDirectiveLine` AFTER the graph
refresh, and the TextField does not have keyboard focus yet when the user
presses Enter — the keystroke lands on the last-focused Unity pane (Project
window with the .uitkx selected → `OpenAsset` → the OS default app, VS2022).
Existing badges open their editor synchronously from the click, so focus is in
the field. Fix direction: the host-pushed inline editors must grab focus when
they materialise, and the badge editor's Enter/Esc must consume the event.

### UB-71 — @switch clause ordering: @default seeds first, @case appends after it `OPEN` `HIGH`

Confirmed in the drive-through screenshots: `Wrap in @switch` seeds `@default:`
as the FIRST arm, and every "Add @case…" inserts above the switch's closing
brace — i.e. after @default, with no way to put a case above it. Fix: the wrap
seeds `@case value:` first (with the wrapped row) and `@default:` last;
"Add @case…" inserts BEFORE the @default arm when one exists, after the last
@case otherwise.

### UB-72 — seeded directive headers do not compile until edited `OPEN` `MED`

`@case value:` and `@if (condition)` reference identifiers that do not exist;
since the preview now reports compile failures loudly (UB-15), every wrap
immediately shows "Preview compile failed … CS1525" until the header is
edited. The owner's report began "when i create a directive and" and cut off —
this is the likely subject; OWNER: please finish that sentence if it was
something else. Options: seed compilable placeholders where a type can be
guessed, or hold the programmatic commit until the header editor closes with
real content (the edit-first flow the badge editor already implies).

### UB-73 — action ledger with global undo/redo `OPEN` `FEATURE`

Owner ask: a visible ledger of every builder action, "down to the smallest
action", with undo/redo across it. Today undo is per-file session buffers
(Ctrl+Z on the focus file); moves, drops, clause surgery, deletes and
cross-file effects (auto-added imports) are only reachable by undoing whole
buffer states file by file. Design: one action log (description + the set of
(file, before, after) buffer pairs per action), a history panel, Ctrl+Z/Y
walking it atomically across files.

### UB-74 — selection-driven keyboard model: Delete removes, Esc cancels `OPEN` `FEATURE`

Owner ask: Delete deletes whatever is selected — element row, directive
clause/block, card, attribute — not just via the context menu; Esc cancels the
active edit anywhere. Needs a real selection model (exactly one selected THING
with a kind, visible focus) rather than today's per-surface selection bits.

### UB-75 — false UITKX0105 on Vector2Field/Vector3Field/… `OPEN` `HIGH`

Caught in the drive-through screenshots (not owner-named): the source pane
flags `<Vector2Field>` etc. as unknown elements. UB-07 wired the check to the
SCHEMA element set, and the schema is missing 7 REGISTERED elements
(Vector2Field, Vector3Field, Vector4Field, Vector2IntField, Vector3IntField,
Hash128Field, UguiHost — the drift warning has said so all along). Two-part
fix: (a) builder-side now — `KnownElementsOrNull` unions the RUNTIME registry
(`ElementRegistryProvider.GetDefaultRegistry().RegisteredNames`, the actual
truth for what renders) so real elements are never errors; (b) root — add the
7 elements to `uitkx-schema.json` with real attribute lists so the palette
and completion offer them (add-unity-version-style work).

### UB-76 — inline-editor intellisense, UN-DEFERRED `UNVERIFIED` `HIGH`

IMPLEMENTED 2026-08-16 (`158813e0`), awaiting the owner's drive-through: one
floating inline editor (fragment-mode CodeField at the window root) replaces
all six in-card TextFields — shared colouring, coloured-edit overlay and
Ctrl+Space completion at the exact mapped file position for every surface
(line-splice for headers/chips/entries, tag-span mapping for attribute values,
re-indented range for islands), with the real buffer re-synced on close. The
overlay owns focus from the frame it opens, so UB-70's stray-Enter race is
structurally gone — verify both together. Original plan text follows.

The owner rejected the UB-05a deferral ("we have many tests that should all be
intellisensed/colored"). Active plan, superseding the REMAINING_WORK entry:
- The four SINGLE-LINE editors (attr value, directive header, hook chip,
  style entry) have exact `(file, line, col)` maps — they get LSP completion
  at the mapped position (buffer synthesized with the in-progress line) and
  the same colouring treatment.
- The two MULTILINE island editors stop being bare TextFields: they embed the
  existing CodeField control, inheriting its colouring, completion, overlay
  diagnostics and coloured-edit machinery instead of duplicating any of it.

### UB-26 follow-up — edge routing, how the field does it (owner question)

Node editors (Unreal Blueprints, Unity Shader Graph, Blender, dagre/ELK-based
web tools) mostly do NOT route around nodes — they make crossings rare and
cheap instead: (1) fixed ports (out-right, in-left) with horizontal-tangent
beziers — we already match; (2) LAYERED AUTO-LAYOUT: columns by dependency
depth with barycenter ordering to minimise crossings (dagre/ELK/Sugiyama —
our import graph is a DAG, ideal for it); (3) hover highlighting/dimming so
remaining crossings stop mattering; (4) optional user reroute pins. Proposal:
a "tidy layout" action running depth-column + barycenter over the graph
(persisted like manual drags), plus hover-highlight of a card's edges with
the rest dimmed. Routing AROUND cards (libavoid-style orthogonal routing) is
the heavyweight option none of the mainstream node editors actually use.
