using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// ffmpeg for what libVLC 3 can't do cleanly: converting and joining files. Not shipped:
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
            using var http = NewHttp();
            await DownloadAsync(http, Builds + "ffmpeg-master-latest-win64-gpl.zip", zip, progress);
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

    const string Builds = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";
    // 8.1 is the release the bindings' DLL names fit (avcodec-62...); master and 9.0 have moved on
    const string EngineLibs = "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip";

    static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromMinutes(20), DefaultRequestHeaders = { { "User-Agent", "Pylon" } } };

    static async Task DownloadAsync(HttpClient http, string url, string file, IProgress<double>? progress)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync();
        await using var dst = File.Create(file);
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

    /// <summary>
    /// The video engine's libraries (about 80 MB, LGPL): only its DLLs and the licence from the archive, checked against the
    /// release's checksums (they load into this process), put in place whole or not at all. False on any failure.
    /// </summary>
    public static async Task<bool> InstallEngineLibsAsync(IProgress<double>? progress)
    {
        var dir = VideoEngine.DllDir;
        var (zip, fresh) = (dir + ".zip", dir + ".new");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            using var http = NewHttp();
            await DownloadAsync(http, Builds + EngineLibs, zip, progress);
            var sums = await http.GetStringAsync(Builds + "checksums.sha256");
            await using (var file = File.OpenRead(zip))
                if (!sums.Contains($"{Convert.ToHexStringLower(await SHA256.HashDataAsync(file))}  {EngineLibs}", StringComparison.Ordinal)) return false;
            if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
            Directory.CreateDirectory(fresh);
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var dll in VideoEngine.DllNames)
                    archive.Entries.First(e => e.FullName.EndsWith("/bin/" + dll, StringComparison.OrdinalIgnoreCase)).ExtractToFile(Path.Combine(fresh, dll));
                archive.Entries.First(e => e.FullName.EndsWith("/LICENSE.txt", StringComparison.OrdinalIgnoreCase)).ExtractToFile(Path.Combine(fresh, "LICENSE.txt"));
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.Move(fresh, dir);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(zip);
                if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
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
        List<string> args = ["-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "0", "-i", list, "-progress", "pipe:1", "-nostats"];
        args.AddRange(copy ? ["-c", "copy"] : ["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
        args.Add(dst);
        try { return await RunAsync(args, dst, total, progress, cancel); }
        finally { try { File.Delete(list); } catch (IOException) { } }
    }

    /// <summary>Runs ffmpeg with "-progress pipe:1" among its options. On failure or cancel the output is deleted; the error is ffmpeg's last line.</summary>
    public static async Task<(bool Ok, string? Error)> RunAsync(IEnumerable<string> args, string dst, long totalMs, IProgress<double>? progress, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(Exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        void Discard() { try { File.Delete(dst); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        Process? process = null;
        try
        {
            var p = process = Process.Start(psi)!;
            var errors = p.StandardError.ReadToEndAsync(cancel);
            // out_time_us is how far it got
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(cancel)) != null)
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && totalMs > 0)
                    progress?.Report(Math.Clamp(us / 1000.0 / totalMs, 0, 1));
            await p.WaitForExitAsync(cancel);
            if (p.ExitCode == 0 && File.Exists(dst) && new FileInfo(dst).Length > 0) return (true, null);
            var lines = (await errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Discard();
            return (false, lines.FirstOrDefault(l => l.Contains("does not contain any stream")) ?? lines.LastOrDefault()); // the last line only says "Invalid argument"
        }
        catch (OperationCanceledException)
        {
            try { process?.Kill(entireProcessTree: true); process?.WaitForExit(2000); } // or it goes on and keeps the file
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            Discard();
            return (false, null);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Discard();
            return (false, e.Message);
        }
        finally { process?.Dispose(); } // after the catches: a "using" inside the try would leave Kill nothing to kill
    }
    static LibVLC? _vlc;

    /// <summary>Length in ms, read by libVLC (no ffprobe needed).</summary>
    public static long Duration(string path)
    {
        _vlc ??= new LibVLC("--quiet", "--no-video", "--no-audio");
        using var media = new Media(_vlc, path, FromType.FromPath);
        media.Parse(MediaParseOptions.ParseLocal).Wait(5000);
        return Math.Max(0, media.Duration);
    }
}
