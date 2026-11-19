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
$ModGuid      = 'com.volcanoidsmp.directconnect'
$Root         = Split-Path -Parent $PSScriptRoot
$SavedPathFile = Join-Path $Root 'gamepath.txt'   # remembers the folder you picked (not committed)

# BepInEx config strings escape backslashes and quotes.
function ConvertTo-CfgString([string]$s) { if ($null -eq $s) { return '' }; return $s.Replace('\', '\\').Replace('"', '\"') }
function ConvertFrom-CfgString([string]$s) { if ($null -eq $s) { return '' }; return $s.Replace('\"', '"').Replace('\\', '\') }
function Set-CfgValue([string]$txt, [string]$key, [string]$value) {
    $line = "$key = $value"
    return [regex]::Replace($txt, "(?m)^$([regex]::Escape($key)) = [^\r\n]*", [System.Text.RegularExpressions.MatchEvaluator] { param($m) $line })
}

function Write-Step($msg) { Write-Host ''; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "    $msg" -ForegroundColor Yellow }

function Test-GameDir([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    # Plain .NET calls: Join-Path/Test-Path throw for drives that don't exist (old Steam libraries, USB disks).
    try {
        return [System.IO.File]::Exists([System.IO.Path]::Combine($dir, 'Volcanoids.exe')) -and
               [System.IO.Directory]::Exists([System.IO.Path]::Combine($dir, 'Volcanoids_Data', 'Managed'))
    } catch { return $false }
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
        if (-not [System.IO.Directory]::Exists($sr)) { continue }
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
        $dir = [System.IO.Path]::Combine($lib, 'steamapps', 'common', 'Volcanoids')
        if (Test-GameDir $dir) { return [System.IO.Path]::GetFullPath($dir) }
    }
    return $null
}

# Turns whatever the user typed/pasted/dragged into the real game folder, if possible.
# Accepts: the game folder, Volcanoids.exe, Volcanoids_Data, a Steam library folder,
# or a steamapps / steamapps\common folder.
function Resolve-GameDir([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    $p = $text.Trim().Trim('"').Trim("'").Trim()
    $p = [Environment]::ExpandEnvironmentVariables($p)
    if ($p.Length -eq 2 -and $p[1] -eq ':') { $p += '\' }
    if ([string]::IsNullOrWhiteSpace($p)) { return $null }
    try { $p = [System.IO.Path]::GetFullPath($p) } catch { return $null }

    try {
        if ([System.IO.File]::Exists($p)) { $p = [System.IO.Path]::GetDirectoryName($p) }   # e.g. ...\Volcanoids.exe
        $p = $p.TrimEnd([char]'\', [char]'/')
        if ($p.Length -eq 2 -and $p[1] -eq ':') { $p += '\' }
        if ([System.IO.Path]::GetFileName($p) -ieq 'Volcanoids_Data') { $p = [System.IO.Path]::GetDirectoryName($p) }
        $candidates = @(
            $p,
            [System.IO.Path]::Combine($p, 'Volcanoids'),
            [System.IO.Path]::Combine($p, 'common', 'Volcanoids'),
            [System.IO.Path]::Combine($p, 'steamapps', 'common', 'Volcanoids')
        )
        foreach ($c in $candidates) {
            if (Test-GameDir $c) { return [System.IO.Path]::GetFullPath($c).TrimEnd([char]'\') }
        }
    } catch { }
    return $null
}

function Read-SavedGamePath {
    try { if (Test-Path $SavedPathFile) { return (Get-Content -LiteralPath $SavedPathFile -Raw).Trim() } } catch { }
    return $null
}

function Save-GamePath([string]$dir) {
    try { Set-Content -LiteralPath $SavedPathFile -Value $dir -Encoding UTF8 } catch { }
}

function Select-GameDir([string]$fromArgs) {
    # 1. A folder given on the command line (or dragged onto setup.bat) wins.
    if (-not [string]::IsNullOrWhiteSpace($fromArgs)) {
        $dir = Resolve-GameDir $fromArgs
        if ($dir) { return $dir }
        Write-Warn2 "That folder doesn't contain Volcanoids: $fromArgs"
    }

    # 2. Suggest the folder used last time, else the one found in Steam.
    $suggest = Resolve-GameDir (Read-SavedGamePath)
    $how = 'used last time'
    if (-not $suggest) { $suggest = Find-Game; $how = 'found in your Steam library' }

    $emptyAnswers = 0
    while ($true) {
        Write-Host ''
        if ($suggest) {
            Write-Host "    Game folder ($how):" -NoNewline
            Write-Host " $suggest" -ForegroundColor White
            Write-Host '    Press ENTER to use it, or type/paste a different folder.'
        } else {
            Write-Warn2 'Could not find Volcanoids automatically.'
            Write-Host  '    Type or paste the folder Volcanoids is installed in.'
            Write-Host  '    Tip: Steam > right-click Volcanoids > Manage > Browse local files, then copy the address bar.'
            Write-Host  '    You can also drag the folder into this window.'
        }
        Write-Host '    (Type Q to quit.)'
        $answer = Read-Host '    Game folder'
        if ($answer -match '^\s*[qQ]\s*$') { throw 'Setup cancelled.' }
        if ([string]::IsNullOrWhiteSpace($answer)) {
            if ($suggest) { return $suggest }
            # Stops an endless loop when there's no keyboard (e.g. input piped in) or the user keeps pressing ENTER.
            if (++$emptyAnswers -ge 3) { throw 'No game folder given. Run setup.bat again and type the folder that contains Volcanoids.exe.' }
            continue
        }
        $emptyAnswers = 0
        $dir = Resolve-GameDir $answer
        if ($dir) { return $dir }
        Write-Warn2 "Volcanoids.exe was not found in: $($answer.Trim().Trim('`"'))"
        Write-Warn2 'Pick the folder that contains Volcanoids.exe and the Volcanoids_Data folder.'
    }
}

function Get-SteamAppId([string]$game) {
    # steamapps\common\Volcanoids -> steamapps\appmanifest_<id>.acf
    try {
        $steamapps = Split-Path -Parent (Split-Path -Parent $game)
        foreach ($acf in Get-ChildItem -LiteralPath $steamapps -Filter 'appmanifest_*.acf' -ErrorAction Stop) {
            $txt = Get-Content -LiteralPath $acf.FullName -Raw
            if ($txt -match '"installdir"\s+"Volcanoids"' -and $txt -match '"appid"\s+"(\d+)"') { return $Matches[1] }
        }
    } catch { }
    return $null
}

# Final check that everything the game needs to load the mod is in place.
function Test-Ready([string]$game, [string]$pluginDll) {
    $ok = $true
    $checks = @(
        @('BepInEx loader (winhttp.dll)',        (Test-Path (Join-Path $game 'winhttp.dll'))),
        @('BepInEx loader config',               (Test-Path (Join-Path $game 'doorstop_config.ini'))),
        @('BepInEx core',                        (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.dll'))),
        @('Direct Connect mod',                  (Test-Path $pluginDll)),
        @('Direct Connect settings',             (Test-Path (Join-Path $game "BepInEx\config\$ModGuid.cfg")))
    )
    $ini = Join-Path $game 'doorstop_config.ini'
    if (Test-Path $ini) {
        $enabled = -not ((Get-Content -LiteralPath $ini -Raw) -match '(?mi)^\s*enabled\s*=\s*false')
        $checks += ,@('BepInEx loader enabled', $enabled)
    }
    foreach ($c in $checks) {
        if ($c[1]) { Write-Host "    [OK]      $($c[0])" -ForegroundColor Green }
        else       { Write-Host "    [MISSING] $($c[0])" -ForegroundColor Red; $ok = $false }
    }
    return $ok
}

function Enable-Doorstop([string]$game) {
    $ini = Join-Path $game 'doorstop_config.ini'
    if (-not (Test-Path $ini)) { return }
    $txt = Get-Content -LiteralPath $ini -Raw
    if ($txt -match '(?mi)^\s*enabled\s*=\s*false') {
        $txt = [regex]::Replace($txt, '(?mi)^(\s*enabled\s*=\s*)false', '${1}true')
        Set-Content -LiteralPath $ini -Value $txt -Encoding ASCII
        Write-Ok 'Re-enabled the BepInEx loader (it was switched off).'
    }
}

# Writes the mod's settings file now, so the game is ready on first launch and
# the user can set port/password before ever starting it. Existing values are kept.
function Write-ModConfig([string]$game, [int]$port, [string]$password, [string]$playerName) {
    $cfgDir = Join-Path $game 'BepInEx\config'
    New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
    $cfg = Join-Path $cfgDir "$ModGuid.cfg"
    if (Test-Path $cfg) {
        $txt = Get-Content -LiteralPath $cfg -Raw
        $txt = Set-CfgValue $txt 'Port' ([string]$port)
        $txt = Set-CfgValue $txt 'Password' (ConvertTo-CfgString $password)
        $txt = Set-CfgValue $txt 'PlayerName' (ConvertTo-CfgString $playerName)
        Set-Content -LiteralPath $cfg -Value $txt -Encoding UTF8 -NoNewline
        Write-Ok "Updated settings: $cfg"
        return
    }
    $content = @"
## Settings file was created by Volcanoids Direct Connect setup
## You can edit it with Notepad. The game must be closed while you edit.

[Advanced]

## Number of UNet channels on direct connections. Raise it if the log says a channel id is out of range.
# Setting type: Int32
# Default value: 16
ChannelCount = 16

## What the game sees as a direct player's connection address. Auto = the joiner's SteamID64 when available, otherwise ip:port. SteamId = always SteamID64 (or 0). IpPort = always ip:port.
# Setting type: String
# Default value: Auto
ConnectionAddressMode = Auto

## Log every connection event in detail.
# Setting type: Boolean
# Default value: false
VerboseLogging = false

[Network]

## TCP port the host listens on. Joiners must use the same port. Open/forward this port if you are not using a VPN like Tailscale.
# Setting type: Int32
# Default value: 27777
Port = $port

## Automatically accept direct IP players as soon as you are hosting a game.
# Setting type: Boolean
# Default value: true
AutoOpenWhenHosting = true

## Optional password. Host and joiners must use the same value. Empty = no password.
# Setting type: String
# Default value:
Password = $(ConvertTo-CfgString $password)

## Max number of players that may join through direct IP at the same time.
# Setting type: Int32
# Default value: 3
MaxDirectPlayers = 3

## Reject joiners whose game version differs from the host's (strongly recommended).
# Setting type: Boolean
# Default value: true
RequireSameGameVersion = true

## Last address you joined (filled in automatically).
# Setting type: String
# Default value: 100.64.0.1:27777
LastAddress = 100.64.0.1:$port

## Name shown to the host in logs. Empty = Windows user name.
# Setting type: String
# Default value:
PlayerName = $(ConvertTo-CfgString $playerName)

[UI]

## Key that opens/closes the Direct Connect window.
# Setting type: KeyCode
# Default value: F8
ToggleKey = F8

## Key that writes a networking diagnostics report (send this file to whoever maintains the mod).
# Setting type: KeyCode
# Default value: F9
DiagnosticsKey = F9

## Open the Direct Connect window automatically on the main menu.
# Setting type: Boolean
# Default value: true
ShowWindowOnMainMenu = true

"@
    Set-Content -LiteralPath $cfg -Value $content -Encoding UTF8
    Write-Ok "Created settings: $cfg"
}

function Read-Port([int]$current) {
    while ($true) {
        $a = Read-Host "    Port for direct connections [ENTER = $current]"
        if ([string]::IsNullOrWhiteSpace($a)) { return $current }
        $n = 0
        if ([int]::TryParse($a.Trim(), [ref]$n) -and $n -ge 1024 -and $n -le 65535) { return $n }
        Write-Warn2 'Enter a number between 1024 and 65535.'
    }
}

function Get-ExistingSetting([string]$game, [string]$key) {
    $cfg = Join-Path $game "BepInEx\config\$ModGuid.cfg"
    if (-not (Test-Path $cfg)) { return $null }
    $m = [regex]::Match((Get-Content -LiteralPath $cfg -Raw), "(?m)^$([regex]::Escape($key)) = ([^\r\n]*)")
    if ($m.Success) { return (ConvertFrom-CfgString $m.Groups[1].Value.Trim()) }
    return $null
}

function Install-BepInEx([string]$game) {
    $core = Join-Path $game 'BepInEx\core\BepInEx.dll'
    if ((Test-Path $core) -and (Test-Path (Join-Path $game 'winhttp.dll')) -and (Test-Path (Join-Path $game 'doorstop_config.ini'))) {
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

    # ---------------------------------------------------------------- 1. game folder
    Write-Step 'Step 1/6 - Where is Volcanoids installed?'
    $GamePath = Select-GameDir $GamePath
    Save-GamePath $GamePath
    Write-Ok "Using: $GamePath"

    $pluginDir = Join-Path $GamePath "BepInEx\plugins\$ModName"
    $outDll = Join-Path $pluginDir "$ModName.dll"

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

    # ---------------------------------------------------------------- 2. BepInEx
    Write-Step 'Step 2/6 - Installing BepInEx (the mod loader)'
    Install-BepInEx $GamePath
    Enable-Doorstop $GamePath
    Set-BepInExConsole $GamePath

    # ---------------------------------------------------------------- 3. build + install mod
    Write-Step 'Step 3/6 - Building the mod against your copy of the game'
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

    # ---------------------------------------------------------------- 4. settings
    Write-Step 'Step 4/6 - Settings (just press ENTER to keep the defaults)'
    $port = $DefaultPort
    $existingPort = Get-ExistingSetting $GamePath 'Port'
    if ($existingPort -match '^\d+$') { $port = [int]$existingPort }
    $port = Read-Port $port

    $password = Get-ExistingSetting $GamePath 'Password'
    if ($null -eq $password) { $password = '' }
    $pwState = 'none'
    if ($password.Length -gt 0) { $pwState = 'one is set' }
    Write-Host "    Optional password - host and friends must type the same one (currently: $pwState)."
    $pwAnswer = Read-Host '    Password [ENTER = keep, - = remove]'
    if ($pwAnswer -eq '-') { $password = '' }
    elseif (-not [string]::IsNullOrEmpty($pwAnswer)) { $password = $pwAnswer }

    $playerName = Get-ExistingSetting $GamePath 'PlayerName'
    if ([string]::IsNullOrEmpty($playerName)) { $playerName = $env:USERNAME }
    $nameAnswer = Read-Host "    Your name shown to the host [ENTER = $playerName]"
    if (-not [string]::IsNullOrWhiteSpace($nameAnswer)) { $playerName = $nameAnswer.Trim() }

    Write-ModConfig $GamePath $port $password $playerName

    # ---------------------------------------------------------------- 5. firewall
    Write-Step 'Step 5/6 - Windows Firewall (only needed on the computer that HOSTS)'
    $ans = Read-Host "    Allow incoming connections on TCP port $port? This asks for admin. [Y/n]"
    if ($ans -notmatch '^\s*[nN]') { Add-FirewallRule $port } else { Write-Warn2 'Skipped.' }

    # ---------------------------------------------------------------- 6. verify
    Write-Step 'Step 6/6 - Checking that the game is ready'
    if (-not (Test-Ready $GamePath $outDll)) {
        throw 'Something is missing (see the list above). Run setup.bat again; if it keeps failing, send a screenshot of this window.'
    }

    Write-Host ''
    Write-Host '    ALL DONE - Volcanoids is ready to play with Direct Connect!' -ForegroundColor Green
    Write-Host @"

    HOW TO PLAY (every player runs this setup once):

    1. Both install Tailscale (https://tailscale.com) and log in to the SAME tailnet
       (or use ZeroTier / Radmin VPN / same LAN / port-forward TCP $port).
    2. HOST: start Volcanoids from Steam as usual and load your save as a co-op /
       multiplayer game. Press F8 to see the address to give your friend
       (the 100.x.x.x one is your Tailscale IP).
    3. FRIEND: start Volcanoids, stay on the MAIN MENU, press F8,
       type the host address (e.g. 100.101.102.103:$port) and click Join.

    F8 = Direct Connect window    F9 = write a diagnostics report
    Settings:    $GamePath\BepInEx\config\$ModGuid.cfg
    Logs:        $GamePath\BepInEx\LogOutput.log
    Diagnostics: $GamePath\BepInEx\VolcanoidsDirectConnect_diagnostics.txt
    A BepInEx console window opens next to the game - that's normal, it shows the mod's log.
"@

    $launch = Read-Host '    Start Volcanoids now? [y/N]'
    if ($launch -match '^\s*[yY]') {
        $appId = Get-SteamAppId $GamePath
        if ($appId) { Start-Process "steam://rungameid/$appId" }
        else { Start-Process -FilePath (Join-Path $GamePath 'Volcanoids.exe') -WorkingDirectory $GamePath }
        Write-Ok 'Launching... (the first start with BepInEx can take a little longer)'
    }
}
catch {
    Write-Host ''
    Write-Host "SETUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}
if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
