# ES-modules campaign — audit-wave findings (2026-07-18)

Consolidated output of the four-agent deep audit run after M0-M9 (branch `feat/es-modules`).
Agents: plan-compliance (PC), parser/language-lib bug hunt (F/P — every finding execution-verified),
emitter/HMR parity trace (H/M/L), codemod + LSP + samples hunt (A/B — codemod findings
execution-verified against repro corpora).

Status legend: **FIXED** (this wave, test-pinned) / **DEFERRED** (recorded in
`Plans~/REMAINING_WORK.md` with trigger) / **OWNER** (needs an owner decision — surfaced in the
audit report) / **DUP** (same defect as another entry).

## 1. Parser / language-lib (execution-verified)

| ID | Sev | Finding | Status |
|---|---|---|---|
| F1 | MAJOR | `ParseComponentBodyAt` exits (`DirectiveParser.cs` ~:851/:913) never resync `line` after a component body — every later declaration in the file gets a wrong `DeclarationLine` (wrong 2320/2100/2321/2322 anchors; the `--es-modules` codemod edits the wrong line). Every sibling path resyncs. | FIXED |
| F2 | MAJOR | Formatter sorts plain-decl entries with unstable `List.Sort` by `DeclarationLine`; with F1 a same-line tie reorders declarations (`B()` emitted before component `A`). | FIXED (F1 + stable `OrderBy`) |
| F3 | MAJOR | Empty / imports-only file: `TryParsePlainDeclarationsFile` commits with zero declarations, and Format fabricates `component Component { return ( ); }` out of thin air (baseline produced UITKX2105). | FIXED (parse requires >=1 declaration → baseline behavior; formatter zero-decl guard as defense) |
| F4 | MAJOR | `StrictImportDetector.Detect` `selfNames` misses `MemberDeclarations` — a file's OWN new-mode hooks get false 2307/2305. | FIXED |
| F5 | MAJOR | `Detect`'s `imported` set misses `DefaultAlias`/`StarAlias`/bound aliases — default-imported component used as tag → false 2305 at error tier. | FIXED |
| F6 | MAJOR | `DetectUnusedImports` checks the ORIGINAL name, not the bound alias — `import { Widget as W }` + `<W/>` → false 2304. | FIXED |
| F7 | MAJOR | 2109 gate (`StrictImportDetector.cs` ~:274) covers `IsStar\|\|IsDefault` only — `as`-rename from a legacy target: no diagnostic, and `ImportScopeFacts` legacy branch emits payloads keyed on original names → alias never binds → raw CS0103 at build. Comment at :291-297 falsely claims renames are covered. (= PC-1, matrix §6 row 5.) | FIXED (gate + legacy-branch payload suppression for renamed names) |
| F8 | MINOR | Format silently adds inline `export` to a declaration exported ONLY via `export default` (parser marks it IsExported; `ExportPrefix` only suppresses list-exported names). | FIXED (parser tracks default-only exports; formatter suppresses) |
| F9 | MINOR | Generic declaration (`export T Identity<T>(…)`) as first decl → whole file dies with misleading line-1 2105; legacy generic hooks have no plain migration target (no `MemberDeclaration.GenericParams`). Spec gap. | FIXED (targeted diagnostic at the right line; FULL generic support in the plain dialect is a family-grammar decision — flagged to owner) |
| F10 | MINOR | Multi-line heads: `nameLine` stays on the head's first line while `NameColumn` is absolute → 2321/2100/2322 squiggles land mid-tuple on the wrong line. | FIXED (head reader resyncs `line` after tuple/generic type scans) |
| F11 | MINOR | Default import from a legacy target double-reports 2109 + 2326 on the same line. | FIXED (2326 suppressed when 2109 fired) |
| F12 | MINOR | `MemberDeclaration.Params` garbles `ref`/`out`/`params` modifiers (`params object[] xs` → type=`params`); raw `ParamsText` is correct. Pre-existing param-parser limitation, newly consumer-visible. | FIXED (modifiers parse into the type text; SG + shared bridge renderers forward `ref`/`out` at call sites) |
| P1 | pre-existing | `hook 123 {}` → NRE in `DirectiveParser.Parse` (`TryParseHookModuleFile` returns false without assigning `ref directiveSet`). Present on baseline too. | FIXED (cheap guard) |
| P2 | pre-existing | Formatting a legacy mixed file (component + tail hook) drops the component (dispatch routes to `FormatHookModuleFile`). Present on baseline. | FIXED (data-loss guard: mixed legacy files format untouched, same precedent as ContainsInlineJsxControlFlow) |

## 2. Emitters / HMR / VDG parity (traced)

| ID | Sev | Finding | Status |
|---|---|---|---|
| H1 | HIGH | HMR hot unit for a new-mode component references the PROJECT assembly's `__Exports`; non-exported members and bridges are `internal` → cross-assembly CS0122 on every hot edit of the mainline pattern (component + private `const` style in one file). Legacy avoided this by inlining companion sources into the hot unit. | FIXED (the file's own `__Exports` — members + bridges — is emitted INTO the component hot unit, the legacy companion-inlining precedent; contract-pinned; live verification lands in the owner's M8 battery) |
| H2 | HIGH | Dotted tags with children are a parse error: `ReadTagName` accepts one `.` but the CLOSING-tag scans (`UitkxParser.cs` ~:1581, ~:836, stop-tag check ~:506) do not — `<X.Comp>…</X.Comp>` → false mismatched-tag error in all four layers. Only self-closing worked. | FIXED |
| H3 | HIGH | `HmrCSharpEmitter` has no star/alias tag maps (SG `PropsResolver` does) — `<X.Comp/>` or renamed component tags hot-compile to CS0426/wrong props type. Build fine; every HMR edit fails. | FIXED (shared `ImportScopeFacts.ComputeStarImportNamespaces`/`ComputeImportAliasTypeMap` feed the hot emitter's tag rewrite; `FindPropsTypeAndRefSlot` is FQN-aware; M8 battery verifies live) |
| H4 | HIGH | HMR member-only route (`CompileHookModuleFile` new-mode) never injects import payloads or bridges — hot edit of an importing member file → CS0103. SG side injects via `ResolveInjectedUsings`. | FIXED (payload injection wired into the member route) |
| M1 | MED | SG↔HMR hookKeyMap drift: SG qualifies renamed hook imports (`map[bound] = {tns}.{container}::{original}`, gated on exported hooks); HMR maps every imported name unqualified/ungated → renamed-hook consumers lose Fast-Refresh reset after first hot edit. | FIXED (HMR mirror aligned) |
| M2 | MED | Editing members of a mixed (component + members) new-mode file: HMR component path never re-emits `__Exports` → member edits silently don't swap. | FIXED (same mechanism as H1 — the hot unit carries fresh member bodies, matching legacy companion-inlining semantics) |
| M3 | MED | SG legacy hook/module short-circuit doesn't thread `peerExports` into `ResolveInjectedUsings` — legacy hook importing a new-mode member: no diagnostic, editor clean, CS0103 at build. | FIXED |
| M4 | MED | SG `ResolveInjectedUsings` legacy peer loops ALSO match new-mode targets → injects whole-container/original-name payloads the LSP mirror deliberately withholds (editor/build disagreement; accidental capture). | FIXED (legacy loops skip new-mode peers) |
| M5 | MED | Value-cycle detection (UITKX2306) blind to new-mode values/utils (only hooks enter the graph via container entries) — migrated cyclic value imports get silence + runtime eager-init hazard. | FIXED (peerExports members seeded into the cycle graph) |
| L1 | LOW | VDG emits the file's own usings at FILE level and the seen-set then defeats the inside-namespace move — editor/build resolution can diverge for alias/static payloads. | FIXED |
| L2 | LOW | VDG's `using static {ns}.__Exports;` not `global::`-qualified (SG/HMR are) — constructible editor-only CS0234 via sibling-stem namespace members. | FIXED |
| L3 | LOW | `ResolveParentComponentFile` legacy-detect regex matches body text (`module.Init();`) → member-file save misrouted to the base component file. | FIXED (regex requires wrapper keyword + identifier) |
| L4 | — | `InjectUsings` marker search: traced, no constructible failure. | no action |
| — | LOW | UITKX2316 known-namespace seeding omits `peerExports` namespaces → possible false warning on `import "@<file-keyed-ns>"` of a member-only file. | FIXED |

## 3. Codemod (`--es-modules`, execution-verified)

| ID | Sev | Finding | Status |
|---|---|---|---|
| A1 | CRITICAL | `FindCloseBraceLine` scans for a line STARTING with `}` — a close brace sharing the last statement's line latches onto the NEXT declaration's brace → the following declaration is silently deleted (0 warnings). | FIXED (span end from parse-record offsets, not text scan; overlap guard) |
| A2 | HIGH | Same-line adjacent declarations produce overlapping replacement spans → corrupted output (dangling body), 0 warnings. | FIXED (overlap detection → whole-file skip with report) |
| A3 | HIGH | `ExplodeModule` regenerates members from parts — drops attributes, XML docs, comments, and `const`-ness silently. | FIXED (leading trivia preserved; attribute/const members → whole-set skip with report) |
| A4 | HIGH | Visibility default inverted: modifier-less module members (C# default private) migrate to `export` — private members join the export surface. (= PC-12a.) | FIXED (default = not exported) |
| A5 | MED-HIGH | Companion member-import pass only covers base←companion; companion←base and companion←sibling references migrate with NO import → broken build, 0 warnings. | FIXED (all intra-set directions) |
| A6 | MED | A failed (still-legacy) set importing a MIGRATED set's exploded module keeps `import { Module }` → 2301 on a previously-compiling file; "skipped files keep compiling" claim false. | FIXED (import rewrite runs over failed-set files too) |
| A7 | MED | `~/` specifiers resolve against the wrong root in `RewriteImports` (importerDir passed as project root) → silently un-starred import. | FIXED |
| A8 | LOW | Files with parse errors are half-rewritten without warning (module exploded, invalid tail left). | FIXED (parse-diagnostic gate → skip + report) |

## 4. LSP handlers

| ID | Sev | Finding | Status |
|---|---|---|---|
| B1 | MED-HIGH | Go-to-definition `s_plainDeclHeadRe` matches statement-shaped CALL lines (`useCounter(0);`) — first match top-down wins → jumps to a call site above the declaration. | FIXED (decl shape requires type token before name; keyword/call exclusion) |
| B2 | MED-LOW | Rename hookDeclPattern's multiline alternative matches call sites (dedup averts double-edit) but has NO comment/string guard → occurrences inside block comments/verbatim strings get renamed (regression of U-38/U-40). | FIXED (guard added) |
| B3 | MED | Alias rename applies BOTH interpretations of `oldName` in one pass — `import { a as b, c as a }`, renaming export `a` also renames the local binding `c as a`. | FIXED (single interpretation from rename origin) |
| B4 | LOW | `changes[uri] = existing.Concat(...)` replaces the `List<TextEdit>` with a lazy iterator — latent `InvalidCastException` if call order ever changes. | FIXED (in-place AddRange) |
| B5 | LOW | WorkspaceIndex phantom entries only for col-0 `VirtualNode` lines in unindented bodies/block comments — narrow. | FIXED (index scans comment/string-scrubbed text via `StrictImportDetector.ScrubNonCode`) |
| B6 | LOW | `RenameComponentDeclarationInFile` can hand the per-file decl edit to an indented same-named PascalCase local — unlikely, rename stays consistent. | FIXED (declaration shape required: line-start head + `(` after the name) |
| B7 | NIT | Import brace-completion `already` set doesn't parse `a as b` → re-suggests an already-imported name. | FIXED |

## 5. Plan compliance (PC — agent 1)

| ID | Sev | Finding | Status |
|---|---|---|---|
| PC-1 | MAJOR | 2109 rename-vs-legacy hole. | DUP F7 |
| PC-2 | MAJOR | StrictImportDetector messages not family-verbatim: backticks instead of the plan §3's single quotes for 2325/2326/2109/2110 (and 2305/2307/2301/2304/2300 use backticks — pre-band style). | FIXED for the §3-frozen new codes (2325/2326/2109/2110 wording aligned verbatim); pre-existing 23xx/2304 backtick style left as-is (predates the band freeze) |
| PC-3 | MAJOR | M7 samples flip is partial: 21 companion sets (35 files) remain legacy (unmigratable shapes) with no `REMAINING_WORK.md` entry; plan gate says flip + adjudicate. | FIXED (owner directed the full flip: blocking members extracted to ambient C# — 21 .cs created, 18 .uitkx retired — then the fixed codemod migrated the rest; VERIFY-UNITY all green; only the byte-frozen UitkxTestFileDoNotTouch fixture stays legacy, deliberately, as legacy-mode coverage) |
| PC-4 | MAJOR | U-05 dotted-tag semantic-token split coloring (`X` alias vs `Comp` type) not implemented. | FIXED |
| PC-5 | MINOR | 2325's "another import" arm is reported as parser 2303 (bound-name key), not 2325. | FIXED (alias-involved collisions re-keyed to family 2325 arm 2, verbatim message; plain name-vs-name duplicates stay 2303) |
| PC-6 | MINOR | 2108 direction asymmetry (wrapper-after-plain vs plain-after-wrapper anchor/coverage). | FIXED (plain-head look-ahead upgrades the legacy paths' trailing-content 2105 to 2108; bare statements keep 2105) |
| PC-7 | MINOR | 2107 (deprecated companion merge) surfaced by SG only, not LSP/HMR. | FIXED (DiagnosticsPublisher mirrors ModuleEmitter's 2107 conditions exactly, warning-tier, debounced path) |
| PC-8 | MINOR | Formatter does not group/sort imports into the plan's canonical order. | FIXED (named → `* as` → default grouping, stable within group; the imports-before-usings RELATIVE order kept — U-10's sentence is ambiguous and the pinned canonical order + legacy byte-identical guardrail win; AppButton.uitkx reordered to canonical) |
| PC-9 | MINOR | Missing star/default import snippets; `export default` absent from brace-completion. | FIXED (snippets) |
| PC-10 | MINOR | Rename-handler test coverage gaps for alias forms. | FIXED (tests with B3) |
| PC-11 | MINOR | `__Exports` emitted even when a file has no members (empty container). | RESEARCHED — the real defect is the inverse: `import * as X` of a COMPONENT-ONLY new-mode file emitted `X = ns.__Exports` against a container that is never emitted → file-breaking CS0246 in the importer. FIXED (both payload layers skip the alias when the target exports no members; dotted tags resolve via the tag maps regardless). |
| PC-12 | MINOR | Codemod exports private members (a) + leaves dead namespace imports (b). | (a) DUP A4 FIXED; (b) RESEARCHED, intentionally not implemented — dead-ns detection is compile-dependent (ambient C# resolves through them); the editor's 2316/2317 Roslyn tier owns it. A syntactic delete would break builds. |
| PC-13 | MINOR | `Ruitk.Examples.csproj` skipped in VERIFY-UNITY (stale machine-generated artifact). | recorded (R10 note stands) |
| PC-14 | MINOR | Docs diagnostics page lacks the §3.3 2322 family-divergence note. | FIXED |
| PC-15 | MINOR | HMR companion gate exercised only indirectly by tests. | FIXED (`HmrAuditWaveContractTests`: parse-clean syntax gate over every Editor/HMR source + direct pins on the redirect regex, bridge flow, tag maps, and key-map alias gate) |
| PC-16..19 | NOTE | Observations, no action required (recorded in the audit transcript). | no action |

## 6. Also fixed in this wave (found by own probe before the agents returned)

- Formatter comment-hoisting: comments between plain declarations hoisted to file top →
  positional trivia interleaving in `FormatPlainDeclarationFile` (+ 6 pins in
  `FormatterEsModulesTests`).

## 7. Residual items after the full-fix wave (2026-07-18)

Everything fixable was fixed this wave; nothing was silently deferred. What remains is either an
owner decision or genuinely blocked on external state:

- **M8 in-editor battery** (owner-run, already scheduled): live verification of the HMR wave
  (H1/H3/H4/M1/M2/L3) — the fixes are contract-pinned and parse-gated, but hot-reload behavior
  can only be proven in a running Unity editor.
- **F9 family question**: should the plain dialect grow generic declarations (`Name<T>(…)`)?
  Family grammar change (G-band) — Unity now gives a precise diagnostic; adding the feature
  needs family-level agreement, not a Unity-local patch.
- **Family corpus re-pin** (G-13): waiting on the sibling legs, owner-coordinated.
- **PC-13**: `Ruitk.Examples.csproj` is a stale Unity-machine-generated artifact — only
  Unity can regenerate it (R10 note stands).
- **Pre-band message style**: 2300/2301/2304/2305/2307 keep their historical backtick quoting
  (they predate the §3.1 band freeze); the §3-frozen codes (2325/2326/2109/2110) are verbatim.
- **PC-12b**: dead namespace-import deletion stays with the editor's compile-aware tier (see
  the PC-12 row for the reasoning).
