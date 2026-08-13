---
name: add-unity-version
description: Add support for a new Unity version to ReactiveUIToolkit. Run when a new Unity release is available (user says "add Unity 6.5 support", "new Unity version came out", etc.). Discovers API changes via the automation~ diff scripts, classifies them, and implements support across all layers (PropsApplier/Style/StyleKeys/CssHelpers, schema, LSP, docs site) plus record-keeping.
---

# Add Unity Version Support

You are adding support for a new Unity version to the **ReactiveUIToolkit** package.
The user will tell you which version was released (e.g. "Unity 6.5 / 6000.5").

Follow these phases **in order**. Do not skip steps. Mark each step done as you go.

---

## Prerequisites

Read these files for full context before starting:

- [VERSIONING_PROCESS.md](../../../Plans~/VERSIONING_PROCESS.md) — Full reference with discovery sources (§1), classification taxonomy (§2), version matrix (§3), implementation checklists (§4), and codebase surface inventory (§8)
- [AUTOMATION.md](../../../AUTOMATION.md) — Overview of automation tooling

---

## Phase 1: Discovery — What Changed

### Step 1.1: Run the API diff script

Run the assembly-comparison script to get an exhaustive, accurate diff.
Both Unity versions must be installed via Unity Hub.

```powershell
.\automation~\unity-api-diff.ps1 -From {PREVIOUS_VERSION} -To {NEW_VERSION} -OutFile .\automation~\diff-reports\{PREV}-to-{NEW}.json
```

Read the output JSON. It contains:
- `istyle.added` / `istyle.removed` / `istyle.changed` — IStyle property changes
- `elements.added` / `elements.removed` — VisualElement subclass changes
- `enums.types.added` + `enums.members` — Enum type and value changes
- `structs.added` / `structs.removed` — New struct types

### Step 1.2: Check Unity docs for narrative context

Fetch these two pages and look for the **UI Toolkit** section:

- **What's New:** `https://docs.unity3d.com/{VERSION}/Documentation/Manual/WhatsNewUnity{MAJOR}{MINOR}.html`
- **Upgrade Guide:** `https://docs.unity3d.com/{VERSION}/Documentation/Manual/UpgradeGuideUnity{MAJOR}{MINOR}.html`

Where `{VERSION}` = e.g. `6000.5`, `{MAJOR}` = `6`, `{MINOR}` = `5`.

The docs give you:
- **Why** things were added (feature context)
- **Breaking changes** not visible in the API diff (behavior changes, parser changes)
- **Deprecation warnings** with migration guidance

### Step 1.3: Compile the change list

Produce a structured summary combining the script output and doc findings:

```
ADDITIONS:
  IStyle properties: [list each with name, C# type]
  Elements: [list each]
  Enum values: [list each enum + new members]
  Struct types: [list each]

DEPRECATIONS:
  [list with: what, deprecated in, replacement]

REMOVALS:
  [list with: what, removed in]

BEHAVIOR CHANGES:
  [list any from Upgrade Guide]
```

---

## Phase 2: Classification — Map to Checklists

For each change, determine the implementation path. Reference §2 and §4 of VERSIONING_PROCESS.md.

| Change Type | Checklist | Files to Touch |
|-------------|-----------|----------------|
| New IStyle property | §4.1 (6 files) | PropsApplier, Style, StyleKeys, CssHelpers, schema, matrix |
| New element | §4.2 (8+ files) | New Props class, V.cs, Registry, PropsResolver, HMR, schema, matrix |
| New enum value | §4.3 (3 files) | PropsApplier, CssHelpers, schema |
| Deprecation | §4.4 (6 files) | Tracker, PropsApplier, Style, CssHelpers, schema, matrix |
| Removal | §4.5 (7 files) | PropsApplier, Style, CssHelpers, StyleKeys, schema, matrix |

---

## Phase 3: Implementation — Execute the Checklists

### For each NEW IStyle property:

1. **PropsApplier.cs** (`Shared/Props/PropsApplier.cs`)
   - Add setter in the `styleSetters` dictionary (~line 41 area)
   - Wrap in `#if UNITY_{VERSION}_OR_NEWER` / `#else` no-op
   - Include type conversion logic matching existing patterns (look at nearby setters for the pattern)
   - Add resetter in `styleResetters` dictionary (~line 558 area)
   - Same `#if` guard, pattern: `e.style.{prop} = StyleKeyword.Null`

2. **Style.cs** (`Shared/Props/Typed/Style.cs`)
   - Add typed set-only property wrapped in `#if`
   - Pattern: `public {StyleType} {PascalName} { set => this["{camelKey}"] = value; }`

3. **StyleKeys.cs** (`Shared/Props/Typed/StyleKeys.cs`)
   - Add `public const string {PascalName} = "{camelKey}";`
   - Wrap in `#if UNITY_{VERSION}_OR_NEWER` guard (same as PropsApplier)
   - **Must stay in sync with `stylePropertyCatalog.ts`** — every catalog entry's
     `name` field must have a matching `StyleKeys.{Name}` constant

4. **CssHelpers.cs** (`Shared/Props/Typed/CssHelpers.cs`)
   - Add shortcut methods/properties if the type is enum-like or struct-based
   - Wrap in `#if` guard

5. **uitkx-schema.json** (`ide-extensions~/grammar/uitkx-schema.json`)
   - Add entry to `styleKeyValues` if the property has enumerable values
   - Add `sinceUnity` annotation to the entry

6. **VERSIONING_PROCESS.md** → Update §3.1 IStyle Properties table
   - Add row, mark status as ✅

### For each NEW element:

Follow §4.2 checklist. Key files:
1. Create `Shared/Props/Typed/{Name}Props.cs` extending `BaseProps`
2. Add factory method in `Shared/Core/V.cs`
3. Add `RegisterIfAllowed()` in `Shared/Elements/ElementRegistryProvider.cs`
4. Add fallback map entry in `SourceGenerator~/Emitter/PropsResolver.cs`
5. Add tag map entry in `Editor/HMR/HmrCSharpEmitter.cs`
6. Add element definition in `uitkx-schema.json`
7. Update §3.2 Elements table

### For each NEW enum value:

Follow §4.3 checklist. Update PropsApplier conversion + CssHelpers + schema.

### For DEPRECATIONS:

Follow §4.4 checklist. Add `[Obsolete]` attributes behind `#if` guards.

### For REMOVALS:

Follow §4.5 checklist. Wrap in `#if !UNITY_{VERSION}_OR_NEWER` guards.

---

## Phase 4: Schema & LSP Updates

1. **Apply diff to schema** (optional automation):
   ```bash
   node automation~/apply-diff-to-schema.mjs automation~/diff-reports/{PREV}-to-{NEW}.json {NEW_VERSION}
   ```
   This auto-patches `uitkx-schema.json` with `sinceUnity` / `removedIn` annotations for
   elements, attributes, and style properties found in the diff.
2. **uitkx-schema.json** — Verify all new entries have `"sinceUnity": "{VERSION}"` annotations
3. **SchemaLoader.cs** — No changes needed (version fields already supported)
4. **CompletionHandler** — Will auto-deprioritize items above user's version (already wired)
5. **DiagnosticsPublisher** — Will auto-warn on version mismatches (already wired)

---

## Phase 4.5: Documentation Website Updates

Update the docs site so the website reflects the new version.

### Important conventions

- **Code block language:** Always use `language="tsx"` for `<CodeBlock>` components.
  Never use `"csharp"` — the site highlighter does not support it and renders grey.
- **Style properties:** Each new property needs a **rich documentation card** with type,
  description, code example, and related CssHelpers — not just a name + badge row.
- **New elements:** Each new element requires a **full dedicated page** with usage
  examples, props table, and event list — not just a table entry.

### Steps

1. **versionManifest.ts** (`ReactiveUIToolkitDocs~/src/versionManifest.ts`)
   - Add entry to `SUPPORTED_VERSIONS`: `{ version: '{VERSION}', label: '{MAJOR}.{MINOR}' }`
   - Add entries to `STYLE_PROPERTY_VERSIONS` for each new IStyle property:
     `{camelName}: { sinceUnity: '{VERSION}' }`
   - Add **rich** entries to `STYLE_PROPERTY_DETAILS` for each new IStyle property:
     `{camelName}: { sinceUnity: '{VERSION}', type: '...', description: '...', example: '...', relatedHelpers?: [...] }`
   - Add entries to `ELEMENT_VERSIONS` for each new element:
     `{ElementName}: { sinceUnity: '{VERSION}' }`
   - Add entries to `CSS_HELPER_VERSIONS` for each new CssHelper

2. **stylePropertyCatalog.ts** (`ReactiveUIToolkitDocs~/src/pages/UITKX/Styling/stylePropertyCatalog.ts`)
   - This is the **data file** that drives the entire Styling reference page.
   - Add a `PropertyCard` entry for each new IStyle property:
     ```ts
     { key: 'camelKey', name: 'PascalName', type: 'StyleType',
       category: 'Filter & Effects', sinceUnity: '{VERSION}',
       description: '...', typedExample: 'PascalName = ...',
       untypedExample: '(StyleKeys.PascalName, ...)', helpers: ['...'] }
     ```
   - **`untypedExample` must use `StyleKeys.X` format** — not raw strings like `("key", val)`.
     This matches how users actually write the tuple syntax.
   - The page (`StylingPage.tsx`) auto-renders from this catalog — no TSX changes needed
     for new properties.
   - Category must be one of: `Layout`, `Positioning`, `Spacing`, `Flexbox`, `Border`,
     `Text`, `Colors`, `Enum Styles`, `Background`, `Transforms`, `Assets`, `Filter & Effects`

3. **StylingPage.tsx** — Only edit this file if:
   - New CssHelper functions were added → update the CssHelpers reference table (inline MUI table)
   - New enum values were added → update the Enum shortcuts table (inline MUI table)
   - New type categories were added → update the Type reference table
   - These tables are hardcoded JSX, not data-driven

4. **New element doc pages** (if elements were added)
   - For each new element:
     - Create a **full component page** in `ReactiveUIToolkitDocs~/src/pages/Components/{Name}/`
       with usage examples, props table, event list, and code samples using `language="tsx"`
     - Add page entry in `pages.tsx` with `sinceUnity: '{VERSION}'`
     - Add `unityDocLinks.ts` entry: `{Name}: { unityElement: '{Name}' }`
   - Pages with `sinceUnity` are automatically hidden when user selects an older version

5. **Verify docs build:**
   ```powershell
   cd ReactiveUIToolkitDocs~; npm run build
   ```

---

## Phase 5: Verification

1. **Build the runtime package** — Verify no compile errors on the floor version (6.2)
2. **Build with new Unity** — If the new version is installed, verify `#if` guards compile correctly
   - **Also probe the editor's bundled tooling layout** — `UitkxHmrCompiler.FindCompilerPaths`
     depends on where the editor ships Roslyn and the dotnet host. 6000.5 silently moved csc
     from `Data/DotNetSdkRoslyn/csc.dll` into `Data/DotNetSdk/sdk/<ver>/Roslyn/bincore/csc.dll`
     (version-numbered!), which broke Start HMR with no API diff to warn about it. Check
     `Data/` for layout drift on every new editor line and extend the probe, never rewrite it.
3. **Build LSP server:**
   ```powershell
   cd ide-extensions~/lsp-server; dotnet build
   ```
4. **Run LSP tests:**
   ```powershell
   cd ide-extensions~/lsp-server/Tests; dotnet test
   ```

---

## Phase 6: Record-Keeping

1. **Version Audit Log** — Add row to §3.4 of VERSIONING_PROCESS.md:
   ```
   | {VERSION} | {DATE} | AI-assisted | {summary of IStyle diff} | {summary of elements diff} | {breaking changes} |
   ```

2. **Deprecation Tracker** — If any deprecations found, add to §3.3

3. **Plans~/REMAINING_WORK.md** — Update relevant entries if any tracked work was resolved or created

---

## Important Patterns & Conventions

- **`#if` guard format:** `UNITY_{MAJOR}_{MINOR}_OR_NEWER` — e.g. `UNITY_6000_3_OR_NEWER`
- **No-op fallback:** When a property doesn't exist on older Unity, the setter should silently do nothing (no error)
- **camelCase keys:** IStyle properties use camelCase (`aspectRatio`), our StyleKeys use PascalCase (`AspectRatio`)
- **StyleKeys ↔ catalog sync:** Every entry in `stylePropertyCatalog.ts` must have a matching `StyleKeys.{Name}` constant in `StyleKeys.cs`. Non-floor keys need `#if` guards in both files.
- **Untyped examples:** Always use `StyleKeys.X` format (`(StyleKeys.Width, 200f)`), never raw strings (`("width", 200f)`)
- **Floor version:** 6.2 (6000.2) — everything at or below this needs no guard
- **Schema sinceUnity format:** String like `"6000.3"` matching the `UnityVersion.TryParse` format
- **Code block language:** Use `language="tsx"` for `<CodeBlock>` components. The CssHelpers/Enum reference tables are MUI `<Table>` components (not CodeBlocks)
