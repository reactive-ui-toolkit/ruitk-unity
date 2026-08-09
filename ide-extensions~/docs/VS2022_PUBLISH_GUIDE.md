# UITKX Extension Publish Guide

How to build, test, and publish the UITKX extensions for **VS Code** and **Visual Studio 2022**.

---

## Architecture Overview

Both extensions share the same **LSP server** (`ide-extensions~/lsp-server/`):

```
ide-extensions~/
├── lsp-server/          ← .NET 8.0 LSP server (shared)
├── language-lib/         ← netstandard2.0 parsing library (shared)
├── vscode/               ← VS Code extension (TypeScript + esbuild)
└── visual-studio/
    └── UitkxVsix/        ← VS2022 extension (net472, MEF-based)
```

The LSP server is published into each extension's `server/` directory.

---

## Quick Reference

| Action | VS Code | VS2022 |
|--------|---------|--------|
| **Publish script** | `scripts/publish-extension.ps1` | `scripts/publish-vsix.ps1` |
| **Bump + publish** | `-BumpVersion -ChangelogEntry "..."` | `-BumpVersion -ChangelogEntry "..."` |
| **Local only** | `-LocalOnly` | `-LocalOnly` |
| **Skip server build** | `-SkipServerBuild` | `-SkipServerBuild` |
| **Version file** | `ide-extensions~/vscode/package.json` | `source.extension.vsixmanifest` |
| **Changelog file** | `ide-extensions~/vscode/CHANGELOG.md` | `ide-extensions~/visual-studio/UitkxVsix/overview.md` |
| **PAT source** | `publisher-secrets.json → vscePatToken` | Same PAT, same file |
| **Marketplace ID** | `ReactiveUITK.uitkx` | `ReactiveUITK.uitkx-visualstudio` |
| **Build tool** | `npm run build` + `vsce package` | MSBuild (VS2022) + CreateVsixContainer |
| **Publish tool** | `vsce publish --pat` | `VsixPublisher.exe publish` |
| **Local install** | `code --install-extension` | `VSIXInstaller.exe /quiet` |

---

## Marketplace Listing Surfaces (what users SEE — know before touching)

Every marketplace-visible string has exactly one source; edit the source, never the output:

| Surface | VS Code | VS2022 |
|---------|---------|--------|
| List/page title | `vscode/package.json` → `displayName` | `UitkxVsix/source.extension.vsixmanifest` → `<DisplayName>` |
| Short description | package.json `description` | vsixmanifest `<Description>` |
| Page body | `vscode/README.md` (packaged into the .vsix; also the Open VSX page) | `UitkxVsix/overview.md` — generated from `overview-template.md` + `changelog.json` via `scripts/changelog.mjs extract-overview`; committed |
| Changelog | `changelog.json` is the single source; per-IDE CHANGELOGs + the overview's `## Changelog` section generate from it | same |

- **Naming scheme (family-wide, 2026-07-16):** display name = `UITKX (Unity - VS Code)` /
  `UITKX (Unity - VS2022)`; body H1 = `Reactive UI - Unity - <IDE> (UITKX)`; body order =
  Title → Description → Features → Requirements → Changelog. Execution details:
  `Plans~/archive/EXTENSION_LISTING_PLAN.md`.
- A LISTING-ONLY change still bumps + gets a changelog entry — shipped bytes change.
- Where a body file is generated (`README.md`, `overview.md`): edit the TEMPLATE
  (`readme-template.md` / `overview-template.md`), re-run `extract-overview`, commit
  template + output together — never hand-edit the generated file.
- Before publishing, run `node scripts/changelog.mjs verify` — it recomposes each generated
  page from its template + `changelog.json` and byte-compares against the committed file,
  failing with the exact regeneration command on drift. `publish-vsix.ps1`'s
  `-ChangelogEntry` currently hand-edits `overview.md` directly (pre-dates the generated-file
  convention) — run `verify` after any publish that used it, and regenerate if it flags drift.

---

## Typical Workflow

### 1. Develop Feature / Fix Bug

Work in the LSP server, language-lib, or extension-specific code.

### 2. Run Tests

```powershell
cd SourceGenerator~/Tests
dotnet test
```

### 3. Publish VS Code Extension

```powershell
.\scripts\publish-extension.ps1 -BumpVersion -ChangelogEntry "Add feature X"
```

### 4. Publish VS2022 Extension

```powershell
.\scripts\publish-vsix.ps1 -BumpVersion -ChangelogEntry "Add feature X"
```

### 5. Local-Only (Test Before Publishing)

```powershell
# VS Code
.\scripts\publish-extension.ps1 -LocalOnly -BumpVersion -ChangelogEntry "Test"

# VS2022
.\scripts\publish-vsix.ps1 -LocalOnly -BumpVersion -ChangelogEntry "Test"
```

---

## VS2022 Publish — Detailed Pipeline

The VS2022 publish is more involved than VS Code. Here's exactly what `publish-vsix.ps1` does:

### Step 1: Bump Version (optional)

Edits `source.extension.vsixmanifest`:
```xml
<Identity Id="UitkxVsix.ReactiveUITK" Version="1.0.53" ... />
```

### Step 2: Update Changelog

Appends entry to `overview.md` (the marketplace listing page).

### Step 3: Build LSP Server

```powershell
dotnet publish -c Release --self-contained false -o ../visual-studio/UitkxVsix/server
```

The server (~13 MB) is embedded inside the VSIX.

### Step 4: Clean obj/ Directory

**Critical**: The VSSDK build system caches `obj/Release/extension.vsixmanifest` (the "detokenized" manifest). If you don't delete `obj/`, MSBuild will reuse the old cached version number even though you updated the source manifest. This was the root cause of our 1.0.30-stuck-in-VSIX bug.

### Step 5: NuGet Restore

```powershell
msbuild UitkxVsix.csproj /t:Restore
```

This regenerates `obj/UitkxVsix.csproj.nuget.g.props` which defines `$(PkgMicrosoft_VSSDK_BuildTools)`. The VSSDK targets import depends on this property.

### Step 6: MSBuild Build

```powershell
msbuild UitkxVsix.csproj /p:Configuration=Release /p:DeployExtension=false
```

Compiles `UitkxVsix.dll` and copies content files. `DeployExtension=false` prevents auto-deployment to the VS experimental instance.

### Step 7: CreateVsixContainer (Separate Invocation!)

```powershell
msbuild UitkxVsix.csproj /p:Configuration=Release /p:DeployExtension=false /t:CreateVsixContainer
```

**This MUST be a separate MSBuild invocation**, not combined with Build via `/t:Build,CreateVsixContainer`. After a clean, combining them causes target resolution failure because the VSSDK targets haven't been evaluated yet in that MSBuild process.

Output: `UitkxVsix.vsix` in the project root (~13.7 MB).

### Step 8: Verify VSIX Version

The script extracts the VSIX (it's a ZIP), reads `extension.vsixmanifest` inside, and confirms the version matches expectations. This catches the stale-cache bug.

### Step 9: Local Install

```powershell
VSIXInstaller.exe /quiet UitkxVsix.vsix
```

### Step 10: Publish to Marketplace

```powershell
VsixPublisher.exe publish -payload UitkxVsix.vsix -publishManifest publishManifest.json -personalAccessToken $PAT
```

---

## Key Gotchas & Lessons Learned

### 1. MSBuild Version Matters

**Use VS2022 MSBuild (v17.x)**, not VS2026 Preview (v18.x). The v18 MSBuild successfully compiles the DLL but silently skips the `CreateVsixContainer` target — no error, no VSIX output.

```
✅ C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe
❌ C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe
```

### 2. Stale Intermediate Manifest

MSBuild caches `obj/Release/extension.vsixmanifest`. If you bump the version in `source.extension.vsixmanifest` but don't clean `obj/`, the VSIX will contain the old version. The script deletes `obj/` before every build to prevent this.

### 3. VsixPublisher Reports Success Even on Failure

`VsixPublisher.exe` can report "Uploaded 'UITKX'" with exit code 0 even when:
- The VSIX has a version that already exists (the real error is in stderr)
- The upload didn't actually go through

Always capture both stdout and stderr, and verify on the marketplace after publishing.

### 4. CreateVsixContainer Must Be Separate

After `Restore`, the VSSDK targets are available. But if you run `/t:Restore,Build,CreateVsixContainer` in one invocation, CreateVsixContainer fails with "target does not exist" because the targets file import was evaluated before Restore regenerated the props file.

Solution: Restore → Build → CreateVsixContainer as 2-3 separate MSBuild invocations.

### 5. PAT Token

The same PAT (`publisher-secrets.json → vscePatToken`) works for both VS Code (`vsce`) and VS2022 (`VsixPublisher`).

Required scopes: **Marketplace → Manage** (all accessible organizations).

### 6. Marketplace Validation Delay

After upload, the marketplace may take a few minutes to validate and show the new version. The public gallery API won't return the new version until validation completes.

---

## File Reference

| File | Purpose |
|------|---------|
| `source.extension.vsixmanifest` | VSIX identity, version, install targets |
| `publishManifest.json` | Marketplace publisher ID + internal name |
| `overview.md` | Marketplace listing page (features + changelog) |
| `UitkxVsix.csproj` | MSBuild project (net472 + VSSDK) |
| `publisher-secrets.json` | PAT token (gitignored) |
| `LICENSE.txt` | License embedded in VSIX |
| `server/` | LSP server binaries (built by dotnet publish) |

---

## Marketplace Links

- **VS Code**: https://marketplace.visualstudio.com/items?itemName=ReactiveUITK.uitkx
- **VS2022**: https://marketplace.visualstudio.com/items?itemName=ReactiveUITK.uitkx-visualstudio
- **Publisher Portal**: https://marketplace.visualstudio.com/manage/publishers/ReactiveUITK

---

## Verifying a Publish

```powershell
# Query marketplace API for current version
$headers = @{ "Accept" = "application/json; api-version=6.1-preview.1" }
$body = '{"filters":[{"criteria":[{"filterType":7,"value":"ReactiveUITK.uitkx-visualstudio"}]}],"flags":55}'
$r = Invoke-RestMethod -Uri "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery" -Method Post -ContentType "application/json" -Headers $headers -Body $body
$r.results[0].extensions[0].versions | ForEach-Object { "v$($_.version) $($_.flags)" }
```
