# Runs only prebuilt launcher copies and an isolated WPF page fixture.
# It never starts/stops Codex or a production watcher and never builds production.
[CmdletBinding()]
param([string]$LauncherPath = '')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $LauncherPath) { $LauncherPath = Join-Path $projectRoot 'Start-Codex-Dragon.exe' }
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) { throw 'Build the launcher before running page integration tests.' }
$runId = 'page-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
$runRoot = Join-Path $PSScriptRoot ('output\' + $runId)
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
}
$fixtureBuild = Join-Path $runRoot 'PageTarget.exe'
$compileArguments = @('/nologo', '/target:winexe', '/main:PageTarget', '/codepage:65001', '/warn:4', '/warnaserror+', ('/out:' + $fixtureBuild))
foreach ($assembly in @('PresentationCore', 'PresentationFramework', 'WindowsBase')) {
    $compileArguments += '/r:' + (Join-Path $framework ('WPF\' + $assembly + '.dll'))
}
$compileArguments += '/r:' + (Join-Path $framework 'System.Xaml.dll')
$compileArguments += Join-Path $PSScriptRoot 'FakeTarget.cs'
$compileArguments += Join-Path $PSScriptRoot 'PageTarget.cs'
& $compiler @compileArguments
if ($LASTEXITCODE -ne 0) { throw 'Page target fixture compilation failed.' }

# Occlusion evidence verifies actual window ownership at the target's center,
# rather than assuming that a live overlay process is visibly covering it.
if (-not ('PageFixtureNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PageFixtureNative
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    public static uint CoveringProcess(long hwnd)
    {
        Rect rect;
        if (!IsWindowVisible(new IntPtr(hwnd)) || !GetWindowRect(new IntPtr(hwnd), out rect)) return 0;
        var point = new Point { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
        IntPtr visible = WindowFromPoint(point);
        uint pid; GetWindowThreadProcessId(visible, out pid); return pid;
    }
}
'@
}

$results = [Collections.Generic.List[object]]::new()
$owned = [Collections.Generic.List[object]]::new()
$rootConfigPath = Join-Path $projectRoot 'launcher.config.xml'
$rootConfigHash = (Get-FileHash -LiteralPath $rootConfigPath -Algorithm SHA256).Hash

function New-PageScenario {
    param([string]$Name, [string]$Theme = 'light', [int]$ReadyDelayMs = 2700, [double]$Timeout = 15,
          [switch]$Spinner, [switch]$MissingVideo, [switch]$Controlled)
    $folder = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item -LiteralPath $LauncherPath -Destination (Join-Path $folder 'Start-Codex-Dragon.exe')
    $target = Join-Path $folder 'PageTarget.exe'
    Copy-Item -LiteralPath $fixtureBuild -Destination $target
    $atlasFolder = Join-Path $folder 'assets'
    New-Item -ItemType Directory -Path $atlasFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\dragon-eye-motion.png') -Destination $atlasFolder
    $marker = Join-Path $folder 'target-events.tsv'
    $control = Join-Path $folder 'page-control.txt'
    $fixtureArguments = '--window-delay-ms 30 --theme ' + $Theme + ' --ready-delay-ms ' + $ReadyDelayMs +
        ' --lifetime-ms 18000 --marker "' + $marker + '"'
    if ($Spinner) { $fixtureArguments += ' --spinner' }
    if ($Controlled) { $fixtureArguments += ' --control "' + $control + '" --flash-duration-ms 320' }
    $video = Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4'
    if ($MissingVideo) { $video = Join-Path $folder 'missing-video.mp4' }
    $xml = [xml]'<launcher />'
    $values = [ordered]@{
        durationSeconds = '8.0'; frameRate = '60'; videoFile = $video
        appUserModelId = ''; executablePath = $target
        # Passive mode must never use this deliberately invalid argument.
        executableArguments = '--unexpected-activation'; requirePageContent = 'true'
        characterImage = (Join-Path $projectRoot 'assets\dragon-cinematic-open.png')
        closedEyeImage = (Join-Path $projectRoot 'assets\dragon-cinematic-closed.png')
        respectReducedMotion = 'false'; startupTimeoutSeconds = $Timeout.ToString([Globalization.CultureInfo]::InvariantCulture)
        windowReadyDelaySeconds = '0.5'; tailHoldSeconds = '7.75'
        blinkIntervalSeconds = '4.0'; blinkDurationSeconds = '0.28'
    }
    foreach ($entry in $values.GetEnumerator()) {
        $node = $xml.CreateElement($entry.Key); $node.InnerText = $entry.Value
        [void]$xml.DocumentElement.AppendChild($node)
    }
    $xml.Save((Join-Path $folder 'launcher.config.xml'))
    return [pscustomobject]@{
        Name = $Name; Folder = $folder; Target = $target; Marker = $marker; Control = $control
        Arguments = $fixtureArguments; Timeout = $Timeout; Theme = $Theme; Spinner = [bool]$Spinner; MissingVideo = [bool]$MissingVideo
    }
}

function Start-Owned {
    param([string]$Path, [string]$Arguments, [string]$Folder)
    $info = [Diagnostics.ProcessStartInfo]::new($Path, $Arguments)
    $info.UseShellExecute = $false; $info.WorkingDirectory = $Folder
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add([pscustomobject]@{ Process = $process; Path = [IO.Path]::GetFullPath($Path); Birth = $process.StartTime.ToUniversalTime() })
    return $process
}

function Stop-Owned {
    param([Diagnostics.Process]$Process)
    if (-not $Process) { return }
    $record = @($owned | Where-Object { $_.Process -eq $Process }) | Select-Object -First 1
    if (-not $record) { throw 'Refusing to stop an unowned process.' }
    try {
        if (-not $Process.HasExited -and
            [IO.Path]::GetFullPath($Process.Path) -eq $record.Path -and
            $Process.StartTime.ToUniversalTime() -eq $record.Birth -and
            $record.Path.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            [void]$Process.CloseMainWindow()
            if (-not $Process.WaitForExit(1200)) { $Process.Kill(); [void]$Process.WaitForExit(3000) }
        }
    } catch [ArgumentException] { }
}

function Wait-Condition {
    param([scriptblock]$Condition, [int]$TimeoutMs = 5000)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 30
    } while ($clock.ElapsedMilliseconds -lt $TimeoutMs)
    return $false
}

function Read-Timing {
    param($Scenario)
    $path = Join-Path $Scenario.Folder 'output\startup-timing.log'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    try { return @(Import-Csv -LiteralPath $path -Delimiter "`t") } catch { return @() }
}

function Read-Markers {
    param($Scenario)
    if (-not (Test-Path -LiteralPath $Scenario.Marker)) { return @() }
    try {
        return @(Get-Content -LiteralPath $Scenario.Marker -Encoding UTF8 | ForEach-Object {
            $parts = $_ -split "`t", 4
            if ($parts.Count -ge 3) {
                [pscustomobject]@{ utc = $parts[0]; pid = [int]$parts[1]; event = $parts[2]; detail = $(if ($parts.Count -eq 4) { $parts[3] } else { '' }) }
            }
        })
    } catch { return @() }
}

function Timing-Epoch {
    param([object[]]$Events)
    # StartupCoordinator's UTC anchor lets independent fixture timestamps be
    # compared with stopwatch logs without guessing process scheduling delay.
    foreach ($item in $Events) {
        if ($item.event -eq 'startup_started' -and $item.detail -match 'utc=([^;\s]+)') {
            $utc = [DateTime]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
            return $utc.AddSeconds(-[double]::Parse($item.seconds, [Globalization.CultureInfo]::InvariantCulture))
        }
    }
    throw 'Startup timing UTC anchor is missing; run the suite against the current page-readiness build.'
}

function Add-Result {
    param([string]$Name, [bool]$Passed, $Evidence)
    $results.Add([pscustomobject]@{ name = $Name; passed = $Passed; evidence = $Evidence })
    Write-Output ((@('FAIL', 'PASS')[[int]$Passed]) + ': ' + $Name)
}

function Start-Scenario {
    param($Scenario)
    $launcher = Start-Owned (Join-Path $Scenario.Folder 'Start-Codex-Dragon.exe') '--observe --startup-test' $Scenario.Folder
    if (-not (Wait-Condition { @(Read-Timing $Scenario | Where-Object event -eq 'observation_started').Count -eq 1 })) {
        throw 'Passive overlay did not start.'
    }
    $target = Start-Owned $Scenario.Target $Scenario.Arguments $Scenario.Folder
    return [pscustomobject]@{ Launcher = $launcher; Target = $target }
}

function Complete-ReadyScenario {
    param($Scenario, $Processes, [bool]$CoveringVerified, [object[]]$ExtraEvidence = @(), [switch]$Transient)
    if (-not $Processes.Launcher.WaitForExit(16000)) { throw 'Isolated overlay did not finish.' }
    $events = @(Read-Timing $Scenario)
    $markers = @(Read-Markers $Scenario)
    $epoch = Timing-Epoch $events
    $rendered = @($markers | Where-Object event -eq 'page_ready_rendered')
    $observed = @($events | Where-Object event -eq 'window_observed')
    $ready = @($events | Where-Object event -eq 'page_content_ready')
    $dismissed = @($events | Where-Object { $_.event -eq 'overlay_dismissed' -and $_.detail -eq 'window_ready' })
    $completed = @($events | Where-Object { $_.event -eq 'completed' -and $_.detail -match '^window_ready;' })
    $activationCount = @($events | Where-Object event -eq 'activation_requested').Count
    $starts = @($markers | Where-Object event -eq 'process_started').Count
    $handoffUtc = $null; $renderUtc = $null; $handoffAfterPageMs = $null; $stableSeconds = $null
    if ($rendered.Count -eq 1 -and $dismissed.Count -eq 1 -and $ready.Count -ge 1) {
        $renderUtc = [DateTime]::Parse($rendered[0].utc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        $handoffUtc = $epoch.AddSeconds([double]::Parse($dismissed[0].seconds, [Globalization.CultureInfo]::InvariantCulture))
        $handoffAfterPageMs = ($handoffUtc - $renderUtc).TotalMilliseconds
        $stableSeconds = [double]::Parse($dismissed[0].seconds, [Globalization.CultureInfo]::InvariantCulture) -
            [double]::Parse($ready[-1].seconds, [Globalization.CultureInfo]::InvariantCulture)
    }
    $passed = $Processes.Launcher.ExitCode -eq 0 -and $rendered.Count -eq 1 -and $observed.Count -ge 1 -and
        $ready.Count -ge 1 -and $dismissed.Count -eq 1 -and $completed.Count -eq 1 -and $starts -eq 1 -and
        $activationCount -eq 0 -and $CoveringVerified -and $null -ne $handoffAfterPageMs -and
        $handoffAfterPageMs -ge 420 -and $stableSeconds -ge 0.48 -and -not $Processes.Target.HasExited
    $intro = @($events | Where-Object event -eq 'intro_completed')
    $fadeStart = @($events | Where-Object event -eq 'ready_fade_started')
    $fadeEnd = @($events | Where-Object event -eq 'ready_fade_completed')
    $passed = $passed -and $intro.Count -eq 1 -and $fadeStart.Count -eq 1 -and $fadeEnd.Count -eq 1 -and
        [Array]::IndexOf($events,$intro[0]) -lt [Array]::IndexOf($events,$dismissed[0]) -and
        @( $events | Where-Object event -in @('ready_fade_failed','shutdown_deadline') ).Count -eq 0
    if ($Transient) {
        $lost = @($events | Where-Object event -eq 'page_content_lost')
        $passed = $passed -and $ready.Count -ge 2 -and $lost.Count -ge 1 -and
            [double]::Parse($lost[-1].seconds, [Globalization.CultureInfo]::InvariantCulture) -lt
            [double]::Parse($ready[-1].seconds, [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Scenario.MissingVideo) {
        $passed = $passed -and @( $events | Where-Object event -eq 'animation_started' ).Count -eq 0 -and
            @( $events | Where-Object event -eq 'fallback_failed' ).Count -eq 0 -and
            @( $events | Where-Object event -eq 'media_failed' ).Count -eq 0
    }
    Add-Result $Scenario.Name $passed ([ordered]@{
        exitCode = $Processes.Launcher.ExitCode; activations = $activationCount; targetStarts = $starts
        overlayCoveredTarget = $CoveringVerified; targetPageRenderedUtc = $renderUtc
        handoffUtc = $handoffUtc; handoffAfterPageMs = $handoffAfterPageMs; stableSeconds = $stableSeconds
        fixtureMarkers = $markers; startupEvents = $events; extra = $ExtraEvidence; folder = $Scenario.Folder
    })
}

function Check-Coverage {
    param($Scenario, $Processes)
    return Wait-Condition {
        if ($Processes.Launcher.HasExited) { return $false }
        $marker = @(Read-Markers $Scenario | Where-Object event -eq 'window_rendered') | Select-Object -First 1
        if (-not $marker -or $marker.detail -notmatch 'hwnd=(\d+)') { return $false }
        return [PageFixtureNative]::CoveringProcess([long]$Matches[1]) -eq [uint32]$Processes.Launcher.Id
    } 3500
}

function Test-DelayedPage {
    param([string]$Name, [string]$Theme = 'light', [switch]$Spinner, [switch]$MissingVideo)
    $scenario = New-PageScenario -Name $Name -Theme $Theme -Spinner:$Spinner -MissingVideo:$MissingVideo
    $processes = $null
    try {
        $processes = Start-Scenario $scenario
        $coverage = Check-Coverage $scenario $processes
        Complete-ReadyScenario $scenario $processes $coverage
    } catch { Add-Result $Name $false ([ordered]@{ error = $_.Exception.ToString(); folder = $scenario.Folder; markers = @(Read-Markers $scenario); events = @(Read-Timing $scenario) }) }
    finally { if ($processes) { Stop-Owned $processes.Target; Stop-Owned $processes.Launcher } }
}

function Test-BlankTimeout {
    param([string]$Name, [string]$Theme, [switch]$Spinner)
    $scenario = New-PageScenario -Name $Name -Theme $Theme -Spinner:$Spinner -ReadyDelayMs -1 -Timeout 3.5
    $processes = $null
    try {
        $processes = Start-Scenario $scenario
        $coverage = Check-Coverage $scenario $processes
        if (-not $processes.Launcher.WaitForExit(8000)) { throw 'Blank-page timeout did not exit.' }
        $events = @(Read-Timing $scenario); $markers = @(Read-Markers $scenario)
        $completed = @($events | Where-Object { $_.event -eq 'completed' -and $_.detail -match '^window_timeout;' })
        $passed = $processes.Launcher.ExitCode -eq 2 -and $coverage -and $completed.Count -eq 1 -and
            @( $events | Where-Object event -eq 'window_observed' ).Count -ge 1 -and
            @( $events | Where-Object event -eq 'page_content_ready' ).Count -eq 0 -and
            @( $events | Where-Object event -eq 'activation_requested' ).Count -eq 0 -and
            [double]::Parse($completed[0].seconds, [Globalization.CultureInfo]::InvariantCulture) -ge $scenario.Timeout -and
            -not $processes.Target.HasExited
        Add-Result $Name $passed ([ordered]@{ exitCode = $processes.Launcher.ExitCode; overlayCoveredTarget = $coverage; markers = $markers; events = $events; folder = $scenario.Folder })
    } catch { Add-Result $Name $false ([ordered]@{ error = $_.Exception.ToString(); folder = $scenario.Folder; markers = @(Read-Markers $scenario); events = @(Read-Timing $scenario) }) }
    finally { if ($processes) { Stop-Owned $processes.Target; Stop-Owned $processes.Launcher } }
}

try {
    Test-DelayedPage 'light-blank-until-page'
    Test-DelayedPage 'dark-blank-until-page' -Theme dark
    Test-DelayedPage 'spinner-until-page' -Spinner
    Test-BlankTimeout 'blank-page-times-out' -Theme light
    Test-BlankTimeout 'spinner-only-times-out' -Theme dark -Spinner
    Test-DelayedPage 'missing-video-still-waits-for-page' -Spinner -MissingVideo

    $scenario = New-PageScenario -Name 'transient-page-resets-stability' -Theme dark -ReadyDelayMs -1 -Timeout 12 -Controlled
    $processes = $null
    try {
        $processes = Start-Scenario $scenario
        $coverage = Check-Coverage $scenario $processes
        if (-not (Wait-Condition { @(Read-Timing $scenario | Where-Object event -eq 'window_observed').Count -ge 1 })) { throw 'Native window alignment was not recorded.' }
        $transientObserved = $false
        # A page may render between sampling ticks. Retry at most three natural
        # 320 ms flashes; each is independently shorter than the 0.5 s gate.
        for ($attempt = 1; $attempt -le 3 -and -not $transientObserved; $attempt++) {
            [IO.File]::WriteAllText($scenario.Control, 'flash:' + $attempt)
            $transientObserved = Wait-Condition {
                @(Read-Timing $scenario | Where-Object event -eq 'page_content_lost').Count -ge 1
            } 1800
            if ($processes.Launcher.HasExited) { throw 'Animation ended during a transient page flash.' }
        }
        if (-not $transientObserved) { throw 'No transient content transition was sampled; insufficient regression evidence.' }
        $beforeFinal = @(Read-Timing $scenario)
        $preFinalSafe = @( $beforeFinal | Where-Object event -eq 'overlay_dismissed' ).Count -eq 0 -and -not $processes.Launcher.HasExited
        [IO.File]::WriteAllText($scenario.Control, 'ready:final')
        Complete-ReadyScenario $scenario $processes ($coverage -and $preFinalSafe) @([ordered]@{ preFinalOverlayStayedOpen = $preFinalSafe; flashesAttempted = $attempt - 1; beforeFinal = $beforeFinal }) -Transient
    } catch { Add-Result $scenario.Name $false ([ordered]@{ error = $_.Exception.ToString(); folder = $scenario.Folder; markers = @(Read-Markers $scenario); events = @(Read-Timing $scenario) }) }
    finally { if ($processes) { Stop-Owned $processes.Target; Stop-Owned $processes.Launcher } }
} finally {
    foreach ($record in $owned) { try { Stop-Owned $record.Process } catch { } }
    # A force-killed overlay cannot run its worker cleanup. These copies have
    # unique scenario paths; terminate only test descendants at an exact path
    # already launched by this run, with a birth time after the owned parent.
    foreach ($record in $owned) {
        if (-not $record.Path.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        foreach ($candidate in @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($record.Path)) -ErrorAction SilentlyContinue)) {
            try {
                if (-not $candidate.HasExited -and [IO.Path]::GetFullPath($candidate.Path) -eq $record.Path -and
                    $candidate.StartTime.ToUniversalTime() -ge $record.Birth) {
                    $candidate.Kill(); [void]$candidate.WaitForExit(3000)
                }
            } catch { }
        }
    }
    $finalConfigHash = (Get-FileHash -LiteralPath $rootConfigPath -Algorithm SHA256).Hash
    Add-Result 'production-config-unchanged' ($rootConfigHash -eq $finalConfigHash) ([ordered]@{ beforeSha256 = $rootConfigHash; afterSha256 = $finalConfigHash })
    $report = [ordered]@{
        runId = $runId; passed = @($results | Where-Object { -not $_.passed }).Count -eq 0
        launcherPath = $LauncherPath; launcherSha256 = (Get-FileHash -LiteralPath $LauncherPath -Algorithm SHA256).Hash
        completedUtc = [DateTime]::UtcNow.ToString('o'); root = $runRoot
        target = 'Isolated WPF page fixtures only; no real Codex or production watcher operations.'
        interpretation = 'Proves delayed interactive page handoff for fixtures; does not prove real Electron app completion.'
        tests = @($results.ToArray())
    }
    $reportPath = Join-Path $runRoot 'results.json'
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 14), [Text.UTF8Encoding]::new($false))
    Write-Output ('Results: ' + $reportPath)
}
if (-not $report.passed) { exit 1 }
