// Regression checks for things that broke silently before; no test framework, exit code 1 on failure.
using System.IO;
using LibVLCSharp.Shared;
using Pylon;

Environment.SetEnvironmentVariable("PYLON_DATA", Directory.CreateTempSubdirectory("pylon-checks").FullName); // never the user's profile
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    if (!ok) failed++;
}

Check("offline names distinguish sources after sanitizing", YtDlp.OfflineStem("Song?", "source1") != YtDlp.OfflineStem("Song*", "source2")
    && YtDlp.OfflineStem("Song?", "source1") == YtDlp.OfflineStem("Song*", "source1")
    && YtDlp.OfflineStem("CON", "source1").Length < 180);

Check("maximum volume change keeps the actual level", Math.Abs(AudioChain.ResizeVolumeSlider(0.4, 125, 300) * 300 - 50) < 0.001
    && AudioChain.ResizeVolumeSlider(1, 300, 100) == 1);
Check("own audio uses fallback instead of silently clipping requested gain", Player.SupportsOwnVolume(1.25, 1)
    && !Player.SupportsOwnVolume(2, 1) && !Player.SupportsOwnVolume(1, 3) && !Player.SupportsOwnVolume(double.NaN, 1));
Check("clip time preserves total hours", Converter.FormatTime(90_123_500) == "25:02:03.5"
    && Converter.ParseTime(Converter.FormatTime(90_123_500)) == 90_123_500);
Settings.Set("video.upscale", "Point");
Settings.Set("video.hdr", "Never");
Check("video options keep their command-line prefixes", Settings.EngineArgs().Contains("--d3d11-upscale-mode=point")
    && Settings.EngineArgs().Contains("--d3d11-hdr-mode=never") && Settings.EngineArgs().All(a => a.StartsWith("--")));
Settings.Set("video.upscale", "Linear");
Settings.Set("video.hdr", "Auto");
{
    var file = Path.Combine(Library.Dir, "broken.mp3");
    Directory.CreateDirectory(Library.Dir);
    File.WriteAllText(file, "");
    Check("playback failure identifies an empty file", PlaybackDiagnostics.Inspect(file, []).Reason == "The file is empty.");
    File.WriteAllText(file, "<!DOCTYPE html><html>download expired</html>");
    Check("playback failure detects a web page named mp3", PlaybackDiagnostics.Inspect(file, []).Reason.Contains("web page"));
    Check("playback failure identifies missing media", PlaybackDiagnostics.Inspect(file + ".missing", []).Reason.Contains("moved or deleted"));
    Check("playback failure does not invent a missing codec", PlaybackDiagnostics.Classify([]).Contains("exact cause is unknown")
        && PlaybackDiagnostics.Classify(["no suitable decoder module"]).Contains("decoder"));
    var errors = new PlaybackDiagnostics();
    for (var i = 0; i < 50; i++) errors.Add("input", "https://user:pass@example.com/a?token=secret#fragment " + i);
    Check("playback error trail is bounded and hides credentials", errors.Snapshot().Length == 32 && !string.Join("\n", errors.Snapshot()).Contains("secret")
        && !string.Join("\n", errors.Snapshot()).Contains("pass"));
    errors.Clear();
    Check("new media starts with a clean error trail", errors.Snapshot().Length == 0);
}

// Video zoom is relative to fit, including anamorphic pixels and explicit aspect overrides.
Check("video zoom: bounds and fit crossing", VideoZoom.Step(25, -120) == 25 && VideoZoom.Step(400, 120) == 400
    && VideoZoom.Step(95, 120) == 100 && VideoZoom.Step(105, -120) == 100 && VideoZoom.Step(90, 120) == 100 && VideoZoom.Step(110, -120) == 100 && VideoZoom.Step(100, 0) == 100);
Check("video zoom: saved percentages", VideoZoom.Percent("110%") == 110 && VideoZoom.Percent("999%") == 400 && VideoZoom.Percent("Fit") == 100);
Check("video zoom: landscape fit", Math.Abs(VideoZoom.Scale(1920, 1080, 1280, 720, 1, 0, 150) - 1) < 0.0001);
Check("video zoom: resize and pillarbox", Math.Abs(VideoZoom.Scale(1920, 1080, 640, 720, 1, 0, 150) - 0.5) < 0.0001);
Check("video zoom: SAR", Math.Abs(VideoZoom.Scale(720, 576, 1024, 576, 64.0 / 45, 0, 200) - 2) < 0.0001);
Check("video zoom: aspect overrides SAR", Math.Abs(VideoZoom.Scale(720, 576, 768, 576, 64.0 / 45, 4.0 / 3, 200) - 2) < 0.0001);
Check("video zoom: no layout yet", VideoZoom.Scale(1920, 1080, 0, 720, 1, 0, 110) == 0);
// Diagnostics must exist even when no video has been opened, including on a new profile.
ErrorLog.Note("startup check", "music-only startup");
Check("diagnostic log without video", File.ReadAllText(Path.Combine(Library.Dir, "errors.log")).Contains("music-only startup"));
Settings.Set("perf.softwareui", true);
InterfaceRendering.Apply();
Check("interface compatibility mode", System.Windows.Media.RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly);
Settings.Set("perf.softwareui", false);
InterfaceRendering.Apply();
Check("interface rendering restored", System.Windows.Media.RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.Default);
Settings.Set("general.updates", false);
Check("automatic update checks respect opt-out", !Updater.DueForCheck());
Settings.Set("general.updates", true);
Settings.Set("app.lastupdatecheck", DateTime.UtcNow.AddDays(2).ToString("o"));
Check("clock correction does not disable update checks", Updater.DueForCheck());
Settings.Set("app.lastupdatecheck", DateTime.UtcNow.ToString("o"));
Check("update checks are not repeated within a day", !Updater.DueForCheck());

// An existing executable can be chosen explicitly; installation must then avoid the network.
{
    var configured = Path.Combine(Library.Dir, "chosen ffmpeg.exe");
    var explicitExe = Path.Combine(Library.Dir, "override ffmpeg.exe");
    File.WriteAllText(configured, "test executable path");
    File.WriteAllText(explicitExe, "test override path");
    var previous = Environment.GetEnvironmentVariable("PYLON_FFMPEG");
    try
    {
        Environment.SetEnvironmentVariable("PYLON_FFMPEG", null);
        Settings.Set("tools.ffmpeg", configured);
        Check("ffmpeg: configured executable avoids download", Ffmpeg.Exe == configured && await Ffmpeg.InstallAsync(null));
        Environment.SetEnvironmentVariable("PYLON_FFMPEG", explicitExe);
        Check("ffmpeg: environment override wins", Ffmpeg.Exe == explicitExe);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PYLON_FFMPEG", previous);
        Settings.Set("tools.ffmpeg", "");
    }
}

// ── lyrics: translations from Apple TTML and bilingual LRC; duplicate LRC times must not end lines early
var ttml = Lyrics.ParseTtml("""
    <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
    <head><metadata><iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal"><translations><translation xml:lang="ru"><text for="L1">Держись</text></translation></translations></iTunesMetadata></metadata></head>
    <body><div><p begin="1.0" end="3.0" itunes:key="L1"><span begin="1.0" end="2.0">Hold</span> <span begin="2.0" end="3.0">on</span></p>
    <p begin="4.0" end="6.0">Second<span ttm:role="x-translation">Вторая</span></p></div></body></tt>
    """, "t")!;
Check("TTML head translation", ttml.Lines[0].Translation == "Держись" && ttml.Lines[0].Words.Length == 2);
Check("TTML inline translation", ttml.Lines[1].Translation == "Вторая" && ttml.Lines[1].Text == "Second");
// background vocals: kept apart from the lead and word-timed, also on a line that is background only (spicy-translate exports those)
var bgTtml = Lyrics.ParseTtml("""
    <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div>
    <p begin="1.0" end="3.0"><span begin="1.0" end="2.0">Lead</span><span ttm:role="x-bg"><span begin="2.3" end="2.6">(oh</span> <span begin="2.6" end="3.0">yeah)</span></span></p>
    <p begin="4.0" end="5.0"><span ttm:role="x-bg"><span begin="4.0" end="4.5">only</span> <span begin="4.5" end="5.0">bg</span></span></p></div></body></tt>
    """, "t")!;
Check("TTML background vocals", bgTtml.Lines.Length == 2 && bgTtml.Lines[0].Words.Length == 1 && bgTtml.Lines[0].Background.Length == 2
    && bgTtml.Lines[1].Words.Length == 0 && bgTtml.Lines[1].Background.Length == 2 && bgTtml.Lines[1].Text == "only bg");
var bgSpicy = SpicyLyrics.Parse("""
    {"Body":{"Type":"Syllable","Content":[
      {"Type":"Vocal","Lead":{"StartTime":1,"EndTime":2,"Syllables":[{"Text":"Lead","StartTime":1,"EndTime":2,"IsPartOfWord":false}]},
       "Background":[{"Syllables":[{"Text":"oh","StartTime":2.3,"EndTime":2.6,"IsPartOfWord":false}]}]},
      {"Type":"Vocal","Lead":{"StartTime":4,"EndTime":5,"Syllables":[]},"Background":[{"Syllables":[{"Text":"only","StartTime":4,"EndTime":4.5,"IsPartOfWord":false},{"Text":"bg","StartTime":4.5,"EndTime":5,"IsPartOfWord":false}]}]}]}}
    """)!;
Check("Spicy background vocals", bgSpicy.Lines.Length == 2 && bgSpicy.Lines[0].Background.Length == 1
    && bgSpicy.Lines[1].Words.Length == 0 && bgSpicy.Lines[1].Background.Length == 2, $"{bgSpicy.Lines.Length} lines");
Check("Spicy plain lyrics without timed Content", SpicyLyrics.Parse("""
    {"Body":{"Type":"Static","Lines":[{"Text":"Hello"},{"Text":"World"}]}}
    """) is { Synced: false, Lines.Length: 2 });
Check("Spicy null response is not lyrics", SpicyLyrics.Parse("{\"Body\":null}") == null);
var lrc = Lyrics.ParseLrc("[00:01.00]Hello\n[00:01.00]Привет\n[00:04.00]Next\n", "l")!;
Check("LRC bilingual", lrc.Lines.Length == 2 && lrc.Lines[0].Translation == "Привет" && lrc.Lines[0].End == 4, $"{lrc.Lines[0].Start}-{lrc.Lines[0].End}");

// ── names: untagged files are named from "Artist - Title"; iTunes' Latin spelling of a Cyrillic artist still matches
Check("name from file", Library.FromFileName(@"C:\m\01. Лолита - Ориентация Север.mp3", "01. Лолита - Ориентация Север.mp3") == ("Ориентация Север", "Лолита")
    && Library.FromFileName(@"C:\m\AC-DC - Thunderstruck.mp3", "AC-DC - Thunderstruck.mp3") == ("Thunderstruck", "AC-DC")
    && Library.FromFileName(@"C:\m\x.mp3", "Real Title") == ("Real Title", null));
// ── "Add to Music": the same song twice is copied once, another one of that name gets " (2)", its .lrc comes along
{
    var root = Path.Combine(Path.GetTempPath(), "pylon-check-music-" + Environment.ProcessId);
    Directory.CreateDirectory(Path.Combine(root, "a"));
    Directory.CreateDirectory(Path.Combine(root, "b"));
    File.WriteAllText(Path.Combine(root, "a", "song.mp3"), "one");
    File.WriteAllText(Path.Combine(root, "a", "song.lrc"), "[00:01.00]hi");
    File.WriteAllText(Path.Combine(root, "b", "song.mp3"), "other");
    var into = Path.Combine(root, "music");
    var first = Library.CopyToMusic([Path.Combine(root, "a", "song.mp3")], into);
    var again = Library.CopyToMusic([Path.Combine(root, "a", "song.mp3")], into);
    var clash = Library.CopyToMusic([Path.Combine(root, "b", "song.mp3")], into);
    Check("add to music", first == 1 && again == 0 && clash == 1 && File.Exists(Path.Combine(into, "song (2).mp3")) && File.Exists(Path.Combine(into, "song.lrc")),
        $"{first} {again} {clash}");
    Directory.Delete(root, true);
}

// ── proxy for websites: the password field goes into the address, socks5h works for .NET, "Off" means none
{
    Settings.Set("net.proxymode", "Automatic");
    Settings.Set("net.proxy", "socks5h://ann@127.0.0.1:1080");
    Settings.Set("net.proxypass", "p@ss");
    var withPass = YtDlp.Proxy;
    var web = YtDlp.WebProxy(withPass!);
    Settings.Set("net.proxymode", "Off");
    var off = YtDlp.Proxy;
    Check("proxy settings", withPass == "socks5h://ann:p%40ss@127.0.0.1:1080" && web.Address?.Scheme == "socks5" && web.Credentials is System.Net.NetworkCredential { Password: "p@ss" } && off == null,
        $"{withPass} {web.Address} {off}");
    foreach (var k in new[] { "net.proxymode", "net.proxy", "net.proxypass" }) Settings.Set(k, Settings.Find(k)!.Default);
}

Check("artist across scripts", Covers.Same("Lolita", "Лолита") && !Covers.Same("Madonna", "Лолита"));

// ── a song from Spotify / the radio finds the library file: title and first artist, case and punctuation aside
{
    var song = Path.Combine(Path.GetTempPath(), $"pylon-check-{Guid.NewGuid():N}.mp3");
    File.WriteAllText(song, "");
    Library.Data.Meta[song] = new TrackMeta { Title = "Hello", Artist = "Adele" };
    var found = Library.FindSong("hello!", "Adele, Someone Else");
    var none = Library.FindSong("Hello", "Lionel Richie");
    Library.Data.Meta.Remove(song);
    File.Delete(song);
    Check("library finds a song by name", found == song && none == null);
}

// ── lyrics "from the cover": a dark cover's tone is lightened on a dark background, a pale one darkened on a light one,
// keeping its hue
{
    var navy = LyricsView.Readable(System.Windows.Media.Color.FromRgb(20, 32, 90), darkBackground: true);
    var butter = LyricsView.Readable(System.Windows.Media.Color.FromRgb(250, 240, 180), darkBackground: false);
    Check("lyrics color stays readable", navy.B > 200 && navy.B > navy.R && butter.R < 110 && butter.R > butter.B, $"{navy} {butter}");
}

// ── default player: every format that doesn't open in Pylon yet is taken, ours are left alone
var take = ShellRegistration.NotOurs(ext => ext == ".mkv" ? "Pylon.mkv" : ext == ".mp3" ? "WMP11.AssocFile.MP3" : ext == ".flac" ? "KLCP.WMP64.flac" : null);
Check("default player: formats to take", !take.Contains(".mkv") && take.Contains(".mp3") && take.Contains(".flac") && take.Contains(".mp4"), string.Join(" ", take.Take(6)));

// ── translations: every language file has the English file's keys, and keeps its {0} {1} placeholders
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Pylon", "lang", "en.json"))) dir = dir.Parent;
    var langDir = Path.Combine(dir!.FullName, "Pylon", "lang");
    Dictionary<string, string> Read(string f) => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f))!;
    var en = Read(Path.Combine(langDir, "en.json"));
    static string Holes(string s) => string.Concat(System.Text.RegularExpressions.Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order());
    foreach (var file in Directory.EnumerateFiles(langDir, "*.json").Where(f => !f.EndsWith("en.json")))
    {
        var tr = Read(file);
        var missing = en.Keys.Except(tr.Keys).ToList();
        var extra = tr.Keys.Except(en.Keys).ToList();
        var broken = tr.Where(p => p.Value.Length > 0 && p.Key != "_name" && en.ContainsKey(p.Key) && Holes(p.Value) != Holes(p.Key)).Select(p => p.Key).ToList();
        Check($"lang {Path.GetFileName(file)}", missing.Count == 0 && extra.Count == 0 && broken.Count == 0,
            $"missing {missing.Count}, unknown {extra.Count}, placeholders {broken.Count}: {string.Join(" | ", missing.Concat(extra).Concat(broken).Take(3))}");
    }
}

// ── subtitles: SRT / ASS read and shifted; auto-sync finds a shift and a 23.976 → 25 speed-up from speech marks
{
    var srt = "1\n00:00:01,500 --> 00:00:03,000\nHello <i>there</i>\n\n2\n00:01:02,250 --> 00:01:04,000\nSecond\n";
    var cues = SubText.Parse(srt, ".srt");
    var shifted = SubText.Parse(SubText.Shift(srt, ".srt", 2, 1), ".srt");
    var ass = "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:05.10,0:00:06.00,Default,,0,0,0,,{\\i1}Hi\\Nthere\n";
    var assCues = SubText.Parse(ass, ".ass");
    Check("subtitles: read and shift", cues.Count == 2 && cues[0].Text == "Hello there" && Math.Abs(cues[1].Start - 62.25) < 0.001
        && Math.Abs(shifted[0].Start - 3.5) < 0.001 && assCues.Count == 1 && assCues[0].Text == "Hi\nthere" && Math.Abs(assCues[0].Start - 5.1) < 0.001);

    // speech where the (shifted, sped-up) lines really are; the file's cues are the unshifted ones
    var rnd = new Random(7);
    var lines = new List<SubText.Cue>();
    for (double t = 10; t < 1700; t += 4 + rnd.NextDouble() * 9) lines.Add(new SubText.Cue(t, t + 1 + rnd.NextDouble() * 3, "x"));
    const double offset = 3.4, scale = 25 / 23.976;
    var frames = (int)(1800 / 0.02);
    var speech = new float[frames];
    for (var i = 0; i < frames; i++) speech[i] = -1;
    foreach (var c in lines)
        for (var i = (int)((c.Start * scale + offset) / 0.02); i < (int)((c.End * scale + offset) / 0.02) && i < frames; i++) speech[i] = rnd.NextDouble() < 0.85 ? 1 : -1; // pauses between words
    var r = SubSync.Match(speech, lines, frames);
    Check("subtitles: auto-sync", r != null && Math.Abs(r.Offset - offset) < 0.1 && Math.Abs(r.Scale - scale) < 0.001, r == null ? "no match" : $"{r.Offset:0.00}s ×{r.Scale:0.0000} z={r.Confidence:0}");
}

// ── fragmented MP4 (OBS, DASH) is told apart from a plain one: it gets FFmpeg's demuxer, which seeks to the keyframe
{
    static byte[] Box(string type, params byte[][] body)
    {
        var len = 8 + body.Sum(b => b.Length);
        return [.. new[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len }, .. System.Text.Encoding.ASCII.GetBytes(type), .. body.SelectMany(b => b)];
    }
    var ftyp = Box("ftyp", new byte[8]);
    string Mp4(string name, params byte[][] boxes)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(path, boxes.SelectMany(b => b).ToArray());
        return path;
    }
    var plain = Mp4("pylon-check-plain.mp4", ftyp, Box("moov", Box("mvhd", new byte[100]), Box("trak")), Box("mdat", new byte[64]));
    var mvex = Mp4("pylon-check-mvex.mp4", ftyp, Box("moov", Box("mvhd", new byte[100]), Box("mvex", Box("trex", new byte[24]))));
    var moof = Mp4("pylon-check-moof.mov", ftyp, Box("moov", Box("mvhd", new byte[100])), Box("moof", Box("mfhd", new byte[8])), Box("mdat", new byte[64]));
    Check("fragmented MP4 detected", !MainWindow.IsFragmentedMp4(plain) && MainWindow.IsFragmentedMp4(mvex) && MainWindow.IsFragmentedMp4(moof)
        && !MainWindow.IsFragmentedMp4(Path.ChangeExtension(moof, ".mkv")));
}

// ── TS / MPEG-PS: the keyframe near a seek target, from the file itself (keyframes every 2 s, 30 fps, start at 1.4 s)
{
    static byte[] PesHead(int sid, long pts)
    {
        var b = new byte[14];
        b[2] = 1; b[3] = (byte)sid; b[6] = 0x80; b[7] = 0x80; b[8] = 5;
        b[9] = (byte)(0x21 | (pts >> 29 & 0x0E)); b[10] = (byte)(pts >> 22); b[11] = (byte)(pts >> 14 | 1); b[12] = (byte)(pts >> 7); b[13] = (byte)(pts << 1 | 1);
        return b;
    }
    static byte[] TsPacket(int pid, bool start, bool rai, byte[] payload)
    {
        var p = new byte[188];
        p[0] = 0x47; p[1] = (byte)((start ? 0x40 : 0) | pid >> 8); p[2] = (byte)pid; p[3] = 0x30; // adaptation field + payload
        p[4] = 1; p[5] = (byte)(rai ? 0x40 : 0);
        payload.CopyTo(p, 6);
        for (var i = 6 + payload.Length; i < 188; i++) p[i] = 0xFF;
        return p;
    }
    var ts = new MemoryStream();
    var ps = new MemoryStream();
    for (var k = 0; k < 30 * 40; k++)
    {
        var pts = 126000 + k * 3000L;
        var key = k % 60 == 0;
        ts.Write(TsPacket(0x100, true, key, PesHead(0xE0, pts)));
        ts.Write(TsPacket(0x101, true, false, PesHead(0xC0, pts)));
        for (var j = 0; j < 20; j++) ts.Write(TsPacket(0x100, false, false, []));
        ps.Write([0, 0, 1, 0xBA, .. new byte[10]]);
        ps.Write(PesHead(0xE0, pts));
        ps.Write(key ? [0, 0, 1, 0xB3, 0x50, 0x02, 0xD0] : [0, 0, 1, 0x00, 0x10, 0x10]);
        ps.Write(new byte[3000]);
    }
    var tsFile = Path.Combine(Path.GetTempPath(), "pylon-check.ts");
    var psFile = Path.Combine(Path.GetTempPath(), "pylon-check.mpg");
    File.WriteAllBytes(tsFile, ts.ToArray());
    File.WriteAllBytes(psFile, ps.ToArray());
    foreach (var file in new[] { tsFile, psFile })
    {
        long? F(long ms, bool fwd) => Keyframes.Near(file, ms, fwd, 40_000)?.Ms;
        var got = new[] { F(5000, true), F(5000, false), F(4000, true), F(4000, false), F(39_000, true) };
        Check($"keyframes: {Path.GetExtension(file)}", got[0] == 6000 && got[1] == 4000 && got[2] == 4000 && got[3] == 4000 && got[4] == null, string.Join(" ", got.Select(g => g?.ToString() ?? "-")));
    }
}

// ── audio output: waveOut by default; its volume follows DirectSound's curve; a device saved under one output's name is found under the other's
{
    double Level(double v) { var (w, r) = AudioChain.SplitVolume(v); return w * r; } // what waveOut is told, in percent
    Check("waveOut volume matches the old curve", AudioChain.Output == "waveout" && AudioChain.SplitVolume(1) == (200, 1f) && AudioChain.SplitVolume(1.25).Whole == 200
        && Math.Abs(Level(1.25) - 390.625) < 1e-3 && Math.Abs(Level(0.25) - 3.125) < 1e-4 && AudioChain.SplitVolume(0.25).Whole == 4 && Math.Abs(Level(0.5) - 25) < 1e-4
        && AudioChain.SplitVolume(0) == (0, 1f),
        $"{AudioChain.SplitVolume(0.25)} {AudioChain.SplitVolume(0.5)}");
}
Check("devices across outputs", AudioChain.DeviceName("CABLE Input (VB-Audio Virtual C ($1,$64)") == "CABLE Input (VB-Audio Virtual C"
    && AudioChain.SameDevice("CABLE Input (VB-Audio Virtual C", "CABLE Input (VB-Audio Virtual Cable)")
    && !AudioChain.SameDevice("Speakers (Realtek(R) Audio)", "Speakers (2- USB Audio Device)"));

// ── shortcuts: every layout works and gives no key two jobs
foreach (var layout in Hotkeys.Layouts.Keys)
{
    Settings.Set("controls.defaults", layout);
    var keys = Hotkeys.Actions.SelectMany(a => Hotkeys.Gesture(a.Id).Split('|')).ToList();
    var twice = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
    Check($"shortcuts: {layout}", twice.Count == 0 && Hotkeys.Match("Space")?.Id == "play", string.Join(", ", twice));
}
Settings.Set("controls.defaults", "YouTube-style");
Check("shortcuts: YouTube J/K/L", Hotkeys.Match("K")?.Id == "play" && Hotkeys.Match("J")?.Id == "back2" && Hotkeys.Match("L")?.Id == "fwd2");
Settings.Set("controls.defaults", "VLC-style");

// ── audio: effects need the output rebuilt ("any" isn't a module: SetAudioOutput refused it and every effect stayed off)
Core.Initialize();
using (var vlc = new LibVLC("--quiet", "--no-video"))
using (var mp = new MediaPlayer(vlc))
    Check("AudioChain rebuilds the output", AudioChain.Apply(mp, null));

// ── audio: per-player gain (crossfade, measured loudness) really changes the level; afile writes what the output gets
var tone = Path.Combine(Path.GetTempPath(), "pylon-check-tone.wav");
WriteTone(tone);
double Rms(float gain)
{
    var wav = Path.Combine(Path.GetTempPath(), $"pylon-check-{gain}.wav");
    using var vlc = new LibVLC("--quiet", "--no-video", "--aout=afile", $"--audiofile-file={wav}", "--audiofile-format=float32");
    using var mp = new MediaPlayer(vlc);
    AudioChain.SetGain(mp, gain);
    using var media = new Media(vlc, tone, FromType.FromPath);
    var done = new ManualResetEventSlim();
    mp.EndReached += (_, _) => done.Set();
    mp.Play(media);
    done.Wait(20_000);
    mp.Stop();
    var b = File.ReadAllBytes(wav);
    var data = b.AsSpan(b.Length - 4 * 44100); // the last second
    double sum = 0;
    for (var i = 0; i < data.Length; i += 4) { double v = BitConverter.ToSingle(data[i..(i + 4)]); sum += v * v; }
    return Math.Sqrt(sum / (data.Length / 4));
}
var full = Rms(1f);
var quarter = Rms(0.25f);
Check("per-player gain", full > 0.01 && Math.Abs(quarter / full - 0.25) < 0.05, $"{quarter / full:0.000}");

// ── audio: effects switch on while playing, without an engine restart (karaoke cancels a centered tone).
// Captured through callbacks: the afile output restarts with the filter chain and rewrites its file.
{
    using var vlc = new LibVLC("--quiet", "--no-video");
    using var mp = new MediaPlayer(vlc);
    var pcm = new List<float>();
    mp.SetAudioFormat("S16N", 44100, 2); // amem's float path hands out garbage
    mp.SetAudioCallbacks((_, samples, count, _) =>
    {
        var buf = new short[count * 2];
        System.Runtime.InteropServices.Marshal.Copy(samples, buf, 0, buf.Length);
        lock (pcm) pcm.AddRange(buf.Select(v => v / 32768f));
    }, null, null, null, null);
    using var media = new Media(vlc, tone, FromType.FromPath);
    Settings.Set("audio.karaoke", "false");
    mp.Play(media);
    Thread.Sleep(1200);
    int switchedAt;
    lock (pcm) switchedAt = pcm.Count;
    Settings.Set("audio.karaoke", "true");
    var live = AudioChain.ApplyLive(mp);
    Thread.Sleep(1200);
    mp.Stop();
    Settings.Set("audio.karaoke", "false");
    float[] all;
    lock (pcm) all = [.. pcm];
    double Level(int from, int to) => from < to ? Math.Sqrt(all.Skip(from).Take(to - from).Average(v => (double)v * v)) : 0;
    var before = Level(switchedAt - 22050, switchedAt);   // the quarter second before the switch
    var after = Level(all.Length - 22050, all.Length);   // the last quarter second
    Check("effects apply live", live && before > 0.05 && after < before * 0.1, $"{before:0.000} -> {after:0.000}");

    // a slider move (equalizer preamp) reaches the running filter through its callback: no chain rebuild, no gap
    lock (pcm) pcm.Clear();
    Settings.Set("audio.eq", "true");
    Settings.Set("fx.eq.preamp", "0");
    mp.Play(media);
    AudioChain.ApplyLive(mp);
    Thread.Sleep(1000);
    lock (pcm) switchedAt = pcm.Count;
    Settings.Set("fx.eq.preamp", "-20");
    var rebuilt = AudioChain.ApplyLive(mp) && AudioChain.LastRebuilt;
    Thread.Sleep(800);
    mp.Stop();
    Settings.Set("audio.eq", "false");
    Settings.Set("fx.eq.preamp", "0");
    lock (pcm) all = [.. pcm];
    before = Level(switchedAt - 22050, switchedAt);
    after = Level(all.Length - 22050, all.Length);
    Check("slider changes without a rebuild", !rebuilt && before > 0.05 && after < before * 0.3, $"{before:0.000} -> {after:0.000}, rebuilt={rebuilt}");
}

// ── audio: seeking keeps playback in sync. With libVLC resampling (mmdevice at 48 kHz + speex) every seek threw the drift
// correction into a spiral of dropped buffers; DirectSound takes the file's rate. Needs a silent output (VB-Audio CABLE).
{
    var warn = 0;
    using var vlc = new LibVLC([.. Settings.EngineArgs(), "--no-video"]);
    vlc.Log += (_, e) => { if (e.Message.Contains("too late") || e.Message.Contains("dropped")) Interlocked.Increment(ref warn); };
    using var mp = new MediaPlayer(vlc);
    AudioChain.Apply(mp, null);
    var cable = mp.AudioOutputDeviceEnum.FirstOrDefault(d => d.Description.Contains("CABLE Input"));
    if (cable.DeviceIdentifier == null) Console.WriteLine("skip seeking keeps sync (no silent output device here)");
    else
    {
        mp.SetOutputDevice(cable.DeviceIdentifier);
        var longTone = Path.Combine(Path.GetTempPath(), "pylon-check-long.wav");
        WriteTone(longTone, 30);
        using var media = new Media(vlc, longTone, FromType.FromPath);
        mp.Play(media);
        Thread.Sleep(2000);
        for (var k = 1; k <= 3; k++) { mp.Time = k * 6000; Thread.Sleep(2500); }
        mp.Stop();
        Check("seeking keeps sync", warn < 10, $"{warn} sync errors over 3 seeks");
    }
}

// ── own video engine: the picture fits the window with its pixel aspect; the color matrix follows tags, else the size
{
    Check("letterbox", VideoEngine.Letterbox(1920, 1080, 1, 1, 1000, 1000) == (0, 219, 1000, 562) && VideoEngine.Letterbox(1080, 1920, 0, 0, 1000, 500) == (359, 0, 281, 500)
        && VideoEngine.Letterbox(720, 576, 16, 11, 1048, 576) == (0, 0, 1048, 576), $"{VideoEngine.Letterbox(720, 576, 16, 11, 1048, 576)}");
    Check("sound gaps: rounding ignored, holes filled, jumps move the timeline", VideoEngine.PlaceGap(5_000, false, 48000) == (0, 0) && VideoEngine.PlaceGap(-50_000, false, 48000) == (0, 0)
        && VideoEngine.PlaceGap(100_000, false, 48000) == (0, 4800) && VideoEngine.PlaceGap(100_000, true, 48000) == (100_000, 0)
        && VideoEngine.PlaceGap(20_000_000, false, 48000) == (20_000_000, 0) && VideoEngine.PlaceGap(-300_000, false, 48000) == (-300_000, 0));
    EngineTrack[] langs = [new(1, "a", "eng", false, false), new(2, "b", "ger", true, false), new(3, "c", "rus", false, true), new(4, "d", "und", false, false)];
    Check("tracks: number, then language (ISO codes of any kind), then forced or default; none otherwise", VideoEngine.PickTrack(langs, 2, "ru", true) == 2 && VideoEngine.PickTrack(langs, 0, "ru", true) == 3
        && VideoEngine.PickTrack(langs, 0, "de", false) == 2 && VideoEngine.PickTrack(langs, 0, "ja", true) == 3 && VideoEngine.PickTrack(langs, 0, "ja", false) == -1 && VideoEngine.PickTrack(langs[..2], 0, "ja", true) == 2 && VideoEngine.PickTrack(langs, 99, "en", false) == 1
        && VideoEngine.TwoLetters("deu") == "de" && VideoEngine.TwoLetters("und") == null);
    Check("subtitle text out of an ASS event", VideoEngine.AssText(@"3,0,Default,,0,0,0,,{\i1}Hello, there{\i0}\Nsecond\hline") == "Hello, there\nsecond line", VideoEngine.AssText(@"3,0,Default,,0,0,0,,{\i1}Hello, there{\i0}\Nsecond\hline"));
    Check("seek preview keeps the picture's shape", SeekPreviewer.Fit(1920, 1080) == (256, 144) && SeekPreviewer.Fit(1080, 1920) == (144, 256) && SeekPreviewer.Fit(1000, 1000) == (256, 256) && SeekPreviewer.Fit(0, 0) == (256, 144), $"{SeekPreviewer.Fit(1080, 1920)}");
    var mp3 = Converter.Presets[0];
    var cut = string.Join(" ", Converter.Args("a.mkv", "b.mp3", mp3, 1500, 5000));
    Check("conversion: a cut's times come before the input, none without a cut", cut.StartsWith("-hide_banner -nostdin -y -ss 1.5 -to 5 -i a.mkv ") && cut.EndsWith(" b.mp3") && !string.Join(" ", Converter.Args("a.mkv", "b.mp3", mp3, -1, -1)).Contains("-ss"), cut);
    Check("trim times read and written", Converter.ParseTime("1:23") == 83000 && Converter.ParseTime("1:02:03") == 3723000 && Converter.ParseTime("83") == 83000 && Converter.ParseTime("1:23.5") == 83500 && Converter.ParseTime("1,5") == 1500 && Converter.ParseTime("abc") == -1 && Converter.ParseTime("1:2:3:4") == -1
        && Converter.FormatTime(83000) == "1:23" && Converter.FormatTime(83500) == "1:23.5" && Converter.FormatTime(3723000) == "1:02:03", Converter.FormatTime(83500));
    const FFmpeg.AutoGen.AVColorSpace none = FFmpeg.AutoGen.AVColorSpace.AVCOL_SPC_UNSPECIFIED, bt709 = FFmpeg.AutoGen.AVColorSpace.AVCOL_SPC_BT709, bt601 = FFmpeg.AutoGen.AVColorSpace.AVCOL_SPC_SMPTE170M, bt2020 = FFmpeg.AutoGen.AVColorSpace.AVCOL_SPC_BT2020_NCL;
    const FFmpeg.AutoGen.AVColorRange tv = FFmpeg.AutoGen.AVColorRange.AVCOL_RANGE_MPEG, pc = FFmpeg.AutoGen.AVColorRange.AVCOL_RANGE_JPEG;
    const FFmpeg.AutoGen.AVColorTransferCharacteristic sdr = FFmpeg.AutoGen.AVColorTransferCharacteristic.AVCOL_TRC_BT709, pq = FFmpeg.AutoGen.AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
    Check("color space from tags and size", VideoEngine.PickColorSpace(none, tv, sdr, 1920, 1080) == Vortice.DXGI.ColorSpaceType.YcbcrStudioG22LeftP709
        && VideoEngine.PickColorSpace(none, tv, sdr, 720, 480) == Vortice.DXGI.ColorSpaceType.YcbcrStudioG22LeftP601
        && VideoEngine.PickColorSpace(bt709, tv, sdr, 720, 480) == Vortice.DXGI.ColorSpaceType.YcbcrStudioG22LeftP709
        && VideoEngine.PickColorSpace(bt601, pc, sdr, 1920, 1080) == Vortice.DXGI.ColorSpaceType.YcbcrFullG22LeftP601
        && VideoEngine.PickColorSpace(bt2020, tv, pq, 3840, 2160) == Vortice.DXGI.ColorSpaceType.YcbcrStudioG2084LeftP2020);
}

// ── offline songs carry their names and cover in the file (m4a / webm too when PYLON_FFMPEG can make them)
{
    var dir = Directory.CreateTempSubdirectory("pylon-tags").FullName;
    var cover = Path.Combine(dir, "c.jpg");
    File.WriteAllBytes(cover, [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, .. "JFIF"u8, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9]);
    List<string> files = [Path.Combine(dir, "a.wav")];
    WriteTone(files[0], 1);
    if (Environment.GetEnvironmentVariable("PYLON_FFMPEG") is { } ff)
        foreach (var (ext, codec) in new[] { ("m4a", "aac"), ("webm", "libopus") })
        {
            var f = Path.Combine(dir, "a." + ext);
            System.Diagnostics.Process.Start(ff, ["-v", "error", "-y", "-i", files[0], "-c:a", codec, f])!.WaitForExit();
            files.Add(f);
        }
    foreach (var f in files)
    {
        await MainWindow.WriteTags(f, "Песня", "Артист", "Альбом", cover);
        using var t = TagLib.File.Create(f);
        Check($"offline tags in .{Path.GetExtension(f)[1..]}", t.Tag.Title == "Песня" && t.Tag.FirstPerformer == "Артист" && (t.Tag.Album == "Альбом" || f.EndsWith(".webm")) && t.Tag.Pictures.Length == 1, $"{t.Tag.Title}|{t.Tag.FirstPerformer}|{t.Tag.Album}|{t.Tag.Pictures.Length}");
    }
}

{
    var clean = BugReport.Sanitize("file C:\\Users\\private\\song.mp3\nhttps://user:pass@example.com/music?token=hidden\nAuthorization: Bearer hidden\n\\\\server\\private\\song.mp3");
    Check("report preview hides paths and credentials", !clean.Contains("private") && !clean.Contains("hidden") && !clean.Contains("pass"));
    Check("partial playback only reports decoder failures", PlaybackDiagnostics.HasDecoderFailure(["no suitable decoder module"])
        && !PlaybackDiagnostics.HasDecoderFailure(["buffering", "late picture"]));
    var result = await Recognize.IdentifyDetailedAsync(Path.Combine(Library.Dir, "absent.mp3"));
    Check("recognition distinguishes missing input from no match", result.Song == null && result.Message.Contains("local audio file"));
    foreach (var (name, load) in new (string, Action)[] { ("settings.json", Settings.Load), ("library.json", Library.Load) })
    {
        var path = Path.Combine(Library.Dir, name);
        var previous = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path, "{broken");
            var rejected = false;
            try { load(); } catch (System.Text.Json.JsonException) { rejected = true; }
            Check("profile preserves invalid " + name, rejected && File.ReadAllText(path) == "{broken");
        }
        finally { if (previous == null) File.Delete(path); else File.WriteAllText(path, previous); }
    }
}
if (Environment.GetEnvironmentVariable("PYLON_FFMPEG") is { } realFfmpeg)
{
    var dir = Directory.CreateDirectory(Path.Combine(Library.Dir, "join-checks")).FullName;
    var source = Path.Combine(dir, "part.wav");
    WriteTone(source, 1);
    foreach (var preset in Converter.Presets.Where(p => p.Extension is ".mp3" or ".flac"))
    {
        var dst = Path.Combine(dir, "joined" + preset.Extension);
        var result = await Ffmpeg.JoinAsync([source, source], dst, preset, null, CancellationToken.None);
        using var media = result.Ok ? TagLib.File.Create(dst) : null;
        Check("join respects preset " + preset.Extension, result.Ok && media?.Properties.Duration.TotalSeconds > 1.8, result.Error ?? "");
    }
    var existing = Path.Combine(dir, "existing.wav");
    File.WriteAllText(existing, "keep my original");
    var wav = Converter.Presets.First(p => p.Extension == ".wav");
    var collision = await Converter.RunAsync(source, existing, wav);
    Check("conversion never overwrites an existing result", !collision.Ok && File.ReadAllText(existing) == "keep my original");
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    var cancelled = await Ffmpeg.RunAsync(Converter.Args(source, existing, wav, -1, -1), existing, 1000, null, cancel.Token);
    Check("cancelled conversion preserves existing data", !cancelled.Ok && File.ReadAllText(existing) == "keep my original"
        && !Directory.EnumerateFiles(dir, ".pylon-*").Any());
}

// Hold opening before it reaches D3D: exercise late events without creating a GPU device or playing sound.
{
    using var vlc = new LibVLC("--quiet", "--no-video", "--no-audio");
    using var player = new Player(vlc);
    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var closing = typeof(Player).GetField("_closing", flags)!;
    var active = typeof(Player).GetField("_engine", flags)!;
    var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    closing.SetValue(player, blocked.Task);
    var started = 0;
    var ended = 0;
    player.OwnPlaying += _ => started++;
    player.OwnEnded += _ => ended++;
    var choice = new TrackChoice(0, "en", 0, "en", false);
    player.PlayOwn("old.mp4", 0, false, null, choice, [], 0);
    var old = (VideoEngine)active.GetValue(player)!;
    var generation = player.OwnGeneration;
    player.PlayOwn("new.mp4", 0, false, null, choice, [], 0);
    ((Action?)typeof(VideoEngine).GetField("FirstFrame", flags)!.GetValue(old))?.Invoke();
    ((Action?)typeof(VideoEngine).GetField("Ended", flags)!.GetValue(old))?.Invoke();
    Check("late engine events cannot change the new track", started == 0 && ended == 0 && player.State == VLCState.Opening
        && player.OwnGeneration != generation);
    player.Stop();
    blocked.SetResult();
    await ((Task)closing.GetValue(player)!).WaitAsync(TimeSpan.FromSeconds(10));
    Check("cancelled queued openings are disposed without opening video", !player.OnOwnEngine && old.Presented == 0);
}

{
    var a = Path.Combine(Library.Dir, "match-a.mp3");
    var b = Path.Combine(Library.Dir, "match-b.mp3");
    File.WriteAllText(a, ""); File.WriteAllText(b, "");
    Library.Data.Meta[a] = new TrackMeta { Title = "Song", Artist = "Joanna", DurationMs = 180000 };
    Library.Data.Meta[b] = new TrackMeta { Title = "Song", Artist = "Ann", DurationMs = 240000, Isrc = "ISRC-B" };
    Library.Touch();
    Check("local song does not match a substring artist", Library.FindSong("Song", "Ann", 180000) == null);
    Check("local song checks recording duration", Library.FindSong("Song", "Ann", 240000) == b && Library.FindSong("Song", "Ann", 300000) == null);
    Check("local song rejects a different ISRC", Library.FindSong("Song", "Ann", 240000, "ISRC-C") == null);
    Library.Data.Meta[b].Title = "Renamed";
    Library.Touch();
    Check("retagging refreshes the song index without changing its size", Library.FindSong("Renamed", "Ann", 240000) == b && Library.FindSong("Song", "Ann", 240000) == null);
    Check("empty artist never matches an arbitrary recording", Library.FindSong("Song", "") == null);
    Library.Data.Meta.Remove(a); Library.Data.Meta.Remove(b); Library.Touch();
    Check("missing selected output differs from system default", AudioOutputWatch.IsMissing("Headphones", ["Speakers"])
        && !AudioOutputWatch.IsMissing("Headphones", ["Headphones"]) && !AudioOutputWatch.IsMissing("System default", []));
}
{
    var path = Path.Combine(Library.Dir, "settings.json");
    var previous = File.Exists(path) ? File.ReadAllText(path) : null;
    try
    {
        for (var i = 0; i < 5; i++) { File.WriteAllText(path, "{\"probe\":\"" + i + "\"}"); ProfileBackups.BeforeReplace(path, force: true); }
        var backup = ProfileBackups.Latest("settings.json")!;
        var saved = File.ReadAllText(backup);
        File.WriteAllText(path, "{broken original");
        ProfileBackups.Restore("settings.json", backup);
        Check("backup restore keeps the damaged original", File.ReadAllText(path) == saved
            && Directory.EnumerateFiles(Library.Dir, "settings.json.unreadable-*").Any(f => File.ReadAllText(f) == "{broken original"));
        Check("profile backups are bounded", Directory.EnumerateFiles(Path.Combine(Library.Dir, "backups"), "settings-*.json").Count() == 3);
        File.WriteAllText(backup, "{bad backup");
        var rejected = false;
        try { ProfileBackups.Restore("settings.json", backup); } catch (System.Text.Json.JsonException) { rejected = true; }
        Check("invalid backup cannot replace a readable profile", rejected && File.ReadAllText(path) == saved);
    }
    finally { if (previous == null) File.Delete(path); else File.WriteAllText(path, previous); }
}
{
    var attempts = 0;
    var job = BackgroundJobs.Start("retry fixture", async (j, ct) => { await Task.Yield(); if (++attempts == 1) throw new IOException("fixture failure"); j.Report(0.5); return "result"; });
    await job.Completion;
    Check("background failure remains retryable", job.State == JobState.Failed);
    job.Retry(); await job.Completion;
    job.Report(0.1);
    Check("background retry completes without stale progress", attempts == 2 && job.State == JobState.Completed && job.Progress == 1 && job.Output == "result");
    var waiting = BackgroundJobs.Start("cancel fixture", async (j, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });
    waiting.Cancel(); await waiting.Completion;
    Check("background cancellation is not a failure", waiting.State == JobState.Cancelled);
}
if (Environment.GetEnvironmentVariable("PYLON_FFMPEG") is { } ffmpegExe)
{
    var dir = Directory.CreateDirectory(Path.Combine(Library.Dir, "normalize-checks")).FullName;
    var a = Path.Combine(dir, "44k.wav"); var b = Path.Combine(dir, "48k.wav");
    WriteTone(a, 1); WriteTone(b, 1, 48000);
    var dst = Path.Combine(dir, "normalized.flac");
    var joined = await Ffmpeg.JoinAsync([a, b], dst, Converter.Presets.First(p => p.Extension == ".flac"), null, CancellationToken.None);
    using (var f = joined.Ok ? TagLib.File.Create(dst) : null)
        Check("join normalizes different audio sample rates", joined.Ok && f?.Properties.Duration.TotalSeconds > 1.8 && f.Properties.AudioSampleRate == 44100, joined.Error ?? "");
    var copy = await Ffmpeg.JoinAsync([a, b], Path.Combine(dir, "copy.wav"), Converter.Presets.First(p => p.Extension == ""), null, CancellationToken.None);
    Check("incompatible lossless stream copy is explained before writing", !copy.Ok && copy.Error?.Contains("matching codecs") == true && !File.Exists(Path.Combine(dir, "copy.wav")));
    var videos = new List<string>();
    foreach (var (ext, codec, size, fps) in new[] { ("mp4", "libx264", "32x32", "10"), ("avi", "mpeg4", "64x48", "15") })
    {
        var file = Path.Combine(dir, "source." + ext);
        var made = await Ffmpeg.RunAsync(["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", $"color=c=black:s={size}:r={fps}:d=1", "-c:v", codec, "-pix_fmt", "yuv420p", "-progress", "pipe:1", file], file, 1000, null, CancellationToken.None);
        Check("video join fixture " + ext, made.Ok, made.Error ?? "");
        videos.Add(file);
    }
    var videoOut = Path.Combine(dir, "normalized.mp4");
    var video = await Ffmpeg.JoinAsync(videos, videoOut, Converter.Presets.First(p => p.Name == "MP4 (H.264 + AAC)"), null, CancellationToken.None);
    using (var f = video.Ok ? TagLib.File.Create(videoOut) : null)
        Check("join normalizes video codec size and frame rate", video.Ok && f?.Properties.VideoWidth == 32 && f.Properties.Duration.TotalSeconds > 1.8, video.Error ?? "");
    var voiced = Path.Combine(dir, "with-audio.mp4");
    var voicedMade = await Ffmpeg.RunAsync(["-hide_banner", "-nostdin", "-y", "-i", videos[1], "-i", a,
        "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-shortest", "-progress", "pipe:1", voiced], voiced, 1000, null, CancellationToken.None);
    var mixed = Path.Combine(dir, "silent-and-voiced.mp4");
    var mixedJoin = voicedMade.Ok ? await Ffmpeg.JoinAsync([videos[0], voiced], mixed,
        Converter.Presets.First(p => p.Name == "MP4 (H.264 + AAC)"), null, CancellationToken.None) : voicedMade;
    var pcm = Path.Combine(dir, "joined-audio.f32");
    var decoded = mixedJoin.Ok ? await Ffmpeg.RunAsync(["-hide_banner", "-nostdin", "-y", "-i", mixed, "-vn", "-c:a", "pcm_f32le", "-f", "f32le", "-progress", "pipe:1", pcm], pcm, 2000, null, CancellationToken.None) : mixedJoin;
    double Quiet = 1, Sound = 0;
    if (decoded.Ok)
    {
        var bytes = File.ReadAllBytes(pcm);
        double Level(double seconds)
        {
            var from = (int)(seconds * 44100 * 2);
            var to = Math.Min(bytes.Length / 4, from + 4410 * 2);
            double energy = 0;
            for (var i = from; i < to; i++) { var value = BitConverter.ToSingle(bytes, i * 4); energy += value * value; }
            return to > from ? Math.Sqrt(energy / (to - from)) : 0;
        }
        Quiet = Level(0.2); Sound = Level(1.4);
    }
    Check("joining a silent and voiced video fills only the missing audio", decoded.Ok && Quiet < 0.001 && Sound > 0.05,
        $"silent={Quiet:0.0000} sound={Sound:0.0000} {decoded.Error}");
    using var stop = new CancellationTokenSource(700);
    var active = Path.Combine(dir, "cancelled.wav");
    var result = await Ffmpeg.RunAsync(["-hide_banner", "-nostdin", "-y", "-re", "-stream_loop", "-1", "-i", a, "-c:a", "pcm_s16le", "-progress", "pipe:1", active], active, 30000, null, stop.Token);
    Check("active conversion cancellation removes its partial result", !result.Ok && !File.Exists(active)
        && !Directory.EnumerateFiles(dir, ".pylon-*").Any() && !Directory.EnumerateDirectories(dir, ".pylon-join-*").Any());
}

{
    QueueSession.Save(["first.wav", "https://example.com/music"], 1, 12345);
    var saved = QueueSession.Load();
    Check("queue session keeps the selection and playback position", saved is { Index: 1, Position: 12345 } && saved.Items.SequenceEqual(new[] { "first.wav", "https://example.com/music" }));
    File.WriteAllText(Path.Combine(Library.Dir, "queue.json"), "{broken");
    Check("damaged queue does not block startup", QueueSession.Load() == null);
    QueueSession.Save([], -1, 0);
    Check("a cleared queue stays empty on restart", QueueSession.Load() == null);
}

Check("isolated data profiles cannot alter the Windows shell", !ShellRegistration.CanWriteShell);
{
    var data = Environment.GetEnvironmentVariable("PYLON_DATA");
    var probe = Environment.GetEnvironmentVariable("PYLON_VELOPACK_TEST");
    try
    {
        Environment.SetEnvironmentVariable("PYLON_VELOPACK_TEST", "fixture");
        Environment.SetEnvironmentVariable("PYLON_DATA", null);
        Check("installer probes cannot alter the Windows shell", !ShellRegistration.CanWriteShell);
    }
    finally { Environment.SetEnvironmentVariable("PYLON_DATA", data); Environment.SetEnvironmentVariable("PYLON_VELOPACK_TEST", probe); }
}

// Audit follow-up: preserve state across JSON reload and accept valid compact JSON in the startup fast path.
{
    const string path = @"C:\Music\Track.mp3";
    Library.Data.LyricFiles[path] = "picked.lrc";
    Library.Data.LyricOffsets[path] = 1.5;
    Library.Data.IntroEnds[path] = 1234;
    Library.Data.Bookmarks[path] = [new Bookmark { Time = 9876 }];
    Library.Save();
    Library.Load();
    var lower = path.ToLowerInvariant();
    Check("all persisted path maps ignore Windows path casing", Library.Data.LyricFiles[lower] == "picked.lrc"
        && Library.Data.LyricOffsets[lower] == 1.5 && Library.Data.IntroEnds[lower] == 1234 && Library.Data.Bookmarks[lower][0].Time == 9876);
    Check("single instance settings accept compact and formatted JSON", SingleInstance.SingleInstanceDisabled("{\"library.single\":\"false\"}", false)
        && SingleInstance.SingleInstanceDisabled("{\"library.singlefm\" : \"false\"}", true)
        && !SingleInstance.SingleInstanceDisabled("{broken", false) && !SingleInstance.SingleInstanceDisabled("{}", true));
}
{
    var malformed = Lyrics.ParseLrc("[offset:2147483648]\n[2147483648:00]bad\n[00:01]<999999999999:00>bad<00:02>good", "fixture");
    Check("malformed LRC numbers do not abort loading", malformed?.Lines.Length == 1 && malformed.Lines[0].Start == 1
        && malformed.Lines[0].Words.All(w => double.IsFinite(w.Start) && double.IsFinite(w.End)));
    Check("large LRC minutes do not overflow integer multiplication", Lyrics.ParseLrc("[40000000:01]long", "fixture")?.Lines[0].Start == 2_400_000_001);
    Check("own engine byte budget has a fixed ceiling", !VideoEngine.PacketBufferFull(32L * 1024 * 1024 - 1)
        && VideoEngine.PacketBufferFull(32L * 1024 * 1024) && VideoEngine.PacketBufferFull(0, 4096));
}
{
    using var vlc = new LibVLC("--quiet", "--no-video", "--no-audio");
    using var player = new Player(vlc);
    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var add = typeof(Player).GetMethod("AddCue", flags)!;
    for (var i = 0; i < 5; i++)
    {
        add.Invoke(player, [0, 1.0, 2.0, "A"]);
        add.Invoke(player, [0, 1.0, 2.0, "B"]);
    }
    var cues = (Dictionary<int, List<SubText.Cue>>)typeof(Player).GetField("_cues", flags)!.GetValue(player)!;
    Check("seeking does not duplicate simultaneous subtitle cues", cues[0].Count == 2);
}
{
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var jobs = Enumerable.Range(0, 40).Select(i => BackgroundJobs.Start("batch " + i, async (_, ct) =>
    { await gate.Task.WaitAsync(ct); throw new IOException("retry fixture"); })).ToArray();
    gate.SetResult();
    await Task.WhenAll(jobs.Select(j => j.Completion));
    Check("finished batch releases background history beyond its limit", BackgroundJobs.All.Count <= 30);
    var removed = jobs.First(j => !BackgroundJobs.All.Contains(j));
    var completion = removed.Completion;
    removed.Retry();
    Check("discarded background jobs release their retry closure", ReferenceEquals(completion, removed.Completion));
}
{
    var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    server.Start();
    var port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;
    using var stopServer = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var serving = Task.Run(async () =>
    {
        using var client = await server.AcceptTcpClientAsync(stopServer.Token);
        await using var stream = client.GetStream();
        var buf = new byte[4096];
        await stream.ReadAsync(buf, stopServer.Token);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\nConnection: close\r\n\r\nx"u8.ToArray(), stopServer.Token);
        bodyStarted.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, stopServer.Token); } catch (OperationCanceledException) { }
    });
    try
    {
        using var http = new System.Net.Http.HttpClient();
        using var cancel = new CancellationTokenSource();
        var downloading = Ffmpeg.DownloadAsync(http, $"http://127.0.0.1:{port}/tool", Path.Combine(Library.Dir, "partial-tool.zip"), null, cancel.Token);
        await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        var cancelled = false;
        try { await downloading.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { cancelled = true; }
        Check("tool download can be cancelled during a stalled response body", cancelled);
    }
    finally { stopServer.Cancel(); server.Stop(); await serving; }
    using var cancelledLookup = new CancellationTokenSource();
    cancelledLookup.Cancel();
    var stopped = false;
    try { await Lyrics.FindDetailedAsync("fixture.mp3", "Fixture", "Artist", null, 1, cancel: cancelledLookup.Token); }
    catch (OperationCanceledException) { stopped = true; }
    Check("superseded lyrics lookup stops before any provider request", stopped);
}

{
    var proxy = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    proxy.Start();
    var port = ((System.Net.IPEndPoint)proxy.LocalEndpoint).Port;
    var oldProxy = Settings.Str("net.proxy");
    var oldMode = Settings.Str("net.proxymode");
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var upstream = Task.Run(async () =>
    {
        using var client = await proxy.AcceptTcpClientAsync(stop.Token);
        await using var stream = client.GetStream();
        var block = new byte[4096];
        await stream.ReadAsync(block, stop.Token);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\nConnection: close\r\n\r\nx"u8.ToArray(), stop.Token);
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
    });
    try
    {
        Settings.Set("net.proxymode", "Manual");
        Settings.Set("net.proxy", $"http://127.0.0.1:{port}");
        using var http = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler { UseProxy = false });
        using var response = await http.GetAsync(StreamRelay.Wrap("http://fixture.invalid/media", null, null),
            System.Net.Http.HttpCompletionOption.ResponseHeadersRead, stop.Token);
        var reading = response.Content.ReadAsByteArrayAsync(stop.Token);
        StreamRelay.Reset();
        var stopped = false;
        try { await reading.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException or OperationCanceledException) { stopped = true; }
        Check("stopping playback closes stalled relay response bodies", stopped);
    }
    finally
    {
        StreamRelay.Reset(); Settings.Set("net.proxy", oldProxy); Settings.Set("net.proxymode", oldMode);
        stop.Cancel(); proxy.Stop(); await upstream;
    }
    var requests = (SemaphoreSlim)typeof(Lyrics).GetField("Requests", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
    await requests.WaitAsync(); await requests.WaitAsync();
    try
    {
        using var cancel = new CancellationTokenSource(100);
        var stopped = false;
        try { await Lyrics.FindDetailedAsync("fixture.mp3", "Fixture", "Artist", null, 1, cancel: cancel.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check("queued lyrics work can be cancelled without taking a provider slot", stopped && requests.CurrentCount == 0);
    }
    finally { requests.Release(2); }
}

{
    var path = Path.Combine(Library.Dir, "lyrics-cache-fixture.lrc");
    File.WriteAllText(path, "old complete lyrics");
    var write = typeof(Lyrics).GetMethod("WriteCacheAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    try { await (Task)write.Invoke(null, [path, "partial replacement", cancel.Token])!; } catch (OperationCanceledException) { }
    Check("cancelled lyrics cache replacement preserves the complete document", File.ReadAllText(path) == "old complete lyrics"
        && !Directory.EnumerateFiles(Library.Dir, "lyrics-cache-fixture.lrc.*.tmp").Any());
}

Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) failed");
return failed == 0 ? 0 : 1;

static void WriteTone(string path, int seconds = 3, int rate = 44100) // a 440 Hz sine, 16-bit stereo
{
    using var w = new BinaryWriter(File.Create(path));
    int bytes = rate * seconds * 4;
    w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
    w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
    for (var i = 0; i < rate * seconds; i++)
    {
        var s = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000);
        w.Write(s); w.Write(s);
    }
}
