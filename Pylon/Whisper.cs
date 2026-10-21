using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// Subtitles from speech: whisper.cpp (github.com/ggml-org/whisper.cpp, MIT) turns a video's dialogue into an .srt
/// beside it. The program (CPU build, or the CUDA one for NVIDIA cards) and a speech model are downloaded on consent
/// into %LocalAppData%\Pylon\tools\whisper; the sound is decoded by libVLC to 16 kHz mono first. Runs in the background.
/// </summary>
public static class Whisper
{
    public enum Quality { Fast, Accurate, Best }

    public record Options(Quality Quality, bool Gpu, string Language, bool ToEnglish);

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pylon", "tools", "whisper");
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestHeaders = { { "User-Agent", "Pylon" } } };

    public static string ModelFile(Quality q) => q switch { Quality.Fast => "ggml-base.bin", Quality.Accurate => "ggml-small.bin", _ => "ggml-large-v3-turbo.bin" };
    public static int ModelMb(Quality q) => q switch { Quality.Fast => 148, Quality.Accurate => 488, _ => 1624 };
    static string BuildDir(bool gpu) => Path.Combine(Dir, gpu ? "cuda" : "cpu");

    /// <summary>An NVIDIA card is present (the CUDA build is offered then).</summary>
    public static bool HasNvidia => BugReport.Gpus().Any(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

    /// <summary>What still has to be downloaded for these options, in MB (0 = ready).</summary>
    public static int MissingMb(Options o) =>
        (Installed(o.Gpu) == null ? (o.Gpu ? 653 : 8) : 0) + (File.Exists(Path.Combine(Dir, "models", ModelFile(o.Quality))) ? 0 : ModelMb(o.Quality));

    /// <summary>
    /// The installed build's program, if it can do its job. A GPU build needs cuBLAS beside it: the CUDA 11.8 one Pylon
    /// fetched until 2.0.3 ships without it, so its GPU part never loaded and it quietly ran on the CPU; it is replaced.
    /// </summary>
    static string? Installed(bool gpu) =>
        FindExe(BuildDir(gpu)) is { } exe && (!gpu || Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "cublas64_*.dll").Any()) ? exe : null;

    /// <summary>
    /// whisper-cli.exe; main.exe only from builds that have nothing else: newer releases still ship a main.exe that just
    /// says it's deprecated and quits, and it came first alphabetically ("whisper.cpp stopped with code 1").
    /// </summary>
    static string? FindExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var exes = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories).ToList();
        return exes.FirstOrDefault(f => Path.GetFileName(f).Equals("whisper-cli.exe", StringComparison.OrdinalIgnoreCase))
            ?? exes.FirstOrDefault(f => Path.GetFileName(f).Equals("main.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The whole job; reports (stage text, 0..1) and returns the .srt path, or null (with the reason in Error).</summary>
    public static async Task<string?> RunAsync(string video, double duration, Options o, IProgress<(string Stage, double P)> progress, CancellationToken cancel)
    {
        Error = null;
        try
        {
            var exe = Installed(o.Gpu) ?? await InstallBuild(o.Gpu, progress, cancel);
            if (exe == null) { Error ??= L.T("Couldn't download whisper.cpp"); return null; }
            var model = Path.Combine(Dir, "models", ModelFile(o.Quality));
            if (!File.Exists(model) && !await Download($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{ModelFile(o.Quality)}", model, L.T("Downloading the speech model…"), progress, cancel)) return null;

            progress.Report((L.T("Taking the sound out of the video…"), 0));
            var wav = Path.Combine(Path.GetTempPath(), $"pylon-whisper-{Guid.NewGuid():N}.wav");
            try
            {
                if (!await Task.Run(() => DecodeSound(video, wav, duration, progress, cancel), cancel)) { Error = L.T("Couldn't read the video's sound"); return null; }
                var outBase = Path.Combine(Path.GetTempPath(), $"pylon-whisper-{Guid.NewGuid():N}");
                if (!await Recognize(exe, model, wav, outBase, o, progress, cancel)) return null;
                var lang = o.ToEnglish ? "en" : o.Language == "auto" ? "auto" : o.Language;
                var target = Path.Combine(Path.GetDirectoryName(video)!, $"{Path.GetFileNameWithoutExtension(video)}.{lang}.whisper.srt");
                File.Move(outBase + ".srt", target, overwrite: true);
                return target;
            }
            finally { try { File.Delete(wav); } catch (IOException) { } }
        }
        catch (OperationCanceledException) { Error = null; return null; }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Error = e.Message;
            return null;
        }
    }

    public static string? Error { get; private set; }

    /// <summary>The newest whisper.cpp release that has Windows builds: the CPU one, or CUDA 12 for NVIDIA cards.</summary>
    static async Task<string?> InstallBuild(bool gpu, IProgress<(string, double)> progress, CancellationToken cancel)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=10", cancel));
        // CUDA 12: its zip carries cuBLAS (the 11.8 one doesn't); the driver it needs (551+) comes with any card from the RTX 20s on
        var pattern = gpu ? new Regex(@"cu(da|blas)-12[^/]*x64\.zip$") : new Regex(@"^whisper-bin-x64\.zip$");
        var url = doc.RootElement.EnumerateArray()
            .SelectMany(r => r.GetProperty("assets").EnumerateArray())
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .FirstOrDefault(a => pattern.IsMatch(a.Name) && a.Url.StartsWith("https://github.com/ggml-org/whisper.cpp/")).Url;
        if (url == null) return null;
        var zip = Path.Combine(Dir, Path.GetFileName(url));
        if (!await Download(url, zip, L.T("Downloading whisper.cpp…"), progress, cancel)) return null;
        var target = BuildDir(gpu);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        ZipFile.ExtractToDirectory(zip, target);
        File.Delete(zip);
        return FindExe(target);
    }

    static async Task<bool> Download(string url, string path, string stage, IProgress<(string, double)> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        using (var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            if (!res.IsSuccessStatusCode) { Error = $"{(int)res.StatusCode} {res.ReasonPhrase}"; return false; }
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var src = await res.Content.ReadAsStreamAsync(cancel);
            await using var dst = File.Create(part);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, cancel)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), cancel);
                done += n;
                if (total > 0) progress.Report((stage, (double)done / total));
            }
        }
        File.Move(part, path, overwrite: true);
        return true;
    }

    static LibVLC? _vlc;

    /// <summary>The video's sound as a 16 kHz mono WAV (what whisper reads), progress from how far the transcode got.</summary>
    static bool DecodeSound(string video, string wav, double duration, IProgress<(string, double)> progress, CancellationToken cancel)
    {
        _vlc ??= new LibVLC("--quiet", "--no-video", "--no-sout-video", "--no-spu");
        using var media = new Media(_vlc, video, FromType.FromPath);
        media.AddOption($":sout=#transcode{{acodec=s16l,channels=1,samplerate=16000}}:std{{access=file,mux=wav,dst='{wav.Replace("'", "")}'}}");
        media.AddOption(":sout-keep");
        using var player = new MediaPlayer(media);
        var done = new ManualResetEventSlim();
        var ok = true;
        player.EndReached += (_, _) => done.Set();
        player.EncounteredError += (_, _) => { ok = false; done.Set(); };
        if (!player.Play()) return false;
        while (!done.Wait(500))
        {
            if (cancel.IsCancellationRequested) { player.Stop(); cancel.ThrowIfCancellationRequested(); }
            progress.Report((L.T("Taking the sound out of the video…"), Math.Clamp(player.Position, 0, 1)));
        }
        player.Stop();
        return ok && File.Exists(wav);
    }

    static async Task<bool> Recognize(string exe, string model, string wav, string outBase, Options o, IProgress<(string, double)> progress, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in new[] { "-m", model, "-f", wav, "-osrt", "-of", outBase, "-pp", "-l", o.Language, "-t", Math.Max(2, Environment.ProcessorCount - 2).ToString() })
            psi.ArgumentList.Add(a);
        if (o.ToEnglish) psi.ArgumentList.Add("-tr");
        if (!o.Gpu) psi.ArgumentList.Add("-ng");
        var stage = L.T("Recognizing speech…");
        progress.Report((stage, 0));
        using var p = Process.Start(psi)!;
        p.PriorityClass = ProcessPriorityClass.BelowNormal; // the computer stays usable meanwhile
        var percent = new Regex(@"progress\s*=\s*(\d+)%");
        var errors = new System.Text.StringBuilder();
        var tail = new Queue<string>(); // the last lines it printed, for a failure that names no error
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (percent.Match(e.Data) is { Success: true } m) { progress.Report((stage, int.Parse(m.Groups[1].Value) / 100.0)); return; }
            if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase)) lock (errors) errors.AppendLine(e.Data);
            lock (tail) { tail.Enqueue(e.Data.Trim()); if (tail.Count > 3) tail.Dequeue(); }
        };
        p.OutputDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        try { await p.WaitForExitAsync(cancel); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        if (p.ExitCode == 0 && File.Exists(outBase + ".srt")) return true;
        Error = errors.Length > 0 ? errors.ToString().Trim()
            : L.F("whisper.cpp stopped with code {0}", p.ExitCode) + (tail.Count > 0 ? ": " + string.Join(" ", tail.Where(t => t.Length > 0)) : "");
        return false;
    }
}
