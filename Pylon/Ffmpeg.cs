using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// ffmpeg for what libVLC 3 can't do cleanly: joining files (its "gather" output restarts the timestamps). Not shipped:
/// downloaded from the BtbN builds on GitHub (GPL, with x264) when the user agrees, into %LocalAppData%\Pylon\tools.
/// </summary>
public static class Ffmpeg
{
    /// <summary>PYLON_FFMPEG points at another ffmpeg (tests).</summary>
    public static string Exe => Environment.GetEnvironmentVariable("PYLON_FFMPEG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pylon", "tools", "ffmpeg.exe");

    public static bool Available => File.Exists(Exe);

    /// <summary>Downloads the zip (about 200 MB) and keeps only ffmpeg.exe; false on failure.</summary>
    public static async Task<bool> InstallAsync(IProgress<double>? progress)
    {
        var zip = Exe + ".zip";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Exe)!);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20), DefaultRequestHeaders = { { "User-Agent", "Pylon" } } };
            using (var res = await http.GetAsync("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip", HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? 0;
                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zip);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.First(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(Exe, overwrite: true);
            }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
        finally { try { File.Delete(zip); } catch (IOException) { } }
    }

    /// <summary>
    /// One file from several, in order. <paramref name="copy"/>: the streams as they are (instant, lossless; the files must
    /// be of one kind, e.g. from the same recorder), else H.264 + AAC MP4. Returns ffmpeg's error line on failure.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> JoinAsync(IReadOnlyList<string> files, string dst, bool copy, IProgress<double>? progress, CancellationToken cancel)
    {
        var total = await Task.Run(() => files.Sum(Duration));
        var list = Path.Combine(Path.GetTempPath(), $"pylon-join-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(list, files.Select(f => $"file '{f.Replace("'", "'\\''")}'"), cancel);
        var psi = new ProcessStartInfo(Exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-y", "-f", "concat", "-safe", "0", "-i", list, "-progress", "pipe:1", "-nostats" }) psi.ArgumentList.Add(a);
        foreach (var a in copy ? ["-c", "copy"] : new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart" })
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(dst);
        try
        {
            using var p = Process.Start(psi)!;
            var errors = p.StandardError.ReadToEndAsync(cancel);
            // "-progress pipe:1" prints key=value lines; out_time_us is how far it got
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(cancel)) != null)
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && total > 0)
                    progress?.Report(Math.Clamp(us / 1000.0 / total, 0, 1));
            await p.WaitForExitAsync(cancel);
            if (p.ExitCode == 0 && File.Exists(dst)) return (true, null);
            var last = (await errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            try { File.Delete(dst); } catch (IOException) { }
            return (false, last);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(dst); } catch (IOException) { }
            return (false, null);
        }
        finally { try { File.Delete(list); } catch (IOException) { } }
    }

    static LibVLC? _vlc;

    /// <summary>Length in ms, read by libVLC (no ffprobe needed).</summary>
    static long Duration(string path)
    {
        _vlc ??= new LibVLC("--quiet", "--no-video", "--no-audio");
        using var media = new Media(_vlc, path, FromType.FromPath);
        media.Parse(MediaParseOptions.ParseLocal).Wait(5000);
        return Math.Max(0, media.Duration);
    }
}
