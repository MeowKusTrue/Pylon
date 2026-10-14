using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Pylon;

/// <summary>A list row in the library panel.</summary>
public class Row : INotifyPropertyChanged
{
    public bool IsHeader { get; init; }
    public string Title { get; init; } = "";
    public string? Glyph { get; init; }        // icon rows: folders, playlists, actions
    public string? GlyphTint { get; init; }
    public string? Path { get; init; }         // media rows: thumbnail + progress
    public string? Image { get; init; }        // custom cover (playlists)
    public double Progress { get; init; }
    public bool Dim { get; init; }
    public bool Active { get; init; }
    public bool Sortable { get; init; } = true; // actions ("Add") and fixed entries stay on top
    public bool IsAction { get; init; }
    public Action? Click { get; init; }
    public Func<IEnumerable<object>>? Menu { get; init; }

    public bool IsMedia => Path != null;
    public bool HasThumbBox => Path != null || Image != null;
    public bool ShowChevron => IsHeader && Click != null;
    public double ProgressWidth => 32 * Progress;
    public string? Tip => Path;
    public string? Placeholder { get; init; }
    public string PlaceholderGlyph => Placeholder ?? (Path == null ? "playlist" : Library.IsAudio(Path) ? "music-note" : "film-strip");

    ImageSource? _thumb;
    bool _thumbRequested;
    public ImageSource? Thumb { get => _thumb; private set { _thumb = value; PropertyChanged?.Invoke(this, new(nameof(Thumb))); } }

    public async void RequestThumb()
    {
        if (_thumbRequested || !HasThumbBox) return;
        _thumbRequested = true;
        Thumb = Image != null ? (Image.Length == 0 ? null : await Task.Run(() => Library.LoadImage(Image))) : await Library.ThumbnailAsync(Path!);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Left sidebar: icon rail + browsable library (Home, Videos, Music, Network, Playlists, History, queue).</summary>
public partial class LibraryPanel : UserControl
{
    public enum Section { Home, Videos, Music, Network, Playlists, History, Queue }

    record Crumb(string Title, Func<Page>? Go);
    record Page(Section Section, Crumb[] Crumbs, Func<List<Row>> Build, string? Folder = null, Func<IEnumerable<object>>? More = null);

    // regular glyph, filled glyph when active (spelled out so tools/sync-icons.py finds both)
    static readonly (Section Section, string Glyph, string Active, string Tip)[] RailItems =
    [
        (Section.Home, "house", "house-fill", "Home"),
        (Section.Videos, "monitor-play", "monitor-play-fill", "Videos"),
        (Section.Music, "music-notes", "music-notes-fill", "Music"),
        (Section.Network, "broadcast", "broadcast-fill", "Network"),
        (Section.Playlists, "playlist", "playlist-fill", "Playlists"),
        (Section.History, "clock-counter-clockwise", "clock-counter-clockwise-fill", "History"),
    ];

    readonly List<Page> _nav = [];
    int _pos = -1;
    bool _descending;
    MainWindow Player => (MainWindow)Window.GetWindow(this)!;

    public LibraryPanel()
    {
        InitializeComponent();
        foreach (var (section, glyph, _, tip) in RailItems)
        {
            var b = new ToggleButton { Style = (Style)FindResource("IconButton"), Tag = glyph, ToolTip = L.T(tip), Width = 30, Margin = new Thickness(0, 0, 0, 14), DataContext = section };
            b.Click += (_, _) => Show(section);
            Rail.Children.Add(b);
        }
        var add = new Button { Style = (Style)FindResource("IconButton"), Tag = "plus", ToolTip = L.T("Open media"), Width = 30 };
        add.Click += (_, _) => Player.ShowOpenMenu(add);
        Rail.Children.Add(add);
        Library.Changed += () => { if (IsVisible) Render(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Render(); };
    }

    // ───────── navigation ─────────

    public void Show(Section s) => Navigate(s switch
    {
        Section.Videos => Videos(),
        Section.Music => Music(),
        Section.Network => Network(),
        Section.Playlists => Playlists(),
        Section.History => History(),
        Section.Queue => Queue(),
        _ => Home(),
    });

    /// <summary>Opens a folder page, rooted at the library folder that contains it (or the folder itself).</summary>
    public void ShowFolder(string dir)
    {
        var root = Library.Data.VideoFolders.Concat(Library.Data.MusicFolders)
            .FirstOrDefault(r => dir.StartsWith(r, StringComparison.OrdinalIgnoreCase)) ?? dir;
        var isMusic = Library.Data.MusicFolders.Contains(root);
        Navigate(Folder(dir, root, isMusic ? Section.Music : Section.Videos));
    }

    public Section Current => _pos >= 0 ? _nav[_pos].Section : Section.Home;

    void Navigate(Page p)
    {
        _nav.RemoveRange(_pos + 1, _nav.Count - _pos - 1);
        _nav.Add(p);
        _pos = _nav.Count - 1;
        CloseSearch();
        Render();
    }

    void Back_Click(object sender, RoutedEventArgs e) { if (_pos > 0) { _pos--; Render(); } }
    void Forward_Click(object sender, RoutedEventArgs e) { if (_pos < _nav.Count - 1) { _pos++; Render(); } }

    public void Render()
    {
        if (!IsVisible) return; // re-rendered by IsVisibleChanged when shown
        if (_pos < 0) { Navigate(Home()); return; }
        var page = _nav[_pos];
        var rows = page.Build();

        var q = SearchBox.Text.Trim();
        if (q.Length > 0) rows = rows.Where(r => !r.IsHeader && r.Sortable && r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!rows.Any(r => r.IsHeader))
        {
            var fixedRows = rows.Where(r => !r.Sortable);
            var sorted = rows.Where(r => r.Sortable);
            // folders before files, then by name
            sorted = _descending
                ? sorted.OrderBy(r => r.IsMedia).ThenByDescending(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
                : sorted.OrderBy(r => r.IsMedia).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase);
            if (page.Section is Section.History or Section.Queue) sorted = rows.Where(r => r.Sortable); // keep chronological / queue order
            rows = fixedRows.Concat(sorted).ToList();
        }
        List.ItemsSource = rows;

        var count = rows.Count(r => !r.IsHeader && !r.IsAction);
        CountText.Text = L.Items(count);
        BackBtn.IsEnabled = _pos > 0;
        ForwardBtn.IsEnabled = _pos < _nav.Count - 1;
        PopoutBtn.Visibility = page.Folder != null ? Visibility.Visible : Visibility.Collapsed;
        MoreBtn.Visibility = page.More != null ? Visibility.Visible : Visibility.Collapsed;
        SortBtn.Tag = _descending ? "sort-descending" : "sort-ascending";

        // breadcrumbs: "… ▶ Parent ▶ Current" (last three)
        Crumbs.Children.Clear();
        var crumbs = page.Crumbs.TakeLast(3).ToList();
        if (page.Crumbs.Length > 3) AddCrumb(new Crumb("…", page.Crumbs[^4].Go), true);
        for (int i = 0; i < crumbs.Count; i++) AddCrumb(crumbs[i], i < crumbs.Count - 1);

        // active rail item: filled glyph, accent tint (IconButton's IsChecked trigger)
        foreach (var b in Rail.Children.OfType<ToggleButton>())
        {
            var (section, glyph, activeGlyph, _) = RailItems.First(r => Equals(r.Section, b.DataContext));
            b.IsChecked = section == page.Section;
            b.Tag = b.IsChecked == true ? activeGlyph : glyph;
        }
    }

    void AddCrumb(Crumb c, bool separatorAfter)
    {
        var t = new TextBlock { Text = c.Title, FontSize = 16, Foreground = (Brush)FindResource("FgA2"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 };
        if (c.Go != null)
        {
            t.Cursor = Cursors.Hand;
            t.MouseLeftButtonUp += (_, _) => Navigate(c.Go());
        }
        Crumbs.Children.Add(t);
        if (separatorAfter)
            Crumbs.Children.Add(new TextBlock { Text = "▶", FontSize = 8, Foreground = (Brush)FindResource("FgA4"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
    }

    // ───────── pages ─────────

    static Crumb[] Crumbs1(string title) => [new(L.T(title), null)];

    Page Home() => new(Section.Home, Crumbs1("Home"), () =>
    {
        var rows = new List<Row>();
        var recent = Library.Data.History.OrderByDescending(h => h.Value.LastPlayed).ToList();
        void Block(string header, Func<Page> more, IEnumerable<Row> items)
        {
            var list = items.Take(3).ToList();
            if (list.Count == 0) return;
            rows.Add(new Row { IsHeader = true, Title = L.T(header), Click = () => Navigate(more()) });
            rows.AddRange(list);
        }
        Block("Continue Watching", History,
            recent.Where(h => !h.Value.IsAudio && h.Value.Progress is > 0.01 and < 0.95).Select(h => MediaRow(h.Key, [h.Key], 0)));
        Block("Listen Again", History, recent.Where(h => h.Value.IsAudio).Select(h => MediaRow(h.Key, [h.Key], 0)));
        Block("Folders", Videos, Library.Data.VideoFolders.Concat(Library.Data.MusicFolders).Select(f => FolderRow(f, f, Library.Data.MusicFolders.Contains(f) ? Section.Music : Section.Videos)));
        Block("Playlists", Playlists, Library.Data.Playlists.Select(PlaylistRow));
        if (rows.Count == 0)
        {
            rows.Add(ActionRow("Add a video folder", AddVideoFolder));
            rows.Add(ActionRow("Add a music folder", AddMusicFolder));
        }
        return rows;
    });

    Page Videos() => new(Section.Videos, Crumbs1("Videos"), () =>
        [ActionRow("Add", AddVideoFolder), .. Library.Data.VideoFolders.Select(f => FolderRow(f, f, Section.Videos))]);

    Page Music() => new(Section.Music, Crumbs1("Music"), () =>
    [
        ActionRow("Add", AddMusicFolder),
        IconRow(L.T("Songs"), "music-note", () => Navigate(Songs()), sortable: false),
        IconRow(L.T("Albums"), "vinyl-record", () => Navigate(Group("Albums", m => m.Album)), sortable: false),
        IconRow(L.T("Artists"), "user", () => Navigate(Group("Artists", m => m.Artist)), sortable: false),
        IconRow(L.T("Genres"), "tag", () => Navigate(Group("Genres", m => m.Genre)), sortable: false),
    ]);

    Page Songs() => new(Section.Music, [new(L.T("Music"), Music), new(L.T("Songs"), null)], () =>
        Library.Data.MusicFolders.Select(f => FolderRow(f, f, Section.Music)).ToList());

    /// <summary>Albums / Artists / Genres, from the tag index.</summary>
    Page Group(string title, Func<TrackMeta, string?> key) => new(Section.Music, [new(L.T("Music"), Music), new(L.T(title), null)], () =>
        Library.Data.Meta.Where(m => File.Exists(m.Key))
            .GroupBy(m => key(m.Value) is { Length: > 0 } k ? k : L.T("Unknown"))
            .Select(g => IconRow(g.Key, title == "Artists" ? "user" : title == "Genres" ? "tag" : "vinyl-record", () =>
            {
                var files = g.Select(x => x.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
                Navigate(new Page(Section.Music, [new(L.T("Music"), Music), new(L.T(title), () => Group(title, key)), new(g.Key, null)],
                    () => files.Select((f, i) => MediaRow(f, files, i)).ToList()));
            }, menu: () => FilesMenu(g.Select(x => x.Key).Order(StringComparer.OrdinalIgnoreCase).ToList())))
            .ToList());

    Page Folder(string dir, string root, Section section)
    {
        var crumbs = new List<Crumb> { new(section.ToString(), section == Section.Music ? Songs : Videos) };
        var rel = Path.GetRelativePath(Path.GetDirectoryName(root.TrimEnd('\\')) ?? root, dir).Split('\\');
        var acc = Path.GetDirectoryName(root.TrimEnd('\\')) ?? "";
        for (int i = 0; i < rel.Length; i++)
        {
            acc = Path.Combine(acc, rel[i]);
            var target = acc;
            crumbs.Add(new(rel[i], i == rel.Length - 1 ? null : () => Folder(target, root, section)));
        }
        return new(section, crumbs.ToArray(), () =>
        {
            var files = Library.MediaFiles(dir).ToList();
            return [
                .. Library.MediaFolders(dir).Select(d => FolderRow(d, root, section)),
                .. files.Select((f, i) => MediaRow(f, files, i)),
            ];
        }, Folder: dir, More: () => [Item("Play All", "play", () => PlayFolder(dir)), Item("Refresh", "arrow-clockwise", Render)]);
    }

    Page Network() => new(Section.Network, Crumbs1("Network"), () =>
    [
        ActionRow("Add Network Stream", AddStream),
        IconRow(L.T("Local Network"), "hard-drives", () => Navigate(Discovered(L.T("Local Network"), ref _lan, "upnp", "microdns")), sortable: false),
        IconRow(L.T("Podcasts"), "rss", () => Navigate(Podcasts()), sortable: false),
        IconRow(L.T("Radio"), "radio", () => Navigate(Radio()), sortable: false),
        .. Library.Data.Streams.Select(s => new Row
        {
            Title = s.Name, Glyph = "broadcast",
            Click = () => Player.PlayList([s.Url], 0),
            Menu = () => [Item("Play", "play", () => Player.PlayList([s.Url], 0)), Item("Add to Queue", "queue", () => Player.Enqueue([s.Url])),
                          new Separator(), Item("Remove", "trash", () => { Library.Data.Streams.Remove(s); Library.Touch(); })],
        }),
    ]);

    DiscoveredList? _lan;
    List<NetItem>? _radioWorld, _radioLocal;
    bool _radioLoading;
    readonly Dictionary<string, List<NetItem>?> _browsed = new();

    /// <summary>Stops service discovery before libVLC goes away.</summary>
    public void Shutdown()
    {
        _lan?.Dispose();
        _lan = null;
    }

    Page Discovered(string title, ref DiscoveredList? list, params string[] services)
    {
        if (list == null)
        {
            list = new DiscoveredList(Player.Vlc, Dispatcher, services);
            list.Changed += () => { if (_pos >= 0 && _nav[_pos].Crumbs[^1].Title == title) Render(); };
        }
        return DiscoveredPage(title, list);
    }

    Page DiscoveredPage(string title, DiscoveredList l) => new(Section.Network, [new(L.T("Network"), Network), new(title, null)], () =>
        !l.Available ? [Info("Not supported by this libVLC build")]
        : l.Items.Count == 0 ? [Info("Searching…")]
        : NetRows(l.Items, [new(L.T("Network"), Network), new(title, () => DiscoveredPage(title, l))]));

    /// <summary>Server folder or podcast feed, listed asynchronously.</summary>
    Page Browse(NetItem dir, Crumb[] parent) => new(Section.Network, [.. parent, new(dir.Title, null)], () =>
    {
        if (!_browsed.TryGetValue(dir.Mrl, out var items))
        {
            _browsed[dir.Mrl] = null;
            _ = Net.Browse(Player.Vlc, dir.Mrl).ContinueWith(t =>
            {
                _browsed[dir.Mrl] = t.IsCompletedSuccessfully ? t.Result : [];
                Render();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        return items == null ? [Info("Loading…")] : items.Count == 0 ? [Info("Nothing here")] : NetRows(items, [.. parent, new(dir.Title, () => Browse(dir, parent))]);
    }, More: () => [Item("Refresh", "arrow-clockwise", () => { _browsed.Remove(dir.Mrl); Render(); })]);

    List<Row> NetRows(List<NetItem> items, Crumb[] crumbs)
    {
        var playable = items.Where(x => !x.IsFolder).Select(x => x.Mrl).ToList();
        return items.Select(x => x.IsFolder
            ? IconRow(x.Title, "folder", () => Navigate(Browse(x, crumbs)))
            : new Row
            {
                Title = x.Title, Glyph = "play-circle",
                Click = () => Player.PlayList(playable, playable.IndexOf(x.Mrl)),
                Menu = () => [Item("Play", "play", () => Player.PlayList([x.Mrl], 0)), Item("Add to Queue", "queue", () => Player.Enqueue([x.Mrl])),
                              Item("Save to Network", "floppy-disk", () => { Library.Data.Streams.Add(new SavedStream { Name = x.Title, Url = x.Mrl }); Library.Touch(); })],
            }).ToList();
    }

    Page Podcasts() => new(Section.Network, [new(L.T("Network"), Network), new(L.T("Podcasts"), null)], () =>
    [
        ActionRow("Add Podcast", () =>
        {
            if (DialogWindow.AskName(Player, "Add Podcast", "Feed URL", "Paste the RSS address of the podcast.", "e.g., https://example.com/feed.xml") is { } url
                && Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                Library.Data.Podcasts.Add(new SavedStream { Name = url, Url = url });
                Library.Touch();
            }
        }),
        .. Library.Data.Podcasts.Select(f => new Row
        {
            Title = f.Name, Glyph = "rss",
            Click = () => Navigate(Browse(new NetItem(f.Url, f.Name, true), [new(L.T("Network"), Network), new(L.T("Podcasts"), Podcasts)])),
            Menu = () => [Item("Rename…", "pencil-simple", () =>
                          {
                              if (DialogWindow.AskName(Player, "Rename Podcast", "Name", "How this feed is shown in the list.", "Podcast name", f.Name) is { } n) { f.Name = n; Library.Touch(); }
                          }),
                          Item("Remove", "trash", () => { Library.Data.Podcasts.Remove(f); Library.Touch(); })],
        }),
    ]);

    Page Radio() => new(Section.Network, [new(L.T("Network"), Network), new(L.T("Radio"), null)], () =>
    {
        var country = Net.HomeCountry();
        if (_radioWorld == null && !_radioLoading)
        {
            _radioLoading = true;
            _ = LoadRadio(country);
        }
        if (_radioWorld == null) return [Info(_radioLoading ? "Loading stations…" : "Couldn't reach the radio directory")];
        var crumbs = new Crumb[] { new(L.T("Network"), Network), new(L.T("Radio"), Radio) };
        var rows = new List<Row>();
        if (_radioLocal is { Count: > 0 })
        {
            rows.Add(new Row { IsHeader = true, Title = L.F("Popular in {0}", new RegionInfo(country!).NativeName) });
            rows.AddRange(StationRows(_radioLocal));
        }
        rows.Add(new Row { IsHeader = true, Title = L.T("Popular Worldwide") });
        rows.AddRange(StationRows(_radioWorld));
        return rows;
    }, More: () => [Item("Refresh", "arrow-clockwise", () => { _radioWorld = _radioLocal = null; Render(); })]);

    async Task LoadRadio(string? country)
    {
        try
        {
            var world = Net.RadioStations(null);
            var local = country != null ? Net.RadioStations(country) : Task.FromResult(new List<NetItem>());
            _radioWorld = await world;
            _radioLocal = await local;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
        _radioLoading = false;
        Render();
    }

    IEnumerable<Row> StationRows(List<NetItem> stations) => stations.Select(x => new Row
    {
        Title = x.Title, Image = x.Image ?? "", Placeholder = "radio",
        Click = () => Player.PlayList([x.Mrl], 0),
        Menu = () => [Item("Play", "play", () => Player.PlayList([x.Mrl], 0)),
                      Item("Save to Network", "floppy-disk", () => { Library.Data.Streams.Add(new SavedStream { Name = x.Title, Url = x.Mrl }); Library.Touch(); })],
    });

    static Row Info(string text) => new() { Title = L.T(text), Glyph = "info", GlyphTint = "#6B6B6B", Dim = true, Sortable = false, IsAction = true };

    Page Playlists() => new(Section.Playlists, Crumbs1("Playlists"), () =>
        [ActionRow("Create playlist", () => CreatePlaylist()), .. Library.Data.Playlists.Select(PlaylistRow)]);

    Page PlaylistPage(Playlist p) => new(Section.Playlists, [new(L.T("Playlists"), Playlists), new(p.Name, null)], () =>
        p.Items.Select((f, i) => MediaRow(f, p.Items, i, extra: [new Separator(), Item("Remove from Playlist", "minus-circle", () => { p.Items.RemoveAt(i); Library.Touch(); })])).ToList(),
        More: () => PlaylistMenu(p));

    Page History() => new(Section.History, Crumbs1("History"), () =>
        Library.Data.History.OrderByDescending(h => h.Value.LastPlayed)
            .Select(h => MediaRow(h.Key, [h.Key], 0, extra: [new Separator(), Item("Remove from History", "trash", () => { Library.Data.History.Remove(h.Key); Library.Touch(); })]))
            .ToList(),
        More: () => [Item("Clear History", "trash", () => { Library.Data.History.Clear(); Library.Touch(); })]);

    Page Queue() => new(Section.Queue, Crumbs1("Now Playing"), () =>
        Player.Queue.Select((f, i) => MediaRow(f, Player.Queue.ToList(), i, queueIndex: i,
            extra: [new Separator(), Item("Remove from Queue", "minus-circle", () => { Player.RemoveFromQueue(i); Render(); })])).ToList(),
        More: () => [Item("Save as Playlist…", "playlist", () => CreatePlaylist(Player.Queue)), Item("Clear Queue", "trash", () => { Player.ClearQueue(); Render(); })]);

    // ───────── rows ─────────

    Row MediaRow(string path, IList<string> context, int index, int queueIndex = -1, IEnumerable<object>? extra = null)
    {
        var h = Library.HistoryOf(path);
        var title = Library.Data.Meta.GetValueOrDefault(path) is { Title: { Length: > 0 } t } m
            ? (m.Artist is { Length: > 0 } a ? $"{t} - {a}" : t)
            : Library.DisplayName(path);
        var active = queueIndex >= 0 ? queueIndex == Player.QueueIndex : string.Equals(path, Player.CurrentPath, StringComparison.OrdinalIgnoreCase);
        return new Row
        {
            Title = title, Path = path, Progress = h?.Progress ?? 0, Dim = h != null && !active, Active = active,
            Click = () => { if (queueIndex >= 0) Player.PlayQueueAt(queueIndex); else Player.PlayList(context.ToList(), index); },
            Menu = () => [.. FilesMenu([path], single: true), .. extra ?? []],
        };
    }

    Row FolderRow(string dir, string root, Section section)
    {
        var isRoot = string.Equals(dir, root, StringComparison.OrdinalIgnoreCase);
        return new Row
        {
            Title = Library.DisplayName(dir), Glyph = "folder",
            Click = () => Navigate(Folder(dir, root, section)),
            Menu = () =>
            [
                .. FilesMenu(null, folder: dir),
                .. isRoot ? new object[] { new Separator(), Item("Remove from Library", "minus-circle", () =>
                {
                    Library.Data.VideoFolders.Remove(dir);
                    Library.Data.MusicFolders.Remove(dir);
                    Library.Touch();
                }) } : [],
            ],
        };
    }

    Row PlaylistRow(Playlist p) => new()
    {
        Title = p.Name, Glyph = "playlist", Image = p.Thumb,
        Click = () => Navigate(PlaylistPage(p)),
        Menu = () => PlaylistMenu(p),
    };

    static Row IconRow(string title, string glyph, Action click, bool sortable = true, Func<IEnumerable<object>>? menu = null) =>
        new() { Title = title, Glyph = glyph, Click = click, Sortable = sortable, Menu = menu };

    static Row ActionRow(string title, Action click) =>
        new() { Title = L.T(title), Glyph = "plus", GlyphTint = "#6B6B6B", Dim = true, Click = click, Sortable = false, IsAction = true };

    // ───────── menus & actions ─────────

    /// <summary>Context menu for files, or for a folder (resolved lazily so big folders aren't scanned until used).</summary>
    IEnumerable<object> FilesMenu(List<string>? files, string? folder = null, bool single = false)
    {
        List<string> Resolve() => files ?? Library.MediaFiles(folder!, true).ToList();
        var add = Item("Add to Playlist", "list-plus", () => { });
        foreach (var p in Library.Data.Playlists)
            add.Items.Add(Item(p.Name, "playlist", () => { p.Items.AddRange(Resolve()); Library.Touch(); }));
        if (add.Items.Count > 0) add.Items.Add(new Separator());
        add.Items.Add(Item("New Playlist…", "plus", () => CreatePlaylist(Resolve())));

        var target = folder ?? files![0];
        return
        [
            Item(single ? "Play" : "Play All", "play", () => Player.PlayList(Resolve(), 0)),
            Item("Add to Queue", "queue", () => Player.Enqueue(Resolve())),
            add,
            new Separator(),
            Item("Show in Explorer", "folder-open", () => Reveal(target)),
        ];
    }

    IEnumerable<object> PlaylistMenu(Playlist p) =>
    [
        Item("Play", "play", () => Player.PlayList(p.Items.ToList(), 0)),
        Item("Add to Queue", "queue", () => Player.Enqueue(p.Items)),
        new Separator(),
        Item("Rename…", "pencil-simple", () =>
        {
            if (DialogWindow.AskName(Player, "Rename Playlist", "Playlist Name", "Choose a new title for this playlist.", "e.g., Favorite Videos", p.Name) is { } n)
            { p.Name = n; Library.Touch(); }
        }),
        Item("Change Thumbnail…", "image", () =>
        {
            if (DialogWindow.PickImage(Player) is { } img) { p.Thumb = img; Library.Touch(); }
        }),
        Item("Delete", "trash", () => { Library.Data.Playlists.Remove(p); Library.Touch(); if (_nav[_pos].Section == Section.Playlists) Navigate(Playlists()); }),
    ];

    static MenuItem Item(string header, string glyph, Action click)
    {
        var mi = new MenuItem { Header = L.T(header), Icon = new Icon { Glyph = glyph } };
        mi.Click += (_, e) => { if (e.OriginalSource == mi) click(); };
        return mi;
    }

    void ShowMenu(FrameworkElement anchor, IEnumerable<object> items, PlacementMode placement = PlacementMode.MousePoint)
    {
        var cm = new ContextMenu { PlacementTarget = anchor, Placement = placement };
        foreach (var i in items) cm.Items.Add(i);
        cm.IsOpen = true;
    }

    void AddVideoFolder() => AddFolder(Library.Data.VideoFolders);

    void AddMusicFolder()
    {
        AddFolder(Library.Data.MusicFolders);
        Library.IndexMusic(Dispatcher);
    }

    void AddFolder(List<string> into)
    {
        var dlg = new OpenFolderDialog { Multiselect = true };
        if (dlg.ShowDialog(Player) != true) return;
        into.AddRange(dlg.FolderNames.Where(f => !into.Contains(f, StringComparer.OrdinalIgnoreCase)));
        Library.Touch();
    }

    void AddStream()
    {
        if (DialogWindow.NetworkStream(Player) is not { } r) return;
        if (r.Playlist != null) Library.Data.Playlists.Add(new Playlist { Name = r.Playlist, Items = r.Urls });
        else Library.Data.Streams.AddRange(r.Urls.Where(u => Library.Data.Streams.All(s => s.Url != u)).Select(u => new SavedStream { Name = u, Url = u }));
        Library.Touch();
        Player.PlayList(r.Urls, 0);
    }

    void CreatePlaylist(IEnumerable<string>? preset = null)
    {
        if (DialogWindow.NewPlaylist(Player, preset) is not { } r) return;
        var p = new Playlist { Name = r.Name, Items = r.Files, Thumb = r.Thumb };
        Library.Data.Playlists.Add(p);
        Library.Touch();
        Navigate(PlaylistPage(p));
    }

    void PlayFolder(string dir) => Player.PlayList(Library.MediaFiles(dir, true).ToList(), 0);

    static void Reveal(string path)
    {
        if (path.Contains("://")) return;
        Process.Start("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"");
    }

    // ───────── header buttons & list events ─────────

    void More_Click(object sender, RoutedEventArgs e) { if (_nav[_pos].More is { } m) ShowMenu(MoreBtn, m(), PlacementMode.Bottom); }
    void Popout_Click(object sender, RoutedEventArgs e) { if (_nav[_pos].Folder is { } f) Reveal(f); }
    void Sort_Click(object sender, RoutedEventArgs e) { _descending = !_descending; Render(); }

    void Search_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBtn.IsChecked == true) OpenSearch();
        else CloseSearch();
    }

    public void OpenSearch()
    {
        SearchBtn.IsChecked = true;
        SearchBox.Visibility = Visibility.Visible;
        CountText.Visibility = Visibility.Collapsed;
        SearchBox.Focus();
    }

    void CloseSearch()
    {
        SearchBtn.IsChecked = false;
        SearchBox.Visibility = Visibility.Collapsed;
        CountText.Visibility = Visibility.Visible;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Render();

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseSearch(); Render(); e.Handled = true; }
        else if (e.Key == Key.Enter && List.Items.OfType<Row>().FirstOrDefault(r => r.Click != null) is { } first) { first.Click!(); e.Handled = true; }
        e.Handled |= e.Key is Key.Space or Key.Left or Key.Right or Key.Up or Key.Down or Key.M or Key.F or Key.N or Key.P; // typing, not player hotkeys
    }

#if DEBUG
    public void DebugClick(string title) => List.Items.OfType<Row>().FirstOrDefault(r => r.Title == title)?.Click?.Invoke();
#endif

    void Row_Loaded(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as Row)?.RequestThumb();

    void Item_Click(object sender, MouseButtonEventArgs e) => ((sender as FrameworkElement)?.DataContext as Row)?.Click?.Invoke();

    void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Row { Menu: { } menu } && sender is FrameworkElement fe)
            ShowMenu(fe, menu());
    }
}
