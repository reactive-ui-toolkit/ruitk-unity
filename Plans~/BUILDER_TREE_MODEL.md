# RUITK Builder — the tree model

Status: planned, not started. Supersedes the unfinished half of
`Plans~/BUILDER_MODEL_REFACTOR.md`.
Branch: `feat/ruitk-builder`.

Owner's statement of the target, 2026-08-23, which this plan implements verbatim:

> when we load an existing file we should scan the whole tree, and build ourself
> a data structure with all that data, all manipulation is done on that data
> structure, rendering is done on that data structure, every leaf of that
> structure should have is-dirty, then on save we walk through that data
> structure and write the files. On a new builder not started from an existing
> file we start from a clean structure and everything we build is dirty to begin
> with. The whole process should be deterministic, idempotent.

Two additions agreed in the same conversation: **`DiskPath` per node** (where the
module currently sits on disk, or null if it has never been written) and **a
snapshot of the last projection** (the set of paths that were on disk as of the
last load or Save). Those two replace all six of the pending mechanisms below.

## Why — the measured problem

The builder has no tree. The whole persisted state is:

```
List<BuilderDocumentSession> _serializedSessions   flat, keyed by path
List<string>                 _pendingDeletes       side-list
List<BuilderFolderMove>      _pendingFolderMoves   side-list
```

Three consequences, each verified in the code rather than assumed:

1. **The tree is recomputed, never stored.** `BuilderGraph` is a local in
   `BuilderCanvasHost.Mount`, assigned to `_graph`, nulled on unmount. Every
   mount rebuilds it: a language-server round trip for the file inventory, a
   reachability walk, and a re-parse.
2. **Not every file is in memory.** Only visited files become sessions;
   everything else is read from disk on demand in `ReadBufferOrDisk`. The model
   is partial by construction, so what the canvas shows depends on which files
   the user happens to have opened.
3. **Deletion is a side-list, not absence.** The module stays in the store and a
   parallel list says to pretend it is gone. Every consumer has to join the two.

Six pending mechanisms, 58 references, and 11 consumers outside the workspace
that each have to remember the join: `_pendingDeletes`, `_pendingFolderMoves`,
`NeedsLocation`, `OriginalDiskPath`, `IsNewFile`, `IsMoved`.

### The bugs this shape produced

Five defects in two days, all one root - the store and the side-list disagreeing:

| | |
|---|---|
| UB-162 | a deleted focus still got a card |
| UB-171 | `ConnectedComponent` seeds itself with the focus, re-adding it one line after the check meant to exclude it |
| UB-172 | a deleted module kept its name reserved |
| UB-165 | an empty member set indexed a zero-length array |
| UB-164 | "already marked" was reported as "read-only" |

All five are structurally impossible when delete means *remove from the tree*:
there is no second list to disagree with.

Four earlier ones - UB-136, UB-143, UB-152, UB-156 - were the other seam: the
language server's view of disk versus the builder's view of its buffers. Taking
the server out of the STRUCTURE path removes that seam too. It stays for
completion, diagnostics, hover and schema, which is what it is for.

## The model

```
BuilderTree
    IReadOnlyList<Module> Modules        ordered, stable
    Module ById(id) / ByPath(path)
    Load(rootFolder, focus)              read + parse once
    Create / Delete / Rename / Move / Edit
    BuilderGraph Project()               cards from cached parses
    SaveResult Save()                    diff against LastProjection

Module
    Id            stable, opaque, survives every rename
    Name          "ShowcasePage"
    Kind          Component | Style | Hook | Util
    Folder        authoritative, mutable
    Text          the buffer - the only mutable content
    Parsed        cached ParseResult, invalidated on Edit
    DiskPath      where it sits on disk now, or null if never written
    IsReadOnly    immutable-package policy, unchanged
    UsedCrlf      line-ending flavour of the file it came from
    NeedsLocation provisional path, Save must not write it
    IsDirty       => Text differs from the text last projected
    Path          => Folder / (Name + Suffix(Kind))   DERIVED

BuilderTree.LastProjection
    HashSet<string>   the paths that were on disk as of the last load or Save
```

`PendingDelete` is deliberately **absent**. The earlier plan had it, and that was
the same mistake one layer up: deletion is absence, not a flag.

## Save, as a pure diff

```
for each module in tree:
    if NeedsLocation        -> skip entirely
    ensure directory
    if DiskPath == null              -> write, created
    else if DiskPath != Path         -> move (AssetDatabase.MoveAsset), then write if dirty
    else if IsDirty                  -> write
    DiskPath = Path

for each path in LastProjection not claimed by any module's DiskPath:
    trash it (AssetDatabase.MoveAssetToTrash, or File.Delete outside Assets)

LastProjection = { every module's DiskPath }
if anything was created -> AssetDatabase.Refresh() OUTSIDE the reload suppressor
```

Run it twice and the second run is a no-op: nothing is dirty, no `DiskPath`
disagrees with its `Path`, and nothing is orphaned. That is the idempotence the
owner asked for, and it falls out of the shape rather than being maintained.

The six disk operations are unchanged from today's `SaveAll` - directory create,
write with CRLF re-inflation, `MoveAsset`, `MoveAssetToTrash`/`File.Delete`, the
conditional `Refresh`, and the `AssemblyReloadSuppressor` bracket keyed on
`UitkxHmrController.IsActive`. Only their ORCHESTRATION changes.

## Load

- **From an existing file:** find the tree's folder (nearest ancestor that owns
  the focus, by the existing convention), read every `.uitkx` under it once,
  parse each, resolve imports through the one canonical `ImportResolver`.
  `LastProjection` = the set of paths read. Nothing is dirty.
- **From the empty state:** an empty tree. Every module created is dirty and has
  `DiskPath == null`, so the first Save writes all of it.

A bounded directory walk, not a project-wide scan, and not per mount. It also
answers the reverse-edge question - "who imports me", needed to pick the root -
which forward-following imports alone cannot.

## Abort

Restore each module's `Text` from what was last projected and drop modules whose
`DiskPath` is null. Equivalent to re-running Load, and can literally be
implemented that way for a tree that came from disk.

## What this changes for rendering

`Project()` builds `BuilderGraph` from the cached parses. `BuilderCanvasNode`
keeps its shape - `FilePath, Title, Signature, ExposedSignature, Kind, X, Y,
IsReadOnly, CachedHeight, Exports, Imports, Body, Markup, ExportDetail,
IslandLines, IslandStart/EndLine` - so the canvas, the drawing code and
`CanvasView.uitkx` are untouched. Only where the node's contents come FROM
changes.

Deleted along the way, because there is nothing left for them to do:
`IsHiddenOnDisk`, `IsPendingDelete`, `PendingNewFiles`, `IsPathAvailable`,
`MarkForDeletion`, `MoveFolder`, `PendingFolderMoves`, the `isHidden` predicate
threaded through `LoadTreeAsync`, and the inventory/override machinery that
exists only to reconcile the server's disk view with the builder's buffers.

## Performance

Per mount today, all verified in `BuilderGraphService`:

1. a language-server round trip for the inventory, retried up to 8 times with
   400 ms x attempt backoff (`RequestGraphWithRetry`)
2. a parse of every overridden module (`BuildStructure`, line 247)
3. a parse of every member AGAIN (`PopulateCardDetail`, line 501)
4. a disk read for every member that is not open

So a dirty module is parsed TWICE per mount, and every mount pays an IPC round
trip.

Under this model a mount does none of it - the parses are on the nodes, so
mounting is projection only. The one-time load is N reads plus N parses, which
is exactly what a single mount already costs today. **The load is not a new
cost; it is today's per-mount cost paid once.**

Not improved by this work, and worth stating so it is not expected: the preview
compile is a separate subsystem, and a commit still spawns an external csc on
Unity 6.5 (HMR-ROSLYN-65).

## Staging

Each stage leaves the builder working and is independently revertible.

1. **Model and load.** `BuilderTree` + `Module`, eager load, `LastProjection`.
   The existing workspace stays and delegates to it, so nothing else moves yet.
2. **Project the graph.** `LoadTreeAsync` becomes `Project()` over the cached
   parses. Delete the inventory, the server round trip, the override predicate
   and the `isHidden` threading.
3. **Save as diff.** Replace `SaveAll` with the projector. Delete
   `_pendingDeletes` and `_pendingFolderMoves` and their consumers.
4. **Collapse the session.** `BuilderDocumentSession` becomes `Module`; delete
   `IsNewFile`/`IsMoved`/`OriginalDiskPath` in favour of `DiskPath`, and delete
   the per-session undo/redo stacks - `Undo`, `Redo`, `CanUndo` and `CanRedo`
   have NO callers outside the class, the ledger owns undo. (This also corrects
   UB-134, which credited the stable id with preserving a per-session undo
   history that was already dead code.)

## Risks, and what I will not claim

- **Domain reloads.** The tree must serialize with the window exactly as the
  sessions do today, or unsaved work dies on a script recompile. This is the
  single highest-risk part and gets its own verification pass.
- **External edits.** With an eager model, a file changing on disk has to reload
  that node. The `BuilderAssetEvents` hook exists; it must now feed the tree.
  Note this trigger is ALREADY missing today (UB-161 research), so the model
  does not regress it - it is the place to fix it.
- **A rewrite mid-testing.** Things will move for one cycle. The staging is what
  keeps that bounded.
- **What it does not fix:** assembly identity in the preview (UB-159), the fiber
  teardown work (UB-154/155), compile cost, and every UI behaviour. This is a
  data-model change, not a general repair.

## Gates

Per stage: builder editor assembly compiles clean against the 6000.5.6f1
reference set, `Plans~/capture-harness/validate-uitkx.ps1` at 0 diagnostics,
`node scripts/check-machine-paths.mjs`, and deploy to the embedded clone with the
running-build check (`grep -a` the new symbols in
`Library/ScriptAssemblies/Ruitk.Builder.Editor.dll`) before asking the owner to
test. The SG and LSP suites only when a stage touches the generator or the
language lib, which none of these should.

## Mitigations for the domain-reload risk

Agreed 2026-08-23. The principle is to make each invariant UNREPRESENTABLE
rather than remembered - "remember to check" has failed three times in this
campaign already (hidden files, import resolution, assembly identity), each time
because a guard sat on the callers instead of at the point of decision.

**1. Encapsulate every hazard behind one accessor.**

- Unity turns a null string into `""` on deserialize, and `DiskPath == null` is
  MEANINGFUL here - it is how a module says it has never been written. So
  `DiskPath` is never compared at a call site: `IsOnDisk` answers the question
  once, over `string.IsNullOrEmpty`. The existing code already does this for
  `OriginalDiskPath`, by convention rather than by construction; this makes it
  structural.
- `Parsed` is a lazy property that re-parses when null. A reload clearing it is
  then indistinguishable from "not parsed yet" and self-heals, so nothing
  downstream has to know a reload happened.
- The id and path indexes stay private and are rebuilt in `OnAfterDeserialize`.
  Callers reach them only through `ById`/`ByPath`, so no stale dictionary can be
  held across a reload.

**2. A constraint to design around, not to mitigate.** `OnAfterDeserialize` can
run OFF the main thread and must not touch Unity APIs. Today's implementation
only moves managed data, which is why it is safe. The re-parse therefore cannot
live there - a second, independent reason for the lazy property above. Do not
"optimise" it later by rebuilding the cache eagerly in the callback.

**3. Validate after every reload.** `OnEnable` runs `BuilderTree.Validate()`:
the module count matches the serialized list, no duplicate paths, the indexes
agree with the list, no module claims an empty-but-not-null disk path. The value
is not the check, it is that a broken round-trip ANNOUNCES ITSELF where it
happens instead of surfacing three stages later as an inexplicable bug.

**4. A reload journal, as the actual backstop.** Everything above lowers the
chance of a serialization bug; none of it recovers the work if one slips
through. `AssemblyReloadEvents.beforeAssemblyReload` already has a subscriber in
`BuilderSaveMetrics`, so the hook is proven. The tree is dumped to JSON under
`UserSettings/` before every reload; after one, if validation fails or the tree
comes back empty while the journal is newer, the user is offered a restore.

This also covers the case nothing currently protects against: the Unity fatal
error the owner hit on 2026-08-22 killed the editor with unsaved modules open.
The journal is the only mitigation here that survives a process death, and it is
worth having independently of this refactor.

**5. Test the round trip outside Unity.** `OnBeforeSerialize` and
`OnAfterDeserialize` are pure functions over managed data - no Unity APIs - so
the shuttle is testable in the ordinary loop: build a tree, run it, assert it
comes back identical, including a null `DiskPath` AND an empty one.

**Honest limit:** none of this proves Unity's own serializer behaves as
documented for these exact field shapes. That needs one real reload with an
unsaved tree open, and it is stage 1's exit gate - before anything depends on it.
