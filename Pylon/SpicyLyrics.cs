using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pylon;

/// <summary>
/// Spicy Lyrics' public API (api.spicylyrics.org, key from developers.spicylyrics.org). It answers only by Spotify track id,
/// so the id comes from the file's tags (spotdl and many download bots store the Spotify link), from ListenBrainz's
/// metadata → Spotify mapping (free, no key), or from the Spotify Web API when the user added a developer.spotify.com app.
/// </summary>
public static class SpicyLyrics
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12), DefaultRequestHeaders = { { "User-Agent", "Pylon (https://github.com/MeowKusTrue/Pylon)" } } };
    static readonly Regex SpotifyLink = new(@"(?:open\.spotify\.com/(?:intl-[a-z]+/)?track/|spotify:track:)([A-Za-z0-9]{22})", RegexOptions.Compiled);
    static string? _token;
    static DateTime _tokenUntil;

    /// <summary>
    /// Pylon's publishable key ("No Origin header" on, as Spicy's docs say for native clients): public by design, so every
    /// copy works without setup. A key in settings wins.
    /// </summary>
    const string BuiltInKey = "";

    static string Key => Settings.Str("lyrics.spicykey").Trim() is { Length: > 0 } own ? own : BuiltInKey;

    public static bool Enabled => Key.Length > 0;

    /// <summary>The Spicy Lyrics JSON for the track (raw, for the cache), or null.</summary>
    public static async Task<string?> FetchAsync(string path, string title, string artist, double duration, string tags)
    {
        var linked = SpotifyLink.Match(tags) is { Success: true } m ? m.Groups[1].Value : LinkInFile(path);
        string[] ids = linked != null ? [linked] : await ListenBrainzIdsAsync(title, artist);
        if (ids.Length == 0 && await SpotifyIdAsync(title, artist, duration) is { } found) ids = [found];
        foreach (var id in ids) // one song has several ids (single, album, reissues); lyrics may sit on any of them
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.spicylyrics.org/v1/lyrics/{id}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            using var res = await Http.SendAsync(req);
            if (res.IsSuccessStatusCode) return await res.Content.ReadAsStringAsync();
            if (res.StatusCode != System.Net.HttpStatusCode.NotFound) res.EnsureSuccessStatusCode(); // bad key, rate limit: not a "no lyrics" answer
        }
        return null;
    }

    /// <summary>ListenBrainz Labs maps artist + title to Spotify ids (no key); up to three, most relevant first.</summary>
    static async Task<string[]> ListenBrainzIdsAsync(string title, string artist)
    {
        var body = JsonSerializer.Serialize(new[] { new { artist_name = artist, release_name = "", track_name = title } });
        using var res = await Http.PostAsync("https://labs.api.listenbrainz.org/spotify-id-from-metadata/json", new StringContent(body, Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode) return [];
        var hits = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        return hits.GetArrayLength() > 0 && hits[0].TryGetProperty("spotify_track_ids", out var ids)
            ? [.. ids.EnumerateArray().Select(i => i.GetString()).OfType<string>().Where(i => i.Length == 22).Take(3)] : [];
    }

    /// <summary>A Spotify link anywhere in the file's tag area (ID3 comment / WOAS, Vorbis comments, MP4 atoms): libVLC doesn't expose those.</summary>
    static string? LinkInFile(string path)
    {
        if (path.Contains("://") || !System.IO.File.Exists(path)) return null;
        try
        {
            using var f = System.IO.File.OpenRead(path);
            var head = new byte[(int)Math.Min(f.Length, 512 * 1024)];
            f.ReadExactly(head);
            if (SpotifyLink.Match(Encoding.Latin1.GetString(head)) is { Success: true } a) return a.Groups[1].Value;
            if (SpotifyLink.Match(Encoding.Unicode.GetString(head)) is { Success: true } u) return u.Groups[1].Value; // UTF-16 ID3 frames
            if (f.Length > head.Length) // tags at the end (ID3v1, APE, some MP4 files)
            {
                var tail = new byte[(int)Math.Min(f.Length - head.Length, 256 * 1024)];
                f.Seek(-tail.Length, System.IO.SeekOrigin.End);
                f.ReadExactly(tail);
                if (SpotifyLink.Match(Encoding.Latin1.GetString(tail)) is { Success: true } t) return t.Groups[1].Value;
            }
        }
        catch (System.IO.IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    static async Task<string?> SpotifyIdAsync(string title, string artist, double duration)
    {
        if (await SpotifyTokenAsync() is not { } token) return null;
        // Deezer's free search knows the recording's ISRC, which is an exact key on Spotify
        var isrc = await DeezerIsrcAsync(title, artist, duration);
        var q = isrc != null ? $"isrc:{isrc}" : $"track:{title} artist:{artist}";
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/search?type=track&limit=5&q=" + Uri.EscapeDataString(q));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        var items = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("tracks").GetProperty("items").EnumerateArray().ToList();
        double Off(JsonElement t) => duration > 0 && t.TryGetProperty("duration_ms", out var d) ? Math.Abs(d.GetDouble() / 1000 - duration) : 0;
        var best = items.OrderBy(Off).FirstOrDefault();
        return best.ValueKind == JsonValueKind.Object && (isrc != null || Off(best) <= 4) ? best.GetProperty("id").GetString() : null;
    }

    static async Task<string?> DeezerIsrcAsync(string title, string artist, double duration)
    {
        var q = Uri.EscapeDataString($"artist:\"{artist}\" track:\"{title}\"");
        var list = JsonDocument.Parse(await Http.GetStringAsync($"https://api.deezer.com/search?limit=10&q={q}")).RootElement;
        if (!list.TryGetProperty("data", out var data)) return null;
        var hits = data.EnumerateArray().Where(e => e.TryGetProperty("isrc", out _)).ToList();
        double Off(JsonElement e) => duration > 0 ? Math.Abs(e.GetProperty("duration").GetDouble() - duration) : 0;
        var best = hits.OrderBy(Off).FirstOrDefault();
        return best.ValueKind == JsonValueKind.Object && Off(best) <= 4 ? best.GetProperty("isrc").GetString() : null;
    }

    /// <summary>Client-credentials token (no user login): enough for search.</summary>
    static async Task<string?> SpotifyTokenAsync()
    {
        if (_token != null && DateTime.UtcNow < _tokenUntil) return _token;
        var id = Settings.Str("lyrics.spotifyid").Trim();
        var secret = Settings.Str("lyrics.spotifysecret").Trim();
        if (id.Length == 0 || secret.Length == 0) return null;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}")));
        using var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode) return null;
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        _token = json.GetProperty("access_token").GetString();
        _tokenUntil = DateTime.UtcNow.AddSeconds(json.GetProperty("expires_in").GetDouble() - 60);
        return _token;
    }

    // ───────── response → lines ─────────

    /// <summary>Spicy's { Body: { Type, Content, source, UploadAttribution } } → our lyrics, with the attribution the API asks for.</summary>
    public static LyricsDoc? Parse(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        if (!root.TryGetProperty("Body", out var body) || !body.TryGetProperty("Content", out var content)) return null;
        var type = Str(body, "Type");
        var lines = new List<LyricLine>();
        foreach (var c in content.EnumerateArray())
        {
            if (Str(c, "Type") is { } t && t != "Vocal") continue;
            var opposite = c.TryGetProperty("OppositeAligned", out var o) && o.ValueKind == JsonValueKind.True;
            if (c.TryGetProperty("Lead", out var lead) && lead.TryGetProperty("Syllables", out var syl))
            {
                var words = Syllables(syl);
                if (words.Length == 0) continue;
                var bg = c.TryGetProperty("Background", out var b) && b.ValueKind == JsonValueKind.Array
                    ? b.EnumerateArray().Where(x => x.TryGetProperty("Syllables", out _)).SelectMany(x => Syllables(x.GetProperty("Syllables"))).ToArray() : [];
                var text = string.Concat(words.Select(w => w.Joined ? w.Text : w.Text + " ")).Trim();
                lines.Add(new LyricLine(Num(lead, "StartTime") ?? words[0].Start, Num(lead, "EndTime") ?? words[^1].End, text,
                    type == "Syllable" ? words : [], opposite, type == "Syllable" ? bg : []));
            }
            else if (Str(c, "Text") is { Length: > 0 } text)
                lines.Add(new LyricLine(Num(c, "StartTime") ?? 0, Num(c, "EndTime") ?? 0, text, [], opposite, []));
        }
        if (lines.Count == 0 && body.TryGetProperty("Lines", out var plain)) // "Static": plain text, no timings
            lines.AddRange(plain.EnumerateArray().Select(l => Str(l, "Text")).Where(t => t != null).Select(t => new LyricLine(0, 0, t!, [], false, [])));
        if (lines.Count == 0) return null;

        var credits = new List<(string Text, string? Url)>
        {
            (Str(body, "source") switch { "apple_music" => "Apple Music", "spotify" => "Spotify", "musixmatch" => "Musixmatch", _ => "Spicy Lyrics" }, "https://spicylyrics.org"),
        };
        if (Str(body, "source") == "spicy_lyrics" && body.TryGetProperty("UploadAttribution", out var up))
            foreach (var (role, label) in new[] { ("Uploader", "uploaded by"), ("Maker", "synced by") })
                if (up.TryGetProperty(role, out var who) && Str(who, "username") is { Length: > 0 } name)
                    credits.Add((L.F(label + " {0}", name), Str(who, "url")));
        return new LyricsDoc([.. lines.OrderBy(l => l.Start)], type != "Static", "Spicy Lyrics", credits);
    }

    static Syllable[] Syllables(JsonElement list) => [.. list.EnumerateArray()
        .Where(s => Str(s, "Text") is { Length: > 0 })
        .Select(s => new Syllable(Num(s, "StartTime") ?? 0, Num(s, "EndTime") ?? 0, Str(s, "Text")!.Trim(), s.TryGetProperty("IsPartOfWord", out var p) && p.ValueKind == JsonValueKind.True))];

    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
