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
