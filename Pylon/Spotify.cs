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
/// Spotify without an account in Pylon: a public playlist / album / track link is read from Spotify's embed page (title,
/// artist, length and track id, up to 50 tracks for big playlists), songs already in the library play from disk and the
/// rest through YouTube (yt-dlp, audio only). The track id goes along, so word-synced lyrics are found exactly.
/// </summary>
public static class SpotifyImport
{
    public record Track(string Title, string Artist, long DurationMs, string Id);

    static readonly Regex Link = new(@"open\.spotify\.com/(?:intl-[a-z-]+/)?(playlist|album|track)/([A-Za-z0-9]{22})", RegexOptions.Compiled);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Pylon" } } };

    public static bool IsLink(string text) => Link.IsMatch(text);

    /// <summary>The list's name and its tracks; null when the page isn't readable (private list, no connection).</summary>
    public static async Task<(string Name, List<Track> Tracks)?> ReadAsync(string url)
    {
        if (Link.Match(url) is not { Success: true } m) return null;
        try
        {
            var html = await Http.GetStringAsync($"https://open.spotify.com/embed/{m.Groups[1].Value}/{m.Groups[2].Value}");
            var json = Regex.Match(html, "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
            using var doc = JsonDocument.Parse(json);
            var entity = doc.RootElement.GetProperty("props").GetProperty("pageProps").GetProperty("state").GetProperty("data").GetProperty("entity");
            var name = entity.TryGetProperty("name", out var n) ? n.GetString() ?? "Spotify" : "Spotify";
            var tracks = new List<Track>();
            if (entity.TryGetProperty("trackList", out var list))
                foreach (var t in list.EnumerateArray())
                    tracks.Add(new Track(Str(t, "title"), Str(t, "subtitle"), t.TryGetProperty("duration", out var d) ? d.GetInt64() : 0, Str(t, "uri").Split(':').Last()));
            else // a single track
                tracks.Add(new Track(name, entity.TryGetProperty("artists", out var a) ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name"))) : "",
                    entity.TryGetProperty("duration", out var d1) ? d1.GetInt64() : 0, m.Groups[2].Value));
            return (name, tracks.Where(t => t.Title.Length > 0).ToList());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// Where each track plays from: a library file with the same title and artist, else a YouTube search through yt-dlp.
    /// Titles and Spotify ids are kept in the library index, so lists show names and lyrics come from Spicy by id.
    /// </summary>
    public static List<string> Resolve(List<Track> tracks)
    {
        static string N(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        var local = Library.Data.Meta.Where(m => m.Value.Title is { Length: > 0 } && System.IO.File.Exists(m.Key)).ToList();
        var items = new List<string>();
        foreach (var t in tracks)
        {
            var file = local.FirstOrDefault(m => N(m.Value.Title!) == N(t.Title) && N(m.Value.Artist ?? "").Contains(N(t.Artist.Split(',')[0]))).Key;
            var item = file ?? "ytsearch://" + Uri.EscapeDataString($"{t.Artist} - {t.Title}");
            if (file == null) Library.Data.Meta[item] = new TrackMeta { Title = t.Title, Artist = t.Artist, SpotifyId = t.Id };
            else Library.Data.Meta[file].SpotifyId = t.Id;
            items.Add(item);
        }
        Library.Touch();
        return items;
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
