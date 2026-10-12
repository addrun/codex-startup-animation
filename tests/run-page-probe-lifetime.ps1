[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $PSScriptRoot ('output\page-worker-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$stubRoot = Join-Path $runRoot 'blocked stub'
New-Item -ItemType Directory -Path $stubRoot -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$compiler = Join-Path $framework 'csc.exe'
$launcher = Join-Path $runRoot 'Start-Codex-Dragon.exe'
Copy-Item -LiteralPath (Join-Path $projectRoot 'Start-Codex-Dragon.exe') -Destination $launcher
Copy-Item -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Destination $runRoot
$stubSource = Join-Path $stubRoot 'BlockedProbe.cs'
[IO.File]::WriteAllText($stubSource, 'using System; using System.Threading; public static class BlockedProbe { public static void Main() { Console.ReadLine(); Thread.Sleep(Timeout.Infinite); } }')
$stubExe = Join-Path $stubRoot 'Start-Codex-Dragon.exe'
& $compiler /nologo /target:winexe /warn:4 /warnaserror+ ('/out:' + $stubExe) $stubSource
if ($LASTEXITCODE -ne 0) { throw 'Blocked probe fixture compilation failed.' }
$fixture = Join-Path $runRoot 'PageProbeLifetime.exe'
& $compiler /nologo /target:exe /warn:4 /warnaserror+ ('/out:' + $fixture) ('/r:' + $launcher) (Join-Path $PSScriptRoot 'PageProbeLifetime.cs')
if ($LASTEXITCODE -ne 0) { throw 'Page client lifetime fixture compilation failed.' }
$output = @(& $fixture $stubRoot)
$clientExit = $LASTEXITCODE
foreach ($line in $output) { Write-Output $line }
$owner = $null; $worker = $null; $parentExit = $false
try {
    $ownerInfo = [Diagnostics.ProcessStartInfo]::new($stubExe)
    $ownerInfo.UseShellExecute=$false; $ownerInfo.CreateNoWindow=$true; $ownerInfo.RedirectStandardInput=$true
    $owner = [Diagnostics.Process]::Start($ownerInfo)
    $workerInfo = [Diagnostics.ProcessStartInfo]::new($launcher, ('--page-probe-server ' + $owner.Id))
    $workerInfo.UseShellExecute=$false; $workerInfo.CreateNoWindow=$true
    $workerInfo.RedirectStandardInput=$true; $workerInfo.RedirectStandardOutput=$true
    $worker = [Diagnostics.Process]::Start($workerInfo)
    Start-Sleep -Milliseconds 900
    if ($worker.HasExited) { throw 'Worker exited before parent termination; regression has no evidence.' }
    $owner.Kill(); [void]$owner.WaitForExit(2000)
    # Keep the worker stdin open: its parent-handle monitor, rather than EOF,
    # must release the worker even while its input/provider thread is blocked.
    $parentExit = $worker.WaitForExit(2500) -and $worker.ExitCode -eq 0
    Write-Output ((@('FAIL','PASS')[[int]$parentExit]) + ': parent-exit-releases-worker')
} finally {
    foreach ($owned in @($worker,$owner)) {
        if ($owned) { try { if (-not $owned.HasExited) { $owned.Kill(); [void]$owned.WaitForExit(2000) } } finally { $owned.Dispose() } }
    }
}
$leftovers = @(Get-Process -Name Start-Codex-Dragon -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $stubExe -or $_.Path -eq $launcher })
$passed = $clientExit -eq 0 -and $parentExit -and $leftovers.Count -eq 0
$report = [ordered]@{ passed=$passed; launcherSha256=(Get-FileHash -LiteralPath $launcher).Hash; completedUtc=[DateTime]::UtcNow.ToString('o'); clientExit=$clientExit; checks=$output; parentExitReleasesWorker=$parentExit; leftoverWorkers=$leftovers.Count; target='Isolated blocked probe stub and parent only; no real Codex activation.' }
$reportPath = Join-Path $runRoot 'results.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output ('Results: ' + $reportPath)
if (-not $passed) { exit 1 }
