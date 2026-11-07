using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PhoneToPC.Receiver;

// Native waveOut (normal Windows shared audio): no NuGet/native third-party DLLs.
// All buffer lifetime and waveOut calls are serialized on one dedicated thread.
internal sealed class WavePlayer : IDisposable
{
    private readonly JitterBuffer _buffer;
    private readonly Func<int> _volume;
    private Thread? _thread;
    private volatile bool _running;
    public string? Error { get; private set; }
    private const int OutputBytes = JitterBuffer.FrameBytes * JitterBuffer.FramesPerOutput;
    private const uint HeaderDone = 0x00000001;

    public WavePlayer(JitterBuffer buffer, Func<int> volume)
    {
        _buffer = buffer;
        _volume = volume;
    }

    public void Start()
    {
        if (_running) return;
        Error = null;
        _running = true;
        _thread = new Thread(PlaybackLoop) { IsBackground = true, Name = "PhoneToPC audio output" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(3000);
        _thread = null;
    }

    public void Dispose() => Stop();

    private void PlaybackLoop()
    {
        IntPtr device = IntPtr.Zero;
        var slots = new List<Slot>();
        try
        {
            var format = new WaveFormat
            {
                FormatTag = 1, Channels = 2, SamplesPerSec = 48000,
                AvgBytesPerSec = 48000 * 4, BlockAlign = 4,
                BitsPerSample = 16, ExtraSize = 0
            };
            Check(WinMM.waveOutOpen(out device, uint.MaxValue, ref format,
                IntPtr.Zero, IntPtr.Zero, 0), "Open Windows audio output");

            int hdrSize = Marshal.SizeOf<WaveHeader>();
            // Six 20 ms buffers; prefill is an additional 80 ms of network jitter.
            for (int i = 0; i < 6; i++)
            {
                var slot = new Slot
                {
                    Data = new byte[OutputBytes],
                    DataPtr = Marshal.AllocHGlobal(OutputBytes),
                    HeaderPtr = Marshal.AllocHGlobal(hdrSize)
                };
                slots.Add(slot);
                Marshal.Copy(slot.Data, 0, slot.DataPtr, OutputBytes);
                Marshal.StructureToPtr(new WaveHeader
                {
                    Data = slot.DataPtr, BufferLength = OutputBytes
                }, slot.HeaderPtr, false);
                Check(WinMM.waveOutPrepareHeader(device, slot.HeaderPtr, (uint)hdrSize), "Prepare audio buffer");
                slot.Prepared = true;
                Check(WinMM.waveOutWrite(device, slot.HeaderPtr, (uint)hdrSize), "Start audio buffer");
            }

            while (_running)
            {
                bool wrote = false;
                foreach (var slot in slots)
                {
                    var header = Marshal.PtrToStructure<WaveHeader>(slot.HeaderPtr);
                    if ((header.Flags & HeaderDone) == 0) continue;
                    _buffer.Fill20Ms(slot.Data, _volume());
                    Marshal.Copy(slot.Data, 0, slot.DataPtr, OutputBytes);
                    Check(WinMM.waveOutWrite(device, slot.HeaderPtr, (uint)hdrSize), "Queue audio");
                    wrote = true;
                }
                if (!wrote) Thread.Sleep(3);
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _running = false;
            if (device != IntPtr.Zero) WinMM.waveOutReset(device);
            int hdrSize = Marshal.SizeOf<WaveHeader>();
            foreach (var slot in slots)
            {
                if (slot.Prepared && device != IntPtr.Zero)
                    WinMM.waveOutUnprepareHeader(device, slot.HeaderPtr, (uint)hdrSize);
                if (slot.HeaderPtr != IntPtr.Zero) Marshal.FreeHGlobal(slot.HeaderPtr);
                if (slot.DataPtr != IntPtr.Zero) Marshal.FreeHGlobal(slot.DataPtr);
            }
            if (device != IntPtr.Zero) WinMM.waveOutClose(device);
        }
    }

    private static void Check(int result, string action)
    {
        if (result != 0) throw new InvalidOperationException($"{action} failed: WinMM error {result}");
    }

    private sealed class Slot
    {
        public byte[] Data = Array.Empty<byte>();
        public IntPtr DataPtr;
        public IntPtr HeaderPtr;
        public bool Prepared;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    private static class WinMM
    {
        [DllImport("winmm.dll")] public static extern int waveOutOpen(
            out IntPtr output, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(
            IntPtr output, IntPtr header, uint headerSize);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(
            IntPtr output, IntPtr header, uint headerSize);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(
            IntPtr output, IntPtr header, uint headerSize);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr output);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr output);
    }
}
