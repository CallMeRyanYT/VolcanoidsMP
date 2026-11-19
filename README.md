# Volcanoids Direct Connect

A multiplayer mod for **Volcanoids v1.33.506.0** that lets players join each other by **IP address** instead of through Steam's multiplayer. Connect over **Tailscale**, ZeroTier, Radmin VPN, Hamachi, a shared LAN, or a forwarded port.

It has two network modes:

| Mode | What it does |
|---|---|
| **Steam + direct IP** (default) | Steam multiplayer keeps working. Players who join by IP are added to the same game. |
| **Direct IP only** | Steam's networking isn't used at all. Only direct IP connections are used. Useful for testing that direct play works on its own. |

The mod only adds a network transport. It doesn't change game ownership, Steam login or DRM.

## Contents

1. [Quick start](#quick-start)
2. [Tutorial 1: Install the mod](#tutorial-1-install-the-mod)
3. [Tutorial 2: Set up Tailscale](#tutorial-2-set-up-tailscale)
4. [Tutorial 3: Host a game](#tutorial-3-host-a-game)
5. [Tutorial 4: Join a friend](#tutorial-4-join-a-friend)
6. [Tutorial 5: Test on one PC](#tutorial-5-test-on-one-pc)
7. [Tutorial 6: Switch between Steam + direct and direct only](#tutorial-6-switch-between-steam--direct-and-direct-only)
8. [Other ways to connect (no Tailscale)](#other-ways-to-connect-no-tailscale)
9. [The Direct Connect window (F8)](#the-direct-connect-window-f8)
10. [Settings](#settings)
11. [Troubleshooting](#troubleshooting)
12. [Updating and uninstalling](#updating-and-uninstalling)
13. [How it works](#how-it-works)
14. [Testing status / reporting problems](#testing-status--reporting-problems)
15. [Building (developers)](#building-developers)

---

## Quick start

1. **Everyone:** run `setup.bat` and press ENTER at each question.
2. **Everyone:** install [Tailscale](https://tailscale.com/download) and sign in. The host then shares their computer with each friend (see [Tutorial 2](#tutorial-2-set-up-tailscale)).
3. **Host:** start Volcanoids and load a save with **Multiplayer turned on**. Press **F8** and copy your Tailscale address (`100.x.x.x:27777`).
4. **Friend:** start Volcanoids. On the main menu, press **F8**, paste the address and click **Join**.

---

## Tutorial 1: Install the mod

Every player does this once, and again after each mod update.

1. **Download the mod.** On the GitHub page, click **Code → Download ZIP**, then extract the ZIP anywhere (for example `Documents\VolcanoidsMP`). If you use git, you can clone the repository instead.
2. **Close Volcanoids** if it's running.
3. **Double-click `setup.bat`.** If Windows shows "Windows protected your PC", click **More info → Run anyway**. The script is plain text, so you can open it in Notepad to check it first.
4. **Step 1/6, game folder.** Setup shows the folder it found:
   ```
   Game folder (found in your Steam library): D:\SteamLibrary\steamapps\common\Volcanoids
   Press ENTER to use it, or type/paste a different folder.
   ```
   - If it's right, press **ENTER**.
   - If not, paste the right folder. To find it: in Steam, right-click **Volcanoids → Manage → Browse local files**, then copy the address bar. You can also drag the folder into the window, or paste the path to `Volcanoids.exe`.
5. **Step 2/6, BepInEx.** Setup downloads and installs the mod loader on its own. Nothing to do.
6. **Step 3/6, mod.** Setup checks your game version (it should say `1.33.506.0 - matches the mod`) and installs the mod.
7. **Step 4/6, settings.**
   - **Port:** press **ENTER** to keep `27777`. If you change it, everyone has to use the new port.
   - **Network mode:** press **ENTER**, or type **1** (Steam + direct IP) or **2** (direct IP only). See [Tutorial 6](#tutorial-6-switch-between-steam--direct-and-direct-only).
8. **Step 5/6, firewall.** On the computer that will **host**, press **ENTER** (yes) and click **Yes** on the Windows admin prompt. This allows the port through Windows Firewall. Joiners can type `n`.
9. **Step 6/6, check.** You should see six `[OK]` lines and `ALL DONE - Volcanoids is ready to play`.
10. Setup offers to start the game. When the game starts, a black **BepInEx console** window opens next to it. That's normal: it shows the mod's log. Look for this line:
    ```
    [Info   :Volcanoids Direct Connect] Volcanoids Direct Connect 0.2.0 loaded (game 1.33.506.0)
    ```

---

## Tutorial 2: Set up Tailscale

Tailscale puts your computers on a private network over the internet. Each computer gets an address like `100.101.102.103` that the others can reach directly, so you don't need to touch your router. It's free for personal use.

### 2.1 Install Tailscale (everyone)

1. Go to <https://tailscale.com/download> and download **Tailscale for Windows**.
2. Run the installer and click through it.
3. A Tailscale icon appears in the system tray (bottom right; click the **^** arrow if you don't see it). Click it, choose **Log in**, and sign in with a Google, Microsoft, GitHub or Apple account. Each player uses **their own** account.
4. When the browser says you're connected, close it. The tray icon should show you as connected.

### 2.2 Connect your friend to the host

Joiners need to reach the **host's** computer. Pick **one** of these two options.

**Option A: the host shares their computer (recommended).** Each friend keeps their own Tailscale account and can only see the host's computer, nothing else on your network.

1. **Host:** open the admin console at <https://login.tailscale.com/admin/machines>.
2. Find your gaming PC in the list, click the **⋯** menu on its row, and choose **Share…**.
3. Either:
   - **Share by email:** enter your friend's email address and click **Share**, or
   - **Copy invite link:** click **Copy share link** and send it to your friend privately, for example in a Discord DM. Treat the link like a password.
4. **Friend:** open the link or the email, sign in to Tailscale, and click **Accept**.
5. The host's computer now shows up in the friend's Tailscale (tray icon, **Network devices**).

The friend can connect **to** the host, which is all the mod needs. The host can't connect to the friend's computer.

**Option B: invite your friend into your tailnet.** Use this if you'll play together a lot or want everyone to be able to host.

1. **Host:** open <https://login.tailscale.com/admin/users> and click **Invite users**.
2. Send the invite and have your friend accept it. Their computers then join **your** network, and anyone on it can host.

### 2.3 Find the host's address

On the **host** computer, use any of these:

- **Easiest:** start Volcanoids and press **F8**. Under *Your addresses*, the line marked **Tailscale (share this)** is the one to use. Click **Copy** to copy it with the port.
- **Tailscale tray icon:** your device and its `100.x.x.x` address are shown in the menu. Clicking it copies the address.
- **PowerShell:** run `tailscale ip -4`.

Send the friend the address **with the port**, for example `100.101.102.103:27777`. If you used Option A, the friend can also use the host's full Tailscale name (like `gaming-pc.tail1234.ts.net:27777`) instead of the IP.

### 2.4 Test the connection (optional, but saves time)

1. **Host:** start Volcanoids and load a multiplayer save (see [Tutorial 3](#tutorial-3-host-a-game)), so the port is open.
2. **Friend:** open PowerShell and run (with the host's address):
   ```powershell
   Test-NetConnection 100.101.102.103 -Port 27777
   ```
3. Check the result:
   - `TcpTestSucceeded : True` means everything works, so go join.
   - `False` means it's blocked. See [Troubleshooting](#troubleshooting).

The host's BepInEx console will log `Ignored a connection ... (not Volcanoids Direct Connect)` for this test. That's expected.

> `ping` often fails even when everything works, because Windows blocks ping by default. `Test-NetConnection` is the reliable test.

---

## Tutorial 3: Host a game

1. Start Volcanoids. You can launch it from Steam, or start `Volcanoids.exe` directly if you're in direct-only mode.
2. **Load or create a save with Multiplayer turned on:**
   - **New game:** in the new game settings, turn **Multiplayer** on.
   - **Existing save:** open the save's settings and turn **Multiplayer** on.
   - **Optional password:** set one in the game's multiplayer/server settings. Your friends will type it in the F8 window.
3. Load into the world. The BepInEx console should show:
   ```
   Direct Connect is accepting players on TCP port 27777.
   Network host created (server, Steam: yes, direct IP: listening on 27777)
   ```
   (`Steam: no` in direct-only mode.)
4. Press **F8**. The **HOST** section should say *Accepting direct players on TCP 27777*. Copy your Tailscale address from *Your addresses* and send it to your friends.
5. When a friend joins, they're listed under the HOST section with their ping. The console shows `Direct player connecting: …`.

**Notes**

- If the **Multiplayer** toggle is greyed out, the save uses *local* mods. Remove them or start a new save.
- The game's own player limit, password and version checks apply to direct players too.
- To stop new direct players joining (people already in stay), press **F8 → Stop accepting**. **Accept direct players** turns it back on.
- Use the same mods (or none) as your friends. Direct joins don't compare mod lists the way the Steam lobby does.

---

## Tutorial 4: Join a friend

1. Make sure you've done [Tutorial 1](#tutorial-1-install-the-mod) and [Tutorial 2](#tutorial-2-set-up-tailscale), and that the host is already in their multiplayer world.
2. Start Volcanoids and stay on the **main menu**. The Direct Connect window opens by itself; if it doesn't, press **F8**.
3. In **Host IP:port**, type the host's address, for example `100.101.102.103:27777`. If you leave the port off, your own `Port` setting (27777 unless you changed it) is used.
4. If the host set a password, type it in **Game password**.
5. Click **Join**.
6. The status line shows `Contacting …`, then `Joining …`. The window closes and the game shows its loading screen while it loads the host's world.
7. You spawn in the host's world.

**If it fails,** the status line or the bar at the bottom of the screen says why:

| Message | Meaning |
|---|---|
| `Could not reach … timed out` | The host isn't in a multiplayer game, the address or port is wrong, or the host's firewall is blocking the port. |
| `Game version mismatch` | You and the host need the same Volcanoids version. |
| `Mod version mismatch` | Install the same version of this mod on both PCs. |
| `…'s game needs a password` | Type the host's game password and click **Join** again. |
| `The host isn't in a multiplayer game yet` | The host has to load into a save with Multiplayer on. |

After you've connected, a wrong password or a full game is reported by the game itself, and it sends you back to the main menu.

---

## Tutorial 5: Test on one PC

You can check that direct play works without a second computer.

1. Run `setup.bat` and choose mode **2** (direct IP only). That way you're testing the direct connection itself, not Steam.
2. Start Volcanoids. Host a multiplayer save (see [Tutorial 3](#tutorial-3-host-a-game)).
3. Start a **second** copy of Volcanoids, by running `Volcanoids.exe` from the game folder again.
4. In the second copy, on the main menu, press **F8**, enter `127.0.0.1:27777` and click **Join**.

Both copies share the same Windows user and settings, so the host sees them as the **same player**. That's fine for testing the connection, but the second copy uses the first one's player data. Use two PCs for a real test.

---

## Tutorial 6: Switch between Steam + direct and direct only

| Mode | `UseSteamTransport` | Use it for |
|---|---|---|
| **1: Steam + direct IP** | `true` | Normal play. Steam lobby friends and direct IP friends can be in the same game. |
| **2: Direct IP only** | `false` | Testing that play works with no Steam networking at all. Steam invites and the lobby browser can't connect in this mode. |

To switch:

- **Easiest:** close the game, run `setup.bat` again, press ENTER through the questions, and type **1** or **2** at *Network mode*.
- **Or by hand:** edit `<game>\BepInEx\config\com.volcanoidsmp.directconnect.cfg` in Notepad (with the game closed) and set `UseSteamTransport = true` or `false`.

The top line of the F8 window shows the active mode: **Steam + direct IP** or **DIRECT IP ONLY**. If Steam isn't running, the mod uses direct IP only regardless of the setting.

---

## Other ways to connect (no Tailscale)

The mod works over any connection where the joiner can reach the host's IP on the TCP port.

**ZeroTier**

1. Everyone installs [ZeroTier](https://www.zerotier.com/download/).
2. The host creates a network at <https://my.zerotier.com> and shares the network ID.
3. Everyone joins it from the ZeroTier tray icon. The host approves each member on the website.
4. Use the host's ZeroTier IP. The F8 window marks it **ZeroTier**.

**Radmin VPN**

1. Everyone installs [Radmin VPN](https://www.radmin-vpn.com/).
2. The host creates a network (name + password) and the others join it.
3. Use the host's Radmin IP (`26.x.x.x`). The F8 window marks it **Radmin VPN**.

**Same house / LAN**

- Use the host's LAN address (`192.168.x.x` or `10.x.x.x`). The F8 window marks it **LAN**.

**Port forwarding (no VPN)**

1. In the host's router, forward **TCP 27777** to the host PC's LAN IP.
2. Allow the port in Windows Firewall (setup step 5 does this).
3. The joiner uses the host's public IP (search "what is my IP" on the host).

This exposes the port to the internet, so set a game password. Tailscale is safer.

---

## The Direct Connect window (F8)

| Part | What it shows / does |
|---|---|
| **Mode / Steam** | Active network mode and whether Steam is running. |
| **JOIN A FRIEND** | Address and password fields, the **Join** button (works on the main menu only), and a status line. |
| **HOST** | Whether you're accepting direct players, the **Stop accepting** / **Accept direct players** button, and connected direct players with ping and traffic. |
| **Your addresses** | This PC's addresses with a type label (Tailscale, ZeroTier, Radmin VPN, LAN, This PC) and **Copy** buttons. |
| **Write diagnostics** | Same as **F9**: saves a report for troubleshooting. |

- **Mouse cursor:** while the window is open, the cursor is shown. Drag the window by its title bar.
- **F8 in game:** you can open the window in game to see players and ping, but joining only works from the main menu.

---

## Settings

Setup writes the settings file to `<game>\BepInEx\config\com.volcanoidsmp.directconnect.cfg`. To change it, run `setup.bat` again or edit the file in Notepad while the game is closed.

| Setting | Default | Meaning |
|---|---|---|
| `Port` | 27777 | TCP port the host listens on |
| `UseSteamTransport` | true | `false` = direct IP only. Steam multiplayer isn't used at all |
| `AcceptDirectPlayers` | true | Whether hosting also accepts direct IP players |
| `TimeoutSeconds` | 30 | Drops a direct connection after this long with no data |
| `LastAddress` | | The last address you joined (filled in automatically) |
| `ToggleKey` / `DiagnosticsKey` | F8 / F9 | Hotkeys |
| `ShowWindowOnMainMenu` | true | Opens the window on its own on the main menu |
| `VerboseLogging` | false | Logs more connection detail |

---

## Troubleshooting

| Problem | Fix |
|---|---|
| No BepInEx console window, and F8 does nothing | BepInEx isn't loading. Run `setup.bat` again and check for six `[OK]` lines. Make sure the game folder you chose is the one you actually start the game from. |
| Console shows red errors from *Volcanoids Direct Connect* | Press **F9** and send the diagnostics and log (see [Testing status](#testing-status--reporting-problems)). |
| `Game version is '…' but this mod was built for 1.33.506.0` | The mod targets 1.33.506.0. A different game version may not work. |
| `Could not open TCP port 27777` (host) | Another program uses the port. Change `Port` on **everyone's** PC (setup step 4). |
| Joiner gets `timed out` | **1.** Is the host loaded into a **multiplayer** save? **2.** Run the `Test-NetConnection` check from [2.4](#24-test-the-connection-optional-but-saves-time). **3.** On the host, run `setup.bat` again and answer **yes** at the firewall step. **4.** Check that both Tailscale icons show *connected* and that the friend accepted the share. |
| `TcpTestSucceeded : False` over Tailscale | Check that the host's Tailscale is running and the share was accepted. Try the host's `.ts.net` name instead of the IP. Check the firewall step. |
| Joiner loads in, then gets kicked to the main menu | Usually a wrong password, a full game, or different mods. Check the host's BepInEx console for the reason. |
| Multiplayer toggle greyed out | The save uses local mods, so the game disables multiplayer. |
| Players lag or disconnect | Check the ping in the F8 HOST list. On Tailscale, a relayed ("DERP") connection is slower than a direct one. Run `tailscale status` to see whether a peer is `direct` or `relay`. |
| Two copies on one PC are "the same player" | Expected; see [Tutorial 5](#tutorial-5-test-on-one-pc). |

---

## Updating and uninstalling

- **Update:** download the new version, close the game, and run `setup.bat` again. Your answers are kept. Everyone must run the **same** mod version.
- **Uninstall the mod:** `setup.bat uninstall`. This removes the mod and the firewall rule.
- **Remove BepInEx too:** delete `BepInEx\`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and `changelog.txt` from the game folder.
- **Turn the mod off temporarily:** open `doorstop_config.ini` in the game folder and set `enabled = false`. Running `setup.bat` turns it back on.

---

## How it works

Volcanoids v1.33 uses its own fork of Unity's networking library. All game traffic goes through a pluggable `NetworkHost` chosen via `Transport.Current`; normally that's the game's `SteamSocketsNetworkTransport`.

The mod installs its own transport on top:

- **Every host the game creates becomes a `DirectHost`.** It contains the game's Steam host (unless you chose direct-only) plus direct TCP connections.
- **The game can't tell them apart.** `DirectHost` gives out its own connection IDs and maps each one to a Steam or a direct connection, so the two can never collide.
- **Hosting:** when you start a multiplayer game, the server's host also listens on the TCP port. Joiners go through the game's normal connect, player and spawn code.
- **Joining:** the F8 window first asks the host which world it's running. It then starts the game's own join (`StartMultiplayer.Connect`) with a placeholder server ID that `DirectHost` recognises as "this IP:port".
- **Player identity:** a direct player is identified as `steamid:<their id>` when Steam is running, just like a Steam join, so per-player save data stays consistent. Without Steam, they're identified as `direct:<name>@<ip>`.
- **Connection health:** a ping every 2 s measures latency and detects dead connections. When a player is kicked or rejected, the reason is sent before the connection closes, so they see the right message.

---

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

---

## Building (developers)

The plugin uses `ReadOnlySpan<byte>`, like the game's networking API does, so it needs the .NET SDK to build. BepInEx must already be installed in the game folder.

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Volcanoids" -UpdatePrebuilt
```

`-UpdatePrebuilt` refreshes `prebuilt\VolcanoidsDirectConnect.dll`, which is the file setup installs. To have setup compile against the chosen game folder instead, run `powershell -ExecutionPolicy Bypass -File tools\setup.ps1 -Build`.
