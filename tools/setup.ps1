# Volcanoids Direct Connect - installer
# Installs BepInEx 5 into Volcanoids, builds the mod against your copy of the game and installs it.
param(
    [string]$GamePath = '',
    [switch]$Uninstall,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$ModName      = 'VolcanoidsDirectConnect'
$BepVersion   = '5.4.23.6'
$BepUrl       = "https://github.com/BepInEx/BepInEx/releases/download/v$BepVersion/BepInEx_win_x64_$BepVersion.zip"
$BepSha256    = '6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755'
$DefaultPort  = 27777
$FirewallRule = 'Volcanoids Direct Connect'
$Root         = Split-Path -Parent $PSScriptRoot

function Write-Step($msg) { Write-Host ''; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "    $msg" -ForegroundColor Yellow }

function Test-GameDir([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    return (Test-Path (Join-Path $dir 'Volcanoids.exe')) -and (Test-Path (Join-Path $dir 'Volcanoids_Data\Managed'))
}

function Get-SteamLibraries {
    $libs = New-Object System.Collections.Generic.List[string]
    $steamRoots = @()
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($item.SteamPath)   { $steamRoots += ($item.SteamPath -replace '/', '\') }
            if ($item.InstallPath) { $steamRoots += $item.InstallPath }
        } catch { }
    }
    $steamRoots += @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")
    foreach ($sr in ($steamRoots | Select-Object -Unique)) {
        if (-not (Test-Path $sr)) { continue }
        $libs.Add($sr)
        $vdf = Join-Path $sr 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
            }
        }
    }
    # Common extra library locations on other drives
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        foreach ($sub in @('SteamLibrary', 'Steam', 'Games\Steam', 'Games\SteamLibrary')) {
            $p = Join-Path $d.Root $sub
            if (Test-Path $p) { $libs.Add($p) }
        }
    }
    return $libs | Select-Object -Unique
}

function Find-Game {
    foreach ($lib in Get-SteamLibraries) {
        $dir = Join-Path $lib 'steamapps\common\Volcanoids'
        if (Test-GameDir $dir) { return (Resolve-Path $dir).Path }
    }
    return $null
}

function Install-BepInEx([string]$game) {
    $core = Join-Path $game 'BepInEx\core\BepInEx.dll'
    if ((Test-Path $core) -and (Test-Path (Join-Path $game 'winhttp.dll'))) {
        Write-Ok "BepInEx already installed."
        return
    }
    $zip = Join-Path $env:TEMP "BepInEx_win_x64_$BepVersion.zip"
    Write-Host "    Downloading BepInEx $BepVersion from GitHub..."
    Invoke-WebRequest -Uri $BepUrl -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
    if ($hash -ne $BepSha256) {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        throw "BepInEx download failed its integrity check (got $hash). Try again later."
    }
    Write-Host "    Extracting into $game"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            $dest = [System.IO.Path]::GetFullPath((Join-Path $game $entry.FullName))
            if (-not $dest.StartsWith([System.IO.Path]::GetFullPath($game), [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Force -Path $dest | Out-Null; continue }
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        }
    } finally { $archive.Dispose() }
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Write-Ok "BepInEx $BepVersion installed."
}

function Set-BepInExConsole([string]$game) {
    # Show the BepInEx log console so problems are visible while testing.
    $cfgDir = Join-Path $game 'BepInEx\config'
    $cfg = Join-Path $cfgDir 'BepInEx.cfg'
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    if (-not (Test-Path $cfg)) {
        Set-Content -Path $cfg -Encoding UTF8 -Value "[Logging.Console]`r`nEnabled = true`r`n"
    } else {
        $txt = Get-Content $cfg -Raw
        if ($txt -match '(?ms)\[Logging\.Console\].*?Enabled\s*=\s*false') {
            $txt = [regex]::Replace($txt, '(?ms)(\[Logging\.Console\].*?Enabled\s*=\s*)false', '${1}true')
            Set-Content -Path $cfg -Value $txt -Encoding UTF8
        }
    }
}

function Add-FirewallRule([int]$port) {
    $cmd = "netsh advfirewall firewall delete rule name=`"$FirewallRule`" >nul 2>&1 & " +
           "netsh advfirewall firewall add rule name=`"$FirewallRule`" dir=in action=allow protocol=TCP localport=$port profile=any"
    try {
        $p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $cmd -Verb RunAs -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -eq 0) { Write-Ok "Firewall: inbound TCP $port allowed." }
        else { Write-Warn2 "Firewall rule may not have been added (exit $($p.ExitCode))." }
    } catch {
        Write-Warn2 "Skipped firewall rule (administrator permission was not granted)."
        Write-Warn2 "If friends can't connect to you, allow TCP port $port in Windows Firewall."
    }
}

function Remove-FirewallRule {
    try {
        Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', "netsh advfirewall firewall delete rule name=`"$FirewallRule`"" -Verb RunAs -Wait -WindowStyle Hidden | Out-Null
    } catch { }
}

try {
    Write-Host ''
    Write-Host '  ===============================================' -ForegroundColor Magenta
    Write-Host '   Volcanoids Direct Connect - setup' -ForegroundColor Magenta
    Write-Host '   Peer-to-peer co-op over Tailscale / LAN / IP' -ForegroundColor Magenta
    Write-Host '  ===============================================' -ForegroundColor Magenta

    # ---------------------------------------------------------------- locate game
    Write-Step 'Looking for Volcanoids'
    if (-not (Test-GameDir $GamePath)) { $GamePath = Find-Game }
    while (-not (Test-GameDir $GamePath)) {
        Write-Warn2 'Could not find Volcanoids automatically.'
        Write-Host  '    Install it from Steam first, then paste the game folder here'
        Write-Host  '    (Steam > right-click Volcanoids > Manage > Browse local files, copy the address bar).'
        $GamePath = (Read-Host '    Game folder (or leave empty to quit)').Trim().Trim('"')
        if ([string]::IsNullOrWhiteSpace($GamePath)) { throw 'Volcanoids folder not provided.' }
    }
    Write-Ok "Found: $GamePath"

    $pluginDir = Join-Path $GamePath "BepInEx\plugins\$ModName"

    if (Get-Process -Name 'Volcanoids' -ErrorAction SilentlyContinue) {
        throw 'Volcanoids is running. Close the game, then run setup again.'
    }

    if ($Uninstall) {
        Write-Step 'Uninstalling'
        if (Test-Path $pluginDir) { Remove-Item -Recurse -Force $pluginDir; Write-Ok 'Mod removed.' }
        else { Write-Ok 'Mod was not installed.' }
        Remove-FirewallRule
        Write-Host '    BepInEx itself was left in place. To remove it too, delete these from the game folder:'
        Write-Host '    BepInEx\, winhttp.dll, doorstop_config.ini, .doorstop_version, changelog.txt'
        return
    }

    # ---------------------------------------------------------------- BepInEx
    Write-Step 'Installing BepInEx (mod loader)'
    Install-BepInEx $GamePath
    Set-BepInExConsole $GamePath

    # ---------------------------------------------------------------- build
    Write-Step 'Building the mod against your copy of the game'
    $outDll = Join-Path $pluginDir "$ModName.dll"
    $tmpDll = Join-Path $env:TEMP "$ModName.build.dll"
    $built = $false
    try {
        & (Join-Path $PSScriptRoot 'build.ps1') -ManagedDir (Join-Path $GamePath 'Volcanoids_Data\Managed') `
            -BepInExCore (Join-Path $GamePath 'BepInEx\core') -OutFile $tmpDll
        $built = $true
    } catch {
        Write-Warn2 "Build against the game failed: $($_.Exception.Message)"
    }
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
    if ($built) {
        Copy-Item -Force $tmpDll $outDll
        Remove-Item -Force $tmpDll -ErrorAction SilentlyContinue
        Write-Ok "Installed $outDll"
    } else {
        $prebuilt = Join-Path $Root "prebuilt\$ModName.dll"
        if (-not (Test-Path $prebuilt)) { throw 'Build failed and no prebuilt DLL is available.' }
        Copy-Item -Force $prebuilt $outDll
        Write-Warn2 "Installed the PREBUILT dll instead (may not match your game version exactly)."
    }

    # ---------------------------------------------------------------- firewall
    Write-Step 'Windows Firewall (needed only on the computer that HOSTS)'
    $ans = Read-Host "    Allow incoming connections on TCP port $DefaultPort? This asks for admin. [Y/n]"
    if ($ans -notmatch '^[nN]') { Add-FirewallRule $DefaultPort } else { Write-Warn2 'Skipped.' }

    # ---------------------------------------------------------------- done
    Write-Step 'Done!'
    Write-Host @"

    HOW TO PLAY (both players install the mod with this setup):

    1. Both install Tailscale (https://tailscale.com) and log in to the SAME tailnet
       (or use ZeroTier / Radmin VPN / same LAN / port-forward TCP $DefaultPort).
    2. HOST: start Volcanoids from Steam, load your save as a co-op/multiplayer game.
       The direct port opens automatically. Press F8 to see the address to share
       (the 100.x.x.x one is your Tailscale IP).
    3. JOINER: start Volcanoids, stay on the MAIN MENU, press F8,
       type the host address (e.g. 100.101.102.103:$DefaultPort) and click Join.

    F8 = Direct Connect window    F9 = write a diagnostics report
    Logs:        $GamePath\BepInEx\LogOutput.log
    Diagnostics: $GamePath\BepInEx\VolcanoidsDirectConnect_diagnostics.txt
    Settings:    $GamePath\BepInEx\config\com.volcanoidsmp.directconnect.cfg (after first launch)
"@
}
catch {
    Write-Host ''
    Write-Host "SETUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}
if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
