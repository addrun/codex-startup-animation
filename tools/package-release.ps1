[CmdletBinding()]
param(
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$')]
    [string]$Version = 'local',
    [ValidateSet('All', 'Source', 'Windows')]
    [string]$Kind = 'All',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
$projectPrefix = $projectRoot + [IO.Path]::DirectorySeparatorChar
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $projectRoot) 'codex-startup-animation-packages'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
if ($OutputDirectory -ieq $projectRoot -or $OutputDirectory.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must be outside the project directory.'
}
foreach ($folder in @('src', 'tests', 'tools', 'assets', 'docs', 'LICENSES')) {
    $path = Join-Path $projectRoot $folder
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Missing project directory: $folder" }
    $links = @(Get-Item -LiteralPath $path; Get-ChildItem -LiteralPath $path -Recurse -Force) |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
    if (@($links).Count) { throw "Reparse points are not packaged: $folder" }
}

$common = @('README.md', 'VALIDATION.md', 'THIRD_PARTY_NOTICES.md', 'launcher.config.xml',
    'Enable-Official-Codex-Animation.ps1', 'Disable-Official-Codex-Animation.ps1',
    'Exit-Animation-Watcher.ps1', 'Get-Animation-WatcherStatus.ps1',
    'Preview-Dragon.cmd', 'Preview-Waiting-Dragon.cmd', 'Start-Codex-Dragon.cmd',
    'output/gpt-dragon-cinematic-2k-60fps.mp4')
$sourceRootFiles = @('.gitignore', '.gitattributes', 'build.ps1', 'build-eye-motion.ps1', 'render-preview.ps1')
$runtimeAssets = @('dragon-cinematic-open.png', 'dragon-cinematic-closed.png', 'dragon-eye-motion.png',
    'dragon-cinematic-open-detail.png', 'dragon-cinematic-closed-detail.png', 'dragon-eye-motion-detail.png',
    'dragon-startup.wav', 'dragon-startup-sound.json')

function Get-AllowedFiles([string]$Folder, [string[]]$Extensions) {
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $projectRoot $Folder) -Recurse -File -Force)) {
        $relative = $file.FullName.Substring($projectPrefix.Length).Replace('\','/')
        if ($relative -match '(^|/)(output|__pycache__|pydeps|\.git|\.venv)(/|$)') { continue }
        if ($Extensions -contains $file.Extension.ToLowerInvariant()) { $relative }
    }
}
$publicDocs = @(Get-AllowedFiles 'docs' @('.md','.json','.png','.jpg')) +
    @(Get-AllowedFiles 'LICENSES' @('.txt','.md'))
$sourceFiles = @($common + $sourceRootFiles + $publicDocs +
    @(Get-AllowedFiles 'src' @('.cs')) + @(Get-AllowedFiles 'tests' @('.cs','.ps1','.py')) +
    @(Get-AllowedFiles 'tools' @('.cs','.ps1','.py','.txt')) +
    @(Get-AllowedFiles 'assets' @('.png','.wav','.json','.txt')) | Sort-Object -Unique)
$windowsFiles = @($common + $publicDocs + @('Start-Codex-Dragon.exe','Watch-Codex-Dragon.exe') +
    @($runtimeAssets | ForEach-Object { 'assets/' + $_ }) | Sort-Object -Unique)
$packages = @()
if ($Kind -in @('All','Source')) { $packages += [pscustomobject]@{ name='source'; files=$sourceFiles } }
if ($Kind -in @('All','Windows')) { $packages += [pscustomobject]@{ name='windows'; files=$windowsFiles } }
foreach ($package in $packages) {
    foreach ($relative in $package.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
        if (-not $path.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing or invalid input: $relative" }
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse point input: $relative"
        }
    }
}

$packageDirectory = Join-Path $OutputDirectory ('release-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
if (Test-Path -LiteralPath $packageDirectory) { throw 'Package directory already exists.' }
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
$utf8 = [Text.UTF8Encoding]::new($false)
$sha = [Security.Cryptography.SHA256]::Create()
$summaries = [Collections.Generic.List[object]]::new()
try {
    foreach ($package in $packages) {
        $name = 'codex-startup-animation-' + $package.name + '-' + $Version + '.zip'
        $zipPath = Join-Path $packageDirectory $name
        $fileStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $zip = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create, $false)
        $manifestFiles = [Collections.Generic.List[object]]::new()
        try {
            foreach ($relative in $package.files) {
                # Hash the exact bytes being packaged; live logs and state are never inputs.
                $bytes = [IO.File]::ReadAllBytes((Join-Path $projectRoot $relative))
                $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','')
                $entry = $zip.CreateEntry(('codex-startup-animation/' + $relative), [IO.Compression.CompressionLevel]::Optimal)
                $stream = $entry.Open()
                try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
                $manifestFiles.Add([pscustomobject]@{ file=$relative; bytes=$bytes.Length; sha256=$hash })
            }
            $manifest = [ordered]@{
                schemaVersion=1; packageKind=$package.name; version=$Version;
                generatedUtc=[DateTime]::UtcNow.ToString('o'); fileCount=$manifestFiles.Count;
                stateAndLogsExcluded=$true; sourceLicense='See THIRD_PARTY_NOTICES.md';
                files=@($manifestFiles.ToArray())
            }
            $manifestBytes = $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 6))
            $manifestEntry = $zip.CreateEntry('codex-startup-animation/PACKAGE-MANIFEST.json')
            $stream = $manifestEntry.Open()
            try { $stream.Write($manifestBytes, 0, $manifestBytes.Length) } finally { $stream.Dispose() }
        } finally { $zip.Dispose(); $fileStream.Dispose() }
        $summaries.Add([pscustomobject]@{
            file=$name; bytes=(Get-Item -LiteralPath $zipPath).Length;
            sha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash;
            packagedFileCount=$manifestFiles.Count
        })
    }
} finally { $sha.Dispose() }
$sumLines = @($summaries | ForEach-Object { $_.sha256 + '  ' + $_.file })
[IO.File]::WriteAllLines((Join-Path $packageDirectory 'SHA256SUMS.txt'), $sumLines, $utf8)
[IO.File]::WriteAllText((Join-Path $packageDirectory 'packages.json'),
    ([ordered]@{ schemaVersion=1; version=$Version; packages=@($summaries.ToArray()) } | ConvertTo-Json -Depth 5), $utf8)
Write-Output $packageDirectory
$summaries | Format-Table file, bytes, packagedFileCount
