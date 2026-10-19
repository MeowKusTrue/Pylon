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
    public static bool Handles(string url) => url.StartsWith("ytsearch://") ||
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

    public static bool IsPlaylistPage(string url) =>
        url.Contains("list=") && !url.Contains("watch?v=") || url.Contains("/playlist") || url.Contains("/sets/")
        || url.Contains("youtube.com/@") || url.Contains("/channel/") || url.Contains("/videos");

    /// <summary>Stream addresses for one item, best quality up to <paramref name="maxHeight"/> (0 = no limit); null when yt-dlp can't.</summary>
    public static async Task<Stream?> ResolveAsync(string url, int maxHeight, bool audioOnly)
    {
        // "ytsearch://Artist - Title" (Spotify imports): the first YouTube result, sound only
        if (url.StartsWith("ytsearch://")) (url, audioOnly) = ("ytsearch1:" + Uri.UnescapeDataString(url["ytsearch://".Length..]), true);
        // H.264 first (hardware decoding everywhere), then whatever is best; m4a audio pairs with it in one MP4 world
        var sort = audioOnly ? "acodec:m4a" : $"res{(maxHeight > 0 ? ":" + maxHeight : "")},vcodec:avc1,acodec:m4a";
        using var doc = await RunJson("--no-playlist", "-f", audioOnly ? "ba/b" : "bv*+ba/b", "-S", sort, url);
        if (doc?.RootElement is not { } d) return null;
        // a search answers with a playlist around what it found: the stream addresses are in its first entry. Reading them
        // from the wrapper found nothing, so every song of a Spotify import was "not found on YouTube"
        if (Str(d, "_type") == "playlist")
        {
            if (!d.TryGetProperty("entries", out var found) || found.ValueKind != JsonValueKind.Array || found.GetArrayLength() == 0) return null;
            d = found[0];
        }
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

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Stream S, DateTime Until)> Ready = new();
    static readonly Dictionary<string, Task<Stream?>> Pending = [];

    /// <summary>
    /// What a queue item plays from: a stream resolved earlier if it's still good (looked up ahead, see the player's
    /// lookahead), else resolved now. A song from a Spotify import is the official release on YouTube Music (found by ISRC
    /// and checked by artist and length, remembered for next time), not the first video a search turns up. Playback and
    /// the lookahead asking for the same item share one yt-dlp run. Called on the UI thread (the library index isn't
    /// thread-safe).
    /// </summary>
    public static Task<Stream?> ResolveItemAsync(string src, int maxHeight, bool audioOnly)
    {
        // a song (Spotify import, radio, search, a YouTube Music link) is music: its sound only, played as a song with
        // lyrics. Swapping the search for the exact video lost this, and songs opened as clips without lyrics
        if (IsSong(src)) audioOnly = true;
        if (Ready.TryGetValue(src, out var r) && r.Until > DateTime.UtcNow) return Task.FromResult<Stream?>(r.S);
        if (Pending.TryGetValue(src, out var running)) return running;
        var task = Run();
        Pending[src] = task;
        return task;

        async Task<Stream?> Run()
        {
            try
            {
                var url = await PageOf(src);
                var meta = src.StartsWith("ytsearch://") ? Library.Data.Meta.GetValueOrDefault(src) : null;
                var s = await ResolveAsync(url, maxHeight, audioOnly);
                if (s == null && meta?.YouTubeId != null) { meta.YouTubeId = null; s = await ResolveAsync(src, maxHeight, audioOnly); } // the video went away: search again
                if (s is { Live: false }) Ready[src] = (s, DateTime.UtcNow + Lifetime(s.Url));
                return s;
            }
            finally { Pending.Remove(src); }
        }
    }

    /// <summary>A song rather than a video: searched by name (Spotify, radio, search) or on music.youtube.com.</summary>
    public static bool IsSong(string src) => src.StartsWith("ytsearch://") || src.Contains("://music.youtube.com/");

    /// <summary>A resolved address works until its "expire" (a few hours on YouTube); 10 minutes of margin, else an hour.</summary>
    static TimeSpan Lifetime(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]expire=(\d+)");
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out var unix)) return TimeSpan.FromHours(1);
        var left = DateTimeOffset.FromUnixTimeSeconds(unix) - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
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
        Common(psi);
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

    /// <summary>What every run needs: the JavaScript runtime, the browser's sign-in, the proxy.</summary>
    static void Common(ProcessStartInfo psi)
    {
        if (JsRuntime is { } js and not "deno") { psi.ArgumentList.Add("--js-runtimes"); psi.ArgumentList.Add(js); }
        // a signed-in browser gets past "confirm you're not a bot" and age checks
        if (Settings.Str("net.cookies") is { Length: > 0 } browser && browser != "Off") { psi.ArgumentList.Add("--cookies-from-browser"); psi.ArgumentList.Add(browser.ToLowerInvariant()); }
        AddProxy(psi, Proxy); // the same one StreamRelay uses
    }

    /// <summary>
    /// The page a queue item plays from: a web song's exact YouTube Music video (found once by ISRC or name, then
    /// remembered), else the item itself. UI thread (the library index isn't thread-safe).
    /// </summary>
    public static async Task<string> PageOf(string src)
    {
        if (!src.StartsWith("ytsearch://") || Library.Data.Meta.GetValueOrDefault(src) is not { Title: { } title, Artist: { } artist } m) return src;
        var id = m.YouTubeId ?? await YtMusic.FindAsync(title, artist, m.DurationMs / 1000.0, m.Isrc);
        if (id != null && m.YouTubeId != id) { m.YouTubeId = id; Library.Touch(); }
        return id != null ? "https://music.youtube.com/watch?v=" + id : src;
    }

    /// <summary>
    /// A song's sound saved as <paramref name="stem"/>.m4a (or the best audio there is), as the site has it: no
    /// re-encoding, no ffmpeg. The saved file's path; null when it failed.
    /// </summary>
    public static async Task<string?> DownloadAudioAsync(string page, string stem)
    {
        if (page.StartsWith("ytsearch://")) page = "ytsearch1:" + Uri.UnescapeDataString(page["ytsearch://".Length..]);
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--no-warnings", "--no-progress", "--no-playlist", "-f", "ba[ext=m4a]/ba", "-o", stem + ".%(ext)s", "--print", "after_move:filepath" })
            psi.ArgumentList.Add(a);
        Common(psi);
        psi.ArgumentList.Add(page);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try { await p.WaitForExitAsync(cancel.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); return null; }
            _ = await stderr;
            var file = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            return p.ExitCode == 0 && file != null && File.Exists(file) ? file : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>Downloads the official yt-dlp.exe (about 18 MB); false on failure.</summary>
    public static async Task<bool> InstallAsync(IProgress<double>? progress)
    {
        var tmp = Exe + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Exe)!);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestHeaders = { { "User-Agent", "Pylon" } } };
            // a stalled connection gives up after 20 s without data (it used to sit there for 5 minutes)
            using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using (var res = await http.GetAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", HttpCompletionOption.ResponseHeadersRead, stall.Token))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? 0;
                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = File.Create(tmp);
                var buf = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, stall.Token)) > 0)
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(20));
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            File.Move(tmp, Exe, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
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

    /// <summary>
    /// The proxy web pages are fetched through: Pylon's own setting, else HTTPS_PROXY / HTTP_PROXY, else the Windows proxy
    /// (what a VPN client like v2rayN switches on). Null: straight out.
    /// </summary>
    public static string? Proxy
    {
        get
        {
            static string Url(string p) => p.Contains("://") ? p : "http://" + p;
            if (Settings.Str("net.proxymode") == "Off") return null;
            if (Settings.Str("net.proxy").Trim() is { Length: > 0 } own) return WithPassword(Url(own));
            foreach (var v in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
                if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } env) return Url(env);
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (k?.GetValue("ProxyEnable") is not 1 || k.GetValue("ProxyServer") is not string server || server.Length == 0) return null;
            // "host:port", or per protocol: "http=host:port;https=host:port;socks=host:port"
            var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var pick = parts.FirstOrDefault(p => p.StartsWith("https=")) ?? parts.FirstOrDefault(p => p.StartsWith("http=")) ?? parts.FirstOrDefault(p => !p.Contains('='));
            if (pick != null) return Url(pick[(pick.IndexOf('=') + 1)..]);
            return parts.FirstOrDefault(p => p.StartsWith("socks=")) is { } socks ? "socks5://" + socks[6..] : null;
        }
    }

    /// <summary>yt-dlp reads HTTPS_PROXY by itself: "Off" has to say so, or it went through the proxy anyway.</summary>
    static void AddProxy(ProcessStartInfo psi, string? proxy)
    {
        if (proxy == null && Settings.Str("net.proxymode") != "Off") return;
        psi.ArgumentList.Add("--proxy");
        psi.ArgumentList.Add(proxy ?? "");
    }

    /// <summary>"socks5://user@host:1080" plus the separate password field: "socks5://user:password@host:1080".</summary>
    static string WithPassword(string url)
    {
        if (Settings.Str("net.proxypass") is not { Length: > 0 } pass || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.UserInfo.Length == 0 || u.UserInfo.Contains(':')) return url;
        return $"{u.Scheme}://{u.UserInfo}:{Uri.EscapeDataString(pass)}@{u.Authority[(u.Authority.IndexOf('@') + 1)..]}{u.PathAndQuery.TrimEnd('/')}";
    }

    /// <summary>The proxy for .NET: it knows http, https, socks4, socks4a and socks5 (socks5 already resolves names on the proxy, like socks5h).</summary>
    public static System.Net.WebProxy WebProxy(string proxy)
    {
        var u = new Uri(proxy.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase) ? "socks5://" + proxy[10..] : proxy);
        var p = new System.Net.WebProxy(new UriBuilder(u) { UserName = "", Password = "" }.Uri);
        if (u.UserInfo.Length > 0)
        {
            var (user, pass) = u.UserInfo.IndexOf(':') is var i and >= 0 ? (u.UserInfo[..i], u.UserInfo[(i + 1)..]) : (u.UserInfo, "");
            p.Credentials = new System.Net.NetworkCredential(Uri.UnescapeDataString(user), Uri.UnescapeDataString(pass));
        }
        return p;
    }

    /// <summary>
    /// Settings → Network → Check the proxy: YouTube through it the way Pylon plays (.NET, as StreamRelay does), then yt-dlp
    /// reading a video's title through it. One line each: what worked, what didn't and why, how long it took.
    /// </summary>
    public static async Task<string> TestProxyAsync()
    {
        var proxy = Proxy;
        var lines = new List<string> { proxy == null ? L.T("Proxy: none, straight out.") : L.F("Proxy: {0}", Redact(proxy)) };
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { Proxy = proxy == null ? null : WebProxy(proxy), UseProxy = proxy != null }) { Timeout = TimeSpan.FromSeconds(12) };
            using var res = await http.GetAsync("https://www.youtube.com/generate_204");
            lines.Add(L.F("YouTube: {0} in {1} ms", (int)res.StatusCode == 204 ? L.T("reachable") : $"HTTP {(int)res.StatusCode}", sw.ElapsedMilliseconds));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or NotSupportedException or ArgumentException)
        {
            lines.Add(L.F("YouTube: not reachable ({0})", e is TaskCanceledException ? L.T("no answer in 12 s") : e.InnerException?.Message ?? e.Message));
        }
        if (!Available) { lines.Add(L.T("yt-dlp: not downloaded yet (it comes with the first website you open).")); return string.Join("\n", lines); }
        sw.Restart();
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "--simulate", "--no-warnings", "--no-playlist", "--print", "title" }) psi.ArgumentList.Add(a);
        AddProxy(psi, proxy);
        if (JsRuntime is { } js and not "deno") { psi.ArgumentList.Add("--js-runtimes"); psi.ArgumentList.Add(js); }
        psi.ArgumentList.Add("https://www.youtube.com/watch?v=jNQXAC9IVRw"); // "Me at the zoo": the first video, it doesn't go away
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            try { await p.WaitForExitAsync(cancel.Token); }
            catch (OperationCanceledException) { p.Kill(entireProcessTree: true); lines.Add(L.T("yt-dlp: no answer in 40 s")); return string.Join("\n", lines); }
            var title = (await stdout).Trim();
            lines.Add(p.ExitCode == 0 && title.Length > 0 ? L.F("yt-dlp: works (“{0}”, {1} ms)", title, sw.ElapsedMilliseconds)
                : L.F("yt-dlp: {0}", (await stderr).Split('\n').LastOrDefault(l => l.StartsWith("ERROR:"))?[6..].Trim() ?? L.T("failed")));
        }
        catch (System.ComponentModel.Win32Exception e) { lines.Add(L.F("yt-dlp: {0}", e.Message)); }
        return string.Join("\n", lines);
    }

    /// <summary>The proxy address as shown: the password hidden.</summary>
    static string Redact(string proxy) => Uri.TryCreate(proxy, UriKind.Absolute, out var u) && u.UserInfo.Contains(':')
        ? proxy.Replace(u.UserInfo, u.UserInfo[..u.UserInfo.IndexOf(':')] + ":•••") : proxy;
}

/// <summary>
/// YouTube Music's own search (the InnerTube API its website uses; no key), "Songs" only: official releases from the
/// artists' Topic channels, not clips, covers or lyric videos. One request, about half a second, with title, artists,
/// album and length — yt-dlp's search took three and gave no length to check.
/// </summary>
public static class YtMusic
{
    public record Hit(string VideoId, string Title, string Artist, string? Album, double Seconds, string? Art = null);

    static HttpClient? _http;
    static string? _proxyOf;

    static HttpClient Client()
    {
        var proxy = YtDlp.Proxy;
        if (_http != null && _proxyOf == proxy) return _http;
        _http = new HttpClient(new SocketsHttpHandler { Proxy = proxy == null ? null : YtDlp.WebProxy(proxy), UseProxy = proxy != null }) { Timeout = TimeSpan.FromSeconds(10) };
        _proxyOf = proxy;
        return _http;
    }

    static readonly object Context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20250101.01.00", hl = "en", gl = "US" } };

    static async Task<JsonDocument?> PostAsync(string endpoint, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://music.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false")
            { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        using var res = await Client().SendAsync(req);
        return res.IsSuccessStatusCode ? JsonDocument.Parse(await res.Content.ReadAsStringAsync()) : null;
    }

    public static async Task<List<Hit>> SearchSongsAsync(string query)
    {
        using var doc = await PostAsync("search", new { context = Context, query, @params = "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D" }); // the "Songs" filter
        var hits = new List<Hit>();
        if (doc == null) return hits;
        foreach (var r in Find(doc.RootElement, "musicResponsiveListItemRenderer"))
        {
            var id = r.TryGetProperty("playlistItemData", out var pd) && pd.TryGetProperty("videoId", out var v) ? v.GetString() : null;
            if (id == null || !r.TryGetProperty("flexColumns", out var cols) || cols.GetArrayLength() < 2) continue;
            var title = Runs(cols[0]).FirstOrDefault().Text ?? "";
            var runs = Runs(cols[1]).ToList();
            var artists = runs.Where(x => x.Page == "MUSIC_PAGE_TYPE_ARTIST").Select(x => x.Text).ToList();
            var duration = runs.LastOrDefault(x => System.Text.RegularExpressions.Regex.IsMatch(x.Text ?? "", @"^\d+(:\d{2}){1,2}$")).Text;
            var art = Find(r, "thumbnails").FirstOrDefault() is { ValueKind: JsonValueKind.Array } th && th.GetArrayLength() > 0
                && th[th.GetArrayLength() - 1].TryGetProperty("url", out var u) ? u.GetString() : null;
            hits.Add(new Hit(id, title, artists.Count > 0 ? string.Join(", ", artists) : runs.FirstOrDefault().Text ?? "",
                runs.FirstOrDefault(x => x.Page == "MUSIC_PAGE_TYPE_ALBUM").Text,
                duration == null ? 0 : duration.Split(':').Aggregate(0.0, (acc, part) => acc * 60 + int.Parse(part)),
                art == null ? null : System.Text.RegularExpressions.Regex.Replace(art, @"=w\d+-h\d+", "=w300-h300")));
        }
        return hits;
    }

    /// <summary>
    /// YouTube Music's radio for a song (its "Start radio"): about 50 similar songs, the song itself first, and a token
    /// that brings 50 more. <paramref name="more"/>: that token, for the next page.
    /// </summary>
    public static async Task<(List<Hit> Songs, string? More)> RadioAsync(string? videoId, string? more)
    {
        try
        {
            using var doc = await PostAsync("next", more != null ? new { context = Context, continuation = more }
                : new { context = Context, videoId, playlistId = "RDAMVM" + videoId, isAudioOnly = true });
            if (doc == null) return ([], null);
            var songs = new List<Hit>();
            foreach (var r in Find(doc.RootElement, "playlistPanelVideoRenderer"))
            {
                if (!r.TryGetProperty("videoId", out var id) || Text(r, "title") is not { Length: > 0 } title) continue;
                // "Artist • Album • 2007"
                var by = (Text(r, "longBylineText") ?? Text(r, "shortBylineText") ?? "").Split(" • ");
                var length = Text(r, "lengthText");
                var art = r.TryGetProperty("thumbnail", out var th) && th.TryGetProperty("thumbnails", out var ts) && ts.GetArrayLength() > 0
                    && ts[ts.GetArrayLength() - 1].TryGetProperty("url", out var u) ? u.GetString() : null;
                songs.Add(new Hit(id.GetString()!, title, by[0], by.Length > 2 ? by[1] : null,
                    length == null ? 0 : length.Split(':').Aggregate(0.0, (acc, part) => acc * 60 + (int.TryParse(part, out var n) ? n : 0)),
                    art == null ? null : System.Text.RegularExpressions.Regex.Replace(art, @"=w\d+-h\d+", "=w300-h300"))); // album covers come as 60 px
            }
            return (songs, Find(doc.RootElement, "nextRadioContinuationData").Select(c => c.TryGetProperty("continuation", out var t) ? t.GetString() : null).FirstOrDefault());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return ([], null); }

        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var t) && t.TryGetProperty("runs", out var runs)
            ? string.Concat(runs.EnumerateArray().Select(x => x.TryGetProperty("text", out var tx) ? tx.GetString() : "")) : null;
    }

    /// <summary>Every <paramref name="key"/> object in the answer, wherever it sits (YouTube moves things around).</summary>
    static IEnumerable<JsonElement> Find(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty(key, out var row)) { yield return row; yield break; }
            foreach (var p in e.EnumerateObject()) foreach (var r in Find(p.Value, key)) yield return r;
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) foreach (var r in Find(x, key)) yield return r;
    }

    static IEnumerable<(string? Text, string? Page)> Runs(JsonElement column)
    {
        if (!column.TryGetProperty("musicResponsiveListItemFlexColumnRenderer", out var c) || !c.TryGetProperty("text", out var t) || !t.TryGetProperty("runs", out var runs)) yield break;
        foreach (var run in runs.EnumerateArray())
        {
            string? page = null;
            if (run.TryGetProperty("navigationEndpoint", out var nav) && nav.TryGetProperty("browseEndpoint", out var be)
                && be.TryGetProperty("browseEndpointContextSupportedConfigs", out var cfg) && cfg.TryGetProperty("browseEndpointContextMusicConfig", out var mc)
                && mc.TryGetProperty("pageType", out var pt)) page = pt.GetString();
            yield return (run.TryGetProperty("text", out var tx) ? tx.GetString() : null, page);
        }
    }

    /// <summary>
    /// The video of a song: by ISRC first (the very recording), then by artist and title; a hit counts only when the
    /// artist matches and the length is within 4 s of Spotify's. Null when nothing fits (yt-dlp's plain search then).
    /// </summary>
    public static async Task<string?> FindAsync(string title, string artist, double seconds, string? isrc)
    {
        static string N(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var main = artist.Split(',')[0].Trim();
        bool Fits(Hit h) => Covers.Same(h.Artist, main) && (seconds <= 0 || h.Seconds <= 0 || Math.Abs(h.Seconds - seconds) <= 4);
        try
        {
            if (isrc != null && (await SearchSongsAsync(isrc)).Take(3).FirstOrDefault(Fits) is { } exact) return exact.VideoId;
            var hits = await SearchSongsAsync($"{main} {title}");
            return (hits.FirstOrDefault(h => Fits(h) && N(h.Title).Contains(N(title))) ?? hits.FirstOrDefault(Fits)
                // another cut of the same song (a remaster, the album version): still the artist's song, not a clip with an
                // intro; the player offers to shift the lyrics when its length differs
                ?? hits.FirstOrDefault(h => Covers.Same(h.Artist, main) && N(h.Title).Contains(N(title))))?.VideoId;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

/// <summary>
/// YouTube binds a stream address to the IP that asked for it, and libVLC 3's https ignores --http-proxy: an address
/// yt-dlp resolved through a proxy, played by libVLC directly, was a 403 ("couldn't find it on YouTube" although it's
/// there). With a proxy in use the stream goes through Pylon: libVLC asks 127.0.0.1, Pylon asks the site through the same
/// proxy, passing ranges on (seeking works) and the headers the site wants.
/// </summary>
public static class StreamRelay
{
    static System.Net.Sockets.TcpListener? _listener;
    static HttpClient? _http;
    static string? _proxyOf;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Url, string? UserAgent, string? Referer)> Map = new();

    /// <summary>The address libVLC should open: the original one without a proxy, else Pylon's local copy of it.</summary>
    public static string Wrap(string url, string? userAgent, string? referer)
    {
        if (YtDlp.Proxy is not { } proxy || !url.StartsWith("http")) return url;
        lock (Map)
        {
            if (_http == null || _proxyOf != proxy)
            {
                _http?.Dispose();
                _http = new HttpClient(new SocketsHttpHandler { Proxy = YtDlp.WebProxy(proxy), UseProxy = true, AutomaticDecompression = System.Net.DecompressionMethods.None })
                    { Timeout = Timeout.InfiniteTimeSpan };
                _proxyOf = proxy;
            }
            if (_listener == null)
            {
                _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                _listener.Start();
                _ = Serve(_listener);
            }
        }
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
        Map[id] = (url, userAgent, referer);
        if (Map.Count > 200) foreach (var old in Map.Keys.Take(100)) Map.TryRemove(old, out _); // addresses expire within hours anyway
        return $"http://127.0.0.1:{((System.Net.IPEndPoint)_listener!.LocalEndpoint).Port}/{id}";
    }

    static async Task Serve(System.Net.Sockets.TcpListener listener)
    {
        while (true)
        {
            System.Net.Sockets.TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception e) when (e is ObjectDisposedException or System.Net.Sockets.SocketException) { return; }
            _ = Task.Run(() => Answer(client));
        }
    }

    static async Task Answer(System.Net.Sockets.TcpClient client)
    {
        using (client)
        using (var net = client.GetStream())
        {
            try
            {
                using var reader = new StreamReader(net, System.Text.Encoding.ASCII, false, 4096, leaveOpen: true);
                var request = (await reader.ReadLineAsync())?.Split(' ');
                string? range = null;
                for (string? h; (h = await reader.ReadLineAsync()) is { Length: > 0 };)
                    if (h.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = h[6..].Trim();
                if (request is not [var method, var path, ..] || !Map.TryGetValue(path.TrimStart('/'), out var target)) { await Reply(net, "404 Not Found", null); return; }
                using var ask = new HttpRequestMessage(method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, target.Url);
                if (range != null) ask.Headers.TryAddWithoutValidation("Range", range);
                if (target.UserAgent != null) ask.Headers.TryAddWithoutValidation("User-Agent", target.UserAgent);
                if (target.Referer != null) ask.Headers.TryAddWithoutValidation("Referer", target.Referer);
                using var res = await _http!.SendAsync(ask, HttpCompletionOption.ResponseHeadersRead);
                var head = new System.Text.StringBuilder($"HTTP/1.1 {(int)res.StatusCode} {res.ReasonPhrase}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n");
                foreach (var name in new[] { "Content-Type", "Content-Length", "Content-Range" })
                    if (res.Content.Headers.TryGetValues(name, out var values)) head.Append($"{name}: {string.Join(",", values)}\r\n");
                head.Append("\r\n");
                await net.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head.ToString()));
                if (method != "HEAD") await (await res.Content.ReadAsStreamAsync()).CopyToAsync(net);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException or ObjectDisposedException) { } // libVLC hung up (a seek) or the site did
        }
    }

    static async Task Reply(Stream net, string status, string? body)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(body ?? "");
        await net.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {b.Length}\r\nConnection: close\r\n\r\n"));
        await net.WriteAsync(b);
    }
}
