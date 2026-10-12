$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319'
    $compilerPath = Join-Path $frameworkPath 'csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows 的 .NET Framework 4.x。' }
$buildArguments = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', '/warn:4', '/warnaserror+')
$buildArguments += '/out:' + (Join-Path $projectRoot 'Start-Codex-Dragon.exe')
$buildArguments += '/r:' + (Join-Path $frameworkPath 'WPF/PresentationCore.dll')
$buildArguments += '/r:' + (Join-Path $frameworkPath 'WPF/PresentationFramework.dll')
$buildArguments += '/r:' + (Join-Path $frameworkPath 'WPF/WindowsBase.dll')
$buildArguments += '/r:' + (Join-Path $frameworkPath 'System.Xaml.dll')
$buildArguments += '/r:' + (Join-Path $frameworkPath 'System.Xml.Linq.dll')
$buildArguments += '/r:' + (Join-Path $frameworkPath 'System.Drawing.dll')
$buildArguments += '/r:' + (Join-Path $frameworkPath 'WPF/UIAutomationClient.dll')
$buildArguments += '/r:' + (Join-Path $frameworkPath 'WPF/UIAutomationTypes.dll')
$buildArguments += (Join-Path $projectRoot 'src/DragonLauncher.cs')
$buildArguments += (Join-Path $projectRoot 'src/CinematicScene.cs')
$buildArguments += (Join-Path $projectRoot 'src/StrokeTrace.cs')
$buildArguments += (Join-Path $projectRoot 'src/FilmSplash.cs')
$buildArguments += (Join-Path $projectRoot 'src/EyeRig.cs')
$buildArguments += (Join-Path $projectRoot 'src/StartupSound.cs')
$buildArguments += (Join-Path $projectRoot 'src/ReadyExitTransition.cs')
$buildArguments += (Join-Path $projectRoot 'src/StartupCoordinator.cs')
$buildArguments += (Join-Path $projectRoot 'src/WindowGeometry.cs')
$buildArguments += (Join-Path $projectRoot 'src/WaitingTail.cs')
$buildArguments += (Join-Path $projectRoot 'src/PageReadiness.cs')
& $compilerPath @buildArguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Output ('已生成：' + (Join-Path $projectRoot 'Start-Codex-Dragon.exe'))
$watchArguments = @($buildArguments | Where-Object { $_.StartsWith('/') -and $_ -notlike '/out:*' })
$watchArguments += '/out:' + (Join-Path $projectRoot 'Watch-Codex-Dragon.exe')
$watchArguments += '/main:DragonLauncher.WatchProgram'
$watchArguments += '/r:' + (Join-Path $frameworkPath 'System.Management.dll')
$watchArguments += '/r:' + (Join-Path $frameworkPath 'System.Windows.Forms.dll')
$watchArguments += '/r:' + (Join-Path $frameworkPath 'System.Drawing.dll')
$watchArguments += '/r:' + (Join-Path $frameworkPath 'System.Web.Extensions.dll')
$watchArguments += @($buildArguments | Where-Object { -not $_.StartsWith('/') })
$watchArguments += (Join-Path $projectRoot 'src/WatchHost.cs')
$watchArguments += (Join-Path $projectRoot 'src/WatcherLifetime.cs')
$watchArguments += (Join-Path $projectRoot 'src/ExplorerStarter.cs')
& $compilerPath @watchArguments
if ($LASTEXITCODE -ne 0) { throw '监视器编译失败。' }
Write-Output ('已生成：' + (Join-Path $projectRoot 'Watch-Codex-Dragon.exe'))
