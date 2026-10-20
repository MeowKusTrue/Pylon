using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pylon;

public partial class DialogWindow
{
    /// <summary>
    /// "Edit Tags…": title, artist, album, album artist, year, track number, genre and the cover, written into the file
    /// itself (TagLib#: ID3 in MP3, Vorbis comments in FLAC / OGG / Opus, atoms in M4A / MP4, ASF in WMA). True when saved.
    /// </summary>
    public static bool EditTags(Window owner, string path, Action? release = null, Action? resume = null)
    {
        TagLib.File file;
        try { file = TagLib.File.Create(path); }
        catch (Exception e) when (e is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, L.F("Pylon can't edit the tags of this file ({0}).", e.Message), L.T("Edit Tags"));
            return false;
        }
        using (file)
        {
            var t = file.Tag;
            var title = Input("Title", t.Title ?? "");
            var artist = Input("Artist", t.JoinedPerformers ?? "");
            var album = Input("Album", t.Album ?? "");
            var albumArtist = Input("Album artist", t.JoinedAlbumArtists ?? "");
            var year = Input("Year", t.Year > 0 ? t.Year.ToString() : "");
            var track = Input("Track number", t.Track > 0 ? t.Track.ToString() : "");
            var genre = Input("Genre", t.JoinedGenres ?? "");

            // the cover: what the file has, a picture chosen on disk, one found online, or none
            byte[]? cover = t.Pictures.FirstOrDefault()?.Data.Data;
            var coverChanged = false;
            var art = new Image { Width = 120, Height = 120, Stretch = Stretch.UniformToFill };
            void ShowCover() => art.Source = cover == null ? null : Decode(cover);
            ShowCover();
            Button Btn(string text) => new() { Content = L.T(text), Style = (Style)Application.Current.FindResource("SecondaryButton"), Height = 32, Margin = new Thickness(0, 0, 0, 8), Background = R("BgRaised") };
            var pick = Btn("Choose a picture…");
            var find = Btn("Find online");
            var remove = Btn("Remove cover");
            pick.Click += (_, _) =>
            {
                if (PickImage(owner) is not { } img) return;
                try { cover = File.ReadAllBytes(img); coverChanged = true; ShowCover(); } catch (IOException) { }
            };
            find.Click += async (_, _) =>
            {
                find.IsEnabled = false;
                if (await Covers.FindAsync(artist.Text.Trim(), title.Text.Trim(), album.Text.Trim()) is { } found)
                    try { cover = File.ReadAllBytes(found); coverChanged = true; ShowCover(); } catch (IOException) { }
                find.IsEnabled = true;
            };
            remove.Click += (_, _) => { cover = null; coverChanged = true; ShowCover(); };

            Grid Row(TextBox a, TextBox b)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 10), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(10) }, new ColumnDefinition() } };
                Grid.SetColumn(b, 2);
                g.Children.Add(a);
                g.Children.Add(b);
                return g;
            }
            var fields = Stack(Row(title, artist), Row(album, albumArtist), Row(year, track), genre);
            var coverBox = new StackPanel { Margin = new Thickness(18, 0, 0, 0), Children =
            {
                new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = R("BgB2"), Width = 120, Height = 120, Margin = new Thickness(0, 0, 0, 10), Child = art },
                pick, find, remove,
            } };
            var layout = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            Grid.SetColumn(coverBox, 1);
            layout.Children.Add(fields);
            layout.Children.Add(coverBox);
            var dlg = new DialogWindow(owner, "Edit Tags", Stack(Section(Path.GetFileName(path), "Saved into the file itself, so other players and phones see the same."), layout, Spacer(16))) { Width = 720 };
            dlg.SaveBtn.Content = L.T("Save");
            if (!dlg.Run(() => true)) return false;

            static string[] Many(string s) => [.. s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            t.Title = title.Text.Trim();
            t.Performers = Many(artist.Text);
            t.Album = album.Text.Trim();
            t.AlbumArtists = Many(albumArtist.Text);
            t.Year = uint.TryParse(year.Text.Trim(), out var y) ? y : 0;
            t.Track = uint.TryParse(track.Text.Trim(), out var n) ? n : 0;
            t.Genres = Many(genre.Text);
            if (coverChanged)
                t.Pictures = cover == null ? [] : [new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover }];
            release?.Invoke(); // the playing file is held open by libVLC: let go of it for the save
            try { file.Save(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TagLib.CorruptFileException)
            {
                MessageBox.Show(owner, L.F("The tags couldn't be saved ({0}).", e.Message), L.T("Edit Tags"));
                return false;
            }
            finally { resume?.Invoke(); }
            return true;
        }
    }

    static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 240;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or ArgumentException) { return null; }
    }
}

public partial class DialogWindow
{
    static readonly (string Code, string Name)[] SpeechLanguages =
    [
        ("auto", "Detect automatically"), ("en", "English"), ("ru", "Русский"), ("uk", "Українська"), ("de", "Deutsch"), ("fr", "Français"),
        ("es", "Español"), ("it", "Italiano"), ("pt", "Português"), ("pl", "Polski"), ("tr", "Türkçe"), ("ja", "日本語"), ("ko", "한국어"), ("zh", "中文"),
    ];

    /// <summary>"Subtitles from Speech…": language, quality, GPU, translation; the download size is shown before starting.</summary>
    public static Whisper.Options? WhisperOptions(Window owner)
    {
        var lang = new ComboBox { Style = (Style)Application.Current.FindResource("Dropdown"), ItemsSource = SpeechLanguages.Select(l => L.T(l.Name)).ToList(), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12) };
        var quality = new ComboBox { Style = (Style)Application.Current.FindResource("Dropdown"), Margin = new Thickness(0, 0, 0, 12),
            ItemsSource = new[] { L.T("Fast"), L.T("Accurate"), L.T("Best (for a graphics card)") }, SelectedIndex = 1 };
        var gpu = new CheckBox { Style = (Style)Application.Current.FindResource("ToggleSwitch"), IsChecked = Whisper.HasNvidia, IsEnabled = Whisper.HasNvidia, VerticalAlignment = VerticalAlignment.Center };
        var english = new CheckBox { Style = (Style)Application.Current.FindResource("ToggleSwitch"), IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
        var size = new TextBlock { FontSize = 13, Foreground = R("FgA3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        Whisper.Options Read() => new((Whisper.Quality)Math.Max(0, quality.SelectedIndex), gpu.IsChecked == true, SpeechLanguages[Math.Max(0, lang.SelectedIndex)].Code, english.IsChecked == true);
        void Update()
        {
            var mb = Whisper.MissingMb(Read());
            size.Text = mb == 0 ? L.T("Everything is downloaded already.")
                : L.F("Pylon downloads about {0} MB once (whisper.cpp and the speech model from GitHub and Hugging Face).", mb);
        }
        lang.SelectionChanged += (_, _) => Update();
        quality.SelectionChanged += (_, _) => Update();
        gpu.Checked += (_, _) => Update();
        gpu.Unchecked += (_, _) => Update();
        Update();
        FrameworkElement Labeled(string label, FrameworkElement control)
        {
            var d = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(control, Dock.Right);
            d.Children.Add(control);
            d.Children.Add(new TextBlock { Text = L.T(label), FontSize = 14, Foreground = R("FgA2"), VerticalAlignment = VerticalAlignment.Center });
            return d;
        }
        var body = Stack(
            Section("Subtitles from speech", "Pylon listens to the dialogue and writes subtitles beside the video. It runs in the background; a film takes from a few minutes on a graphics card to about its own length on the processor."),
            new TextBlock { Text = L.T("Spoken language"), FontSize = 14, Foreground = R("FgA2"), Margin = new Thickness(0, 0, 0, 6) }, lang,
            new TextBlock { Text = L.T("Quality"), FontSize = 14, Foreground = R("FgA2"), Margin = new Thickness(0, 0, 0, 6) }, quality,
            Labeled(Whisper.HasNvidia ? "Use the NVIDIA graphics card" : "Use the graphics card (NVIDIA only)", gpu),
            Labeled("Translate into English", english),
            size, Spacer(14));
        var dlg = new DialogWindow(owner, "Subtitles from Speech", body) { Width = 600 };
        dlg.SaveBtn.Content = L.T("Start");
        return dlg.Run(() => true) ? Read() : null;
    }
}

