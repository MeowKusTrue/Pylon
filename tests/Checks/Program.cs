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
var lrc = Lyrics.ParseLrc("[00:01.00]Hello\n[00:01.00]Привет\n[00:04.00]Next\n", "l")!;
Check("LRC bilingual", lrc.Lines.Length == 2 && lrc.Lines[0].Translation == "Привет" && lrc.Lines[0].End == 4, $"{lrc.Lines[0].Start}-{lrc.Lines[0].End}");

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

Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) failed");
return failed == 0 ? 0 : 1;

static void WriteTone(string path) // 3 s of a 440 Hz sine, 16-bit stereo
{
    const int rate = 44100, seconds = 3;
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
