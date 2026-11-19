using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Steamworks;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace VolcanoidsDirectConnect
{
    /// <summary>
    /// Replaces <see cref="Transport.Current"/>. Every host the game creates becomes a <see cref="DirectHost"/>
    /// that carries the game's own Steam host inside it (when Steam is available and enabled) plus
    /// direct TCP connections. The game's networking code can't tell the two apart.
    /// </summary>
    internal sealed class DirectTransport : INetworkTransport
    {
        /// <summary>The game's original transport (Steam sockets), or null when Steam isn't available / DirectOnly mode.</summary>
        public readonly INetworkTransport Inner;

        public DirectTransport(INetworkTransport inner)
        {
            Inner = inner;
        }

        public NetworkHost AddHost(int port, string ip, bool listen)
        {
            NetworkHost inner = null;
            if (Inner != null)
            {
                try { inner = Inner.AddHost(port, ip, listen); }
                catch (Exception e) { Plugin.Log.LogWarning("Steam transport failed to create a host: " + e.Message); }
                if (inner == null) Plugin.Log.LogWarning("Steam transport gave no host - continuing with direct IP only.");
            }
            return new DirectHost(inner, listen);
        }

        // ------------------------------------------------------------------ join targets
        // The game's join flow identifies the server by a ulong "Steam ID". For direct joins we hand it a
        // made-up id and remember which IP:port it stands for; DirectHost.Connect() recognises it.
        // These ids have an invalid Steam universe, so they can never collide with a real Steam ID.
        private const ulong FakeIdBase = 0x0DC0000000000000UL;
        private static readonly Dictionary<string, Target> targets = new Dictionary<string, Target>();
        private static ulong nextTarget = 1;

        internal sealed class Target
        {
            public string Host;
            public int Port;
            public override string ToString() { return Host + ":" + Port; }
        }

        public static ulong RegisterTarget(string host, int port)
        {
            foreach (KeyValuePair<string, Target> kv in targets)
                if (kv.Value.Host == host && kv.Value.Port == port) return ulong.Parse(kv.Key);
            ulong id = FakeIdBase | nextTarget++;
            targets[id.ToString()] = new Target { Host = host, Port = port };
            return id;
        }

        public static bool TryGetTarget(string address, out Target target)
        {
            return targets.TryGetValue(address ?? "", out target);
        }

        public static bool IsDirectServerId(ulong id)
        {
            return targets.ContainsKey(id.ToString());
        }
    }

    /// <summary>A NetworkHost that multiplexes the game's Steam host and direct TCP peers.</summary>
    internal sealed class DirectHost : NetworkHost
    {
        public const int ReasonLinkLost = 9001;
        public const int ReasonConnectFailed = 9002;

        private const byte CtlPing = 1;
        private const byte CtlPong = 2;
        private const byte CtlBye = 3;

        internal enum PeerState { Connecting, Connected, Failed, Closed }

        internal sealed class Peer
        {
            public int Id;
            public bool ServerSide;
            public volatile PeerState State;
            public TcpLink Link;
            public string Address = "";
            public string Name = "";
            public string Error = "";
            public bool ConnectQueued;
            public bool ConnectDelivered;
            public bool DisconnectQueued;
            public int RemoteByeReason = -1;
            public int PingMs;
            public long LastPingSentMs;
            // Filled by the background connect thread (client side).
            public volatile TcpLink PendingLink;
            public volatile string PendingError;

            public string Describe()
            {
                string where = Link != null ? Link.RemoteAddress : "?";
                return (string.IsNullOrEmpty(Name) ? "" : Name + " ") + "(" + where + ", conn " + Id + ")";
            }
        }

        private sealed class Slot
        {
            public Peer Direct;   // set for direct peers
            public int InnerId;   // set for Steam connections
        }

        private sealed class Accepted
        {
            public TcpLink Link;
            public Handshake.Hello Hello;
        }

        private static readonly List<DirectHost> live = new List<DirectHost>();
        private static readonly Action<IntPtr> NoRelease = delegate { };

        private readonly NetworkHost inner;
        private readonly bool isServer;
        private readonly Dictionary<int, Slot> slots = new Dictionary<int, Slot>();
        private readonly Dictionary<int, int> innerToOuter = new Dictionary<int, int>();
        private readonly List<Peer> peers = new List<Peer>();
        private readonly Queue<int> connectEvents = new Queue<int>();
        private readonly Queue<KeyValuePair<int, int>> disconnectEvents = new Queue<KeyValuePair<int, int>>();
        private readonly object acceptLock = new object();
        private readonly Queue<Accepted> accepted = new Queue<Accepted>();
        private int roundRobin;
        private bool disposed;

        private TcpListener listener;
        private volatile bool listening;
        // Snapshots taken on the main thread for the handshake threads.
        private volatile bool snapServerActive;
        private volatile string snapScene = "";
        private volatile bool snapPassword;
        private volatile string snapHostName = "";
        private string gameVersion = "";

        public DirectHost(NetworkHost inner, bool listen)
        {
            this.inner = inner;
            isServer = listen;
            gameVersion = UnityEngine.Application.version;
            live.Add(this);
            if (listen && Plugin.Cfg.AcceptDirectPlayers.Value) StartListening(Plugin.Cfg.Port.Value);
            Plugin.Log.LogInfo("Network host created (" + (listen ? "server" : "client") + ", Steam: "
                + (inner != null ? "yes" : "no") + ", direct IP: " + (listen ? (listening ? "listening on " + ListenPort : "not listening") : "yes") + ")");
        }

        public static List<DirectHost> Live { get { return live; } }
        public bool IsServer { get { return isServer; } }
        public bool HasSteam { get { return inner != null; } }
        public bool IsListening { get { return listening; } }
        public int ListenPort { get; private set; }
        public string ListenError { get; private set; }
        public List<Peer> Peers { get { return peers; } }

        public override int MaxPacketSize { get { return inner != null ? inner.MaxPacketSize : 524288; } }

        // ------------------------------------------------------------------ listening (host side)

        public bool StartListening(int port)
        {
            StopListening();
            ListenError = null;
            try
            {
                // Dual-stack: accepts IPv4 (Tailscale 100.x, LAN, Radmin 26.x...) and IPv6.
                listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.DualMode = true;
                listener.Start(16);
            }
            catch (Exception)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Any, port);
                    listener.Start(16);
                }
                catch (Exception e2)
                {
                    ListenError = "Could not open TCP port " + port + ": " + e2.Message;
                    Plugin.Log.LogError(ListenError);
                    listener = null;
                    return false;
                }
            }
            ListenPort = port;
            listening = true;
            Snapshot();
            TcpListener l = listener;
            new Thread(delegate () { AcceptLoop(l); }) { IsBackground = true, Name = "VDC accept" }.Start();
            Plugin.Log.LogInfo("Direct Connect is accepting players on TCP port " + port + ".");
            return true;
        }

        public void StopListening()
        {
            listening = false;
            if (listener != null)
            {
                try { listener.Stop(); } catch (Exception) { }
                listener = null;
                Plugin.Log.LogInfo("Direct Connect stopped accepting new players.");
            }
        }

        private void AcceptLoop(TcpListener l)
        {
            while (listening && l == listener)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch (Exception)
                {
                    if (listening && l == listener) Thread.Sleep(100);
                    continue;
                }
                new Thread(delegate () { ServerHandshake(c); }) { IsBackground = true, Name = "VDC handshake" }.Start();
            }
        }

        private void ServerHandshake(TcpClient c)
        {
            TcpLink link = null;
            try
            {
                c.ReceiveTimeout = 10000;
                c.SendTimeout = 10000;
                link = new TcpLink(c);
                Handshake.Hello hello = Handshake.ReadHello(link.Stream);
                if (hello == null)
                {
                    Plugin.Log.LogWarning("Ignored a connection from " + link.RemoteAddress + " (not Volcanoids Direct Connect).");
                    link.Close("bad hello");
                    return;
                }

                Handshake.Reply rep = new Handshake.Reply
                {
                    Result = Handshake.ResultOk,
                    Message = "ok",
                    HostGameVersion = gameVersion,
                    SceneName = snapScene,
                    PasswordRequired = snapPassword,
                    HostName = snapHostName
                };
                if (hello.Protocol != ModInfo.ProtocolVersion)
                {
                    rep.Result = Handshake.ResultProtocolMismatch;
                    rep.Message = "Mod version mismatch: host has " + ModInfo.Version + ", you have " + hello.ModVersion + ". Install the same mod version.";
                }
                else if (hello.GameVersion != gameVersion)
                {
                    rep.Result = Handshake.ResultVersionMismatch;
                    rep.Message = "Game version mismatch: host has " + gameVersion + ", you have " + hello.GameVersion + ".";
                }
                else if (!snapServerActive || string.IsNullOrEmpty(snapScene))
                {
                    rep.Result = Handshake.ResultNotHosting;
                    rep.Message = "The host isn't in a multiplayer game yet.";
                }

                Handshake.WriteReply(link.Stream, rep);
                if (rep.Result != Handshake.ResultOk || hello.Mode != Handshake.ModeJoin)
                {
                    if (rep.Result != Handshake.ResultOk) Plugin.Log.LogWarning("Rejected " + hello.PlayerName + " (" + link.RemoteAddress + "): " + rep.Message);
                    link.CloseAfterFlush(rep.Message);
                    return;
                }

                link.Start();
                lock (acceptLock) { accepted.Enqueue(new Accepted { Link = link, Hello = hello }); }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Direct Connect handshake failed: " + e.Message);
                if (link != null) link.Close("handshake failed");
                else { try { c.Close(); } catch (Exception) { } }
            }
        }

        // ------------------------------------------------------------------ main-thread pump

        /// <summary>Called every frame by the runner, and at the start of each receive cycle.</summary>
        public static void PumpAll()
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                try { live[i].Pump(); }
                catch (Exception e) { Plugin.Log.LogError("Direct Connect pump error: " + e); }
            }
        }

        private void Snapshot()
        {
            snapServerActive = NetworkServer.active;
            try { snapScene = SceneManager.sceneCount > 0 ? SceneManager.GetSceneAt(0).name : ""; } catch (Exception) { snapScene = ""; }
            try { snapPassword = MultiplayerSettings.Current != null && !string.IsNullOrEmpty(MultiplayerSettings.Current.Password); } catch (Exception) { snapPassword = false; }
            try { snapHostName = Steam.IsInitialized ? SteamFriends.GetPersonaName() : Environment.UserName; } catch (Exception) { snapHostName = Environment.UserName; }
        }

        private void Pump()
        {
            if (disposed) return;
            if (isServer) Snapshot();

            // New players that finished the handshake.
            while (true)
            {
                Accepted a = null;
                lock (acceptLock) { if (accepted.Count > 0) a = accepted.Dequeue(); }
                if (a == null) break;
                Peer p = new Peer
                {
                    ServerSide = true,
                    State = PeerState.Connected,
                    Link = a.Link,
                    Name = a.Hello.PlayerName,
                    Address = PlayerAddress(a.Hello, a.Link),
                };
                p.Id = Allocate(new Slot { Direct = p });
                peers.Add(p);
                p.ConnectQueued = true;
                connectEvents.Enqueue(p.Id);
                Plugin.Log.LogInfo("Direct player connecting: " + p.Describe() + " as '" + p.Address + "'");
            }

            long now = TcpLink.NowMs;
            for (int i = peers.Count - 1; i >= 0; i--)
            {
                Peer p = peers[i];

                // Client side: background connect finished?
                if (p.State == PeerState.Connecting)
                {
                    if (p.PendingLink != null)
                    {
                        p.Link = p.PendingLink;
                        p.State = PeerState.Connected;
                        p.ConnectQueued = true;
                        connectEvents.Enqueue(p.Id);
                        Plugin.Log.LogInfo("Connected to host " + p.Link.RemoteAddress + " via Direct Connect.");
                    }
                    else if (p.PendingError != null)
                    {
                        p.Error = p.PendingError;
                        p.State = PeerState.Failed;
                        DirectConnectRunner.Notify("Could not join: " + p.Error);
                        Plugin.Log.LogWarning("Direct join failed: " + p.Error);
                    }
                    continue;
                }

                if (p.Link == null || p.DisconnectQueued) continue;

                if (!p.Link.IsClosed)
                {
                    if (now - p.LastPingSentMs > 2000)
                    {
                        p.LastPingSentMs = now;
                        Span<byte> ts = stackalloc byte[8];
                        BitConverter.TryWriteBytes(ts, now);
                        p.Link.SendControl(CtlPing, ts);
                    }
                    int timeout = Math.Max(5, Plugin.Cfg.TimeoutSeconds.Value) * 1000;
                    if (p.Link.MsSinceLastReceive > timeout) p.Link.Close("no response for " + (timeout / 1000) + " s");
                }

                // Link gone: report the disconnect once everything it sent has been handed to the game.
                if (p.Link.IsClosed && (p.Link.PendingCount == 0 || !p.ConnectDelivered))
                {
                    p.State = PeerState.Closed;
                    p.DisconnectQueued = true;
                    int reason = p.RemoteByeReason >= 0 ? 1000 + p.RemoteByeReason : ReasonLinkLost;
                    Plugin.Log.LogInfo("Direct connection closed: " + p.Describe() + " - " + p.Link.CloseReason);
                    if (p.ConnectDelivered) disconnectEvents.Enqueue(new KeyValuePair<int, int>(p.Id, reason));
                    else Release(p.Id);
                }
            }
        }

        private static string PlayerAddress(Handshake.Hello h, TcpLink link)
        {
            // The game keys player data (inventory, position...) on this string, like a Steam join does.
            if (h.SteamId >= 0x0110000100000000UL && (h.SteamId >> 56) == 0x01) return "steamid:" + h.SteamId;
            string name = string.IsNullOrEmpty(h.PlayerName) ? "player" : h.PlayerName;
            return "direct:" + name + "@" + link.RemoteIp;
        }

        // ------------------------------------------------------------------ id mapping

        private int Allocate(Slot s)
        {
            int id = 1; // 0 is the host's local client
            while (slots.ContainsKey(id)) id++;
            slots[id] = s;
            return id;
        }

        private void Release(int id)
        {
            Slot s;
            if (!slots.TryGetValue(id, out s)) return;
            slots.Remove(id);
            if (s.Direct != null) peers.Remove(s.Direct);
            else innerToOuter.Remove(s.InnerId);
        }

        private int MapInner(int innerId)
        {
            int outer;
            if (innerToOuter.TryGetValue(innerId, out outer)) return outer;
            outer = Allocate(new Slot { InnerId = innerId });
            innerToOuter[innerId] = outer;
            return outer;
        }

        private bool TryGetInner(int id, out int innerId)
        {
            Slot s;
            if (inner != null && slots.TryGetValue(id, out s) && s.Direct == null)
            {
                innerId = s.InnerId;
                return true;
            }
            innerId = -1;
            return false;
        }

        private Peer GetPeer(int id)
        {
            Slot s;
            return slots.TryGetValue(id, out s) ? s.Direct : null;
        }

        // ------------------------------------------------------------------ NetworkHost API

        public override int Connect(string address, int port)
        {
            DirectTransport.Target t;
            if (port == 0 && DirectTransport.TryGetTarget(address, out t))
            {
                Peer p = new Peer { ServerSide = false, State = PeerState.Connecting, Address = t.ToString(), Name = "host" };
                p.Id = Allocate(new Slot { Direct = p });
                peers.Add(p);
                new Thread(delegate () { ClientConnect(p, t); }) { IsBackground = true, Name = "VDC connect" }.Start();
                return p.Id;
            }
            if (inner == null)
            {
                Plugin.Log.LogWarning("Can't connect to '" + address + "' - Steam networking is not available. Use the Direct Connect window (F8) to join by IP.");
                return -1;
            }
            int innerId = inner.Connect(address, port);
            return innerId < 0 ? -1 : MapInner(innerId);
        }

        private static void ClientConnect(Peer p, DirectTransport.Target t)
        {
            try
            {
                Handshake.Reply rep;
                TcpLink link = DirectConnectRunner.OpenLink(t.Host, t.Port, Handshake.ModeJoin, out rep);
                if (rep.Result != Handshake.ResultOk)
                {
                    if (link != null) link.Close("rejected");
                    p.PendingError = rep.Message;
                    return;
                }
                link.Start();
                p.PendingLink = link;
            }
            catch (Exception e)
            {
                p.PendingError = e.Message;
            }
        }

        public override void Disconnect(int connectionId, DisconnectReason reason, string debugMsg)
        {
            int innerId;
            if (TryGetInner(connectionId, out innerId)) { inner.Disconnect(innerId, reason, debugMsg); return; }
            Peer p = GetPeer(connectionId);
            if (p == null) return;
            if (p.Link != null && !p.Link.IsClosed)
            {
                p.Link.SendControl(CtlBye, new byte[] { (byte)reason });
                p.Link.CloseAfterFlush("disconnected (" + reason + (string.IsNullOrEmpty(debugMsg) ? "" : ", " + debugMsg) + ")");
            }
            if (p.DisconnectQueued) return;
            p.DisconnectQueued = true;
            p.State = PeerState.Closed;
            Plugin.Log.LogInfo("Direct connection " + p.Describe() + " disconnected by the game: " + reason);
            // Same as the Steam host: a game-initiated disconnect is reported back through ReceiveDisconnect.
            if (p.ConnectDelivered) disconnectEvents.Enqueue(new KeyValuePair<int, int>(p.Id, 1000 + (int)reason));
            else Release(p.Id);
        }

        public override void Flush(int connectionId)
        {
            int innerId;
            if (TryGetInner(connectionId, out innerId)) inner.Flush(innerId);
            // Direct links are flushed continuously by their writer thread.
        }

        public override bool GetCurrentPing(int connectionId, out int pingMs)
        {
            int innerId;
            if (TryGetInner(connectionId, out innerId)) return inner.GetCurrentPing(innerId, out pingMs);
            Peer p = GetPeer(connectionId);
            pingMs = p != null ? p.PingMs : 0;
            return p != null && p.State == PeerState.Connected;
        }

        public override bool GetConnectionInfo(int connectionId, out string address, out int port, out int state)
        {
            int innerId;
            if (TryGetInner(connectionId, out innerId)) return inner.GetConnectionInfo(innerId, out address, out port, out state);
            if (!isServer) Pump(); // the join screen polls this while connecting
            Peer p = GetPeer(connectionId);
            if (p == null)
            {
                address = null;
                port = 0;
                state = 0;
                return false;
            }
            address = p.Address;
            port = 0;
            // Same numbers as ESteamNetworkingConnectionState, which the game's join code checks.
            switch (p.State)
            {
                case PeerState.Connecting: state = 1; break;
                case PeerState.Connected: state = 3; break;
                case PeerState.Closed: state = 4; break;
                default: state = 5; break;
            }
            return true;
        }

        public override bool ReceiveConnect(out int connectionId)
        {
            Pump();
            int innerId;
            if (inner != null && inner.ReceiveConnect(out innerId))
            {
                connectionId = MapInner(innerId);
                return true;
            }
            while (connectEvents.Count > 0)
            {
                int id = connectEvents.Dequeue();
                Peer p = GetPeer(id);
                if (p == null || p.DisconnectQueued) continue;
                p.ConnectDelivered = true;
                connectionId = id;
                return true;
            }
            connectionId = 0;
            return false;
        }

        public override bool ReceiveDisconnect(out int connectionId, out int connectionEndReason)
        {
            int innerId;
            if (inner != null && inner.ReceiveDisconnect(out innerId, out connectionEndReason))
            {
                int outer;
                if (innerToOuter.TryGetValue(innerId, out outer))
                {
                    connectionId = outer;
                    Release(outer);
                }
                else connectionId = MapInnerOnce(innerId);
                return true;
            }
            if (disconnectEvents.Count > 0)
            {
                KeyValuePair<int, int> e = disconnectEvents.Dequeue();
                connectionId = e.Key;
                connectionEndReason = e.Value;
                Release(e.Key);
                return true;
            }
            connectionId = 0;
            connectionEndReason = 0;
            return false;
        }

        // An unknown inner id is disconnecting: give it a throwaway outer id the game won't find.
        private int MapInnerOnce(int innerId)
        {
            int id = 1;
            while (slots.ContainsKey(id)) id++;
            return id;
        }

        public override bool ReceiveData(out int connectionId, out NetworkRawBuffer data)
        {
            int innerId;
            if (inner != null && inner.ReceiveData(out innerId, out data))
            {
                connectionId = MapInner(innerId);
                return true;
            }

            int n = peers.Count;
            for (int k = 0; k < n; k++)
            {
                Peer p = peers[(roundRobin + k) % n];
                if (!p.ConnectDelivered || p.Link == null) continue;
                Frame f;
                while (p.Link.TryReceive(out f))
                {
                    if (f.Channel == TcpLink.ControlChannel)
                    {
                        HandleControl(p, f.Data);
                        continue;
                    }
                    if (p.DisconnectQueued) continue; // game already dropped this connection
                    roundRobin = (roundRobin + k + 1) % Math.Max(1, n);
                    connectionId = p.Id;
                    data = new NetworkRawBuffer(f.Data, IntPtr.Zero, NoRelease);
                    return true;
                }
            }
            connectionId = 0;
            data = default(NetworkRawBuffer);
            return false;
        }

        private static void HandleControl(Peer p, byte[] d)
        {
            if (d.Length == 0) return;
            switch (d[0])
            {
                case CtlPing:
                    p.Link.SendControl(CtlPong, new ReadOnlySpan<byte>(d, 1, d.Length - 1));
                    break;
                case CtlPong:
                    if (d.Length >= 9) p.PingMs = (int)Math.Max(0, TcpLink.NowMs - BitConverter.ToInt64(d, 1));
                    break;
                case CtlBye:
                    if (d.Length >= 2) p.RemoteByeReason = d[1];
                    break;
            }
        }

        public override SendResult Send(int connectionId, int channelId, ReadOnlySpan<byte> buffer)
        {
            int innerId;
            if (TryGetInner(connectionId, out innerId)) return inner.Send(innerId, channelId, buffer);
            Peer p = GetPeer(connectionId);
            if (p == null) return new SendResult(3);
            if (p.DisconnectQueued) return SendResult.Success; // mirrors the Steam host
            if (p.Link == null || p.Link.IsClosed) return new SendResult(3);
            p.Link.Send(buffer, channelId);
            return SendResult.Success;
        }

        public override string GetReasonDesc(int connectionEndReason)
        {
            if (connectionEndReason >= 1000 && connectionEndReason <= 1999) return ((DisconnectReason)(connectionEndReason - 1000)).ToString();
            if (connectionEndReason == ReasonLinkLost) return "Direct connection lost";
            if (connectionEndReason == ReasonConnectFailed) return "Direct connection failed";
            return inner != null ? inner.GetReasonDesc(connectionEndReason) : connectionEndReason.ToString();
        }

        public override bool TryGetReason(int connectionEndReason, out DisconnectReason disconnectReason)
        {
            if (connectionEndReason >= 1000 && connectionEndReason <= 1999)
            {
                disconnectReason = (DisconnectReason)(connectionEndReason - 1000);
                return true;
            }
            if (inner != null) return inner.TryGetReason(connectionEndReason, out disconnectReason);
            disconnectReason = DisconnectReason.Invalid;
            return false;
        }

        public override void Dispose()
        {
            if (disposed) return;
            disposed = true;
            live.Remove(this);
            StopListening();
            foreach (Peer p in peers)
            {
                if (p.Link != null && !p.Link.IsClosed)
                {
                    p.Link.SendControl(CtlBye, new byte[] { (byte)DisconnectReason.Shutdown });
                    p.Link.CloseAfterFlush("host closed");
                }
            }
            peers.Clear();
            slots.Clear();
            innerToOuter.Clear();
            if (inner != null)
            {
                try { inner.Dispose(); } catch (Exception e) { Plugin.Log.LogWarning("Steam host dispose: " + e.Message); }
            }
            Plugin.Log.LogInfo("Network host closed (" + (isServer ? "server" : "client") + ").");
        }
    }
}
