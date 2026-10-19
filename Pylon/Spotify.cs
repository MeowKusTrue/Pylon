using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Pylon;

/// <summary>
/// Spotify playlists, albums and tracks in Pylon. Signed in to Spotify, the whole list comes from its Web API (every track,
/// its cover, ISRC and album); without an account Spotify's public embed page gives the first 50, and Deezer fills in
/// covers and ISRCs in the background. Songs already in the library play from disk, the rest from YouTube Music (the
/// official release, found by ISRC). The list remembers its link, so it follows the Spotify one (see <see cref="SyncAsync"/>).
/// </summary>
public static class SpotifyImport
{
    public record Track(string Title, string Artist, long DurationMs, string Id, string? Isrc = null, string? Art = null, string? Album = null);

    /// <summary>A Spotify list as read: its tracks, its cover's address, and whether only part of it could be read.</summary>
    public record ImportedList(string Name, List<Track> Tracks, string? Cover, bool Partial);

    static readonly Regex Link = new(@"open\.spotify\.com/(?:intl-[a-z-]+/)?(playlist|album|track)/([A-Za-z0-9]{22})", RegexOptions.Compiled);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Pylon" } } };

    public static bool IsLink(string text) => Link.IsMatch(text);

    /// <summary>The list behind a link; null when it can't be read (private, gone, no connection).</summary>
    public static async Task<ImportedList?> ReadAsync(string url)
    {
        if (Link.Match(url) is not { Success: true } m) return null;
        var (kind, id) = (m.Groups[1].Value, m.Groups[2].Value);
        if (SpotifyApi.SignedIn && await ReadWebApiAsync(kind, id) is { Tracks.Count: > 0 } full) return full;
        try
        {
            var html = await Http.GetStringAsync($"https://open.spotify.com/embed/{kind}/{id}");
            var json = Regex.Match(html, "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
            using var doc = JsonDocument.Parse(json);
            var entity = doc.RootElement.GetProperty("props").GetProperty("pageProps").GetProperty("state").GetProperty("data").GetProperty("entity");
            var name = entity.TryGetProperty("name", out var n) ? n.GetString() ?? "Spotify" : "Spotify";
            var cover = entity.TryGetProperty("coverArt", out var ca) && ca.TryGetProperty("sources", out var src) && src.GetArrayLength() > 0 ? Str(src[0], "url") : null;
            var tracks = new List<Track>();
            if (entity.TryGetProperty("trackList", out var list))
                foreach (var t in list.EnumerateArray())
                    tracks.Add(new Track(Str(t, "title"), Str(t, "subtitle").Replace(' ', ' '), t.TryGetProperty("duration", out var d) ? d.GetInt64() : 0, Str(t, "uri").Split(':').Last(),
                        Album: kind == "album" ? name : null, Art: kind == "album" ? cover : null));
            else // a single track
                tracks.Add(new Track(name, entity.TryGetProperty("artists", out var a) ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name"))) : "",
                    entity.TryGetProperty("duration", out var d1) ? d1.GetInt64() : 0, id, Art: cover));
            tracks = [.. tracks.Where(t => t.Title.Length > 0)];
            // the embed page stops at 50: a playlist that long is most likely cut
            return new ImportedList(name, tracks, string.IsNullOrEmpty(cover) ? null : cover, kind == "playlist" && tracks.Count >= 50);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// The whole list as the signed-in user, 100 tracks a page. Null when Spotify refuses (its own editorial playlists are
    /// closed to apps in development mode): the embed page is tried then.
    /// </summary>
    static async Task<ImportedList?> ReadWebApiAsync(string kind, string id)
    {
        static string? Image(JsonElement owner) => owner.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array && im.GetArrayLength() > 0
            ? Str(im.EnumerateArray().OrderBy(i => Math.Abs((i.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 640) - 300)).First(), "url") : null;
        static string Artists(JsonElement t) => t.TryGetProperty("artists", out var a) ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name"))) : "";
        static Track? FromTrack(JsonElement t, string? album = null, string? art = null)
        {
            if (t.ValueKind != JsonValueKind.Object || Str(t, "id") is not { Length: > 0 } tid) return null; // local files and removed tracks have none
            var al = t.TryGetProperty("album", out var alb) ? alb : default;
            return new Track(Str(t, "name"), Artists(t), t.TryGetProperty("duration_ms", out var d) ? d.GetInt64() : 0, tid,
                t.TryGetProperty("external_ids", out var x) ? Str(x, "isrc") is { Length: > 0 } i ? i : null : null,
                art ?? (al.ValueKind == JsonValueKind.Object ? Image(al) : null), album ?? (al.ValueKind == JsonValueKind.Object ? Str(al, "name") : null));
        }
        switch (kind)
        {
            case "track":
                using (var t = await SpotifyApi.GetAsync($"tracks/{id}"))
                    return t != null && FromTrack(t.RootElement) is { } one ? new ImportedList(one.Title, [one], one.Art, false) : null;
            case "album":
            {
                using var a = await SpotifyApi.GetAsync($"albums/{id}");
                if (a == null) return null;
                var (name, art) = (Str(a.RootElement, "name"), Image(a.RootElement));
                var tracks = new List<Track>();
                for (var page = $"albums/{id}/tracks?limit=50"; page.Length > 0;)
                {
                    using var p = await SpotifyApi.GetAsync(page);
                    if (p == null) break;
                    foreach (var t in p.RootElement.GetProperty("items").EnumerateArray()) if (FromTrack(t, name, art) is { } tr) tracks.Add(tr);
                    page = Str(p.RootElement, "next");
                }
                return new ImportedList(name, tracks, art, false);
            }
            default:
            {
                using var head = await SpotifyApi.GetAsync($"playlists/{id}?fields=name,images");
                if (head == null) return null;
                var (name, art) = (Str(head.RootElement, "name"), Image(head.RootElement));
                var tracks = new List<Track>();
                for (var page = $"playlists/{id}/tracks?limit=100&fields=next,items(track(id,name,duration_ms,external_ids(isrc),artists(name),album(name,images)))"; page.Length > 0;)
                {
                    using var p = await SpotifyApi.GetAsync(page);
                    if (p == null) break;
                    foreach (var it in p.RootElement.GetProperty("items").EnumerateArray())
                        if (it.TryGetProperty("track", out var t) && FromTrack(t) is { } tr) tracks.Add(tr);
                    page = Str(p.RootElement, "next");
                }
                return new ImportedList(name, tracks, art, false);
            }
        }
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// Where each track plays from: a library file with the same title and artist, else YouTube (found when it plays).
    /// Title, artist, album, cover, ISRC, length and Spotify id go into the library index: lists show names and covers, the
    /// YouTube search finds the official release, and lyrics come by exact id.
    /// </summary>
    public static List<string> Resolve(List<Track> tracks)
    {
        var items = new List<string>();
        foreach (var t in tracks)
        {
            var file = Library.FindSong(t.Title, t.Artist);
            var item = file ?? "ytsearch://" + Uri.EscapeDataString($"{t.Artist} - {t.Title}");
            if (file != null) Library.Data.Meta[file].SpotifyId = t.Id is { Length: > 0 } ? t.Id : Library.Data.Meta[file].SpotifyId;
            else
            {
                var meta = Library.Data.Meta.GetValueOrDefault(item) ?? new TrackMeta(); // a known song keeps what was found for it (its YouTube video)
                (meta.Title, meta.Artist) = (t.Title, t.Artist);
                if (t.Id.Length > 0) meta.SpotifyId = t.Id; // a radio song (YouTube Music) has no Spotify id
                meta.Album = t.Album ?? meta.Album;
                meta.Art = t.Art ?? meta.Art;
                meta.Isrc = t.Isrc ?? meta.Isrc;
                if (t.DurationMs > 0) meta.DurationMs = t.DurationMs;
                Library.Data.Meta[item] = meta;
            }
            items.Add(item);
        }
        Library.Touch();
        return items;
    }

    /// <summary>
    /// What the embed page doesn't give: each song's cover and ISRC, from Deezer (free, no key), a few a second in the
    /// background. <paramref name="changed"/> runs on the UI thread now and then so lists redraw.
    /// </summary>
    public static async Task EnrichAsync(IEnumerable<string> items, Action changed)
    {
        var todo = items.Where(i => i.StartsWith("ytsearch://") && Library.Data.Meta.GetValueOrDefault(i) is { } m && (m.Art == null || m.Isrc == null)).Distinct().ToList();
        var n = 0;
        foreach (var item in todo)
        {
            if (Library.Data.Meta.GetValueOrDefault(item) is not { Title: { } title, Artist: { } artist } meta) continue;
            if (await Deezer.FindAsync(title, artist, meta.DurationMs / 1000.0) is { } hit)
            {
                if (meta.Art == null && hit.Cover != null) { meta.Art = hit.Cover; Library.ForgetThumb(item); }
                meta.Isrc ??= hit.Isrc;
                meta.Album ??= hit.Album;
            }
            if (++n % 10 == 0) changed();
            await Task.Delay(150); // Deezer allows 50 requests in 5 s
        }
        if (n % 10 != 0) changed();
    }

    /// <summary>A list's cover, saved with the profile (its address on Spotify's servers changes with the list).</summary>
    public static async Task<string?> SaveCoverAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var dir = Path.Combine(Library.Dir, "covers");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "spotify-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..16] + ".jpg");
            if (!File.Exists(file)) await File.WriteAllBytesAsync(file, await Http.GetByteArrayAsync(url));
            return file;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { return null; }
    }

    /// <summary>
    /// Follows the Spotify list a playlist came from: songs added there are added here in the same order, removed ones go.
    /// Returns how many came and went; null when the list couldn't be read (then it stays as it is).
    /// </summary>
    public static async Task<(int Added, int Removed)?> SyncAsync(Playlist p)
    {
        if (p.Source is not { } link || await ReadAsync(link) is not { Tracks.Count: > 0 } list) return null;
        if (list.Partial && p.Items.Count > list.Tracks.Count) return null; // signed out since: a shorter page isn't the list shrinking
        var items = Resolve(list.Tracks);
        var added = items.Except(p.Items).Count();
        var removed = p.Items.Except(items).Count();
        p.LastSync = DateTime.UtcNow;
        if (added + removed > 0 || !p.Items.SequenceEqual(items)) p.Items = items;
        if (p.Thumb == null || !File.Exists(p.Thumb)) p.Thumb = await SaveCoverAsync(list.Cover) ?? p.Thumb;
        return (added, removed);
    }
}

/// <summary>Deezer's free search: a song's ISRC, album and cover by its name, checked by artist and length.</summary>
public static class Deezer
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public record Hit(string? Isrc, string? Cover, string? Album, double Seconds);

    public static async Task<Hit?> FindAsync(string title, string artist, double seconds)
    {
        var main = artist.Split(',')[0].Trim();
        // the strict form (artist:"…" track:"…") finds nothing for names with an apostrophe ("Plain White T's"): plain words too
        foreach (var q in new[] { $"artist:\"{main}\" track:\"{title}\"", $"{main} {title}" })
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.deezer.com/search?limit=10&q=" + Uri.EscapeDataString(q)));
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                var best = data.EnumerateArray()
                    .Where(e => e.TryGetProperty("artist", out var a) && Covers.Same(a.TryGetProperty("name", out var an) ? an.GetString() : null, main))
                    .Select(e => (e, off: seconds > 0 && e.TryGetProperty("duration", out var d) ? Math.Abs(d.GetDouble() - seconds) : 0))
                    .Where(x => x.off <= 4).OrderBy(x => x.off).Select(x => x.e).FirstOrDefault();
                if (best.ValueKind != JsonValueKind.Object) continue;
                var album = best.TryGetProperty("album", out var al) ? al : default;
                return new Hit(best.TryGetProperty("isrc", out var i) ? i.GetString() : null,
                    album.ValueKind == JsonValueKind.Object && album.TryGetProperty("cover_medium", out var c) ? c.GetString() : null,
                    album.ValueKind == JsonValueKind.Object && album.TryGetProperty("title", out var t) ? t.GetString() : null,
                    best.TryGetProperty("duration", out var du) ? du.GetDouble() : 0);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
        }
        return null;
    }
}

/// <summary>
/// "Lyrics for Spotify": a window with the Spicy-style lyrics of what the Spotify app on this PC is playing. The track and
/// its position come from Windows' media sessions (what the volume flyout shows); a click on a line seeks Spotify.
/// </summary>
public sealed class SpotifyLyricsWindow : Window
{
    GlobalSystemMediaTransportControlsSession? _session;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    readonly LyricsView _lyrics = new();
    readonly Image _cover = new() { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
    readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _artist = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    string? _for;
    double _position;          // seconds at _at
    DateTime _at;
    bool _playing;
    readonly DispatcherTimer _poll;

    public SpotifyLyricsWindow(Window owner)
    {
        Owner = owner;
        Title = L.T("Lyrics for Spotify");
        Width = 560;
        Height = 760;
        FontFamily = (FontFamily)Application.Current.FindResource("Inter");
        SetResourceReference(BackgroundProperty, "StageBg");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "FgA0");
        _artist.SetResourceReference(TextBlock.ForegroundProperty, "FgA2");
        var head = new DockPanel { Margin = new Thickness(20, 16, 20, 8) };
        var art = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _cover, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(art, Dock.Left);
        head.Children.Add(art);
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _artist } });
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_lyrics);
        Content = root;
        _lyrics.Clock = () => _position + (_playing ? (DateTime.UtcNow - _at).TotalSeconds : 0);
        _lyrics.Seek += s => _ = _session?.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(s).Ticks);
        _lyrics.ShowMessage(L.T("Start something in Spotify…"));
        // sessions come and go (Spotify restarted); a slow poll keeps up without event plumbing for every case
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (_, _) => await Refresh(), Dispatcher);
        Closed += (_, _) => _poll.Stop();
        // a dark title bar to match (Windows 10 2004+ / 11)
        SourceInitialized += (_, _) =>
        {
            var dark = ThemeManager.IsLight ? 0 : 1;
            DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Loaded += async (_, _) => await Refresh();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    async Task Refresh()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _session = _manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
            if (_session == null) { _for = null; _lyrics.ShowMessage(L.T("Spotify isn't playing on this computer.")); return; }
            var timeline = _session.GetTimelineProperties();
            var info = _session.GetPlaybackInfo();
            _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            // Spotify reports its position only on changes: carry it on from when it was reported
            _position = timeline.Position.TotalSeconds + (_playing ? (DateTime.UtcNow - timeline.LastUpdatedTime.UtcDateTime).TotalSeconds : 0);
            _at = DateTime.UtcNow;
            var props = await _session.TryGetMediaPropertiesAsync();
            var key = $"{props.Artist}|{props.Title}";
            if (key == _for || string.IsNullOrWhiteSpace(props.Title)) return;
            _for = key;
            _title.Text = props.Title;
            _artist.Text = props.Artist;
            _cover.Source = await Thumbnail(props);
            _lyrics.ShowMessage(L.T("Looking for lyrics…"));
            var doc = await Lyrics.FindAsync("spotify://now", props.Title, props.Artist, props.AlbumTitle, timeline.EndTime.TotalSeconds);
            if (_for != key) return;
            if (doc != null) _lyrics.Show(doc);
            else _lyrics.ShowMessage(L.T("No lyrics for this track."));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException) { }
    }

    internal static async Task<ImageSource?> Thumbnail(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null) return null;
        try
        {
            using var stream = (await props.Thumbnail.OpenReadAsync()).AsStreamForRead();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is System.IO.IOException or NotSupportedException or System.Runtime.InteropServices.COMException) { return null; }
    }
}
