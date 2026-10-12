[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $PSScriptRoot ('output\watcher-audio-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$fixture = Join-Path $runRoot 'FakeTarget.exe'
$compilerArgs = @('/nologo','/target:winexe','/codepage:65001','/warn:4','/warnaserror+',('/out:' + $fixture))
foreach ($assembly in @('PresentationCore','PresentationFramework','WindowsBase')) {
    $compilerArgs += '/r:' + (Join-Path $framework ('WPF\' + $assembly + '.dll'))
}
$compilerArgs += '/r:' + (Join-Path $framework 'System.Xaml.dll')
$compilerArgs += Join-Path $PSScriptRoot 'FakeTarget.cs'
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Fake target build failed.' }
Add-Type -Path (Join-Path $PSScriptRoot 'OwnedAudioMeter.cs')
foreach ($name in @('Start-Codex-Dragon.exe','Watch-Codex-Dragon.exe')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $runRoot
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets') -Destination $runRoot -Recurse
[xml]$config = Get-Content -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Raw -Encoding utf8
$rootConfigHash = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Algorithm SHA256).Hash
$audioPath = Join-Path $runRoot 'assets\dragon-startup.wav'
$config.SelectSingleNode('/launcher/soundEnabled').InnerText = 'true'
$config.SelectSingleNode('/launcher/soundVolume').InnerText = '0.44'
$config.SelectSingleNode('/launcher/soundFile').InnerText = [string]$audioPath
$config.SelectSingleNode('/launcher/respectReducedMotion').InnerText = 'false'
$config.SelectSingleNode('/launcher/appUserModelId').InnerText = ''
$config.SelectSingleNode('/launcher/executablePath').InnerText = [string]$fixture
$config.SelectSingleNode('/launcher/executableArguments').InnerText = '--this-must-never-be-used'
$config.SelectSingleNode('/launcher/videoFile').InnerText = [string](Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4')
$config.SelectSingleNode('/launcher/startupTimeoutSeconds').InnerText = '18'
$config.Save((Join-Path $runRoot 'launcher.config.xml'))
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$samples = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
$watchExe = Join-Path $runRoot 'Watch-Codex-Dragon.exe'
$overlayExe = Join-Path $runRoot 'Start-Codex-Dragon.exe'
$marker = Join-Path $runRoot 'target.tsv'
$overlay = $null; $watcher = $null
$events = @()
$timingPath = Join-Path $runRoot 'output\startup-timing.log'
function Start-Owned([string]$File, [string]$Arguments = '') {
    $info = [Diagnostics.ProcessStartInfo]::new($File, $Arguments)
    $info.UseShellExecute = $false; $info.WorkingDirectory = $runRoot
    $p = [Diagnostics.Process]::Start($info); $owned.Add($p); return $p
}
function Read-Status {
    try { return (Get-Content -LiteralPath (Join-Path $runRoot 'output\watcher-status.json') -Raw -Encoding utf8 | ConvertFrom-Json) }
    catch { return $null }
}
function Wait-Condition([scriptblock]$Condition, [int]$Deadline = 7000) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 100 } while ($clock.ElapsedMilliseconds -lt $Deadline)
    return $false
}
function Check([string]$Name, [bool]$Passed, $Evidence) {
    $checks.Add([pscustomobject]@{name=$Name;passed=$Passed;evidence=$Evidence})
    Write-Output ((@('FAIL','PASS')[[int]$Passed]) + ': ' + $Name)
    if (-not $Passed) { throw $Name }
}
try {
    Write-Output 'This test plays the deployed 8.3-second WAV once at 44% through an owned overlay; no real Codex activation.'
    $watcher = Start-Owned $watchExe
    Check 'empty-owned-baseline' (Wait-Condition { $s=Read-Status; $s -and $s.running -and $s.cyclesStarted -eq 0 }) (Read-Status)
    $target = Start-Owned $fixture ('--delay-ms 11000 --lifetime-ms 18000 --marker "' + $marker + '"')
    Check 'watcher-created-overlay' (Wait-Condition { $s=Read-Status; $s -and $s.cyclesStarted -eq 1 -and $s.activeOverlayPid }) (Read-Status)
    $overlay = [Diagnostics.Process]::GetProcessById([int](Read-Status).activeOverlayPid)
    if (-not $overlay.Path.Equals($overlayExe, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to inspect an unowned audio process.' }
    # Retain the native handle while the watcher-owned child is alive. Unlike a
    # process started by this PowerShell, a PID-only Process has no cached exit
    # handle and can lose its exit code after the OS releases the process.
    $null = $overlay.Handle
    $owned.Add($overlay)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $tailSeenAt = $null
    $soundFinishedSeenAt = $null
    while (-not $overlay.HasExited -and $clock.ElapsedMilliseconds -lt 19000) {
        # Read the test overlay's own timing log to align peak samples with
        # its handoff and natural finish, without relying on different clock
        # origins between the coordinator and this PowerShell process.
        $liveEvents = @()
        try {
            if (Test-Path -LiteralPath $timingPath) { $liveEvents = @(Import-Csv -LiteralPath $timingPath -Delimiter "`t") }
        } catch { }
        $sampleSeconds = $clock.Elapsed.TotalSeconds
        if ($null -eq $tailSeenAt -and @($liveEvents | Where-Object event -eq 'tail_handoff_started').Count -eq 1) {
            $tailSeenAt = $sampleSeconds
        }
        if ($null -eq $soundFinishedSeenAt -and @($liveEvents | Where-Object event -eq 'sound_finished').Count -eq 1) {
            $soundFinishedSeenAt = $sampleSeconds
        }
        $sample = [OwnedAudioMeter]::Read($overlay.Id)
        $afterFinish = $null
        if ($null -ne $soundFinishedSeenAt) { $afterFinish = $sampleSeconds - $soundFinishedSeenAt }
        $samples.Add([pscustomobject]@{seconds=$sampleSeconds;found=$sample.Found;peak=$sample.Peak;muted=$sample.Muted;sessionVolume=$sample.SessionVolume;sessions=$sample.Sessions;error=$sample.Error;
            tailHandoffSeen=($null -ne $tailSeenAt);soundFinishedSeen=($null -ne $soundFinishedSeenAt);secondsAfterSoundFinish=$afterFinish})
        Start-Sleep -Milliseconds 120
    }
    if ($overlay.HasExited) { $overlay.WaitForExit() }
    Check 'overlay-exited-ready' ($overlay.HasExited -and $overlay.ExitCode -eq 0) @{pid=$overlay.Id;exited=$overlay.HasExited;exitCode=$overlay.ExitCode}
    $events = @(Import-Csv -LiteralPath $timingPath -Delimiter "`t")
    $watchEvents = @(Import-Csv -LiteralPath (Join-Path $runRoot 'output\watcher-events.tsv') -Delimiter "`t" -Header utc,event,detail)
    $starts = @($events | Where-Object event -eq 'sound_started')
    $opens = @($events | Where-Object event -eq 'sound_opened')
    Check 'approved-wav-started-once' ($starts.Count -eq 1 -and $starts[0].detail -match '; volume=0\.440$' -and $opens.Count -eq 1 -and $opens[0].detail -match '^duration=8\.300s;' -and @($events | Where-Object event -in @('sound_disabled','sound_failed','sound_resumed','sound_completion_failed')).Count -eq 0) @{opened=$opens;started=$starts}
    $triggers = @($watchEvents | Where-Object event -eq 'overlay_started')
    Check 'production-observe-no-activation' ($triggers.Count -eq 1 -and $triggers[0].detail -match 'arguments=--observe$' -and @($events | Where-Object event -eq 'activation_requested').Count -eq 0 -and @(Get-Content -LiteralPath $marker | Where-Object { $_ -match 'process_started$' }).Count -eq 1) $triggers
    $positive = @($samples | Where-Object { $_.found -and -not $_.muted -and $_.sessionVolume -gt 0 -and $_.peak -gt 0.00001 })
    $peak = ($samples | Measure-Object -Property peak -Maximum).Maximum
    Check 'owned-windows-session-has-signal' ($positive.Count -ge 3) @{maximumPeak=$peak;positiveSamples=$positive.Count;sampleCount=$samples.Count;errors=@($samples | Where-Object error | Select-Object -ExpandProperty error -Unique)}
    $tail = @($events | Where-Object event -eq 'animation_tail')
    $handoff = @($events | Where-Object event -eq 'tail_handoff_started')
    $clockReleased = @($events | Where-Object event -eq 'sound_video_released')
    $release = @($events | Where-Object event -eq 'sound_finished')
    $earlyFades = @($events | Where-Object event -eq 'sound_fade_completed')
    $finishDelay = $null
    if ($handoff.Count -eq 1 -and $release.Count -eq 1) {
        $finishDelay = [double]::Parse($release[0].seconds, [Globalization.CultureInfo]::InvariantCulture) - [double]::Parse($handoff[0].seconds, [Globalization.CultureInfo]::InvariantCulture)
    }
    Check 'sound-naturally-released-after-tail-handoff' ($tail.Count -eq 1 -and $handoff.Count -eq 1 -and $clockReleased.Count -eq 1 -and $release.Count -eq 1 -and $earlyFades.Count -eq 0 -and
        $release[0].detail -match 'decoder released' -and $finishDelay -ge 0.28 -and $finishDelay -lt 1.5 -and
        [Array]::IndexOf($events,$release[0]) -gt [Array]::IndexOf($events,$tail[0]) -and
        $tail[0].detail -match 'blink, hair and title lights=enabled' -and
        @($events | Where-Object { $_.event -eq 'overlay_dismissed' -and $_.detail -eq 'window_ready' }).Count -eq 1) @{tail=$tail;handoff=$handoff;clockReleased=$clockReleased;release=$release;secondsAfterHandoff=$finishDelay}
    $tailSignal = @($positive | Where-Object { $_.tailHandoffSeen -and -not $_.soundFinishedSeen })
    Check 'owned-audio-signal-crosses-tail-boundary' ($tailSignal.Count -ge 1) @{positiveSamplesAfterHandoff=$tailSignal.Count;samples=$tailSignal}
    # Allow the audio endpoint's short buffered release interval before checking
    # several late samples. An absent owned session and a zero peak both mean
    # that this overlay has stopped producing audio; unrelated sessions are
    # never inspected.
    $late = @($samples | Where-Object { $_.soundFinishedSeen -and $_.secondsAfterSoundFinish -ge 0.35 })
    $lateSignal = @($late | Where-Object { $_.found -and $_.peak -gt 0.00001 })
    Check 'waiting-tail-stays-silent-after-natural-end' ($late.Count -ge 3 -and $lateSignal.Count -eq 0 -and @($late | Where-Object error).Count -eq 0) @{lateSamples=$late.Count;latePositiveSamples=$lateSignal.Count;maximumLatePeak=($late | Measure-Object -Property peak -Maximum).Maximum}
    $intro = @($events | Where-Object event -eq 'intro_completed')
    $dismiss = @($events | Where-Object { $_.event -eq 'overlay_dismissed' -and $_.detail -eq 'window_ready' })
    $fadeStart = @($events | Where-Object event -eq 'ready_fade_started')
    $fadeEnd = @($events | Where-Object event -eq 'ready_fade_completed')
    $shutdown = @($events | Where-Object event -eq 'shutdown')
    Check 'complete-cue-before-transparent-ready-fade' ($intro.Count -eq 1 -and $dismiss.Count -eq 1 -and
        $fadeStart.Count -eq 1 -and $fadeEnd.Count -eq 1 -and $shutdown.Count -eq 1 -and
        [Array]::IndexOf($events,$release[0]) -lt [Array]::IndexOf($events,$intro[0]) -and
        [Array]::IndexOf($events,$intro[0]) -lt [Array]::IndexOf($events,$dismiss[0]) -and
        [Array]::IndexOf($events,$fadeEnd[0]) -lt [Array]::IndexOf($events,$shutdown[0]) -and
        $shutdown[0].detail -match 'overlay_closed=True' -and
        @($events | Where-Object event -in @('ready_fade_failed','shutdown_deadline')).Count -eq 0) @{intro=$intro;dismiss=$dismiss;fadeStart=$fadeStart;fadeEnd=$fadeEnd;shutdown=$shutdown}
} catch {
    $checks.Add([pscustomobject]@{name='unexpected-error';passed=$false;evidence=$_.Exception.ToString()})
} finally {
    if ($watcher -and -not $watcher.HasExited) {
        try { $stop=Start-Owned $watchExe '--stop'; [void]$stop.WaitForExit(5000); [void]$watcher.WaitForExit(7000) } catch { }
    }
    foreach ($p in $owned) {
        try { if (-not $p.HasExited -and $p.Path.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { $p.Kill(); [void]$p.WaitForExit(3000) } } catch { }
    }
    $configUnchanged = $rootConfigHash -eq (Get-FileHash -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Algorithm SHA256).Hash
    $checks.Add([pscustomobject]@{name='production-config-unchanged';passed=$configUnchanged;evidence=$rootConfigHash})
    $report = [ordered]@{
        passed=@($checks | Where-Object { -not $_.passed }).Count -eq 0
        completedUtc=[DateTime]::UtcNow.ToString('o');root=$runRoot
        launcherSha256=(Get-FileHash -LiteralPath $overlayExe -Algorithm SHA256).Hash
        watcherSha256=(Get-FileHash -LiteralPath $watchExe -Algorithm SHA256).Hash
        audioSha256=(Get-FileHash -LiteralPath $audioPath -Algorithm SHA256).Hash
        soundVolume=0.44;audioDurationSeconds=8.3;actualWavPlaybackRequested=$true;activationRequests=@($events | Where-Object event -eq 'activation_requested').Count
        checks=$checks.ToArray();samples=$samples.ToArray()
        verificationBoundary='Windows audio-session counters for the explicitly owned overlay only. No recording; no mixer changes; speaker audibility and subjective quality not verified.'
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    Write-Output ('Results: ' + (Join-Path $runRoot 'results.json'))
}
if (-not $report.passed) { exit 1 }
