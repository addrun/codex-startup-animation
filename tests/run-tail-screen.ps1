[CmdletBinding()]
param([string]$LauncherPath = '', [ValidateSet('waiting','film','both')] [string]$Mode = 'both')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ge 6) {
    $forward = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Mode',$Mode)
    if ($LauncherPath) { $forward += @('-LauncherPath',$LauncherPath) }
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @forward
    exit $LASTEXITCODE
}
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $LauncherPath) { $LauncherPath = Join-Path $projectRoot 'Start-Codex-Dragon.exe' }
$LauncherPath = [IO.Path]::GetFullPath($LauncherPath)
$runRoot = Join-Path $PSScriptRoot ('output\tail-screen-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
if (-not ('TailScreenCapture' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'TailScreenCapture.cs') -ReferencedAssemblies @('System.Drawing','System.Windows.Forms')
}
[TailScreenCapture]::EnableDpi()
$checks = [Collections.Generic.List[object]]::new()
$modes = if ($Mode -eq 'both') { @('waiting','film') } else { @($Mode) }
foreach ($scenario in $modes) {
    $folder = Join-Path $runRoot $scenario
    New-Item -ItemType Directory -Path (Join-Path $folder 'assets') -Force | Out-Null
    $copy = Join-Path $folder 'Start-Codex-Dragon.exe'
    Copy-Item -LiteralPath $LauncherPath -Destination $copy
    foreach ($asset in @('dragon-eye-motion.png','dragon-eye-motion-detail.png')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot ('assets\' + $asset)) -Destination (Join-Path $folder 'assets')
    }
    [xml]$config = Get-Content -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Raw
    $config.SelectSingleNode('/launcher/appUserModelId').InnerText = ''
    $config.SelectSingleNode('/launcher/executablePath').InnerText = (Join-Path $folder 'NeverActivate.exe')
    $config.SelectSingleNode('/launcher/executableArguments').InnerText = '--must-never-launch'
    $config.SelectSingleNode('/launcher/characterImage').InnerText = (Join-Path $projectRoot 'assets\dragon-cinematic-open.png')
    $config.SelectSingleNode('/launcher/closedEyeImage').InnerText = (Join-Path $projectRoot 'assets\dragon-cinematic-closed.png')
    $config.SelectSingleNode('/launcher/videoFile').InnerText = (Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4')
    $config.SelectSingleNode('/launcher/respectReducedMotion').InnerText = 'false'
    $config.SelectSingleNode('/launcher/startupTimeoutSeconds').InnerText = '19'
    $config.Save((Join-Path $folder 'launcher.config.xml'))
    $arguments = if ($scenario -eq 'waiting') { '--waiting-preview' } else { '--observe --startup-test' }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $copy -ArgumentList $arguments -WorkingDirectory $folder -WindowStyle Hidden -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(13)
        if ($scenario -eq 'film') {
            $tailStarted = $false
            do {
                $log = Get-Content -LiteralPath (Join-Path $folder 'output\startup-timing.log') -Raw -ErrorAction SilentlyContinue
                if ($log -match '\tanimation_tail\t' -and $log -notmatch 'animation_tail_fallback') { $tailStarted = $true; break }
                $process.Refresh(); if ($process.HasExited) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            if (-not $tailStarted) { throw 'Actual video-to-tail path did not initialize.' }
        }
        $visibleDeadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $visible = [TailScreenCapture]::Capture($process.Id, (Join-Path $folder 'visible-start.png'))
            if ($visible) { break }
            Start-Sleep -Milliseconds 150
        } while ([DateTime]::UtcNow -lt $visibleDeadline)
        if (-not $visible) { throw ('Test window did not become visible: ' + [TailScreenCapture]::LastFailure) }
        $sampleClock = [Diagnostics.Stopwatch]::StartNew()
        $records = [Collections.Generic.List[object]]::new()
        foreach ($sample in @(0.8,1.8,2.8,3.8,5.2)) {
            while ($sampleClock.Elapsed.TotalSeconds -lt $sample) { Start-Sleep -Milliseconds 50 }
            $image = Join-Path $folder ('screen-' + $sample.ToString('F1',[Globalization.CultureInfo]::InvariantCulture) + '.png')
            $captured = [TailScreenCapture]::Capture($process.Id, $image)
            $records.Add([pscustomobject]@{seconds=$sampleClock.Elapsed.TotalSeconds; path=$image; windowOwnedVisible=$captured})
            if (-not $captured) { throw ('Owned test animation was not fully visible: ' + [TailScreenCapture]::LastFailure) }
        }
        $log = if ($scenario -eq 'film') { Get-Content -LiteralPath (Join-Path $folder 'output\startup-timing.log') -Raw } else { '' }
        $checks.Add([pscustomobject]@{mode=$scenario;pid=$process.Id; captured=$records.ToArray(); passive=$true;
            activationRequests=([regex]::Matches($log,'activation_requested').Count);filmTailConfirmed=($scenario -eq 'waiting' -or $log -match '\tanimation_tail\t')})
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(1500)) { $process.Kill(); [void]$process.WaitForExit(3000) }
    } finally {
        $process.Refresh()
        if (-not $process.HasExited -and $process.Path -ieq $copy) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(1500)) { $process.Kill(); [void]$process.WaitForExit(3000) }
        }
    }
}
$report = [ordered]@{completedUtc=[DateTime]::UtcNow.ToString('o'); root=$runRoot;
    launcherSha256=(Get-FileHash -LiteralPath $LauncherPath -Algorithm SHA256).Hash;
    screenInsetPixels=16;
    captureMethod='Owned visible window interior using CopyFromScreen; verified window ownership';
    scenarios=$checks.ToArray(); note='Capture evidence only; compare visible motion separately.'}
$report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $runRoot 'capture.json') -Encoding UTF8
Set-Content -LiteralPath (Join-Path $projectRoot 'output\tail-screen-latest.txt') -Value $runRoot -Encoding UTF8
Write-Output $runRoot
