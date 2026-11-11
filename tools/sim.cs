// Offline quality test for the receiver's JitterBuffer.
// Build:  csc /out:sim.exe tools\sim.cs windows\Receiver.cs
// Feeds a 997 Hz tone through simulated bad Wi-Fi (loss, jitter, power-save stalls, clock drift)
// and measures the noise/distortion of what would be played (SNR per 85 ms window).
using System;
using System.Collections.Generic;
using AudioBridge;

static class Sim
{
    struct Pkt { public double At; public long Seq; public bool Resend; }

    static void Main(string[] args)
    {
        double seconds = args.Length > 0 ? double.Parse(args[0]) : 90;
        double lossPct = args.Length > 1 ? double.Parse(args[1]) : 5;
        double driftPpm = args.Length > 2 ? double.Parse(args[2]) : 100;
        int targetMs = args.Length > 3 ? int.Parse(args[3]) : 300;
        var rnd = new Random(7);

        var jb = new JitterBuffer();
        jb.TargetMs = targetMs;
        double simNow = 0;
        jb.Clock = delegate { return (int)(simNow * 1000); };
        bool inOrder = args.Length > 4 && args[4] == "inorder";
        const int CF = JitterBuffer.ChunkFrames;
        double senderRate = 48000 * (1 + driftPpm * 1e-6);
        double fIn = 997.0 / 48000;      // cycles per (phone) sample

        Func<long, byte[]> chunk = delegate (long seq)
        {
            var b = new byte[CF * 4];
            for (int k = 0; k < CF; k++)
            {
                long i = seq * CF + k;
                short v = (short)Math.Round(Math.Sin(2 * Math.PI * fIn * i) * 16384);
                b[k * 4] = (byte)v; b[k * 4 + 1] = (byte)(v >> 8); b[k * 4 + 2] = (byte)v; b[k * 4 + 3] = (byte)(v >> 8);
            }
            return b;
        };
        Func<long, byte[]> packet = delegate (long seq)
        {
            var p = new byte[8 + 2 * CF * 4];
            Array.Copy(chunk(seq), 0, p, 8, CF * 4);
            Array.Copy(chunk(Math.Max(0, seq - 8)), 0, p, 8 + CF * 4, CF * 4);
            return p;
        };

        // Originals: send time + random delay; occasional 150 ms power-save stalls.
        var queue = new List<Pkt>();
        long total = (long)(seconds * senderRate / CF);
        double stallUntil = 0;
        int dropped = 0;
        for (long s = 0; s < total; s++)
        {
            double t = s * CF / senderRate;
            if (t > stallUntil && rnd.NextDouble() < 0.002) stallUntil = t + 0.15;
            double at = Math.Max(t, stallUntil) + 0.002 + -Math.Log(1 - rnd.NextDouble()) * 0.008;
            if (inOrder && queue.Count > 0) at = Math.Max(at, queue[queue.Count - 1].At); // real Wi-Fi keeps order
            if (rnd.NextDouble() * 100 < lossPct) { dropped++; continue; }
            queue.Add(new Pkt { At = at, Seq = s });
        }
        queue.Sort((a, b) => a.At.CompareTo(b.At));

        var resends = new List<Pkt>();
        var output = new List<float>();
        var ratios = new List<double>();
        var buf = new float[960 * 2];
        var nacks = new uint[100];
        int qi = 0;
        long nackCount = 0;
        for (int step = 0; step * 0.02 < seconds; step++)
        {
            double now = step * 0.02;
            for (double sub = now; sub < now + 0.02; sub += 0.005)
            {
                simNow = sub;
                while (qi < queue.Count && queue[qi].At <= sub) { jb.Push(queue[qi].Seq, packet(queue[qi].Seq), 8, 8 + CF * 4); qi++; }
                resends.Sort((a, b) => a.At.CompareTo(b.At));
                while (resends.Count > 0 && resends[0].At <= sub) { jb.Push(resends[0].Seq, packet(resends[0].Seq), 8, 8 + CF * 4); resends.RemoveAt(0); }
                int n = jb.CollectMissing(nacks);
                nackCount += n;
                for (int i = 0; i < n; i++)
                    if (rnd.NextDouble() * 100 >= lossPct)
                        resends.Add(new Pkt { At = sub + 0.006 + rnd.NextDouble() * 0.01, Seq = nacks[i], Resend = true });
            }
            jb.Pull(buf, 960);
            for (int i = 0; i < 960; i++) output.Add(buf[i * 2]);
            ratios.Add(1 + jb.DriftPpm * 1e-6);
            System.Threading.Thread.Sleep(0);
        }

        // Analyse after 15 s settle: least-squares fit of the tone per 4096-sample window.
        int W = 4096, bad = 0, windows = 0;
        double worst = 999, sum = 0;
        for (int start = 48000 * 15; start + W < output.Count; start += W)
        {
            double ratio = ratios[Math.Min(ratios.Count - 1, start / 960)];
            double f = fIn * ratio;
            double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
            for (int i = 0; i < W; i++)
            {
                double s = Math.Sin(2 * Math.PI * f * i), c = Math.Cos(2 * Math.PI * f * i), y = output[start + i];
                ss += s * s; cc += c * c; sc += s * c; ys += y * s; yc += y * c;
            }
            double det = ss * cc - sc * sc;
            double a = (ys * cc - yc * sc) / det, b = (yc * ss - ys * sc) / det;
            double sig = 0, res = 0;
            for (int i = 0; i < W; i++)
            {
                double fit = a * Math.Sin(2 * Math.PI * f * i) + b * Math.Cos(2 * Math.PI * f * i);
                double e = output[start + i] - fit;
                sig += fit * fit; res += e * e;
            }
            double snr = 10 * Math.Log10(sig / Math.Max(res, 1e-30));
            windows++; sum += snr; worst = Math.Min(worst, snr);
            if (snr < 60) bad++;
        }
        Console.WriteLine("sim {0}s, loss {1}%, drift {2} ppm, buffer {3} ms", seconds, lossPct, driftPpm, targetMs);
        Console.WriteLine("  packets dropped by network: {0}, resend requests: {1}", dropped, nackCount);
        Console.WriteLine("  repaired(redundant) {0}, resent {1}, still lost {2}, underruns {3}, jumps {4}",
            jb.Recovered, jb.Resent, jb.Concealed, jb.Underruns, jb.Jumps);
        double avgRatio = 0;
        for (int i = ratios.Count / 2; i < ratios.Count; i++) avgRatio += ratios[i];
        avgRatio /= ratios.Count - ratios.Count / 2;
        Console.WriteLine("  clock correction, 2nd-half average {0:F0} ppm (actual drift {1}), buffer now {2} ms",
            (avgRatio - 1) * 1e6, driftPpm, jb.BufferedMs);
        Console.WriteLine("  SNR avg {0:F1} dB, worst {1:F1} dB, glitchy windows (<60 dB): {2}/{3}", sum / windows, worst, bad, windows);
    }
}
