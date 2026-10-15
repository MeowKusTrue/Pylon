using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Pylon;

/// <summary>
/// Right-hand "Effects &amp; Filters" panel (mockup: Audio - Equalizer / Compressor / Spatializer,
/// Video - Essential / Crop / Colors; each card has collapsed and expanded states).
/// </summary>
public class EffectsPanel : Border
{
    readonly StackPanel _body = new();
    bool _video;
    readonly HashSet<string> _expanded = ["Equalizer", "Essential"];

    public event Action? CloseRequested;

    public EffectsPanel()
    {
        Width = 400;
        SetResourceReference(BackgroundProperty, "PanelBg");
        Padding = new Thickness(20, 20, 12, 0);
        Loaded += (_, _) => Render();
        ThemeManager.Changed += () => { if (IsLoaded) Render(); };
    }

    public void ShowTab(bool video)
    {
        _video = video;
        Render();
    }

    static Brush R(string key) => (Brush)Application.Current.FindResource(key);
    static Style St(string key) => (Style)Application.Current.FindResource(key);

    void Render()
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 8, 16) };
        var close = new Button { Style = St("IconButton"), Tag = "x", Padding = new Thickness(2) };
        close.Click += (_, _) => CloseRequested?.Invoke();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = L.T("Effects & Filters"), FontSize = 16, Foreground = R("FgA0"), VerticalAlignment = VerticalAlignment.Center });

        var tabs = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, 8, 16) };
        foreach (var (name, isVideo) in new[] { ("Audio", false), ("Video", true) })
        {
            var rb = new RadioButton { Content = L.T(name), Style = St("Segment"), GroupName = "fxtab" + GetHashCode(), IsChecked = _video == isVideo };
            rb.Checked += (_, _) => { if (_video != isVideo) { _video = isVideo; Render(); } };
            tabs.Children.Add(rb);
        }

        _body.Children.Clear();
        if (_video)
        {
            Card("Essential", null,
                Row("video.aspect"), Row("video.crop"), Row("video.zoom"), Row("video.deint"), Row("video.deintmode"), Row("fxv.rotate"));
            Card("Crop", null, Effects.Video[1].Opts.Select(o => OptView.Row(o, stacked: true)).ToArray());
            Card("Colors", null,
                Row("fxv.hue"), Row("fxv.brightness"), Row("fxv.contrast"), Row("fxv.saturation"), Row("fxv.gamma"),
                Reset("Reset colors", "fxv.hue", "fxv.brightness", "fxv.contrast", "fxv.saturation", "fxv.gamma"));
        }
        else
        {
            Card("Equalizer", "audio.eq", Row("audio.eqpreset"), EqualizerBands(), Row("audio.eq2pass", false));
            Card("Compressor", "audio.compressor", [.. Effects.Audio[0].Opts.Select(o => OptView.Row(o, stacked: true)), Reset("Reset compressor", [.. Effects.Audio[0].Opts.Select(o => o.Key)])]);
            Card("Spatializer", "audio.spatial", [Row("audio.spatialmode"), .. Effects.Audio[1].Opts.Select(o => OptView.Row(o, stacked: true))]);
            Card("Voice & Pitch", null, Row("audio.pitch", false), Row("audio.karaoke", false), Row("audio.chorus", false), Row("audio.normalize", false));
        }

        Child = new DockPanel
        {
            Children =
            {
                Docked(header, Dock.Top), Docked(tabs, Dock.Top),
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body, Padding = new Thickness(0, 0, 8, 20) },
            },
        };
    }

    static FrameworkElement Docked(FrameworkElement e, Dock d) { DockPanel.SetDock(e, d); return e; }

    static FrameworkElement Row(string key, bool stacked = true) => OptView.Row(Settings.Find(key)!, 150, stacked);

    static FrameworkElement Reset(string text, params string[] keys)
    {
        var b = new Button { Content = L.T(text), Style = St("SecondaryButton"), Background = R("BgRaised"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        b.Click += (_, _) => { foreach (var k in keys) Settings.Set(k, Settings.Find(k)!.Default); };
        return b;
    }

    /// <summary>Collapsible card; the toggle (when given) switches the effect itself.</summary>
    void Card(string title, string? toggleKey, params FrameworkElement[] content)
    {
        var expanded = _expanded.Contains(title);
        var head = new DockPanel { Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        if (toggleKey != null)
        {
            var toggle = OptView.Row(new Opt { Kind = OptKind.Toggle, Key = toggleKey, Label = "" }, 0);
            toggle.Margin = new Thickness(0);
            DockPanel.SetDock(toggle, Dock.Right);
            head.Children.Add(toggle);
        }
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Icon { Glyph = expanded ? "caret-down" : "caret-right", Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) },
                new TextBlock { Text = L.T(title), FontSize = 15, Foreground = R("FgA0"), VerticalAlignment = VerticalAlignment.Center },
            },
        });
        head.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) != null) return; // clicks on the toggle
            if (!_expanded.Remove(title)) _expanded.Add(title);
            Render();
        };

        var stack = new StackPanel { Children = { head } };
        if (expanded)
        {
            var inner = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            foreach (var c in content) inner.Children.Add(c);
            stack.Children.Add(inner);
        }
        _body.Children.Add(new Border { Background = R("BgB1"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, expanded ? 6 : 14), Margin = new Thickness(0, 0, 0, 12), Child = stack });
    }

    static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p)) if (p is T t) return t;
        return null;
    }

    /// <summary>Preamp + 10 vertical band sliders (the mockup's "EQ" component).</summary>
    static FrameworkElement EqualizerBands()
    {
        var grid = new UniformGrid { Rows = 1, Height = 200, Margin = new Thickness(0, 4, 0, 14) };
        for (var i = -1; i < Effects.Bands; i++)
        {
            var key = i < 0 ? "fx.eq.preamp" : $"fx.eq.b{i}";
            var slider = new Slider { Style = St("SettingsSlider"), Orientation = Orientation.Vertical, Minimum = -20, Maximum = 20, SmallChange = 0.5, LargeChange = 2, HorizontalAlignment = HorizontalAlignment.Center };
            var value = new TextBlock { FontSize = 10, Foreground = R("FgA3"), HorizontalAlignment = HorizontalAlignment.Center };
            var updating = false;
            void Refresh() { updating = true; slider.Value = Settings.Num(key); value.Text = $"{slider.Value:+0;-0;0}"; updating = false; }
            Action<string> onChange = k => { if (k == key || k == "*") Refresh(); };
            slider.Loaded += (_, _) => { Settings.Changed += onChange; Refresh(); };
            slider.Unloaded += (_, _) => Settings.Changed -= onChange;
            slider.ValueChanged += (_, e) =>
            {
                value.Text = $"{e.NewValue:+0;-0;0}";
                if (updating) return;
                Settings.Set(key, Math.Round(e.NewValue * 2) / 2);
                Effects.BandEdited();
            };
            slider.MouseDoubleClick += (_, _) => Settings.Set(key, 0.0);
            var col = new DockPanel();
            var label = new TextBlock { Text = i < 0 ? L.T("Pre") : Effects.BandLabel(i), FontSize = 10, Foreground = i < 0 ? R("Accent") : R("FgA2"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(label, Dock.Bottom);
            DockPanel.SetDock(value, Dock.Top);
            col.Children.Add(label);
            col.Children.Add(value);
            col.Children.Add(slider);
            grid.Children.Add(col);
        }
        return grid;
    }
}
