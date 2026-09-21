# GitHub issues #252–#259 — triage, risk analysis and fix plan

**Raised:** 2026-09-17 on `reactive-ui-toolkit/ruitk-unity`, eight issues, one reporter.
**Analysed:** 2026-09-20 against `feat/ruitk-builder` @ `7265f14c` (0.19.3).

**Provenance note.** The reports are AI-authored. That is irrelevant to whether they
are *true* and highly relevant to whether their *patches* should be used. Every claim
below was re-derived from this repository's source before being accepted, and every
file:line in this document was read directly. **No code from the issues is to be
copied.** Where a fix is described here it is our own. For #255 it is deliberately
different, because the suggested path is unverifiable from here. For #252 our fix
happens to land on the same guard the report proposes — but only after the evidence
in §3 established that it is safe, which the report never shows, and our test plan
covers the case its tests omit.

---

## 0a. Status - SHIPPED in 0.20.0, 2026-09-20

Everything below was written before the work. It is kept as written; this section
records what actually happened, including the four things the plan did not predict.

| # | Status | Commit |
|---|---|---|
| 256 | **done** - and the gate found 6 more than the 3 reported | `fix(package): every Unity-visible tracked file now carries a .meta` |
| 254 | **done** - plus a second instance in the csproj postprocessor | `fix(editor): build paths with the platform's separator` |
| 253 | **done + PROVEN on IL2CPP 2026-09-21** - all five generic trackers, not just the one reported | `fix(elements): constrain tracker state parameters to reference types` |
| 252 | **done** - five tests, all failing beforehand | `fix(fiber): an empty fragment or portal now unmounts its children` |
| 255 | **done** - one locator, probing; macOS still unverified | `fix(editor): probe for the bundled runtime` |
| 257 | **done** - `SignalFactory.Create<T>` | `feat(signals): SignalFactory.Create` |
| 259 | **done** - wired the dead `WhyDidYouRender` seam | `feat(diagnostics): report why each component rendered` |
| 258 | **deferred**, as recommended in 8 - consumer compile break, own release |  |

**Found during the work, not in the reports.** Each is in `Plans~/REMAINING_WORK.md`:

- **PKG-DEPS** - `package.json` declares only newtonsoft-json while `Ruitk.Ugui`
  needs `com.unity.ugui` and the Doom sample needs `com.unity.inputsystem`. A
  project without them gets assemblies that do not compile. Found by the new
  editor-compile gate on its first run.
- **SG-HMR-LOOP** - the two emitters give `@foreach` different tree shapes, so
  sibling positional identity differs between the Editor and a build. The parity
  contract does not cover loop emission at all.
- **VNODE-POOL** - the #252 fix is correct only while the VNode pool stays
  dormant. Reciprocal comments are in place at all three sites; the decision
  (wire it up, or delete it) is still open.
- **DIST-TILDE** - `publish.yml` ships most `~` folders to UPM consumers,
  including `SourceGenerator~/`, `ide-extensions~/` and `Plans~/`. `~` hides a
  folder from Unity, not from the packer.

**The gap in 10, partly closed.** Three gates shipped:
`check-meta-files.mjs`, `check-path-separators.mjs` and
`unity-editor-compile.mjs` (wired into `.github/workflows/unity-editor.yml`,
inert until `UNITY_EMAIL`/`UNITY_PASSWORD` are set, not yet a required context).
**The IL2CPP job now exists** (`scripts/unity-il2cpp-check.mjs` plus a `-executeMethod`
entry point in `CICD/Editor/`), and it did three jobs on its first use: it PROVED
#253 - the pre-fix build stops with `IL2CPP error for method
MultiColumnLayoutTracker`2::Attach`, the exact method reported, and the post-fix
build succeeds with the shared generic emitted in the generated C++ - it settled
IL2CPP-BIND as a non-defect on the same evidence, and it found SAMPLES-PLAYER: an
editor-only sample compiled into the all-platforms samples assembly, which breaks
a PLAYER build for every consumer on every platform. Still open from that list:
the git-URL install smoke test.

**Verification.** SG 1915/1915, LSP 185/185, shared core 93/93 (78 before),
`unity-compile-check` green on both configurations, and a real Unity batch-mode
import building 20 assemblies with 0 missing and 0 compile errors.

---

## 0. Summary

| # | Kind | Claim verified? | Fix risk | Consumer-visible? | Wave |
|---|---|---|---|---|---|
| 256 | Bug — no `.meta` on 3 files | **Yes** | Very low | **Yes — package is dead on UPM install** | 1 |
| 254 | Bug — backslash path on macOS/Linux | **Yes** | Very low | Yes (macOS/Linux only) | 1 |
| 253 | Bug — IL2CPP generic constraint | **Yes** | Very low | Yes (WebGL/IL2CPP builds fail) | 1 |
| 252 | Bug — empty fragment/portal keeps children | **Yes** | Very low — two lines; ten-layer sweep in §3 | Yes (silent wrong render) | 2 |
| 255 | Bug — HMR dotnet discovery Windows-only | **Yes** | Medium — unverifiable here | Yes (HMR dead on macOS) | 2 |
| 257 | Proposal — owner-scoped signal | **Yes (leak is real)** | Low, additive | Additive only | 3 |
| 259 | Proposal — render-reason trace | **Yes** | Low | New public API surface | 3 |
| 258 | Proposal — `Ruitk.Signals` asmdef | **Yes** | **High — breaking** | **Yes, breaks consumer asmdefs** | 4 / deferred |

**The pattern worth more than any single item:** every one of the five bugs lives in
territory no test or CI job covers — macOS, WebGL/IL2CPP, and installing the package
by git URL into `Library/PackageCache`. Fixing the five without closing that gap
leaves the next five to be found the same way. See §10.

**Found during the #252 sweep, not reported by anyone:** the source generator
splices `@foreach`/`@for`/`@while` items directly into the parent's child list,
while the HMR emitter wraps them in a `V.Fragment`. Output matches; tree shape and
sibling identity do not, and `HmrEmitterParityContractTests` does not cover loop
emission at all. Pre-existing, independent of #252, tracked in §3.4 layer 2.

---

## 1. #256 — three Builder files ship without `.meta`

### Verified

`git ls-files` confirms no `.meta` is tracked for:

- `Builder/Editor/BuilderPerf.cs`
- `Builder/Editor/Document/BuilderSignatureEdit.cs`
- `Builder/Editor/Document/BuilderSourceEditSession.cs`

A sweep of every tracked file under `Runtime/ Shared/ Editor/ Builder/ Samples/
Diagnostics/ Ugui/ Analyzers/ CICD/` finds **exactly these three** (plus two
`.gitkeep` dotfiles, which Unity ignores). So this is an omission, not a habit — all
three were added during the builder campaign.

### Impact

Unity refuses to import a file with no `.meta` inside an **immutable** package
(`Library/PackageCache`). `Ruitk.Builder.Editor` then fails to compile, and a failed
assembly means the editor loads **none** of the package's assemblies. Anyone
installing by git URL gets a dead package; the only workaround is embedding it under
`Packages/`. Asset Store customers are unaffected — the Uploader re-exports from a
writable `Assets/` folder where Unity has generated metas itself.

### Risk of fixing

Minimal. The GUIDs we commit become canonical. GUID churn only matters for assets
referenced *by GUID* — MonoBehaviours, ScriptableObjects, scenes. All three files are
plain classes (`grep -c 'MonoBehaviour|ScriptableObject|EditorWindow'` → 0), so
nothing can hold a reference to them.

One edge to respect: a developer with the repo embedded already has locally generated
metas with *different* GUIDs. Committing ours changes the GUID on their next pull.
Harmless for plain classes, but it is the reason to do this once and never churn them
again.

### Plan

1. Generate the three `.meta` files with fresh GUIDs, in the same `MonoImporter`
   shape as their siblings.
2. Add `scripts/check-meta-files.mjs` — every tracked file under the Unity-visible
   roots must have a tracked `.meta`, excluding dotfiles. Wire it into the
   hygiene-gates job of `.github/workflows/test.yml`, next to
   `check-machine-paths.mjs`, whose structure it should copy.
3. The gate is the real deliverable. The three metas are a one-time correction; the
   gate is what stops the fourth.

---

## 2. #254 — registry folder built with backslashes

### Verified

`Editor/UitkxAssetRegistrySync.cs:304`:

```csharp
string absFolder = Path.Combine(GetProjectRoot(), RegistryFolder.Replace('/', '\\'));
```

with `RegistryFolder = "Assets/Ruitk/Resources"` (line 26).

### Impact

On macOS/Linux `\` is an ordinary filename character, so `Directory.CreateDirectory`
creates **one** directory literally named `Assets\Ruitk\Resources` in the project
root, while `AssetDatabase.CreateAsset` still expects `Assets/Ruitk/Resources` to
exist. Result: an exception on every `.uitkx` save, plus a junk folder. The asset
registry never gets written, so `Asset<T>()` / `Ast<T>()` / `@uss` lookups silently
fail in player builds.

### Risk of fixing

Very low, and provable by inspection rather than observation — which matters, because
we cannot reproduce the failure on Windows. `Directory.*` and `AssetDatabase.*` both
accept forward slashes on **every** platform Unity runs on, Windows included. So
removing the `Replace` cannot regress the platform we can test and fixes the ones we
cannot. This is the strongest class of blind fix available.

### Plan

1. Drop the `Replace`; use `RegistryFolder` as-is.
2. Add a unit-level assertion that the constructed path contains no `\`. This is
   cheap and it is the only part of the fix that can be verified on Windows.
3. **Migration:** a macOS user who already hit this has a junk `Assets\Ruitk\Resources`
   directory. Follow the precedent already in this file — `WarnIfStaleRegistryExists`
   (lines 328–354) warns once per domain reload with an exact remedy. Add the same
   shape for the junk folder. Do **not** delete it automatically.
4. Audit the rest of the file for other separator assumptions while we are in it.

---

## 3. #252 — an empty fragment or portal keeps its children mounted

**Verdict: real bug, safe to fix, and the fix is two lines. This section records a
full ten-layer sweep — source generator, HMR emitter, core reconciler, host adapters,
Builder, tests, router, suspense, samples, docs — so the conclusion can be checked
rather than believed.**

### 3.1 What is wrong

Four sites decide whether to reconcile children:

| Site | Guard | Else branch | Correct? |
|---|---|---|---|
| `FiberReconciler.UpdateHostComponent` :1573 | `Children != null` | bailout :1582 | yes |
| `FiberReconciler.UpdateErrorBoundary` :1660 | reconciles empty explicitly | — | yes |
| `FiberFragment.UpdateFragment` :17 | `Children != null && Children.Count > 0` | bailout :29 | **no** |
| `FiberReconciler.UpdatePortal` :1598 | `Children != null && Children.Count > 0` | bailout :1608 | **no** |

Host components treat **null** as "no information, keep what is there". Fragments and
portals additionally treat **empty** that way. An empty children list is a legitimate
render result, so those two paths refuse to unmount children the model says are gone.
N to N-1 works; N to 0 does not.

### 3.2 The decisive structural fact: `Children` can never be null

This was the missing piece in every earlier pass, and it settles the question.

- `VirtualNode._children` is assigned in exactly five places (`VNode.cs:164, 184, 200,
  265`, and the `V.*` factories), and every one of them is `children ?? EmptyChildren()`
  or `EmptyChildrenInstance` outright. `grep -rn "_children = null" Shared/` returns
  nothing.
- `FiberNode.Children` is assigned from only two sources: `vnode.Children`
  (`FiberFactory.cs:29`, `FiberChildReconciliation.cs:360`) and `FiberFactory.cs:124`,
  `clone.Children = newVNode != null ? newVNode.Children : current.Children`.
  `grep -rn "Children = null" Shared/Core/Fiber/` returns nothing.

Therefore `V.Fragment()` with no children produces `Count == 0`, never `null`, and the
`else if` bailout branch is reachable **only** through the `Count == 0` condition.

Two consequences follow:

1. In `UpdateHostComponent` the bailout branch is already **dead code** — which is
   precisely why host elements empty correctly today.
2. In `UpdateFragment` and `UpdatePortal` the bailout branch's stated purpose
   ("Children cleared after commit") no longer exists anywhere in the codebase. The
   branch now serves nothing except the bug.

### 3.3 The hazard I was worried about, and why it cannot occur

`VirtualNode` is pooled, and `__Reset()` sets `_children = EmptyChildrenInstance`
(`VNode.cs:265`). If a live fiber could hold a recycled node, reconciling against its
empty list would delete **live** UI. Five independent lines of evidence rule it out.

**(1) The guard was never a bug fix.** `git log -S "Children.Count > 0"` on
`FiberFragment.cs` returns exactly one commit: `69ae4c07 "changed to fibers"`. The
`Count > 0` came in with the original fiber conversion. Nothing was ever repaired by
it, so nothing regresses by removing it.

**(2) The bailout's stated reason was obsolete in the commit that wrote it.**
`git log -S "Children cleared after commit"` gives `def3c6e7 "fixed issues caused by
the optimization"`. That single commit does both of these:

```diff
- // All fields extracted — schedule VNode for pool return.
- VirtualNode.__ScheduleReturn(vnode);
+ // All fields extracted — VNode data lives on the fiber now.
```
```diff
- else if (wipFiber.Children == null && wipFiber.Alternate?.Child != null)
+ else if (wipFiber.Alternate?.Child != null)
-     // Bailout: Children is null (cleared after previous commit to prevent
-     // stale VNode pool references). Clone existing child fibers instead.
+     // Bailout: Children cleared after commit. Clone existing child fibers.
```

It **removed the vnode pool returns** and **widened the bailout** in the same diff.
The original condition was `Children == null` — the host-component semantic. The fix
below restores it.

**(3) Pool returns never came back.** `git log -S "__ScheduleReturn(vnode)" --all`
shows the optimization landing, being undone (`bc227065`), and `def3c6e7`. An
exhaustive grep at HEAD, including reflection-by-name forms, finds the only
`VirtualNode` pool references to be its own unused definitions at `VNode.cs:282,293`.
The pool rents but never returns, so `s_pool` is always empty, `__Rent()` always
allocates, and `__Reset()` never executes. An earlier audit recorded the same thing
independently: `Plans~/archive/FAMILY_PARITY_PLAN.md:865`. Props and `Style` pooling
**are** live via `__ScheduleReturnToFamilyPool`, which is why the machinery reads as
active.

**(4) Two sites in the same file already do the right thing.** Besides
`UpdateHostComponent`, the error-boundary path at `FiberReconciler.cs:1660-1670`:

```csharp
fiber.Children = targetChildren;              // may be Array.Empty
if (targetChildren.Count > 0)
    ReconcileChildren(fiber, targetChildren);
else
    // Ensure any previous children are deleted.
    ReconcileChildren(fiber, Array.Empty<VirtualNode>());
```

Decisive twice over: the codebase already treats empty as "delete", **and** it already
depends on `ReconcileChildren` with an empty list performing that delete. The
fragment/portal fix is not new behaviour; it is the third caller of an established one.

**(5) The deletion machinery is tag-agnostic.** `CommitDeletion`
(`FiberReconciler.cs:1529`) walks the subtree depth-first and, for a host-less fiber,
climbs to the nearest host parent before removing. It does not branch on `Fragment` or
`HostPortal`, and it is the same path N to N-1 already exercises every frame.

### 3.4 Layer sweep — who can actually produce an empty fragment

**Layer 1, source generator.** `CSharpEmitter.EmitFragment` (:2005) emits
`V.Fragment(key: X)` when statically empty, `V.Fragment(key: X, a, b)` for simple
children, and `V.Fragment(key: X, __C(...))` otherwise. `__C(params object[])` (:1081)
flattens arrays and filters nulls, returning `Array.Empty` when everything is filtered.
So a fragment whose children are all conditional, with every condition false, yields a
**stable-identity fragment with zero children** — the bug, reachable from ordinary
markup, no HMR needed.

**Layer 2, HMR emitter — and a genuine SG divergence.** `HmrCSharpEmitter.EmitFragment`
(:1470) matches the SG shapes. The loop emitters do not:

| | SG | HMR |
|---|---|---|
| `@foreach` | `((Func<VirtualNode[]>)(() => { ... return __r.ToArray(); }))()` (:2141) | `((Func<VirtualNode>)(() => { ... return V.Fragment(key: null, __items.ToArray()); }))()` (:2058) |
| `@for` | same array shape (:2095) | same fragment shape (:2080) |
| `@while` | same array shape (:2118) | same fragment shape |

SG splices loop items **directly into the parent's child list** through `__C`; HMR
wraps them in a Fragment. Visible output is identical, since fragments are host-less,
but:

- an empty loop produces **no node at all** under SG and an **empty Fragment** under
  HMR, so this bug fires in the Editor under HMR and not in a player build — exactly
  the "HMR does not match the build" class of report;
- sibling positional identity differs between the two worlds (`[a, i0, i1, b]` versus
  `[a, Fragment[i0,i1], b]`), so a keyless loop beside keyless siblings reconciles on
  different indices.

`HmrEmitterParityContractTests` does **not** cover loop emission shape; grep finds no
`foreach` or `Fragment` assertions in it. This divergence is pre-existing, is not
caused by the #252 fix, and does not block it, but it should be tracked.

**Layer 3, core reconciler.** Covered in 3.1 to 3.3. Nothing else branches on fragment
or portal tag during child reconciliation.

**Layer 4, host adapters (UI Toolkit and uGUI).** The only backend-specific fragment or
portal code is `U.Portal` constructing a Portal `VirtualNode`. Neither adapter
implements fragment semantics; the core is host-agnostic here, so the fix lands once
and applies to both.

**Layer 5, Builder.** Every `Fragment` hit under `Builder/` is an IntelliSense *text*
fragment helper, unrelated to `FiberTag.Fragment`. The Builder preview renders through
the same core, so it inherits the fix and needs no change of its own.

**Layer 6, tests.** `SharedTests~/FiberReconcilerTests.cs:72`
(`FragmentProducesNoHostElementAndFlattensChildren`) pins only mount-time flattening.
Nothing anywhere asserts fragment or portal children counts across an update, so the
fix breaks no existing expectation — and that absence is why this survived.

**Layer 7, router — checked because it is the heaviest fragment consumer.**
`RouterRenderUtils.Fragment(children)` (`RouterComponents.cs:763`) returns
`V.Fragment()` for zero children, **`children[0]` directly for one**, and a real
fragment for two or more. Its comment says the single-child shortcut exists to avoid
"exercising Fragment handling in Fiber" — the router has been routing around this area
on purpose. Three call sites:

| Line | Component | Empty when | Affected? |
|---|---|---|---|
| 129 | `RouterFunc` | `<Router>` has no children | no, degenerate |
| 262 | `RouteFunc` | `<Route>` with no `element`/`render` and no children | only on a 2-or-more to 0 transition |
| 395 | `OutletFunc` | no nested match **and** no own children | **no**, see below |

`OutletFunc` looks like the dangerous one but is safe: when the matched slot
disappears, the returned node changes *type* (host or component VNode becomes a
Fragment), so the new fragment fiber has no `Alternate`, the bailout branch is never
entered, and the old subtree is deleted by ordinary type-change reconciliation. No
router behaviour depends on empty-fragment-keeps-children, and the fix turns the
single-child shortcut from a workaround into a plain optimization.

**Layer 8, suspense.** The only fragment reference is a comment at
`FiberReconciler.cs:1047` enumerating host-less tags. No suspense path constructs or
empties a fragment.

**Layer 9, samples.** No fragment literals in any sample `.uitkx`. Samples reach the
path only through loops, i.e. HMR-only per layer 2.

**Layer 10, docs.** Nothing in the docs site or `CHANGELOG.md` documents the current
behaviour, so no published contract is broken. A changelog entry is still owed.

### 3.5 Reachability summary

The bug fires whenever a fragment or portal keeps **stable identity** across renders
and goes from N children to 0:

- a fragment whose children are all conditional, with every condition false — **SG and
  HMR** (layer 1);
- `@foreach` / `@for` / `@while` over an emptied collection — **HMR only** (layer 2);
- a portal whose children empty — both, and the stale nodes sit in a detached target,
  which is the worst-looking version of the symptom;
- a `<Route>` with two or more children collapsing to zero — both, narrow.

### 3.6 What could still hurt

1. **The dormant pool is a standing landmine.** If vnode pooling is ever re-enabled,
   the 3.3 hazard becomes real and this fix becomes a bug. Either wire the returns up
   deliberately with the guards revisited, or delete the dead `__ScheduleReturn` and
   `__FlushReturns` and the pool with them. Until that is decided, both fix sites and
   `VNode.__ScheduleReturn` carry a reciprocal comment.
2. **Null and empty must stay distinguishable in intent.** The fix adopts the
   host-component semantic (`!= null`) rather than deleting the guard, so if a future
   change ever does clear `Children`, the bailout still means "no information".

### 3.7 Plan

1. Align `UpdateFragment` (`FiberFragment.cs:17`) and `UpdatePortal`
   (`FiberReconciler.cs:1598`) with `UpdateHostComponent` — guard on `Children != null`
   only. This restores `def3c6e7`'s pre-existing condition. Two lines.
2. Add the reciprocal comments described in 3.6(1).
3. Tests in `SharedTests~` (synchronous renderer, no scheduler):
   - fragment N to N-1 to 0, asserting host `childCount` reaches 0;
   - portal N to 0 against the portal target;
   - a fragment nested under a host parent, asserting the host parent's remaining
     children are untouched (guards `CommitDeletion`'s climb to the nearest host
     parent);
   - the null-`Children` bailout can only be built by hand-constructing a `FiberNode`,
     since no VNode can produce it (3.2). Pin it that way or not at all — do **not**
     claim coverage of a path real markup cannot reach.
4. File two entries in `Plans~/REMAINING_WORK.md`: the dormant-pool decision, and the
   SG/HMR loop-emission divergence from layer 2 together with the parity-test gap.

---

## 4. #253 — IL2CPP cannot build `MultiColumnLayoutTracker.Attach`

### Verified

`Shared/Elements/Trackers/MultiColumnLayoutTracker.cs:12` declares
`where TState : IColumnLayoutState` with no `class` constraint. IL2CPP's generic
sharing cannot emit an assignment to a `TState` that might be a value type, so the
WebGL build fails outright — not a warning, the build stops.

Both concrete instantiations pass a reference type:

- `MultiColumnListViewElementAdapter.cs:42` → `Cached`, declared `public sealed class` (:15)
- `MultiColumnTreeViewElementAdapter.cs:42` → `Cached`, declared `public sealed class` (:15)

### Risk of fixing

Very low, and there is **no consumer blast radius at all**: the tracker is
`internal sealed`, so no user code can instantiate it. Adding `class` narrows a
constraint that both call sites already satisfy. Mono and editor builds are
unaffected either way, which is precisely why this survived.

### Plan

1. Add the `class` constraint.
2. Audit the other trackers in `Shared/Elements/Trackers/` for the same shape — a
   generic state parameter without `class` that is assigned through. One instance of
   a pattern is rarely the only one.
3. Verification is possible **on Windows**: WebGL is an installable Hub module and
   always uses IL2CPP. Build the sample project for WebGL before and after.
4. Consider a CI WebGL build. Expensive, but it is the only thing that catches this
   class of defect, and today nothing does.

---

## 5. #255 — HMR compiler discovery assumes the Windows layout

### Verified

`Editor/HMR/UitkxHmrCompiler.FindCompilerPaths`:

```csharp
string editorDir = Path.GetDirectoryName(EditorApplication.applicationPath);
string dataDir   = Path.Combine(editorDir, "Data");
_dotnetPath = Path.Combine(dataDir, "NetCoreRuntime", "dotnet.exe");
if (!File.Exists(_dotnetPath))
    _dotnetPath = Path.Combine(dataDir, "NetCoreRuntime", "dotnet");   // extension fallback only
if (!File.Exists(_dotnetPath))
    throw new FileNotFoundException(...);
```

The `dotnet.exe` → `dotnet` fallback handles the *filename* but not the *layout*:
`dataDir` is always `<editor dir>/Data`, which is the Windows install shape. HMR
therefore cannot start on macOS.

### Risk of fixing — the honest part

We cannot verify the macOS layout from here. The issue asserts
`Unity.app/Contents/Resources/Scripting/NetCoreRuntime`; that may be right, may be
version-specific, and taking it on faith just relocates the failure.

**So the fix must not be "hardcode the macOS path".** It should be a probe, which is
already this repository's documented convention for exactly this problem — see
`CLAUDE.md` § Machine-local paths: *"Tools are probed, then overridden … → an error
naming all three"*, with
`ide-extensions~/lsp-server/Roslyn/ReferenceAssemblyLocator.cs` given as the model
(it probes Unity Hub locations per-OS).

### Plan

1. Rewrite `FindCompilerPaths` as an ordered probe over candidate roots:
   `<editorDir>/Data`, `applicationContentsPath`,
   `applicationContentsPath/Resources/Scripting`, and any `$RUITK_DOTNET` /
   `.ruitk-local.json` override, per the established chain.
2. On exhaustion, throw listing **every** path probed. Today's message names one
   path, which is why the report had to read our source to explain it. An actionable
   error is worth as much as the fix.
3. Same treatment for `FindBundledCsc(dataDir)`, which inherits the assumption.
4. **Verification without owning a Mac:** GitHub Actions provides `macos-latest`
   runners, and Unity already runs in CI with a licence (`UNITY_EMAIL` /
   `UNITY_PASSWORD`, `publish.yml:496–523`). Today every job is `ubuntu-latest`
   except one `windows-latest`. A small macOS job that boots Unity and prints the
   directory tree under `applicationContentsPath` settles the layout factually, and
   afterwards guards against the next Windows-only path assumption.
5. Until that job exists, treat the macOS path as **unverified** in the changelog.
   Do not claim macOS HMR works because the code changed.

---

## 6. #257 — a signal owned by its creator

### Verified

- `Signal<T>`'s constructor is `internal` (`Signal.cs:28`), so user code cannot make
  one without modifying the package.
- The only public route is `SignalFactory.Get<T>(key)` (`SignalsRuntime.cs:55`),
  which goes through `SignalRegistry.GetOrCreate<T>` (:12) and parks the signal in a
  process-wide registry.
- `SignalRegistry` has **no `Remove`** — a grep for `Remove|Clear` in
  `SignalsRuntime.cs` returns nothing. The leak in the report is real: a short-lived
  owner must invent a unique key and leaks a registry entry plus its last snapshot,
  per instance, for the process lifetime.

### Risk of fixing

Low and additive; nothing existing changes behaviour. Two things to settle during
implementation rather than now:

1. **Which shape.** Three options — `SignalFactory.Create<T>`, a public constructor,
   or `SignalRegistry.Remove(key)`. They are not equivalent: `Create` adds an
   un-keyed lifetime concept, a public constructor exposes the comparer overload, and
   `Remove` keeps the registry model but makes teardown the caller's problem.
   `Create` is the smallest surface for the stated need; the choice is ours, not the
   reporter's.
2. **Empty keys must be genuinely safe.** `Hooks.cs:509` already branches on
   `!string.IsNullOrEmpty(metadata.Key)`, so an empty key is an anticipated state
   rather than a new one — but confirm nothing keys identity, dedup or diagnostics off
   `Key` before minting signals with `""`. If anything does, prefer a sentinel or a
   generated unique key over reusing empty.

### Plan

1. Decide the shape (default recommendation: `Create<T>`).
2. Implement, with a test that an owner-scoped signal is **not** discoverable via
   `TryGet` and is collected when its owner is dropped.
3. Document the distinction in the docs site's signals page: keyed/global vs
   owner-scoped.

---

## 7. #259 — a dev-only "why did you render" trace

### Verified

The bailout decision in `FiberFunctionComponent.RenderFunctionComponent` already
computes every input the trace would report:

- `propsEqual` (:61), `contextUnchanged` (:62), `childrenChanged` (:70), combined at
  the bailout at :77–79, with the state-update flag alongside.

So the information exists and is discarded. `FiberReconciler.MetricsEmitted` gives
per-commit totals with no component names, which is the gap described.

### Risk of fixing

Low functionally — a null delegate check in a hot path is free. Two design points:

1. **API surface.** A public static mutable `Action<string, RenderReason>` is easy to
   ship and awkward to change later. Prefer routing it through the existing
   `FiberConfig` / metrics seam so there is one diagnostics entry point rather than
   two, and gate it `#if UNITY_EDITOR` (or a define) so it compiles out of player
   builds entirely.
2. **Name resolution cost.** The proposal resolves a component name per render via
   `ElementType ?? TypedRender?.Method.DeclaringType?.Name`. Reflection on
   `DeclaringType` must not run when no subscriber is attached, and ideally is cached
   per fiber rather than recomputed.

This is a hot path — `CLAUDE.md` lists the reconciler among them — so the
"zero cost when unsubscribed" claim needs to be measured, not assumed.

### Plan

1. Add the `RenderReason` flags enum and emit it from the existing bailout inputs.
2. Route through the metrics seam, editor-gated.
3. Cache the component name per fiber; resolve lazily.
4. Benchmark a render-heavy sample with and without a subscriber to confirm no
   regression.

---

## 8. #258 — move `Ruitk.Signals` into its own assembly definition

**This is the only item that can hurt users, and it should not ride along with the
others.**

### Verified — and smaller internally than the report suggests

- `Shared/Core/Signals/` is just `Signal.cs` + `SignalsRuntime.cs`.
- Their only dependencies are `System`, `System.Collections.Generic`, `UnityEngine` —
  no virtual DOM, no hooks, no reconciler. So the split is *technically* clean.
- Internals are read from **one place only**: `Hooks.cs` (`UntypedValue` :1603/:1700,
  `SubscribeRaw` :1693). Everything else referencing `SignalBase` /
  `SignalSubscription` is also inside `Ruitk.Shared`. So **one**
  `InternalsVisibleTo("Ruitk.Shared")` is required, not the six the report lists.
- `SignalsRuntimeHost` is a `MonoBehaviour`, but it is created dynamically with
  `AddComponent` under `HideFlags.HideAndDontSave` + `DontDestroyOnLoad`
  (`SignalsRuntime.cs:101–112`). It is never scene-serialised, so **no script GUID or
  serialisation risk** — the usual reason assembly moves are dangerous does not apply.
- The `.cs` files do not move, only a new `.asmdef` is added beside them, so their
  GUIDs are untouched.

### Why it is still the risky one

**It is a breaking change for consumers.** Today a user asmdef that references
`Ruitk.Shared` can use `Signal<T>`. After the split, the type lives in
`Ruitk.Signals`, and every such asmdef must add a second reference or fail to
compile. Nothing in Unity's asmdef model gives us type-forwarding to soften it.

That is a real cost paid by every existing user, in exchange for a boundary that
benefits a particular architecture (presenters that must not see the virtual DOM).
The benefit is legitimate; the question is whether it is worth a compile break, and
that is the owner's call, not a technical one.

Second-order effects to weigh:

- One more assembly in the package, so one more entry in the store package's assembly
  list and one more thing for `AssetStoreExport` to carry.
- `Ruitk.Shared`, and anything else that touches signals, must now reference
  `Ruitk.Signals`.
- Assembly splits are far easier to add than to undo.

### Plan — if accepted

1. Land it **alone**, on a minor or major boundary, never bundled with bug fixes.
2. `Shared/Core/Signals/Ruitk.Signals.asmdef` (+ `.meta`), no dependencies beyond
   UnityEngine; `AssemblyInfo.cs` with a single `InternalsVisibleTo("Ruitk.Shared")`
   — add others only where a build actually demands one.
3. `Ruitk.Shared` references `Ruitk.Signals`.
4. `CHANGELOG.md` **Breaking** entry plus a migration note: *"if your assembly
   definition references `Ruitk.Shared` and uses `Signal<T>`, add a reference to
   `Ruitk.Signals`."*
5. Re-run the Asset Store export and confirm the new assembly is carried.

**Recommendation:** defer. Take #257 first — it solves a concrete leak with no
breakage — and treat #258 as a separate architectural decision with its own release.

---

## 9. Sequencing

**Wave 1 — ship first, lowest risk, highest user impact.** #256, #254, #253.
All three are small, independently verifiable, and two of them mean "the package does
not work at all" on some install path or platform. #256 is the most urgent: UPM
installs are broken today.

**Wave 2 — needs care.** #252 (reconciler semantics + the dormant-pool note) and
#255 (probe rewrite, macOS verification outstanding).

**Wave 3 — additive.** #257, #259.

**Wave 4 / deferred.** #258, as its own release with a breaking-change note.

---

## 10. The gap underneath all of it

Nothing in CI builds for WebGL, runs on macOS, or installs the package from a git URL
into `Library/PackageCache`. All five bugs live in exactly those three blind spots,
and one reporter found them in two days. Fixing the five without closing the gap
simply waits for the next five.

Proposed, in order of value per unit of effort:

1. **`.meta` gate** (§1) — trivial, deterministic, catches a class of defect that
   silently kills the whole package.
2. **macOS CI job** (§5) — settles the layout question and guards every future path
   assumption. Unity-in-CI credentials already exist.
3. **git-URL install smoke test** — install the package from the `dist` branch into a
   throwaway project and assert the assemblies load. This is what #256 actually
   needed, and it runs fine on `ubuntu-latest`.
4. **WebGL/IL2CPP build job** (§4) — the most expensive, and the only thing that
   catches the generic-sharing class of failure.

Related existing entries: `RT-BLD` (the smoke compile covers only `Shared/` +
`Runtime/`) and `RT-HMR` (nothing under `Editor/HMR/` is reachable from either test
suite) in `Plans~/REMAINING_WORK.md`. Both are the same disease.

---

## 11. Open questions for the owner

1. **#258** — accept the consumer compile break, or keep signals inside
   `Ruitk.Shared` and rely on a lint test for the boundary?
2. **#257** — `Create<T>`, a public constructor, or `Registry.Remove`?
3. **The dormant vnode pool** (§3) — wire it up deliberately, or delete it? Leaving
   it dormant means #252's fix carries a standing assumption.
4. **CI spend** — which of the four jobs in §10 are worth the minutes?
