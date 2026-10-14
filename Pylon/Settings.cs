using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Pylon;

public enum OptKind { Toggle, Choice, Segmented, Slider, Number, Text, Password, Folder, File, Color, Accent, Action }

/// <summary>One row on a settings page.</summary>
public class Opt
{
    public OptKind Kind;
    public string Key = "", Label = "", Default = "";
    public string? Info;
    public string[] Options = [];
    public Func<string[]>? DynamicOptions;          // audio devices, fonts, drives
    public double Min, Max = 100, Step = 1;
    public string Unit = "";                        // "%", " s", " ms"
    public string? ButtonText;
    public Action<Window>? Run;
    public bool Engine;                             // applied by restarting libVLC with new arguments
    public string? Unavailable;                     // shown disabled with this reason
    public Func<bool>? Visible;

    public string[] Choices => DynamicOptions?.Invoke() is { Length: > 0 } d ? d : Options;
}

public record Section(string Heading, List<Opt> Opts)
{
    public Func<bool>? Visible { get; init; }
}

public record Category(string Name, string Glyph, string Title, string Group, List<Section> Sections, List<Section> Advanced);

/// <summary>Persistent preferences (settings.json next to the library) and the settings schema from the mockup.</summary>
public static class Settings
{
    static readonly string FilePath = Path.Combine(Library.Dir, "settings.json");
    static Dictionary<string, string> _values = new();
    static readonly DispatcherTimer SaveTimer = new(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Save(), Dispatcher.CurrentDispatcher) { IsEnabled = false };

    /// <summary>Raised on the UI thread with the changed key (or "*" after a reset).</summary>
    public static event Action<string>? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) _values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (JsonException) { _values = new(); }
    }

    public static void Save()
    {
        SaveTimer.Stop();
        Directory.CreateDirectory(Library.Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Str(string key) => _values.TryGetValue(key, out var v) ? v : Find(key)?.Default ?? "";
    public static bool Bool(string key) => Str(key) == "true";
    public static double Num(string key) => double.TryParse(Str(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    public static int Int(string key) => (int)Math.Round(Num(key));

    public static void Set(string key, string value)
    {
        if (Str(key) == value) return;
        _values[key] = value;
        SaveTimer.Stop();
        SaveTimer.Start();
        Changed?.Invoke(key);
    }

    public static void Set(string key, bool value) => Set(key, value ? "true" : "false");
    public static void Set(string key, double value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public static void ResetAll()
    {
        _values.Clear();
        Save();
        Changed?.Invoke("*");
    }

    static Dictionary<string, Opt>? _index;
    static readonly List<Opt> Extra = [];

    /// <summary>Options that live outside the settings pages (e.g. control bar buttons), so defaults resolve.</summary>
    public static void Register(Opt o)
    {
        Extra.Add(o);
        _index = null;
    }

    public static Opt? Find(string key)
    {
        _index ??= Categories.SelectMany(c => c.Sections.Concat(c.Advanced)).SelectMany(s => s.Opts).Concat(Effects.AllOpts).Concat(Extra)
            .Where(o => o.Key != "").GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.First());
        return _index.GetValueOrDefault(key);
    }

    public static bool IsEngineKey(string key) => key == "*" || Find(key)?.Engine == true;

    // ───────────────────────── libVLC arguments ─────────────────────────

    static readonly Dictionary<string, string> Languages = new()
    {
        ["English"] = "en", ["Russian"] = "ru", ["Ukrainian"] = "uk", ["German"] = "de", ["French"] = "fr", ["Spanish"] = "es", ["Italian"] = "it",
        ["Portuguese"] = "pt", ["Polish"] = "pl", ["Turkish"] = "tr", ["Japanese"] = "ja", ["Chinese"] = "zh", ["Korean"] = "ko",
    };
    static readonly string[] LanguageNames = ["System language", .. Languages.Keys];

    static string Lang(string key) => Str(key) is var v && Languages.TryGetValue(v, out var code) ? code : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    static string ColorInt(string key)
    {
        var hex = Str(key).TrimStart('#');
        return int.TryParse(hex, NumberStyles.HexNumber, null, out var v) ? v.ToString(CultureInfo.InvariantCulture) : "0";
    }

    static string Inv(double d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary>Everything the settings need from libVLC at creation time.</summary>
    public static List<string> EngineArgs()
    {
        var a = new List<string> { "--no-video-title-show", "--no-osd", "--no-snapshot-preview" };
        void Add(string arg) => a.Add(arg);

        // General
        if (Str("general.cmdline") is { Length: > 0 } extra)
            a.AddRange(extra.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (Bool("library.netmeta")) Add("--metadata-network-access");

        // Playback
        if (Str("playback.quality") is var q && q != "Best available") Add($"--preferred-resolution={q.TrimEnd('p')}");
        Add($"--menu-language={Lang("playback.menulang")}");
        if (Int("playback.audiotrack") > 0) Add($"--audio-track={Int("playback.audiotrack") - 1}");
        if (Int("playback.subtrack") > 0) Add($"--sub-track={Int("playback.subtrack") - 1}");
        if (!Bool("playback.precise")) Add("--input-fast-seek");
        Add($"--avi-index={Array.IndexOf(["Ask", "Always", "Never"], Str("playback.avi")) switch { 1 => 1, 2 => 2, _ => 0 }}");
        if (Str("playback.tsfolder") is { Length: > 0 } ts) Add($"--input-timeshift-path={ts}");
        Add($"--input-timeshift-granularity={Str("playback.tssize") switch { "Small" => 10_000_000, "Large" => 200_000_000, _ => 50_000_000 }}");

        // Video
        if (!Bool("video.enable")) Add("--no-video");
        if (Effects.TransformArg() is { } transform) { Add("--video-filter=transform"); Add($"--transform-type={transform}"); }
        Add($"--snapshot-format={Str("video.snapformat").ToLowerInvariant()}");
        if (Str("video.par") is { Length: > 0 } par && par != "1:1") Add($"--monitor-par={par}");

        // Audio
        if (!Bool("audio.enable")) Add("--no-audio");
        var filters = new List<string>();
        if (Bool("audio.normalize")) { filters.Add("normvol"); Add($"--norm-max-level={Inv(Num("audio.normlevel"))}"); }
        if (Bool("audio.compressor") || Bool("audio.night"))
        {
            filters.Add("compressor");
            if (Bool("audio.night")) { Add("--compressor-ratio=8"); Add("--compressor-threshold=-30"); Add("--compressor-makeup-gain=12"); }
            else
            {
                Add($"--compressor-rms-peak={Inv(Num("fx.comp.rmspeak"))}"); Add($"--compressor-attack={Inv(Num("fx.comp.attack"))}");
                Add($"--compressor-release={Inv(Num("fx.comp.release"))}"); Add($"--compressor-threshold={Inv(Num("fx.comp.threshold"))}");
                Add($"--compressor-ratio={Inv(Num("fx.comp.ratio"))}"); Add($"--compressor-knee={Inv(Num("fx.comp.knee"))}");
                Add($"--compressor-makeup-gain={Inv(Num("fx.comp.makeup"))}");
            }
        }
        if (Bool("audio.spatial"))
        {
            if (Str("audio.spatialmode") == "Room spatializer")
            {
                filters.Add("spatializer");
                Add($"--spatializer-roomsize={Inv(Num("fx.spat.size"))}"); Add($"--spatializer-width={Inv(Num("fx.spat.width"))}");
                Add($"--spatializer-wet={Inv(Num("fx.spat.wet"))}"); Add($"--spatializer-dry={Inv(Num("fx.spat.dry"))}");
                Add($"--spatializer-damp={Inv(Num("fx.spat.damp"))}");
            }
            else filters.Add("headphone");
        }
        if (Bool("audio.widen"))
        {
            filters.Add("stereo_widen");
            Add($"--stereowiden-delay={Inv(Num("fx.widen.delay"))}"); Add($"--stereowiden-feedback={Inv(Num("fx.widen.feedback"))}");
            Add($"--stereowiden-crossfeed={Inv(Num("fx.widen.crossfeed"))}"); Add($"--stereowiden-dry-mix={Inv(Num("fx.widen.dry"))}");
        }
        if (Num("audio.pitch") != 0) { filters.Add("scaletempo_pitch"); Add($"--pitch-shift={Inv(Num("audio.pitch"))}"); }
        if (Bool("audio.chorus")) filters.Add("chorus_flanger");
        if (Bool("audio.karaoke")) filters.Add("karaoke");
        if (filters.Count > 0) Add("--audio-filter=" + string.Join(":", filters));
        if (Bool("audio.eq2pass")) Add("--equalizer-2pass");
        Add($"--audio-replay-gain-mode={Str("audio.replaygain").ToLowerInvariant() switch { "track" => "track", "album" => "album", _ => "none" }}");
        Add($"--audio-replay-gain-preamp={Inv(Num("audio.preamp"))}");
        Add($"--audio-language={Lang("audio.lang")}");
        switch (Str("audio.visualizer"))
        {
            case "Goom": Add("--audio-visual=goom"); break;
            case "Spectrum" or "Scope" or "Spectrometer" or "VU meter" or "Wave":
                Add("--audio-visual=visual");
                Add("--effect-list=" + Str("audio.visualizer").ToLowerInvariant() switch { "vu meter" => "vuMeter", var v => v });
                break;
        }
        if (Str("audio.vissize") is var vs && vs.Contains('x')) { Add($"--effect-width={vs.Split('x')[0]}"); Add($"--effect-height={vs.Split('x')[1]}"); }
        if (Bool("audio.spdif")) Add("--spdif");
        Add($"--force-dolby-surround={Str("audio.dolby") switch { "On" => 1, "Off" => 2, _ => 0 }}");
        Add($"--speex-resampler-quality={Str("audio.resampler") switch { "Fast" => 0, "Best" => 10, _ => 4 }}");
        Add($"--role={Str("audio.role").ToLowerInvariant()}");

        // Subtitles
        if (!Bool("subs.enable")) Add("--no-spu");
        Add($"--sub-language={Lang("subs.lang")}");
        if (Str("subs.encoding") is var enc && enc != "Automatic") Add($"--subsdec-encoding={enc.Split(' ')[0]}");
        if (!Bool("subs.autoload")) Add("--no-sub-autodetect-file");
        Add($"--sub-autodetect-fuzzy={Str("subs.matching") switch { "Exact name" => 3, "Any file in the folder" => 4, _ => 2 }}");
        if (Str("subs.folders") is { Length: > 0 } sf) Add($"--sub-autodetect-path={sf}");
        if (Str("subs.font") is var font && font != "Default") Add($"--freetype-font={font}");
        if (Str("subs.monofont") is var mono && mono != "Default") Add($"--freetype-monofont={mono}");
        if (Str("subs.size") is var size && size != "Automatic")
            Add($"--freetype-rel-fontsize={size switch { "Smaller" => 20, "Small" => 18, "Large" => 12, "Larger" => 6, _ => 16 }}");
        if (Bool("subs.bold")) Add("--freetype-bold");
        Add($"--freetype-color={ColorInt("subs.color")}");
        Add($"--freetype-opacity={(int)(Num("subs.opacity") * 2.55)}");
        Add($"--freetype-outline-thickness={Str("subs.outline") switch { "None" => 0, "Thin" => 2, "Thick" => 6, _ => 4 }}");
        Add($"--freetype-outline-color={ColorInt("subs.outlinecolor")}");
        Add($"--freetype-shadow-opacity={(Bool("subs.shadow") ? 128 : 0)}");
        Add($"--freetype-shadow-color={ColorInt("subs.shadowcolor")}");
        Add($"--freetype-shadow-distance={Str("subs.shadowdist") switch { "Medium" => "0.06", "Large" => "0.1", _ => "0.03" }}");
        Add($"--freetype-shadow-angle={Int("subs.shadowangle")}");
        Add($"--freetype-background-opacity={(Str("subs.bg") == "Box" ? (int)(Num("subs.bgopacity") * 2.55) : 0)}");
        Add($"--freetype-background-color={ColorInt("subs.bgcolor")}");
        Add($"--sub-margin={Str("subs.position") switch { "Higher" => 60, "Much higher" => 140, _ => 0 }}");
        Add($"--freetype-text-direction={Str("subs.direction") switch { "Left to right" => 0, "Right to left" => 1, _ => 2 }}");

        // Controls: web remote (Lua HTTP interface)
        if (Bool("controls.web") && Str("controls.webpass") is { Length: > 0 } pass)
        {
            Add("--extraintf=http"); Add($"--http-password={pass}"); Add($"--http-port={Int("controls.webport")}");
        }

        // Network
        Add($"--file-caching={Int("net.file")}"); Add($"--disc-caching={Int("net.disc")}");
        Add($"--network-caching={Int("net.stream")}"); Add($"--live-caching={Int("net.live")}");
        if (Str("net.proxy") is { Length: > 0 } proxy) { Add($"--http-proxy={proxy}"); if (Str("net.proxypass") is { Length: > 0 } pp) Add($"--http-proxy-pwd={pp}"); }
        if (!Bool("net.systemtrust")) Add("--no-gnutls-system-trust");
        Add($"--gnutls-priorities={Str("net.cipher") switch { "Performance" => "PERFORMANCE", "Security" => "SECURE256", "Compatibility" => "LEGACY", _ => "NORMAL" }}");

        // Performance
        Add($"--avcodec-hw={Str("perf.hw") switch { "Direct3D 11" => "d3d11va", "DXVA2" => "dxva2", "Off" => "none", _ => "any" }}");
        Add($"--avcodec-skiploopfilter={Array.IndexOf(["Never", "Non-reference frames", "Bidirectional frames", "Non-key frames", "All frames"], Str("perf.loopfilter")) switch { -1 => 0, var i => i }}");
        Add($"--avcodec-threads={Int("perf.threads")}");
        if (!Bool("perf.drop")) { Add("--no-drop-late-frames"); Add("--no-skip-frames"); }
        if (Str("perf.decoder") is var dec && dec != "Automatic") Add($"--codec={dec.ToLowerInvariant()}");
        Add($"--clock-synchro={Str("perf.clocksync") switch { "Disabled" => 0, "Enabled" => 1, _ => -1 }}");
        Add($"--clock-jitter={Int("perf.jitter")}");

        // Recording / casting
        Add($"--sout-chromecast-conversion-quality={Str("rec.castquality") switch { "Low" => 0, "Medium" => 1, "High" => 2, _ => 1 }}");
        return a;
    }

    // ───────────────────────── schema (texts from the Figma file) ─────────────────────────

    const string NotInLibVlc = "Not available with libVLC 3";
    const string NotYet = "Not available in this player yet";

    static Opt T(string key, string label, bool def, string? info = null, bool engine = false) => new() { Kind = OptKind.Toggle, Key = key, Label = label, Default = def ? "true" : "false", Info = info, Engine = engine };
    static Opt C(string key, string label, string def, string[] options, string? info = null, bool engine = false) => new() { Kind = OptKind.Choice, Key = key, Label = label, Default = def, Options = options, Info = info, Engine = engine };
    static Opt Seg(string key, string label, string def, params string[] options) => new() { Kind = OptKind.Segmented, Key = key, Label = label, Default = def, Options = options };
    static Opt S(string key, string label, double def, double min, double max, double step, string unit, bool engine = false) => new() { Kind = OptKind.Slider, Key = key, Label = label, Default = def.ToString(CultureInfo.InvariantCulture), Min = min, Max = max, Step = step, Unit = unit, Engine = engine };
    static Opt N(string key, string label, double def, double min, double max, string unit = "", bool engine = false, double step = 1) => new() { Kind = OptKind.Number, Key = key, Label = label, Default = def.ToString(CultureInfo.InvariantCulture), Min = min, Max = max, Unit = unit, Engine = engine, Step = step };
    static Opt Txt(string key, string label, string def = "", string? placeholder = null, bool engine = false) => new() { Kind = OptKind.Text, Key = key, Label = label, Default = def, Info = null, ButtonText = placeholder, Engine = engine };
    static Opt Pw(string key, string label, bool engine = true) => new() { Kind = OptKind.Password, Key = key, Label = label, Engine = engine };
    static Opt Dir(string key, string label, string def, bool engine = false) => new() { Kind = OptKind.Folder, Key = key, Label = label, Default = def, Engine = engine };
    static Opt FileOpt(string key, string label, string def) => new() { Kind = OptKind.File, Key = key, Label = label, Default = def };
    static Opt Col(string key, string label, string def) => new() { Kind = OptKind.Color, Key = key, Label = label, Default = def, Engine = true };
    static Opt Act(string label, string button, Action<Window> run, string? info = null) => new() { Kind = OptKind.Action, Label = label, ButtonText = button, Run = run, Info = info };
    static Opt Off(this Opt o, string reason = NotYet) { o.Unavailable = reason; return o; }
    static Opt Eng(this Opt o) { o.Engine = true; return o; }
    static Section Sec(string heading, params Opt[] opts) => new(heading, [.. opts]);

    static readonly string Videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    static readonly string Pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    public static readonly string[] AccentColors = ["#D37918", "#C2412D", "#A68A1E", "#2E8B3A", "#1E8FA3", "#2F5FC4", "#7A3FD1", "#C2408A", "#8F8F8F"];

    public static string[] Fonts() => ["Default", .. System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order()];
    public static string[] Drives() => [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).Select(d => d.Name.TrimEnd('\\'))];

    /// <summary>Filled in by MainWindow (needs the live MediaPlayer).</summary>
    public static Func<string[]>? AudioDevices;
    public static Action<Window>? OpenHotkeys, ClearHistory, OpenEqualizer, RescanPlugins;

    public static readonly List<Category> Categories =
    [
        new("General", "sliders-horizontal", "Global Settings", "App",
        [
            Sec("Appearance",
                C("general.language", "Interface language", "English", ["English"], "More languages are coming."),
                Seg("general.theme", "Theme", "Dark", "Dark", "Light", "System"),
                new Opt { Kind = OptKind.Accent, Key = "general.accent", Label = "Accent color", Default = AccentColors[0] }),
            Sec("Startup",
                T("general.errors", "Show confirmation and error dialogs", false, "Pop up a message when a file can't be played."),
                T("general.updates", "Check for updates automatically", false).Off()),
            Sec("Diagnostics",
                T("general.dev", "Developer mode", false, "Shows developer options under Advanced."),
                T("general.log", "Save a log", false, "Write libVLC messages to a text file."),
                FileOpt("general.logfile", "Log file", Path.Combine(Library.Dir, "pylon-log.txt")),
                C("general.loglevel", "Log details", "Errors only", ["Errors only", "Warnings", "Everything"])),
        ],
        [
            Sec("",
                T("general.minor", "Show minor warnings", false, "Include notices in the log."),
                Txt("general.cmdline", "Additional command line options", "", "e.g., --no-sub-autodetect-file", engine: true),
                C("general.logformat", "Log format", "Plain text", ["Plain text", "HTML"]),
                T("general.stats", "Collect playback statistics", false).Off(),
                Act("Open configurations folder", "Open", _ => System.Diagnostics.Process.Start("explorer.exe", $"\"{Library.Dir}\"")),
                Act("Reset all settings", "Reset", w =>
                {
                    if (MessageBox.Show(w, "Reset every setting to its default?", "Pylon", MessageBoxButton.OKCancel) == MessageBoxResult.OK) ResetAll();
                })),
            Sec("Developer Options",
                T("dev.syslog", "Write to the system log", false).Off(NotInLibVlc),
                T("dev.syslogdebug", "Include debug messages in the system log", false).Off(NotInLibVlc),
                Txt("dev.syslogid", "System log identity", "pylon").Off(NotInLibVlc),
                C("dev.facility", "System log facility", "User", ["User", "Daemon", "Local0"]).Off(NotInLibVlc))
                with { Visible = () => Bool("general.dev") },
        ]),

        new("Interface", "cursor", "Interface Settings", "App",
        [
            Sec("Skin",
                T("ui.skin", "Use a custom skin", false).Off(),
                FileOpt("ui.skinfile", "Skin file", "").Off()),
            Sec("Window",
                T("ui.inside", "Play video inside main window", true).Off("Video always plays inside the player"),
                C("ui.fit", "Resize window to fit video", "Never", ["Never", "On first video", "Always"]),
                T("ui.remember", "Remember window size and position", true),
                T("ui.ontop", "Keep window above other windows", false),
                T("ui.decorations", "Show window decorations", true, "The top bar with the title and window buttons."),
                T("ui.titlefile", "Show the file name in the title bar", true),
                C("ui.fsscreen", "Open fullscreen on", "Current screen", ["Current screen", "Primary screen"])),
            Sec("Playback Controls",
                T("ui.compact", "Start in compact mode", false, "Open in the miniplayer."),
                C("ui.autohide", "Auto-hide the controls", "In fullscreen only", ["Never", "In fullscreen only", "Always"]),
                S("ui.hideafter", "Hide after", 3, 0, 10, 0.5, " s"),
                T("ui.fscontrols", "Show controls in fullscreen", true)),
            Sec("Notifications and System Tray",
                T("ui.tray", "Show a system tray icon", false).Off(),
                T("ui.traymin", "Start minimized to the tray", false).Off(),
                C("ui.popup", "Show a popup when the track changes", "When minimized", ["Never", "When minimized", "Always"]).Off(),
                S("ui.popuptime", "Popup duration", 5, 0, 10, 1, " s")
                    .Off()),
            Sec("Playlist",
                T("ui.tree", "Show the playlist as a tree", false).Off()),
        ],
        [
            Sec("",
                S("ui.opacity", "Window opacity", 100, 10, 100, 5, "%").Off("Video windows can't be translucent"),
                T("ui.pausemin", "Pause video when window is minimized", false),
                T("ui.front", "Bring Pylon to the front when playback starts", false),
                T("ui.cover", "Show cover art in the window when playing audio", true),
                T("ui.browser", "Show the file browser inside the open dialog", true).Off(),
                T("ui.appicon", "Allow the app icon to change", true).Off(),
                S("ui.fsopacity", "Fullscreen controls opacity", 100, 10, 100, 5, "%"),
                C("ui.sensitivity", "Fullscreen controls sensitivity", "Medium", ["Low", "Medium", "High"])),
        ]),

        new("Privacy", "shield", "Privacy Settings", "App",
        [
            Sec("History",
                T("privacy.history", "Save recently played items", true),
                T("privacy.position", "Remember the playback position", true),
                Txt("privacy.exclude", "Never remember items matching", "", "e.g., *private*; D:\\Secret\\*"),
                Act("Clear history now", "Clear", w => ClearHistory?.Invoke(w)),
                T("library.netmeta", "Fetch titles and cover art from the internet", false, "Looks up missing tags and artwork online.", engine: true),
                T("privacy.lastfm", "Submit played tracks to last.fm", false).Off(NotInLibVlc),
                Txt("privacy.lastfmuser", "Last.fm username").Off(NotInLibVlc),
                Pw("privacy.lastfmpass", "Last.fm password").Off(NotInLibVlc),
                Txt("privacy.lastfmurl", "Last.fm submission URL").Off(NotInLibVlc)),
        ], []),

        new("Controls", "keyboard", "Mouse, Keyboard & Remote Controls", "App",
        [
            Sec("Keyboard Shortcuts",
                C("controls.defaults", "Defaults", "VLC-style", ["VLC-style"]),
                Act("Edit Keyboard Shortcuts", "Edit", w => OpenHotkeys?.Invoke(w)),
                Act("Reset all shortcuts", "Reset", _ => Hotkeys.ResetAll())),
            Sec("Skip Amounts",
                N("controls.skip0", "Very short skip", 3, 1, 600, " s"),
                N("controls.skip1", "Short skip", 10, 1, 600, " s"),
                N("controls.skip2", "Medium skip", 60, 1, 3600, " s"),
                N("controls.skip3", "Long skip", 300, 1, 7200, " s")),
            Sec("Mouse",
                C("controls.click", "Single click on the video", "Play / Pause", ["Play / Pause", "Nothing"]),
                C("controls.dblclick", "Double click on the video", "Toggle fullscreen", ["Toggle fullscreen", "Nothing"]),
                C("controls.wheel", "Scroll wheel", "Volume", ["Volume", "Seek", "Nothing"]),
                C("controls.hwheel", "Horizontal scroll", "Seek", ["Seek", "Volume", "Nothing"]),
                T("controls.reverse", "Reverse scroll direction", false)),
            Sec("Remotes",
                T("controls.web", "Web remote control", false, "Control playback from a browser on your network (http://this-pc:port).", engine: true),
                N("controls.webport", "Port", 8080, 1024, 65535, engine: true),
                Pw("controls.webpass", "Password")),
        ], []),

        new("Playback", "play", "Playback Preferences", "Media",
        [
            Sec("Startup",
                C("playback.resume", "Continue where you left off", "Ask", ["Never", "Ask", "Always"]),
                T("playback.autoplay", "Start playing as soon as a file opens", true),
                S("playback.speed", "Default playback speed", 1, 0.25, 2, 0.25, "×"),
                T("playback.duck", "Pause when another app uses audio", false).Off()),
            Sec("Tracks",
                C("playback.quality", "Preferred video quality", "Best available", ["Best available", "2160p", "1440p", "1080p", "720p", "480p", "360p"], "For adaptive streams.", engine: true),
                C("playback.menulang", "Preferred menu language", "System language", LanguageNames, "For DVD and Blu-ray menus.", engine: true),
                N("playback.audiotrack", "Default audio track", 0, 0, 99, engine: true),
                N("playback.subtrack", "Default subtitle track", 0, 0, 99, engine: true)),
            Sec("Order",
                C("playback.repeat", "Repeat", "Off", ["Off", "One", "All"]),
                T("playback.shuffle", "Shuffle", false),
                T("playback.stopeach", "Stop after each item", false),
                C("playback.finish", "When playback finishes", "Stop", ["Stop", "Clear the queue", "Close Pylon"])),
            Sec("Seeking",
                T("playback.precise", "Seek precisely", true, "Slower, but lands on the exact frame.", engine: true),
                T("playback.thumbs", "Show thumbnail while scrubbing", true)),
            Sec("Files & Discs",
                new Opt { Kind = OptKind.Choice, Key = "playback.drive", Label = "Default optical drive", Default = "", DynamicOptions = Drives, Options = ["-"] },
                C("playback.avi", "Repair damaged AVI files", "Ask", ["Ask", "Always", "Never"], engine: true),
                T("playback.mkv", "Load matching .mkv files from the same folder", false).Off(NotInLibVlc)),
        ],
        [
            Sec("",
                Dir("playback.tsfolder", "Time-shift buffer folder", "", engine: true),
                C("playback.tssize", "Time-shift buffer size", "Medium", ["Small", "Medium", "Large"], engine: true)),
        ]),

        new("Video", "monitor-play", "Video Defaults", "Media",
        [
            Sec("Output",
                T("video.enable", "Enable video", true, engine: true),
                T("video.fullscreen", "Start videos in fullscreen", false),
                T("video.screensaver", "Stop the screensaver during playback", true),
                T("video.wallpaper", "Play video as desktop wallpaper", false).Off(NotInLibVlc)),
            Sec("Picture",
                S("fxv.hue", "Hue", 0, -180, 180, 1, "°"),
                S("fxv.brightness", "Brightness", 1, 0, 2, 0.01, ""),
                S("fxv.contrast", "Contrast", 1, 0, 2, 0.01, ""),
                S("fxv.saturation", "Saturation", 1, 0, 3, 0.01, ""),
                S("fxv.gamma", "Gamma", 1, 0.01, 10, 0.01, ""),
                Act("Reset picture", "Reset", _ => { foreach (var k in new[] { "fxv.hue", "fxv.brightness", "fxv.contrast", "fxv.saturation", "fxv.gamma" }) Set(k, Find(k)!.Default); })),
            Sec("Deinterlacing",
                C("video.deint", "Deinterlacing", "Automatic", ["Automatic", "On", "Off"]),
                C("video.deintmode", "Method", "Automatic", ["Automatic", "Discard", "Blend", "Mean", "Bob", "Linear", "X", "Yadif", "Yadif (2x)", "Phosphor", "IVTC"])),
            Sec("Framing",
                C("video.aspect", "Aspect ratio", "Automatic", ["Automatic", "16:9", "4:3", "1:1", "16:10", "2.21:1", "2.35:1", "2.39:1", "5:4"]),
                C("video.crop", "Crop", "None", ["None", "16:9", "4:3", "1:1", "16:10", "2.21:1", "2.35:1", "2.39:1", "5:4"]),
                T("video.scale", "Scale video to the window", true),
                C("video.zoom", "Zoom", "Fit", ["Fit", "1:4", "1:2", "1:1", "2:1"]),
                C("video.position", "Position in the window", "Center", ["Center"]).Off(NotInLibVlc)),
            Sec("Snapshots",
                Dir("video.snapdir", "Save to", Pictures),
                Txt("video.snapprefix", "File name prefix", "snapshot-"),
                C("video.snapformat", "Format", "PNG", ["PNG", "JPG"], engine: true),
                C("video.snapsize", "Size", "Same as video", ["Same as video", "Half", "Quarter"]),
                T("video.snapseq", "Number files sequentially", false),
                T("video.snappreview", "Show a preview after capture", true)),
        ],
        [
            Sec("",
                Txt("video.customaspect", "Custom aspect ratios", "", "e.g., 21:9, 32:9"),
                Txt("video.customcrop", "Custom crop ratios", "", "e.g., 2.76:1"),
                Txt("video.par", "Monitor pixel aspect ratio", "1:1", engine: true)),
        ]),

        new("Audio", "waveform", "Audio Defaults", "Media",
        [
            Sec("Output",
                T("audio.enable", "Enable audio", true, engine: true),
                new Opt { Kind = OptKind.Choice, Key = "audio.device", Label = "Output device", Default = "System default", Options = ["System default"], DynamicOptions = () => AudioDevices?.Invoke() ?? [] },
                C("audio.speakers", "Speaker setup", "Automatic", ["Automatic", "Stereo", "Reverse stereo", "Left", "Right", "Dolby Surround"])),
            Sec("Volume",
                Seg("audio.maxvol", "Maximum volume", "125%", "100%", "125%", "200%", "300%"),
                S("audio.startvol", "Startup volume", 100, 0, 125, 1, "%"),
                N("audio.step", "Volume step", 5, 1, 25, "%")),
            Sec("Loudness",
                T("audio.normalize", "Normalize loudness", false, "Evens out loud and quiet files.", engine: true),
                N("audio.normlevel", "Target level", 2, 0, 10, "", engine: true, step: 0.1),
                T("audio.night", "Night mode", false, "Compresses loud peaks so you can keep the volume low.", engine: true),
                C("audio.replaygain", "Replay gain", "Off", ["Off", "Track", "Album"], engine: true),
                N("audio.preamp", "Replay gain preamp", 0, -20, 20, " dB", engine: true, step: 0.5)),
            Sec("Effects",
                T("audio.eq", "Equalizer", false),
                new Opt { Kind = OptKind.Choice, Key = "audio.eqpreset", Label = "Equalizer preset", Default = "Flat", DynamicOptions = () => Effects.PresetNames },
                Act("Equalizer bands", "Open", w => OpenEqualizer?.Invoke(w)),
                T("audio.eq2pass", "Two-pass", false, "Applies the equalizer twice for a stronger effect.", engine: true),
                T("audio.spatial", "Spatial audio", false, engine: true),
                C("audio.spatialmode", "Spatial audio mode", "Headphone virtualization", ["Headphone virtualization", "Room spatializer"], engine: true),
                T("audio.widen", "Stereo widening", false, engine: true),
                T("audio.compressor", "Compressor", false, engine: true),
                N("audio.pitch", "Pitch shift", 0, -12, 12, " semitones", engine: true, step: 0.5),
                T("audio.chorus", "Chorus/flanger", false, engine: true),
                T("audio.karaoke", "Karaoke", false, "Removes centered vocals.", engine: true)),
            Sec("Tracks",
                C("audio.lang", "Preferred audio language", "System language", LanguageNames, engine: true),
                N("audio.delay", "Audio delay", 0, -10, 10, " s", step: 0.05)),
            Sec("Visualization",
                C("audio.visualizer", "Visualizer", "Off", ["Off", "Spectrum", "Scope", "Spectrometer", "VU meter", "Goom"], "Shown instead of cover art when playing music.", engine: true)),
        ],
        [
            Sec("",
                T("audio.spdif", "Pass compressed audio to the receiver", false, "S/PDIF or HDMI passthrough.", engine: true),
                C("audio.dolby", "Detect Dolby Surround", "Automatic", ["Automatic", "On", "Off"], engine: true),
                C("audio.resampler", "Resampling quality", "Balanced", ["Fast", "Balanced", "Best"], engine: true),
                C("audio.role", "Media role", "Video", ["Video", "Music", "Communication", "Game", "Notification", "Animation", "Production", "Accessibility", "Test"], engine: true),
                C("audio.vissize", "Visualization size", "Match window", ["Match window", "640x360", "1280x720", "1920x1080"], engine: true)),
        ]),

        new("Subtitles", "subtitles", "Subtitle Defaults", "Media",
        [
            Sec("Tracks & Languages",
                T("subs.enable", "Enable subtitles", true, engine: true),
                C("subs.lang", "Preferred language", "System language", LanguageNames, engine: true),
                C("subs.encoding", "Text encoding", "Automatic", ["Automatic", "UTF-8", "Windows-1251 (Cyrillic)", "KOI8-R (Cyrillic)", "Windows-1252 (Western)", "ISO-8859-2 (Central European)", "Shift_JIS (Japanese)", "GB18030 (Chinese)", "Big5 (Chinese)"], "Use this when subtitles show garbled letters.", engine: true)),
            Sec("Loading Subtitles",
                T("subs.autoload", "Load subtitle files automatically", true, engine: true),
                C("subs.matching", "File name matching", "Title with extra characters", ["Exact name", "Title with extra characters", "Any file in the folder"], engine: true)),
            Sec("Appearance",
                new Opt { Kind = OptKind.Choice, Key = "subs.font", Label = "Font", Default = "Default", DynamicOptions = Fonts, Engine = true },
                C("subs.size", "Font size", "Automatic", ["Automatic", "Smaller", "Small", "Normal", "Large", "Larger"], engine: true),
                T("subs.bold", "Bold", false, engine: true),
                Col("subs.color", "Text color", "#FFFFFF"),
                S("subs.opacity", "Text opacity", 100, 0, 100, 5, "%", engine: true),
                C("subs.outline", "Outline thickness", "Normal", ["None", "Thin", "Normal", "Thick"], engine: true),
                Col("subs.outlinecolor", "Outline color", "#000000"),
                T("subs.shadow", "Shadow", true, engine: true),
                Col("subs.shadowcolor", "Shadow color", "#000000"),
                C("subs.shadowdist", "Shadow distance", "Small", ["Small", "Medium", "Large"], engine: true),
                C("subs.bg", "Background", "None", ["None", "Box"], engine: true),
                Col("subs.bgcolor", "Background color", "#000000"),
                S("subs.bgopacity", "Background opacity", 50, 0, 100, 5, "%", engine: true),
                C("subs.position", "Vertical position", "Bottom", ["Bottom", "Higher", "Much higher"], engine: true)),
            Sec("Timing",
                S("subs.delay", "Subtitle delay", 0, -5, 5, 0.1, " s")),
            Sec("On-Screen Messages",
                T("osd.enable", "Show feedback for shortcuts", true),
                S("osd.time", "Show feedback for", 1.2, 0.5, 5, 0.1, " s"),
                T("osd.filename", "Show filename when a video starts", false),
                S("osd.filetime", "Show filename for", 3, 1, 10, 0.5, " s")),
        ],
        [
            Sec("",
                Txt("subs.folders", "Load subtitles from these folders", "", "e.g., .\\subtitles, .\\subs", engine: true),
                T("subs.ignoreass", "Ignore styling in ASS and SSA subtitles", false).Off(NotInLibVlc),
                N("subs.shadowangle", "Shadow angle", -45, -360, 360, "°", engine: true),
                C("subs.direction", "Text direction", "Automatic", ["Automatic", "Left to right", "Right to left"], engine: true),
                new Opt { Kind = OptKind.Choice, Key = "subs.monofont", Label = "Monospace font", Default = "Default", DynamicOptions = Fonts, Engine = true }),
        ]),

        new("Library", "folder", "Playlists, Folders & Files", "Media",
        [
            Sec("Opening Files",
                T("library.single", "Only allow one Pylon window", true),
                T("library.singlefm", "Apply to files opened from the file manager", true),
                T("library.enqueue", "Add to the playlist instead of replacing it", false),
                C("library.subfolders", "When opening a folder", "Include subfolders", ["Include subfolders", "Only this folder"]),
                T("library.hidden", "Show hidden files", false),
                Act("Default player", "Choose", _ => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Pylon") { UseShellExecute = true }),
                    "Windows lets only you pick the default app. File types are registered by the installer.")),
            Sec("Metadata & Artwork",
                C("library.artsource", "Fetch titles and artwork", "From the file only", ["From the file only", "From the internet too"], "Same as the internet option under Privacy."),
                T("library.enabled", "Use the media library", true, "Show the library sidebar.")),
        ], []),

        new("Network", "broadcast", "Network Connections & Security", "System",
        [
            Sec("Buffering",
                C("net.preset", "Buffering", "Normal", ["Low latency", "Normal", "Smooth", "Custom"]),
                N("net.file", "Local files", 300, 0, 60000, " ms", engine: true),
                N("net.disc", "Discs", 300, 0, 60000, " ms", engine: true),
                N("net.stream", "Network streams", 1000, 0, 60000, " ms", engine: true),
                N("net.live", "Live capture", 300, 0, 60000, " ms", engine: true)),
            Sec("Proxy",
                Txt("net.proxy", "HTTP proxy", "", "System proxy", engine: true),
                Pw("net.proxypass", "HTTP proxy password")),
            Sec("Security",
                T("net.systemtrust", "Verify certificates against the system trust store", true, engine: true),
                C("net.cipher", "Cipher preference", "Balanced", ["Balanced", "Performance", "Security", "Compatibility"], engine: true)),
        ], []),

        new("Performance", "gauge", "Performance Settings", "System",
        [
            Sec("Decoding",
                C("perf.hw", "Hardware-accelerated decoding", "Automatic", ["Automatic", "Direct3D 11", "DXVA2", "Off"], engine: true),
                C("perf.postproc", "Video post-processing", "Off", ["Off", "Low", "Medium", "High"]).Off(NotInLibVlc),
                C("perf.loopfilter", "Skip the H.264 loop filter", "Never", ["Never", "Non-reference frames", "Bidirectional frames", "Non-key frames", "All frames"], "Faster on weak CPUs, slightly blurrier.", engine: true),
                N("perf.threads", "Decoding threads", 0, 0, 64, "", engine: true)),
            Sec("Smooth Playback",
                T("perf.drop", "Drop frames when the system can't keep up", true, engine: true),
                T("perf.realtime", "Give Pylon real-time priority", false, "Raises the process priority."),
                C("perf.priority", "Process priority", "Normal", ["Below normal", "Normal", "Above normal", "High"]),
                Act("Rescan plugins", "Rescan", w => RescanPlugins?.Invoke(w))),
        ],
        [
            Sec("Developer",
                C("perf.encoder", "Encoder", "Automatic", ["Automatic"]).Off(NotYet),
                C("perf.decoder", "Decoder", "Automatic", ["Automatic", "avcodec", "d3d11va", "dxva2"], engine: true),
                C("perf.clocksync", "Clock synchronisation", "Default", ["Default", "Disabled", "Enabled"], engine: true),
                N("perf.jitter", "Clock jitter", 5000, 0, 60000, " ms", engine: true),
                N("perf.averaging", "Clock reference averaging", 40, 1, 1000).Off(NotInLibVlc)),
        ]),

        new("Recording", "record", "Recording, Casting & Streaming Out", "System",
        [
            Sec("",
                Dir("rec.dir", "Save recordings to", Videos),
                T("rec.raw", "Record without re-encoding when possible", true).Off("Recordings are always saved without re-encoding")),
            Sec("Casting",
                T("rec.cast", "Discover cast devices", true, "Chromecast devices on your network."),
                C("rec.castquality", "Cast quality", "Automatic", ["Automatic", "Low", "Medium", "High"], engine: true)),
            Sec("Streaming",
                T("rec.local", "Play locally while streaming", true).Off(),
                T("rec.audio", "Include audio", true).Off(),
                T("rec.video", "Include video", true).Off(),
                T("rec.subs", "Include subtitles", false).Off(),
                C("rec.preset", "Encoding preset", "Medium", ["Medium"]).Off(),
                C("rec.tuning", "Tuning", "None", ["None"]).Off()),
        ], []),

        new("Extensions", "puzzle-piece", "Add-ons & Scripts", "System",
        [
            Sec("Add-ons",
                Act("Find more extensions", "Browse", _ => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://addons.videolan.org") { UseShellExecute = true })),
                T("ext.load", "Load add-ons on startup", false).Off("VLC Lua extensions need the original Qt interface")),
        ], []),
    ];
}
