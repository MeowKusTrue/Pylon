using System.Diagnostics;
using System.Globalization;
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
    public const string ApiKey = "ipVDcde5dp";

    public static bool Possible => ApiKey.Length > 0;

    public record Song(string Title, string Artist, string? Album, string? Cover);
    public record Result(Song? Song, string Message);

    static readonly SemaphoreSlim Gate = new(1, 1);

    static string Fpcalc => Environment.GetEnvironmentVariable("PYLON_FPCALC")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pylon", "tools", "fpcalc.exe");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), DefaultRequestHeaders = { { "User-Agent", "Pylon (https://github.com/MeowKusTrue/Pylon)" } } };
    static readonly string CoverDir = Path.Combine(Library.Dir, "covers");

    /// <summary>The song, or null when it isn't known (or fpcalc / the network isn't there).</summary>
    public static async Task<Song?> IdentifyAsync(string path) => (await IdentifyDetailedAsync(path)).Song;

    public static async Task<Result> IdentifyDetailedAsync(string path)
    {
        var message = "No matching recording was found. Try another version or edit the track information manually.";
        var song = await IdentifyCoreAsync(path, reason => message = reason);
        return new(song, message);
    }

    static async Task<Song?> IdentifyCoreAsync(string path, Action<string> fail)
    {
        if (!Possible || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        { fail("Recognition needs an available local audio file."); return null; }
        await Gate.WaitAsync();
        try
        {
            if (!await EnsureFpcalcAsync()) { fail("The recognition tool is unavailable. Check your connection and try again."); return null; }
            var (duration, fingerprint) = await FingerprintAsync(path);
            if (duration <= 0 || string.IsNullOrWhiteSpace(fingerprint))
            { fail("Could not read enough audio for recognition. Try a longer, playable recording."); ErrorLog.Note("recognition", "fingerprint unavailable or timed out"); return null; }
            // POST: a fingerprint of a long song is too long for a URL
            using var res = await Http.PostAsync("https://api.acoustid.org/v2/lookup", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client"] = ApiKey, ["meta"] = "recordings releasegroups compress", ["duration"] = duration.ToString(CultureInfo.InvariantCulture), ["fingerprint"] = fingerprint,
            }));
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("status", out var status) || status.GetString() != "ok")
            { fail("The recognition service is unavailable. Try again later."); ErrorLog.Note("recognition service", "AcoustID returned an error response"); return null; }
            var best = doc.RootElement.GetProperty("results").EnumerateArray()
                .Where(r => r.GetProperty("score").GetDouble() > 0.6 && r.TryGetProperty("recordings", out var recordings) && recordings.ValueKind == JsonValueKind.Array)
                .OrderByDescending(r => r.GetProperty("score").GetDouble())
                .SelectMany(r => r.GetProperty("recordings").EnumerateArray())
                .FirstOrDefault(r => r.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(title.GetString()) && r.TryGetProperty("artists", out var artists)
                    && artists.ValueKind == JsonValueKind.Array && artists.EnumerateArray().Any(a =>
                        a.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())));
            if (best.ValueKind != JsonValueKind.Object)
            {
                if (duration < 10) fail("No matching recording was found. Try a longer audio fragment.");
                ErrorLog.Note("recognition", "no matches above confidence threshold with usable metadata");
                return null;
            }
            var artist = string.Join(", ", best.GetProperty("artists").EnumerateArray().Select(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null).Where(n => !string.IsNullOrWhiteSpace(n)));
            var group = best.TryGetProperty("releasegroups", out var g) && g.GetArrayLength() > 0 ? g[0] : default;
            string? album = null, cover = null;
            if (group.ValueKind == JsonValueKind.Object)
            {
                album = group.TryGetProperty("title", out var t) ? t.GetString() : null;
                if (group.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var groupId))
                    cover = await CoverAsync(groupId.ToString());
            }
            return new Song(best.GetProperty("title").GetString()!, artist, album, cover);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { fail(e is HttpRequestException or TaskCanceledException ? "The recognition service could not be reached. Check your connection and try again." : "Could not read this recording for recognition."); NoteFailure("recognition", e); return null; }
        finally { Gate.Release(); }
    }

    // Exception messages can contain request URLs, fingerprints or file names; log only fixed diagnoses.
    static void NoteFailure(string where, Exception error) => ErrorLog.Note(where,
        error.GetType().Name + (error is HttpRequestException { StatusCode: { } code } ? $" HTTP {(int)code}" : " operation failed"));

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
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        { NoteFailure("recognition artwork", e); return null; } // optional artwork must never discard a recognized song
    }

    static async Task<(int Duration, string? Fingerprint)> FingerprintAsync(string path)
    {
        var psi = new ProcessStartInfo(Fpcalc) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-json");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(Path.GetFullPath(path));
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await p.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            return (0, null);
        }
        var json = await output;
        await errors;
        // fpcalc can emit a usable fingerprint and then report a decoder EOF error (exit 3).
        if (p.ExitCode != 0 && p.ExitCode != 3 || string.IsNullOrWhiteSpace(json)) return (0, null);
        using var doc = JsonDocument.Parse(json);
        var duration = doc.RootElement.GetProperty("duration").GetDouble();
        if (!double.IsFinite(duration) || duration < 1 || duration > int.MaxValue) return (0, null);
        return ((int)Math.Round(duration), doc.RootElement.GetProperty("fingerprint").GetString());
    }

    /// <summary>fpcalc from Chromaprint's GitHub releases, fetched once.</summary>
    static async Task<bool> EnsureFpcalcAsync()
    {
        if (File.Exists(Fpcalc)) return true;
        var executable = Path.GetFullPath(Fpcalc);
        var zip = executable + "." + Guid.NewGuid().ToString("N") + ".zip";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllBytesAsync(zip, await Http.GetByteArrayAsync("https://github.com/acoustid/chromaprint/releases/download/v1.5.1/chromaprint-fpcalc-1.5.1-windows-x86_64.zip"));
            using (var archive = ZipFile.OpenRead(zip))
                archive.Entries.First(e => e.Name.Equals("fpcalc.exe", StringComparison.OrdinalIgnoreCase)).ExtractToFile(Fpcalc, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        { NoteFailure("recognition tool setup", e); return false; }
        finally { try { File.Delete(zip); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
}
