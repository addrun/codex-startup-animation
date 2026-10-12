[CmdletBinding()]
param(
    [string]$LauncherPath,
    [string]$AudioPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $LauncherPath) { $LauncherPath = Join-Path $projectRoot 'Start-Codex-Dragon.exe' }
if (-not $AudioPath) { $AudioPath = Join-Path $projectRoot 'assets\dragon-startup.wav' }
$LauncherPath = (Get-Item -LiteralPath $LauncherPath).FullName
$AudioPath = (Get-Item -LiteralPath $AudioPath).FullName
$runRoot = Join-Path $PSScriptRoot ('output\sound-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runRoot | Out-Null

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework compiler is unavailable.' }

# Load production types from a frozen copy. Its entry point is never executed.
$assemblyPath = Join-Path $runRoot 'Start-Codex-Dragon.exe'
Copy-Item -LiteralPath $LauncherPath -Destination $assemblyPath
$fixture = Join-Path $runRoot 'StartupSoundIntegration.exe'
$fixtureReport = Join-Path $runRoot 'lifecycle.json'
$arguments = @('/nologo', '/target:exe', '/optimize+', '/warn:4', '/warnaserror+', '/codepage:65001')
$arguments += '/out:' + $fixture
$arguments += '/r:' + $assemblyPath
$arguments += '/r:' + (Join-Path $framework 'WPF\PresentationCore.dll')
$arguments += '/r:' + (Join-Path $framework 'WPF\PresentationFramework.dll')
$arguments += '/r:' + (Join-Path $framework 'WPF\WindowsBase.dll')
$arguments += '/r:' + (Join-Path $framework 'System.Web.Extensions.dll')
$arguments += Join-Path $PSScriptRoot 'StartupSoundIntegration.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Sound integration fixture compilation failed; build the final launcher first.' }

# Only this console helper runs. Its playback cases use an all-zero PCM fixture.
$output = @(& $fixture $projectRoot $AudioPath $runRoot $fixtureReport)
$helperExit = $LASTEXITCODE
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllLines((Join-Path $runRoot 'checks.txt'), [string[]]$output, $utf8)
foreach ($line in $output) { Write-Output $line }
if (-not (Test-Path -LiteralPath $fixtureReport)) { throw 'Sound integration helper produced no report.' }
$lifecycle = [IO.File]::ReadAllText($fixtureReport) | ConvertFrom-Json
$passed = $helperExit -eq 0 -and $lifecycle.passed
$report = [ordered]@{
    passed = $passed
    completedUtc = [DateTime]::UtcNow.ToString('o')
    launcher = $LauncherPath
    launcherSha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
    audio = $AudioPath
    audioSha256 = (Get-FileHash -LiteralPath $AudioPath -Algorithm SHA256).Hash
    helperExit = $helperExit
    passedCount = $lifecycle.passedCount
    checkCount = $lifecycle.checkCount
    activationRequests = 0
    animationExecutableStarted = $false
    originalAudioPlaybackRequested = $false
    playbackFixture = 'All-zero PCM WAV; no audible output.'
    silentFixtureDurationSeconds = $lifecycle.silentFixtureDurationSeconds
    configuredPlaybackVolume = $lifecycle.configuredPlaybackVolume
    expectedCheckCount = $lifecycle.expectedCheckCount
    tailClockAndTimerLifetimeVerified = $lifecycle.tailClockAndTimerLifetimeVerified
    checks = $lifecycle.checks
}
$reportPath = Join-Path $runRoot 'results.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 12), $utf8)
Write-Output ('Results: ' + $reportPath)
if (-not $passed) { exit 1 }
