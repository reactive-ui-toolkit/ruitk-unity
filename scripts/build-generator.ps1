<#
Build script for Ruitk.SourceGenerator + the Editor-referenced language variant

Usage:
  .\scripts\build-generator.ps1           # Release build (default)
  .\scripts\build-generator.ps1 -Debug    # Debug build

What this does:
  1) Builds SourceGenerator~/Ruitk.SourceGenerator.csproj.
  2) The csproj builds to bin/; its PublishGeneratorToAnalyzers post-build target copies the DLLs into Analyzers/.
  3) Builds language-lib a SECOND time as Ruitk.Language.Editor.dll (distinct
     assembly identity - the analyzer copy is Assembly.LoadFrom'd by HMR and
     must never collide with a normally-referenced copy) and copies it into
     Editor/Plugins/ for the RUITK Builder assembly. Separate invocation on
     UitkxLanguage.csproj with its own obj dir: passing -p:AssemblyName to the
     SG csproj would rename the SG via ProjectReference property flow.
  4) Unity picks up the updated DLLs on refresh/recompile.
#>
param(
    [switch] $Debug
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot     = Split-Path -Parent $scriptDir
$generatorDir = Join-Path $repoRoot "SourceGenerator~"
$analyzersDir = Join-Path $repoRoot "Analyzers"
$csproj       = Join-Path $generatorDir "Ruitk.SourceGenerator.csproj"

if (-not (Test-Path $csproj)) {
    throw "Cannot find $csproj"
}

$configuration = if ($Debug) { "Debug" } else { "Release" }

Write-Host ""
Write-Host "===============================================================" -ForegroundColor Cyan
Write-Host "  Ruitk.SourceGenerator - building ($configuration)" -ForegroundColor Cyan
Write-Host "===============================================================" -ForegroundColor Cyan
Write-Host "  Project  : $csproj"
Write-Host "  Output   : $analyzersDir"
Write-Host ""

Push-Location $generatorDir
try {
    Write-Host "-- dotnet restore ------------------------------------------------" -ForegroundColor DarkCyan
    dotnet restore $csproj --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)" }

    Write-Host "-- dotnet build --------------------------------------------------" -ForegroundColor DarkCyan
    dotnet build $csproj --configuration $configuration --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$languageCsproj  = Join-Path $repoRoot "ide-extensions~/language-lib/UitkxLanguage.csproj"
$editorPluginsDir = Join-Path $repoRoot "Editor/Plugins"
$variantRoot      = Join-Path $repoRoot "ide-extensions~/.language-editor-variant"
$editorVariantOut = Join-Path $variantRoot "bin"

Write-Host "-- dotnet build (Ruitk.Language.Editor variant) ------------------" -ForegroundColor DarkCyan
# Intermediates/outputs live OUTSIDE the project folder: a redirected obj/bin
# INSIDE it would be globbed as source by the other build flavor (CS0579
# duplicate-attribute storms in whichever build runs second).
# -t:Rebuild: the variant's isolated obj/ has proven unreliable at detecting
# source changes incrementally (a stale DLL shipped once, caught by the CI
# pairing gate design); the project builds in ~1 s, forced rebuild is cheap.
dotnet build $languageCsproj --configuration $configuration --verbosity minimal -t:Rebuild `
    -p:RuitkEditorVariant=true `
    -p:BaseIntermediateOutputPath="$variantRoot/obj/" `
    -p:BaseOutputPath="$variantRoot/bin/"
if ($LASTEXITCODE -ne 0) { throw "language editor-variant build failed (exit $LASTEXITCODE)" }

if (-not (Test-Path $editorPluginsDir)) {
    New-Item -ItemType Directory -Path $editorPluginsDir | Out-Null
}
$variantDll = Get-ChildItem -Path $editorVariantOut -Recurse -Filter "Ruitk.Language.Editor.dll" | Select-Object -First 1
if ($null -eq $variantDll) {
    throw "editor-variant build succeeded but Ruitk.Language.Editor.dll not found under $editorVariantOut"
}
Copy-Item $variantDll.FullName (Join-Path $editorPluginsDir "Ruitk.Language.Editor.dll") -Force
$variantPdb = [System.IO.Path]::ChangeExtension($variantDll.FullName, ".pdb")
if (Test-Path $variantPdb) {
    Copy-Item $variantPdb (Join-Path $editorPluginsDir "Ruitk.Language.Editor.pdb") -Force
}
Write-Host "  Variant : $(Join-Path $editorPluginsDir "Ruitk.Language.Editor.dll")"

$dll = Join-Path $analyzersDir "Ruitk.SourceGenerator.dll"
if (Test-Path $dll) {
    $size = (Get-Item $dll).Length
    Write-Host ""
    Write-Host "Build succeeded" -ForegroundColor Green
    Write-Host "  DLL     : $dll"
    Write-Host "  Size    : $("{0:N0}" -f $size) bytes"
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Yellow
    Write-Host "  1. In Unity, run Assets -> Refresh or refocus the editor window."
    Write-Host "  2. Reproduce the UITKX compile error and click the red console line."
} else {
    throw "Build appeared to succeed but DLL not found at $dll"
}
