# Builder isolation, component signatures, and the router

Three pieces of work, in the order the owner set: **isolation first**, then the
component signature surface, then routing. The first is a correctness campaign;
the other two are authoring features that both become easier once it lands.

Defect history is `Plans~/UI_BUILDER_BUGS.md` (UB-##). Behaviour contract is
`plans/UI_BUILDER_CAPABILITIES.md`. This file is the plan.

---

## Part 1 — Isolation (do this first)

### 1.1 The principle, in the owner's words

> The whole canvas process should be idempotent and isolated.
>
> **Path one, existing files:** open the builder, scan all files, build a data
> model for that whole tree. From that moment everything we do — every action,
> every render, everything — is done on that data model. Then Save performs the
> exact writes in the exact order.
>
> **Path two, no files:** start with an empty data model, fill it up, and Save
> does the same thing.

The document layer already works this way. What does not is **rendering**.

### 1.2 What is actually true today (measured, 2026-08-28)

Filesystem / loaded-assembly touches per file:

| File | touches | verdict |
|---|---|---|
| `Builder/Editor/Canvas/BuilderGraphService.cs` | 0 | the canvas projection is pure |
| `Builder/Editor/Compile/BuilderPreviewCompiler.cs` | 1 | fine |
| `Builder/Editor/Preview/BuilderPreviewPane.cs` | 1 | fine |
| `Builder/Editor/Document/BuilderWorkspace.cs` | 29 | **correct** — load and save are its job |
| `Editor/HMR/UitkxHmrCompiler.cs` | **44** | the leak |

**The builder's own editing path is already isolated.** Every defect in the
2026-08 wave came from the last row: the preview borrows the HMR compiler, and
that compiler's entire vocabulary is file paths.

### 1.3 Why it leaks

An in-memory module has no natural identity in a path-shaped API, so it is given
a **synthetic path** under `Assets/__RuitkBuilderUnsaved__~/`. Nothing is ever
written there — verified: the directory does not exist on disk — so the path is
a pure fiction that 44 call sites then treat as a filesystem fact.

The seam that makes the fiction work is `SourceOverlay`, and its weakness is
structural: it is **ambient global state**, installed by reflection into a static
field.

```csharp
_importScopeOverlay.SetValue(null, SourceOverlay);   // static
```

An overlay that is not a parameter cannot be enforced — every call site has to
*remember* to route through it. `UitkxSourceExists` exists precisely because
`File.Exists` is wrong here, and nothing stops the next author typing
`File.Exists`.

### 1.4 The three worlds

Every defect in the wave was a leak from world 1 into world 2 or 3:

| # | World | Staleness | Defect it produced |
|---|---|---|---|
| 1 | The builder's tree | authoritative | — |
| 2 | The disk | stale until Save | **UB-222** — `IsPathAvailable` asked `File.Exists` about a pending move |
| 3 | The loaded assembly table | stale **and** polluted by every tree opened this session | **UB-223** — `ResolveComponentFqn` scanned by simple name |
| 2 | The disk | (same) | **UB-203** — an importer bound to the SAVED copy of a style module |

World 3 deserves emphasis: it accumulates every component from every tree opened
since the editor started, which is why UB-223 presented as *"the builder
remembers all files"*.

### 1.5 The genuine remaining leaks

Audited rather than assumed. Most of the 44 are **toolchain** — the dotnet path,
reference-assembly locations, temp dirs, output DLLs. Those are legitimately
files and stay. `NewCsFileDiscovery` reading plain `.cs` sources from disk is
also correct: those are real files the builder does not model.

What is left, and what this campaign fixes:

| id | Leak | Where | Symptom |
|---|---|---|---|
| **ISO-1** | Companion `.uitkx` discovery by directory scan — `Directory.GetFiles(dir, prefix + "*.uitkx")` | `UitkxHmrCompiler.cs` ~819 and ~1356 | an unsaved companion is invisible to its own component; a same-prefixed file from elsewhere in the directory is picked up |
| **ISO-2** | `ResolveComponentFqn` assembly scan | `HmrCSharpEmitter.cs` ~182 | now a fallback after the UB-223 fix, but still reachable and still first-match-by-simple-name |
| **ISO-3** | The overlay is ambient static state | `UitkxHmrCompiler.PublishSourceOverlay` | correctness depends on every author remembering; unenforceable |
| **ISO-4** | Existence and read are two separate seams (`UitkxSourceExists`, `ReadUitkxText`) that a caller can bypass | throughout | UB-222's shape, one layer down |

### 1.6 The shape of the fix

**A resolution context, passed — not ambient.**

The builder already knows the closed set of modules; `BuilderPreviewCompiler`
hands them to the compiler in import-graph order. What is missing is the context
that travels with them:

```
IModuleSource                       // the compile path's ONLY view of module truth
    bool   Exists(path)
    string ReadText(path)
    IEnumerable<string> SiblingsWithPrefix(dir, prefix)   // replaces the glob
    string ComponentNameOf(path)                          // replaces the assembly scan
```

- The **builder** implements it over `BuilderTree`. No disk, ever.
- **HMR** implements it over the filesystem — HMR genuinely is file-driven and
  must stay that way.
- The compile path takes it as a **parameter** and has no other way to ask.

The precedent is already in the codebase and was written for exactly this
reason. `BuilderPreviewCompiler._built`:

> *"The preview has to render the CURRENT build, and it cannot work out which
> that is by scanning loaded assemblies… The compiler produced these assemblies,
> so it is the only thing that knows which is current. It says so rather than
> letting the pane guess."*

Replace "scan ambient state" with "the producer tells you". That is the whole
campaign.

### 1.7 Staging

Ordered so each stage is separately verifiable and none is a big-bang.

| Stage | Work | Done when |
|---|---|---|
| **ISO-A** | Introduce `IModuleSource` + a filesystem implementation. Route `ReadUitkxText` / `UitkxSourceExists` through it. Behaviour identical. | builder smoke green, HMR battery unchanged |
| **ISO-B** | Builder implements it over `BuilderTree`; `BuilderPreviewCompiler` passes it. `SourceOverlay` becomes a thin adapter onto it. | preview renders unsaved trees with no disk read for module text |
| **ISO-C** | Replace the two companion globs (ISO-1) with `SiblingsWithPrefix`. | an unsaved companion is found by its component |
| **ISO-D** | Replace the `ResolveComponentFqn` fallback (ISO-2) with `ComponentNameOf`, keeping the scan only for hand-written package components with no import. | two trees sharing a component name cannot cross |
| **ISO-E** | DONE — an installed overlay is AUTHORITATIVE; the static stays as transport, with one policy behind it (see 1.9) | the language lib no longer reads disk while the builder is driving |
| **ISO-G** | DONE — every fall-through to disk is counted and named per compile | a memory-only tree reports zero |
| **ISO-F** | Guard: a test that compiles a tree whose modules exist ONLY in memory, with the filesystem implementation deliberately throwing. | any new disk read on the builder path fails loudly |

**ISO-F is the point of the campaign.** Everything before it fixes today's
leaks; ISO-F is what stops tomorrow's. Without it this is discipline again, and
discipline is what produced three bugs in one day.

### 1.9 ISO-E: the static stays, but it is no longer a second policy

RESOLVED 2026-08-28. My first read of this was wrong in a way worth keeping:
I said "four parity layers" and treated it as a wall. It has exactly ONE
consumer, `ImportScopeFacts.ReadTargetDirectives`, and the contract test guards
the COMPILER surface, not this file.

The field still exists, because it is how the language lib is told what a
module says and its API is static. What changed is that it is no longer a
second POLICY:

- The builder overlay now delegates to `BuilderModuleSource`, so there is one
  implementation of "tree first, disk only for what the tree does not own".
- An installed overlay is AUTHORITATIVE: the lib no longer falls back to
  `File.Exists` / `File.ReadAllText` behind it. A null means the module is
  genuinely absent, rather than "ask the disk instead" - which is what used to
  resurrect a module the builder had deleted or moved.
- No overlay means HMR, whose truth IS the disk. That path is untouched.

One hazard found while doing it, and the reason guard ORDER matters: the
filesystem read retries eight times with exponential backoff, and a MISSING
file throws `FileNotFoundException`, which is an `IOException`, which that loop
treats as a lock worth waiting out. Roughly 635ms of sleeping to discover a
file is absent, on a path that runs per import probe. `BuilderModuleSource`
therefore tests existence BEFORE it reads.

Verified: SG suite 1879/1879, LSP suite 180/180, ModelTests ALL PASS, builder
smoke 0 errors, DLLs rebuilt Release after the test run clobbered them.

The overlay is not the compiler's own state. It is pushed by reflection into a
**public static field in the language library**:

```csharp
// ide-extensions~/language-lib/ImportScopeFacts.cs
public static Func<string, string?>? SourceOverlay;
```

The language lib resolves import targets of its own when it computes the using
aliases an import implies, and that is how it sees an unsaved one. Deleting the
static without replacing the mechanism does not make the builder cleaner - it
makes unsaved imports invisible to alias resolution, which is a regression.

Replacing it means changing a public API on a file that is `<Compile Include>`-
linked into the generator, the LSP server and the Unity runtime - four parity
layers that must agree. That is its own campaign, not a stage of this one.

What holds today: `PublishSourceOverlay` runs per compile and writes whatever
that compiler instance has, so an HMR compile (overlay null) resets it and a
builder compile sets it. Both are main-thread and serialized per compile, so
the two instances cannot interleave. It is safe, but it is safe by timing
rather than by construction, which is exactly the property ISO-F now guards
against elsewhere.

### 1.8 Risk

- `UitkxHmrCompiler` is shared with HMR, which real users depend on. Every stage
  must keep the HMR path byte-identical in behaviour; the filesystem
  implementation exists so that it is.
- `HmrEmitterParityContractTests` (50 tests) guards SG↔HMR emitter drift and
  must pass at every stage.
- Running the SG suite clobbers the committed `Analyzers/` DLLs — restore them
  after (`git restore Analyzers/`) and kill `VBCSCompiler`.

---

## Part 2 — Component signatures (#2)

**Settled 2026-08-28. IMPLEMENTED and shipped in 0.19.0 on 2026-08-29.**

### 2.1 The gap

There is no way to declare what props a component takes. The card shows
`SomeComponent()`, the signature is parsed but read-only, and the preview's
knobs therefore have nothing to bind to. Not a bug — a missing authoring
surface.

### 2.2 What props actually are

Ordinary C# parameters on the export:

```
export VirtualNode DoomHUD(int health, int armor, WeaponType weapon, int[] ammo, ...)
export VirtualNode ContextConsumer(string label = "Primary Panel")
```

Three facts from the research that shape everything below:

1. **The type vocabulary is OPEN.** Style keys are a closed set of ~86; prop
   types are arbitrary C# — `WeaponType`, `int[]`, `KeyCard`, user enums. So a
   "pick a type" menu can never be exhaustive, and the answer is the idiom the
   builder already uses for attributes: a searchable typed menu with a
   free-text fallback.

2. **The parser already records defaults.**
   `FunctionParam(string Type, string Name, string? DefaultValue)` — so
   "was a default written?" is answerable from the AST today.

3. **The generator ERASES that distinction.** `int x` and `int x = 0` emit
   identical code:

   ```csharp
   public sealed class FooProps : IProps
   {
       public int X { get; set; } = 0;             // from `int x`
       public string Label { get; set; } = "hi";   // from `string label = "hi"`
   }
   ```

   A missing prop silently becomes `default(T)`, and there is no diagnostic for
   it anywhere in the codebase. That is the thing being changed.

### 2.3 Decisions (owner, 2026-08-28)

- **`required` means NO DEFAULT WRITTEN.** No new marker syntax. This makes
  `int x` and `int x = 0` semantically different for the first time — a real,
  quiet language change, and the reason it belongs in a minor rather than a
  patch.
- **A call site omitting a required prop is an ERROR**, project-wide, straight
  to Error with no deprecation window. Considered and rejected: the builder
  auto-supplying a default, which hides the mistake rather than surfacing it.
- **In the preview an error keeps the LAST GOOD render**, which is already how
  the preview handles a failed compile — no new mechanism needed.
- **Removing a prop strips the attribute at every known call site**, as one
  undoable action. The builder has the tree and the import edges, and module
  rename already rewrites every importer, so this is the same machinery
  pointed at attributes. Call sites OUTSIDE the open tree get the diagnostic;
  that is the honest limit.
- **All types plus free text**, as with style keys and attributes.
- **v1 is the whole thing** — add, rename and remove together, not staged.

### 2.4 Blast radius of the Error tier — MEASURED

A Warning→Error bump is breaking by this repo's own rule (CLAUDE.md), so it was
measured before being accepted. Two attempts to automate the scan by hand-rolled
search returned 0/46 and 46/46, both provably wrong against the source; the
reliable instrument is the analyzer itself.

**The number, from the analyzer, over the whole bundled corpus: TWO props on ONE
call site.** The `SamplesCorpusGateTests` gate — the same one that runs the real
generator over `Samples/` the way a fresh Unity import does — failed on exactly:

```
GameScreen.uitkx(34,1): UITKX0115: <HUD> is missing required prop 'spriteSheet'
GameScreen.uitkx(34,1): UITKX0115: <HUD> is missing required prop 'wave'
```

Both were **genuine defects, not false positives**. `HUD` renders
`$"WAVE {wave}"` and builds its life icons from `MakeSpriteStyle(spriteSheet, …)`,
and the call site passed neither — so the shipped Galaga sample rendered
"WAVE 0" with a null sprite sheet, and had since it was written. Both values were
already in scope at the call site (`spriteSheet` on line 11, `state.Wave`). Fixed
in 0.19.0.

The package's own `.uitkx` files (`Builder/Editor/**`) have **no
component-to-component markup call sites at all** — every one of them is mounted
from C# — so their blast radius is zero.

The check finding two real bugs the first time it ran, and nothing else across
the whole corpus, is the strongest evidence available that the convention was
already being followed by hand and that enforcing it is correction rather than
disruption.

### 2.5 The work — DONE (0.19.0)

**Analyzer (language-wide):**

| id | Work | Where it landed |
|---|---|---|
| **SIG-A** | `UITKX0115` — a call site omitting a parameter with no written default. Error tier, no deprecation window. | `SourceGenerator~/Diagnostics/UitkxDiagnostics.cs`, `PropsResolver.GetRequiredPropNamesByQualifiedName`, `CSharpEmitter.EmitFuncComponent`; `language-lib/Diagnostics/DiagnosticCodes.cs` + `DiagnosticsAnalyzer.CheckElement` |
| **SIG-B** | Parity. Only TWO of the four layers ever validated attributes — the SG emitter and the analyzer (`UITKX0109` lives in both). The HMR emitters and the IDE virtual doc do no attribute checking at all, so leaving them untouched is consistency, not drift. Verified by grep before writing anything. | — |
| **SIG-C** | CHANGELOG states the breaking change with the migration and the exemptions; minor version 0.19.0; extensions 1.12.0. | `CHANGELOG.md`, `changelog.json`, `DISCORD_CHANGELOG.md` |

The decisive design point: the generated `*Props` class **cannot** answer
"was a default written", because it emits an initialiser either way. So the
required set is read from the DECLARED PARAMETERS on both sides — from the
same-pass peer's `FunctionParams` in the SG, and from `WorkspaceIndex.PropInfo`
(which gained `ParamName` and `HasDefault`) in the LSP.

The analyzer's `knownAttributes` map became a map of
`ElementAttributeContract` rather than gaining a second parallel dictionary.
Both facts are decided in one place because their **failure directions are
opposite**: when a tag has several declarants and resolution is ambiguous,
`Known` falls open to the UNION (never invent an unknown-attribute error) while
`Required` falls open to EMPTY (never invent a missing-prop error). Two
independent maps could have drifted into requiring a prop from the union.

**Builder (the three gestures):**

| id | Work | Where it landed |
|---|---|---|
| **SIG-1** | Signature row is now a gesture: click it (or **Props…** on the card menu) → add a prop, with a searchable type menu seeded from the types the OPEN TREE already uses, plus a free-text row. Name, then required-or-default. A required prop is inserted before the first optional one, because C# rejects the other order. | `BuilderWindow.ShowPropsMenu` / `ShowAddPropTypeMenu`, `CanvasView.uitkx`, `BuilderCanvasHost.OnEditProps` |
| **SIG-2** | Rename a prop — the declaration, its USES in the component's own body, and the attribute at every call site in the tree, as ONE ledger entry. | `RenamePropAcrossTree` |
| **SIG-3** | Remove a prop — strips the attribute at every call site the tree knows about, one undo, naming how many callers it touched and warning when the body still uses it. | `RemovePropAcrossTree` |
| **SIG-4** | Knobs: nothing to do. `BuildKnobs` reflects `_knobProps.GetType().GetProperties(...)`, so a new prop appears as a knob once the buffer recompiles. **Verified in the source, not assumed.** | — |

The builder reports `UITKX0115` itself, in the source pane, while you type. Its
contracts are built from the TREE's buffers and carry a NULL accepted-set on
purpose: the builder knows what a component declares, and therefore what is
required, but the full set of what an element ACCEPTS lives in the schema, and
an incomplete one would manufacture `UITKX0109` for legal attributes. So
`ElementAttributeContract.Known` is nullable, and null means "skip the
unknown-attribute check for this element" — which is not the same as empty. The
contracts are recomputed per call rather than cached, because the buffers change
with every keystroke and a snapshot handed over once would report a prop that has
since been renamed.

A fourth gesture fell out of the model: **make required / make optional**.
"Required" is not a flag stored anywhere — it IS the absence of a written
default — so the toggle writes one or takes it away.

All of it is text surgery on the tree's in-memory buffers, in
`Builder/Editor/Document/BuilderSignatureEdit.cs` — pure, Unity-free, and
`Compile`-linked into `Builder~/ModelTests` so every parsing edge is checked
outside the editor. Nothing opens a file: a caller the user has not opened is
still a caller, and `EditSession` reaches it through the tree.

What that scanner exists to survive, each pinned by a check: a generic argument
list split on its own comma (`Dictionary<string, int> map`), a lambda default
whose arrow is not an assignment (`Action<int> onPick = i => { }`), an attribute
value with a brace inside it, a brace inside a *string* value, an attribute
whose name is a prefix of another, and markup inside a C# string literal — which
the builder's own `CodeFieldSpike` contains, and which a naive sweep would have
rewritten. The same scanner now backs the existing attribute menu, which had the
generic-comma bug.

**Known limits, stated rather than hidden:**

- Renaming a prop skips an identifier immediately followed by `=`, because in
  markup that is an ATTRIBUTE NAME rather than a use. The trade is that
  *assigning to* a parameter is missed; that is rare and fails loudly at compile
  time, where silently rewriting an unrelated attribute name would change what
  the component renders.
- Call sites outside the open tree are not rewritten. They get `UITKX0115`.

---

## Part 3 — Router

**Decisions settled 2026-08-27/28. Researched and costed 2026-08-29; not started.**

### 3.1 What exists

- Elements: `Router`, `Routes`, `Route`, `Outlet`, `NavLink`, `Link`, `Navigate`
  — all in the schema with full typed attributes. **`Outlet` exists.**
- 16 router hooks in `Shared/Core/Router/RouterHooks.cs`: `UseRouter`,
  `UseLocationInfo`, `UseLocation`, `UseQuery`, `UseNavigationState`,
  `UseParams`, `UseRouteMatch`, `UseNavigate`, `UseNavigationBase`, `UseGo`,
  `UseCanGo`, `UseBlocker`, `UseMatches`, `UseResolvedPath`, `UseSearchParams`,
  `UsePrompt`.
- **No object/config route definition.** `RoutesFunc` walks `<Route>` children;
  there is no `useRoutes(objects)` analogue. Not adding one.

### 3.2 Decisions

- **Routes are authored as children only.** `<Route path="x"><Foo/></Route>`.
  The builder never offers the `element` attribute and never emits `V.*` — a
  user should not have to know the codegen form exists.
- **`element` stays in the LANGUAGE.** It takes a `VirtualNode`, so it is the
  only way to pass a computed node (`element={flag ? A() : B()}`), hand-written
  C# uses it, and our own `MainMenuRouterDemoFunc` sample uses it twice.
- **A file that already uses `element={…}` still displays it** as an opaque
  attribute row. No conversion: it is only possible for trivially-recognisable
  calls and impossible for `element={someVar}`.
- **Dropping onto `<Router>` is fine** — those children are always-on shell.
  Only `<Route>` subtrees are conditional.

### 3.3 Research findings that change the costing

Done 2026-08-29, before any code.

**RTR-1 carries no arity risk.** The `FiberSlots` column in
`HookRegistry.s_callSiteTable` is the dangerous one — a wrong number shifts hook
ordering and corrupts state at runtime. Every router hook is built on
`Hooks.UseContext` (0 slots) and, in `UseBlocker`'s case, `Hooks.UseEffect` (also
0). Nothing in `RouterHooks.cs` touches `UseState`, `UseRef`, `UseMemo` or
`UseCallback` — verified by scanning the whole file, not by sampling. **All 16
entries are arity 0.** What looked like the risky half of Part 3 is a table
append.

**The table has 12 consumers**, which is the actual work of RTR-1:
`HmrCSharpEmitter`, `HmrHookEmitter`, `BuilderWindow`, `BuilderPreviewPane`,
SG `CSharpEmitter`, SG `HooksValidator`, `UitkxPipeline`, analyzer
`DiagnosticsAnalyzer`, `VirtualDocumentGenerator`, LSP `DiagnosticsPublisher`,
`HoverHandler`, plus the registry itself. `HookRegistry.cs` is
`<Compile Include>`-linked into the language lib, so one edit reaches the
generator, the analyzer, the LSP and the runtime together — that is the
mechanism, and it is why the parity pass is mandatory rather than optional.

Two columns beyond arity need real content per hook: the **insertion snippet**
(compilable against the virtual-doc stubs — this is what the builder's "+ hook"
palette pastes) and the **hover doc**, keyed by BOTH casings
(`useNavigate` and `Hooks.UseNavigate`), as every existing entry is.

**RTR-2 cannot simply always-wrap.** `RouterFunc.Render` throws
`InvalidOperationException` on a nested `<Router>`, by design, disambiguated via
an owner-stamp so a re-render is not mistaken for nesting. The previewed
component may not declare a `<Router>` itself but may IMPORT a child that does —
wrapping then puts a Router inside a Router and trips the guard.

So the rule is: **provide a router only when no module in the focus closure
declares one.** The closure is already computed for the union compile, and its
buffers are the tree's — the check is a tag scan over text the builder already
holds, with no disk access. When a Router IS found, provide nothing and let the
component's own one run.

**RTR-2's construction is settled by the runtime.** `RouterFunc.Render(IProps,
IReadOnlyList<VirtualNode>)` is the ordinary component shape, and
`RouterFuncProps` carries `History`, `InitialPath`, `Basename`, resolving
`providedHistory ?? new MemoryHistory(initialPath)`.

**RTR-3 should own the history, not re-mount with a new `InitialPath`.**
`MemoryHistory` exposes `Push`, `Replace`, `Go`, `CanGo`, `Index`, `EntryCount`,
`Location` and `Listen`. If the preview constructs the `MemoryHistory` and hands
it in as `History`, the address bar is `history.Push(path)` and the active match
is readable — with back/forward for free from `Go`/`CanGo`. Re-mounting with a
different `InitialPath` would instead throw away all component state on every
navigation, which is the opposite of what a preview is for.

**RTR-4 is a one-line ordering change plus routing.** Sections come from
`BuilderLibraryPane.s_sectionOrder`, today five entries: Native elements, Custom
components, Hooks, Style modules, Util modules.

**RTR-5's row text comes from `AttrsDisplay`/`AttrPairsOf`** in
`BuilderGraphService` — a flat `name="value"` join. Route identity means giving
`<Route>` a row label built from its `path` attribute rather than the generic
attribute string.

### 3.4 The deeper point behind RTR-2

The preview mounts the focused component **bare** —
`BuilderPreviewPane.cs:919`:

```csharp
_renderer.Render(V.Func(_renderDelegate, _knobProps));
```

No ancestors, no context. `UseRouter()` is `UseContext(...)`, so it returns null
and `RoutesFunc` renders nothing — **silently**. This is not router-specific: the
same hole swallows any component depending on an ancestor (`ProvideContext`, a
portal target, a signal scope). The router is the most visible instance.

So RTR-2 should be the first provider of a general **preview environment**, not
a router special case — otherwise the same hole sits next to it for the next
context-dependent component. The seam is one function that wraps the focused
node in whatever ancestors the closure says it needs, with the router as its
first and only implementation today.

### 3.5 Work

| id | Work | Cost | Risk |
|---|---|---|---|
| **RTR-1** | The 16 router hooks into `HookRegistry`: arity (all 0), insertion snippet, hover doc under both casings. Then the parity pass over all 12 consumers. | Medium — the table is small, the parity sweep is the work | LOW, now that arity is known |
| **RTR-2** | Preview environment seam (2a), router provider (2b), missing-provider diagnosis (2c) — see §3.8. | Medium–Large | MED — the nesting guard throws; the gate must be right |
| **RTR-3** | Preview address bar over a preview-owned `MemoryHistory`; back/forward from `Go`/`CanGo`; show the active match. | Medium | LOW |
| **RTR-4** | A "Routing" section in the library. | Small | LOW |
| **RTR-5** | `<Route>` rows identified by `path`; a `<Route>` subtree visually distinct from always-on shell. | Small–Medium | LOW |

### 3.6 Sequence, and why

1. **RTR-1 first.** It is framework-wide — completion, hover, the unused-hook
   analyzer and the builder palette all improve the moment the table lands, with
   or without the rest of Part 3. It is also the only item that touches the
   generator and the LSP, so it wants its own commit and its own parity run.
2. **RTR-2 next**, because until it exists a routed component renders EMPTY in
   the preview and nothing else in Part 3 is observable.
3. **RTR-3** turns a rendering router into a drivable one — the first point at
   which route branches are reachable at all.
4. **RTR-4 / RTR-5** are discoverability and legibility; they are worth least
   when the thing they describe cannot yet render, and most once it can.

### 3.7 What to verify before flipping each on

- **RTR-1:** the SG and LSP suites, plus `HmrEmitterParityContractTests` — the
  registry feeds both emitters. A new hook that the analyzer knows and the
  generator does not is exactly the drift that test exists to catch.
- **RTR-2:** a component with `<Routes>` renders its matching branch in the
  preview; a component that declares its OWN `<Router>` still renders and does
  not throw; a parent importing a child that declares one does not throw. That
  third case is the one the gate exists for and the easiest to forget.
  For 2c: a component reading a context key nothing provides reports THAT, by
  name, instead of rendering blank - which is the whole reason 2c is in this
  batch rather than a later one.
- **RTR-3:** navigating the address bar preserves component state (that is the
  reason for owning the history rather than re-mounting).
- **RTR-5:** a `<Route>` with no `path` (index route) still gets a sensible row.

### 3.8 The preview environment — scope, and a correction

The first draft of this plan scoped RTR-2 as "auto-provide a router" and deferred
everything else until "a real component needs a context value the builder cannot
guess". The owner rejected that on 2026-08-29: *"why do we need to hit the issue
before fixing it when we know what the issue is?"*

That is right, and the error is worth recording because it is a reasoning error
rather than a detail. **YAGNI applies to speculative features, not to a defect
already diagnosed.** The draft bundled two different things under one heading and
deferred both:

- **The silence.** `UseContext` returns null when no ancestor provides the key,
  so `RoutesFunc` renders nothing and the stage is blank with no error anywhere.
  That is a known defect, it is general — `provideContext`, portal targets,
  signal scopes — and this campaign has already spent three separate rounds on
  failures whose only symptom was silence.
- **Value entry.** A UI for supplying an actual context value. That genuinely
  needs product design: where it lives, how values are typed, whether it persists
  per component or per tree.

Only the second is a feature. The first is a bug, and deferring it also meant
shipping a seam with exactly ONE implementation — which is not a seam, it is a
hook shaped like the router, and the second consumer would have forced a redesign
of code that had just been settled.

**Revised scope: the silence is fixed in the same batch.** None of it requires
guessing a value.

| id | Work |
|---|---|
| **RTR-2a** | The preview environment seam: one function that wraps the focused node in whatever ancestors the closure says it needs. |
| **RTR-2b** | Router provider — the first implementation, gated on "no `<Router>` in the focus closure" (§3.3). |
| **RTR-2c** | Missing-provider diagnosis: a context key CONSUMED anywhere in the previewed closure and PROVIDED nowhere in it is named in the preview banner. "reads context key 'theme'; nothing in the preview provides it" instead of a blank stage. |

RTR-2c is what gives the seam two consumers on day one, so its shape is checked
against two cases rather than fitted to one.

**Honest limit:** the detection is STATIC — a scan of the tree's own buffers for
`provideContext(...)` and `useContext(...)` keys, which is isolation-correct and
needs no disk. A key computed at runtime cannot be matched and will not be
reported. The alternative is instrumenting `Hooks.UseContext` in `Shared/` to
record misses during a preview render; that is more accurate and is rejected —
it puts builder machinery in the shipped runtime, and "everything we do should
not hurt our current library" outranks a diagnostic's completeness. The static
version catches every case a user can author by hand in the builder, which is
the population that matters here.

**Still deferred, deliberately:** user-entered context values. With RTR-2c in
place that deferral no longer hides anything — the user is told exactly which key
is missing, which is also the information the eventual UI would need to collect.

---

## Order

1. ~~**Part 1** — ISO-A through ISO-F.~~ **DONE**, shipped 0.18.x.
2. ~~**Part 2** — SIG-A..C and SIG-1..4.~~ **DONE**, shipped 0.19.0. It ran
   before Part 3 rather than after: the signature gestures were what the owner
   needed next, and they turned out to depend on nothing in Part 3.
3. **Part 3** — RTR-1 first (framework-wide, benefits everything), then RTR-2/3
   which make routed components renderable and drivable, then RTR-4/5.

Part 2 landing first means Part 3 inherits a settled pattern for adding an
authoring surface to a card: the gesture menu off the card and its row, edits as
pure text transforms in `Builder/Editor/Document/`, and the out-of-Unity model
checks that go with them.
