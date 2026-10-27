<#
.SYNOPSIS
    Measures what BrimDeck costs when nobody touches it.

.DESCRIPTION
    Samples the whole process tree — the app and its WebView2 children — once a second and reports CPU seconds per
    profile, so an idle cost can be attributed instead of argued about.

    A profile is a throwaway data folder passed as --data-dir, which the app reads instead of %LOCALAPPDATA%\BrimDeck
    (src/BrimDeck.Core/AppPaths.cs). That is what makes an A/B possible: copy a profile, change one line of its
    settings.json, and the two runs differ by exactly that setting. Nothing here touches the installed app's own data.

    Two traps this script has already fallen into, and works around:

      - Get-CimInstance Win32_Process takes seconds on a busy machine, so the process tree is taken once per profile,
        not once per sample. A refresh inside the loop shows up as a two-second "burst" that is the harness, not the app.
      - [math]::Max(0, $double) binds the Int32 overload in Windows PowerShell and truncates every sub-second delta to
        zero, which reads as a perfectly idle app. Deltas are compared explicitly for that reason.

.EXAMPLE
    .\Measure-Idle.ps1 -LiveOnly -ProcessId 58712 -Seconds 420
    Watch the instance the user actually runs for long enough to span the five-minute memory trim.

.EXAMPLE
    .\Measure-Idle.ps1 -Profiles "bare,clock=..\samples\clock-web" -Seconds 130
    Start two throwaway profiles — one empty, one with the sample plugin — and report each.

.PARAMETER ProcessId
    A running BrimDeck to measure instead of starting one.

.PARAMETER Profiles
    Comma-separated throwaway profiles to create and measure: "name" for an empty one, "name=path" to seed a folder of plugins.

.PARAMETER Keep
    Reuse the profile folders as they are instead of deleting them first, so a second run measures a warm cache.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\BrimDeck\bin\Release\net10.0-windows10.0.19041.0\BrimDeck.exe"),
    [int]$Seconds = 120,
    [int]$ProcessId = 0,
    [string]$Profiles = "",
    [string]$Sample = (Join-Path $PSScriptRoot "..\samples\clock-web"),
    [switch]$LiveOnly,
    [switch]$Keep
)

$ErrorActionPreference = "Continue"

function Say([string]$text) { [Console]::Out.WriteLine($text); [Console]::Out.Flush() }

# Every process under $root, by walking parent ids. One query, because it is expensive.
function Get-TreePids([int]$root) {
    $rows = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId -ErrorAction SilentlyContinue)
    $keep = @($root); $frontier = @($root)
    while ($frontier.Count -gt 0) {
        $next = @($rows | Where-Object { $frontier -contains $_.ParentProcessId } | ForEach-Object { $_.ProcessId })
        $frontier = @($next | Where-Object { $keep -notcontains $_ })
        $keep = @($keep + $frontier)
    }
    return @($keep | Where-Object { $_ })
}

function Measure-Tree([string]$label, [int]$root) {
    $previous = @{}
    $start = Get-Date
    foreach ($found in @(Get-TreePids $root)) {
        try { $previous[$found] = (Get-Process -Id $found -ErrorAction Stop).TotalProcessorTime.TotalSeconds } catch { }
    }
    $rows = @()
    for ($i = 0; $i -lt $Seconds; $i++) {
        Start-Sleep -Milliseconds 1000
        $app = 0.0; $web = 0.0; $memory = 0; $live = 0
        foreach ($child in @($previous.Keys)) {
            try {
                $process = Get-Process -Id $child -ErrorAction Stop
                $total = $process.TotalProcessorTime.TotalSeconds
                $delta = [double]($total - $previous[$child])
                if ($delta -lt 0) { $delta = 0 }
                if ($process.ProcessName -like "*webview2*") { $web += $delta } else { $app += $delta }
                $previous[$child] = $total
                $memory += $process.WorkingSet64
                $live++
            } catch { }
        }
        $rows += [pscustomobject]@{
            Second = $i; At = (Get-Date -Format "HH:mm:ss"); Total = [math]::Round($app + $web, 4)
            App = [math]::Round($app, 4); Web = [math]::Round($web, 4); Live = $live; Mb = [math]::Round($memory / 1MB)
        }
    }
    Report $label $rows ((Get-Date) - $start).TotalSeconds
}

function Report([string]$label, $rows, [double]$wall) {
    $appTotal = [math]::Round(($rows | Measure-Object App -Sum).Sum, 2)
    $webTotal = [math]::Round(($rows | Measure-Object Web -Sum).Sum, 2)
    $all = $appTotal + $webTotal
    $sorted = @($rows | Sort-Object Total | ForEach-Object { $_.Total })
    Say ("PROFILE {0}: cpu {1}s (app {2}s + webview {3}s) over {4}s wall = {5}% of one core; median {6}s/s, p90 {7}s/s, peak {8}s/s; procs {9}; mem {10}MB" -f `
        $label, $all, $appTotal, $webTotal, [math]::Round($wall, 1), [math]::Round(100 * $all / $wall, 2), `
        $sorted[[int]($sorted.Count / 2)], $sorted[[int](($sorted.Count - 1) * 0.9)], $sorted[-1], $rows[0].Live, `
        [math]::Round(($rows | Measure-Object Mb -Average).Average))
    # Periodic work shows here as named seconds; a flat cost shows as none. Memory rising through a burst is work that
    # allocates, memory falling through one is the collector giving pages back.
    $loud = @($rows | Where-Object { $_.Total -ge [math]::Round(6 * $sorted[[int]($sorted.Count / 2)], 4) -and $_.Total -ge 0.1 })
    Say ("   bursts: " + $(if ($loud.Count) { ($loud | ForEach-Object { $_.At + " #" + $_.Second + " cpu=" + $_.Total + " mb=" + $_.Mb }) -join " | " } else { "none" }))
}

function Invoke-Profile([string]$spec) {
    # "name" for an empty profile, or "name=path" to seed one folder of plugins. "=" because a Windows path is full of
    # colons, and the separator must not be one of them.
    $name = $spec; $folder = ""
    if ($spec.Contains("=")) { $name = $spec.Split("=", 2)[0]; $folder = $spec.Split("=", 2)[1] }
    $data = Join-Path $env:TEMP ("BrimDeck-Idle-" + $name)
    if (-not $Keep -and (Test-Path $data)) { Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    if ($folder -ne "") {
        New-Item -ItemType Directory -Force -Path (Join-Path $data "extensions") | Out-Null
        Copy-Item -Recurse -Force $folder (Join-Path $data ("extensions\" + (Split-Path $folder -Leaf)))
    }
    $env:BRIMDECK_DATA_DIR = $data
    $env:BRIMDECK_EXTENSION_LOG = "1"
    $root = Start-Process -FilePath $Exe -ArgumentList @("--data-dir", $data) -PassThru
    Say ("profile " + $name + " started as pid " + $root.Id + " at " + (Get-Date -Format "HH:mm:ss") + "  data: " + $data)
    Start-Sleep -Seconds 14   # the boot animation, the first layout, the opening scan, and the 15s reclaim grace
    Measure-Tree $name $root.Id
    foreach ($child in @(Get-TreePids $root.Id)) { Stop-Process -Id $child -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
}

Say ("start " + (Get-Date -Format "HH:mm:ss") + "  window " + $Seconds + "s  keep=" + [bool]$Keep)
if (-not $Keep -and -not $LiveOnly -and $Profiles.Trim().Length -gt 0) { Say "note: profiles are recreated empty unless -Keep is given, so the first run measures cold caches" }
if ($ProcessId -gt 0) { Measure-Tree "running" $ProcessId }
if (-not $LiveOnly) { foreach ($spec in ($Profiles -split ",")) { if ($spec.Trim().Length) { Invoke-Profile $spec.Trim() } } }
Say ("end " + (Get-Date -Format "HH:mm:ss"))
