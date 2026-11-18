using System;
using System.Collections.Generic;
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
        public const int MaxFrameSize = 1024 * 1024;
        private const int MaxQueuedSends = 20000;

        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly object sendLock = new object();
        private readonly Queue<byte[]> sendQueue = new Queue<byte[]>();
        private readonly object inboxLock = new object();
        private readonly Queue<Frame> inbox = new Queue<Frame>();
        private volatile bool closed;
        private string closeReason = "";
        private long bytesIn;
        private long bytesOut;

        public readonly string RemoteAddress;

        public TcpLink(TcpClient client)
        {
            this.client = client;
            client.NoDelay = true;
            client.ReceiveBufferSize = 256 * 1024;
            client.SendBufferSize = 256 * 1024;
            try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }
            catch (Exception) { }
            stream = client.GetStream();
            RemoteAddress = DescribeEndPoint(client.Client.RemoteEndPoint);
        }

        public NetworkStream Stream { get { return stream; } }
        public bool IsClosed { get { return closed; } }
        public string CloseReason { get { return closeReason; } }
        public long BytesIn { get { return Interlocked.Read(ref bytesIn); } }
        public long BytesOut { get { return Interlocked.Read(ref bytesOut); } }

        public static string DescribeEndPoint(EndPoint ep)
        {
            IPEndPoint ip = ep as IPEndPoint;
            if (ip == null) return ep == null ? "?" : ep.ToString();
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
            Thread reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Name = "VDC reader " + RemoteAddress;
            reader.Start();
            Thread writer = new Thread(WriteLoop);
            writer.IsBackground = true;
            writer.Name = "VDC writer " + RemoteAddress;
            writer.Start();
        }

        public void Send(byte[] data, int count, int channel)
        {
            byte[] frame = new byte[5 + count];
            WriteInt32(frame, 0, count + 1);
            frame[4] = (byte)channel;
            Buffer.BlockCopy(data, 0, frame, 5, count);
            lock (sendLock)
            {
                if (closed) return;
                if (sendQueue.Count >= MaxQueuedSends)
                {
                    // The peer stopped reading; drop it rather than eating all memory.
                    CloseInternal("send queue overflow (peer not reading)");
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

        public void Close(string reason)
        {
            lock (sendLock) { CloseInternal(reason); }
        }

        // Caller must hold sendLock.
        private void CloseInternal(string reason)
        {
            if (closed) return;
            closeReason = reason;
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
                    if (!ReadExact(stream, header, 4)) { Close("connection closed by remote"); return; }
                    int len = ReadInt32(header, 0);
                    if (len < 1 || len > MaxFrameSize) { Close("invalid frame length " + len); return; }
                    byte[] body = new byte[len];
                    if (!ReadExact(stream, body, len)) { Close("connection closed by remote"); return; }
                    Interlocked.Add(ref bytesIn, len + 4);

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
                    lock (sendLock)
                    {
                        while (sendQueue.Count == 0 && !closed) Monitor.Wait(sendLock);
                        if (closed) return;
                        while (sendQueue.Count > 0) batch.Add(sendQueue.Dequeue());
                    }
                    for (int i = 0; i < batch.Count; i++)
                    {
                        stream.Write(batch[i], 0, batch[i].Length);
                        Interlocked.Add(ref bytesOut, batch[i].Length);
                    }
                    stream.Flush();
                    batch.Clear();
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
        public const uint Magic = 0x31434456; // "VDC1"

        public const byte ResultOk = 0;
        public const byte ResultBadPassword = 1;
        public const byte ResultVersionMismatch = 2;
        public const byte ResultNotHosting = 3;
        public const byte ResultFull = 4;
        public const byte ResultProtocolMismatch = 5;

        internal sealed class Hello
        {
            public int Protocol;
            public string ModVersion = "";
            public string GameVersion = "";
            public string Password = "";
            public string PlayerName = "";
            public string SteamId = "";
        }

        public static void WriteHello(Stream s, Hello h)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(Magic);
            w.Write(h.Protocol);
            w.Write(h.ModVersion ?? "");
            w.Write(h.GameVersion ?? "");
            w.Write(h.Password ?? "");
            w.Write(h.PlayerName ?? "");
            w.Write(h.SteamId ?? "");
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
            h.ModVersion = r.ReadString();
            h.GameVersion = r.ReadString();
            h.Password = r.ReadString();
            h.PlayerName = r.ReadString();
            h.SteamId = r.ReadString();
            return h;
        }

        public static void WriteReply(Stream s, byte result, string message, string hostGameVersion)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8);
            w.Write(Magic);
            w.Write(result);
            w.Write(message ?? "");
            w.Write(hostGameVersion ?? "");
            w.Flush();
            WriteBlock(s, ms.ToArray());
        }

        public static bool ReadReply(Stream s, out byte result, out string message, out string hostGameVersion)
        {
            result = 255;
            message = "no reply from host";
            hostGameVersion = "";
            byte[] block = ReadBlock(s);
            if (block == null) return false;
            BinaryReader r = new BinaryReader(new MemoryStream(block), Encoding.UTF8);
            if (r.ReadUInt32() != Magic) { message = "host is not running Volcanoids Direct Connect"; return false; }
            result = r.ReadByte();
            message = r.ReadString();
            hostGameVersion = r.ReadString();
            return true;
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
