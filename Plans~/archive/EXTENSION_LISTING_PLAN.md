# Extension marketplace-listing overhaul — UNITY leg (family campaign, owner directive 2026-07-16)

> **The problem (owner-reported):** all six family extensions appear on the VS Marketplace
> publisher page as bare acronyms — `UITKX`, `UETKX`, `GUITKX`, each twice (VS Code + VS2022) —
> indistinguishable from one another, and this repo's listings carry the thinnest
> descriptions/bodies of the family.
>
> **The fix (all three repos, same canonical scheme):** distinguishable display names, and every
> extension page structured **Title → Description → Features → Requirements → Changelog**.
> Sibling plans: `ReactiveUI-Unreal/plans/EXTENSION_LISTING_PLAN.md` (the reference
> implementation — its §1 defines the family scheme) and
> `ReactiveUI-Gadot/plans/EXTENSION_LISTING_PLAN.md`.
>
> **Execution notes:** this folder is NOT a git repository — edits are direct file changes.
> The publish flow is the PowerShell scripts + guide under `ide-extensions~/docs/`
> (`VS2022_PUBLISH_GUIDE.md` + `vscode-publish.md` + `visual-studio-publish.md`) — follow them
> for every release mechanic. The Rider plugin (JetBrains marketplace) is OUT OF SCOPE here.

## §0 Where every marketplace-visible string lives (researched 2026-07-16; paths relative to `Assets/ReactiveUIToolkit/`)

| Surface | VS Code | VS2022 |
|---|---|---|
| List/page title | `ide-extensions~/vscode/package.json` → `displayName` (currently `"UITKX"`, v1.4.2) | `ide-extensions~/visual-studio/UitkxVsix/source.extension.vsixmanifest` → `<DisplayName>` (line ~10, currently `UITKX`, Version 1.4.2) |
| Short description | package.json `description` — currently the THIN "Language support for .uitkx ReactiveUIToolkit component templates" (rewrite, §1) | vsixmanifest `<Description>` (same thin sentence + one clause — rewrite, §1) |
| Page body | `ide-extensions~/vscode/README.md` (exists: Features/Requirements present, H1 is `# UITKX — VS Code Extension`, no Changelog section) | `UitkxVsix/overview.md` — COMMITTED, generated from `UitkxVsix/overview-template.md` + `ide-extensions~/changelog.json` via `scripts/changelog.mjs extract-overview` (the template is currently THIN — flesh out, §2) |
| Changelog | `ide-extensions~/changelog.json` is the single source; per-IDE CHANGELOGs + the overview's `## Changelog` section generate from it via `scripts/changelog.mjs` | same |
| Version | package.json `version` | vsixmanifest `Identity Version` |

**⚠ Version reconciliation FIRST:** local manifests say **1.4.2** but the marketplace showed
**1.4.1** on 2026-07-16. Before bumping anything, check the live marketplace version of BOTH
extensions. If 1.4.2 is still unpublished, fold this listing work into 1.4.2 (no new bump);
if 1.4.2 went out, bump both to 1.4.3 (listing-only changes still bump — shipped bytes change).

## §1 Canonical strings (family-wide — defined in the Unreal plan §1, do not improvise)

| Field | New value |
|---|---|
| package.json `displayName` | `UITKX (Unity - VS Code)` |
| vsixmanifest `<DisplayName>` | `UITKX (Unity - VS2022)` |
| VS Code body H1 | `# Reactive UI - Unity - VS Code (UITKX)` |
| VS2022 body H1 (overview-template) | `# Reactive UI - Unity - VS2022 (UITKX)` |
| package.json `description` | `Syntax highlighting + language intelligence for .uitkx markup (ReactiveUIToolkit for Unity). Completions, hover, diagnostics and formatting from the bundled language server — fully offline, no running Unity editor required. Discord: https://discord.gg/Knedqu4Wyv` |
| vsixmanifest `<Description>` | same sentence (post-2026-07-16 update: Unity leg appends the Discord link; family parity with Unreal/Godot not yet reconciled). ⚠ HARD LIMIT: VS Marketplace rejects `<Description>` ≥ 280 chars (VsixPub0024, found the hard way in Publish #109) — repo link deliberately NOT in the description (it lives in the dedicated `repository.url`/`<MoreInfo>` fields both marketplaces already display). |

Body structure (both templates, this exact order): H1 → description paragraph(s) →
`## Features` → `## Requirements` → Changelog section (generated — the template file ENDS
after Requirements). Keep the Discord link line (`https://discord.gg/Knedqu4Wyv`) in the
description block — it is this repo's convention.

**Content rule: PRESERVE existing prose.** The current README's feature bullets
(highlighting, completions, hover, bracket/folding) and the `.NET 8+` requirement survive
verbatim; the VS2022 template gets fleshed out to the SAME feature list (it is currently
3 thin bullets — bring it up to the README's level).

## §2 File changes

1. `ide-extensions~/vscode/package.json` — `displayName` + `description` per §1.
2. `ide-extensions~/visual-studio/UitkxVsix/source.extension.vsixmanifest` — `<DisplayName>`
   + `<Description>` per §1.
3. **NEW `ide-extensions~/vscode/readme-template.md`** — the current README.md restructured
   per §1 (ends after Requirements).
4. `UitkxVsix/overview-template.md` — H1 per §1 + fleshed-out Features/Requirements.
5. **Generate + commit both outputs** (check `node scripts/changelog.mjs` usage and the
   changelog.json `versions` keys first — use the ide keys THIS repo's json actually uses):
   ```bash
   node scripts/changelog.mjs extract-overview --ide vscode \
     --template ide-extensions~/vscode/readme-template.md \
     --out ide-extensions~/vscode/README.md
   node scripts/changelog.mjs extract-overview --ide vs2022 \
     --template ide-extensions~/visual-studio/UitkxVsix/overview-template.md \
     --out ide-extensions~/visual-studio/UitkxVsix/overview.md
   ```
   (Run from `Assets/ReactiveUIToolkit/`; adjust `--ide` values if this repo's json uses
   different keys — verify, don't assume.)
6. `.vscodeignore` — exclude `readme-template.md` from the .vsix (README.md stays included).
7. `scripts/changelog.mjs` — extend `verify` (if this port has one; otherwise add the check to
   the publish scripts): when `readme-template.md` exists, recompose and byte-compare the
   committed README.md; fail with the regeneration command on drift. Same for the committed
   overview.md. Generated-and-committed files always get a drift gate (family scar).

## §3 Release mechanics (this repo's publish flow)

1. Reconcile versions per §0 ⚠ first.
2. Changelog entry (one per change) through `scripts/changelog.mjs add` — check its usage for
   the exact flags this port takes; message content:
   - "Marketplace listing overhaul: distinguishable display names — `UITKX (Unity - VS Code)` /
     `UITKX (Unity - VS2022)` — and a structured page body (Title / Description / Features /
     Requirements / Changelog) on both marketplaces + Open VSX."
3. Regenerate every generated target the entry names (per-IDE CHANGELOGs, README.md,
   overview.md) and commit them together with the json.
4. Publish per `ide-extensions~/docs/VS2022_PUBLISH_GUIDE.md`:
   `scripts/publish-extension.ps1` (VS Code Marketplace + Open VSX) and
   `scripts/publish-vsix.ps1` (VS Marketplace) — use their `-BumpVersion -ChangelogEntry`
   integration if reconciliation (§0) calls for a bump; PATs come from
   `publisher-secrets.json` (`vscePatToken` etc.). **The owner runs the publish scripts.**

## §4 What does NOT change

- publisher (`ReactiveUITK`), extension ids (`uitkx`, `UitkxVsix.ReactiveUITK`) — renaming ids
  orphans installs. Display strings only.
- The Rider plugin (`ide-extensions~/rider/`) — JetBrains marketplace, separate campaign if the
  owner wants matching names there.
- The lsp-server, grammar, icons, categories.

## §5 Doc upkeep (part of this campaign; DONE — see also `.claude/skills/changelog/SKILL.md`, which now carries these rules)

Add a **"Marketplace listing surfaces"** section to
`ide-extensions~/docs/VS2022_PUBLISH_GUIDE.md` carrying the §0 table + the §1 naming scheme +
two rules: (a) listing-only changes still bump + changelog, (b) edit the TEMPLATE and
regenerate — never hand-edit README.md / overview.md.

## §6 Post-publish verification

- Publisher page rows read `UITKX (Unity - VS Code)` / `UITKX (Unity - VS2022)`.
- `items?itemName=ReactiveUITK.uitkx` + `items?itemName=ReactiveUITK.uitkx-visualstudio`
  (the VS2022 marketplace ID per the publish guide's Quick Reference): §1 body structure
  including the Changelog section.
- Open VSX listing shows the same README.
