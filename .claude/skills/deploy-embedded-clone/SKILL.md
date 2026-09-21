---
name: deploy-embedded-clone
description: Deploy the working tree into the owner's embedded Unity project so they can test package changes in a real editor. Use when the user says "deploy", "copy it to UnityComponents", "let me test it", "push it to the clone", or whenever a turn is about to end with "re-test this" — editing the repo alone changes NOTHING in their Unity. Covers the locked-DLL rules, stale-file deletion, and what must never be overwritten.
---

# Deploying to the embedded clone

The owner tests Unity-side behaviour in an **embedded copy** of the package inside a real
Unity project. The repo is where fixes are authored; the clone is a deploy target and
nothing else. It is a real directory copy, not a junction — so **editing the repo changes
nothing in their editor until you deploy.**

> **DEPLOY IS MANDATORY BEFORE ASKING THE OWNER TO TEST ANYTHING.**
> This cost a whole session once: builder fixes across many rounds, "please re-test" every
> time, every screenshot showing stale code, ending in *"i tabbed to unity, no compile
> changes happened"*. If a turn ends with "re-test", it deploys first.

## The command

```bash
node scripts/deploy-embedded-clone.mjs --dry-run   # list every copy and delete
node scripts/deploy-embedded-clone.mjs            # deploy
```

The clone path is machine-local and is never written into a tracked file. It resolves
`$RUITK_EMBEDDED_CLONE` → `.ruitk-local.json` `"embeddedClone"` → an error naming both. The
value points at the **package** folder, not the project:
`<project>/Packages/com.reactiveuitoolkit`.

## Close Unity first

The script refuses to run while Unity is open, and the refusal is the point.

`Analyzers/Ruitk.Language.dll` is held for the life of the Unity **process** —
`UitkxHmrCompiler` does `Assembly.LoadFrom` on that exact path, and a domain reload does not
release it. Copying it fails with a permission error, silently leaving the old language lib
live.

Worse, deploying `Editor/Plugins/Ruitk.Language.Editor.dll` **while HMR is running** breaks
the Builder outright: HMR holds `LockReloadAssemblies()`, which blocks the domain *reload*
but not script *compilation*, so Unity recompiles `Ruitk.Builder.Editor` against the new
plugin metadata while the AppDomain still holds the old one. `ImmutableArray<ImportDeclaration>`
becomes two different types and you get `MissingMethodException: DirectiveSet.get_Imports()`
with an empty Builder window. It is a type-identity mismatch, not a missing method.

`--force` deploys everything except those four files. Use it only when the generator did NOT
change — otherwise the clone runs the old generator against new sources, which is its own
class of confusing failure.

## Stale files are the quiet failure

Deploying only ever **adds**. A file the repo deleted survives in the clone and keeps
compiling; a file the repo **moved** exists twice.

That is not hypothetical — the 0.21.0 signals split moved `Signal<T>` from
`Shared/Core/Signals/` to `Signals/`. A clone keeping both defines the type twice, and every
single use becomes an ambiguous reference.

The script does not guess. It reads the clone's own version out of its `package.json`, then
asks git what the repository deleted between that tag and **the working tree** — not `HEAD`,
because a deploy exists to test uncommitted work, and a move that is staged but not
committed is exactly the case that leaves the duplicate.

## What it must never touch

The clone legitimately holds files the repo does not, and a mirror would destroy them:

- `Builder/Editor/UITKX_GeneratorTrigger.g.cs` (+ `.meta`) — written by the change watcher;
- the owner's experiments under `Samples/`, typically `.uitkx` files used for HMR testing.

**Never `robocopy /MIR` the payload folders.** The script deletes only what the repository
deleted, reports everything else that is clone-only, and leaves it alone.

## After deploying

1. Focus the Unity window so it reimports.
2. **Read the Console before saying anything works.** A successful copy is not a successful
   deploy.
3. If the language lib changed, the owner must restart Unity — a locked domain cannot unload
   a plugin it already loaded. Verify with `grep -ac <newSymbol> <clone>/Analyzers/Ruitk.Language.dll`
   on the **clone's** copy, not the repo's.

## Compile before you deploy

Deploying a tree that does not compile wastes a round trip. `Editor/` and `Builder/` have no
coverage in the .NET suites, so use the real thing:

```bash
node scripts/unity-editor-compile.mjs     # every asmdef, in a real editor, ~1 min
```

That imports the repo as a package into a throwaway project and asserts each asmdef produced
an assembly — which also catches a missing `.meta`, since an asmdef whose scripts did not all
import produces nothing, silently.

## Related

- `rebuild-ide-extensions` — the F5 loop for the VS Code / VS 2022 extensions. Different
  target, different procedure.
- `scripts/unity-il2cpp-check.mjs` — a player build, for the class of defect that only
  appears outside the editor.
