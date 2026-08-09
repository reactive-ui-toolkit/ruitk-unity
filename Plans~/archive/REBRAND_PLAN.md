# REBRAND PLAN v2 — Unity leg (`ReactiveUIToolKit` → org `reactive-ui-toolkit`, repo `ruitk-unity`)

**Status: EXECUTED — archived 2026-08-04.** The full breaking rename shipped as 0.12.0
(org `reactive-ui-toolkit`, repo `ruitk-unity`, identifier root `Ruitk`); the post-rebrand audit
ledger is `Plans~/archive/BUGS_FOUND_AFTER_RENAME.md`.

Original status: **PLANNED + FULLY RATIFIED — v2. All U-Q questions resolved (2026-07-28, §5);
census re-measured 2026-07-28 @ `dev` (`db2470a6`), tree clean. Blocked only on the §2
gates (org transfer + Godot-first ordering).**
**v2 supersedes v1 entirely.** v1 recommended keeping the `ReactiveUITK` identifier root; the
owner overruled on 2026-07-28: **"yes rename — to the same one Godot uses… no half-assing."**
So this leg is now a **FULL breaking rename**: namespace/assembly root **`ReactiveUITK` →
`Ruitk`** (the same prefix as the Godot leg's `Ruitk*` classes), install-folder casing
**`ReactiveUIToolKit` → `ReactiveUIToolkit`** (U-Q2, also ratified), clean break + codemod,
every deliverable bumps **one minor** (family versioning ruling, 2026-07-28).

**Authority:** family rebrand ruling (owner, 2026-07-27/28) — umbrella **Reactive UI Toolkit**,
GitHub org **`reactive-ui-toolkit`**, Scheme C slugs (`ruitk-unity`), transfer FIRST / rename
SECOND, marketplace extension identity AND display names frozen. Sibling plans:
Godot `plans/REBRAND_PLAN.md` v4 and Unreal `plans/REBRAND_PLAN.md`, each on
`docs/rebrand-plan` in its repo.

Written for a **lesser-model executor**. The contract in §1 is binding.

---

## 1. Executor contract (binding)

1. **Never free-lance a sweep.** Only the replacements listed here. A grep hit not accounted
   for by a step or by §10's expected-leftovers table = **STOP and report**.
2. **Exact strings.** OLD not found exactly where stated → STOP (tree drifted).
3. **Regexes are normative** — do not widen them. `ReactiveUITK(?![A-Za-z_])` is NOT the same
   as a bare substring replace; composites like `ReactiveUITKConfig` are handled by their own
   rule (§7.C1).
4. **Unity `.meta` pairing:** every `git mv` of a tracked file/folder MUST move its `.meta`
   sibling in the same commit. A dangling `.meta` (or a renamed asset with a stale meta) makes
   Unity re-generate GUIDs and silently breaks references. (`~`-suffixed folders are
   unimported — files inside them have no metas.)
5. **Case-only renames** (`ReactiveUIToolKitDocs~` → `ReactiveUIToolkitDocs~`) on Windows'
   case-insensitive filesystem MUST be two-step: `git mv X X__tmp && git mv X__tmp Y`.
6. **Tier 3 frozen:** `Plans~/archive/**`, `CHANGELOG.md` bodies below the new wave section,
   `plans/DISCORD_CHANGELOG.md` old post bodies, git history. Live URLs inside them still
   update (§7.A). Everything else in `Plans~/` is live planning doc — it converts.
7. **Identity-frozen fields** (family ruling — these keep the literal string `ReactiveUITK`
   forever; every sweep must EXCLUDE the exact locations enumerated in §7.E1).
8. **[DOTNET] steps** need the .NET SDK (10.x for SG tests, 8.x for LSP). **[UNITY]** steps
   need Unity `6000.2.6f1` — if unavailable, finish everything else and report them pending.
9. **Branch flow (house rule):** feature branch `rebrand/umbrella` off `dev`; push the branch
   ONLY; owner PRs → dev → checks → merge. No `Co-Authored-By` trailers.

---

## 2. Gates

| Gate | What | Who |
|---|---|---|
| G1 | Org `reactive-ui-toolkit` exists; repo transferred + renamed `ruitk-unity` (issues/PRs/stars/secrets survive; git+web URLs redirect; never reuse the freed name) | OWNER |
| G2 | Godot leg executed first (family ordering) | OWNER |
| G3 | ~~U-Q3 sequencing~~ — RESOLVED, no gate (0.11.0 already shipped; §5) | — |
| G4 | CI secrets present post-transfer (`UNITY_EMAIL`/`UNITY_PASSWORD`, marketplace PATs) | OWNER |

---

## 3. Name Registry (U-N1 … U-N18)

| # | Thing | OLD | NEW |
|---|---|---|---|
| U-N1 | GitHub owner/org | `yanivkalfa` | `reactive-ui-toolkit` |
| U-N2 | Repo slug | `ReactiveUIToolKit` | `ruitk-unity` |
| U-N3 | Product display name | `Reactive UIToolKit` (the broken UPM displayName) / prose variants | `Reactive UI Toolkit — Unity` (final family pattern per Godot R2 batch; if R2 lands differently, match it) |
| U-N4 | UPM package id | `com.reactiveuitoolkit` | **UNCHANGED** (package identity = the marketplace-ID analog; renaming breaks every user's upgrade path, and it already spells the umbrella) |
| U-N5 | **Namespace/assembly root (RATIFIED 2026-07-28)** | `ReactiveUITK` — 3,579 occ / 748 text files (589 `namespace` decls, 1,041 `using` lines, 245 `global::` refs) | **`Ruitk`** (`namespace Ruitk.Core`, `using Ruitk;`, `global::Ruitk.Core.VirtualNode`) |
| U-N6 | Composite identifiers | `ReactiveUITKConfig` (+ anything §7.C1's enumeration surfaces) | `RuitkConfig` |
| U-N7 | The 10 asmdefs | `ReactiveUITK.{CICD.Editor, Diagnostics.Benchmark.Editor, Diagnostics.Logs.Editor, Diagnostics, Editor, Runtime, Samples, Shared, Ugui, Ugui.Tests}` | `Ruitk.{same suffixes}` — file renames §7.C4, incl. fixing the pre-existing mismatch where file `ReactiveUITK.Examples.asmdef` carries name `ReactiveUITK.Samples` (new file: `Ruitk.Samples.asmdef`) |
| U-N8 | Committed analyzer DLLs | `Analyzers/ReactiveUITK.Language.dll`, `Analyzers/ReactiveUITK.SourceGenerator.dll` (+ tracked stale `….SourceGenerator.dll.old` — bug) | `Ruitk.Language.dll`, `Ruitk.SourceGenerator.dll`, rebuilt via `scripts/build-generator.ps1`; the `.old` artifact is **deleted** |
| U-N9 | Test-framework define | `REACTIVEUITK_HAS_TEST_FRAMEWORK` (2 sites in `Ugui/Tests/….asmdef`) | `RUITK_HAS_TEST_FRAMEWORK` |
| U-N10 | Editor menu root + titles | `MenuItem("ReactiveUITK/…` (53 items), window title `"ReactiveUITK Bench"` | `MenuItem("Reactive UI Toolkit/…`, `"Reactive UI Toolkit Bench"` (display surfaces follow U-N3, not the code prefix) |
| U-N11 | **Install folder casing (U-Q2, RATIFIED)** | `Assets/ReactiveUIToolKit` | `Assets/ReactiveUIToolkit` — with the **Linux dup-folder note** in MIGRATION (Windows/macOS merge case-insensitively; Linux upgraders get BOTH folders and must delete the old one) |
| U-N12 | Docs folder + workspace file | `ReactiveUIToolKitDocs~/`, `ReactiveUIToolKit.code-workspace` | `ReactiveUIToolkitDocs~/`, `ReactiveUIToolkit.code-workspace` (casing follows U-N11's token) |
| U-N13 | Extension identities | vscode `name:"uitkx"` / `publisher:"ReactiveUITK"` / `UITKX (Unity - VS Code)`; vsix `Id="UitkxVsix.ReactiveUITK"` / `Publisher="Yaniv Kalfa"` / `UITKX (Unity - VS2022)`; rider `pluginId=com.reactiveuitk.uitkx` / `pluginName=UITKX`; publishManifest `internalName:"uitkx-visualstudio"` | **ALL UNCHANGED** (family ruling; exact frozen list §7.E1) |
| U-N14 | Docs domain | `reactiveuitoolkit.info` (+ `public/CNAME`) | **UNCHANGED** — custom domain; verified: NO vite `base`, NO router basename exist, and Pages deploys from the `documentations` branch. Nothing Pages-related changes on transfer. |
| U-N15 | Wave versions (RATIFIED: +1 minor each) | UPM `0.11.0` · vscode `1.7.0` · vs2022 `1.7.0` · rider `1.4.0` | UPM **0.12.0** (BREAKING) · vscode **1.8.0** · vs2022 **1.8.0** · rider **1.5.0** |
| U-N16 | Codemod + guide | (precedent: `SourceGenerator~/Tools/UitkxMigrateImports`) | `SourceGenerator~/Tools/RuitkMigrateBrand` + `MIGRATION-0.12.md` |
| U-N17 | `RUITK` all-caps abbreviation (3 code sites: Bench viewer title + pref key, demo window title) | `RUITK` | **UNCHANGED** — it abbreviates the NEW name (`Ruitk` uppercased) exactly as well |
| U-N18 | Store package asset | `ReactiveUIToolKit-<ver>.unitypackage` | `ReactiveUIToolkit-<ver>.unitypackage` (falls out of the U-N11 token sweep in `publish.yml`) |

---

## 4. Census (2026-07-28, `dev` @ `db2470a6`, clean tree)

Verification anchors — §7.0 re-runs these and STOPs on drift:

```bash
git grep -oI "ReactiveUITK" | wc -l            # 3579  (748 files; +3 binary DLL matches)
git grep -oI "namespace ReactiveUITK" | wc -l  #  589
git grep -oI "using ReactiveUITK" | wc -l      # 1041
git grep -oI "global::ReactiveUITK" | wc -l    #  245
git grep -oI "Ruitk" | wc -l                   #    0   ← collision check
git grep -oI "ReactiveUIToolKit" | wc -l       #  704  (99 files; capital K)
git grep -oI "ReactiveUIToolkit" | wc -l       #    0   ← lowercase-k target is free
git grep -nIi "yanivkalfa" | wc -l             #    8   (lines; 10 occ / 5 files)
```

| Surface | Facts | Disposition |
|---|---|---|
| Area breakdown of the 3,579 | Plans~ 999 · SourceGenerator~ 645 (Tests 447) · Shared 528 · Samples 384 · ide-extensions~ 300 · Docs 293 · Editor 188 · Ugui 87 · Diagnostics 58 · root 50 · rest <15 each | §7.C |
| Roslyn **string-typed seams** (fail SILENTLY if missed — the sweep covers them, the battery proves them) | `GetTypeByMetadataName("ReactiveUITK.Core.VirtualNode")` ×3 (`UitkxPipeline.cs:124,174,288`) · `PropsResolver.cs:45-46` `VTypeName`/`UTypeName` · `UitkxHmrSwapWriteAnalyzer.cs:28` `AttributeFullName` · diagnostic categories (`"ReactiveUITK.Parser"` etc.) · `AutoInjectedUsings.cs:25-29` · `RoslynHost.cs:115,1234-1236` · the identical emitted-`using` block duplicated in 4 emitters · `"ReactiveUITK.Generated"` fallback ×4 | §7.C2 |
| `InternalsVisibleTo` | 7 live sites (Shared ×3, Runtime, Ugui, SourceGenerator~, lsp-server csproj) | §7.C2 |
| MenuItems | 54 total; 53 under `ReactiveUITK/` | §7.B5 |
| EditorPrefs | `"ReactiveUITK.UitkxNavVerbose"` (1 key — swept; users' saved value resets, cosmetic); `RUITK_*`/`UITKX_HMR_*` keys stay | §7.C2 |
| Folder-token hotspots (the 704) | `Plans~/codebase-index.json` 462 (U-Q4) · `publish.yml` 10 · 22 C# path-literal lines (`AssetStoreExport.cs` `PackageRoot`, `PublishUtility.cs`, `UitkxHmrCompiler.cs:3323-3345`, `ReactiveUITKConfig.cs:76`, `UitkxTestRunnerWindow.cs:287-288`…) · `.gitignore:8-10` · `config.json:20,28` · README ×6 | §7.D |
| Wrong-org / placeholder URLs (bugs) | `github.com/ReactiveUITK/ReactiveUIToolKit` ×3 (vscode `package.json:11`, vsix manifest `:12` MoreInfo, rider `build.gradle.kts:45`) · `github.com/your-org/…` ×3 (rider README:26, plugin.xml:4, VS README:20) · `RELEASE_OPS.md:72` `<org>` | §7.A |
| `yanivkalfa` | 10 occ / 5 files: **4 live URLs** (TopBar.tsx:52 clone URL, LicensingPage.tsx:102/110 blob links, `UitkxGettingStartedPage.example.ts:1` — the `#dist` install URL users paste into Package Manager) + **6 mailto/prose** (stay — person, not URL) | §7.A |
| **Family corpus** (`ide-extensions~/lsp-server/test-fixtures/uitkx-scanner-cases.json`) | familyCore = `[skipNoncodeMarkup, findMatchingMarkup, fileScan]`; **0 `ReactiveUITK`, 0 `VirtualNode`, 0 `Ruitk` in ANY tier**; Unity's perLeg sections are empty arrays; `fileScan`'s 11 `UITKX` tokens are the frozen language brand | §7.F STOP-gate |
| Suites | SG dotnet suite (changelog: 1754/1754; 1,230 `[Fact]`/`[Theory]` attrs) · LSP dotnet suite (152/152; 139 attrs) · Ugui Unity tests (12 attrs) | §8 |
| Defects found by census, fixed in this wave | broken UPM displayName · wrong-org URLs ×3 + placeholders ×4 · stale tracked `.dll.old` · asmdef file-vs-name mismatch · vs2022 `CHANGELOG.md` stuck at 1.6.0 while its manifest is 1.7.0 · `CLAUDE.md` "currently `0.6.x`" stale | §7.B/C/E + §9 |

---

## 5. Open questions — ALL RESOLVED (owner, 2026-07-28)

- **U-Q3 — RESOLVED (moot): 0.11.0 IS released and published** (GitHub release
  `ReactiveUIToolKit 0.11.0`, tag `v0.11.0` @ `899c938`, `.unitypackage` attached,
  2026-07-26). The wave ships as a clean "0.12.0 = the rename, nothing else".
  **Stale-heading bug found while verifying:** `CHANGELOG.md` and
  `plans/DISCORD_CHANGELOG.md` on `dev` still head their top sections
  `## [0.11.0] - Unreleased` — date both to the actual release date as a §9 housekeeping
  step (or a separate hotfix before the wave).
- **U-Q4 — RESOLVED: sweep `Plans~/codebase-index.json`** with everything else (it indexes
  the post-rename tree; regenerate instead only if its generator turns up during execution).
- **Family license ruling (UE-Q5, applies to ALL THREE repos):** the license document itself
  renames + version-bumps — `ReactiveUI Community License 1.0` → **`Reactive UI Toolkit
  Community License 1.1`** — product references inside updated, credit-line clause → `Made
  with Reactive UI Toolkit`, legal terms otherwise unchanged, copyright holder stays;
  licensees under 1.0 keep their 1.0 terms. For THIS repo: retitle `LICENSE.md`, matching
  labels in `LICENSE-COMMERCIAL.md`, and the docs Licensing page copy (§7.B4 executes it).

---

## 6. Phases

| Phase | What | Who |
|---|---|---|
| 0 | Preflight §7.0 | executor |
| 1 | Org transfer + rename → `ruitk-unity` (G1) | OWNER |
| 2 | Branch `rebrand/umbrella` off post-transfer `dev` | executor |
| 3 | Groups A–H (§7, in order) | executor |
| 4 | Battery §8 | executor + [DOTNET]/[UNITY] |
| 5 | Release wave §9 | executor prepares, OWNER merges + tags |

## 7. Phase 3 — the work

### 7.0 Preflight
`git status --short` empty → run every §4 anchor → STOP on drift → record the corpus hash
(`node scripts/corpus-hash.mjs` output) as the §7.F baseline.

### 7.A Group A — URL swap (post-transfer; touches Tier-3 only for URLs)

| OLD | NEW |
|---|---|
| `https://github.com/yanivkalfa/ReactiveUIToolKit` (incl. `.git`, `#dist`, `/blob/master/…` forms) | `https://github.com/reactive-ui-toolkit/ruitk-unity` (suffix preserved) |
| `https://github.com/ReactiveUITK/ReactiveUIToolKit` (wrong-org bug ×3) | same NEW value |
| `https://github.com/your-org/ReactiveUIToolKit` (placeholder ×3) | same NEW value |
| `RELEASE_OPS.md:72` `https://github.com/<org>/ReactiveUIToolKit/actions` | `https://github.com/reactive-ui-toolkit/ruitk-unity/actions` |

Files: `ReactiveUIToolKitDocs~/src/components/TopBar/TopBar.tsx:52` ·
`…/pages/Licensing/LicensingPage.tsx:102,110` ·
`…/pages/UITKX/GettingStarted/UitkxGettingStartedPage.example.ts:1` ·
`ide-extensions~/vscode/package.json:11` ·
`ide-extensions~/visual-studio/UitkxVsix/source.extension.vsixmanifest:12` ·
`ide-extensions~/rider/build.gradle.kts:45` · `ide-extensions~/rider/README.md:26` ·
rider `plugin.xml:4` (vendor `url` attribute ONLY — the vendor TEXT is display-frozen) ·
`ide-extensions~/visual-studio/README.md:20` · `RELEASE_OPS.md:72`.
**Mailtos and person attributions stay** (`yanivkalfa@gmail.com` ×6, `Publisher="Yaniv Kalfa"`).
Then `git remote set-url origin https://github.com/reactive-ui-toolkit/ruitk-unity.git`.
**Verify:** `git grep -ni "yanivkalfa"` → only the 6 mailto/prose hits;
`git grep -n "your-org\|github.com/ReactiveUITK"` → 0.

### 7.B Group B — display strings

1. `package.json` line 3: OLD `"displayName": "Reactive UIToolKit",` →
   NEW `"displayName": "Reactive UI Toolkit — Unity",` (the broken string dies here).
2. `.github/ISSUE_TEMPLATE/bug_report.yml:2` description → U-N3 phrasing.
3. `README.md` H1 + product-name prose → U-N3 on first mention, "the toolkit" after.
4. Licenses — **the full license-1.1 rewrite (family ruling, §5):** retitle `LICENSE.md`
   `ReactiveUI Community License 1.0` → `Reactive UI Toolkit Community License 1.1`, version
   refs `1.0` → `1.1` throughout, product references + credit-line clause updated, terms
   otherwise unchanged; matching labels in `LICENSE-COMMERCIAL.md` + `THIRDPARTY.md` + the
   docs Licensing page. Ambiguous hit → STOP-list it.
5. MenuItem root sweep: `"ReactiveUITK/` → `"Reactive UI Toolkit/` — exactly 53 sites, all
   inside `MenuItem(`/`GetWindow` attribute strings; verify the count before and after.
6. `Diagnostics/Benchmark/BenchEditorHost.cs:29` `"ReactiveUITK Bench"` →
   `"Reactive UI Toolkit Bench"`. (`"RUITK Bench Viewer"` / `"RUITK Editor Controls"` and the
   `RUITK_BenchViewer_LastRunFolder` pref stay — U-N17.)
7. `CLAUDE.md` stale "currently `0.6.x`" → the wave version.
8. Docs-site titles/nav (`TopBar`, landing hero, `<title>`) → U-N3.

### 7.C Group C — THE identifier rename (`ReactiveUITK` → `Ruitk`)

Ordered rules, repo-wide over tracked text files, EXCLUDING `Plans~/archive/**`,
CHANGELOG/Discord old bodies, and the §7.E1 identity-frozen lines.

- **C1 — composites (enumerate-then-map):** `git grep -ohI "ReactiveUITK[A-Za-z_]\+" | sort -u`.
  Expected: `ReactiveUITKConfig` → `RuitkConfig`. Any OTHER token → STOP, add it to this table
  first. Then `git mv Shared/Core/Config/ReactiveUITKConfig.cs Shared/Core/Config/RuitkConfig.cs`
  (+ its `.meta`).
- **C2 — bare token:** regex `ReactiveUITK(?![A-Za-z_])` → `Ruitk`. Converts: 589 `namespace`
  decls, 1,041 `using`s, 245 `global::` refs, asmdef `name`+`references`, 7 `InternalsVisibleTo`,
  every Roslyn seam in §4, the EditorPrefs key, diagnostic categories, csproj
  `AssemblyName`/`RootNamespace`, emitted-`using` blocks, `"ReactiveUITK.Generated"` fallbacks,
  `.csharpierignore` prose, changelog.json/extension prose, and SG test expectation strings.
- **C3 — define:** `REACTIVEUITK_HAS_TEST_FRAMEWORK` → `RUITK_HAS_TEST_FRAMEWORK`
  (2 sites: the Ugui.Tests asmdef, lines 24 + 30).
- **C4 — file renames (`git mv`, `.meta` rule 4):** the 10 asmdefs `ReactiveUITK.X.asmdef` →
  `Ruitk.X.asmdef` in place — Samples specifically
  `git mv Samples/ReactiveUITK.Examples.asmdef Samples/Ruitk.Samples.asmdef` (finally matching
  its `name` field); `SourceGenerator~/ReactiveUITK.SourceGenerator.csproj` →
  `Ruitk.SourceGenerator.csproj`; `SourceGenerator~/Tests/ReactiveUITK.SourceGenerator.Tests.csproj`
  → `Ruitk.SourceGenerator.Tests.csproj` (no metas inside `~` folders). Then fix every path
  reference to the renamed files: the Tests csproj `ProjectReference`,
  `scripts/build-generator.ps1` (`$csproj`, `$dll`), `.github/workflows/test.yml:53-64` `cmp`
  paths, `publish.yml:44-46`, `config.json` `pathsToOmitFromDist` (three `Analyzers/ReactiveUITK.*`
  entries), CLAUDE.md/skills command lines. Locator: after C2,
  `git grep -n "ReactiveUITK\." -- "*.yml" "*.ps1" "*.json" "*.csproj" "*.md"` must be 0.
- **C5 — DLL artifacts [DOTNET]:**
  (a) `git rm Analyzers/ReactiveUITK.SourceGenerator.dll.old Analyzers/ReactiveUITK.SourceGenerator.dll.old.meta`
  (stale tracked artifact — bug dies).
  (b) Preserve GUIDs by renaming the metas FIRST:
  `git mv Analyzers/ReactiveUITK.Language.dll.meta Analyzers/Ruitk.Language.dll.meta` (guid
  `25c4db1d…` survives) and likewise `….SourceGenerator.dll.meta` (guid `9f4e2c1a…`); `git rm`
  the two old DLL binaries.
  (c) Run `scripts/build-generator.ps1` → publishes `Ruitk.SourceGenerator.dll` +
  `Ruitk.Language.dll` (+ unchanged `System.Collections.Immutable.dll`) into `Analyzers/`;
  `git add` them. Verify `Analyzers/` = exactly 3 DLLs + 3 metas + `.gitkeep`.
**Verify Group C:** `git grep -cI "ReactiveUITK"` → only §7.E1 frozen lines + Tier-3;
`git grep -oI "Ruitk" | wc -l` ≈ 3,579 minus the frozen/Tier-3 remainder.

### 7.D Group D — folder casing (`ReactiveUIToolKit` → `ReactiveUIToolkit`, U-Q2)

1. Token sweep `ReactiveUIToolKit` → `ReactiveUIToolkit`, same exclusions. Rewrites:
   `publish.yml` `STORE_DIR`/`OUT_FILE`/artifact-name/`--title` lines
   (`:450,492,606,633,645,663,668`) + docs-path lines (`:165,169,183`), `test.yml:146`,
   `CICD/Editor/AssetStoreExport.cs:13,29,40` (`PackageRoot` + default out filename),
   `CICD/Editor/PublishUtility.cs` `Path.Combine(Application.dataPath, …)` sites, the rest of
   the 22 C# path literals (§4), `Shared/Core/Config/RuitkConfig.cs:76`, `.gitignore:8-10`,
   `config.json:20,28`, `README.md`, docs prose, `Plans~/codebase-index.json` (U-Q4).
2. Case-only `git mv` (two-step, rule 5): `ReactiveUIToolKitDocs~` → `ReactiveUIToolkitDocs~`;
   `ReactiveUIToolKit.code-workspace` (+ `.meta`) → `ReactiveUIToolkit.code-workspace`.
3. The Linux both-folders upgrade note goes into `MIGRATION-0.12.md` (§7.G).
**Verify:** `git grep -cI "ReactiveUIToolKit"` → only Tier-3;
`git ls-files | grep "ReactiveUIToolK"` → 0.

### 7.E Group E — extensions: identity frozen, content converted

- **E1 — the frozen lines (sweep exclusions; verify byte-identical at the end):**
  `ide-extensions~/vscode/package.json` → `"name": "uitkx"`, `"publisher": "ReactiveUITK"`,
  `"displayName": "UITKX (Unity - VS Code)"`;
  `ide-extensions~/visual-studio/UitkxVsix/source.extension.vsixmanifest` →
  `Id="UitkxVsix.ReactiveUITK"`, `Publisher="Yaniv Kalfa"`,
  `<DisplayName>UITKX (Unity - VS2022)</DisplayName>`;
  `ide-extensions~/visual-studio/UitkxVsix/publishManifest.json` →
  `"publisher": "ReactiveUITK"`, `"internalName": "uitkx-visualstudio"`;
  `ide-extensions~/rider/gradle.properties` → `pluginId = com.reactiveuitk.uitkx`,
  `pluginName = UITKX`; rider `plugin.xml` vendor element TEXT `ReactiveUITK`.
- E2 — the other ~290 `ide-extensions~` occurrences convert via Groups A/C. Searchable
  metadata is NOT identity: vsix `<Tags>` token `ReactiveUI` → `Reactive UI Toolkit`; vscode
  `keywords` likewise if brand-bearing.
- E3 — the stale vs2022 changelog (stuck at 1.6.0 while its manifest is 1.7.0 — pre-existing
  bug) gets regenerated in §9 when the new `changelog.json` entry lands.

### 7.F Group F — corpus + generated-output expectations

1. `node scripts/corpus-hash.mjs --check` → **MUST pass UNCHANGED** vs the §7.0 baseline.
   familyCore holds 0 brand tokens (verified 2026-07-28) — drift means a sweep leaked into a
   familyCore section of `uitkx-scanner-cases.json`: revert that hunk; NEVER re-pin.
2. SG snapshot/golden tests embed generated code (`global::ReactiveUITK…`) — C2 rewrote both
   emitters and expectations; `dotnet test` (§8) is the proof. A snapshot that regenerates
   rather than string-matches → regenerate per that suite's convention.

### 7.G Group G — codemod + migration doc

1. `SourceGenerator~/Tools/RuitkMigrateBrand/` (net8 console exe, modeled 1:1 on
   `Tools/UitkxMigrateImports`): rewrites a USER project — `using`/`namespace`/`global::`
   `ReactiveUITK` → `Ruitk` (C1+C2 rules verbatim), user asmdef `references` `ReactiveUITK.*`
   → `Ruitk.*`, the U-N9 define, and `Assets/ReactiveUIToolKit` path strings in user code.
   Idempotent (second run reports 0); prints per-file counts; never edits inside the package
   folder itself.
2. `MIGRATION-0.12.md` at repo root: delete-old-folder instruction (with the Linux
   both-folders callout), the codemod command, the "EditorPrefs value resets" cosmetic note,
   the C1/C2 rules verbatim for hand-migration, the U-N15 version table.
3. `CHANGELOG.md`: new `## [0.12.0]` BREAKING section on top; ALSO date the stale
   `## [0.11.0] - Unreleased` headings (CHANGELOG + DISCORD) to the actual 0.11.0 release
   date — §5 U-Q3 bug. Old bodies otherwise frozen.

### 7.H Group H — expected-leftovers audit (defines DONE)

Run every §10 grep; any unexplained hit = STOP, report, never silently fix.

## 8. Phase 4 — battery

```bash
node scripts/corpus-hash.mjs --check                                            # UNCHANGED
dotnet test SourceGenerator~/Tests/Ruitk.SourceGenerator.Tests.csproj           # [DOTNET] full SG suite green (was 1754/1754)
dotnet test ide-extensions~/lsp-server/Tests/UitkxLanguageServer.Tests.csproj   # [DOTNET] 152/152
cd ide-extensions~/vscode && npm ci && npm run build && npx vsce package --no-dependencies -o test.vsix
cd ReactiveUIToolkitDocs~ && npm ci && npm run build                            # renamed folder
```
**[UNITY]** open in `6000.2.6f1`: compiles clean, the `Reactive UI Toolkit` menu appears, Ugui
play-mode tests (12) green, HMR window opens, generator emits `global::Ruitk.*` (inspect one
generated file). The store-packaging job stays inert (secret-gated) — its next armed run
produces `ReactiveUIToolkit-0.12.0.unitypackage`.

## 9. Phase 5 — release wave (owner merges first)

Per U-N15: `package.json` → `0.12.0`; vscode `package.json` → `1.8.0`; vsix `Identity Version`
→ `1.8.0`; rider `gradle.properties` → `1.5.0`; new `ide-extensions~/changelog.json` entry
`{vscode:1.8.0, vs2022:1.8.0, rider:1.5.0}` → run the extract scripts (also fixes the stale
vs2022 CHANGELOG); CHANGELOG `0.12.0` section; `plans/DISCORD_CHANGELOG.md` new post.
Owner: PR → dev → checks → merge → tags `v0.12.0` / `vscode-v1.8.0` / `vs2022-v1.8.0`
(+ `store-v0.12.0` when the store job is armed).

## 10. Expected leftovers (the ONLY permitted survivors)

| Grep | Allowed |
|---|---|
| `ReactiveUITK` | §7.E1 frozen identity lines · rider vendor display text · `Plans~/archive/**` · CHANGELOG/Discord old bodies · MIGRATION/codemod OLD columns |
| `ReactiveUIToolKit` (capital K) | Tier-3 bodies · MIGRATION OLD column |
| `RUITK` | the 3 U-N17 sites (+ archive) |
| `yanivkalfa` | the 6 mailto/person-attribution hits (people keep their names) |
| `reactiveuitoolkit` (lowercase) | UPM id `com.reactiveuitoolkit` · domain `reactiveuitoolkit.info` + CNAME · rider `com.reactiveuitk.uitkx` |

## 11. Rollback

Pre-merge: delete `rebrand/umbrella`. Post-merge: revert the merge on dev. Transfers stay
(redirects hold). The rebuilt DLLs and renamed metas revert with the branch — GUIDs were
preserved, so Unity references survive in either direction.
