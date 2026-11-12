using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Pylon;

/// <summary>
/// Frame previews for the seek bar (mockup "thumbnail"): a silent second player decodes the hovered position
/// into a small buffer that is copied into a WriteableBitmap. Frames already seen are kept (every 0.5 % of the file),
/// so moving back over the bar shows them at once.
/// </summary>
public sealed class SeekPreviewer : IDisposable
{
    const int LongSide = 256;
    int W = LongSide, H = 144;
    string? _path;
    readonly Dispatcher _ui;
    readonly IntPtr _buffer = Marshal.AllocHGlobal(LongSide * LongSide * 4);
    readonly MediaPlayer.LibVLCVideoLockCb _lock;
    MediaPlayer.LibVLCVideoDisplayCb? _display;
    readonly object _operationsGate = new();
    Task _operations = Task.CompletedTask;
    int _generation;
    volatile bool _disposed;
    public Task Completion { get { lock (_operationsGate) return _operations; } }
    readonly DispatcherTimer _throttle;
    LibVLC? _vlc;
    MediaPlayer? _mp;
    float _pending = -1;
    volatile bool _awaitingFrame;
    volatile int _key = -1; // cache slot of the position being decoded
    long _targetMs = -1;     // its time
    long _nowMs = -1;
    long _seekFromMs = -1;   // where the seek started from
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _cache = new();

    public WriteableBitmap Bitmap { get; private set; } = new(LongSide, 144, 96, 96, PixelFormats.Bgr32, null);
    public bool HasMedia => !_disposed && _path != null;

    public SeekPreviewer(Dispatcher ui)
    {
        _ui = ui;
        _lock = (_, planes) => { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; };
        // at most ~8 seeks a second while the mouse moves
        _throttle = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Flush(), ui);
    }

    /// <summary>Local video files only; null closes the preview player. The size is the picture's as shown: the preview keeps its shape.</summary>
    public void Open(string? path, double width = 16, double height = 9)
    {
        if (_disposed) return;
        var (w, h) = Fit(width, height);
        if (path == _path && w == W && h == H) return;
        var generation = Interlocked.Increment(ref _generation);
        _path = path;
        _pending = -1;
        _throttle.Stop();
        _cache.Clear();
        if (w != W || h != H)
        {
            (W, H) = (w, h);
            Bitmap = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgr32, null);
        }
        Queue(() =>
        {
            Close();
            if (generation != Volatile.Read(ref _generation) || path == null || _disposed) return;
            // the GPU decodes (heavy 1440p HEVC took the CPU ~0.3 s a frame); a late decoder otherwise shows half-decoded grey frames
            // and it jumps to the nearest keyframe: a preview needn't be frame-exact, and decoding up to it took a second
            var args = new List<string> { "--no-audio", "--no-spu", "--no-osd", "--quiet", "--no-video-title-show", "--no-avcodec-hurry-up", "--no-avcodec-corrupted", "--input-fast-seek",
                "--no-drop-late-frames", "--no-skip-frames" };
#if DEBUG
            if (Environment.GetEnvironmentVariable("PYLON_SNAP") != null) args.Add("--avcodec-hw=none");
#endif
            _vlc ??= new LibVLC(args.ToArray());
            _key = -1;
            _targetMs = -1;
            _mp = new MediaPlayer(_vlc);
            var player = _mp;
            var bitmap = Bitmap;
            _display = (_, _) =>
            {
                if (generation != Volatile.Read(ref _generation) || !_awaitingFrame) return;
                var now = Interlocked.Read(ref _nowMs);
                if (_targetMs >= 0 && Math.Abs(now - _targetMs) > 3000 && Math.Abs(now - Interlocked.Read(ref _seekFromMs)) < 1000) return;
                var copy = new byte[w * h * 4];
                Marshal.Copy(_buffer, copy, 0, copy.Length);
                var key = _key;
                _awaitingFrame = false;
                _ui.BeginInvoke(() =>
                {
                    if (generation != Volatile.Read(ref _generation) || _disposed) return;
                    bitmap.WritePixels(new Int32Rect(0, 0, w, h), copy, w * 4, 0);
                    if (key >= 0 && _cache.Count < 200) _cache[key] = copy;
                });
                Queue(() => { if (generation == Volatile.Read(ref _generation)) player.SetPause(true); });
            };
            _mp.SetVideoFormat("RV32", (uint)w, (uint)h, (uint)w * 4);
            _mp.SetVideoCallbacks(_lock, null, _display);
            _mp.TimeChanged += (_, e) => { if (generation == Volatile.Read(ref _generation)) Interlocked.Exchange(ref _nowMs, e.Time); };
            _awaitingFrame = true; // pause on the first frame
            var media = new Media(_vlc, path, FromType.FromPath);
            player.Play(media);
            Library.DisposeLater(media);
        });
    }

    public void Request(double position)
    {
        if (_disposed || _path == null) return;
        _pending = (float)Math.Clamp(position, 0, 1);
        if (_cache.TryGetValue(Key(_pending), out var seen)) Bitmap.WritePixels(new Int32Rect(0, 0, W, H), seen, W * 4, 0);
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    void Flush()
    {
        _throttle.Stop();
        if (_disposed || _pending < 0) return;
        var p = _pending;
        _pending = -1;
        if (_cache.ContainsKey(Key(p))) return; // already on screen
        var generation = _generation;
        Queue(() =>
        {
            if (generation != Volatile.Read(ref _generation) || _mp is not { } player) return;
            _key = Key(p);
            Interlocked.Exchange(ref _seekFromMs, Interlocked.Read(ref _nowMs));
            Interlocked.Exchange(ref _targetMs, (long)(p * player.Length));
            _awaitingFrame = true;
            player.Position = p;
            player.SetPause(false);
        });
    }

    static int Key(float position) => (int)Math.Round(position * 200);

    /// <summary>The longer side 256, the shorter one by the picture's shape; even, as video formats like it.</summary>
    public static (int W, int H) Fit(double width, double height)
    {
        if (width <= 0 || height <= 0) return (LongSide, 144);
        var scale = LongSide / Math.Max(width, height);
        return (Math.Max(2, (int)Math.Round(width * scale / 2) * 2), Math.Max(2, (int)Math.Round(height * scale / 2) * 2));
    }

    public string DebugState() => $"key={_key} target={_targetMs} awaiting={_awaitingFrame} time={_mp?.Time} state={_mp?.State} pending={_pending} cached={_cache.Count}";

    void Close()
    {
        if (_mp is not { } old) return;
        _mp = null;
        old.Stop();
        old.Dispose();
        _display = null;
    }

    void Queue(Action action)
    {
        lock (_operationsGate)
            _operations = _operations.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception e) { ErrorLog.Note("seek preview", e.GetType().Name); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _generation);
        _path = null;
        _throttle.Stop();
        Queue(() => { Close(); _vlc?.Dispose(); Marshal.FreeHGlobal(_buffer); });
    }
}
