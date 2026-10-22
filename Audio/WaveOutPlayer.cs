using System.Runtime.InteropServices;

namespace Tubase.Audio;

public sealed class WaveOutPlayer : IDisposable
{
    private const int SampleRate = 44100;
    private const int BufferFrames = 2048;
    private const int BufferCount = 3;
    private const uint WhdrDone = 0x00000001;
    private const uint WhdrPrepared = 0x00000002;
    private IntPtr _handle;
    private readonly List<Buffer> _buffers = new();
    private CancellationTokenSource? _cancel;
    private Task? _worker;
    private ProjectDocument? _project;
    private IReadOnlyDictionary<string, AudioAsset>? _assets;
    private volatile int _cursor;

    private sealed class Buffer : IDisposable
    {
        public readonly byte[] Data = new byte[BufferFrames * 4];
        public readonly GCHandle Pin;
        public readonly IntPtr Header;
        public Buffer()
        {
            Pin = GCHandle.Alloc(Data, GCHandleType.Pinned);
            Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(new WaveHeader
            {
                Data = Pin.AddrOfPinnedObject(),
                BufferLength = (uint)Data.Length
            }, Header, false);
        }
        public void Dispose() { Marshal.FreeHGlobal(Header); Pin.Free(); }
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
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Mmtime
    {
        public uint Type;
        public uint Milliseconds;
        public uint Sample;
        public uint Bytes;
        public uint Ticks;
        public uint SongPointer;
        public uint Track;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr handle, ref Mmtime time, uint size);

    public event Action<double>? PositionChanged;
    public bool IsPlaying => _worker is { IsCompleted: false };
    public double PositionSeconds => (double)_cursor / SampleRate;

    public void Play(ProjectDocument project, IReadOnlyDictionary<string, AudioAsset> assets, double positionSeconds)
    {
        Stop();
        _project = project;
        _assets = assets;
        _cursor = Math.Max(0, (int)(positionSeconds * SampleRate));
        var fmt = new WaveFormat
        {
            FormatTag = 1, Channels = 2, SamplesPerSec = SampleRate,
            AvgBytesPerSec = SampleRate * 4, BlockAlign = 4, BitsPerSample = 16
        };
        Check(waveOutOpen(out _handle, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0), "open audio output");
        _buffers.Clear();
        for (var i = 0; i < BufferCount; i++)
        {
            var buffer = new Buffer();
            Check(waveOutPrepareHeader(_handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()), "prepare audio buffer");
            _buffers.Add(buffer);
        }
        _cancel = new CancellationTokenSource();
        _worker = Task.Run(() => Run(_cancel.Token));
    }

    private void Run(CancellationToken token)
    {
        var active = new bool[BufferCount];
        var next = 0;
        var left = new float[BufferFrames];
        var right = new float[BufferFrames];
        while (!token.IsCancellationRequested)
        {
            var submitted = false;
            for (var offset = 0; offset < BufferCount; offset++)
            {
                var i = (next + offset) % BufferCount;
                var h = Marshal.PtrToStructure<WaveHeader>(_buffers[i].Header);
                if (active[i] && (h.Flags & WhdrDone) == 0) continue;
                if (_project is null || _assets is null) break;
                Mixer.Render(_project, _assets, left, right, _cursor, SampleRate);
                var frames = BufferFrames;
                var endFrame = (int)(Mixer.ProjectDuration(_project) * SampleRate);
                if (_cursor >= endFrame) { token.ThrowIfCancellationRequested(); return; }
                if (_cursor + frames > endFrame) frames = endFrame - _cursor;
                for (var f = 0; f < frames; f++)
                {
                    var l = (short)Math.Clamp((int)(Math.Clamp(left[f], -1f, 1f) * 32767), short.MinValue, short.MaxValue);
                    var r = (short)Math.Clamp((int)(Math.Clamp(right[f], -1f, 1f) * 32767), short.MinValue, short.MaxValue);
                    _buffers[i].Data[f * 4] = (byte)l;
                    _buffers[i].Data[f * 4 + 1] = (byte)(l >> 8);
                    _buffers[i].Data[f * 4 + 2] = (byte)r;
                    _buffers[i].Data[f * 4 + 3] = (byte)(r >> 8);
                }
                h = Marshal.PtrToStructure<WaveHeader>(_buffers[i].Header);
                h.BufferLength = (uint)(frames * 4);
                Marshal.StructureToPtr(h, _buffers[i].Header, false);
                Check(waveOutWrite(_handle, _buffers[i].Header, (uint)Marshal.SizeOf<WaveHeader>()), "write audio buffer");
                active[i] = true;
                _cursor += frames;
                next = (i + 1) % BufferCount;
                submitted = true;
            }
            if (!submitted) Thread.Sleep(2);
            PositionChanged?.Invoke(PositionSeconds);
        }
    }

    public void Stop()
    {
        var cancel = _cancel;
        if (cancel is null) return;
        cancel.Cancel();
        if (_handle != IntPtr.Zero) waveOutReset(_handle);
        try { _worker?.Wait(1000); } catch (AggregateException) { }
        var size = (uint)Marshal.SizeOf<WaveHeader>();
        foreach (var b in _buffers)
        {
            var h = Marshal.PtrToStructure<WaveHeader>(b.Header);
            if ((h.Flags & WhdrPrepared) != 0 && _handle != IntPtr.Zero)
                waveOutUnprepareHeader(_handle, b.Header, size);
            b.Dispose();
        }
        _buffers.Clear();
        if (_handle != IntPtr.Zero) { waveOutClose(_handle); _handle = IntPtr.Zero; }
        cancel.Dispose();
        _cancel = null;
        _worker = null;
    }

    private static void Check(int result, string action)
    {
        if (result != 0) throw new InvalidOperationException("Could not " + action + " (waveOut error " + result + ").");
    }

    public void Dispose() => Stop();
}
