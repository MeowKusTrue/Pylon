using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Pylon;

/// <summary>
/// Subtitles from OpenSubtitles.com: searched by the file's OpenSubtitles hash (an exact match to this copy of the video)
/// and its name, in the preferred subtitle language and English; the chosen one is saved next to the video.
/// </summary>
public static class OnlineSubtitles
{
    /// <summary>Pylon's consumer key from opensubtitles.com/consumers. Empty = the menu item stays hidden.</summary>
    public const string ApiKey = "A4UPZMJicwEDXiCwkswANXRDFlpQ6M4N";

    public static bool Possible => ApiKey.Length > 0;

    public record Result(long FileId, string Name, string Language, int Downloads, bool HashMatch);

    static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api.opensubtitles.com/api/v1/"), Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "Api-Key", ApiKey }, { "User-Agent", $"Pylon v{Updater.Current.ToString(3)}" } },
    };

    public static async Task<List<Result>> SearchAsync(string video, string languages)
    {
        // the API wants its parameters (and the languages) in alphabetical order, or it answers with a redirect
        var langs = string.Join(",", languages.Split(',').Select(l => l.Trim().ToLowerInvariant()).Order());
        // two searches: by hash (exact matches for this copy) and by name; together the hash narrowed the name search wrongly
        var byHash = Hash(video) is { } hash ? FetchAsync($"subtitles?languages={langs}&moviehash={hash}") : Task.FromResult(new List<Result>());
        var byName = FetchAsync($"subtitles?languages={langs}&query={Uri.EscapeDataString(Query(video))}");
        var exact = (await byHash).Where(r => r.HashMatch).ToList();
        return [.. exact.Concat((await byName).Where(r => exact.All(e => e.FileId != r.FileId))).Take(20)];
    }

    static async Task<List<Result>> FetchAsync(string query)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(query));
        return [.. doc.RootElement.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("attributes"))
            .Where(a => a.TryGetProperty("files", out var f) && f.GetArrayLength() > 0)
            .Select(a => new Result(
                a.GetProperty("files")[0].GetProperty("file_id").GetInt64(),
                a.TryGetProperty("release", out var r) && r.GetString() is { Length: > 0 } rel ? rel : a.GetProperty("files")[0].GetProperty("file_name").GetString() ?? "",
                a.GetProperty("language").GetString() ?? "",
                a.TryGetProperty("download_count", out var dc) ? dc.GetInt32() : 0,
                a.TryGetProperty("moviehash_match", out var m) && m.ValueKind == JsonValueKind.True))
            .OrderByDescending(x => x.Downloads)];
    }

    /// <summary>Downloads the subtitle next to the video ("Movie.ru.srt") and returns its path.</summary>
    public static async Task<string> DownloadAsync(Result r, string video)
    {
        using var res = await Http.PostAsync("download", new StringContent(JsonSerializer.Serialize(new { file_id = r.FileId }), Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var link = doc.RootElement.GetProperty("link").GetString()!;
        var ext = Path.GetExtension(doc.RootElement.TryGetProperty("file_name", out var fn) ? fn.GetString() ?? ".srt" : ".srt");
        var path = Path.ChangeExtension(video, $"{r.Language}{(ext.Length > 0 ? ext : ".srt")}");
        await File.WriteAllBytesAsync(path, await Http.GetByteArrayAsync(link));
        return path;
    }

    static readonly System.Text.RegularExpressions.Regex Noise = new(
        @"\b(\d{3,4}p|[48]k|x26[45]|h\.?26[45]|hevc|av1|web-?(dl|rip)|bluray|brrip|bdrip|hdtv|hdrip|dvdrip|aac|ac3|dts|ddp?5\.1|surround|hdr|10bit|proper|repack)\b.*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The file name as people search it: words, no release tags ("Big Buck Bunny", "Show S01E02").</summary>
    static string Query(string video)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(video), @"[._\[\]()]+", " ");
        return System.Text.RegularExpressions.Regex.Replace(Noise.Replace(name, ""), @"\s+", " ").Trim().ToLowerInvariant();
    }

    /// <summary>OpenSubtitles hash: file size plus the 64-bit sums of its first and last 64 KB.</summary>
    static string? Hash(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            if (f.Length < 131072) return null;
            ulong sum = (ulong)f.Length;
            var buf = new byte[65536];
            foreach (var at in new[] { 0, f.Length - 65536 })
            {
                f.Position = at;
                f.ReadExactly(buf);
                for (var i = 0; i < buf.Length; i += 8) sum += BitConverter.ToUInt64(buf, i);
            }
            return sum.ToString("x16");
        }
        catch (IOException) { return null; }
    }
}
