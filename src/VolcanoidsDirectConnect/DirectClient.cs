using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace VolcanoidsDirectConnect
{
    /// <summary>
    /// Joiner side: connects to a host over TCP, then plugs the link into the game's
    /// NetworkManager as an external NetworkClient so the normal join flow runs.
    /// </summary>
    internal sealed class DirectClient
    {
        public enum State { Idle, Connecting, HandshakeDone, Connected, Failed }

        private volatile State state = State.Idle;
        private volatile string status = "Not connected";
        private TcpLink link;
        private DirectConnection conn;
        private NetworkClient netClient;
        private int attempt;

        public State CurrentState { get { return state; } }
        public string Status { get { return status; } }
        public DirectConnection Connection { get { return conn; } }
        public bool Busy { get { return state == State.Connecting || state == State.HandshakeDone || state == State.Connected; } }

        public void Connect(string hostAndPort, int defaultPort, string password)
        {
            if (Busy) Disconnect("starting a new connection");

            string host;
            int port;
            if (!ParseAddress(hostAndPort, defaultPort, out host, out port))
            {
                state = State.Failed;
                status = "Invalid address. Use IP:port, e.g. 100.101.102.103:" + defaultPort;
                return;
            }

            Handshake.Hello hello = new Handshake.Hello();
            hello.Protocol = ModInfo.ProtocolVersion;
            hello.ModVersion = ModInfo.Version;
            hello.GameVersion = Application.version;
            hello.Password = password ?? "";
            hello.PlayerName = string.IsNullOrEmpty(Plugin.Cfg.PlayerName.Value) ? Environment.UserName : Plugin.Cfg.PlayerName.Value;
            hello.SteamId = SteamIdentity.TryGetLocalSteamId();

            state = State.Connecting;
            status = "Connecting to " + host + ":" + port + " ...";
            int myAttempt = ++attempt;
            Thread t = new Thread(delegate () { ConnectThread(myAttempt, host, port, hello); });
            t.IsBackground = true;
            t.Name = "VDC connect";
            t.Start();
        }

        private void ConnectThread(int myAttempt, string host, int port, Handshake.Hello hello)
        {
            TcpClient c = null;
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                if (addrs.Length == 0) throw new Exception("could not resolve " + host);

                Exception last = null;
                foreach (IPAddress a in addrs)
                {
                    try
                    {
                        c = new TcpClient(a.AddressFamily);
                        IAsyncResult ar = c.BeginConnect(a, port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                        {
                            c.Close();
                            throw new Exception("timed out (is the host listening, and is the port open in their firewall?)");
                        }
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
                TcpLink l = new TcpLink(c);
                Handshake.WriteHello(l.Stream, hello);

                byte result;
                string message;
                string hostVersion;
                if (!Handshake.ReadReply(l.Stream, out result, out message, out hostVersion) || result != Handshake.ResultOk)
                {
                    l.Close("rejected");
                    Fail(myAttempt, "Host refused: " + message);
                    return;
                }

                if (myAttempt != attempt) { l.Close("superseded"); return; }
                l.Start();
                link = l;
                status = "Connected to host, joining game...";
                state = State.HandshakeDone; // Main thread finishes the join in Tick().
            }
            catch (Exception e)
            {
                try { if (c != null) c.Close(); } catch (Exception) { }
                Fail(myAttempt, "Could not connect to " + host + ":" + port + " - " + e.Message);
            }
        }

        private void Fail(int myAttempt, string msg)
        {
            if (myAttempt != attempt) return;
            state = State.Failed;
            status = msg;
            Plugin.Log.LogWarning(msg);
        }

        /// <summary>Main-thread pump. Call every frame.</summary>
        public void Tick()
        {
            if (state == State.HandshakeDone && conn == null) FinishJoin();

            if (conn == null) return;

            if (!link.IsClosed && conn.IsDisconnectRequested)
                link.Close("left the game");

            conn.PumpIncoming();

            if (link.IsClosed)
            {
                string reason = link.CloseReason;
                Plugin.Log.LogInfo("Direct connection to host closed: " + reason);
                TearDown(true);
                state = State.Idle;
                status = "Disconnected (" + reason + ")";
            }
        }

        public void LateTick()
        {
            // External NetworkClients are never flushed by the HLAPI itself (NetworkClient.Update
            // bails out when it doesn't own an LLAPI host), so we flush every frame.
            if (conn != null && !conn.CleanedUp) conn.Flush();
        }

        private void FinishJoin()
        {
            NetworkManager nm = FindNetworkManager();
            try
            {
                conn = new DirectConnection(link, false, "host");
                conn.Setup(1, link.RemoteAddress);

                // NetworkClient(conn) registers the HLAPI system handlers (spawn, ownership, sync...).
                netClient = new NetworkClient(conn);

                if (nm != null)
                {
                    nm.networkAddress = link.RemoteAddress;
                    // Lets the game's NetworkManager register its client handlers and run OnStartClient.
                    nm.UseExternalClient(netClient);
                }
                else
                {
                    Plugin.Log.LogWarning("No NetworkManager found; joining with a bare NetworkClient. If nothing happens, press "
                        + Plugin.Cfg.DiagnosticsKey.Value + " and send the diagnostics file.");
                }

                conn.Registered = true;
                // Same as a freshly connected LLAPI client: fire the Connect message so the game
                // sends its ready / add-player requests and starts loading the host's world.
                conn.InvokeHandlerNoData(MsgType.Connect);

                state = State.Connected;
                status = "Connected to " + link.RemoteAddress;
                Plugin.Log.LogInfo("Joined host " + link.RemoteAddress + " via Direct Connect (NetworkManager: "
                    + (nm == null ? "none" : nm.GetType().FullName) + ")");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Joining failed inside the game's network code: " + e);
                status = "Join failed: " + e.Message + " (see BepInEx/LogOutput.log)";
                state = State.Failed;
                link.Close("join failed");
                TearDown(false);
            }
        }

        public void Disconnect(string reason)
        {
            attempt++;
            if (conn != null && conn.Registered)
            {
                // Let the game leave the session the normal way first.
                NetworkManager nm = FindNetworkManager();
                try
                {
                    if (nm != null && nm.client == netClient) nm.StopClient();
                }
                catch (Exception e) { Plugin.Log.LogWarning("StopClient threw: " + e.Message); }
            }
            if (link != null) link.Close(reason);
            TearDown(false);
            state = State.Idle;
            status = "Disconnected";
        }

        private void TearDown(bool notifyGame)
        {
            if (conn != null && !conn.CleanedUp)
            {
                conn.CleanedUp = true;
                if (notifyGame && conn.Registered)
                {
                    try { conn.InvokeHandlerNoData(MsgType.Disconnect); }
                    catch (Exception e) { Plugin.Log.LogWarning("Disconnect handler threw: " + e.Message); }
                }
                try
                {
                    if (netClient != null && NetworkClient.allClients.Contains(netClient)) netClient.Shutdown();
                }
                catch (Exception) { }
            }
            conn = null;
            netClient = null;
            link = null;
        }

        internal static NetworkManager FindNetworkManager()
        {
            if (NetworkManager.singleton != null) return NetworkManager.singleton;
            NetworkManager[] all = Resources.FindObjectsOfTypeAll<NetworkManager>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].gameObject.scene.IsValid()) return all[i];
            return null;
        }

        internal static bool ParseAddress(string input, int defaultPort, out string host, out int port)
        {
            host = null;
            port = defaultPort;
            if (string.IsNullOrEmpty(input)) return false;
            string s = input.Trim();
            if (s.StartsWith("["))
            {
                // [ipv6]:port
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
                else host = s; // hostname, IPv4, or bare IPv6
            }
            return !string.IsNullOrEmpty(host) && port > 0 && port < 65536;
        }
    }
}
