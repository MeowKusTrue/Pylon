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
    const int W = 256, H = 144;
    readonly Dispatcher _ui;
    readonly IntPtr _buffer = Marshal.AllocHGlobal(W * H * 4);
    readonly MediaPlayer.LibVLCVideoLockCb _lock;
    readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    readonly DispatcherTimer _throttle;
    LibVLC? _vlc;
    MediaPlayer? _mp;
    float _pending = -1;
    volatile bool _awaitingFrame;
    volatile int _key = -1; // cache slot of the position being decoded
    long _targetMs = -1;     // its time
    long _nowMs = -1;
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _cache = new();

    public WriteableBitmap Bitmap { get; } = new(W, H, 96, 96, PixelFormats.Bgr32, null);
    public bool HasMedia => _mp != null;

    public SeekPreviewer(Dispatcher ui)
    {
        _ui = ui;
        _lock = (_, planes) => { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; };
        // libVLC 3 doesn't redraw after a seek while paused: run until the next frame lands, then pause again
        _display = (_, _) =>
        {
            // resuming first shows the old picture again: wait for one from near the requested time
            // (_nowMs comes from TimeChanged: calling libVLC from here deadlocks against Stop())
            if (_awaitingFrame && _targetMs >= 0 && Math.Abs(Interlocked.Read(ref _nowMs) - _targetMs) > 3000) return;
            _ui.BeginInvoke(() => Bitmap.WritePixels(new Int32Rect(0, 0, W, H), _buffer, W * H * 4, W * 4));
            if (_awaitingFrame && _key >= 0 && _cache.Count < 200) // ~30 MB at most
            {
                var copy = new byte[W * H * 4];
                Marshal.Copy(_buffer, copy, 0, copy.Length);
                _cache[_key] = copy;
            }
            if (_awaitingFrame && _mp is { } p)
            {
                _awaitingFrame = false;
                Task.Run(() => p.SetPause(true));
            }
        };
        // at most ~8 seeks a second while the mouse moves
        _throttle = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Flush(), ui);
    }

    /// <summary>Local video files only; null closes the preview player.</summary>
    public void Open(string? path)
    {
        Close();
        if (path == null) return;
        // the GPU decodes (heavy 1440p HEVC took the CPU ~0.3 s a frame); a late decoder otherwise shows half-decoded grey frames
        // and it jumps to the nearest keyframe: a preview needn't be frame-exact, and decoding up to it took a second
        _vlc ??= new LibVLC(["--no-audio", "--no-spu", "--no-osd", "--quiet", "--no-video-title-show", "--no-avcodec-hurry-up", "--no-avcodec-corrupted", "--input-fast-seek",
            "--no-drop-late-frames", "--no-skip-frames"]); // the first frame after a seek is always "late": show it anyway
        _cache.Clear();
        _key = -1;
        _targetMs = -1;
        _mp = new MediaPlayer(_vlc);
        _mp.SetVideoFormat("RV32", W, H, W * 4);
        _mp.SetVideoCallbacks(_lock, null, _display);
        _mp.TimeChanged += (_, e) => Interlocked.Exchange(ref _nowMs, e.Time);
        var player = _mp;
        _awaitingFrame = true; // pause on the first frame
        var media = new Media(_vlc, path, FromType.FromPath);
        player.Play(media);
        Library.DisposeLater(media);
    }

    public void Request(double position)
    {
        _pending = (float)Math.Clamp(position, 0, 1);
        if (_cache.TryGetValue(Key(_pending), out var seen)) Bitmap.WritePixels(new Int32Rect(0, 0, W, H), seen, W * 4, 0);
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    void Flush()
    {
        _throttle.Stop();
        if (_mp == null || _pending < 0) return;
        var p = _pending;
        _pending = -1;
        var player = _mp;
        if (_cache.ContainsKey(Key(p))) return; // already on screen
        _key = Key(p);
        Interlocked.Exchange(ref _targetMs, (long)(p * player.Length));
        _awaitingFrame = true;
        Task.Run(() => { player.Position = p; player.SetPause(false); });
    }

    static int Key(float position) => (int)Math.Round(position * 200);

    public string DebugState() => $"key={_key} target={_targetMs} awaiting={_awaitingFrame} time={_mp?.Time} state={_mp?.State} pending={_pending} cached={_cache.Count}";

    void Close()
    {
        if (_mp is not { } old) return;
        _mp = null;
        Task.Run(() => { old.Stop(); old.Dispose(); }); // Stop() blocks on the decoder threads
    }

    public void Dispose()
    {
        Close();
        _throttle.Stop();
        Task.Delay(3000).ContinueWith(_ => { _vlc?.Dispose(); Marshal.FreeHGlobal(_buffer); });
    }
}
