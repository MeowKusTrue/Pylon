using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Pylon;

/// <summary>
/// Spotify's Web API, only for what Windows' media session can't tell: the queue, and skipping ahead in it. Sign-in is
/// PKCE in the browser (no secret, the password never passes through Pylon) with the user's own free Spotify app: its
/// Client ID is the one under Settings → Online → Spotify, with <see cref="Redirect"/> added as a redirect URI. The
/// refresh token stays in the profile folder.
/// </summary>
public static class SpotifyApi
{
    /// <summary>Sign-in comes back here; the same address has to be in the Spotify app's redirect URIs (port: a setting).</summary>
    public static string Redirect => $"http://127.0.0.1:{Port}/callback";
    static int Port => Settings.Int("spotify.port") is var p and > 1023 and < 65536 ? p : 47851;

    /// <summary>Why the last sign-in failed, for the message.</summary>
    public static string? LastError { get; private set; }
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static string TokenFile => Path.Combine(Library.Dir, "spotify-token.txt");
    static string? _access;
    static DateTime _expires;

    public static string ClientId => Settings.Str("lyrics.spotifyid").Trim();
    public static bool SignedIn => File.Exists(TokenFile);

    public record Song(string Id, string Title, string Artist, string? Art, double Seconds);

    /// <summary>Opens Spotify's sign-in page and waits (up to 3 minutes) for it to come back to Pylon.</summary>
    public static async Task<bool> SignInAsync()
    {
        if (ClientId.Length == 0) return false;
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(12));
        LastError = null;
        var listener = new TcpListener(IPAddress.Loopback, Port); // a plain socket: no URL reservation or admin rights
        try { listener.Start(); }
        catch (SocketException)
        {
            LastError = L.F("Port {0} is taken by another program. Pick another one in Settings → Online → Spotify and add http://127.0.0.1:<port>/callback to your Spotify app.", Port);
            return false;
        }
        try
        {
            Process.Start(new ProcessStartInfo("https://accounts.spotify.com/authorize?" + Query(("client_id", ClientId), ("response_type", "code"),
                ("redirect_uri", Redirect), ("code_challenge_method", "S256"), ("code_challenge", challenge), ("state", state),
                ("scope", "user-read-playback-state user-modify-playback-state user-read-currently-playing"))) { UseShellExecute = true });
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();
                var buf = new byte[4096];
                var n = await stream.ReadAsync(buf, cts.Token);
                var path = Encoding.ASCII.GetString(buf, 0, n).Split(' ').ElementAtOrDefault(1) ?? "";
                if (!path.StartsWith("/callback")) { await Reply(stream, "", cts.Token); continue; } // the browser's favicon request
                var q = Parse(path);
                var code = q.GetValueOrDefault("state") == state ? q.GetValueOrDefault("code") : null;
                var ok = code != null && await TokenAsync([("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Redirect), ("client_id", ClientId), ("code_verifier", verifier)], false);
                await Reply(stream, L.T(ok ? "Pylon is connected to Spotify. You can close this tab." : "Spotify sign-in didn't finish. Try again from Pylon."), cts.Token);
                return ok;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or System.ComponentModel.Win32Exception) { return false; }
        finally { listener.Stop(); }
    }

    static async Task Reply(Stream stream, string text, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Pylon</title><body style=\"font:17px system-ui;background:#111;color:#eee;padding:48px\">{WebUtility.HtmlEncode(text)}");
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct);
        await stream.WriteAsync(body, ct);
    }

    public static void SignOut()
    {
        _access = null;
        try { File.Delete(TokenFile); } catch (IOException) { }
    }

    static async Task<bool> TokenAsync((string, string)[] form, bool refreshing)
    {
        try
        {
            using var res = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form.Select(f => KeyValuePair.Create(f.Item1, f.Item2))));
            if (!res.IsSuccessStatusCode)
            {
                if (refreshing && res.StatusCode == HttpStatusCode.BadRequest) SignOut(); // revoked: sign in again
                return false;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            _access = doc.RootElement.GetProperty("access_token").GetString();
            _expires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
            if (doc.RootElement.TryGetProperty("refresh_token", out var r) && r.GetString() is { } refresh)
            {
                Directory.CreateDirectory(Library.Dir);
                File.WriteAllText(TokenFile, refresh);
            }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    static async Task<string?> AccessAsync()
    {
        if (_access != null && DateTime.UtcNow < _expires) return _access;
        if (!SignedIn || ClientId.Length == 0) return null;
        string refresh;
        try { refresh = File.ReadAllText(TokenFile).Trim(); }
        catch (IOException) { return null; }
        return await TokenAsync([("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", ClientId)], true) ? _access : null;
    }

    /// <summary>The song playing and the ones after it; null when not signed in, offline, or Spotify plays nothing.</summary>
    public static async Task<(Song? Now, List<Song> Next)?> QueueAsync()
    {
        if (await AccessAsync() is not { } token) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/queue");
            req.Headers.Authorization = new("Bearer", token);
            using var res = await Http.SendAsync(req);
            if (res.StatusCode == HttpStatusCode.Unauthorized) { _access = null; return null; }
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            return (root.TryGetProperty("currently_playing", out var cur) ? ToSong(cur) : null,
                root.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.Array ? [.. q.EnumerateArray().Select(ToSong).OfType<Song>()] : []);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    static Song? ToSong(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) return null;
        var by = t.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name")))
            : t.TryGetProperty("show", out var show) ? Str(show, "name") : ""; // a podcast episode
        var holder = t.TryGetProperty("album", out var album) ? album : t;
        var art = holder.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array && im.GetArrayLength() > 0 ? Str(im[im.GetArrayLength() - 1], "url") : null; // the smallest
        return new Song(Str(t, "id"), Str(t, "name"), by, art, t.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() / 1000 : 0);
    }

    /// <summary>Skips ahead to the queued song <paramref name="times"/> places on. Spotify allows it with Premium only.</summary>
    public static async Task<bool> SkipAsync(int times)
    {
        if (await AccessAsync() is not { } token) return false;
        try
        {
            for (var k = 0; k < times; k++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.spotify.com/v1/me/player/next");
                req.Headers.Authorization = new("Bearer", token);
                using var res = await Http.SendAsync(req);
                if (!res.IsSuccessStatusCode) return false;
                await Task.Delay(300); // Spotify applies them in order only with a little room
            }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }

    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static string Query(params (string K, string V)[] q) => string.Join("&", q.Select(p => p.K + "=" + Uri.EscapeDataString(p.V)));
    static Dictionary<string, string> Parse(string path) =>
        path.Split('?', 2) is [_, var qs] ? qs.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1])) : [];
}

/// <summary>
/// The remote: while Pylon plays nothing and another app does (Spotify first, else Yandex Music, a browser…), the stage
/// shows that app's song, cover and synced lyrics, with its play / pause, previous / next, shuffle, repeat and seeking,
/// all through Windows' media session (what the volume flyout uses). Signed in to Spotify, a Queue tab shows what comes
/// next. Windows reports the position only on changes, so it runs on between reports from when it was given.
/// </summary>
public sealed class RemoteView : Grid
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly DispatcherTimer _poll;
    readonly Backdrop _backdrop = new() { Opacity = 0.32 };
    readonly Image _cover = new() { Stretch = Stretch.UniformToFill };
    readonly Border _art;
    readonly TextBlock _app = new() { FontSize = 13, Margin = new Thickness(0, 22, 0, 4), HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _title = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _artist = new() { FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 14) };
    readonly SeekBar _seek = new() { Margin = new Thickness(0, 0, 0, 2) };
    readonly TextBlock _time = new() { FontSize = 12 }, _length = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
    readonly ToggleButton _play, _shuffle, _repeat;
    readonly LyricsView _lyrics = new();
    readonly StackPanel _queue = new();
    readonly ScrollViewer _queueScroll;
    readonly Button _lyricsTab, _queueTab;
    string? _for;
    double _position, _length0;
    DateTime _at;
    bool _playing;
    List<SpotifyApi.Song> _next = [];
    string? _spotifyId;

    /// <summary>Another app is playing something this view can show.</summary>
    public bool HasSession => _session != null && _for != null;

    /// <summary>A session appeared, changed or went away: the host shows or hides this view.</summary>
    public event Action? AvailabilityChanged;

    /// <summary>The close button: the host goes back to what it showed before.</summary>
    public event Action? Closed;

    readonly Grid _layout;
    readonly TextBlock _empty = new()
    {
        FontSize = 18, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The host wants to know about other players (Pylon idle, window awake): polls every 1.5 s, else nothing.</summary>
    public bool Watching { get => _poll.IsEnabled; set { if (value == _poll.IsEnabled) return; _poll.IsEnabled = value; if (value) _ = Refresh(); } }

    double Now => _position + (_playing ? (DateTime.UtcNow - _at).TotalSeconds : 0);

    public RemoteView()
    {
        var bg = new System.Windows.Shapes.Rectangle();
        bg.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "StageBg");
        Children.Add(bg);
        Children.Add(_backdrop);

        _art = new Border { Width = 300, Height = 300, CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = _cover, HorizontalAlignment = HorizontalAlignment.Center };
        _art.SetResourceReference(Border.BackgroundProperty, "BgB1");
        _cover.Clip = new RectangleGeometry(new Rect(0, 0, 300, 300), 12, 12);
        _app.SetResourceReference(TextBlock.ForegroundProperty, "FgA3");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "FgA0");
        _artist.SetResourceReference(TextBlock.ForegroundProperty, "FgA2");
        _time.SetResourceReference(TextBlock.ForegroundProperty, "FgA3");
        _length.SetResourceReference(TextBlock.ForegroundProperty, "FgA3");
        _seek.Seek += v => { if (_length0 > 0) { _position = v * _length0; _at = DateTime.UtcNow; _ = _session?.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(_position).Ticks); } };

        _shuffle = new ToggleButton { Style = (Style)Application.Current.FindResource("IconButton"), Tag = "shuffle", Padding = new Thickness(3, 2, 3, 2), ToolTip = L.T("Shuffle") };
        _repeat = new ToggleButton { Style = (Style)Application.Current.FindResource("IconButton"), Tag = "repeat", Padding = new Thickness(3, 2, 3, 2), ToolTip = L.T("Repeat") };
        _play = new ToggleButton { Style = (Style)Application.Current.FindResource("PlayButton"), Margin = new Thickness(12, 0, 12, 0) };
        var prev = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Tag = "skip-back-fill", Padding = new Thickness(3, 2, 3, 2), ToolTip = L.T("Previous") };
        var next = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Tag = "skip-forward-fill", Padding = new Thickness(3, 2, 3, 2), ToolTip = L.T("Next") };
        _play.Click += (_, _) => TogglePlay();
        prev.Click += (_, _) => Previous();
        next.Click += (_, _) => Next();
        _shuffle.Click += (_, _) => _ = _session?.TryChangeShuffleActiveAsync(_shuffle.IsChecked == true);
        _repeat.Click += (_, _) => _ = _session?.TryChangeAutoRepeatModeAsync(_repeat.IsChecked == true ? Windows.Media.MediaPlaybackAutoRepeatMode.List : Windows.Media.MediaPlaybackAutoRepeatMode.None);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new Control[] { _shuffle, prev, _play, next, _repeat }) { b.Margin = new Thickness(b == _play ? 12 : 6, 0, b == _play ? 12 : 6, 0); b.VerticalAlignment = VerticalAlignment.Center; buttons.Children.Add(b); }

        var times = new Grid();
        times.Children.Add(_time);
        times.Children.Add(_length);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 360, Children = { _art, _app, _title, _artist, _seek, times, buttons } };

        _lyricsTab = Tab("Lyrics", () => Show(lyrics: true));
        _queueTab = Tab("Queue", () => Show(lyrics: false));
        _queueScroll = new ScrollViewer { Content = _queue, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        _lyrics.Clock = () => Now;
        _lyrics.Seek += s => { _position = s; _at = DateTime.UtcNow; _ = _session?.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(s).Ticks); };
        _lyrics.Margin = new Thickness(0, 12, 0, 0);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Children = { _lyricsTab, _queueTab } };
        var right = new DockPanel();
        DockPanel.SetDock(tabs, Dock.Top);
        right.Children.Add(tabs);
        right.Children.Add(new Grid { Children = { _lyrics, _queueScroll } });

        var layout = _layout = new Grid { Margin = new Thickness(40, 56, 40, 28), ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } } };
        Grid.SetColumn(right, 1);
        right.Margin = new Thickness(56, 0, 0, 0);
        layout.Children.Add(left);
        layout.Children.Add(right);
        Children.Add(layout);
        // opened by hand with nothing playing elsewhere: say so instead of an empty remote
        _empty.Text = L.T("Nothing is playing in other apps. Start a song in Spotify, Yandex Music or a browser, and it shows here.");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "FgA2");
        Children.Add(_empty);
        var close = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Tag = "x", Padding = new Thickness(3, 2, 3, 2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 14, 16, 0), ToolTip = L.T("Close") };
        close.Click += (_, _) => Closed?.Invoke();
        Children.Add(close);
        ShowEmpty();
        Show(lyrics: true);

        _poll = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, async (_, _) => await Refresh(), Dispatcher) { IsEnabled = false };
        var tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => ShowTime(), Dispatcher) { IsEnabled = false };
        IsVisibleChanged += (_, _) => { tick.IsEnabled = IsVisible; _lyrics.Visibility = IsVisible && _lyricsTab.Tag is true ? Visibility.Visible : Visibility.Collapsed; };
    }

    Button Tab(string text, Action click)
    {
        var b = new Button { Content = L.T(text), Style = (Style)Application.Current.FindResource("SecondaryButton"), Height = 32, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    void Show(bool lyrics)
    {
        _lyricsTab.Tag = lyrics;
        _lyrics.Visibility = lyrics ? Visibility.Visible : Visibility.Collapsed;
        _queueScroll.Visibility = lyrics ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (tab, on) in new[] { (_lyricsTab, lyrics), (_queueTab, !lyrics) })
        {
            if (on) tab.SetResourceReference(BackgroundProperty, "BgRaised");
            else tab.Background = Brushes.Transparent;
            tab.SetResourceReference(Control.ForegroundProperty, on ? "FgA0" : "FgA3");
        }
        if (!lyrics) _ = LoadQueue();
    }

    public void TogglePlay() { _playing = !_playing; _position = Now; _at = DateTime.UtcNow; _play.IsChecked = _playing; _ = _session?.TryTogglePlayPauseAsync(); }
    public void Next() { _ = _session?.TrySkipNextAsync(); }
    public void Previous() { _ = _session?.TrySkipPreviousAsync(); }

    void ShowTime()
    {
        // browsers often report no length: then there's nothing to seek in or count
        var known = _length0 > 0;
        _seek.Visibility = _time.Visibility = _length.Visibility = known ? Visibility.Visible : Visibility.Hidden;
        if (!known) return;
        var now = Math.Min(Now, _length0);
        if (!_seek.IsDragging) _seek.Value = now / _length0;
        _time.Text = TimeSpan.FromSeconds(now).ToString(@"m\:ss");
        _length.Text = TimeSpan.FromSeconds(_length0).ToString(@"m\:ss");
    }

#if DEBUG
    /// <summary>Snapshot tests: a paused session counts too (nothing may be playing on the test machine).</summary>
    public static bool DebugAnySession;
#endif

    /// <summary>
    /// Spotify if it's playing; else another app that's playing; else the one shown already, also when paused (pausing it
    /// from here mustn't make the remote vanish). Never Pylon itself.
    /// </summary>
    GlobalSystemMediaTransportControlsSession? Pick()
    {
        var all = _manager!.GetSessions().Where(s => !s.SourceAppUserModelId.Contains("Pylon", StringComparison.OrdinalIgnoreCase)).ToList();
        static bool Playing(GlobalSystemMediaTransportControlsSession s) =>
            s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var shown = _session is { } cur && _for != null ? all.FirstOrDefault(s => s.SourceAppUserModelId == cur.SourceAppUserModelId) : null;
        var pick = all.FirstOrDefault(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) && Playing(s))
            ?? all.FirstOrDefault(Playing) ?? shown;
#if DEBUG
        if (DebugAnySession) pick ??= all.FirstOrDefault();
#endif
        return pick;
    }

    static string AppName(string aumid)
    {
        var id = aumid.ToLowerInvariant();
        return id.Contains("spotify") ? "Spotify" : id.Contains("yandex") ? L.T("Yandex Music") : id.Contains("chrome") ? "Chrome" : id.Contains("msedge") ? "Edge"
            : id.Contains("firefox") ? "Firefox" : id.Contains("opera") ? "Opera" : id.Contains("vk") ? "VK"
            : System.Text.RegularExpressions.Regex.IsMatch(aumid, "^[0-9A-Fa-f]{8,}$") ? L.T("Another player") // a browser tab's hash
            : Path.GetFileNameWithoutExtension(aumid.Split('!')[0]);
    }

    bool _announced;

    /// <summary>Tells the host when there came to be something to show, or stopped being.</summary>
    void Announce()
    {
        ShowEmpty();
        if (HasSession == _announced) return;
        _announced = HasSession;
        AvailabilityChanged?.Invoke();
    }

    void ShowEmpty()
    {
        _layout.Visibility = HasSession ? Visibility.Visible : Visibility.Hidden;
        _empty.Visibility = HasSession ? Visibility.Collapsed : Visibility.Visible;
    }

    async Task Refresh()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = Pick();
            _session = session;
            if (session == null) { _for = null; Announce(); return; }
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            _playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _play.IsChecked = _playing;
            _shuffle.IsChecked = info.IsShuffleActive == true;
            _repeat.IsChecked = info.AutoRepeatMode is Windows.Media.MediaPlaybackAutoRepeatMode.List or Windows.Media.MediaPlaybackAutoRepeatMode.Track;
            _shuffle.IsEnabled = info.Controls.IsShuffleEnabled;
            _repeat.IsEnabled = info.Controls.IsRepeatEnabled;
            // reported only on changes: carried on from when it was reported
            _position = timeline.Position.TotalSeconds + (_playing ? Math.Max(0, (DateTime.UtcNow - timeline.LastUpdatedTime.UtcDateTime).TotalSeconds) : 0);
            _length0 = (timeline.EndTime - timeline.StartTime).TotalSeconds;
            _at = DateTime.UtcNow;
            var props = await session.TryGetMediaPropertiesAsync();
            if (string.IsNullOrWhiteSpace(props.Title)) { _for = null; Announce(); return; }
            _app.Text = AppName(session.SourceAppUserModelId);
            var key = $"{props.Artist}|{props.Title}";
            if (key == _for) return;
            _for = key;
            Announce();
            _title.Text = props.Title;
            _artist.Text = props.Artist;
            var art = await SpotifyLyricsWindow.Thumbnail(props);
            _cover.Source = art;
            _backdrop.SetImage(art);
            if (_queueScroll.Visibility == Visibility.Visible || session.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) await LoadQueue();
            _lyrics.ShowMessage(L.T("Looking for lyrics…"));
            var tags = _spotifyId != null ? "spotify:track:" + _spotifyId : "";
            var doc = await Lyrics.FindAsync("remote://" + key, props.Title, props.Artist, props.AlbumTitle, _length0, tags);
            if (_for != key) return;
            if (doc != null) _lyrics.Show(doc);
            else _lyrics.ShowMessage(L.T("No lyrics for this track."));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException) { }
    }

    /// <summary>The Queue tab: Spotify's own list when signed in; else how to get it.</summary>
    async Task LoadQueue()
    {
        var spotify = _session?.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase) == true;
        if (!spotify) { QueueNote(L.T("Windows doesn't tell what comes next. Pylon can show the queue for Spotify.")); return; }
        if (!SpotifyApi.SignedIn)
        {
            QueueNote(SpotifyApi.ClientId.Length == 0
                ? L.F("To see Spotify's queue, create a free app at developer.spotify.com with the redirect URI {0}, put its Client ID in Settings → Online → Spotify, then sign in here.", SpotifyApi.Redirect)
                : L.T("Sign in to Spotify to see what comes next."), signIn: SpotifyApi.ClientId.Length > 0);
            return;
        }
        var q = await SpotifyApi.QueueAsync();
        if (q is not { } got) { QueueNote(L.T("Spotify didn't send the queue. It shows when something plays.")); return; }
        _spotifyId = got.Now?.Id;
        _next = got.Next;
        _queue.Children.Clear();
        for (var i = 0; i < _next.Count && i < 50; i++)
        {
            var song = _next[i];
            var skip = i + 1;
            var img = new Image { Width = 40, Height = 40, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 12, 0) };
            if (song.Art != null) img.Source = new BitmapImage(new Uri(song.Art)) { DecodePixelWidth = 80 };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
            {
                new TextBlock { Text = song.Title, FontSize = 15, Foreground = R("FgA0"), TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = song.Artist, FontSize = 13, Foreground = R("FgA3"), TextTrimming = TextTrimming.CharacterEllipsis },
            } };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = L.T("Play this one (Spotify Premium)") };
            DockPanel.SetDock(img, Dock.Left);
            row.Children.Add(img);
            row.Children.Add(text);
            row.MouseLeftButtonUp += async (_, _) =>
            {
                if (!await SpotifyApi.SkipAsync(skip)) QueueNote(L.T("Spotify allows skipping ahead with Premium only."), keep: true);
            };
            _queue.Children.Add(row);
        }
        if (_next.Count == 0) QueueNote(L.T("Nothing queued after this song."));
    }

    void QueueNote(string text, bool signIn = false, bool keep = false)
    {
        if (!keep) _queue.Children.Clear();
        _queue.Children.Insert(0, new TextBlock { Text = text, FontSize = 14, Foreground = R("FgA3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        if (!signIn) return;
        var b = new Button { Content = L.T("Sign in to Spotify"), Style = (Style)Application.Current.FindResource("PrimaryButton"), Height = 36, Padding = new Thickness(18, 0, 18, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            var ok = await SpotifyApi.SignInAsync();
            b.IsEnabled = true;
            if (ok) await LoadQueue();
            else QueueNote(SpotifyApi.LastError ?? L.T("Spotify sign-in didn't finish. Try again from Pylon."), signIn: true);
        };
        _queue.Children.Insert(1, b);
    }

    static Brush R(string key) => (Brush)Application.Current.FindResource(key);
}
