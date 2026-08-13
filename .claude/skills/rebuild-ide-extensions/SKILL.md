---
name: rebuild-ide-extensions
description: Rebuild the VS Code and/or Visual Studio 2022 extensions locally for F5 / Extension-Development-Host testing. Use when the user says "rebuild for F5", "rebuild the extension", "build the LSP server locally", "test the extension change" or after editing files under `ide-extensions~/language-lib/`, `ide-extensions~/lsp-server/`, `ide-extensions~/vscode/`, `ide-extensions~/visual-studio/`, or `Editor/HMR/`. Covers the full local pipeline — language-lib build, LSP server emit, TS client bundle, VSIX server-binary copy — and the artifact-size verification step. Does NOT cover Marketplace releases — those are handled by `.github/workflows/publish.yml`.
---

# Rebuild IDE extensions for F5

Every path below is relative to the **repo root — the checkout you are already in**
(the folder holding `config.json`, `Analyzers/` and `ide-extensions~/`). Commands are
PowerShell-flavoured; run them from that root unless a step says otherwise. The root is
never written down as an absolute path: it differs per clone, and a stale literal is
exactly how the rebrand wave broke a sibling repo's F5 (see CLAUDE.md
"Machine-local paths").

Use this skill when the user wants to test changes to the VS Code or
VS 2022 extension by launching an Extension Development Host (F5) or the
VS 2022 experimental hive.

**The loop:** the assistant researches/fixes/rebuilds per this skill and says
"F5-ready"; the OWNER presses F5 and tests. Never publish to a marketplace to
test anything.

## Out of scope — Marketplace / OpenVSX releases

Releasing to the VS Code Marketplace, OpenVSX, or the VS 2022 Marketplace
is handled by the CI pipeline at
[.github/workflows/publish.yml](../../../.github/workflows/publish.yml). Do **not**
run `vsce publish`, `ovsx publish`, or any equivalent command from a
developer machine. If the user asks to "ship a release" or "push to
marketplace", point them at the CI workflow instead — this skill stops
at the F5-ready local artifacts.

## Decide which pipelines to rebuild

Pick the smallest set that covers the changed files:

- Edited `ide-extensions~/language-lib/**/*.cs` or
  `ide-extensions~/lsp-server/**/*.cs` → **LSP server** must be rebuilt
  (and copied into VS 2022's `UitkxVsix/server/` if VS 2022 is in scope).
- Edited `ide-extensions~/vscode/src/**/*.ts` or
  `ide-extensions~/vscode/package.json` → **VS Code TS client** must be
  rebuilt.
- Edited `ide-extensions~/grammar/**` → both extensions repackage
  (grammar is bundled by each VSIX).
- Edited `Editor/HMR/**` only → **no IDE rebuild needed** (HMR runs
  inside Unity Editor at play time).

The two extensions share the LSP server but have separate client wrappers.

## VS Code rebuild (full F5-ready)

Run from repo root unless noted. PowerShell-friendly (use `cmd /c` for
the npm step to dodge execution-policy blocks on `npm.ps1`).

```powershell
# 1. Build + emit the LSP server into the VS Code extension's server/ dir.
#    `dotnet publish` is the .NET CLI command for emit-to-folder — it is
#    NOT marketplace publishing.
dotnet publish ide-extensions~/lsp-server/UitkxLanguageServer.csproj `
  -c Debug --self-contained false `
  -o ide-extensions~/vscode/server

# 2. Build the TS client bundle (esbuild → extension.js)
cmd /c "cd /d ide-extensions~\vscode && npm run build"
```

**Verify the artifacts** before launching F5:

```powershell
Get-ChildItem ide-extensions~/vscode/out/extension.js,
              ide-extensions~/vscode/server/UitkxLanguageServer.dll,
              ide-extensions~/vscode/server/Ruitk.Language.dll |
  Select-Object Name, @{n='KB';e={[int]($_.Length/1KB)}}, LastWriteTime
```

Expected sizes (rough sanity check, drift-tolerant):
- `extension.js` ~ 700-900 KB
- `UitkxLanguageServer.dll` ~ 250-320 KB
- `Ruitk.Language.dll` ~ 200-320 KB

If `out/extension.js` is < 50 KB the bundle is broken (esbuild silently
emitted a stub) — check `npm run build` output. (The bundle path is
`out/extension.js` — `package.json` `"main"` — not `dist/`.)

Then in VS Code: open the `ide-extensions~/vscode` folder, **F5**.
Close any prior Extension Development Host first; the LSP DLL is
file-locked while attached.

## VS 2022 rebuild (full F5-ready / experimental hive)

VS 2022's `UitkxVsix` bundles a **static copy** of the LSP server in
`UitkxVsix/server/` and `UitkxVsix/server/win-x64/`. These are **not**
auto-synced from `lsp-server/bin/`; the VSIX project copies them at
its own build time only when its `BeforeBuild` target runs cleanly.
The reliable sequence is:

```powershell
# 1. Build the LSP server (Debug, framework-dependent)
dotnet build ide-extensions~/lsp-server/UitkxLanguageServer.csproj -c Debug

# 2. Mirror server binaries into the VSIX
$src = "ide-extensions~/lsp-server/bin/Debug/net8.0"
$dst = "ide-extensions~/visual-studio/UitkxVsix/server"
foreach ($d in $dst, "$dst/win-x64") {
  Copy-Item "$src/UitkxLanguageServer.dll"  "$d/" -Force
  Copy-Item "$src/UitkxLanguageServer.pdb"  "$d/" -Force
  Copy-Item "$src/Ruitk.Language.dll" "$d/" -Force
  Copy-Item "$src/Ruitk.Language.pdb" "$d/" -Force
}

# 3. Build the VSIX (uses the wrapper script)
Push-Location ide-extensions~/visual-studio
.\build-local.ps1
Pop-Location
```

The wrapper produces `UitkxVsix/bin/Debug/UitkxVsix.vsix`. Open the
`.sln` in VS 2022 and press **F5** to launch the experimental instance,
or double-click the `.vsix` to install into the main hive.

## Source generator rebuild (Unity-side)

Independent of either IDE. Outputs to `Analyzers/` so Unity picks it up
on the next domain reload:

```powershell
dotnet build SourceGenerator~/Ruitk.SourceGenerator.csproj -c Release
```

If the user is testing both an SG change *and* an IDE change in the
same session, rebuild the SG **first** so the next IDE rebuild's
parity contract tests run against the new SG.

## Common pitfalls

- **PowerShell execution policy** blocks `npm.ps1`. Always wrap npm in
  `cmd /c "..."`.
- **DLL file lock.** If the emit or copy step fails with "file in
  use", close the running Extension Development Host (or the VS 2022
  experimental hive) first.
- **Stale TS bundle.** `npm run build` is incremental; if the output
  looks wrong, `Remove-Item ide-extensions~/vscode/out -Recurse -Force`
  and re-run.
- **VS 2022 sees an old server.** Almost always the binary copy step
  was skipped — re-run step 2 of the VS 2022 sequence.
- **VS Code `uitkx.server.path` setting** can override the bundled
  `server/` directory for a developer-local LSP build. Check user
  settings if behaviour differs from a fresh F5.
- **F5 opens an empty Extension Development Host every time ("it keeps
  forgetting my project").** The owner presses F5 from the REPO ROOT
  workspace, so the config that runs is the **machine-local, untracked
  `.vscode/launch.json` at the repo root** — not the tracked
  `ide-extensions~/vscode/.vscode/launch.json` (that one only applies when
  the extension folder itself is opened as the workspace). The root config
  pins the dev-host workspace with a `--file-uri=…ReactiveUIToolkit.code-workspace`
  argument pointing into the consumer Unity project's embedded package
  clone, and runs the npm build as a preLaunchTask. Two standing facts:
  (1) VS Code does NOT error when that pinned path no longer exists — it
  silently opens an empty window on every F5, which presents as "the dev
  host stopped remembering my folder" (bit us after the 0.12 rebrand +
  `Assets/` → `Packages/` package move). Fix: search the consumer project
  for `*.code-workspace` and repoint the `--file-uri`. (2) The pin is a
  machine-local path, which is exactly WHY that file is untracked — never
  move it into a tracked file; the machine-paths gate forbids it. The same
  untracked `.vscode/settings.json` chain has previously overridden
  extension behaviour (`formatOnSave` off for `[uitkx]`) — when F5
  behaviour diverges from a clean install, read the repo-root `.vscode/`
  files FIRST, before theorizing about the extension.

## After rebuild

- Reload window in the Extension Development Host (or restart it) to
  pick up the new LSP DLL — VS Code does not hot-swap server processes.
- Open a `.uitkx` file from **any real Unity project of yours that consumes the
  package** — not a file inside this repo. The point is to exercise the extension
  against a consumer project's own folder layout (its `Assets/` root, its asmdefs),
  which is where namespace derivation and import resolution actually get tested.
  Verify diagnostics, hover, completion, and formatting work end-to-end.
- **Opening this repo's `Samples/` in the dev host shows a storm of
  unresolved-type errors — that is expected, not a regression.** The Roslyn
  layer resolves `Ruitk.*` and Unity engine types by walking up from the opened
  folder to a Unity project root and loading
  `Library/ScriptAssemblies/*.dll` (`ReferenceAssemblyLocator`). This package
  repo has no `Library/`, so every C# splice loses its references. The same
  files analyze clean when opened from inside a consumer project (the embedded
  package under `UnityComponents/Packages/` includes these very samples).
- The VS Code extension's "Output → UITKX Language Server" pane shows
  the server's stdout/stderr; any unhandled exception there means the
  rebuild produced a binary with a broken dependency graph (most often
  `Microsoft.CodeAnalysis.*` mismatch).
