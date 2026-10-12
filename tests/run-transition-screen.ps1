[CmdletBinding()]
param(
    [string]$LauncherPath = '',
    [switch]$LegacyBaseline,
    [string]$CompareBaseline = '',
    [ValidateRange(30,60)] [int]$SampleFps = 60,
    [ValidateRange(200,800)] [int]$ImageWidth = 384,
    [string]$PythonPath = ''
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 6) {
    $forward = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,
        '-SampleFps',$SampleFps,'-ImageWidth',$ImageWidth)
    if ($LauncherPath) { $forward += @('-LauncherPath',$LauncherPath) }
    if ($LegacyBaseline) { $forward += '-LegacyBaseline' }
    if ($CompareBaseline) { $forward += @('-CompareBaseline',$CompareBaseline) }
    if ($PythonPath) { $forward += @('-PythonPath',$PythonPath) }
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @forward
    exit $LASTEXITCODE
}
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $LauncherPath) { $LauncherPath = Join-Path $projectRoot 'Start-Codex-Dragon.exe' }
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) { throw 'Launcher does not exist.' }
$PythonPath = & (Join-Path $projectRoot 'tools/Resolve-Python.ps1') -PythonPath $PythonPath -RequiredModules @('PIL','numpy')

$runRoot = Join-Path $PSScriptRoot ('output\transition-screen-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path (Join-Path $runRoot 'assets') -Force | Out-Null
$copy = Join-Path $runRoot 'Start-Codex-Dragon.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $copy
foreach ($asset in @('dragon-eye-motion.png','dragon-eye-motion-detail.png')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot ('assets\' + $asset)) -Destination (Join-Path $runRoot 'assets')
}
[xml]$config = Get-Content -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Raw
$config.SelectSingleNode('/launcher/appUserModelId').InnerText = ''
$config.SelectSingleNode('/launcher/executablePath').InnerText = (Join-Path $runRoot 'NeverActivate.exe')
$config.SelectSingleNode('/launcher/executableArguments').InnerText = '--must-never-launch'
$config.SelectSingleNode('/launcher/characterImage').InnerText = (Join-Path $projectRoot 'assets\dragon-cinematic-open.png')
$config.SelectSingleNode('/launcher/closedEyeImage').InnerText = (Join-Path $projectRoot 'assets\dragon-cinematic-closed.png')
$config.SelectSingleNode('/launcher/videoFile').InnerText = (Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4')
$config.SelectSingleNode('/launcher/respectReducedMotion').InnerText = 'false'
$config.SelectSingleNode('/launcher/startupTimeoutSeconds').InnerText = '18'
# --startup-test is always present and suppresses sound in the new launcher.
# Explicitly set either supported audio flag false as a second guard.
foreach ($name in @('enableSound','soundEnabled','audioEnabled')) {
    $node = $config.SelectSingleNode('/launcher/' + $name)
    if ($node) { $node.InnerText = 'false' }
}
$config.Save((Join-Path $runRoot 'launcher.config.xml'))
if (-not ('TransitionScreenCapture' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'TransitionScreenCapture.cs') -ReferencedAssemblies @('System.Drawing','System.Windows.Forms')
}
[TransitionScreenCapture]::EnableDpi()
$logPath = Join-Path $runRoot 'output\startup-timing.log'
function Read-Events {
    param([string]$Path)
    $events = [Collections.Generic.List[object]]::new()
    if (-not (Test-Path -LiteralPath $Path)) { return $events.ToArray() }
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        $fields = $line.Split([char]9, 3)
        $seconds = 0.0
        if ($fields.Length -lt 3 -or -not [double]::TryParse($fields[0],
            [Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$seconds)) { continue }
        $events.Add([pscustomobject]@{seconds=$seconds; event=$fields[1]; detail=$fields[2]})
    }
    return $events.ToArray()
}

$process = Start-Process -FilePath $copy -ArgumentList '--observe --startup-test' -WorkingDirectory $runRoot -WindowStyle Hidden -PassThru
$burst = $null
$failure = ''
$startupUtc = $null
$startupOffset = 0.0
$clockSource = ''
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(6)
    do {
        $events = @(Read-Events $logPath)
        $started = $events | Where-Object event -eq 'startup_started' | Select-Object -First 1
        $opened = $events | Where-Object { $_.event -in @('media_opened','film_opened','animation_media_opened') } | Select-Object -First 1
        $requested = $events | Where-Object event -eq 'animation_started' | Select-Object -First 1
        if ($started -and $requested -and [TransitionScreenCapture]::OwnedWindowVisible($process.Id)) {
            if ($opened -or [DateTime]::UtcNow -ge $deadline.AddSeconds(-3)) { break }
        }
        $process.Refresh(); if ($process.HasExited) { throw 'Isolated overlay exited before video sampling.' }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $started -or -not $requested -or $started.detail -notmatch 'utc=([^;\s]+)') {
        throw 'Unable to establish the isolated overlay timing epoch.'
    }
    $startupUtc = [DateTime]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    $startupOffset = [double]$started.seconds
    $filmEpoch = if ($opened) { $opened } else { $requested }
    $clockSource = $filmEpoch.event
    # The old EXE has no decoder-opened event. Starting from playback request
    # leaves enough margin for decoder startup and its former 0.7 s tail setup.
    $samplingStart = $startupUtc.AddSeconds([double]$filmEpoch.seconds - $startupOffset + 6.85)
    while ([DateTime]::UtcNow -lt $samplingStart) {
        $process.Refresh(); if ($process.HasExited) { throw 'Overlay exited before the transition interval.' }
        Start-Sleep -Milliseconds 5
    }
    $burst = [TransitionScreenCapture]::Capture($process.Id, $SampleFps, 3.2, $ImageWidth)
    if ($burst.Failure) { $failure = $burst.Failure }
    [TransitionScreenCapture]::SaveAndRelease($burst, (Join-Path $runRoot 'frames'))
} catch {
    $failure = $_.Exception.Message
} finally {
    $process.Refresh()
    if (-not $process.HasExited -and $process.Path -ieq $copy) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(1500)) { $process.Kill(); [void]$process.WaitForExit(3000) }
    }
}
$events = @(Read-Events $logPath)
$frames = @()
if ($burst) {
    $frames = @($burst.Frames | ForEach-Object {
        $utc = [DateTime]::Parse($_.Utc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        [pscustomobject]@{
            index=$_.Index; seconds=$_.Seconds; utc=$_.Utc;
            startupElapsedSeconds=($utc - $startupUtc).TotalSeconds + $startupOffset;
            captureMilliseconds=$_.CaptureMilliseconds; path=$_.Path;
            windowHandle=$_.WindowHandle; ownershipVerified=$_.OwnershipVerified
        }
    })
}
$report = [ordered]@{
    completedUtc=[DateTime]::UtcNow.ToString('o'); root=$runRoot;
    launcherPath=$LauncherPath; launcherSha256=(Get-FileHash -LiteralPath $LauncherPath -Algorithm SHA256).Hash;
    videoSha256=(Get-FileHash -LiteralPath (Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4') -Algorithm SHA256).Hash;
    processId=$process.Id; passiveArguments='--observe --startup-test'; legacyBaseline=[bool]$LegacyBaseline;
    failure=$failure; startupUtc=if ($startupUtc) { $startupUtc.ToString('o') } else { '' };
    clockSource=$clockSource; requestedFps=$SampleFps;
    captureMethod='DwmFlush and owned screen ROI, Z order plus 25 ownership points before/after each capture, in-memory burst then PNG writes';
    region=if ($burst) { [ordered]@{
        client=@($burst.ClientScreenX,$burst.ClientScreenY,$burst.ClientWidth,$burst.ClientHeight);
        screenRoi=@($burst.RoiScreenX,$burst.RoiScreenY,$burst.RoiWidth,$burst.RoiHeight);
        normalizedRoi=@(0.52,0.22,0.36,0.58); imageSize=@($burst.ImageWidth,$burst.ImageHeight)
    }} else { $null };
    events=$events; frames=$frames;
    note='Only this test animation ROI was sampled. No Codex activation or chat-window capture. Sampling is evidence at the measured cadence, not proof between samples.'
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runRoot 'capture.json') -Encoding UTF8
$analysisArgs = @((Join-Path $PSScriptRoot 'analyze_transition.py'),$runRoot)
if ($CompareBaseline) { $analysisArgs += @('--baseline',[IO.Path]::GetFullPath($CompareBaseline)) }
& $PythonPath @analysisArgs
$analysisExit = $LASTEXITCODE
Set-Content -LiteralPath (Join-Path $projectRoot 'output\transition-screen-latest.txt') -Value $runRoot -Encoding UTF8
Write-Output $runRoot
if ($failure -or $analysisExit -ne 0) { exit 1 }
