using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace VolcanoidsDirectConnect
{
    /// <summary>Main-thread driver: pumps the transport and draws the Direct Connect window.</summary>
    internal sealed class DirectConnectRunner : MonoBehaviour
    {
        private readonly DirectServer server = new DirectServer();
        private readonly DirectClient client = new DirectClient();

        private bool showWindow;
        private Rect windowRect = new Rect(40, 40, 470, 10);
        private string addressField;
        private string passwordField;
        private string portField;
        private bool hostManuallyStopped;
        private bool lastServerActive;
        private string lastScene = "";
        private string notice = "";
        private float noticeUntil;
        private List<KeyValuePair<string, string>> localAddresses;
        private float addressesRefreshedAt = -100f;
        private bool cursorForced;
        private CursorLockMode prevLock;
        private bool prevVisible;

        private void Start()
        {
            addressField = Plugin.Cfg.LastAddress.Value;
            passwordField = Plugin.Cfg.Password.Value;
            portField = Plugin.Cfg.Port.Value.ToString();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Diagnostics.Write("startup", server, client);
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            server.Stop();
            client.Disconnect("game closing");
        }

        private void OnApplicationQuit()
        {
            server.Stop();
            client.Disconnect("game closing");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            lastScene = scene.name;
            if (Plugin.Cfg.VerboseLogging.Value) Plugin.Log.LogInfo("Scene loaded: " + scene.name);
            if (scene.name.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hostManuallyStopped = false;
                if (Plugin.Cfg.ShowWindowOnMainMenu.Value && !client.Busy) showWindow = true;
            }
            else if (showWindow && client.CurrentState == DirectClient.State.Connected)
            {
                showWindow = false; // got into the world, get out of the way
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(Plugin.Cfg.ToggleKey.Value)) showWindow = !showWindow;
            if (Input.GetKeyDown(Plugin.Cfg.DiagnosticsKey.Value))
            {
                string path = Diagnostics.Write("hotkey", server, client);
                Notify("Diagnostics written to " + path);
            }

            try
            {
                // Auto-open the direct port whenever this game becomes a host.
                bool serverActive = NetworkServer.active;
                bool isPureClient = client.CurrentState == DirectClient.State.Connected;
                if (serverActive && !lastServerActive)
                {
                    Plugin.Log.LogInfo("Game started hosting (NetworkServer active).");
                    Diagnostics.Write("hosting-started", server, client);
                }
                if (serverActive && !server.IsRunning && Plugin.Cfg.AutoOpenWhenHosting.Value && !hostManuallyStopped && !isPureClient)
                {
                    if (!server.Start(Plugin.Cfg.Port.Value)) hostManuallyStopped = true; // don't retry every frame
                }
                if (!serverActive && lastServerActive && server.IsRunning)
                {
                    server.Stop();
                }
                lastServerActive = serverActive;

                server.Tick();
                client.Tick();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Direct Connect update error: " + e);
            }

            UpdateCursor();
        }

        private void LateUpdate()
        {
            try
            {
                server.LateTick();
                client.LateTick();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Direct Connect flush error: " + e);
            }
        }

        private void UpdateCursor()
        {
            // The game may hide/lock the cursor in-world; free it while our window is open.
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

        private void Notify(string msg)
        {
            notice = msg;
            noticeUntil = Time.realtimeSinceStartup + 6f;
            Plugin.Log.LogInfo(msg);
        }

        private void OnGUI()
        {
            if (!string.IsNullOrEmpty(notice) && Time.realtimeSinceStartup < noticeUntil)
            {
                GUI.Box(new Rect(10, Screen.height - 40, Mathf.Min(Screen.width - 20, 900), 28), notice);
            }
            if (!showWindow) return;
            GUI.depth = -1000;
            windowRect.height = 10; // auto-size
            windowRect = GUILayout.Window(0x56444331, windowRect, DrawWindow,
                "Volcanoids Direct Connect " + ModInfo.Version + "   [" + Plugin.Cfg.ToggleKey.Value + " to close]");
        }

        private void DrawWindow(int id)
        {
            // ---------------- Join ----------------
            GUILayout.Label("== JOIN A FRIEND == (start from the main menu)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Host IP:port", GUILayout.Width(90));
            addressField = GUILayout.TextField(addressField ?? "", GUILayout.MinWidth(250));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Password", GUILayout.Width(90));
            passwordField = GUILayout.PasswordField(passwordField ?? "", '*', GUILayout.MinWidth(250));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !client.Busy;
            if (GUILayout.Button("Join"))
            {
                Plugin.Cfg.LastAddress.Value = addressField;
                Plugin.Cfg.Password.Value = passwordField;
                client.Connect(addressField, Plugin.Cfg.Port.Value, passwordField);
            }
            GUI.enabled = client.Busy;
            if (GUILayout.Button("Disconnect")) client.Disconnect("disconnect pressed");
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Status: " + client.Status);

            GUILayout.Space(10);

            // ---------------- Host ----------------
            GUILayout.Label("== HOST == (load your save as a co-op / multiplayer game first)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(90));
            portField = GUILayout.TextField(portField ?? "", GUILayout.Width(80));
            GUILayout.Label("Game is hosting: " + (NetworkServer.active ? "YES" : "no"));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = !server.IsRunning;
            if (GUILayout.Button("Open direct port"))
            {
                int p;
                if (int.TryParse(portField, out p) && p > 0 && p < 65536)
                {
                    Plugin.Cfg.Port.Value = p;
                    Plugin.Cfg.Password.Value = passwordField;
                    hostManuallyStopped = false;
                    if (!NetworkServer.active)
                        Notify("Load a world as host first - the port only accepts players while you are in a hosted game.");
                    if (!server.Start(p)) Notify(server.LastError);
                }
                else Notify("Invalid port.");
            }
            GUI.enabled = server.IsRunning;
            if (GUILayout.Button("Close direct port"))
            {
                hostManuallyStopped = true;
                server.Stop();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (server.IsRunning)
            {
                GUILayout.Label("Listening on TCP " + server.Port + "  -  direct players: " + server.Connections.Count);
                for (int i = 0; i < server.Connections.Count; i++)
                {
                    DirectConnection c = server.Connections[i];
                    GUILayout.Label("   • " + c.Describe() + "   in " + (c.Link.BytesIn / 1024) + " KB / out " + (c.Link.BytesOut / 1024) + " KB");
                }
            }
            else if (!string.IsNullOrEmpty(server.LastError))
            {
                GUILayout.Label("Error: " + server.LastError);
            }

            GUILayout.Space(6);
            GUILayout.Label("Give your friend one of these addresses (with :" + Plugin.Cfg.Port.Value + "):");
            if (Time.realtimeSinceStartup - addressesRefreshedAt > 10f || localAddresses == null)
            {
                localAddresses = GetLocalAddresses();
                addressesRefreshedAt = Time.realtimeSinceStartup;
            }
            for (int i = 0; i < localAddresses.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("   " + localAddresses[i].Key, GUILayout.Width(210));
                GUILayout.Label(localAddresses[i].Value);
                if (GUILayout.Button("Copy", GUILayout.Width(50)))
                {
                    GUIUtility.systemCopyBuffer = localAddresses[i].Key + ":" + Plugin.Cfg.Port.Value;
                    Notify("Copied " + GUIUtility.systemCopyBuffer);
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Write diagnostics (" + Plugin.Cfg.DiagnosticsKey.Value + ")"))
            {
                Notify("Diagnostics written to " + Diagnostics.Write("button", server, client));
            }
            if (GUILayout.Button("Close")) showWindow = false;
            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private static List<KeyValuePair<string, string>> GetLocalAddresses()
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
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
            // Put VPN addresses first: they are the ones friends usually need.
            result.Sort(delegate (KeyValuePair<string, string> a, KeyValuePair<string, string> b)
            {
                return Rank(a.Value).CompareTo(Rank(b.Value));
            });
            return result;
        }

        private static int Rank(string kind)
        {
            if (kind.StartsWith("Tailscale")) return 0;
            if (kind.StartsWith("ZeroTier") || kind.StartsWith("Radmin") || kind.StartsWith("Hamachi")) return 1;
            if (kind.StartsWith("LAN")) return 2;
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
