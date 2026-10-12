# Full FilmSplash / StartupCoordinator checks using isolated passive instances
# and an entirely silent PCM file. Never activates, closes, or reconfigures Codex.
[CmdletBinding()]
param(
    [string]$LauncherPath = '',
    [ValidateSet('close','ready','timeout','tail','escape','tail-close','preview')]
    [string[]]$Cases = @('close','ready','timeout','tail','escape','tail-close','preview')
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $LauncherPath) { $LauncherPath = Join-Path $projectRoot 'Start-Codex-Dragon.exe' }
$LauncherPath = (Get-Item -LiteralPath $LauncherPath).FullName
$runRoot = Join-Path $PSScriptRoot ('output\sound-overlay-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$frozenLauncher = Join-Path $runRoot 'Start-Codex-Dragon.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $frozenLauncher
$launcherHash = (Get-FileHash -LiteralPath $frozenLauncher -Algorithm SHA256).Hash
$rootConfig = Join-Path $projectRoot 'launcher.config.xml'
$rootConfigHash = (Get-FileHash -LiteralPath $rootConfig -Algorithm SHA256).Hash
$silentPath = Join-Path $runRoot 'silent-fixture.wav'
$utf8 = [Text.UTF8Encoding]::new($false)
# Match the welcome mix's 8.3-second decoder lifetime. Its final 0.55 seconds
# must survive the switch from the video clock to the dynamic waiting tail.
$fixtureSeconds = 8.3
$rate = 44100; $channels = 2; $frames = [int][Math]::Round($rate * $fixtureSeconds); $dataBytes = $frames * $channels * 2
$silentStream = [IO.File]::Create($silentPath)
$silentWriter = [IO.BinaryWriter]::new($silentStream)
try {
    $silentWriter.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $silentWriter.Write([int](36 + $dataBytes))
    $silentWriter.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $silentWriter.Write([int]16)
    $silentWriter.Write([int16]1); $silentWriter.Write([int16]$channels); $silentWriter.Write([int]$rate)
    $silentWriter.Write([int]($rate * $channels * 2)); $silentWriter.Write([int16]($channels * 2)); $silentWriter.Write([int16]16)
    $silentWriter.Write([Text.Encoding]::ASCII.GetBytes('data')); $silentWriter.Write([int]$dataBytes)
    $silentWriter.Write([byte[]]::new($dataBytes))
} finally { $silentWriter.Dispose() }
$silentBytes = [IO.File]::ReadAllBytes($silentPath)
$silenceVerified = $silentBytes.Length -eq (44 + $dataBytes)
for ($sampleByte = 44; $sampleByte -lt $silentBytes.Length; $sampleByte++) {
    if ($silentBytes[$sampleByte] -ne 0) { $silenceVerified = $false; break }
}
if (-not $silenceVerified) { throw 'Playback WAV is not exactly zero-valued PCM; refusing to run.' }

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$compiler = Join-Path $framework 'csc.exe'
$targetBuild = Join-Path $runRoot 'PageTarget.exe'
$compileArguments = @('/nologo', '/target:winexe', '/main:PageTarget', '/codepage:65001', '/warn:4', '/warnaserror+', ('/out:' + $targetBuild))
foreach ($name in @('PresentationCore', 'PresentationFramework', 'WindowsBase')) {
    $compileArguments += '/r:' + (Join-Path $framework ('WPF\' + $name + '.dll'))
}
$compileArguments += '/r:' + (Join-Path $framework 'System.Xaml.dll')
$compileArguments += Join-Path $PSScriptRoot 'FakeTarget.cs'
$compileArguments += Join-Path $PSScriptRoot 'PageTarget.cs'
& $compiler @compileArguments
if ($LASTEXITCODE -ne 0) { throw 'Page readiness fixture compilation failed.' }
if (-not ('SoundOverlayNative' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'SoundOverlayNative.cs') }
if (-not ('SoundOverlayEscapeNative' -as [type])) {
    # Address key messages to this fixture's verified HWND; never inject a
    # global keystroke or change the user's foreground application.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SoundOverlayEscapeNative
{
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    public static bool Escape(long handle, int expectedProcessId)
    {
        var window = new IntPtr(handle);
        uint owner; GetWindowThreadProcessId(window, out owner);
        if (owner != (uint)expectedProcessId) return false;
        bool down = PostMessage(window, 0x100, new IntPtr(0x1b), new IntPtr(0x00010001));
        bool up = PostMessage(window, 0x101, new IntPtr(0x1b), new IntPtr(unchecked((int)0xc0010001)));
        return down && up;
    }
}
'@
}
$results = [Collections.Generic.List[object]]::new()
$owned = [Collections.Generic.List[object]]::new()

function New-SoundScenario {
    param([string]$Name, [double]$Timeout = 12, [switch]$PageTarget)
    $folder = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Path $folder | Out-Null
    $launcher = Join-Path $folder 'Start-Codex-Dragon.exe'
    Copy-Item -LiteralPath $frozenLauncher -Destination $launcher
    $target = Join-Path $folder 'NeverActivate.exe'
    if ($PageTarget) {
        $target = Join-Path $folder 'PageTarget.exe'
        Copy-Item -LiteralPath $targetBuild -Destination $target
    }
    $assets = Join-Path $folder 'assets'
    New-Item -ItemType Directory -Path $assets | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\dragon-eye-motion.png') -Destination $assets
    $xml = [xml]'<launcher />'
    $values = [ordered]@{
        durationSeconds = '8.0'; frameRate = '60'
        videoFile = (Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4')
        appUserModelId = ''; executablePath = $target; executableArguments = '--unexpected-activation'
        characterImage = (Join-Path $projectRoot 'assets\dragon-cinematic-open.png')
        closedEyeImage = (Join-Path $projectRoot 'assets\dragon-cinematic-closed.png')
        respectReducedMotion = 'false'; preferDetailTextures = 'false'
        soundEnabled = 'true'; soundVolume = '0.44'; soundFile = $silentPath
        startupTimeoutSeconds = $Timeout.ToString([Globalization.CultureInfo]::InvariantCulture)
        windowReadyDelaySeconds = '0.5'; requirePageContent = 'true'
        tailHoldSeconds = '7.75'; blinkIntervalSeconds = '4.0'; blinkDurationSeconds = '0.28'
    }
    foreach ($entry in $values.GetEnumerator()) {
        $node = $xml.CreateElement($entry.Key); $node.InnerText = $entry.Value
        [void]$xml.DocumentElement.AppendChild($node)
    }
    $xml.Save((Join-Path $folder 'launcher.config.xml'))
    return [pscustomobject]@{ Name=$Name; Folder=$folder; Launcher=$launcher; Target=$target; Timeout=$Timeout; Events=(Join-Path $folder 'output\startup-timing.log'); Marker=(Join-Path $folder 'target-events.tsv') }
}

function Start-OwnedSoundProcess {
    param([string]$Path, [string]$Arguments, [string]$Folder)
    $info = [Diagnostics.ProcessStartInfo]::new($Path, $Arguments)
    $info.UseShellExecute = $false; $info.WorkingDirectory = $Folder
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add([pscustomobject]@{ Process=$process; Path=[IO.Path]::GetFullPath($Path); Birth=$process.StartTime.ToUniversalTime() })
    return $process
}

function Assert-OwnedSoundProcess {
    param([Diagnostics.Process]$Process)
    $record = @($owned | Where-Object { $_.Process -eq $Process }) | Select-Object -First 1
    if (-not $record -or $Process.HasExited -or
        [IO.Path]::GetFullPath($Process.Path) -ne $record.Path -or
        $Process.StartTime.ToUniversalTime() -ne $record.Birth -or
        -not $record.Path.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to send close to a process outside the owned fixture.'
    }
}

function Stop-OwnedSoundProcess {
    param([Diagnostics.Process]$Process)
    if (-not $Process -or $Process.HasExited) { return }
    Assert-OwnedSoundProcess $Process
    [void]$Process.CloseMainWindow()
    if (-not $Process.WaitForExit(1300)) { $Process.Kill(); [void]$Process.WaitForExit(2000) }
}

function Wait-SoundCondition {
    param([scriptblock]$Condition, [int]$Milliseconds=5000)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 15
    } while ($clock.ElapsedMilliseconds -lt $Milliseconds)
    return $false
}

function Read-SoundEvents {
    param($Scenario)
    if (-not (Test-Path -LiteralPath $Scenario.Events)) { return @() }
    try { return @(Import-Csv -LiteralPath $Scenario.Events -Delimiter "`t") } catch { return @() }
}

function Require-SoundStarted {
    param($Scenario, [Diagnostics.Process]$Process)
    if (-not (Wait-SoundCondition { $Process.HasExited -or @(Read-SoundEvents $Scenario | Where-Object event -eq 'sound_started').Count -eq 1 } 5000) -or $Process.HasExited) {
        throw 'Isolated overlay never started its silent cue.'
    }
}

function Send-OwnedClose {
    param([Diagnostics.Process]$Process, [switch]$Twice)
    Assert-OwnedSoundProcess $Process
    $handle = [SoundOverlayNative]::Find($Process.Id)
    if ($handle -eq 0) { throw 'No visible owned overlay window is available for WM_CLOSE.' }
    $first = [SoundOverlayNative]::Close($handle, $Process.Id)
    $second = $false
    if ($Twice) { Start-Sleep -Milliseconds 8; $second = [SoundOverlayNative]::Close($handle, $Process.Id) }
    if (-not $first -or ($Twice -and -not $second)) { throw 'Owned WM_CLOSE request was not accepted.' }
    return [pscustomobject]@{ hwnd=$handle; firstAccepted=$first; secondAccepted=$second; gapMilliseconds=8 }
}

function Send-OwnedEscape {
    param([Diagnostics.Process]$Process)
    Assert-OwnedSoundProcess $Process
    $handle = [SoundOverlayNative]::Find($Process.Id)
    if ($handle -eq 0) { throw 'No visible owned overlay window is available for Esc.' }
    if (-not [SoundOverlayEscapeNative]::Escape($handle, $Process.Id)) { throw 'Owned Esc request was not accepted.' }
    return [pscustomobject]@{ hwnd=$handle; escapeAccepted=$true; processId=$Process.Id }
}

function Finish-SoundScenario {
    param($Scenario, [Diagnostics.Process]$Process, [string]$Completion, [int]$ExitCode, [bool]$RequireFade=$true, $Extra=$null, [double]$EndLimit=6)
    if (-not $Process.WaitForExit(4000)) { throw 'Owned overlay did not exit within the close deadline.' }
    $events = @(Read-SoundEvents $Scenario)
    $shutdown = @($events | Where-Object event -eq 'shutdown')
    $fade = @($events | Where-Object event -eq 'sound_fade_completed')
    $completed = @($events | Where-Object event -eq 'completed')
    $starts = @($events | Where-Object event -eq 'sound_started')
    $failures = @($events | Where-Object { $_.event -in @('sound_failed','media_failed','shutdown_deadline','single_instance_busy') })
    $activation = @($events | Where-Object { $_.event -in @('activation_requested','activation_accepted') })
    $pass = $Process.ExitCode -eq $ExitCode -and $starts.Count -eq 1 -and $shutdown.Count -eq 1 -and
        $completed.Count -eq 1 -and $completed[0].detail -eq ($Completion + '; exit=' + $ExitCode) -and
        $failures.Count -eq 0 -and $activation.Count -eq 0 -and
        $shutdown[0].detail -eq ('exit=' + $ExitCode + '; overlay_closed=True') -and
        [double]::Parse($shutdown[0].seconds, [Globalization.CultureInfo]::InvariantCulture) -lt $EndLimit
    $fadeOrder = $false
    $fadeMs = $null
    $tailRelease = $null
    $tailReleaseAfterAnimation = $null
    if ($RequireFade) {
        $fadeOrder = $fade.Count -eq 1 -and [Array]::IndexOf($events,$fade[0]) -lt [Array]::IndexOf($events,$shutdown[0])
        $pass = $pass -and $fadeOrder
        $waiting = @($events | Where-Object event -eq 'shutdown_waiting')
        if ($fade.Count -eq 1 -and $waiting.Count -eq 1) {
            $fadeMs = 1000 * ([double]::Parse($fade[0].seconds,[Globalization.CultureInfo]::InvariantCulture) - [double]::Parse($waiting[0].seconds,[Globalization.CultureInfo]::InvariantCulture))
            $pass = $pass -and $fadeMs -ge 0 -and $fadeMs -lt 250
        }
    }
    if ($Scenario.Name -eq 'waiting-tail-natural-sound-end') {
        $tail = @($events | Where-Object event -eq 'animation_tail')
        $handoff = @($events | Where-Object event -eq 'tail_handoff_started')
        $clockReleased = @($events | Where-Object event -eq 'sound_video_released')
        $opened = @($events | Where-Object event -eq 'sound_opened')
        $releases = @($events | Where-Object { $_.event -eq 'sound_finished' -and $_.detail -match 'decoder released' })
        $resumes = @($events | Where-Object event -eq 'sound_resumed')
        $tailReleaseAfterAnimation = $tail.Count -eq 1 -and $handoff.Count -eq 1 -and $releases.Count -eq 1 -and
            [Array]::IndexOf($events,$releases[0]) -gt [Array]::IndexOf($events,$tail[0]) -and
            [Array]::IndexOf($events,$releases[0]) -gt [Array]::IndexOf($events,$handoff[0])
        if ($releases.Count -eq 1) { $tailRelease = $releases[0] }
        $pass = $pass -and $tailReleaseAfterAnimation -and $clockReleased.Count -eq 1 -and
            [Array]::IndexOf($events,$clockReleased[0]) -lt [Array]::IndexOf($events,$releases[0]) -and $opened.Count -eq 1 -and
            $opened[0].detail -match '^duration=8\.300s;' -and $resumes.Count -eq 0 -and $fade.Count -eq 0 -and
            $tail[0].detail -match 'blink, hair and title lights=enabled'
    }
    if ($Scenario.Name -eq 'waiting-tail-close-with-audio') {
        $handoff = @($events | Where-Object event -eq 'tail_handoff_started')
        $clockReleased = @($events | Where-Object event -eq 'sound_video_released')
        $finished = @($events | Where-Object event -eq 'sound_finished')
        $pass = $pass -and $handoff.Count -eq 1 -and $clockReleased.Count -eq 1 -and $finished.Count -eq 0 -and $fade.Count -eq 1 -and
            [Array]::IndexOf($events,$fade[0]) -gt [Array]::IndexOf($events,$handoff[0])
    }
    if ($Scenario.Name -eq 'escape-close-with-audio') {
        $pass = $pass -and @($events | Where-Object { $_.event -eq 'overlay_dismissed' -and $_.detail -eq 'skip' }).Count -eq 1
    }
    if ($Scenario.Name -eq 'page-ready-close-with-audio') {
        $intro = @($events | Where-Object event -eq 'intro_completed')
        $natural = @($events | Where-Object event -eq 'sound_finished')
        $dismiss = @($events | Where-Object { $_.event -eq 'overlay_dismissed' -and $_.detail -eq 'window_ready' })
        $visualStart = @($events | Where-Object event -eq 'ready_fade_started')
        $visualEnd = @($events | Where-Object event -eq 'ready_fade_completed')
        $visualMs = if($visualStart.Count -eq 1 -and $visualEnd.Count -eq 1){1000*([double]$visualEnd[0].seconds-[double]$visualStart[0].seconds)}else{0}
        $pass = $pass -and $intro.Count -eq 1 -and $natural.Count -eq 1 -and $dismiss.Count -eq 1 -and
            @($events | Where-Object event -eq 'page_ready_deferred').Count -eq 1 -and
            [Array]::IndexOf($events,$natural[0]) -lt [Array]::IndexOf($events,$dismiss[0]) -and
            [Array]::IndexOf($events,$intro[0]) -lt [Array]::IndexOf($events,$dismiss[0]) -and
            $visualStart.Count -eq 1 -and $visualEnd.Count -eq 1 -and $visualMs -ge 440 -and $visualMs -lt 1000 -and
            [Array]::IndexOf($events,$visualEnd[0]) -lt [Array]::IndexOf($events,$shutdown[0]) -and $fade.Count -eq 0 -and
            @($events | Where-Object event -in @('ready_fade_failed','ready_fade_unavailable')).Count -eq 0
    }
    $result = [pscustomobject]@{ name=$Scenario.Name; passed=$pass; exitCode=$Process.ExitCode; expectedCompletion=$Completion;
        fadeBeforeShutdown=$fadeOrder; shutdownWaitToFadeMs=$fadeMs; activationRequests=$activation.Count; soundStartCount=$starts.Count;
        tailSoundRelease=$tailRelease; tailSoundReleasedAfterAnimation=$tailReleaseAfterAnimation;
        deadlineFailures=$failures; extra=$Extra; events=$events; log=$Scenario.Events }
    $results.Add($result)
    Write-Output ((@('FAIL','PASS')[[int]$pass]) + ': ' + $Scenario.Name)
}

try {
    if ($Cases -contains 'close') {
    $scenario = New-SoundScenario 'reentrant-window-close'
    $overlay = Start-OwnedSoundProcess $scenario.Launcher '--observe' $scenario.Folder
    Require-SoundStarted $scenario $overlay
    $closeEvidence = Send-OwnedClose $overlay -Twice
    Finish-SoundScenario $scenario $overlay 'dismissed' 0 $true $closeEvidence
    }

    if ($Cases -contains 'ready') {
    $scenario = New-SoundScenario 'page-ready-close-with-audio' -PageTarget
    $overlay = Start-OwnedSoundProcess $scenario.Launcher '--observe' $scenario.Folder
    Require-SoundStarted $scenario $overlay
    $targetArguments = '--window-delay-ms 20 --ready-delay-ms 250 --lifetime-ms 20000 --marker "' + $scenario.Marker + '"'
    $target = Start-OwnedSoundProcess $scenario.Target $targetArguments $scenario.Folder
    if (-not (Wait-SoundCondition { $overlay.HasExited } 12500)) { throw 'Complete intro and ready fade did not finish the passive overlay.' }
    $pageEvents = @(Read-SoundEvents $scenario | Where-Object { $_.event -in @('window_observed','page_state','overlay_dismissed') })
    Finish-SoundScenario $scenario $overlay 'window_ready' 0 $false $pageEvents 14
    Stop-OwnedSoundProcess $target
    }

    if ($Cases -contains 'timeout') {
    $scenario = New-SoundScenario 'timeout-close-with-audio' -Timeout 2
    $overlay = Start-OwnedSoundProcess $scenario.Launcher '--observe' $scenario.Folder
    Require-SoundStarted $scenario $overlay
    if (-not (Wait-SoundCondition { $overlay.HasExited } 5000)) { throw 'Timeout did not finish the passive overlay.' }
    Finish-SoundScenario $scenario $overlay 'window_timeout' 2 $true $null 3
    }

    if ($Cases -contains 'tail') {
    $scenario = New-SoundScenario 'waiting-tail-natural-sound-end' -Timeout 16
    $overlay = Start-OwnedSoundProcess $scenario.Launcher '--observe' $scenario.Folder
    Require-SoundStarted $scenario $overlay
    if (-not (Wait-SoundCondition { $overlay.HasExited -or @(Read-SoundEvents $scenario | Where-Object event -eq 'animation_tail').Count -eq 1 } 11500) -or $overlay.HasExited) {
        throw 'Passive overlay did not enter its waiting tail.'
    }
    if (-not (Wait-SoundCondition { $overlay.HasExited -or @(Read-SoundEvents $scenario | Where-Object event -eq 'sound_finished').Count -eq 1 } 2500) -or $overlay.HasExited) {
        throw 'The waiting tail did not let the cue finish naturally and release its decoder.'
    }
    # Continue waiting past the complete cue, proving that subsequent tail
    # activity never starts or resumes it again.
    Start-Sleep -Milliseconds 450
    $closeEvidence = Send-OwnedClose $overlay
    Finish-SoundScenario $scenario $overlay 'dismissed' 0 $false $closeEvidence 14
    }

    if ($Cases -contains 'escape') {
    $scenario = New-SoundScenario 'escape-close-with-audio'
    $overlay = Start-OwnedSoundProcess $scenario.Launcher '--observe' $scenario.Folder
    Require-SoundStarted $scenario $overlay
    $escapeEvidence = Send-OwnedEscape $overlay
    Finish-SoundScenario $scenario $overlay 'dismissed' 0 $true $escapeEvidence
    }

    if ($Cases -contains 'tail-close') {
    $scenario = New-SoundScenario 'waiting-tail-close-with-audio' -Timeout 16
    $overlay = Start-OwnedSoundProcess $scenario.Launcher '--observe' $scenario.Folder
    Require-SoundStarted $scenario $overlay
    if (-not (Wait-SoundCondition { $overlay.HasExited -or @(Read-SoundEvents $scenario | Where-Object event -eq 'tail_handoff_started').Count -eq 1 } 11500) -or $overlay.HasExited) {
        throw 'Passive overlay did not reach the waiting-tail handoff.'
    }
    if (@(Read-SoundEvents $scenario | Where-Object event -eq 'sound_finished').Count -ne 0) {
        throw 'The cue ended before the close-in-tail test could exercise its remaining audio.'
    }
    $closeEvidence = Send-OwnedClose $overlay
    Finish-SoundScenario $scenario $overlay 'dismissed' 0 $true $closeEvidence 13
    }

    if ($Cases -contains 'preview') {
    $scenario = New-SoundScenario 'preview-complete-cue-after-video'
    $overlay = Start-OwnedSoundProcess $scenario.Launcher '--preview' $scenario.Folder
    if (-not $overlay.WaitForExit(15000)) { throw 'Silent preview did not finish its video and complete cue.' }
    $previewLog = Join-Path $scenario.Folder 'output\film-transition.log'
    $events = @(Import-Csv -LiteralPath $previewLog -Delimiter "`t" -Header utc,event,detail)
    $starts = @($events | Where-Object event -eq 'sound_started')
    $opens = @($events | Where-Object event -eq 'sound_opened')
    $released = @($events | Where-Object event -eq 'sound_video_released')
    $finished = @($events | Where-Object event -eq 'sound_finished')
    $forbidden = @($events | Where-Object { $_.event -in @('sound_failed','media_failed','sound_resumed','sound_fade_completed','tail_handoff_started','activation_requested','activation_accepted') })
    $finishAfterVideoSeconds = $null
    if ($released.Count -eq 1 -and $finished.Count -eq 1) {
        $finishAfterVideoSeconds = ([DateTime]::Parse($finished[0].utc).ToUniversalTime() - [DateTime]::Parse($released[0].utc).ToUniversalTime()).TotalSeconds
    }
    $pass = $overlay.ExitCode -eq 0 -and $starts.Count -eq 1 -and $opens.Count -eq 1 -and
        $opens[0].detail -match '^duration=8\.300s;' -and $starts[0].detail -match '; volume=0\.440$' -and
        $released.Count -eq 1 -and $finished.Count -eq 1 -and $finished[0].detail -match 'decoder released' -and
        $finishAfterVideoSeconds -ge 0.10 -and $finishAfterVideoSeconds -lt 1.5 -and $forbidden.Count -eq 0 -and
        (Test-Path -LiteralPath (Join-Path $scenario.Folder 'output\native-preview-validation.txt')) -and
        -not (Test-Path -LiteralPath $scenario.Marker)
    $results.Add([pscustomobject]@{name=$scenario.Name;passed=$pass;exitCode=$overlay.ExitCode;activationRequests=0;
        soundStartCount=$starts.Count;finishAfterVideoSeconds=$finishAfterVideoSeconds;events=$events;log=$previewLog})
    Write-Output ((@('FAIL','PASS')[[int]$pass]) + ': ' + $scenario.Name)
    }
} catch {
    $results.Add([pscustomobject]@{ name='suite-error'; passed=$false; error=$_.Exception.ToString() })
    Write-Output ('FAIL: suite-error; ' + $_.Exception.Message)
} finally {
    foreach ($record in $owned) {
        try { Stop-OwnedSoundProcess $record.Process } catch { Write-Output ('Owned fixture cleanup: ' + $_.Exception.Message) }
    }
}
$rootConfigUnchanged = $rootConfigHash -eq (Get-FileHash -LiteralPath $rootConfig -Algorithm SHA256).Hash
$passedCount = @($results | Where-Object passed -eq $true).Count
$expectedCount = @($Cases | Select-Object -Unique).Count
$passed = $results.Count -eq $expectedCount -and $passedCount -eq $expectedCount -and $rootConfigUnchanged
$report = [ordered]@{ passed=$passed; passedCount=$passedCount; checkCount=$results.Count; completedUtc=[DateTime]::UtcNow.ToString('o');
    launcher=$LauncherPath; launcherSha256=$launcherHash;
    silentFixture=$silentPath; silentFixtureDurationSeconds=$fixtureSeconds; silentFixtureSamplesVerifiedZero=$silenceVerified; originalAudioPlaybackRequested=$false;
    activationRequests=@($results | Where-Object { $_.activationRequests } | Measure-Object -Property activationRequests -Sum).Sum;
    soundVolume=0.44; rootConfigUnchanged=$rootConfigUnchanged; results=$results }
$reportPath = Join-Path $runRoot 'results.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 15), $utf8)
Write-Output ('Results: ' + $reportPath)
if (-not $passed) { exit 1 }
