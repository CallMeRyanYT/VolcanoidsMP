using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using Steamworks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace VolcanoidsDirectConnect
{
    /// <summary>Main-thread driver: installs the transport, pumps it, and draws the Direct Connect window.</summary>
    internal sealed class DirectConnectRunner : MonoBehaviour
    {
        private static DirectConnectRunner instance;
        private static string notice = "";
        private static float noticeUntil;

        // Cached on the main thread for background threads.
        private static volatile string localName = "";
        private static ulong localSteamId;

        private bool showWindow;
        private Rect windowRect = new Rect(40, 40, 500, 10);
        private string addressField;
        private string passwordField = "";
        private int lastToggleFrame = -1;
        private bool legacyInputBroken;
        private readonly HashSet<string> loggedErrors = new HashSet<string>();
        private List<KeyValuePair<string, string>> localAddresses;
        private float addressesRefreshedAt = -100f;
        private bool cursorForced;
        private CursorLockMode prevLock;
        private bool prevVisible;

        // Join (probe) state
        private volatile bool probing;
        private volatile string joinStatus = "";
        private volatile Handshake.Reply probeReply;
        private string probeHost;
        private int probePort;
        private string probePassword;

        private void Awake()
        {
            instance = this;
            addressField = Plugin.Cfg.LastAddress.Value;
        }

        private void Start()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            Guard("diagnostics", delegate { Diagnostics.Write("startup"); });
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        public static void Notify(string msg)
        {
            notice = msg;
            noticeUntil = Time.realtimeSinceStartup + 8f;
            if (Plugin.Log != null) Plugin.Log.LogInfo(msg);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (Plugin.Cfg.VerboseLogging.Value) Plugin.Log.LogInfo("Scene loaded: " + scene.name);
            if (AtMainMenu())
            {
                if (Plugin.Cfg.ShowWindowOnMainMenu.Value) showWindow = true;
            }
            else if (showWindow && !probing)
            {
                showWindow = false; // entering a world - get out of the way
            }
        }

        private static bool AtMainMenu()
        {
            return NetworkManager.singleton == null && !NetworkServer.active && !NetworkClient.active;
        }

        // ------------------------------------------------------------------ per frame

        private void Update()
        {
            Guard("hotkeys", PollLegacyHotkeys);
            Guard("transport", InstallTransport);
            Guard("identity", CacheIdentity);
            Guard("pump", DirectHost.PumpAll);
            Guard("join", FinishProbe);
            UpdateCursor();
        }

        /// <summary>Runs one piece of work; a failure is logged once instead of every frame.</summary>
        private void Guard(string what, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                string key = what + ":" + e.GetType().Name + ":" + e.Message;
                if (loggedErrors.Add(key)) Plugin.Log.LogError("Direct Connect " + what + " error (shown once): " + e);
            }
        }

        private void PollLegacyHotkeys()
        {
            if (legacyInputBroken) return;
            try
            {
                if (Input.GetKeyDown(Plugin.Cfg.ToggleKey.Value)) ToggleWindow();
                if (Input.GetKeyDown(Plugin.Cfg.DiagnosticsKey.Value)) WriteDiagnostics();
            }
            catch (Exception)
            {
                // The game may only enable the new Input System; OnGUI key events still work then.
                legacyInputBroken = true;
            }
        }

        private void ToggleWindow()
        {
            if (lastToggleFrame == Time.frameCount) return;
            lastToggleFrame = Time.frameCount;
            showWindow = !showWindow;
        }

        private void WriteDiagnostics()
        {
            if (lastDiagFrame == Time.frameCount) return;
            lastDiagFrame = Time.frameCount;
            Notify("Diagnostics written to " + Diagnostics.Write("hotkey"));
        }
        private int lastDiagFrame = -1;

        private static void InstallTransport()
        {
            INetworkTransport current = Transport.Current;
            if (current is DirectTransport) return;
            // Runs again whenever the game (re)assigns its own transport, so we always end up on top.
            INetworkTransport inner = Plugin.Cfg.UseSteamTransport.Value ? current : null;
            Transport.Current = new DirectTransport(inner);
            Plugin.Log.LogInfo("Direct Connect transport installed. Mode: " + (inner != null ? "Steam + direct IP"
                : (current == null ? "direct IP only (Steam networking not available)" : "direct IP only (UseSteamTransport = false)")));
        }

        private static void CacheIdentity()
        {
            if (localName.Length > 0) return;
            bool steam = false;
            try { steam = Steam.IsInitialized; } catch (Exception) { }
            if (steam)
            {
                localSteamId = SteamUser.GetSteamID().m_SteamID;
                localName = SteamFriends.GetPersonaName();
            }
            if (string.IsNullOrEmpty(localName)) localName = Environment.UserName;
        }

        // ------------------------------------------------------------------ joining

        /// <summary>Opens a TCP connection and performs the hello. Runs on a background thread.</summary>
        internal static TcpLink OpenLink(string host, int port, byte mode, out Handshake.Reply reply)
        {
            reply = new Handshake.Reply();
            IPAddress[] addrs = Dns.GetHostAddresses(host);
            if (addrs.Length == 0) throw new Exception("could not resolve " + host);

            TcpClient c = null;
            Exception last = null;
            foreach (IPAddress a in addrs)
            {
                try
                {
                    c = new TcpClient(a.AddressFamily);
                    IAsyncResult ar = c.BeginConnect(a, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                        throw new Exception("timed out - is the host in a multiplayer game, and is TCP port " + port + " allowed through their firewall?");
                    c.EndConnect(ar);
                    last = null;
                    break;
                }
                catch (Exception e)
                {
                    last = e;
                    try { if (c != null) c.Close(); } catch (Exception) { }
                    c = null;
                }
            }
            if (c == null) throw last ?? new Exception("could not connect");

            c.ReceiveTimeout = 15000;
            c.SendTimeout = 15000;
            TcpLink link = new TcpLink(c);
            Handshake.WriteHello(link.Stream, new Handshake.Hello
            {
                Protocol = ModInfo.ProtocolVersion,
                Mode = mode,
                ModVersion = ModInfo.Version,
                GameVersion = Application.version,
                PlayerName = localName,
                SteamId = localSteamId
            });
            reply = Handshake.ReadReply(link.Stream);
            return link;
        }

        private void BeginJoin()
        {
            string host;
            int port;
            if (!ParseAddress(addressField, Plugin.Cfg.Port.Value, out host, out port))
            {
                joinStatus = "Invalid address. Use IP:port, e.g. 100.101.102.103:" + Plugin.Cfg.Port.Value;
                return;
            }
            if (!AtMainMenu())
            {
                joinStatus = "Go back to the main menu first.";
                return;
            }
            Plugin.Cfg.LastAddress.Value = addressField.Trim();
            probeHost = host;
            probePort = port;
            probePassword = passwordField ?? "";
            probeReply = null;
            probing = true;
            joinStatus = "Contacting " + host + ":" + port + " ...";
            new Thread(delegate ()
            {
                Handshake.Reply rep;
                try
                {
                    TcpLink l = OpenLink(host, port, Handshake.ModeProbe, out rep);
                    l.Close("probe done");
                }
                catch (Exception e)
                {
                    rep = new Handshake.Reply { Message = "Could not reach " + host + ":" + port + " - " + e.Message };
                }
                probeReply = rep;
            }) { IsBackground = true, Name = "VDC probe" }.Start();
        }

        private void FinishProbe()
        {
            if (!probing || probeReply == null) return;
            Handshake.Reply rep = probeReply;
            probing = false;
            probeReply = null;

            if (rep.Result != Handshake.ResultOk)
            {
                joinStatus = rep.Message;
                Notify("Join failed: " + rep.Message);
                return;
            }
            if (rep.PasswordRequired && string.IsNullOrEmpty(probePassword))
            {
                joinStatus = (string.IsNullOrEmpty(rep.HostName) ? "The host" : rep.HostName) + "'s game needs a password - type it and press Join again.";
                return;
            }
            if (!AtMainMenu())
            {
                joinStatus = "Go back to the main menu first.";
                return;
            }

            ulong serverId = DirectTransport.RegisterTarget(probeHost, probePort);
            joinStatus = "Joining " + (string.IsNullOrEmpty(rep.HostName) ? "" : rep.HostName + " ") + "(" + rep.SceneName + ") ...";
            Plugin.Log.LogInfo("Joining " + probeHost + ":" + probePort + " via Direct Connect, scene '" + rep.SceneName + "'.");
            showWindow = false;
            // Same call the game's own lobby browser makes; the game then loads the world and connects
            // through Transport.Current, where DirectHost recognises serverId as this IP:port.
            StartMultiplayer.Connect(serverId, rep.SceneName, probePassword);
        }

        internal static bool ParseAddress(string input, int defaultPort, out string host, out int port)
        {
            host = null;
            port = defaultPort;
            if (string.IsNullOrEmpty(input)) return false;
            string s = input.Trim();
            if (s.StartsWith("["))
            {
                int close = s.IndexOf(']');
                if (close < 0) return false;
                host = s.Substring(1, close - 1);
                string rest = s.Substring(close + 1);
                if (rest.StartsWith(":") && !int.TryParse(rest.Substring(1), out port)) return false;
            }
            else
            {
                int colon = s.LastIndexOf(':');
                if (colon > 0 && s.IndexOf(':') == colon)
                {
                    host = s.Substring(0, colon);
                    if (!int.TryParse(s.Substring(colon + 1), out port)) return false;
                }
                else host = s;
            }
            return !string.IsNullOrEmpty(host) && port > 0 && port < 65536;
        }

        // ------------------------------------------------------------------ UI

        private void UpdateCursor()
        {
            if (showWindow)
            {
                if (!cursorForced)
                {
                    prevLock = Cursor.lockState;
                    prevVisible = Cursor.visible;
                    cursorForced = true;
                }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (cursorForced)
            {
                cursorForced = false;
                Cursor.lockState = prevLock;
                Cursor.visible = prevVisible;
            }
        }

        private void OnGUI()
        {
            Event e = Event.current;
            if (e != null && e.type == EventType.KeyDown)
            {
                if (e.keyCode == Plugin.Cfg.ToggleKey.Value) { ToggleWindow(); e.Use(); }
                else if (e.keyCode == Plugin.Cfg.DiagnosticsKey.Value) { Guard("diagnostics", WriteDiagnostics); e.Use(); }
            }

            if (!string.IsNullOrEmpty(notice) && Time.realtimeSinceStartup < noticeUntil)
                GUI.Box(new Rect(10, Screen.height - 40, Mathf.Min(Screen.width - 20, 900), 28), notice);

            if (!showWindow) return;
            GUI.depth = -1000;
            windowRect.height = 10;
            windowRect = GUILayout.Window(0x56444332, windowRect, DrawWindow,
                ModInfo.Name + " " + ModInfo.Version + "   [" + Plugin.Cfg.ToggleKey.Value + " closes]");
        }

        private void DrawWindow(int id)
        {
            try { DrawWindowContents(); }
            catch (Exception ex)
            {
                string key = "ui:" + ex.Message;
                if (loggedErrors.Add(key)) Plugin.Log.LogError("Direct Connect window error (shown once): " + ex);
                GUILayout.Label("Window error - see BepInEx log.");
            }
            GUI.DragWindow();
        }

        private void DrawWindowContents()
        {
            INetworkTransport t = Transport.Current;
            DirectTransport dt = t as DirectTransport;
            bool steamRunning = false;
            try { steamRunning = Steam.IsInitialized; } catch (Exception) { }
            string mode = dt == null ? "starting..." : (dt.Inner != null ? "Steam + direct IP" : "DIRECT IP ONLY");
            GUILayout.Label("Mode: " + mode + "     Steam: " + (steamRunning ? "running" : "not running"));

            // ---------------- Join ----------------
            GUILayout.Space(4);
            GUILayout.Label("== JOIN A FRIEND ==  (from the main menu)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host IP:port", GUILayout.Width(95));
            addressField = GUILayout.TextField(addressField ?? "", GUILayout.MinWidth(260));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Game password", GUILayout.Width(95));
            passwordField = GUILayout.PasswordField(passwordField ?? "", '*', GUILayout.MinWidth(260));
            GUILayout.EndHorizontal();
            GUILayout.Label("(only if the host set a password in their multiplayer settings)");

            GUILayout.BeginHorizontal();
            GUI.enabled = !probing && AtMainMenu();
            if (GUILayout.Button("Join")) BeginJoin();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(joinStatus)) GUILayout.Label("Status: " + joinStatus);

            // ---------------- Host ----------------
            GUILayout.Space(8);
            GUILayout.Label("== HOST ==");
            DirectHost server = null;
            foreach (DirectHost h in DirectHost.Live) if (h.IsServer) { server = h; break; }

            if (server == null)
            {
                GUILayout.Label("Not hosting. Load (or create) a save with Multiplayer turned ON in its");
                GUILayout.Label("settings - Direct Connect then accepts players on TCP " + Plugin.Cfg.Port.Value + ".");
            }
            else
            {
                if (server.IsListening) GUILayout.Label("Accepting direct players on TCP " + server.ListenPort + ".");
                else GUILayout.Label("Not accepting direct players." + (string.IsNullOrEmpty(server.ListenError) ? "" : " " + server.ListenError));
                GUILayout.BeginHorizontal();
                if (server.IsListening)
                {
                    if (GUILayout.Button("Stop accepting")) server.StopListening();
                }
                else if (GUILayout.Button("Accept direct players")) server.StartListening(Plugin.Cfg.Port.Value);
                GUILayout.EndHorizontal();

                int shown = 0;
                foreach (DirectHost.Peer p in server.Peers)
                {
                    if (p.State != DirectHost.PeerState.Connected) continue;
                    shown++;
                    GUILayout.Label("   • " + p.Describe() + "  ping " + p.PingMs + " ms  in " + (p.Link.BytesIn / 1024) + " KB / out " + (p.Link.BytesOut / 1024) + " KB");
                }
                if (shown == 0) GUILayout.Label("   No direct players connected yet.");
            }

            GUILayout.Space(6);
            GUILayout.Label("Your addresses (give one to your friend, with :" + Plugin.Cfg.Port.Value + "):");
            if (localAddresses == null || Time.realtimeSinceStartup - addressesRefreshedAt > 10f)
            {
                localAddresses = GetLocalAddresses();
                addressesRefreshedAt = Time.realtimeSinceStartup;
            }
            foreach (KeyValuePair<string, string> a in localAddresses)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("   " + a.Key, GUILayout.Width(200));
                GUILayout.Label(a.Value);
                if (GUILayout.Button("Copy", GUILayout.Width(50)))
                {
                    GUIUtility.systemCopyBuffer = a.Key + ":" + Plugin.Cfg.Port.Value;
                    Notify("Copied " + GUIUtility.systemCopyBuffer);
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Write diagnostics (" + Plugin.Cfg.DiagnosticsKey.Value + ")")) WriteDiagnostics();
            if (GUILayout.Button("Close")) showWindow = false;
            GUILayout.EndHorizontal();
        }

        private static List<KeyValuePair<string, string>> GetLocalAddresses()
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        result.Add(new KeyValuePair<string, string>(ua.Address.ToString(), Classify(ua.Address, ni.Name)));
                    }
                }
            }
            catch (Exception e)
            {
                result.Add(new KeyValuePair<string, string>("?", "could not list addresses: " + e.Message));
            }
            result.Add(new KeyValuePair<string, string>("127.0.0.1", "This PC only (testing 2 copies)"));
            result.Sort((a, b) => Rank(a.Value).CompareTo(Rank(b.Value)));
            return result;
        }

        private static int Rank(string kind)
        {
            if (kind.StartsWith("Tailscale")) return 0;
            if (kind.StartsWith("ZeroTier") || kind.StartsWith("Radmin") || kind.StartsWith("Hamachi")) return 1;
            if (kind.StartsWith("LAN")) return 2;
            if (kind.StartsWith("This PC")) return 4;
            return 3;
        }

        private static string Classify(IPAddress a, string adapter)
        {
            byte[] b = a.GetAddressBytes();
            string name = (adapter ?? "").ToLowerInvariant();
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "Tailscale (share this)";
            if (name.Contains("zerotier")) return "ZeroTier (share this)";
            if (b[0] == 26 || name.Contains("radmin")) return "Radmin VPN (share this)";
            if (b[0] == 25 || name.Contains("hamachi")) return "Hamachi (share this)";
            if (b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31))
                return "LAN (same network only)";
            return "Public / other (" + adapter + ")";
        }
    }
}
