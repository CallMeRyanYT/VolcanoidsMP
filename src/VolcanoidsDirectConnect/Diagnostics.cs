using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace VolcanoidsDirectConnect
{
    /// <summary>
    /// Writes a report of how the game's networking is wired (types, transport, live connections).
    /// The mod can't be tested without the game, so this report is what lets it be adapted quickly.
    /// </summary>
    internal static class Diagnostics
    {
        private static bool typeScanDone;
        private static string typeScan = "";

        public static string Write(string reason, DirectServer server, DirectClient client)
        {
            string path = Path.Combine(Paths(), "VolcanoidsDirectConnect_diagnostics.txt");
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  reason: " + reason + " ====");
                Section(sb, "Runtime", delegate { Runtime(sb, server, client); });
                if (!typeScanDone)
                {
                    StringBuilder ts = new StringBuilder();
                    Section(ts, "Type scan", delegate { ScanTypes(ts); });
                    typeScan = ts.ToString();
                    typeScanDone = true;
                    sb.Append(typeScan);
                }
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not write diagnostics: " + e);
            }
            return path;
        }

        private static string Paths()
        {
            try { return BepInEx.Paths.BepInExRootPath; }
            catch (Exception) { return Application.persistentDataPath; }
        }

        private delegate void Body();

        private static void Section(StringBuilder sb, string title, Body body)
        {
            sb.AppendLine("--- " + title + " ---");
            try { body(); }
            catch (Exception e) { sb.AppendLine("  !! section failed: " + e); }
        }

        private static void Runtime(StringBuilder sb, DirectServer server, DirectClient client)
        {
            sb.AppendLine("  Mod " + ModInfo.Version + " / game " + Application.version + " / Unity " + Application.unityVersion
                + " / scene '" + SceneManager.GetActiveScene().name + "'");
            sb.AppendLine("  NetworkServer.active=" + NetworkServer.active + " dontListen=" + NetworkServer.dontListen
                + " localClientActive=" + NetworkServer.localClientActive);
            try
            {
                HostTopology ht = NetworkServer.hostTopology;
                if (ht != null)
                {
                    ConnectionConfig cc = ht.DefaultConfig;
                    StringBuilder ch = new StringBuilder();
                    for (int i = 0; i < cc.ChannelCount; i++) ch.Append(i + ":" + cc.Channels[i].QOS + " ");
                    sb.AppendLine("  Server topology: packet=" + cc.PacketSize + " fragment=" + cc.FragmentSize + " channels=[" + ch + "]");
                }
            }
            catch (Exception e) { sb.AppendLine("  Server topology: " + e.Message); }

            try
            {
                object t = NetworkManager.activeTransport;
                sb.AppendLine("  NetworkManager.activeTransport = " + (t == null ? "null" : t.GetType().FullName));
            }
            catch (Exception e) { sb.AppendLine("  activeTransport: " + e.Message); }

            NetworkManager nm = DirectClient.FindNetworkManager();
            if (nm == null) sb.AppendLine("  NetworkManager: NONE FOUND");
            else
            {
                sb.AppendLine("  NetworkManager: " + nm.GetType().FullName + " on '" + nm.gameObject.name + "' active=" + nm.isNetworkActive
                    + " online='" + nm.onlineScene + "' offline='" + nm.offlineScene + "' port=" + nm.networkPort
                    + " customConfig=" + nm.customConfig + " channels=" + (nm.channels == null ? 0 : nm.channels.Count));
                sb.AppendLine("    client=" + (nm.client == null ? "null" : nm.client.GetType().FullName));
            }

            if (NetworkServer.active)
            {
                var conns = NetworkServer.connections;
                sb.AppendLine("  Server connections: " + conns.Count);
                for (int i = 0; i < conns.Count; i++)
                {
                    NetworkConnection c = conns[i];
                    if (c == null) { sb.AppendLine("    [" + i + "] null"); continue; }
                    sb.AppendLine("    [" + i + "] " + c.GetType().FullName + " id=" + c.connectionId + " hostId=" + c.hostId
                        + " address='" + c.address + "' ready=" + c.isReady + " players=" + c.playerControllers.Count);
                }
            }

            var clients = NetworkClient.allClients;
            sb.AppendLine("  NetworkClients: " + clients.Count);
            for (int i = 0; i < clients.Count; i++)
            {
                NetworkClient c = clients[i];
                NetworkConnection cc = c.connection;
                sb.AppendLine("    " + c.GetType().FullName + " connected=" + c.isConnected + " conn="
                    + (cc == null ? "null" : (cc.GetType().FullName + " address='" + cc.address + "' hostId=" + cc.hostId)));
            }

            sb.AppendLine("  Direct server running=" + server.IsRunning + " port=" + server.Port + " players=" + server.Connections.Count
                + (string.IsNullOrEmpty(server.LastError) ? "" : " lastError=" + server.LastError));
            sb.AppendLine("  Direct client state=" + client.CurrentState + " status='" + client.Status + "'");
            sb.AppendLine("  Local SteamID found: " + (SteamIdentity.TryGetLocalSteamId().Length > 0));
        }

        private static readonly string[] Interesting =
        {
            "steam", "lobby", "network", "multiplayer", "coop", "co-op", "session", "join", "host", "server", "client",
            "connection", "transport", "p2p", "invite", "matchmak", "netmsg", "message"
        };

        private static void ScanTypes(StringBuilder sb)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            sb.AppendLine("  Assemblies:");
            for (int i = 0; i < asms.Length; i++)
            {
                AssemblyName n = asms[i].GetName();
                sb.AppendLine("    " + n.Name + " " + n.Version);
            }

            Type connType = typeof(NetworkConnection);
            Type nmType = typeof(NetworkManager);
            Type clientType = typeof(NetworkClient);
            Type transportIface = null;
            try { transportIface = typeof(NetworkManager).Assembly.GetType("UnityEngine.Networking.INetworkTransport"); }
            catch (Exception) { }

            List<Type> keyTypes = new List<Type>();
            List<string> nameHits = new List<string>();
            int behaviours = 0, messages = 0;

            for (int a = 0; a < asms.Length; a++)
            {
                string an = asms[a].GetName().Name;
                if (!IsGameAssembly(an)) continue;
                Type[] types;
                try { types = asms[a].GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch (Exception) { continue; }

                for (int i = 0; i < types.Length; i++)
                {
                    Type t = types[i];
                    if (t == null) continue;
                    try
                    {
                        if (typeof(NetworkBehaviour).IsAssignableFrom(t)) behaviours++;
                        if (typeof(MessageBase).IsAssignableFrom(t)) messages++;
                        bool key = (t != connType && connType.IsAssignableFrom(t))
                            || (t != nmType && nmType.IsAssignableFrom(t))
                            || (t != clientType && clientType.IsAssignableFrom(t))
                            || (transportIface != null && t != transportIface && transportIface.IsAssignableFrom(t));
                        if (key) keyTypes.Add(t);
                        else if (an != "com.unity.multiplayer-hlapi.Runtime" && MatchesName(t.FullName)) nameHits.Add(an + " :: " + t.FullName);
                    }
                    catch (Exception) { }
                }
            }

            sb.AppendLine("  NetworkBehaviour subclasses: " + behaviours + ", MessageBase subclasses: " + messages);
            sb.AppendLine("  KEY TYPES (custom connections / managers / clients / transports):");
            for (int i = 0; i < keyTypes.Count; i++) DumpType(sb, keyTypes[i]);

            nameHits.Sort();
            sb.AppendLine("  Types with networking-related names (" + nameHits.Count + "):");
            int max = Math.Min(nameHits.Count, 600);
            for (int i = 0; i < max; i++) sb.AppendLine("    " + nameHits[i]);
            if (nameHits.Count > max) sb.AppendLine("    ... " + (nameHits.Count - max) + " more");
        }

        private static bool IsGameAssembly(string name)
        {
            if (name.StartsWith("System") || name.StartsWith("mscorlib") || name.StartsWith("Mono.") || name == "netstandard") return false;
            if (name.StartsWith("UnityEngine") || name.StartsWith("Unity.") || name.StartsWith("BepInEx") || name.StartsWith("0Harmony")) return false;
            if (name.StartsWith("MonoMod") || name.StartsWith("Mono.Cecil") || name == "VolcanoidsDirectConnect") return false;
            return true;
        }

        private static bool MatchesName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || fullName.IndexOf('<') >= 0) return false;
            string l = fullName.ToLowerInvariant();
            for (int i = 0; i < Interesting.Length; i++)
                if (l.Contains(Interesting[i])) return true;
            return false;
        }

        private static void DumpType(StringBuilder sb, Type t)
        {
            sb.AppendLine("    * " + t.Assembly.GetName().Name + " :: " + t.FullName + " : " + (t.BaseType == null ? "-" : t.BaseType.FullName));
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            try
            {
                foreach (FieldInfo f in t.GetFields(all))
                    sb.AppendLine("        field " + (f.IsStatic ? "static " : "") + f.FieldType.Name + " " + f.Name);
                foreach (MethodInfo m in t.GetMethods(all))
                {
                    if (m.IsSpecialName && !(m.Name.StartsWith("get_") || m.Name.StartsWith("set_"))) continue;
                    StringBuilder ps = new StringBuilder();
                    ParameterInfo[] p = m.GetParameters();
                    for (int i = 0; i < p.Length; i++) ps.Append((i > 0 ? ", " : "") + p[i].ParameterType.Name + " " + p[i].Name);
                    sb.AppendLine("        " + (m.IsStatic ? "static " : "") + (m.IsVirtual ? "virtual " : "") + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
                }
            }
            catch (Exception e) { sb.AppendLine("        (members unavailable: " + e.Message + ")"); }
        }
    }
}
