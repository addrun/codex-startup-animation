$ErrorActionPreference = 'Stop'
$watcher = Join-Path $PSScriptRoot 'Watch-Codex-Dragon.exe'
if (-not (Test-Path -LiteralPath $watcher -PathType Leaf)) { throw "找不到监视器：$watcher" }
$statusRequest = Start-Process -FilePath $watcher -ArgumentList '--status' -WindowStyle Hidden -Wait -PassThru
if ($statusRequest.ExitCode -ne 0) { throw "读取监视器状态失败，退出码：$($statusRequest.ExitCode)" }
$report = Join-Path $PSScriptRoot 'output\watcher-status-query.json'
if (Test-Path -LiteralPath $report) {
    Get-Content -LiteralPath $report -Encoding UTF8 | ConvertFrom-Json | Format-List
} else {
    Write-Host "状态报告尚未生成：$report"
}
