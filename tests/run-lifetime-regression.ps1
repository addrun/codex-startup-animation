[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
# A space in this path also exercises the desktop broker's argument quoting.
$runRoot = Join-Path $PSScriptRoot ('output\lifetime regression-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compiler = Join-Path $framework 'csc.exe'
$parentFixture = Join-Path $runRoot 'WatcherLifetimeFixture.exe'
& $compiler /nologo /target:exe /codepage:65001 /warn:4 /warnaserror+ (('/out:' + $parentFixture)) (Join-Path $PSScriptRoot 'WatcherLifetimeFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Lifetime fixture build failed.' }
$targetExe = Join-Path $runRoot 'FakeTarget.exe'
$compileTarget = @('/nologo','/target:winexe','/codepage:65001','/warn:4','/warnaserror+',('/out:' + $targetExe))
foreach ($name in @('PresentationCore','PresentationFramework','WindowsBase')) { $compileTarget += '/r:' + (Join-Path $framework ('WPF\' + $name + '.dll')) }
$compileTarget += '/r:' + (Join-Path $framework 'System.Xaml.dll')
$compileTarget += Join-Path $PSScriptRoot 'FakeTarget.cs'
& $compiler @compileTarget
if ($LASTEXITCODE -ne 0) { throw 'Fake target build failed.' }
foreach ($name in @('Watch-Codex-Dragon.exe','Start-Codex-Dragon.exe')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $runRoot }
New-Item -ItemType Directory -Path (Join-Path $runRoot 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\dragon-eye-motion.png') -Destination (Join-Path $runRoot 'assets')
[xml]$config = Get-Content -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Raw -Encoding UTF8
$config.launcher.appUserModelId = ''
$config.launcher.executablePath = $targetExe
$config.launcher.executableArguments = '--unexpected-activation'
$config.launcher.videoFile = Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4'
$config.launcher.characterImage = Join-Path $projectRoot 'assets\dragon-cinematic-open.png'
$config.launcher.closedEyeImage = Join-Path $projectRoot 'assets\dragon-cinematic-closed.png'
$config.launcher.startupTimeoutSeconds = '5'
$config.Save((Join-Path $runRoot 'launcher.config.xml'))
$watchExe = Join-Path $runRoot 'Watch-Codex-Dragon.exe'
$statusPath = Join-Path $runRoot 'output\watcher-status.json'
$results = [Collections.Generic.List[object]]::new()
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$watcher = $null
$parentData = $null

function Start-Owned([string]$File, [string]$Arguments = '') {
    $info = [Diagnostics.ProcessStartInfo]::new($File, $Arguments)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WorkingDirectory = $runRoot
    $process = [Diagnostics.Process]::Start($info); $owned.Add($process); return $process
}
function Read-Status {
    try { return (Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}
function Wait-Condition([scriptblock]$Condition, [int]$Timeout = 7000) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 100 } while ($clock.ElapsedMilliseconds -lt $Timeout)
    return $false
}
function Check([string]$Name, [bool]$Passed, $Evidence) {
    $results.Add([pscustomobject]@{name=$Name;passed=$Passed;evidence=$Evidence})
    Write-Output ((@('FAIL','PASS')[[int]$Passed]) + ': ' + $Name)
    if (-not $Passed) { throw $Name }
}
function Query-Status([string]$Name) {
    $path = Join-Path $runRoot ('output\' + $Name + '.json')
    $request = Start-Owned $watchExe ('--status --report "' + $path + '"')
    if (-not $request.WaitForExit(5000) -or $request.ExitCode -ne 0) { throw 'Status query failed.' }
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}
function Start-DesktopTarget([string]$Marker) {
    $lookup = New-Object -ComObject Shell.Application
    $windows = $lookup.Windows()
    [int]$desktopWindow = 0
    $desktop = $windows.FindWindowSW(0,0,8,[ref]$desktopWindow,1)
    if (-not $desktop -or $desktop.FullName -ine (Join-Path $env:WINDIR 'explorer.exe')) { throw 'Desktop Explorer not available.' }
    # Tool-launched fixtures can live on a different desktop or use a process
    # ACL that a normal Explorer child cannot inspect. Model the official icon
    # by creating the fake desktop app from Explorer as well.
    $desktop.Document.Application.ShellExecute($targetExe,('--delay-ms 1300 --lifetime-ms 5000 --marker "' + $Marker + '"'),$runRoot,'open',0)
    if (-not (Wait-Condition { Test-Path -LiteralPath $Marker } 5000)) { throw 'Desktop target did not start.' }
    $started = Get-Content -LiteralPath $Marker | Where-Object { $_ -match 'process_started$' } | Select-Object -First 1
    $targetPid = [int]($started -split "`t")[1]
    $process = Get-Process -Id $targetPid -ErrorAction Stop
    $owned.Add($process)
    return $process
}
function Test-ProcessExists([int]$ProcessId, [string]$StartedUtc) {
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        return ([Math]::Abs(($process.StartTime.ToUniversalTime() - ([DateTime]$StartedUtc).ToUniversalTime()).TotalMilliseconds) -lt 20)
    } catch { return $false }
}

try {
    $parent = Start-Owned $parentFixture ('--run-parent "' + $watchExe + '" "' + $runRoot + '"')
    if (-not $parent.WaitForExit(30000)) { throw 'Lifetime parent did not exit.' }
    $parentData = Get-Content -LiteralPath (Join-Path $runRoot 'lifetime-parent.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check 'host-job-has-kill-on-close' ($parentData.parentInOwnedJob -and $parentData.controlInOwnedJob -and (($parentData.jobFlags -band 8192) -ne 0)) $parentData
    Check 'explorer-launch-created-independent-watcher' ($parentData.passed -and $parentData.bootstrapExitCode -eq 0 -and -not $parentData.watcherInOwnedJob) $parentData
    $watcher = Get-Process -Id $parentData.watcherPid -ErrorAction Stop
    $owned.Add($watcher)
    Check 'ordinary-child-dies-with-host' (Wait-Condition { -not (Test-ProcessExists $parentData.controlPid $parentData.controlStartedUtc) } 5000) $parentData.controlPid
    $controlBefore = Get-Content -LiteralPath $parentData.controlStatusPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $controlReport = Join-Path $runRoot 'output\dead-control-query.json'
    $controlQueryProcess = Start-Owned $parentData.controlExe ('--status --report "' + $controlReport + '"')
    if (-not $controlQueryProcess.WaitForExit(5000) -or $controlQueryProcess.ExitCode -ne 0) { throw 'Control status query failed.' }
    $controlQuery = Get-Content -LiteralPath $controlReport -Raw -Encoding UTF8 | ConvertFrom-Json
    Check 'dead-original-watcher-status-is-rejected' ($controlBefore.running -and -not $controlQuery.running -and -not $controlQuery.processAlive -and -not $controlQuery.mutexExists) $controlQuery
    $before = Read-Status
    Check 'watcher-survives-host-exit' (-not $watcher.HasExited -and $before.running -and $before.lifetimeIndependent -and $before.desktopParent) $before
    Check 'heartbeat-continues-after-host-exit' (Wait-Condition { $s=Read-Status; $s -and $s.pid -eq $watcher.Id -and ([DateTime]$s.updatedUtc).ToUniversalTime() -gt ([DateTime]$before.updatedUtc).ToUniversalTime() }) (Read-Status)
    $query = Query-Status 'live-query'
    Check 'live-status-verifies-process-and-heartbeat' ($query.running -and $query.processAlive -and $query.mutexExists -and $query.heartbeatFresh) $query

    for ($cycle = 1; $cycle -le 2; $cycle++) {
        $marker = Join-Path $runRoot ('target-' + $cycle + '.tsv')
        $target = Start-DesktopTarget $marker
        Check ("target-start-$cycle-shows-one-overlay") (Wait-Condition { $s=Read-Status; $s -and $s.cyclesStarted -eq $cycle -and $s.mainWindowCount -eq 1 -and $null -eq $s.activeOverlayPid } 9000) (Read-Status)
        $timing = Get-Content -LiteralPath (Join-Path $runRoot 'output\startup-timing.log') -Raw -Encoding UTF8
        $targetStarts = @(Get-Content -LiteralPath $marker | Where-Object { $_ -match 'process_started$' }).Count
        Check ("target-start-$cycle-remains-observe-only") ($targetStarts -eq 1 -and $timing -match 'window_ready' -and $timing -notmatch 'activation_requested') $timing
        if (-not $target.WaitForExit(7000)) { throw 'Fake target failed to finish.' }
        Check ("target-exit-$cycle-rearms-watcher") (Wait-Condition { $s=Read-Status; $s -and $s.mainWindowCount -eq 0 -and $s.cyclesStarted -eq $cycle }) (Read-Status)
    }

    $stop = Start-Owned $watchExe '--stop'
    if (-not $stop.WaitForExit(5000) -or -not $watcher.WaitForExit(7000)) { throw 'Owned watcher stop failed.' }
    $query = Query-Status 'stopped-query'
    Check 'stopped-status-is-not-running' (-not $query.running -and -not $query.mutexExists -and -not $query.processAlive) $query
    # Preserve a final heartbeat marked running to reproduce the user's stale
    # status. Querying it must not turn history into a claim of a live monitor.
    $before.running = $true
    $before.updatedUtc = [DateTime]::UtcNow.AddMinutes(-5).ToString('o')
    $before | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
    $originalHash = (Get-FileHash -LiteralPath $statusPath).Hash
    $query = Query-Status 'stale-query'
    Check 'stale-running-report-is-rejected' (-not $query.running -and -not $query.heartbeatFresh -and -not $query.processAlive) $query
    Check 'status-query-preserves-last-heartbeat-evidence' ((Get-FileHash -LiteralPath $statusPath).Hash -eq $originalHash) $originalHash
} catch {
    $results.Add([pscustomobject]@{name='unexpected-error';passed=$false;evidence=$_.Exception.ToString()})
} finally {
    # Every process touched here belongs to this new, isolated test directory.
    # The real Codex and the production watcher are never stopped.
    if ($watcher -and -not $watcher.HasExited) {
        try { $stop=Start-Owned $watchExe '--stop'; [void]$stop.WaitForExit(5000); [void]$watcher.WaitForExit(7000) } catch { }
    }
    if ($parentData -and $parentData.watcherPid -and -not $watcher) {
        try { $owned.Add((Get-Process -Id $parentData.watcherPid -ErrorAction Stop)) } catch { }
    }
    foreach ($process in $owned) {
        try {
            if (-not $process.HasExited -and $process.Path.StartsWith($runRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                $process.Kill(); [void]$process.WaitForExit(3000)
            }
        } catch { }
    }
    $report = [ordered]@{passed=@($results | Where-Object { -not $_.passed }).Count -eq 0;tests=$results.ToArray();root=$runRoot;target='Disposable parent job and fake target only; no real Codex activation.'}
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding UTF8
    Write-Output ('Results: ' + (Join-Path $runRoot 'results.json'))
}
if (-not $report.passed) { exit 1 }
