# Issues #266 and #267 - investigation and fix

Status: **IMPLEMENTED** in 0.21.1 on `feat/ruitk-builder`.

Both fixes are in `Shared/Core/Fiber/FiberReconciler.cs`. The repro tests were promoted
into the permanent suite as `SharedTests~/PlacementWalkTests.cs` (11) and
`SharedTests~/DeferredUpdateTests.cs` (3); the scratch `IssueReproTests.cs` is gone.
Package suite 107/107. Unity editor compile gate: 21 assemblies, 0 missing. The same
behaviour is asserted against the real UI Toolkit backend in the owner's project under
`ruitk-test` (`Assets/RuitkTests/Editor/RuitkTestChecks.cs`, areas "Keyed reorder" and
"Deferred updates"), with a headless runner at `RuitkTests.RuitkTestBatch.Run`.

Baseline for comparison: against master those same tests were 96/106.

---

## 1. #266 - a keyed reorder of components/fragments never moves their host elements

### Real? Yes - reproduced independently of the reporter's rig

On the mock host (`MockHostConfig`, no Unity), four keyed function components
`a,b,c,d` re-rendered as `d,c,b,a`:

    expected  root(list(d,c,b,a))
    actual    root(list(a,b,c,d))

The order is **byte-identical to before the update** - nothing moved at all.
The same list built from bare host elements (`Issue266_KeyedHosts_ReorderToo_ControlCase`)
reorders correctly, which is exactly what the reporter described.

### Root cause (one line)

`CommitPlacement` returns early for any fiber with no host element of its own, so a
`Placement` effect landing on a FunctionComponent / Fragment / ErrorBoundary is
silently dropped instead of moving that wrapper's host descendants.

`Shared/Core/Fiber/FiberReconciler.cs`:

```csharp
private void CommitPlacement(FiberNode fiber)
{
    if (fiber.Tag == FiberTag.HostPortal) { return; }
    if (fiber.HostElement == null) { return; }   // <- wrappers end here
```

Everything upstream is correct and was verified:

- `FiberChildReconciliation.ReconcileChildrenWithKeys` implements React's
  `lastPlacedIndex` rule faithfully and sets `EffectFlags.Placement` on the moved
  fiber regardless of its tag (line 203).
- `CompleteWork` appends any fiber with a non-empty `EffectTag` to the effect list,
  so the wrapper *is* visited in the commit phase.
- `GetHostSibling` is already a faithful port of React's `getHostSibling` and already
  copes with being called on a wrapper.

The defect is exclusively the missing descent. React's `commitPlacement` computes the
anchor once and then calls `insertOrAppendPlacementNode`, which recurses through
host-less fibers and inserts **every** top-level host descendant before that anchor.
RUITK has the anchor half and not the recursion half.

### The fix

Split `CommitPlacement` into the React shape - resolve host parent, compute the anchor
**once**, then walk:

```csharp
private void CommitPlacement(FiberNode fiber)
{
    if (fiber.Tag == FiberTag.HostPortal) { return; }

    // Fresh-mount guard - see "cost" below.
    if (fiber.HostElement == null && fiber.Alternate == null) { return; }

    var parentFiber = fiber.Parent;
    while (parentFiber != null && parentFiber.HostElement == null) { parentFiber = parentFiber.Parent; }
    if (parentFiber?.HostElement == null) { /* existing warning */ return; }

    var before = GetHostSibling(fiber);
    PlaceHostSubtree(fiber, parentFiber, before);
}

private void PlaceHostSubtree(FiberNode node, FiberNode parentFiber, object before)
{
    // BEFORE the HostElement test - a HostPortal's HostElement IS the portal target.
    if (node.Tag == FiberTag.HostPortal) { return; }

    if (node.HostElement != null)
    {
        InsertPlacedHost(node, parentFiber, before);   // the old body, anchor passed in
        return;                                        // a host carries its own subtree
    }

    // Allow-list, not deny-list: a tag added later is never silently descended into.
    if (node.Tag != FiberTag.FunctionComponent
        && node.Tag != FiberTag.Fragment
        && node.Tag != FiberTag.ErrorBoundary) { return; }

    for (var child = node.Child; child != null; child = child.Sibling)
    {
        PlaceHostSubtree(child, parentFiber, before);
    }
}
```

`InsertPlacedHost` is the existing move / initial-props / insert body verbatim, with
`before` passed in rather than recomputed. For a `HostComponent` fiber the new code is
behaviourally identical to the old one: same anchor, one insert, no descent.

### Why it is not a patch

It fixes the layer the bug lives at (the commit-phase placement walk) and restores the
React algorithm RUITK was already half-implementing. No call site, no backend and no
caller changes. It replaces a special case ("only hosts can be placed") rather than
adding one.

### Two things that are load-bearing and easy to get wrong

1. **The `HostPortal` check must precede the `HostElement` check.** A portal fiber's
   `HostElement` *is* the portal target. I wrote it the other way round in one prototype
   iteration and the portal edge test immediately caught it: the target element itself
   was spliced into the list (`root(list(b,a,target(pa,pb)))`). Keep that test.

2. **The fresh-mount guard is a measured requirement, not a micro-optimisation.**
   Without it, mounting 50 component-wrapped hosts costs **101** host insert operations
   instead of 51 - every wrapper re-inserts hosts its children already placed. In UI
   Toolkit each of those is a `Remove` + `Add`; in uGUI it is a `SetParent` round trip.
   The guard is sound because reuse never crosses parents (`MapRemainingChildren` only
   maps one parent's children), so a wrapper with `Alternate == null` provably has a
   wholly new host subtree, each member of which carries its own `Placement` effect that
   the post-order effect list commits first. React avoids the same work earlier, by not
   flagging children at all on a fresh mount (`shouldTrackSideEffects`).

### Cost, measured

| | before | after |
|---|---|---|
| mount 50 component-wrapped hosts | 51 inserts | **51** (unchanged) |
| reverse a 50-item keyed list | 0 (the bug) | **49** (the minimum) |

All four host quadrants of `CommitPlacement` keep their old behaviour:
host+new -> insert; host+reused -> move; hostless+new -> return;
hostless `HostComponent`+reused -> return.

---

## 2. #267 - the second of two coalesced deferred updates is lost

### Real? Yes - reproduced, and the mechanism proven, not inferred

`Issue267_BothDeferredUpdatesRender`: two signals set from inside a render pass, both
deferred, both replayed after commit. The first renders, the second does not
(`expected second1, actual second0`). Extending it to six leaves
(`Issue267_ManyCoalescedDeferredUpdatesAllRender`) shows the first renders and
**all five others are lost** - so this is "everything after the first", not "the second".

### Root cause (one line)

`ScheduleUpdateOnFiber` marks the update only on the fiber face it was handed, and once
the first replay has recreated the WIP from the superseded root object, the
`rootCurrent == _root.WorkInProgress` branch shadows the
`rootCurrent == _root.Current.Alternate` redirect that would have re-marked the live
face - so the next pass clones from a fiber that was never marked.

**Proof, not inference:** swapping those two `else if` branches - changing nothing else -
makes both #267 tests pass. That isolates the shadowing exactly.

Sequence: replay #1 calls `CreateWorkInProgress(_root.Current, ...)`, which reuses
`_root.Current.Alternate` as the WIP root. From that moment
`_root.WorkInProgress` and `_root.Current.Alternate` are the *same object*, and the
`WorkInProgress` branch is tested first. Replay #2 therefore takes the
"cascading update on the WIP" path, no redirect runs, and `CloneForReuse` overwrites
the flag from the live fiber that was never marked.

### Why the branch swap is NOT the fix

During any *normal* render pass `_root.WorkInProgress` is **always**
`_root.Current.Alternate` - that is what `CreateWorkInProgress` does. Swapping the
branches would therefore redirect every genuine mid-render cascading update to its
stale face: a semantic change on a hot path. Worth recording that **the existing suite
does not catch that** - a real coverage gap (see follow-ups).

### The fix - mark both faces of the double buffer

```csharp
if (fiber != null)
{
    fiber.HasPendingStateUpdate = true;
    MarkPendingUpdateOnAlternate(fiber.Alternate);
}

private static void MarkPendingUpdateOnAlternate(FiberNode alternate)
{
    if (alternate == null) { return; }

    alternate.HasPendingStateUpdate = true;
    for (var parent = alternate.Parent; parent != null; parent = parent.Parent)
    {
        parent.SubtreeHasUpdates = true;
    }
}
```

This is the reporter's proposal, and it is also what React does - see
`markUpdateLaneFromFiberToRoot`, which merges the lane into `sourceFiber` *and*
`sourceFiber.alternate`, and into every ancestor *and* its alternate.

It fixes the cause rather than the routing. The invariant the codebase actually relies
on is **"mark the face the next pass clones from"**, and the caller cannot know which
face that will be. The *other* marking site in the codebase,
`Hooks.PropagateContextChange`, already satisfies that invariant - it deliberately walks
`fiber.Alternate` (the committed tree) so `CloneForReuse` carries the flags into the WIP.
`ScheduleUpdateOnFiber` was the odd one out.

The branch chain is left exactly as it is. With both faces marked, the redirect branch
is still needed and still fires for replay #1 (where `_root.WorkInProgress` is null),
and the shadowing for replay #2 becomes harmless.

### Why over-marking cannot hurt - verified, not assumed

Marking the *stale* face in the common case is inert because every path that puts a
fiber object into a tree refreshes both flags from the current face first:

- `FiberFactory.CloneForReuse` lines 133-134 - unconditional
  `clone.HasPendingStateUpdate = current.HasPendingStateUpdate;` / `SubtreeHasUpdates`.
- `FiberChildReconciliation.PlaceExistingChild` line 431 -
  `fiber.SubtreeHasUpdates = fiber.Alternate.SubtreeHasUpdates;`.
- `FiberFactory.CreateFiber` lines 33-34 - explicit `false`.

Nothing reads a stale-face flag before one of those overwrites it. And even if one did
survive, the only consequence is a bail-out that does not happen: the component
re-renders, hooks compare deps, `CompleteWork` finds the host props equal and emits no
`Update`. Wasted work, never wrong output.

Cost: one extra ancestor walk, `O(tree depth)`, per update.

---

## Blast radius - checked, all layers

| Layer | Verdict |
|---|---|
| `CommitPlacement` / `GetHostSibling` callers | none outside `FiberReconciler.cs` |
| Source generator, language-lib, LSP | no Fiber file is compiled into them - structurally unaffected |
| HMR (`Editor/HMR/**`) | only reference is a prose comment; no reflection entry point touched, no new shared-parser entry point |
| Emitter parity (SG / HMR / virtual doc / parser) | untouched - this is runtime reconciler code, not an emission layer |
| Source maps / generated-line offsets | untouched |
| Backends | `UitkHostConfig`, `UguiHostConfig` and `MockHostConfig` all already implement DOM `insertBefore` semantics including same-parent move and null-anchor append. **No backend change needed.** |
| Element adapters | none does its own child ordering - all ordering flows through the host config |
| Portals | covered by a dedicated test; the check order is the trap (above) |
| Router / Suspense / error boundaries | these are wrapper fibers - beneficiaries of #266, not at risk |
| Backwards compatibility | no API change, no diagnostic severity change; existing `.uitkx` files compile and render identically, except that keyed component reorders now actually reorder |

Not run, deliberately: the SG and LSP suites. They compile no Fiber file, so they carry
zero information here, and `dotnet test` on `SourceGenerator~` publishes Debug DLLs over
`Analyzers/`, which breaks all `.uitkx` generation until restored.

Still owed before merge: the Unity Editor smoke-compile gate
(`node scripts/unity-editor-compile.mjs`) and the Unity-side `Ugui/Tests` EditMode run,
which cannot run from the dotnet harness.
`ReconcilerKnobTests.TimeSlicingBypass_StateUpdateDuringCommit_ReplaysSynchronously`
sits in the #267 area; it is a single-deferred-update case, already passing, and
unaffected by both-faces marking.

---

## Test plan

Promote `SharedTests~/IssueReproTests.cs` into the permanent suite, renamed to the
behaviour it pins rather than the issue number, keeping all ten:

- keyed function components reorder their hosts; keyed fragments too; a component with
  two hosts moves both; nested wrappers move the deep host; the bare-host control case;
  mixed add/remove/move
- portal content is not dragged along by a moved component
- a moved host carries its own subtree
- a component rendering nothing does not break the reorder
- mount op count is **51** (the no-regression pin) and a 50-item reversal is **49**
- both deferred updates render; six coalesced deferred updates all render

## Follow-ups (not part of this fix)

- **Coverage gap:** no test fails when the `WorkInProgress` / `Current.Alternate`
  branches are swapped. A cascading-update-on-WIP test should exist.
- **Optimisation, separate change:** adopt React's `shouldTrackSideEffects` so children
  are not flagged `Placement` at all on a fresh mount. That removes the
  redundant-insert class at its source and makes the fresh-mount guard unnecessary.
  Bigger blast radius (`FiberChildReconciliation` + `CompleteWork`) - do not fold it in.

## Verified during implementation

The prototype 'tightening' that moved the HostPortal check after the HostElement
check shipped a regression the portal test caught immediately - the target element was
spliced into the list. That ordering is now commented in the source as load-bearing.

The claim that swapping the WorkInProgress / Current.Alternate branches is hazardous
could NOT be demonstrated once both faces are marked: with the fix in place the two
orderings are behaviourally equivalent in everything the suite can construct. That is a
property of the fix, not a gap - the routing no longer has to be right for the update to
land. The branch order was left exactly as it was.
