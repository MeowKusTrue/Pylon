using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace Pylon;

/// <summary>
/// First-run onboarding (mockup "Onboarding", 1090x759): welcome + media folder, theme and accent with a live preview,
/// then the basic keyboard shortcuts.
/// </summary>
public class OnboardingWindow : Window
{
    int _step;
    string _folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    readonly Border _card = new();

    public OnboardingWindow(Window owner, int step = 0)
    {
        Owner = owner;
        _step = step;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 1122;
        Height = 791;
        FontFamily = (FontFamily)FindResource("Inter");
        UseLayoutRounding = true;
        _card.Margin = new Thickness(16, 8, 16, 24);
        _card.CornerRadius = new CornerRadius(14);
        _card.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        _card.Effect = new DropShadowEffect { BlurRadius = 15, ShadowDepth = 8, Direction = 270, Opacity = 0.46 };
        _card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Content = _card;
        ThemeManager.Changed += Render;
        Closed += (_, _) => ThemeManager.Changed -= Render;
        Render();
    }

    static Brush R(string key) => (Brush)Application.Current.FindResource(key);
    static Style St(string key) => (Style)Application.Current.FindResource(key);

    void Render()
    {
        var footer = new DockPanel { Margin = new Thickness(0, 24, 0, 0) };
        if (_step > 0)
        {
            var back = new Button { Content = L.T("Back"), Style = St("SecondaryButton") };
            back.Click += (_, _) => { _step--; Render(); };
            footer.Children.Add(back);
        }
        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 3; i++)
            dots.Children.Add(new Border { Width = i == _step ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 0), Background = i == _step ? R("Accent") : R("FgA4") });
        var next = new Button { Content = L.T(_step == 2 ? "Finish" : "Next"), Style = St("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right };
        next.Click += (_, _) => Next();
        DockPanel.SetDock(next, Dock.Right);
        footer.Children.Insert(0, next);
        footer.Children.Add(dots);

        var page = _step switch { 0 => Welcome(), 1 => ThemeStep(), _ => Shortcuts() };
        DockPanel.SetDock(footer, Dock.Bottom);
        _card.Child = new DockPanel { Margin = new Thickness(56, 48, 56, 32), Children = { footer, page } };
    }

    void Next()
    {
        if (_step == 0 && Directory.Exists(_folder))
        {
            var lib = Library.Data.VideoFolders;
            if (!lib.Contains(_folder, StringComparer.OrdinalIgnoreCase) && !Library.Data.MusicFolders.Contains(_folder, StringComparer.OrdinalIgnoreCase))
            {
                lib.Add(_folder);
                Library.Touch();
            }
        }
        if (_step == 2)
        {
            Settings.Set("app.onboarded", true);
            Close();
            return;
        }
        _step++;
        Render();
    }

    FrameworkElement Welcome()
    {
        var path = new TextBox { Style = St("Input"), Text = _folder, Height = 40, Padding = new Thickness(12, 8, 12, 8) };
        path.TextChanged += (_, _) => _folder = path.Text.Trim();
        var browse = new Button { Content = L.T("Browse"), Style = St("SecondaryButton"), Background = R("BgRaised"), Height = 40, Margin = new Thickness(10, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { InitialDirectory = _folder };
            if (dlg.ShowDialog(this) == true) path.Text = dlg.FolderName;
        };
        var pick = new DockPanel { Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        DockPanel.SetDock(browse, Dock.Right);
        pick.Children.Add(browse);
        pick.Children.Add(path);

        return new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Icon { Glyph = "logo", Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left },
                new TextBlock { Text = L.T("Welcome to Pylon!"), FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = R("FgA0"), Margin = new Thickness(0, 28, 0, 14) },
                new TextBlock
                {
                    Text = L.T("Pylon is a free and open source media player built on the VLC engine. It plays most multimedia files as well as DVDs, Audio CDs, VCDs, and various streaming protocols."),
                    FontSize = 16, Foreground = R("FgA2"), TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left, LineHeight = 26,
                },
                new TextBlock { Text = L.T("Choose a Media Folder to get started with"), FontSize = 16, Foreground = R("FgA0"), Margin = new Thickness(0, 48, 0, 12) },
                pick,
            },
        };
    }

    FrameworkElement ThemeStep()
    {
        var themes = new UniformGrid { Rows = 1, Margin = new Thickness(0, 24, 0, 24) };
        foreach (var t in new[] { "Light", "Dark", "System" })
        {
            var selected = Settings.Str("general.theme") == t;
            var swatch = new Border
            {
                Height = 90, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 10),
                Background = t switch { "Light" => new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)), "Dark" => new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)), _ => new LinearGradientBrush(Color.FromRgb(0xF2, 0xF2, 0xF2), Color.FromRgb(0x1B, 0x1B, 0x1B), 0) },
                BorderBrush = R("DividerBright"), BorderThickness = new Thickness(1),
            };
            var card = new Border
            {
                Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = selected ? R("AccentBright") : R("BgRaised"), BorderBrush = selected ? R("Accent") : Brushes.Transparent, BorderThickness = new Thickness(2),
                Child = new StackPanel { Children = { swatch, new TextBlock { Text = L.T(t), FontSize = 14, Foreground = selected ? R("Accent") : R("FgA2"), HorizontalAlignment = HorizontalAlignment.Center } } },
            };
            card.MouseLeftButtonUp += (_, _) => Settings.Set("general.theme", t); // ThemeManager re-renders this window
            themes.Children.Add(card);
        }

        var right = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = L.T("Select your preferred theme and color."), FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = R("FgA0"), TextWrapping = TextWrapping.Wrap },
                themes,
                OptView.Row(Settings.Find("general.accent")!, 0, stacked: true),
            },
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var preview = Preview();
        Grid.SetColumn(right, 2);
        grid.Children.Add(preview);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A miniature of the player (sidebar, stage, controls) drawn with the current theme.</summary>
    static FrameworkElement Preview()
    {
        Border Box(string bg, double w, double h, double r = 3) => new() { Background = R(bg), Width = w, Height = h, CornerRadius = new CornerRadius(r) };
        var rail = new StackPanel { Background = R("BgA4"), Width = 22 };
        for (var i = 0; i < 6; i++) rail.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 8, 0, 0), Background = i == 0 ? R("Accent") : R("FgA4") });
        var list = new StackPanel { Background = R("PanelBg"), Width = 120 };
        for (var i = 0; i < 8; i++)
            list.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(10, i == 0 ? 12 : 7, 0, 0),
                Children = { Box("ThumbBg", 14, 14, 2), new Border { Width = 70, Height = 5, CornerRadius = new CornerRadius(2), Margin = new Thickness(6, 0, 0, 0), Background = i == 2 ? R("Accent") : R("FgA4"), VerticalAlignment = VerticalAlignment.Center } },
            });
        var stage = new Border { Background = R("BgB1"), CornerRadius = new CornerRadius(6), Margin = new Thickness(6), Child = new Icon { Glyph = "play-fill", Tint = "@FgA4", Width = 40, Height = 40 } };
        var controls = new DockPanel { Background = R("BgA3"), Height = 30 };
        var play = Box("Accent", 22, 14);
        play.Margin = new Thickness(10, 0, 10, 0);
        DockPanel.SetDock(play, Dock.Left);
        controls.Children.Add(play);
        controls.Children.Add(new Grid { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Children = { Box("BgB2", double.NaN, 10), new Border { Background = R("AccentBright"), Width = 90, Height = 8, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1) } } });

        var body = new DockPanel();
        DockPanel.SetDock(rail, Dock.Left);
        DockPanel.SetDock(list, Dock.Left);
        body.Children.Add(rail);
        body.Children.Add(list);
        body.Children.Add(stage);
        var top = new Border { Background = R("BgA3"), Height = 18 };
        var shell = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(controls, Dock.Bottom);
        shell.Children.Add(top);
        shell.Children.Add(controls);
        shell.Children.Add(body);
        return new Border
        {
            Height = 330, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(10), ClipToBounds = true, BorderBrush = R("DividerBright"), BorderThickness = new Thickness(1),
            Background = R("BgA3"), Child = shell, Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.35 },
        };
    }

    FrameworkElement Shortcuts()
    {
        (string Name, string[] Keys)[] left =
        [
            ("Play / Pause", ["Space"]), ("Seek Backward / Forward", ["←", "/", "→"]), ("Previous / Next Item", ["P", "/", "N"]),
            ("Next Frame", ["."]), ("Cycle Subtitle Track", ["V"]), ("Cycle Audio Track", ["B"]),
        ];
        (string Name, string[] Keys)[] right =
        [
            ("Toggle Fullscreen", ["F"]), ("Increase / Decrease Volume", ["↑", "/", "↓"]), ("Show / Hide Queue", ["Q"]),
            ("Show / Hide File Browser", ["L"]), ("Take a Snapshot", ["SHIFT", "S"]), ("Record", ["SHIFT", "R"]),
        ];
        StackPanel Column((string Name, string[] Keys)[] rows)
        {
            var col = new StackPanel();
            foreach (var (name, keys) in rows)
            {
                var caps = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var k in keys)
                    caps.Children.Add(k == "/"
                        ? new TextBlock { Text = "/", Foreground = R("FgA3"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }
                        : new Border { MinWidth = 32, Height = 32, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(6), Background = R("BgRaised"), BorderBrush = R("DividerBright"), BorderThickness = new Thickness(0, 0, 0, 2), Margin = new Thickness(4, 0, 0, 0), Child = new TextBlock { Text = k, FontSize = 13, Foreground = R("FgA0"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } });
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
                DockPanel.SetDock(caps, Dock.Right);
                row.Children.Add(caps);
                row.Children.Add(new TextBlock { Text = L.T(name), FontSize = 15, Foreground = R("FgA2"), VerticalAlignment = VerticalAlignment.Center });
                col.Children.Add(row);
            }
            return col;
        }
        var grid = new Grid { Margin = new Thickness(0, 36, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var r = Column(right);
        Grid.SetColumn(r, 2);
        grid.Children.Add(Column(left));
        grid.Children.Add(r);
        return new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = L.T("Learn the basic keyboard shortcuts."), FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = R("FgA0") },
                new TextBlock { Text = L.T("Find the full list in the menu."), FontSize = 16, Foreground = R("FgA3"), Margin = new Thickness(0, 10, 0, 0) },
                grid,
            },
        };
    }
}
