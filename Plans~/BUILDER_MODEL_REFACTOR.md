# RUITK Builder — project model refactor

Status: SUPERSEDED for its remaining scope by `Plans~/BUILDER_TREE_MODEL.md`.

Stages 1-3 shipped and stand. Stage 4 is partly done. The part this plan got
WRONG is recorded there rather than here: its `Module` still modelled deletion as
a `PendingDelete` FLAG, and it kept the lazy/disk-fallback read instead of loading
the tree. Those two omissions are what produced UB-162, UB-164, UB-165, UB-171 and
UB-172 - five bugs in two days, all of them the store and a side-list disagreeing.
Branch: `feat/ruitk-builder`.

## The one-line problem

The builder holds **text** in memory but derives **identity and structure** from
disk. Every defect in the UB-110..133 range is that seam:

| Defect | Surface | Same root |
|---|---|---|
| UB-111 | a new component has no card | the graph comes from files on disk |
| UB-121 | its import draws no edge | so do the edges |
| UB-132 | `DirectoryNotFoundException` per keystroke | its folder does not exist yet |
| UB-133 | renaming the root collapses the tree | the renamed path is unknown to disk |
| (open) | renaming a folder-owning module orphans its children | children's paths are relative to the old folder |

Three separate compensations already exist for the same thing —
`AppendPendingNewNodes`, `AppendMissingImportEdges`, `LinkPendingImports` — each
teaching one more consumer that a module can exist without a file. That is the
signature of a missing owner.

## The correction

**An in-memory project model owns module identity. Disk is a deterministic
projection of that model, computed at save time.**

The subtlety that decides the design: a path is *not* purely a storage detail.
The compiler consumes it as identity — `EffectiveNamespace.Resolve` derives a
module's namespace, family key and `__Exports` registry key from its file path
(`NamespaceDerivation.DeriveFileModule`), and `[UitkxSource]` stamps that path
into the assembly so `BuilderPreviewPane.ResolveComponentType` can match a
compiled type back to its source.

So the model does not *replace* paths. It **owns** them:

- `Module.Id` is stable and opaque. It never changes.
- `Module.Path` is **derived** — `Folder / (Name + Suffix(Kind))` — recomputed
  from model state, never stored as truth.
- Everything downstream keeps consuming paths (LSP URIs, compile, preview,
  disk). They are now a *view* of the model rather than the truth.

This is what makes the refactor tractable: it fixes identity ownership without
rewriting the 40+ canvas callbacks that pass a path.

## Types

```
BuilderProject
    IReadOnlyList<Module> Modules
    Module ById(id) / ByPath(path)          // path index, rebuilt on mutation
    Create / Delete / Rename / Move / Edit  // the only mutators
    BuilderGraph Project()                  // graph derived from the model
    SaveResult Save()                       // the projector

Module
    Id          stable, opaque
    Name        "ShowcasePage"
    Kind        Component | Style | Hook | Util
    Folder      authoritative, mutable
    Text        buffer
    DiskPath    null until first projected to disk
    IsDirty     Text differs from the last projected text
    PendingDelete
    Path        => Folder / (Name + Suffix(Kind))   DERIVED
    OwnsFolder  => GetFileName(Folder) == Name
```

`Folder` is **stored, not derived**. Deriving it from a parent link would be
elegant but would want to *move* every existing tree that does not follow the
`ComponentName/ComponentName.uitkx` convention. Storing it honours whatever
layout the user already has; it is only *computed* for newly created modules and
*rewritten* on a folder-owning rename.

## Rename, correctly

One model operation:

1. Set the module's `Name` to the new name.
2. If `OwnsFolder`, set `Folder` to `parent(oldFolder)/newName`, and **every
   module whose `Folder` is at or under `oldFolder` has that prefix rewritten**.
   That is the subtree move — it falls out of the model instead of being a
   separate feature.
3. Every module's `Path` is now correct by derivation. Import specifiers are
   re-emitted from the new relative paths.
4. `Id` is unchanged, so undo history, `UsedCrlf`, layout position and card
   selection all survive.

Today rename replaces the session object via `CreateNew`, losing undo history
and the recorded line-ending flavour. That defect disappears with id stability.

## The graph is projected, not fetched

`BuilderGraphService` builds nodes and edges from the model: parse each module's
buffer with `BuilderLanguage.Parse`, resolve each import specifier through the
**canonical** `ide-extensions~/language-lib/ImportResolver` against model
identities.

`PopulateCardDetail` already builds a card's entire content — exports,
signature, imports, body, markup, islands — from text alone. No language server
is involved. And `RequestWorkspaceGraph` is the *only* LSP call that needs files
on disk; completion, semantic tokens, component props, schema, hooks and
diagnostics are all buffer-pushed or global. So `RequestWorkspaceGraph` demotes
from a per-mount dependency to a **one-shot discovery bootstrap**: it tells the
model which files exist on disk when a tree is opened, and is never consulted
again.

Deleted by this stage: `AppendPendingNewNodes`, `AppendMissingImportEdges`,
`LinkPendingImports`, and the three duplicate specifier resolvers
(`BuilderCanvasHost.ResolveSpecifier`, `BuilderGraphService.ResolvePendingSpecifier`,
`BuilderPreviewCompiler.ResolveImports`) — all of which exist only because
nothing owned resolution.

## Save is a projector

`Save()` diffs the model's derived paths against the last projected state and
emits one ordered batch:

```
create directories -> write dirty -> move renamed -> trash deleted -> Refresh
```

It owns the six disk operations that live in `BuilderWorkspace.SaveAll` today
(`Directory.CreateDirectory`, `File.WriteAllText` with CRLF re-inflation,
`AssetDatabase.MoveAssetToTrash`, `File.Delete`, the conditional
`AssetDatabase.Refresh`, and the `AssemblyReloadSuppressor` bracket keyed on
`UitkxHmrController.IsActive`), and nothing else in the builder touches disk.

## Compiling a module that has no file

Already supported, and verified: every `.uitkx` read and existence check on the
compile path routes through `UitkxHmrCompiler.SourceOverlay` /
`UitkxSourceExists`, and both companion directory scans are guarded against a
missing directory. The path is an identity token, not a handle.

Two residual constraints, both to be handled by the model rather than worked
around:

1. Companion `.uitkx` files are discovered by **directory glob**, so an unsaved
   sibling in a not-yet-existing folder is only found if it is reachable as an
   *import target* (overlay-aware). The model knows the sibling set, so it can
   supply companions explicitly.
2. `AsmdefResolver.ResolveDirectory` wraps its upward walk in a single `try`;
   `Directory.GetFiles` on a **non-existent** directory throws, exits the walk
   immediately, and falls back to the `Assembly-CSharp` reference closure — then
   **caches that wrong answer** in a static dictionary until `InvalidateAll()`.
   A pending module in a new folder therefore compiles against the wrong
   references. This is a real latent bug, filed separately; the fix is to skip
   missing directories and keep walking, not to swallow the walk.

## What this does not fix

A module's namespace is path-derived **by language design**. Renaming a module
changes its compiled namespace and family key, so the preview's knob state
resets across a rename. That is inherent to file-keyed namespaces, not to this
refactor, and is left as-is.

## Staging

Each stage leaves the builder working.

- **Stage 1 — identity. DONE.** Sessions carry a stable `Id` and a separate
  `OriginalDiskPath`; the path map is now an index rather than the truth, and
  `Reindex` is the single place a module changes location. Rename re-files the
  same session instead of destroying it, so undo history and `UsedCrlf` survive;
  Save projects the move as one operation and Abort points the path back. The
  ledger gained one `IsMove` change kind. Filed as UB-134, with the two defects
  that fell out of it (a session opened on a missing path claiming a disk origin
  it did not have, and the ghost card a properly-expressed move would have left
  behind). Gate: builder editor assembly compiles clean against 6000.5.6f1.
- **Stage 2 — derived graph. DONE.** Structure now comes from the modules
  themselves, with the server's answer used as what it actually is — a cache of
  the disk state, derived from the same text — for any module the builder has
  not touched. Parsing the whole inventory on every mount was measured against
  173 `.uitkx` files and rejected as a regression; an override predicate keeps
  the parsing bounded to what the builder is actually holding. The three
  compensations and the three duplicate resolvers are deleted, and one canonical
  `ImportResolver` serves both the canvas edges and the preview's compile order.
  Filed as UB-136, with the `didOpen`/`didChange` defect found in the same seam.
- **Stage 3 — projector. DONE.** A folder-owning rename is a FOLDER move: the
  subtree is brought into the model, the move is pending like every other edit,
  and Save projects it as one `AssetDatabase.MoveAsset` so child GUIDs and the
  files the builder does not manage both survive. Ledger changes carry their
  module identity, so a replay still finds a module after a rename has moved the
  path out from under it. Filed as UB-135.
- **Stage 4 — loose ends. DONE.** Both contract-bypassing writers are folded
  in: a UXML import is now a pending module (UB-139). The saved layout follows a
  module or a folder to its new home rather than being keyed by identity —
  `Id` is stable within a session but a fresh window generates new ids, so
  identity-keying would have lost the layout BETWEEN sessions instead of across
  renames; the rename tells the layout where it went instead (UB-140). History
  jumps replay whole entries through the same path undo and redo use, so a jump
  across a create, delete or rename moves the tree and not just the text
  (UB-141).

## Gates

Unchanged per stage: `Plans~/capture-harness/validate-uitkx.ps1`, the SG-backed
csc smoke, `node scripts/check-machine-paths.mjs`, the SG suite (1879) and the
LSP suite (180). Editor smoke-compile in the embedded clone before any stage is
called done.
