# Developer build: compiles the plugin against a Volcanoids install (needs the .NET SDK).
# Normal players don't need this - setup.bat installs prebuilt\VolcanoidsDirectConnect.dll.
#
#   powershell -ExecutionPolicy Bypass -File tools\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Volcanoids"
#   (add -UpdatePrebuilt to also refresh prebuilt\VolcanoidsDirectConnect.dll)
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string]$OutFile = '',
    [switch]$UpdatePrebuilt
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\VolcanoidsDirectConnect\VolcanoidsDirectConnect.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is required to build (https://dotnet.microsoft.com/download). Players can use setup.bat without it.'
}
if (-not (Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) {
    throw "BepInEx isn't installed in $GameDir yet - run setup.bat once first."
}

$outDir = Join-Path $env:TEMP 'VolcanoidsDirectConnect.build'
& dotnet build $proj -c Release -nologo "-p:GameDir=$GameDir" -o $outDir
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }
$dll = Join-Path $outDir 'VolcanoidsDirectConnect.dll'

if ($OutFile) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile) | Out-Null
    Copy-Item -Force $dll $OutFile
    Write-Host "Built $OutFile"
}
if ($UpdatePrebuilt) {
    Copy-Item -Force $dll (Join-Path $root 'prebuilt\VolcanoidsDirectConnect.dll')
    Write-Host 'Updated prebuilt\VolcanoidsDirectConnect.dll'
}
