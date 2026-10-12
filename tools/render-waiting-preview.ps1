[CmdletBinding()]
param([double]$Seconds = 6, [int]$FrameRate = 15)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $projectRoot 'Start-Codex-Dragon.exe'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'Build the launcher first.' }
$folder = Join-Path $projectRoot ('output\waiting-art-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
}
$helper = Join-Path $folder 'RenderWaitingPreview.exe'
$compile = @('/nologo', '/target:exe', '/optimize+', '/warn:4', '/warnaserror+', ('/out:' + $helper), ('/r:' + $launcher))
foreach ($assembly in @('PresentationCore', 'PresentationFramework', 'WindowsBase')) {
    $compile += '/r:' + (Join-Path $framework ('WPF\' + $assembly + '.dll'))
}
$compile += '/r:' + (Join-Path $framework 'System.Xaml.dll')
$compile += Join-Path $PSScriptRoot 'RenderWaitingPreview.cs'
& $compiler @compile
if ($LASTEXITCODE -ne 0) { throw 'Offscreen preview helper build failed.' }
# Put the assembly beside the helper so .NET resolves the exact current build.
Copy-Item -LiteralPath $launcher -Destination (Join-Path $folder 'Start-Codex-Dragon.exe')
$arguments = @(('"' + $projectRoot + '"'), ('"' + $folder + '"'),
    $Seconds.ToString([Globalization.CultureInfo]::InvariantCulture), $FrameRate.ToString())
$process = Start-Process -FilePath $helper -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Offscreen preview failed: $($process.ExitCode)" }
Set-Content -LiteralPath (Join-Path $projectRoot 'output\waiting-art-latest.txt') -Value $folder -Encoding UTF8
Write-Output $folder
Get-Content -LiteralPath (Join-Path $folder 'cpu-update-timing.txt')
