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
    /// Convert / Save: a list of files with a progress bar each, a format and a destination. One file can be trimmed; with
    /// a clip range (A-B loop) that starts out set. Conversions run one after another; Cancel stops the current one.
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
            ShowTrim();
        }
        // trimming: one file only, the range in the text boxes and on the bar
        var trim = new CheckBox { Style = (Style)Application.Current.FindResource("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), IsChecked = clip };
        var trimLabel = new TextBlock { Text = L.T("Trim"), FontSize = 14, Foreground = R("FgA1"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        var fromBox = Input("0:00", clip ? Converter.FormatTime(clipFrom) : "");
        var toBox = Input(L.T("end"), clip ? Converter.FormatTime(clipTo) : "");
        fromBox.Width = toBox.Width = 110;
        var dash = new TextBlock { Text = "–", FontSize = 14, Foreground = R("FgA3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
        var trimFields = new StackPanel { Orientation = Orientation.Horizontal };
        trimFields.Children.Add(fromBox);
        trimFields.Children.Add(dash);
        trimFields.Children.Add(toBox);
        var trimRow = new StackPanel { Orientation = Orientation.Horizontal };
        trimRow.Children.Add(trim);
        trimRow.Children.Add(trimLabel);
        trimRow.Children.Add(trimFields);
        var range = new RangeBar { Margin = new Thickness(0, 10, 0, 0) };
        var trimPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        trimPanel.Children.Add(trimRow);
        trimPanel.Children.Add(range);
        long length = 0;
        var syncing = false;
        void ShowTrim()
        {
            trimPanel.Visibility = items.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
            trimFields.Visibility = trim.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            range.Visibility = trim.IsChecked == true && length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        (long From, long To)? Trimmed()
        {
            if (trim.IsChecked != true || items.Count != 1) return (-1, -1);
            var from = fromBox.Text.Trim().Length == 0 ? 0 : Converter.ParseTime(fromBox.Text);
            var to = toBox.Text.Trim().Length == 0 ? (length > 0 ? length : -1) : Converter.ParseTime(toBox.Text);
            if (from < 0 || (to >= 0 && to <= from) || (length > 0 && (from >= length || to > length + 500))) return null;
            return (from, to);
        }
        void BarFromText()
        {
            if (length <= 0 || syncing || Trimmed() is not { } t || t.From < 0) return;
            syncing = true;
            range.Set((double)t.From / length, t.To > 0 ? (double)t.To / length : 1);
            syncing = false;
        }
        range.Changed += () =>
        {
            if (syncing || length <= 0) return;
            syncing = true;
            fromBox.Text = Converter.FormatTime((long)(range.Lower * length));
            toBox.Text = Converter.FormatTime((long)(range.Upper * length));
            syncing = false;
        };
        fromBox.TextChanged += (_, _) => BarFromText();
        toBox.TextChanged += (_, _) => BarFromText();
        foreach (var f in files) AddFile(f);
        if (items.Count == 1)
            _ = Task.Run(() => Ffmpeg.Duration(items[0].Path)).ContinueWith(t =>
            {
                length = t.Status == TaskStatus.RanToCompletion ? t.Result : 0;
                BarFromText();
                ShowTrim();
            }, TaskScheduler.FromCurrentSynchronizationContext());

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
        var chooseFfmpeg = new Button { Content = L.T("Choose ffmpeg…"), Style = (Style)Application.Current.FindResource("SecondaryButton"), Margin = new Thickness(12, 0, 0, 0), ToolTip = Settings.Str("tools.ffmpeg") };
        chooseFfmpeg.Click += (_, _) =>
        {
            var pick = new OpenFileDialog { Filter = "ffmpeg (ffmpeg.exe)|ffmpeg.exe", Title = L.T("Choose ffmpeg…") };
            if (pick.ShowDialog(Window.GetWindow(chooseFfmpeg)) != true) return;
            Settings.Set("tools.ffmpeg", pick.FileName);
            chooseFfmpeg.ToolTip = pick.FileName;
        };
        var add = new Button { Content = L.T("Add files"), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        if (!clip) tools.Children.Add(add);
        // several files can become one (ffmpeg): copied as they are, or re-encoded to MP4
        var join = new CheckBox { Style = (Style)Application.Current.FindResource("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 10, 0) };
        var joinLabel = new TextBlock { Text = L.T("Join into one file"), FontSize = 14, Foreground = R("FgA1"), VerticalAlignment = VerticalAlignment.Center };
        if (!clip) { tools.Children.Add(join); tools.Children.Add(joinLabel); }
        tools.Children.Add(chooseFfmpeg);

        var body = Stack(
            Section(clip ? L.T("Save clip") : L.T("Convert"),
                clip ? L.T("\"Copy streams\" is instant and keeps the quality, but starts at the nearest keyframe before the start; the other formats are exact. GIF makes a looping animation.")
                     : L.T("Files are converted one after another. MP3, M4A, FLAC, Opus and WAV keep only the sound.")),
            new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            trimPanel, tools, presets, whereRow, Spacer(8));

        var dlg = new DialogWindow(owner, clip ? "Save clip" : "Convert", body) { Width = 640, AllowDrop = !clip };
        dlg.SaveBtn.Content = L.T(clip ? "Save" : "Convert");
        trim.Checked += (_, _) => { ShowTrim(); dlg.Revalidate(); };
        trim.Unchecked += (_, _) => { ShowTrim(); dlg.Revalidate(); };
        ShowTrim();
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

        BackgroundJob? runningJob = null;
        var succeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stopWork = new Button { Content = L.T("Cancel task"), Style = (Style)Application.Current.FindResource("SecondaryButton"), Visibility = Visibility.Collapsed };
        tools.Children.Add(stopWork);
        stopWork.Click += (_, _) => runningJob?.Cancel();
        var outputs = new List<string>();
        var finished = false;
        dlg._work = async () =>
        {
            if (finished) // second press: "Show in folder"
            {
                if (outputs.Count > 0) Process.Start("explorer.exe", $"/select,\"{outputs[0]}\"");
                dlg.DialogResult = true;
                return;
            }
            var preset = (ConvertPreset)((ComboBoxItem)presets.SelectedItem).Tag;
            if (!await EnsureFfmpeg(dlg, items[0].State, items[0].Bar)) return;
            stopWork.Visibility = Visibility.Visible;
            stopWork.IsEnabled = true;
            dlg.CancelBtn.Content = L.T("Run in background");
            if (runningJob == null)
            {
                runningJob = BackgroundJobs.Start(L.T(join.IsChecked == true ? "Join into one file" : "Convert"), async (job, cancel) =>
                {
                    var failures = 0;
                    if (join.IsChecked == true && items.Count > 1)
                    {
                        presets.IsEnabled = change.IsEnabled = add.IsEnabled = chooseFfmpeg.IsEnabled = join.IsEnabled = trim.IsEnabled = false;
                        if (!await JoinFiles(dlg, items, preset, folder, outputs, job, cancel))
                        { cancel.ThrowIfCancellationRequested(); throw new IOException(items[0].State.ToolTip?.ToString() ?? items[0].State.Text); }
                        finished = true;
                        dlg.SaveBtn.IsEnabled = outputs.Count > 0;
                        dlg.SaveBtn.Content = L.T("Show in folder");
                        dlg.CancelBtn.Content = L.T("Close");
                        return outputs.FirstOrDefault();
                    }
                    var (cutFrom, cutTo) = Trimmed() ?? (-1, -1);
                    presets.IsEnabled = change.IsEnabled = add.IsEnabled = chooseFfmpeg.IsEnabled = trim.IsEnabled = trimFields.IsEnabled = range.IsEnabled = false;
                    dlg.SaveBtn.IsEnabled = false;
                    dlg.SaveBtn.Content = L.T("Converting…");
                    foreach (var (path, bar, state) in items)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (succeeded.Contains(path)) continue;
                        var dst = Converter.OutputPath(path, preset, folder, cutFrom >= 0 ? $" ({Time(cutFrom).Replace(':', '.')}-{Time(cutTo > 0 ? cutTo : length).Replace(':', '.')})" : "");
                        state.Text = "…";
                        var progress = new Progress<double>(p => { bar.ScaleX = p; job.Report((succeeded.Count + p) / items.Count, Path.GetFileName(path)); });
                        var (ok, error) = await Converter.RunAsync(path, dst, preset, cutFrom, cutTo, progress, cancel);
                        bar.ScaleX = ok ? 1 : 0;
                        state.Text = ok ? "✓" : L.T(cancel.IsCancellationRequested ? "Cancelled" : error?.Contains("does not contain any stream") == true ? "No sound in this file" : "Failed");
                        state.ToolTip = ok ? null : error;
                        state.Foreground = ok ? R("Accent") : R("FgA3");
                        if (ok) { outputs.Add(dst); succeeded.Add(path); job.Output = dst; }
                        else { cancel.ThrowIfCancellationRequested(); failures++; }
                    }
                    if (failures > 0) throw new IOException(L.F("{0} files could not be converted. Retry to process the remaining files.", failures));
                    finished = true;
                    dlg.SaveBtn.IsEnabled = outputs.Count > 0;
                    dlg.SaveBtn.Content = L.T("Show in folder");
                    dlg.CancelBtn.Content = L.T("Close");
                    return outputs.FirstOrDefault();
                });
            }
            else runningJob.Retry();
            await runningJob.Completion;
            stopWork.IsEnabled = false;
            finished = runningJob.State == JobState.Completed;
            dlg.CancelBtn.Content = L.T("Close");
            if (!finished) { dlg.SaveBtn.IsEnabled = true; dlg.SaveBtn.Content = L.T("Retry"); }
        };
        dlg.Run(() => items.Count > 0 && Trimmed() != null, fromBox, toBox);
    }

    /// <summary>Asks and fetches ffmpeg the first time; false when it isn't there after that.</summary>
    static async Task<bool> EnsureFfmpeg(DialogWindow dlg, TextBlock status, ScaleTransform bar)
    {
        if (await Task.Run(() => Ffmpeg.Available)) return true;
        if (!OfferTool(dlg, "ffmpeg", "Convert with ffmpeg",
                "Converting needs ffmpeg, a free open-source tool. Pylon can download it now (about 200 MB from github.com/BtbN/FFmpeg-Builds).")) return false;
        dlg.SaveBtn.IsEnabled = false;
        var download = BackgroundJobs.Start("ffmpeg", async (job, cancel) =>
        {
            var ok = await Ffmpeg.InstallAsync(new Progress<double>(p =>
            {
                job.Report(p);
                status.Text = L.F("ffmpeg {0}%", (int)(p * 100)); bar.ScaleX = p;
            }), cancel);
            if (!ok) throw new IOException(L.T("Couldn't download ffmpeg"));
            return Ffmpeg.Exe;
        });
        EventHandler closed = (_, _) => download.Cancel();
        dlg.Closed += closed;
        await download.Completion;
        dlg.Closed -= closed;
        var ok = download.State == JobState.Completed && dlg.IsVisible;
        bar.ScaleX = 0;
        status.Text = ok ? "" : L.T("Couldn't download ffmpeg");
        dlg.SaveBtn.IsEnabled = true;
        return ok;
    }

    /// <summary>
    /// "Join into one file": ffmpeg (fetched on first use) concatenates in list order. "Copy streams" keeps them as they
    /// are; other presets encode to the selected format.
    /// </summary>
    static async Task<bool> JoinFiles(DialogWindow dlg, List<(string Path, ScaleTransform Bar, TextBlock State)> items, ConvertPreset preset,
        string? folder, List<string> outputs, BackgroundJob job, CancellationToken cancel)
    {
        var status = items[0].State;
        var copy = preset.Extension.Length == 0;
        var first = items[0].Path;
        var dst = Converter.OutputPath(first, preset, folder, " " + L.T("(joined)"));
        dlg.SaveBtn.IsEnabled = false;
        dlg.SaveBtn.Content = L.T("Joining…");
        foreach (var i in items) i.State.Text = "…";
        var (done, error) = await Ffmpeg.JoinAsync([.. items.Select(i => i.Path)], dst, preset,
            new Progress<double>(p => { foreach (var i in items) i.Bar.ScaleX = p; job.Report(p); }), cancel);
        foreach (var i in items)
        {
            i.Bar.ScaleX = done ? 1 : 0;
            i.State.Text = done ? "✓" : L.T(cancel.IsCancellationRequested ? "Cancelled" : "Failed");
            i.State.Foreground = done ? R("Accent") : R("FgA3");
        }
        if (done) outputs.Add(dst);
        else if (error != null)
        {
            status.Text = L.T(copy ? "Joining failed. Stream copying requires matching codecs and track parameters. Convert the parts to the same format first." : "Joining failed. Check the input files and the selected preset.");
            status.ToolTip = error;
        }
        return done;
    }

    static string Time(long ms) => Converter.FormatTime(ms / 1000 * 1000);

    /// <summary>The trim range: two handles on a line, 0 to 1.</summary>
    sealed class RangeBar : FrameworkElement
    {
        const double Radius = 8;
        bool _upperHeld;

        public double Lower { get; private set; }
        public double Upper { get; private set; } = 1;
        public event Action? Changed;

        public RangeBar()
        {
            Height = 22;
            Cursor = System.Windows.Input.Cursors.Hand;
        }

        public void Set(double lower, double upper)
        {
            (Lower, Upper) = (Math.Clamp(lower, 0, 1), Math.Clamp(upper, 0, 1));
            InvalidateVisual();
        }

        double At(double value) => Radius + value * Math.Max(1, ActualWidth - 2 * Radius);
        double Value(double x) => Math.Clamp((x - Radius) / Math.Max(1, ActualWidth - 2 * Radius), 0, 1);

        protected override void OnRender(DrawingContext dc)
        {
            var y = ActualHeight / 2;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // the whole strip takes the mouse
            dc.DrawRoundedRectangle(R("BgB2"), null, new Rect(Radius, y - 2, Math.Max(0, ActualWidth - 2 * Radius), 4), 2, 2);
            dc.DrawRectangle(R("Accent"), null, new Rect(At(Lower), y - 2, Math.Max(0, At(Upper) - At(Lower)), 4));
            dc.DrawEllipse(R("Accent"), null, new Point(At(Lower), y), Radius, Radius);
            dc.DrawEllipse(R("Accent"), null, new Point(At(Upper), y), Radius, Radius);
        }

        protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
        {
            var x = e.GetPosition(this).X;
            _upperHeld = Math.Abs(x - At(Upper)) < Math.Abs(x - At(Lower)) || (Lower == Upper && Lower < 0.5);
            CaptureMouse();
            Move(x);
        }

        protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
        {
            if (IsMouseCaptured) Move(e.GetPosition(this).X);
        }

        protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e) => ReleaseMouseCapture();

        void Move(double x)
        {
            var v = Value(x);
            if (_upperHeld) Upper = Math.Max(v, Lower + 0.001);
            else Lower = Math.Min(v, Upper - 0.001);
            InvalidateVisual();
            Changed?.Invoke();
        }
    }
}
