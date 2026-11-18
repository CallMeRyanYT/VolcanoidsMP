// Volcanoids Direct Connect
// Adds a direct IP (TCP) multiplayer transport next to the game's Steam transport,
// so players can join each other over Tailscale, ZeroTier, Radmin VPN, LAN or a
// forwarded port instead of Steam's relay network.
//
// NOTE: this file (and the rest of the mod) is intentionally written in C# 5 so it
// can be compiled by the csc.exe that ships with every Windows install.

using System;
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
        public const string Version = "0.1.0";
        public const int ProtocolVersion = 1;
        public const string TargetGameVersion = "1.33.506.0";
    }

    [BepInPlugin(ModInfo.Guid, ModInfo.Name, ModInfo.Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ModConfig Cfg;
        internal static string PluginFolder;

        private void Awake()
        {
            Log = Logger;
            Cfg = new ModConfig(Config);
            PluginFolder = System.IO.Path.GetDirectoryName(Info.Location);

            // Run the mod on our own hidden, persistent object. Some games destroy the
            // BepInEx manager object during scene loads; this keeps us alive regardless.
            GameObject go = new GameObject("VolcanoidsDirectConnect");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<DirectConnectRunner>();

            Log.LogInfo(ModInfo.Name + " " + ModInfo.Version + " loaded. Game version: " + Application.version
                + " (mod built for " + ModInfo.TargetGameVersion + "). Press " + Cfg.ToggleKey.Value
                + " in game to open the Direct Connect window.");
            if (Application.version != ModInfo.TargetGameVersion)
            {
                Log.LogWarning("Game version " + Application.version + " differs from the version this mod targets ("
                    + ModInfo.TargetGameVersion + "). It may still work.");
            }
        }
    }

    internal sealed class ModConfig
    {
        public readonly ConfigEntry<int> Port;
        public readonly ConfigEntry<bool> AutoOpenWhenHosting;
        public readonly ConfigEntry<string> Password;
        public readonly ConfigEntry<int> MaxDirectPlayers;
        public readonly ConfigEntry<bool> RequireSameGameVersion;
        public readonly ConfigEntry<string> LastAddress;
        public readonly ConfigEntry<string> PlayerName;
        public readonly ConfigEntry<KeyCode> ToggleKey;
        public readonly ConfigEntry<KeyCode> DiagnosticsKey;
        public readonly ConfigEntry<bool> ShowWindowOnMainMenu;
        public readonly ConfigEntry<int> ChannelCount;
        public readonly ConfigEntry<string> ConnectionAddressMode;
        public readonly ConfigEntry<bool> VerboseLogging;

        public ModConfig(ConfigFile cfg)
        {
            Port = cfg.Bind("Network", "Port", 27777,
                "TCP port the host listens on. Joiners must use the same port. Open/forward this port if you are not using a VPN like Tailscale.");
            AutoOpenWhenHosting = cfg.Bind("Network", "AutoOpenWhenHosting", true,
                "Automatically accept direct IP players as soon as you are hosting a game.");
            Password = cfg.Bind("Network", "Password", "",
                "Optional password. Host and joiners must use the same value. Empty = no password.");
            MaxDirectPlayers = cfg.Bind("Network", "MaxDirectPlayers", 3,
                "Max number of players that may join through direct IP at the same time.");
            RequireSameGameVersion = cfg.Bind("Network", "RequireSameGameVersion", true,
                "Reject joiners whose game version differs from the host's (strongly recommended).");
            LastAddress = cfg.Bind("Network", "LastAddress", "100.64.0.1:27777",
                "Last address you joined (filled in automatically).");
            PlayerName = cfg.Bind("Network", "PlayerName", "",
                "Name shown to the host in logs. Empty = Windows user name.");

            ToggleKey = cfg.Bind("UI", "ToggleKey", KeyCode.F8, "Key that opens/closes the Direct Connect window.");
            DiagnosticsKey = cfg.Bind("UI", "DiagnosticsKey", KeyCode.F9,
                "Key that writes a networking diagnostics report (send this file to whoever maintains the mod).");
            ShowWindowOnMainMenu = cfg.Bind("UI", "ShowWindowOnMainMenu", true,
                "Open the Direct Connect window automatically on the main menu.");

            ChannelCount = cfg.Bind("Advanced", "ChannelCount", 16,
                "Number of UNet channels on direct connections. Raise it if the log says a channel id is out of range.");
            ConnectionAddressMode = cfg.Bind("Advanced", "ConnectionAddressMode", "Auto",
                "What the game sees as a direct player's connection address. Auto = the joiner's SteamID64 when available, otherwise ip:port. "
                + "SteamId = always SteamID64 (or 0). IpPort = always ip:port.");
            VerboseLogging = cfg.Bind("Advanced", "VerboseLogging", false, "Log every connection event in detail.");
        }
    }
}
