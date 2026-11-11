// WinMM waveOut player: 32-bit float output (16-bit fallback), deep buffer queue,
// "Pro Audio" MMCSS thread priority. Pulls audio from a JitterBuffer on its own thread.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioBridge
{
    internal static class WinMM
    {
        public const int WHDR_DONE = 0x1;
        public const int CALLBACK_EVENT = 0x00050000;
        public const int WAVE_MAPPER = -1;
        public const short WAVE_FORMAT_PCM = 1;
        public const short WAVE_FORMAT_IEEE_FLOAT = 3;

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct WAVEFORMATEX
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEOUTCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
        }

        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(out IntPtr hwo, IntPtr uDeviceID, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")]
        public static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")]
        public static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")]
        public static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")]
        public static extern int waveOutGetDevCaps(IntPtr uDeviceID, ref WAVEOUTCAPS caps, int size);
        [DllImport("winmm.dll")]
        public static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")]
        public static extern int timeEndPeriod(int ms);
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);
        [DllImport("avrt.dll")]
        public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

        public static List<string> OutputDevices()
        {
            var list = new List<string>();
            int n = waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                var caps = new WAVEOUTCAPS();
                if (waveOutGetDevCaps(new IntPtr(i), ref caps, Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0)
                    list.Add(caps.szPname);
                else
                    list.Add("Device " + i);
            }
            return list;
        }
    }

    internal sealed class WaveOutPlayer : IDisposable
    {
        private const int BufferCount = 6;
        private const int BufferFrames = 960; // 20 ms -> 120 ms queued at the device

        private readonly JitterBuffer source;
        private readonly int deviceId;
        private Thread thread;
        private volatile bool running;
        public volatile string Error;
        public long DeviceStarved;      // times Windows ran out of queued audio
        public volatile bool FloatOutput;

        public WaveOutPlayer(JitterBuffer source, int deviceId)
        {
            this.source = source;
            this.deviceId = deviceId;
        }

        public void Start()
        {
            running = true;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "waveOut";
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
        }

        private static WinMM.WAVEFORMATEX Format(bool useFloat)
        {
            var f = new WinMM.WAVEFORMATEX();
            f.wFormatTag = useFloat ? WinMM.WAVE_FORMAT_IEEE_FLOAT : WinMM.WAVE_FORMAT_PCM;
            f.nChannels = 2;
            f.nSamplesPerSec = JitterBuffer.SampleRate;
            f.wBitsPerSample = (short)(useFloat ? 32 : 16);
            f.nBlockAlign = (short)(2 * f.wBitsPerSample / 8);
            f.nAvgBytesPerSec = f.nSamplesPerSec * f.nBlockAlign;
            return f;
        }

        private void Run()
        {
            int mmIndex = 0;
            IntPtr mmcss = IntPtr.Zero;
            try { mmcss = WinMM.AvSetMmThreadCharacteristics("Pro Audio", ref mmIndex); } catch { }
            WinMM.timeBeginPeriod(1);

            int hdrSize = Marshal.SizeOf(typeof(WinMM.WAVEHDR));
            int flagsOffset = (int)Marshal.OffsetOf(typeof(WinMM.WAVEHDR), "dwFlags");
            var headers = new IntPtr[BufferCount];
            var datas = new IntPtr[BufferCount];
            var samples = new float[BufferFrames * 2];
            var pcm16 = new short[BufferFrames * 2];
            IntPtr hwo = IntPtr.Zero;
            int bufferBytes = 0;

            using (var evt = new AutoResetEvent(false))
            {
                try
                {
                    IntPtr cb = evt.SafeWaitHandle.DangerousGetHandle();
                    var fmt = Format(true);
                    int rc = WinMM.waveOutOpen(out hwo, new IntPtr(deviceId), ref fmt, cb, IntPtr.Zero, WinMM.CALLBACK_EVENT);
                    FloatOutput = rc == 0;
                    if (rc != 0)
                    {
                        fmt = Format(false);
                        rc = WinMM.waveOutOpen(out hwo, new IntPtr(deviceId), ref fmt, cb, IntPtr.Zero, WinMM.CALLBACK_EVENT);
                    }
                    if (rc != 0) { hwo = IntPtr.Zero; Error = "Cannot open audio device (WinMM error " + rc + ")"; return; }
                    bufferBytes = BufferFrames * fmt.nBlockAlign;

                    for (int i = 0; i < BufferCount; i++)
                    {
                        datas[i] = Marshal.AllocHGlobal(bufferBytes);
                        headers[i] = Marshal.AllocHGlobal(hdrSize);
                        var h = new WinMM.WAVEHDR();
                        h.lpData = datas[i];
                        h.dwBufferLength = bufferBytes;
                        Marshal.StructureToPtr(h, headers[i], false);
                        WinMM.waveOutPrepareHeader(hwo, headers[i], hdrSize);
                        Fill(samples, pcm16, datas[i]);
                        WinMM.waveOutWrite(hwo, headers[i], hdrSize);
                    }

                    while (running)
                    {
                        evt.WaitOne(20);
                        int done = 0;
                        for (int i = 0; i < BufferCount; i++)
                            if ((Marshal.ReadInt32(headers[i], flagsOffset) & WinMM.WHDR_DONE) != 0) done++;
                        if (done == BufferCount) DeviceStarved++;
                        for (int i = 0; i < BufferCount && running; i++)
                        {
                            if ((Marshal.ReadInt32(headers[i], flagsOffset) & WinMM.WHDR_DONE) == 0) continue;
                            Fill(samples, pcm16, datas[i]);
                            rc = WinMM.waveOutWrite(hwo, headers[i], hdrSize);
                            if (rc != 0) { Error = "Audio device error (WinMM " + rc + ")"; running = false; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                }
                finally
                {
                    if (hwo != IntPtr.Zero)
                    {
                        WinMM.waveOutReset(hwo);
                        for (int i = 0; i < BufferCount; i++)
                            if (headers[i] != IntPtr.Zero) WinMM.waveOutUnprepareHeader(hwo, headers[i], hdrSize);
                        WinMM.waveOutClose(hwo);
                    }
                    for (int i = 0; i < BufferCount; i++)
                    {
                        if (headers[i] != IntPtr.Zero) Marshal.FreeHGlobal(headers[i]);
                        if (datas[i] != IntPtr.Zero) Marshal.FreeHGlobal(datas[i]);
                    }
                    WinMM.timeEndPeriod(1);
                    if (mmcss != IntPtr.Zero) { try { WinMM.AvRevertMmThreadCharacteristics(mmcss); } catch { } }
                }
            }
        }

        private void Fill(float[] samples, short[] pcm16, IntPtr dest)
        {
            // A bug in the buffer must never kill playback: output one block of silence instead.
            try { source.Pull(samples, BufferFrames); }
            catch { Array.Clear(samples, 0, samples.Length); }

            if (FloatOutput)
            {
                Marshal.Copy(samples, 0, dest, samples.Length);
                return;
            }
            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i] * 32768f;
                pcm16[i] = (short)(v >= 32767f ? 32767 : (v <= -32768f ? -32768 : (int)Math.Round(v)));
            }
            Marshal.Copy(pcm16, 0, dest, pcm16.Length);
        }

        public void Dispose()
        {
            running = false;
            if (thread != null) { thread.Join(1000); thread = null; }
        }
    }
}
