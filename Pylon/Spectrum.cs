using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Pylon;

/// <summary>Bar spectrum of what Pylon itself is playing. Draws nothing while inactive or when capture isn't available.</summary>
public class Spectrum : FrameworkElement
{
    const int Bars = 56;
    readonly double[] _levels = new double[Bars];
    bool _active, _hooked;

    public Spectrum()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Sync();
    }

    /// <summary>Set while media is playing; the capture only runs when an active spectrum is on screen.</summary>
    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Sync(); } }
    }

    void Sync()
    {
        var on = _active && IsVisible;
        if (on == _hooked) return;
        _hooked = on;
        if (on)
        {
            AudioTap.Subscribe();
            CompositionTarget.Rendering += Tick;
        }
        else
        {
            AudioTap.Unsubscribe();
            CompositionTarget.Rendering -= Tick;
            Array.Clear(_levels);
            InvalidateVisual();
        }
    }

    void Tick(object? sender, EventArgs e)
    {
        var bands = AudioTap.Bands(Bars);
        for (var i = 0; i < Bars; i++)
            _levels[i] = bands[i] > _levels[i] ? _levels[i] + (bands[i] - _levels[i]) * 0.6 : Math.Max(bands[i], _levels[i] - 0.025); // quick rise, slow fall
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_hooked || ActualWidth <= 0) return;
        var brush = TryFindResource("Accent") as Brush ?? Brushes.Orange;
        var slot = ActualWidth / Bars;
        var w = Math.Max(1, slot * 0.62);
        var r = Math.Min(w / 2, 3);
        for (var i = 0; i < Bars; i++)
        {
            var h = _levels[i] * ActualHeight;
            if (h < 1) continue; // silence draws nothing, not a dotted baseline
            dc.DrawRoundedRectangle(brush, null, new Rect(i * slot + (slot - w) / 2, ActualHeight - h, w, h), r, r);
        }
    }
}

/// <summary>
/// Captures Pylon's own audio output with WASAPI process loopback (Windows 10 2004+ / 11), so other apps' sound
/// never shows up. libVLC keeps playing through its normal output; this only listens.
/// </summary>
public static class AudioTap
{
    const int Rate = 48000, N = 2048;
    static readonly float[] Ring = new float[N];
    static readonly object Gate = new();
    static int _pos, _users, _gen;

    /// <summary>Player volume (1 = 100%). Loopback hears the stream after libVLC's cubic volume curve, so the bars undo it.</summary>
    public static double Volume { get; set; } = 1;

    public static void Subscribe()
    {
        if (_users++ > 0) return;
        var gen = ++_gen;
        new Thread(() => Loop(gen)) { IsBackground = true, Name = "Spectrum capture" }.Start();
    }

    public static void Unsubscribe()
    {
        if (--_users > 0) return;
        _gen++; // the capture thread sees the change and exits
        lock (Gate) Array.Clear(Ring);
    }

    /// <summary>Log-spaced band levels 40 Hz … 16 kHz, 0..1.</summary>
    public static double[] Bands(int count)
    {
        var buf = new Complex[N];
        lock (Gate)
            for (var i = 0; i < N; i++)
                buf[i] = Ring[(_pos + i) % N] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1))); // Hann window
        Fft(buf);

        var result = new double[count];
        const double lo = 40, hi = 16000;
        for (var b = 0; b < count; b++)
        {
            var k0 = (int)(lo * Math.Pow(hi / lo, (double)b / count) * N / Rate);
            var k1 = Math.Max(k0 + 1, (int)(lo * Math.Pow(hi / lo, (double)(b + 1) / count) * N / Rate));
            double peak = 0;
            for (var k = k0; k < k1 && k < N / 2; k++) peak = Math.Max(peak, buf[k].Magnitude);
            var db = 20 * Math.Log10(peak / (N / 4.0) / Math.Max(Math.Pow(Volume, 3), 1e-6) + 1e-9); // 0 dB = full-scale sine at 100%
            // ponytail: fixed -60..-6 dB window, add auto-gain if quiet masters look flat
            result[b] = Math.Clamp((db + 60) / 54, 0, 1);
        }
        return result;
    }

    static void Fft(Complex[] a)
    {
        for (int i = 1, j = 0; i < a.Length; i++)
        {
            var bit = a.Length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (var len = 2; len <= a.Length; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (var i = 0; i < a.Length; i += len)
            {
                Complex wk = 1;
                for (var k = 0; k < len / 2; k++, wk *= w)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * wk;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                }
            }
        }
    }

    static void Loop(int gen)
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        try
        {
            client = Activate();
            var format = new WaveFormat { Tag = 3, Channels = 2, Rate = Rate, Bits = 32, BlockAlign = 8, BytesPerSec = Rate * 8 }; // float: low volumes keep their detail
            client.Initialize(0, 0x00020000 | 0x80000000, 2_000_000, 0, ref format, IntPtr.Zero); // shared, LOOPBACK | AUTOCONVERTPCM, 200 ms
            var iid = typeof(IAudioCaptureClient).GUID;
            client.GetService(ref iid, out var service);
            capture = (IAudioCaptureClient)service;
            client.Start();
            while (Volatile.Read(ref _gen) == gen)
            {
                Thread.Sleep(15);
                while (capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
                {
                    capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                    lock (Gate)
                        for (var f = 0; f < frames; f++)
                        {
                            Ring[_pos] = (flags & 2) != 0 ? 0 : (BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, f * 8)) + BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, f * 8 + 4))) / 2; // 2 = silent
                            _pos = (_pos + 1) % N;
                        }
                    capture.ReleaseBuffer(frames);
                }
            }
            client.Stop();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or EntryPointNotFoundException or TimeoutException)
        {
            // older Windows without process loopback: the spectrum just stays empty
        }
        finally
        {
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    static IAudioClient Activate()
    {
        var p = new ActivationParams { Type = 1, ProcessId = (uint)Environment.ProcessId, Mode = 0 }; // PROCESS_LOOPBACK, include child processes
        var pParams = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        var pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            Marshal.StructureToPtr(new BlobVariant { Vt = 65, Size = (uint)Marshal.SizeOf<ActivationParams>(), Data = pParams }, pVariant, false); // VT_BLOB
            var done = new Completion();
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, pVariant, done, out var op);
            if (!done.Event.Wait(3000)) throw new TimeoutException();
            op.GetActivateResult(out var hr, out var client);
            Marshal.ThrowExceptionForHR(hr);
            return (IAudioClient)client;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    // ───────── interop ─────────

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid iid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams { public int Type; public uint ProcessId; public int Mode; }

    [StructLayout(LayoutKind.Sequential)]
    struct BlobVariant { public ushort Vt, R1, R2, R3; public uint Size; public IntPtr Data; } // PROPVARIANT holding a BLOB

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat { public ushort Tag, Channels; public int Rate, BytesPerSec; public ushort BlockAlign, Bits, Extra; }

    sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Event = new();
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Event.Set();
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormat format, IntPtr session);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long period, out long minimum);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
