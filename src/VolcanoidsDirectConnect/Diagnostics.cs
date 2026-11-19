using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace VolcanoidsDirectConnect
{
    /// <summary>Appends a snapshot of the networking state to BepInEx\VolcanoidsDirectConnect_diagnostics.txt.</summary>
    internal static class Diagnostics
    {
        public static string Write(string reason)
        {
            string path;
            try { path = Path.Combine(BepInEx.Paths.BepInExRootPath, "VolcanoidsDirectConnect_diagnostics.txt"); }
            catch (Exception) { path = Path.Combine(Application.persistentDataPath, "VolcanoidsDirectConnect_diagnostics.txt"); }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  (" + reason + ") ====");
            Section(sb, delegate
            {
                sb.AppendLine("Mod " + ModInfo.Version + " / game " + Application.version + " / Unity " + Application.unityVersion);
                string scenes = "";
                for (int i = 0; i < SceneManager.sceneCount; i++) scenes += (i > 0 ? ", " : "") + SceneManager.GetSceneAt(i).name;
                sb.AppendLine("Scenes: " + scenes);
            });
            Section(sb, delegate { sb.AppendLine("Steam initialized: " + Steam.IsInitialized); });
            Section(sb, delegate
            {
                INetworkTransport t = Transport.Current;
                DirectTransport dt = t as DirectTransport;
                sb.AppendLine("Transport.Current: " + (t == null ? "null" : t.GetType().FullName)
                    + (dt != null ? "  inner: " + (dt.Inner == null ? "none (direct only)" : dt.Inner.GetType().FullName) : ""));
                sb.AppendLine("Settings: port=" + Plugin.Cfg.Port.Value + " acceptDirect=" + Plugin.Cfg.AcceptDirectPlayers.Value
                    + " useSteam=" + Plugin.Cfg.UseSteamTransport.Value + " timeout=" + Plugin.Cfg.TimeoutSeconds.Value + "s");
            });
            Section(sb, delegate
            {
                NetworkManager nm = NetworkManager.singleton;
                sb.AppendLine("NetworkManager: " + (nm == null ? "none" : nm.GetType().FullName + " active=" + nm.isNetworkActive));
                sb.AppendLine("NetworkServer.active=" + NetworkServer.active + " dontListen=" + NetworkServer.dontListen + " NetworkClient.active=" + NetworkClient.active);
                try
                {
                    MultiplayerSettings ms = MultiplayerSettings.Current;
                    if (ms != null) sb.AppendLine("Multiplayer settings: maxPlayers=" + ms.MaxPlayers + " password=" + (string.IsNullOrEmpty(ms.Password) ? "no" : "yes"));
                }
                catch (Exception) { }
            });
            Section(sb, delegate
            {
                if (!NetworkServer.active) return;
                var conns = NetworkServer.connections;
                sb.AppendLine("Server connections: " + conns.Count);
                for (int i = 0; i < conns.Count; i++)
                {
                    NetworkConnection c = conns[i];
                    if (c == null) continue;
                    string addr = "?";
                    int port, state;
                    if (c.hostId != null) c.hostId.GetConnectionInfo(c.connectionId, out addr, out port, out state);
                    sb.AppendLine("  [" + i + "] id=" + c.connectionId + " local=" + c.isLocal + " ready=" + c.isReady + " address='" + addr
                        + "' rtt=" + c.rtt + " host=" + (c.hostId == null ? "null" : c.hostId.GetType().Name));
                }
            });
            Section(sb, delegate
            {
                foreach (NetworkClient c in NetworkClient.allClients)
                {
                    NetworkConnection cc = c.connection;
                    sb.AppendLine("Client: connected=" + c.isConnected + " conn=" + (cc == null ? "null" : ("id=" + cc.connectionId + " host=" + (cc.hostId == null ? "null" : cc.hostId.GetType().Name))));
                }
            });
            Section(sb, delegate
            {
                sb.AppendLine("Direct hosts: " + DirectHost.Live.Count);
                foreach (DirectHost h in DirectHost.Live)
                {
                    sb.AppendLine("  " + (h.IsServer ? "server" : "client") + " steam=" + h.HasSteam + " listening=" + h.IsListening
                        + (h.IsListening ? " port=" + h.ListenPort : "") + (string.IsNullOrEmpty(h.ListenError) ? "" : " error=" + h.ListenError));
                    foreach (DirectHost.Peer p in h.Peers)
                        sb.AppendLine("    peer " + p.Describe() + " state=" + p.State + " address='" + p.Address + "' ping=" + p.PingMs
                            + (p.Link != null ? " in=" + p.Link.BytesIn + " out=" + p.Link.BytesOut + (p.Link.IsClosed ? " closed: " + p.Link.CloseReason : "") : "")
                            + (string.IsNullOrEmpty(p.Error) ? "" : " error=" + p.Error));
                }
            });
            sb.AppendLine();

            try { File.AppendAllText(path, sb.ToString()); }
            catch (Exception e) { Plugin.Log.LogError("Could not write diagnostics: " + e.Message); }
            return path;
        }

        private static void Section(StringBuilder sb, Action body)
        {
            try { body(); }
            catch (Exception e) { sb.AppendLine("  !! " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
