# Volcanoids Direct Connect

A Volcanoids co-op mod (targets game **v1.33.506.0**) that lets players join each other by **IP address** instead of through Steam's networking. You can use **Tailscale**, ZeroTier, Radmin VPN, Hamachi, a shared LAN, or a forwarded port.

> Steam still launches the game, because you own it there. The mod only replaces how players **connect** to each other. It doesn't touch game ownership or DRM.

## Install (both players)

1. Install Volcanoids from Steam. Close the game if it's running.
2. Double-click **`setup.bat`** and answer its questions. Pressing **ENTER** keeps the suggested answer.

You don't need to download or install anything yourself. Setup does all of this:

| Step | What happens |
|---|---|
| 1. Game folder | Shows the Volcanoids folder it found (or the one you used last time). Press **ENTER** to accept it, or type, paste or drag in a different folder. |
| 2. BepInEx | Downloads and installs the [BepInEx 5.4.23.6](https://github.com/BepInEx/BepInEx) mod loader into the game, after checking the download's SHA-256. If BepInEx is already there, it's reused. If its loader was switched off, setup switches it back on. |
| 3. Mod | Compiles the mod against **your** copy of the game and installs it to `BepInEx\plugins\VolcanoidsDirectConnect\`. |
| 4. Settings | Asks for the port (default 27777), an optional password and the name the host sees, then writes the settings file. Running setup again keeps your existing answers. |
| 5. Firewall | Optionally allows the chosen port through Windows Firewall. Only the host needs this. Windows will ask for admin permission. |
| 6. Check | Confirms every piece is in place, prints `ALL DONE - Volcanoids is ready to play`, and offers to start the game through Steam. |

### Typing the game folder

When setup asks for the folder, you can give it any of these:

- the game folder itself, e.g. `D:\SteamLibrary\steamapps\common\Volcanoids`
- the path to `Volcanoids.exe` or to the `Volcanoids_Data` folder
- a Steam library folder, e.g. `D:\SteamLibrary`; setup finds the game inside it

Quotes around the path are fine. You can also drag the folder into the setup window. To find the folder: in Steam, right-click Volcanoids, choose **Manage → Browse local files**, and copy the address bar.

Setup remembers your answer in `gamepath.txt` next to `setup.bat`, so next time you can just press ENTER.

### Other ways to run it

- `setup.bat "D:\SteamLibrary\steamapps\common\Volcanoids"` uses that folder without asking. Dragging the game folder onto `setup.bat` does the same.
- `setup.bat uninstall` removes the mod and its firewall rule. BepInEx stays installed; setup lists the files to delete if you want it gone too.

When the game starts with the mod, a BepInEx console window opens next to it. That's normal: it shows the mod's log.

## Play

| Who | What to do |
|---|---|
| Both | Join the same Tailscale tailnet (or the same ZeroTier/Radmin network, LAN, etc.) |
| Host | Start the game and load your save as a **co-op / multiplayer** game. The direct port opens on its own. Press **F8** to see which address to share (`100.x.x.x` is your Tailscale IP). |
| Joiner | Start the game, stay on the **main menu**, press **F8**, enter `HOST-IP:27777` and click **Join**. |

- **F8** opens and closes the Direct Connect window.
- **F9** writes a diagnostics report.

## Settings

Setup creates the settings file at `<game>\BepInEx\config\com.volcanoidsmp.directconnect.cfg`. To change the port, password or name later, either run `setup.bat` again or edit the file in Notepad while the game is closed.

| Setting | Default | Meaning |
|---|---|---|
| `Port` | 27777 | TCP port the host listens on |
| `Password` | *(empty)* | Optional. Must match on both sides |
| `PlayerName` | your Windows user name | The name the host sees in their log |
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
