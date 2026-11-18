using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace VolcanoidsDirectConnect
{
    /// <summary>
    /// Host side: accepts TCP joiners and hands them to the game's NetworkServer as external connections.
    /// </summary>
    internal sealed class DirectServer
    {
        private sealed class PendingPeer
        {
            public TcpLink Link;
            public Handshake.Hello Hello;
        }

        private TcpListener listener;
        private volatile bool running;
        private readonly object pendingLock = new object();
        private readonly Queue<PendingPeer> pending = new Queue<PendingPeer>();
        private readonly List<DirectConnection> connections = new List<DirectConnection>();

        // Snapshots written on the main thread, read by the handshake threads.
        private volatile bool serverActive;
        private volatile int liveCount;
        private string password = "";
        private string gameVersion = "";
        private bool requireSameVersion = true;
        private int maxPlayers = 3;

        public int Port { get; private set; }
        public bool IsRunning { get { return running; } }
        public List<DirectConnection> Connections { get { return connections; } }
        public string LastError { get; private set; }

        public bool Start(int port)
        {
            Stop();
            LastError = null;
            password = Plugin.Cfg.Password.Value ?? "";
            gameVersion = Application.version;
            requireSameVersion = Plugin.Cfg.RequireSameGameVersion.Value;
            maxPlayers = Math.Max(1, Plugin.Cfg.MaxDirectPlayers.Value);
            serverActive = NetworkServer.active;

            try
            {
                // Dual-stack listener: accepts IPv4 (Tailscale 100.x, LAN, Radmin 26.x...) and IPv6.
                listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)27, false); // IPV6_V6ONLY off
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
                    LastError = "Could not listen on port " + port + ": " + e2.Message;
                    Plugin.Log.LogError(LastError);
                    listener = null;
                    return false;
                }
            }

            Port = port;
            running = true;
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Name = "VDC accept";
            t.Start();
            Plugin.Log.LogInfo("Direct Connect host is listening on TCP port " + port + ". Friends can join with <your IP>:" + port);
            return true;
        }

        public void Stop()
        {
            if (!running && listener == null && connections.Count == 0) return;
            running = false;
            if (listener != null)
            {
                try { listener.Stop(); } catch (Exception) { }
                listener = null;
            }
            lock (pendingLock)
            {
                while (pending.Count > 0) pending.Dequeue().Link.Close("host stopped");
            }
            for (int i = 0; i < connections.Count; i++)
            {
                connections[i].Link.Close("host stopped");
                Cleanup(connections[i], false);
            }
            connections.Clear();
            Plugin.Log.LogInfo("Direct Connect host stopped.");
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try
                {
                    c = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (running) Thread.Sleep(100);
                    continue;
                }
                TcpClient captured = c;
                Thread h = new Thread(delegate () { DoHandshake(captured); });
                h.IsBackground = true;
                h.Name = "VDC handshake";
                h.Start();
            }
        }

        private void DoHandshake(TcpClient c)
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
                    Plugin.Log.LogWarning("Rejected " + link.RemoteAddress + ": not a Volcanoids Direct Connect client.");
                    link.Close("bad hello");
                    return;
                }

                byte result = Handshake.ResultOk;
                string msg = "Welcome!";
                if (hello.Protocol != ModInfo.ProtocolVersion)
                {
                    result = Handshake.ResultProtocolMismatch;
                    msg = "Mod version mismatch: host has " + ModInfo.Version + ", you have " + hello.ModVersion + ". Install the same mod version.";
                }
                else if (!serverActive)
                {
                    result = Handshake.ResultNotHosting;
                    msg = "The host is not in a game right now. Ask them to load their save (as a co-op/host game) first.";
                }
                else if (password.Length > 0 && hello.Password != password)
                {
                    result = Handshake.ResultBadPassword;
                    msg = "Wrong password.";
                }
                else if (requireSameVersion && hello.GameVersion != gameVersion)
                {
                    result = Handshake.ResultVersionMismatch;
                    msg = "Game version mismatch: host has " + gameVersion + ", you have " + hello.GameVersion + ".";
                }
                else if (liveCount >= maxPlayers)
                {
                    result = Handshake.ResultFull;
                    msg = "Host is full (" + maxPlayers + " direct players).";
                }

                Handshake.WriteReply(link.Stream, result, msg, gameVersion);
                if (result != Handshake.ResultOk)
                {
                    Plugin.Log.LogWarning("Rejected " + hello.PlayerName + " (" + link.RemoteAddress + "): " + msg);
                    link.Close(msg);
                    return;
                }

                link.Start();
                PendingPeer p = new PendingPeer();
                p.Link = link;
                p.Hello = hello;
                lock (pendingLock) { pending.Enqueue(p); }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Handshake failed: " + e.Message);
                if (link != null) link.Close("handshake failed");
                else { try { c.Close(); } catch (Exception) { } }
            }
        }

        /// <summary>Main-thread pump. Call every frame.</summary>
        public void Tick()
        {
            serverActive = NetworkServer.active;
            liveCount = connections.Count;

            // 1. Promote handshaken peers to real game connections.
            while (true)
            {
                PendingPeer p = null;
                lock (pendingLock) { if (pending.Count > 0) p = pending.Dequeue(); }
                if (p == null) break;
                Register(p);
            }

            // 2. Pump data / detect disconnects.
            for (int i = connections.Count - 1; i >= 0; i--)
            {
                DirectConnection conn = connections[i];
                if (!conn.Link.IsClosed && conn.IsDisconnectRequested)
                {
                    Plugin.Log.LogInfo("Game disconnected " + conn.Describe());
                    conn.Link.Close("kicked or disconnected by host");
                }
                if (!conn.CleanedUp) conn.PumpIncoming();
                if (conn.Link.IsClosed)
                {
                    Plugin.Log.LogInfo("Direct player left: " + conn.Describe() + " - " + conn.Link.CloseReason);
                    Cleanup(conn, true);
                    connections.RemoveAt(i);
                }
            }
        }

        public void LateTick()
        {
            for (int i = 0; i < connections.Count; i++)
                if (!connections[i].CleanedUp) connections[i].Flush();
        }

        private void Register(PendingPeer p)
        {
            if (!NetworkServer.active)
            {
                p.Link.Close("host is no longer in a game");
                return;
            }

            DirectConnection conn = new DirectConnection(p.Link, true, p.Hello.PlayerName);
            int id = FindFreeConnectionId();
            conn.Setup(id, ChooseAddress(p));
            connections.Add(conn);

            bool ok;
            try
            {
                // Adds it to NetworkServer.connections, gives it the server's message handlers
                // and fires MsgType.Connect, exactly like the game's own Steam connections.
                ok = NetworkServer.AddExternalConnection(conn);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("The game threw while accepting " + conn.Describe() + ": " + e);
                ok = false;
            }

            if (!ok)
            {
                Plugin.Log.LogError("NetworkServer refused connection " + conn.Describe() + ". Press "
                    + Plugin.Cfg.DiagnosticsKey.Value + " and send the diagnostics file.");
                p.Link.Close("server refused the connection");
                conn.CleanedUp = true;
                return;
            }
            conn.Registered = true;
            Plugin.Log.LogInfo("Direct player joined: " + conn.Describe() + " address seen by game: '" + conn.address + "'");
        }

        private string ChooseAddress(PendingPeer p)
        {
            string mode = (Plugin.Cfg.ConnectionAddressMode.Value ?? "Auto").Trim().ToLowerInvariant();
            bool hasSteam = SteamIdentity.LooksLikeSteamId(p.Hello.SteamId);
            if (mode == "ipport") return p.Link.RemoteAddress;
            if (mode == "steamid") return hasSteam ? p.Hello.SteamId : "0";
            return hasSteam ? p.Hello.SteamId : p.Link.RemoteAddress;
        }

        // Steam players keep using the game's built-in multiplayer at the same time. Their connection
        // ids are handed out by the game's transport starting at 1, and the HLAPI silently overwrites
        // whatever sits in that slot. Direct connections therefore live in their own high id range.
        internal const int FirstDirectConnectionId = 1000;

        private static int FindFreeConnectionId()
        {
            var list = NetworkServer.connections;
            for (int i = FirstDirectConnectionId; i < list.Count; i++)
                if (list[i] == null) return i;
            return Math.Max(FirstDirectConnectionId, list.Count);
        }

        private void Cleanup(DirectConnection conn, bool notifyGame)
        {
            if (conn.CleanedUp) return;
            conn.CleanedUp = true;
            if (!conn.Registered) return;
            try
            {
                bool stillThere = NetworkServer.active
                    && conn.connectionId < NetworkServer.connections.Count
                    && NetworkServer.connections[conn.connectionId] == conn;
                if (stillThere)
                {
                    // Mirror what NetworkServer does when a normal client drops.
                    if (notifyGame) conn.InvokeHandlerNoData(MsgType.Disconnect);
                    NetworkServer.RemoveExternalConnection(conn.connectionId);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Error while removing " + conn.Describe() + ": " + e.Message);
            }
            try { conn.Dispose(); } catch (Exception) { }
        }
    }
}
