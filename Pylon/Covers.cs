using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pylon;

/// <summary>
/// Cover art for songs that carry none: the iTunes Search API (free, no key) by artist and album or title, 600 px,
/// kept in %AppData%\Pylon\covers. Only with "Fetch titles and cover art from the internet" on.
/// </summary>
public static class Covers
{
    static readonly string Dir = Path.Combine(Library.Dir, "covers");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10), DefaultRequestHeaders = { { "User-Agent", "Pylon (https://github.com/MeowKusTrue/Pylon)" } } };

    /// <summary>A local image file, or null.</summary>
    public static async Task<string?> FindAsync(string? artist, string title, string? album)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return null;
        var what = string.IsNullOrWhiteSpace(album) ? title : album;
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{artist}|{what}".ToLowerInvariant())))[..20];
        var file = Path.Combine(Dir, key + ".jpg");
        var miss = Path.Combine(Dir, key + ".none");
        if (File.Exists(file)) return file;
        if (File.Exists(miss)) return null;
        try
        {
            var entity = string.IsNullOrWhiteSpace(album) ? "song" : "album";
            var url = $"https://itunes.apple.com/search?media=music&entity={entity}&limit=10&term={Uri.EscapeDataString($"{artist} {what}")}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            // the artist has to match: a cover of somebody else's song is worse than none
            var hit = doc.RootElement.GetProperty("results").EnumerateArray()
                .FirstOrDefault(r => r.TryGetProperty("artistName", out var a) && Same(a.GetString(), artist) && r.TryGetProperty("artworkUrl100", out _));
            Directory.CreateDirectory(Dir);
            var art = hit.ValueKind == JsonValueKind.Object ? hit.GetProperty("artworkUrl100").GetString()!.Replace("100x100bb", "600x600bb") : await SpotifyArtAsync(artist, title);
            if (art == null) { await File.WriteAllTextAsync(miss, ""); return null; }
            await File.WriteAllBytesAsync(file, await Http.GetByteArrayAsync(art));
            return file;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or KeyNotFoundException) { return null; } // offline: try next time
    }

    /// <summary>
    /// Spotify has nearly every song: its id comes from ListenBrainz (the same lookup the lyrics use) and its public oEmbed
    /// answer names the cover (no key). 640 px instead of the 300 px thumbnail.
    /// </summary>
    static async Task<string?> SpotifyArtAsync(string artist, string title)
    {
        foreach (var id in await SpicyLyrics.SpotifyIdsAsync(title, artist))
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://open.spotify.com/oembed?url=" + Uri.EscapeDataString("https://open.spotify.com/track/" + id)));
            if (doc.RootElement.TryGetProperty("thumbnail_url", out var t) && t.GetString() is { Length: > 0 } url) return url.Replace("ab67616d00001e02", "ab67616d0000b273");
        }
        return null;
    }

    /// <summary>The same artist, also across scripts: iTunes often spells Russian names in Latin ("Лолита" = "Lolita").</summary>
    public static bool Same(string? a, string b)
    {
        static string N(string s) => new([.. Latin(s.ToLowerInvariant()).Where(char.IsLetterOrDigit)]);
        return a != null && (N(a).Contains(N(b)) || N(b).Contains(N(a))) && N(a).Length > 0;
    }

    // ponytail: Russian / Ukrainian letters only; other scripts compare as they are
    static readonly string[] Lat = ["a", "b", "v", "g", "d", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "h", "ts", "ch", "sh", "sch", "", "y", "", "e", "yu", "ya"];

    static string Latin(string s) => string.Concat(s.Select(c => c switch
    {
        >= 'а' and <= 'я' => Lat[c - 'а'],
        'ё' or 'є' => "e", 'і' or 'ї' => "i",
        _ => c.ToString(),
    }));
}
