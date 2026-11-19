// Volcanoids Direct Connect
// Adds direct IP (TCP) multiplayer to Volcanoids v1.33.506.0, so players can join each other over
// Tailscale, ZeroTier, Radmin VPN, LAN or a forwarded port - with or without Steam's networking.

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace VolcanoidsDirectConnect
{
    public static class ModInfo
    {
        public const string Guid = "com.volcanoidsmp.directconnect";
        public const string Name = "Volcanoids Direct Connect";
        public const string Version = "0.2.0";
        public const int ProtocolVersion = 2;
        public const string TargetGameVersion = "1.33.506.0";
    }

    [BepInPlugin(ModInfo.Guid, ModInfo.Name, ModInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ModConfig Cfg;

        private void Awake()
        {
            Log = Logger;
            Cfg = new ModConfig(Config);

            // Run on our own hidden, persistent object so scene changes can't destroy it.
            GameObject go = new GameObject("VolcanoidsDirectConnect");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<DirectConnectRunner>();

            Log.LogInfo(ModInfo.Name + " " + ModInfo.Version + " loaded (game " + Application.version
                + "). Press " + Cfg.ToggleKey.Value + " for the Direct Connect window.");
            if (Application.version != ModInfo.TargetGameVersion)
                Log.LogWarning("This mod was built for Volcanoids " + ModInfo.TargetGameVersion + " but the game is " + Application.version + ".");
        }
    }

    internal sealed class ModConfig
    {
        public readonly ConfigEntry<int> Port;
        public readonly ConfigEntry<bool> AcceptDirectPlayers;
        public readonly ConfigEntry<bool> UseSteamTransport;
        public readonly ConfigEntry<int> TimeoutSeconds;
        public readonly ConfigEntry<string> LastAddress;
        public readonly ConfigEntry<KeyCode> ToggleKey;
        public readonly ConfigEntry<KeyCode> DiagnosticsKey;
        public readonly ConfigEntry<bool> ShowWindowOnMainMenu;
        public readonly ConfigEntry<bool> VerboseLogging;

        public ModConfig(ConfigFile cfg)
        {
            Port = cfg.Bind("Network", "Port", 27777,
                "TCP port the host listens on. Joiners type host-ip:port. Open/forward it if you aren't using a VPN like Tailscale.");
            AcceptDirectPlayers = cfg.Bind("Network", "AcceptDirectPlayers", true,
                "When you host a multiplayer game, also accept players who join by IP.");
            UseSteamTransport = cfg.Bind("Network", "UseSteamTransport", true,
                "true = Steam multiplayer keeps working alongside direct IP (when Steam is running). "
                + "false = DIRECT ONLY: Steam's networking is not used at all, only direct IP connections (for testing).");
            TimeoutSeconds = cfg.Bind("Network", "TimeoutSeconds", 30,
                "Drop a direct connection after this many seconds without any data.");
            LastAddress = cfg.Bind("Network", "LastAddress", "100.64.0.1:27777",
                "Last address you joined (filled in automatically).");

            ToggleKey = cfg.Bind("UI", "ToggleKey", KeyCode.F8, "Key that opens/closes the Direct Connect window.");
            DiagnosticsKey = cfg.Bind("UI", "DiagnosticsKey", KeyCode.F9,
                "Key that writes a networking diagnostics report.");
            ShowWindowOnMainMenu = cfg.Bind("UI", "ShowWindowOnMainMenu", true,
                "Open the Direct Connect window automatically on the main menu.");

            VerboseLogging = cfg.Bind("Advanced", "VerboseLogging", false, "Log more detail about connections.");
        }
    }
}
