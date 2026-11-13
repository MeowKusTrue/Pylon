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
    static string DownloadExe => Path.Combine(StartupOptions.ToolsDirectory, "ffmpeg.exe");

    /// <summary>An explicit override wins; otherwise use an existing installation before downloading.</summary>
    public static string Exe
    {
        get
        {
            if (Environment.GetEnvironmentVariable("PYLON_FFMPEG") is { Length: > 0 } explicitPath) return explicitPath;
            if (Settings.Str("tools.ffmpeg") is { Length: > 0 } configured && File.Exists(configured)) return configured;
            return Candidates().FirstOrDefault(File.Exists) ?? DownloadExe;
        }
    }

    static IEnumerable<string> Candidates()
    {
        yield return DownloadExe;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'));
            if (Path.IsPathFullyQualified(path)) yield return Path.Combine(path, "ffmpeg.exe");
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "ffmpeg"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg"),
            Path.Combine(local, "Programs", "ffmpeg"), Path.Combine(home, "scoop", "apps", "ffmpeg", "current"),
            Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "chocolatey", "bin") })
        {
            yield return Path.Combine(root, "ffmpeg.exe");
            yield return Path.Combine(root, "bin", "ffmpeg.exe");
        }
        // WinGet's portable packages include a versioned directory, without always updating this process's PATH.
        string[] packages;
        try { packages = Directory.GetDirectories(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "*FFmpeg*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var package in packages)
        {
            yield return Path.Combine(package, "bin", "ffmpeg.exe");
            string[] builds;
            try { builds = Directory.GetDirectories(package, "ffmpeg*"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var build in builds) yield return Path.Combine(build, "bin", "ffmpeg.exe");
        }
    }

    public static bool Available => File.Exists(Exe);

    /// <summary>Downloads the zip (about 200 MB) and keeps only ffmpeg.exe; false on failure.</summary>
    public static async Task<bool> InstallAsync(IProgress<double>? progress, CancellationToken cancel = default)
    {
        if (Available) return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PYLON_FFMPEG"))) return false;
        var zip = DownloadExe + ".zip";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DownloadExe)!);
            using var http = NewHttp();
            await DownloadAsync(http, Builds + "ffmpeg-master-latest-win64-gpl.zip", zip, progress, cancel);
            using (var archive = ZipFile.OpenRead(zip))
            {
                var entry = archive.Entries.First(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                entry.ExtractToFile(DownloadExe, overwrite: true);
            }
            cancel.ThrowIfCancellationRequested();
            return Available;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException or InvalidOperationException)
        {
            cancel.ThrowIfCancellationRequested();
            return false;
        }
        finally { try { File.Delete(zip); } catch (IOException) { } }
    }

    const string Builds = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";
    // 8.1 is the release the bindings' DLL names fit (avcodec-62...); master and 9.0 have moved on
    const string EngineLibs = "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip";

    static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromMinutes(20), DefaultRequestHeaders = { { "User-Agent", "Pylon" } } };

    public static async Task DownloadAsync(HttpClient http, string url, string file, IProgress<double>? progress, CancellationToken cancel = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        idle.CancelAfter(TimeSpan.FromSeconds(30));
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync(idle.Token);
        await using var dst = File.Create(file);
        var buf = new byte[1 << 16];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, idle.Token)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), idle.Token);
            done += n;
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            if (total > 0) progress?.Report((double)done / total);
        }
    }

    /// <summary>
    /// The video engine's libraries (about 80 MB, LGPL): only its DLLs and the licence from the archive, checked against the
    /// release's checksums (they load into this process), put in place whole or not at all. False on any failure.
    /// </summary>
    public static async Task<bool> InstallEngineLibsAsync(IProgress<double>? progress, CancellationToken cancel = default)
    {
        var dir = VideoEngine.DllDir;
        var (zip, fresh) = (dir + ".zip", dir + ".new");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            using var http = NewHttp();
            await DownloadAsync(http, Builds + EngineLibs, zip, progress, cancel);
            var sums = await http.GetStringAsync(Builds + "checksums.sha256", cancel);
            await using (var file = File.OpenRead(zip))
                if (!sums.Contains($"{Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel))}  {EngineLibs}", StringComparison.Ordinal)) return false;
            if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
            Directory.CreateDirectory(fresh);
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var dll in VideoEngine.DllNames)
                {
                    cancel.ThrowIfCancellationRequested();
                    archive.Entries.First(e => e.FullName.EndsWith("/bin/" + dll, StringComparison.OrdinalIgnoreCase)).ExtractToFile(Path.Combine(fresh, dll));
                }
                archive.Entries.First(e => e.FullName.EndsWith("/LICENSE.txt", StringComparison.OrdinalIgnoreCase)).ExtractToFile(Path.Combine(fresh, "LICENSE.txt"));
            }
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.Move(fresh, dir);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException or InvalidOperationException)
        {
            cancel.ThrowIfCancellationRequested();
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
    /// One file from several, in order, using the selected output preset. Copying requires compatible streams.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> JoinAsync(IReadOnlyList<string> files, string dst, ConvertPreset preset, IProgress<double>? progress, CancellationToken cancel)
    {
        if (files.Count == 0) return (false, "No files to join.");
        if (preset.Extension.Length == 0 && Path.GetExtension(dst).Equals(".flac", StringComparison.OrdinalIgnoreCase))
            return (false, "Choose the FLAC lossless preset to join FLAC files with a correct duration header.");
        var media = await Task.Run(() => files.Select(f => JoinInfo(f, preset)).ToArray(), cancel);
        cancel.ThrowIfCancellationRequested();
        if (media.Any(m => m.Signature.Length == 0)) return (false, "Could not read the tracks of one of the input files.");
        if (media.Select(m => m.Signature).Distinct().Count() > 1)
        {
            if (preset.Extension.Length == 0) return (false, "Stream copying requires matching codecs and track parameters. Choose an encoding preset to make the parts compatible.");
            return await JoinCompatible(files, media, dst, preset, progress, cancel);
        }
        return await Concat(files, media.Sum(m => m.Ms), dst, preset, progress, cancel);
    }

    sealed record JoinShape(long Ms, int Width, int Height, double Fps, int Rate, int Channels, string Signature);

    static JoinShape JoinInfo(string path, ConvertPreset preset)
    {
        var media = new Media(ProbeVlc.Value, path, FromType.FromPath);
        try
        {
            media.Parse(MediaParseOptions.ParseLocal).Wait(5000);
            var tracks = media.Tracks;
            var video = tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
            var audio = tracks.FirstOrDefault(t => t.TrackType == TrackType.Audio);
            var signature = string.Join(";", tracks.Where(t => preset.Extension.Length == 0
                || t.TrackType == TrackType.Video && preset.Video || t.TrackType == TrackType.Audio && !preset.Args.Contains("-an"))
                .Select(t => $"{t.TrackType}:{t.Codec}:" + (t.TrackType == TrackType.Video
                    ? $"{t.Data.Video.Width}:{t.Data.Video.Height}:{t.Data.Video.FrameRateNum}/{t.Data.Video.FrameRateDen}"
                    : t.TrackType == TrackType.Audio ? $"{t.Data.Audio.Rate}:{t.Data.Audio.Channels}" : "")));
            return new(Math.Max(0, media.Duration), (int)video.Data.Video.Width, (int)video.Data.Video.Height,
                video.Data.Video.FrameRateDen > 0 ? video.Data.Video.FrameRateNum / (double)video.Data.Video.FrameRateDen : 30,
                (int)audio.Data.Audio.Rate, (int)audio.Data.Audio.Channels, signature);
        }
        finally { Library.DisposeLater(media); }
    }

    static async Task<(bool Ok, string? Error)> JoinCompatible(IReadOnlyList<string> files, JoinShape[] shapes, string dst,
        ConvertPreset preset, IProgress<double>? progress, CancellationToken cancel)
    {
        var hasVideo = preset.Video && shapes.Any(s => s.Width > 0);
        if (hasVideo && shapes.Any(s => s.Width == 0)) return (false, "Some parts contain no video. Choose an audio preset to join their sound.");
        var hasAudio = !preset.Args.Contains("-an") && shapes.Any(s => s.Rate > 0);
        if (!preset.Video && shapes.Any(s => s.Rate == 0)) return (false, "Some parts contain no audio. They cannot be joined with an audio preset.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(dst))!;
        var folder = Path.Combine(parent, ".pylon-join-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var parts = new List<string>();
        try
        {
            var settings = preset.Args.ToList();
            if (hasVideo)
            {
                var width = Math.Max(2, shapes[0].Width / 2 * 2);
                var height = Math.Max(2, shapes[0].Height / 2 * 2);
                var fps = Math.Clamp(shapes[0].Fps, 1, 240).ToString("0.######", CultureInfo.InvariantCulture);
                var filter = $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps}";
                var vf = settings.IndexOf("-vf");
                if (vf >= 0) settings[vf + 1] = filter + "," + settings[vf + 1];
                else settings.AddRange(["-vf", filter]);
            }
            if (hasAudio)
            {
                var sound = shapes.First(s => s.Rate > 0);
                var ac = settings.IndexOf("-ac");
                settings.AddRange(["-ar", sound.Rate.ToString(CultureInfo.InvariantCulture)]);
                if (ac < 0) settings.AddRange(["-ac", sound.Channels.ToString(CultureInfo.InvariantCulture)]);
            }
            for (var i = 0; i < files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var part = Path.Combine(folder, $"part-{i:D5}" + preset.Extension);
                parts.Add(part);
                var options = new List<string>();
                if (hasVideo) options.AddRange(["-map", "0:v:0"]);
                if (hasAudio) options.AddRange(["-map", shapes[i].Rate > 0 ? "0:a:0" : "1:a:0"]);
                options.AddRange(settings);
                if (hasAudio && shapes[i].Rate == 0) options.Add("-shortest");
                var args = Converter.Args(files[i], part, preset with { Args = [.. options] }, -1, -1);
                if (hasAudio && shapes[i].Rate == 0)
                    args.InsertRange(args.IndexOf("-i") + 2, ["-f", "lavfi", "-i", "anullsrc"]);
                var index = i;
                var result = await RunAsync(args, part, shapes[i].Ms,
                    new Progress<double>(p => progress?.Report(0.8 * (index + p) / files.Count)), cancel);
                if (!result.Ok) return result;
            }
            // FLAC's copied STREAMINFO retains the first part's sample count; rebuild that header losslessly.
            var joinedPreset = preset.Extension == ".flac" ? preset : preset with { Args = ["-map", "0", "-c", "copy"] };
            return await Concat(parts, shapes.Sum(s => s.Ms), dst, joinedPreset,
                new Progress<double>(p => progress?.Report(0.8 + p * 0.2)), cancel);
        }
        finally
        {
            foreach (var part in parts) try { File.Delete(part); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try { Directory.Delete(folder); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    static async Task<(bool Ok, string? Error)> Concat(IReadOnlyList<string> files, long total, string dst, ConvertPreset preset, IProgress<double>? progress, CancellationToken cancel)
    {
        var list = Path.Combine(Path.GetTempPath(), $"pylon-join-{Guid.NewGuid():N}.txt");
        List<string> args = ["-hide_banner", "-nostdin", "-y", "-f", "concat", "-safe", "0", "-i", list, "-progress", "pipe:1", "-nostats"];
        args.AddRange(preset.Args);
        args.Add(dst);
        try
        {
            await File.WriteAllLinesAsync(list, files.Select(f => $"file '{f.Replace("'", "'\\''")}'"), cancel);
            return await RunAsync(args, dst, total, progress, cancel);
        }
        finally { try { File.Delete(list); } catch (IOException) { } }
    }

    /// <summary>Runs ffmpeg with "-progress pipe:1" among its options. On failure or cancel the output is deleted; the error is ffmpeg's last line.</summary>
    public static async Task<(bool Ok, string? Error)> RunAsync(IEnumerable<string> args, string dst, long totalMs, IProgress<double>? progress, CancellationToken cancel)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dst))!, ".pylon-" + Guid.NewGuid().ToString("N") + Path.GetExtension(dst));
        var psi = new ProcessStartInfo(Exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a == dst ? temporary : a);
        void Discard() { try { File.Delete(temporary); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        Process? process = null;
        using var cancelled = cancel.Register(() =>
        {
            try { process?.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        try
        {
            cancel.ThrowIfCancellationRequested();
            var required = psi.ArgumentList.Select((a, i) => (a, i))
                .Where(x => x.a is "-c:v" or "-c:a" or "-c")
                .Select(x => psi.ArgumentList[x.i + 1]).Where(a => a != "copy").Distinct().ToArray();
            if (required.Length > 0)
            {
                var encoders = await EncodersAsync(psi.FileName, cancel);
                var missing = required.Where(e => !encoders.Contains(e)).ToArray();
                if (missing.Length > 0) return (false, "ffmpeg: missing encoders: " + string.Join(", ", missing));
            }
            cancel.ThrowIfCancellationRequested();
            var p = process = Process.Start(psi)!;
            if (cancel.IsCancellationRequested) { p.Kill(entireProcessTree: true); cancel.ThrowIfCancellationRequested(); }
            var errors = p.StandardError.ReadToEndAsync(cancel);
            // out_time_us is how far it got
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(cancel)) != null)
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && totalMs > 0)
                    progress?.Report(Math.Clamp(us / 1000.0 / totalMs, 0, 1));
            await p.WaitForExitAsync(cancel);
            if (p.ExitCode == 0 && File.Exists(temporary) && new FileInfo(temporary).Length > 0)
            {
                File.Move(temporary, dst, overwrite: false);
                return (true, null);
            }
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
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Discard();
            return (false, e.Message);
        }
        finally { process?.Dispose(); } // after the catches: a "using" inside the try would leave Kill nothing to kill
    }

    static readonly Dictionary<(string Exe, DateTime Stamp), HashSet<string>> EncoderCache = [];

    static async Task<HashSet<string>> EncodersAsync(string exe, CancellationToken cancel)
    {
        var key = (exe, File.GetLastWriteTimeUtc(exe));
        lock (EncoderCache) if (EncoderCache.TryGetValue(key, out var known)) return known;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-encoders");
        using var process = Process.Start(psi)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            var error = await errors;
            if (process.ExitCode != 0) throw new IOException("ffmpeg: " + error.Trim());
            var found = text.Split('\n').Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2 && p[0].Length == 6 && "VAS".Contains(p[0][0]))
                .Select(p => p[1]).ToHashSet(StringComparer.Ordinal);
            lock (EncoderCache)
            {
                if (EncoderCache.Count >= 4) EncoderCache.Clear();
                EncoderCache[key] = found;
            }
            return found;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new IOException("ffmpeg: encoder detection timed out.");
        }
        finally
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(2000); } }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
    static readonly Lazy<LibVLC> ProbeVlc = new(() => new LibVLC("--quiet", "--no-video", "--no-audio"));

    /// <summary>Length in ms, read by libVLC (no ffprobe needed).</summary>
    public static long Duration(string path)
    {
        var media = new Media(ProbeVlc.Value, path, FromType.FromPath);
        try { media.Parse(MediaParseOptions.ParseLocal).Wait(5000); return Math.Max(0, media.Duration); }
        finally { Library.DisposeLater(media); }
    }
}
