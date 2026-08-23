# Captures the POC (ruitkUiBuiler/index.html) in a set of scripted UI states via
# headless Edge/Chrome. Each state is produced by copying the POC to a temp file
# with a boot script appended, then screenshotting that copy — the POC exposes
# its driver functions globally, so any state it can reach interactively is
# reachable here.
#
#   powershell -File capture-poc.ps1 [-OutDir <dir>] [-Only <stateName>]
#
# Output: <OutDir>\poc-<state>.png  (+ poc-states.json listing what was taken)

param(
    [string]$OutDir = "$env:TEMP\ruitk-parity\poc",
    [string]$Only = ""
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$poc = Join-Path $repoRoot 'ruitkUiBuiler\index.html'
if (-not (Test-Path $poc)) { throw "POC not found at $poc" }

$browser = @(
    "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    "C:\Program Files\Google\Chrome\Application\chrome.exe",
    "C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw "no Edge/Chrome found for headless capture" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$work = Join-Path $env:TEMP 'ruitk-parity\work'
New-Item -ItemType Directory -Force $work | Out-Null

# state name -> JS run after the POC boots. Keep each one to the smallest set of
# driver calls that reaches the state a reviewer needs to see.
$states = [ordered]@{
    'boot'            = ''
    'l0-architecture' = 'setZoom(0.30);'
    'l1-cards'        = 'setZoom(0.75);'
    'l2-edit'         = 'setZoom(1.25);'
    'l2-shopscreen'   = 'selectNode("ShopScreen"); setZoom(1.25);'
    'style-card'      = 'selectNode("shopStyles"); setZoom(1.25);'
    'hook-card'       = 'selectNode("useCart"); setZoom(1.25);'
    'util-card'       = 'selectNode("shopData"); setZoom(1.25);'
    'itemcard'        = 'selectNode("ItemCard"); setZoom(1.25);'
    'help-open'       = 'document.getElementById("help").classList.add("open");'
    'library-search'  = 'var s=document.getElementById("lib-search"); s.value="Lab"; s.dispatchEvent(new Event("input"));'
    'row-context'     = @'
setZoom(1.25);
selectNode("ShopScreen");
setTimeout(function(){
  var row = document.querySelector("#card-ShopScreen .jsx-row");
  if (row) {
    var r = row.getBoundingClientRect();
    row.dispatchEvent(new MouseEvent("contextmenu", {bubbles:true, clientX:r.left+40, clientY:r.top+8}));
  }
}, 120);
'@
    'canvas-context'  = @'
setTimeout(function(){
  var w = document.getElementById("canvasWrap");
  w.dispatchEvent(new MouseEvent("contextmenu", {bubbles:true, clientX:700, clientY:500}));
}, 120);
'@
}

$html = [IO.File]::ReadAllText($poc)
$taken = @()

foreach ($name in $states.Keys) {
    if ($Only -and $Only -ne $name) { continue }
    $js = $states[$name]
    $boot = if ([string]::IsNullOrWhiteSpace($js)) { '' } else {
        "<script>window.addEventListener('load',function(){setTimeout(function(){try{$js}catch(e){console.error(e);}},250);});</script>"
    }
    $page = if ($boot) { $html -replace '</body>', "$boot</body>" } else { $html }

    $tmp = Join-Path $work "poc-$name.html"
    [IO.File]::WriteAllText($tmp, $page, (New-Object System.Text.UTF8Encoding($false)))

    $png = Join-Path $OutDir "poc-$name.png"
    if (Test-Path $png) { Remove-Item $png -Force }
    $uri = 'file:///' + ($tmp -replace '\\', '/')

    # Edge reports "N bytes written" on stderr; under ErrorActionPreference=Stop
    # PowerShell 5.1 turns that into a terminating NativeCommandError.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $browser --headless=new --disable-gpu --hide-scrollbars `
        --virtual-time-budget=3000 `
        --screenshot="$png" --window-size=1920,1080 $uri 2>$null | Out-Null
    $ErrorActionPreference = $prev

    if (Test-Path $png) {
        $taken += [pscustomobject]@{ state = $name; file = $png; bytes = (Get-Item $png).Length }
        Write-Host ("captured {0,-16} -> {1}" -f $name, $png)
    }
    else {
        Write-Host ("FAILED   {0}" -f $name)
    }
}

$taken | ConvertTo-Json -Depth 3 |
    Set-Content (Join-Path $OutDir 'poc-states.json') -Encoding utf8
Write-Host ("`n{0} state(s) captured into {1}" -f $taken.Count, $OutDir)
