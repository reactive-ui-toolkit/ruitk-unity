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
| **ISO-E** | Delete the ambient static overlay (ISO-3). | `PublishSourceOverlay` gone; nothing static remains |
| **ISO-F** | Guard: a test that compiles a tree whose modules exist ONLY in memory, with the filesystem implementation deliberately throwing. | any new disk read on the builder path fails loudly |

**ISO-F is the point of the campaign.** Everything before it fixes today's
leaks; ISO-F is what stops tomorrow's. Without it this is discipline again, and
discipline is what produced three bugs in one day.

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

**Gap:** there is no way to declare what props a component takes. The card shows
`SomeComponent()` and the signature is parsed but read-only. Without it,
components cannot be parameterised, and the preview's props knobs have nothing
to bind to.

Not a bug — a missing authoring surface, and the same shape as the router work,
which is why the owner grouped them.

| id | Work |
|---|---|
| **SIG-1** | Make the signature row editable: add / rename / remove a prop, with a type from the same typed vocabulary the style-key menu uses. |
| **SIG-2** | Emit the props type and thread it through, so `V.Func<P>(…)` and the knobs pick it up. |
| **SIG-3** | Call sites: adding a required prop invalidates existing usages — surface that rather than silently breaking importers, the way rename already does. |
| **SIG-4** | Defaults, so an added prop does not break every existing usage at once. |

**Depends on isolation** only lightly, but SIG-3 needs the tree to answer "who
uses this component", which is a data-model question and cleaner after Part 1.

---

## Part 3 — Router

**Settled in conversation, 2026-08-27/28.**

### 3.1 What exists

- Elements: `Router`, `Routes`, `Route`, `Outlet`, `NavLink`, `Link`, `Navigate`
  — all in the schema with full typed attributes. **`Outlet` exists.**
- 16 router hooks in the API: `UseNavigate`, `UseLocation`, `UseParams`,
  `UseQuery`, `UseSearchParams`, `UseRouteMatch`, `UseMatches`, `UseGo`,
  `UseCanGo`, `UseBlocker`, `UsePrompt`, `UseResolvedPath`, `UseRouter`,
  `UseNavigationState`, `UseNavigationBase`, `UseLocationInfo`.
- **No object/config route definition.** `RoutesFunc` walks `<Route>` children;
  there is no `useRoutes(objects)` analogue. Not adding one.

### 3.2 Decisions

- **Routes are authored as children only.** `<Route path="x"><Foo/></Route>`.
  The builder never offers the `element` attribute and never emits `V.*` — a
  user should not have to know the codegen form exists.
- **`element` stays in the LANGUAGE.** It takes a `VirtualNode`, so it is the
  only way to pass a computed node (`element={flag ? A() : B()}`), hand-written
  C# uses it, and our own `MainMenuRouterDemoFunc` sample uses it twice.
  Deprecating it would cost a minor and buy nothing.
- **A file that already uses `element={…}` still displays it** as an opaque
  attribute row. No conversion: it is only possible for trivially-recognisable
  calls and impossible for `element={someVar}`, and a half-working conversion is
  worse than none.
- **Dropping onto `<Router>` is fine** — those children are always-on shell, and
  that is legitimate. Only `<Route>` subtrees are conditional.

### 3.3 Work

| id | Work | Notes |
|---|---|---|
| **RTR-1** | The 16 router hooks into `HookRegistry` | **Framework-wide, not builder-only.** Generator, analyzer, LSP hover/completion, HMR emitters and the builder palette all read that table. Needs the full parity pass. |
| **RTR-2** | Auto-provide a router in the preview when a component references routing and does not supply its own | Guarded: nested `<Router>` throws by design. `<Router>` needs no props — `providedHistory ?? new MemoryHistory(initialPath ?? "/")`. |
| **RTR-3** | Preview address bar feeding `initialPath`, showing the active match | Route branches are unreachable today even when rendering works. |
| **RTR-4** | A "Routing" section in the library | The tags are all present but sit in a flat 79-entry list showing 5 + "+74 more". Present is not findable. |
| **RTR-5** | `<Route>` rows show `path` as their identity; a `<Route>` subtree is visually distinct from shell | Conditional vs always-on is the whole point of a router and is invisible in a flat markup list. |

### 3.4 The deeper point behind RTR-2

The preview mounts the focused component **bare**:

```csharp
_renderer.Render(V.Func(_renderDelegate, _knobProps));
```

No ancestors, no context. `UseRouter()` is `UseContext(...)`, so it returns null
and `RoutesFunc` renders nothing — **silently**. This is not router-specific: the
same hole swallows any component depending on an ancestor (`ProvideContext`, a
portal target, a signal scope). The router is the most visible instance.

So RTR-2 should be the first provider of a general **preview environment**, not
a router special case — otherwise the same hole sits next to it for the next
context-dependent component.

---

## Order

1. **Part 1** — ISO-A through ISO-F.
2. **Part 3** — RTR-1 first (framework-wide, benefits everything), then RTR-2/3
   which make routed components actually renderable, then RTR-4/5.
3. **Part 2** — SIG-1..4.

Parts 2 and 3 both add authoring surfaces to the same card, so whichever runs
second inherits a settled pattern from the first.
