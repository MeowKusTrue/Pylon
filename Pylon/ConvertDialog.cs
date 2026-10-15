using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Pylon;

public partial class DialogWindow
{
    Func<Task>? _work;

    /// <summary>
    /// Convert / Save: a list of files with a progress bar each, a format and a destination. With a clip range it saves
    /// that part of one file (A-B loop). Conversions run one after another; Cancel stops the current one.
    /// </summary>
    public static void Convert(Window owner, IList<string> files, long clipFrom = -1, long clipTo = -1)
    {
        var clip = clipFrom >= 0 && clipTo > clipFrom;
        var items = new List<(string Path, ScaleTransform Bar, TextBlock State)>();
        var list = new StackPanel();
        void AddFile(string path)
        {
            if (items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            var bar = new ScaleTransform(0, 1);
            var state = new TextBlock { FontSize = 12, Foreground = R("FgA3"), HorizontalAlignment = HorizontalAlignment.Right };
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.RowDefinitions.Add(new RowDefinition());
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            row.Children.Add(new TextBlock { Text = Library.DisplayName(path), FontSize = 14, Foreground = R("FgA1"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 90, 6) });
            row.Children.Add(state);
            var track = new Border { Background = R("BgB2"), CornerRadius = new CornerRadius(2) };
            var fill = new Border { Background = R("Accent"), CornerRadius = new CornerRadius(2), RenderTransform = bar, RenderTransformOrigin = new Point(0, 0.5) };
            Grid.SetRow(track, 1);
            Grid.SetRow(fill, 1);
            row.Children.Add(track);
            row.Children.Add(fill);
            list.Children.Add(row);
            items.Add((path, bar, state));
        }
        foreach (var f in files) AddFile(f);

        var presets = new ComboBox { Style = (Style)Application.Current.FindResource("Dropdown"), Height = 41 };
        foreach (var p in Converter.Presets) presets.Items.Add(new ComboBoxItem { Content = L.T(p.Name), Tag = p });
        var anyVideo = files.Any(f => !MainWindow.AudioExt.Contains(Path.GetExtension(f)));
        presets.SelectedIndex = Array.FindIndex(Converter.Presets, p => clip ? p.Extension.Length == 0 : p.Video == anyVideo);
        if (presets.SelectedIndex < 0) presets.SelectedIndex = 0;

        string? folder = null;
        var where = new TextBlock { Text = L.T("Next to the original files"), FontSize = 13, Foreground = R("FgA3"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var change = new Button { Content = L.T("Change folder"), Style = (Style)Application.Current.FindResource("SecondaryButton"), Margin = new Thickness(12, 0, 0, 0) };
        var whereRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(change, Dock.Right);
        whereRow.Children.Add(change);
        whereRow.Children.Add(where);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var add = new Button { Content = L.T("Add files"), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        if (!clip) tools.Children.Add(add);
        // several files can become one (ffmpeg): copied as they are, or re-encoded to MP4
        var join = new CheckBox { Style = (Style)Application.Current.FindResource("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 10, 0) };
        var joinLabel = new TextBlock { Text = L.T("Join into one file"), FontSize = 14, Foreground = R("FgA1"), VerticalAlignment = VerticalAlignment.Center };
        if (!clip) { tools.Children.Add(join); tools.Children.Add(joinLabel); }

        var body = Stack(
            Section(clip ? L.T("Save clip") : L.T("Convert"),
                clip ? L.F("From {0} to {1}. \"Copy streams\" is instant and keeps the quality; GIF makes a looping animation.", Time(clipFrom), Time(clipTo))
                     : L.T("Files are converted one after another. MP3, M4A, FLAC, Opus and WAV keep only the sound.")),
            new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            tools, presets, whereRow, Spacer(8));

        var dlg = new DialogWindow(owner, clip ? "Save clip" : "Convert", body) { Width = 640, AllowDrop = !clip };
        dlg.SaveBtn.Content = L.T(clip ? "Save" : "Convert");
        add.Click += (_, _) =>
        {
            var pick = new OpenFileDialog { Multiselect = true, Filter = MainWindow.MediaFilter };
            if (pick.ShowDialog(dlg) == true) foreach (var f in pick.FileNames) AddFile(f);
            dlg.Revalidate();
        };
        dlg.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) foreach (var f in dropped.Where(File.Exists)) AddFile(f); dlg.Revalidate(); };
        change.Click += (_, _) =>
        {
            var pick = new OpenFolderDialog();
            if (pick.ShowDialog(dlg) != true) return;
            folder = pick.FolderName;
            where.Text = folder;
        };

        var cancel = new CancellationTokenSource();
        var outputs = new List<string>();
        var finished = false;
        dlg.CancelBtn.Click += (_, _) => cancel.Cancel();
        dlg._work = async () =>
        {
            if (finished) // second press: "Show in folder"
            {
                if (outputs.Count > 0) Process.Start("explorer.exe", $"/select,\"{outputs[0]}\"");
                dlg.DialogResult = true;
                return;
            }
            var preset = (ConvertPreset)((ComboBoxItem)presets.SelectedItem).Tag;
            if (join.IsChecked == true && items.Count > 1)
            {
                if (!await JoinFiles(dlg, items, preset, folder, outputs, cancel.Token)) return;
                finished = true;
                dlg.SaveBtn.IsEnabled = outputs.Count > 0;
                dlg.SaveBtn.Content = L.T("Show in folder");
                dlg.CancelBtn.Content = L.T("Close");
                return;
            }
            presets.IsEnabled = change.IsEnabled = add.IsEnabled = false;
            dlg.SaveBtn.IsEnabled = false;
            dlg.SaveBtn.Content = L.T("Converting…");
            foreach (var (path, bar, state) in items)
            {
                if (cancel.IsCancellationRequested) break;
                var dst = Converter.OutputPath(path, preset, folder, clip ? $" ({Time(clipFrom).Replace(':', '.')}-{Time(clipTo).Replace(':', '.')})" : "");
                state.Text = "…";
                var progress = new Progress<double>(p => bar.ScaleX = p);
                var ok = await Converter.RunAsync(path, dst, preset, clipFrom, clipTo, progress, cancel.Token).ContinueWith(t => t.Status == TaskStatus.RanToCompletion && t.Result);
                bar.ScaleX = ok ? 1 : 0;
                state.Text = ok ? "✓" : L.T(cancel.IsCancellationRequested ? "Cancelled" : "Failed");
                state.Foreground = ok ? R("Accent") : R("FgA3");
                if (ok) outputs.Add(dst);
            }
            finished = true;
            dlg.SaveBtn.IsEnabled = outputs.Count > 0;
            dlg.SaveBtn.Content = L.T("Show in folder");
            dlg.CancelBtn.Content = L.T("Close");
        };
        dlg.Run(() => items.Count > 0);
        cancel.Cancel(); // closing the window stops a running conversion
    }

    /// <summary>
    /// "Join into one file": ffmpeg (fetched on first use) concatenates in list order. "Copy streams" keeps them as they
    /// are; any other format joins into an H.264 MP4.
    /// </summary>
    static async Task<bool> JoinFiles(DialogWindow dlg, List<(string Path, ScaleTransform Bar, TextBlock State)> items, ConvertPreset preset,
        string? folder, List<string> outputs, CancellationToken cancel)
    {
        var status = items[0].State;
        if (!Ffmpeg.Available)
        {
            if (!OfferTool(dlg, "ffmpeg", "Join files with ffmpeg",
                    "Joining needs ffmpeg, a free open-source tool. Pylon can download it now (about 200 MB from github.com/BtbN/FFmpeg-Builds); it's only used for joining.")) return false;
            dlg.SaveBtn.IsEnabled = false;
            var ok = await Ffmpeg.InstallAsync(new Progress<double>(p => { status.Text = L.F("ffmpeg {0}%", (int)(p * 100)); items[0].Bar.ScaleX = p; }));
            items[0].Bar.ScaleX = 0;
            if (!ok) { status.Text = L.T("Couldn't download ffmpeg"); dlg.SaveBtn.IsEnabled = true; return false; }
        }
        var copy = preset.Extension.Length == 0;
        var first = items[0].Path;
        var dst = Converter.OutputPath(first, copy ? preset : preset with { Extension = ".mp4" }, folder, " " + L.T("(joined)"));
        dlg.SaveBtn.IsEnabled = false;
        dlg.SaveBtn.Content = L.T("Joining…");
        foreach (var i in items) i.State.Text = "…";
        var (done, error) = await Ffmpeg.JoinAsync([.. items.Select(i => i.Path)], dst, copy,
            new Progress<double>(p => { foreach (var i in items) i.Bar.ScaleX = p; }), cancel);
        foreach (var i in items)
        {
            i.Bar.ScaleX = done ? 1 : 0;
            i.State.Text = done ? "✓" : L.T(cancel.IsCancellationRequested ? "Cancelled" : "Failed");
            i.State.Foreground = done ? R("Accent") : R("FgA3");
        }
        if (done) outputs.Add(dst);
        else if (error != null && copy) status.Text = L.T("Failed: files differ, choose MP4"); // copying needs files of one kind
        else if (error != null) status.ToolTip = error;
        return true;
    }

    static string Time(long ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
}
