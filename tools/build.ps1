# Compiles the Volcanoids Direct Connect plugin against the game's own DLLs.
# Uses the C# compiler that ships with Windows (.NET Framework 4.x), so no SDK install is needed.
param(
    [Parameter(Mandatory = $true)][string]$ManagedDir,   # <game>\Volcanoids_Data\Managed
    [Parameter(Mandatory = $true)][string]$BepInExCore,  # <game>\BepInEx\core
    [Parameter(Mandatory = $true)][string]$OutFile
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $root 'src\VolcanoidsDirectConnect'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "Could not find csc.exe (.NET Framework 4). It ships with Windows 10/11 - enable '.NET Framework 4.8' in Windows Features." }

$refs = New-Object System.Collections.Generic.List[string]
$useGameCorlib = Test-Path (Join-Path $ManagedDir 'mscorlib.dll')
foreach ($n in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'netstandard.dll')) {
    $p = Join-Path $ManagedDir $n
    if ($useGameCorlib -and (Test-Path $p)) { $refs.Add($p) }
}
foreach ($n in @('UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll',
                 'UnityEngine.InputLegacyModule.dll', 'UnityEngine.UNETModule.dll',
                 'com.unity.multiplayer-hlapi.Runtime.dll')) {
    $p = Join-Path $ManagedDir $n
    if (Test-Path $p) { $refs.Add($p) }
    elseif ($n -ne 'UnityEngine.InputLegacyModule.dll') { throw "Missing game library: $p  (is this really the Volcanoids folder, and is the game fully installed?)" }
}
foreach ($n in @('BepInEx.dll', '0Harmony.dll')) {
    $p = Join-Path $BepInExCore $n
    if (Test-Path $p) { $refs.Add($p) }
    elseif ($n -eq 'BepInEx.dll') { throw "Missing $p - BepInEx is not installed correctly." }
}

$cscArgs = New-Object System.Collections.Generic.List[string]
$cscArgs.AddRange([string[]]@('/nologo', '/target:library', '/optimize+', '/debug-', '/warn:1', '/langversion:5'))
if ($useGameCorlib) { $cscArgs.Add('/nostdlib+'); $cscArgs.Add('/noconfig') }
$cscArgs.Add("/out:$OutFile")
foreach ($r in $refs) { $cscArgs.Add("/reference:$r") }
Get-ChildItem -Path $srcDir -Filter '*.cs' | ForEach-Object { $cscArgs.Add($_.FullName) }

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

Write-Host "Compiling with $csc"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed (exit code $LASTEXITCODE). Copy the errors above and send them to whoever maintains the mod." }
Write-Host "Built $OutFile"
