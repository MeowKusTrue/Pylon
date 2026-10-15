using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Pylon;

/// <summary>
/// Names a song by its sound, like Shazam: Chromaprint's fpcalc fingerprints the audio (downloaded on first use, about
/// 3 MB, LGPL), AcoustID matches it to a MusicBrainz recording, and the Cover Art Archive gives the album cover.
/// </summary>
public static class Recognize
{
    /// <summary>Pylon's application key from acoustid.org/new-application (meant to ship inside apps). Empty = off.</summary>
    public const string ApiKey = "";

    public static bool Possible => ApiKey.Length > 0;

    public record Song(string Title, string Artist, string? Album, string? Cover);

    static string Fpcalc => Environment.GetEnvironmentVariable("PYLON_FPCALC")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pylon", "tools", "fpcalc.exe");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), DefaultRequestHeaders = { { "User-Agent", "Pylon (https://github.com/MeowKusTrue/Pylon)" } } };
    static readonly string CoverDir = Path.Combine(Library.Dir, "covers");

    /// <summary>The song, or null when it isn't known (or fpcalc / the network isn't there).</summary>
    public static async Task<Song?> IdentifyAsync(string path)
    {
        if (!Possible || !await EnsureFpcalcAsync()) return null;
        try
        {
            var (duration, fingerprint) = await FingerprintAsync(path);
            if (fingerprint == null) return null;
            var url = $"https://api.acoustid.org/v2/lookup?client={ApiKey}&meta=recordings+releasegroups+compress&duration={duration}&fingerprint={fingerprint}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var best = doc.RootElement.GetProperty("results").EnumerateArray()
                .Where(r => r.GetProperty("score").GetDouble() > 0.6 && r.TryGetProperty("recordings", out _))
                .OrderByDescending(r => r.GetProperty("score").GetDouble())
                .SelectMany(r => r.GetProperty("recordings").EnumerateArray())
                .FirstOrDefault(r => r.TryGetProperty("title", out _) && r.TryGetProperty("artists", out _));
            if (best.ValueKind != JsonValueKind.Object) return null;
            var artist = string.Join(", ", best.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
            var group = best.TryGetProperty("releasegroups", out var g) && g.GetArrayLength() > 0 ? g[0] : default;
            string? album = null, cover = null;
            if (group.ValueKind == JsonValueKind.Object)
            {
                album = group.TryGetProperty("title", out var t) ? t.GetString() : null;
                cover = await CoverAsync(group.GetProperty("id").GetString()!);
            }
            return new Song(best.GetProperty("title").GetString()!, artist, album, cover);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    static async Task<string?> CoverAsync(string releaseGroup)
    {
        var file = Path.Combine(CoverDir, "mb-" + releaseGroup + ".jpg");
        if (File.Exists(file)) return file;
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://coverartarchive.org/release-group/{releaseGroup}/front-500");
            Directory.CreateDirectory(CoverDir);
            await File.WriteAllBytesAsync(file, bytes);
            return file;
        }
        catch (HttpRequestException) { return null; } // no cover for that album
    }

    static async Task<(int Duration, string? Fingerprint)> FingerprintAsync(string path)
    {
        var psi = new ProcessStartInfo(Fpcalc) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-json");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        var json = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) return (0, null);
        using var doc = JsonDocument.Parse(json);
        return ((int)doc.RootElement.GetProperty("duration").GetDouble(), doc.RootElement.GetProperty("fingerprint").GetString());
    }

    /// <summary>fpcalc from Chromaprint's GitHub releases, fetched once.</summary>
    static async Task<bool> EnsureFpcalcAsync()
    {
        if (File.Exists(Fpcalc)) return true;
        var zip = Fpcalc + ".zip";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Fpcalc)!);
            await File.WriteAllBytesAsync(zip, await Http.GetByteArrayAsync("https://github.com/acoustid/chromaprint/releases/download/v1.5.1/chromaprint-fpcalc-1.5.1-windows-x86_64.zip"));
            using (var archive = ZipFile.OpenRead(zip))
                archive.Entries.First(e => e.Name.Equals("fpcalc.exe", StringComparison.OrdinalIgnoreCase)).ExtractToFile(Fpcalc, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or InvalidOperationException) { return false; }
        finally { try { File.Delete(zip); } catch (IOException) { } }
    }
}
