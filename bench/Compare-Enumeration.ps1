<#
.SYNOPSIS
    Compare UI Automation hint-enumeration speed: uncached (pre-optimization)
    vs cached (current), against a target window.

.DESCRIPTION
    Uses the System.Windows.Automation managed wrapper (from the .NET Framework
    assemblies UIAutomationClient / UIAutomationTypes, present on all Windows)
    rather than the COM CUIAutomation coclass, whose ProgID is not registered
    on stock Windows.

    Mirrors the two strategies in hunt-and-peck's UiAutomationHintProviderService
    before and after the cache-request optimization (commit 449435c):

      * UNCACHED (old): FindAll + per-element Current.BoundingRectangle + up to
        six GetCurrentPattern calls.
      * CACHED   (new): activate a CacheRequest that pre-fetches the bounding
        rectangle, the six patterns and the two IsReadOnly values, then FindAll
        + Cached reads.

    The managed wrapper and the app's COM IUIAutomation share the same UI
    Automation core, so the cached-vs-uncached ratio is representative of the
    app's improvement.

.PARAMETER Iterations
    Timed passes per strategy (default 10). More = steadier averages.

.PARAMETER ProcessName
    Target a process's main window directly (e.g. chrome, msedge, devenv, code,
    explorer, WINWORD). Omit to use a 5-second countdown + the foreground window.

.EXAMPLE
    .\Compare-Enumeration.ps1 -ProcessName chrome
    .\Compare-Enumeration.ps1 -ProcessName msedge -Iterations 25
    .\Compare-Enumeration.ps1            # then Alt+Tab to the target within 5s
#>
[CmdletBinding()]
param(
    [int]$Iterations = 10,
    [string]$ProcessName
)

$ErrorActionPreference = 'Stop'

# Managed UI Automation wrapper (ships with .NET Framework).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Type shortcuts (functions see these via PowerShell dynamic scoping).
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

# --- Win32: foreground window handle ---------------------------------------
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HapBench {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@

if ($ProcessName) {
    $proc = Get-Process -Name $ProcessName -ErrorAction Stop |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } |
            Select-Object -First 1
    if (-not $proc) { throw "No process '$ProcessName' with a visible window found." }
    $hwnd = $proc.MainWindowHandle
} else {
    Write-Host "Switch to the window you want to measure now (Alt+Tab to it)."
    for ($s = 5; $s -ge 1; $s--) {
        Write-Host ("  grabbing foreground window in {0}..." -f $s)
        Start-Sleep -Milliseconds 1000
    }
    $hwnd = [HapBench]::GetForegroundWindow()
}
if ($hwnd -eq [IntPtr]::Zero) {
    throw "No target window (HWND is zero). Bring a window to the front first, or pass -ProcessName."
}

$root = $AE::FromHandle($hwnd)
if ($null -eq $root) { throw "FromHandle returned null for HWND $hwnd." }

# Patterns to probe (same set the app inspects per element).
$patterns = @(
    [System.Windows.Automation.InvokePattern]::Pattern,
    [System.Windows.Automation.TogglePattern]::Pattern,
    [System.Windows.Automation.SelectionItemPattern]::Pattern,
    [System.Windows.Automation.ExpandCollapsePattern]::Pattern,
    [System.Windows.Automation.ValuePattern]::Pattern,
    [System.Windows.Automation.RangeValuePattern]::Pattern
)

# Condition: control view AND enabled AND on-screen (same as the app).
# Use -ArgumentList with the fixed-arity ctors; New-Object Type($a,$b,$c) was
# collapsing the args into one array and mis-binding to AndCondition(params).
$cv      = $AE::ControlViewCondition
$enabled = New-Object System.Windows.Automation.PropertyCondition -ArgumentList $AE::IsEnabledProperty,  $true
$onscr   = New-Object System.Windows.Automation.PropertyCondition -ArgumentList $AE::IsOffscreenProperty, $false
$cond1   = New-Object System.Windows.Automation.AndCondition -ArgumentList $cv, $enabled
$cond    = New-Object System.Windows.Automation.AndCondition -ArgumentList $cond1, $onscr

function New-HintCacheRequest {
    # Matches the app's CreateHintCacheRequest.
    $cr = New-Object System.Windows.Automation.CacheRequest
    $cr.TreeScope = $TS::Element
    [void]$cr.Add($AE::BoundingRectangleProperty)
    [void]$cr.Add([System.Windows.Automation.ValuePattern]::IsReadOnlyProperty)
    [void]$cr.Add([System.Windows.Automation.RangeValuePattern]::IsReadOnlyProperty)
    foreach ($p in $patterns) { [void]$cr.Add($p) }
    return $cr
}

function Invoke-Uncached {
    param($root, $cond, $patterns)
    $coll = $root.FindAll($TS::Descendants, $cond)
    $n = if ($coll) { $coll.Count } else { 0 }
    for ($i = 0; $i -lt $n; $i++) {
        $el = $coll[$i]
        [void]$el.Current.BoundingRectangle
        foreach ($p in $patterns) { try { [void]$el.GetCurrentPattern($p) } catch { } }
    }
    return $n
}

function Invoke-Cached {
    param($root, $cond, $patterns, $cr)
    $cr.Activate()
    try {
        $coll = $root.FindAll($TS::Descendants, $cond)
        $n = if ($coll) { $coll.Count } else { 0 }
        for ($i = 0; $i -lt $n; $i++) {
            $el = $coll[$i]
            [void]$el.Cached.BoundingRectangle
            foreach ($p in $patterns) { try { [void]$el.GetCachedPattern($p) } catch { } }
        }
    } finally {
        $cr.Pop()
    }
    return $n
}

# Warm-up + element count (and surface which window we targeted).
$warmUncached = Invoke-Uncached $root $cond $patterns
$cacheRequest = New-HintCacheRequest
$warmCached   = Invoke-Cached   $root $cond $patterns $cacheRequest

Write-Host ""
Write-Host ("Target window       : HWND {0}" -f $hwnd)
Write-Host ("  Name             : {0}" -f $root.Current.Name)
Write-Host ("  ClassName        : {0}" -f $root.Current.ClassName)
Write-Host ("Matching elements  : {0} (control view, enabled, on-screen)" -f $warmUncached)
Write-Host ("Iterations/strategy: {0}`n" -f $Iterations)

if ($warmUncached -eq 0) {
    Write-Warning "No matching elements in the target window. Open a denser window (browser/IDE/large list) and re-run."
    return
}

# Time UNCACHED
$uncachedMs = @()
for ($r = 0; $r -lt $Iterations; $r++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void](Invoke-Uncached $root $cond $patterns)
    $sw.Stop()
    $uncachedMs += $sw.Elapsed.TotalMilliseconds
}

# Time CACHED
$cachedMs = @()
for ($r = 0; $r -lt $Iterations; $r++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void](Invoke-Cached $root $cond $patterns $cacheRequest)
    $sw.Stop()
    $cachedMs += $sw.Elapsed.TotalMilliseconds
}

function Get-Stats($xs) {
    $sorted = $xs | Sort-Object
    $avg = ($xs | Measure-Object -Average).Average
    return [pscustomobject]@{ Min = $sorted[0]; Avg = $avg; Max = $sorted[-1] }
}

$u = Get-Stats $uncachedMs
$c = Get-Stats $cachedMs

Write-Host ("UNCACHED (old)  min/avg/max ms : {0,8:N1} / {1,8:N1} / {2,8:N1}" -f $u.Min, $u.Avg, $u.Max)
Write-Host ("CACHED   (new)  min/avg/max ms : {0,8:N1} / {1,8:N1} / {2,8:N1}" -f $c.Min, $c.Avg, $c.Max)
Write-Host ""
if ($c.Avg -gt 0) {
    Write-Host ("Speedup (avg): {0:N2}x   ({1:N0} ms -> {2:N0} ms,  saved {3:N0} ms per enumeration)") -f ($u.Avg / $c.Avg), $u.Avg, $c.Avg, ($u.Avg - $c.Avg)
}
Write-Host ""
Write-Host "Note: speedup scales with element count. A window with few controls shows"
Write-Host "little change; a dense window (browser/IDE/big list) shows a large one."
