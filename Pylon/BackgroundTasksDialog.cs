using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Pylon;

public partial class DialogWindow
{
    public static void BackgroundTasks(Window owner)
    {
        var list = new StackPanel();
        var dlg = new DialogWindow(owner, "Background tasks", new ScrollViewer { Content = list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }) { Width = 740 };
        dlg.CancelBtn.Content = L.T("Close");
        dlg.SaveBtn.Visibility = Visibility.Collapsed;
        var rows = new Dictionary<BackgroundJob, (TextBlock Detail, ProgressBar Bar, Button Cancel, Button Retry, Button Open)>();
        void Render()
        {
            var jobs = BackgroundJobs.All.ToArray();
            if (rows.Keys.Except(jobs).Any()) { rows.Clear(); list.Children.Clear(); }
            if (jobs.Length == 0 && list.Children.Count == 0) list.Children.Add(Section("No background tasks", "Downloads and conversions will appear here."));
            if (jobs.Length > 0 && rows.Count == 0) list.Children.Clear();
            foreach (var job in jobs.Reverse())
            {
                if (!rows.TryGetValue(job, out var row))
                {
                    var title = new TextBlock { Text = job.Name, Foreground = R("FgA1"), FontSize = 14, TextWrapping = TextWrapping.Wrap };
                    var detail = new TextBlock { Foreground = R("FgA3"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
                    var bar = new ProgressBar { Minimum = 0, Maximum = 1, Style = (Style)Application.Current.FindResource("ThinProgressBar") };
                    Button Action(string label, Action run)
                    {
                        var b = new Button { Content = L.T(label), Style = (Style)Application.Current.FindResource("SecondaryButton"), Margin = new Thickness(0, 8, 8, 0) };
                        b.Click += (_, _) => run();
                        return b;
                    }
                    var cancel = Action("Cancel", job.Cancel);
                    var retry = Action("Retry", job.Retry);
                    var open = Action("Show in folder", () =>
                    {
                        if (job.Output is not { } path) return;
                        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        else if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                    });
                    list.Children.Add(new Border { Margin = new Thickness(0, 0, 0, 16), Child = Stack(title, detail, bar,
                        new StackPanel { Orientation = Orientation.Horizontal, Children = { cancel, retry, open } }) });
                    rows[job] = row = (detail, bar, cancel, retry, open);
                }
                row.Detail.Text = L.T(job.State.ToString()) + (job.Detail.Length > 0 ? " — " + L.T(job.Detail) : "");
                row.Bar.Value = job.Progress;
                row.Cancel.IsEnabled = job.State == JobState.Running;
                row.Retry.IsEnabled = job.State is JobState.Failed or JobState.Cancelled;
                row.Open.IsEnabled = job.Output != null;
            }
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => { timer.Stop(); Render(); };
        void Changed() => dlg.Dispatcher.BeginInvoke(() => { if (dlg.IsLoaded && !timer.IsEnabled) timer.Start(); });
        dlg.Loaded += (_, _) => { BackgroundJobs.Changed += Changed; Render(); };
        dlg.Closed += (_, _) => { BackgroundJobs.Changed -= Changed; timer.Stop(); };
        dlg.Run(() => false);
    }
}
