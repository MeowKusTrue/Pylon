using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Pylon;

public partial class MainWindow : Window
{
    public static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma", ".alac", ".ape", ".aiff", ".aif", ".mka", ".mpc", ".wv" };
    public static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".vob", ".ogv", ".divx", ".rmvb" };
    public static readonly string MediaFilter =
        $"Media files|{string.Join(";", AudioExt.Concat(VideoExt).Select(x => "*" + x))}|All files|*.*";

    static readonly bool SnapMode = Environment.GetEnvironmentVariable("PYLON_SNAP") != null;

    MediaPlayer _mp = null!;
    readonly List<string> _queue = [];
    List<int> _shuffle = [];
    int _index = -1;
    bool _showRemaining, _fullscreen, _minimal, _mini, _isAudio, _rateApplied, _rescan, _recording, _netPresetApplying;
    long _resumeAt;
    string? _recordFile;
    string _title = "";
    string? _artist; // media title for history, independent of what the title bar shows
    WindowState _restoreState;
    Rect _restoreBounds;
    readonly DispatcherTimer _tick, _hideTimer, _osdTimer, _clickTimer, _resumeTimer, _engineTimer;
    int _ticks;
    SettingsWindow? _settings;
    readonly SeekPreviewer _preview;
    RendererDiscoverer? _renderers;
    readonly List<RendererItem> _castDevices = [];
    RendererItem? _castingTo;
    StreamWriter? _log;

    public LibVLC Vlc { get; private set; } = null!;
    public IReadOnlyList<string> Queue => _queue;
    public int QueueIndex => _index;
    public string? CurrentPath => _index >= 0 && _index < _queue.Count ? _queue[_index] : null;

    public MainWindow()
    {
        Perf.Mark("ctor");
        InitializeComponent();
#if DEBUG
        if (SnapMode)
        {
            // UI test run: stay off-screen and never take focus from whoever is using the PC
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            Top = 0;
        }
#endif
        Perf.Mark("window created");
        if (Perf.Enabled)
        {
            // profiling run: off-screen, no focus, closes itself after the first frame
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            ContentRendered += async (_, _) => { await Task.Delay(1500); Perf.Mark("idle +1.5 s"); Close(); };
        }
        Settings.Load();
        Library.Load();
        Perf.Mark("settings + library loaded");
        ThemeManager.Apply(Settings.Str("general.theme"), Settings.Str("general.accent"));
        Core.Initialize();
        Perf.Mark("theme applied");
        SourceInitialized += (_, _) => ApplyCorners();
        CreateEngine(resume: false);
        Perf.Mark("libVLC created");
        _preview = new SeekPreviewer(Dispatcher);
        PreviewImage.Source = _preview.Bitmap;

        Settings.AudioDevices = () => ["System default", .. _mp.AudioOutputDeviceEnum.Select(d => d.Description)];
        Settings.OpenHotkeys = w => DialogWindow.EditHotkeys(w);
        Settings.ClearHistory = w =>
        {
            if (MessageBox.Show(w, L.T("Clear the list of recently played items?"), "Pylon", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            Library.Data.History.Clear();
            Library.Touch();
        };
        Settings.OpenEqualizer = _ => { ShowEffects(video: false); Activate(); };
        Settings.RescanPlugins = _ => { _rescan = true; ReloadEngine(); Osd(L.T("Plugins rescanned")); };
        Settings.CheckUpdates = w => CheckForUpdates(manual: true, w);
        Settings.Changed += OnSettingChanged;

        var startVol = Settings.Bool("audio.remember") && Settings.Str("app.volume") != "" ? Settings.Num("app.volume") : Settings.Num("audio.startvol");
        Volume.Value = Math.Min(1, startVol / MaxVolume);
        ApplyVolume();
        Volume.Changed += _ =>
        {
            ApplyVolume();
            if (_mp.Mute) SetMute(false);
            Osd(L.F("Volume {0}%", _mp.Volume));
        };
        Seek.Seek += p => { if (_mp.IsSeekable) _mp.Position = (float)p; };
        MiniSeek.Seek += p => { if (_mp.IsSeekable) _mp.Position = (float)p; };
        FxPanel.CloseRequested += HideEffects;

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _hideTimer = new DispatcherTimer(TimeSpan.FromSeconds(Math.Max(0.5, Settings.Num("ui.hideafter"))), DispatcherPriority.Normal, (_, _) => HideControls(), Dispatcher) { IsEnabled = false };
        _osdTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.2), DispatcherPriority.Normal, (_, _) => { Motion.FadeOut(OsdBorder); Motion.FadeOut(StageOsd); _osdTimer!.Stop(); }, Dispatcher) { IsEnabled = false };
        _clickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(GetDoubleClickTime()), DispatcherPriority.Normal, (_, _) => { _clickTimer!.Stop(); var wasPlaying = _mp.IsPlaying; TogglePlay(); FlashPlayState(!wasPlaying); }, Dispatcher) { IsEnabled = false };
        _resumeTimer = new DispatcherTimer(TimeSpan.FromSeconds(10), DispatcherPriority.Normal, (_, _) => { HideResume(); _resumeTimer!.Stop(); }, Dispatcher) { IsEnabled = false };
        _engineTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => { _engineTimer!.Stop(); ReloadEngine(); }, Dispatcher) { IsEnabled = false };

        SourceInitialized += (_, _) =>
        {
            StartMediaSession();
            RestoreSavedBounds();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WndProc);
        };
        ContentRendered += (_, _) => Perf.Mark("first frame");
        SetupThumbButtons();
        CompositionTarget.Rendering += SmoothSeek;
        // the preview card's shadow reaches down over the bar; with a hit-testable popup window the pointer "left" the bar,
        // the popup closed, the pointer was back on the bar, it reopened... (flickering cursor and card). Make it click-through.
        SeekPreview.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(SeekPreview.Child) is HwndSource src)
                SetWindowLong(src.Handle, -20 /*GWL_EXSTYLE*/, GetWindowLong(src.Handle, -20) | 0x20 /*WS_EX_TRANSPARENT*/ | 0x08000000 /*WS_EX_NOACTIVATE*/);
        };
        Loaded += (_, _) =>
        {
            Perf.Mark("loaded");
            // VideoView creates its video HWND in OnApplyTemplate, which a Collapsed element never gets until first shown.
            // Without it the first video plays with Hwnd = 0 and libVLC opens its own "Direct3D11 output" window.
            Video.ApplyTemplate();
            Video.MediaPlayer = _mp;
            HookOverlayWindow();
            Topmost = Settings.Bool("ui.ontop");
            Library.IndexMusic(Dispatcher);
            Hotkeys.RegisterGlobal(this);
            StartCastDiscovery();
            ApplyPriority();
            UpdateControlButtons();
            SingleInstance.Listen(this);
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Length > 0) Open(args);
            if (Settings.Bool("ui.compact")) ToggleMiniplayer();
#if DEBUG
            DebugSnapshot();
            if (SnapMode) return;
#endif
            if (!Settings.Bool("app.onboarded")) Dispatcher.BeginInvoke(() => new OnboardingWindow(this).ShowDialog(), DispatcherPriority.ApplicationIdle);
            else
            {
                ShowWhatsNewIfUpdated();
                if (Settings.Bool("general.updates")) Dispatcher.BeginInvoke(() => CheckForUpdates(manual: false), DispatcherPriority.ApplicationIdle);
            }
        };
        Overlay.SizeChanged += (_, _) => UpdateCornerMask();
        StateChanged += (_, _) =>
        {
            FitMaximized();
            MaxBtn.Tag = WindowState == WindowState.Maximized ? "copy" : "square";
            if (_mini && WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            if (WindowState == WindowState.Minimized && Settings.Bool("ui.pausemin") && _mp.IsPlaying && !_isAudio) _mp.SetPause(true);
        };
        PreviewKeyDown += OnKey;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Open(files, append: Settings.Bool("library.enqueue"));
        };
        Closing += (_, _) =>
        {
            if (!_mini && !_fullscreen) SaveBounds();
            Sidebar.Shutdown();
            SaveProgress();
            Library.Save();
            Settings.Set("app.volume", Math.Round(Volume.Value * MaxVolume));
            Settings.Save();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            _preview.Dispose();
            _cast?.Dispose();
            _renderers?.Stop();
            Video.MediaPlayer = null;
            _mp.Stop();
            _mp.Dispose();
            Vlc.Dispose();
            _log?.Dispose();
            KeepAwake(false);
        };
    }

    // ───────────────────────── engine ─────────────────────────

    /// <summary>(Re)creates libVLC with the arguments the settings ask for. With resume, the current item continues where it was.</summary>
    void CreateEngine(bool resume)
    {
        var args = Settings.EngineArgs();
        if (_rescan) { args.Add("--reset-plugins-cache"); _rescan = false; }
        if (SnapMode && Environment.GetEnvironmentVariable("PYLON_SNAP_AUDIO") == null) args.Add("--aout=dummy"); // silent UI test runs
        var oldMp = _mp;
        var oldVlc = Vlc;
        var time = oldMp?.Time ?? 0;
        var playing = oldMp?.IsPlaying ?? false;

        try { Vlc = new LibVLC(args.ToArray()); }
        catch (VLCException)
        {
            // a bad "additional command line option" must not leave the player without an engine
            Vlc = new LibVLC("--no-video-title-show", "--no-osd");
            Osd(L.T("Some settings were rejected by libVLC"));
        }
        HookLog();
        _mp = new MediaPlayer(Vlc);
        AudioChain.Apply(_mp, SnapMode && Environment.GetEnvironmentVariable("PYLON_SNAP_AUDIO") == null ? "adummy" : null);
        // libVLC raises events on its own thread; never call back into libVLC from there.
        _mp.EndReached += (_, _) => Dispatcher.BeginInvoke(OnEnded);
        _mp.EncounteredError += (_, _) => Dispatcher.BeginInvoke(OnError);
        _mp.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
        _mp.Vout += (_, e) => { if (e.Count > 0) Dispatcher.BeginInvoke(async () => { if (_mini) SizeMini(); else FitToVideo(); FitVideo(); await Task.Delay(400); if (AmbientOn) GrabAmbient(); }); };

        if (oldMp != null)
        {
            Video.MediaPlayer = null;
            oldMp.Stop();
            oldMp.Dispose();
            Task.Delay(4000).ContinueWith(_ => oldVlc.Dispose()); // after deferred Media disposals
            Video.MediaPlayer = _mp;
            Sidebar.Shutdown();
            StartCastDiscovery();
        }
        ApplyVolume();
        if (resume && _index >= 0) PlayAt(_index, startAt: time, autoplay: playing);
    }

    void ReloadEngine() => CreateEngine(resume: true);

    void HookLog()
    {
        _log?.Dispose();
        _log = null;
        if (!Settings.Bool("general.log")) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Settings.Str("general.logfile"))!);
            _log = new StreamWriter(Settings.Str("general.logfile"), append: true) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return; }
        var min = Settings.Str("general.loglevel") switch
        {
            "Everything" => LogLevel.Debug,
            "Warnings" => Settings.Bool("general.minor") ? LogLevel.Notice : LogLevel.Warning,
            _ => LogLevel.Error,
        };
        var html = Settings.Str("general.logformat") == "HTML";
        var log = _log;
        Vlc.Log += (_, e) =>
        {
            if (e.Level < min) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{e.Level}] {e.Module}: {e.Message}";
            lock (log) log.WriteLine(html ? $"<div>{System.Net.WebUtility.HtmlEncode(line)}</div>" : line);
        };
    }

    // ───────────────────────── queue / playback API ─────────────────────────

    public void Open(IEnumerable<string> items, bool append = false)
    {
        var deep = Settings.Str("library.subfolders") != "Only this folder";
        // libVLC on Windows rejects "C:/x/y" — normalize every local path.
        var expanded = items.Select(p => p.Contains("://") ? p : Path.GetFullPath(p))
            .SelectMany(p => Directory.Exists(p) ? Library.MediaFiles(p, deep) : [p]).ToList();
        if (expanded.Count == 0) return;
        if (append && _index >= 0) Enqueue(expanded);
        else PlayList(expanded, 0);
        if (Settings.Bool("ui.front")) { Activate(); Topmost = true; Topmost = Settings.Bool("ui.ontop"); }
    }

    public void PlayList(IList<string> items, int start)
    {
        if (items.Count == 0) return;
        SaveProgress();
        _queue.Clear();
        _queue.AddRange(items);
        BuildShuffle();
        PlayAt(start);
    }

    public void Enqueue(IEnumerable<string> items)
    {
        var wasEmpty = _index < 0;
        _queue.AddRange(items);
        BuildShuffle();
        if (wasEmpty) PlayAt(0);
        else Osd(L.T("Added to queue"));
        Sidebar.Render();
    }

    public void PlayQueueAt(int i)
    {
        SaveProgress();
        PlayAt(i);
    }

    public void RemoveFromQueue(int i)
    {
        if (i < 0 || i >= _queue.Count) return;
        _queue.RemoveAt(i);
        BuildShuffle();
        if (i < _index) _index--;
        else if (i == _index) PlayAt(Math.Min(i, _queue.Count - 1), stopAtEnd: true);
    }

    public void ClearQueue()
    {
        SaveProgress();
        _mp.Stop();
        _queue.Clear();
        ShowIdle();
    }

    void BuildShuffle()
    {
        _shuffle = Enumerable.Range(0, _queue.Count).OrderBy(_ => Random.Shared.Next()).ToList();
    }

    int NextIndex(int dir)
    {
        if (_queue.Count == 0) return -1;
        if (!Settings.Bool("playback.shuffle")) return _index + dir;
        var pos = _shuffle.IndexOf(_index) + dir;
        return pos >= 0 && pos < _shuffle.Count ? _shuffle[pos] : -1;
    }

    async void PlayAt(int i, bool stopAtEnd = false, long startAt = -1, bool? autoplay = null)
    {
        if (i < 0 || i >= _queue.Count)
        {
            if (stopAtEnd) { _mp.Stop(); ShowIdle(); }
            return;
        }
        _index = i;
        _switch = Stopwatch.StartNew();
        var src = _queue[i];
        var isUrl = src.Contains("://");
        var media = isUrl ? new Media(Vlc, new Uri(src)) : new Media(Vlc, src, FromType.FromPath);
        try
        {
            var parse = isUrl ? MediaParseOptions.ParseNetwork : MediaParseOptions.ParseLocal | MediaParseOptions.FetchLocal;
            if (Settings.Bool("library.netmeta")) parse |= MediaParseOptions.FetchNetwork;
            var parsing = media.Parse(parse, 3000);
            var ext = isUrl ? "" : Path.GetExtension(src);
            if (!isUrl && (AudioExt.Contains(ext) || VideoExt.Contains(ext)))
            {
                // a known local file starts playing at once; its tags and cover fill in when parsing is done (~0.1 s)
                _metaPending = true;
                StartMedia(media, src, isUrl, startAt, autoplay ?? Settings.Bool("playback.autoplay"));
                await parsing;
                _switchParsed = _switch?.ElapsedMilliseconds ?? 0;
                _metaPending = false;
                if (_index != i || CurrentPath != src) return;
                ApplyMeta(media, src, isUrl, parsed: true);
                return;
            }
            await parsing;
            _switchParsed = _switch?.ElapsedMilliseconds ?? 0;
            if (_index != i || _queue.ElementAtOrDefault(i) != src) return; // user moved on while we were parsing
            StartMedia(media, src, isUrl, startAt, autoplay ?? Settings.Bool("playback.autoplay"));
        }
        finally { Library.DisposeLater(media); }
    }

    bool _metaPending;

    /// <summary>Title, artist, cover and everything derived from the tags; run again once a quick-started file is parsed.</summary>
    void ApplyMeta(Media media, string src, bool isUrl, bool parsed)
    {
        if (parsed && !isUrl && VideoExt.Contains(Path.GetExtension(src)) && media.Tracks.Length > 0 && media.Tracks.All(t => t.TrackType != TrackType.Video))
        {
            _isAudio = true; // e.g. an .mp4 that only holds music
            Seek.Waveform = MiniSeek.Waveform = true;
            ApplyLayout();
            UpdateControlButtons();
        }
        var title = media.Meta(MetadataType.Title);
        if (string.IsNullOrWhiteSpace(title)) title = Library.DisplayName(src);
        var artist = media.Meta(MetadataType.Artist);
        _title = title;
        _artist = string.IsNullOrWhiteSpace(artist) ? null : artist;
        _track = (title, _artist, media.Meta(MetadataType.Album), media.Duration / 1000.0);
        _tags = string.Join("\n", Enum.GetValues<MetadataType>().Select(t => media.Meta(t)).Where(v => !string.IsNullOrEmpty(v)));
        if (_cast != null) _cast.Title = title;
        _session?.SetTrack(_isAudio, title, _artist, _track.Album, media.Meta(MetadataType.ArtworkURL));
        TitleText.Text = Settings.Bool("ui.titlefile") ? title : "Pylon";
        MiniTitle.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title} - {artist}";
        Title = $"{title} - Pylon";
        MusicCaption.Text = MiniTitle.Text;
        SetArtwork(media.Meta(MetadataType.ArtworkURL));
        if (!parsed) return;
        if (Library.HistoryOf(src) is { } h) Library.Remember(src, title, _artist, h.Time, media.Duration, _isAudio);
        _lyricsFor = null;
        ApplyLyricsLayout(); // now with the real title and artist
        Sidebar.Render();
    }

    void StartMedia(Media media, string src, bool isUrl, long startAt, bool autoplay)
    {
        var ext = isUrl ? "" : Path.GetExtension(src);
        _isAudio = AudioExt.Contains(ext) || (media.Tracks.Length > 0 && media.Tracks.All(t => t.TrackType != TrackType.Video));
        _lyricsFor = null;
        ApplyMeta(media, src, isUrl, parsed: false);
        var title = _title;
        var folder = isUrl ? null : Path.GetFileName(Path.GetDirectoryName(src));
        FolderText.Text = folder;
        FolderBtn.Visibility = string.IsNullOrEmpty(folder) ? Visibility.Collapsed : Visibility.Visible;

        Seek.Waveform = MiniSeek.Waveform = _isAudio;
        Seek.SetWaveSeed(src);
        MiniSeek.SetWaveSeed(src);
        if (_isAudio && !isUrl && Settings.Bool("ui.realwave")) LoadWaveform(src);
        ClearLoop();
        _is360 = false;
        _view.Reset(animate: false);
        _chapters = [];
        UpdateMarks();

        // continue where you left off: Never / Ask (mockup banner) / Always
        var h = Library.HistoryOf(src);
        var resumable = h is { Time: > 10_000, Progress: < 0.95 } && !_isAudio && Settings.Bool("privacy.position");
        _resumeAt = 0;
        if (startAt < 0 && resumable)
        {
            switch (Settings.Str("playback.resume"))
            {
                case "Always": startAt = h!.Time; break;
                case "Ask": _resumeAt = h!.Time; break;
            }
        }
        if (_resumeAt > 0) Motion.In(ResumeBanner, dy: 24, scale: 0.96);
        else ResumeBanner.Visibility = Visibility.Collapsed;
        ResumeText.Text = L.F("You’ve watched till {0} already.", Fmt(_resumeAt));
        if (_resumeAt > 0) { _resumeTimer.Stop(); _resumeTimer.Start(); }

        if (startAt > 0) media.AddOption($":start-time={startAt / 1000.0:0.###}".Replace(',', '.'));
        if (!autoplay) media.AddOption(":start-paused");
        foreach (var sub in Library.Data.Subtitles.GetValueOrDefault(src) ?? [])
            if (File.Exists(sub)) media.AddSlave(MediaSlaveType.Subtitle, 4, new Uri(sub).AbsoluteUri);
        // recording and network streaming both duplicate what's playing; local playback stays on the display branch
        var branches = new List<string>();
        if (_recording && _recordFile != null) branches.Add($"std{{access=file,mux=ts,dst='{_recordFile.Replace("'", "")}'}}");
        if (_cast != null) branches.Add(_cast.SoutBranch);
        if (branches.Count > 0)
        {
            media.AddOption($":sout=#duplicate{{dst=display,{string.Join(",", branches.Select(b => "dst=" + b))}}}");
            media.AddOption(":sout-keep"); // the network stream stays open from one item to the next
        }

        _rateApplied = false;
        if (!_isAudio && _mp.Hwnd == IntPtr.Zero) { Video.ApplyTemplate(); Video.MediaPlayer = null; Video.MediaPlayer = _mp; } // never let libVLC open its own window
        Perf.Mark($"play, video hwnd {_mp.Hwnd}");
        _mp.Play(media);
        Library.Remember(src, title, _artist, h?.Time ?? 0, media.Duration, _isAudio);
        _preview.Open(!_isAudio && !isUrl && Settings.Bool("playback.thumbs") ? src : null);
        if (Settings.Bool("osd.filename") && !_isAudio) Osd(title, Settings.Num("osd.filetime"));
        ApplyLayout();
        UpdateControlButtons();
        Sidebar.Render();
        if (_mini) SizeMini();
    }

    Stopwatch? _switch;
    long _switchParsed, _switchStartMedia;
    public string SwitchTiming = "";

    void OnPlaying()
    {
        if (_switch != null) { SwitchTiming = $"parsed {_switchParsed} ms, StartMedia blocked the UI {_switchStartMedia} ms, playing {_switch.ElapsedMilliseconds} ms"; _switch = null; }
        LoadChapters();
        Detect360();
        if (!_rateApplied)
        {
            _rateApplied = true;
            if (Math.Abs(Settings.Num("playback.speed") - 1) > 0.01) _mp.SetRate((float)Settings.Num("playback.speed"));
            ApplyVideoSettings();
            ApplyAudioSettings();
            if (!_isAudio && Settings.Bool("video.fullscreen") && !_fullscreen && !_mini) ToggleFullscreen();
        }
        KeepAwake(!_isAudio && Settings.Bool("video.screensaver"));
    }

    void OnEnded()
    {
        SaveProgress(finished: true);
        KeepAwake(false);
        if (_sleepEndOfItem) { _sleepEndOfItem = false; Refresh(); Osd(L.T("Sleep timer: stopped")); return; }
        if (Settings.Str("playback.repeat") == "One") { PlayAt(_index, startAt: 0); return; }
        if (Settings.Bool("playback.stopeach")) { Refresh(); return; }
        var next = NextIndex(+1);
        if (next < 0 && Settings.Str("playback.repeat") == "All" && _queue.Count > 0)
        {
            BuildShuffle();
            next = Settings.Bool("playback.shuffle") ? _shuffle[0] : 0;
        }
        if (next >= 0 && next < _queue.Count) { PlayAt(next); return; }
        switch (Settings.Str("playback.finish"))
        {
            case "Clear the queue": ClearQueue(); break;
            case "Close Pylon": Close(); break;
            default: _mp.Stop(); ShowIdle(); break;
        }
    }

    void OnError()
    {
        var name = CurrentPath is { } p ? Library.DisplayName(p) : "this media";
        if (Settings.Bool("general.errors"))
            MessageBox.Show(this, L.F("Pylon can't open {0}.\nThe file may be damaged or in a format that isn't supported.", name), L.T("Playback error"), MessageBoxButton.OK, MessageBoxImage.Warning);
        else Osd(L.F("Can't open {0}", name));
        var next = NextIndex(+1);
        if (next >= 0 && next < _queue.Count) PlayAt(next);
    }

    /// <summary>Stores position for history / "Continue Watching".</summary>
    void SaveProgress(bool finished = false)
    {
        if (CurrentPath is not { } path || _mp.Length <= 0) return;
        var t = finished ? _mp.Length : _mp.Time;
        if (t <= 0) return;
        Library.Remember(path, _title, _artist, t, _mp.Length, _isAudio);
    }

    void SetArtwork(string? url)
    {
        var bmp = url == null ? null : LoadFull(url);
        ArtImage.Source = bmp;
        ArtBackdrop.SetImage(bmp);
        ArtPlaceholder.Visibility = bmp == null ? Visibility.Visible : Visibility.Collapsed;
    }

    static BitmapImage? LoadFull(string url)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 900;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; } // unreadable art just falls back to the placeholder
    }

    void ShowIdle()
    {
        _index = -1;
        TitleText.Text = MiniTitle.Text = "Pylon";
        Title = "Pylon";
        FolderBtn.Visibility = Visibility.Collapsed;
        ResumeBanner.Visibility = Visibility.Collapsed;
        TimeText.Text = DurationText.Text = "--:--:--";
        Seek.Value = MiniSeek.Value = 0;
        ClearLoop();
        _chapters = [];
        UpdateMarks();
        _is360 = false;
        _session?.Clear();
        _preview.Open(null);
        KeepAwake(false);
        ApplyLayout();
        UpdateControlButtons();
        Sidebar.Render();
    }

    void Refresh()
    {
        long len = _mp.Length, t = Math.Max(0, _mp.Time);
        if (len > 0)
        {
            if (!_mp.IsPlaying) ShowPosition((double)t / len); // while playing, SmoothSeek moves the thumb every frame
        }
        if (_index >= 0)
        {
            TimeText.Text = Fmt(t);
            DurationText.Text = len <= 0 ? "--:--:--" : _showRemaining ? "-" + Fmt(len - t) : Fmt(len);
            MiniTime.Text = len > 0 ? $"{Fmt(t)} / {Fmt(len)}" : Fmt(t);
        }
        PlayBtn.IsChecked = MiniPlay.IsChecked = _mp.IsPlaying;
        UpdateThumbPlay();
        _session?.SetState(_mp.IsPlaying, _index >= 0);
        if (_ticks % 5 == 0 && _index >= 0) _session?.SetTimeline(TimeSpan.FromMilliseconds(t), TimeSpan.FromMilliseconds(len));
        var spectrum = Settings.Str("ui.spectrum");
        AudioTap.Volume = _mp.Volume / 100.0;
        MusicSpectrum.Active = _mp.IsPlaying && spectrum != "Off";
        VideoSpectrum.Active = _mp.IsPlaying && spectrum == "Music and video";
        SpeedText.Text = $"{_mp.Rate:0.##}×";
        if (++_ticks % 25 == 0 && _mp.IsPlaying) SaveProgress(); // every ~5 s
    }

    static string Fmt(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"hh\:mm\:ss");

    // ───────────────────────── actions (buttons, hotkeys, menus) ─────────────────────────

    public void TogglePlay()
    {
        if (_index < 0) { if (_queue.Count > 0) PlayAt(0); else OpenFiles(); return; }
        if (_mp.IsPlaying) { _mp.SetPause(true); SaveProgress(); }
        else if (_mp.State is VLCState.Ended or VLCState.Stopped) PlayAt(_index, startAt: 0);
        else _mp.SetPause(false);
        PlayBtn.IsChecked = !_mp.IsPlaying; // optimistic; Refresh() corrects it
    }

    public void Stop()
    {
        SaveProgress();
        _mp.Stop();
        if (_recording) ToggleRecord();
        KeepAwake(false);
        Osd(L.T("Stopped"));
    }

    public void Next() => PlayQueueAt(NextIndex(+1) is var n && n >= 0 && n < _queue.Count ? n : (Settings.Str("playback.repeat") == "All" ? 0 : -1));

    public void Previous()
    {
        if (_mp.Time > 3000) _mp.Time = 0; // restart first, previous on a second press
        else PlayQueueAt(NextIndex(-1));
    }

    public void NextFrame() => _mp.NextFrame();

    public void Jump(long deltaMs)
    {
        if (!_mp.IsSeekable) return;
        _mp.Time = Math.Clamp(_mp.Time + deltaMs, 0, Math.Max(0, _mp.Length - 500));
        Osd($"{Fmt(_mp.Time)} / {Fmt(_mp.Length)}");
    }

    /// <summary>Skip amounts from Settings → Controls (very short / short / medium / long).</summary>
    public void Skip(int level, int dir) => Jump(dir * (long)(Settings.Num($"controls.skip{level}") * 1000));

    public void ChangeRate(int dir)
    {
        var rates = new[] { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f, 4f };
        var cur = Array.FindIndex(rates, r => r >= _mp.Rate - 0.01f);
        SetRate(dir == 0 ? 1f : rates[Math.Clamp((cur < 0 ? 3 : cur) + dir, 0, rates.Length - 1)]);
    }

    void SetRate(float rate)
    {
        rate = Math.Clamp(rate, 0.25f, 4f);
        _mp.SetRate(rate);
        Osd(L.F("Speed {0}×", rate.ToString("0.##")));
    }

    double MaxVolume => double.TryParse(Settings.Str("audio.maxvol").TrimEnd('%'), out var m) ? m : 125;

    void ApplyVolume()
    {
        _mp.Volume = (int)Math.Round(Volume.Value * MaxVolume);
        UpdateVolumeIcon();
    }

    void SetMute(bool mute)
    {
        _mp.Mute = mute;
        Volume.Muted = mute;
        UpdateVolumeIcon();
        Osd(mute ? L.T("Muted") : L.F("Volume {0}%", _mp.Volume));
    }

    public void ToggleMute() => SetMute(!_mp.Mute);

    public void NudgeVolume(int sign)
    {
        Volume.Value = Math.Clamp(Volume.Value + sign * Settings.Num("audio.step") / MaxVolume, 0, 1);
        ApplyVolume();
        if (_mp.Mute) SetMute(false);
        Osd(L.F("Volume {0}%", _mp.Volume));
    }

    void UpdateVolumeIcon() => MuteBtn.Tag = MiniMute.Tag = _mp.Mute ? "speaker-x" : Volume.Value <= 0 ? "speaker-none" : Volume.Value < 0.4 ? "speaker-low" : "speaker-high";

    public void CycleAudioTrack()
    {
        var tracks = _mp.AudioTrackDescription.Where(t => t.Id >= 0).ToList();
        if (tracks.Count == 0) return;
        var next = tracks[(tracks.FindIndex(t => t.Id == _mp.AudioTrack) + 1) % tracks.Count];
        _mp.SetAudioTrack(next.Id);
        Osd(L.F("Audio: {0}", next.Name));
    }

    public void CycleSubtitle()
    {
        var tracks = _mp.SpuDescription.ToList();
        if (tracks.Count == 0) { Osd(L.T("No subtitles")); return; }
        var next = tracks[(tracks.FindIndex(t => t.Id == _mp.Spu) + 1) % tracks.Count];
        _mp.SetSpu(next.Id);
        Osd(L.F("Subtitles: {0}", next.Name));
    }

    public void ShiftAudioDelay(double seconds) => Settings.Set("audio.delay", Math.Round(Settings.Num("audio.delay") + seconds, 2));
    public void ShiftSubtitleDelay(double seconds) => Settings.Set("subs.delay", Math.Round(Settings.Num("subs.delay") + seconds, 1));

    public void CycleAspect() => CycleSetting("video.aspect");
    public void CycleCrop() => CycleSetting("video.crop");
    public void ToggleDeinterlace() => Settings.Set("video.deint", Settings.Str("video.deint") == "On" ? "Off" : "On");

    void CycleSetting(string key)
    {
        var opts = Settings.Find(key)!.Options;
        var next = opts[(Array.IndexOf(opts, Settings.Str(key)) + 1) % opts.Length];
        Settings.Set(key, next);
        Osd($"{L.T(Settings.Find(key)!.Label)}: {L.T(next)}");
    }

    public void ShowPosition() => Osd($"{Fmt(_mp.Time)} / {Fmt(_mp.Length)}", 2);

    public void Snapshot()
    {
        if (_index < 0 || _isAudio) return;
        uint w = 0, h = 0;
        _mp.Size(0, ref w, ref h);
        var div = Settings.Str("video.snapsize") switch { "Half" => 2u, "Quarter" => 4u, _ => 1u };
        var dir = Settings.Str("video.snapdir");
        Directory.CreateDirectory(dir);
        var ext = Settings.Str("video.snapformat").ToLowerInvariant();
        var prefix = Settings.Str("video.snapprefix");
        string file;
        if (Settings.Bool("video.snapseq"))
        {
            var n = 1;
            while (File.Exists(file = Path.Combine(dir, $"{prefix}{n:00000}.{ext}"))) n++;
        }
        else file = Path.Combine(dir, $"{prefix}{DateTime.Now:yyyy-MM-dd-HH'h'mm'm'ss's'}.{ext}");
        var ok = _mp.TakeSnapshot(0, file, w / div, h / div);
        if (!ok) { Osd(L.T("Couldn't take a snapshot"), 2); return; }
        if (Settings.Bool("video.snappreview")) Osd(L.F("Snapshot saved: {0}", Path.GetFileName(file)), 2);
    }

    /// <summary>libVLC 3 has no record API: reopen the item at the same position with a duplicating stream output.</summary>
    public void ToggleRecord()
    {
        if (_index < 0) { RecordBtn.IsChecked = false; return; }
        var t = _mp.Time;
        _recording = !_recording;
        if (_recording)
        {
            var dir = Settings.Str("rec.dir");
            Directory.CreateDirectory(dir);
            var name = string.Concat(Path.GetFileNameWithoutExtension(Library.DisplayName(CurrentPath!)).Split(Path.GetInvalidFileNameChars()));
            _recordFile = Path.Combine(dir, $"recording-{DateTime.Now:yyyy-MM-dd-HH'h'mm'm'ss's'}-{name}.ts");
            Osd(L.T("Recording…"), 2);
        }
        else Osd(L.F("Saved {0}", Path.GetFileName(_recordFile)), 3);
        RecordBtn.IsChecked = _recording;
        PlayAt(_index, startAt: t, autoplay: true);
    }

    public void Escape()
    {
        if (_fullscreen) ToggleFullscreen();
        else if (_minimal) ToggleMinimal();
        else if (_mini) ToggleMiniplayer();
        else if (FxPanel.Visibility == Visibility.Visible) { FxPanel.Visibility = Visibility.Collapsed; ApplyLayout(); }
    }

    public void ToggleMinimal() { _minimal = !_minimal; ApplyLayout(); }
    public void ToggleLibrary() => ToggleSidebar();
    public void ToggleQueue() => ToggleSidebar(LibraryPanel.Section.Queue);

    public void FocusSearch()
    {
        if (Sidebar.Visibility != Visibility.Visible) ToggleSidebar();
        Sidebar.OpenSearch();
    }

    public void ShowControlsNow() { if (_fullscreen) ShowControls(); }

    public void ToggleEffects()
    {
        if (Motion.IsShown(FxPanel)) HideEffects();
        else ShowEffects(video: !_isAudio && _index >= 0);
    }

    void HideEffects()
    {
        SlideHost(FxHost, 0);
        Motion.Out(FxPanel, () => { FxPanel.Visibility = Visibility.Collapsed; ApplyLayout(); }, dx: 24, spring: Spring.Smooth, animate: Motion.ByPointer);
    }

    /// <summary>Opens or closes a side panel's column on a spring (instant from the keyboard, like the panel itself).</summary>
    static void SlideHost(Border host, double width)
    {
        if (Motion.ByPointer) Motion.Animate(host, WidthProperty, width, Spring.Smooth);
        else Motion.Set(host, WidthProperty, width);
    }

    void PanelHost_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyStageMargin();

    void ShowEffects(bool video)
    {
        if (_fullscreen) ToggleFullscreen();
        if (_mini) ToggleMiniplayer();
        FxPanel.ShowTab(video);
        Motion.In(FxPanel, dx: 24, animate: Motion.ByPointer);
        SlideHost(FxHost, FxPanel.Width);
        ApplyLayout();
    }

    public void OpenSettings() => OpenSettings(null);

    public void OpenSettings(string? category)
    {
        if (_settings is { IsLoaded: true }) { if (category != null) _settings.Show(category); _settings.Activate(); return; }
        _settings = new SettingsWindow(this, category);
        _settings.Show();
    }

    /// <summary>Texts are resolved when windows are built, so a new language needs a fresh start.</summary>
    void AskRestart()
    {
        Settings.Save();
        var lang = Settings.Str("general.language");
        var ru = lang == "Русский" || (lang == "System" && System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru");
        var text = ru ? "Перезапустить Pylon, чтобы сменить язык?" : "Restart Pylon to change the language?";
        if (MessageBox.Show(_settings is { IsLoaded: true } ? _settings : this, text, "Pylon", MessageBoxButton.YesNo) == MessageBoxResult.Yes) Restart();
    }

    public void Restart()
    {
        SingleInstance.Release();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        Close();
    }

    bool _updating;

    /// <summary>Asks GitHub for a newer release. Silent on errors / no update unless the user asked.</summary>
    public async void CheckForUpdates(bool manual, Window? owner = null)
    {
        if (_updating) return;
        owner ??= this;
        Updater.Release? r;
        try
        {
            r = await Updater.CheckAsync();
            Updater.MarkChecked();
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            if (manual) MessageBox.Show(owner, L.T("Couldn't check for updates. Check your internet connection."), "Pylon");
            return;
        }
        if (r == null)
        {
            if (manual) MessageBox.Show(owner, L.F("You're up to date (version {0}).", Updater.Current.ToString(3)), "Pylon");
            return;
        }
        var installed = Updater.IsInstalled;
        if (!DialogWindow.UpdateAvailable(owner, r, installed)) return;
        if (!installed || r.SetupUrl == null) { Updater.OpenPage(r); return; }
        _updating = true;
        try
        {
            await Updater.DownloadAndRunAsync(r, new Progress<double>(p => Osd(L.F("Downloading update… {0}%", (int)(p * 100)), 2)));
            SaveProgress();
            SingleInstance.Release();
            Close(); // the installer replaces the files and starts the new version
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            _updating = false;
            MessageBox.Show(owner, L.T("The update couldn't be downloaded. Try again later."), "Pylon");
        }
    }

    /// <summary>First start after an update: show what this version brought (the release notes on GitHub).</summary>
    async void ShowWhatsNewIfUpdated()
    {
        var current = Updater.Current;
        var last = Settings.Str("app.lastversion");
        Settings.Set("app.lastversion", current.ToString(3));
        if (!Version.TryParse(last, out var previous) || previous >= current) return;
        string? notes;
        try { notes = await Updater.NotesForAsync(current); }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException) { return; }
        if (notes != null) DialogWindow.WhatsNew(this, current, notes);
    }

    public void ShowAbout() => DialogWindow.About(this, Vlc.Version);

    // ───────────────────────── live settings ─────────────────────────

    void OnSettingChanged(string key)
    {
        switch (key)
        {
            case "*": ThemeManager.Apply(Settings.Str("general.theme"), Settings.Str("general.accent")); ApplyVideoSettings(); ApplyAudioSettings(); UpdateControlButtons(); break;
            case "general.language": AskRestart(); break;
            case "general.theme" or "general.accent": ThemeManager.Apply(Settings.Str("general.theme"), Settings.Str("general.accent")); break;
            case "ui.corners": ApplyCorners(); break;
            case "general.log" or "general.logfile" or "general.loglevel" or "general.minor" or "general.logformat": HookLog(); break;
            case "ui.ontop": Topmost = Settings.Bool("ui.ontop") || _mini; break;
            case "ui.decorations" or "library.enabled" or "ui.cover" or "ui.spectrum" or "ui.videobg" or "ui.livebg" or "ui.lyrics" or "audio.visualizer" or "ui.fscontrols" or "ui.fsopacity": ApplyLayout(); break;
            case "ui.titlefile": if (_index >= 0) TitleText.Text = Settings.Bool(key) ? _title : "Pylon"; break;
            case "playback.speed": SetRate((float)Settings.Num(key)); break;
            case "playback.shuffle": BuildShuffle(); UpdateControlButtons(); break;
            case "playback.repeat" or "controls.skipbtn": UpdateControlButtons(); break;
            case "playback.thumbs": _preview.Open(Settings.Bool(key) && !_isAudio && CurrentPath is { } p && !p.Contains("://") ? p : null); break;
            case "audio.eqpreset": Effects.ApplyPreset(Settings.Str(key)); ApplyEqualizer(edited: true); break;
            case "audio.eq": ApplyEqualizer(edited: true); break;
            case "audio.maxvol": ApplyVolume(); break;
            case "audio.device" or "audio.speakers" or "audio.delay": ApplyAudioSettings(); break;
            case "subs.delay": _mp.SetSpuDelay((long)(Settings.Num(key) * 1_000_000)); Osd(L.F("Subtitle delay {0} s", Settings.Num(key).ToString("+0.0;-0.0;0"))); break;
            case "net.preset": ApplyNetworkPreset(); break;
            case "net.file" or "net.disc" or "net.stream" or "net.live": if (!_netPresetApplying) Settings.Set("net.preset", "Custom"); break;
            case "perf.realtime" or "perf.priority": ApplyPriority(); break;
            case "rec.cast": StartCastDiscovery(); break;
            case "ui.hideafter": _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.5, Settings.Num(key))); break;
        }
        if (key.StartsWith("fx.eq.")) ApplyEqualizer(edited: true);
        if (key.StartsWith("fxv.") || key.StartsWith("video.")) ApplyVideoSettings();
        if (key.StartsWith("ui.btn.")) UpdateControlButtons();
        if (key.StartsWith("hotkey.") || key.StartsWith("ghotkey.")) Hotkeys.RegisterGlobal(this);
        if (Settings.IsEngineKey(key)) { _engineTimer.Stop(); _engineTimer.Start(); } // debounce slider drags
    }

    void ApplyVideoSettings()
    {
        if (_index < 0 || _isAudio) return;
        var adjust = new[] { ("fxv.hue", VideoAdjustOption.Hue), ("fxv.brightness", VideoAdjustOption.Brightness), ("fxv.contrast", VideoAdjustOption.Contrast), ("fxv.saturation", VideoAdjustOption.Saturation), ("fxv.gamma", VideoAdjustOption.Gamma) };
        var custom = adjust.Any(a => Math.Abs(Settings.Num(a.Item1) - double.Parse(Settings.Find(a.Item1)!.Default, System.Globalization.CultureInfo.InvariantCulture)) > 0.001);
        _mp.SetAdjustInt(VideoAdjustOption.Enable, custom ? 1 : 0);
        if (custom) foreach (var (key, opt) in adjust) _mp.SetAdjustFloat(opt, (float)Settings.Num(key));

        _mp.AspectRatio = Settings.Str("video.aspect") is var ar && ar != "Automatic" ? ar : null;
        uint w = 0, h = 0;
        _mp.Size(0, ref w, ref h);
        int t = Settings.Int("fxv.crop.t"), b = Settings.Int("fxv.crop.b"), l = Settings.Int("fxv.crop.l"), r = Settings.Int("fxv.crop.r");
        if (t + b + l + r > 0 && w > l + r && h > t + b) _mp.CropGeometry = $"{w - l - r}x{h - t - b}+{l}+{t}";
        else _mp.CropGeometry = Settings.Str("video.crop") is var crop && crop != "None" ? crop : null;

        _mp.Scale = Settings.Str("video.zoom") switch { "1:4" => 0.25f, "1:2" => 0.5f, "1:1" => 1f, "2:1" => 2f, _ => Settings.Bool("video.scale") ? 0f : 1f };
        var mode = Settings.Str("video.deintmode") switch { "Automatic" => "blend", "Yadif (2x)" => "yadif2x", var m => m.ToLowerInvariant() };
        switch (Settings.Str("video.deint"))
        {
            case "On": _mp.SetDeinterlace(mode); break;
            case "Off": _mp.SetDeinterlace(null); break;
        }
        FitVideo(); // aspect / crop changes reshape the box
    }

    void ApplyAudioSettings()
    {
        var device = Settings.Str("audio.device");
        if (device != "System default" && _mp.AudioOutputDeviceEnum.FirstOrDefault(d => d.Description == device) is { } dev) _mp.SetOutputDevice(dev.DeviceIdentifier);
        var channel = Settings.Str("audio.speakers") switch
        {
            "Stereo" => AudioOutputChannel.Stereo, "Reverse stereo" => AudioOutputChannel.RStereo, "Left" => AudioOutputChannel.Left,
            "Right" => AudioOutputChannel.Right, "Dolby Surround" => AudioOutputChannel.Dolbys, _ => (AudioOutputChannel?)null,
        };
        if (channel is { } c) _mp.SetChannel(c);
        _mp.SetAudioDelay((long)(Settings.Num("audio.delay") * 1_000_000));
        _mp.SetSpuDelay((long)(Settings.Num("subs.delay") * 1_000_000));
        ApplyEqualizer();
    }

    /// <summary>
    /// Live equalizer through libVLC's API, unless other effects are on: that API would replace the whole filter chain
    /// with just the equalizer, so then the chain is rebuilt with the engine instead (debounced restart, resumes in place).
    /// </summary>
    void ApplyEqualizer(bool edited = false)
    {
        if (AudioChain.HasOthers)
        {
            if (edited) { _engineTimer.Stop(); _engineTimer.Start(); }
            return;
        }
        using var eq = Effects.Build();
        if (eq != null) _mp.SetEqualizer(eq);
        else _mp.UnsetEqualizer();
    }

    void ApplyNetworkPreset()
    {
        var v = Settings.Str("net.preset") switch
        {
            "Low latency" => new[] { 100, 300, 300, 100 },
            "Smooth" => [1000, 1000, 3000, 1000],
            "Normal" => [300, 300, 1000, 300],
            _ => null,
        };
        if (v == null) return;
        _netPresetApplying = true;
        Settings.Set("net.file", v[0]); Settings.Set("net.disc", v[1]); Settings.Set("net.stream", v[2]); Settings.Set("net.live", v[3]);
        _netPresetApplying = false;
    }

    static void ApplyPriority()
    {
        var p = Process.GetCurrentProcess();
        p.PriorityClass = Settings.Bool("perf.realtime") ? ProcessPriorityClass.High : Settings.Str("perf.priority") switch
        {
            "Below normal" => ProcessPriorityClass.BelowNormal, "Above normal" => ProcessPriorityClass.AboveNormal, "High" => ProcessPriorityClass.High, _ => ProcessPriorityClass.Normal,
        };
    }

    /// <summary>"Resize window to fit video": Never / On first video / Always.</summary>
    bool _fittedOnce;
    void FitToVideo()
    {
        var mode = Settings.Str("ui.fit");
        if (mode == "Never" || (mode == "On first video" && _fittedOnce) || _fullscreen || _mini || WindowState != WindowState.Normal) return;
        uint w = 0, h = 0;
        if (!_mp.Size(0, ref w, ref h) || w == 0 || h == 0) return;
        _fittedOnce = true;
        var work = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double vw = Math.Min(w / dpi, work.Width * 0.9), vh = vw * h / w;
        if (vh > work.Height * 0.85) { vh = work.Height * 0.85; vw = vh * w / h; }
        var chromeW = ActualWidth - Stage.ActualWidth;
        var chromeH = ActualHeight - Stage.ActualHeight;
        Width = Math.Max(MinWidth, vw + chromeW);
        Height = Math.Max(MinHeight, vh + chromeH);
    }

    // ───────────────────────── layout / chrome ─────────────────────────

    void ApplyLayout()
    {
        var hasMedia = _index >= 0;
        var visualizer = Settings.Str("audio.visualizer") != "Off";
        bool video = hasMedia && (!_isAudio || visualizer);
        IdleView.Visibility = hasMedia ? Visibility.Collapsed : Visibility.Visible;
        MusicView.Visibility = hasMedia && _isAudio && !visualizer ? Visibility.Visible : Visibility.Collapsed;
        ArtFrame.Visibility = Settings.Bool("ui.cover") ? Visibility.Visible : Visibility.Hidden;
        MusicCaption.Visibility = _mini ? Visibility.Collapsed : Visibility.Visible;
        var spectrum = Settings.Str("ui.spectrum") != "Off";
        MusicSpectrum.Visibility = spectrum && !_mini ? Visibility.Visible : Visibility.Collapsed;
        MusicLayout.Margin = _mini ? new Thickness(16) : new Thickness(40, 72, 40, spectrum ? 92 : 24); // the bars get their own strip under the caption
        Video.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        ControlsBar.BorderThickness = new Thickness(0, _isAudio && hasMedia ? 1 : 0, 0, 0);

        bool chromeless = _fullscreen || _minimal || _mini || !Settings.Bool("ui.decorations");
        TopBar.Visibility = chromeless ? Visibility.Collapsed : Visibility.Visible;
        WindowChrome.GetWindowChrome(this).CaptionHeight = chromeless ? 0 : 40;
        IdleHint.Visibility = _mini ? Visibility.Collapsed : Visibility.Visible;
        IdleLogo.Width = IdleLogo.Height = _mini ? 64 : 96;
        Root.RowDefinitions[0].Height = new GridLength(chromeless ? 0 : 40);
        if (_fullscreen || _minimal || _mini || !Settings.Bool("library.enabled")) Sidebar.Visibility = Visibility.Collapsed;
        if (_fullscreen || _minimal || _mini) FxPanel.Visibility = Visibility.Collapsed;
        if (Sidebar.Visibility != Visibility.Visible) Motion.Set(SidebarHost, WidthProperty, 0);
        if (FxPanel.Visibility != Visibility.Visible) Motion.Set(FxHost, WidthProperty, 0);
        SidebarBtn.Visibility = Settings.Bool("library.enabled") ? Visibility.Visible : Visibility.Collapsed;
        var sidebar = Sidebar.Visibility == Visibility.Visible;
        var fx = FxPanel.Visibility == Visibility.Visible;
        ApplyStageMargin();

        // Fullscreen video: controls float over the picture inside the VideoView overlay window.
        bool floating = _fullscreen && video && Settings.Bool("ui.fscontrols");
        if (floating && ControlsBar.Parent == Root)
        {
            Root.Children.Remove(ControlsBar);
            OverlayControlsHost.Content = ControlsBar;
            ShowControls();
        }
        else if (!floating && ControlsBar.Parent != Root)
        {
            OverlayControlsHost.Content = null;
            Root.Children.Add(ControlsBar);
            Motion.In(OverlayControlsHost, animate: false); // also cancels a running fade-out
            Overlay.Cursor = null;
            _hideTimer.Stop();
        }
        ControlsBar.Opacity = floating ? Settings.Num("ui.fsopacity") / 100 : 1;
        var hideBar = (_minimal && !_fullscreen) || _mini || (_fullscreen && !Settings.Bool("ui.fscontrols"));
        ControlsBar.Visibility = hideBar ? Visibility.Collapsed : Visibility.Visible;
        MiniBar.Visibility = _mini ? Visibility.Visible : Visibility.Collapsed;
        UpdateAmbient();
        ApplyLyricsLayout();
        UpdateCornerMask();
    }

    // ───────── seek bar that glides instead of stepping 5 times a second ─────────

    double _shownMs = -1;
    long _lastFrame;

    void ShowPosition(double v)
    {
        v = Math.Clamp(v, 0, 1);
        if (!Seek.IsDragging) Seek.Value = v;
        if (!MiniSeek.IsDragging) MiniSeek.Value = v;
    }

    /// <summary>
    /// Every frame the thumb advances on its own clock and is eased toward libVLC's reported time, so it never steps
    /// or jumps back by a few pixels; real jumps (seeking, a new track) are taken as they are.
    /// </summary>
    void SmoothSeek(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = _lastFrame == 0 ? 0 : Math.Min(0.1, (now - _lastFrame) / (double)Stopwatch.Frequency);
        _lastFrame = now;
        long len = _mp.Length;
        if (_index < 0 || len <= 0 || !_mp.IsPlaying) { _shownMs = -1; return; }
        var target = PlaybackClock() * 1000;
        if (_shownMs < 0 || Math.Abs(target - _shownMs) > 700) _shownMs = target;
        else _shownMs = Math.Max(_shownMs, _shownMs + dt * 1000 * _mp.Rate + (target - _shownMs) * 0.1); // never backwards
        ShowPosition(_shownMs / len);
    }

    // ───────── lyrics ─────────

    string? _lyricsFor;
    (string Title, string? Artist, string? Album, double Duration) _track;
    string _tags = "";
    long _clockMs, _clockStamp;

    void Lyrics_Click(object sender, RoutedEventArgs e) => Settings.Set("ui.lyrics", !Settings.Bool("ui.lyrics"));

    void ApplyLyricsLayout()
    {
        var on = Settings.Bool("ui.lyrics") && _isAudio && _index >= 0 && !_mini;
        LyricsBtn.IsChecked = Settings.Bool("ui.lyrics");
        if ((LyricsPane.Visibility == Visibility.Visible) != on)
        {
            LyricsColumn.Width = on ? new GridLength(1.5, GridUnitType.Star) : new GridLength(0);
            if (on) Motion.In(LyricsPane, dx: 40, animate: Motion.ByPointer);
            else LyricsPane.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(() => MusicLayout_SizeChanged(MusicLayout, null!), DispatcherPriority.Loaded);
        }
        if (on && !_metaPending && CurrentPath is { } path && _lyricsFor != path) LoadLyrics(path); // fetched only once someone looks, with real tags
    }

    async void LoadLyrics(string path)
    {
        _lyricsFor = path;
        LyricsPane.Clock ??= PlaybackClock;
        if (LyricsPane.Tag == null)
        {
            LyricsPane.Tag = true;
            LyricsPane.Seek += s => { if (_mp.IsSeekable) _mp.Time = (long)(s * 1000); };
        }
        LyricsPane.ShowMessage(L.T("Looking for lyrics…"));
        var doc = await Lyrics.FindAsync(path, _track.Title, _track.Artist, _track.Album, _track.Duration, _tags);
        if (CurrentPath != path) return;
        if (doc != null) LyricsPane.Show(doc);
        else LyricsPane.ShowMessage(L.T("No lyrics for this track.\nPut a .ttml or .lrc file with the same name next to it."));
    }

    /// <summary>libVLC reports time in steps; between them the lyrics clock runs on its own so the fill sweeps smoothly.</summary>
    double PlaybackClock()
    {
        var t = _mp.Time;
        var now = Stopwatch.GetTimestamp();
        if (t != _clockMs) { _clockMs = t; _clockStamp = now; }
        var ahead = _mp.IsPlaying ? Math.Min(0.5, (now - _clockStamp) / (double)Stopwatch.Frequency * _mp.Rate) : 0;
        return t / 1000.0 + ahead;
    }

    // ───────── around the video: blurred frames instead of black bars ─────────

    DispatcherTimer? _ambientTimer;
    int _ambientSlot;
    bool _ambientBusy;

    bool AmbientOn => Settings.Str("ui.videobg") != "Black" && Video.Visibility == Visibility.Visible && !_isAudio && !_fullscreen && !_mini;

    void UpdateAmbient()
    {
        var on = AmbientOn;
        VideoBackdrop.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Ambient.Live = on && Settings.Bool("ui.livebg");
        ArtBackdrop.Live = MusicView.Visibility == Visibility.Visible && Settings.Bool("ui.livebg");
        FitVideo();
        _ambientTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { if (AmbientOn && _mp.IsPlaying) GrabAmbient(); }, Dispatcher);
        _ambientTimer.IsEnabled = on;
    }

    /// <summary>With a backdrop the video box takes the picture's own shape, so the blur shows around it.</summary>
    void FitVideo()
    {
        uint w = 0, h = 0;
        if (!AmbientOn || !_mp.Size(0, ref w, ref h) || w == 0 || h == 0 || Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0)
        {
            Video.Width = Video.Height = double.NaN;
            return;
        }
        static double? Ratio(string s) => s.Split(':') is [var a, var b] && double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) && y > 0 ? x / y : null;
        var sar = _mp.Media?.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video).Data.Video is { SarNum: > 0, SarDen: > 0 } v ? (double)v.SarNum / v.SarDen : 1;
        var aspect = Ratio(Settings.Str("video.aspect")) ?? Ratio(Settings.Str("video.crop")) ?? w * sar / h;
        double sw = Stage.ActualWidth, sh = Stage.ActualHeight;
        if (sw / sh > aspect) { Video.Height = sh; Video.Width = Math.Round(sh * aspect); }
        else { Video.Width = sw; Video.Height = Math.Round(sw / aspect); }
    }

    void Video_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCornerMask();
    void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => FitVideo();

    /// <summary>A tiny snapshot of the current frame becomes the blurred backdrop (taken off the UI thread).</summary>
    async void GrabAmbient()
    {
        if (_ambientBusy) return;
        _ambientBusy = true;
        var file = Path.Combine(Path.GetTempPath(), $"pylon-ambient-{_ambientSlot ^= 1}.png");
        var mp = _mp;
        var frame = await Task.Run(() =>
        {
            try
            {
                File.Delete(file);
                if (!mp.TakeSnapshot(0, file, 160, 0)) return null;
                for (var i = 0; i < 40 && !File.Exists(file); i++) Thread.Sleep(25);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(File.ReadAllBytes(file));
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception e) when (e is IOException or NotSupportedException or ObjectDisposedException or UnauthorizedAccessException) { return null; }
        });
        _ambientBusy = false;
        if (frame != null && AmbientOn) Ambient.SetImage(frame);
    }

    void ToggleSidebar(LibraryPanel.Section? section = null)
    {
        if (!Settings.Bool("library.enabled")) return;
        var show = !Motion.IsShown(Sidebar) || (section != null && Sidebar.Current != section);
        if (show && (_fullscreen || _minimal || _mini)) { _minimal = false; if (_fullscreen) ToggleFullscreen(); if (_mini) ToggleMiniplayer(); }
        if (show) Motion.In(Sidebar, dx: -24, animate: Motion.ByPointer);
        if (show && section != null) Sidebar.Show(section.Value);
        SidebarBtn.IsChecked = show;
        ListBtn.IsChecked = show && Sidebar.Current == LibraryPanel.Section.Queue;
        SlideHost(SidebarHost, show ? Sidebar.Width : 0);
        if (show) ApplyLayout();
        else Motion.Out(Sidebar, () => { Sidebar.Visibility = Visibility.Collapsed; ApplyLayout(); }, dx: -24, spring: Spring.Smooth, animate: Motion.ByPointer);
    }

    /// <summary>
    /// Video sits in a rounded 10px frame; music artwork bleeds to the window edges (per mockup). The 10px gap shrinks as a
    /// side panel's column grows, so opening one glides instead of jumping by 10px.
    /// </summary>
    void ApplyStageMargin()
    {
        var hasMedia = _index >= 0;
        double Edge(Border host) => Math.Max(0, 10 - (double.IsNaN(host.Width) ? 0 : host.Width));
        Stage.Margin = _fullscreen || _mini || (hasMedia && _isAudio && !_minimal) ? new Thickness(0)
            : _minimal ? new Thickness(10) : new Thickness(Edge(SidebarHost), 1, Edge(FxHost), 0);
    }

    double _radius = 10;

    /// <summary>"Corner rounding": the stage, cover art and (Windows 11) the window itself.</summary>
    void ApplyCorners()
    {
        _radius = Settings.Str("ui.corners") switch { "Off" => 0, "Slight" => 6, "Strong" => 16, _ => 10 };
        IdleView.CornerRadius = new CornerRadius(_radius);
        ArtFrame.CornerRadius = new CornerRadius(_radius + 2);
        ArtClip.RadiusX = ArtClip.RadiusY = _radius + 2;
        UpdateCornerMask();
        int pref = _radius == 0 ? 1 : _radius <= 6 ? 3 : 2; // DWMWCP_DONOTROUND / ROUNDSMALL / ROUND
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref pref, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE, no-op before Windows 11
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void UpdateCornerMask()
    {
        var r = new Rect(0, 0, Overlay.ActualWidth, Overlay.ActualHeight);
        CornerMask.Data = _fullscreen || _mini || r.IsEmpty || _radius == 0 || AmbientOn // the mask is a solid color; over the blur it would show as dark corners
            ? null
            : new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(r), new RectangleGeometry(r, _radius, _radius));
    }

    void ShowControls()
    {
        Motion.In(OverlayControlsHost, dy: 12, animate: _fullscreen);
        Overlay.Cursor = null;
        _hideTimer.Stop();
        if (Settings.Str("ui.autohide") != "Never") _hideTimer.Start();
    }

    void HideControls()
    {
        _hideTimer.Stop();
        if (!_fullscreen || ControlsBar.IsMouseOver) return;
        Motion.Out(OverlayControlsHost, () => OverlayControlsHost.Visibility = Visibility.Hidden, dy: 12, spring: Spring.Smooth);
        Overlay.Cursor = Cursors.None;
    }

    public void ToggleFullscreen()
    {
        if (_mini) ToggleMiniplayer();
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _restoreState = WindowState;
            _restoreBounds = RestoreBounds;
            var b = MonitorBounds(Settings.Str("ui.fsscreen") == "Primary screen");
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            Root.Margin = new Thickness(0);
            Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
            Left = _restoreBounds.Left; Top = _restoreBounds.Top; Width = _restoreBounds.Width; Height = _restoreBounds.Height;
            WindowState = _restoreState;
        }
        ApplyLayout();
    }

    // ───────── miniplayer (mockup "Miniplayer", 431px wide) ─────────

    Rect _miniRestore;
    WindowState _miniRestoreState;

    public void ToggleMiniplayer()
    {
        if (_fullscreen) ToggleFullscreen();
        _mini = !_mini;
        if (_mini)
        {
            _miniRestoreState = WindowState;
            _miniRestore = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            WindowState = WindowState.Normal;
            MinWidth = 320;
            MinHeight = 200;
            Width = 431;
            SizeMini();
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 24;
            Top = work.Bottom - Height - 24;
            Topmost = true;
        }
        else
        {
            MinWidth = 820;
            MinHeight = 460;
            Left = _miniRestore.Left; Top = _miniRestore.Top; Width = _miniRestore.Width; Height = _miniRestore.Height;
            WindowState = _miniRestoreState;
            Topmost = Settings.Bool("ui.ontop");
        }
        ApplyLayout();
    }

    /// <summary>Miniplayer height: video at its aspect ratio (16:9 until known), a square-ish cover for music. Keeps the bottom edge in place.</summary>
    void SizeMini()
    {
        double stageH = 300;
        if (_index >= 0 && !_isAudio)
        {
            uint w = 0, h = 0;
            stageH = _mp.Size(0, ref w, ref h) && w > 0 && h > 0 ? Math.Clamp(Width * h / w, 160, 480) : Width * 9 / 16;
        }
        var bottom = Top + Height;
        Height = stageH + MiniBarHeight;
        if (!double.IsNaN(bottom) && IsLoaded) Top = bottom - Height;
    }

    const double MiniBarHeight = 118;

    /// <summary>Without the top bar, the stage and the mini controls move the window.</summary>
    void DragArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // a click on the music view plays / pauses like a click on video does (not on lyrics lines, which seek)
        if (MusicView.IsVisible && !_mini && !_minimal && e.ChangedButton == MouseButton.Left && Settings.Str("controls.click") == "Play / Pause"
            && !(e.OriginalSource is DependencyObject src && (FindAncestor<LyricsView>(src) != null || FindAncestor<ButtonBase>(src) != null)))
        {
            _clickTimer.Stop();
            if (e.ClickCount == 1) _clickTimer.Start();
        }
        if (e.ClickCount == 2 && Video.IsVisible && !_mini && e.ChangedButton == MouseButton.Left && Settings.Str("controls.dblclick") == "Toggle fullscreen")
        {
            ToggleFullscreen(); // double click on the blurred area around the video
            e.Handled = true;
            return;
        }
        if (!(_mini || _minimal) || _fullscreen || e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null) return;
        if (e.OriginalSource is DependencyObject d2 && FindAncestor<SeekBar>(d2) != null) return;
        if (e.ClickCount == 2 && _mini) { ToggleMiniplayer(); e.Handled = true; return; }
        try { DragMove(); } catch (InvalidOperationException) { }
        e.Handled = true;
    }

    static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    void FitMaximized()
    {
        // WindowChrome lets a maximized borderless window spill past the screen edge by the resize frame.
        if (_fullscreen) return;
        if (WindowState != WindowState.Maximized) { Root.Margin = new Thickness(0); return; }
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var px = GetSystemMetrics(32 /*SM_CXFRAME*/) + GetSystemMetrics(92 /*SM_CXPADDEDBORDER*/);
        Root.Margin = new Thickness(px / dpi);
    }

    Rect MonitorBounds(bool primary)
    {
        var mon = primary ? MonitorFromPoint(default, 1 /*PRIMARY*/) : MonitorFromWindow(new WindowInteropHelper(this).Handle, 2 /*NEAREST*/);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref info);
        var dpi = VisualTreeHelper.GetDpi(this);
        var r = info.rcMonitor;
        return new Rect(r.left / dpi.DpiScaleX, r.top / dpi.DpiScaleY, (r.right - r.left) / dpi.DpiScaleX, (r.bottom - r.top) / dpi.DpiScaleY);
    }

    void SaveBounds()
    {
        if (!Settings.Bool("ui.remember")) return;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        Settings.Set("app.bounds", FormattableString.Invariant($"{b.Left},{b.Top},{b.Width},{b.Height},{(WindowState == WindowState.Maximized ? 1 : 0)}"));
    }

    void RestoreSavedBounds()
    {
        if (SnapMode || !Settings.Bool("ui.remember")) return;
        var p = Settings.Str("app.bounds").Split(',');
        if (p.Length != 5 || !p.Take(4).All(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))) return;
        var v = p.Take(4).Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var r = new Rect(v[0], v[1], v[2], v[3]);
        if (!r.IntersectsWith(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
        if (p[4] == "1") WindowState = WindowState.Maximized;
    }

    void MusicLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = LyricsPane.Visibility == Visibility.Visible ? MusicLayout.ActualWidth * 0.4 : MusicLayout.ActualWidth; // cover column next to the lyrics
        var size = Math.Max(80, Math.Min(738, Math.Min(width, MusicLayout.ActualHeight - (_mini ? 0 : 70))));
        ArtFrame.Width = ArtFrame.Height = size;
        ArtClip.Rect = new Rect(0, 0, size, size);
    }

    // ───────── customize player ─────────

    static readonly (string Id, string Label, bool Default)[] ControlButtons =
    [
        ("stop", "Stop", false), ("skip15", "Skip back / forward", true), ("time", "Elapsed time", true), ("duration", "Duration", true),
        ("frame", "Next frame", false), ("speed", "Playback speed", false), ("snapshot", "Snapshot", false), ("record", "Record", false),
        ("abloop", "A-B loop", false), ("loop", "Repeat", false), ("shuffle", "Shuffle", false), ("volume", "Volume", true), ("audio", "Audio", true), ("subs", "Subtitles", true),
        ("lyrics", "Lyrics", true), ("psettings", "Playback settings", true), ("fullscreen", "Fullscreen", false), ("queue", "Queue", true), ("mini", "Miniplayer", false),
    ];

    static MainWindow()
    {
        foreach (var (id, label, def) in ControlButtons)
            Settings.Register(new Opt { Kind = OptKind.Toggle, Key = "ui.btn." + id, Label = label, Default = def ? "true" : "false" });
        Settings.Register(new Opt { Kind = OptKind.Toggle, Key = "ui.lyrics", Label = "Lyrics", Default = "false" }); // lyrics panel open
    }

    static bool ButtonOn(string id) => Settings.Bool("ui.btn." + id);

    void UpdateControlButtons()
    {
        static Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.Visibility = V(ButtonOn("stop"));
        Back15.Visibility = Fwd15.Visibility = V(ButtonOn("skip15") && _index >= 0);
        var skip = Settings.Int("controls.skipbtn");
        Back15.Tag = Fwd15.Tag = skip.ToString();
        Back15.ToolTip = L.F("Back {0} s", skip);
        Fwd15.ToolTip = L.F("Forward {0} s", skip);
        TimeText.Visibility = V(ButtonOn("time"));
        DurationText.Visibility = V(ButtonOn("duration"));
        FrameBtn.Visibility = V(ButtonOn("frame"));
        SpeedBtn.Visibility = V(ButtonOn("speed"));
        SnapBtn.Visibility = V(ButtonOn("snapshot"));
        RecordBtn.Visibility = V(ButtonOn("record"));
        LoopBtn.Visibility = V(ButtonOn("loop"));
        AbBtn.Visibility = V(ButtonOn("abloop") && _index >= 0);
        LyricsBtn.Visibility = V(ButtonOn("lyrics") && _index >= 0 && _isAudio);
        ShuffleBtn.Visibility = V(ButtonOn("shuffle"));
        MuteBtn.Visibility = Volume.Visibility = V(ButtonOn("volume"));
        AudioBtn.Visibility = V(ButtonOn("audio"));
        SubsBtn.Visibility = V(ButtonOn("subs"));
        PSettingsBtn.Visibility = V(ButtonOn("psettings"));
        FullBtn.Visibility = V(ButtonOn("fullscreen"));
        ListBtn.Visibility = V(ButtonOn("queue"));
        MiniBtn.Visibility = V(ButtonOn("mini"));
        LoopBtn.IsChecked = Settings.Str("playback.repeat") != "Off";
        LoopBtn.Tag = Settings.Str("playback.repeat") == "One" ? "repeat-once" : "repeat";
        ShuffleBtn.IsChecked = Settings.Bool("playback.shuffle");
    }

    public void ShowCustomize()
    {
        CustomizeList.Children.Clear();
        foreach (var (id, label, def) in ControlButtons)
        {
            var row = OptView.Row(Settings.Find("ui.btn." + id)!, 0);
            row.Margin = new Thickness(0, 0, 0, 4);
            CustomizeList.Children.Add(row);
        }
        CustomizePopup.IsOpen = true;
        Motion.Pop(CustomizePopup.Child, dy: 10, scale: 0.95, origin: new Point(0, 1));
    }

    void CustomizeDone_Click(object sender, RoutedEventArgs e) => CustomizePopup.IsOpen = false;

    // ───────── casting (Chromecast via libVLC renderers) ─────────

    void StartCastDiscovery()
    {
        _renderers?.Stop();
        _renderers?.Dispose();
        _renderers = null;
        _castDevices.Clear();
        if (!Settings.Bool("rec.cast") || SnapMode) return;
        var desc = Vlc.RendererList.FirstOrDefault();
        if (desc.Name == null) return;
        _renderers = new RendererDiscoverer(Vlc, desc.Name);
        _renderers.ItemAdded += (_, e) => Dispatcher.BeginInvoke(() => _castDevices.Add(e.RendererItem));
        _renderers.ItemDeleted += (_, e) => Dispatcher.BeginInvoke(() => _castDevices.RemoveAll(d => d.Name == e.RendererItem.Name));
        _renderers.Start();
    }

    void CastTo(RendererItem? device)
    {
        _castingTo = device;
        _mp.SetRenderer(device);
        if (_index >= 0) PlayAt(_index, startAt: _mp.Time, autoplay: true);
        Osd(device == null ? L.T("Stopped casting") : L.F("Casting to {0}", device.Name), 3);
    }

    // ───────── open media ─────────

    public void OpenFiles()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = MediaFilter };
        if (dlg.ShowDialog(this) == true) Open(dlg.FileNames, append: Settings.Bool("library.enqueue"));
    }

    public void OpenFolder()
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true) Open([dlg.FolderName], append: Settings.Bool("library.enqueue"));
    }

    public void OpenStream()
    {
        if (DialogWindow.NetworkStream(this) is not { } r) return;
        if (r.Playlist != null)
        {
            Library.Data.Playlists.Add(new Playlist { Name = r.Playlist, Items = r.Urls });
            Library.Touch();
        }
        PlayList(r.Urls, 0);
    }

    public void OpenDisc() => OpenDisc("dvd", Settings.Str("playback.drive") is { Length: > 0 } d ? d : Settings.Drives().FirstOrDefault());

    void OpenDisc(string scheme, string? drive)
    {
        if (drive == null) { Osd(L.T("No disc drive found"), 2); return; }
        PlayList([$"{scheme}:///{drive}/"], 0);
    }


    // ───────────────────────── input ─────────────────────────

    void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (Keyboard.FocusedElement is TextBox && !ctrl && e.Key != Key.Escape) return; // typing in search etc.
        switch (e.Key)
        {
            case Key.MediaPlayPause: TogglePlay(); e.Handled = true; return;
            case Key.MediaNextTrack: Next(); e.Handled = true; return;
            case Key.MediaPreviousTrack: Previous(); e.Handled = true; return;
            case Key.MediaStop: Stop(); e.Handled = true; return;
        }
        var gesture = Hotkeys.FromEvent(e);
        if (gesture is "Alt+Return" or "F11") { ToggleFullscreen(); e.Handled = true; return; }
        if (Hotkeys.Match(gesture) is not { } action) return;
        action.Run(this);
        e.Handled = true;
    }

    void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Activate(); // the overlay is a separate window; keep keyboard focus on the player
        if (_is360 && e.ClickCount == 1) BeginLook(e.GetPosition(Overlay));
        if (e.ClickCount == 2)
        {
            _clickTimer.Stop();
            if (_mini) ToggleMiniplayer();
            else if (Settings.Str("controls.dblclick") == "Toggle fullscreen") ToggleFullscreen();
        }
        else if ((_minimal || _mini) && !_fullscreen)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
        else if (Settings.Str("controls.click") == "Play / Pause")
        {
            _clickTimer.Stop();
            _clickTimer.Start(); // single click pauses, unless it turns into a double click
        }
    }

    Point _lastMouse;

    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_looking) { Look(e.GetPosition(Overlay)); return; }
        if (!_fullscreen) return;
        // sensitivity: ignore jitter of a few pixels on Low
        var p = e.GetPosition(Overlay);
        var threshold = Settings.Str("ui.sensitivity") switch { "Low" => 12, "High" => 0, _ => 4 };
        if (Math.Abs(p.X - _lastMouse.X) + Math.Abs(p.Y - _lastMouse.Y) < threshold) return;
        _lastMouse = p;
        ShowControls();
    }

    void Overlay_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_is360) { _view.Zoom(-e.Delta / 120.0 * 6); return; } // wheel = field of view in 360° video
        Wheel(e.Delta, horizontal: false);
    }

    void Overlay_MouseUp(object sender, MouseButtonEventArgs e) => EndLook();

    // ───────── 360° video: drag to look around, the view keeps gliding after a flick ─────────

    bool _is360, _looking;
    readonly ViewAngles _view = new();
    Point _lookLast;
    double _lookVx, _lookVy;
    long _lookStamp;

    void Detect360()
    {
        var was = _is360;
        _is360 = _mp.Media?.Tracks.Any(t => t.TrackType == TrackType.Video && t.Data.Video.Projection != VideoProjection.Rectangular) == true;
        if (!_is360 || was) return;
        _view.Attach(_mp);
        Osd(L.T("360° video: drag to look around, scroll to zoom"), 4);
    }

    void BeginLook(Point p)
    {
        _looking = true;
        _lookLast = p;
        _lookVx = _lookVy = 0;
        _lookStamp = Stopwatch.GetTimestamp();
        _view.Hold();
        Overlay.CaptureMouse();
    }

    void Look(Point p)
    {
        var dx = p.X - _lookLast.X;
        var dy = p.Y - _lookLast.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 3) _clickTimer.Stop(); // a drag, not a click: don't pause
        var degPerPx = _view.Fov / Math.Max(1, Overlay.ActualWidth); // the picture stays under the pointer
        _view.Move(-dx * degPerPx, -dy * degPerPx);
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Max(1e-3, (now - _lookStamp) / (double)Stopwatch.Frequency);
        _lookVx = 0.6 * _lookVx + 0.4 * (-dx * degPerPx / dt); // smoothed velocity in degrees/s
        _lookVy = 0.6 * _lookVy + 0.4 * (-dy * degPerPx / dt);
        _lookStamp = now;
        _lookLast = p;
    }

    void EndLook()
    {
        if (!_looking) return;
        _looking = false;
        Overlay.ReleaseMouseCapture();
        if ((Stopwatch.GetTimestamp() - _lookStamp) / (double)Stopwatch.Frequency > 0.08) return; // held still before letting go
        _view.Fling(Math.Clamp(_lookVx, -720, 720), Math.Clamp(_lookVy, -720, 720)); // a wild flick spins a little, not for whole turns
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!e.Handled && Stage.IsMouseOver) Wheel(e.Delta, horizontal: false);
        base.OnMouseWheel(e);
    }

    void Wheel(int delta, bool horizontal)
    {
        var sign = Math.Sign(delta) * (Settings.Bool("controls.reverse") ? -1 : 1);
        switch (Settings.Str(horizontal ? "controls.hwheel" : "controls.wheel"))
        {
            case "Volume": NudgeVolume(sign); break;
            case "Seek": Skip(0, sign); break;
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x020E /*WM_MOUSEHWHEEL*/ && Stage.IsMouseOver)
        {
            Wheel((short)((wParam.ToInt64() >> 16) & 0xFFFF), horizontal: true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ───────────────────────── buttons ─────────────────────────

    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void Prev_Click(object sender, RoutedEventArgs e) => Previous();
    void Next_Click(object sender, RoutedEventArgs e) => Next();
    void Stop_Click(object sender, RoutedEventArgs e) => Stop();
    void Back15_Click(object sender, RoutedEventArgs e) => Jump(-Settings.Int("controls.skipbtn") * 1000);
    void Fwd15_Click(object sender, RoutedEventArgs e) => Jump(Settings.Int("controls.skipbtn") * 1000);
    void Frame_Click(object sender, RoutedEventArgs e) => NextFrame();
    void Snap_Click(object sender, RoutedEventArgs e) => Snapshot();
    void Record_Click(object sender, RoutedEventArgs e) => ToggleRecord();
    void Loop_Click(object sender, RoutedEventArgs e) => Settings.Set("playback.repeat", Settings.Str("playback.repeat") switch { "Off" => "All", "All" => "One", _ => "Off" });
    void Shuffle_Click(object sender, RoutedEventArgs e) => Settings.Set("playback.shuffle", !Settings.Bool("playback.shuffle"));
    void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
    void Mini_Click(object sender, RoutedEventArgs e) => ToggleMiniplayer();
    void MiniQueue_Click(object sender, RoutedEventArgs e) { ToggleMiniplayer(); ToggleQueue(); }
    void Duration_Click(object sender, RoutedEventArgs e) { _showRemaining = !_showRemaining; Refresh(); }
    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Full_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void SidebarBtn_Click(object sender, RoutedEventArgs e) => ToggleSidebar();
    void ListBtn_Click(object sender, RoutedEventArgs e) => ToggleQueue();

    void FolderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPath is not { } p || Path.GetDirectoryName(p) is not { } dir) return;
        if (Sidebar.Visibility != Visibility.Visible) ToggleSidebar();
        Sidebar.ShowFolder(dir);
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (_resumeAt > 0) _mp.Time = _resumeAt;
        ResumeBanner.Visibility = Visibility.Collapsed;
    }

    void ResumeClose_Click(object sender, RoutedEventArgs e) => HideResume();
    void HideResume() => Motion.Out(ResumeBanner, () => ResumeBanner.Visibility = Visibility.Collapsed, dy: 24, scale: 0.96);

    const double PreviewOverhang = 150; // the card may stick out past either end of the bar

    void Seek_MouseMove(object sender, MouseEventArgs e) => ShowSeekPreview(e.GetPosition(Seek).X);

    void ShowSeekPreview(double x)
    {
        if (_index < 0 || _mp.Length <= 0) return;
        var pos = Math.Clamp((x - 2) / Math.Max(1, Seek.ActualWidth - 4), 0, 1); // same mapping as a click
        var at = (long)(pos * _mp.Length);
        PreviewTime.Text = _chapters.LastOrDefault(c => c.Time <= at) is { Name: { Length: > 0 } chapter } ? $"{Fmt(at)}  ·  {chapter}" : Fmt(at);
        PreviewFrame.Visibility = _preview.HasMedia ? Visibility.Visible : Visibility.Collapsed;
        if (_preview.HasMedia) _preview.Request(pos);
        if (!SeekPreview.IsOpen)
        {
            PreviewLane.Width = Seek.ActualWidth + 2 * PreviewOverhang;
            SeekPreview.HorizontalOffset = -PreviewOverhang;
            SeekPreview.IsOpen = true;
            PreviewSlider.UpdateLayout();
            Motion.Pop(PreviewCard, dy: 10, scale: 0.9, origin: new Point(0.5, 1));
        }
        // 1:1 with the pointer (direct manipulation); the screen round-trip copes with the popup being nudged at screen edges
        var laneX = PresentationSource.FromVisual(PreviewLane) != null ? PreviewLane.PointFromScreen(Seek.PointToScreen(new Point(x, 0))).X : x + PreviewOverhang;
        var w = PreviewSlider.ActualWidth;
        PreviewX.X = Math.Clamp(laneX - w / 2, 0, Math.Max(0, PreviewLane.Width - w));
    }

    void Seek_MouseLeave(object sender, MouseEventArgs e) => SeekPreview.IsOpen = false;

    // ───────── menus (texts from the mockup's "menu" and "right-click menu") ─────────

    MenuItem Item(string header, string? glyph, string? gesture, Action onClick, bool check = false, bool enabled = true, string? tip = null)
    {
        var mi = new MenuItem { Header = L.T(header), InputGestureText = gesture ?? "", IsChecked = check, IsEnabled = enabled, ToolTip = tip == null ? null : L.T(tip) };
        if (glyph != null) mi.Icon = new Icon { Glyph = glyph };
        if (tip != null) ToolTipService.SetShowOnDisabled(mi, true);
        mi.Click += (_, e) => { if (e.OriginalSource == mi) onClick(); };
        return mi;
    }

    MenuItem Sub(string header, string? glyph, IEnumerable<object> items)
    {
        var mi = Item(header, glyph, null, () => { });
        foreach (var i in items) mi.Items.Add(i);
        if (mi.Items.Count == 0) mi.Items.Add(Item("Nothing here", null, null, () => { }, enabled: false));
        return mi;
    }

    static string KeyOf(string id) => Hotkeys.Display(Hotkeys.Gesture(id));

    void MenuBtn_Click(object sender, RoutedEventArgs e) => ShowMenu(MenuBtn, MainMenu());

    object[] MainMenu() =>
    [
        Item("Open File(s)", "file-duotone", KeyOf("open"), OpenFiles),
        Item("Open Folder", "folder-duotone", KeyOf("openfolder"), OpenFolder),
        Item("Open Network Stream", "broadcast-duotone", KeyOf("openstream"), OpenStream),
        DiscMenu(),
        Item("Convert", "arrows-left-right-duotone", KeyOf("convert"), OpenConverter),
        Sub("Open Recent", "clock-counter-clockwise-duotone",
            Library.Data.History.OrderByDescending(h => h.Value.LastPlayed).Take(12).Select(h => (object)Item(Library.TitleOf(h.Key), null, null, () => PlayList([h.Key], 0)))),
        new Separator(),
        Sub($"{L.T("Profile")} ({L.T(Settings.Str("general.profile") is { Length: > 0 } pr ? pr : "Default")})", "user-duotone",
            Profiles.Names.Select(n => (object)Item(n, null, null, () => Profiles.Apply(n), check: (Settings.Str("general.profile") is { Length: > 0 } cur ? cur : "Default") == n))),
        CastMenu(),
        Item(_cast != null ? "Streaming to TV / Phone…" : "Stream to TV / Phone…", "broadcast-duotone", null, StreamToNetwork, check: _cast != null, enabled: _index >= 0 || _cast != null),
        SleepMenu("moon-duotone"),
        Item("Effects & Filters", "sliders-horizontal-duotone", KeyOf("effects"), ToggleEffects),
        Item("Keyboard Shortcuts", "keyboard-duotone", null, () => DialogWindow.EditHotkeys(this)),
        Item("Customize Player", "paint-brush-duotone", null, ShowCustomize),
        Item("Settings", "gear-six-duotone", KeyOf("settings"), OpenSettings),
        Item("Check for Updates…", "arrow-circle-up-duotone", null, () => CheckForUpdates(manual: true)),
        Item("About Pylon", "info-duotone", KeyOf("about"), ShowAbout),
    ];

    // ───────── Windows media session (volume flyout, lock screen, media keys) ─────────

    MediaSession? _session;

    void StartMediaSession()
    {
        if (SnapMode) return;
        try { _session = new MediaSession(new WindowInteropHelper(this).Handle); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or TypeLoadException or PlatformNotSupportedException) { return; }
        _session.Command += c => Dispatcher.BeginInvoke(() =>
        {
            switch (c)
            {
                case "Play": if (!_mp.IsPlaying) TogglePlay(); break;
                case "Pause": if (_mp.IsPlaying) TogglePlay(); break;
                case "Next": Next(); break;
                case "Previous": Previous(); break;
                case "Stop": Stop(); break;
            }
        });
        _session.Seek += p => Dispatcher.BeginInvoke(() => { if (_mp.IsSeekable) _mp.Time = (long)p.TotalMilliseconds; });
    }

    // ───────── taskbar thumbnail toolbar (open / previous / play / next under the window preview) ─────────

    ThumbButtonInfo? _thumbPlay;
    bool? _thumbPlaying;

    static ImageSource? ThumbIcon(string glyph) => Pylon.Icon.Load(glyph, ThemeManager.SystemUsesLight() ? "#202020" : "#FFFFFF"); // the preview follows the Windows theme, not ours

    void SetupThumbButtons()
    {
        ThumbButtonInfo Button(string glyph, string tip, Action click)
        {
            var b = new ThumbButtonInfo { ImageSource = ThumbIcon(glyph), Description = L.T(tip) };
            b.Click += (_, _) => click();
            return b;
        }
        _thumbPlay = Button("play-fill", "Play", TogglePlay);
        TaskbarItemInfo = new TaskbarItemInfo
        {
            ThumbButtonInfos = { Button("plus", "Open media", OpenFiles), Button("skip-back-fill", "Previous", Previous), _thumbPlay, Button("skip-forward-fill", "Next", Next) },
        };
    }

    void UpdateThumbPlay()
    {
        if (_thumbPlay == null || _thumbPlaying == _mp.IsPlaying) return;
        _thumbPlaying = _mp.IsPlaying;
        _thumbPlay.ImageSource = ThumbIcon(_mp.IsPlaying ? "pause-fill" : "play-fill");
        _thumbPlay.Description = L.T(_mp.IsPlaying ? "Pause" : "Play");
    }

    // ───────── chapters & bookmarks ─────────

    (long Time, string Name)[] _chapters = [];

    void LoadChapters()
    {
        _chapters = [.. _mp.FullChapterDescriptions(-1).Select((c, i) => (c.TimeOffset, string.IsNullOrWhiteSpace(c.Name) ? L.F("Chapter {0}", i + 1) : c.Name!))];
        UpdateMarks();
    }

    List<Bookmark> CurrentBookmarks => CurrentPath is { } p && Library.Data.Bookmarks.TryGetValue(p, out var list) ? list : [];

    void UpdateMarks()
    {
        var len = _mp.Length;
        double[] At(IEnumerable<long> times) => len > 0 ? [.. times.Where(t => t > 0 && t < len).Select(t => (double)t / len)] : [];
        var chapters = At(_chapters.Select(c => c.Time));
        var bookmarks = At(CurrentBookmarks.Select(b => b.Time));
        Seek.SetMarks(chapters, bookmarks);
        MiniSeek.SetMarks(chapters, bookmarks);
    }

    public void JumpChapter(int dir)
    {
        if (_chapters.Length < 2) return;
        var now = _mp.Time;
        // "previous" restarts the current chapter first, like Previous does for tracks
        var target = dir > 0 ? _chapters.FirstOrDefault(c => c.Time > now + 500)
                             : _chapters.LastOrDefault(c => c.Time < now - 3000);
        if (target.Name == null) return;
        _mp.Time = target.Time;
        Osd(target.Name);
    }

    public void AddBookmark()
    {
        if (CurrentPath is not { } path || !_mp.IsSeekable) return;
        if (!Library.Data.Bookmarks.TryGetValue(path, out var list)) Library.Data.Bookmarks[path] = list = [];
        var t = _mp.Time;
        list.Add(new Bookmark { Time = t, Name = L.F("Bookmark {0}", Fmt(t)) });
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        Library.Touch();
        UpdateMarks();
        Osd(L.F("Bookmark added at {0}", Fmt(t)));
    }

    MenuItem ChaptersMenu()
    {
        var now = _mp.Time;
        var current = _chapters.LastOrDefault(c => c.Time <= now);
        var item = Sub("Chapters", null, _chapters.Select(c => (object)Item($"{Fmt(c.Time)}   {c.Name}", null, null, () => { _mp.Time = c.Time; Osd(c.Name); }, check: c == current)));
        item.IsEnabled = _chapters.Length > 0;
        return item;
    }

    MenuItem BookmarksMenu()
    {
        var items = new List<object> { Item("Add Bookmark", null, KeyOf("bookmark"), AddBookmark, enabled: _index >= 0) };
        var list = CurrentBookmarks;
        if (list.Count > 0) items.Add(new Separator());
        foreach (var b in list.ToList())
            items.Add(Sub($"{Fmt(b.Time)}   {b.Name}", null,
            [
                Item("Go to Bookmark", null, null, () => _mp.Time = b.Time),
                Item("Rename", null, null, () =>
                {
                    if (DialogWindow.AskName(this, "Rename Bookmark", "Name", "", "Bookmark name", b.Name) is not { } n) return;
                    b.Name = n;
                    Library.Touch();
                }),
                Item("Delete", null, null, () => { list.Remove(b); Library.Touch(); UpdateMarks(); }),
            ]));
        return Sub("Bookmarks", null, items);
    }

    // ───────── sleep timer ─────────

    DateTime? _sleepAt;
    bool _sleepEndOfItem;
    DispatcherTimer? _sleepTimer;

    MenuItem SleepMenu(string? glyph)
    {
        var header = _sleepAt is { } at ? L.F("Sleep Timer ({0} min left)", Math.Max(1, (int)Math.Ceiling((at - DateTime.Now).TotalMinutes)))
                   : _sleepEndOfItem ? L.T("Sleep Timer (end of item)") : L.T("Sleep Timer");
        return Sub(header, glyph,
        [
            Item("Off", null, null, () => SetSleep(0), check: _sleepAt == null && !_sleepEndOfItem),
            .. new[] { 15, 30, 45, 60, 90 }.Select(m => (object)Item(L.F("{0} min", m), null, null, () => SetSleep(m))),
            Item("End of current item", null, null, () => SetSleep(-1), check: _sleepEndOfItem, enabled: _index >= 0),
        ]);
    }

    /// <summary>Minutes until playback pauses (the last 10 s fade out); -1 = when the current item ends; 0 = off.</summary>
    void SetSleep(int minutes)
    {
        _sleepEndOfItem = minutes == -1;
        _sleepAt = minutes > 0 ? DateTime.Now.AddMinutes(minutes) : null;
        _sleepTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SleepTick(), Dispatcher);
        _sleepTimer.Interval = TimeSpan.FromSeconds(1);
        if (_sleepAt != null) _sleepTimer.Start(); else _sleepTimer.Stop();
        ApplyVolume(); // undo a fade that was in progress
        Osd(minutes switch { 0 => L.T("Sleep timer off"), -1 => L.T("Stops at the end of this item"), _ => L.F("Sleep timer: {0} min", minutes) });
    }

    void SleepTick()
    {
        if (_sleepAt is not { } at) return;
        var left = at - DateTime.Now;
        if (left > TimeSpan.FromSeconds(10)) { _sleepTimer!.Interval = TimeSpan.FromSeconds(1); return; }
        _sleepTimer!.Interval = TimeSpan.FromMilliseconds(100);
        if (left > TimeSpan.Zero)
        {
            _mp.Volume = (int)Math.Round(Volume.Value * MaxVolume * left.TotalSeconds / 10); // fade out
            return;
        }
        _sleepTimer.Stop();
        _sleepAt = null;
        if (_mp.IsPlaying) _mp.Pause();
        ApplyVolume();
        Osd(L.T("Sleep timer: paused"));
    }

    // ───────── A-B loop ─────────

    long _loopA = -1, _loopB = -1;
    DispatcherTimer? _loopTimer;

    /// <summary>First press marks A, second marks B and starts looping, third clears it.</summary>
    public void ToggleAbLoop()
    {
        if (_index < 0 || !_mp.IsSeekable) return;
        var t = _mp.Time;
        if (_loopA < 0) { _loopA = t; Osd(L.F("Loop start (A): {0}", Fmt(t))); }
        else if (_loopB < 0 && t > _loopA + 300) { _loopB = t; _mp.Time = _loopA; Osd(L.F("A-B loop: {0} – {1}", Fmt(_loopA), Fmt(_loopB))); }
        else { ClearLoop(); Osd(L.T("A-B loop off")); return; }
        ShowLoop();
    }

    void ClearLoop()
    {
        _loopA = _loopB = -1;
        ShowLoop();
    }

    void ShowLoop()
    {
        var len = _mp.Length;
        double At(long ms) => ms < 0 || len <= 0 ? double.NaN : (double)ms / len;
        Seek.SetLoop(At(_loopA), At(_loopB));
        MiniSeek.SetLoop(At(_loopA), At(_loopB));
        AbBtn.IsChecked = _loopA >= 0;
        _loopTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Render, (_, _) =>
        {
            if (_loopB >= 0 && _mp.Time >= _loopB) _mp.Time = _loopA;
        }, Dispatcher);
        if (_loopB >= 0) _loopTimer.Start(); else _loopTimer.Stop();
    }

    void Ab_Click(object sender, RoutedEventArgs e) => ToggleAbLoop();

    CastServer? _cast;

    /// <summary>Starts streaming the current item to the local network (or shows the running stream) with a QR code to join.</summary>
    public void StreamToNetwork()
    {
        if (_cast == null)
        {
            if (_index < 0) return;
            _cast = new CastServer { Title = _title };
            PlayAt(_index, startAt: _mp.Time, autoplay: true); // reopen with the stream branch
        }
        if (!DialogWindow.Cast(this, _cast)) return;
        StopStreaming();
    }

    void StopStreaming()
    {
        if (_cast == null) return;
        _cast.Dispose();
        _cast = null;
        if (_index >= 0) PlayAt(_index, startAt: _mp.Time, autoplay: _mp.IsPlaying);
        Osd(L.T("Stopped streaming"));
    }

    public void OpenConverter() => DialogWindow.Convert(this, CurrentPath is { } p && !p.Contains("://") ? [p] : []);

    /// <summary>The A-B range of the current file as a file of its own (or a GIF).</summary>
    void SaveClip()
    {
        if (CurrentPath is { } p && _loopB > _loopA && _loopA >= 0) DialogWindow.Convert(this, [p], _loopA, _loopB);
    }

    async void LoadWaveform(string src)
    {
        var peaks = await Waveform.GetAsync(src);
        if (peaks == null || CurrentPath != src) return;
        Seek.SetPeaks(peaks);
        MiniSeek.SetPeaks(peaks);
    }

    MenuItem DiscMenu()
    {
        var drives = Settings.Drives();
        return Sub("Open Disc", "disc-duotone", drives.SelectMany(d => new object[]
        {
            Item($"DVD ({d})", null, null, () => OpenDisc("dvd", d)),
            Item($"Blu-ray ({d})", null, null, () => OpenDisc("bluray", d)),
            Item($"Audio CD ({d})", null, null, () => OpenDisc("cdda", d)),
        }));
    }

    MenuItem CastMenu()
    {
        var items = _castDevices.Select(d => (object)Item(d.Name, "television", null, () => CastTo(d), check: _castingTo == d)).ToList();
        if (_castingTo != null) items.Add(Item("Stop Casting", null, null, () => CastTo(null)));
        if (items.Count == 0) items.Add(Item(Settings.Bool("rec.cast") ? "Searching for devices…" : "Device discovery is off", null, null, () => { }, enabled: false));
        return Sub("Chromecast", "screencast-duotone", items); // libVLC renderers; "Stream to TV / Phone" is the LAN stream
    }

    void Stage_RightClick(object sender, MouseButtonEventArgs e)
    {
        Activate();
        var speeds = new[] { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f };
        var items = new object[]
        {
            Item(_mp.IsPlaying ? "Pause" : "Play", _mp.IsPlaying ? "pause" : "play", KeyOf("play"), TogglePlay),
            Item("Stop", "stop", KeyOf("stop"), Stop, enabled: _index >= 0),
            Item("Previous Item", "skip-back", KeyOf("prev"), Previous, enabled: _index > 0),
            Item("Next Item", "skip-forward", KeyOf("next"), Next, enabled: _index >= 0 && _index < _queue.Count - 1),
            Item(_recording ? "Stop Recording" : "Record", "record", KeyOf("record"), ToggleRecord, check: _recording, enabled: _index >= 0),
            Item(_minimal ? "Show UI" : "Hide UI", "eye-slash", KeyOf("hideui"), ToggleMinimal, check: _minimal),
            new Separator(),
            Sub("Playback", "play-circle", [
                .. speeds.Select(s => (object)Item(s == 1f ? "Normal speed" : $"{s:0.##}×", null, null, () => SetRate(s), check: Math.Abs(_mp.Rate - s) < 0.01)),
                new Separator(),
                Item("Repeat One", null, null, () => Settings.Set("playback.repeat", Settings.Str("playback.repeat") == "One" ? "Off" : "One"), check: Settings.Str("playback.repeat") == "One"),
                Item("Repeat All", null, null, () => Settings.Set("playback.repeat", Settings.Str("playback.repeat") == "All" ? "Off" : "All"), check: Settings.Str("playback.repeat") == "All"),
                Item("Shuffle", null, null, () => Settings.Set("playback.shuffle", !Settings.Bool("playback.shuffle")), check: Settings.Bool("playback.shuffle")),
                Item("Next Frame", null, KeyOf("frame"), NextFrame),
                Item("A-B Loop", null, KeyOf("abloop"), ToggleAbLoop, check: _loopA >= 0, enabled: _index >= 0),
                Item("Save A-B Clip…", null, null, SaveClip, enabled: _loopB > _loopA && _loopA >= 0 && CurrentPath is { } cp && !cp.Contains("://")),
                ChaptersMenu(),
                BookmarksMenu(),
                SleepMenu(null),
            ]),
            Sub("Video", "monitor", [
                Item("Fullscreen", null, KeyOf("fullscreen"), ToggleFullscreen, check: _fullscreen),
                Item("Take Snapshot", null, KeyOf("snapshot"), Snapshot, enabled: _index >= 0 && !_isAudio),
                Sub("Aspect Ratio", null, Settings.Find("video.aspect")!.Options.Select(o => (object)Item(o, null, null, () => Settings.Set("video.aspect", o), check: Settings.Str("video.aspect") == o))),
                Sub("Crop", null, Settings.Find("video.crop")!.Options.Select(o => (object)Item(o, null, null, () => Settings.Set("video.crop", o), check: Settings.Str("video.crop") == o))),
                Item("Reset 360° View", null, null, () => _view.Reset(), enabled: _is360),
                Sub("Deinterlace", null, new[] { "Automatic", "On", "Off" }.Select(o => (object)Item(o, null, null, () => Settings.Set("video.deint", o), check: Settings.Str("video.deint") == o))),
            ]),
            Sub("Audio", "speaker-high", AudioMenuItems()),
            Sub("Subtitle", "subtitles", SubtitleMenuItems()),
            Item("More Settings", "sliders-horizontal", KeyOf("effects"), ToggleEffects),
            Item("Playlist", "queue", KeyOf("queue"), ToggleQueue),
            Sub("Open Media", "folder-open", [Item("Open File(s)", null, KeyOf("open"), OpenFiles), Item("Open Folder", null, KeyOf("openfolder"), OpenFolder), Item("Open Network Stream", null, KeyOf("openstream"), OpenStream)]),
            new Separator(),
            Item("Quit Pylon", "sign-out", KeyOf("quit"), Close),
        };
        var cm = new ContextMenu { Placement = PlacementMode.MousePoint };
        foreach (var i in items) cm.Items.Add(i);
        cm.IsOpen = true;
        e.Handled = true;
    }

    List<object> AudioMenuItems()
    {
        var items = new List<object>();
        foreach (var t in _mp.AudioTrackDescription)
        {
            var id = t.Id;
            items.Add(Item(t.Name ?? L.F("Track {0}", id), null, null, () => _mp.SetAudioTrack(id), check: _mp.AudioTrack == id));
        }
        if (items.Count == 0) items.Add(Item("No audio tracks", null, null, () => { }, enabled: false));
        items.Add(new Separator());
        items.Add(Sub("Output Device", null, (Settings.AudioDevices?.Invoke() ?? []).Select(d => (object)Item(d, null, null, () => Settings.Set("audio.device", d), check: Settings.Str("audio.device") == d))));
        items.Add(Item("Mute", null, KeyOf("mute"), ToggleMute, check: _mp.Mute));
        items.Add(Item("Audio Effects…", null, null, () => ShowEffects(video: false)));
        return items;
    }

    List<object> SubtitleMenuItems()
    {
        var items = new List<object>();
        foreach (var t in _mp.SpuDescription)
        {
            var id = t.Id;
            items.Add(Item(t.Name ?? L.F("Track {0}", id), null, null, () => _mp.SetSpu(id), check: _mp.Spu == id));
        }
        if (items.Count > 0) items.Add(new Separator());
        items.Add(Item("Add Subtitles…", "file-duotone", null, AddSubtitles, enabled: _index >= 0 && CurrentPath?.Contains("://") == false));
        items.Add(Item("Subtitle Settings…", null, null, () => OpenSettings("Subtitles")));
        return items;
    }

    void AddSubtitles()
    {
        if (CurrentPath is not { } video) return;
        if (DialogWindow.AddSubtitles(this, video) is not { } map) return;
        foreach (var (v, subs) in map) Library.Data.Subtitles[v] = subs;
        Library.Touch();
        foreach (var sub in map.GetValueOrDefault(video) ?? []) _mp.AddSlave(MediaSlaveType.Subtitle, new Uri(sub).AbsoluteUri, true);
    }

    void Audio_Click(object sender, RoutedEventArgs e) => ShowMenu(AudioBtn, AudioMenuItems().ToArray());
    void Subs_Click(object sender, RoutedEventArgs e) => ShowMenu(SubsBtn, SubtitleMenuItems().ToArray());

    void PSettings_Click(object sender, RoutedEventArgs e)
    {
        var speeds = new[] { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f };
        ShowMenu(PSettingsBtn, [
            .. speeds.Select(s => (object)Item(s == 1f ? "Normal speed" : $"{s:0.##}×", null, null, () => SetRate(s), check: Math.Abs(_mp.Rate - s) < 0.01)),
            new Separator(),
            Item("Effects & Filters", "sliders-horizontal-duotone", KeyOf("effects"), ToggleEffects),
            Item("Playback Settings…", "gear-six-duotone", null, () => OpenSettings("Playback")),
        ]);
    }

    /// <summary>The rail's "+" button.</summary>
    public void ShowOpenMenu(FrameworkElement anchor) => ShowMenu(anchor,
        Item("Open File(s)", "file-duotone", KeyOf("open"), OpenFiles),
        Item("Open Folder", "folder-duotone", KeyOf("openfolder"), OpenFolder),
        Item("Open Network Stream", "broadcast-duotone", KeyOf("openstream"), OpenStream));

    /// <summary>Opens a styled menu above the anchor (or below / beside it when the anchor sits in the top bar / rail).</summary>
    void ShowMenu(FrameworkElement anchor, params object[] items)
    {
        var below = !anchor.IsDescendantOf(ControlsBar);
        var cm = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = below ? (anchor.IsDescendantOf(Sidebar) ? PlacementMode.Right : PlacementMode.Bottom) : PlacementMode.Top,
            VerticalOffset = below ? 6 : -4,
        };
        foreach (var i in items) cm.Items.Add(i);
        cm.IsOpen = true;
    }

    void Osd(string text, double seconds = 0)
    {
        if (!Settings.Bool("osd.enable")) return;
        OsdText.Text = StageOsdText.Text = text;
        var osd = Video.IsVisible ? OsdBorder : StageOsd; // the overlay window only exists over video
        Motion.Set(osd == OsdBorder ? StageOsd : OsdBorder, OpacityProperty, 0);
        if (osd.Opacity < 0.05) Motion.Pop(osd, dy: -6, scale: 0.94, origin: new Point(1, 0)); // appears from its corner
        else Motion.Set(osd, OpacityProperty, 1);
        _osdTimer.Stop();
        _osdTimer.Interval = TimeSpan.FromSeconds(seconds > 0 ? seconds : Settings.Num("osd.time"));
        _osdTimer.Start();
    }

    int _flashGen;

    /// <summary>A round play / pause badge pops in the middle of the picture and dissolves while growing a little (YouTube / iOS).</summary>
    async void FlashPlayState(bool playing)
    {
        var (flash, icon) = Video.IsVisible ? (VideoFlash, VideoFlashIcon) : (StageFlash, StageFlashIcon);
        icon.Glyph = playing ? "play-fill" : "pause-fill";
        icon.Margin = new Thickness(playing ? 4 : 0, 0, 0, 0); // optical center of the triangle
        var gen = ++_flashGen;
        Motion.Pop(flash, scale: 0.72, spring: Spring.Snappy);
        await Task.Delay(420);
        if (gen != _flashGen || flash.RenderTransform is not TransformGroup { Children: [ScaleTransform s, ..] }) return;
        Motion.Animate(flash, OpacityProperty, 0, Spring.Smooth);
        Motion.Animate(s, ScaleTransform.ScaleXProperty, 1.25, Spring.Smooth);
        Motion.Animate(s, ScaleTransform.ScaleYProperty, 1.25, Spring.Smooth);
    }

    Window? _overlayWindow;

    /// <summary>
    /// LibVLCSharp shows its overlay window (OSD, fullscreen controls, corner mask) once the video host loads and only hides it
    /// on unload, so over music or the idle screen it lingered as an invisible, click-eating layer. Keep it in step with the video.
    /// </summary>
    void HookOverlayWindow()
    {
        _overlayWindow = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "LibVLCSharp.WPF");
        if (_overlayWindow == null) return;
        _overlayWindow.IsVisibleChanged += (_, _) => { if (_overlayWindow.IsVisible != Video.IsVisible) Dispatcher.BeginInvoke(SyncOverlayWindow); };
        Video.IsVisibleChanged += (_, _) => SyncOverlayWindow();
        SyncOverlayWindow();
    }

    void SyncOverlayWindow()
    {
        if (_overlayWindow == null || _overlayWindow.Owner == null) return; // not set up by LibVLCSharp yet
        if (Video.IsVisible && !_overlayWindow.IsVisible) _overlayWindow.Show();
        else if (!Video.IsVisible && _overlayWindow.IsVisible) _overlayWindow.Hide();
    }

    static void KeepAwake(bool on) => SetThreadExecutionState(on ? 0x80000000u | 0x00000002u /*CONTINUOUS|DISPLAY_REQUIRED*/ : 0x80000000u);

#if DEBUG
    /// <summary>
    /// Debug-only UI test hook: PYLON_SNAP=out.png renders the window to a PNG (no input injection, no focus needed) and exits.
    /// PYLON_SNAP_DO is a ';'-separated script: library:Videos | folder:&lt;dir&gt; | row:&lt;title&gt; | menu | ctx | search:&lt;text&gt; |
    /// set:key=value | settings:Category | fx:audio|video | mini | minimal | customize | hotkeys | onboarding | about | wait:&lt;sec&gt; | dialog:playlist|stream|subs
    /// </summary>
    async void DebugSnapshot()
    {
        if (Environment.GetEnvironmentVariable("PYLON_SNAP") is not { } outFile) return;
        await Task.Delay(3000);
        Window? shot = null;
        foreach (var step in (Environment.GetEnvironmentVariable("PYLON_SNAP_DO") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (cmd, arg) = step.IndexOf(':') is var i and > 0 ? (step[..i], step[(i + 1)..]) : (step, "");
            switch (cmd)
            {
                case "library": ToggleSidebar(Enum.Parse<LibraryPanel.Section>(arg)); break;
                case "folder": if (Sidebar.Visibility != Visibility.Visible) ToggleSidebar(); Sidebar.ShowFolder(arg); break;
                case "search": Sidebar.OpenSearch(); ((TextBox)Sidebar.FindName("SearchBox")).Text = arg; break;
                case "menu": MenuBtn_Click(MenuBtn, new RoutedEventArgs()); break;
                case "ctx": Stage_RightClick(Stage, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = MouseRightButtonUpEvent }); break;
                case "minimal": _minimal = true; ApplyLayout(); break;
                case "mini": ToggleMiniplayer(); break;
                case "row": Sidebar.DebugClick(arg); break;
                case "set": Settings.Set(arg[..arg.IndexOf('=')], arg[(arg.IndexOf('=') + 1)..]); break;
                case "fx": ShowEffects(arg == "video"); break;
                case "customize": ShowCustomize(); break;
                case "do": Hotkeys.Actions.First(x => x.Id == arg).Run(this); break;
                case "preview":
                    ShowSeekPreview(2 + double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) * (Seek.ActualWidth - 4));
                    await Task.Delay(1500);
                    break;
                case "state":
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), Environment.NewLine + $"osd={StageOsd.Opacity:0.00}/{OsdBorder.Opacity:0.00} hwnd={_mp.Hwnd} overlay={_overlayWindow?.IsVisible} video={Video.IsVisible} time={_mp.Time} playing={_mp.IsPlaying} vol={_mp.Volume} rate={_mp.Rate} index={_index} audio={_isAudio} snapdir={Settings.Str("video.snapdir")} rec={_recording} file={_recordFile}");
                    break;
                case "settings":
                    OpenSettings(arg.Length > 0 ? arg : null);
                    shot = _settings;
                    break;
                case "listsd":
                    File.WriteAllLines(Path.ChangeExtension(outFile, ".txt"), Enum.GetValues<MediaDiscovererCategory>()
                        .SelectMany(c => Vlc.MediaDiscoverers(c).Select(d => $"{c}: {d.Name} | {d.LongName}"))
                        .Concat(Vlc.RendererList.Select(r => $"Renderer: {r.Name} | {r.LongName}")));
                    break;
                case "look": // 360°: a flick at 130°/s should glide on for about 65° and settle without overshoot
                    _is360 = true;
                    _view.Attach(_mp);
                    _view.Fling(130, 0);
                    var samples = new List<string>();
                    for (var k = 0; k < 6; k++) { await Task.Delay(200); samples.Add($"{_view.Yaw:0.0}"); }
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"yaw {string.Join(" ", samples)}{Environment.NewLine}");
                    break;
                case "seekprobe": // thumb position every ~16 ms: must never go back, steps should be even
                    {
                        var vals = new List<double>();
                        for (var k = 0; k < 90; k++) { await Task.Delay(16); SmoothSeek(null, EventArgs.Empty); vals.Add(Seek.Value * _mp.Length); } // frames by hand: the off-screen test window gets few real ones
                        var steps = vals.Zip(vals.Skip(1), (x, y) => y - x).ToList();
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"),
                            $"backwards={steps.Count(d => d < -0.5)} zeroSteps={steps.Count(d => Math.Abs(d) < 0.5)} min={steps.Min():0.0}ms max={steps.Max():0.0}ms avg={steps.Average():0.0}ms{Environment.NewLine}");
                        break;
                    }
                case "spicytest":
                    try
                    {
                        var json = await SpicyLyrics.FetchAsync(CurrentPath!, _track.Title, _track.Artist ?? "", _track.Duration, _tags);
                        var parsed = json == null ? null : SpicyLyrics.Parse(json);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"tags={_tags.Replace('\n', '|')} json={(json == null ? "null" : json.Length.ToString())} lines={parsed?.Lines.Length} credits={string.Join(", ", parsed?.Credits?.Select(c => c.Text) ?? [])}{Environment.NewLine}");
                    }
                    catch (Exception ex) { File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), "spicy failed: " + ex + Environment.NewLine); }
                    break;
                case "switch":
                    {
                        var t0 = Stopwatch.StartNew();
                        Next();
                        var started = t0.ElapsedMilliseconds;
                        await Task.Delay(2500);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"Next() returned after {started} ms; {SwitchTiming}{Environment.NewLine}");
                        break;
                    }
                case "encodetest": // every preset on a 4 s clip of the playing file, into PYLON_ENC_OUT
                    foreach (var preset in Converter.Presets)
                    {
                        var dst = Path.Combine(Environment.GetEnvironmentVariable("PYLON_ENC_OUT")!, "out" + Array.IndexOf(Converter.Presets, preset) + (preset.Extension.Length > 0 ? preset.Extension : Path.GetExtension(CurrentPath!)));
                        var sw = Stopwatch.StartNew();
                        var ok = await Converter.RunAsync(CurrentPath!, dst, preset, 1000, 5000);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"{preset.Name}: ok={ok} {(File.Exists(dst) ? new FileInfo(dst).Length : -1)} bytes {sw.ElapsedMilliseconds} ms{Environment.NewLine}");
                    }
                    break;
                case "cast": // start streaming, then fetch the page and the first bytes of the stream like a phone would
                    {
                        _cast = new CastServer { Title = _title };
                        PlayAt(_index, startAt: _mp.Time, autoplay: true);
                        await Task.Delay(2500);
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        var page = await http.GetStringAsync(_cast.PageUrl);
                        using var res = await http.GetAsync(_cast.StreamUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                        var buf = new byte[188 * 20];
                        var n = await (await res.Content.ReadAsStreamAsync()).ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"),
                            $"page {page.Length} chars, vlc link {page.Contains("vlc://")}; stream {(int)res.StatusCode} {res.Content.Headers.ContentType}, {n} bytes, TS sync {buf[0] == 0x47 && buf[188] == 0x47}; still playing {_mp.IsPlaying}{Environment.NewLine}");
                        break;
                    }
                case "lyricsstate":
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), LyricsPane.DebugState() + Environment.NewLine);
                    break;
                case "wait": await Task.Delay(TimeSpan.FromSeconds(double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture))); break;
                case "hotkeys" or "onboarding" or "about" or "dialog":
                    _ = Dispatcher.BeginInvoke(async () =>
                    {
                        await Task.Delay(1500);
                        var dlg = Application.Current.Windows.OfType<Window>().First(w => w != this && w.IsVisible && w != _overlayWindow);
                        Snap(dlg, outFile);
                        Application.Current.Shutdown();
                    });
                    switch (cmd, arg)
                    {
                        case ("hotkeys", _): DialogWindow.EditHotkeys(this); break;
                        case ("onboarding", _): new OnboardingWindow(this, arg.Length > 0 ? int.Parse(arg) : 0).ShowDialog(); break;
                        case ("about", _): ShowAbout(); break;
                        case (_, "stream"): DialogWindow.NetworkStream(this); break;
                        case (_, "convert"): DialogWindow.Convert(this, [CurrentPath!]); break;
                        case (_, "cast"): DialogWindow.Cast(this, _cast ??= new CastServer { Title = _title }); break;
                        case (_, "clip"): DialogWindow.Convert(this, [CurrentPath!], 12_000, 27_500); break;
                        case (_, "subs"): DialogWindow.AddSubtitles(this, CurrentPath ?? "C:\\video.mp4"); break;
                        case (_, "update"):
                            DialogWindow.UpdateAvailable(this, new Updater.Release(new Version(1, 1, 0), "v1.1.0",
                                "## What's Changed\n* Russian interface\n* In-app updates from GitHub\n* Miniplayer fixes", "https://github.com/x", "https://github.com/x"), true);
                            break;
                        default: DialogWindow.NewPlaylist(this); break;
                    }
                    return;
            }
            await Task.Delay(400);
        }
        await Task.Delay(1500);
        Snap(shot ?? this, outFile);
        // menus and popups live in their own windows: save each one next to the main shot
        var popups = FindPopups();
        for (int k = 0; k < popups.Count; k++) Snap(popups[k], Path.ChangeExtension(outFile, $".popup{k}.png"));
        Application.Current.Shutdown();
    }

    static List<FrameworkElement> FindPopups() =>
        PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual).OfType<FrameworkElement>()
            .Where(v => v.GetType().Name == "PopupRoot" && v.ActualWidth > 0).ToList();

    static void Snap(FrameworkElement v, string file)
    {
        var dpi = VisualTreeHelper.GetDpi(v);
        var rtb = new RenderTargetBitmap((int)(v.ActualWidth * dpi.DpiScaleX), (int)(v.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
#endif

    // ───────────────────────── native ─────────────────────────

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
}
