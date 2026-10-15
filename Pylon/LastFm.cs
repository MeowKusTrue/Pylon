using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pylon;

/// <summary>
/// Last.fm scrobbling. Signing in uses Last.fm's desktop flow: the browser asks the user to allow Pylon, no password ever
/// goes through Pylon; the session key it gets is kept in settings. Calls are signed with md5(params + secret).
/// </summary>
public static class LastFm
{
    /// <summary>Pylon's API account from last.fm/api/account/create. Empty = the feature stays hidden.</summary>
    public const string ApiKey = "846e998a76df8dd3f80fa7619239781b", Secret = "f4b8d05fdd0e12533fcf8b4a99df4f41"; // desktop apps ship both, as Last.fm expects

    public static bool Possible => ApiKey.Length > 0 && Secret.Length > 0;
    public static bool SignedIn => Settings.Str("lastfm.session").Length > 0;

    const string Api = "https://ws.audioscrobbler.com/2.0/";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15), DefaultRequestHeaders = { { "User-Agent", "Pylon (https://github.com/MeowKusTrue/Pylon)" } } };

    /// <summary>Opens Last.fm in the browser to allow Pylon, then waits (up to 3 minutes) for the user to do it.</summary>
    public static async Task<bool> ConnectAsync()
    {
        if (await CallAsync("auth.getToken", [], post: false) is not { } t || Str(t, "token") is not { } token) return false;
        Process.Start(new ProcessStartInfo($"https://www.last.fm/api/auth/?api_key={ApiKey}&token={token}") { UseShellExecute = true });
        for (var i = 0; i < 90; i++)
        {
            await Task.Delay(2000);
            if (await CallAsync("auth.getSession", new() { ["token"] = token }, post: false) is { } s && s.TryGetProperty("session", out var session)
                && Str(session, "key") is { } key)
            {
                Settings.Set("lastfm.session", key);
                Settings.Set("lastfm.user", Str(session, "name") ?? "");
                return true;
            }
        }
        return false;
    }

    public static void Disconnect()
    {
        Settings.Set("lastfm.session", "");
        Settings.Set("lastfm.user", "");
    }

    public static Task NowPlayingAsync(string artist, string track, string? album, double seconds) =>
        CallAsync("track.updateNowPlaying", Track(artist, track, album, seconds), post: true);

    public static Task ScrobbleAsync(string artist, string track, string? album, double seconds, DateTime startedUtc) =>
        CallAsync("track.scrobble", new(Track(artist, track, album, seconds)) { ["timestamp"] = new DateTimeOffset(startedUtc).ToUnixTimeSeconds().ToString() }, post: true);

    static Dictionary<string, string> Track(string artist, string track, string? album, double seconds)
    {
        var p = new Dictionary<string, string> { ["artist"] = artist, ["track"] = track };
        if (!string.IsNullOrWhiteSpace(album)) p["album"] = album;
        if (seconds > 0) p["duration"] = ((int)seconds).ToString();
        return p;
    }

    /// <summary>A signed call; null when it failed (offline, revoked access…). Scrobbling never interrupts playback.</summary>
    static async Task<JsonElement?> CallAsync(string method, Dictionary<string, string> p, bool post)
    {
        if (!Possible) return null;
        p["method"] = method;
        p["api_key"] = ApiKey;
        if (method.StartsWith("track.")) p["sk"] = Settings.Str("lastfm.session");
        var signature = string.Concat(p.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + kv.Value)) + Secret;
        p["api_sig"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
        p["format"] = "json";
        try
        {
            using var res = post
                ? await Http.PostAsync(Api, new FormUrlEncodedContent(p))
                : await Http.GetAsync(Api + "?" + string.Join("&", p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")));
            var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
            if (json.TryGetProperty("error", out var code) && code.GetInt32() == 9) Disconnect(); // session revoked on the website
            return json.TryGetProperty("error", out _) ? null : json;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
