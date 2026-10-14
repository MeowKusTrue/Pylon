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
/// into a small buffer that is copied into a WriteableBitmap.
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

    public WriteableBitmap Bitmap { get; } = new(W, H, 96, 96, PixelFormats.Bgr32, null);
    public bool HasMedia => _mp != null;

    public SeekPreviewer(Dispatcher ui)
    {
        _ui = ui;
        _lock = (_, planes) => { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; };
        // libVLC 3 doesn't redraw after a seek while paused: run until the next frame lands, then pause again
        _display = (_, _) =>
        {
            _ui.BeginInvoke(() => Bitmap.WritePixels(new Int32Rect(0, 0, W, H), _buffer, W * H * 4, W * 4));
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
        _vlc ??= new LibVLC("--no-audio", "--no-spu", "--no-osd", "--quiet", "--no-video-title-show", "--avcodec-hw=none");
        _mp = new MediaPlayer(_vlc);
        _mp.SetVideoFormat("RV32", W, H, W * 4);
        _mp.SetVideoCallbacks(_lock, null, _display);
        var player = _mp;
        _awaitingFrame = true; // pause on the first frame
        var media = new Media(_vlc, path, FromType.FromPath);
        player.Play(media);
        Library.DisposeLater(media);
    }

    public void Request(double position)
    {
        _pending = (float)Math.Clamp(position, 0, 1);
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    void Flush()
    {
        _throttle.Stop();
        if (_mp == null || _pending < 0) return;
        var p = _pending;
        _pending = -1;
        var player = _mp;
        _awaitingFrame = true;
        Task.Run(() => { player.Position = p; player.SetPause(false); });
    }

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
