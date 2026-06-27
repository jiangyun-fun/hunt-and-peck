<#
.SYNOPSIS
    Compare UI Automation hint-enumeration speed: uncached (pre-optimization)
    vs cached (current), against the foreground window.

.DESCRIPTION
    Mirrors the two strategies in hunt-and-peck's UiAutomationHintProviderService
    before and after the cache-request optimization (commit 449435c):

      * UNCACHED (old): FindAll + per-element CurrentBoundingRectangle + up to
        six GetCurrentPattern calls -> ~7 cross-process round-trips per element.
      * CACHED   (new): one FindAllBuildCache that pre-fetches the bounding
        rectangle, the six patterns and the two IsReadOnly values, then reads
        them from the cache.

    Bring a UI-element-rich window to the foreground (a browser tab with many
    links, an IDE, a large file/list view) and run the script. The speedup is
    proportional to the number of matching elements, so pick a dense window to
    see a meaningful difference. No compilation required.

.PARAMETER Iterations
    Timed passes per strategy (default 10). More = steadier averages.

.EXAMPLE
    .\bench\Compare-Enumeration.ps1
    .\bench\Compare-Enumeration.ps1 -Iterations 25
#>
[CmdletBinding()]
param(
    [int]$Iterations = 10
)

$ErrorActionPreference = 'Stop'

# --- Win32: foreground window handle ---------------------------------------
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HapBench {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@

$hwnd = [HapBench]::GetForegroundWindow()
if ($hwnd -eq [IntPtr]::Zero) {
    throw "No foreground window. Bring the target window to the front first."
}

# --- UI Automation (COM CUIAutomation, same API the app uses) --------------
try {
    $ua = New-Object -ComObject UIAutomation.CUIAutomation
} catch {
    throw "Could not create UIAutomation.CUIAutomation COM object. Ensure UI Automation is available (it is on all desktop Windows). Error: $($_.Exception.Message)"
}

$root = $ua.ElementFromHandle($hwnd)
if ($null -eq $root) { throw "ElementFromHandle returned null for HWND $hwnd." }

# Property / pattern IDs (UIAutomationClient.h)
$UIA_IsEnabled        = 30010
$UIA_IsOffscreen      = 30009
$UIA_BoundingRect     = 30001
$UIA_ValueIsReadOnly  = 30046
$UIA_RangeValueIsRO   = 30048
$patterns = 10000, 10015, 10036, 10005, 10002, 10033  # Invoke, Toggle, SelectionItem, ExpandCollapse, Value, RangeValue

# Condition: control view AND enabled AND on-screen (same as the app)
$cv      = $ua.ControlViewCondition
$enabled = $ua.CreatePropertyCondition($UIA_IsEnabled, $true)
$onscr   = $ua.CreatePropertyCondition($UIA_IsOffscreen, $false)
$cond    = $ua.CreateAndCondition($ua.CreateAndCondition($cv, $enabled), $onscr)

$TreeScope_Descendants = 4
$TreeScope_Element     = 1

# Cache request matching the app's CreateHintCacheRequest
$cache = $ua.CreateCacheRequest()
$cache.TreeScope = $TreeScope_Element
$cache.AddProperty($UIA_BoundingRect)
$cache.AddProperty($UIA_ValueIsReadOnly)
$cache.AddProperty($UIA_RangeValueIsRO)
foreach ($p in $patterns) { [void]$cache.AddPattern($p) }

function Invoke-Uncached {
    param($root, $cond, $patterns, $scope)
    $arr = $root.FindAll($scope, $cond)
    $n = if ($arr) { $arr.Length } else { 0 }
    for ($i = 0; $i -lt $n; $i++) {
        $el = $arr.GetElement($i)
        [void]$el.CurrentBoundingRectangle            # cross-proc
        # GetCurrentPattern may return null or throw for unsupported patterns;
        # swallow to mirror the app's defensive try/catch in CreateHint.
        foreach ($p in $patterns) { try { [void]$el.GetCurrentPattern($p) } catch { } }
    }
    return $n
}

function Invoke-Cached {
    param($root, $cond, $cache, $patterns, $scope)
    $arr = $root.FindAllBuildCache($scope, $cond, $cache)
    $n = if ($arr) { $arr.Length } else { 0 }
    for ($i = 0; $i -lt $n; $i++) {
        $el = $arr.GetElement($i)
        [void]$el.CachedBoundingRectangle             # from cache
        foreach ($p in $patterns) { try { [void]$el.GetCachedPattern($p) } catch { } }
    }
    return $n
}

# Warm-up + element count (also surfaces which window we targeted)
$warmUncached = Invoke-Uncached $root $cond $patterns $TreeScope_Descendants
$warmCached   = Invoke-Cached   $root $cond $cache $patterns $TreeScope_Descendants

Write-Host ""
Write-Host ("Foreground window : HWND {0}" -f $hwnd)
Write-Host ("  Name            : {0}" -f $root.CurrentName)
Write-Host ("  ClassName       : {0}" -f $root.CurrentClassName)
Write-Host ("Matching elements : {0} (control view, enabled, on-screen)" -f $warmUncached)
Write-Host ("Iterations/strategy: {0}`n" -f $Iterations)

if ($warmUncached -eq 0) {
    Write-Warning "No matching elements found in the foreground window. Open a denser window (browser/IDE/large list) and re-run."
    return
}

# Time UNCACHED
$uncachedMs = @()
for ($r = 0; $r -lt $Iterations; $r++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void](Invoke-Uncached $root $cond $patterns $TreeScope_Descendants)
    $sw.Stop()
    $uncachedMs += $sw.Elapsed.TotalMilliseconds
}

# Time CACHED
$cachedMs = @()
for ($r = 0; $r -lt $Iterations; $r++) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    [void](Invoke-Cached $root $cond $cache $patterns $TreeScope_Descendants)
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
