using System.IO;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>A track as the window's menus list it, from libVLC or the engine.</summary>
public sealed record PlayerTrack(int Id, string? Name);

/// <summary>What the settings ask of the tracks a file starts with.</summary>
public sealed record TrackChoice(int AudioNumber, string AudioLanguage, int SubNumber, string SubLanguage, bool SubsOn);

/// <summary>
/// The main window's player: libVLC, or for a plain local video Pylon's own engine, which opens several times faster.
/// The members below hide libVLC's of the same name, so the window's calls stay as they are and what's not here
/// (effects, tracks, recording) simply goes to libVLC: the window uses the engine only for files that need none of it.
/// </summary>
public sealed class Player(LibVLC vlc) : MediaPlayer(vlc)
{
    volatile VideoEngine? _engine;
    readonly object _engineGate = new();
    public long OwnGeneration { get; private set; }
    Task _opening = Task.CompletedTask, _closing = Task.CompletedTask;
    volatile bool _started, _ended;
    long _startMs;
    float _rate = 1;
    bool _mute;
    double _level = 1, _gain = 1;
    readonly List<(MediaSlaveType Type, string Uri, bool Select)> _slaves = [];
    readonly Dictionary<int, List<SubText.Cue>> _cues = [];
    readonly List<(string Name, List<SubText.Cue> Cues)> _subFiles = [];
    volatile int _spu = -1;
    bool _soundOff;

    /// <summary>The first picture of a file on the engine, on its thread.</summary>
    public event Action<long>? OwnPlaying;
    public event Action<long>? OwnEnded;
    /// <summary>The engine can't play this file (or stopped on an error): libVLC's turn, on the engine's thread.</summary>
    public event Action<long, string>? OwnRefused;

    public bool OnOwnEngine => _engine != null;
    public bool OwnHardwareDecoding => _engine?.HardwareDecoding == true;

    public bool OwnPaused => _engine?.Paused == true;

    /// <param name="device">Start of the output device's name; null for the Windows default.</param>
    public void PlayOwn(string path, long startMs, bool paused, string? device, TrackChoice choice, IReadOnlyList<string> subtitleFiles, int addedFiles)
    {
        lock (_engineGate)
        {
            StopOwn();
            base.Stop(); // it may have played the last file
            var engine = _engine = new VideoEngine(Hwnd, device);
            _started = _ended = _soundOff = false;
            _startMs = startMs;
            _spu = -1;
            lock (_cues) _cues.Clear();
            _subFiles.Clear();
            var generation = OwnGeneration;
            engine.SubtitleCue += (track, start, end, text) =>
            {
                lock (_engineGate) if (_engine == engine) AddCue(track, start, end, text);
            };
            engine.Volume = EngineVolume;
            if (_rate != 1) engine.Speed = _rate;
            engine.FirstFrame += () =>
            {
                lock (_engineGate)
                {
                    if (_engine != engine) return;
                    _started = true;
                    OwnPlaying?.Invoke(generation);
                }
            };
            engine.Ended += () =>
            {
                lock (_engineGate)
                {
                    if (_engine != engine) return;
                    _ended = true;
                    OwnEnded?.Invoke(generation);
                }
            };
            engine.Failed += e => Refuse(engine, e.Message);
            engine.SoundFailed += e => Refuse(engine, e.Message);
            var closing = _closing;
            _opening = Task.Run(async () =>
            {
                try
                {
                    await closing; // a window takes a new swap chain only when the old one is gone
                    if (_engine != engine) return;
                    var loaded = subtitleFiles.Select(file => (Path.GetFileName(file), SubText.Load(file))).ToList();
                    lock (_engineGate)
                    {
                        if (_engine != engine) return;
                        _subFiles.AddRange(loaded);
                    }
                    engine.Open(path, startMs, paused, strict: true, chooseAudio: (audio, subs) =>
                    {
                        var inside = VideoEngine.PickTrack(subs, choice.SubNumber, choice.SubLanguage, flagged: true);
                        // added by hand in Pylon: that's what the user wants to see
                        lock (_engineGate)
                        {
                            if (_engine != engine) throw new OperationCanceledException();
                            _spu = !choice.SubsOn ? -1 : addedFiles > 0 || (inside < 0 && subtitleFiles.Count > 0) ? SubFileIds : inside;
                        }
                        return VideoEngine.PickTrack(audio, choice.AudioNumber, choice.AudioLanguage, flagged: false);
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or SharpGen.Runtime.SharpGenException or DllNotFoundException or BadImageFormatException)
                {
                    Refuse(engine, e.Message);
                }
            });
        }
    }

    void Refuse(VideoEngine engine, string why)
    {
        lock (_engineGate) if (_engine == engine) OwnRefused?.Invoke(OwnGeneration, why);
    }

    void StopOwn()
    {
        VideoEngine engine;
        lock (_engineGate)
        {
            OwnGeneration++;
            if (_engine is not { } active) return;
            engine = active;
            _engine = null;
            engine.RequestStop();
            var opening = _opening;
            var closing = _closing;
            _closing = Task.Run(async () => // joining its threads takes a moment, which the window needn't wait for
            {
                try { await Task.WhenAll(opening, closing); }
                finally { engine.Dispose(); }
            });
        }
    }

    /// <summary>Volume as libVLC's output has it: the slider's level cubed, 1 = as in the file.</summary>
    float EngineVolume => _mute || _soundOff ? 0 : (float)(_level * _level * _level * _gain);

    /// <summary>Track ids from here up are subtitle files, not streams of the video.</summary>
    public const int SubFileIds = 100_000;

    void AddCue(int track, double start, double end, string text)
    {
        lock (_cues)
        {
            if (!_cues.TryGetValue(track, out var list)) _cues[track] = list = [];
            var at = list.FindLastIndex(c => c.Start <= start); // they come in order, and again after a seek
            for (var i = at; i >= 0 && list[i].Start == start; i--)
                if (list[i].Text == text && list[i].End == end) return;
            list.Insert(at + 1, new SubText.Cue(start, end, text));
        }
    }

    /// <summary>What the chosen subtitle shows at this moment (seconds); null for nothing. For the window, on the engine only.</summary>
    public string? SubtitleAt(double seconds)
    {
        if (_engine == null || _spu < 0) return null;
        lock (_cues)
        {
            var list = _spu >= SubFileIds ? _spu - SubFileIds < _subFiles.Count ? _subFiles[_spu - SubFileIds].Cues : null : _cues.GetValueOrDefault(_spu);
            if (list == null) return null;
            int low = 0, high = list.Count - 1, last = -1;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (list[mid].Start <= seconds) (last, low) = (mid, mid + 1);
                else high = mid - 1;
            }
            var shown = new List<string>();
            for (var i = last; i >= 0 && i > last - 8; i--) // overlapping cues: the few before it may still be on
                if (seconds < list[i].End) shown.Insert(0, list[i].Text);
            return shown.Count == 0 ? null : string.Join("\n", shown);
        }
    }

    public new PlayerTrack[] AudioTrackDescription => _engine is { } engine
        ? [new PlayerTrack(-1, L.T("Disable")), .. engine.AudioTracks.Select(t => new PlayerTrack(t.Id, t.Name))]
        : [.. base.AudioTrackDescription.Select(t => new PlayerTrack(t.Id, t.Name))];

    public new int AudioTrack => _engine is { } engine ? _soundOff ? -1 : engine.AudioTrack : base.AudioTrack;

    public new bool SetAudioTrack(int id)
    {
        if (_engine is not { } engine) return base.SetAudioTrack(id);
        _soundOff = id < 0;
        engine.Volume = EngineVolume;
        if (id >= 0) engine.SelectAudio(id);
        return true;
    }

    public new PlayerTrack[] SpuDescription => _engine is { } engine
        ? [new PlayerTrack(-1, L.T("Disable")), .. engine.SubtitleTracks.Select(t => new PlayerTrack(t.Id, t.Name)), .. _subFiles.Select((f, i) => new PlayerTrack(SubFileIds + i, f.Name))]
        : [.. base.SpuDescription.Select(t => new PlayerTrack(t.Id, t.Name))];

    public new int Spu => _engine != null ? _spu : base.Spu;

    public new bool SetSpu(int id)
    {
        if (_engine == null) return base.SetSpu(id);
        _spu = id;
        return true;
    }

    /// <summary>The slider's level (1 = 100 %) and the track's measured gain, for the engine; libVLC gets its own through AudioChain.</summary>
    public void SetOwnVolume(double level, double gain)
    {
        (_level, _gain) = (level, gain);
        if (_engine is { } engine)
        {
            engine.Volume = EngineVolume;
            if (!SupportsOwnVolume(level, gain)) Refuse(engine, "requested amplification requires libVLC");
        }
    }

    public static bool SupportsOwnVolume(double level, double gain) =>
        double.IsFinite(level) && double.IsFinite(gain) && level >= 0 && gain >= 0 && level * level * level * gain <= 2;

    public new bool Play(Media media)
    {
        StopOwn();
        // what changed while the engine played
        base.SetRate(_rate);
        base.Mute = _mute;
        var ok = base.Play(media);
        foreach (var (type, uri, select) in _slaves) base.AddSlave(type, uri, select);
        _slaves.Clear();
        return ok;
    }

    /// <summary>A text subtitle goes on with the engine; anything else added is libVLC's job, so it takes over and gets it.</summary>
    public new bool AddSlave(MediaSlaveType type, string uri, bool select)
    {
        if (_engine is not { } engine) return base.AddSlave(type, uri, select);
        var file = new Uri(uri).LocalPath;
        if (type == MediaSlaveType.Subtitle && Path.GetExtension(file).ToLowerInvariant() is ".srt" or ".vtt")
        {
            _subFiles.Add((Path.GetFileName(file), SubText.Load(file)));
            if (select) SetSpu(SubFileIds + _subFiles.Count - 1);
            return true;
        }
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
        get => _mute;
        set
        {
            _mute = value;
            base.Mute = value;
            if (_engine is { } engine) engine.Volume = EngineVolume;
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
