[CmdletBinding()]
param(
    [string]$PythonPath = '',
    [string[]]$RequiredModules = @()
)

$ErrorActionPreference = 'Stop'
$candidates = [Collections.Generic.List[string]]::new()
if ($PythonPath) {
    if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
        throw "Python interpreter does not exist: $PythonPath"
    }
    $candidates.Add((Get-Item -LiteralPath $PythonPath).FullName)
} else {
    foreach ($name in @('python', 'python3')) {
        foreach ($command in @(Get-Command $name -CommandType Application -All -ErrorAction SilentlyContinue)) {
            # Windows Store aliases can open the Store instead of an interpreter.
            if ($command.Source -and $command.Source -notmatch '\\Microsoft\\WindowsApps\\') {
                $candidates.Add($command.Source)
            }
        }
    }
}
foreach ($module in $RequiredModules) {
    if ($module -notmatch '^[A-Za-z_][A-Za-z0-9_.]*$') { throw "Invalid module name: $module" }
}
$probe = 'import sys; assert sys.version_info >= (3, 9)'
if ($RequiredModules.Count) { $probe += '; import ' + ($RequiredModules -join ', ') }
if ($RequiredModules -contains 'PIL') {
    # Single quotes survive native -c argument passing in Windows PowerShell 5.1.
    $probe += '; from PIL import Image; assert hasattr(Image, ''Resampling'') and hasattr(Image, ''Quantize'')'
}
foreach ($candidate in @($candidates | Select-Object -Unique)) {
    try {
        & $candidate -B -c $probe 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return $candidate }
    } catch {
        # In Windows PowerShell, native stderr can become a terminating error.
        continue
    }
}
$required = if ($RequiredModules.Count) { ' with ' + ($RequiredModules -join ', ') } else { '' }
throw "An existing Python 3.9+ interpreter$required is required. Pass -PythonPath to select it. No packages were installed."
