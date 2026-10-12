$ErrorActionPreference = 'Stop'
$watcher = Join-Path $PSScriptRoot 'Watch-Codex-Dragon.exe'
if (-not (Test-Path -LiteralPath $watcher -PathType Leaf)) { throw "找不到监视器：$watcher" }
$stopRequest = Start-Process -FilePath $watcher -ArgumentList '--stop' -WindowStyle Hidden -Wait -PassThru
if ($stopRequest.ExitCode -ne 0) { throw "监视器停止请求失败，退出码：$($stopRequest.ExitCode)" }
$ownedRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\','/').ToUpperInvariant()
$hash = [Security.Cryptography.MD5]::Create()
try { $suffix = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($ownedRoot))).Replace('-', '').Substring(0,16) } finally { $hash.Dispose() }
$deadline = [DateTime]::UtcNow.AddSeconds(8)
do {
    try { $runningMutex = [Threading.Mutex]::OpenExisting('Local\CodexDragonWatcher_' + $suffix); $runningMutex.Dispose() }
    catch [Threading.WaitHandleCannotBeOpenedException] { Write-Host '监视器已退出。'; return }
    Start-Sleep -Milliseconds 200
} while ([DateTime]::UtcNow -lt $deadline)
throw '8 秒内未确认监视器退出。'
