// Minimal WinMM waveOut player. Pulls PCM from a PcmBuffer on its own thread.
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
        private const int BufferCount = 4;
        private readonly PcmBuffer source;
        private readonly int deviceId;
        private readonly int bufferBytes;
        private Thread thread;
        private volatile bool running;
        public volatile string Error;

        public WaveOutPlayer(PcmBuffer source, int deviceId, int bufferMs)
        {
            this.source = source;
            this.deviceId = deviceId;
            bufferBytes = PcmBuffer.BytesPerMs * bufferMs;
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

        private void Run()
        {
            var fmt = new WinMM.WAVEFORMATEX();
            fmt.wFormatTag = 1; // PCM
            fmt.nChannels = PcmBuffer.Channels;
            fmt.nSamplesPerSec = PcmBuffer.SampleRate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = (short)(PcmBuffer.Channels * 2);
            fmt.nAvgBytesPerSec = PcmBuffer.SampleRate * fmt.nBlockAlign;

            int hdrSize = Marshal.SizeOf(typeof(WinMM.WAVEHDR));
            int flagsOffset = (int)Marshal.OffsetOf(typeof(WinMM.WAVEHDR), "dwFlags");
            var headers = new IntPtr[BufferCount];
            var datas = new IntPtr[BufferCount];
            var managed = new byte[bufferBytes];
            IntPtr hwo = IntPtr.Zero;

            using (var evt = new AutoResetEvent(false))
            {
                try
                {
                    int rc = WinMM.waveOutOpen(out hwo, new IntPtr(deviceId), ref fmt,
                        evt.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WinMM.CALLBACK_EVENT);
                    if (rc != 0) { Error = "Cannot open audio device (WinMM error " + rc + ")"; return; }

                    for (int i = 0; i < BufferCount; i++)
                    {
                        datas[i] = Marshal.AllocHGlobal(bufferBytes);
                        headers[i] = Marshal.AllocHGlobal(hdrSize);
                        var h = new WinMM.WAVEHDR();
                        h.lpData = datas[i];
                        h.dwBufferLength = bufferBytes;
                        Marshal.StructureToPtr(h, headers[i], false);
                        WinMM.waveOutPrepareHeader(hwo, headers[i], hdrSize);
                        source.Pull(managed, bufferBytes);
                        Marshal.Copy(managed, 0, datas[i], bufferBytes);
                        WinMM.waveOutWrite(hwo, headers[i], hdrSize);
                    }

                    while (running)
                    {
                        evt.WaitOne(50);
                        for (int i = 0; i < BufferCount && running; i++)
                        {
                            int flags = Marshal.ReadInt32(headers[i], flagsOffset);
                            if ((flags & WinMM.WHDR_DONE) == 0) continue;
                            source.Pull(managed, bufferBytes);
                            Marshal.Copy(managed, 0, datas[i], bufferBytes);
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
                }
            }
        }

        public void Dispose()
        {
            running = false;
            if (thread != null) { thread.Join(1000); thread = null; }
        }
    }
}
