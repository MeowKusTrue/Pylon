using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pylon;

public partial class DialogWindow
{
    public static void CommandPalette(MainWindow owner)
    {
        var search = Input("Search actions or settings");
        var list = new ListBox { Height = 350, Background = R("BgB2"), Foreground = R("FgA1"), BorderThickness = new Thickness(0) };
        var actions = Hotkeys.Actions.Where(a => a.Id != "commands").Select(a => (Label: L.T(a.Name), Detail: Hotkeys.Gesture(a.Id), Run: (Action)(() => a.Run(owner))))
            .Concat(Settings.Categories.SelectMany(c => c.Sections.Concat(c.Advanced).SelectMany(s => s.Opts)
                .Where(o => o.Unavailable == null && o.Label.Length > 0).Select(o => (Label: L.T(o.Label), Detail: L.T(c.Name),
                    Run: (Action)(() => owner.FindSetting(c.Name, L.T(o.Label))))))).ToArray();
        var dialog = new DialogWindow(owner, "Search actions", Stack(search, list)) { Width = 720 };
        dialog.SaveBtn.Content = L.T("Open");
        void Filter()
        {
            var terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list.ItemsSource = actions.Where(a => terms.All(t => (a.Label + " " + a.Detail).Contains(t, StringComparison.CurrentCultureIgnoreCase)))
                .Select(a => new ListBoxItem { Content = a.Label + "    " + a.Detail, Tag = a.Run, Padding = new Thickness(10, 8, 10, 8) }).ToArray();
            list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
        }
        Action? selected = null;
        void Select() { if (list.SelectedItem is ListBoxItem { Tag: Action run }) { selected = run; dialog.DialogResult = true; } }
        search.TextChanged += (_, _) => Filter();
        search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Up)
            { list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Math.Max(0, list.Items.Count - 1)); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
            if (e.Key == Key.Enter) { Select(); e.Handled = true; }
        };
        list.MouseDoubleClick += (_, _) => Select();
        list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Select(); e.Handled = true; } };
        dialog._work = () => { Select(); return Task.CompletedTask; };
        Filter(); dialog.Run(() => list.SelectedIndex >= 0, search);
        selected?.Invoke();
    }
}

public partial class MainWindow
{
    public void ShowCommandPalette() => DialogWindow.CommandPalette(this);
    public void FindSetting(string category, string query) { OpenSettings(category); _settings?.Search(query); }
}
