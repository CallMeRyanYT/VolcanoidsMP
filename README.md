# Volcanoids Direct Connect

A Volcanoids co-op mod (targets game **v1.33.506.0**) that lets players join each other by **IP address** instead of through Steam's networking. You can use **Tailscale**, ZeroTier, Radmin VPN, Hamachi, a shared LAN, or a forwarded port.

> Steam still launches the game, because you own it there. The mod only replaces how players **connect** to each other. It doesn't touch game ownership or DRM.

## Install (both players)

1. Install Volcanoids from Steam and launch it once, then close it.
2. Run **`setup.bat`** from this folder. It will:
   - find your Volcanoids install (or ask you for the folder),
   - install [BepInEx 5.4.23.6](https://github.com/BepInEx/BepInEx) (the mod loader; it checks the download's SHA-256),
   - compile the mod against **your** copy of the game and copy it to `BepInEx\plugins\VolcanoidsDirectConnect\`,
   - optionally add a Windows Firewall rule for TCP port 27777 (only the host needs it).

Other commands:

- `setup.bat "D:\SteamLibrary\steamapps\common\Volcanoids"` installs into a specific folder.
- `setup.bat uninstall` removes the mod.

## Play

| Who | What to do |
|---|---|
| Both | Join the same Tailscale tailnet (or the same ZeroTier/Radmin network, LAN, etc.) |
| Host | Start the game and load your save as a **co-op / multiplayer** game. The direct port opens on its own. Press **F8** to see which address to share (`100.x.x.x` is your Tailscale IP). |
| Joiner | Start the game, stay on the **main menu**, press **F8**, enter `HOST-IP:27777` and click **Join**. |

- **F8** opens and closes the Direct Connect window.
- **F9** writes a diagnostics report.

## Settings

The settings file is `<game>\BepInEx\config\com.volcanoidsmp.directconnect.cfg`. It appears after the first launch.

| Setting | Default | Meaning |
|---|---|---|
| `Port` | 27777 | TCP port the host listens on |
| `Password` | *(empty)* | Optional. Must match on both sides |
| `AutoOpenWhenHosting` | true | Opens the port as soon as you host |
| `MaxDirectPlayers` | 3 | Maximum number of direct-IP joiners |
| `RequireSameGameVersion` | true | Rejects joiners whose game version is different |
| `ChannelCount` | 16 | Raise this if the log says a channel id is out of range |
| `ConnectionAddressMode` | Auto | What the game sees as the joiner's "address": `Auto`, `SteamId` or `IpPort` |

## How it works

Volcanoids runs on Unity's UNet HLAPI. The mod adds its own `NetworkConnection` subclass that sends the HLAPI's bytes over a framed TCP socket:

- **Host:** accepted sockets are handed to `NetworkServer.AddExternalConnection`. That's the same hook the game's Steam P2P connections use.
- **Joiner:** the socket is wrapped in a `NetworkClient` and given to the game's `NetworkManager` through `UseExternalClient`.

**Works alongside the built-in Steam multiplayer:** the game's Steam networking is left in place. A host can have Steam friends and direct-IP players in the same session. Direct players get connection IDs starting at 1000, so they never take the slots the game hands out to Steam players. When the host kicks someone or ends the session, direct players are disconnected the same way Steam players are.

The game's own message handlers, spawning and world sync run unchanged. Only the transport underneath is different. Socket input/output runs on background threads, and the game thread only queues and drains messages.

## Testing status / reporting problems

The mod was compiled and its TCP transport was tested on its own. It has **not** been run inside Volcanoids yet. If something doesn't work:

1. Reproduce the problem, then press **F9** on both computers.
2. Send these files:
   - `<game>\BepInEx\VolcanoidsDirectConnect_diagnostics.txt`
   - `<game>\BepInEx\LogOutput.log`

The diagnostics report lists the game's networking classes and live connection state. With it, the mod can be adapted to how Volcanoids sets up its sessions.

## Building by hand

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -ManagedDir "<game>\Volcanoids_Data\Managed" -BepInExCore "<game>\BepInEx\core" -OutFile out\VolcanoidsDirectConnect.dll
```

The source is plain C# 5, so it builds with the `csc.exe` that comes with Windows. No SDK is needed. `prebuilt\VolcanoidsDirectConnect.dll` is a fallback build made against Unity 2019.4 reference assemblies. Setup only uses it if compiling against your game fails.
