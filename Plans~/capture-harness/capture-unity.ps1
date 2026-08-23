# Drives RuitkParityCaptureHarness inside a RUNNING Unity editor and captures a
# cropped screenshot of each staged builder state.
#
#   powershell -File capture-unity.ps1 [-Round N] [-OutDir <dir>] [-ArchiveRoot <dir>]
#
# Images land in -OutDir (always the LATEST set, what reviewers read) and, when
# -Round is given, are also archived to <ArchiveRoot>\round-NN so successive
# rounds can be diffed for regressions and for "did anything actually change".
#
# Requires Unity open on the UnityComponents project with the harness installed.
# Missing images mean "no fresh visual evidence" — never treat them as parity.

param(
    [int]$Round = 0,
    [string]$OutDir = "$env:TEMP\ruitk-parity\unity-shots",
    [string]$ArchiveRoot = "$env:TEMP\ruitk-parity\archive",
    [int]$TimeoutSec = 700,
    [int]$CompileWaitSec = 25
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# Unity must be FOREGROUND: it only imports/compiles changed assets on focus,
# and a desktop grab of a background editor photographs whatever is in front.
# SetForegroundWindow is refused unless the calling thread shares input state
# with the current foreground thread, hence AttachThreadInput.
# EnumWindows is used to locate the floating "RUITK Builder" window so captures
# can be cropped to the app instead of shipping the whole desktop.
if (-not ('Win32Fg' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Fg {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static RECT FindWindowRectByTitle(string needle) {
        RECT found = new RECT();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            if (!IsWindowVisible(h)) return true;
            StringBuilder sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) {
                RECT r; GetWindowRect(h, out r);
                if (r.Right - r.Left > 400 && r.Bottom - r.Top > 300) { found = r; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
}

function Focus-Unity {
    # Match by WINDOW TITLE, not "first Unity process": a second editor on
    # another project would otherwise be focused and screenshotted, and every
    # critic downstream would grade the wrong app.
    $candidates = Get-Process Unity -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }
    $unity = $candidates | Where-Object { $_.MainWindowTitle -like '*UnityComponents*' } |
        Select-Object -First 1
    if (-not $unity) {
        if ($candidates) {
            Write-Host "REFUSING: Unity running but no window titled '*UnityComponents*'."
            Write-Host "          Open editors: $(($candidates | ForEach-Object { $_.MainWindowTitle }) -join ' | ')"
        }
        return $false
    }
    $h = $unity.MainWindowHandle
    [void][Win32Fg]::ShowWindow($h, 9)
    $fg = [Win32Fg]::GetForegroundWindow()
    $tmpPid = 0
    $fgThread = [Win32Fg]::GetWindowThreadProcessId($fg, [ref]$tmpPid)
    $mine = [Win32Fg]::GetCurrentThreadId()
    [void][Win32Fg]::AttachThreadInput($mine, $fgThread, $true)
    [void][Win32Fg]::SetForegroundWindow($h)
    [void][Win32Fg]::AttachThreadInput($mine, $fgThread, $false)
    Start-Sleep -Milliseconds 600
    return $true
}

function Save-Screen($path) {
    # Crop to the floating "RUITK Builder" window when it can be located, so the
    # reviewer compares app-to-app instead of app-to-desktop. Falls back to the
    # full virtual screen (all monitors) if the window cannot be found.
    $r = [Win32Fg]::FindWindowRectByTitle("RUITK Builder")
    if (($r.Right - $r.Left) -gt 400) {
        $x = $r.Left; $y = $r.Top
        $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    }
    else {
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $x = $b.X; $y = $b.Y; $w = $b.Width; $h = $b.Height
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $g.Dispose()

    # A locked session or sleeping display makes CopyFromScreen return black
    # while still "succeeding" — the worst failure mode, because the loop would
    # review blank rectangles. Sample the frame and refuse it.
    $lit = 0; $samples = 0
    for ($px = 0; $px -lt $bmp.Width; $px += 40) {
        for ($py = 0; $py -lt $bmp.Height; $py += 40) {
            $c = $bmp.GetPixel($px, $py); $samples++
            if (($c.R + $c.G + $c.B) -gt 60) { $lit++ }
        }
    }
    $ratio = if ($samples -gt 0) { $lit / $samples } else { 0 }
    if ($ratio -lt 0.02) {
        $bmp.Dispose()
        throw "capture is blank ($([int]($ratio*100))% lit) - screen locked or display asleep"
    }

    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$root = "$env:TEMP\ruitk-parity\unity"
New-Item -ItemType Directory -Force $root, $OutDir | Out-Null
Get-ChildItem $root -File -ErrorAction SilentlyContinue | Remove-Item -Force

$pkg = 'C:\Yanivs\GameDev\UnityComponents\Packages\com.reactiveuitoolkit'
$showcase = "$pkg\Samples\Components\ShowcaseDemoPage"
$galaga = "$pkg\Samples\Components\GalagaGame"

# Mirrors the POC reference states. Zooms are the POC's own L0/L1/L2 presets,
# and the builder derives detail level from zoom exactly as the POC does, so
# these cover every LOD branch. Opening any member of a tree opens the whole
# tree, so the Galaga targets bring up a component + hook module + style module
# together — the shape of the POC's ShopScreen tree.
$jobs = @(
    @{ name = 'showcase-l0'; file = "$showcase\ShowcaseDemoPage.uitkx"; zoom = 0.30 },
    @{ name = 'showcase-l1'; file = "$showcase\ShowcaseDemoPage.uitkx"; zoom = 0.75 },
    @{ name = 'showcase-l2'; file = "$showcase\ShowcaseDemoPage.uitkx"; zoom = 1.25 },
    @{ name = 'tree-l0';     file = "$galaga\components\GameScreen\GameScreen.hooks.uitkx"; zoom = 0.30 },
    @{ name = 'tree-l1';     file = "$galaga\components\GameScreen\GameScreen.hooks.uitkx"; zoom = 0.75 },
    @{ name = 'tree-l2';     file = "$galaga\components\GameScreen\GameScreen.hooks.uitkx"; zoom = 1.25 },
    @{ name = 'style-card';  file = "$galaga\GalagaGameStyles.style.uitkx"; zoom = 1.25 },
    @{ name = 'panel-l2';    file = "$showcase\components\ShowcaseFieldsPanel\ShowcaseFieldsPanel.uitkx"; zoom = 1.25 }
)

if (-not (Focus-Unity)) { Write-Host "Unity process not found - is the editor running?"; exit 1 }
Write-Host "Unity foregrounded; waiting for import/compile…"
Start-Sleep -Seconds $CompileWaitSec

$payload = @{ jobs = $jobs } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText("$root\jobs.json", $payload, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "queued $($jobs.Count) capture job(s); waiting on Unity…"

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$taken = @()
$i = 0
while ((Get-Date) -lt $deadline) {
    if ((Test-Path "$root\done.txt") -and $i -ge $jobs.Count) { break }
    $ready = "$root\ready-$i.txt"
    if (Test-Path $ready) {
        $name = (Get-Content $ready -Raw).Trim()
        $png = Join-Path $OutDir "unity-$name.png"
        [void](Focus-Unity)
        Start-Sleep -Milliseconds 1200
        Save-Screen $png
        $taken += $png
        Write-Host ("captured {0,-16} -> {1}" -f $name, (Split-Path $png -Leaf))
        [IO.File]::WriteAllText("$root\captured-$i.txt", 'ok')
        $i++
    }
    Start-Sleep -Milliseconds 250
}

if (Test-Path "$root\error.txt") {
    Write-Host "`nUNITY ERROR:`n$(Get-Content "$root\error.txt" -Raw)"
}
if ($taken.Count -eq 0) {
    Write-Host "`nno captures - Unity open with the harness installed?"
    exit 1
}

if ($Round -gt 0) {
    $dest = Join-Path $ArchiveRoot ("round-{0:D2}" -f $Round)
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$OutDir\*.png" $dest -Force
    Write-Host "archived round $Round -> $dest"
}

Write-Host "`n$($taken.Count) Unity state(s) captured into $OutDir"
