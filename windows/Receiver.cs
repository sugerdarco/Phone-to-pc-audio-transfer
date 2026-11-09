// UDP receiver + jitter buffer.
//
// Wire protocol (UDP port 45999), all integers little-endian:
//   Audio:      "PAU1" | uint32 seq | PCM s16le, 48 kHz, stereo (normally 960 bytes = 5 ms)
//   Discovery:  phone -> broadcast "PAUD"; PC replies "PAUH" + UTF-8 computer name
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AudioBridge
{
    internal sealed class PcmBuffer
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;
        public const int BytesPerMs = SampleRate * Channels * 2 / 1000; // 192
        private const int DriftSlackMs = 40;

        private readonly byte[] ring = new byte[BytesPerMs * 1000];
        private readonly object gate = new object();
        private int readPos, count;
        private bool primed;
        private int targetBytes = BytesPerMs * 60;

        public volatile int VolumePercent = 100;
        public long Underruns, Skips;
        public volatile int Peak; // 0..32767, last pulled block

        public int TargetMs
        {
            set { lock (gate) targetBytes = Align(value * BytesPerMs); }
        }

        public int BufferedMs
        {
            get { lock (gate) return count / BytesPerMs; }
        }

        private static int Align(int bytes) { return bytes & ~3; }

        public void Reset()
        {
            lock (gate) { readPos = 0; count = 0; primed = false; }
        }

        public void Push(byte[] src, int offset, int len)
        {
            len = Align(len);
            if (len <= 0) return;
            lock (gate)
            {
                if (count + len > ring.Length) Discard(count + len - ring.Length);
                int writePos = (readPos + count) % ring.Length;
                int first = Math.Min(len, ring.Length - writePos);
                Buffer.BlockCopy(src, offset, ring, writePos, first);
                if (first < len) Buffer.BlockCopy(src, offset + first, ring, 0, len - first);
                count += len;

                // Keep latency bounded: after a Wi-Fi burst or slow clock drift, jump back to target.
                if (primed && count > targetBytes + DriftSlackMs * BytesPerMs)
                {
                    Discard(count - targetBytes);
                    Skips++;
                }
            }
        }

        private void Discard(int bytes)
        {
            bytes = Math.Min(Align(bytes), count);
            readPos = (readPos + bytes) % ring.Length;
            count -= bytes;
        }

        public void Pull(byte[] dst, int len)
        {
            bool haveAudio = false;
            lock (gate)
            {
                if (!primed && count >= targetBytes && count > 0) primed = true;
                if (primed && count < len)
                {
                    primed = false; // underrun: wait until the buffer refills to target
                    Underruns++;
                }
                if (primed)
                {
                    int first = Math.Min(len, ring.Length - readPos);
                    Buffer.BlockCopy(ring, readPos, dst, 0, first);
                    if (first < len) Buffer.BlockCopy(ring, 0, dst, first, len - first);
                    readPos = (readPos + len) % ring.Length;
                    count -= len;
                    haveAudio = true;
                }
            }
            if (!haveAudio) { Array.Clear(dst, 0, len); Peak = 0; return; }

            int vol = VolumePercent;
            int peak = 0;
            for (int i = 0; i + 1 < len; i += 2)
            {
                int s = (short)(dst[i] | (dst[i + 1] << 8));
                if (vol != 100)
                {
                    s = s * vol / 100;
                    if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
                    dst[i] = (byte)s;
                    dst[i + 1] = (byte)(s >> 8);
                }
                int a = s < 0 ? -s : s;
                if (a > peak) peak = a;
            }
            Peak = Math.Min(peak, 32767);
        }
    }

    internal sealed class UdpAudioReceiver : IDisposable
    {
        public const int Port = 45999;
        private const int SIO_UDP_CONNRESET = -1744830452;

        private readonly PcmBuffer buffer;
        private Socket socket;
        private Thread thread;
        private volatile bool running;

        private IPAddress sender;
        private int lastPacketTick;
        private uint lastSeq;
        private bool haveSeq;

        public long Packets, Lost;
        public volatile string Error;

        public UdpAudioReceiver(PcmBuffer buffer) { this.buffer = buffer; }

        public string CurrentSender
        {
            get
            {
                IPAddress s = sender;
                if (s == null || Environment.TickCount - lastPacketTick > 1500) return null;
                return s.ToString();
            }
        }

        public void Start()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ReceiveBufferSize = 1 << 20;
            try { socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            running = true;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "udp-rx";
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
        }

        private void Run()
        {
            var buf = new byte[4096];
            byte[] hello = Encoding.UTF8.GetBytes("PAUH" + Environment.MachineName);
            while (running)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = socket.ReceiveFrom(buf, ref from); }
                catch (SocketException ex)
                {
                    if (!running) break;
                    if (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize) continue;
                    Error = "Network: " + ex.Message;
                    break;
                }
                catch (ObjectDisposedException) { break; }

                if (n < 4 || buf[0] != 'P' || buf[1] != 'A' || buf[2] != 'U') continue;
                if (buf[3] == 'D')
                {
                    try { socket.SendTo(hello, from); } catch { }
                    continue;
                }
                if (buf[3] != '1' || n < 8) continue;

                IPAddress addr = ((IPEndPoint)from).Address;
                int now = Environment.TickCount;
                bool stale = now - lastPacketTick > 1000;
                if (sender != null && !sender.Equals(addr) && !stale) continue; // one phone at a time

                uint seq = BitConverter.ToUInt32(buf, 4);
                if (stale || sender == null || !sender.Equals(addr))
                {
                    buffer.Reset();
                    buffer.Underruns = 0;
                    buffer.Skips = 0;
                    Lost = 0;
                    haveSeq = false;
                }
                if (haveSeq)
                {
                    int delta = unchecked((int)(seq - lastSeq));
                    if (delta <= 0 && delta > -200) continue;        // duplicate / late packet
                    if (delta < -200) buffer.Reset();               // phone restarted the stream
                    else if (delta > 1 && delta < 400) Lost += delta - 1;
                }
                sender = addr;
                lastPacketTick = now;
                lastSeq = seq;
                haveSeq = true;
                Packets++;
                buffer.Push(buf, 8, n - 8);
            }
        }

        public void Dispose()
        {
            running = false;
            if (socket != null) { try { socket.Close(); } catch { } socket = null; }
            if (thread != null) { thread.Join(1000); thread = null; }
        }
    }
}
