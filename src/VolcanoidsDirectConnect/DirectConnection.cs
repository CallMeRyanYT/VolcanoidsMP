using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace VolcanoidsDirectConnect
{
    /// <summary>
    /// A UNet HLAPI connection whose bytes travel over our TCP link instead of Steam P2P / LLAPI.
    /// The game's networking code (NetworkServer / NetworkClient / NetworkManager) sees an ordinary
    /// NetworkConnection, so all of its message handlers, spawning and sync keep working unchanged.
    /// </summary>
    internal sealed class DirectConnection : NetworkConnection
    {
        public readonly TcpLink Link;
        public readonly bool ServerSide;
        public readonly string PlayerName;
        public bool Registered;
        public bool CleanedUp;
        private bool warnedChannel;

        public DirectConnection(TcpLink link, bool serverSide, string playerName)
        {
            Link = link;
            ServerSide = serverSide;
            PlayerName = playerName ?? "";
        }

        /// <summary>Builds the channel layout used for direct connections.</summary>
        public static HostTopology BuildTopology()
        {
            int channels = Mathf.Clamp(Plugin.Cfg.ChannelCount.Value, 2, 64);
            try
            {
                if (NetworkServer.active && NetworkServer.hostTopology != null)
                    channels = Math.Max(channels, NetworkServer.hostTopology.DefaultConfig.ChannelCount);
            }
            catch (Exception) { }
            try
            {
                if (NetworkManager.singleton != null && NetworkManager.singleton.channels != null)
                    channels = Math.Max(channels, NetworkManager.singleton.channels.Count + 2);
            }
            catch (Exception) { }

            // TCP is already reliable and ordered, so every channel is "ReliableSequenced". That also lets
            // the HLAPI split messages larger than the packet size into fragments automatically.
            ConnectionConfig cfg = new ConnectionConfig();
            try
            {
                cfg.PacketSize = 16384;
                cfg.FragmentSize = 1024;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not raise packet size, using defaults: " + e.Message);
            }
            for (int i = 0; i < channels; i++) cfg.AddChannel(QosType.ReliableSequenced);
            return new HostTopology(cfg, 32);
        }

        public void Setup(int connectionId, string address)
        {
            // hostId -1 means "not owned by the LLAPI transport": base.Disconnect() then never touches
            // NetworkTransport, it only clears the address (which we watch for, see IsDisconnectRequested).
            Initialize(address, -1, connectionId, BuildTopology());
        }

        /// <summary>The game called conn.Disconnect() (kick / leave): HLAPI clears the address.</summary>
        public bool IsDisconnectRequested
        {
            get { return Registered && string.IsNullOrEmpty(address); }
        }

        public override bool TransportSend(byte[] bytes, int numBytes, int channelId, out byte error)
        {
            if (Link.IsClosed)
            {
                error = (byte)NetworkError.WrongConnection;
                return false;
            }
            Link.Send(bytes, numBytes, channelId);
            error = (byte)NetworkError.Ok;
            return true;
        }

        /// <summary>Feeds queued network frames into the HLAPI (main thread only).</summary>
        public void PumpIncoming()
        {
            Frame f;
            int budget = 4096;
            while (budget-- > 0 && Link.TryReceive(out f))
            {
                int channel = f.Channel;
                int count = ChannelCount();
                if (channel >= count)
                {
                    if (!warnedChannel)
                    {
                        warnedChannel = true;
                        Plugin.Log.LogWarning("Received data on channel " + channel + " but only " + count
                            + " channels exist. Raise Advanced.ChannelCount in the config on BOTH computers.");
                    }
                    channel = 0;
                }
                try
                {
                    TransportReceive(f.Data, f.Data.Length, channel);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Error while the game handled a message from " + Describe() + ": " + e);
                }
            }
        }

        public void Flush()
        {
            try { FlushChannels(); }
            catch (Exception) { }
        }

        private int ChannelCount()
        {
            try
            {
                // m_Channels is private; the HLAPI exposes no getter, so ask via reflection once.
                if (channelsField == null)
                    channelsField = typeof(NetworkConnection).GetField("m_Channels",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Array arr = channelsField == null ? null : channelsField.GetValue(this) as Array;
                return arr == null ? 0 : arr.Length;
            }
            catch (Exception) { return 256; }
        }

        private static System.Reflection.FieldInfo channelsField;

        public string Describe()
        {
            string name = string.IsNullOrEmpty(PlayerName) ? "" : (PlayerName + " ");
            return name + "(" + Link.RemoteAddress + ", conn " + connectionId + ")";
        }

        public override string ToString()
        {
            return "DirectConnection " + Describe();
        }
    }

    /// <summary>Optional helpers to read the local SteamID (purely to keep per-player saves stable).</summary>
    internal static class SteamIdentity
    {
        public static string TryGetLocalSteamId()
        {
            try
            {
                foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    // Steamworks.NET
                    Type user = asm.GetType("Steamworks.SteamUser", false);
                    if (user != null)
                    {
                        System.Reflection.MethodInfo m = user.GetMethod("GetSteamID", Type.EmptyTypes);
                        if (m != null && m.IsStatic)
                        {
                            object id = m.Invoke(null, null);
                            string s = SteamIdToString(id);
                            if (s != null) return s;
                        }
                    }
                    // Facepunch.Steamworks
                    Type client = asm.GetType("Steamworks.SteamClient", false);
                    if (client != null)
                    {
                        System.Reflection.PropertyInfo p = client.GetProperty("SteamId");
                        if (p != null)
                        {
                            string s = SteamIdToString(p.GetValue(null, null));
                            if (s != null) return s;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (Plugin.Cfg.VerboseLogging.Value) Plugin.Log.LogInfo("SteamID lookup failed: " + e.Message);
            }
            return "";
        }

        private static string SteamIdToString(object id)
        {
            if (id == null) return null;
            Type t = id.GetType();
            System.Reflection.FieldInfo f = t.GetField("m_SteamID") ?? t.GetField("Value");
            object raw = f != null ? f.GetValue(id) : id;
            ulong v;
            if (ulong.TryParse(Convert.ToString(raw), out v) && v != 0) return v.ToString();
            return null;
        }

        public static bool LooksLikeSteamId(string s)
        {
            ulong v;
            return !string.IsNullOrEmpty(s) && ulong.TryParse(s, out v) && v > 76561190000000000UL;
        }
    }
}
