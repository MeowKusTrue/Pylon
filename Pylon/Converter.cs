using System.Globalization;
using System.IO;

namespace Pylon;

/// <summary>A conversion target: ffmpeg's output options and the file extension (empty: the source's).</summary>
public record ConvertPreset(string Name, string Extension, bool Video, string[] Args);

/// <summary>Converts files with ffmpeg (fetched on consent) and cuts clips. libVLC's stream output lost the picture of MP4s and ignored start times.</summary>
public static class Converter
{
    public static readonly ConvertPreset[] Presets =
    [
        new("MP3 (320 kbps)", ".mp3", false, ["-vn", "-c:a", "libmp3lame", "-b:a", "320k", "-ac", "2"]),
        new("M4A (AAC 256 kbps)", ".m4a", false, ["-vn", "-c:a", "aac", "-b:a", "256k", "-ac", "2", "-movflags", "+faststart"]),
        new("FLAC (lossless)", ".flac", false, ["-vn", "-c:a", "flac"]),
        new("Opus (160 kbps)", ".opus", false, ["-vn", "-c:a", "libopus", "-b:a", "160k", "-ac", "2"]),
        new("WAV", ".wav", false, ["-vn", "-c:a", "pcm_s16le", "-ac", "2"]),
        // 8-bit 4:2:0 and even sizes: what phones and TVs play
        new("MP4 (H.264 + AAC)", ".mp4", true, ["-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]),
        new("GIF (400 px)", ".gif", true, ["-an", "-vf", "fps=20,scale=400:-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-loop", "0"]),
        // every stream as it is; a cut starts at the keyframe before the start
        new("Copy streams (fast)", "", true, ["-map", "0", "-dn", "-ignore_unknown", "-c", "copy", "-avoid_negative_ts", "make_zero"]),
        new("MP4 (H.264, high quality)", ".mp4", true, ["-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-preset", "slow", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart"]),
        new("MP4 (H.265 / HEVC)", ".mp4", true, ["-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx265", "-preset", "medium", "-crf", "28", "-tag:v", "hvc1", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]),
        new("WebM (VP9 + Opus)", ".webm", true, ["-c:v", "libvpx-vp9", "-crf", "30", "-b:v", "0", "-deadline", "good", "-cpu-used", "2", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "160k"]),
        new("WebM (AV1 + Opus, slow)", ".webm", true, ["-c:v", "libaom-av1", "-crf", "30", "-b:v", "0", "-cpu-used", "4", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "160k"]),
        new("MOV (ProRes 422 HQ)", ".mov", true, ["-vf", "scale=trunc(iw/2)*2:ih", "-c:v", "prores_ks", "-profile:v", "3", "-pix_fmt", "yuv422p10le", "-c:a", "pcm_s24le"]),
    ];

    /// <summary>The whole ffmpeg command line. A start or stop below 0 means none; seeking before -i is both fast and exact when encoding.</summary>
    public static List<string> Args(string src, string dst, ConvertPreset preset, long startMs, long stopMs)
    {
        List<string> args = ["-hide_banner", "-nostdin", "-y"];
        if (startMs > 0) args.AddRange(["-ss", Seconds(startMs)]);
        if (stopMs > 0) args.AddRange(["-to", Seconds(stopMs)]);
        args.AddRange(["-i", src]);
        args.AddRange(preset.Args);
        args.AddRange(["-progress", "pipe:1", "-nostats", dst]);
        return args;
    }

    static string Seconds(long ms) => (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Runs one conversion. <paramref name="startMs"/> / <paramref name="stopMs"/> cut a clip (-1 = whole file). Returns ffmpeg's last line on failure.</summary>
    public static async Task<(bool Ok, string? Error)> RunAsync(string src, string dst, ConvertPreset preset, long startMs = -1, long stopMs = -1,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var length = await Task.Run(() => Ffmpeg.Duration(src));
        // MPEG-TS has no index and ffmpeg's seek lands past the keyframe: copying then started seconds late, or at the next one
        if (startMs > 0 && preset.Extension.Length == 0 && !src.Contains("://") && Keyframes.IsTs(src)
            && await Task.Run(() => Keyframes.Near(src, startMs, forward: false, length)) is { } key) startMs = Math.Max(0, key.Ms - 30);
        var end = stopMs > 0 ? stopMs : length;
        return await Ffmpeg.RunAsync(Args(src, dst, preset, startMs, stopMs), dst, end - Math.Max(0, startMs), progress, cancel);
    }

    /// <summary>"1:23", "1:02:03", "83" or "1:23.5" in ms; -1 when it isn't a time.</summary>
    public static long ParseTime(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length > 3) return -1;
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return -1;
            seconds = seconds * 60 + value;
        }
        return (long)Math.Round(seconds * 1000);
    }

    /// <summary>"1:23.5": what ParseTime reads, tenths only when there are any.</summary>
    public static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(ms / 100.0) * 100);
        var whole = t.TotalHours >= 1
            ? ((long)t.TotalHours).ToString(CultureInfo.InvariantCulture) + t.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        return t.Milliseconds == 0 ? whole : whole + "." + t.Milliseconds / 100;
    }

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
