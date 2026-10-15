using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Pylon;

/// <summary>
/// YouTube and the other sites yt-dlp knows (Twitch, Vimeo, SoundCloud, VK, Rutube, TikTok…): yt-dlp turns the page into
/// direct stream addresses each time an item plays (they expire after a few hours). yt-dlp.exe is not shipped; it is
/// downloaded from its GitHub releases when the user agrees, into %LocalAppData%\Pylon\tools, and keeps itself updated.
/// </summary>
public static class YtDlp
{
    /// <summary>PYLON_YTDLP points at another yt-dlp (tests).</summary>
    public static string Exe => Environment.GetEnvironmentVariable("PYLON_YTDLP")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pylon", "tools", "yt-dlp.exe");

    public static bool Available => File.Exists(Exe);

    /// <summary>The video (or the only stream), audio to play alongside, and what the site wants to see in the request.</summary>
    public record Stream(string Url, string? Audio, string Title, string? Artist, string? Thumbnail, string? UserAgent, string? Referer, bool Live, bool HasVideo);

    static readonly string[] MediaExt = [.. MainWindow.VideoExt, .. MainWindow.AudioExt, ".m3u8", ".mpd", ".m3u", ".pls", ".xspf"];

    /// <summary>A web page rather than a media file or stream: worth asking yt-dlp.</summary>
    public static bool Handles(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
        && !MediaExt.Contains(Path.GetExtension(u.AbsolutePath), StringComparer.OrdinalIgnoreCase);

    /// <summary>A playlist / channel page becomes its items (page addresses, resolved when they play); null when it isn't one.</summary>
    public static async Task<List<(string Url, string Title)>?> ExpandAsync(string url)
    {
        if (!IsPlaylistPage(url)) return null;
        using var doc = await RunJson("--flat-playlist", "--playlist-end", "500", url);
        if (doc?.RootElement is not { } root || Str(root, "_type") != "playlist" || !root.TryGetProperty("entries", out var entries)) return null;
        return [.. entries.EnumerateArray()
            .Select(e => (Url: Str(e, "url") ?? Str(e, "webpage_url"), Title: Str(e, "title") ?? ""))
            .Where(e => e.Url is { Length: > 0 } && e.Url.StartsWith("http"))
            .Select(e => (e.Url!, e.Title))];
    }

    static bool IsPlaylistPage(string url) =>
        url.Contains("list=") && !url.Contains("watch?v=") || url.Contains("/playlist") || url.Contains("/sets/")
        || url.Contains("youtube.com/@") || url.Contains("/channel/") || url.Contains("/videos");

    /// <summary>Stream addresses for one item, best quality up to <paramref name="maxHeight"/> (0 = no limit); null when yt-dlp can't.</summary>
    public static async Task<Stream?> ResolveAsync(string url, int maxHeight, bool audioOnly)
    {
        // H.264 first (hardware decoding everywhere), then whatever is best; m4a audio pairs with it in one MP4 world
        var sort = audioOnly ? "acodec:m4a" : $"res{(maxHeight > 0 ? ":" + maxHeight : "")},vcodec:avc1,acodec:m4a";
        using var doc = await RunJson("--no-playlist", "-f", audioOnly ? "ba/b" : "bv*+ba/b", "-S", sort, url);
        if (doc?.RootElement is not { } d) return null;
        string? video, audio = null;
        JsonElement headersFrom = d;
        if (d.TryGetProperty("requested_formats", out var rf) && rf.GetArrayLength() >= 2)
        {
            video = Str(rf[0], "url");
            audio = Str(rf[1], "url");
            headersFrom = rf[0];
        }
        else video = Str(d, "url");
        if (video == null) return null;
        var headers = headersFrom.TryGetProperty("http_headers", out var h) ? h : default;
        return new Stream(video, audio, Str(d, "title") ?? url, Str(d, "artist") ?? Str(d, "uploader") ?? Str(d, "channel"), Str(d, "thumbnail"),
            headers.ValueKind == JsonValueKind.Object ? Str(headers, "User-Agent") : null,
            headers.ValueKind == JsonValueKind.Object ? Str(headers, "Referer") : null,
            d.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True,
            !audioOnly && (audio != null || Str(d, "vcodec") is not (null or "none")));
    }

    /// <summary>yt-dlp's own words for the last failure ("Sign in to confirm you're not a bot", "Video unavailable"…).</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// YouTube needs a JavaScript runtime since 2025; yt-dlp only looks for Deno by itself, so Node or Bun is passed on
    /// when that's what is installed.
    /// </summary>
    static readonly string? JsRuntime = new[] { "deno", "node", "bun" }.FirstOrDefault(r =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d.Trim(), r + ".exe"))));

    static async Task<JsonDocument?> RunJson(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-J", "--no-warnings", "--no-progress" }.Concat(args.SkipLast(1))) psi.ArgumentList.Add(a);
        if (JsRuntime is { } js and not "deno") { psi.ArgumentList.Add("--js-runtimes"); psi.ArgumentList.Add(js); }
        // a signed-in browser gets past "confirm you're not a bot" and age checks
        if (Settings.Str("net.cookies") is { Length: > 0 } browser && browser != "Off") { psi.ArgumentList.Add("--cookies-from-browser"); psi.ArgumentList.Add(browser.ToLowerInvariant()); }
        if (Settings.Str("net.proxy") is { Length: > 0 } proxy) { psi.ArgumentList.Add("--proxy"); psi.ArgumentList.Add(proxy.Contains("://") ? proxy : "http://" + proxy); }
        psi.ArgumentList.Add(args[^1]);
        LastError = null;
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync(); // drained so a chatty run can't block on a full pipe
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await p.WaitForExitAsync(cancel.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); return null; }
            var json = await stdout;
            if (p.ExitCode == 0 && json.Length > 0) return JsonDocument.Parse(json);
            LastError = (await stderr).Split('\n').LastOrDefault(l => l.StartsWith("ERROR:"))?[6..].Trim();
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Downloads the official yt-dlp.exe (about 18 MB); false on failure.</summary>
    public static async Task<bool> InstallAsync(IProgress<double>? progress)
    {
        var tmp = Exe + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Exe)!);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5), DefaultRequestHeaders = { { "User-Agent", "Pylon" } } };
            using (var res = await http.GetAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? 0;
                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
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
            File.Move(tmp, Exe, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            return false;
        }
    }

    /// <summary>Sites change often and yt-dlp follows: let it update itself once a day (in the background).</summary>
    public static void UpdateIfDue()
    {
        if (!Available || Environment.GetEnvironmentVariable("PYLON_YTDLP") != null) return;
        if (DateTime.TryParse(Settings.Str("app.ytdlpcheck"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) && DateTime.UtcNow - last < TimeSpan.FromDays(1)) return;
        Settings.Set("app.ytdlpcheck", DateTime.UtcNow.ToString("o"));
        try { Process.Start(new ProcessStartInfo(Exe, "-U") { UseShellExecute = false, CreateNoWindow = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
