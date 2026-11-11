// UDP receiver + music-grade jitter buffer.
//
// Wire protocol, UDP port 45999, integers little-endian:
//   phone -> PC  "PAU2" | uint32 seq | chunk[seq] | chunk[seq - 8]
//                chunk = 480 bytes PCM s16le 48 kHz stereo (2.5 ms); the second chunk is a
//                redundant copy of audio sent 20 ms earlier (instant repair of single losses).
//   PC -> phone  "PAUN" | uint16 count | count x uint32 seq   resend request for missing packets
//   PC -> phone  "PAUK"                                        keep-alive ("PC is receiving")
//   discovery    phone sends "PAUD" (broadcast); PC replies "PAUH" + UTF-8 computer name
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
    /// Sequence-indexed jitter buffer for music.
    ///  * Missing packets are first repaired from the redundant copy, then re-requested from the
    ///    phone (several tries) while they are still in the future of the play position.
    ///  * Phone/PC clock drift is absorbed by a 64-tap Kaiser-windowed sinc resampler whose speed
    ///    moves by at most +/-0.05% (inaudible; no clicks, no sample drops).
    ///  * Underruns and catch-up jumps fade out/in instead of cutting.
    /// </summary>
    internal sealed class JitterBuffer
    {
        public const int SampleRate = 48000;
        public const int ChunkFrames = 120;                 // 2.5 ms
        public const int ChunkBytes = ChunkFrames * 4;      // 480
        public const int RedundancyDistance = 8;            // chunks (20 ms)
        private const int Slots = 2048;                     // 5.12 s of packets
        private const int Taps = 64, Half = 32, Phases = 512;
        private const int InSize = 8192, InMask = InSize - 1;
        private const int FadeFrames = 480;                 // 10 ms
        private const double MaxDeviation = 0.0005;         // +/-500 ppm
        private const int MaxNackTries = 8;
        private const int NackIntervalMs = 25;
        private const int FirstNackDelayMs = 15;

        /// <summary>Millisecond clock (replaceable for offline simulation).</summary>
        public Func<int> Clock = delegate { return Environment.TickCount; };

        private static readonly float[] Table = BuildTable();

        private readonly short[][] slot = new short[Slots][];
        private readonly long[] slotSeq = new long[Slots];
        private readonly long[] nackSeq = new long[Slots];
        private readonly int[] nackTick = new int[Slots];
        private readonly byte[] nackTries = new byte[Slots];
        private readonly object gate = new object();

        private bool started;
        private long sessionStart;
        private long highest = -1;
        private bool primed;
        private long playSeq;               // last chunk moved into the resampler input

        private readonly float[] inL = new float[InSize], inR = new float[InSize];
        private long inWrite;               // absolute frames written to the input ring
        private double pos;                 // absolute input position of the next output frame
        private readonly float[] lastChunk = new float[ChunkFrames * 2];
        private int concealRun;
        private bool rampNext;

        private double avgLag = -1;
        private int blocksSincePrime;
        private double ratio = 1.0;
        private int targetFrames = 300 * 48;
        private int fadeIn, fadeOut;
        private bool jumpAfterFade;
        private float gain = 1f;
        private float lastOutL, lastOutR;

        public volatile int VolumePercent = 100;
        public long Underruns, Recovered, Resent, Concealed, Jumps;
        public volatile int Peak;           // 0..32767
        public volatile float DriftPpm;

        public JitterBuffer()
        {
            for (int i = 0; i < Slots; i++) { slot[i] = new short[ChunkFrames * 2]; slotSeq[i] = -1; nackSeq[i] = -1; }
        }

        private static float[] BuildTable()
        {
            // Fractional-delay windowed sinc. Phase 0 is an exact unit impulse, so with no drift
            // correction the output equals the input sample-for-sample.
            const double beta = 8.6;
            var t = new float[(Phases + 1) * Taps];
            double i0b = BesselI0(beta);
            for (int p = 0; p <= Phases; p++)
            {
                double f = (double)p / Phases, sum = 0;
                var row = new double[Taps];
                for (int k = 0; k < Taps; k++)
                {
                    double x = (k - (Half - 1)) - f;            // distance from the output point
                    double sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                    double r = x / Half;
                    double w = Math.Abs(r) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - r * r)) / i0b;
                    row[k] = sinc * w;
                    sum += row[k];
                }
                for (int k = 0; k < Taps; k++) t[p * Taps + k] = (float)(row[k] / sum);
            }
            return t;
        }

        private static double BesselI0(double x)
        {
            double sum = 1, term = 1, q = x * x / 4;
            for (int k = 1; k < 50; k++) { term *= q / (k * k); sum += term; if (term < 1e-12 * sum) break; }
            return sum;
        }

        private static int Idx(long seq) { return (int)(((seq % Slots) + Slots) % Slots); }

        public int TargetMs
        {
            set { lock (gate) { targetFrames = value * 48; avgLag = -1; } }
        }

        public int BufferedMs
        {
            get { lock (gate) return primed ? (int)(LagFrames() / 48) : 0; }
        }

        private long LagFrames()
        {
            return (highest - playSeq) * ChunkFrames + (inWrite - (long)pos);
        }

        public void Reset()
        {
            lock (gate)
            {
                started = false; highest = -1; primed = false; avgLag = -1; ratio = 1.0;
                concealRun = 0; fadeOut = 0; jumpAfterFade = false;
                for (int i = 0; i < Slots; i++) { slotSeq[i] = -1; nackSeq[i] = -1; }
                Underruns = Recovered = Resent = Concealed = Jumps = 0;
            }
        }

        public long Unwrap(uint seq)
        {
            lock (gate) return highest < 0 ? seq : highest + unchecked((int)(seq - (uint)highest));
        }

        public bool IsFarBehind(long seq)
        {
            lock (gate) return highest >= 0 && seq < highest - 1000;
        }

        public void Push(long seq, byte[] buf, int mainOffset, int redundantOffset)
        {
            lock (gate)
            {
                if (!started) { started = true; highest = seq - 1; sessionStart = seq; }
                Store(seq, buf, mainOffset, false);
                if (redundantOffset >= 0 && seq - RedundancyDistance >= sessionStart)
                    Store(seq - RedundancyDistance, buf, redundantOffset, true);
                if (seq > highest) highest = seq;

                if (!primed)
                {
                    int targetChunks = targetFrames / ChunkFrames;
                    long first = Math.Max(highest - targetChunks + 1, sessionStart);
                    if (highest - first + 1 >= targetChunks * 9 / 10 && CountValid(first, highest) >= targetChunks * 9 / 10)
                        Prime(first - 1);
                }
            }
        }

        private void Prime(long beforeFirst)
        {
            primed = true;
            playSeq = beforeFirst;
            Array.Clear(inL, 0, InSize);
            Array.Clear(inR, 0, InSize);
            inWrite = Half;
            pos = Half;
            fadeIn = FadeFrames;
            fadeOut = 0;
            jumpAfterFade = false;
            concealRun = 0;
            avgLag = -1;
            blocksSincePrime = 0;
        }

        private int CountValid(long from, long to)
        {
            int n = 0;
            for (long s = from; s <= to; s++) if (slotSeq[Idx(s)] == s) n++;
            return n;
        }

        private void Store(long seq, byte[] buf, int offset, bool redundant)
        {
            if (seq < sessionStart || (primed && seq <= playSeq) || seq <= highest - Slots) return;
            int i = Idx(seq);
            if (slotSeq[i] == seq) return;
            short[] d = slot[i];
            for (int k = 0; k < ChunkFrames * 2; k++)
                d[k] = (short)(buf[offset + k * 2] | (buf[offset + k * 2 + 1] << 8));
            slotSeq[i] = seq;
            if (redundant) Recovered++;
            else if (nackSeq[i] == seq && nackTries[i] > 0) Resent++;
        }

        /// <summary>Missing packets that are still worth asking the phone for.</summary>
        public int CollectMissing(uint[] output)
        {
            lock (gate)
            {
                if (!started) return 0;
                long from = primed ? playSeq + 1 : Math.Max(sessionStart, highest - Slots / 2);
                long to = highest - 2; // tolerate a little reordering
                int now = Clock(), n = 0;
                for (long s = from; s <= to && n < output.Length; s++)
                {
                    int i = Idx(s);
                    if (slotSeq[i] == s) continue;
                    // First noticed: give it 15 ms to arrive late/out of order before asking.
                    if (nackSeq[i] != s) { nackSeq[i] = s; nackTries[i] = 0; nackTick[i] = now - NackIntervalMs + FirstNackDelayMs; }
                    if (nackTries[i] >= MaxNackTries || now - nackTick[i] < NackIntervalMs) continue;
                    nackTries[i]++;
                    nackTick[i] = now;
                    output[n++] = unchecked((uint)s);
                }
                return n;
            }
        }

        /// <summary>Fills dst (interleaved stereo float) with frames of output.</summary>
        public void Pull(float[] dst, int frames)
        {
            float peak = 0;
            float targetGain = VolumePercent / 100f;
            lock (gate)
            {
                if (!primed)
                {
                    FadeTail(dst, 0, frames);
                    Peak = 0;
                    return;
                }

                // --- drift control (once per block) ---
                long lag = LagFrames();
                avgLag = avgLag < 0 ? lag : avgLag + (lag - avgLag) * 0.003;      // ~6 s smoothing
                double errSec = (avgLag - targetFrames) / SampleRate;
                double want = 1.0 + Math.Max(-MaxDeviation, Math.Min(MaxDeviation, errSec * 0.004)); // gentle: smooth pitch matters more than exact delay
                ratio += (want - ratio) * 0.02;
                DriftPpm = (float)((ratio - 1.0) * 1e6);

                // Way too much audio queued (after a long Wi-Fi stall or a burst at start-up):
                // fade out and jump back to the target delay once, instead of creeping down for minutes.
                blocksSincePrime++;
                bool hugeNow = lag > targetFrames * 2 + SampleRate / 2;
                bool tooHighSettled = blocksSincePrime > 500 && avgLag > targetFrames + Math.Max(SampleRate / 10, targetFrames / 2);
                if (!jumpAfterFade && (hugeNow || tooHighSettled))
                {
                    jumpAfterFade = true;
                    fadeOut = FadeFrames;
                }

                for (int i = 0; i < frames; i++)
                {
                    if (jumpAfterFade && fadeOut == 0)
                    {
                        Jumps++;
                        Prime(highest - targetFrames / ChunkFrames);
                    }

                    long n = (long)pos;
                    while (inWrite - n < Half + 1)
                    {
                        if (!LoadNext())
                        {
                            primed = false;          // network underrun: fade out, re-buffer
                            Underruns++;
                            FadeTail(dst, i, frames);
                            Peak = (int)(Math.Min(1f, peak) * 32767);
                            return;
                        }
                    }

                    double frac = pos - n;
                    double ph = frac * Phases;
                    int p0 = (int)ph;
                    float pf = (float)(ph - p0);
                    int t0 = p0 * Taps, t1 = t0 + Taps;
                    long start = n - (Half - 1);
                    float l = 0, r = 0;
                    if (p0 == 0 && pf == 0f)
                    {
                        int c = (int)((start + Half - 1) & InMask);
                        l = inL[c]; r = inR[c];
                    }
                    else
                    {
                        for (int k = 0; k < Taps; k++)
                        {
                            float c = Table[t0 + k] + (Table[t1 + k] - Table[t0 + k]) * pf;
                            int x = (int)((start + k) & InMask);
                            l += inL[x] * c;
                            r += inR[x] * c;
                        }
                    }
                    pos += ratio;

                    float g = gain += (targetGain - gain) * 0.002f;
                    if (fadeIn > 0) { g *= 1f - (float)fadeIn / FadeFrames; fadeIn--; }
                    if (fadeOut > 0) { g *= (float)fadeOut / FadeFrames; fadeOut--; }
                    else if (jumpAfterFade) g = 0;
                    l *= g; r *= g;
                    dst[i * 2] = l; dst[i * 2 + 1] = r;
                    lastOutL = l; lastOutR = r;
                    float al = l < 0 ? -l : l, ar = r < 0 ? -r : r;
                    if (al > peak) peak = al;
                    if (ar > peak) peak = ar;
                }
            }
            Peak = (int)(Math.Min(1f, peak) * 32767);
        }

        /// <summary>Moves the next packet into the resampler input. False = nothing to play yet.</summary>
        private bool LoadNext()
        {
            long next = playSeq + 1;
            if (next > highest) return false;
            playSeq = next;
            int i = Idx(next);
            int w = (int)(inWrite & InMask);
            if (slotSeq[i] == next)
            {
                short[] d = slot[i];
                bool ramp = rampNext;
                for (int k = 0; k < ChunkFrames; k++)
                {
                    float gk = ramp ? (float)k / ChunkFrames : 1f;
                    float l = d[k * 2] / 32768f * gk, r = d[k * 2 + 1] / 32768f * gk;
                    lastChunk[k * 2] = l; lastChunk[k * 2 + 1] = r;
                    inL[(w + k) & InMask] = l; inR[(w + k) & InMask] = r;
                }
                rampNext = false;
                concealRun = 0;
            }
            else
            {
                // Lost even after resend requests: replay the previous 2.5 ms fading to silence.
                Concealed++;
                for (int k = 0; k < ChunkFrames; k++)
                {
                    float gk = concealRun == 0 ? 1f - (float)k / ChunkFrames : 0f;
                    inL[(w + k) & InMask] = lastChunk[k * 2] * gk;
                    inR[(w + k) & InMask] = lastChunk[k * 2 + 1] * gk;
                }
                concealRun++;
                rampNext = true;
            }
            inWrite += ChunkFrames;
            return true;
        }

        private void FadeTail(float[] dst, int from, int frames)
        {
            float l0 = lastOutL, r0 = lastOutR;
            for (int i = from, k = 0; i < frames; i++, k++)
            {
                float g = k < FadeFrames ? 1f - (float)k / FadeFrames : 0f;
                dst[i * 2] = l0 * g; dst[i * 2 + 1] = r0 * g;
            }
            lastOutL = 0; lastOutR = 0;
        }
    }

    internal sealed class AudioReceiver : IDisposable
    {
        public const int Port = 45999;
        private const int SIO_UDP_CONNRESET = -1744830452;
        private const int PacketBytes = 8 + 2 * JitterBuffer.ChunkBytes;

        private readonly JitterBuffer buffer;
        private Socket udp;
        private Thread thread, nackThread;
        private volatile bool running;
        private volatile IPEndPoint sender;
        private int lastPacketTick;

        public long Packets;
        public volatile string Error;

        public AudioReceiver(JitterBuffer buffer) { this.buffer = buffer; }

        public IPAddress CurrentSender
        {
            get
            {
                IPEndPoint s = sender;
                if (s == null || Environment.TickCount - lastPacketTick > 1500) return null;
                return s.Address;
            }
        }

        public void Start()
        {
            udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.ReceiveBufferSize = 4 << 20;
            try { udp.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }
            udp.Bind(new IPEndPoint(IPAddress.Any, Port));
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "udp-rx";
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
            nackThread = new Thread(NackLoop);
            nackThread.IsBackground = true;
            nackThread.Name = "udp-nack";
            nackThread.Priority = ThreadPriority.AboveNormal;
            nackThread.Start();
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

                var ep = (IPEndPoint)from;
                IPEndPoint cur = sender;
                int now = Environment.TickCount;
                bool stale = now - lastPacketTick > 1000;
                if (cur != null && !cur.Address.Equals(ep.Address) && !stale) continue; // one phone at a time

                uint raw = BitConverter.ToUInt32(buf, 4);
                long seq = buffer.Unwrap(raw);
                // New phone, long pause, or the phone restarted its stream (sequence jumped far back).
                if (stale || cur == null || !cur.Equals(ep) || buffer.IsFarBehind(seq))
                {
                    buffer.Reset();
                    seq = raw;
                }
                sender = ep;
                lastPacketTick = now;
                Packets++;
                Error = null;
                buffer.Push(seq, buf, 8, 8 + JitterBuffer.ChunkBytes);
            }
        }

        // Every 10 ms: ask the phone to resend missing packets; every second: tell it we're alive.
        private void NackLoop()
        {
            var seqs = new uint[100];
            var pkt = new byte[6 + 4 * seqs.Length];
            pkt[0] = (byte)'P'; pkt[1] = (byte)'A'; pkt[2] = (byte)'U'; pkt[3] = (byte)'N';
            byte[] keepAlive = Encoding.ASCII.GetBytes("PAUK");
            int lastKeepAlive = 0;
            while (running)
            {
                Thread.Sleep(10);
                IPEndPoint ep = sender;
                if (ep == null || Environment.TickCount - lastPacketTick > 1500) continue;
                try
                {
                    int n = buffer.CollectMissing(seqs);
                    if (n > 0)
                    {
                        pkt[4] = (byte)n; pkt[5] = (byte)(n >> 8);
                        for (int i = 0; i < n; i++) Array.Copy(BitConverter.GetBytes(seqs[i]), 0, pkt, 6 + i * 4, 4);
                        udp.SendTo(pkt, 6 + n * 4, SocketFlags.None, ep);
                    }
                    if (Environment.TickCount - lastKeepAlive > 1000)
                    {
                        lastKeepAlive = Environment.TickCount;
                        udp.SendTo(keepAlive, ep);
                    }
                }
                catch { }
            }
        }

        public void Dispose()
        {
            running = false;
            if (udp != null) { try { udp.Close(); } catch { } udp = null; }
            if (thread != null) { thread.Join(1000); thread = null; }
            if (nackThread != null) { nackThread.Join(1000); nackThread = null; }
        }
    }
}
