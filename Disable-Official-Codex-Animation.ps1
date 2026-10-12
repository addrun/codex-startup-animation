$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$watcher = Join-Path $root 'Watch-Codex-Dragon.exe'
$startup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$linkPath = Join-Path $startup 'Codex Dragon Animation Watcher.lnk'
$ownedArguments = '--watch-owned=codex-dragon-animation-v1'
$startupArguments = '--start-detached ' + $ownedArguments

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
    throw "监视器尚未确认退出，未删除登录项：$ownedPath"
}

if (Test-Path -LiteralPath $linkPath) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($linkPath)
    $isOwned = ($shortcut.Arguments -eq $ownedArguments -or $shortcut.Arguments -eq $startupArguments) -and
        ([IO.Path]::GetFileName($shortcut.TargetPath) -ieq 'Watch-Codex-Dragon.exe')
    if ($isOwned) {
        $ownedWatcher = [IO.Path]::GetFullPath($shortcut.TargetPath)
        if (Test-Path -LiteralPath $ownedWatcher -PathType Leaf) {
            $stopRequest = Start-Process -FilePath $ownedWatcher -ArgumentList '--stop' -WindowStyle Hidden -Wait -PassThru
            if ($stopRequest.ExitCode -ne 0) { throw "监视器停止请求失败，未移除登录快捷方式。" }
            Wait-OwnedWatcherStopped $ownedWatcher
        }
        $stateDirectory = Join-Path $root 'output\watcher-control'
        New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
        $backupPath = Join-Path $stateDirectory ('startup-owned-disabled-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.lnk')
        Copy-Item -LiteralPath $linkPath -Destination $backupPath
        Remove-Item -LiteralPath $linkPath -Force
        Write-Host "已移除本项目拥有的登录快捷方式：$linkPath"
    } else {
        Write-Warning "同名快捷方式不属于本项目，未移除：$linkPath"
    }
} elseif (Test-Path -LiteralPath $watcher -PathType Leaf) {
    $stopRequest = Start-Process -FilePath $watcher -ArgumentList '--stop' -WindowStyle Hidden -Wait -PassThru
    if ($stopRequest.ExitCode -ne 0) { throw "监视器停止请求失败，退出码：$($stopRequest.ExitCode)" }
    Wait-OwnedWatcherStopped $watcher
}

Write-Host '已发送监视器退出请求。Codex 官方应用和其他登录项未更改。'
