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

Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) failed");
return failed == 0 ? 0 : 1;

static void WriteTone(string path, int seconds = 3) // a 440 Hz sine, 16-bit stereo
{
    const int rate = 44100;
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
