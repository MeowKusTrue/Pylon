using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Pylon;

/// <summary>Preferences window from the mockup: 13 categories in three groups, search across everything, collapsible advanced options.</summary>
public partial class SettingsWindow : Window
{
    Category _current;
    bool _advanced;

    public SettingsWindow(Window owner, string? category = null)
    {
        InitializeComponent();
        if (Motion.ByPointer) Loaded += (_, _) => Motion.Pop((UIElement)Content);
        Owner = owner;
        var work = SystemParameters.WorkArea;
        if (Height > work.Height - 20) Height = work.Height - 20;
        _current = Settings.Categories.FirstOrDefault(c => c.Name == category) ?? Settings.Categories[0];
        BuildNav();
        Render();
        // the theme and accent can change from this very window
        Action rerender = () => { BuildNav(); Render(); };
        ThemeManager.Changed += rerender;
        Closed += (_, _) => ThemeManager.Changed -= rerender;
        Action<string> onChange = k => { if (k is "general.dev" or "*") Render(); };
        Settings.Changed += onChange;
        Closed += (_, _) => Settings.Changed -= onChange;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    public void Show(string category)
    {
        _current = Settings.Categories.FirstOrDefault(c => c.Name == category) ?? _current;
        BuildNav();
        Render();
        Activate();
    }

    void BuildNav()
    {
        Nav.Children.Clear();
        foreach (var group in Settings.Categories.GroupBy(c => c.Group))
        {
            Nav.Children.Add(new TextBlock { Text = L.T(group.Key), FontSize = 13, Foreground = (Brush)FindResource("FgA3"), Margin = new Thickness(10, Nav.Children.Count == 0 ? 6 : 14, 0, 6) });
            foreach (var c in group)
            {
                var rb = new RadioButton { Content = L.T(c.Name), Tag = c.Glyph, Style = (Style)FindResource("NavItem"), GroupName = "nav", IsChecked = c == _current };
                rb.Checked += (_, _) =>
                {
                    _current = c;
                    _advanced = false;
                    if (SearchBox.Text.Length > 0) SearchBox.Text = ""; // also re-renders
                    else Render();
                    Scroller.ScrollToTop();
                    if (Motion.ByPointer) Motion.Pop(Page, dy: 14, scale: 1, spring: Spring.Smooth);
                };
                Nav.Children.Add(rb);
            }
        }
    }

    void Render()
    {
        Page.Children.Clear();
        var q = SearchBox.Text.Trim();
        if (q.Length > 0) { RenderSearch(q); return; }

        PageTitle.Text = L.T(_current.Title);
        foreach (var s in _current.Sections.Where(s => s.Visible?.Invoke() != false)) AddSection(s);
        if (_current.Advanced.Count > 0)
        {
            var bar = new Button { Style = (Style)FindResource("SecondaryButton"), Height = 36, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 10, 0, 16), Background = (Brush)FindResource("BgRaised") };
            bar.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = L.T("Advanced Options"), FontWeight = FontWeights.Normal, Foreground = (Brush)FindResource("FgA2"), VerticalAlignment = VerticalAlignment.Center },
                    new Icon { Glyph = _advanced ? "caret-down" : "caret-right", Width = 18, Height = 18, Margin = new Thickness(8, 0, 0, 0) },
                },
            };
            bar.Click += (_, _) => { _advanced = !_advanced; Render(); };
            Page.Children.Add(bar);
            if (_advanced) foreach (var s in _current.Advanced.Where(s => s.Visible?.Invoke() != false)) AddSection(s);
        }
    }

    void AddSection(Section s, string? prefix = null)
    {
        // options libVLC can't do (or Pylon doesn't yet) stay out of sight: greyed-out rows only confused
        s = s with { Opts = [.. s.Opts.Where(o => o.Unavailable == null)] };
        if (s.Opts.Count == 0) return;
        if (s.Heading.Length > 0 || prefix != null)
            Page.Children.Add(new TextBlock
            {
                Text = prefix != null ? $"{L.T(prefix)}{(s.Heading.Length > 0 ? " › " + L.T(s.Heading) : "")}" : L.T(s.Heading),
                FontSize = 16, Foreground = (Brush)FindResource("FgA0"), Margin = new Thickness(0, Page.Children.Count == 0 ? 0 : 14, 0, 14),
            });
        foreach (var o in s.Opts) Page.Children.Add(OptView.Row(o));
    }

    void RenderSearch(string q)
    {
        PageTitle.Text = $"{L.T("Search")}: {q}";
        var any = false;
        foreach (var c in Settings.Categories)
            foreach (var s in c.Sections.Concat(c.Advanced))
            {
                var hits = s.Opts.Where(o => Match(o.Label) || Match(s.Heading)).ToList();
                if (hits.Count == 0) continue;
                any = true;
                AddSection(s with { Opts = hits }, c.Name);
            }
        if (!any) Page.Children.Add(new TextBlock { Text = L.T("Nothing matches your search."), FontSize = 14, Foreground = (Brush)FindResource("FgA3") });
    }

    /// <summary>Search finds both the English and the translated label.</summary>
    bool Match(string text) => text.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) || L.T(text).Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Render();
    void Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove(); }
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
