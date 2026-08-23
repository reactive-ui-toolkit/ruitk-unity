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

### UB-71 — @switch clause ordering: @default seeds first, @case appends after it `UNVERIFIED` `HIGH`

Confirmed in the drive-through screenshots: `Wrap in @switch` seeds `@default:`
as the FIRST arm, and every "Add @case…" inserts above the switch's closing
brace — i.e. after @default, with no way to put a case above it. Fix: the wrap
seeds `@case value:` first (with the wrapped row) and `@default:` last;
"Add @case…" inserts BEFORE the @default arm when one exists, after the last
@case otherwise.

SHIPPED: `WrapRowInSwitch` now seeds `@switch (0) { @case 0: … }` holding the
wrapped row and seeds NO @default at all — "Add @default" stays on the menu and
appends at the closing brace, which is where C# wants it. `AddSwitchClause`
takes the node + row index and inserts a new @case at the @default arm's line
when one exists (`ConstructClause`, the row-returning form of the old
`ConstructHasClause`), else at the closing brace. Also fixed: the wrap emitted
its `);` one indent level too deep in BOTH wrappers — the house form every
sample uses aligns it with its own `return (`.

### UB-72 — seeded directive headers do not compile until edited `UNVERIFIED` `MED`

`@case value:` and `@if (condition)` reference identifiers that do not exist;
since the preview now reports compile failures loudly (UB-15), every wrap
immediately shows "Preview compile failed … CS1525" until the header is
edited. The owner's report began "when i create a directive and" and cut off —
this is the likely subject; OWNER: please finish that sentence if it was
something else. Options: seed compilable placeholders where a type can be
guessed, or hold the programmatic commit until the header editor closes with
real content (the edit-first flow the badge editor already implies).

SHIPPED (option 1, compilable placeholders — option 2 would have had the header
editor anchor to a canvas row that does not exist until the commit):
`@if (true)`, `@for (int i = 0; i < 1; i++)`, `@while (false)`, `@switch (0)`
+ `@case 0:`. @while seeds FALSE deliberately — a true-seeded render loop would
not terminate. Added @case arms seed the next unused integer (`NextCaseLabel`),
so they compile against the seeded subject and cannot collide (CS0152). The
header editor still opens on the seed, so the prompt to replace it is
unchanged; what is gone is the buffer being committed broken.

### UB-73 — action ledger with global undo/redo `UNVERIFIED` `FEATURE`

Owner ask: a visible ledger of every builder action, "down to the smallest
action", with undo/redo across it. Today undo is per-file session buffers
(Ctrl+Z on the focus file); moves, drops, clause surgery, deletes and
cross-file effects (auto-added imports) are only reachable by undoing whole
buffer states file by file. Design: one action log (description + the set of
(file, before, after) buffer pairs per action), a history panel, Ctrl+Z/Y
walking it atomically across files.

SHIPPED exactly that shape: `Builder/Editor/Document/BuilderActionLedger.cs` —
entries of (Description, At, List<(FilePath, Before, After)>), Begin/Record/End
with COLLAPSING nesting (a compound gesture reusing a single-file primitive
stays one entry), a redo tail truncated on new work, a 400-entry cap, and a
`Suppress()` scope so replaying is never itself recorded. `ApplyProgrammaticEdit`
(the funnel all nine delete/edit operations already went through), source-pane
typing and the source-edit cancel all record. Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y now
walk the LEDGER, not the focus file's own stack, so a gesture that touched two
files reverts as one step from whichever file is in focus. A "History" toolbar
button opens a panel listing every action with the cursor drawn live; clicking
any row walks the buffers to that point in one atomic step (`WalkTo`).

### UB-74 — selection-driven keyboard model: Delete removes, Esc cancels `UNVERIFIED` `FEATURE`

Owner ask: Delete deletes whatever is selected — element row, directive
clause/block, card, attribute — not just via the context menu; Esc cancels the
active edit anywhere. Needs a real selection model (exactly one selected THING
with a kind, visible focus) rather than today's per-surface selection bits.

SHIPPED: the fiber's row selection now leaves the component — a new
`onRowSelect(path, rowIdx, line)` prop mirrors into `BuilderCanvasHost`
(`SelectedRowPath/Index/Line`, matching the `_selectPath` pattern the card ring
already used). `BuilderWindow.OnKeyDown` no longer early-returns on unmodified
keys: Delete runs `DeleteSelection` (row selection beats card selection), Escape
runs `CancelActiveEdit` (inline editor, then source-pane edit, then clear the
selection). Both are suppressed while a text surface holds focus, so Delete
still deletes CHARACTERS inside an editor. Every menu guard is honoured by
routing to the same methods — return root refuses with a toast, a continuation
clause deletes as a clause, a construct head deletes its block, and the card
delete goes through `RequestDeleteCard`, which is the referenced-by guard
EXTRACTED out of `ShowCardMenu` so the two paths cannot drift.

### UB-75 — false UITKX0105 on Vector2Field/Vector3Field/… `UNVERIFIED` `HIGH`

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

SHIPPED both halves. (a) `KnownElementsOrNull` unions
`ElementRegistryProvider.GetDefaultRegistry().RegisteredNames`, so schema drift
can cost completion but can never manufacture a UITKX0105 for a tag that
actually renders. (b) All 7 (Vector2/3/4Field, Vector2Int/Vector3IntField,
Hash128Field, UguiHost) added to `uitkx-schema.json` with their real attributes
read off the Props classes. NOTE: the schema is an EMBEDDED LSP resource — the
builder reads it over `RequestSchema`, so the palette/completion half only
lands once the LSP server is rebuilt and the extensions re-published.

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

Owner drive-through rounds (2026-08-16/17), both fixed same-day:
- Round 2 (`ecec78a0`): coloured-edit showed two scrollbars (listing scrollers
  now hidden while the input's inner ScrollView drives, styled dark); common
  C# keywords (void/int/bool/true/null/…) were plain outside Roslyn coverage
  (keyword regex widened, string cells protected); islands scrolled both axes
  with a stale horizontal offset clipping line heads (vertical-only + wrapping
  lines + MaxHeight 220 — deliberate POC divergence).
- Round 3, "the real gap in coloring still remains — look at all that white":
  identifiers/methods/types rendered plain ink in BOTH the source pane and
  every fragment. Root cause was two-layer: `ColorFor` painted
  Function/Variable #CFCFDA (indistinguishable from ink #D6D6DC) and
  Attribute/Property nothing, and the Roslyn merge drops "property name" +
  unresolved "identifier" spans while fragments get no tokens at all, so most
  identifiers had no colour source. Fix (CodeField only, no LSP change):
  real VS-dark palette in `ColorFor` (function gold #DCDCAA, member/variable
  blue #9CDCFE, type teal #4EC9B0, number green #B5CEA8) plus a lexical
  identifier/number pass in `BuildLineRichText` that claims only cells no
  token/string/{expr} run coloured — call sites gold, tag-position names via
  the schema split, dotted members + camelCase blue, PascalCase teal —
  keyword/comment passes still win. Covers pane, islands and all fragment
  editors deterministically; server tokens refine on top. `UNVERIFIED`.

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

## 10. Field reports — owner drive-through, 2026-08-17

ALL IMPLEMENTED 2026-08-17 in one execution wave together with the UB-71..75
tail (owner: "execute everything that's remaining in the plan, the whole thing,
defer nothing"). Every item below is `UNVERIFIED` — gates green
(validate-uitkx 0, SG-backed csc smoke EXIT=0, machine-path clean, SG 1879/1879,
LSP 180/180, committed generator + language DLLs rebuilt Release), none of it
driven on screen yet. Notes per item record what shipped.

ONE MANUAL STEP OUTSTANDING (UB-75 palette half only): the embedded schema
reaches the builder through the LSP server, and the clone's builder resolves
`Server~/UitkxLanguageServer.dll` first (`RuitkDotnetLocator`). That file was
locked by the live server process of the running Unity session, so it still
carries the pre-UB-75 schema. With Unity closed, run:

```
dotnet publish ide-extensions~/lsp-server -c Release --self-contained false `
  -o "<embedded-clone-package-root>/Server~"
```

(the clone root is the one the embedded-clone sync procedure already uses —
never write it into a tracked file).

Nothing else waits on it — the repo copy under `ide-extensions~/vscode/server`
is already rebuilt and verified to embed all seven new elements, and the
false-UITKX0105 half of UB-75 is fixed builder-side by the registry union,
which needs no server at all.

### UB-77 — diagnostics console is not copyable `UNVERIFIED` `MED`

The error console/window (UITKX0105 storm in the capture) renders as plain
Labels: no selection, no Ctrl+A, no copy. Owner ask: make the whole console
selectable/copyable so all diagnostics can be selected and pasted elsewhere.
Likely shape: selectable text (UI Toolkit `selection`-enabled text element or
a read-only multiline field) plus an explicit "Copy all" affordance; Ctrl+A
inside the console must select console text only, not trigger canvas-wide
selection.

SHIPPED: the console is a vertical ScrollView holding a SELECTABLE Label with
the window's dark scroller chrome. The hard 4-line cap is gone — every
diagnostic line is in the text, so a copy cannot silently drop the tail.
Ctrl+A selects all and Ctrl+C copies, both scoped to the console element so
they never race the canvas or the source editor; right-click adds "Copy all
diagnostics" (disabled when empty). Also fixed en route: fragment mode set
`display = None` on the label and never restored it, so any CodeField instance
that had run a fragment recolor kept its console hidden forever — display is
now driven explicitly in both directions off the scroll container.

### UB-78 — component signature lines are not coloured `UNVERIFIED` `MED`

The card's signature block (name + full props signature under the title) is
one flat-grey Label while everything around it now colours. Route the
signature text through `CodeField.BuildLineRichText(line, null)` like islands
and detail entries already do — the UB-76-round-3 lexical pass then gives
types teal, parameter names blue, defaults (null/false/numbers) their real
colours for free. Watch rich-text vs. the signature's wrapping/ellipsis.

SHIPPED: `BuilderCanvasDrawing.SignatureRichText` now calls
`CodeField.BuildLineRichText(signature, null, boldPrefix)` instead of emitting
the parameter list RAW. The name keeps its bold via a new `boldPrefix` run
break (splitting at `(` BEFORE colouring would have robbed the name of the
paren that classifies it as a call, so the whole line is coloured once and the
run is broken at the paren). Required a classifier fix that helps everywhere:
`List<T>` was being read as markup because the identifier after `<` looked like
a tag — a `<` only opens a tag when the character before it is not an
identifier character, which is exactly what separates `<Label` from `List<`.

### UB-79 — raise the max zoom-OUT level `UNVERIFIED` `LOW`

Owner ask: allow zooming further out to see a bigger stretch of canvas. Pure
clamp change on the zoom range (plus verifying grid/edge/text rendering stays
sane at the new minimum scale); explicitly NOT a layout change.

SHIPPED: floor 0.30 -> `BuilderCanvasDrawing.ZoomMin` = 0.10 (a ~9x larger
visible area), ceiling unchanged as `ZoomMax` = 2.2. The range had been THREE
duplicated literal pairs (the canvas wheel handler, the host ctrl-wheel
handler, the persisted-layout clamp) that had to agree; they now share the one
pair. Card LOD already collapses to pills below 0.45, so nothing new renders at
the new floor.

### UB-80 — component list: double-click to focus a card `UNVERIFIED` `MED`

The palette's "custom components" section already lists every component on
the canvas. Owner ask: double-clicking a list entry focuses/centres that
card in the viewport (pan + sensible zoom, maybe a brief highlight pulse).
Note the double-click-navigation precedent: canvas rows already double-click
to navigate to a component's file — keep the two gestures consistent.

SHIPPED: `BuilderCanvasHost.FocusNode(path)` centres the camera on the card
(cam = viewportCentre - cardCentre * zoom, the same screen = world*zoom + cam
model the wheel handler anchors on) and moves the gold selection ring to it; a
card taller than the viewport pins near its top instead of centring, which
would otherwise scroll its title off-screen. `BuilderLibraryPane` entries carry
their node's FilePath and a double-click fires `FocusComponent` INSTEAD of
arming the drag, so the focused card is not also dropped somewhere on release.
All four workspace sections get it (components, style, hook and util modules),
not only components; schema natives have no file path and fall through to the
drag-arm unchanged.

Owner follow-up same day ("double clicking a custom component just place it in
view, it doesnt also fully zoom it which it should"): FocusNode is now a real
FRAME operation, not a pan. It solves the zoom so the card fills the viewport
(88% margin) and only then centres. Card width is LOD-dependent and LOD is
zoom-dependent, so the fit is solved twice - the second pass uses the width the
first pass's zoom implies, which converges because there are only three bands.
ZoomChanged fires so the toolbar layer readout and the scroller restyle track
it, exactly as the wheel and preset paths do.

### UB-81 — max zoom-IN gets slow, cause DIAGNOSED `UNVERIFIED` `MED`

Owner report: at high zoom the canvas becomes sluggish. Diagnose before
fixing — candidate causes: every card/edge still rendered + hit-tested when
mostly off-screen (no viewport culling), rich-text label re-layout at scale,
edge bezier tessellation in generateVisualContent, dot-grid painting the full
element at fine pitch. Profile first (Editor profiler on the builder window),
name the dominant cost in the register, then fix at that layer.

DIAGNOSIS — read from the code, NOT from a profiler run (the Unity profiler
could not be driven from the implementing session; the owner should confirm the
win on screen). Dominant cost: **there was no viewport culling at all**.
`CanvasView` looped `graph.Nodes` unconditionally and built every card's full
subtree wherever it sat, and L2 is precisely where that explodes — each markup
row adds a flex wrapper plus THREE labels per attribute, each with pointer
callbacks, so the element count is roughly 4 x cards x rows x attributes and
none of it was bounded by what was on screen. That is why the slowdown tracks
zoom-IN (the L2 band, `zoom >= 1.05`) rather than the number of visible cards.
Ruled out on inspection: the dot grid is screen-space, fixed 26px pitch and
capped at 16000 quads, so it is zoom-invariant.

SHIPPED: cards more than one viewport outside the visible rect render as a
sized empty box instead of their sections (`BuilderCanvasDrawing.IsNearViewport`,
screen rect = world * zoom + camera, inflated one viewport on each side so a
pan never pops a card in at its edge). The box is deliberately KEPT rather than
dropping the element: the edge painter measures `card-{index}` and, when it
cannot find one, both guesses the geometry and schedules a 12-repaint retry
burst — and `CurrentZoom`'s setter resets that counter, so a dropped element
would have restarted the burst on every zoom step. Viewport size arrives as
`viewportW/H` props (pans and zooms recompute the cull inside the fiber from its
own camera state; only a RESIZE needs a push, wired on GeometryChangedEvent and
guarded on an actual size change). Card height for the placeholder and the cull
test comes from the graph layout's own `EstimateCardHeight`, now memoised per
node (`CachedHeight`, reset on re-parse) because the cull consults it for every
node on every render. A viewport that has not measured yet returns "near" —
a missing measurement must never hide content.

## 11. Field report — owner drive-through, 2026-08-17 round 2

Verdicts on the §10 wave: identifier colouring, the copyable console, signature
colouring, zoom-out, double-click focus and the zoom-in speed all confirmed on
screen. `UNVERIFIED` still: UB-72 and UB-75 (owner: "not sure how to test").
The rest produced this round, all fixed same-day.

### UB-82 — window shortcuts never fired: nothing was focusable `UNVERIFIED` `HIGH`

Owner: "none of the keybind works" (UB-73 undo/redo) and "nothing get deleted"
(UB-74). One root cause for both, and it predates them: a `KeyDownEvent` is
dispatched to the FOCUSED element, `rootVisualElement` was not focusable, and
nothing in the canvas is — so `OnKeyDown` only ever ran while a TextField
happened to hold focus, which is precisely when the field wants the key for
itself. Ctrl+S/Ctrl+Z had the same hole since they were written. Worse, UB-74's
own "ignore this while typing" guard meant Delete could never fire at all: it
needed focus somewhere for the event to route, and bailed whenever that
somewhere was a text field.

FIX: the root is focusable and takes focus on any pointer-down whose TARGET is
not a typing surface. The decision reads the target, not the current focus, and
the handler is TrickleDown — canvas rows `StopPropagation` on pointer-down, so
a bubble-phase handler would never have seen the very clicks that select the
thing Delete acts on. `IsTypingSurface` is now one shared predicate for both
the focus grab and the OnKeyDown guard.

### UB-83 — every menu action no-ops on a card that was never opened `UNVERIFIED` `HIGH`

Owner: "clicking on wrap with switch without selecting the component doesnt do
anything so no point even bring the menu up if its a must - i think it should
work regardless." Correct on both counts. A canvas card is parsed straight from
disk, so its rows, menus and drop targets all exist without a document session
— and every mutation began `_workspace.TryGet(path)`, which returns null for a
file the user has never opened, then silently returned. The menu was honest;
the action was not.

FIX: `EditSession(path)` = `TryGet ?? OpenSession`, opening on demand. Applied
to all 23 read-then-write sites (clause adds, wraps, deletes, line/tag edits,
indent and brace-matching helpers, `ApplyProgrammaticEdit`). Deliberately NOT
applied to `ReadBufferOrDisk` (its disk fallback is the point) or to
`ApplyLedgerWrites` (an undo must not resurrect a file that is no longer open).
Read-only sessions still refuse to mutate one layer down, where that decision
belongs.

### UB-84 — inline editor opened un-typable and narrower than its row `UNVERIFIED` `MED`

Owner: "it doesnt focus that input, it should automatically focus it so you can
type right away" and "the input doesnt cover the entire selected item which
looks weird".

FIX (focus): the edit box is `display:none` until `SetEditing`, so it had no
layout in the tick where `Focus()` was called and the call was a silent no-op.
Focus is now retried on later ticks until `CodeField.EditorHasFocus` confirms
it took (8 attempts, 24 ms apart), which also survives a canvas re-render
landing between the open and the focus — exactly what a freshly seeded
directive header does.

FIX (width): the panel spanned the clicked BADGE. It now spans the row the
anchor belongs to (`row-{card}-{row}`, the same handle the drag hit-test walks
for), falling back to the anchor for non-row surfaces.

### UB-85 — a long page card panned but never zoomed `UNVERIFIED` `MED`

Owner: "double clicking on showcaseDemoPage doesnt zoom it just pans - and it
seems like the only component where this happens". Not the root: the height.
UB-80's frame fitted BOTH axes, so a card with hundreds of markup rows solved
to `ZoomMin` — it did zoom, all the way out, which is indistinguishable from
not zooming.

FIX: fit WIDTH only. Card width is uniform per LOD, so the gesture now lands at
the same readable zoom for every card instead of one that swings with how much
markup a file happens to hold; a card taller than the viewport stays pinned
near its top, where its title is.

### UB-86 — source pane scrolled itself, and kept the offset across files `UNVERIFIED` `MED`

Owner: "Right side code display scrolls both veriticle and horiznotal and in
random direction for no reason.. it should not be scroll unless the user does
and the scroll should reset when you select another component."

Two causes. `FocusLine` (row click -> source sync) used `ScrollView.ScrollTo`,
which moves BOTH axes to reveal the whole element — and a source row is as wide
as the longest line in the file, so revealing one row yanked the pane sideways
to a position nobody asked for. And `SetContent` never reset the offset, so
selecting another component showed it already scrolled to wherever the previous
file had been left.

FIX: `ScrollRowIntoView` adjusts the vertical offset only, leaves the
horizontal offset exactly where the user put it, and does nothing at all when
the row is already fully in view. `SetContent` resets the offset when the file
actually changes.

### UB-87 — Delete destroyed sample FILES off disk `UNVERIFIED` `CRITICAL`

Owner, 2026-08-17: "the actual file got deleted the one i started the builder
with". Two files were lost from the embedded clone — `ShowcaseDemoPage.uitkx`
and `ShowcaseFieldsPanel.uitkx`, plus their metas. Both were restored with
`git restore` in the clone (it is a checkout; the repo copies were never
touched, which is why the repo showed no changes).

ROOT CAUSE, and it is mine — UB-74's `DeleteSelection` falls back to the CARD
selection when no row is selected, and deleting a card deletes a FILE. Three
things compounded:
1. The card selection is NEVER empty. `Mount` rings the focus file's card from
   the frame the window opens, so "no row selected" always resolved to "delete
   the file you just opened".
2. Deleting a row CLEARS the row selection. So two Delete presses in a row
   deleted an element and then the whole file.
3. There was no prompt. A menu click at least names the file it is about to
   delete; a keypress said nothing.

The owner's other symptom — "only showed 1 module even zoomed out max and no
connections, and i undo alot but didnt help" — is a consequence, not a separate
bug: the deleted root was the file that imports everything, and the ledger
holds BUFFERS, so no amount of undo could bring a file back.

FIX: the confirmation lives in `RequestDeleteCard`, the shared guard, so the
MENU path is covered too — a click is one slip away from the same loss. It is a
modal naming the file, and it says plainly that undo cannot reverse it. The
delete itself now uses `AssetDatabase.MoveAssetToTrash` rather than
`DeleteAsset`: same keystroke, but the file is recoverable afterwards.

SUPERSEDED SAME DAY by UB-88 - see below. The owner corrected the premise:
nothing may reach disk before Save at all, which dissolves the GUID problem
rather than working around it.

### UB-88 — deletion violated the save-only contract `UNVERIFIED` `CRITICAL`

Owner, correcting UB-87's fix: "Nothing should be created or deleted or
anything, really applied until save is clicked, user can play with the builder
millions of times and years and unless they save.. everything get discarded..
so this whole thing doesnt apply."

Right, and it makes UB-87's GUID reasoning moot rather than a tradeoff to be
managed. `BuilderWorkspace` documents the save-only disk contract (VE-D2) in
its own class comment — "during editing nothing here writes" — and deletion was
the one operation that ignored it, calling `AssetDatabase` the instant it was
asked. That is why a keypress could destroy files the user never saved, and why
no amount of undo helped.

FIX: a deletion is a pending INTENT, like every other edit.
- `BuilderWorkspace` owns `_pendingDeletes` (serialized, so it survives a domain
  reload with the rest of the session). `MarkForDeletion` / `UnmarkForDeletion` /
  `IsPendingDelete`, and `HasUnsavedChanges` counts them.
- The card leaves the canvas immediately: `LoadTreeAsync` takes an `isHidden`
  predicate that drops the node and every edge touching it, wired to
  `IsPendingDelete`. The file itself is untouched.
- `SaveAll` performs the deletions in the same batch as the writes, via
  `MoveAssetToTrash`, then clears the list. `AbortAll` discards them.
- The ledger records deletions as `Change.IsDeletion`, so Ctrl+Z un-marks and
  Ctrl+Y re-marks. Nothing is re-created, so no GUID churns.
- The confirmation moved OFF the delete gesture and ONTO Save, which is the
  moment it stops being reversible; it lists every file by name and says they
  go to the trash.

### UB-89 — builder shortcuts leaked into Unity `UNVERIFIED` `HIGH`

Owner: "i think ctrl+z and ctrol + shield + z.. and y also happened in the
window unity not just the ui tree.. which is also bad it should all be
contained within that window!!"

`StopPropagation()` ends UI Toolkit's own propagation but leaves the underlying
IMGUI event alive, so the Editor went on to run its GLOBAL Undo/Redo for the
same keystroke — mutating the scene behind the user while the builder undid a
buffer. Every builder shortcut had this, including Ctrl+S.

FIX: one `ConsumeKey` helper — `StopImmediatePropagation` + `PreventDefault` +
`imguiEvent.Use()`. Using the imgui event is the part that tells the Editor the
keystroke is spoken for.

### Provenance note — the six "modified" sample files were NOT builder writes

Owner: "no change should have happened, i changed nothing deliberately !! didnt
save one". Confirmed, and the builder is exonerated: all six files in the clone
are byte-identical to the repo working copy, and the repo's `Samples/` tree is
clean against its own HEAD. They read as modified only because the CLONE sits
on an older commit (`4ac0b2cd` vs the repo's tip) and was robocopy-synced from
the repo at some earlier point. Nothing to revert - the content is already the
committed content. Worth remembering when reading clone `git status`: it is a
deploy target, not a second checkout of the same commit.

## 12. Field report — owner drive-through, 2026-08-18 round 3

Dogfooding decision recorded: the canvas is a real `.uitkx` component, the
surrounding chrome is hand-built UI Toolkit C#. Owner: "for now let it stay,
later we will make 1 go at trying to convert it all to full or close to full
dogfooding". Not a defect - tracked as a future campaign, not in this wave.

Owner question answered: a tree with no persisted layout opens at zoom **1.0**
(`BuilderCanvasConfig.Zoom` defaults to 1f), which is LOD 1 - the 340px card
with signature, imports, hook chips and markup rows but no attributes or code
islands. A tree the user has opened before restores its saved zoom, clamped
into the live range on load.

### UB-90 — capabilities document + the skill that keeps it current `UNVERIFIED` `PROCESS`

Owner: a `plans/` document listing everything the builder can do, updated with
every capability change, "the reason for that is if we need to add that same
builder to the other project we will have something to go by". Needs the doc
AND a skill making the update mandatory, since a doc nobody is obliged to
touch rots within a wave.

### UB-91 — the loading state is invisible `UNVERIFIED` `LOW`

`ShowMessage("Loading tree…")` is a small dim label in the canvas's top-left.
Owner wants it centred and much larger, or a spinner in the middle.

### UB-92 — inline editor opens with the WINDOW unfocused `UNVERIFIED` `HIGH`

The UB-70 symptom is back and the real cause is finally identified: every menu
is a separate `EditorWindow` (`BuilderSearchMenu : EditorWindow`, `ShowPopup`).
When it closes after a pick, focus does NOT return to the builder - it lands on
whatever Unity had focused before, typically the Project window. So the inline
editor's `Focus()` succeeds INSIDE the builder's panel while the builder window
itself is not the focused window, and the keystroke goes elsewhere: Enter
reaches the Project window, which runs `OpenAsset`, which opens VS2022. The
focus-retry loop added in UB-84 cannot help, because the field it is focusing
is in an unfocused window. `BuilderSearchMenu` already records the invoking
window in `s_pointerWindow` for positioning - it just never focuses it back.

### UB-93 — wrap/edit commit semantics `UNVERIFIED` `MED`

Owner: after a wrap the header input must be focused and typable, with Escape
cancelling the whole addition, and Enter or clicking away committing. Escape
currently cancels the EDIT but leaves the seeded directive in the buffer, so
"cancel" does not undo the wrap that produced it.

### UB-94 — only markup rows are selectable/deletable `UNVERIFIED` `MED`

Owner: "everything should be delitable, not just the footer part of the
component, hooks/ custom code.. etc.. i should be able to select and delete it
without right clicking and delete xyz." Needs selection to cover hook chips,
import rows, code-island lines and style/util export entries, each with a
delete that knows its own line range.

### UB-95 — flat wrap items should be one submenu `UNVERIFIED` `LOW`

Five sibling "Wrap in @x" rows crowd the row menu. Owner wants a single
"Wrap in…" opening a submenu of the directives.

### UB-96 — code islands render with wrapped, ugly formatting `UNVERIFIED` `MED`

Owner: "our coloring is phenomenal - formatting is terrible". The island in
DISPLAY mode wraps long lines mid-expression; the source pane and the island's
own EDIT mode both look right. Self-inflicted: the round-2 fix for "islands
should scroll vertically only" set `CodeIslandLine` to `WhiteSpace = WsNormal`,
which stopped the horizontal scrolling by making every long line wrap. The ask
was about SCROLLING, not wrapping - the line should stay on one line and be
clipped at the card edge.

### UB-97 — focus selects the whole text `UNVERIFIED` `MED`

Owner: entering edit mode selects everything (immediately in an island, after
one extra click in the source pane). A field that selects-all on focus means
the first keystroke destroys the content.

### UB-98 — edit-mode scrollbar sits inside the text `UNVERIFIED` `MED`

The coloured-edit overlay's scroller is inset from the right edge, overlapping
the text instead of riding the boundary.

### UB-99 — inline editor mismatches the row at non-default zoom `UNVERIFIED` `MED`

The overlay is unscaled window chrome positioned over a SCALED canvas row, so
at high zoom the highlighted row is large while the editor keeps its 12px font
and 30px height - "the selected wrap is big and the input is ugly".

### UB-100 — attribute editors do not auto-focus `UNVERIFIED` `MED`

Same root as UB-92: clicking an attribute value opens the editor without the
keyboard, so the user must click again before typing.


All of UB-90..UB-100 implemented 2026-08-18 in one wave; gates green
(validate-uitkx 0, SG-backed csc smoke EXIT=0, machine-path clean). Key
implementation notes:
- UB-92 is the ACTUAL fix for the long-standing UB-70 "Enter opens VS2022":
  `BuilderSearchMenu.CloseAndRestoreFocus` focuses the invoking window before
  running the pick, and the invoker is captured at Place() time rather than
  relying on the positioning-only `s_pointerWindow`. The overlay also calls
  `BuilderWindow.FocusExisting` before focusing its field.
- UB-94 adds a second selection channel, `onLineSelect(path, from, to, label)`,
  mirrored on the host beside the row selection; selecting either kind clears
  the other. Wired to hook chips, import rows, code islands and style entries;
  Delete routes the range through the same DeleteLinesInFile primitive.
- UB-96 reverses the round-2 `WsNormal` island change: the ask then was about
  SCROLLING, and wrapping was the wrong lever. Islands keep vertical-only
  scrolling and clip instead.
- UB-99 scales the inline editor from the anchor's worldBound height, so no
  zoom value needs threading into the overlay.

## 13. Field report — owner drive-through, 2026-08-18 round 4

Owner confirmed fixed this round: the code islands now colour (UB-76 wave).
Everything below was raised in the same message and fixed same-day.

### UB-101 — island editor floats instead of replacing the island `UNVERIFIED` `MED`

Owner: "the body code input doesnt match the size (as if its outside island) -
maybe we should replace in place the display mode to edit mode same size and
everything." The multiline branch ignored the anchor's measured size and used a
fixed 140-380 band plus a +24 pad, so the editor opened as a different-sized
box overlapping its own island. It now takes the anchor's exact worldBound for
both position and size, with the 3px lift suppressed for multiline.

### UB-102 — a selected chip or island showed no selection `UNVERIFIED` `MED`

Owner: "when you click 1 time on the field it should be orange selected to
demonstrate that its selected". UB-94 made these things selectable but only the
import rows got a highlight. Hook chips now take the warm band plus the accent
outline, and islands get a `CodeIslandSelected` style — the same signal a
selected markup row uses, so "this is what Delete removes" reads identically
everywhere.

### UB-103 — edges leave a card from the middle of its content `UNVERIFIED` `MED`

Owner: "the linking points are in bad position, they should be right top most
of each component." The SOURCE end of every edge sat at the import or markup row
that produced it, so at L2 on a tall card the endpoint was far down the card and
the curve ran back across its own content. Edges now leave from the card's
TOP-RIGHT corner (`SourceTopRight`), incoming still arrive at the top-left.
CORRECTED same day. Moving the curve while leaving the DOTS on their rows
orphaned them - the owner's next look was "where are their lines ?", with dots
beside rows and no line touching any of them. A dot and its curve are one
thing and must share a point. The source is the row's dot again, and the
original complaint is answered by the POSITION instead: both the dot and the
curve now sit ON the card's right border (`RowAnchorFallback`) rather than
16px inside it, so an edge leaves at the card edge instead of starting over
its own content. Lesson recorded: an anchor GLYPH and the geometry it
represents must be computed from one expression, never two.

### UB-104 — setState during render, every frame the host pushed `UNVERIFIED` `HIGH`

`[Hooks][Strict] State update scheduled during render` at CanvasView.uitkx:63.
Real, and ours: the host pushes selection and camera as version bumps, and the
component applied them by calling `setSelected`/`setZoom`/`setCamX`/`setCamY`
in the render body. The render now DERIVES the incoming value for the current
pass and a `useEffect` keyed on the version commits it for later ones — the
shape React's "adjusting state on a prop change" guidance describes. This is
also the most likely source of the "it broke at some point" render corruption
seen in round 2, since a set during render re-enters the reconciler mid-pass.

### UB-105 — obsolete PreventDefault (CS0618) `UNVERIFIED` `LOW`

Two sites. `EventBase.PreventDefault` is obsolete in Unity 6; it was also
redundant, since consuming the underlying IMGUI event is what actually stops
the Editor acting on the keystroke. Both now use
`StopImmediatePropagation` + `imguiEvent.Use()`.

### UB-106 — inline editor clipped its own text `UNVERIFIED` `MED`

Owner: "the textfield is cut off (maybe font size too big, or too big of
padding)". Both, and self-inflicted by UB-99: the font was scaled to the row
height but the box was sized without accounting for the input's 8px vertical
padding pair and Gutter-wide sides. Fragment editors now use compact chrome
(2px vertical, 6px sides) and the box is sized FROM the chosen font
(`font * 1.65 + 8`) rather than the other way round.

### Still outstanding — clone LSP publish

The schema-drift warning naming Vector2Field and friends is the pre-UB-75
server still running: `Server~/UitkxLanguageServer.dll` in the clone remains
locked by two live `.NET Host` processes. Publish it with Unity closed; nothing
else waits on it.

## 14. Field report — owner drive-through, 2026-08-18 round 5

Owner convention from here: "If i didnt mention a feature consider it success,
I will only report failures." Unreported items in a wave are CLOSED.

### UB-107 — anchor dots sat on top of the code `UNVERIFIED` `MED`

Owner: "either we have lines and the points are in wrong place, or the points
are in right place but we dont have lines? what i want is for the points/dots
to be on the right side, but still have lines."

Both halves were the same bug seen from two sides. `AnchorOf` returns the
marker ELEMENT's centre, and the marker is laid out after the row's attribute
run — so on a long L2 row it sits in the middle of the code, and both the dot
and its curve started from there. UB-103's first attempt then moved the curve
to the card corner and left the dot behind, which orphaned it.

FIX: `RowAnchor` keeps the marker's measured Y (so it still tracks its row and
the section's scroll clamp) and pins X to the card's right BORDER. Every dot
lands in one clean column on the card edge, each on its own row, with its line
attached. One expression feeds both the dot and the curve.

### UB-108 — closing an inline editor dropped window focus `UNVERIFIED` `HIGH`

Owner: "right click, wrap in, foreach, enter - loses focus, ctrl z goes on
unity, - same with any other directive", and the same after Escape. Closing the
editor destroys the focused element and Unity hands the keyboard to no one, so
the builder stops being the focused window and the next Ctrl+Z ran UNITY's
undo. UB-92 fixed focus on the way IN; this is the way OUT.

FIX: `RemovePanel` — the single exit path for commit, cancel and blur — calls
`BuilderWindow.FocusExisting`, then re-focuses the window ROOT on the next tick
(a KeyDownEvent needs a focused ELEMENT, not just a focused window).

### UB-109 — sibling drop hint was too faint to read `UNVERIFIED` `MED`

Owner: "i just tried for several attempt to drop the label between the 2
visualElements and it failed every time or at least i thought it did, but it
didnt". The drop was landing correctly — `AfterAnchor` already uses the row's
whole-block `EndLine` — but a 2px accent rule at the row boundary was too easy
to miss, so a correct drop read as a failed one.

FIX: a sibling insert paints a thick DASHED rule with end caps at the exact
line the element will land on, clearly distinct from the tinted box that means
"nest inside". Dash and cap sizes divide by the live zoom so the caret is the
same weight at every LOD.

### UB-110 — the drop caret pointed at a different place than the drop `UNVERIFIED` `HIGH`

Owner: "i see the dotted line and it drops it on the first visualElement as if
there can be 2 right positions". Exactly that — there were two.

The canvas lists markup FLATTENED with indentation, so the row element for
`<VisualElement style={safeStyle}>` is only its OPEN TAG line, and the gap
drawn under it is visually the gap before its FIRST CHILD. But the "after"
band inserted at `AfterAnchor`, which is the row's whole-block `EndLine` —
past every descendant, hundreds of lines below on a deep tree. The caret and
the edit were reading the same gesture in two different coordinate systems, so
UB-109's clearer caret made the mismatch obvious rather than causing it.

FIX, and it is the model the owner described: the caret is a POSITION IN THE
LISTED TREE, and the edit lands there.
- Hovering a row (middle band) appends INSIDE it, at the end. Unchanged.
- The gap under a row whose next listed row is DEEPER means "become that row's
  first child" — `InsertFirstChildTag`, inserting under the open tag (which
  `OpenTagEndLine` finds, so a multi-line attribute run is handled).
- The gap under a row whose next listed row is a sibling or shallower still
  means "after this row's block", which is the same point visually.
- The top band still means "before this row", as a sibling.

Applied to BOTH the insert path and the move path, so dragging a new element
and relocating an existing one read the caret identically. A self-closing
target has no inside to be first in, so it falls back to the append path that
rewrites `/>` into an open/close pair.

## 15. Creation flow — save-gated, 2026-08-18

Raised while auditing the create path before the owner drove it.

### UB-111 — creation wrote to disk immediately `UNVERIFIED` `HIGH`

`BuilderNewFileDialog.Create` called `File.WriteAllText` + `AssetDatabase.Refresh`
the moment the name prompt was confirmed. Same class as UB-88 on the other
side: the owner's rule is "nothing should be created/update/deleted anything on
files until save". A created file survived Abort, survived closing without
saving, and Ctrl+Z could not remove it.

Why it was written that way, and the real work in fixing it: the canvas cards
come from the LSP module graph, which is built from files on DISK, so a module
that exists only as a buffer has no node and would show no card at all.

FIX:
- `BuilderNewFileDialog` no longer touches disk. It answers two questions —
  `PathFor` and `TemplateFor`.
- The window opens a never-saved session (`BuilderWorkspace.CreateNew`, which
  already existed and had NO callers — the save-gated path was half-built) and
  records a ledger entry.
- `BuilderCanvasHost.AppendPendingNewNodes` synthesises a node per pending new
  module after the graph loads and fills it through the same
  `PopulateCardDetail` every other card uses, so a never-saved module is a real
  card with real parsed content.
- `SaveAll` writes it (creating the folder chain) and then calls
  `AssetDatabase.Refresh` OUTSIDE the reload suppressor, since a plain
  `File.WriteAllText` leaves no `.meta` and Unity would not see the asset.
- `AbortAll` already dropped never-saved sessions; `DiscardNew` lets undo do
  the same for one file, and redo re-opens it from the text kept on the entry.

### UB-112 — new modules landed flat, with invented exports `UNVERIFIED` `MED`

Two owner corrections in one:

FOLDER LAYOUT — files were created beside the focus file. The house layout puts
a component in its OWN folder and nests children under `components/`:
```
ComponentName/
    ComponentName.uitkx
    ComponentName.style.uitkx
    components/
        SubComponent/
            SubComponent.uitkx
```
`PathFor` now sends a new COMPONENT to `<focusDir>/components/<Name>/<Name>.uitkx`
and a style/hook/util module beside the component it belongs to. Suffixes are
unchanged (`.style.uitkx`, `.hooks.uitkx`, plain `.uitkx`): the builder detects
module KIND from them (`ClassifyByPathAndExports`) and every existing sample
uses them, so the owner's shorthand `.hook.uitkx` / `.util.uitkx` was read as
the established convention rather than a rename.

TEMPLATES — "we should only export what the user add". The old templates
invented members nobody asked for: `nameRoot` for a style, `nameText` for a
util, plus a counter body and a decorative `<Label>` for a component. Now a
style and a util module start EMPTY (their exports are named one at a time
through the card's own affordances), and a component and a hook emit exactly
the export the user just named, with the smallest legal body. NOTE for the
owner: a hook must return something, so `useX` still emits one `useState` —
that is the smallest thing that IS a hook, and the only invention left.

### UB-113 — the menu item opened a dead end `UNVERIFIED` `HIGH`

Owner: "i just clicked on our reactiveUiToolkit -> Ui Builder. and its empty,
how do i even start ? theres no new no nothing, pointless to have that menu
item if we cannot do anythig with it."

Correct — `MountCanvas` returned immediately with no focus file, so the window
mounted nothing and its only hint pointed back at the Project window. Every
create path also required a focus file to derive a folder from, so "+ new" and
the canvas right-click both refused with "Open a tree first".

FIX, following the owner's proposal exactly:
- An EMPTY STATE is now the way in: a centred "Start a UI" panel with the four
  module kinds, the reassurance that nothing is written until Save, and the
  right-click route for an existing tree.
- `CreateModule` no longer needs a tree. With one open, a module lands relative
  to the focus file as before. With none, it lands under a provisional root
  that exists only in memory, and the first COMPONENT owns its own folder
  rather than nesting under a `components/` directory with nothing above it.
- Save is where an unrooted tree gets a home: `ResolveUnsavedLocation` asks for
  a folder (starting at the project root), refuses one outside the project
  because a `.uitkx` there is never compiled, refuses to clobber existing
  files, relocates every pending session with `BuilderWorkspace.Relocate`, and
  only then writes. Cancelling the folder prompt cancels the save with nothing
  written. The canvas remounts afterwards, since the tree now lives at a
  different path than the graph was built from.

Note on the provisional root: it sits under `Assets/` rather than the temp
directory because `IsReadOnlyLocation` treats everything outside the project as
immutable — a temp path would have opened the first card READ-ONLY and refused
every edit. Nothing is ever written there; Save relocates first or does not
write at all.

## 16. Field report — building a component from zero, 2026-08-19

### UB-114 — a card froze whenever a re-parse threw `UNVERIFIED` `HIGH`

Owner: "When i added a button it automatically added a text attribute but only
in the right side component code, in the card the attribute didnt show until i
tried to add another attribute."

`BuilderCanvasHost.RefreshGraph` wrapped `PopulateCardDetail` in a try/catch
whose catch RETURNED — skipping `RenderCanvas`. So any edit whose re-parse
threw (routine while a buffer is half-typed) left the card showing its previous
content, and the change only appeared when a LATER edit happened to parse
cleanly, which is why two edits arrived together. The catch now swallows and
falls through to the redraw: the node keeps whatever it managed to populate,
and a broken buffer is reported by diagnostics rather than by a frozen card.

### UB-115 — the value editor nested its own wrapper `UNVERIFIED` `MED`

Owner: "when editing a field it adds "" automatically which makes {value} turn
into "{value}"." The attribute editors deliberately keep the quotes or braces
OUTSIDE the field, so a value typed with braces got wrapped again and became a
literal brace string. Typing a value the user wrapped themselves now switches
the attribute's FORM: `{…}` makes it an expression, `"…"` makes it a string
literal. Both directions, so an expression slot accepts a quoted literal too.

### UB-116 — canvas edits were never formatted `UNVERIFIED` `MED`

Owner: "the formatting of the text doesnt work there and it should happened
when you type and when you save and if both is too much so just when you save."
Canvas edits splice LINES into the buffer, so an inserted tag carried whatever
indent the splice guessed. Save now runs every dirty buffer through the same
AST formatter the source pane's apply uses, recording the result in the ledger
so it is undoable. Chosen deliberately: format on SAVE only, not per keystroke
— the formatter is a whole-file reprint and would fight the caret while typing.
A buffer that does not format cleanly is left EXACTLY as it was, since the
formatter's non-Formatted outcomes are data-loss guards, not results.

### UB-117 — no way to add body code except the source pane `UNVERIFIED` `MED`

Owner: "The only way to add custom logic / code in body of component is editing
the text on right sidebar. we should add a button like the + hook so + code."
Added beside "+ hook" on component and hook cards. It rides the same path — the
body is one list of statement lines and a hook call is just one of them — so it
seeds a statement, opens the inline editor on it, and is undoable like any
other edit.

### Not a builder defect — `event` as a lambda parameter

The CS1525 storm in the owner's capture comes from `onClick={event => …}`:
`event` is a reserved C# keyword and cannot name a lambda parameter. The
diagnostics reported it correctly. Worth noting only because the cascade
(CS1002/CS1031/CS1055/CS0065 …) reads like a builder failure and is not one.

### UB-118/119 — an unsaved tree reached disk anyway `UNVERIFIED` `CRITICAL`

Owner: "Why this file exist if i discarded and never saved?" It existed at
`Assets/__RuitkBuilderUnsaved__/SomeComponent/SomeComponent.uitkx`, with a
`.meta`, so Unity had imported it. Two independent defects, either of which was
enough on its own.

UB-118 — `SaveChanges()`, the override UNITY calls from its own prompt (closing
the window, a domain reload, entering play mode), went straight to
`_workspace.SaveAll()`. That bypassed the window's SaveAll completely: no
location prompt, no format pass. So the user never pressed Save — Unity did, on
their behalf, through a door that skipped every guard. It now routes through
the same SaveAll the toolbar uses and, if the location prompt is cancelled,
leaves the window dirty rather than reporting a save that did not happen.

UB-119 — even the window's own SaveAll would have written it. `UnsavedRoot` was
built with `Path.Combine(Application.dataPath, …)`, and `Application.dataPath`
returns FORWARD slashes on Windows while Combine only inserts a separator
without rewriting the ones already there. The root therefore kept forward
slashes in its prefix while every session path had been through
`Path.GetFullPath` and was all backslashes, so the `StartsWith` prefix test
never matched, `pending` came back empty, and the relocation was skipped.

FIX, in three layers so no single comparison carries the invariant:
1. Both sides of the test are `GetFullPath`-normalised.
2. `BuilderDocumentSession.NeedsLocation` marks a module whose path is
   provisional, and `BuilderWorkspace.SaveAll` REFUSES to write one — whoever
   calls it, however the paths compare. `Relocate` clears the flag.
3. `SaveChanges` shares the window's save path.

CORRECTION — the leftover folder was NOT inert, as first reported. It sat in
Assets/, so Unity imported it and the source generator compiled it: after a
Reimport All the whole project failed on its single bad token (CS0065), and the
failure cascaded into Mono.Cecil assembly-resolution errors for
Assembly-CSharp-Editor across Burst. It was deleted, and UB-120 makes a repeat
harmless.

### UB-120 — the provisional root was visible to Unity `UNVERIFIED` `MED`

The in-memory root lived at a normal folder name under Assets, so anything that
reached it became a REAL asset. The folder now ends in '~', which the Asset
Database ignores wholesale: a future leak cannot be imported, cannot get a
.meta, and cannot be compiled. Defence in depth behind UB-119 rather than a
replacement for it.

Lesson worth keeping: a save-only contract cannot be enforced by a string
comparison at one call site, because the framework has its own save door.

## 17. Field report — wiring a style module, 2026-08-19

### UB-121 — an import to an unsaved module drew a dot but no line `UNVERIFIED` `HIGH`

Owner: "even when connected no pint line, just dots." Edges came ONLY from the
language server's module graph, which is built from files on DISK, so an import
pointing at a module that is still an unsaved buffer produced no edge at all.
The anchor DOTS are painted per import ROW, independently, which is exactly why
one appeared without the other. `AppendMissingImportEdges` now resolves every
relative import specifier against the nodes on the canvas and adds any edge the
server did not know about — so a brand-new style module wires up immediately,
and the dot always has its line.

### UB-122 — menus were mouse-only `UNVERIFIED` `MED`

Owner: "the menus doesnt have up / down for navigating them." Arrow keys now
move a highlight through the pickable rows (headers and separators skipped) and
Enter takes it; the list scrolls to follow. Typing refilters and resets the
highlight to "first match", which is what Enter always did.

### UB-123 — an import's binding name was unreachable text `UNVERIFIED` `MED`

Owner: "we cannot copy the name of the variable in the import so either make it
copyable, or make it autocomplete in fields." Double-clicking an import row now
copies just the BINDING — `BuilderText.ImportAliasOf` reads the three shapes the
language allows (`* as Alias`, `{ A, B }`, a bare default) — not the specifier
or the import chrome, since the binding is the only part a reference has to
spell.

### Answered, not defects

- "Why is the component not visible / shows nothing on the right" — a preview
  needs a compiled TYPE, and a module that has never been saved has never been
  compiled, so there is nothing to instantiate. The pane already says so. This
  is inherent to save-gating, not a regression: save once and previews work
  from then on.
- "How do I connect a new style to the component" — dragging the style module
  from the library onto the CARD already adds the import (`stylemod` drop,
  guarded to components). The menu is not the only route; the gesture the owner
  proposed is the one that exists.

### Deferred with a reason

- Renaming a module — REMAINING_WORK UB-124. It is a cross-file refactor
  (export, file, folder, and every importer's specifier and binding), not a
  menu item.
- An apostrophe in a `//` comment inside a `{…}` attribute expression breaks
  SOURCE GENERATION while parsing clean — REMAINING_WORK SG-APOSTROPHE. Found
  while editing CanvasView; minimal repro recorded. It affects any .uitkx
  author, not just the builder.

## 18. Rename, and a selection regression, 2026-08-19

### UB-124 — rename a module `UNVERIFIED` `FEATURE`

Owner: "all component should have an option to renaim them" — and, on the
scope: "yes that make sense." Implemented as a real refactor rather than a
menu item, because a rename is FOUR edits that must land together or not at
all:
1. the EXPORT the module declares (`RenameExportIn` rewrites the declaration
   only — a word-boundary sweep over the file would rewrite unrelated
   identifiers that merely share the name);
2. every IMPORTER's specifier AND binding, plus that binding's uses
   (`RenameReferencesIn` — the two move together, since renaming either alone
   leaves the file pointing at something that no longer exists);
3. the FILE name;
4. the FOLDER, when the module owns one (the house layout for components), so
   the folder can never contradict the file.

All of it is pending buffer work: `BuilderWorkspace.Rename` relocates a
never-saved module outright, and expresses a SAVED one with the two pending
mechanisms that already exist — a new session carrying the text at the new
path plus a deletion mark on the old. Save writes the new file and trashes the
old in one batch; Abort drops both. The whole rename is ONE ledger entry
including the creation and the deletion, so a single Ctrl+Z puts the module
back where it was. Name validation reuses the create-prompt rules via
`KindKeyOf`, so a rename cannot produce a name creation would have refused.

### UB-126 — menu keys existed only where a search field did `UNVERIFIED` `MED`

Owner: "not fixed in all menus, new component menu doesnt have those keys."
Correct, and the previous fix was the shallow one: the arrow/Enter handling was
bound to the SEARCH FIELD, so every menu built without one — the create menu
and every simple pick list — stayed mouse-only. The keys now live in a single
`OnListKeyDown` shared by both menu shapes, bound to the root for the
non-searchable case with the root taking focus so they arrive at all.

### UB-127 — the hook editor clipped its own line `UNVERIFIED` `MED`

A hook chip is only as wide as its summary ("useState -> value, setValue"), but
the line it edits is the full declaration. A single-line editor now takes the
width of the CARD SECTION it sits in when that is wider than the thing clicked.

### UB-128 — selection highlight covered half the line `UNVERIFIED` `MED`

Owner: "the text select only marks half of the text." A regression from the
compact chrome added for UB-106/127. The coloured-edit overlay only registers
while the INPUT and the LISTING share identical text geometry, and compact mode
moved the input to a 2px vertical band while leaving the listing on the source
pane's 8px one. The selection highlight is drawn by the input, the glyphs the
user sees are the listing, so the band sat several pixels off the text. Both
surfaces are now driven from one pair of constants, and the row builder reads
the same pair, so they cannot diverge again.

## 19. Rename fallout, 2026-08-19 — four defects, three of them mine

### UB-129 — the rename prompt could not be edited `UNVERIFIED` `HIGH`

Owner: "it shows the original name but it doesnt allow you to select part of it
or all of it... typing any letter just clears the entire value." Exactly right,
and a call-site blunder: `ShowNamePrompt` takes a PLACEHOLDER, and I passed the
current name as one. A placeholder is grey hint text drawn over an EMPTY field —
there is nothing to select and the first keystroke is simply the first
character. The prompt now takes an `initialValue` distinct from the
placeholder, seeds the field with it, and selects it on open, so a rename can
be edited or replaced wholesale.

### UB-130 — an undo chord inside a prompt hit Unity `UNVERIFIED` `HIGH`

Owner: "if while editing the name you mistakenly ctrl + z it applies to unity."
The prompt is its own EditorWindow and never claimed the chord, so it fell
through to Unity's global undo and mutated the scene. UB-89 fixed this for the
builder window; the popups were a second, unfixed door. The name field now
consumes Ctrl+Z/Y for the life of the prompt.

### UB-131 — undoing a rename blanked the window `UNVERIFIED` `CRITICAL`

Owner: "after the change i ctrl Z and [the card and source pane are empty]".
Two compounding faults in the replay:
1. Undoing a rename DISCARDS the renamed module's session, but `_focusFile`
   still named it — so every pane rendered emptiness over a tree that was
   perfectly intact, and the card had no content because the graph had no such
   node.
2. `ApplyLedgerWrites` remounted the canvas BEFORE writing the buffers and
   before validating the focus, so the whole window was rebuilt around a file
   that no longer existed.

Fixed by ordering the replay the way it should always have been: every model
change lands FIRST, then the focus is validated (`RebindFocusIfMissing` moves
to any live, non-deleted session), then the views refresh exactly once.

### UB-132 — a pending module in a non-existent folder broke the preview `UNVERIFIED` `HIGH`

Owner: "everything broke... its extremely slow to type in", with
`DirectoryNotFoundException: Could not find a part of the path ...\SomeBodyThat`.
NOT a rename bug — a hole in the save-gated design that rename merely exposed
hard. Two companion scans in `UitkxHmrCompiler` call `Directory.GetFiles(dir)`
unguarded, and a module the builder holds as a pending buffer can legitimately
sit in a directory that does not exist yet: a new component owns a fresh folder,
and so does a rename. The exception killed the preview compile and, because the
compile is debounced per keystroke, threw on every character — which is the
typing slowness. An absent directory HAS no companions; that is an answer, not
a failure, and both scans now say so. The same exception was visible in the
CREATE flow days earlier and was misread as noise.

Gates: SG 1879/1879 (including the Hmr parity contracts, since this touched
`Editor/HMR/`), csc smoke EXIT=0, validate-uitkx 0, machine-path clean.

### UB-133 — renaming the root collapsed the whole tree `UNVERIFIED` `CRITICAL`

Owner renamed ShowcaseDemoPage to BhowcaseDemoPage and the canvas went from the
full tree (seven-plus cards, every usage edge) to TWO cards.

ROOT CAUSE, and it is the same hole as UB-132 seen from the graph side: the
canvas adjacency is built ONLY from the language server's edge list, which is
derived from files on DISK. A renamed module is a PENDING buffer at a path the
server has never seen, and the old path is hidden as a pending delete, so its
edges are filtered out too. `ConnectedComponent` therefore found nothing
attached to the focus file, `member` came back as just that one file, and every
other module dropped out of the graph. `AppendPendingNewNodes` had added the
node itself, and `AppendMissingImportEdges` could only draw edges BETWEEN nodes
that were already there — which is why exactly one neighbour survived.

FIX at the layer the fault lives: `LoadTreeAsync` now takes the pending set and
`LinkPendingImports` parses each pending buffer's own import directives,
resolves the specifiers, and feeds them into the adjacency BEFORE the
reachability walk. The walk then sees the tree the user is actually looking at,
whether or not the server knows the file exists. A specifier that resolves to
nothing is skipped, so a half-typed import cannot break the tree.

This also fixes the same collapse for a NEWLY CREATED component that imports
existing modules — it was always latent there; rename just made it obvious.

### UB-134 — a rename destroyed the module it renamed `UNVERIFIED` `HIGH`

Found while doing the project-model refactor (`Plans~/BUILDER_MODEL_REFACTOR.md`,
Stage 1) rather than from a report, but it is a real defect with three separate
symptoms.

ROOT CAUSE: `BuilderWorkspace.Rename` expressed a move as a fresh session at the
new path plus a deletion mark on the old one. The session object IS the module -
it carries the undo history and the recorded line-ending flavour - so renaming a
module silently threw both away: the user's undo stack for that file was gone,
and a file that had been CRLF on disk would be rewritten LF at Save. The ledger
recorded the same rename as an unrelated creation and deletion, which is why
undoing it could put the module back without its history.

FIX: identity now lives on the session (`Id`), and where the module lives on
disk is recorded separately (`OriginalDiskPath`). A rename re-files the SAME
session under a new path; the two fields disagreeing is precisely what a pending
move is, and `Save` projects it as one operation - write the new file, retire the
old - instead of inferring it from a create/delete pair. `AbortAll` points the
path back. The ledger gained one `IsMove` change kind, so undo and redo walk the
move rather than re-creating the module.

Two further defects fell out of the same work:

- `BuilderDocumentSession.Open` was also used for paths with NO file behind them
  (the workspace opens a missing file with an empty buffer). Such a session would
  have claimed a disk origin it did not have, so `Save` would write the file
  without setting `createdAssets`, skip `AssetDatabase.Refresh`, and leave a
  `.uitkx` on disk that Unity never imports or compiles - the UB-111 failure
  again, from the other end. `Open` now takes the existence the caller already
  computed.
- The canvas hid a renamed module's old path only because the rename marked it
  for deletion. With the move expressed properly there is no deletion mark, so
  the disk-derived graph would have shown a ghost card at the old name and none
  at the new one. `IsHiddenOnDisk` now owns both facts - marked for deletion, or
  vacated by a move - and `PendingNewFiles` yields a moved module's new path,
  which has no file behind it yet.

Verified by compiling the builder editor assembly against the 6000.5.6f1
reference set: 0 errors.

### UB-135 — renaming a folder-owning component orphaned its children `UNVERIFIED` `CRITICAL`

The bug the owner hit from two sides and the reason for the project-model
refactor. Renaming a component that owns its folder moved ONLY its own file:
`Showcase/Showcase.uitkx` became `Bowcase/Bowcase.uitkx` while
`Showcase/components/Sub/Sub.uitkx` stayed exactly where it was. The parent's
own import then read `./components/Sub/Sub` relative to `Bowcase/`, where
nothing existed, so every sub-component silently detached.

ROOT CAUSE: the rename computed a new folder path but nothing ever moved the
folder. Only the module's file was expressed as a pending change, so the
children were never part of the operation at all.

FIX, at the layer the fault lives: a folder-owning rename is now a FOLDER move.
Every module inside the folder is brought into the model first, the folder is
recorded as a pending move like every other edit, and Save projects it as ONE
`AssetDatabase.MoveAsset`. That matters beyond tidiness: moving the children
one file at a time would write each anew and trash the original, churning every
child GUID and stranding everything in the folder the builder does not manage -
companion `.cs`, `.uss`, nested folders. Because the folder keeps its depth,
every relative import inside the subtree, and every one pointing out of it,
stays correct without being touched; imports from OUTSIDE into the subtree are
rewritten by the existing reference pass.

Two further defects fixed in the same pass:

- Importers that were not already OPEN were skipped. Step 2 of the rename used
  `TryGet` rather than `EditSession`, so any importer the user had not visited
  kept pointing at the module's old name. A rename has to reach every importer,
  not just the visited ones.
- A folder that is not on disk is no longer queued for a move. Renaming a
  module the user had only just created would otherwise have queued a move of a
  directory that was never written, and the whole Save would have failed on it.

### UB-136 — the canvas learned about buffers three separate times `UNVERIFIED` `HIGH`

Not a new report; the shape behind UB-111, UB-121 and UB-133. The module graph
came from the language server, which reads DISK, so anything the builder held
as a buffer had no edges - and each symptom got its own patch:
`AppendPendingNewNodes`, `AppendMissingImportEdges`, `LinkPendingImports`.
Three patches teaching three consumers the same fact is the signature of a
missing owner.

FIX: the graph's structure now comes from the modules themselves. The server's
answer is treated as what it actually is - a CACHE of the disk state, derived
from the same text - and is used for any module the builder has not touched,
which keeps the cost where it was; for a module the builder holds differently,
created, renamed or merely edited, the server is stale by definition and that
module's own text is parsed instead. All three patches are deleted.

The three duplicate specifier resolvers went with them. The builder had one in
the canvas host, one in the graph service and one in the preview compiler, each
probing a different set of filename suffixes - which is why the same import
could resolve in one place and not another - while the language's own
`ImportResolver` sat unused. There is now one resolver, shared by the edges the
canvas draws and the order the preview compiles in, and it needed no suffix
probing at all: a style module is imported as `./Thing.style`, and the resolver
appends `.uitkx` to exactly that.

Also fixed here, found while tracing the same seam: `SyncLspBuffer` gated its
`didOpen` on `open && _lspOpened.Add(path)`. `&&` short-circuits, so a file
first synced by an EDIT rather than by a mount never entered the set and then
received `didChange` forever for a document the server had never been told
about. A rename makes that routine, since the module arrives at a path nothing
has opened. A document is now opened before it is changed.

### UB-139 — UXML import wrote to disk immediately `UNVERIFIED` `MED`

Both import entry points - the toolbar's "Import .uxml..." and the asset-menu
"Convert UXML to UITKX" - called `File.WriteAllText` and `AssetDatabase.Refresh`
the moment the conversion finished, which is precisely the save-only contract
(VE-D2) the rest of the builder obeys and the one the owner asked for in these
words: nothing should be created, updated or deleted on disk until Save.

A conversion produces a MODULE, not a file. Both paths now hand the builder a
pending buffer - the asset-menu one through a new `OpenFor(path, pendingText)`
overload - so Save writes it, Abort drops it, and Ctrl+Z takes it back like any
other creation.

### UB-140 — the canvas layout was thrown away by a rename `UNVERIFIED` `MED`

Arrange the cards, rename a component, and the layout resets to the default
fan-out.

ROOT CAUSE: `BuilderCanvasConfig` names its file after a SHA-1 of the tree
ROOT's full path and keys each card position by a path relative to that root.
A rename changes member paths and, for the root or a folder-owning component,
the file name too - so `LoadForRoot` missed and the by-member scan missed as
well, because every member path had just changed. The layout was never
corrupted, only unreachable, and the next save wrote a fresh config under the
new key while the old one was left behind forever.

FIX: the rename tells the layout where it went. `Repath(old, new, isFolder)`
resolves every stored key back to an absolute path, moves it, and re-keys it
against the new root; the config the tree has outgrown is deleted on the next
save rather than orphaned. It is called for the folder move and the file move,
and for both directions of undo and redo, so the layout follows a rename out
and back.

Worth recording why the obvious approach was NOT taken: keying positions by
`BuilderDocumentSession.Id` looks right and is wrong. That id is stable within a
session and across domain reloads, but a fresh window generates new ones, so
identity-keyed layout would have survived renames and lost everything BETWEEN
sessions - trading a visible annoyance for a worse invisible one.

### UB-141 — a history jump moved the text but not the tree `UNVERIFIED` `MED`

Clicking an entry in the History panel replayed only buffer writes. Structural
changes - a creation, a deletion, a module move, a folder move - carry paths
rather than text in their Before/After, so they were skipped; feeding them to
the write path handed null to `ApplyEdit`, which rejects it. A jump across a
rename therefore moved the text while leaving the module where it was.

FIX at the layer the fault lives: the per-entry replay is now one method,
`ApplyEntry(entry, undo, writes)`, and undo, redo and the history jump all go
through it. A jump is simply N undos or N redos, so every change kind is
honoured; buffer writes are still collected across the whole walk and applied
once, so the model settles before anything redraws. `WalkTo`, which existed only
to return the write list, is gone.

### UB-142 — renaming a style companion would have moved its component's folder `UNVERIFIED` `CRITICAL`

Caught in review of UB-135 rather than in use, and it only became destructive
BECAUSE of UB-135: a folder-owning rename now really moves the folder.

A card's title has `.style`/`.hooks` stripped, so `Showcase.style.uitkx` sitting
in `Showcase/` reports its name as "Showcase" - exactly like the component that
owns the folder. The folder-ownership test compared that title to the folder
name and nothing else, so renaming the STYLE companion satisfied it. Before
UB-135 that merely placed the renamed companion in a new folder; after it, the
rename would have taken the entire component folder, every sub-component
included, along with a companion.

FIX: only a plain `.uitkx` module can own a folder. A companion never does.

### UB-143 — an unsaved module could never be an import target `UNVERIFIED` `CRITICAL`

Owner report 2026-08-21: a component and a style module, both created in the
builder and neither saved, with `import * as SomeComponentStyle from
"./someComponentStyle.style"`. No preview, and the console carried
`hmr_SomeCompnent_1.cs(98,105): error CS0103: The name 'SomeComponentStyle'
does not exist in the current context`.

ROOT CAUSE, confirmed by running the resolver directly rather than by reading
it: `ImportResolver.MapSpecifierToPath` builds its answer by joining strings
with FORWARD slashes, so it returns a path whose separators are all `/` where
every session in the workspace is keyed by a `Path.GetFullPath` path, which on
Windows uses backslashes. The HMR compiler passes that raw forward-slash path
straight into
`UitkxSourceExists`, which consults the builder's `SourceOverlay` - and the
overlay's dictionary lookup missed, every time, for every import target.
`UitkxSourceExists` then fell through to `File.Exists`, which is false for a
module that has never been saved. The import was dropped in silence, no alias
was emitted, and the component failed to compile on the alias name.

Only UNSAVED targets were affected: for a saved module the `File.Exists`
fallback answers true, forward slashes and all, which is why this never showed
up before the builder could hold a whole tree in memory.

FIX: the overlay canonicalises its argument before looking up. That is the right
layer - the overlay is the builder's adapter between "any path the compiler
happens to hold" and "my canonically-keyed sessions", and owning that mismatch
is its job. `File.Exists`, the retry reader and namespace derivation all already
tolerate forward slashes, and have been receiving them for every saved import
target all along, so nothing else needed changing.

### UB-144 — a new import drew an anchor dot and no line `UNVERIFIED` `HIGH`

Same report: the import row appeared on the card with its anchor dot, and no
edge was ever drawn to the style card.

ROOT CAUSE: `BuilderCanvasHost.RefreshGraph` - the commit path every canvas edit
funnels through - re-parsed the changed file into its graph node and then called
`RenderCanvas`. It rebuilt the card's CONTENT and never touched `graph.Edges`.
The drawing code paints an anchor dot per import ROW, independent of any edge,
so a freshly added import got its dot immediately while the edge list still knew
nothing about it. The edge only appeared if something happened to remount the
whole canvas. Its own doc comment claimed it "rebuilds ONE card and redraws the
edges", and the gap between redrawing edges and rebuilding them is the bug.

This is why the earlier UB-121 fix did not hold: `AppendMissingImportEdges` ran
at mount, so it too only helped after a remount.

FIX: imports are STRUCTURE, not card decoration, so re-parsing a module rebuilds
what it points at. `BuilderGraphService.RefreshEdgesFor(graph, nodeIndex)`
rebuilds that one node's import edges against the nodes already on the canvas,
through the same single resolver a full load uses - no language-server round
trip, so it can run on every commit.

Also added, because the failure was invisible: an import that resolves to
nothing now logs one warning naming the file, the specifier and the path it
looked for. A dot with no line said nothing about why, and that silence is what
made this take a screenshot to find.

### UB-145 — a name was refused because another KIND used it `UNVERIFIED` `HIGH`

Owner report 2026-08-21: creating a style module called `someComponent` was
refused with "someComponent already exists" because a COMPONENT called
`SomeComponent` was on the canvas. They produce `someComponent.style.uitkx` and
`SomeComponent.uitkx` - two different files, and exactly the pairing the folder
convention is built around.

ROOT CAUSE: `ValidateNewName` compared the candidate against every card's
DISPLAY TITLE, case-insensitively. A title has its `.style`/`.hooks` stripped,
so a style module and its component report the same title by design, and the
casing convention (PascalCase components, camelCase modules) was erased by the
case-insensitive compare. The create flow's own commit step already had the
right test - does the FILE this would produce already exist - so the live
validation was both wrong and a duplicate of a correct check further down.

FIX: a name collides only when the file it would produce collides. Both prompts
now pass the mapping from name to path, so the validation and the commit ask the
same question. `RenameTargetPath` is that mapping for a rename, shared with
`RenameModule` so the prompt and the rename can never disagree about what a name
would produce.

### UB-146 — dropping a style module on an element did not style it `UNVERIFIED` `MED`

Owner report 2026-08-21: a style module with `Height = Px(200)` and a dark
background, imported into a component, and the preview showed nothing but an
empty stage. The markup was `<VisualElement>` with no attributes: the import had
been added and nothing used it.

ROOT CAUSE: the drop handler ignored `rowIdx` for style modules entirely. Every
style-module drop added the IMPORT and stopped, whether it landed on a card or
on a specific element - and an import styles nothing. The card gained a line,
the preview looked identical, and the actual styling had to be typed by hand.

FIX: a style module dropped ON AN ELEMENT is applied to it - the style attribute
is set and the import added if the file lacks it, as one undoable action. A
module with several exports asks which. Dropped on the card rather than a row it
still adds the import alone, which remains the right answer there.

The write order is load-bearing: the attribute is written first, against the
row's current source line, because inserting the import at the top shifts every
line below it by one.

### UB-147 — the style alias collided with the component it was imported into `UNVERIFIED` `CRITICAL`

Owner report 2026-08-21, immediately after UB-145 made the name pair legal:
`CS0117: 'SomeComponent' does not contain a definition for 'container'`.

ROOT CAUSE, and it is UB-145's direct consequence: a star import's alias was the
module name PascalCased. A style module called `someComponent` therefore bound
the alias `SomeComponent` - the importing component's OWN name - so
`SomeComponent.container` resolved to the component type, which has no such
member. Allowing the pair was right; deriving the alias by capitalising it was
not, because the two conventions (PascalCase components, camelCase modules)
collapse onto the same identifier by construction.

FIX: `ImportAliasFor` chooses a name that cannot collide with something the file
already means - the importing component's own name and every binding its
existing imports introduce - falling back to `NameStyle`, then a counter. A
module that is ALREADY imported keeps whatever alias it was given, so styling a
second element from the same module references the name the file actually binds
rather than inventing a fresh one.

### UB-148 — every keystroke recompiled every unsaved module `UNVERIFIED` `HIGH`

Owner report 2026-08-21: "it all feels much much slower".

ROOT CAUSE, and it is an interaction with the save-only contract rather than a
plain bug: `CompileDirty` compiled EVERY dirty session on every debounced edit.
Nothing is saved until the user says so, so the dirty set only grows as they
work - and on Unity 6.5 in-process Roslyn is unavailable (HMR-ROSLYN-65), so
each of those compiles spawns an external csc process. The editor therefore got
measurably slower with every module added to an unsaved tree, which is exactly
the state the builder is designed to keep you in.

FIX: a module is rebuilt only when its own text has moved since it last
compiled, or when something it imports was rebuilt this round. Walking in import
order is what makes the second test valid - every dependency is decided before
its dependents. A successful compile records the text it was built from; a
failure forgets it, so a broken module keeps retrying.

### UB-149 — a module the user had just created reported itself broken `UNVERIFIED` `LOW`

`UITKX2105: 'someComponent.style.uitkx' does not contain a valid top-level
declaration`, the moment a style module was created.

ROOT CAUSE: a style or util module is created EMPTY by design (UB-112 - only
what the user adds is exported), and an empty file genuinely has no valid
top-level declaration. The builder was manufacturing an invalid file every time
and then reporting it back to the user about a file they had not started
writing.

FIX: a blank buffer reports nothing. It is not broken, it is unstarted.

### UB-150 — the using alias for an unsaved import target was never emitted `UNVERIFIED` `CRITICAL`

Owner report 2026-08-21, after UB-147 fixed the alias NAME:
`CS0103: The name 'SomeComponentStyle' does not exist in the current context`,
with the import and the style usage both correct on screen.

ROOT CAUSE, and it is NOT in the builder: `ImportScopeFacts` - the language-lib
code that works out which `using` lines a file's imports imply - resolves every
import TARGET itself, and read those targets straight off the filesystem:
`File.Exists(target)` then `File.ReadAllText(target)`, at four separate sites. A
module that has never been written fails the existence test, so the import is
skipped, no alias is emitted for it, and every reference to that alias fails to
compile.

This is the same hole as UB-143 one layer further in. UB-143 fixed the overlay
lookup inside `UitkxHmrCompiler`; this is a SECOND, independent disk read, in
the shared language library, which no overlay reached.

FIX at the layer the fault lives: all four sites now go through one
`ReadTargetDirectives` accessor with an injectable `SourceOverlay`. A null
overlay - which is what the source generator and the language server pass -
falls through to disk and behaves exactly as before, so their behaviour is
unchanged by construction. `UitkxHmrCompiler` publishes its own unsaved-buffer
overlay into that hook before every compile, so ordering between setting the
overlay and initialising the reflection handles cannot matter.

SG suite 1879/1879, LSP suite 180/180.

DEPLOY NOTE: the fix ships in `Analyzers/Ruitk.Language.dll`, which HMR loads
with `Assembly.LoadFrom`. That locks the file for the life of the Unity process,
so this one DLL cannot be replaced while the editor is running - unlike every
other payload file. Unity has to be closed for it to land.

### UB-151 — the inline editor's text sat at the top of its box `UNVERIFIED` `LOW`

The single-line inline editor takes the HEIGHT of the canvas row it covers, and
the compact chrome pins the text 2px from the top - so whenever the row was
taller than one line of text, the glyphs sat at the top with all the slack
underneath. `CenterSingleLine` splits the surplus evenly instead.

### UB-152 — the CS0103 the owner kept seeing came from the LANGUAGE SERVER `UNVERIFIED` `CRITICAL`

Owner report 2026-08-21, after UB-150 shipped and Unity was closed, reopened and
all assets reimported: still `CS0103: The name 'SomeComponentStyle' does not
exist in the current context`.

WHAT WAS ACTUALLY TRUE at that moment, established from the artefacts rather
than from reading code: HMR's temp directory held BOTH
`hmr_SomeComponent_11.dll` and
`hmr_Ruitk.Uitkx.__RuitkBuilderUnsaved___.SomeComponent.someComponent_style.__Exports_8.dll`,
timestamped three minutes earlier, and a string scan of the component assembly
showed it referencing `someComponent_style`, `__Exports` and `container`. A DLL
is only written on success. The Editor log carried no `preview compile` failure
and no `Render failed`. **The compile was already fixed by UB-150.**

ROOT CAUSE of the message that remained: it was never the compiler's. The
builder's source pane shows `OnLspDiagnosticsPublished`, and the language server
is a SEPARATE PROCESS that runs Roslyn over a virtual document. It holds the
focus file's buffer because the builder pushes it, but `ImportScopeFacts` -
which it also uses - resolved the IMPORT TARGET off the filesystem, where an
unsaved module does not exist. So the server reported a genuine-looking CS0103
about code that compiles perfectly well.

This is the fourth of the parallel layers CLAUDE.md names, and the one UB-150
did not reach: SG and HMR were fixed by the overlay, the IDE virtual doc was
not, because nothing set the overlay in the server process.

FIX: the server sets `ImportScopeFacts.SourceOverlay` to its own `DocumentStore`
at startup, so a module that is OPEN IN THE EDITOR is visible as an import
target. Editor content wins; anything not open falls through to disk exactly as
before, so a normal IDE session is unaffected.

LESSON, and the reason this entry is long: I twice declared CS0103 fixed after
fixing ONE resolver, without checking whether anything else resolved import
targets. There were four, in three processes. The check that finally settled it
was reading the compiler's own output artefacts instead of reasoning about the
code - the DLLs on disk said the compile had been working for some time while I
was still looking for a compile bug.

### UB-153 — the preview only ever compiled in response to an EDIT `UNVERIFIED` `CRITICAL`

This is the "no render", and it is why restarting Unity never helped: restarting
is the one thing guaranteed to reproduce it.

`MountPreview` calls `ShowFile(_focusFile, buffer, assemblyHint: null)` and
triggers no compile. `ResolveComponentType` then looks for a type carrying a
matching `[UitkxSource]` among the assemblies ALREADY LOADED. A compile only
ever ran from `NotifyBufferChanged`, which fires on an edit.

A builder tree that has not been edited in the CURRENT process therefore has no
compiled type to resolve, and the stage renders empty - no error, because
nothing failed. And that is every unsaved tree after a domain reload or an
editor restart: the buffers survive with the window's serialized state while the
compiled assemblies do not. The owner restarted Unity and reimported repeatedly,
each time landing back in exactly the state that cannot render.

Confirmed from artefacts before changing anything: `%TEMP%/UitkxHmr` did not
exist at all in the fresh session, and the Editor log contained no builder
output whatsoever - no compile had been attempted since Unity started, despite a
tree being open with two dirty buffers.

FIX: mounting the preview asks for the compile. It is the same debounced request
an edit makes, and UB-148 means it skips every module whose text has not moved,
so remounting is cheap.

WHY IT HID FOR SO LONG: during active editing the very next keystroke produced a
compile, so the preview worked. It only failed on a freshly opened session -
which is the state a frustrated user reaches for first.

### UB-154 — the preview render was never pumped `UNVERIFIED` `CRITICAL`

The stage stayed empty while the compile succeeded, the type resolved and
nothing threw. The reconciler time-slices its work onto a scheduler driven by
EditorApplication.update, and `Mount` enqueued the render and returned.
`UnmountPreview` already pumped that scheduler on the way OUT; nothing pumped it
on the way IN, so a mount became visible only if something else happened to tick
it - and when nothing did, the failure was completely silent, because no work
had run to throw. `Mount` now drains the render, and a mount that produces no
elements says so in the console naming the type and its assembly.

### UB-155 — the fiber tree outlived the types it was built for `UNVERIFIED` `CRITICAL`

Once the render was pumped, a second failure surfaced underneath it, and the
owner's repro is the clearest possible statement of it: they aborted, started a
fresh tree, added a new component and a new style module, changed NOTHING - and
the preview showed the PREVIOUS component's render, label and all. Editing a
style module's colour or height also changed nothing on screen.

ROOT CAUSE: `Mount` created its `VNodeHostRenderer` once and reused it forever.
A recompile hands back a new Type from a new swap assembly, and opening another
file hands back a different component entirely, but the live fiber tree kept
holding the ORIGINAL types - so re-rendering into it re-rendered the old build.
On top of that, `UnmountPreview` tore down the FIBERS and left the elements they
had produced in the host, so whatever was on screen stayed on screen underneath
the next render.

FIX: the renderer is torn down whenever the component TYPE object changes -
which is every hot swap and every file switch - and the host is cleared with it,
in both the teardown path and the type-change path. Knob values still survive,
because they are carried across separately by `CarryOver`.

### UB-156 — open documents were invisible to path lookups in the LSP `UNVERIFIED` `HIGH`

The third appearance of one bug. `DocumentStore.TryGetByPath` compared
`Uri.LocalPath` - backslashes on Windows - against the caller's path with an
ordinal comparison. Anything that resolved a path by JOINING STRINGS, which is
what `ImportResolver.MapSpecifierToPath` does, therefore never matched a single
open document, so the overlay added in UB-152 could never fire and the server
kept publishing CS0103 for an unsaved import target.

Both sides are canonicalised now. Same root as UB-143 (the HMR overlay) and the
same shape as UB-119 (the unsaved-root prefix test): a path built by
concatenation is compared against one produced by the platform, and on Windows
those never agree.

### UB-157 — the preview vanished on the first click `UNVERIFIED` `HIGH`

Owner report 2026-08-22: a new component with a label renders, and disappears
the moment anything in it is clicked.

ROOT CAUSE, and it is UB-155's fix meeting an older weakness: every hot swap
LOADS ANOTHER assembly, and they all carry the same `[UitkxSource]` path, so
`ResolveComponentType` with no assembly hint can return a type from ANY of them
- in practice the oldest still loaded. A remount with no hint, which is what a
selection or a canvas rebuild produces, therefore resolved a DIFFERENT Type
object than the live tree had been built for. UB-155 now treats a changed type
as a hot swap and tears the tree down, clearing the stage - so the preview went
blank. It could not recover either, because UB-148 correctly skips recompiling a
module whose text has not moved, so no new assembly arrived to rebuild from.

The three changes are each right on their own; together they exposed that the
pane never knew which assembly it was currently showing.

FIX: the pane remembers the assembly its live build came from and resolves
against it whenever no newer hint is supplied. Switching to a different file
forgets it, because a different component has nothing to do with that build.

### UB-158 — every debounced edit built modules the preview cannot show `UNVERIFIED` `HIGH`

Owner 2026-08-22: "this entire process is freaking slow. doing anything is
freaking slow."

ROOT CAUSE: `CompileDirty` built every DIRTY module in the workspace, and under
the save-only contract every module is dirty for the whole session - so the cost
of one keystroke grew with the size of the tree, and on Unity 6.5 each of those
builds is an external csc process (in-process Roslyn is unavailable,
HMR-ROSLYN-65). UB-148 already stopped rebuilding modules whose TEXT had not
moved; this is the other half.

FIX: only the focused module and what it imports, transitively, are built. A
module the preview cannot reach cannot change what is on screen.

LIKELY ALSO FIXES the owner's second report in the same message - a style module
linked to a component had no visual effect until an unrelated edit to the
component. A style module is created EMPTY, and as a dirty module it was
compiled immediately, loading an assembly that defines its `__Exports` with no
entries. Types are resolved by NAME across the swap assemblies, so the component
could keep binding to that first, empty one. With this change an unimported
module is never built, so the empty version never gets loaded, and the style is
first compiled as a DEPENDENCY of the component that imports it. NOT VERIFIED -
recorded as the mechanism that fits, not as a confirmed fix.

OPEN, owner ask in the same message: the preview should refresh deterministically
after every action rather than on a 300 ms debounce. The debounce exists because
a compile is expensive; that is the thing to make cheap first, and
HMR-ROSLYN-65 (external csc per compile) is the real ceiling.

### UB-159 — nothing owned "which assembly is the current build" `UNVERIFIED` `CRITICAL`

This is the root cause behind a run of symptoms I chased separately and patched
one at a time. Recording it as one entry, because it is one bug.

Every hot swap LOADS ANOTHER assembly, and each carries the same
`[UitkxSource]` path for the module it was built from. `ResolveComponentType`
resolved a component by SCANNING loaded assemblies for that stamp - so once a
session had produced more than one swap, the scan returned an arbitrary one, in
practice the oldest still loaded. Nothing in the system held the answer to
"which assembly is the current build of this module"; the pane guessed, every
time, from data that could not distinguish them.

Symptoms this produced, all of which I previously attributed to other causes:

- Leaving a component and coming back rendered an EARLIER build (owner,
  2026-08-22). `MountPreview` passed no assembly hint, so the scan chose.
- It STUCK that way, because UB-148 correctly skips rebuilding a module whose
  text has not moved - so no fresh assembly arrived to displace the stale one.
  Before UB-148 the constant rebuilding hid this.
- A style module linked to a component had no visual effect until an unrelated
  edit: the component's own build was current, but the pane was rendering an
  older component assembly compiled before the style had any entries.
- The preview vanishing on a click (UB-157), which I fixed by having the PANE
  remember an assembly - a patch at the wrong layer, treating the symptom.

FIX at the layer that has the answer: `BuilderPreviewCompiler` produced these
assemblies, so it records which one each module was last built into and exposes
`BuiltAssemblyFor(path)`. The window hands the pane an explicit assembly on
every path - after a compile, after a SKIPPED compile, and on mount - so the
pane never scans and never guesses. A failed build forgets the entry, so a
broken module does not keep serving its last good one.

LESSON: I fixed four symptoms of this across four rounds without asking who was
supposed to own the fact. The question that would have found it in one step is
"which component knows this, and is it being asked?" - not "what could make this
particular render wrong".

### UB-160 — typing ran a full analyzer pass and rebuilt the element set per keystroke `UNVERIFIED` `MED`

Owner 2026-08-22: typing a component or attribute name produces spikes.

Two costs sat directly on the keystroke path, both measured against what the
user can actually perceive rather than guessed at:

1. `CodeField.Recolor` ran `BuilderLanguage.Diagnose` - the whole diagnostics
   analyzer - on every character. Colouring genuinely has to be synchronous,
   because the user is looking at it; nothing about DIAGNOSTICS has to be true
   this keystroke. They now run 250 ms after typing settles, sharing nothing
   with the colouring pass but one extra parse per settle instead of one full
   analyze per character.

2. `KnownElementsOrNull` rebuilt its set - schema element names plus every
   runtime-registered element plus the graph's exports - on EVERY call, and it
   is passed to `SetContent` on every programmatic edit. Worse than the
   allocation: `SetKnownElements` skips its re-colour when handed the SAME
   INSTANCE, so a fresh set each time forced a SECOND full re-colour of the
   source pane per edit. It is now built once per graph and invalidated where
   the graph changes.

NOT ADDRESSED, and the next thing to look at if spikes remain: `RecolorRows`
rebuilds the rich text of every visible line on each keystroke. That is real
work and it is on the synchronous path by necessity, but it could be limited to
the lines that actually changed. Left alone deliberately - it is a correctness-
sensitive path and the two cheap wins above should be measured first.

### UB-161 — the preview compiled on keystrokes instead of on actions `UNVERIFIED` `MED`

Owner ask 2026-08-22: compile when an ACTION happens, on the same rule the
history uses - not while typing, where a name typed and abandoned costs a build
of half-written code that can only fail.

RESEARCH FIRST, because the rule is only as good as the trigger list. Every way
a rendered result can change was enumerated from the mutation sites, not from
memory: `ApplyEdit` (8 call sites), direct `BufferText` assignment, `Undo`/`Redo`,
`AdoptDiskText`, `MarkClean`, session creation and removal, and the path changes
a rename produces. That collapses to four triggers:

1. an action commits - every canvas gesture, an applied or cancelled source
   edit, undo, redo, a history jump, a rename, a format-on-save
2. the preview mounts - opening, switching file, canvas remount, abort
3. the source pane's edit finishes - focus leaves the field
4. a buffer is adopted from disk - an external change

TWO GAPS THE RESEARCH FOUND, both pre-existing:

- Trigger 4 did not exist. An external file change refreshed the CARD and never
  rebuilt, so the preview silently kept showing the pre-change build. Rare
  enough that nobody hit it; wrong all the same.
- `RefreshEditedBuffer` has no callers at all, not even a delegate reference.
  Dead code that looked like a fifth trigger.

AND THE FINDING THAT DECIDED THE DESIGN: `Record` with no open scope commits
IMMEDIATELY, so the source pane was already creating one history entry PER
CHARACTER - a hundred rows for typing a name, and a Ctrl+Z that walked back one
letter at a time. Compiling "on history commit" would therefore have changed
nothing. Typing now goes through `RecordTyping`, which merges consecutive
keystrokes in the same file into one entry, at the tip of the history only and
never inside a gesture scope.

The CARD still re-parses per keystroke - that is a cheap local parse and it is
what makes the canvas feel live. Only the COMPILE waits for a boundary.

NOT FIXED, and it is the other half of what the owner feels: a single compile
still spawns an external csc process on Unity 6.5 (HMR-ROSLYN-65), so the pause
AFTER committing an edit is unchanged. Fewer compiles, not faster ones.

### UB-162 — undoing the only module left a card that could not be deleted `UNVERIFIED` `HIGH`

Owner report 2026-08-22: create a component in a fresh builder, Ctrl+Z, and the
card stays - emptied rather than removed - and right-click delete does nothing to
it. The title bar reads "1 file(s), 0 dirty", which is the tell: there was no
session at all, so nothing was dirty and nothing could be deleted.

TWO causes, both needed for the symptom:

1. `RebindFocusIfMissing` walked the sessions looking for a new focus and, when
   there were NONE left, simply returned - leaving `_focusFile` naming the module
   that had just been discarded. `MountCanvas` mounts whatever `_focusFile`
   names, so it mounted a module with no session behind it. An empty workspace
   now clears the focus, which is what makes `MountCanvas` show the empty state.

2. `LoadTreeAsync` added the focus file to its inventory UNCONDITIONALLY, after
   the hidden-file filter had been applied to everything else. So a focused
   module that was deleted - or whose path a move had vacated - still got a node,
   and deleting it again changed nothing, because the card was never coming from
   a session in the first place. The focus is now filtered like every other file,
   and if it is hidden it is left out of the member set too.

Also removed here: `RefreshEditedBuffer`, which had no callers at all, not even
a delegate reference. It was found while enumerating the compile triggers for
UB-161, where it looked like a fifth trigger.

### UB-163 — an empty workspace threw on every focus lookup `UNVERIFIED` `HIGH`

Introduced by UB-162 and reported immediately: after Ctrl+Z removed the only
module the card finally went away, and the console filled with
`ArgumentNullException: Value cannot be null. Parameter name: key` from
`ApplyLedgerWrites`, `RecompileWhenQuiet` and `RequestServerTokensWhenQuiet`.

ROOT CAUSE: UB-162 made `_focusFile` null when nothing is left to focus, which
is correct - and exposed that NOTHING tolerated it. `Dictionary.TryGetValue`
throws on a null key, so `BuilderWorkspace.TryGet(null)` threw rather than
answering "nothing"; and nine separate `Path.GetFullPath(_focusFile)`
comparisons threw for the same reason. Only one of the ten had guarded itself.

FIX at both layers rather than at the ten call sites:

- `TryGet` treats a null path as NOT FOUND. A lookup asked about nothing should
  answer "nothing"; every caller already handles a null result.
- A single `FocusFull` accessor returns the focused full path or empty, and all
  ten comparisons go through it. An empty string simply never equals a real
  path, so "there is no focus" reads as "this is not the focused file" - which
  is what each of those comparisons actually wants.

This also explains the second half of the report - right-click delete doing
nothing. The delete DID mark the file, then threw on its way to remounting the
canvas, so the card never went away. One exception, two symptoms.

### UB-164 — "read-only" was blamed for a refusal that had nothing to do with permissions `UNVERIFIED` `MED`

Owner report 2026-08-22: delete refuses with "Can't delete NewerComp.uitkx
(read-only)" on a module the builder itself created under Assets, which is
writable by definition.

ROOT CAUSE: `MarkForDeletion` returns false for TWO unrelated reasons - the
location is read-only, or the module is ALREADY marked - and the window reported
both as read-only. A module that had been marked by an earlier attempt therefore
reported a permissions problem that did not exist, and repeating the delete
could never have helped: it was already deleted as far as the model was
concerned, and the CARD was the thing that was stale.

That staleness came from UB-163: the first delete marked the file and then threw
on its way to remounting the canvas, so the card survived. Every attempt after
that hit the already-marked branch and blamed permissions.

FIX: the handler names each case. An already-marked module re-syncs the canvas
instead of complaining, which self-heals exactly the state UB-163 produced, and
a genuine read-only refusal names the DIRECTORY so the claim can be checked
rather than taken on faith.

### UB-165 — deleting the last visible module crashed the tree build `UNVERIFIED` `CRITICAL`

Owner report 2026-08-22: "delete still doesnt work at all, its broken
completely."

ROOT CAUSE, and it is UB-162's fix reaching a state that was never reachable
before: `SeedDefaultPositions` opens with `depth[rootIndex] = 0` over an array
sized by the node count. UB-162 correctly stopped a hidden focus from being
forced into the member set, which made an EMPTY member set possible for the
first time - delete the last visible module and there is genuinely nothing left
to lay out. Indexing a zero-length array threw, inside `LoadTreeAsync`.

WHY IT LOOKED LIKE DELETE DID NOTHING: `BuilderCanvasHost.Mount` wrapped the LSP
call and the graph build in ONE catch that reported everything as "LSP
unavailable". So an IndexOutOfRangeException in the layout surfaced as a
language-server problem, the canvas never updated, and the delete - which had
already marked the file correctly - appeared to be ignored.

FIXED, three things:

- An empty tree lays out as empty. A root index outside the node list falls back
  to 0 rather than throwing.
- The two failures are separated. An LSP failure still says so; a build failure
  says "Could not build the tree" AND logs its stack, so the next one names
  itself instead of pointing at the wrong subsystem.
- The import context menu uses `ShowSimple`, like the card menu, so it opens at
  the row that was right-clicked instead of away from it. One action does not
  need a search field either.

### UB-168 — the card menu deleted an INDEX, not a module `UNVERIFIED` `CRITICAL`

Owner report 2026-08-23: delete silently does nothing, with no toast, no error
and nothing in the Unity log - verified, the log is clean.

ROOT CAUSE: `ShowCardMenu` built its items as `OnPick = () => RequestDeleteCard(index)`,
capturing the node's INDEX at the moment the menu opened. A pick runs later, and
by then the graph may have been rebuilt - a compile finishing, a canvas refresh -
so the index no longer addresses the module it was aimed at. `RequestDeleteCard`
then hit its bounds guard and returned false INTO SILENCE: no toast, no log,
nothing. That is why it read as "completely broken" rather than as a failure.

This is the same index-as-identity fragility the project-model refactor was
about, surviving in a menu closure.

FIX: the menu captures the module's PATH and calls `OnDeleteFile` directly, so a
pick cannot address the wrong module or a missing one. The keyboard path still
resolves by index - it acts immediately, on the live selection - but now says
"Nothing selected to delete" instead of returning false quietly.

### UB-169 — the import menu opened wherever the last menu had `UNVERIFIED` `MED`

Owner report 2026-08-23: right-clicking an import row on the COMPONENT opened
the menu over the STYLE card.

ROOT CAUSE: every menu opens at the click only because the GESTURE records where
that was, via `RememberMenuPointer`. Two gestures do it - the canvas right-click
and the library. The import row added in UB-166 did not, so `Place` fell back to
the STALE remembered point, which was wherever the previous menu had been opened
from. Not a placement bug so much as a missing handshake.

FIX: the import right-click records its pointer like every other menu gesture.

### UB-170 — deleting one card deleted a different one `UNVERIFIED` `CRITICAL`

Owner report 2026-08-23, and the repro names the bug exactly: create a
component, create a style module, link NOTHING. Delete the style - nothing
happens, just a blink. Delete the COMPONENT - and the STYLE disappears.

TWO causes, compounding:

1. NODE ORDER WAS NONDETERMINISTIC. The member set is a `HashSet<string>`, and
   the nodes were built by iterating it. HashSet order is unspecified and shifts
   as the set changes, so adding or deleting a module could RENUMBER every card.
   Anything holding a card index from a previous render then addressed a
   different module than the one it was built for.

2. THE CARD MENU CARRIED AN INDEX. `onCardContext(index)` is a lambda living on
   a KEYED element - keyed by file path, so the element survives re-renders by
   design. When the numbering shifted underneath it, the element kept a lambda
   closed over the OLD index. Right-clicking one card opened the menu for
   another, and the delete followed the menu.

That is the full explanation of the repro: the two cards had swapped numbering,
so each delete acted on the other card - and deleting the style "did nothing"
because it marked the component, which was then hidden and re-added by the
pending-new pass on the same load.

FIX, both halves:

- Nodes are built from a SORTED list, so numbering changes only when membership
  genuinely does.
- The card menu carries the module PATH and the host resolves it when the menu
  opens. UB-168 fixed this one layer up - the menu ITEM - while the number
  reaching the menu was already wrong. This is the same lesson one layer deeper:
  an index is a position, not an identity, and a position is not safe to carry
  across a render.

STILL INDEX-BASED, and left alone deliberately: `onSelect` and `setSelected`
carry indices through the same keyed lambdas. With ordering now deterministic
the window for staleness is much smaller, and the consequence is cosmetic - the
wrong card highlights - rather than destructive. Worth converting, not worth
churning the whole view for in the same pass as a data-loss bug.

### UB-171 — the focused module could never be deleted `UNVERIFIED` `CRITICAL`

Owner report 2026-08-23: the toast reads "Deleted someNew.style.uitkx - applies
on Save" - the RIGHT module, correctly marked - and the card stays on the canvas.

ROOT CAUSE: `ConnectedComponent` seeds its result with the start node
(`seen.Add(start)`), so the FOCUS is always in the member set. UB-162 added
`if (focusVisible) member.Add(focus)` to keep a hidden focus out - and that guard
was a NO-OP, because the walk had already put it in one line earlier. A deleted
module that happened to be the focused one therefore kept its card forever, and
since the builder focuses a module the moment you create it, that is the normal
case rather than an edge one.

FIX at the layer that matters: ONE gate, where the cards are built. Guarding each
ROUTE into the member set is what failed - there were three, and the walk's own
seeding was invisible from the call site. Every contributor now passes the same
hidden check before a node exists.

Also fixed here: a module marked for deletion is no longer a valid FOCUS. Its
session lives until Save, so the missing-session test alone left the window
pointed at a module the user had just deleted, with the preview still describing
it and the source pane still showing it.

LESSON, and it is the third time in this campaign: I guarded the CALLERS instead
of the place the decision is finally made. UB-162 guarded two routes into the
member set and missed the one inside a helper; the fix is a single gate at the
point of use, which cannot be bypassed by a route nobody remembered.

### UB-172 — a deleted module kept its NAME reserved `UNVERIFIED` `HIGH`

Owner report 2026-08-23: delete a style module (which now works), then create it
again with the same name - "already exists".

ROOT CAUSE: a deletion is PENDING until Save, so the session goes on occupying
its path. `CreateNew` refused on `_sessions.ContainsKey`, and `ValidateNewName`
refused on `TryGet != null`, both of which are true for a module that is deleted
as far as the user is concerned. The name was reserved by something invisible.

FIX: one rule - `IsPathAvailable` - used by the prompt, by the creation guard and
by `CreateNew` itself, so they cannot disagree. A path whose deletion is pending
counts as AVAILABLE, and creating there REVIVES that session: the deletion is
taken back and the buffer replaced, rather than a second session being added for
the same path.

Reviving rather than adding also avoids a save-ordering hazard that would
otherwise have been introduced here: `SaveAll` writes before it deletes, so a
fresh session at a path already queued for deletion would have been written and
then immediately trashed.

KNOWN ASYMMETRY, recorded rather than papered over: undoing the re-creation
discards the revived session but does not restore the deletion MARK, so undo
lands on "absent" rather than on "deleted, pending". For a never-saved module -
the reported case - those are the same state. For a SAVED module they differ, and
the module would come back at Save. Deleting a saved module and re-creating it
under the same name inside one session is the only way to reach that, and the
right fix is for the ledger to model a revive as its own change kind.

### UB-173 — the tree model `SHIPPED` `STRUCTURAL`

Not a defect report: the shape that produced UB-135 through UB-172. Intent was
stored BESIDE the data - a pending-delete list, a pending-folder-move list - and
every consumer had to join the two. Five defects in two days were one consumer
that did not know to join, or one route that bypassed the join.

Plans~/BUILDER_TREE_MODEL.md is the plan and it is fully implemented. Load reads
the tree once; every manipulation happens on it; rendering reads it; Save walks
it and writes. Deletion is ABSENCE - a module leaves the tree and that is the
whole of it - and Save works out what that implies by diffing against the paths
that were on disk last time. There is nothing left to join.

This RETIRES the known asymmetry recorded under UB-172. There is no "deleted,
pending" state for an undo to land on any more: the ledger holds the module the
deletion removed, and undo puts that same module back with its identity, its
buffer and its DiskPath intact. Deleting a saved module and re-creating it under
the same name inside one session is now the same operation as any other move
through the tree.

### UB-174 — the library pane was one long scroll `SHIPPED` `LOW`

Owner ask 2026-08-22, deferred behind the delete fix: "on the left side menu,
lets have 5 items alwyas shown, the rest should be collapse".

Each section now shows five rows and folds the rest behind "+N more"; opening one
is remembered across rebuilds, so a new graph cannot close a drawer the user just
opened. A SEARCH reaches the whole library, folded rows included - a filter that
could not see past the fold would be a search that lies about what is there.

### UB-175 — style entries do not chain `OPEN` `LOW`

Owner ask 2026-08-22, deferred behind the delete fix: "lets make it so when 1
styles is over the +entry is 'focused' right a way and enter behave as if you
clicked the +entry".

Two readings, materially different, and the owner has not been asked yet: FOCUS
the card's "+ entry" row after an entry commits, so Enter opens the key menu
again; or RE-OPEN the key menu automatically, so entries chain until Escape. The
first needs the fiber-rendered canvas row to be focusable, addressable after a
re-render, and Enter-activated - none of which is verifiable outside the editor.
