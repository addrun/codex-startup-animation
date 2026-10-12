# This checks the compiled launcher against a WPF fake target only.
# It writes everything under tests/output and never changes launcher.config.xml.
[CmdletBinding()]
param([string]$LauncherPath = '')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $LauncherPath) { $LauncherPath = Join-Path $projectRoot 'Start-Codex-Dragon.exe' }
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) { throw 'Build the launcher first with build.ps1.' }

$runId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$runRoot = Join-Path $PSScriptRoot ('output\' + $runId)
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compilerPath = Join-Path $frameworkPath 'csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Windows .NET Framework csc.exe is required.' }
$fakeBuild = Join-Path $runRoot 'FakeTarget.exe'
$compileArguments = @('/nologo', '/target:winexe', '/codepage:65001', '/warn:4', '/warnaserror+', ('/out:' + $fakeBuild))
foreach ($assembly in @('PresentationCore', 'PresentationFramework', 'WindowsBase')) {
    $compileArguments += '/r:' + (Join-Path $frameworkPath ('WPF\' + $assembly + '.dll'))
}
$compileArguments += '/r:' + (Join-Path $frameworkPath 'System.Xaml.dll')
$compileArguments += Join-Path $PSScriptRoot 'FakeTarget.cs'
& $compilerPath @compileArguments
if ($LASTEXITCODE -ne 0) { throw 'Fake target compilation failed.' }

$results = [Collections.Generic.List[object]]::new()
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$rootConfigHash = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Algorithm SHA256).Hash
$videoPath = Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4'

function New-Scenario {
    param([string]$Name, [int]$DelayMs = 1500, [double]$Timeout = 15, [switch]$NoWindow, [string]$VideoMode = 'normal')
    $folder = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item -LiteralPath $LauncherPath -Destination (Join-Path $folder 'Start-Codex-Dragon.exe')
    Copy-Item -LiteralPath $fakeBuild -Destination (Join-Path $folder 'FakeTarget.exe')
    # EyeRig's fallback atlas is not configurable in upstream, unlike both character images.
    $atlasFolder = Join-Path $folder 'assets'
    New-Item -ItemType Directory -Path $atlasFolder -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\dragon-eye-motion.png') -Destination $atlasFolder
    $marker = Join-Path $folder 'target-events.tsv'
    $targetExe = Join-Path $folder 'FakeTarget.exe'
    $targetArguments = '--delay-ms ' + $DelayMs + ' --lifetime-ms 12000 --marker "' + $marker + '"'
    if ($NoWindow) { $targetArguments += ' --no-window' }
    $scenarioVideo = $videoPath
    if ($VideoMode -eq 'corrupt') {
        $scenarioVideo = Join-Path $folder 'invalid.mp4'
        [IO.File]::WriteAllBytes($scenarioVideo, [Text.Encoding]::UTF8.GetBytes('Deliberately invalid MP4 integration fixture.'))
    }
    if ($VideoMode -eq 'missing') { $scenarioVideo = Join-Path $folder 'absent.mp4' }
    $xml = [xml]'<launcher />'
    $values = [ordered]@{
        durationSeconds = '8.0'; frameRate = '60'; videoFile = $scenarioVideo
        appUserModelId = ''; executablePath = $targetExe; executableArguments = $targetArguments
        characterImage = (Join-Path $projectRoot 'assets\dragon-cinematic-open.png')
        closedEyeImage = (Join-Path $projectRoot 'assets\dragon-cinematic-closed.png')
        respectReducedMotion = 'false'; startupTimeoutSeconds = $Timeout.ToString([Globalization.CultureInfo]::InvariantCulture)
        windowReadyDelaySeconds = '0.4'
    }
    foreach ($entry in $values.GetEnumerator()) {
        $node = $xml.CreateElement($entry.Key); $node.InnerText = $entry.Value; [void]$xml.DocumentElement.AppendChild($node)
    }
    $xml.Save((Join-Path $folder 'launcher.config.xml'))
    return [pscustomobject]@{ Name = $Name; Folder = $folder; Marker = $marker; Target = $targetExe; Timeout = $Timeout }
}

function Start-Launcher {
    param($Scenario, [string]$Arguments = '--startup-test')
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Join-Path $Scenario.Folder 'Start-Codex-Dragon.exe'
    $info.WorkingDirectory = $Scenario.Folder
    $info.Arguments = $Arguments
    $info.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($info)
    $ownedProcesses.Add($process)
    return $process
}

function Wait-Launcher {
    param([Diagnostics.Process]$Process, [int]$TimeoutMs = 14000)
    if (-not $Process.WaitForExit($TimeoutMs)) {
        $Process.Kill(); $Process.WaitForExit()
        throw "Isolated launcher PID $($Process.Id) did not exit within $TimeoutMs ms."
    }
    return $Process.ExitCode
}

function Read-Events {
    param($Scenario)
    $logPath = Join-Path $Scenario.Folder 'output\startup-timing.log'
    if (Test-Path -LiteralPath $logPath) { return @(Import-Csv -LiteralPath $logPath -Delimiter "`t") }
    return @()
}

function Target-StartCount {
    param($Scenario)
    if (-not (Test-Path -LiteralPath $Scenario.Marker)) { return 0 }
    return @(Get-Content -LiteralPath $Scenario.Marker | Where-Object { $_ -match "`tprocess_started$" }).Count
}

function Stop-FakeTarget {
    param($Scenario)
    if (-not (Test-Path -LiteralPath $Scenario.Marker)) { return }
    $targetPids = @(Get-Content -LiteralPath $Scenario.Marker | ForEach-Object { ($_ -split "`t")[1] } | Select-Object -Unique)
    foreach ($targetPidText in $targetPids) {
        $targetProcess = Get-Process -Id ([int]$targetPidText) -ErrorAction SilentlyContinue
        if ($targetProcess -and ([IO.Path]::GetFullPath($targetProcess.Path) -eq $Scenario.Target)) {
            # Kill only the fake target created in this exact isolated scenario.
            $targetProcess.Kill(); $targetProcess.WaitForExit()
        }
    }
}

function Add-Result {
    param([string]$Name, [bool]$Passed, $Evidence)
    $results.Add([pscustomobject]@{ name = $Name; passed = $Passed; evidence = $Evidence })
    Write-Output ((@('FAIL', 'PASS')[[int]$Passed]) + ': ' + $Name)
}

function Test-ReadyScenario {
    param([string]$Name, [string]$VideoMode = 'normal', [string]$Arguments = '--startup-test')
    $scenario = New-Scenario -Name $Name -VideoMode $VideoMode
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Launcher -Scenario $scenario -Arguments $Arguments
        $exitCode = Wait-Launcher $process
        $timer.Stop()
        $events = @(Read-Events $scenario)
        $requested = @($events | Where-Object event -eq 'activation_requested')
        $observed = @($events | Where-Object event -eq 'window_observed')
        $dismissed = @($events | Where-Object event -eq 'overlay_dismissed')
        $accepted = @($events | Where-Object event -eq 'activation_accepted')
        $starts = Target-StartCount $scenario
        $mediaFailure = @($events | Where-Object event -eq 'media_failed')
        $passed = $exitCode -eq 0 -and $requested.Count -eq 1 -and $accepted.Count -eq 1 -and $starts -eq 1 -and $timer.Elapsed.TotalSeconds -lt 13
        if ($VideoMode -ne 'corrupt' -and $Arguments -notmatch 'reduced-motion') {
            $passed = $passed -and @($events | Where-Object event -eq 'intro_completed').Count -eq 1 -and
                @($events | Where-Object event -eq 'ready_fade_completed').Count -eq 1 -and
                @($events | Where-Object event -in @('ready_fade_failed','shutdown_deadline')).Count -eq 0
        }
        if ($VideoMode -eq 'corrupt') { $passed = $passed -and $mediaFailure.Count -eq 1 }
        else { $passed = $passed -and $observed.Count -ge 1 -and @($dismissed | Where-Object detail -eq 'window_ready').Count -eq 1 }
        Add-Result $Name $passed ([ordered]@{ exitCode = $exitCode; externalElapsedSeconds = $timer.Elapsed.TotalSeconds; targetStarts = $starts; events = $events; folder = $scenario.Folder })
    } catch { Add-Result $Name $false ([ordered]@{ error = $_.Exception.Message; folder = $scenario.Folder }) }
    finally { Stop-FakeTarget $scenario }
}

try {
    Test-ReadyScenario 'delayed-window'
    Test-ReadyScenario 'corrupt-video' -VideoMode 'corrupt'
    Test-ReadyScenario 'reduced-motion' -Arguments '--startup-test --reduced-motion'
    Test-ReadyScenario 'missing-video' -VideoMode 'missing'

    $timeoutScenario = New-Scenario -Name 'no-window-timeout' -Timeout 2 -NoWindow
    try {
        $process = Start-Launcher $timeoutScenario
        $exitCode = Wait-Launcher $process
        $events = @(Read-Events $timeoutScenario)
        $completed = @($events | Where-Object event -eq 'completed')
        Add-Result 'no-window-timeout' ($exitCode -eq 2 -and (Target-StartCount $timeoutScenario) -eq 1 -and $completed.Count -eq 1 -and $completed[0].detail -match '^window_timeout;') ([ordered]@{ exitCode = $exitCode; events = $events; folder = $timeoutScenario.Folder })
    } catch { Add-Result 'no-window-timeout' $false ([ordered]@{ error = $_.Exception.Message; folder = $timeoutScenario.Folder }) }
    finally { Stop-FakeTarget $timeoutScenario }

    $previewScenario = New-Scenario -Name 'preview-no-activation'
    try {
        $process = Start-Launcher $previewScenario '--preview-test'
        $exitCode = Wait-Launcher $process
        $previewLog = Join-Path $previewScenario.Folder 'output\native-preview-validation.txt'
        Add-Result 'preview-no-activation' ($exitCode -eq 0 -and (Target-StartCount $previewScenario) -eq 0 -and @(Read-Events $previewScenario).Count -eq 0 -and (Test-Path -LiteralPath $previewLog)) ([ordered]@{ exitCode = $exitCode; targetStarts = (Target-StartCount $previewScenario); nativePreviewLog = $previewLog; folder = $previewScenario.Folder })
    } catch { Add-Result 'preview-no-activation' $false ([ordered]@{ error = $_.Exception.Message; folder = $previewScenario.Folder }) }
    finally { Stop-FakeTarget $previewScenario }

    foreach ($conflict in @(
        @{name='preview-activation-conflict'; args='--preview-test --activation-test'},
        @{name='render-check-activation-conflict'; args='--self-test --activation-test'}
    )) {
        $conflictScenario = New-Scenario -Name $conflict.name
        try {
            $process = Start-Launcher $conflictScenario $conflict.args
            $exitCode = Wait-Launcher $process
            Add-Result $conflict.name ($exitCode -eq 1 -and (Target-StartCount $conflictScenario) -eq 0) ([ordered]@{ exitCode=$exitCode; targetStarts=(Target-StartCount $conflictScenario); folder=$conflictScenario.Folder })
        } catch { Add-Result $conflict.name $false ([ordered]@{ error=$_.Exception.Message; folder=$conflictScenario.Folder }) }
        finally { Stop-FakeTarget $conflictScenario }
    }

    $duplicateScenario = New-Scenario -Name 'duplicate-start' -DelayMs 2500
    try {
        $first = Start-Launcher $duplicateScenario
        # Wait for the first activation marker, rather than relying on launch scheduling.
        $markerWait = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $duplicateScenario.Marker) -and $markerWait.Elapsed.TotalSeconds -lt 3) { Start-Sleep -Milliseconds 50 }
        $second = Start-Launcher $duplicateScenario
        $secondExit = Wait-Launcher $second
        $firstExit = Wait-Launcher $first
        $events = @(Read-Events $duplicateScenario)
        $requests = @($events | Where-Object event -eq 'activation_requested').Count
        $starts = Target-StartCount $duplicateScenario
        Add-Result 'duplicate-start' ($firstExit -eq 0 -and $secondExit -eq 0 -and $requests -eq 1 -and $starts -eq 1) ([ordered]@{ firstExit = $firstExit; secondExit = $secondExit; requests = $requests; targetStarts = $starts; events = $events; folder = $duplicateScenario.Folder })
    } catch { Add-Result 'duplicate-start' $false ([ordered]@{ error = $_.Exception.Message; folder = $duplicateScenario.Folder }) }
    finally { Stop-FakeTarget $duplicateScenario }
} finally {
    foreach ($ownedProcess in $ownedProcesses) {
        try { if (-not $ownedProcess.HasExited) { $ownedProcess.Kill(); $ownedProcess.WaitForExit() } } catch { }
    }
    $finalConfigHash = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Algorithm SHA256).Hash
    Add-Result 'root-config-unchanged' ($rootConfigHash -eq $finalConfigHash) ([ordered]@{ beforeSha256 = $rootConfigHash; afterSha256 = $finalConfigHash })
    $report = [ordered]@{
        runId = $runId; launcherPath = $LauncherPath
        launcherSha256 = (Get-FileHash -LiteralPath $LauncherPath -Algorithm SHA256).Hash
        target = 'Tests compiled fake WPF app only; no real Codex activation.'
        completedUtc = [DateTime]::UtcNow.ToString('o'); passed = @($results | Where-Object { -not $_.passed }).Count -eq 0
        tests = @($results.ToArray())
    }
    $reportPath = Join-Path $runRoot 'results.json'
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Write-Output ('Results: ' + $reportPath)
}
if (-not $report.passed) { exit 1 }
