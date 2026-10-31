using System.IO;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// The main window's player: libVLC, or for a plain local video Pylon's own engine, which opens several times faster.
/// The members below hide libVLC's of the same name, so the window's calls stay as they are and what's not here
/// (effects, tracks, recording) simply goes to libVLC: the window uses the engine only for files that need none of it.
/// </summary>
public sealed class Player(LibVLC vlc) : MediaPlayer(vlc)
{
    VideoEngine? _engine;
    Task _opening = Task.CompletedTask, _closing = Task.CompletedTask;
    volatile bool _started, _ended;
    long _startMs;
    float _rate = 1;
    bool _mute;
    double _level = 1, _gain = 1;
    readonly List<(MediaSlaveType Type, string Uri, bool Select)> _slaves = [];

    /// <summary>The first picture of a file on the engine, on its thread.</summary>
    public event Action? OwnPlaying;
    public event Action? OwnEnded;
    /// <summary>The engine can't play this file (or stopped on an error): libVLC's turn, on the engine's thread.</summary>
    public event Action<string>? OwnRefused;

    public bool OnOwnEngine => _engine != null;

    public bool OwnPaused => _engine?.Paused == true;

    /// <param name="device">Start of the output device's name; null for the Windows default.</param>
    public void PlayOwn(string path, long startMs, bool paused, string? device)
    {
        StopOwn();
        base.Stop(); // it may have played the last file
        var engine = _engine = new VideoEngine(Hwnd, device);
        _started = _ended = false;
        _startMs = startMs;
        engine.Volume = EngineVolume;
        engine.FirstFrame += () =>
        {
            _started = true;
            OwnPlaying?.Invoke();
        };
        engine.Ended += () =>
        {
            _ended = true;
            OwnEnded?.Invoke();
        };
        engine.Failed += e => Refuse(engine, e.Message);
        engine.SoundFailed += e => Refuse(engine, e.Message);
        var closing = _closing;
        _opening = Task.Run(async () =>
        {
            await closing; // a window takes a new swap chain only when the old one is gone
            try { engine.Open(path, startMs, paused, strict: true); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or SharpGen.Runtime.SharpGenException or DllNotFoundException)
            {
                Refuse(engine, e.Message);
            }
        });
    }

    void Refuse(VideoEngine engine, string why)
    {
        if (_engine == engine) OwnRefused?.Invoke(why);
    }

    void StopOwn()
    {
        if (_engine is not { } engine) return;
        _engine = null;
        var opening = _opening;
        var closing = _closing;
        _closing = Task.Run(async () => // joining its threads takes a moment, which the window needn't wait for
        {
            await opening;
            await closing;
            engine.Dispose();
        });
    }

    /// <summary>Volume as libVLC's output has it: the slider's level cubed, 1 = as in the file.</summary>
    float EngineVolume => _mute ? 0 : (float)(_level * _level * _level * _gain);

    /// <summary>The slider's level (1 = 100 %) and the track's measured gain, for the engine; libVLC gets its own through AudioChain.</summary>
    public void SetOwnVolume(double level, double gain)
    {
        (_level, _gain) = (level, gain);
        if (_engine is { } engine) engine.Volume = EngineVolume;
    }

    public new bool Play(Media media)
    {
        StopOwn();
        var ok = base.Play(media);
        foreach (var (type, uri, select) in _slaves) base.AddSlave(type, uri, select);
        _slaves.Clear();
        return ok;
    }

    /// <summary>A subtitle added while on the engine: libVLC's job, so it takes over and gets it.</summary>
    public new bool AddSlave(MediaSlaveType type, string uri, bool select)
    {
        if (_engine is not { } engine) return base.AddSlave(type, uri, select);
        _slaves.Add((type, uri, select));
        Refuse(engine, "a subtitle was added");
        return true;
    }

    public new void NextFrame()
    {
        if (_engine is { } engine) engine.Step();
        else base.NextFrame();
    }

    public new bool TakeSnapshot(uint number, string path, uint width, uint height)
    {
        if (_engine is not { } engine) return base.TakeSnapshot(number, path, width, height);
        if (!_started || engine.Snapshot((int)width, (int)height) is not { } shot) return false;
        var bitmap = BitmapSource.Create(shot.Width, shot.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, shot.Pixels, shot.Width * 4);
        BitmapEncoder encoder = Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        return true;
    }

    public new void Stop()
    {
        StopOwn();
        base.Stop();
    }

    public new long Time
    {
        get => _engine is { } engine ? _started ? engine.TimeMs : _startMs : base.Time;
        set
        {
            if (_engine is { } engine)
            {
                if (_started) engine.Seek(value);
            }
            else base.Time = value;
        }
    }

    public new long Length => _engine is { } engine ? _started ? engine.DurationMs : 0 : base.Length;

    public new float Position
    {
        get => _engine != null ? Length > 0 ? (float)Time / Length : 0 : base.Position;
        set
        {
            if (_engine != null) Time = (long)(value * Length);
            else base.Position = value;
        }
    }

    public new bool IsPlaying => _engine is { } engine ? _started && !_ended && !engine.Paused : base.IsPlaying;

    public new bool IsSeekable => _engine != null ? _started : base.IsSeekable;

    public new VLCState State => _engine is { } engine
        ? !_started ? VLCState.Opening : _ended ? VLCState.Ended : engine.Paused ? VLCState.Paused : VLCState.Playing
        : base.State;

    public new void SetPause(bool pause)
    {
        if (_engine is { } engine) engine.Paused = pause;
        else base.SetPause(pause);
    }

    public new void Pause()
    {
        if (_engine is { } engine) engine.Paused = !engine.Paused;
        else base.Pause();
    }

    public new float Rate => _engine != null ? _rate : base.Rate;

    public new int SetRate(float rate)
    {
        _rate = rate;
        if (_engine is not { } engine) return base.SetRate(rate);
        engine.Speed = rate;
        return 0;
    }

    public new bool Mute
    {
        get => _engine != null ? _mute : base.Mute;
        set
        {
            _mute = value;
            if (_engine is { } engine) engine.Volume = EngineVolume;
            else base.Mute = value;
        }
    }

    public new bool Size(uint number, ref uint width, ref uint height)
    {
        if (_engine is not { } engine) return base.Size(number, ref width, ref height);
        if (!_started) return false;
        (width, height) = ((uint)engine.VideoWidth, (uint)engine.VideoHeight);
        return true;
    }
}
