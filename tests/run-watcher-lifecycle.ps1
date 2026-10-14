[CmdletBinding()]
param([string]$WatcherPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $WatcherPath) { $WatcherPath = Join-Path $projectRoot 'Watch-Codex-Dragon.exe' }
$WatcherPath = [IO.Path]::GetFullPath($WatcherPath)
if (-not (Test-Path -LiteralPath $WatcherPath -PathType Leaf)) { throw 'Watcher executable missing.' }
$runRoot = Join-Path $PSScriptRoot ('output\watcher-lifecycle-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$fixture = Join-Path $runRoot 'FakeTarget.exe'
$watchExe = Join-Path $runRoot 'Watch-Codex-Dragon.exe'
$observer = Join-Path $runRoot 'Start-Codex-Dragon.exe'
$controlFile = Join-Path $runRoot 'control.txt'
$marker = Join-Path $runRoot 'target.tsv'
$observerLog = Join-Path $runRoot 'output\observer.tsv'
$results = [Collections.Generic.List[object]]::new()
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$watcher = $null
$buildArguments = @('/nologo','/target:winexe','/codepage:65001','/warn:4','/warnaserror+',('/out:' + $fixture))
foreach ($assembly in @('PresentationCore','PresentationFramework','WindowsBase')) { $buildArguments += '/r:' + (Join-Path $framework ('WPF\' + $assembly + '.dll')) }
$buildArguments += '/r:' + (Join-Path $framework 'System.Xaml.dll')
$buildArguments += Join-Path $PSScriptRoot 'FakeTarget.cs'
& $compiler @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Fake target build failed.' }
& $compiler '/nologo' '/target:winexe' '/codepage:65001' '/warn:4' '/warnaserror+' ('/out:' + $observer) (Join-Path $PSScriptRoot 'FakeObserver.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake observer build failed.' }
Copy-Item -LiteralPath $WatcherPath -Destination $watchExe
Copy-Item -LiteralPath $fixture -Destination (Join-Path $runRoot 'UnrelatedCodexCli.exe')
[xml]$config = Get-Content -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Raw -Encoding utf8
$config.SelectSingleNode('/launcher/appUserModelId').InnerText = ''
$config.SelectSingleNode('/launcher/executablePath').InnerText = $fixture
$config.SelectSingleNode('/launcher/executableArguments').InnerText = '--this-must-never-be-used'
$config.SelectSingleNode('/launcher/soundEnabled').InnerText = 'false'
$config.Save((Join-Path $runRoot 'launcher.config.xml'))

function Start-Owned([string]$File, [string]$Arguments = '') {
    $info = [Diagnostics.ProcessStartInfo]::new($File, $Arguments)
    $info.UseShellExecute = $false; $info.WorkingDirectory = $runRoot
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add($process)
    return $process
}
function Read-Status {
    try { return Get-Content -LiteralPath (Join-Path $runRoot 'output\watcher-status.json') -Raw -Encoding utf8 | ConvertFrom-Json } catch { return $null }
}
function Status-Evidence {
    $status = Read-Status
    return [ordered]@{pid=$status.pid;running=[bool]$status.running;cyclesStarted=[int]$status.cyclesStarted;mainWindowCount=[int]$status.mainWindowCount;knownDesktopProcessCount=[int]$status.knownDesktopProcessCount;activeOverlayPid=$status.activeOverlayPid;lastTrigger=[string]$status.lastTrigger;lastError=[string]$status.lastError;observerCount=(Count-Observer)}
}
function Count-Observer {
    if (-not (Test-Path -LiteralPath $observerLog)) { return 0 }
    return @(Get-Content -LiteralPath $observerLog -Encoding utf8).Count
}
function Count-TargetStarts {
    if (-not (Test-Path -LiteralPath $marker)) { return 0 }
    return @(Get-Content -LiteralPath $marker -Encoding utf8 | Where-Object { $_ -match '\tprocess_started$' }).Count
}
function Wait-Condition([scriptblock]$Condition, [int]$Deadline = 9000) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 100 } while ($clock.ElapsedMilliseconds -lt $Deadline)
    return $false
}
function Check([string]$Name, [bool]$Passed, $Evidence) {
    $results.Add([ordered]@{name=$Name;passed=$Passed;evidence=$Evidence})
    Write-Output ((@('FAIL','PASS')[[int]$Passed]) + ': ' + $Name)
    if (-not $Passed) { throw $Name }
}
function Start-Watch {
    $script:watcher = Start-Owned $watchExe
    if (-not (Wait-Condition { $status=Read-Status; $status -and $status.pid -eq $script:watcher.Id -and $status.running })) { throw 'Watcher heartbeat missing.' }
}
function Stop-Watch {
    $stop = Start-Owned $watchExe '--stop'
    if (-not $stop.WaitForExit(5000) -or -not $script:watcher.WaitForExit(7000)) { throw 'Watcher stop timed out.' }
}
function Set-Control([string]$Command) {
    $previousCount = 0
    if (Test-Path -LiteralPath $marker) { $previousCount = @(Get-Content -LiteralPath $marker | Where-Object { $_ -match ('\tcommand_' + [regex]::Escape($Command) + '$') }).Count }
    [IO.File]::WriteAllText($controlFile, $Command + '-' + [Guid]::NewGuid().ToString('N'))
    if (-not (Wait-Condition { @(Get-Content -LiteralPath $marker -ErrorAction SilentlyContinue | Where-Object { $_ -match ('\tcommand_' + [regex]::Escape($Command) + '$') }).Count -gt $previousCount })) { throw 'Fixture command not received.' }
}
function Start-Target([int]$Delay = 500) {
    return Start-Owned $fixture ('--delay-ms ' + $Delay + ' --lifetime-ms 180000 --control "' + $controlFile + '" --marker "' + $marker + '"')
}
try {
    Start-Watch
    Start-Sleep -Milliseconds 4200
    Check 'watcher-empty-baseline-never-activates-target-or-observer' ((Read-Status).cyclesStarted -eq 0 -and (Count-Observer) -eq 0 -and (Count-TargetStarts) -eq 0) (Status-Evidence)
    $unrelated = Start-Owned (Join-Path $runRoot 'UnrelatedCodexCli.exe') '--no-window --lifetime-ms 1500'
    [void]$unrelated.WaitForExit(4000)
    Check 'unrelated-cli-excluded' ((Read-Status).cyclesStarted -eq 0 -and (Count-Observer) -eq 0) (Status-Evidence)
    $target = Start-Target
    Check 'first-cold-start-once-passive-only' (Wait-Condition { $status=Read-Status; $status.cyclesStarted -eq 1 -and $status.mainWindowCount -eq 1 -and $null -eq $status.activeOverlayPid -and (Count-Observer) -eq 1 }) (Status-Evidence)
    Check 'observer-never-restarts-target' ((Count-TargetStarts) -eq 1 -and @(Get-Content -LiteralPath $observerLog | Where-Object { $_ -notmatch '\tpassive_only\t--observe$' }).Count -eq 0) ([ordered]@{targetStarts=(Count-TargetStarts);observerCount=(Count-Observer)})
    Set-Control 'minimize'; Start-Sleep -Milliseconds 1200
    Set-Control 'restore'; Start-Sleep -Milliseconds 1200
    Check 'minimized-restore-no-replay' ((Read-Status).cyclesStarted -eq 1 -and (Count-Observer) -eq 1) (Status-Evidence)
    Set-Control 'hide'
    Check 'tray-hide-keeps-live-primary' (Wait-Condition { $status=Read-Status; $status.mainWindowCount -eq 0 -and $status.knownDesktopProcessCount -eq 1 }) (Status-Evidence)
    Set-Control 'show'; Start-Sleep -Milliseconds 1400
    Check 'tray-restore-no-replay' (Wait-Condition { $status=Read-Status; $status.mainWindowCount -eq 1 -and $status.cyclesStarted -eq 1 -and (Count-Observer) -eq 1 }) (Status-Evidence)
    Set-Control 'destroy'
    Check 'native-window-destroy-live-primary-suppressed' (Wait-Condition { $status=Read-Status; $status.mainWindowCount -eq 0 -and $status.knownDesktopProcessCount -eq 1 }) (Status-Evidence)
    Set-Control 'create'; Start-Sleep -Milliseconds 1400
    Check 'native-window-recreate-same-primary-no-replay' (Wait-Condition { $status=Read-Status; $status.cyclesStarted -eq 1 -and $status.mainWindowCount -eq 1 -and (Count-Observer) -eq 1 }) (Status-Evidence)
    Set-Control 'hide'
    if (-not (Wait-Condition { (Read-Status).mainWindowCount -eq 0 })) { throw 'Hidden fixture was not observed.' }
    Stop-Watch; Start-Watch; Start-Sleep -Milliseconds 1200
    Check 'watcher-restart-hidden-primary-no-baseline-replay' ((Read-Status).knownDesktopProcessCount -eq 1 -and (Read-Status).cyclesStarted -eq 0 -and (Count-Observer) -eq 1) (Status-Evidence)
    Set-Control 'child'; Start-Sleep -Milliseconds 2500
    Check 'electron-child-does-not-create-new-cycle' ((Read-Status).knownDesktopProcessCount -eq 1 -and (Read-Status).cyclesStarted -eq 0 -and (Count-Observer) -eq 1) (Status-Evidence)
    $renderer = Start-Owned $fixture '--type=renderer --no-window --lifetime-ms 6500'
    [void]$renderer.WaitForExit(9000)
    Check 'renderer-type-excluded-through-poll-interval' ((Read-Status).knownDesktopProcessCount -eq 1 -and (Read-Status).cyclesStarted -eq 0 -and (Count-Observer) -eq 1) (Status-Evidence)
    $activation = Start-Owned $fixture '--no-window --lifetime-ms 6500'
    Check 'new-activation-observed-with-surviving-primary-suppressed' (Wait-Condition { $status=Read-Status; $status.knownDesktopProcessCount -eq 2 -and $status.cyclesStarted -eq 0 -and (Count-Observer) -eq 1 }) (Status-Evidence)
    [void]$activation.WaitForExit(9000)
    Check 'temporary-activation-exit-retains-original-primary-cycle' (Wait-Condition { $status=Read-Status; $status.knownDesktopProcessCount -eq 1 -and $status.cyclesStarted -eq 0 -and (Count-Observer) -eq 1 }) (Status-Evidence)
    Set-Control 'show'; Start-Sleep -Milliseconds 1400
    Check 'hidden-baseline-restore-no-replay' (Wait-Condition { $status=Read-Status; $status.mainWindowCount -eq 1 -and $status.cyclesStarted -eq 0 -and (Count-Observer) -eq 1 }) (Status-Evidence)
    Set-Control 'exit'
    if (-not $target.WaitForExit(3000)) { throw 'Target did not exit.' }
    [IO.File]::WriteAllText($controlFile, 'idle-' + [Guid]::NewGuid().ToString('N'))
    $target = Start-Target 800
    Check 'complete-exit-rapid-relaunch-new-cycle-once' (Wait-Condition { $status=Read-Status; $status.cyclesStarted -eq 1 -and $status.mainWindowCount -eq 1 -and $null -eq $status.activeOverlayPid -and (Count-Observer) -eq 2 }) (Status-Evidence)
    Start-Sleep -Milliseconds 1800
    Check 'second-cold-start-no-duplicates-or-target-activation' ((Read-Status).cyclesStarted -eq 1 -and (Count-Observer) -eq 2 -and (Count-TargetStarts) -eq 2) ([ordered]@{targetStarts=(Count-TargetStarts);observerCount=(Count-Observer)})
    Stop-Watch
    Check 'watcher-stop-does-not-stop-live-target' (-not $target.HasExited) ([ordered]@{targetAlive=(-not $target.HasExited);watcherExited=$watcher.HasExited})
    Set-Control 'exit'; [void]$target.WaitForExit(3000)
} catch {
    $results.Add([ordered]@{name='unexpected-error';passed=$false;evidence=[string]$_.Exception.Message})
} finally {
    foreach ($process in $owned) {
        try {
            # Operate only on absolute executable paths verified within this run.
            $ownedPath = [IO.Path]::GetFullPath($process.Path)
            if (-not $process.HasExited -and $ownedPath.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { $process.Kill(); [void]$process.WaitForExit(3000) }
        } catch { }
    }
    $report = [ordered]@{passed=@($results | Where-Object { -not $_.passed }).Count -eq 0;watcherSource=$WatcherPath;watcherSha256=(Get-FileHash -LiteralPath $watchExe -Algorithm SHA256).Hash;tests=$results.ToArray();root=$runRoot;target='Only isolated FakeTarget and FakeObserver executables; no media or real Codex activation.';originalAudioPlaybackRequested=$false}
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding utf8
    Write-Output ('Results: ' + (Join-Path $runRoot 'results.json'))
    foreach ($process in $owned) { $process.Dispose() }
}
if (-not $report.passed) { exit 1 }
