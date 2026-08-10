# Reactive UI Toolkit - Unity - VS2022 (UITKX)

Syntax highlighting + language intelligence for `.uitkx` markup (ReactiveUIToolkit for Unity). Completions, hover, diagnostics and formatting from the bundled language server — fully offline, no running Unity editor required.

💬 Join the community on Discord: https://discord.gg/Knedqu4Wyv

## Features

- **Syntax highlighting** — directives, control flow, element tags, attributes, embedded C# expressions
- **Completions** — element tags, attribute names, via the shared UITKX LSP server
- **Hover documentation** — element descriptions, attribute types and descriptions
- **Formatting** support via the UITKX formatter

## Requirements

- **Visual Studio 2022** (17.0+)
- **.NET 8+** runtime on PATH (`dotnet --version` must succeed)

## Changelog

### [1.10.0] - 2026-08-10
- Breaking: the legacy `component`/`hook`/`module` wrapper keywords and the `@component`/`@props`/`@key`/`@inject` directives were removed (package 0.16.0) — `UITKX2320` is now an error naming the `UitkxMigrateImports --es-modules` codemod. Completions, hover, references, rename, semantic tokens, the TextMate grammar, and the embedded schema now speak only the plain-declaration dialect (`@namespace`/`@using` still parse but left completions); references/rename regexes learned the modern heads including generic hooks (`useSel<T>(...)`), and retired diagnostics `UITKX2107`/`2108`/`2109`/`0211` no longer fire. Feature: generic declaration heads are first-class across the LSP (parse, virtual docs, bridges, formatter, highlighting). Fix: the formatter wraps over-width plain declaration heads again (parameters one per line, tuple returns ahead of the name). SG suite 1819/1819, LSP suite 155/155.

### [1.9.1] - 2026-08-09
- Fix: two live diagnostics still used pre-ES-modules name scoping, producing false errors in any workspace with same-named components in different folders (e.g. several games each with their own GameScreen or MainMenu).

UITKX0113 (duplicate component) flagged any two same-named components in the same asmdef. Since the ES-modules redesign namespaces are file-keyed, so same-named components in different folders are legal and compile cleanly; the diagnostic now fires only when two files declare the same name into the same effective namespace (legacy files sharing an explicit @namespace) - the only case where the generated classes actually collide - and it now checks every component declared in the file, not just the first.

UITKX0109 (unknown attribute) validated a tag against an arbitrary declarant when several components shared a name, e.g. rejecting onStartGame on one game's <MainMenu> because another game's MainMenu was checked instead. The attribute surface now resolves through the current file's imports with the build-identical specifier rule (same-file declaration first, then named/aliased/default imports, then import "@Ns" namespace visibility), and falls open to the union of all declarants when unresolvable - a wrong-file mismatch can no longer invent an error.

SG suite 1828/1828, LSP suite 157/157.

### [1.9.0] - 2026-08-01
- Feature: Unity 6.5 and 6.4 controls in UITKX markup - MaskField, Mask64Field and GUIDField.

<MaskField> and <Mask64Field> (Unity 6.5+) are multi-select bitmask dropdowns backed by int and ulong respectively, with choices plus an optional choicesMasks override for composite flags. <GUIDField> (Unity 6.4+) edits a UnityEngine.GUID; despite the name it is a runtime control and works in player builds, not only in the editor.

These are the first schema elements to carry a sinceUnity annotation, so completion and diagnostics are now version-aware for elements as well as style properties: on a project targeting an older Unity the new tags are reported instead of being silently offered.

One behaviour worth knowing when you diff or persist a mask: "Everything" is ~0 (that is -1) and "Nothing" is 0. "Everything" is NOT (1 << n) - 1, and the two must never be normalised into each other, or "Everything" quietly degrades into "every bit that happened to be defined at the time".

SG suite 1828/1828, LSP suite 152/152.

### [1.8.0] - 2026-07-28
- Breaking: the Reactive UI Toolkit family rebrand (pairs with Unity package 0.12.0). The repository moved to https://github.com/reactive-ui-toolkit/ruitk-unity and the code identifier root renamed ReactiveUITK → Ruitk: embedded Roslyn analysis and IntelliSense virtual documents now mirror the Ruitk.* namespaces, generated code emits global::Ruitk.*, asmdef names are Ruitk.*, and diagnostic categories follow. The UITKX language, file format, and every extension marketplace identity and display name are unchanged — existing installs keep updating in place. Migrate your own code with the RuitkMigrateBrand codemod (see MIGRATION-0.12.md in the repo). The bundled LICENSE files now carry the Reactive UI Toolkit Community License 1.1 — renamed and version-bumped family-wide, terms otherwise unchanged; previously published extension versions keep the license they shipped with.

SG suite 1754/1754, LSP suite 152/152.

### [1.7.0] - 2026-07-25
- Feature: @backend ugui — the uGUI element vocabulary across the toolchain (pairs with Unity package 0.11.0). A .uitkx file that opts in with the @backend ugui preamble directive switches to the 18-tag uGUI vocabulary (Canvas, Panel, Image, RawImage, Text, Button, the three LayoutGroups, Toggle/ToggleGroup, Slider, Scrollbar, ScrollRect, Dropdown, InputField, Prefab, UitkHost) with backend-aware completions, hover, diagnostics, and virtual-document IntelliSense.

New: UITKX2111 flags unknown or duplicate @backend values; UITKX2112 warns that @uss has no effect in a ugui file; UITKX2113 flags cross-backend imports in both directions.

UI Toolkit files are untouched — IDE behavior and generated code are byte-identical when the directive is absent.

SG suite 1744/1744, LSP suite 152/152.
- Feature: null-only components (React case 2) — a .uitkx component may now end in an explicit top-level `return null;` with no markup return: the component always renders nothing (the effect-only shape React permits). Diagnostics stay strict — a body with no top-level return at all is still UITKX2101 (its message now mentions the null form), and `return expr;` is still UITKX2102. Uniform across the compiler, hot reload, the formatter (no synthesized markup return on format), IntelliSense virtual documents, and both backends (uitk and @backend ugui).

SG suite 1754/1754, LSP suite 152/152.

### [1.6.0] - 2026-07-18
- Field-testing wave on the 0.9.0 ES-modules surface (pairs with Unity package 0.10.0).

New: the ES COMBINED import forms are supported end to end -- `import Def, { a, b as c } from "./file"` and `import Def, * as X from "./file"` parse, format (one canonical line), lower, and color as a single declaration whose every part binds.

Fix: named VALUE imports now resolve in the editor. The semantic workspace only loaded peer files with legacy hooks/modules, so a new-mode member file's `__Exports` never existed in the analysis -- `import { container }` plus a bare `container` reference squiggled CS0103 while the build was clean.

Fix: no more false "unused import" (UITKX2304) on used value imports -- bare identifier references (`style={container}`) now count; previously only tag/hook-call/dotted shapes did.

Fix: Ctrl+Space inside the braces of the combined form (`import Def, {} from`) now completes the target's remaining exports; the default binding and the default-exported name are excluded from the list.

New: imported binding names color by the KIND of the export they bind -- components as elements, hooks/utils as functions, values as variables (star aliases as variables; a default binding takes its export's kind) -- instead of the grammar's uniform type tint.

SG suite 1705/1705, LSP suite 152/152.
- License: from this release the extensions ship under the ReactiveUI Community License 1.0 (previously PolyForm Shield 1.0.0) — free to use and to ship commercially for any company under US $250,000 trailing-12-month revenue; above that, shipping a product needs a commercial license ($2,000 per title or $2,500 per studio per year — LICENSE-COMMERCIAL.md in the repo). No functional changes: the bundled LICENSE files (VS Code, VS2022 VSIX) carry the new text. Every previously published extension version keeps the license it shipped with.

### [1.5.0] - 2026-07-18
- ES-modules redesign (family campaign): a .uitkx file IS a module. Plain typed `export` declarations replace the `component`/`hook`/`module` wrapper keywords (classification from the signature alone: a VirtualNode return is a component, a use-prefixed name is a hook, `= initializer` is a value, anything else a util); full ES import surface (`import { a as b }`, `import * as X`, default imports + `export default`, deferred `export { ... }` lists); per-file (file-keyed) namespaces with real file-privacy; companion partial-class merging deprecated. Wrapper keywords keep parsing for this minor with UITKX2320/2107 deprecation warnings; the `UitkxMigrateImports --es-modules` codemod migrates whole trees (companion sets atomically). New family diagnostics UITKX2320-2327 + Unity-local 2107-2110. Editor surface updated across the board: grammar (star/default/rename imports, export lists, plain declaration heads), completions (plain-declaration snippets; wrapper snippets marked deprecated), go-to-definition/rename/references across both grammars (rename is alias-aware), semantic tokens, schema 1.2, and the Roslyn virtual documents mirror the new `__Exports` emission so editor diagnostics match the build byte-for-byte.
- Pre-release audit hardening (four-agent deep review of the ES-modules surface; ships in the same 1.5.0/1.2.0 release).

Fix: go-to-definition could jump to a CALL site instead of the declaration -- a statement like `useCounter(0);` above the declaration matched the plain-declaration head pattern (name directly followed by a paren satisfied it). Declaration matching now requires a real head shape: a type token before the name, C# statement keywords excluded.

Fix: rename applied BOTH interpretations of an aliased import name in one pass -- renaming the export `a` in `import { a as b, c as a }` also renamed the unrelated local binding `c as a`. Rename now derives ONE interpretation from where the rename was invoked (export name vs local binding) and touches only that surface; comment/string occurrences of statement-shaped hook lines are guarded; workspace indexing scans comment-scrubbed text so a declaration inside a block comment can't index a phantom component.

New: UITKX2107 (deprecated companion partial-class merging) now surfaces live in the editor with the same conditions the build uses; import brace-completion is alias-aware (an already-imported `a as b` entry is no longer re-suggested); default-import snippet; dotted tags (`<X.Comp/>`) color as binding + element.

Also ships the library-side audit fixes via the bundled server/DLLs: dotted tags with CHILDREN parse (`<X.Comp>...</X.Comp>` was a false mismatched-tag error), bound-name reference checking (`import { Widget as W }` satisfies `<W/>`, flags a bare `<Widget/>`), the `as`-rename-against-legacy-target gate (UITKX2109 instead of silence-then-CS0103), no more 2109+2326 double-reports, family-verbatim message wording, and correct diagnostic line anchors for every declaration after a component body.

SG suite 1730/1730, LSP suite 151/151.

### [1.4.4] - 2026-07-16
- Ships library 0.8.3's import-parser fix in the bundled LSP server + analyzer DLL.

Fix: a trailing `;` on a file import (`import { X } from "./file";` -- the JS-canonical form) wrecked the whole file: the parser's cursor stalled on the semicolon, the preamble loop bailed, and the file failed with a misleading UITKX2105 ("no valid component declaration") plus phantom "single root element" squiggles on lines that were never markup. The file-import reader was the only preamble reader missing the consume-to-end-of-line step its siblings (`@using Ns;`, `import "@Ns";`) already had -- it now has parity, so the semicolon is tolerated in the editor, the Unity build, and hot reload alike. The formatter re-emits the canonical, semicolon-less form on save.

With the meltdown gone, genuinely wrong imports get their real diagnostic -- e.g. importing a module's field instead of the module now correctly reports UITKX2301 ("not exported") on the exact name.

No new commands, settings, grammar, or diagnostic codes. Pairs with Unity package 0.8.3 (same parser via the committed analyzer DLLs).

SG suite 1550/1550, LSP suite 118/118.
- Two editor-only false-positive classes fixed (both surfaced by the stamp-less, path-derived samples; every build was clean throughout).

Fix: companion-hook calls showed CS0121 "call is ambiguous" in the editor. The virtual-document generator emitted its component/hook/module classes under the RAW parsed namespace -- the parser default `Ruitk.FunctionStyle` for every stamp-less file -- while the injected import using-statics (and the compiled assembly) used the EFFECTIVE namespace. Two containers with the same name in scope = ambiguous everything. All virtual documents and the peer-companion `using static` injection now resolve through the same `EffectiveNamespace` seam the build uses, so the editor sees exactly one container in exactly the namespace the build emits.

Fix: `onClick` (and every other BaseProps attribute) false-flagged UITKX0109 on 30 built-in elements that were registered in the element registry but missing from uitkx-schema.json -- the whole editor Toolbar family, MultiColumnListView/TreeView, ObjectField, TwoPaneSplitView, ColorField, MinMaxSlider, the editor value fields, and more. Schema-less elements fell into the user-component branch (declared-parameters-only). All 30 now have schema entries generated from their actual Props classes, so they also gain real attribute completion and hover docs. A new registry<->schema parity test fails the suite if an element is ever registered without a schema entry again.

SG suite 1551/1551, LSP suite 119/119.
- Two more editor-only fixes from the same F5 field-testing session (build clean throughout).

Fix: a false CS1662 ("cannot convert lambda") on block-bodied lambdas whose body calls a state setter in value form (`setGrid(newGrid)` -- the suppressed sugar for `.Set(_ => v)`). The cascade suppression compared spans: it only worked for expression-bodied lambdas, where Roslyn's CS1662 span covers the whole body -- for block-bodied lambdas Roslyn anchors the diagnostic on the parameter/arrow tokens alone, so the causal (already-suppressed) CS1503 was never inside it and the phantom error surfaced. The check now resolves the enclosing lambda NODE from the syntax tree and suppresses when a suppressed setter call sits anywhere in it. A genuinely wrong return still reports at its own site.

Fix: go-to-definition on an exported hook or module call site silently did nothing. The peer text-search fallback (used because a hook's method signature is scaffold with no source-map entry) matched lines starting with `hook ` / `module ` -- written before the 0.7.0 import/export grammar put `export ` in front of every consumable declaration. The prefix is now stripped before matching, so F12 on `useMyHook()` lands on the `export hook useMyHook` line.

SG suite 1551/1551, LSP suite 121/121.
- Fix: F2-renaming a hook, module, or component updated the declaration and every call site/tag -- but left `import { oldName }` lists untouched, instantly breaking each importing file (the import bound a name that no longer existed). Import names are uitkx-only preamble syntax: they never appear in the C# virtual document (imports lower to `using` lines), so Roslyn's renamer cannot see them, and the text-scan collectors match call/tag shapes an import list doesn't have. Rename now runs an import-list pass over every file participating in the edit, patching the exact name tokens via the parsed import model (line + column precise, no regex-over-text).

SG suite 1551/1551, LSP suite 123/123.
- Perf: editing a peer .uitkx or companion .cs file re-published diagnostics for EVERY dependent file INLINE inside the didChange notification handler, serializing seconds of re-analysis into the server message pipeline -- queued requests (formatting, hover, completion) lagged behind it on every keystroke in a widely-imported file. Dependent re-publishes now ride the existing 500 ms debounced revalidation; the edited file itself keeps its immediate per-keystroke publish.

### [1.4.3] - 2026-07-16
- Ships library 0.8.2's import-scope parity in the bundled LSP server.

Fix: IntelliSense aliased imported modules/components with the target file's RAW parsed namespace -- wrong for every stamp-less (path-derived) file -- so projects using the new `namespacePrefix` config showed false red squiggles on cross-file references. The virtual document now computes aliases from the target's EFFECTIVE namespace (explicit `@namespace` wins, else path-derived + config prefix), and its same-namespace guard now matches the source generator's (no alias when importer and target share a namespace).

The injection rule is now a single shared helper (`ImportScopeFacts` in the language lib) consumed by the editor virtual document, the Unity build, and Unity's hot-reload compiler alike -- one rule, three consumers, contract-tested against drift.

No new commands, settings, grammar, or diagnostic codes. Pairs with Unity package 0.8.2 (which carries the HMR half).

SG suite 1541/1541, LSP suite 118/118.
- Marketplace listing overhaul: distinguishable display names -- `UITKX (Unity - VS Code)` / `UITKX (Unity - VS2022)` -- and a structured page body (Title / Description / Features / Requirements / Changelog) on both marketplaces + Open VSX.

### [1.4.1] - 2026-07-16
- Ships library 0.8.1's import-resolution fixes in the bundled LSP server (virtual-document parity).

Fix: C# body references to a cross-namespace file-imported COMPONENT's type (`Widget.WidgetProps`, `TableView.Column`) showed false red squiggles in the editor -- component imports only qualified tags, never C# code. The virtual document now injects a type alias for each imported component (exactly like modules get), so IntelliSense matches the real build's new behavior. Surfaced by path-derived namespaces, where companions in other folders always land in different namespaces.

No new commands, settings, grammar, or diagnostic codes. Pairs with Unity package 0.8.1 (which carries the build-side halves: baseline-namespace resolver scope + component alias injection in the generator).

SG suite 1532/1532, LSP suite 118/118.

### [1.4.0] - 2026-07-15
- Ships library 0.8.0's namespace-import unification via the bundled LSP server + analyzer DLL.

New syntax: a C# namespace can now be imported with `import "@Namespace"` -- exactly equivalent to `@using Namespace` (same generated `using`). The two preamble shapes are now distinct on purpose: `import { X } from "./file"` (braces + `from`) imports a peer .uitkx file and is name-checked; a quoted `"@Namespace"` imports a C# namespace. Static/alias payloads work too (`import "@static UnityEngine.Mathf"`). `@using` keeps working -- the unified form is the recommended spelling. `import "@Ns"` is highlighted identically to a file import (same colour), and a discoverable `import "@..."` completion snippet is offered in the preamble.

New diagnostics (editor + build via the analyzer DLL): UITKX2316 flags a `@using`/`import "@Ns"` whose namespace resolves nowhere -- the namespace analogue of UITKX2300. A misspelled `@using` used to be silent in the editor (only a raw CS0246 in generated code); it is now an editor error squiggled on the namespace token, and a build warning (it never breaks a valid build -- the emitted using's CS0246 stays the gate). Validated against the compilation plus every peer .uitkx namespace, so generated namespaces are never false-flagged. UITKX2317 flags a using already auto-injected into every file (`@using UnityEngine`, `@using System`, `@using Ruitk.Router`) as a faded redundant-using Hint.

New quick-fixes: 'Remove redundant using' (2317) and a cursor-position 'Convert to import "@..."' refactor on any `@using` line. The formatter round-trips both spellings without rewriting one to the other, and now puts `@namespace` first with all imports grouped under it; the bundled codemod gains `--tidy` (convert + strip redundant usings) and a `--format` batch mode.

Also: `RouterHooks` is auto-injected -- `import "@Ruitk.Router"` is never needed (and is flagged redundant). And a new uitkx.config.json `namespacePrefix` key lets a whole project carry its own path-derived namespace root with no per-file `@namespace`, falling back to the owning .asmdef's `rootNamespace`, then `Ruitk.Uitkx`.

SG suite 1527/1527, LSP suite 118/118.

### [1.3.1] - 2026-07-15
- Correctness fixes for 1.3.0's strict import diagnostics, shipped in the bundled LSP server + analyzer DLL. No new commands, settings, or diagnostic codes.

Fix: false UITKX2307 errors on built-in hooks. The strict-import exemption list only contained the canonical PascalCase names (`UseState`, `UseMemo`, ...) while real files call the camelCase aliases (`useState(`, `useMemo(`) -- so the moment a project had any exported peer hook/module, every built-in hook call site was flagged as an error. The exemption now covers both spellings from the single-source `HookRegistry` (`AmbientHookNames`), in both the editor squiggles and the Unity build.

Change: heuristic strict findings are now warnings, not errors. Bare hook-call (`useX(`) and module member-access (`Name.member`) matches are scanned out of plain C# expression text, which ambient C# legitimately produces -- a hand-written C# hook, a nested enum reachable via `@using static`, or a Unity type like `Screen.width` that merely collides with some file's exported module name. Those UITKX2305/2307 findings are warning-tier now; a truly missing import still fails the build with CS0103, and component-tag `<X>` findings (uitkx-only syntax, sound evidence) stay errors.

Fix (Linux/macOS): import specifier resolution dropped the leading `/` of rooted paths (`/home/...` resolved as `home/...`), silently breaking go-to-definition on imports, completion inside `import { }`, the add-missing-import quick-fix, and the live 23xx diagnostics on those platforms. Windows was unaffected (its `C:` root survives as an ordinary path segment).

SG suite 1472/1472, LSP suite 107/107.

### [1.3.0] - 2026-07-12
- Ships library 0.7.0's import/export grammar for `.uitkx` via the bundled analyzer DLL + the shared language-lib/LSP server.

New syntax highlighting: `import { A, B } from "./path"`, `export`-prefixed `component`/`hook`/`module` declarations, and the `from` keyword are now recognized by the TextMate grammar (specifier strings scoped as paths). The workspace index is export-aware -- its component/hook/module scanners accept the optional `export` prefix so cross-file symbols resolve after migration.

Build enforcement (via the analyzer DLL): referencing a peer-exported name without importing it is UITKX2305 (the message names the exact `import { X } from "./rel"` line to add); a `useX()` call that no file exports and is not a builtin hook is UITKX2307. Namespaces are path-derived from the owning .asmdef (`@namespace` is now an optional interop override). A `~/` root alias is accepted in `import` specifiers and in `Asset<T>`/`@uss` paths.

Upgrade note: run the `UitkxMigrateImports` codemod (shipped in the package) once to add imports/exports to existing `.uitkx` files -- new syntax is additive, so an un-migrated project keeps working until you do.

SG suite 1465/1465, LSP suite 107/107.

### [1.2.18] - 2026-07-08
- Ships library 0.6.5's correctness fixes via the bundled analyzer DLL and the shared language-lib/LSP server. All bug fixes -- no new commands, settings, or diagnostic codes.

Fix: formatter no longer corrupts commented-out code. A `/* old UI: (<Label/>) was removed */`-style block comment could be misdetected as live JSX by the setup-code JSX-block scanners, producing a false CS1002 in the virtual document and re-indenting the comment's interior on every save. Both scanners now skip block comments (matching their sibling detector), and comment-interior lines are re-emitted byte-verbatim like the existing multi-line-string handling.

Fix: `@switch (expr)` now gets hover, diagnostics, and go-to-definition on its own expression in the editor, matching `@if`/`@foreach`/`@for`/`@while` conditions -- previously the switch expression was never mapped into the virtual document at all.

Fix: mismatched closing-tag diagnostic (UITKX0302) now anchors at the actual mismatched tag with a tracked column, instead of a stale line and a fabricated "opened at line N" when the opening location wasn't tracked.

Fix: hover/completion prop indexing now recognizes expression-bodied properties (`public string Foo => ...;`), not just brace-bodied ones.

Fix: several parser edge cases that could misfire live diagnostics or silently mis-parse: literal `@` in text content, duplicate `@namespace`, Allman-style `@else`, `::`-qualified `@case` values, an unwrapped `return expr;` inside a control block, an unclosed `{`/`{expr}` in a snippet, and `attr={(<Tag/>)}` (paren-wrapped inline JSX attribute) now recognized as JSX instead of opaque C#.

Fix: `x = cond && <Tag/>` and other logical-AND-before-JSX shapes now desugar correctly regardless of LHS shape.

Fix: a CRLF-handling edge case and a lexer drift (`{ $@"{a("}")}" }` truncating the extracted expression) are covered by a consolidated, shared lexer used across the parser, formatter, and diagnostics -- six previously-independent copies could drift from each other after edits to just one.

Fix: editor asset-path diagnostics now resolve relative paths (`"./icon.png"`, `"../ui/theme.uss"`) with the same rule the build uses. Previously the analyzer, the generated code, and HMR could each resolve a bare or relative path differently, so the editor might show no error while the build emitted an unresolvable path.

SG suite 1337/1337 passing, LSP suite 82/82 passing.

### [1.2.17] - 2026-06-13
- Feature: Ships library 0.6.3 declarative custom rendering. Built-in elements now accept an `onGenerateVisualContent` attribute - a binding for Unity UI Toolkit's `VisualElement.generateVisualContent` delegate, typed `Action<MeshGenerationContext>` - plus a companion `redrawKey` int that forces an on-demand repaint without changing the callback reference. Editor support is complete and schema-driven: both attributes are recognized on every built-in element (no false UITKX0109 unknown-attribute error), surface in completion and hover, and the `ctx` lambda parameter is typed as `MeshGenerationContext` so `ctx.painter2D` and `ctx.Allocate(...)` autocomplete and type-check inside the attribute value. The grammar schema (`uitkx-schema.json`, embedded in the LSP server) gained the two `intrinsicElementAttributes` entries, and the virtual document generator maps `onGenerateVisualContent` to `MeshGenerationContext` in its event-callback parameter table. No completion/hover/diagnostics code changed - the new attributes flow from the schema. The source generator needs no change either: it resolves the new `BaseProps` members by semantic reflection, so the attributes compile in Unity with no extension rebuild required.

### [1.2.16] - 2026-05-23
- Fix: language server reference cache no longer poisoned by mid-recompile DLL state; open .uitkx files refresh diagnostics automatically after Unity recompiles. Added uitkx.restartLanguageServer command for faster recovery than Reload Window.

### [1.2.15] - 2026-05-23
- Ships library 0.6.1: unified hook metadata into a single source of truth (`Ruitk.Core.HookRegistry`). Eight hand-maintained tables describing the 20 public hooks - `CSharpEmitter.s_hookAliases` / `s_genericHookAliasRe` / `s_hookSignatureRe`, `HooksValidator.s_hookPatterns`, the two HMR mirrors, `DiagnosticsAnalyzer.s_hookPatterns` (live IDE diagnostics), `HoverHandler.s_hookDocs` (IDE hover), and the two `VirtualDocumentGenerator` Roslyn-only stub blocks (static + instance forms) - lived in parallel across 5 files and consistently drifted from each other after every hook addition (7 missing camelCase aliases in 0.5.21; 10 missing hover docs in 0.5.22). All 5 consumers now read from `HookRegistry` accessors (`GetAliasTable`, `GetSignatureRegexPattern`, `GetGenericHookPattern`, `GetValidationPatterns`, `GetDocMap`, `GenerateVirtualDocStubs(bool staticForm)`); the registry is `#if UNITY_EDITOR`-wrapped so it adds zero player-build cost. Linked into `UitkxLanguage.csproj` via `<Compile Include Link>`; the SG inherits transitively via its existing `ProjectReference`, avoiding CS0436 duplicate-type. Per-keystroke hot paths retain their zero-allocation contract via cached-reference accessors.

Fix: `useLayoutEffect` is now covered by `UITKX0013` (rules-of-hooks) live diagnostics and the IDE hover layer. The hook shipped runtime-side several releases ago and had a SG alias-rewrite entry, but the validator's `s_hookPatterns` and `HoverHandler.s_hookDocs` were never updated, so calling `useLayoutEffect` inside an `if` block silently passed the validator and hovering over the call site returned no documentation. Registry adds the missing 3 validation patterns (`Hooks.UseLayoutEffect(`, `UseLayoutEffect(`, `useLayoutEffect(`) and the 2 hover entries (camelCase + qualified) as pure additive coverage - no existing diagnostic behaviour changes.

SG suite `1264/1264` passing (+16 new `HookRegistryTests` covering internal invariants, runtime reflection parity against the `Hooks` type, and byte-for-byte golden-file equality against 8 pre-refactor snapshots).

### [1.2.14] - 2026-05-22
- Formatter no longer drifts indentation on commented-out blocks containing braces. Lines starting with `//` were still scanned for `{` / `(` characters by the block-stack and paren-depth trackers in `EmitCSharpLines` (~L1095) and `EmitSetupCodeNormalized` (~L1378) -- a commented `useEffect(() => {` pushed a phantom block onto the stack on every format pass, so each subsequent save indented the comment block one level deeper than the previous save. Fix: skip brace and paren state updates when a line starts with `//`; normal indent computation (which preserves the relative indent against `lastStatementInputIndent`) still runs so existing CSharpier-corruption cases (comments at 4sp inside paren blocks) remain idempotent. Locked in by `C16b_CommentedOutHookBlock_TrailingBraceInComment_DoesNotShiftIndent` in `FormatterSnapshotTests.cs`; full formatter suite 924/924 passing.

### [1.2.13] - 2026-05-20
- JSX inside attribute-lambda bodies is now type-checked by the IDE. The virtual document generator's `EmitMappedExpressionStrippingJsx` (VDG L736) replaced JSX subtrees inside attribute values - e.g. `onClick={e => <Inner badProp={42} />}` or ternaries returning JSX from an inline expression - with `(VirtualNode)null!`, dropping the entire subtree from Roslyn's view. Result: bad props, undefined components, and type mismatches inside attribute-lambda JSX produced zero diagnostics in VS Code / VS 2022 even though the SG / cold-build flagged them correctly. Fix: extended the strip helper with an optional `deferredJsxRanges` list that records every stripped subtree position; a new `EmitDeferredJsxAttributeChecks` helper parses each subtree with `UitkxParser.Parse` and wraps the node-check code in a Pattern-B `dynamic __uitkx_jsxattr{pos}() { ... return default!; }` local function so Roslyn sees the full type-check graph without affecting the runtime emission. Thread-static `t_jsxAttrContext` (set in a try/finally around `EmitFunctionStyleBodyCore`) publishes the source + directives context to the three deferred sites (`EmitExpressionStatement`, `EmitTypedPropsCheck`, `EmitBodyWithReturnFix`) without threading two nullable parameters through six method signatures. Locked in by `AttributeLambda_JsxBody_DeferredPatternBEmitted` + `AttributeLambda_WithJsxSubtree_GeneratesVirtualDoc` in `RoslynHostTests`.

New runtime hook `Hooks.UseTransition()`. Returns `(bool isPending, Action<Action> startTransition)` matching React's API. UITKX has no concurrent renderer, so `isPending` is always `false` and `startTransition(action)` runs `action` synchronously; the start delegate is a cached `static readonly` field so per-call allocation is zero. Provided for source compatibility with React-targeted code being ported to UITKX, and so the SG / HMR alias rewrite from `useTransition(...)` to `Hooks.UseTransition(...)` (already in 0.5.21's `s_hookAliases`) now resolves at runtime instead of failing with `CS0117: 'Hooks' does not contain a definition for 'UseTransition'`.

10 more hooks now resolve in the IDE virtual document. The VDG's static + component stub blocks were missing per-component shadow declarations for `useReducer`, `useDeferredValue`, `useImperativeHandle`, `useStableFunc`, `useStableAction`, `useStableCallback`, `useTweenFloat`, `useAnimate`, `useSafeArea`, and `useTransition`. The SG alias-rewrite layer in 0.5.21 sent the camelCase forms through to `Hooks.UseXxx(...)`, but the IDE's per-component scope shadowed the unqualified call sites so hovering / go-to-definition / signature-help silently failed. Both stub blocks now mirror the SG's hook-shadow set 1:1 (20 hooks x both invocation forms = 40 stubs each side).

Hooks-in-conditional validator and hover documentation widened. `HooksValidator.s_hookPatterns` (SG) and `DiagnosticsAnalyzer.s_hookPatterns` (language-lib mirror for live diagnostics) grew from 30 to 60 strings - 20 public hooks x 3 forms (`Hooks.UseX(`, `UseX(`, `useX(`) - so `UITKX0013` ("hook called inside conditional / loop") now fires for `UseImperativeHandle`, `UseSafeArea`, `UseStableFunc`, `UseStableAction`, `UseStableCallback`, `UseAnimate`, `UseTweenFloat`, `UseSfx`, `UseUiDocumentRoot`, and `ProvideContext` (previously these 10 silently broke React's rules-of-hooks contract). `HoverHandler.s_hookDocs` grew from 16 to 40 entries so hover over the 12 newer hooks produces the documented signature, params, and a UITKX-specific note where the runtime semantics diverge from React (e.g. `useTransition`'s synchronous behaviour).

SG suite `1245/1245` passing (+1 `Sg_UseTransitionHook_LowercaseAliasRewritten` parity test); LSP suite `+2 RoslynHostTests` for the attribute-lambda fix.

### [1.2.12] - 2026-05-19
- Ships the library 0.5.21 fixes via the bundled analyzer DLL. Three changes, all on the SG / HMR side; no LSP, language-lib, or per-IDE plumbing was touched, so consumer behaviour in the editor is unchanged - this release exists to deliver the new analyzer build.

HMR cascade-batch no longer re-fires mount effects on cascaded ancestors. When a `.uitkx` save pulled transitive dependents into a batch compile, `UitkxHmrController.ApplySuccessfulCompileResult` ran per-file and `UitkxHmrComponentTrampolineSwapper.SwapAll` -> `NotifyMatchingFibers` unconditionally invoked `FullResetComponentState` on every matched fiber whenever the `[HookSignature]` byte-differed between the SG-emitted and HMR-emitted attributes. For cascade-pulled ancestors that wiped `useEffect` cleanups and re-fired mount effects on the next render - observed in the wild as additive `SceneManager.LoadSceneAsync` calls on a parent firing a second time when an unrelated sibling subtree was edited. `SwapAll` now takes an `allowFullStateReset` parameter (default `true`, preserving single-file semantics); the controller threads `isOriginatingChange = (i == paths.Count - 1)` from the cascade walker's dependents-first / originator-last ordering. Cascaded files still receive the cheap trampoline-field swap (so their next render uses the new IL) but retain their fiber state and effect cleanups intact.

HMR wrong-namespace warning spam eliminated. The hook-module branch resolved the assembly's namespace via `LoadedAssembly.GetTypes().FirstOrDefault().Namespace`. Roslyn's embedded `Microsoft.CodeAnalysis.EmbeddedAttribute` materialises first in metadata order, so the probe returned `Microsoft.CodeAnalysis` and the swapper logged "type not found" warnings on every save. The declared `@namespace` is now threaded through `HmrCompileResult.Namespace` (set in `Compile`, populated in `CompileBatch` per-file results from `ComponentBuildArtifacts.Namespace`) and read directly with a name-filtered `GetTypes()` fallback for defensive resolution.

camelCase hook aliases now work for 7 more public hooks. `useTweenFloat`, `useAnimate`, `useSafeArea`, `useStableFunc`, `useStableAction`, `useStableCallback`, and `useImperativeHandle` were listed in every layer's `s_hookSignatureRe` (signature scanner) but missing from every layer's `s_hookAliases` rewrite table and from `s_genericHookAliasRe` for the 3 generic ones. Writing them in their natural camelCase form in a `.uitkx` setup block produced `CS0103: The name 'useTweenFloat' does not exist in the current context`, forcing consumers into manual `Hooks.UseXxx(...)` qualification with no compiler help. Fix: 7 alias entries plus 3 generic-regex entries added in lockstep to `SourceGenerator~/Emitter/CSharpEmitter.cs`, `Editor/HMR/HmrCSharpEmitter.cs`, and `Editor/HMR/HmrHookEmitter.cs`. Locked in by 7 new `Sg_Use*_AliasRewritten` parity-contract tests in `HmrEmitterParityContractTests.cs` so future hook additions cannot silently drift again.

SG suite `1244/1244` passing (+7 alias-rewrite tests, +0 regressions).

### [1.2.11] - 2026-05-18
- LSP workspace index now stores every declarant of a duplicated `component <Name>` instead of silently overwriting on the second commit. `WorkspaceIndex._elementInfo` becomes multi-valued; `TryGetElementInfo` keeps its deterministic-first-match contract for existing callers, and new `GetAllElementInfo` / `GetDuplicateDeclarations` accessors expose the full set for diagnostics and rename. `EvictElementsFromFile` now removes only the per-file entry so editing one duplicate no longer evicts the entire name globally. `DiagnosticsPublisher` raises `UITKX0113` (Warning) against each duplicate's `component <Name>` line, asmdef-scoped (cross-asmdef collisions are legal), suppressed until the workspace scan completes to avoid transient false positives during indexing.

New analyzer `UITKX0211` (Warning) in the language-lib `DiagnosticsAnalyzer` flags `const` fields inside a `module { }` body. Const values inline into every consumer's IL at compile time and never propagate under HMR; recommends `static readonly` (which the SG strips of `readonly` and the HMR static-swapper refreshes on every edit-save cycle). Six new tests in the SG suite pin the warning shape (`public const`, severity, no-fire on `static readonly`, no-fire on commented const, per-decl on multi-decl). Ships in lockstep with library 0.5.20 which lands the HMR save-cascade walker (one save propagates through every transitive consumer in the same asmdef), the per-SCC union-compile path (closes TD22B - cross-component prop-signature refactors no longer need a second save), and new-`.cs` pickup so helper `.cs` files referenced from a `.uitkx` are visible to HMR before Unity has recompiled the project DLL. LSP suite 69/69 passing (+6 `WorkspaceIndexDuplicateTests`); SG suite 1237/1237 passing.

### [1.2.10] - 2026-05-16
- Unity 6.3+ types now resolve by short name in .uitkx source. `StyleMaterialDefinition`, `MaterialDefinition`, `StyleRatio`, `Ratio` and `FilterFunction` are emitted as preprocessor-guarded `using` aliases by every emitter (component, hook, module, HMR, and the LSP virtual document). Eliminates spurious CS0246 errors on expressions like `UnityMaterial = new StyleMaterialDefinition(new MaterialDefinition(mat))`.
- HMR's component emitter (`HmrCSharpEmitter`) now emits the full targeted UnityEngine.UIElements alias block in lock-step with the SG. Previously it only emitted `using Color = UnityEngine.Color;` so hot-reloads of files referencing `EasingFunction`, `BackgroundRepeat`, `Length`, `StyleKeyword`, `TextAutoSizeMode`, etc. failed with CS0246 even though cold builds worked.
- New CssHelpers: `MaterialDef(Material)` returns `StyleMaterialDefinition` (mirrors the existing `FontDef` precedent). `Ratio(float)` returns `StyleRatio` so `AspectRatio` no longer requires manual wrapping.
- `PropsApplier.unityMaterial` now accepts a bare `Material` in addition to the existing `StyleMaterialDefinition` / `MaterialDefinition` shapes (matches `aspectRatio`'s multi-shape precedent).

### [1.2.9] - 2026-05-15
- Fix: Go-To-Definition could navigate to a stale file after copy-rename-edit cycles. WorkspaceIndex.IndexUitkxFile / IndexFile only ever added or overwrote element entries; when a file's `component Foo { ... }` was edited to `component Bar { ... }` (or a *Props class was renamed) the old `Foo` entry kept pointing at the file forever, surviving until process restart. Same bug on file deletion: the `.uitkx` deletion branch only removed from `_moduleHookFiles` and never evicted element entries, so go-to-definition would land on a non-existent path. Now WorkspaceIndex tracks element-name -> file ownership in a reverse map (_elementsByFile) and evicts a file's previously-contributed names before re-indexing or on deletion. Symptom that motivated the fix: copy `PlayerHud.uitkx` into a new `HealthBar/` folder, rename + edit to `component HealthBar { ... }`, then go-to-definition on a sibling `<StatsPanel />` reference jumped to `HealthBar.uitkx` because an intermediate state during the copy had been indexed and never evicted.

### [1.2.8] - 2026-05-14
- Fix: cross-directory and cross-namespace hook resolution. The SG, LSP virtual document, and Editor HMR all used a same-folder + same-namespace assumption when injecting `using static <Ns>.<HookContainer>;` directives for peer hook files; a hook in `Assets/UI/Hooks/UseUiDocumentSlot.hooks.uitkx` (namespace `PrettyUi.UIHooks`) consumed by a component in `Assets/UI/Pages/MenuPage/MenuPage.uitkx` (namespace `PrettyUi.UI.Pages`) needed a manual `using static` line at every consumer, and HMR recompiles silently dropped the directive. Three layers fixed in lockstep: (1) SG `UitkxPipeline` Stage 3d drops the strict-namespace check (asmdef ownership is already enforced by the pre-scan via `IsOwnedByCompilation`); (2) LSP `RoslynHost.EnrichWithPeerHookUsings` mirrors the SG fix and gates injection by asmdef ownership via a new `AsmdefResolver` helper, and `WorkspaceIndex` now tracks every indexed `.cs` file so `FindCompanionFiles` unions same-folder `.cs` with workspace-wide `.cs` filtered to the consumer's asmdef; (3) Editor HMR's new `HookContainerRegistry` (seeded asynchronously from `UitkxHmrController.Start`, invalidated per-file by the watcher, reset on `Stop`) gives `UitkxHmrCompiler.EmitCompanionUitkxSources` the cross-directory hook FQNs without scanning the workspace per recompile. `AsmdefResolver` is mirrored verbatim across `Editor/HMR/` and the LSP server; `AsmdefResolverParityTests` pins the no-asmdef Editor / non-Editor fallback contract. Closes TECH_DEBT_V2 #18 and #19. SG suite 1228/1228 passing (+6 new); LSP suite 63/63 passing (+1 new).

### [1.2.7] - 2026-05-13
- Fix: Go-To-Definition for module/hook symbols across directories. WorkspaceIndex now tracks workspace-wide .uitkx files containing top-level `module` or `hook` declarations and exposes them via `GetModuleAndHookFiles()`. `RoslynHost.FindPeerUitkxFiles` appends them to the per-document peer set, so Roslyn loads the declaring file as a workspace document and resolves symbols like `Theme.SidebarWidth` even when `Theme.uitkx` lives in a different folder. Downstream peer consumers already filter by `HookDeclarations`/`ModuleDeclarations`, so the wider candidate set is cost-free for non-module/hook files. 62/62 LSP server tests passing.

### [1.2.6] - 2026-05-13
- Fix: LSP false "unused local" diagnostic on hook setter/value pairs that are only consumed inside lambdas. `RoslynHost.AnalyzeUnusedLocals` now includes `dataFlow.Captured` in the read set when computing unused-locals diagnostics. Captures by nested lambdas / local functions are by definition a future use; flagging them as unused was a false positive on hook setter pairs (`var (count, setCount) = useState<int>(0);`) consumed inside event-handler lambdas (e.g. `<Button onClick={() => setCount(count + 1)} />`) or JSX fragments lowered to render lambdas. Adds a regression test in `RoslynHostTests`. Ships in lockstep with library 0.5.10 which rewrites component HMR as a static trampoline field (eliminates per-fiber rollback closures on every save, fixes stale renders after rapid Ctrl+S, makes incompatible hook-signature edits reset state in-place without a domain reload - React Fast Refresh semantics) and adds two complementary HMR fixes: rude-edit-triggered domain reloads are deferred from Play mode to the next `EnteredEditMode` transition (calling `RequestScriptReload` during Play mode produced partial reloads that broke `MonoBehaviour` script refs), and HMR Stop no longer stalls 30-40s by dropping `RequestScriptCompilationOptions.CleanBuildCache` from the change-watcher trigger (Roslyn's content-hashed `AdditionalText` cache picks up modified `.uitkx` files via normal incremental compilation without a clean rebuild). LSP server build clean; SG suite 1222/1222 passing (+2 new trampoline-shape parity tests).

### [1.2.5] - 2026-05-12
- Fix: HMR now refreshes module-scope `static readonly` fields without a domain reload (B28). Editing a `Style` or `Color` field initializer inside a `module { ... }` body (e.g. `PaddingTop = 4` -> `16` in `Sidebar.style.uitkx`) used to report a successful HMR cycle but leave the UI on the cold-build value until you exited Play mode - already-mounted components saw the change (their `Render` delegate was hot-swapped) but components mounted afterwards on subsequent navigation kept reading the old reference. Byte-level IL diagnostics confirmed the cause: the Mono JIT inlines `ldsfld <static readonly>` into native code at first call-site emission, so `FieldInfo.SetValue` updates the field slot but already-JIT'd methods continue reading the inlined cold reference. The source generator now strips `readonly` from every top-level `static readonly` field inside a `module { ... }` body and decorates the rewrite with `[global::Ruitk.UitkxHmrSwap]`; HMR mirrors the same rewrite in `HmrHookEmitter.EmitModules` via a hand-written tokenizer so the Editor assembly does not need a direct Microsoft.CodeAnalysis dependency. The two generator-managed module statics (`__sty_N` style hoists, `__uitkx_ussKeys`) get the same treatment. The hook-cache `static readonly ConcurrentDictionary` is intentionally left as `initonly` (its reference is genuinely immutable; only contents are HMR-replaced) and the swapper still matches it via `FieldInfo.IsInitOnly`. The fix is permanent IL and applies to IL2CPP and Mono AOT player builds too - Editor and Player IL stay identical so HMR remains a faithful Player preview. Per-access cost is one extra static-slot load (~1 ns, L1-cached). New Roslyn analyzer `UITKX0210` (Warning) flags writes to `[UitkxHmrSwap]` fields from anywhere other than the containing type's static constructor - the HMR pipeline will overwrite any external write on the next save. Categories: SimpleAssignment, CompoundAssignment, Increment, Decrement. Allowed when the containing symbol is `MethodKind.StaticConstructor`. Suppress with `#pragma warning disable UITKX0210` if intentional. New `Ruitk.UitkxHmrSwap` attribute (in `Shared/Core/`) is the source-generator-emitted marker that opts a field into HMR-managed re-initialization - it is the live semantic distinction between user-immutable module statics (where writes overwrite an initializer the HMR pipeline owns) and ordinary mutable statics (lazy caches, counters) whose value should carry across HMR cycles. Known limitation: static auto-properties (`public static Style Root { get; } = ...`) lower to a private `static readonly` backing field that the source generator cannot see during emission, so the JIT inlines the cold reference and HMR cannot refresh it - prefer fields for HMR-able module values (`public static readonly Style Root = new Style { ... }`). Promotion of static auto-properties into HMR-able backing fields is on the roadmap. Tests: 1218/1218 SG passing (1198 pre-existing plus 20 new - 9 stripper unit tests, 6 analyzer tests, 5 end-to-end module-strip tests). Docs: HMR docs page corrected (module saves no longer claim to trigger a domain reload), diagnostics reference picked up a `UITKX0210` row.

### [1.2.4] - 2026-05-11
- Migrated `[OnOpenAsset]` Console-navigation callbacks to Unity 6.3's EntityId callback shape — `UitkxConsoleNavigation.cs` callbacks split by `#if UNITY_6000_3_OR_NEWER` and call `AssetDatabase.GetAssetPath(EntityId)` / `EditorUtility.EntityIdToObject(EntityId)` directly. Zero `int↔EntityId` conversions, zero `CS0618` warnings on 6.3 / 6.4. Pre-6.3 retains the legacy int callbacks. Common path-resolution and external-editor-launch logic extracted to a shared `ResolveAndDispatch`.
Also fixed `DoomTextures` sample `CS8618` (six lazy-init fields suffixed `= null!`) and added the Unity 6.3 storm-time interactivity caveat to the web API docs.

### [1.2.3] - 2026-05-11
- Feature: SG and HMR `s_hookAliases` rewrite tables and the IDE Roslyn `VirtualDocumentGenerator` stubs now include `useUiDocumentRoot` so the lowercase form participates on identical terms with the rest of the hook suite. 0.5.6 added `Hooks.UseUiDocumentRoot` to the signature regex (hook counting/ordering) but missed three downstream sites — SG `CSharpEmitter.s_hookAliases`, HMR `HmrCSharpEmitter`/`HmrHookEmitter` `s_hookAliases`, and the two `VirtualDocumentGenerator` stub blocks (static-method and instance-method scopes). Result was that `var root = useUiDocumentRoot(uiDoc);` in `.uitkx` produced CS0103 in both the Unity build output and the IDE LSP preview while `Hooks.UseUiDocumentRoot(uiDoc)` worked. All four sites now in sync. Ships in lockstep with library 0.5.7 which adds the Fiber-side commit-phase `EffectFlags.PortalRetarget` path closing bug class 2 of the Unity 6.3 silent `rootVisualElement` rebuild defense (world-space `<Portal target={x}>` chrome was vanishing on Hierarchy click because stable portal children stayed parented to the dead old target — `RootRenderer.Initialize(UIDocument)` from 0.5.6 only handles the host-renderer path, not portals). LSP server build clean; SG `dotnet build` clean (0 errors).

### [1.2.2] - 2026-05-10
- Feature: SG and HMR emitter hook-signature regex extended with `useUiDocumentRoot` / `UseUiDocumentRoot` so the new hook participates in hook detection on identical terms with the rest of the suite. Ships in lockstep with library 0.5.6, which adds the hook itself and the `RootRenderer.Initialize(UIDocument)` overload that defends against the Unity 6.3 regression where `UIDocument.rootVisualElement` is silently recreated on every `InspectorWindow` redraw (confirmed via standalone repro probe — 6.2 fires zero events on the same project, 6.3 fires repeated `DetachFromPanelEvent` → fresh `VisualElement` instance with call stack ending at `UnityEditor.InspectorWindow.RedrawFromNative`; reported to Unity; distinct from UUM-47682 which is closed "By Design" for the UI Builder Live Reload trigger). Six emit sites covered across both pipelines (three in SG `CSharpEmitter`, three in HMR `HmrCSharpEmitter`). No diagnostic-surface or IDE-language behaviour change beyond accepting the new hook name; LSP server build clean.

### [1.2.1] - 2026-05-09
- Feature: JSX `&&` short-circuit now splices cleanly in markup expression positions. The React idiom `{cond && <Tag/>}` is impossible to emit verbatim because C# `&&` is bool-only and `bool && VirtualNode` is CS0019. The splicer now detects a trailing `&&` operator at the end of the prefix preceding any JSX literal in `{expr}` or `attr={expr}` positions and rewrites the expression to a ternary form `((cond) ? V.Tag(...) : (global::Ruitk.Core.VirtualNode?)null)` reusing the already-tested Phase 1 ternary path. The `null` fallback is dropped at render time by `__C(params object[])` which filters nulls. New shared `DirectiveParser.FindLhsStartForLogicalAnd` precedence-aware forward walker locates where the LHS of the `&&` begins inside the surrounding expression — single forward pass over `expr[prev..ampStart]` with per-paren-depth boundary tracking, lexer-aware string/comment skipping, and recognition of `?`, `:`, `??`, `||`, `,`, `;` as boundary tokens at the same paren depth as the `&&`. Mirrored across all four code layers: shared scanner adds the `&&` trigger in `FindBareJsxRanges` and the LHS walker; SG `CSharpEmitter.SpliceExpressionMarkup` and HMR `HmrCSharpEmitter.SpliceExpressionMarkup` emit the ternary desugar (HMR via a third reflection delegate `FindLhsStartFunc` plumbed through `UitkxHmrCompiler`); IDE `VirtualDocumentGenerator.EmitMappedExpressionStrippingJsx` rewrites the same shape to a typed-null ternary placeholder so Roslyn doesn't show a permanent CS0019 squiggle on the `&&` line. Walker degenerate-input failure (e.g. `{ && <X/>}`) emits `#error UITKX0026` with an actionable message instead of cascading into raw-JSX compile errors. Setup-code and directive-body `&&` JSX positions remain unsupported and tracked in TECH_DEBT_V2 item 15. 8 new regression tests in `JsxInExpressionTests` cover simple bool, null comparison, parenthesised LHS, method-call LHS, nested-in-`?:`, nested-in-`||`, bitwise-`&` non-trigger, and the UITKX0026 diagnostic path. 1198/1198 SG tests passing; LSP server build clean.
- Fix: source generator and HMR emitter now inject `using UnityEngine;` into the generated component compilation unit. Six emit sites (three in SG `CSharpEmitter`, three in HMR `HmrCSharpEmitter`) covered the namespace block, the partial-class body, and the function-component overload. The IDE virtual document already pulled `UnityEngine` into scope via its Roslyn workspace, so user code referencing types like `Texture2D`, `Color`, `Vector2`, `Mathf` etc. without an explicit `@using UnityEngine` directive compiled green in the editor but red at build/HMR time. Now both pipelines see the same surface area and the editor-vs-build asymmetry on `UnityEngine.*` symbols is gone. 3 regression tests added covering the namespace-scope, class-scope, and function-component-overload positions.

### [1.2.0] - 2026-05-08
- Breaking: User components now reject any attribute that isn't a declared parameter (or `key`/`ref`). Previously the schema treated all 60 BaseProps members — `style`, `name`, `className`, `onClick`, `extraProps`, `enabledInHierarchy`, etc. — as universal across every tag, so a typo or stale attribute on a user component (`<AppButton style={x}/>` when `AppButton` doesn't declare a `style` parameter) silently produced `Style = x` against the generated `AppButtonProps` class and exploded at C# compile time as CS0117 with no useful pointer back to the .uitkx source. The schema is now split into `structuralAttributes` (just `key` and `ref` — apply everywhere because `key` is a VirtualNode reconciliation slot and `ref` is routed to the unique `Hooks.MutableRef<T>` parameter via `forwardRef`-style semantics) and `intrinsicElementAttributes` (the 58 BaseProps members — only valid on built-in V.* tags that actually back a `VisualElement`). User components no longer inherit the intrinsic set: every attribute you pass must either be in the function-style `(...)` parameter list or be `key`/`ref`. Unknown attributes raise UITKX0109 at Error severity (was Warning) with an actionable hint — `did you mean 'X'?` for close matches, otherwise `Available on '<Comp>': a, b, c. Add a parameter to the component or remove the attribute.` The unknown attribute is also skipped in the generated C# so a single UITKX0109 doesn't cascade into CS0117/CS0246 against the synthesized props class. Editor and build-time error paths (LSP DiagnosticsPublisher.BuildKnownAttributes, source-generator EmitFuncComponent) now share the same element-class-aware attribute map, eliminating the previous editor-shows-red but build-shows-yellow asymmetry. Migration: if you were forwarding `style`/`name`/`className`/etc. through a user component, declare them as explicit parameters (`component AppButton(IStyle? style = null) { ... }`) and forward them yourself in the body. Built-ins are unchanged — `<Button style={...} extraProps={...}/>` still works exactly as before. PropsResolver.GetPublicPropertyNamesByQualifiedName now also has a same-pass peer fallback so cross-file user-component validation works on a clean build before the generated `*Props` symbol exists in compiled metadata. 9 new regression tests added (5 analyzer-level, 4 SG end-to-end) — 1187/1187 SG tests passing. HoverHandler now distinguishes structural-vs-intrinsic in the "Common attributes" section for built-in tags.

### [1.1.15] - 2026-05-08
- Breaking: the `@(expr)` markup-embed syntax has been removed. The canonical and only embed form for arbitrary C# expressions inside markup is now `{expr}` (matching JSX/Babel). The `@` prefix survives only as the directive marker (`@if`, `@else`, `@for`, `@foreach`, `@while`, `@switch`, `@case`, `@default`, `@using`, `@namespace`, `@component`, `@props`, `@key`, `@inject`, `@uss`). Authors who still have legacy `@(expr)` in markup will see a hard parser error UITKX0306 (`@(expr) is no longer supported — use {expr}`). Migration is mechanical: replace every `@(` with `{` and every matching `)` with `}`. The unification removes one of two competing embed forms across the parser, formatter, analyzer, IntelliSense, virtual-document generator, HMR emitter, source generator, TextMate grammar, and all 12 shipped sample files. xmldoc and inline comments updated end-to-end.
- Fix: TextMate grammar failed to load entirely because the JSON file had a UTF-8 BOM (`EF BB BF`) prepended during the Phase 2 edit pass. `vscode-textmate`'s `parseRawGrammar()` rejects the BOM with `Unexpected token "", "{ "$sc..." is not valid JSON`, killing the grammar and forcing every TextMate-scoped token to fall back to plain text. Symptoms: keywords (`public`, `static`, `readonly`, `var`), property names (`FlexGrow`, `BackgroundImage`, `Width`), operators (`=`, `,`, `;`), and braces appeared white inside module/hook bodies and component setup blocks. LSP semantic tokens (markup tags, AST-classified types) still rendered, masking the breadth of the regression. Both grammar copies (`ide-extensions~/grammar/uitkx.tmLanguage.json` and the deployed `ide-extensions~/vscode/syntaxes/uitkx.tmLanguage.json`) are now BOM-free UTF-8 and parse cleanly (37 repository rules loaded). Mojibake'd em dashes in comment fields restored to `—`.
- Fix: source generator and HMR emitter no longer hide pool-rent declarations inside line comments. The naive backward-scan that picked the insertion point for `var __p_N = __Rent<TProps>();` statements stopped at the first `;` or `}` it saw — including `}` characters that lived inside `// see {catBadge}`-style line comments. The result was C# emitted as `// ... {catBadge}var __p_12 = __Rent<...>();` (compiler reads it as part of the comment), then `__p_12` referenced below tripped CS0103 (`The name '__p_12' does not exist in the current context`). Replaced all four sites (two in `CSharpEmitter`, two in `HmrCSharpEmitter`) with a single shared `FindLastTopLevelStatementBoundary` lexer-aware forward scanner that correctly skips line comments, block comments, regular and verbatim and interpolated strings (with `{{`/`}}` escape handling and brace-depth tracking inside interpolation holes), and char literals. Pre-Phase-2 the bug was masked by `@(catBadge)` in the comment text (no `}` to trip on); Phase 2 unification of `@(...)` → `{...}` exposed the latent flaw. 1178/1178 SG tests still pass.
- Fix: `LooksLikeMarkupRoot` (function-style component setup-vs-return discriminator) now accepts a bare `{` opener. With Phase 2, the leading character of an inline markup expression in a return position is `{` (formerly `(` for `@(...)`); the discriminator continued to recognise only the legacy openers and misclassified the entire return body as setup code, producing CS-level cascade errors. Expanded acceptance set covers `(`, `<`, and `{`.
- Fix: `AstCursorContext` IntelliSense block-2a (post-`{` cursor classification) now correctly recognises `{` as the opening of a markup-embed expression rather than as an unrelated brace, so completions inside `<Tag attr={cursor}/>` and `<Box>{cursor}</Box>` resolve under the right scope (C# expression, not markup attribute key).

### [1.1.14] - 2026-05-08
- Feature: JSX literals are now allowed in any C# expression position — matching React/Babel semantics. Patterns that previously emitted raw JSX and tripped Roslyn now splice cleanly to V.Tag(...) factory calls: ternary branches ({cond ? <A/> : <B/>}), null-coalescing ({fallback ?? <Default/>}), JSX in attribute expressions (icon={active ? <Check/> : <X/>}), JSX inside lambda bodies (items.Select(x => <Item key={x.Id}/>)), and the same patterns through @(...) child syntax. The scanner driving this (DirectiveParser.FindBareJsxRanges + FindJsxBlockRanges) was already proven on component preamble and directive bodies; it is now wired into the two remaining emit sites in CSharpEmitter (EmitExpressionNode and the CSharpExpressionValue branch of AttrVal) plus mirrored in HmrCSharpEmitter for hot reload and in VirtualDocumentGenerator so the IDE no longer shows phantom Roslyn errors on files that compile cleanly. Embedded JSX is replaced with a typed-null stub in the virtual document while surrounding C# stays source-mapped for completions and squiggles. Zero runtime impact (same V.Tag/__Rent shape); compile-time cost is a single O(n) scanner pass per expression and returns unchanged when no JSX is present. Backed by 14 new tests across SG, HMR parity, and VDG layers — 1178/1178 passing.
- Fix: nullable component-parameter types written with whitespace before the question mark (e.g. 'Texture2D ? iconName = null', 'Action ? onClick = null') no longer have the '?' silently dropped on save. DirectiveParser.TryReadTypeName required the '?' to immediately follow the type name with no whitespace; with whitespace, the trailing '?' was left unconsumed and the formatter re-emitted the parameter as non-nullable. Same root pathology as the recent @else blank-line bug — formatter re-emit-from-AST is lossy when the parser drops tokens. Fix peeks past whitespace, consumes the '?', and canonicalises the captured type as '<base>?' so the formatter re-emits a clean 'Texture2D? name' regardless of input spacing. Locked down by a new FormatterSnapshotTests case covering Action, Texture2D, and List<int> nullable params with varied whitespace, asserting both idempotency across three format passes and canonical re-emit.

### [1.1.13] - 2026-05-07
- Fix: format-on-save no longer adds a phantom blank line on every pass between '} @else {' (or any directive opener) and a bare-C# body line. AstFormatter.FormatDirectiveBody fallback path passed the raw body code starting with a newline to EmitSetupCodeNormalized, which emitted it as a blank line on top of the newline already written by the directive opener. The parsed-body path already trimmed leading newlines for the same reason; the fallback (no-JSX body) is now symmetric. New regression test asserts three consecutive format passes produce identical output and that no blank line appears between the opener brace and the first body line.

### [1.1.12] - 2026-05-06
- Feature: New <Video> and <Audio> elements plus useSfx() hook for one-shot SFX. <Video> wraps a pooled VideoPlayer + RenderTexture under a shared MediaHost peer (HideAndDontSave GameObject), drives an Image sink via VideoPlayer.frameReady, and exposes a VideoController ref for imperative Play/Pause/Seek. <Audio> renders no visible content and rents an AudioSource from the same pool with declarative Clip/Loop/Volume/Autoplay props plus an AudioController ref. useSfx() returns a stable Action<AudioClip,float> that plays one-shots on MediaHost.Instance.SfxSource without per-render allocations. New MediaPlayground demo (editor window + runtime bootstrap) exercises every surface end-to-end. IDE virtual-document generator now scaffolds useSfx() in both function-style component and hook-document templates so Roslyn no longer reports CS0103 in the editor.
- Fix: FiberRenderer.Clear() now runs effect cleanups before dropping the tree. The previous implementation cleared the container and nulled the root in one shot, never invoking the depth-first CommitDeletion path that fires UseEffect cleanup callbacks and disposes signals. Any UseEffect-owned resource (audio sources, timers, signal subscriptions, RenderTextures, etc.) leaked across editor-window close/reopen cycles. New FiberReconciler.UnmountRoot() walks _root.Current.Child and runs CommitDeletion on each, then nulls the root; FiberRenderer.Clear() calls it before the container clear. The leak only became visible with the new <Audio> element (background music kept playing forever after closing the demo window) but the root cause affected every Func-Component using UseEffect cleanup.

### [1.1.11] - 2026-05-03
- Fix: HMR no longer fails compilation with CS0426: The type name 'RouterFuncProps' does not exist in the type 'RouterFunc' (or any function-component whose Props class is declared as a sibling at namespace scope rather than nested). HMR's FindPropsType previously only walked nested types and shipped {Type}.{Type}Props unconditionally — diverging from the source generator's PropsResolver.TryGetFuncComponentPropsTypeName which already handled all three resolution paths. The HMR resolver now mirrors source-gen: (1) sibling top-level {Type}Props in the same namespace (the RouterFunc/RouterFuncProps shape), (2) nested {Type}.{Type}Props, (3) any nested IProps (legacy ValuesBarFunc.Props), with a final convention fallback. Locked down by 6 new regression tests (1 SG parity + 5 in-memory Roslyn contract tests mirroring the HMR algorithm). 1070/1070 SG passing.

### [1.1.10] - 2026-05-03
- Fix: HMR no longer fails with `ArgumentException: 'List<ParseDiagnostic>' cannot be converted to 'System.String'` on every `.uitkx` save. The compiler's reflective `InvokeWithDefaults` had two competing `params object[]` overloads; C# overload resolution silently bound `string` arguments into the receiver slot of the wrong overload, shifting every subsequent argument by one position and dropping a `List<ParseDiagnostic>` into `Parse(...)`'s `filePath` slot. The two overloads were collapsed into a single canonical `(MethodInfo method, object target, params object[] args)` signature so the bug class is structurally impossible to recur. Hot reload of components, hooks, and module/style files works again.
- Fix: `Asset<T>("./x.png")` and `Asset<T>("../x.png")` now resolve correctly inside `module { ... }` and `hook { ... }` bodies (e.g. `AppRoot.style.uitkx`). Both the source generator's `ModuleEmitter`/`HookEmitter` and HMR's `HmrHookEmitter` previously emitted module/hook bodies verbatim, leaving relative path literals unrewritten — the runtime `UitkxAssetRegistry.Get<T>` then missed because the registry indexes by resolved Unity asset paths (e.g. `Assets/Resources/background-01.png`). The path-rewrite pipeline that already covered component setup code, JSX attributes, and directive bodies now also runs on module/hook bodies in both source-gen and HMR. Backed by 4 new regression tests in `EmitterTests` (1064/1064 SG passing).

### [1.1.9] - 2026-05-03
- Fix: `Asset<T>(...)` and `Ast<T>(...)` no longer report CS0103 (`The name Asset does not exist in the current context`) inside component setup blocks, hook bodies, and module/style initializers (e.g. `AppRoot.style.uitkx`). The IDE virtual document now injects `using static Ruitk.AssetHelpers;` so Roslyn binds the helpers exactly the way the runtime emitter does.

### [1.1.8] - 2026-05-03
- Feature: schema entries for the new router primitives — <Outlet/>, <Routes>, <NavLink>, <Navigate> — with full attribute metadata. Autocomplete and inline docs work in VS Code, Rider, and Visual Studio without per-IDE changes (single uitkx-schema.json source).
- Feature: <Router> schema gained 'basename' attribute; <Route> schema gained 'index' and 'caseSensitive' attributes. Layout-route description on <Route> now mentions Element + child Routes co-existence.

### [1.1.7] - 2026-05-01
- Feature: autocomplete for IStyle 9-slice + clip-box + text-generator + editor-text-rendering enums (unitySliceType, unityOverflowClipBox, unityTextGenerator, unityEditorTextRenderingMode)

### [1.1.6] - 2026-04-28
- Perf: source generator now hoists `style={new Style{...}}` literals to class-level static fields, eliminating per-render Style allocations and diff-walks for static styles. Handles both setter form (`Width = 5f`) and tuple form (`(StyleKeys.Width, 5f)`). Whitelist-based; falls back to existing pool-rent path for any non-literal value (state/captures/method calls).
- Perf: source generator emits child arguments directly into `params VirtualNode[]` instead of allocating a transient `__C(...)` array when the JSX children list is statically simple (no spreads, no conditional fragments). Cuts per-frame allocations on the children path.

### [1.1.5] - 2026-04-27
- Fix: cross-document Ref<T> unification — useRef<T>() now returns canonical global::Ruitk.Core.Ref<T> in virtual docs, eliminating false CS1503 when passing refs to peer hooks (e.g. useGalagaGame(boardRef))
- Fix: polyfill stubs (Ref<T>, ReactiveEvent, etc.) now load correctly in workspaces where Unity has not yet compiled — detection scoped to runtime assembly names (Ruitk.Shared/Runtime/Core), no longer suppressed by the LSP-internal Ruitk.Language.dll
- Fix: formatter idempotency for multi-line paren-continuation (e.g. && / || chains) inside nested blocks — no more indent drift on save in files like the Galaga GameScreen sample
- Fix: source-generator diagnostic IDs unified with live analyzer (UITKX0006/0002/0009/0010/0017/0022/0023 → UITKX0103/0109/0106/0104/0108/0120/0121) so the same logical issue surfaces with the same code in both Unity Console and VS Code Problems pane
- Fix: RS2008 'analyzer release tracking' build warning suppressed in the LSP server csproj (warning targets analyzer NuGet packages, not the server EXE)

### [1.1.4] - 2026-04-18
- Fix: UITKX0105 false positives eliminated — unknown-element diagnostics suppressed until workspace scan completes
- Fix: @(__children) no longer produces CS0103 in function-style components

### [1.1.3] - 2026-04-17
- Fix: VDG inline expression checks no longer cause false code dimming past directive boundaries
- Fix: formatter handles deeply nested structures correctly (22 idempotency regressions resolved)
- Fix: improved diagnostic line mapping for UITKX0014, UITKX0013, and CS0219
- Fix: workspace scan now discovers components when VS2022 provides no rootUri (fixes UITKX0105 false positives, go-to-definition, and F2 rename)

### [1.1.2] - 2026-04-11
- Feature: capture-phase event handlers (onClickCapture, onKeyDownCapture, onChangeCapture, etc.) — every bubble event now has a Capture suffix variant that fires during trickle-down
- Feature: refactored ApplyEvent/RemoveEvent with generic RegisterEvent<T>/UnregisterEvent<T> helpers — zero-overhead, no behavioral change
- Fix: removed dead diagnostic counters (totalStyleSets, totalStyleResets, totalEventsRegistered, totalEventsRemoved) and unused GetStyleMetrics()
- Feature: IDE autocompletion and IntelliSense for all capture-phase event props

### [1.1.1] - 2026-04-11
- Fix: hook/module files with arbitrary middle segments (e.g. Counter.hook.uitkx, Counter.custom.uitkx) no longer produce invalid C# class names — container class derivation now truncates at the first dot instead of relying on a suffix whitelist

### [1.1.0] - 2026-04-10
- Feature: full IDE support for hook companion files (.hooks.uitkx) — parsing, diagnostics, hover, completions, coloring, formatting
- Feature: full IDE support for module companion files (.style.uitkx, .utils.uitkx) — parsing, diagnostics, hover, completions, coloring, formatting
- Feature: cross-file peer resolution reads editor buffers instead of disk — no stale diagnostics when editing companion files
- Feature: hover shows type info for local variable, parameter, and field declarations
- Feature: hover shows delegate invoke signature (e.g. void Action(int value)) instead of raw type name
- Fix: CS1662 lambda cascade errors suppressed when caused by state-setter type mismatches
- Fix: removed 7 hot-path log calls that fired on every keystroke, rebuild, or hover
- Feature: hook and module keywords colored as keywords in the classifier
- Feature: hover tooltip correctly renders (kind) prefix for Roslyn-style hover format

### [1.0.85] - 2026-04-09
- Feature: delegate-typed variables (useState setters, callbacks) now colored as functions in both VS Code and VS2022
- Fix: generic function calls like useState<string>() no longer lose function coloring
- Feature: custom notification channel (CustomMessageTarget) for server-to-client classification overrides

### [1.0.84] - 2026-04-08
- Fix: unreachable code after return now correctly dims entire scope without squiggly lines
- Fix: stale gray no longer persists after removing return statement — unreachable diagnostics stripped from T3 carry-forward cache
- Fix: unreachable code now renders fully gray — custom high-priority classification overrides competing VS2022 LSP syntax colors

### [1.0.84] - 2026-04-08
- Fix: eliminated color flickering/dancing in unreachable code regions caused by redundant classification sources and didChange spam
- Fix: centralized buffer sync via BufferSyncService — deduplicated 6 independent didChange senders to eliminate redundant server re-parses
- Fix: classifier subscriber leak — stale instances no longer accumulate on static diagnostic events

### [1.0.83] - 2026-04-07
- Fix: CI pipeline now correctly packages LSP server binaries in VSIX — clean marketplace installs no longer fail with 'no launch strategy succeeded'

### [1.0.82] - 2026-04-07
- Breaking: comment syntax changed — {/* */} replaced with // (line) and /* */ (block) in markup
- Fix: @(expr) inline expressions now type-checked as VirtualNode in IDE diagnostics
- Fix: UITKX0025 single-root validation now covers var x = (<A/><B/>)
- Fix: formatter uses block diff (single TextEdit) — eliminates corruption on blank-line variations
- Fix: formatter bare-return idempotency — first format pass now matches canonical form
- Fix: formatter no longer collapses empty container elements to self-closing
- Fix: semantic tokens for // and /* */ comments in markup
- Fix: simplified comment handler — always uses // line comments, removed context-dependent {/* */} logic

### [1.0.81] - 2026-04-05
- Feature: @uss and Asset<T>("...") path completion — filesystem-aware autocomplete inside quoted paths
- Feature: @uss added to preamble autocomplete and uitkx-schema.json directive list
- Feature: Rules of Hooks diagnostics (UITKX0013–0016) now reported in the IDE with accurate squiggle positions

### [1.0.80] - 2026-04-04
- Feature: control block bodies — @if, @for, @foreach, @while, @switch cases now support setup code before return() (var declarations, lambdas, local computation)
- Feature: switch fallthrough — adjacent @case labels with no body share the same branch
- Feature: Rules of Hooks diagnostics (UITKX0013–0016) now reported in the IDE — hooks called inside @if, @foreach, @for, @while, @switch, or event-handler attributes are flagged as errors
- Fix: @foreach with setup code generated invalid C# (double-brace bug in IIFE emitter)
- Fix: ResolveAssetPaths now applied inside all control block bodies (SG + HMR emitters)
- Fix: removed dead @code, @break, @continue grammar rules and schema entries

### [1.0.79] - 2026-04-02
- Breaking: CssHelpers renamed all shortcuts with consistent prefixes for autocomplete discoverability
- Feature: compound struct factory helpers in CssHelpers (BgRepeat, BgPos, BgSize, Origin, Xlate, Easing + 24 easing presets)
- Feature: TextAutoSizeMode support across all layers (StyleKeys, Style, CssHelpers, PropsApplier, schema, LSP)
- Feature: LSP style value completions for backgroundRepeat, backgroundPosition, backgroundSize, transitionTimingFunction

### [1.0.78] - 2026-04-01
- Feature: LSP enum value completions for CssHelpers shortcuts (PickingMode, SelectionType, ScrollerVisibility, LanguageDirection, SliderDirection, ScrollViewMode, ScaleMode, TwoPaneSplitViewOrientation, ColumnSortingMode)

### [1.0.78] - 2026-03-29
- Fix: updated plan documents to reflect actual implementation status

### [1.0.77] - 2026-03-29
- Fix: formatter no longer expands empty open+close elements to multi-line
- Feature: LSP attribute-level version filtering in completions and UITKX0200 diagnostics
- Docs: component reference pages now show inline Unity docs link next to title

### [1.0.76] - 2026-03-28
- Fix: formatter no longer collapses empty open+close tags to self-closing (<Box></Box> is preserved)
- Fix: tag completion no longer inserts closing tag for elements that accept children

### [1.0.75] - 2026-03-28
- Feature: native LSP routing via CodeRemoteContentTypeName — removes 3 custom GoToDefinition handler files
- Feature: Find All References (Shift+F12) now works natively through VS2022 LSP framework

### [1.0.74] - 2026-03-28
-  Fix: tag completion no longer inserts closing tag snippet when editing an existing tag name

### [1.0.73] - 2026-03-28
- Fix: autocomplete no longer inserts duplicate ={} when attribute already has a value binding

### [1.0.72] - 2026-03-27
- Feature: @uss directive with formatter, syntax highlighting, and UITKX0022/0023 SG diagnostics
- Feature: HMR support for @uss — .uss file changes trigger hot-reload of dependent components

### [1.0.71] - 2026-03-28
- Feature: UITKX0121 — LSP error when Asset<T> type is incompatible with file extension
- Feature: Extension-to-type validation map covering 20+ file types (images, audio, fonts, materials, USS)

### [1.0.70] - 2026-03-27
- Feature: UITKX0120 — LSP error squiggle when Asset/Ast path references a file that does not exist
- Feature: Asset<T>/Ast<T> stub support in LSP virtual document for IntelliSense

### [1.0.69] - 2026-03-27
- Rename Style.TextColor to Style.Color to match StyleKeys and Unity IStyle naming
- Remove classic directive mode — function-style only
- Centralized changelog system: single changelog.json source of truth, CI generates per-IDE changelogs
