[CmdletBinding()]
param([ValidateSet('early','late','loss','missing-audio','silent','zero','reduced','fade-escape','fade-close')]
    [string[]]$Cases=@('early','late','loss','missing-audio','silent','zero','reduced','fade-escape','fade-close'),
    [switch]$CaptureScreen, [string]$PythonPath='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$runRoot=Join-Path $PSScriptRoot ('output\intro-fade-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$targetBuild=Join-Path $runRoot 'PageTarget.exe'
$build=@('/nologo','/target:winexe','/main:PageTarget','/warn:4','/warnaserror+','/codepage:65001',('/out:'+$targetBuild))
foreach($name in @('PresentationCore','PresentationFramework','WindowsBase')){$build+='/r:'+(Join-Path $framework ('WPF\'+$name+'.dll'))}
$build+='/r:'+(Join-Path $framework 'System.Xaml.dll')
$build+=Join-Path $PSScriptRoot 'FakeTarget.cs';$build+=Join-Path $PSScriptRoot 'PageTarget.cs'
& (Join-Path $framework 'csc.exe') @build
if($LASTEXITCODE -ne 0){throw 'Synthetic page compilation failed'}
if(-not ('SoundOverlayNative' -as [type])){Add-Type -Path (Join-Path $PSScriptRoot 'SoundOverlayNative.cs')}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class IntroFadeKeys {
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 public static bool Escape(long h,int p){uint owner;GetWindowThreadProcessId(new IntPtr(h),out owner);return owner==p && PostMessage(new IntPtr(h),0x100,new IntPtr(27),new IntPtr(0x10001)) && PostMessage(new IntPtr(h),0x101,new IntPtr(27),new IntPtr(unchecked((int)0xc0010001)));}
}
'@
if($CaptureScreen){
    $PythonPath = & (Join-Path $projectRoot 'tools/Resolve-Python.ps1') -PythonPath $PythonPath -RequiredModules @('PIL','numpy')
    Add-Type -Path (Join-Path $PSScriptRoot 'ExitFadeScreenCapture.cs') -ReferencedAssemblies @('System.Drawing','System.Windows.Forms')
    [ExitFadeScreenCapture]::EnableDpi()
}
$silent=Join-Path $runRoot 'silent.wav';$bytes=44100*4*8.3
$writer=[IO.BinaryWriter]::new([IO.File]::Create($silent))
try{$writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'));$writer.Write([int](36+$bytes));$writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '));$writer.Write([int]16);$writer.Write([int16]1);$writer.Write([int16]2);$writer.Write([int]44100);$writer.Write([int]176400);$writer.Write([int16]4);$writer.Write([int16]16);$writer.Write([Text.Encoding]::ASCII.GetBytes('data'));$writer.Write([int]$bytes);$writer.Write([byte[]]::new([int]$bytes))}finally{$writer.Dispose()}
$results=[Collections.Generic.List[object]]::new();$owned=[Collections.Generic.List[object]]::new()
function Start-Owned([string]$File,[string]$Arguments,[string]$Directory){
    $info=[Diagnostics.ProcessStartInfo]::new($File,$Arguments);$info.UseShellExecute=$false;$info.WorkingDirectory=$Directory
    $p=[Diagnostics.Process]::Start($info);$owned.Add([pscustomobject]@{process=$p;path=$File;birth=$p.StartTime.ToUniversalTime()});return $p
}
function Events([string]$Folder){try{return @(Import-Csv -LiteralPath (Join-Path $Folder 'output\startup-timing.log') -Delimiter "`t")}catch{return @()}}
function Wait-For([scriptblock]$Condition,[int]$Milliseconds){$clock=[Diagnostics.Stopwatch]::StartNew();do{if(& $Condition){return $true};Start-Sleep -Milliseconds 15}while($clock.ElapsedMilliseconds -lt $Milliseconds);return $false}
try{
 foreach($case in $Cases){
    $folder=Join-Path $runRoot $case;New-Item -ItemType Directory -Path (Join-Path $folder 'assets') -Force | Out-Null
    $launcher=Join-Path $folder 'Start-Codex-Dragon.exe';$target=Join-Path $folder 'PageTarget.exe';$control=Join-Path $folder 'control.txt';$marker=Join-Path $folder 'target.tsv'
    Copy-Item -LiteralPath (Join-Path $projectRoot 'Start-Codex-Dragon.exe') -Destination $launcher
    Copy-Item -LiteralPath $targetBuild -Destination $target
    Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\dragon-eye-motion.png') -Destination (Join-Path $folder 'assets')
    [xml]$config=Get-Content -LiteralPath (Join-Path $projectRoot 'launcher.config.xml') -Raw -Encoding UTF8
    # InnerText handles path strings returned by Join-Path consistently in
    # Windows PowerShell 5.1 and PowerShell 7.
    $config.SelectSingleNode('/launcher/appUserModelId').InnerText=''
    $config.SelectSingleNode('/launcher/executablePath').InnerText=[string]$target
    $config.SelectSingleNode('/launcher/executableArguments').InnerText='--must-not-activate'
    $config.SelectSingleNode('/launcher/videoFile').InnerText=[string](Join-Path $projectRoot 'output\gpt-dragon-cinematic-2k-60fps.mp4')
    $config.SelectSingleNode('/launcher/characterImage').InnerText=[string](Join-Path $projectRoot 'assets\dragon-cinematic-open.png')
    $config.SelectSingleNode('/launcher/closedEyeImage').InnerText=[string](Join-Path $projectRoot 'assets\dragon-cinematic-closed.png')
    $config.SelectSingleNode('/launcher/soundFile').InnerText=[string]$(if($case -eq 'missing-audio'){Join-Path $folder 'absent.wav'}else{$silent})
    $config.SelectSingleNode('/launcher/soundEnabled').InnerText=$(if($case -eq 'silent'){'false'}else{'true'})
    $config.SelectSingleNode('/launcher/soundVolume').InnerText=$(if($case -eq 'zero'){'0'}else{'0.44'})
    $config.SelectSingleNode('/launcher/respectReducedMotion').InnerText='false'
    $config.SelectSingleNode('/launcher/startupTimeoutSeconds').InnerText='24'
    $config.Save((Join-Path $folder 'launcher.config.xml'))
    $delay=if($case -eq 'late'){11000}else{250}
    $fixtureArguments='--window-delay-ms 20 --ready-delay-ms '+$delay+' --lifetime-ms 28000 --control "'+$control+'" --marker "'+$marker+'"'
    if($CaptureScreen -and $case -eq 'early'){
        $fixture=Start-Owned $target ($fixtureArguments+' --topmost') $folder
        if(-not (Wait-For {Test-Path -LiteralPath $marker} 3000)){throw 'Owned capture fixture did not start'}
        Start-Sleep -Milliseconds 250
    }
    $overlay=Start-Owned $launcher $(if($case -eq 'reduced'){'--observe --reduced-motion'}else{'--observe'}) $folder
    if(-not ($CaptureScreen -and $case -eq 'early')){$fixture=Start-Owned $target $fixtureArguments $folder}
    if($case -eq 'loss'){
        if(-not (Wait-For {@(Events $folder|Where-Object event -eq 'page_ready_deferred').Count -eq 1} 5000)){throw 'Early page readiness was not deferred'}
        [IO.File]::WriteAllText($control,'blank:1')
        if(-not (Wait-For {@(Events $folder|Where-Object event -eq 'intro_completed').Count -eq 1} 11000)){throw 'Intro did not finish while page was blank'}
        Start-Sleep -Milliseconds 350
        if($overlay.HasExited -or @(Events $folder|Where-Object event -eq 'ready_fade_started').Count){throw 'Stale early readiness dismissed the blank page'}
        [IO.File]::WriteAllText($control,'ready:2')
    }
    if($CaptureScreen -and $case -eq 'early'){
        if(-not (Wait-For {@(Events $folder|Where-Object event -eq 'media_opened').Count -eq 1} 5000)){throw 'Video did not open'}
        $events=@(Events $folder);$start=$events|Where-Object event -eq 'startup_started'|Select-Object -First 1
        $opened=$events|Where-Object event -eq 'media_opened'|Select-Object -First 1
        $utc=[DateTime]::Parse($start.detail.Substring(4)).ToUniversalTime().AddSeconds([double]$opened.seconds-[double]$start.seconds+7.15)
        while([DateTime]::UtcNow -lt $utc){Start-Sleep -Milliseconds 10}
        $capture=[ExitFadeScreenCapture]::Capture($fixture.Id,$overlay.Id,(Join-Path $folder 'screen'))
        $capture|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $folder 'capture.json') -Encoding UTF8
        if($capture.error){throw $capture.error}
        & $PythonPath -B (Join-Path $PSScriptRoot 'analyze_exit_fade.py') $folder
        if($LASTEXITCODE -ne 0){throw 'Screen fade validation failed'}
    }
    if($case -in @('fade-escape','fade-close')){
        if(-not (Wait-For {@(Events $folder|Where-Object event -eq 'ready_fade_started').Count -eq 1} 15000)){throw 'Ready fade did not begin'}
        $handle=[SoundOverlayNative]::Find($overlay.Id)
        if($handle -eq 0){throw 'Owned transition layer not visible'}
        $sent=if($case -eq 'fade-close'){[SoundOverlayNative]::Close($handle,$overlay.Id)}else{[IntroFadeKeys]::Escape($handle,$overlay.Id)}
        if(-not $sent){throw 'Owned transition skip message failed'}
    }
    if(-not $overlay.WaitForExit(22000)){throw 'Owned animation did not exit'}
    $events=@(Events $folder);$intro=@($events|Where-Object event -eq 'intro_completed');$dismiss=@($events|Where-Object{ $_.event -eq 'overlay_dismissed' -and $_.detail -eq 'window_ready'})
    $fade=@($events|Where-Object event -eq 'ready_fade_completed');$skip=@($events|Where-Object event -eq 'ready_fade_skipped');$shutdown=@($events|Where-Object event -eq 'shutdown')
    $pass=$overlay.ExitCode -eq 0 -and -not $fixture.HasExited -and $intro.Count -eq 1 -and $dismiss.Count -eq 1 -and $shutdown.Count -eq 1 -and $shutdown[0].detail -match 'overlay_closed=True' -and
        @($events|Where-Object event -in @('activation_requested','ready_fade_failed','shutdown_deadline')).Count -eq 0
    if($case -eq 'reduced'){$pass=$pass -and $fade.Count -eq 0 -and [double]$dismiss[0].seconds -lt 5 -and @($events|Where-Object event -eq 'sound_started').Count -eq 0}
    elseif($case -in @('fade-escape','fade-close')){$pass=$pass -and $skip.Count -eq 1}
    else{$pass=$pass -and $fade.Count -eq 1 -and [double]$dismiss[0].seconds -ge 8.3 -and [Array]::IndexOf($events,$intro[0]) -lt [Array]::IndexOf($events,$dismiss[0])}
    if($case -eq 'late'){$pass=$pass -and [double]$dismiss[0].seconds -ge 11.5}
    if($case -eq 'loss'){$pass=$pass -and @($events|Where-Object event -eq 'page_content_lost').Count -ge 1 -and @($events|Where-Object event -eq 'page_content_ready').Count -ge 2}
    if($case -notin @('missing-audio','silent','zero','reduced')){$pass=$pass -and @($events|Where-Object event -eq 'sound_finished').Count -eq 1 -and @($events|Where-Object event -eq 'sound_fade_completed').Count -eq 0}
    $results.Add([pscustomobject]@{name=$case;passed=$pass;exitCode=$overlay.ExitCode;events=$events;folder=$folder})
    Write-Output ((@('FAIL','PASS')[[int]$pass])+': '+$case)
    [void]$fixture.CloseMainWindow();[void]$fixture.WaitForExit(2000)
 }
}catch{$results.Add([pscustomobject]@{name='suite-error';passed=$false;error=$_.Exception.ToString()});Write-Output ('FAIL: '+$_.Exception.Message)}
finally{
 foreach($record in $owned){try{$p=$record.process;if(-not $p.HasExited -and $p.Path -eq $record.path -and $p.StartTime.ToUniversalTime() -eq $record.birth -and $record.path.StartsWith($runRoot+'\',[StringComparison]::OrdinalIgnoreCase)){[void]$p.CloseMainWindow();if(-not $p.WaitForExit(1200)){$p.Kill();[void]$p.WaitForExit(2000)}}}catch{}}
 $passed=$results.Count -eq $Cases.Count -and @($results|Where-Object passed -ne $true).Count -eq 0
 [ordered]@{passed=$passed;launcherSha256=(Get-FileHash -LiteralPath (Join-Path $projectRoot 'Start-Codex-Dragon.exe')).Hash;checkCount=$results.Count;activationRequests=0;screenCapture=[bool]$CaptureScreen;tests=$results.ToArray();completedUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding UTF8
 Write-Output ('Results: '+(Join-Path $runRoot 'results.json'))
}
if(-not $passed){exit 1}
