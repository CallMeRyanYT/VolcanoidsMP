# Volcanoids Direct Connect

A multiplayer mod for **Volcanoids v1.33.506.0** that lets players join each other by **IP address** instead of through Steam's multiplayer. Connect over **Tailscale**, ZeroTier, Radmin VPN, Hamachi, a shared LAN, or a forwarded port.

It has two network modes:

| Mode | What it does |
|---|---|
| **Steam + direct IP** (default) | Steam multiplayer keeps working. Players who join by IP are added to the same game. |
| **Direct IP only** | Steam's networking isn't used at all. Only direct IP connections are used. Useful for testing that direct play works on its own. |

The mod only adds a network transport. It doesn't change game ownership, Steam login or DRM.

## Install (every player)

1. Close Volcanoids if it's running.
2. Double-click **`setup.bat`** and answer its questions. Pressing **ENTER** keeps the suggested answer.

You don't need to download or install anything yourself. Setup does all of this:

| Step | What happens |
|---|---|
| 1. Game folder | Shows the Volcanoids folder it found, or the one you used last time. Press **ENTER** to accept it, or type, paste or drag in a different folder. |
| 2. BepInEx | Downloads and installs the [BepInEx 5.4.23.6](https://github.com/BepInEx/BepInEx) mod loader, after checking the download's SHA-256. If BepInEx is already there, it's reused or repaired. |
| 3. Mod | Checks `version.txt` in the game folder, then installs the mod. The mod is already built against v1.33.506.0, so no build tools are needed. |
| 4. Settings | Asks for the port (default 27777) and the network mode: **1** = Steam + direct IP, **2** = direct IP only. Running setup again keeps your answers. |
| 5. Firewall | Optionally allows the port through Windows Firewall. Only the host needs this. Windows will ask for admin permission. |
| 6. Check | Confirms every piece is in place, prints `ALL DONE - Volcanoids is ready to play`, and offers to start the game. |

### Typing the game folder

When setup asks for the folder, you can give it any of these:

- the game folder itself
- the path to `Volcanoids.exe` or to the `Volcanoids_Data` folder
- a Steam library folder, e.g. `D:\SteamLibrary`; setup finds the game inside it

Quotes around the path are fine. You can also drag the folder into the setup window. Setup remembers your answer in `gamepath.txt` next to `setup.bat`, which stays on your PC.

### Other ways to run it

- `setup.bat "D:\Games\Volcanoids"` uses that folder without asking. Dragging the game folder onto `setup.bat` does the same.
- `setup.bat uninstall` removes the mod and its firewall rule. BepInEx stays installed; setup lists the files to delete if you want it gone too.

When the game starts with the mod, a BepInEx console window opens next to it. That's normal: it shows the mod's log.

## Play

| Who | What to do |
|---|---|
| Both | Join the same Tailscale tailnet (or the same ZeroTier/Radmin network, LAN, etc.). |
| Host | Start Volcanoids and load or create a save with **Multiplayer turned ON** in the save's settings. The mod then accepts players on the port. Press **F8** to see which address to share (`100.x.x.x` is your Tailscale IP). |
| Joiner | Start Volcanoids, stay on the **main menu**, press **F8**, type `HOST-IP:27777` and click **Join**. If the host set a game password, type it in the window too. |

- **F8** opens and closes the Direct Connect window. On the main menu it opens by itself.
- **F9** writes a diagnostics report.

The game's own checks still apply to direct joins:

- **Password:** the password is the one set in the game's multiplayer settings.
- **Version:** the joiner's game version has to match the host's.
- **Full:** a game that's already full rejects new players.
- **Kicks:** kicked players are disconnected the normal way.

**Testing on one PC:** start the game twice. Host in one copy, then join `127.0.0.1:27777` from the other.

## Settings

Setup writes the settings file to `<game>\BepInEx\config\com.volcanoidsmp.directconnect.cfg`. To change it, run `setup.bat` again or edit the file in Notepad while the game is closed.

| Setting | Default | Meaning |
|---|---|---|
| `Port` | 27777 | TCP port the host listens on |
| `UseSteamTransport` | true | `false` = direct IP only. Steam multiplayer isn't used at all |
| `AcceptDirectPlayers` | true | Whether hosting also accepts direct IP players |
| `TimeoutSeconds` | 30 | Drops a direct connection after this long with no data |
| `ToggleKey` / `DiagnosticsKey` | F8 / F9 | Hotkeys |

## How it works

Volcanoids v1.33 uses its own fork of Unity's networking library. All game traffic goes through a pluggable `NetworkHost` chosen via `Transport.Current`; normally that's the game's `SteamSocketsNetworkTransport`.

The mod installs its own transport on top:

- **Every host the game creates becomes a `DirectHost`.** It contains the game's Steam host (unless you chose direct-only) plus direct TCP connections.
- **The game can't tell them apart.** `DirectHost` gives out its own connection IDs and maps each one to a Steam or a direct connection, so the two can never collide.
- **Hosting:** when you start a multiplayer game, the server's host also listens on the TCP port. Joiners go through the game's normal connect, player and spawn code.
- **Joining:** the F8 window first asks the host which world it's running. It then starts the game's own join (`StartMultiplayer.Connect`) with a placeholder server ID that `DirectHost` recognises as "this IP:port".
- **Player identity:** a direct player is identified as `steamid:<their id>` when Steam is running, just like a Steam join, so per-player save data stays consistent. Without Steam, they're identified as `direct:<name>@<ip>`.
- **Connection health:** a ping every 2 s measures latency and detects dead connections. When a player is kicked or rejected, the reason is sent before the connection closes, so they see the right message.

## Testing status / reporting problems

What has been verified:

- **Build:** the mod builds against the real v1.33.506.0 DLLs, and every assembly it references exists in the game folder.
- **Connection:** the TCP layer passed a stress test (5,000 messages, 1.4 GB, intact and in order). The handshake and the send-goodbye-then-close sequence were tested too.
- **Setup:** setup was tested end to end.

**Joining an actual game hasn't been tested yet.** If something goes wrong:

1. Reproduce the problem, then press **F9** on both computers.
2. Send these files from the game folder:
   - `BepInEx\VolcanoidsDirectConnect_diagnostics.txt`
   - `BepInEx\LogOutput.log`

## Building (developers)

The plugin uses `ReadOnlySpan<byte>`, like the game's networking API does, so it needs the .NET SDK to build. BepInEx must already be installed in the game folder.

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Volcanoids" -UpdatePrebuilt
```

`-UpdatePrebuilt` refreshes `prebuilt\VolcanoidsDirectConnect.dll`, which is the file setup installs. To have setup compile against the chosen game folder instead, run `powershell -ExecutionPolicy Bypass -File tools\setup.ps1 -Build`.
