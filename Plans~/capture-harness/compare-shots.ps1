# Compares two capture sets image-by-image and reports how much changed.
#
#   powershell -File compare-shots.ps1 -A <dirA> -B <dirB>
#
# Two uses in the parity loop:
#   * STALL DETECTION - if round N looks identical to round N-1, the builder
#     changed nothing visible no matter what it claimed.
#   * SMOOTHER VERIFICATION - a refactor is behaviour-preserving by definition,
#     so before/after a smoothing pass must be pixel-identical. Any drift means
#     the "refactor" was actually a regression.
#
# Prints a per-image percentage and an overall verdict line beginning with
# IDENTICAL / CHANGED so a caller can branch on it.

param(
    [Parameter(Mandatory = $true)][string]$A,
    [Parameter(Mandatory = $true)][string]$B,
    [double]$Tolerance = 0.001   # fraction of sampled pixels allowed to differ
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $A)) { Write-Host "MISSING: $A"; exit 2 }
if (-not (Test-Path $B)) { Write-Host "MISSING: $B"; exit 2 }

$worst = 0.0
$rows = @()

foreach ($fileA in Get-ChildItem $A -Filter *.png | Sort-Object Name) {
    $fileB = Join-Path $B $fileA.Name
    if (-not (Test-Path $fileB)) {
        $rows += [pscustomobject]@{ image = $fileA.Name; diff = 'ABSENT' }
        $worst = 1.0
        continue
    }

    $ia = [System.Drawing.Bitmap]::FromFile($fileA.FullName)
    $ib = [System.Drawing.Bitmap]::FromFile($fileB)
    try {
        if ($ia.Width -ne $ib.Width -or $ia.Height -ne $ib.Height) {
            $rows += [pscustomobject]@{ image = $fileA.Name; diff = 'SIZE' }
            $worst = 1.0
            continue
        }
        # Sample on a grid rather than every pixel: 1920x1080 per-pixel in
        # PowerShell takes minutes, and a 6px grid still catches any change a
        # human would notice.
        $differing = 0; $total = 0
        for ($x = 0; $x -lt $ia.Width; $x += 6) {
            for ($y = 0; $y -lt $ia.Height; $y += 6) {
                $ca = $ia.GetPixel($x, $y); $cb = $ib.GetPixel($x, $y)
                $total++
                if ([Math]::Abs($ca.R - $cb.R) + [Math]::Abs($ca.G - $cb.G) +
                    [Math]::Abs($ca.B - $cb.B) -gt 24) { $differing++ }
            }
        }
        $ratio = if ($total -gt 0) { $differing / $total } else { 0 }
        if ($ratio -gt $worst) { $worst = $ratio }
        $rows += [pscustomobject]@{ image = $fileA.Name; diff = ('{0:P2}' -f $ratio) }
    }
    finally {
        $ia.Dispose(); $ib.Dispose()
    }
}

$rows | Format-Table -AutoSize | Out-String | Write-Host

if ($worst -le $Tolerance) {
    Write-Host ("IDENTICAL - worst image differs by {0:P3} (tolerance {1:P3})" -f $worst, $Tolerance)
    exit 0
}
Write-Host ("CHANGED - worst image differs by {0:P2}" -f $worst)
exit 1
