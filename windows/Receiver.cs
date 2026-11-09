// UDP receiver + bit-exact jitter buffer with packet-loss recovery.
//
// Wire protocol, UDP port 45999, integers little-endian:
//   Audio:     "PAU2" | uint32 seq | chunk[seq] | chunk[seq - 8]
//              chunk = 480 bytes PCM s16le 48 kHz stereo (2.5 ms). The second chunk is a
//              redundant copy of the audio sent 20 ms earlier, used to repair lost packets.
//   Discovery: phone sends "PAUD" (broadcast); PC replies "PAUH" + UTF-8 computer name.
//
// Works the same over Wi-Fi, phone hotspot, or USB tethering (a network over the cable).
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AudioBridge
{
    /// <summary>
    /// Sequence-indexed jitter buffer. Audio is played back sample-for-sample as captured on
    /// the phone (no resampling, no filtering). Clock drift is corrected by dropping or repeating
    /// a single sample frame at most once per 10 ms, which is inaudible.
    /// </summary>
    internal sealed class JitterBuffer
    {
        public const int SampleRate = 48000;
        public const int ChunkFrames = 120;                 // 2.5 ms
        public const int ChunkBytes = ChunkFrames * 4;      // 480
        public const int RedundancyDistance = 8;            // chunks (20 ms)
        public const double ChunkMs = 2.5;
        private const int Slots = 1024;                     // 2.56 s
        private const int FadeFrames = 120;

        private readonly short[][] slot = new short[Slots][];
        private readonly long[] slotSeq = new long[Slots];
        private readonly object gate = new object();

        private bool started;           // received anything in this session
        private long sessionStart;      // first seq of this session
        private long highest = -1;      // highest seq received
        private long playSeq;           // seq of the chunk currently being played
        private bool primed;
        private readonly short[] cur = new short[ChunkFrames * 2];
        private int curPos = ChunkFrames; // frame index inside cur; == ChunkFrames means "load next"
        private int fadeIn;
        private int concealed;          // consecutive concealed chunks
        private double avgLag = -1;
        private int targetChunks = 32;
        private short lastL, lastR;

        public volatile int VolumePercent = 100;
        public long Underruns, Recovered, Concealed, Slips;
        public volatile int Peak;

        public JitterBuffer()
        {
            for (int i = 0; i < Slots; i++) { slot[i] = new short[ChunkFrames * 2]; slotSeq[i] = -1; }
        }

        public int TargetMs
        {
            set { lock (gate) { targetChunks = Math.Max(4, (int)Math.Round(value / ChunkMs)); avgLag = -1; } }
        }

        public int BufferedMs
        {
            get { lock (gate) return primed ? (int)((highest - playSeq) * ChunkMs) : 0; }
        }

        public void Reset()
        {
            lock (gate)
            {
                started = false; highest = -1; primed = false; curPos = ChunkFrames; avgLag = -1; concealed = 0;
                for (int i = 0; i < Slots; i++) slotSeq[i] = -1;
                Underruns = Recovered = Concealed = Slips = 0;
            }
        }

        /// <summary>Unwraps a 32-bit sequence number relative to the newest one seen.</summary>
        public long Unwrap(uint seq)
        {
            lock (gate)
            {
                if (highest < 0) return seq;
                return highest + unchecked((int)(seq - (uint)highest));
            }
        }

        public bool IsFarBehind(long seq)
        {
            lock (gate) return highest >= 0 && seq < highest - 400;
        }

        public void Push(long seq, byte[] buf, int mainOffset, int redundantOffset)
        {
            lock (gate)
            {
                if (!started) { started = true; highest = seq - 1; sessionStart = seq; }
                Store(seq, buf, mainOffset, false);
                if (redundantOffset >= 0 && seq >= RedundancyDistance)
                    Store(seq - RedundancyDistance, buf, redundantOffset, true);
                if (seq > highest) highest = seq;

                if (!primed)
                {
                    // Start (or restart after underrun) once a target's worth of audio is queued.
                    long first = Math.Max(highest - targetChunks + 1, sessionStart);
                    if (CountValid(first, highest) >= targetChunks * 3 / 4)
                    {
                        primed = true;
                        playSeq = first - 1;
                        curPos = ChunkFrames;
                        fadeIn = FadeFrames;
                        concealed = 0;
                        avgLag = -1;
                    }
                }
                else if (highest - playSeq > targetChunks + 80)
                {
                    // Large burst after a Wi-Fi stall: jump forward instead of keeping the extra delay.
                    playSeq = highest - targetChunks;
                    curPos = ChunkFrames;
                    fadeIn = FadeFrames;
                    Slips++;
                }
            }
        }

        private int CountValid(long from, long to)
        {
            int n = 0;
            for (long s = Math.Max(0, from); s <= to; s++) if (slotSeq[(int)(s % Slots)] == s) n++;
            return n;
        }

        private void Store(long seq, byte[] buf, int offset, bool redundant)
        {
            if (seq < 0 || (primed && seq <= playSeq) || seq < highest - Slots + 1) return;
            int i = (int)(seq % Slots);
            if (slotSeq[i] == seq) return;
            short[] d = slot[i];
            for (int k = 0; k < ChunkFrames * 2; k++)
                d[k] = (short)(buf[offset + k * 2] | (buf[offset + k * 2 + 1] << 8));
            slotSeq[i] = seq;
            if (redundant) Recovered++;
        }

        /// <summary>Fills dst with len bytes of output.</summary>
        public void Pull(byte[] dst, int len)
        {
            int frames = len / 4;
            int vol = VolumePercent;
            int peak = 0;
            lock (gate)
            {
                // Drift control: decide whether this block drops or repeats one frame.
                int slip = 0;
                if (primed)
                {
                    double lag = highest - playSeq;
                    avgLag = avgLag < 0 ? lag : avgLag + (lag - avgLag) * 0.005; // ~2 s smoothing
                    if (avgLag > targetChunks + 2) slip = 1;        // too much delay: drop a frame
                    else if (avgLag < targetChunks - 2) slip = -1;  // running dry: repeat a frame
                }

                for (int i = 0; i < frames; i++)
                {
                    if (curPos >= ChunkFrames && !LoadNext())
                    {
                        // Underrun: fade the tail to silence.
                        FadeOut(dst, i, frames);
                        Peak = Math.Min(peak, 32767);
                        return;
                    }
                    int l = cur[curPos * 2], r = cur[curPos * 2 + 1];
                    if (slip == 1 && curPos > 0 && curPos < ChunkFrames - 1) { curPos++; slip = 0; Slips++; l = cur[curPos * 2]; r = cur[curPos * 2 + 1]; }
                    if (slip == -1 && curPos > 0) { curPos--; slip = 0; Slips++; }
                    curPos++;

                    if (fadeIn > 0)
                    {
                        int g = FadeFrames - fadeIn;
                        l = l * g / FadeFrames; r = r * g / FadeFrames;
                        fadeIn--;
                    }
                    if (vol != 100) { l = Clip(l * vol / 100); r = Clip(r * vol / 100); }
                    int o = i * 4;
                    dst[o] = (byte)l; dst[o + 1] = (byte)(l >> 8);
                    dst[o + 2] = (byte)r; dst[o + 3] = (byte)(r >> 8);
                    lastL = (short)l; lastR = (short)r;
                    int al = l < 0 ? -l : l, ar = r < 0 ? -r : r;
                    if (al > peak) peak = al;
                    if (ar > peak) peak = ar;
                }
            }
            Peak = Math.Min(peak, 32767);
        }

        private bool LoadNext()
        {
            if (!primed) return false;
            long next = playSeq + 1;
            if (next > highest)
            {
                primed = false;     // nothing newer has arrived: real underrun, re-buffer
                Underruns++;
                return false;
            }
            playSeq = next;
            curPos = 0;
            int i = (int)(((next % Slots) + Slots) % Slots);
            if (next >= 0 && slotSeq[i] == next)
            {
                Array.Copy(slot[i], cur, cur.Length);
                if (concealed > 0) { fadeIn = FadeFrames; concealed = 0; }
            }
            else
            {
                // Lost and not recoverable: repeat the previous 2.5 ms, fading out if losses continue.
                Concealed++;
                concealed++;
                if (concealed > 1) Array.Clear(cur, 0, cur.Length);
                else
                    for (int k = 0; k < ChunkFrames; k++)
                    {
                        int g = ChunkFrames - k;
                        cur[k * 2] = (short)(cur[k * 2] * g / ChunkFrames);
                        cur[k * 2 + 1] = (short)(cur[k * 2 + 1] * g / ChunkFrames);
                    }
            }
            return true;
        }

        private void FadeOut(byte[] dst, int from, int frames)
        {
            short sl0 = lastL, sr0 = lastR;
            for (int i = from, k = 0; i < frames; i++, k++)
            {
                int g = k < FadeFrames ? FadeFrames - k : 0;
                short l = (short)(sl0 * g / FadeFrames), r = (short)(sr0 * g / FadeFrames);
                int o = i * 4;
                dst[o] = (byte)l; dst[o + 1] = (byte)(l >> 8);
                dst[o + 2] = (byte)r; dst[o + 3] = (byte)(r >> 8);
                lastL = l; lastR = r;
            }
        }

        private static int Clip(int v) { return v > 32767 ? 32767 : (v < -32768 ? -32768 : v); }
    }

    internal sealed class AudioReceiver : IDisposable
    {
        public const int Port = 45999;
        private const int SIO_UDP_CONNRESET = -1744830452;
        private const int PacketBytes = 8 + 2 * JitterBuffer.ChunkBytes;

        private readonly JitterBuffer buffer;
        private Socket udp;
        private Thread thread;
        private volatile bool running;
        private volatile IPAddress sender;
        private int lastPacketTick;

        public long Packets;
        public volatile string Error;

        public AudioReceiver(JitterBuffer buffer) { this.buffer = buffer; }

        public IPAddress CurrentSender
        {
            get
            {
                IPAddress s = sender;
                if (s == null || Environment.TickCount - lastPacketTick > 1500) return null;
                return s;
            }
        }

        public void Start()
        {
            udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.ReceiveBufferSize = 1 << 20;
            try { udp.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
            udp.Bind(new IPEndPoint(IPAddress.Any, Port));
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "udp-rx";
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
        }

        private void Loop()
        {
            var buf = new byte[4096];
            byte[] hello = Encoding.UTF8.GetBytes("PAUH" + Environment.MachineName);
            while (running)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = udp.ReceiveFrom(buf, ref from); }
                catch (SocketException ex)
                {
                    if (!running) break;
                    if (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize) continue;
                    Error = "Network: " + ex.Message;
                    break;
                }
                catch (ObjectDisposedException) { break; }

                if (n < 4 || buf[0] != 'P' || buf[1] != 'A' || buf[2] != 'U') continue;
                if (buf[3] == 'D' && n == 4)
                {
                    try { udp.SendTo(hello, from); } catch { }
                    continue;
                }
                if (buf[3] == '1')
                {
                    Error = "The phone app is outdated. Install the new AudioBridge.apk.";
                    continue;
                }
                if (buf[3] != '2' || n != PacketBytes) continue;

                IPAddress addr = ((IPEndPoint)from).Address;
                int now = Environment.TickCount;
                bool stale = now - lastPacketTick > 1000;
                if (sender != null && !sender.Equals(addr) && !stale) continue; // one phone at a time

                uint raw = BitConverter.ToUInt32(buf, 4);
                long seq = buffer.Unwrap(raw);
                // New phone, long pause, or the phone restarted its stream (sequence jumped far back).
                if (stale || sender == null || !sender.Equals(addr) || buffer.IsFarBehind(seq))
                {
                    buffer.Reset();
                    seq = raw;
                }
                sender = addr;
                lastPacketTick = now;
                Packets++;
                Error = null;
                buffer.Push(seq, buf, 8, 8 + JitterBuffer.ChunkBytes);
            }
        }

        public void Dispose()
        {
            running = false;
            if (udp != null) { try { udp.Close(); } catch { } udp = null; }
            if (thread != null) { thread.Join(1000); thread = null; }
        }
    }
}
