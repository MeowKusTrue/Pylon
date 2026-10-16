using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Pylon;

/// <summary>Dropdown entry: stores the English value, shows the translation.</summary>
public record Choice(string Value)
{
    public override string ToString() => L.T(Value);
}

/// <summary>Builds one settings row (label + control) bound to <see cref="Settings"/>. Shared by the settings window and the effects panel.</summary>
public static class OptView
{
    static Brush R(string key) => (Brush)Application.Current.FindResource(key);
    static Style St(string key) => (Style)Application.Current.FindResource(key);
    static string Fmt(double v, double step) => v.ToString(step < 0.1 ? "0.00" : step < 1 ? "0.0#" : "0", CultureInfo.InvariantCulture);

    /// <summary>stacked = label above a full-width control (narrow panels).</summary>
    public static FrameworkElement Row(Opt o, double controlWidth = 320, bool stacked = false)
    {
        if (o.Kind == OptKind.Custom)
        {
            var block = o.Build!();
            block.Margin = new Thickness(0, 0, 0, 14);
            block.Tag = o; // search
            return block;
        }
        var control = Control(o, stacked ? double.NaN : controlWidth);
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = L.T(o.Label), FontSize = 14, Foreground = R("FgA2"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (o.Info != null)
            label.Children.Add(new Icon { Glyph = "info", Width = 16, Height = 16, Tint = "@FgA3", Margin = new Thickness(8, 0, 0, 0), ToolTip = L.T(o.Info), VerticalAlignment = VerticalAlignment.Center });

        FrameworkElement row;
        if (stacked && o.Kind is not (OptKind.Toggle or OptKind.Action))
        {
            row = new StackPanel { Children = { label, new Border { Height = 8 }, control } };
        }
        else
        {
            var g = new Grid { MinHeight = 34 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            control.HorizontalAlignment = HorizontalAlignment.Right;
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            label.Margin = new Thickness(0, 0, 16, 0);
            g.Children.Add(label);
            g.Children.Add(control);
            row = g;
        }
        row.Margin = new Thickness(0, 0, 0, 10);
        row.Tag = o; // search
        if (o.Unavailable != null)
        {
            row.IsEnabled = false;
            row.Opacity = 0.45;
            ToolTipService.SetShowOnDisabled(row, true);
            row.ToolTip = L.T(o.Unavailable);
        }
        return row;
    }

    /// <summary>Re-reads the value when it changes elsewhere (presets, reset, hotkeys) while the control is on screen.</summary>
    static void Bind(FrameworkElement el, string key, Action refresh)
    {
        Action<string> handler = k => { if (k == key || k == "*") refresh(); };
        el.Loaded += (_, _) => { Settings.Changed += handler; refresh(); };
        el.Unloaded += (_, _) => Settings.Changed -= handler;
    }

    static FrameworkElement Control(Opt o, double width)
    {
        switch (o.Kind)
        {
            case OptKind.Toggle:
            {
                // the value from the start: set only on Loaded it raised Checked after the knob was placed at "off", and every
                // switch that was on slid across whenever the page was drawn again (developer mode, theme, presets)
                var cb = new CheckBox { Style = St("ToggleSwitch"), IsChecked = Settings.Bool(o.Key) };
                Bind(cb, o.Key, () => cb.IsChecked = Settings.Bool(o.Key));
                cb.Click += (_, _) => Settings.Set(o.Key, cb.IsChecked == true);
                return cb;
            }
            case OptKind.Choice:
            {
                var combo = new ComboBox { Style = St("Dropdown"), Width = double.IsNaN(width) ? double.NaN : width };
                var updating = false;
                Bind(combo, o.Key, () =>
                {
                    updating = true;
                    var items = o.Choices.ToList();
                    if (Settings.Str(o.Key) is { Length: > 0 } cur && !items.Contains(cur)) items.Insert(0, cur);
                    var choices = items.Select(v => new Choice(v)).ToList();
                    combo.ItemsSource = choices;
                    var selected = Settings.Str(o.Key) is { Length: > 0 } sv ? sv : items.FirstOrDefault();
                    combo.SelectedItem = choices.FirstOrDefault(c => c.Value == selected);
                    updating = false;
                });
                combo.SelectionChanged += (_, _) => { if (!updating && combo.SelectedItem is Choice c) Settings.Set(o.Key, c.Value); };
                return combo;
            }
            case OptKind.Segmented:
            {
                var grid = new UniformGrid { Rows = 1, MinWidth = double.IsNaN(width) ? 0 : width }; // grows for long labels ("Размытая обложка") instead of cutting them
                var group = "seg-" + o.Key + Guid.NewGuid();
                foreach (var option in o.Options)
                {
                    var rb = new RadioButton { Content = L.T(option), Tag = option, Style = St("Segment"), GroupName = group };
                    rb.Checked += (_, _) => Settings.Set(o.Key, option);
                    grid.Children.Add(rb);
                }
                Bind(grid, o.Key, () => { foreach (RadioButton rb in grid.Children) rb.IsChecked = (string)rb.Tag == Settings.Str(o.Key); });
                return new Border { Background = R("BgB2"), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = grid };
            }
            case OptKind.Slider:
            {
                var value = new TextBlock { Width = 64, FontSize = 13, Foreground = R("FgA2"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                var slider = new Slider { Style = St("SettingsSlider"), Minimum = o.Min, Maximum = o.Max, SmallChange = o.Step, LargeChange = o.Step * 5, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = o.Step };
                var updating = false;
                Bind(slider, o.Key, () =>
                {
                    updating = true;
                    slider.Value = Settings.Num(o.Key);
                    value.Text = Fmt(slider.Value, o.Step) + L.T(o.Unit);
                    updating = false;
                });
                slider.ValueChanged += (_, e) =>
                {
                    value.Text = Fmt(e.NewValue, o.Step) + L.T(o.Unit);
                    if (!updating) Settings.Set(o.Key, Math.Round(e.NewValue / o.Step) * o.Step);
                };
                slider.MouseDoubleClick += (_, _) => Settings.Set(o.Key, o.Default); // double click = default
                var dock = new DockPanel { Width = double.IsNaN(width) ? double.NaN : width };
                DockPanel.SetDock(value, Dock.Right);
                dock.Children.Add(value);
                dock.Children.Add(slider);
                return dock;
            }
            case OptKind.Number:
            {
                var box = new TextBox { Style = St("Input"), Width = 110, Height = 36, Padding = new Thickness(12, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
                void Commit()
                {
                    if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        Settings.Set(o.Key, Math.Clamp(d, o.Min, o.Max));
                    box.Text = Fmt(Settings.Num(o.Key), o.Step);
                }
                Bind(box, o.Key, () => { if (!box.IsKeyboardFocused) box.Text = Fmt(Settings.Num(o.Key), o.Step); });
                box.LostKeyboardFocus += (_, _) => Commit();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
                box.MouseWheel += (_, e) => { Settings.Set(o.Key, Math.Clamp(Settings.Num(o.Key) + Math.Sign(e.Delta) * o.Step, o.Min, o.Max)); e.Handled = true; };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(box);
                if (o.Unit.Length > 0) sp.Children.Add(new TextBlock { Text = L.T(o.Unit).Trim(), Foreground = R("FgA3"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Width = 66 });
                return sp;
            }
            case OptKind.Text:
            {
                var box = new TextBox { Style = St("Input"), Width = double.IsNaN(width) ? double.NaN : width, Height = 36, Padding = new Thickness(12, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center, Tag = L.T(o.ButtonText) };
                Bind(box, o.Key, () => { if (!box.IsKeyboardFocused) box.Text = Settings.Str(o.Key); });
                box.LostKeyboardFocus += (_, _) => Settings.Set(o.Key, box.Text.Trim());
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Settings.Set(o.Key, box.Text.Trim()); };
                return box;
            }
            case OptKind.Password:
            {
                var box = new PasswordBox { Width = double.IsNaN(width) ? double.NaN : width, Height = 36, Background = R("BgB2"), Foreground = R("FgA1"), BorderBrush = R("BgA4"), BorderThickness = new Thickness(2), Padding = new Thickness(10, 7, 10, 7), CaretBrush = R("FgA1") };
                box.Password = Settings.Str(o.Key);
                box.LostKeyboardFocus += (_, _) => Settings.Set(o.Key, box.Password);
                return box;
            }
            case OptKind.Folder or OptKind.File:
            {
                var box = new TextBox { Style = St("Input"), Height = 36, Padding = new Thickness(12, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center };
                var browse = new Button { Content = L.T("Browse"), Style = St("SecondaryButton"), MinWidth = 90, Height = 36, Margin = new Thickness(10, 0, 0, 0), Background = R("BgRaised") };
                Bind(box, o.Key, () => { if (!box.IsKeyboardFocused) box.Text = Settings.Str(o.Key); });
                box.LostKeyboardFocus += (_, _) => Settings.Set(o.Key, box.Text.Trim());
                browse.Click += (_, _) =>
                {
                    var owner = Window.GetWindow(browse);
                    if (o.Kind == OptKind.Folder)
                    {
                        var dlg = new OpenFolderDialog { InitialDirectory = Settings.Str(o.Key) };
                        if (dlg.ShowDialog(owner) == true) Settings.Set(o.Key, dlg.FolderName);
                    }
                    else
                    {
                        var dlg = new SaveFileDialog { FileName = Settings.Str(o.Key), OverwritePrompt = false, Filter = "All files|*.*" };
                        if (dlg.ShowDialog(owner) == true) Settings.Set(o.Key, dlg.FileName);
                    }
                };
                var dock = new DockPanel { Width = double.IsNaN(width) ? double.NaN : width };
                DockPanel.SetDock(browse, Dock.Right);
                dock.Children.Add(browse);
                dock.Children.Add(box);
                return dock;
            }
            case OptKind.Color:
            {
                var swatch = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), BorderBrush = R("DividerBright"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
                var box = new TextBox { Style = St("Input"), Width = 110, Height = 36, Padding = new Thickness(12, 0, 12, 0), VerticalContentAlignment = VerticalAlignment.Center };
                void Paint() { try { swatch.Background = (Brush)new BrushConverter().ConvertFromString(Settings.Str(o.Key))!; } catch (FormatException) { } }
                Bind(box, o.Key, () => { if (!box.IsKeyboardFocused) box.Text = Settings.Str(o.Key); Paint(); });
                void Commit()
                {
                    var t = box.Text.Trim();
                    if (!t.StartsWith('#')) t = "#" + t;
                    if (t.Length == 7 && int.TryParse(t[1..], NumberStyles.HexNumber, null, out _)) Settings.Set(o.Key, t.ToUpperInvariant());
                    box.Text = Settings.Str(o.Key);
                }
                box.LostKeyboardFocus += (_, _) => Commit();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
                return new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, box } };
            }
            case OptKind.Accent:
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var hex in Settings.AccentColors)
                {
                    var b = new Border
                    {
                        Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(2), Padding = new Thickness(4),
                        Child = new Border { CornerRadius = new CornerRadius(3), Background = (Brush)new BrushConverter().ConvertFromString(hex)! }, Tag = hex,
                    };
                    b.MouseLeftButtonUp += (_, _) => Settings.Set(o.Key, hex);
                    row.Children.Add(b);
                }
                Bind(row, o.Key, () =>
                {
                    foreach (Border b in row.Children)
                    {
                        var sel = string.Equals((string)b.Tag, Settings.Str(o.Key), StringComparison.OrdinalIgnoreCase);
                        b.BorderBrush = sel ? R("FgA0") : Brushes.Transparent; // not the accent: a grey accent would hide its own swatch
                        b.Background = sel ? R("AccentBright") : Brushes.Transparent;
                    }
                });
                return new Border { Background = R("BgB2"), CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = row };
            }
            case OptKind.Action:
            {
                var btn = new Button { Content = L.T(o.ButtonText), Style = St("SecondaryButton"), MinWidth = 100, Height = 32, Background = R("BgRaised") };
                btn.Click += (_, _) => o.Run?.Invoke(Window.GetWindow(btn));
                return btn;
            }
        }
        return new Border();
    }
}
