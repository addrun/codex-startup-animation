[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $PSScriptRoot ('output\watcher-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$fixture = Join-Path $runRoot 'FakeTarget.exe'
$argsForCompiler = @('/nologo','/target:winexe','/codepage:65001','/warn:4','/warnaserror+',('/out:' + $fixture))
foreach ($assembly in @('PresentationCore','PresentationFramework','WindowsBase')) { $argsForCompiler += '/r:' + (Join-Path $framework ('WPF\' + $assembly + '.dll')) }
$argsForCompiler += '/r:' + (Join-Path $framework 'System.Xaml.dll')
$argsForCompiler += Join-Path $PSScriptRoot 'FakeTarget.cs'
& $compiler @argsForCompiler
if ($LASTEXITCODE -ne 0) { throw 'Fake target build failed.' }
foreach ($name in @('Start-Codex-Dragon.exe','Watch-Codex-Dragon.exe')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $runRoot }
Copy-Item -LiteralPath $fixture -Destination (Join-Path $runRoot 'UnrelatedCodexCli.exe')
[xml]$config = Get-Content -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Raw -Encoding utf8
# Exercise the production watcher's normal --observe path without emitting
# sound. Unlike --startup-test, a zero PCM cue keeps the audio engine enabled,
# so a stray muting flag in WatchHost.Trigger cannot pass this regression.
$silentPath = Join-Path $runRoot 'silent-watcher-fixture.wav'
$fixtureSeconds = 8.3
$sampleRate = 44100; $channels = 2; $frames = [int][Math]::Round($sampleRate * $fixtureSeconds)
$dataBytes = $frames * $channels * 2
$writer = [IO.BinaryWriter]::new([IO.File]::Create($silentPath))
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int](36 + $dataBytes))
    $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
    $writer.Write([int16]1); $writer.Write([int16]$channels); $writer.Write([int]$sampleRate)
    $writer.Write([int]($sampleRate * $channels * 2)); $writer.Write([int16]($channels * 2)); $writer.Write([int16]16)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]$dataBytes)
    $writer.Write([byte[]]::new($dataBytes))
} finally { $writer.Dispose() }
$silentBytes = [IO.File]::ReadAllBytes($silentPath)
$silenceVerified = $silentBytes.Length -eq (44 + $dataBytes)
for ($sampleByte = 44; $sampleByte -lt $silentBytes.Length; $sampleByte++) {
    if ($silentBytes[$sampleByte] -ne 0) { $silenceVerified = $false; break }
}
if (-not $silenceVerified) { throw 'Watcher playback fixture is not zero-valued PCM; refusing to run.' }
$config.SelectSingleNode('/launcher/soundEnabled').InnerText = 'true'
$config.SelectSingleNode('/launcher/soundVolume').InnerText = '0.44'
$config.SelectSingleNode('/launcher/soundFile').InnerText = $silentPath
$config.SelectSingleNode('/launcher/respectReducedMotion').InnerText = 'false'
$config.SelectSingleNode('/launcher/appUserModelId').InnerText = ''
$config.SelectSingleNode('/launcher/executablePath').InnerText = $fixture
$config.SelectSingleNode('/launcher/executableArguments').InnerText = '--this-must-never-be-used'
$config.SelectSingleNode('/launcher/videoFile').InnerText = Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4'
$config.SelectSingleNode('/launcher/characterImage').InnerText = Join-Path $projectRoot 'assets\dragon-cinematic-open.png'
$config.SelectSingleNode('/launcher/closedEyeImage').InnerText = Join-Path $projectRoot 'assets\dragon-cinematic-closed.png'
$config.SelectSingleNode('/launcher/startupTimeoutSeconds').InnerText = '3'
$config.Save((Join-Path $runRoot 'launcher.config.xml'))
$results = [Collections.Generic.List[object]]::new()
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$watchExe = Join-Path $runRoot 'Watch-Codex-Dragon.exe'
$launcher = Join-Path $runRoot 'Start-Codex-Dragon.exe'
$controlFile = Join-Path $runRoot 'control.txt'
$marker = Join-Path $runRoot 'target.tsv'
function Start-Owned([string]$File, [string]$Arguments = '') {
    $info = [Diagnostics.ProcessStartInfo]::new($File, $Arguments)
    $info.UseShellExecute = $false; $info.WorkingDirectory = $runRoot
    $p = [Diagnostics.Process]::Start($info); $owned.Add($p); return $p
}
function Read-Status {
    try { return (Get-Content -LiteralPath (Join-Path $runRoot 'output\watcher-status.json') -Raw -Encoding utf8 | ConvertFrom-Json) } catch { return $null }
}
function Wait-Condition([scriptblock]$Condition, [int]$Deadline = 7000) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 100 } while ($clock.ElapsedMilliseconds -lt $Deadline)
    return $false
}
function Check([string]$Name, [bool]$Passed, $Evidence) {
    $results.Add([pscustomobject]@{name=$Name;passed=$Passed;evidence=$Evidence})
    Write-Output ((@('FAIL','PASS')[[int]$Passed]) + ': ' + $Name)
    if (-not $Passed) { throw $Name }
}
function Stop-Watch {
    $stop = Start-Owned $watchExe '--stop'
    [void]$stop.WaitForExit(5000)
    if ($script:watcher -and -not $script:watcher.WaitForExit(7000)) { throw 'Watcher stop timed out' }
}
function Set-Control([string]$Command) {
    [IO.File]::WriteAllText($controlFile, $Command + '-' + [Guid]::NewGuid().ToString('N'))
    if (-not (Wait-Condition { (Get-Content -LiteralPath $marker -ErrorAction SilentlyContinue) -match ("command_" + $Command) })) { throw 'Fixture control not received' }
}
try {
    $atlasFolder = Join-Path $runRoot 'assets'
    New-Item -ItemType Directory -Path $atlasFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\dragon-eye-motion.png') -Destination $atlasFolder
    $config.SelectSingleNode('/launcher/startupTimeoutSeconds').InnerText = '14'
    $config.Save((Join-Path $runRoot 'launcher.config.xml'))
    $tailObserver = Start-Owned $launcher '--observe --startup-test'
    $tailExited = $tailObserver.WaitForExit(20000)
    $tailLog = Get-Content -LiteralPath (Join-Path $runRoot 'output\startup-timing.log') -Raw
    Check 'video-once-tail-no-activation' ($tailExited -and $tailObserver.ExitCode -eq 2 -and $tailLog -match 'animation_tail' -and $tailLog -notmatch 'animation_loop|activation_requested' -and -not (Test-Path -LiteralPath $marker)) $tailLog
    $config.SelectSingleNode('/launcher/startupTimeoutSeconds').InnerText = '3'
    $config.Save((Join-Path $runRoot 'launcher.config.xml'))
    $observer = Start-Owned $launcher '--observe --startup-test'
    Check 'observe-timeout-no-activation' ($observer.WaitForExit(7000) -and $observer.ExitCode -eq 2 -and -not (Test-Path -LiteralPath $marker) -and (Get-Content -LiteralPath (Join-Path $runRoot 'output\startup-timing.log') -Raw) -notmatch 'activation_requested') 'Target executable configured but never started by passive mode.'
    $config.SelectSingleNode('/launcher/startupTimeoutSeconds').InnerText = '15'
    $config.Save((Join-Path $runRoot 'launcher.config.xml'))
    $script:watcher = Start-Owned $watchExe
    Check 'watcher-start-empty-baseline' (Wait-Condition { $s=Read-Status; $s -and $s.running -and $s.cyclesStarted -eq 0 }) (Read-Status)
    $unrelated = Start-Owned (Join-Path $runRoot 'UnrelatedCodexCli.exe') '--delay-ms 1 --lifetime-ms 1600'
    [void]$unrelated.WaitForExit(4000)
    Check 'unrelated-executable-excluded' ((Read-Status).cyclesStarted -eq 0) (Read-Status)
    $target = Start-Owned $fixture ('--delay-ms 1300 --lifetime-ms 60000 --control "' + $controlFile + '" --marker "' + $marker + '"')
    Check 'new-target-one-overlay' (Wait-Condition { (Read-Status).cyclesStarted -eq 1 }) (Read-Status)
    Check 'observe-completes-with-no-activation' (Wait-Condition { $s=Read-Status; $s.cyclesStarted -eq 1 -and $s.mainWindowCount -eq 1 -and $null -eq $s.activeOverlayPid } 15000) (Read-Status)
    $timing = Get-Content -LiteralPath (Join-Path $runRoot 'output\startup-timing.log') -Raw
    Check 'passive-ready-and-single-process' ($timing -match 'window_ready' -and $timing -notmatch 'activation_requested' -and @(Get-Content -LiteralPath $marker | Where-Object { $_ -match 'process_started$' }).Count -eq 1) $timing
    $soundEvents = @(Import-Csv -LiteralPath (Join-Path $runRoot 'output\startup-timing.log') -Delimiter "`t")
    $watchEvents = @(Import-Csv -LiteralPath (Join-Path $runRoot 'output\watcher-events.tsv') -Delimiter "`t" -Header utc,event,detail)
    $soundStarted = @($soundEvents | Where-Object event -eq 'sound_started')
    $soundOpened = @($soundEvents | Where-Object { $_.event -eq 'sound_opened' -and $_.detail -match '^duration=8\.300s;' })
    $soundSuppressed = @($soundEvents | Where-Object { $_.event -in @('sound_disabled','sound_failed') })
    $overlayStarts = @($watchEvents | Where-Object event -eq 'overlay_started')
    Check 'watcher-auto-sound-config-respected' ($soundStarted.Count -eq 1 -and $soundStarted[0].detail -match '; volume=0\.440$' -and $soundOpened.Count -eq 1 -and $soundSuppressed.Count -eq 0 -and $overlayStarts.Count -eq 1 -and $overlayStarts[0].detail -match '; arguments=--observe$') ([pscustomobject]@{soundEvents=@($soundEvents | Where-Object { $_.event -like 'sound_*' });overlayStarts=$overlayStarts;silentFixtureDurationSeconds=$fixtureSeconds;silentFixtureSamplesVerifiedZero=$silenceVerified})
    Check 'watcher-complete-intro-and-ready-fade' ($timing -match 'page_ready_deferred' -and $timing -match 'intro_completed' -and $timing -match 'ready_fade_completed' -and $timing -notmatch 'ready_fade_failed|shutdown_deadline|sound_fade_completed') $timing
    Set-Control 'minimize'
    Start-Sleep -Milliseconds 1200
    Set-Control 'restore'
    Start-Sleep -Milliseconds 1200
    Check 'minimized-restore-no-replay' ((Read-Status).cyclesStarted -eq 1 -and (Read-Status).mainWindowCount -eq 1) (Read-Status)
    Stop-Watch
    $script:watcher = Start-Owned $watchExe
    Check 'watcher-restart-existing-window-no-replay' (Wait-Condition { $s=Read-Status; $s.running -and $s.mainWindowCount -eq 1 -and $s.cyclesStarted -eq 0 }) (Read-Status)
    Set-Control 'hide'
    Check 'all-windows-closed-rearms' (Wait-Condition { (Read-Status).mainWindowCount -eq 0 }) (Read-Status)
    [IO.File]::WriteAllText($controlFile, 'child')
    if (-not (Wait-Condition { (Get-Content -LiteralPath $marker -ErrorAction SilentlyContinue) -match 'command_child' })) { throw 'Fixture child command not received' }
    Start-Sleep -Milliseconds 2200
    Check 'electron-child-no-overlay' ((Read-Status).cyclesStarted -eq 0) (Read-Status)
    Set-Control 'show'
    Check 'surviving-process-reopen-once' (Wait-Condition { (Read-Status).cyclesStarted -eq 1 }) (Read-Status)
    Stop-Watch
    Check 'stop-does-not-stop-target' (-not $target.HasExited) (Read-Status)
    Set-Control 'exit'
    [void]$target.WaitForExit(3000)
} catch { $results.Add([pscustomobject]@{name='unexpected-error';passed=$false;evidence=$_.Exception.ToString()}) }
finally {
    foreach ($p in $owned) {
        try { if (-not $p.HasExited -and $p.Path.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { $p.Kill(); [void]$p.WaitForExit(3000) } } catch { }
    }
    $report = [ordered]@{passed=@($results | Where-Object { -not $_.passed }).Count -eq 0; launcherSha256=(Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash; watcherSha256=(Get-FileHash -LiteralPath $watchExe -Algorithm SHA256).Hash; tests=$results.ToArray(); root=$runRoot; target='Only isolated fake executables. No real Codex activation.'; silentFixture=$silentPath; silentFixtureDurationSeconds=$fixtureSeconds; silentFixtureSamplesVerifiedZero=$silenceVerified; soundVolume=0.44; originalAudioPlaybackRequested=$false}
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    Write-Output ('Results: ' + (Join-Path $runRoot 'results.json'))
}
if (-not $report.passed) { exit 1 }
