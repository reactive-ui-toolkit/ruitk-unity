# Post-rebrand audit — bugs found

Branch `rebrand/umbrella` @ `b0da6f24`, audited against the `dev` baseline on 2026-07-28.
Findings only — nothing in this wave was fixed, and no code was changed while auditing.

**Verdict.** The mechanical rename itself is in unusually good shape: git-index casing really
landed (596 new-casing paths, 0 old), all 12 renamed `.meta` GUIDs are byte-identical to `dev`,
the committed analyzer DLLs are genuine post-rebrand rebuilds (0 old tokens in the binaries),
all 10 asmdefs resolve, the 53 menu items share one root, and both suites re-run green at this
tip (SG 1754/1754, LSP 152/152). **Every finding below is a string the sweep bent that it should
not have, or a consequence of the rename that nothing cleans up.** The single dominant cause is
the codemod's bare-token rule `ReactiveUITK(?![A-Za-z_])` leaking into identities the release
explicitly froze.

Entries marked **[pre-existing]** are not caused by the rebrand; they are recorded because this
release inherits them.

> **SUPERVISOR VERIFICATION (2026-07-28):** independently re-verified against the tree — C1's
> shipped `"Ruitk.uitkx"` manifest value, H1's trigger-folder/namespace/skip-list trio, H2's
> runbook identities, M1's live orphaned registry asset (confirmed on disk in the outer project),
> M10's 24 tracked bin/obj files, M11's 23 on origin/dist, M13's `[dev, main]` triggers, and every
> other spot-checked anchor CONFIRMED. **Nothing deleted; all findings valid and cleared for the
> fix pass.** Fixes must NOT be committed — the supervisor reviews the diffs first.

---

## CRITICAL

### C1 — the shipped VS Code default formatter points at an extension that does not exist
**`ide-extensions~/vscode/package.json:85`**

```json
"editor.defaultFormatter": "Ruitk.uitkx",     // was "ReactiveUITK.uitkx" on dev
```

The same file still declares `"name": "uitkx"` (line 2) and `"publisher": "ReactiveUITK"`
(line 7), so the extension's real identifier is **`ReactiveUITK.uitkx`** — deliberately frozen,
and the 1.8.0 changelog promises "every extension marketplace identity … unchanged". The
bare-token rule rewrote a shipped `configurationDefaults` value.

**Failure:** the next `publish-vscode` run ships 1.8.0; every user who opens a `.uitkx` file gets
`[uitkx].editor.defaultFormatter` bound to an unresolvable ID → *"Extension 'Ruitk.uitkx' is
configured as a formatter but it cannot format"*, and format-on-save silently dies. 100% of
users, first save after update, a straight regression from 1.7.0.

**Why it survived:** the branch's last commit fixed exactly this string in three docs-site files
(`UitkxConfigPage.tsx:183`, `UitkxDebuggingPage.tsx:123`, `docs.tsx:413` — all correctly back to
`ReactiveUITK.uitkx`) but missed the manifest that actually ships. Docs and shipped config now
contradict each other. Found independently by three of the four audit passes.

---

## HIGH

### H1 — the documented upgrade path makes the user's project stop compiling (CS0101)
**`Editor/UitkxChangeWatcher.cs:62` and `:149`, with `SourceGenerator~/Tools/RuitkMigrateBrand/Program.cs:75-86`**

Two things moved in the same release: the default-assembly trigger folder
`Assets/ReactiveUITK` → `Assets/Ruitk` (line 62), and the namespace the trigger file emits,
`ReactiveUITK.Generated` → `Ruitk.Generated` (line 149) — same filename, same
`internal static class UitkxRecompileTrigger`. Nothing deletes the old folder, and the codemod's
`IsSkipped` skips `ReactiveUIToolKit`/`ReactiveUIToolkit`/`~`/`.git`/`Library`/`Temp`/`obj`/`bin`
— **not** `ReactiveUITK`.

**Failure**, following `MIGRATION-0.12.md` verbatim: a 0.11.x project has
`Assets/ReactiveUITK/UITKX_GeneratorTrigger.g.cs` declaring
`ReactiveUITK.Generated.UitkxRecompileTrigger`. Step 2 runs the codemod, which scans that file
(it is `.cs`, and its folder is not skipped) and rewrites it to `Ruitk.Generated`. The next
`.uitkx` save writes an identical type into `Assets/Ruitk/`. Two identical types in
`Assembly-CSharp` → **CS0101** + CS0102, the whole default assembly stops compiling, and the
error points at a hidden auto-generated file the user has never heard of. Afterwards
`RuitkMigrateBrand --check` exits 0, so any CI gate reports clean while the project is broken.

`MIGRATION-0.12.md` never mentions `Assets/ReactiveUITK`; step 1 (line 43) only says to delete
`Assets/ReactiveUIToolKit` — a *different* folder.

### H2 — the publish runbooks now teach a publisher/extension identity that does not exist
**`ide-extensions~/docs/vscode-publish.md:11,66-69` · `VS2022_PUBLISH_GUIDE.md:35,114,234-236,245` · `visual-studio/docs/visual-studio-publish.md:62,87,103,116,119,120` · `RELEASE_OPS.md:168` · `Plans~/EXTENSION_LISTING_PLAN.md:115` · `.csharpierignore:2` (comment only)**

The same over-eager rewrite hit every marketplace identity in the release documentation, while
the real manifests were correctly left alone (`publishManifest.json:2` and
`vscode/package.json:7` still `"publisher": "ReactiveUITK"`; `source.extension.vsixmanifest:6`
still `Identity Id="UitkxVsix.ReactiveUITK"`). The docs now instruct:
`vsce login Ruitk`, `manage/publishers/Ruitk`, `<Identity Id="UitkxVsix.Ruitk">`,
`"extensionId": "UitkxVsix.Ruitk"`, `ext install Ruitk.uitkx`.

**Failure:** `vsce login Ruitk` fails outright. Worse, an operator (or a future agent) who copies
the `Identity Id` from the guide into the real manifest publishes a **new** marketplace identity:
existing installs are orphaned with no auto-update path and the listing's rating history is lost.
Marketplace IDs are permanent — this is the publish-once-unrecoverable class.

### H3 — the only documented migration command cannot run in any shipped install
**`MIGRATION-0.12.md:48,50` and `ReactiveUIToolkitDocs~/src/pages/Migration/MigrationPage.tsx:7,10`**

Both teach:
```
dotnet run --project Assets/ReactiveUIToolkit/SourceGenerator~/Tools/RuitkMigrateBrand -- Assets
```
That path exists only in the maintainer's repo-in-Assets layout. Asset Store customers get
`Assets/ReactiveUIToolkit/` **without** `SourceGenerator~` (`CICD/Editor/AssetStoreExport.cs:44-52`
builds the export from `AssetDatabase.FindAssets`, and Unity never imports `~` folders). UPM
customers — the docs' primary install channel — resolve the package into
`Library/PackageCache/com.reactiveuitoolkit@<hash>/`, never under `Assets/`.

**Failure:** MSB1003 "project file does not exist". The migration story is unrunnable for every
real customer.

### H4 — no upgrade step for `Packages/manifest.json`, whose URL changed
**`MIGRATION-0.12.md:36-55`**

The UPM install URL moved to `https://github.com/reactive-ui-toolkit/ruitk-unity.git#dist`. The
guide's only "upgrade" instruction is to delete `Assets/ReactiveUIToolKit` — a folder UPM users
do not have.

**Failure:** an existing UPM user follows the guide to the letter and never receives 0.12.0; if
the old URL stops resolving, package resolution fails and the project will not open.

### H5 — the codemod has no error handling, pre-flight, or backup; a failure leaves a half-migrated project
**`SourceGenerator~/Tools/RuitkMigrateBrand/Program.cs:61-66`**

The write loop calls `File.WriteAllText` per changed file with no `try`, no writability check, and
no backup. The first read-only file throws an unhandled `UnauthorizedAccessException`.

**Failure:** Perforce and Plastic Cloud (both mainstream in Unity teams) keep unopened files
read-only. The tool rewrites files 1..N, crashes on N+1 with a stack trace, and leaves the project
in a mixed `Ruitk`/`ReactiveUITK` state with no rollback and no record of what was written.

---

## MEDIUM

### M1 — upgrading leaves two `__uitkx_registry` assets; `Resources.Load` picks one non-deterministically
**`Editor/UitkxAssetRegistrySync.cs:26` ↔ `Shared/Core/UitkxAssetRegistry.cs:62`**

`RegistryFolder` moved `Assets/ReactiveUITK/Resources` → `Assets/Ruitk/Resources`, but the asset
name is unchanged and the runtime read is name-based:
`Resources.Load<UitkxAssetRegistry>("__uitkx_registry")`. Nothing migrates or deletes the old
asset — one exists right now in this project at
`Assets/ReactiveUITK/Resources/__uitkx_registry.asset` with populated entries.

**Failure:** `GetOrCreateRegistry()` finds nothing at the new path and creates a second registry;
`Resources.Load` by name with duplicates resolves ambiguously. When the stale copy wins, every
`Asset<T>()`/`Ast<T>()` lookup and every `uss=` stylesheet load added or changed after the
upgrade returns null → missing styles/assets in editor *and* player builds.
`ClearRegistryIfExists()` only touches the new path, so it never self-heals; both registries also
get force-included in builds. Not mentioned in `MIGRATION-0.12.md` or `CHANGELOG.md`.

> H1 and M1 share one mitigation: the orphaned `Assets/ReactiveUITK/` folder (trigger file **and**
> registry) needs an explicit "delete this after migrating" step, plus a codemod skip for it.

### M2 — the codemod has no menu-root rule, and its bare token actively corrupts menu strings
**`SourceGenerator~/Tools/RuitkMigrateBrand/BrandMigrator.cs:34-35`**

`/` is not in `[A-Za-z_]`, so the bare-token rule matches menu paths:
`"ReactiveUITK/HMR Mode"` → `"Ruitk/HMR Mode"` — but the new root is `"Reactive UI Toolkit/…"`.

**Failure:** a user's `ExecuteMenuItem("ReactiveUITK/HMR Mode")` still fails after migration, now
pointing at a plausible-looking wrong path that greps clean; a user's own
`[MenuItem("ReactiveUITK/My Tool")]` silently relocates to a third orphan menu.
`MIGRATION-0.12.md:34` lists the menu rename under "What renamed", but the
"what the codemod does, verbatim" section (57-73) has no menu rule and never says to hand-fix.

### M3 — the codemod does not round-trip file encoding
**`SourceGenerator~/Tools/RuitkMigrateBrand/Program.cs:55` and `:66`**

`File.ReadAllText` detects a BOM; `File.WriteAllText` always writes UTF-8 **without** BOM.

**Failure A:** every user `.cs` that had a UTF-8 BOM (Visual Studio's default for new C# files) is
rewritten BOM-less — a whole-file VCS diff on top of the intended rename, defeating review.
**Failure B (data loss):** a file saved in an OS ANSI codepage (Shift-JIS, Windows-1252 — normal
in non-English studios) is decoded as UTF-8, every invalid byte becomes U+FFFD, and it is written
back as UTF-8 → irreversible mojibake in the user's own comments and strings, with no backup.
(Line endings *are* preserved — verified, not a defect.)

### M4 — codemod under-coverage: file kinds it never scans are not listed as manual steps either
**`SourceGenerator~/Tools/RuitkMigrateBrand/Program.cs:26`**

Scans `.cs`, `.uitkx`, `.asmdef` only. Not scanned: `.asmref` (can name `ReactiveUITK.Runtime`),
`csc.rsp`/`mcs.rsp` (`-define:REACTIVEUITK_HAS_TEST_FRAMEWORK`), `uitkx.config.json` (the
package's own `Samples/uitkx.config.json` needed the rename), and Project Settings scripting
defines. **Failure:** a user carrying the define in Project Settings or an `.rsp` gets a silently
inactive conditional block with no diagnostic.
*(Verified genuinely unnecessary, so not gaps: no `.uxml`, no `[SerializeReference]`, no
`link.xml`; `.meta` GUIDs are unchanged so scene/prefab YAML is safe.)*

### M5 — a user-visible break is missing from the changelog and both migration documents
**`ide-extensions~/language-lib/NamespaceDerivation.cs:22` · `Parser/DirectiveParser.cs:32`**

The default generated namespace for **user** `.uitkx` files changed: `Root`
`ReactiveUITK.Uitkx` → `Ruitk.Uitkx`, and `FunctionStyleDefaultNamespace`
`ReactiveUITK.FunctionStyle` → `Ruitk.FunctionStyle`. Every user component without an explicit
`@namespace`/`namespacePrefix` moves namespace. The changelog only says "generated code emits
`global::Ruitk.*`", which is about attribute qualification, not the user's own components.

**Failure:** reflection and serialized type names
(`Type.GetType("ReactiveUITK.Uitkx.UI.HelloWorld, …")`) break at **runtime**, not compile time,
and assembly-qualified strings are not reliably fixed by the codemod.

### M6 — "delete the old package folder first" silently destroys the user's `config.json`
**`MIGRATION-0.12.md:38-40` and `MigrationPage.tsx:47-49`**

`Shared/Core/Config/RuitkConfig.cs:73-77` loads `<Assets>/ReactiveUIToolkit/config.json` — the
user-editable env/trace config lives **inside** the package folder (documented at
`UitkxConceptsPage.tsx:98`). **Failure:** following step 1 resets `env`, `traceLevel`,
`diffTracing` and `exceptionControlFlow` to defaults with no warning and no "back this up" note.

### M7 — install-path casing: on Linux an in-place upgrade silently loses configuration
**`Shared/Core/Config/RuitkConfig.cs:76`, `Editor/HMR/UitkxHmrCompiler.cs:3339`, `Editor/UitkxTestRunnerWindow.cs:288`, `CICD/Editor/AssetStoreExport.cs:29`, `CICD/Editor/PublishUtility.cs:47,168,338,504,505`, `Diagnostics/Benchmark/BenchLogging/BenchPerSecondLogger.cs:364`, `Diagnostics/Benchmark/Editor/BenchResultsViewer.cs:778,791`, `Diagnostics/Logs/ReactiveLogCapture.cs:74`**

All these literals went `ReactiveUIToolKit` → `ReactiveUIToolkit`, which is invisible on
Windows/macOS. `MIGRATION-0.12.md:41-44` explicitly acknowledges that a Linux project can end up
with both folders — but not the consequence: with the capital-K folder still in place,
`RuitkConfig` silently falls back to defaults and the HMR analyzers-dir fallback-1 misses (saved
only by the recursive fallback-2).

### M8 — the codemod's install-path rule only matches the `Assets/`-prefixed literal
**`SourceGenerator~/Tools/RuitkMigrateBrand/BrandMigrator.cs:40-45`**

The three path rules all require an `Assets/` (or `Assets\`, `Assets\\`) prefix. The package's own
idiom is a **bare** segment — `Path.Combine(Application.dataPath, "ReactiveUIToolkit", "config.json")`
(`Shared/Core/Config/RuitkConfig.cs:73-77`) — and user code copying that pattern, or using
`"Packages/ReactiveUIToolKit"` or a bare `"ReactiveUIToolKit/…"` segment, is never rewritten.

**Failure:** the stale capital-K path keeps working on Windows/macOS (case-insensitive) and breaks
on Linux/CI — so the defect surfaces days later on a build agent rather than on the developer's
machine, which is the worst possible place to find it. Neither document warns. Compounds M7.

### M9 — the codemod has zero tests and no CI wiring
**`SourceGenerator~/Tools/RuitkMigrateBrand/*`**

Referenced by nothing outside its own three source files: no test in `SourceGenerator~/Tests`, no
solution entry, no workflow. None of the ordering, idempotency, or boundary properties its own XML
docs assert is pinned by anything. **Failure:** a future edit to the composite table or the C2
regex silently regresses a tool that rewrites customers' source.

### M10 — the codemod's `bin/`+`obj/` build output is committed and ships to UPM users
**`SourceGenerator~/Tools/RuitkMigrateBrand/bin`, `/obj` (24 files)**

`.gitignore:15-18` covers only `SourceGenerator~/{bin,obj}` and `Tests/`, not `Tools/*/`. The
sibling tool carries its own `SourceGenerator~/Tools/UitkxMigrateImports/.gitignore`; this one
(whose csproj claims to be "modeled 1:1" on it) does not. The artifacts embed the maintainer's
absolute build path (the `wave-unity` worktree this wave was built in) and a SourceLink map pinned
to an intermediate rebrand commit. `SourceGenerator~` is not in `pathsToOmitFromDist`, so these are
published on the `dist` branch.

### M11 — the `dist` branch ships build intermediates **[pre-existing]**
**`.github/workflows/publish.yml:57-71` and `:127`**

The rsync into `dist_build` excludes only `.git/.vs/.github/dist_build/dist~` — not
`SourceGenerator~/bin|obj`, which the immediately preceding "build source generator DLLs" step
just created. And `cp -r dist_build/*` does not match dotfiles, so `.gitignore` never reaches
`_dist_worktree` and nothing filters them at `git add -A`. Verified live on `origin/dist`:
**23 tracked files** under `SourceGenerator~/bin|obj`, including `.pdb`s and an
`obj/…nuget.dgspec.json` embedding the CI runner's absolute paths. This also defeats the
`Analyzers/Ruitk.*.pdb` omit entries — the pdbs are stripped from `Analyzers/` and shipped anyway
from `SourceGenerator~/bin/Release/`.

### M12 — `pathsToOmitFromStore` is never applied to the dist/UPM channel **[pre-existing]**
**`.github/workflows/publish.yml:48-55` vs `:508-512`**

The dist job reads only `pathsToOmitFromDist`; the store job concatenates both lists.
`config.json:8-22` describes the store list as "anything a paying customer's Assets folder must
not contain". Verified live on `origin/dist`: `CLAUDE.md`, `AUTOMATION.md`, `RELEASE_OPS.md`,
`VERSIONING.md`, `publisher-secrets.example.json`, `Plans~/`, `automation~/`, `ide-extensions~/`
and the `.code-workspace` are all present at the dist root. Post-rebrand this is worse than
before, because `RELEASE_OPS.md` and `Plans~/` now carry the wrong marketplace identities from H2.

### M13 — CI never runs on the release branch **[pre-existing]**
**`.github/workflows/test.yml:4-7`** — triggers on `[dev, main]`; there is no `origin/main`, the
release branch is `origin/master`. **Failure:** this entire wave can merge to `master` with zero
CI signal — no SG suite, no LSP suite, no docs build, and no `Analyzers/*.dll` drift check.

### M14 — docs-site migration page drifts from `MIGRATION-0.12.md`
**`ReactiveUIToolkitDocs~/src/pages/Migration/MigrationPage.tsx:32-62`** — the site's "What
renamed" list omits the editor menu-root change and the license 1.1 change, both of which the
markdown guide carries. A user who only reads the site never learns the menu moved.

---

## LOW

| # | Location | Issue |
|---|---|---|
| L1 | `Diagnostics/Benchmark/BenchEditorHost.cs:350-351` | The *identifier* rule was applied to a **menu-path** string: the post-run hint says `Ruitk > Diagnostics > Benchmark > Results Viewer`, but the item is `[MenuItem("Reactive UI Toolkit/Diagnostics/Benchmark/Results Viewer")]` (`BenchResultsViewer.cs:13`). User-visible dead end. Same mis-swept text, XML-comments only (no runtime effect): `Editor/UitkxTestRunnerWindow.cs:18`, `Samples/Showcase/Editor/EditorUitkxCustomDrawDemoWindow.cs:18`. |
| L2 | `Editor/UitkxChangeWatcher.cs:25` | Doc comment claims `Samples/*.uitkx → Ruitk.Examples`; no such assembly — it is `Ruitk.Samples`. The sweep mechanically carried a pre-existing staleness forward instead of correcting it. |
| L3 | `ide-extensions~/vscode/readme-template.md:1`, `ide-extensions~/visual-studio/UitkxVsix/overview-template.md:1` | Marketplace page H1 reads `Reactive UI - Unity - VS Code (UITKX)` / `- VS2022 -` — a **third** brand variant, neither the old name nor `Reactive UI Toolkit`. The rebrand re-cased the body line directly beneath it and left the heading. Regenerated pages inherit it and `changelog.mjs verify` is green, so it will not self-correct. |
| L4 | `ReactiveUIToolkitDocs~/src/docs.tsx:453` | Licensing `searchContent` still says `reactiveui community license 1.0` — old license name *and* old version — while the page body (`LicensingPage.tsx:106,191`) was updated and `LICENSE.md` is 1.1. |
| L5 | `Editor/HMR/HmrCSharpEmitter.cs:252` | Namespace fallback is still `"UITKX.Generated"` while all seven siblings now say `Ruitk.Generated`. **[pre-existing]** — it read `UITKX.Generated` on `dev` too — but the rebrand renamed the other seven and left this one, and `HmrEmitterParityContractTests` does not cover it. |
| L6 | `ide-extensions~/rider/src/main/resources/META-INF/plugin.xml:4` | Dead `<vendor>ReactiveUITK</vendor>` literal; `build.gradle.kts:44` patches it at build time, so gradle wins — misleading, not broken. |
| L7 | `BrandMigrator.cs:35` | The bare-token regex has a right boundary but **no left boundary**, so any scanned text *ending* in `ReactiveUITK` is rewritten — including the frozen `UitkxVsix.ReactiveUITK`, the marketplace URL `itemName=ReactiveUITK.uitkx`, and the frozen formatter id, if they appear in a `.cs`/`.uitkx` file. User `.vscode/settings.json` escapes only because `.json` is not scanned — by accident, not design. |
| L8 | `RuitkMigrateBrand/Program.cs:39-40` | `args[0]` is unconditionally the directory, so the natural `--check Assets` ordering fails with "directory not found: …\--check". No `--help`. |
| L9 | `RuitkMigrateBrand/Program.cs:75-86` | The "never edits inside the package folder" guarantee (`CHANGELOG.md:38`, `MIGRATION-0.12.md:45`) is conditional: the skip test runs on the path *relative to the scan root*, so scanning the package folder directly rewrites it, and an embedded UPM install at `Packages/com.reactiveuitoolkit/` is not skipped at all. Harmless in 0.12 (already renamed → no-op), but the documented invariant is stronger than the code. |
| L10 | `MIGRATION-0.12.md:45-51`, `MigrationPage.tsx:75-81` | The .NET 8 SDK prerequisite is never stated; Unity ships no dotnet SDK, so a user without one gets `dotnet: command not found` with no explanation. |
| L11 | `config.json:31,33` | `Analyzers/publish` and `Analyzers/Ruitk.SourceGenerator.deps.json` can never match — `PublishGeneratorToAnalyzers` copies only `.dll`/`.pdb` + `System.Collections.Immutable.dll`. Faithfully renamed no-ops. Cosmetic, except `RELEASE_OPS.md:86` tells a human operator to hand-uncheck "PDBs, deps.json" based on this list. |
| L12 | `.github/workflows/publish.yml:653-672` vs `:141-147` | `build-unitypackage` and `deploy-dist` run in parallel (no `needs:`) and both can create the `v<ver>` tag. Today both resolve to the same `GITHUB_SHA` so the push is a harmless no-op, but the inline comment asserts an ordering ("deploy-dist finishes minutes before") that nothing enforces. |
| L13 | `Diagnostics/Benchmark/Results.meta`, `Diagnostics/Logs/Results.meta`, `Samples/Components/StressTest/components.meta`, `Samples/Showcase/Runtime/RuntimeUitkxDemo.meta` | Four orphaned `.meta` files with no corresponding asset. **[pre-existing]** — confirmed present on `dev`. |

---

## Verified clean — do not re-audit

Recorded so the next pass does not repeat this work.

- **Rename mechanics.** Git-index casing (596 new / 0 old, no case-collisions across 1052+ paths);
  all 12 renamed `.meta` GUIDs byte-identical to `dev`; **zero** `.meta` adds or deletes across the
  1293-file diff; 0 duplicate GUIDs; all 10 asmdef `"name"` fields match their filenames (`dev`'s
  real `ReactiveUITK.Examples.asmdef` / `"name": ReactiveUITK.Samples"` mismatch is now fixed).
- **Analyzers.** Only the two new DLLs + `System.Collections.Immutable`; the stale
  `ReactiveUITK.SourceGenerator.dll.old` pair is deleted; a UTF-16 string scan of both committed
  DLLs finds **0** `ReactiveUITK` occurrences — genuine rebuilds, no duplicate-generator risk.
- **Emission seams.** Every emitted literal resolves against a real declaration
  (`Ruitk.Core.VirtualNode/IProps/Ref<T>`, `Ruitk.Refresh.*`, `Ruitk.Signals.*`,
  `Ruitk.Props.Typed.*`, `Ruitk.Ugui.U`, `Ruitk.V`, `Ruitk.AssetHelpers`, …). SG↔HMR emitted
  using-blocks are byte-identical. All seven HMR reflection targets exist under exactly the named
  namespaces; `Ruitk.Language.dll` matches both the file on disk and
  `UitkxLanguage.csproj`'s `<AssemblyName>`. Every `InternalsVisibleTo` matches a real assembly.
- **Codemod core logic.** Replacement order is correct (composites → bare token → define → paths);
  idempotency verified (a second run is a genuine no-op); the `__ReactiveUITK_*` table is complete;
  `.meta`/binary/`Library`/`Temp`/`obj`/`bin` exclusions work; line endings are preserved.
- **Packaging.** Store path chain matches end to end (`STORE_DIR` ↔ `AssetStoreExport.PackageRoot`,
  both new casing); `-executeMethod Ruitk.CICD.AssetStoreExport.Run` resolves; every `config.json`
  omit path that can exist does exist at the exact casing; `test.yml` fully rebranded; all 18 repo
  URLs migrated; `changelog.mjs verify` green.
- **Consistency.** All 53 `[MenuItem]`s share the `Reactive UI Toolkit/` root and the only
  `ExecuteMenuItem` is a Unity built-in; `RUITK_HAS_TEST_FRAMEWORK` renamed on **both**
  `defineConstraints` and `versionDefines`; versions coherent (UPM 0.12.0, VS Code/VSIX 1.8.0,
  Rider 1.5.0, `changelog.json` 2026-07-28, all `[0.x]` headings dated); license reads 1.1
  everywhere that ships; no `link.xml`; no new TODOs.
- **Suites re-run at `b0da6f24`** (not inherited from an earlier commit): SG 1754/1754,
  LSP 152/152.

---

## Fix pass

Executed 2026-07-28 on `rebrand/umbrella` (from `b0da6f24`). Owner ruling: everything on
this branch, including the **[pre-existing]** entries. **Nothing committed** — the whole
fix pass is left uncommitted for supervisor diff review.

**All 33 findings fixed** (C1, H1–H5, M1–M14, L1–L13). Nothing deferred.

### Verification

| Gate | Result |
|---|---|
| `dotnet test SourceGenerator~/Tests` | **1822/1822** (was 1754; +68 new codemod tests) |
| `dotnet test ide-extensions~/lsp-server/Tests` | **152/152** |
| `node scripts/corpus-hash.mjs --check` | unchanged (`917dd8cd…52169`) |
| `node scripts/changelog.mjs verify` | OK — 2 generated pages match their templates |
| `ReactiveUIToolkitDocs~` `npm run build` | green (`tsc -b` + vite) |
| Codemod smoke on a scratch fixture | **30/30** byte-level assertions |
| Codemod exit codes | 0 clean / 1 `--check` dirty / 2 usage / 3 file errors — all confirmed |

### Disposition

- **C1** — `ide-extensions~/vscode/package.json:85` back to `"ReactiveUITK.uitkx"`. The
  shipped manifest now agrees with the three docs-site files the previous commit fixed.
- **H1 + M1** — the three-part mitigation as adjudicated. `ScanRules.IsSkipped` now skips
  `ReactiveUITK` *and* `Ruitk` segments, so the stale trigger keeps its old namespace and
  cannot collide (no CS0101). `Editor/UitkxAssetRegistrySync.cs` gained
  `WarnIfStaleRegistryExists()` (called from `FullRescan`, once per domain reload) naming the
  exact remedy. Both migration surfaces gained an explicit "delete `Assets/ReactiveUITK/`"
  step covering trigger **and** registry.
- **H2** — 24 identity strings restored across all six files
  (`vsce login ReactiveUITK`, `publishers/ReactiveUITK`, `UitkxVsix.ReactiveUITK`,
  `itemName=ReactiveUITK.*`, `ext install ReactiveUITK.uitkx`). Verified line-for-line
  against the `dev` baseline: **0** brand-token diff lines remain in those files vs `dev`.
- **H3** — both surfaces now document cloning `ruitk-unity` and running the tool from the
  clone against the user's own project path (the package-relative path is gone), plus the
  .NET 8 SDK prerequisite (**L10**).
- **H4** — new `Packages/manifest.json` upgrade step with the new git URL, on both surfaces.
- **H5 + M3** — per-file try/catch on read/migrate/write; a partial run reports every failure
  **and** the list of files it wrote, with recovery instructions, and exits 3. BOM detected
  and written back identically (UTF-8 + UTF-16 LE/BE); non-UTF-8 files are refused with a
  warning instead of being mojibaked.
- **M2** — menu-root rule `"ReactiveUITK/` → `"Reactive UI Toolkit/`, ordered **before** the
  bare token; documented in the verbatim-rules section.
- **M4** — scan extended to `.asmref` + `.rsp`; Project Settings scripting defines added as a
  documented manual step on both surfaces and in the changelog. `.json` is still deliberately
  unscanned (it holds the frozen formatter id) — now stated rather than accidental.
- **M5** — the default-namespace change (`Ruitk.Uitkx` / `Ruitk.FunctionStyle`) is disclosed
  in the CHANGELOG 0.12.0 entry and on both migration surfaces, with the
  runtime-vs-compile-time failure mode spelled out.
- **M6** — `config.json` backup warning on both surfaces.
- **M7 + M8** — rule D is now a boundaried bare-segment regex covering `Assets/`, `Assets\`,
  `Assets\\`, `Packages/` and bare `Path.Combine` segments, while leaving
  `ReactiveUIToolKitExtras` alone; the Linux consequence is documented on both surfaces.
- **M9** — `SourceGenerator~/Tests/BrandCodemodTests.cs`, 68 tests, running under the existing
  SG test csproj (tool sources linked the same way the sibling codemod's are).
- **M10** — 24 tracked `bin`/`obj` files untracked; tool-local `.gitignore` mirroring the
  sibling, plus repo-level `SourceGenerator~/Tools/*/{bin,obj}` coverage.
- **M11** — the dist rsync now excludes `SourceGenerator~` `bin`/`obj` (root, Tests, Tools);
  the dotfile-exclusion behaviour of `cp -r dist_build/*` is documented as deliberate, with
  the corollary that `git add -A` filters nothing.
- **M12** — the dist job now concatenates `pathsToOmitFromStore` with `pathsToOmitFromDist`
  (de-duplicated), matching the store job and `config.json`'s own description.
- **M13** — `test.yml` triggers are `[dev, master]`.
- **M14** — the docs-site "What renamed" list now carries the menu-root and license-1.1 items
  (plus the new namespace item), matching the markdown guide.
- **L1–L6** — menu-path hint and two XML comments corrected to `Reactive UI Toolkit`;
  `Ruitk.Examples` → `Ruitk.Samples`; both marketplace H1s → `Reactive UI Toolkit - …`
  (generated pages regenerated via `changelog.mjs`, never hand-edited); licensing
  `searchContent` → `reactive ui toolkit community license 1.1`; `"UITKX.Generated"` →
  `"Ruitk.Generated"`; Rider `<vendor>` synced to the value gradle patches in.
- **L7** — left word boundary added: `(?<![A-Za-z0-9_.])ReactiveUITK(?![A-Za-z_])`.
- **L8** — flags accepted in any position; `--help`/`-h`/`-?`; distinct usage exit code.
- **L9** — `com.reactiveuitoolkit` added to the skip list (embedded UPM); scanning a package
  folder directly now warns loudly.
- **L11** — the two never-matching `config.json` entries removed; the `RELEASE_OPS.md`
  operator instruction corrected (it names both omit lists and no longer mentions deps.json).
- **L12** — the comment now states that **no** ordering is enforced and why the race is benign,
  with a pointer to add `needs:` if the tag ever stops resolving to `GITHUB_SHA`.
- **L13** — the four orphaned `.meta` files deleted.

### Judgment calls (supervisor may want to overrule)

1. **New rule F — frozen-identity guard (beyond the literal L7 ask).** The left boundary alone
   does *not* save `ReactiveUITK.uitkx`: there the token is *followed* by `.uitkx`, and no
   boundary can separate it from the legitimate `ReactiveUITK.Runtime`. Since the pass
   criterion was "prove C1-class frozen IDs survive the codemod", the codemod now masks the
   frozen marketplace identities with a NUL-delimited sentinel before any rule runs and
   restores them afterwards. Masking is not counted as a replacement, so idempotency is
   unaffected.
2. **L12 resolved by correcting the comment, not by adding `needs:`.** The spec allowed either.
   No job in `publish.yml` declares `needs:`; serializing `build-unitypackage` behind
   `deploy-dist` would change release timing and couple the two jobs' skip conditions. The
   race is genuinely benign today, and the comment now says so explicitly.
3. **`cp -r dist_build/*` left dotfile-excluding.** Switching to `dist_build/.` would close the
   `.gitignore` gap but would newly publish `.claude/`, `.config/` and `.gitignore` to the dist
   branch. M11 is fully solved at the rsync layer instead, and the behaviour is now documented
   rather than accidental.
4. **L6 vendor synced to `Ruitk`, not `Reactive UI Toolkit`.** `build.gradle.kts:44` — a
   committed, already-reviewed rebrand decision — patches the vendor to `Ruitk` at build time
   and wins. The finding was that the dead literal is *misleading*, so it was aligned with the
   effective value rather than re-opening a marketplace-adjacent naming decision.
5. **`Analyzers/*.dll` reverted.** Running the SG suite re-triggers
   `PublishGeneratorToAnalyzers`, which rewrote both committed DLLs (identical size, new build
   metadata). No generator source changed in this pass, so both were restored to keep the
   review diff free of binary churn.
6. **`Program.cs` split into `ScanRules.cs` + `FileEncodings.cs`.** Required by M9: the test
   csproj cannot link a file containing `Main()` without an entry-point conflict, and the
   skip/encoding rules are exactly what needed pinning.

### Not done, deliberately

- **Nothing committed or pushed** — per instruction.
- **Frozen surfaces untouched:** marketplace identity fields in the real manifests
  (`publishManifest.json`, `vscode/package.json` `publisher`, `source.extension.vsixmanifest`
  `Identity Id`), `Plans~/archive`, `Plans~/REBRAND_PLAN.md`, and every changelog HISTORY body
  below the 0.12.0 entry.
- **Generated files regenerated, never hand-edited** (`vscode/README.md`,
  `UitkxVsix/overview.md`, via `scripts/changelog.mjs extract-overview`).
- **Unity-side compilation not run** — no Unity binary in this environment. The three touched
  editor files (`UitkxAssetRegistrySync.cs`, `UitkxChangeWatcher.cs`, `UitkxTestRunnerWindow.cs`)
  sit outside the SG/LSP test assemblies; the registry change is the only non-comment one and
  uses only already-imported `UnityEditor`/`UnityEngine` APIs.
