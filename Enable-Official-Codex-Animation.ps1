$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$watcher = Join-Path $root 'Watch-Codex-Dragon.exe'
if (-not (Test-Path -LiteralPath $watcher -PathType Leaf)) {
    throw "找不到监视器：$watcher。请先构建项目。"
}

$startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$linkPath = Join-Path $startup 'Codex Dragon Animation Watcher.lnk'
$ownedArguments = '--watch-owned=codex-dragon-animation-v1'
$startupArguments = '--start-detached ' + $ownedArguments
$shell = New-Object -ComObject WScript.Shell
$createdByThisRun = $false
$shortcutBackup = $null
$existing = $null
$stateDirectory = Join-Path $root 'output\watcher-control'
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null

function Wait-OwnedWatcherStopped([string] $ownedPath) {
    $ownedRoot = [IO.Path]::GetDirectoryName($ownedPath).TrimEnd('\','/').ToUpperInvariant()
    $hash = [Security.Cryptography.MD5]::Create()
    try { $suffix = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($ownedRoot))).Replace('-', '').Substring(0,16) } finally { $hash.Dispose() }
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        try { $runningMutex = [Threading.Mutex]::OpenExisting('Local\CodexDragonWatcher_' + $suffix); $runningMutex.Dispose() }
        catch [Threading.WaitHandleCannotBeOpenedException] { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "监视器尚未确认退出，未更新登录项：$ownedPath"
}

if (Test-Path -LiteralPath $linkPath) {
    $existing = $shell.CreateShortcut($linkPath)
    $isOwned = ($existing.Arguments -eq $ownedArguments -or $existing.Arguments -eq $startupArguments) -and
        ([IO.Path]::GetFileName($existing.TargetPath) -ieq 'Watch-Codex-Dragon.exe')
    if (-not $isOwned) {
        throw "启动目录中已存在同名且不属于本项目的快捷方式，未覆盖：$linkPath"
    }
    $shortcutBackup = Join-Path $stateDirectory ('startup-owned-before-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.lnk')
    Copy-Item -LiteralPath $linkPath -Destination $shortcutBackup
    $previousWatcher = [IO.Path]::GetFullPath($existing.TargetPath)
    if (-not [string]::Equals($previousWatcher, $watcher, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $previousWatcher -PathType Leaf)) {
        # The project may have moved. Stop only the monitor belonging to the
        # owned old shortcut before redirecting that same owned shortcut.
        $stopRequest = Start-Process -FilePath $previousWatcher -ArgumentList '--stop' -WindowStyle Hidden -Wait -PassThru
        if ($stopRequest.ExitCode -ne 0) { throw "旧目录监视器停止请求失败，未更新登录快捷方式。" }
        Wait-OwnedWatcherStopped $previousWatcher
    }
} else {
    $createdByThisRun = $true
}

$controlRecord = [ordered]@{
    action = 'enable'; startedUtc = [DateTime]::UtcNow.ToString('o'); completed = $false
    linkPath = $linkPath; previouslyExisted = -not $createdByThisRun
    previousTarget = $(if ($existing) { $existing.TargetPath } else { $null })
    previousArguments = $(if ($existing) { $existing.Arguments } else { $null })
    newArguments = $startupArguments
    previousShortcutBackup = $shortcutBackup; newTarget = $watcher; root = $root
}
$recordPath = Join-Path $stateDirectory ('enable-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
$controlRecord | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding UTF8

$shortcut = $shell.CreateShortcut($linkPath)
$shortcut.TargetPath = $watcher
$shortcut.Arguments = $startupArguments
$shortcut.WorkingDirectory = $root
$shortcut.Description = '观察 Codex 官方应用启动并显示龙娘动画'
$shortcut.IconLocation = (Join-Path $root 'Start-Codex-Dragon.exe') + ',0'
$shortcut.Save()

try {
    $startedUtc = [DateTime]::UtcNow
    # The short bootstrap asks the existing desktop Explorer to create the
    # resident monitor. Ordinary Start-Process alone inherits the host's job.
    $bootstrap = Start-Process -FilePath $watcher -ArgumentList $startupArguments -WorkingDirectory $root -WindowStyle Hidden -Wait -PassThru
    if ($bootstrap.ExitCode -ne 0) { throw "监视器独立启动失败，退出码：$($bootstrap.ExitCode)。请查看 output\watcher-error.txt。" }
    $statusPath = Join-Path $root 'output\watcher-status.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $confirmed = $false
    do {
        if (Test-Path -LiteralPath $statusPath) {
            try {
                $status = Get-Content -LiteralPath $statusPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $freshStatus = $status.updatedUtc -and (([DateTime]$status.updatedUtc).ToUniversalTime() -ge $startedUtc.AddSeconds(-1))
                $liveProcess = if ($status.pid) { Get-Process -Id $status.pid -ErrorAction SilentlyContinue } else { $null }
                if ($status.running -and $freshStatus -and $status.lifetimeIndependent -and
                    ($status.root.TrimEnd('\','/') -ieq $root.TrimEnd('\','/')) -and
                    $liveProcess -and ($liveProcess.Path -ieq $watcher)) { $confirmed = $true; break }
            } catch { }
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $confirmed) { throw '10 秒内未确认独立监视器的进程和心跳。' }
    $launchReceiptPath = Join-Path $root 'output\watcher-launch.json'
    $launchReceipt = Get-Content -LiteralPath $launchReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $status.desktopParent -or $status.parentPid -ne $launchReceipt.explorerPid) { throw '监视器实际父进程与 Explorer 启动记录不一致。' }
    $launchReceipt.watcherPid = $status.pid
    $launchReceipt.watcherInJob = $status.inJob
    $launchReceipt.detachedConfirmed = $true
    $launchReceipt.verification = 'live_heartbeat_and_explorer_parent_confirmed'
    $launchReceipt | ConvertTo-Json | Set-Content -LiteralPath $launchReceiptPath -Encoding UTF8
    $controlRecord.completed = $true
    $controlRecord.completedUtc = [DateTime]::UtcNow.ToString('o')
    $controlRecord | ConvertTo-Json | Set-Content -LiteralPath $recordPath -Encoding UTF8
} catch {
    if ($createdByThisRun -and (Test-Path -LiteralPath $linkPath)) {
        $check = $shell.CreateShortcut($linkPath)
        if (($check.Arguments -eq $startupArguments) -and ($check.TargetPath -eq $watcher)) {
            Remove-Item -LiteralPath $linkPath -Force
        }
    } elseif ($null -ne $shortcutBackup -and (Test-Path -LiteralPath $shortcutBackup)) {
        Copy-Item -LiteralPath $shortcutBackup -Destination $linkPath -Force
    }
    throw
}

Write-Host '已创建当前用户登录监视快捷方式并独立启动监视器；退出 Codex 后监视器可继续运行。'
Write-Host "快捷方式：$linkPath"
if ($null -ne $shortcutBackup) { Write-Host "启用前已有登录项备份：$shortcutBackup" }
