$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$exePath = Join-Path $projectRoot 'Start-Codex-Dragon.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw '请先运行 build.ps1。' }
foreach ($tool in @('ffmpeg', 'ffprobe')) {
    if (-not (Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue)) {
        throw ('重新导出视频需要 PATH 中已有的 ' + $tool + '。正常运行动画不需要此工具。')
    }
}
[xml]$configuration = Get-Content -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Raw
$fps = [int]$configuration.launcher.frameRate
if ($fps -ne 60) { throw '此导出脚本要求 launcher.config.xml 的 frameRate 为 60。' }
$duration = [double]::Parse($configuration.launcher.durationSeconds, [Globalization.CultureInfo]::InvariantCulture)
$expectedFrames = [int][Math]::Ceiling($duration * $fps)
$frameRoot = Join-Path $projectRoot ('output/render-2k-60fps-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $frameRoot -Force | Out-Null
$process = Start-Process -FilePath $exePath -ArgumentList @('--render', ('"' + $frameRoot + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw '逐帧渲染失败，请查看 output/launcher-error.txt。' }
$frameCount = (Get-ChildItem -LiteralPath $frameRoot -Filter 'frame-*.png').Count
if ($frameCount -ne $expectedFrames) { throw ('帧数错误：' + $frameCount + '，期望：' + $expectedFrames) }
$moviePath = Join-Path $projectRoot 'output/gpt-dragon-cinematic-2k-60fps.mp4'
& ffmpeg -hide_banner -loglevel error -y -framerate 60 -i (Join-Path $frameRoot 'frame-%04d.png') -frames:v $expectedFrames -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -r 60 -movflags +faststart $moviePath
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg 编码失败。' }
Copy-Item -LiteralPath (Join-Path $frameRoot 'poster.png') -Destination (Join-Path $projectRoot 'output/gpt-dragon-cinematic-2k-60fps-poster.png')
$probeOutput = & ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=width,height,r_frame_rate,avg_frame_rate,nb_read_frames,duration -of json $moviePath
if ($LASTEXITCODE -ne 0) { throw 'FFprobe 检查失败。' }
$probe = ($probeOutput -join "`n") | ConvertFrom-Json
$stream = $probe.streams[0]
if ($stream.width -ne 2560 -or $stream.height -ne 1440 -or $stream.r_frame_rate -ne '60/1' -or $stream.avg_frame_rate -ne '60/1' -or [int]$stream.nb_read_frames -ne $expectedFrames -or [Math]::Abs([double]$stream.duration - $duration) -gt 0.017) { throw '视频尺寸、帧率、帧数或时长与要求不符。' }
Write-Output ('已生成：' + $moviePath + '；2560 × 1440（2K / 16:9）；60 帧/秒；' + $expectedFrames + ' 帧。')
