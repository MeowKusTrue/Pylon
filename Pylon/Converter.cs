using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>A conversion target: a libVLC stream-output chain and the file extension it writes.</summary>
/// <param name="Chain">sout chain without the destination; "{dst}" is replaced by the output path.</param>
public record ConvertPreset(string Name, string Extension, bool Video, string Chain);

/// <summary>
/// Converts files with libVLC's stream output on a separate engine (like the waveform): transcode → file, faster than
/// real time. Also cuts clips (start / stop time) and copies streams without re-encoding.
/// </summary>
public static class Converter
{
    // encoders in the bundled libVLC (checked): MP3 (lame), AAC, FLAC, Opus, PCM, MPEG-4 part 2, GIF; no VP8 encoder and
    // H.264 only as 10-bit x264, which phones and TVs often can't play
    public static readonly ConvertPreset[] Presets =
    [
        new("MP3 (320 kbps)", ".mp3", false, "#transcode{vcodec=none,acodec=mp3,ab=320,channels=2,samplerate=44100}:std{access=file,mux=raw,dst='{dst}'}"), // "mpga" would be MP2
        new("M4A (AAC 256 kbps)", ".m4a", false, "#transcode{vcodec=none,acodec=mp4a,ab=256,channels=2}:std{access=file,mux=mp4,dst='{dst}'}"),
        new("FLAC (lossless)", ".flac", false, "#transcode{vcodec=none,acodec=flac}:std{access=file,mux=raw,dst='{dst}'}"),
        new("Opus (160 kbps)", ".opus", false, "#transcode{vcodec=none,acodec=opus,ab=160,channels=2,samplerate=48000}:std{access=file,mux=ogg,dst='{dst}'}"),
        new("WAV", ".wav", false, "#transcode{vcodec=none,acodec=s16l,channels=2}:std{access=file,mux=wav,dst='{dst}'}"),
        new("MP4 (MPEG-4 + AAC)", ".mp4", true, "#transcode{vcodec=mp4v,vb=6000,acodec=mp4a,ab=192,channels=2}:std{access=file,mux=mp4,dst='{dst}'}"),
        // the GIF encoder always writes 50 ms frame delays, so anything but 20 fps plays at the wrong speed
        new("GIF (400 px)", ".gif", true, "#transcode{vcodec=gif,width=400,fps=20,acodec=none}:std{access=file,mux=raw,dst='{dst}'}"),
        new("Copy streams (fast)", "", true, "#std{access=file,mux={mux},dst='{dst}'}"),
    ];

    static LibVLC? _vlc;

    /// <summary>Runs one conversion. <paramref name="startMs"/> / <paramref name="stopMs"/> cut a clip (-1 = whole file).</summary>
    /// <returns>True when the output file was written.</returns>
    public static Task<bool> RunAsync(string src, string dst, ConvertPreset preset, long startMs = -1, long stopMs = -1,
        IProgress<double>? progress = null, CancellationToken cancel = default) => Task.Run(() =>
    {
        _vlc ??= new LibVLC("--quiet", "--no-video-title-show", "--no-sout-display", "--sout-keep");
        var ext = Path.GetExtension(dst).ToLowerInvariant();
        var mux = ext switch { ".mp4" or ".m4v" or ".mov" => "mp4", ".mkv" or ".webm" => "mkv", ".ogg" or ".ogv" => "ogg", _ => "ts" };
        var chain = preset.Chain.Replace("{dst}", dst.Replace("'", "")).Replace("{mux}", mux);
        using var media = src.Contains("://") ? new Media(_vlc, new Uri(src)) : new Media(_vlc, src, FromType.FromPath);
        media.AddOption(":sout=" + chain);
        if (startMs >= 0) media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        if (stopMs > 0) media.AddOption(":stop-time=" + (stopMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        using var player = new MediaPlayer(media);
        var done = new ManualResetEventSlim();
        var ok = true;
        player.EndReached += (_, _) => done.Set();
        player.EncounteredError += (_, _) => { ok = false; done.Set(); };
        if (progress != null)
            player.PositionChanged += (_, e) =>
            {
                // positions are of the whole file; a clip reports its share
                var len = player.Length;
                if (len <= 0) return;
                var from = startMs >= 0 ? startMs : 0;
                var to = stopMs > 0 ? Math.Min(stopMs, len) : len;
                progress.Report(Math.Clamp((e.Position * len - from) / Math.Max(1, to - from), 0, 1));
            };
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (!player.Play()) return false;
        while (!done.Wait(200))
            if (cancel.IsCancellationRequested) { ok = false; break; }
        player.Stop(); // never from a libVLC event thread: we're on a worker
        if (!ok) { try { File.Delete(dst); } catch (IOException) { } return false; }
        return File.Exists(dst) && new FileInfo(dst).Length > 0;
    }, cancel);

    /// <summary>Output path next to the source (or in <paramref name="folder"/>), never overwriting an existing file.</summary>
    public static string OutputPath(string src, ConvertPreset preset, string? folder = null, string suffix = "")
    {
        var dir = folder ?? (src.Contains("://") ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) : Path.GetDirectoryName(src)!);
        var name = src.Contains("://") ? "stream" : Path.GetFileNameWithoutExtension(src);
        var ext = preset.Extension.Length > 0 ? preset.Extension : Path.GetExtension(src);
        var path = Path.Combine(dir, name + suffix + ext);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{name}{suffix} ({n}){ext}");
        return path;
    }
}
