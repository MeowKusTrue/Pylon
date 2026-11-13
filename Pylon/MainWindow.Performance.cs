using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Pylon;

public static class PerformanceProbe
{
    public static bool Enabled => Environment.GetEnvironmentVariable("PYLON_BENCHMARK") != null;
}

public partial class MainWindow
{
    async void RunPerformanceProbe()
    {
        if (Environment.GetEnvironmentVariable("PYLON_BENCHMARK") is not { } output) return;
        if (Environment.GetEnvironmentVariable("PYLON_DATA") == null) throw new InvalidOperationException("Performance probes require an isolated profile.");
        var result = new Dictionary<string, object> { ["version"] = Updater.Current.ToString(), ["runtime"] = Environment.Version.ToString(), ["cpuCount"] = Environment.ProcessorCount };
        try
        {
            for (var n = 0; n < 100 && _openingMedia; n++) await Task.Delay(50);
            Perf.Mark("benchmark ready");
            var source = CurrentPath;
            var process = Process.GetCurrentProcess();
            async Task<double> CpuSample()
            {
                var before = process.TotalProcessorTime.TotalMilliseconds;
                var timer = Stopwatch.StartNew(); await Task.Delay(1000); process.Refresh();
                return (process.TotalProcessorTime.TotalMilliseconds - before) / timer.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount;
            }
            result["playingCpuPercent"] = await CpuSample();
            _mp.SetPause(true); await Task.Delay(200);
            result["pausedCpuPercent"] = await CpuSample();
            Hide(); result["hiddenCpuPercent"] = await CpuSample(); Show();
            foreach (var size in new[] { 1000, 10000 })
            {
                _queue.Clear(); _queue.AddRange(Enumerable.Range(0, size).Select(i => Path.Combine(Library.Dir, $"bench-{i}.wav")));
                var before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                if (Sidebar.Visibility != Visibility.Visible) ToggleSidebar(LibraryPanel.Section.Queue); else Sidebar.Show(LibraryPanel.Section.Queue);
                Sidebar.UpdateLayout();
                result[$"queue{size}"] = new { milliseconds = timer.Elapsed.TotalMilliseconds, bytes = GC.GetAllocatedBytesForCurrentThread() - before };
                timer.Restart();
                ((System.Windows.Controls.TextBox)Sidebar.FindName("SearchBox")).Text = "bench-99";
                result[$"search{size}Ms"] = timer.Elapsed.TotalMilliseconds;
                ((System.Windows.Controls.TextBox)Sidebar.FindName("SearchBox")).Clear();
            }
            if (source != null)
            {
                var timer = Stopwatch.StartNew();
                for (var n = 0; n < 10; n++)
                {
                    PlayList([source], 0);
                    for (var wait = 0; wait < 100 && _openingMedia; wait++) await Task.Delay(20);
                    if (_openingMedia) throw new TimeoutException("Playback did not start during switching benchmark.");
                }
                result["tenAudioSwitchesMs"] = timer.Elapsed.TotalMilliseconds;
            }
            process.Refresh(); result["workingSetBytes"] = process.WorkingSet64; result["managedBytes"] = GC.GetTotalMemory(false);
            result["passed"] = true;
        }
        catch (Exception e) { result["passed"] = false; result["error"] = e.GetType().Name + ": " + e.Message; }
        finally
        {
            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            _quitting = true; Application.Current.Shutdown();
        }
    }
}
