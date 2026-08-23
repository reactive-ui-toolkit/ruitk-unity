$ErrorActionPreference = 'Stop'
# Derived, never written down: this harness lives at <repo>/Plans~/capture-harness.
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'Analyzers\Ruitk.Language.dll'))
$immAsm = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'Editor\Plugins\System.Collections.Immutable.dll'))

$dirType = $asm.GetType('Ruitk.Language.Parser.DirectiveParser')
$parserType = $asm.GetType('Ruitk.Language.Parser.UitkxParser')
$diagType = $asm.GetType('Ruitk.Language.ParseDiagnostic')
$resultType = $asm.GetTypes() | Where-Object { $_.Name -eq 'ParseResult' } | Select-Object -First 1
$analyzerType = $asm.GetTypes() | Where-Object { $_.Name -eq 'DiagnosticsAnalyzer' } | Select-Object -First 1
$diagListType = [System.Collections.Generic.List`1].MakeGenericType($diagType)

$dirParse = $dirType.GetMethods() | Where-Object { $_.Name -eq 'Parse' } | Select-Object -First 1
$uitkxParse = $parserType.GetMethods() | Where-Object { $_.Name -eq 'Parse' } | Select-Object -First 1
$analyze = $analyzerType.GetMethods() | Where-Object { $_.Name -eq 'Analyze' } | Select-Object -First 1

$immArrayStatic = $immAsm.GetType('System.Collections.Immutable.ImmutableArray')
$immCreate = ($immArrayStatic.GetMethods() | Where-Object { $_.Name -eq 'CreateRange' -and $_.GetParameters().Count -eq 1 } | Select-Object -First 1).MakeGenericMethod($diagType)

$files = @(
  'Builder\Editor\Canvas\canvasStyles.style.uitkx',
  'Builder\Editor\Canvas\CanvasView.uitkx'
)

$totalErrors = 0
foreach ($rel in $files) {
  $path = Join-Path $repo $rel
  $text = [IO.File]::ReadAllText($path) -replace "`r`n", "`n"
  $diags = [Activator]::CreateInstance($diagListType)

  $dirArgs = New-Object object[] 4
  $dirArgs[0] = [string]$text; $dirArgs[1] = [string]$path
  $dirArgs[2] = $diags.psobject.BaseObject; $dirArgs[3] = [bool]$true
  $directives = $dirParse.Invoke($null, $dirArgs)

  $parseArgs = New-Object object[] 6
  $parseArgs[0] = [string]$text; $parseArgs[1] = [string]$path
  $parseArgs[2] = $directives.psobject.BaseObject
  $parseArgs[3] = $diags.psobject.BaseObject; $parseArgs[4] = [bool]$false; $parseArgs[5] = [int]0
  $nodes = $uitkxParse.Invoke($null, $parseArgs)

  $immDiags = $immCreate.Invoke($null, @(, $diags.psobject.BaseObject))
  $ctorArgs = New-Object object[] 3
  $ctorArgs[0] = $directives.psobject.BaseObject
  $ctorArgs[1] = $nodes.psobject.BaseObject
  $ctorArgs[2] = $immDiags.psobject.BaseObject
  $parseResult = [Activator]::CreateInstance($resultType, $ctorArgs)

  $analyzer = [Activator]::CreateInstance($analyzerType)
  $anArgs = New-Object object[] 5
  $anArgs[0] = $parseResult.psobject.BaseObject; $anArgs[1] = [string]$path
  $anArgs[2] = $null; $anArgs[3] = $null; $anArgs[4] = [string]$text
  $t2 = $analyze.Invoke($analyzer.psobject.BaseObject, $anArgs)

  $all = @()
  foreach ($d in $diags) { $all += $d }
  foreach ($d in $t2) { $all += $d }

  $errCount = 0
  Write-Host ("== {0}: {1} diag(s)" -f $rel, $all.Count)
  foreach ($d in $all) {
    $sev = $d.GetType().GetProperty('Severity').GetValue($d).ToString()
    if ($sev -eq 'Error') { $errCount++ }
    $code = $d.GetType().GetProperty('Code').GetValue($d)
    $msg = $d.GetType().GetProperty('Message').GetValue($d)
    $line = $d.GetType().GetProperty('SourceLine').GetValue($d)
    Write-Host ('  [' + $sev + '] ' + $code + ' L' + $line + ': ' + $msg)
  }
  $totalErrors += $errCount
}
Write-Host ("TOTAL ERRORS: {0}" -f $totalErrors)
if ($totalErrors -gt 0) { exit 1 }
