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
    public const string ApiKey = "";

    public static bool Possible => ApiKey.Length > 0;

    public record Result(long FileId, string Name, string Language, int Downloads, bool HashMatch);

    static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api.opensubtitles.com/api/v1/"), Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "Api-Key", ApiKey }, { "User-Agent", $"Pylon v{Updater.Current.ToString(3)}" } },
    };

    public static async Task<List<Result>> SearchAsync(string video, string languages)
    {
        var q = $"subtitles?languages={Uri.EscapeDataString(languages)}&query={Uri.EscapeDataString(Path.GetFileNameWithoutExtension(video))}";
        if (Hash(video) is { } hash) q += "&moviehash=" + hash;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(q));
        return [.. doc.RootElement.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("attributes"))
            .Where(a => a.TryGetProperty("files", out var f) && f.GetArrayLength() > 0)
            .Select(a => new Result(
                a.GetProperty("files")[0].GetProperty("file_id").GetInt64(),
                a.TryGetProperty("release", out var r) && r.GetString() is { Length: > 0 } rel ? rel : a.GetProperty("files")[0].GetProperty("file_name").GetString() ?? "",
                a.GetProperty("language").GetString() ?? "",
                a.TryGetProperty("download_count", out var dc) ? dc.GetInt32() : 0,
                a.TryGetProperty("moviehash_match", out var m) && m.ValueKind == JsonValueKind.True))
            .OrderByDescending(x => x.HashMatch).ThenByDescending(x => x.Downloads).Take(20)];
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
