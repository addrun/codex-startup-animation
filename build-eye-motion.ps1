[CmdletBinding()]
param([string]$PythonPath = '')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$PythonPath = & (Join-Path $projectRoot 'tools/Resolve-Python.ps1') -PythonPath $PythonPath -RequiredModules @('cv2', 'numpy')
& $PythonPath -B (Join-Path $projectRoot 'tools/build_eye_motion.py')
if($LASTEXITCODE -ne 0) { throw '眼睑动画图集生成失败。请先按 README 的说明安装制作脚本依赖。' }
