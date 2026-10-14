$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$package = @(Get-AppxPackage -Name OpenAI.Codex)
if ($package.Count -ne 1 -or $package[0].Name -ne 'OpenAI.Codex') {
    throw '未找到当前用户的官方 Codex 安装包。请在日常登录的 Windows 用户下运行。'
}
$package = $package[0]
[xml]$manifest = Get-Content -LiteralPath (Join-Path $package.InstallLocation 'AppxManifest.xml') -Raw
$extensions = @($manifest.SelectNodes("//*[local-name()='Extension' and @Category='windows.startupTask']") |
    Where-Object { ($_.Executable -replace '\\','/') -eq 'app/ChatGPT.exe' })
if ($extensions.Count -ne 1) { throw '官方 Codex 登录任务声明已变化，未修改任何设置。' }
$definitions = @($extensions[0].SelectNodes(".//*[local-name()='StartupTask']"))
if ($definitions.Count -ne 1 -or $definitions[0].TaskId -ne 'App') {
    throw '官方 Codex 登录任务身份已变化，未修改任何设置。'
}
$key = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\' + $package.PackageFamilyName + '\App'
if (-not (Test-Path -LiteralPath $key)) { throw '未找到当前用户的 Codex 登录任务状态，未创建注册表项。' }
$before = Get-ItemProperty -LiteralPath $key
if ($null -eq $before.State -or [int]$before.State -notin @(0,1,2)) {
    throw '登录任务状态未知或由系统策略管理，未修改任何设置。'
}
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex Dragon Animation Watcher.lnk'
$watcherHash = if (Test-Path -LiteralPath $startup) { (Get-FileHash -LiteralPath $startup).Hash } else { $null }
$stateDirectory = Join-Path $root 'output\watcher-control'
New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
$recordPath = Join-Path $stateDirectory ('official-login-disabled-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
$record = [ordered]@{
    account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    packageFamily = $package.PackageFamilyName; packageVersion = [string]$package.Version
    taskId = 'App'; key = $key; previousState = [int]$before.State
    previousUserEnabledStartupOnce = $before.UserEnabledStartupOnce
    capturedUtc = [DateTime]::UtcNow.ToString('o'); completed = $false
    watcherShortcutSha256 = $watcherHash
}
$record | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $recordPath -Encoding UTF8
# Persist the explicit user choice. Do not touch Windows-wide RestartApps,
# other startup entries, or either application's running processes.
if ([int]$before.State -ne 1) { Set-ItemProperty -LiteralPath $key -Name State -Type DWord -Value 1 }
$after = Get-ItemProperty -LiteralPath $key
if ([int]$after.State -ne 1 -or $after.UserEnabledStartupOnce -ne $before.UserEnabledStartupOnce) {
    throw 'Codex 登录任务状态复核失败，请查看本机备份。'
}
$afterWatcherHash = if (Test-Path -LiteralPath $startup) { (Get-FileHash -LiteralPath $startup).Hash } else { $null }
if ($afterWatcherHash -ne $watcherHash) { throw '监视器快捷方式在修复期间发生变化，请复核。' }
$record.completed = $true
$record.afterState = 1
$record.completedUtc = [DateTime]::UtcNow.ToString('o')
$record.watcherStartupPreserved = $true
$record | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $recordPath -Encoding UTF8
Write-Output '官方 Codex（启动应用中可能显示为 ChatGPT）登录自启动已关闭；动画监视器登录项保留。'
Write-Output ('本机恢复记录：' + $recordPath)
