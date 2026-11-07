using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PhoneToPC.Receiver;

// Each UDP packet contains exactly 5 ms of stereo PCM audio (960 bytes).
// Maintain 80 ms of jitter protection while keeping the queue bounded.
internal sealed class JitterBuffer
{
    public const int FrameBytes = 960;
    public const int FramesPerOutput = 4; // 20 ms / output buffer
    private const int Prefill = 16;        // 80 ms
    private const int MaxQueued = 60;      // 300 ms, bound total latency

    private readonly object _gate = new();
    private readonly SortedDictionary<int, byte[]> _frames = new();
    private int? _nextSequence;
    private int? _sessionId;
    private long _lastPacketTimestamp;

    public void Reset()
    {
        lock (_gate)
        {
            _frames.Clear();
            _nextSequence = null;
            _sessionId = null;
            _lastPacketTimestamp = 0;
        }
    }

    public void Add(int sessionId, int sequence, byte[] pcm)
    {
        if (pcm.Length != FrameBytes || sequence < 0) return;
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            bool timedOut = _lastPacketTimestamp != 0 &&
                            Stopwatch.GetElapsedTime(_lastPacketTimestamp, now) > TimeSpan.FromSeconds(1);
            if (_sessionId != sessionId || timedOut)
            {
                _sessionId = sessionId;
                _frames.Clear();
                _nextSequence = null;
            }
            _lastPacketTimestamp = now;
            if (_nextSequence.HasValue && sequence < _nextSequence.Value) return;
            _frames[sequence] = pcm;
            if (_frames.Count > MaxQueued)
            {
                // A slow receiver must drop stale sound rather than grow latency forever.
                int latest = MaxKey();
                int skipTo = Math.Max(0, latest - Prefill + 1);
                RemoveOlderThan(skipTo);
                _nextSequence = skipTo;
            }
        }
    }

    public void Fill20Ms(byte[] destination, int volumePercent)
    {
        if (destination.Length != FrameBytes * FramesPerOutput)
            throw new ArgumentException("Expected 20 ms PCM block", nameof(destination));
        Array.Clear(destination);
        lock (_gate)
        {
            if (!_nextSequence.HasValue)
            {
                if (_frames.Count < Prefill) return;
                _nextSequence = MinKey();
            }
            for (int i = 0; i < FramesPerOutput; i++)
            {
                int seq = _nextSequence.Value;
                if (_frames.TryGetValue(seq, out byte[]? payload))
                {
                    Buffer.BlockCopy(payload, 0, destination, i * FrameBytes, FrameBytes);
                    _frames.Remove(seq);
                }
                _nextSequence = seq == int.MaxValue ? 0 : seq + 1;
            }
            RemoveOlderThan(_nextSequence.Value);
        }

        if (volumePercent == 100) return;
        // Volume applies only to *this* stream, not to other Windows applications.
        for (int i = 0; i < destination.Length; i += 2)
        {
            short sample = (short)(destination[i] | (destination[i + 1] << 8));
            short adjusted = (short)(sample * Math.Clamp(volumePercent, 0, 100) / 100);
            destination[i] = (byte)adjusted;
            destination[i + 1] = (byte)(adjusted >> 8);
        }
    }

    private int MinKey()
    {
        using var it = _frames.Keys.GetEnumerator();
        it.MoveNext();
        return it.Current;
    }

    private int MaxKey()
    {
        int last = 0;
        foreach (int key in _frames.Keys) last = key;
        return last;
    }

    private void RemoveOlderThan(int sequence)
    {
        while (_frames.Count != 0)
        {
            int first = MinKey();
            if (first >= sequence) break;
            _frames.Remove(first);
        }
    }
}
