# Namespace-Import Unification Plan (`import "@Namespace"`)

**Status: IMPLEMENTED** — 2026-07-15 (v0.8.0 / extensions 1.4.0), branch
`fix/import-diagnostic-anchors`. Steps 0–10 done; deferred follow-ups noted inline
(semantic unused-using 2317, per-segment namespace completion + hover, `2316` "did you mean"
suggestion). SG 1527/1527, LSP 118/118.

**Follow-up polish shipped in the same 0.8.0 release** (user-driven, after the core):
1. **Formatter preamble ordering** — `@namespace` first, then all imports grouped (shared
   `EmitPreamble`). Plus a `--format` batch mode on the codemod CLI.
2. **`Ruitk.Router` auto-injected** into the baseline (all 7 emit sites +
   `AutoInjectedUsings`) — `RouterHooks` needs no import; an explicit one is 2317-redundant.
3. **`namespacePrefix` config + asmdef `rootNamespace` default** — project-wide namespace root,
   so `@namespace` can disappear from whole projects. Chain: `@namespace` > `namespacePrefix` >
   asmdef `rootNamespace` > `Ruitk.Uitkx` (asmdef *name* deliberately excluded). SG/LSP/HMR
   all route through `EffectiveNamespace.Resolve` (HMR by reflection, signature unchanged).
4. **Consistent import colour** — `import "@Ns"` scoped identically to a file import.

**JSO status:** re-tidied (Router imports dropped, preamble reordered) — uncommitted. Removing the
remaining `@namespace UI.App` + the file-import-covered `import "@UI.App.Pages…"` lines is a
coordinated refactor (flat→structured namespaces + 3 hand-written `.cs` `using UI.App;` sites, and
an unresolved `Styles` module reference) that must be **compile-verified**, which needs 0.8.0
published first (JSO consumes `#dist` at 0.7.1). Deferred to a verified follow-up.
**Scope: Unity leg only.** GDScript has no namespaces, so `.guitkx` never had `@using`; the shared
`import { X } from "path"` family grammar is untouched. The family diagnostic table reserves the new
codes (§5) with a Unity-only annotation.

---

## 1. Motivation

A migrated file's preamble mixes two syntaxes doing related jobs (real example,
JustStayOn `GameOverPage.uitkx`):

```
import { GameOverPageSidebar } from "./components/GameOverPageSidebar/GameOverPageSidebar"
import { GameOverStats } from "./components/states/GameOverStats"
@namespace UI.App
@using Ruitk.Router
@using UI.App.Pages.GameOverPage.Components        ← redundant (covered by import #1)
@using UI.App.Pages.GameOverPage.Components.States ← redundant (covered by import #2)
@using UnityEngine                                 ← redundant (auto-injected baseline)
@using UnityEngine.UIElements                      ← LOAD-BEARING (PickingMode; see §2.2!)
```

Beyond aesthetics, `@using` is second-class in a way that bites:

- **Zero validation.** A misspelled `@using UI.App.Pages.GameOvr` produces **no editor
  diagnostic at all**; at Unity build time it becomes CS0246 pointing into the *generated*
  `.g.cs`, not the `.uitkx` line. Contrast: a misspelled `import` specifier gets UITKX2300
  live in the editor (and, since the anchoring fix that ships alongside this plan, squiggled
  on the specifier string itself).
- **Zero position tracking.** `DirectiveSet.Usings` is `ImmutableArray<string>` — payload
  text only, no line/column (`DirectiveParser.TryParseUsing`, ~line 1690). Even if we wanted
  to diagnose, there is nothing to anchor a squiggle to.
- **Two mental models** where the dialect's JSX soul suggests one: *everything the file pulls
  in is an `import`*.

End state:

```
import { GameOverPageSidebar } from "./components/GameOverPageSidebar/GameOverPageSidebar"
import { GameOverStats } from "./components/states/GameOverStats"
import "@Ruitk.Router"
import "@UnityEngine.UIElements"
@namespace UI.App
```

(`@namespace` stays — it's an override; namespaces are path-derived by default since §4 of the
import/export plan, so most files won't have it.)

## 2. Current mechanics (verified 2026-07-15)

### 2.1 The desugar target

`@using X` lands in `DirectiveSet.Usings` (verbatim payload — including `static T` and
`Alias = T` forms) and both emitters print `using {u};` into the generated C#
(`SourceGenerator~/Emitter/CSharpEmitter.cs` ~line 206; hook/module emitters equivalent).
Nothing downstream cares which surface syntax produced the entry — **the parser is the single
desugar point**, so both emitters, HMR, and the strict detector are untouched by a new spelling.
No `Hmr*ContractTests` churn expected (they compare emitter output, which is identical).

### 2.2 The auto-injected baseline — and its trap

Every generated file already gets (CSharpEmitter ~lines 194–236): `System`,
`System.Collections.Generic`, `System.Linq`, `Ruitk`, `Ruitk.Core`,
`Ruitk.Core.Animation`, `Ruitk.Props.Typed`, `UnityEngine`,
`static StyleKeys`, `static CssHelpers`, `static AssetHelpers`, plus type aliases
(`Color`, `Length`, `EasingFunction`, …).

**Trap:** `UnityEngine.UIElements` is deliberately NOT wholesale-imported (its enum/struct
names collide with `StyleKeys` string constants; only targeted aliases are injected). So
`@using UnityEngine.UIElements` is often load-bearing (`PickingMode`, `DisplayStyle`, …).
⇒ Any "unused using" detection MUST be Roslyn-decided (CS8019 on the real compilation), never
a hand-maintained "baseline set" heuristic — except for the exact-duplicate subset (§6).

### 2.3 Import machinery this plan reuses

- `ImportDeclaration` now carries `Line`, `Column`, `NameColumns`, `SpecifierColumn` — the
  anchoring fix shipped with this plan's PR. `StrictImportDetector.Finding` carries
  `Column`/`EndColumn`; the LSP `DiagnosticsPublisher.ToDiag` and `UitkxPipeline` both map them.
- The SG runs inside a Roslyn `Compilation` with all project references → namespace existence
  is *decidable at build time* (walk `Compilation.GlobalNamespace` by dotted segments;
  `GetTypeByMetadataName` for `static`/alias targets).
- The LSP `RoslynHost` compiles a virtual document per file (Tier 3); `RoslynDiagnosticMapper`
  maps vdoc diagnostics back through `SourceMapEntry`s. Using-lines currently have **no map
  entries**, which is why vdoc CS0246 on a bad using never reaches the editor. (Verify as
  probe P0 before building §5.2 — if wrong, the fix is even simpler.)

## 3. Grammar

```
namespace-import := "import" ws string-literal
string-literal   := '"' '@' payload '"'
payload          := dotted-name                      // using Ns;
                  | "static" ws dotted-name          // using static T;   (parity with @using)
                  | ident ws "=" ws dotted-name      // using A = T;      (parity with @using)
```

Decisions (recommended; flag at review):
- **Double quotes**, matching every other `.uitkx` string (file specifiers, `@uss`). The
  formatter normalizes.
- The `@` sigil INSIDE the string disambiguates from a (hypothetical future) bare-string file
  import and reads npm-scope-ish. `import "@..."` with braces (`import { X } from "@Ns"`) is
  **reserved, not implemented** — see §9 stretch.
- `static`/alias payloads accepted verbatim for full `@using` parity, so `--tidy` can convert
  100% of existing files. Docs lead with the plain-namespace form.
- Preamble-only, same as file imports; interleaving with file imports allowed; formatter groups.

**Parser change** (`DirectiveParser.TryReadFunctionStyleImport`): after `import`, if the next
non-space char is `"` (not `{`), read the string; require leading `@`; strip it; append a
`UsingDirective` (§4) flagged `FromImportSyntax = true`. Malformed forms (`import "Ns"` without
`@`, unterminated string, empty payload) restore the cursor and fall through to the existing
non-declaration dispatch — same failure path as today's malformed imports.

## 4. Model: positions for usings (prerequisite for ALL diagnostics)

`DirectiveSet.Usings` (`ImmutableArray<string>`) → add parallel
`ImmutableArray<UsingDirective> UsingDirectives`:

```csharp
public sealed record UsingDirective(
    string Payload,          // "UnityEngine.Audio" | "static DoomTypes" | "UColor = ..."
    int Line,                // 1-based line of the directive/import keyword
    int Column,              // 0-based col of the keyword
    int PayloadColumn,       // 0-based col of the payload (namespace token) — squiggle anchor
    bool FromImportSyntax);  // true → import "@..." ; false → @using
```

Keep `Usings` as a computed view (`UsingDirectives.Select(u => u.Payload)`) so the emitters,
HMR emitter, and codemod compile unchanged; migrate consumers opportunistically. This mirrors
how `ImportDeclaration` grew `NameColumns`/`SpecifierColumn`.

## 5. Diagnostics (the point of the exercise)

New codes — next free in the 23xx block (2310/2313 retired-reserved, 2315 family-reserved):

| Code | Severity | Fires when | Anchor |
|---|---|---|---|
| **UITKX2316** | **Warning** at build (SG), **Error** in editor (LSP) | A plain-namespace `@using`/namespace-import does not resolve against the compilation | payload token span |
| **UITKX2317** | Hint (editor-only, `Unnecessary` tag → faded) | The using exactly duplicates the auto-injected baseline (`AutoInjectedUsings`) | payload token span |

> **Severity decision (implemented).** 2316 is a **build warning, editor error.** Build-time
> namespace validation is only as sound as the compilation is complete (guaranteed in a real Unity
> build, but not provable by the analyzer), so per the codebase's "never break an otherwise-valid
> build" rule it must never be the build gate — the emitted `using`'s CS0246 stays the gate; 2316
> just names the offending .uitkx token. In the editor (non-breaking squiggles) it is an error, the
> immediate red feedback the user asked for. Mirrors UITKX2304's build/editor split.
>
> **2317 scope (implemented vs deferred).** The **sound, high-value** case — a using that *exactly
> duplicates* an auto-injected baseline namespace (`@using UnityEngine`, `@using System`; the exact
> JSOAppButton motivation) — is implemented: compilation-free, no false positives, editor Hint +
> codemod `--tidy` strip. The **semantic "present-but-genuinely-unreferenced"** case is **deferred**:
> the vdoc suppresses CS8019 globally (P0) and un-suppressing it floods the ~25 scaffold-injected
> usings, so a robust version needs a narrow per-line semantic reference scan — real work with FP
> risk, low marginal value over the baseline-duplicate case. Tracked as a follow-up.

### 5.1 UITKX2316 — build side (SG)

New validator step in `UitkxPipeline` (beside `ValidateImports`), gated on the compilation:
split payload → walk `Compilation.GlobalNamespace.GetNamespaceMembers()` segment-by-segment;
`static`/alias targets via `GetTypeByMetadataName` falling back to namespace walk. Report as a
regular strict finding with `Column = PayloadColumn`, `EndColumn = PayloadColumn + token.Length`
— the anchoring machinery from this PR carries it the rest of the way. Surfaced in Unity via
the existing `#error` prepend path (message includes file:line), replacing today's raw CS0246
that points into generated code.

*No false positives by construction*: the same compilation that would fail with CS0246 is the
one consulted. Cost: a dictionary-free walk per using, trivially cached per compilation.

### 5.2 UITKX2316 + 2317 — editor side (LSP)

Two mechanisms, use both:
1. **2316 parity check** in `DiagnosticsPublisher.ComputeStrictImportDiagnostics` against the
   `RoslynHost` workspace compilation's global namespace (synchronous, T2-speed — no 300 ms
   debounce lag for the common typo case).
2. **Source-map entries for using lines** in the virtual-document generator, so vdoc CS8019
   maps back → re-emit as UITKX2317 Hint (drop CS8019's own severity/noise), and any other
   using-anchored Roslyn diagnostic stops being silently dropped.

Per the "conventions are docs, not warnings" principle already adopted for this feature family:
2317 is Hint-tier, never a build warning, never emitted by the SG.

### 5.3 Quick-fixes (`ImportCodeActionHandler`)

- 2316 → "Did you mean `@X.Y.Zed`?" — nearest-namespace suggestion (edit-distance over sibling
  namespace members at the failing segment; offer top ≤3).
- 2317 → "Remove unused using/import" (delete the line).
- New: "Convert `@using X` → `import \"@X\"`" (refactor-tier action on any `@using` line).
- Existing 2305 add-import fix is message-driven — unaffected.

## 6. Codemod: `--tidy`

Extend `UitkxMigrateImports` (reuses its file-set walker, `--check` mode, idempotence tests):

1. **Canonicalize syntax**: `@using X` → `import "@X"` (verbatim payload, including
   static/alias forms). Pure rewrite, zero semantic risk.
2. **Strip exact duplicates of the auto-injected baseline** (`System`, `UnityEngine`, … the
   §2.2 list, exact string match): always safe — a duplicate `using` is a C# no-op. The list
   is emitted by the emitters; expose it as `CSharpEmitter.AutoInjectedUsings` (single source
   of truth) so the codemod and docs can't drift from the emitter.
3. **Do NOT do semantic unused-removal** in the codemod (v1): that requires a compilation;
   the editor's 2317 Hint + quick-fix covers it interactively, correctly (§2.2 trap).
4. Preamble ordering: file imports → namespace imports → `@namespace` → anything else,
   matching the formatter (§7).

Idempotent; `--check` for CI. New `CodemodTests` fixtures: conversion, baseline-strip,
idempotence, static/alias payload passthrough, ordering.

## 7. Formatter + grammar + tooling surfaces

- **AstFormatter** (`language-lib/Formatter/`): preamble canonical grouping/ordering per §6.4;
  normalize quotes; one blank line after the preamble. Format-on-save then *maintains* the
  clean form — migration becomes automatic background behavior.
- **TextMate grammar**: single source `ide-extensions~/grammar/uitkx.tmLanguage.json`
  (prebuild copies to vscode + VSIX + Rider): scope the `@Ns` payload distinctly
  (`entity.name.namespace.uitkx`) so namespace imports read differently from file specifiers.
- **LSP completion**: a discoverable `import "@…"` snippet is offered alongside `@using`/`@namespace`
  in the preamble (implemented). Per-segment namespace completion after `import "@` (enumerating the
  compilation's namespaces) and hover on the payload are **deferred** — net-new completion-provider
  work with modest value; the snippet + diagnostics cover discoverability + correctness for v1.

## 8. Migration & compatibility

- **Ships in a minor bump (0.8.0)** — additive grammar. SemVer: new syntax = minor per
  VERSIONING.md.
- **`@using` keeps parsing indefinitely.** It desugars to the same node, so support costs
  nothing. Per the established principle, "prefer `import \"@...\"`" is a *documentation
  style rule*, not a code-level deprecation: no warning tier, no removal timeline. The only
  nudges are the refactor quick-fix (§5.3) and `--tidy`.
- **Own samples/docs/JSO**: bulk `--tidy` over `Samples/` is **deferred** — the Samples corpus
  contains a deliberately-unmigrated fixture (`UitkxTestFileDoNotTouch`) and a pre-existing
  ambiguity (`DoomGame`/`GameScreen`), so a blanket codemod sweep there is a separate, review-heavy
  change, not a tail-end step. The docs demonstrate the syntax instead; the codemod is user-facing.
- **HMR**: no protocol change (DirectiveSet-level); add one `HmrUsingParityTest` proving the
  HMR emitter output for a file using `import "@X"` is byte-identical to the `@using X` twin.
- **Docs website**: Imports page (new "Namespace imports" section + 2316/2317 in the
  diagnostics table), Concepts authoring rules, Diagnostics page, best-practices section
  ("write `import` for everything; you rarely need any namespace import — check the
  auto-injected baseline first"), MIGRATION_GUIDE.md (codemod `--tidy` section), README
  preamble examples.
- **CHANGELOG/DISCORD_CHANGELOG** entries (Discord ≤2000 chars).

## 9. Explicitly out of scope (v1) / stretch

- **Braced namespace imports** `import { RouterHooks } from "@Ruitk.Router"` —
  name-level, verifiable via `GetTypeByMetadataName`, emits a precise `using static`/alias
  instead of a broad using. This is the step that makes unification *semantic* rather than
  cosmetic; reserved grammar space, design after v1 lands.
- Auto-inferring namespace imports from unresolved identifiers (Unity's `Debug`/`Random`/
  `Object` collisions make silent inference wrong by design; the 2316 quick-fix's explicit
  suggestions are the ceiling).
- Godot leg: N/A (no namespaces). Codes 2316/2317 reserved in the family table as Unity-only.

## 10. Execution order (each step = green suites + committed)

| # | Step | Touches |
|---|---|---|
| 0 | ~~Squiggle anchoring for 2300/2301/2303/2304/2305/2306/2307/2308/2314~~ | **DONE — ships with this plan's PR** |
| P0 | Probe: confirm vdoc using-lines lack source-map entries; confirm CS8019 appears in vdoc diagnostics | lsp-server (read-only) |
| 1 | `UsingDirective` model + parser positions (+ back-compat `Usings` view) | language-lib, tests |
| 2 | Grammar: `import "@..."` desugar in `TryReadFunctionStyleImport` + parser tests (good/malformed/static/alias/interleave) | language-lib, tests |
| 3 | Emit-equivalence + HMR parity tests (byte-identical output vs `@using` twin) | SG tests |
| 4 | UITKX2316 SG validator + `#error` surfacing + tests (incl. anchoring) | SourceGenerator~, DiagnosticCodes |
| 5 | UITKX2316 LSP parity + 2317 via vdoc CS8019 mapping + tests | lsp-server |
| 6 | Quick-fixes (typo suggestion, remove-unused, convert-@using) | lsp-server |
| 7 | Formatter preamble canonicalization + tests | language-lib |
| 8 | Codemod `--tidy` (+ `AutoInjectedUsings` single source) + tests | SG Tools, emitters |
| 9 | TextMate grammar + completion/hover | grammar, lsp-server, extensions |
| 10 | Docs site + MIGRATION_GUIDE + best practices + samples `--tidy` + changelogs | docs, Samples |
| 11 | Rebuild committed generator DLLs (`scripts/build-generator.ps1`), extensions bump (minor — new language feature) | Analyzers/, extensions |

Estimated shape: steps 1–3 are one PR (grammar), 4–6 one PR (diagnostics), 7–9 one PR
(tooling), 10–11 the release PR. Corpus gate + `ImportCorpusManifestTests` guard every step
(`family-corpus.hash` untouched — no shared-grammar change).
