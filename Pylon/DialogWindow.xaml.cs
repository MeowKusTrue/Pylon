using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace Pylon;

/// <summary>Dialog shell from the mockup (header + body + Cancel/Save). The static helpers build the concrete dialogs.</summary>
public partial class DialogWindow : Window
{
    Func<bool> _canSave = () => true;

    DialogWindow(Window owner, string title, UIElement body)
    {
        InitializeComponent();
        if (Motion.ByPointer) Loaded += (_, _) => Motion.Pop((UIElement)Content);
        Owner = owner;
        TitleText.Text = L.T(title);
        CancelBtn.Content = L.T("Cancel");
        SaveBtn.Content = L.T("Save");
        Body.Content = body;
        Loaded += (_, _) => (FindFirst<TextBox>(body))?.Focus();
    }

    void Revalidate() => SaveBtn.IsEnabled = _canSave();
    void Header_MouseDown(object sender, MouseButtonEventArgs e) => DragMove();
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_canSave()) return;
        if (_work != null) await _work(); // dialogs that do their job in place (Convert) and close themselves
        else DialogResult = true;
    }

    bool Run(Func<bool> canSave, params TextBox[] watch)
    {
        _canSave = canSave;
        foreach (var t in watch) t.TextChanged += (_, _) => Revalidate();
        Revalidate();
        return ShowDialog() == true;
    }

    // ───────── concrete dialogs ─────────

    public static string? AskName(Window owner, string title, string heading, string hint, string placeholder, string initial = "")
    {
        var name = Input(placeholder, initial);
        var dlg = new DialogWindow(owner, title, Stack(Section(heading, hint), name, Spacer(20)));
        dlg.Width = 560;
        return dlg.Run(() => name.Text.Trim().Length > 0, name) ? name.Text.Trim() : null;
    }

    public static string? PickImage(Window owner)
    {
        var ofd = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*" };
        return ofd.ShowDialog(owner) == true ? ofd.FileName : null;
    }

    public static (string Name, List<string> Files, string? Thumb)? NewPlaylist(Window owner, IEnumerable<string>? preset = null)
    {
        var files = preset?.ToList() ?? [];
        string? thumb = null;
        var name = Input("e.g., Favorite Videos");

        // 112px thumbnail picker next to the title field (mockup)
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };
        var thumbBox = new Border
        {
            Width = 112, Height = 112, CornerRadius = new CornerRadius(6), Background = R("BgB2"), BorderBrush = R("BgA4"), BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand, Margin = new Thickness(20, 0, 0, 0), ClipToBounds = true, ToolTip = L.T("Choose a thumbnail"),
            Child = new Grid { Children = { new Border { Background = R("FgB1"), CornerRadius = new CornerRadius(5), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = new Icon { Glyph = "plus" } }, thumbImage } },
        };
        DialogWindow? dlg = null;
        thumbBox.MouseLeftButtonUp += (_, _) =>
        {
            if (PickImage(dlg!) is { } img && Library.LoadImage(img) is { } src) { thumb = img; thumbImage.Source = src; }
        };

        var dropText = new TextBlock { Foreground = R("FgA4"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
        var addBtn = new Border { Background = R("FgB1"), CornerRadius = new CornerRadius(5), Padding = new Thickness(30, 3, 30, 3), Child = new Icon { Glyph = "plus" } };
        var drop = new Border
        {
            Height = 186, CornerRadius = new CornerRadius(6), Background = R("BgB2"), Cursor = Cursors.Hand, AllowDrop = true,
            BorderBrush = new VisualBrush(new Rectangle { Stroke = R("DividerBright"), StrokeThickness = 2, StrokeDashArray = [3, 2], Width = 676, Height = 186, RadiusX = 6, RadiusY = 6 }),
            BorderThickness = new Thickness(2),
            Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { dropText, Spacer(20), new Border { HorizontalAlignment = HorizontalAlignment.Center, Child = addBtn } } },
        };
        void Refresh() => dropText.Text = files.Count == 0 ? L.T("Drag files into the box or click here to select files.") : L.F("{0} file(s) selected — click to add more.", files.Count);
        Refresh();
        drop.MouseLeftButtonUp += (_, _) =>
        {
            var ofd = new OpenFileDialog { Multiselect = true, Filter = MainWindow.MediaFilter };
            if (ofd.ShowDialog(dlg) == true) { files.AddRange(ofd.FileNames); Refresh(); }
        };
        drop.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
            {
                files.AddRange(dropped.SelectMany(p => Directory.Exists(p) ? Library.MediaFiles(p, true) : [p]));
                Refresh();
            }
        };

        var details = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(thumbBox, Dock.Right);
        details.Children.Add(thumbBox);
        details.Children.Add(Stack(Section("Playlist Details", "The playlist will be saved and displayed with this title and thumbnail."), name));

        var body = Stack(details, Spacer(20), Divider(), Spacer(20), Section("Select Files", "These files will be added to the playlist."), drop);
        dlg = new DialogWindow(owner, "New Playlist", body);
        return dlg.Run(() => name.Text.Trim().Length > 0, name) ? (name.Text.Trim(), files, thumb) : null;
    }

    public static (List<string> Urls, string? Playlist)? NetworkStream(Window owner)
    {
        var urls = Input("e.g., https://www.example.com/stream.avi");
        urls.AcceptsReturn = true;
        urls.Height = 160;
        urls.VerticalContentAlignment = VerticalAlignment.Top;
        urls.TextWrapping = TextWrapping.NoWrap;
        var name = Input("e.g., Favorite TV Channels");
        var optional = Section("Playlist Name", "The playlist will be saved and displayed with this title.");
        ((TextBlock)optional.Children[0]).Inlines.Add(new System.Windows.Documents.Run(" " + L.T("(optional)")) { Foreground = Brush("#6B6B6B") });

        var body = Stack(Section("Network URL", "You can enter multiple links to load."), urls, Spacer(20), Divider(), Spacer(20), optional, name, Spacer(40));
        var dlg = new DialogWindow(owner, "Open Network Stream", body) { Width = 1078 };
        dlg.SaveBtn.Content = L.T("Open");
        List<string> Links() => urls.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var x) && !x.IsFile).ToList();
        return dlg.Run(() => Links().Count > 0, urls) ? (Links(), string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim()) : null;
    }

    // ───────── Keyboard Shortcuts (mockup 1048x800: search, Action | Hotkey | Global Hotkey) ─────────

    public static void EditHotkeys(Window owner)
    {
        var local = Hotkeys.Actions.ToDictionary(a => a.Id, a => Hotkeys.Gesture(a.Id));
        var global = Hotkeys.Actions.ToDictionary(a => a.Id, a => Hotkeys.GlobalGesture(a.Id));
        Button? capturing = null;
        Action<string>? capture = null;

        var search = Input("Search Keyboard Shortcuts...");
        var field = new ComboBox { Style = (Style)Application.Current.FindResource("Dropdown"), Width = 160, Margin = new Thickness(12, 0, 0, 0), ItemsSource = new[] { L.T("All Fields"), L.T("Action"), L.T("Hotkey") }, SelectedIndex = 0 };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(field, Dock.Right);
        top.Children.Add(field);
        top.Children.Add(search);

        var header = Columns(Label("Action"), Label("Hotkey"), Label("Global Hotkey"));
        header.Margin = new Thickness(0, 0, 0, 8);
        var list = new StackPanel();
        var scroll = new ScrollViewer { Height = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list, Padding = new Thickness(0, 0, 8, 0) };

        Button KeyButton(string id, Dictionary<string, string> map)
        {
            var b = new Button { Style = (Style)Application.Current.FindResource("SecondaryButton"), Background = R("BgRaised"), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140, Height = 30, FontWeight = FontWeights.Normal };
            b.Content = map[id] is { Length: > 0 } g ? Hotkeys.Display(g) : "—";
            b.Click += (_, _) =>
            {
                if (capturing != null) capturing.Content = "…";
                capturing = b;
                b.Content = L.T("Press keys… (Backspace clears)");
                capture = g => { map[id] = g; b.Content = g.Length > 0 ? Hotkeys.Display(g) : "—"; };
            };
            return b;
        }

        void Fill()
        {
            list.Children.Clear();
            var q = search.Text.Trim();
            foreach (var group in Hotkeys.Actions.GroupBy(a => a.Group))
            {
                var rows = group.Where(a => q.Length == 0 || field.SelectedIndex switch
                {
                    1 => (a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || L.T(a.Name).Contains(q, StringComparison.OrdinalIgnoreCase)),
                    2 => Hotkeys.Display(local[a.Id]).Contains(q, StringComparison.OrdinalIgnoreCase),
                    _ => (a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || L.T(a.Name).Contains(q, StringComparison.OrdinalIgnoreCase)) || Hotkeys.Display(local[a.Id]).Contains(q, StringComparison.OrdinalIgnoreCase),
                }).ToList();
                if (rows.Count == 0) continue;
                list.Children.Add(new TextBlock { Text = L.T(group.Key), FontSize = 16, Foreground = R("FgA0"), Margin = new Thickness(0, list.Children.Count == 0 ? 4 : 18, 0, 8) });
                foreach (var a in rows)
                {
                    var row = Columns(new TextBlock { Text = L.T(a.Name), FontSize = 14, Foreground = R("FgA2"), VerticalAlignment = VerticalAlignment.Center }, KeyButton(a.Id, local), KeyButton(a.Id, global));
                    row.Margin = new Thickness(0, 0, 0, 6);
                    list.Children.Add(row);
                }
            }
        }
        search.TextChanged += (_, _) => Fill();
        field.SelectionChanged += (_, _) => Fill();
        Fill();

        var dlg = new DialogWindow(owner, "Keyboard Shortcuts", Stack(top, header, Divider(), Spacer(8), scroll, Spacer(10))) { Width = 1080 };
        dlg.PreviewKeyDown += (_, e) =>
        {
            if (capturing == null) return;
            e.Handled = true; // swallow Esc / Enter so they can be bound too
            var g = e.Key == Key.Back ? "" : Hotkeys.FromEvent(e);
            if (e.Key != Key.Back && g.Length == 0) return; // lone modifier: keep waiting
            capture?.Invoke(g);
            capturing = null;
        };
        if (!dlg.Run(() => true)) return;
        foreach (var a in Hotkeys.Actions)
        {
            Settings.Set("hotkey." + a.Id, local[a.Id] == a.Default ? "" : local[a.Id]);
            Settings.Set("ghotkey." + a.Id, global[a.Id]);
        }
    }

    static TextBlock Label(string text) => new() { Text = L.T(text), FontSize = 13, Foreground = R("FgA3") };

    static Grid Columns(params UIElement[] cells)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]); }
        return g;
    }

    // ───────── Update available ─────────

    /// <summary>"Pylon x.y.z is available" with the release notes. True = install (or download, for portable copies).</summary>
    public static bool UpdateAvailable(Window owner, Updater.Release r, bool installed)
    {
        var notes = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(r.Notes) ? L.T("No release notes.") : PlainNotes(LocalNotes(r.Notes)),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = R("FgA2"), FontSize = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320,
        };
        var body = Stack(
            Section(L.F("Pylon {0} is available", r.Version.ToString(3)), L.F("You have version {0}.", Updater.Current.ToString(3))),
            new Border { Background = R("BgB2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Child = notes },
            Spacer(10));
        var dlg = new DialogWindow(owner, "Update available", body) { Width = 640 };
        dlg.CancelBtn.Content = L.T("Later");
        dlg.SaveBtn.Content = L.T(installed && r.SetupUrl != null ? "Update now" : "Download");
        return dlg.Run(() => true);
    }

    public static void WhatsNew(Window owner, Version v, string notes)
    {
        var text = new TextBox
        {
            Text = PlainNotes(LocalNotes(notes)), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = R("FgA2"), FontSize = 14, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 380,
        };
        var body = Stack(
            Section(L.F("Pylon updated to {0}", v.ToString(3)), L.T("Here's what's new.")),
            new Border { Background = R("BgB2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 10, 14, 10), Child = text },
            Spacer(10));
        var dlg = new DialogWindow(owner, "What's new", body) { Width = 640 };
        dlg.CancelBtn.Visibility = Visibility.Collapsed;
        dlg.SaveBtn.Content = L.T("Got it");
        dlg.Run(() => true);
    }

    /// <summary>Release notes carry both languages, marked "&lt;!-- ru --&gt;" and "&lt;!-- en --&gt;": keep the one the interface uses.</summary>
    static string LocalNotes(string md)
    {
        var ru = md.IndexOf("<!-- ru -->", StringComparison.Ordinal);
        var en = md.IndexOf("<!-- en -->", StringComparison.Ordinal);
        if (ru < 0 || en < 0) return md;
        var (from, to) = L.Ru ? (ru, en > ru ? en : md.Length) : (en, ru > en ? ru : md.Length);
        return md[(from + 11)..to].Trim();
    }

    /// <summary>GitHub release notes are Markdown: drop heading marks and emphasis, turn list items into bullets.</summary>
    static string PlainNotes(string md) => string.Join("\n", md.Replace("\r\n", "\n").Split('\n')
        .Select(l => System.Text.RegularExpressions.Regex.Replace(l.TrimEnd(), @"^#+\s*", ""))
        .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\s*[*-]\s+", "• "))
        .Select(l => l.Replace("**", "").Replace("`", ""))).Trim();

    // ───────── About ─────────

    public static void About(Window owner, string libVlcVersion)
    {
        var logo = new Icon { Glyph = "logo", Tint = "@FgA0", Width = 72, Height = 72, Margin = new Thickness(0, 0, 24, 0), VerticalAlignment = VerticalAlignment.Top };
        var text = Stack(
            new TextBlock { Text = "Pylon", FontSize = 22, Foreground = R("FgA0") },
            new TextBlock { Text = L.T("A VLC redesign built on libVLC"), FontSize = 14, Foreground = R("Accent"), Margin = new Thickness(0, 4, 0, 14) },
            new TextBlock { Text = $"{L.T("Player")} {typeof(DialogWindow).Assembly.GetName().Version?.ToString(3)}  ·  libVLC {libVlcVersion.Split(' ')[0]}", FontSize = 14, Foreground = R("FgA2") },
            new TextBlock
            {
                Text = L.T("Free software under the GPL-3.0. Playback by libVLC from VideoLAN (LGPL-2.1); VLC is a trademark of VideoLAN and Pylon is not affiliated with it. Interface based on Juxtopposed's \"VLC Redesign\" (Figma Community) — thank you! Icons: Phosphor (MIT). Radio directory: radio-browser.info."),
                FontSize = 13, Foreground = R("FgA3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), LineHeight = 21,
            });
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(logo, Dock.Left);
        row.Children.Add(logo);
        row.Children.Add(text);
        var dlg = new DialogWindow(owner, "About Pylon", row) { Width = 620 };
        dlg.CancelBtn.Visibility = Visibility.Collapsed;
        dlg.SaveBtn.Content = L.T("Close");
        dlg.Run(() => true);
    }

    // ───────── Add Subtitles (mockup: target "to this file / folder", origin, file list) ─────────

    public static Dictionary<string, List<string>>? AddSubtitles(Window owner, string video)
    {
        var subs = new List<string>();
        var dir = Path.GetDirectoryName(video) ?? "";
        var videos = Directory.Exists(dir) ? Library.MediaFiles(dir).Where(Library.IsVideo).ToList() : [video];
        var target = new ComboBox { Style = (Style)Application.Current.FindResource("Dropdown"), Width = 240, ItemsSource = new[] { L.T("To this file"), L.T("To this folder") }, SelectedIndex = 0 };
        var origin = new ComboBox { Style = (Style)Application.Current.FindResource("Dropdown"), Width = 240, Margin = new Thickness(12, 0, 0, 0), ItemsSource = new[] { L.T("From computer"), L.T("From OpenSubtitles (needs an account)") }, SelectedIndex = 0 };
        origin.SelectionChanged += (_, _) => origin.SelectedIndex = 0; // only local files for now

        var mapping = new StackPanel();
        var dropText = new TextBlock { Foreground = R("FgA4"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
        var drop = new Border
        {
            Height = 110, CornerRadius = new CornerRadius(6), Background = R("BgB2"), Cursor = Cursors.Hand, AllowDrop = true, Margin = new Thickness(0, 16, 0, 16),
            BorderBrush = new VisualBrush(new Rectangle { Stroke = R("DividerBright"), StrokeThickness = 2, StrokeDashArray = [3, 2], Width = 676, Height = 110, RadiusX = 6, RadiusY = 6 }),
            BorderThickness = new Thickness(2), Child = dropText,
        };

        Dictionary<string, List<string>> Map()
        {
            if (target.SelectedIndex == 0) return new() { [video] = subs.ToList() };
            // folder: pair each subtitle with the video sharing its episode tag (S01E02 / 1x02 / trailing number) or name
            var map = new Dictionary<string, List<string>>();
            foreach (var s in subs)
            {
                var v = videos.FirstOrDefault(x => Tag(x) is { } t && t == Tag(s))
                        ?? videos.FirstOrDefault(x => Path.GetFileNameWithoutExtension(s).StartsWith(Path.GetFileNameWithoutExtension(x), StringComparison.OrdinalIgnoreCase));
                if (v != null) (map.TryGetValue(v, out var l) ? l : map[v] = []).Add(s);
            }
            return map;
        }

        void Refresh()
        {
            dropText.Text = subs.Count == 0 ? L.T("Drag subtitle files here or click to select them.") : L.F("{0} subtitle file(s) — click to add more.", subs.Count);
            mapping.Children.Clear();
            var map = Map();
            foreach (var v in target.SelectedIndex == 0 ? [video] : videos)
            {
                var row = Columns(new TextBlock { Text = Path.GetFileName(v), FontSize = 14, Foreground = R("FgA2"), TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = "←", FontSize = 14, Foreground = R("FgA4"), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = map.TryGetValue(v, out var l) ? string.Join(", ", l.Select(Path.GetFileName)) : "—", FontSize = 14, Foreground = map.ContainsKey(v) ? R("Accent") : R("FgA4"), TextTrimming = TextTrimming.CharacterEllipsis });
                row.Margin = new Thickness(0, 0, 0, 6);
                mapping.Children.Add(row);
            }
        }

        DialogWindow? dlg = null;
        drop.MouseLeftButtonUp += (_, _) =>
        {
            var ofd = new OpenFileDialog { Multiselect = true, InitialDirectory = dir, Filter = "Subtitles|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.idx;*.smi|All files|*.*" };
            if (ofd.ShowDialog(dlg) == true) { subs.AddRange(ofd.FileNames); Refresh(); }
        };
        drop.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) { subs.AddRange(f); Refresh(); } };
        target.SelectionChanged += (_, _) => Refresh();
        Refresh();

        var options = new StackPanel { Orientation = Orientation.Horizontal, Children = { target, origin } };
        var body = Stack(Section("Add Subtitles", $"{(target.SelectedIndex == 0 ? Path.GetFileName(video) : dir)}"), options, drop,
            new ScrollViewer { MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = mapping }, Spacer(10));
        dlg = new DialogWindow(owner, "Add Subtitles", body) { Width = 1080 };
        return dlg.Run(() => subs.Count > 0) ? Map() : null;

        static string? Tag(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"[Ss](\d{1,2})[Ee](\d{1,3})|(\d{1,2})x(\d{2,3})");
            if (m.Success) return m.Groups[1].Success ? $"{int.Parse(m.Groups[1].Value)}-{int.Parse(m.Groups[2].Value)}" : $"{int.Parse(m.Groups[3].Value)}-{int.Parse(m.Groups[4].Value)}";
            var n = System.Text.RegularExpressions.Regex.Match(name, @"(\d+)\D*$");
            return n.Success ? "n" + int.Parse(n.Groups[1].Value) : null;
        }
    }

    // ───────── building blocks ─────────

    static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    static Brush R(string key) => (Brush)Application.Current.FindResource(key); // current theme brush

    static TextBox Input(string placeholder, string text = "") =>
        new() { Style = (Style)Application.Current.FindResource("Input"), Tag = L.T(placeholder), Text = text, Height = 41 };

    static StackPanel Section(string heading, string hint) => new()
    {
        Margin = new Thickness(0, 0, 0, 20),
        Children =
        {
            new TextBlock { Text = L.T(heading), FontSize = 16, Foreground = Brush("#C6C6C6"), LineHeight = 28 },
            new TextBlock { Text = L.T(hint), FontSize = 14, Foreground = Brush("#6B6B6B"), LineHeight = 24.5, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap },
        },
    };

    static Border Divider() => new() { Height = 1, Background = Brush("#222222") };
    static FrameworkElement Spacer(double h) => new Border { Height = h };

    static StackPanel Stack(params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindFirst<T>(c) is { } found) return found;
        }
        return null;
    }
}
