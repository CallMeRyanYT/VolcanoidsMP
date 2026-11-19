using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VolcanoidsDirectConnect
{
    /// <summary>One message received from the network, waiting for the Unity main thread.</summary>
    internal struct Frame
    {
        public int Channel;
        public byte[] Data;
    }

    /// <summary>
    /// A framed, threaded TCP link. Frames are: [int32 length][byte channel][payload].
    /// Reading and writing happen on background threads; the Unity main thread only
    /// enqueues outgoing frames and drains incoming ones, so the game never blocks on the network.
    /// </summary>
    internal sealed class TcpLink
    {
        public const int MaxFrameSize = 2 * 1024 * 1024;
        public const byte ControlChannel = 255;
        private const int MaxQueuedSends = 50000;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly object sendLock = new object();
        private readonly Queue<byte[]> sendQueue = new Queue<byte[]>();
        private readonly object inboxLock = new object();
        private readonly Queue<Frame> inbox = new Queue<Frame>();
        private volatile bool closed;
        private bool closeWhenDrained;
        private string closeReason = "";
        private long bytesIn;
        private long bytesOut;
        private long lastReceiveMs;

        public readonly string RemoteAddress;
        public readonly IPAddress RemoteIp;

        public TcpLink(TcpClient client)
        {
            this.client = client;
            client.NoDelay = true;
            client.ReceiveBufferSize = 512 * 1024;
            client.SendBufferSize = 512 * 1024;
            try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }
            catch (Exception) { }
            stream = client.GetStream();
            IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
            RemoteIp = ep == null ? IPAddress.None : (ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address);
            RemoteAddress = DescribeEndPoint(ep);
            lastReceiveMs = NowMs;
        }

        public static long NowMs { get { return Clock.ElapsedMilliseconds; } }

        public NetworkStream Stream { get { return stream; } }
        public bool IsClosed { get { return closed; } }
        public string CloseReason { get { return closeReason; } }
        public long BytesIn { get { return Interlocked.Read(ref bytesIn); } }
        public long BytesOut { get { return Interlocked.Read(ref bytesOut); } }
        public long MsSinceLastReceive { get { return NowMs - Interlocked.Read(ref lastReceiveMs); } }
        public int PendingCount { get { lock (inboxLock) { return inbox.Count; } } }

        public static string DescribeEndPoint(IPEndPoint ip)
        {
            if (ip == null) return "?";
            IPAddress addr = ip.Address;
            if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
            if (addr.AddressFamily == AddressFamily.InterNetworkV6) return "[" + addr + "]:" + ip.Port;
            return addr + ":" + ip.Port;
        }

        /// <summary>Starts the background reader/writer threads (call after the handshake).</summary>
        public void Start()
        {
            client.ReceiveTimeout = 0;
            client.SendTimeout = 0;
            lastReceiveMs = NowMs;
            Thread reader = new Thread(ReadLoop) { IsBackground = true, Name = "VDC reader " + RemoteAddress };
            reader.Start();
            Thread writer = new Thread(WriteLoop) { IsBackground = true, Name = "VDC writer " + RemoteAddress };
            writer.Start();
        }

        public void Send(ReadOnlySpan<byte> data, int channel)
        {
            byte[] frame = new byte[5 + data.Length];
            WriteInt32(frame, 0, data.Length + 1);
            frame[4] = (byte)channel;
            data.CopyTo(new Span<byte>(frame, 5, data.Length));
            Enqueue(frame);
        }

        public void SendControl(byte type, ReadOnlySpan<byte> payload)
        {
            byte[] frame = new byte[6 + payload.Length];
            WriteInt32(frame, 0, payload.Length + 2);
            frame[4] = ControlChannel;
            frame[5] = type;
            payload.CopyTo(new Span<byte>(frame, 6, payload.Length));
            Enqueue(frame);
        }

        private void Enqueue(byte[] frame)
        {
            lock (sendLock)
            {
                if (closed || closeWhenDrained) return;
                if (sendQueue.Count >= MaxQueuedSends)
                {
                    // The peer stopped reading; drop it rather than eating all memory.
                    CloseInternal("send queue overflow (peer stopped reading)");
                    return;
                }
                sendQueue.Enqueue(frame);
                Monitor.Pulse(sendLock);
            }
        }

        public bool TryReceive(out Frame frame)
        {
            lock (inboxLock)
            {
                if (inbox.Count > 0)
                {
                    frame = inbox.Dequeue();
                    return true;
                }
            }
            frame = default(Frame);
            return false;
        }

        /// <summary>Closes immediately, dropping anything not yet sent.</summary>
        public void Close(string reason)
        {
            lock (sendLock) { CloseInternal(reason); }
        }

        /// <summary>Sends everything already queued (e.g. a goodbye message), then closes.</summary>
        public void CloseAfterFlush(string reason)
        {
            lock (sendLock)
            {
                if (closed) return;
                closeReason = reason;
                closeWhenDrained = true;
                Monitor.PulseAll(sendLock);
            }
            // Safety net in case the peer never reads.
            ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(3000); Close(reason); });
        }

        // Caller must hold sendLock.
        private void CloseInternal(string reason)
        {
            if (closed) return;
            if (!closeWhenDrained || string.IsNullOrEmpty(closeReason)) closeReason = reason;
            closed = true;
            Monitor.PulseAll(sendLock);
            try { client.Close(); } catch (Exception) { }
        }

        private void ReadLoop()
        {
            byte[] header = new byte[4];
            try
            {
                while (!closed)
                {
                    if (!ReadExact(stream, header, 4)) { Close("connection closed by the other side"); return; }
                    int len = ReadInt32(header, 0);
                    if (len < 1 || len > MaxFrameSize) { Close("invalid frame length " + len); return; }
                    byte[] body = new byte[len];
                    if (!ReadExact(stream, body, len)) { Close("connection closed by the other side"); return; }
                    Interlocked.Add(ref bytesIn, len + 4);
                    Interlocked.Exchange(ref lastReceiveMs, NowMs);

                    Frame f;
                    f.Channel = body[0];
                    f.Data = new byte[len - 1];
                    Buffer.BlockCopy(body, 1, f.Data, 0, len - 1);
                    lock (inboxLock) { inbox.Enqueue(f); }
                }
            }
            catch (Exception e)
            {
                Close(closed ? closeReason : ("read error: " + e.Message));
            }
        }

        private void WriteLoop()
        {
            List<byte[]> batch = new List<byte[]>();
            try
            {
                while (true)
                {
                    bool drainedAndClosing;
                    lock (sendLock)
                    {
                        while (sendQueue.Count == 0 && !closed && !closeWhenDrained) Monitor.Wait(sendLock);
                        if (closed) return;
                        while (sendQueue.Count > 0) batch.Add(sendQueue.Dequeue());
                        drainedAndClosing = closeWhenDrained;
                    }
                    for (int i = 0; i < batch.Count; i++)
                    {
                        stream.Write(batch[i], 0, batch[i].Length);
                        Interlocked.Add(ref bytesOut, batch[i].Length);
                    }
                    stream.Flush();
                    batch.Clear();
                    if (drainedAndClosing)
                    {
                        lock (sendLock)
                        {
                            if (sendQueue.Count == 0)
                            {
                                try { client.Client.Shutdown(SocketShutdown.Send); } catch (Exception) { }
                                Thread.Sleep(100);
                                CloseInternal(closeReason);
                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Close(closed ? closeReason : ("write error: " + e.Message));
            }
        }

        internal static bool ReadExact(Stream s, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = s.Read(buffer, offset, count - offset);
                if (n <= 0) return false;
                offset += n;
            }
            return true;
        }

        internal static void WriteInt32(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        internal static int ReadInt32(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        }
    }

    /// <summary>The small hello exchanged before any game traffic.</summary>
    internal static class Handshake
    {
        public const uint Magic = 0x32434456; // "VDC2"

        public const byte ModeProbe = 0;  // "what are you running?" - used before loading the host's world
        public const byte ModeJoin = 1;   // real game connection

        public const byte ResultOk = 0;
        public const byte ResultVersionMismatch = 2;
        public const byte ResultNotHosting = 3;
        public const byte ResultProtocolMismatch = 5;

        internal sealed class Hello
        {
            public int Protocol;
            public byte Mode;
            public string ModVersion = "";
            public string GameVersion = "";
            public string PlayerName = "";
            public ulong SteamId;
        }

        internal sealed class Reply
        {
            public byte Result = 255;
            public string Message = "no reply from host";
            public string HostGameVersion = "";
            public string SceneName = "";
            public bool PasswordRequired;
            public string HostName = "";
        }

        public static void WriteHello(Stream s, Hello h)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(Magic);
            w.Write(h.Protocol);
            w.Write(h.Mode);
            w.Write(h.ModVersion ?? "");
            w.Write(h.GameVersion ?? "");
            w.Write(h.PlayerName ?? "");
            w.Write(h.SteamId);
            w.Flush();
            WriteBlock(s, ms.ToArray());
        }

        public static Hello ReadHello(Stream s)
        {
            byte[] block = ReadBlock(s);
            if (block == null) return null;
            BinaryReader r = new BinaryReader(new MemoryStream(block), Encoding.UTF8);
            if (r.ReadUInt32() != Magic) return null;
            Hello h = new Hello();
            h.Protocol = r.ReadInt32();
            h.Mode = r.ReadByte();
            h.ModVersion = r.ReadString();
            h.GameVersion = r.ReadString();
            h.PlayerName = r.ReadString();
            h.SteamId = r.ReadUInt64();
            return h;
        }

        public static void WriteReply(Stream s, Reply rep)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(Magic);
            w.Write(rep.Result);
            w.Write(rep.Message ?? "");
            w.Write(rep.HostGameVersion ?? "");
            w.Write(rep.SceneName ?? "");
            w.Write(rep.PasswordRequired);
            w.Write(rep.HostName ?? "");
            w.Flush();
            WriteBlock(s, ms.ToArray());
        }

        public static Reply ReadReply(Stream s)
        {
            Reply rep = new Reply();
            byte[] block = ReadBlock(s);
            if (block == null) return rep;
            BinaryReader r = new BinaryReader(new MemoryStream(block), Encoding.UTF8);
            if (r.ReadUInt32() != Magic)
            {
                rep.Message = "the host is not running the same Volcanoids Direct Connect version";
                return rep;
            }
            rep.Result = r.ReadByte();
            rep.Message = r.ReadString();
            rep.HostGameVersion = r.ReadString();
            rep.SceneName = r.ReadString();
            rep.PasswordRequired = r.ReadBoolean();
            rep.HostName = r.ReadString();
            return rep;
        }

        private static void WriteBlock(Stream s, byte[] data)
        {
            byte[] len = new byte[4];
            TcpLink.WriteInt32(len, 0, data.Length);
            s.Write(len, 0, 4);
            s.Write(data, 0, data.Length);
            s.Flush();
        }

        private static byte[] ReadBlock(Stream s)
        {
            byte[] len = new byte[4];
            if (!TcpLink.ReadExact(s, len, 4)) return null;
            int n = TcpLink.ReadInt32(len, 0);
            if (n < 4 || n > 64 * 1024) return null;
            byte[] data = new byte[n];
            if (!TcpLink.ReadExact(s, data, n)) return null;
            return data;
        }
    }
}
