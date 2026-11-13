using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Pylon;

/// <summary>The player: the snapshot script UI tests run (debug builds only).</summary>
public partial class MainWindow
{
#if DEBUG
    bool _debugCtrlWheel;
    /// <summary>
    /// Debug-only UI test hook: PYLON_SNAP=out.png renders the window to a PNG (no input injection, no focus needed) and exits.
    /// PYLON_SNAP_DO is a ';'-separated script: library:Videos | folder:&lt;dir&gt; | row:&lt;title&gt; | menu | ctx | search:&lt;text&gt; |
    /// set:key=value | settings:Category | fx:audio|video | mini | minimal | customize | hotkeys | onboarding | about | wait:&lt;sec&gt; | dialog:playlist|stream|subs
    /// </summary>
    async void DebugSnapshot()
    {
        if (Environment.GetEnvironmentVariable("PYLON_SNAP") is not { } outFile) return;
        await Task.Delay(3000);
        Window? shot = null;
        foreach (var step in (Environment.GetEnvironmentVariable("PYLON_SNAP_DO") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var (cmd, arg) = step.IndexOf(':') is var i and > 0 ? (step[..i], step[(i + 1)..]) : (step, "");
            switch (cmd)
            {
                case "opening-ui":
                    {
                        FinishOpening();
                        UpdateLayout();
                        var before = PlayBtn.TranslatePoint(new Point(), ControlsBar);
                        var request = ++_playRequest;
                        _openingMedia = true;
                        ShowOpeningIfSlow(request);
                        var immediate = CancelOpeningBtn.Visibility == Visibility.Collapsed;
                        FinishOpening();
                        await Task.Delay(900);
                        var fast = CancelOpeningBtn.Visibility == Visibility.Collapsed;
                        request = ++_playRequest;
                        _openingMedia = true;
                        ShowOpeningIfSlow(request);
                        await Task.Delay(900);
                        UpdateLayout();
                        var slow = CancelOpeningBtn.IsVisible || StageCancelOpeningBtn.IsVisible;
                        var stable = before == PlayBtn.TranslatePoint(new Point(), ControlsBar);
                        Snap(this, Path.ChangeExtension(outFile, ".slow.png"));
                        FinishOpening();
                        request = ++_playRequest;
                        _openingMedia = true;
                        ShowOpeningIfSlow(request);
                        ++_playRequest;
                        await Task.Delay(900);
                        var stale = CancelOpeningBtn.Visibility == Visibility.Collapsed;
                        FinishOpening();
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"opening-ui passed={immediate && fast && slow && stable && stale} immediate={immediate} fast={fast} slow={slow} stable={stable} stale={stale}\n");
                        break;
                    }
                case "library": ToggleSidebar(Enum.Parse<LibraryPanel.Section>(arg)); break;
                case "folder": if (Sidebar.Visibility != Visibility.Visible) ToggleSidebar(); Sidebar.ShowFolder(arg); break;
                case "search": Sidebar.OpenSearch(); ((TextBox)Sidebar.FindName("SearchBox")).Text = arg; break;
                case "menu": MenuBtn_Click(MenuBtn, new RoutedEventArgs()); break;
                case "ctx": Stage_RightClick(Stage, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = MouseRightButtonUpEvent }); break;
                case "minimal": _minimal = true; ApplyLayout(); break;
                case "mini": ToggleMiniplayer(); break;
                case "row": Sidebar.DebugClick(arg); break;
                case "set": Settings.Set(arg[..arg.IndexOf('=')], arg[(arg.IndexOf('=') + 1)..]); break;
                case "fx": ShowEffects(arg == "video"); break;
                case "customize": ShowCustomize(); break;
                case "do": Hotkeys.Actions.First(x => x.Id == arg).Run(this); break;
                case "preview":
                    ShowSeekPreview(2 + double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) * (Seek.ActualWidth - 4));
                    await Task.Delay(1500);
                    break;
                case "next": Next(); break;
                case "safe-portable":
                    {
                        var old = Settings.Str("general.theme"); Settings.Set("general.theme", "Light"); Settings.Save();
                        var portable = StartupOptions.Portable && StartupOptions.ToolsDirectory == Path.Combine(Library.Dir, "tools");
                        var safe = StartupOptions.SafeMode && !Settings.Bool("playback.autoplay") && Settings.Bool("perf.softwareui") && !Settings.Bool("playback.ownengine") && !_mp.IsPlaying;
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"safe-portable passed={portable && safe && !ShellRegistration.CanWriteShell} portable={portable} safe={safe} shellBlocked={!ShellRegistration.CanWriteShell}\n");
                        Settings.Set("general.theme", old);
                        break;
                    }
                case "panel-beauty":
                    {
                        var pointer = Motion.DebugPointer; var effects = Motion.DebugEnabled;
                        async Task Settle(int ms)
                        {
                            var watch = Stopwatch.StartNew();
                            while (watch.ElapsedMilliseconds < ms) { await Task.Delay(8); Motion.DebugFrame(); }
                        }
                        try
                        {
                            Motion.DebugPointer = true; Motion.DebugEnabled = true;
                            Motion.Out(Sidebar, () => Sidebar.Visibility = Visibility.Collapsed, animate: false);
                            Motion.Set(SidebarHost, WidthProperty, 0); _sidebarMeasured = false;
                            Motion.Reset(Stage);
                            var widths = new List<double>(); var times = new List<double>(); var stable = true;
                            var timer = Stopwatch.StartNew(); ToggleSidebar(LibraryPanel.Section.Music);
                            var shots = 0;
                            while (timer.ElapsedMilliseconds < 750)
                            {
                                await Task.Delay(8); Motion.DebugFrame();
                                widths.Add(SidebarHost.Width); times.Add(timer.Elapsed.TotalMilliseconds);
                                stable &= Math.Abs(Stage.RenderTransform.Value.M11 - 1) < 0.0001 && Math.Abs(Stage.RenderTransform.Value.M22 - 1) < 0.0001;
                                if (shots < 3 && timer.ElapsedMilliseconds >= new[] { 60, 150, 300 }[shots])
                                { Snap(this, Path.ChangeExtension(outFile, $".panel{shots}.png")); shots++; }
                            }
                            var smooth = widths.Zip(widths.Skip(1), (a, b) => b >= a - 0.05).All(v => v)
                                && widths.Any(w => w > 5 && w < Sidebar.Width - 5);
                            var opened = Math.Abs(SidebarHost.Width - Sidebar.Width) < 1;
                            ToggleSidebar(); await Settle(80); var before = SidebarHost.Width; ToggleSidebar();
                            var reversed = Math.Abs(before - SidebarHost.Width) < 0.001;
                            await Settle(700); ToggleSidebar(); await Settle(700);
                            var closed = Sidebar.Visibility == Visibility.Collapsed && SidebarHost.Width == 0;
                            Motion.DebugPointer = false; ToggleSidebar();
                            var keyboard = SidebarHost.Width == Sidebar.Width;
                            Motion.DebugEnabled = false; ToggleSidebar();
                            var reduced = SidebarHost.Width == 0 && Sidebar.Visibility == Visibility.Collapsed;
                            File.WriteAllText(Path.ChangeExtension(outFile, ".panel.json"), System.Text.Json.JsonSerializer.Serialize(new { widths, times }));
                            File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"panel passed={stable && smooth && opened && reversed && closed && keyboard && reduced} stable={stable} smooth={smooth} firstOpened={opened} reversed={reversed} closed={closed} keyboard={keyboard} reduced={reduced} video={Video.IsVisible} decodedVideo={_mp.Media?.Statistics.DecodedVideo}\n");
                        }
                        finally { Motion.DebugPointer = pointer; Motion.DebugEnabled = effects; }
                        break;
                    }
                case "features12":
                    {
                        if (CurrentPath is not { } path) throw new InvalidOperationException("Audio fixture required.");
                        SetRate(1.25f); ShiftAudioDelay(0.15); ShiftSubtitleDelay(0.4);
                        RememberSeriesTracks(); PlayList([path], 0);
                        for (var attempt = 0; attempt < 100 && _openingMedia; attempt++) await Task.Delay(30);
                        await Task.Delay(100);
                        var remembered = Math.Abs(_mp.Rate - 1.25) < 0.001 && Math.Abs(_fileAudioDelay - 0.15) < 0.001 && Math.Abs(_fileSubtitleDelay - 0.4) < 0.001;
                        var series = Library.Data.SeriesTracks.ContainsKey(System.IO.Path.GetDirectoryName(path)!);
                        Enqueue([path, path]); RemoveFromQueue(2); UndoQueue();
                        var undone = _queue.Count == 3 && _index == 0;
                        ClearQueue(); UndoQueue();
                        for (var attempt = 0; attempt < 100 && _openingMedia; attempt++) await Task.Delay(30);
                        await Task.Delay(100);
                        var restoredPaused = _queue.Count == 3 && !_mp.IsPlaying;
                        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(Volume)!;
                        var range = (System.Windows.Automation.Provider.IRangeValueProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.RangeValue)!;
                        var old = Volume.Value; range.SetValue(25); var accessible = Math.Abs(VolumePercent - 25) <= 1; Volume.Value = old; ApplyVolume();
                        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _ = Dispatcher.BeginInvoke(async () =>
                        {
                            try
                            {
                                await Task.Delay(120);
                                var dialog = Application.Current.Windows.OfType<DialogWindow>().First();
                                var body = (StackPanel)dialog.Body.Content;
                                ((TextBox)body.Children[0]).Text = L.T("Show playback position and time");
                                await Task.Delay(50); var count = ((ListBox)body.Children[1]).Items.Count;
                                Snap(dialog, Path.ChangeExtension(outFile, ".commands.png"));
                                dialog.SaveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                completed.TrySetResult(count > 0 && !dialog.IsVisible);
                            }
                            catch (Exception e) { completed.TrySetException(e); }
                        });
                        ShowCommandPalette(); var commands = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _ = Dispatcher.BeginInvoke(async () =>
                        {
                            try
                            {
                                await Task.Delay(400); var dialog = Application.Current.Windows.OfType<DialogWindow>().First();
                                var text = ((TextBox)((StackPanel)dialog.Body.Content).Children[0]).Text;
                                Snap(dialog, Path.ChangeExtension(outFile, ".pipeline.png"));
                                dialog.CancelBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                completed.TrySetResult(text.Contains("libVLC") && text.Contains("HDR"));
                            }
                            catch (Exception e) { completed.TrySetException(e); }
                        });
                        ShowPlaybackPipeline(); var pipeline = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"features12 passed={remembered && series && undone && restoredPaused && accessible && commands && pipeline} remembered={remembered} series={series} undo={undone} restoredPaused={restoredPaused} accessible={accessible} commands={commands} pipeline={pipeline}\n");
                        break;
                    }
                case "audit20-lyrics":
                    {
                        if (CurrentPath is not { } path) throw new InvalidOperationException("An audio fixture is required.");
                        var track = _track;
                        var early = new TaskCompletionSource<Lyrics.Result>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var finish = new TaskCompletionSource<LyricsDoc?>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var chosen = new LyricsDoc([new LyricLine(0, 1, "chosen", [], false, [])], true, "chosen");
                        try
                        {
                            Lyrics.DebugLookup = (_, title, _) => { started.TrySetResult(title); return finish.Task; };
                            _prefetchPath = path; _prefetch = early.Task;
                            _track = ("Original", "Artist", null, 1);
                            LoadLyrics(path);
                            _track = ("Changed", "Artist", null, 1);
                            early.SetResult(new(null, false));
                            var captured = await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                            CancelLyricsWork();
                            LyricsPane.Show(chosen);
                            finish.SetResult(new LyricsDoc([new LyricLine(0, 1, "old", [], false, [])], true, "old"));
                            await Task.Delay(200);
                            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                            var shown = (LyricsDoc?)typeof(LyricsView).GetField("_doc", flags)!.GetValue(LyricsPane);
                            var kept = ReferenceEquals(shown, chosen);
                            File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"lyrics passed={captured == "Original" && kept} captured={captured} manualChoiceKept={kept}\n");
                        }
                        finally { finish.TrySetResult(null); Lyrics.DebugLookup = null; _track = track; CancelLyricsWork(); }
                        break;
                    }
                case "audit20-preview":
                    {
                        var files = arg.Split('|');
                        var preview = new SeekPreviewer(Dispatcher);
                        try
                        {
                            for (var attempt = 0; attempt < 8; attempt++)
                            {
                                preview.Open(files[attempt % 2], attempt % 2 == 0 ? 1 : 1.5, 1);
                                preview.Request(0.5);
                                await Task.Delay(80);
                            }
                            preview.Open(files[1], 1.5, 1);
                            await preview.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                            await Task.Delay(500);
                            preview.Request(0.5);
                            await Task.Delay(500);
                            var pixels = new byte[preview.Bitmap.PixelWidth * preview.Bitmap.PixelHeight * 4];
                            preview.Bitmap.CopyPixels(pixels, preview.Bitmap.PixelWidth * 4, 0);
                            var white = pixels[0] > 200 && pixels[1] > 200 && pixels[2] > 200;
                            preview.Dispose();
                            await preview.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                            File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"preview passed={white && !preview.HasMedia} white={white} disposed={!preview.HasMedia}\n");
                        }
                        finally { preview.Dispose(); }
                        break;
                    }
                case "audit20-library":
                    {
                        var root = Path.Combine(Library.Dir, "index-fixture");
                        var first = Path.Combine(root, "first");
                        var second = Path.Combine(root, "second");
                        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
                        var a = Path.Combine(first, "first.wav");
                        var b = Path.Combine(second, "second.wav");
                        File.Copy(arg, a, true); File.Copy(arg, b, true);
                        await Library.ThumbnailAsync(a);
                        Library.Data.MusicFolders.Clear(); Library.Data.MusicFolders.Add(first);
                        Library.IndexMusic(Dispatcher);
                        Library.Data.MusicFolders.Add(second);
                        Library.IndexMusic(Dispatcher);
                        for (var attempt = 0; attempt < 150 && (!Library.Data.Meta.ContainsKey(a) || !Library.Data.Meta.ContainsKey(b)); attempt++) await Task.Delay(100);
                        var indexed = Library.Data.Meta.ContainsKey(a) && Library.Data.Meta.ContainsKey(b);
                        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                        var thumbs = (System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?>)typeof(Library).GetField("Thumbs", flags)!.GetValue(null)!;
                        var invalidated = !thumbs.ContainsKey(a);
                        ToggleSidebar(LibraryPanel.Section.Music);
                        Sidebar.DebugClick(L.T("Artists"));
                        var groupFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        var cache = (Dictionary<string, List<Row>>)typeof(LibraryPanel).GetField("_groups", groupFlags)!.GetValue(Sidebar)!;
                        for (var attempt = 0; attempt < 100 && !cache.ContainsKey("Artists"); attempt++) await Task.Delay(50);
                        var rows = cache.GetValueOrDefault("Artists");
                        ((TextBox)Sidebar.FindName("SearchBox")).Text = "Un";
                        var reused = rows != null && ReferenceEquals(rows, cache.GetValueOrDefault("Artists"));
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"library passed={indexed && invalidated && reused} indexed={indexed} thumbnailInvalidated={invalidated} groupsReused={reused}\n");
                        break;
                    }
                case "audit20-ui":
                    {
                        async Task Settle(int ms)
                        {
                            var clock = Stopwatch.StartNew();
                            while (clock.ElapsedMilliseconds < ms) { Motion.DebugFrame(); await Task.Delay(8); }
                        }
                        var pointer = Motion.DebugPointer;
                        var enabled = Motion.DebugEnabled;
                        try
                        {
                            Motion.DebugPointer = true; Motion.DebugEnabled = true;
                            SyncSeekPulse();
                            var playing = _mp.IsPlaying && _seekPulse.IsEnabled;
                            Hide();
                            var asleep = !_seekPulse.IsEnabled;
                            Show(); SyncSeekPulse();
                            var awake = _mp.IsPlaying && _seekPulse.IsEnabled;
                            _mp.SetPause(true); await Task.Delay(150); SyncSeekPulse();
                            var paused = !_seekPulse.IsEnabled;
                            ClearQueue();
                            _queue.AddRange(Enumerable.Range(0, 10000).Select(i => Path.Combine(Library.Dir, $"queue-{i}.wav")));
                            var allocated = GC.GetAllocatedBytesForCurrentThread();
                            var watch = Stopwatch.StartNew();
                            ToggleSidebar(LibraryPanel.Section.Queue);
                            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                            var elapsed = watch.ElapsedMilliseconds;
                            var count = ((System.Collections.ICollection)((ListBox)Sidebar.FindName("List")).ItemsSource).Count;
                            var undistortedStage = Math.Abs(Stage.RenderTransform.Value.M11 - 1) < 0.001;
                            ToggleSidebar(); await Settle(40); ToggleSidebar(); await Settle(700);
                            var reversed = Sidebar.Visibility == Visibility.Visible && Math.Abs(SidebarHost.Width - Sidebar.Width) < 0.01;
                            ToggleSidebar(); await Settle(700);
                            var closed = Sidebar.Visibility == Visibility.Collapsed && SidebarHost.Width == 0;
                            Motion.DebugPointer = false;
                            ToggleSidebar();
                            var keyboard = SidebarHost.Width == Sidebar.Width && Math.Abs(Stage.RenderTransform.Value.M11 - 1) < 0.001;
                            Motion.DebugEnabled = false;
                            ToggleSidebar();
                            var reduced = SidebarHost.Width == 0 && Sidebar.Visibility == Visibility.Collapsed;
                            var ok = playing && asleep && awake && paused && count == 10000 && bytes < 100_000_000 && undistortedStage && reversed && closed && keyboard && reduced;
                            File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"),
                                $"audit20 passed={ok} playing={playing} asleep={asleep} awake={awake} paused={paused} rows={count} allocated={bytes} renderMs={elapsed} undistortedStage={undistortedStage} reversed={reversed} closed={closed} keyboard={keyboard} reduced={reduced} video={Video.IsVisible} decodedVideo={_mp.Media?.Statistics.DecodedVideo}\n");
                        }
                        finally { Motion.DebugPointer = pointer; Motion.DebugEnabled = enabled; ClearQueue(); }
                        break;
                    }
                case "reload-audio-state-test":
                    {
                        var value = Volume.Value;
                        _mp.Mute = true;
                        CreateEngine(resume: false);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"reload-audio-state muted={_mp.Mute} volumeKept={Math.Abs(value - Volume.Value) < 0.0001}\n");
                        break;
                    }
                case "background-convert-test":
                    {
                        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        BackgroundJobs.DebugGate = gate.Task;
                        _ = Dispatcher.BeginInvoke(async () =>
                        {
                            try
                            {
                                DialogWindow? dialog = null;
                                for (var i = 0; i < 60 && dialog == null; i++)
                                { await Task.Delay(50); dialog = Application.Current.Windows.OfType<DialogWindow>().FirstOrDefault(); }
                                if (dialog == null) throw new TimeoutException("Convert dialog did not open.");
                                dialog.SaveBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                for (var i = 0; i < 60 && !BackgroundJobs.All.Any(j => j.State == JobState.Running); i++) await Task.Delay(50);
                                var job = BackgroundJobs.All.Last(j => j.State == JobState.Running);
                                dialog.CancelBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                var closedWhileRunning = !dialog.IsVisible && job.State == JobState.Running;
                                gate.TrySetResult();
                                await job.Completion.WaitAsync(TimeSpan.FromSeconds(20));
                                File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"background-convert closedWhileRunning={closedWhileRunning} completed={job.State == JobState.Completed} outputExists={File.Exists(job.Output)}\n");
                                completed.TrySetResult();
                            }
                            catch (Exception error) { completed.TrySetException(error); }
                            finally { gate.TrySetResult(); BackgroundJobs.DebugGate = null; }
                        });
                        DialogWindow.Convert(this, [arg]);
                        await completed.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        break;
                    }
                case "jobs-demo":
                    {
                        var done = BackgroundJobs.Start("Completed conversion", (_, _) => Task.FromResult<string?>(Library.Dir));
                        await done.Completion;
                        var failed = BackgroundJobs.Start("Failed download", (_, _) => Task.FromException<string?>(new IOException("Example: the server is unavailable")));
                        await failed.Completion;
                        BackgroundJobs.Start("Downloading playlist", async (job, cancel) => { job.Report(0.4, "Track 4 of 10"); await Task.Delay(60000, cancel); return Library.Dir; });
                        break;
                    }
                case "audio-device-test":
                    {
                        if (_queue.Count == 0) _queue.Add("device-test.wav");
                        _index = 0;
                        var level = Volume.Value;
                        _recoveringOutput = true;
                        Settings.Set("audio.device", "Disconnected fixture output");
                        _recoveringOutput = false;
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _ = Dispatcher.BeginInvoke(async () =>
                        {
                            for (var i = 0; i < 30; i++)
                            {
                                await Task.Delay(50);
                                if (Application.Current.Windows.OfType<DialogWindow>().FirstOrDefault() is not { } dialog) continue;
                                Snap(dialog, Path.ChangeExtension(outFile, ".dialog.png"));
                                dialog.CancelBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                closed.TrySetResult();
                                return;
                            }
                            closed.TrySetException(new TimeoutException("Audio output dialog did not open."));
                        });
                        OutputsChanged(["Headphones"], false);
                        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"audio-device-test stopped={!_mp.IsPlaying && _stoppedRequest == _playRequest} volumeKept={Math.Abs(Volume.Value - level) < 0.0001}\n");
                        Hide();
                        RecoverAudioOutput(["Headphones"], position: 1234, resume: true);
                        await _pendingStop;
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        SaveQueueSession();
                        var deferred = _deferredOutput is { Position: 1234 } && !Application.Current.Windows.OfType<DialogWindow>().Any();
                        var positionKept = QueueSession.Load()?.Position == 1234;
                        Show();
                        var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _ = Dispatcher.BeginInvoke(async () =>
                        {
                            for (var i = 0; i < 30; i++)
                            {
                                await Task.Delay(50);
                                if (Application.Current.Windows.OfType<DialogWindow>().FirstOrDefault() is not { } dialog) continue;
                                dialog.CancelBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                                dismissed.TrySetResult(); return;
                            }
                            dismissed.TrySetException(new TimeoutException("Deferred audio output dialog did not open."));
                        });
                        TogglePlay();
                        await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"audio-device-background deferred={deferred} positionKept={positionKept} stayedStopped={!_mp.IsPlaying}\n");
                        break;
                    }
                case "lyrics-motion-test":
                    {
                        Motion.DebugEnabled = Motion.DebugPointer = true;
                        _metaPending = true;
                        _isAudio = true;
                        if (_queue.Count == 0) _queue.Add("motion-test.wav");
                        _index = 0;
                        LyricsPane.ShowMessage("Motion test\nLyrics appear and leave without a jump.");
                        async Task Frames(int milliseconds)
                        {
                            var clock = Stopwatch.StartNew();
                            while (clock.ElapsedMilliseconds < milliseconds) { Motion.DebugFrame(); await Task.Delay(8); }
                        }
                        try
                        {
                            Settings.Set("ui.lyrics", false); ApplyLyricsLayout(); await Frames(600);
                            Settings.Set("ui.lyrics", true); ApplyLyricsLayout(); await Frames(60);
                            var entering = LyricsPane.Opacity > 0 && LyricsPane.Opacity < 1 && Motion.IsShown(LyricsPane);
                            Settings.Set("ui.lyrics", false); ApplyLyricsLayout(); await Frames(24);
                            var leaving = !Motion.IsShown(LyricsPane) && LyricsPane.Visibility == Visibility.Visible && LyricsColumn.Width.Value > 0;
                            var before = LyricsPane.Opacity;
                            Settings.Set("ui.lyrics", true); ApplyLyricsLayout();
                            var reversed = Math.Abs(LyricsPane.Opacity - before) < 0.01 && Motion.IsShown(LyricsPane);
                            await Frames(600);
                            var shown = LyricsPane.Visibility == Visibility.Visible && Math.Abs(LyricsPane.Opacity - 1) < 0.001;
                            Snap(this, Path.ChangeExtension(outFile, ".shown.png"));
                            Settings.Set("ui.lyrics", false); ApplyLyricsLayout(); await Frames(750);
                            var hidden = LyricsPane.Visibility == Visibility.Collapsed && LyricsColumn.Width.Value == 0;
                            Motion.DebugEnabled = false;
                            Settings.Set("ui.lyrics", true); ApplyLyricsLayout();
                            var reducedOn = LyricsPane.Visibility == Visibility.Visible && LyricsPane.Opacity == 1;
                            Settings.Set("ui.lyrics", false); ApplyLyricsLayout();
                            var reducedOff = LyricsPane.Visibility == Visibility.Collapsed;
                            Motion.DebugEnabled = true; Motion.DebugPointer = false;
                            Settings.Set("ui.lyrics", true); ApplyLyricsLayout();
                            var keyboardOn = LyricsPane.Opacity == 1;
                            Settings.Set("ui.lyrics", false); ApplyLyricsLayout();
                            var keyboardOff = LyricsPane.Visibility == Visibility.Collapsed;
                            File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"lyrics-motion entering={entering} leaving={leaving} reversed={reversed} shown={shown} hidden={hidden} reducedOn={reducedOn} reducedOff={reducedOff} keyboardOn={keyboardOn} keyboardOff={keyboardOff}\n");
                        }
                        finally { Motion.DebugEnabled = Motion.DebugPointer = null; }
                        break;
                    }
                case "own-event-race":
                    {
                        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        var closing = typeof(Player).GetField("_closing", flags)!;
                        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        closing.SetValue(_mp, blocked.Task);
                        _ownPlayRequest = ++_playRequest;
                        _mp.PlayOwn("queued-test.mp4", 0, false, null, new TrackChoice(0, "en", 0, "en", false), [], 0);
                        var old = (VideoEngine)typeof(Player).GetField("_engine", flags)!.GetValue(_mp)!;
                        ++_playRequest; // the UI chose a new item but is still waiting for the old player to stop
                        _openingMedia = true;
                        var before = _index;
                        try
                        {
                            ((Action?)typeof(VideoEngine).GetField("FirstFrame", flags)!.GetValue(old))?.Invoke();
                            ((Action?)typeof(VideoEngine).GetField("Ended", flags)!.GetValue(old))?.Invoke();
                            ((Action<Exception>?)typeof(VideoEngine).GetField("Failed", flags)!.GetValue(old))?.Invoke(new IOException("old test failure"));
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            var passed = _openingMedia && _index == before && _mp.OnOwnEngine;
                            File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"own-event-race passed={passed}\n");
                        }
                        finally
                        {
                            _mp.Stop();
                            blocked.TrySetResult();
                            await ((Task)closing.GetValue(_mp)!).WaitAsync(TimeSpan.FromSeconds(10));
                            FinishOpening();
                        }
                        break;
                    }
                case "prev": Previous(); break;
                case "path": File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"path={Path.GetFileName(CurrentPath)} queue={_queue.Count}{Environment.NewLine}"); break;
                case "state":
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"opening={_openingMedia} request={_playRequest} cancelVisible={CancelOpeningBtn.IsVisible} pendingStop={_pendingStop.Status}\n");
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), Environment.NewLine + $"osd={StageOsd.Opacity:0.00}/{OsdBorder.Opacity:0.00} own={_mp.OnOwnEngine} audiotrack={_mp.AudioTrack} spu={_mp.Spu} subtext=[{(_mp.OnOwnEngine ? _mp.SubtitleAt(_mp.Time / 1000.0)?.Replace("\n", "/") : "")}] state={_mp.State} length={_mp.Length} hwnd={_mp.Hwnd} overlay={_overlayWindow?.IsVisible} video={Video.IsVisible} time={_mp.Time} playing={_mp.IsPlaying} vol={_mp.Volume} rate={_mp.Rate} index={_index} audio={_isAudio} snapdir={Settings.Str("video.snapdir")} rec={_recording} file={_recordFile} party={_party} playingPath={CurrentPath} osdtext=[{StageOsdText.Text}] osdvisible={StageOsd.Opacity + OsdBorder.Opacity > 0.5} pausedBy={_pausedBy}");
                    break;
                case "settings":
                    OpenSettings(arg.Length > 0 ? arg : null);
                    shot = _settings;
                    break;
                case "settingssearch": _settings!.SearchBox.Text = arg; _settings.SearchBox.Focus(); break;
                case "winstate": WindowState = arg == "min" ? WindowState.Minimized : WindowState.Normal; break; // sleep / wake
                case "lyricsreshow": LyricsPane.Restyle(); break; // the same lyrics built again (same height)
                case "seekresume": // seeks; for each, how long until pictures flow again (libVLC's displayed-picture count, real video output)
                    {
                        var res = new List<string>();
                        var fps = _mp.Media?.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video) is { } vt && vt.Data.Video.FrameRateDen > 0
                            ? vt.Data.Video.FrameRateNum / (double)vt.Data.Video.FrameRateDen : 30;
                        var need = (int)(fps * 0.4 * 0.8); // 80 % of the video's own pace over 400 ms
                        var targets = arg.Length > 0 ? arg.Split(',').Select(long.Parse).ToArray() // "seekresume:24000,46000" = these times
                            : new[] { 0.2, 0.37, 0.53, 0.11, 0.71 }.Select(f => (long)(_mp.Length * f)).ToArray();
                        foreach (var target in targets)
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var start = _mp.Media?.Statistics.DisplayedPictures ?? 0;
                            SeekTo(target);
                            // smooth again: 20 new pictures within the last 400 ms (60 fps video), first time it holds
                            var marks = new List<(double T, int N)>();
                            double resumed = -1;
                            while (sw.ElapsedMilliseconds < 4000)
                            {
                                await Task.Delay(40);
                                var n = (_mp.Media?.Statistics.DisplayedPictures ?? 0) - start;
                                marks.Add((sw.Elapsed.TotalMilliseconds, n));
                                var back = marks.FirstOrDefault(m => m.T >= sw.Elapsed.TotalMilliseconds - 400);
                                if (resumed < 0 && marks.Count > 2 && n - back.N >= need) { resumed = back.T; }
                            }
                            res.Add($"{resumed:0}");
                            // pictures shown per 100 ms after the seek, for reading by eye
                            File.AppendAllText(Path.ChangeExtension(outFile, ".trace.txt"), $"{target}: " + string.Join(" ", Enumerable.Range(1, 30)
                                .Select(k => marks.LastOrDefault(m => m.T <= k * 100).N - marks.LastOrDefault(m => m.T <= (k - 1) * 100).N)) + Environment.NewLine);
                        }
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"seek resume ms ({fps:0} fps, len {_mp.Length / 1000}s, tracks {_mp.Media?.Tracks.Length}, shown {_mp.Media?.Statistics.DisplayedPictures}, decoded {_mp.Media?.Statistics.DecodedVideo}, vout {_mp.VoutCount}, hwnd {_mp.Hwnd}): " + string.Join(" / ", res) + Environment.NewLine);
                    }
                    break;
                case "fps": // pictures shown and decoded per second over 10 s of plain playback
                    {
                        var st0 = _mp.Media!.Statistics;
                        await Task.Delay(10000);
                        var st = _mp.Media!.Statistics;
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"fps shown {(st.DisplayedPictures - st0.DisplayedPictures) / 10.0:0.0} decoded {(st.DecodedVideo - st0.DecodedVideo) / 10.0:0.0} lost {(st.LostPictures - st0.LostPictures) / 10.0:0.0}" + Environment.NewLine);
                        break;
                    }
                case "ctrlwheel": // signed delta, optionally ",stage"; routes a WPF wheel event without global input injection.
                    {
                        var parts = arg.Split(',');
                        var target = parts.Length > 1 && parts[1] == "stage" ? Stage : Overlay;
                        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture))
                            { RoutedEvent = MouseWheelEvent, Source = target };
                        _debugCtrlWheel = true;
                        try { target.RaiseEvent(wheel); }
                        finally { _debugCtrlWheel = false; }
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"ctrlwheel target={target.Name} delta={wheel.Delta} handled={wheel.Handled}{Environment.NewLine}");
                        break;
                    }
                case "zoomstate":
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), FormattableString.Invariant(
                        $"zoom={Settings.Str("video.zoom")} scale={_mp.Scale:0.######} fit={Settings.Bool("video.scale")} viewport={Video.ActualWidth:0.##}x{Video.ActualHeight:0.##} dpi={VisualTreeHelper.GetDpi(Video).DpiScaleX:0.##} own={_mp.OnOwnEngine} audio={_isAudio} is360={_is360} fov={_view.Fov:0.##} time={_mp.Time} playing={_mp.IsPlaying} volume={_mp.Volume}{Environment.NewLine}"));
                    break;
                case "lyricswheel": // a notch down on the lyrics: they scroll and the scrollbar shows
                    LyricsPane.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = MouseWheelEvent, Source = LyricsPane });
                    break;
                case "wheel": // two notches down on the settings page, then the offset over time
                    {
                        var sv = _settings!.Scroller;
                        for (var k = 0; k < 2; k++) sv.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = PreviewMouseWheelEvent, Source = sv });
                        var trace = new List<string>();
                        for (var k = 0; k < 12; k++) { trace.Add(sv.VerticalOffset.ToString("0")); await Task.Delay(40); }
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), "scroll " + string.Join(" ", trace) + Environment.NewLine);
                        break;
                    }
                case "listsd":
                    File.WriteAllLines(Path.ChangeExtension(outFile, ".txt"), Enum.GetValues<MediaDiscovererCategory>()
                        .SelectMany(c => Vlc.MediaDiscoverers(c).Select(d => $"{c}: {d.Name} | {d.LongName}"))
                        .Concat(Vlc.RendererList.Select(r => $"Renderer: {r.Name} | {r.LongName}")));
                    break;
                case "look": // 360°: a flick at 130°/s should glide on for about 65° and settle without overshoot
                    _is360 = true;
                    _view.Attach(_mp);
                    _view.Fling(130, 0);
                    var samples = new List<string>();
                    for (var k = 0; k < 6; k++) { await Task.Delay(200); samples.Add($"{_view.Yaw:0.0}"); }
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"yaw {string.Join(" ", samples)}{Environment.NewLine}");
                    break;
                case "seekprobe": // thumb position every ~16 ms: must never go back, steps should be even
                    {
                        var vals = new List<double>();
                        for (var k = 0; k < 90; k++) { await Task.Delay(16); SmoothSeek(null, EventArgs.Empty); vals.Add(Seek.Value * _mp.Length); } // frames by hand: the off-screen test window gets few real ones
                        var steps = vals.Zip(vals.Skip(1), (x, y) => y - x).ToList();
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"),
                            $"backwards={steps.Count(d => d < -0.5)} zeroSteps={steps.Count(d => Math.Abs(d) < 0.5)} min={steps.Min():0.0}ms max={steps.Max():0.0}ms avg={steps.Average():0.0}ms{Environment.NewLine}");
                        break;
                    }
                case "spicytest":
                    try
                    {
                        var json = await SpicyLyrics.FetchAsync(CurrentPath!, _track.Title, _track.Artist ?? "", _track.Duration, _tags);
                        var parsed = json == null ? null : SpicyLyrics.Parse(json);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"tags={_tags.Replace('\n', '|')} json={(json == null ? "null" : json.Length.ToString())} lines={parsed?.Lines.Length} credits={string.Join(", ", parsed?.Credits?.Select(c => c.Text) ?? [])}{Environment.NewLine}");
                    }
                    catch (Exception ex) { File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), "spicy failed: " + ex + Environment.NewLine); }
                    break;
                case "switch":
                    {
                        var t0 = Stopwatch.StartNew();
                        Next();
                        var started = t0.ElapsedMilliseconds;
                        await Task.Delay(2500);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"Next() returned after {started} ms; {SwitchTiming}{Environment.NewLine}");
                        break;
                    }
                case "encodetest": // every preset on a 4 s clip of the playing file, into PYLON_ENC_OUT
                    foreach (var preset in Converter.Presets)
                    {
                        var dst = Path.Combine(Environment.GetEnvironmentVariable("PYLON_ENC_OUT")!, "out" + Array.IndexOf(Converter.Presets, preset) + (preset.Extension.Length > 0 ? preset.Extension : Path.GetExtension(CurrentPath!)));
                        var sw = Stopwatch.StartNew();
                        var (ok, error) = await Converter.RunAsync(CurrentPath!, dst, preset, 1000, 5000);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"{preset.Name}: ok={ok} {(File.Exists(dst) ? new FileInfo(dst).Length : -1)} bytes {sw.ElapsedMilliseconds} ms {error}{Environment.NewLine}");
                    }
                    break;
                case "enccancel": // a whole file, then one cancelled on the way: its ffmpeg must go and leave no file
                    {
                        var dir = Environment.GetEnvironmentVariable("PYLON_ENC_OUT")!;
                        var whole = await Converter.RunAsync(CurrentPath!, Path.Combine(dir, "whole.mp3"), Converter.Presets[0]);
                        using var stop = new CancellationTokenSource(300);
                        var dst = Path.Combine(dir, "cancelled.mp4");
                        var cancelled = await Converter.RunAsync(CurrentPath!, dst, Converter.Presets[5], cancel: stop.Token);
                        await Task.Delay(300);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"whole ok={whole.Ok}; cancelled ok={cancelled.Ok} file left={File.Exists(dst)} ffmpeg running={Process.GetProcessesByName("ffmpeg").Length}{Environment.NewLine}");
                        break;
                    }
                case "cast": // start streaming, then fetch the page and the first bytes of the stream like a phone would
                    {
                        _cast = new CastServer { Title = _title };
                        PlayAt(_index, startAt: _mp.Time, autoplay: true);
                        await Task.Delay(2500);
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        var page = await http.GetStringAsync(_cast.PageUrl);
                        using var res = await http.GetAsync(_cast.StreamUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                        var buf = new byte[188 * 20];
                        var n = await (await res.Content.ReadAsStreamAsync()).ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"),
                            $"page {page.Length} chars, vlc link {page.Contains("vlc://")}; stream {(int)res.StatusCode} {res.Content.Headers.ContentType}, {n} bytes, TS sync {buf[0] == 0x47 && buf[188] == 0x47}; still playing {_mp.IsPlaying}{Environment.NewLine}");
                        break;
                    }
                case "frames": // run the per-frame code by hand for <sec> (the off-screen test window gets few real frames)
                    for (var k = 0; k < double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) * 60; k++) { Motion.DebugFrame(); LyricsPane.DebugFrame(); await Task.Delay(16); }
                    break;
                case "flash": FlashPlayState(false); break;
                case "shot": Snap(this, Path.Combine(Path.GetDirectoryName(outFile)!, $"{Path.GetFileNameWithoutExtension(outFile)}-{arg}.png")); break; // a picture mid-script
                case "desktop": // tray icon added and removed at once, other apps' sound, the track card
                    {
                        using var tray = new TrayIcon("Pylon test");
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"tray added={tray.Added} othersPlaying={OtherAudio.Playing()}{Environment.NewLine}");
                        break;
                    }
                case "popup":
                    TrackPopup.Show(ArtImage.Source, _title, _artist, 10, () => { });
                    await Task.Delay(1200);
                    Snap(Application.Current.Windows.OfType<Window>().First(w => w != this && w.IsVisible && w != _overlayWindow), outFile);
                    Application.Current.Shutdown();
                    return;
                case "previewdump": // the seek-bar preview frame, as a PNG next to the snapshot
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(_preview.Bitmap));
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"preview{arg}: {_preview.DebugState()}{Environment.NewLine}");
                        using var f = File.Create(Path.ChangeExtension(outFile, $".preview{arg}.png"));
                        enc.Save(f);
                        break;
                    }
                case "seek": _mp.Time = long.Parse(arg); await Task.Delay(600); Refresh(); break;
                case "skipstate": File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"skip={SkipBtn.Visibility} '{SkipBtn.Content}' chapters={_chapters.Length} next={NextInFolder()}{Environment.NewLine}"); break;
                case "sheet": File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"sheet={await Frames.ContactSheetAsync(CurrentPath!, Path.GetDirectoryName(outFile)!)}{Environment.NewLine}"); break;
                case "tracks": File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), string.Join(" | ", _mp.AudioTrackDescription.Select(AudioTrackName)) + " || " + string.Join(" | ", _mp.SpuDescription.Select(t => $"{t.Id}:{t.Name}")) + Environment.NewLine); break;
                case "remote": // the remote page and one command, fetched like a phone would
                    {
                        _remote ??= new RemoteServer(RemoteCommand);
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        var page = await http.GetStringAsync(_remote.Url);
                        var state = await http.GetStringAsync(_remote.Url + "api/toggle");
                        var denied = await http.GetAsync(_remote.Url.Replace(_remote.Url.Split('/')[3], "nope") + "api/toggle");
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"page {page.Length} chars; toggle -> {state}; wrong key -> {(int)denied.StatusCode}{Environment.NewLine}");
                        break;
                    }
                case "join": // the queue joined by ffmpeg ("copy" or "mp4"), next to the snapshot
                    {
                        var dst = Path.Combine(Path.GetDirectoryName(outFile)!, "joined-" + arg + (arg == "copy" ? Path.GetExtension(_queue[0]) : ".mp4"));
                        var (ok, error) = await Ffmpeg.JoinAsync(_queue.ToList(), dst, Converter.Presets.First(p => arg == "copy" ? p.Extension.Length == 0 : p.Name == "MP4 (H.264 + AAC)"), null, CancellationToken.None);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"join {arg}: ok={ok} error={error}{Environment.NewLine}");
                        break;
                    }
                case "host": _remote ??= new RemoteServer(RemoteCommand); File.WriteAllText(Path.ChangeExtension(outFile, ".url.txt"), _remote.Url); break;
                case "partyhost": StartHosting(); File.WriteAllText(Path.ChangeExtension(outFile, ".url.txt"), _relay?.RoomUrl ?? _remote!.Url); break; // Watch Together host, no dialog
                case "party":
                    {
                        JoinParty(File.ReadAllText(arg));
                        break;
                    }
                case "filetypes":
                    _ = Dispatcher.BeginInvoke(async () =>
                    {
                        await Task.Delay(1500);
                        Snap(Application.Current.Windows.OfType<Window>().First(w => w != this && w.IsVisible && w != _overlayWindow), outFile);
                        Application.Current.Shutdown();
                    });
                    DialogWindow.FileTypes(this);
                    return;
                case "subsearch":
                    {
                        var found = await OnlineSubtitles.SearchAsync(CurrentPath!, "ru,en");
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"subs {found.Count}: {string.Join(" | ", found.Take(3).Select(r => $"{r.Language} {r.Name} hash={r.HashMatch}"))}{Environment.NewLine}");
                        break;
                    }
                case "recognize":
                    {
                        var song = await Recognize.IdentifyAsync(CurrentPath!);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"song {song}{Environment.NewLine}");
                        break;
                    }
                case "findmusic": // the drives searched for music folders (read only), with the time it took
                    {
                        var sw = Stopwatch.StartNew();
                        var found = await Task.Run(() => Library.FindMusicFolders(null, CancellationToken.None));
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"findmusic {sw.ElapsedMilliseconds} ms: {string.Join(" | ", found.Select(f => $"{f.Folder}={f.Songs}"))}{Environment.NewLine}");
                        break;
                    }
                case "drag": // the seek bar dragged across: ~125 requests a second, how many seeks reach libVLC and where it lands
                    {
                        var applied = 0; var was = _seekDone;
                        var sw = Stopwatch.StartNew();
                        for (var k = 1; k <= 75; k++) { SeekTo(_mp.Length * k / 100); if (_seekDone != was) { applied++; was = _seekDone; } await Task.Delay(8); }
                        await Task.Delay(400);
                        if (_seekDone != was) applied++;
                        await Task.Delay(800);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"drag: 75 asked in {sw.ElapsedMilliseconds - 1200} ms, {applied} seeks, target {_seekTarget} ms, now at {_mp.Time} ms{Environment.NewLine}");
                        break;
                    }
                case "rapid": // switching items fast (the queue, the menu): how long each switch holds the UI, and whether it recovers
                    {
                        var blocks = new List<long>();
                        for (var k = 0; k < int.Parse(arg); k++) { var t0 = Stopwatch.StartNew(); Next(); blocks.Add(t0.ElapsedMilliseconds); await Task.Delay(60); }
                        var ping = Stopwatch.StartNew();
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                        var pingMs = ping.ElapsedMilliseconds;
                        await Task.Delay(3000);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"rapid: Next() held the UI {string.Join(",", blocks)} ms (the last Play() alone {_switchStartMedia} ms); UI answered after {pingMs} ms; playing={_mp.IsPlaying} index={_index} file={Path.GetFileName(CurrentPath)}{Environment.NewLine}");
                        break;
                    }
                case "remoteopen": OpenOtherPlayer(); break;
                case "import": ImportSpotify(arg); break; // a Spotify link imported like the user does it
                case "radio": StartRadio(); break;
                case "browsers": File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), string.Join(" | ", BrowserCookies.Installed().Select(b => $"{b}{(BrowserCookies.IsDefault(b) ? "*" : "")}: {string.Join(", ", BrowserCookies.Profiles(b).Select(x => $"{x.Name}={x.Id}"))}")) + Environment.NewLine); break;
                case "offline": // a playlist downloaded like the menu does it; waits until it's done
                    {
                        var pl = Library.Data.Playlists.First(x => x.Name == arg);
                        DownloadPlaylist(pl);
                        await Task.Delay(500);
                        while (IsDownloading(pl)) await Task.Delay(200);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"offline '{arg}': {string.Join(" | ", pl.Items.Select(i => $"{i} art={Library.Data.Meta.GetValueOrDefault(i)?.Art != null}"))}{Environment.NewLine}");
                        break;
                    }
                case "like": LikeSong(); break;
                case "dislike": DislikeSong(); break;
                case "queue": File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"queue {_queue.Count} at {_index} radio={_radio != null}: {string.Join(" | ", _queue.Skip(Math.Max(0, _index - 1)).Take(8).Select(i => Library.TitleOf(i)))}{Environment.NewLine}"); break;
                case "nexttiming": // how long Next() takes to sound when the next item is a web one (lookahead should make it quick)
                    {
                        SwitchTiming = "";
                        var t0 = Stopwatch.StartNew();
                        Next();
                        while (t0.ElapsedMilliseconds < 30_000 && SwitchTiming.Length == 0) await Task.Delay(20); // OnPlaying of the new item
                        var meta = Library.Data.Meta.GetValueOrDefault(CurrentPath ?? "");
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"next: playing after {t0.ElapsedMilliseconds} ms: {meta?.Artist} - {meta?.Title} yt={meta?.YouTubeId} isrc={meta?.Isrc} art={(meta?.Art != null)}{Environment.NewLine}");
                        break;
                    }
                case "playlists": File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), string.Join(" | ", Library.Data.Playlists.Select(pl => $"{pl.Name}: {pl.Items.Count} items, thumb={(pl.Thumb != null && File.Exists(pl.Thumb))}, source={pl.Source != null}, art={pl.Items.Count(i => Library.Data.Meta.GetValueOrDefault(i)?.Art != null)}, isrc={pl.Items.Count(i => Library.Data.Meta.GetValueOrDefault(i)?.Isrc != null)}")) + Environment.NewLine); break;
                case "proxytest": File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), (await YtDlp.TestProxyAsync()).Replace('\n', '|') + Environment.NewLine); break;
                case "seekto": SeekTo(long.Parse(arg)); break; // a user seek (Watch Together sends it to everyone)
                case "toggle": TogglePlay(); break; // the play / pause button
                case "addslave": _mp.AddSlave(MediaSlaveType.Subtitle, new Uri(arg).AbsoluteUri, true); break; // a subtitle added while playing
                case "vol": Volume.Value = double.Parse(arg) / MaxVolume; ApplyVolume(); break; // the volume slider, in percent
                case "remoteany": RemoteView.DebugAnySession = true; WatchOtherPlayers(); break; // a paused session shows too
                case "throw": _ = Dispatcher.BeginInvoke(() => throw new InvalidOperationException("test of the safety net")); break; // must be logged, not fatal
                case "spectrum": MusicSpectrum.DebugFill(1.3); break;
                case "scene": Scene.DebugFill(); break;
                case "spotify":
                    {
                        var list = await SpotifyImport.ReadAsync(arg);
                        var items = list is { } l ? SpotifyImport.Resolve(l.Tracks) : [];
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"spotify '{list?.Name}' partial={list?.Partial} cover={list?.Cover != null} isrc={list?.Tracks.Count(t => t.Isrc != null)} art={list?.Tracks.Count(t => t.Art != null)} {items.Count}: {string.Join(" | ", items.Take(3).Select(i => Library.TitleOf(i) + " <- " + i))} id={Library.Data.Meta.GetValueOrDefault(items.FirstOrDefault() ?? "")?.SpotifyId}{Environment.NewLine}");
                        break;
                    }
                case "sessions":
                    {
                        var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                        var list = new List<string>();
                        foreach (var ses in manager.GetSessions()) { var pr = await ses.TryGetMediaPropertiesAsync(); list.Add($"{ses.SourceAppUserModelId}: {pr.Artist} - {pr.Title} @ {ses.GetTimelineProperties().Position.TotalSeconds:0}s"); }
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), "sessions: " + string.Join(" | ", list) + Environment.NewLine);
                        break;
                    }
                case "spotifylyrics":
                    {
                        var w = new SpotifyLyricsWindow(this);
                        w.Show();
                        await Task.Delay(9000);
                        Snap(w, outFile);
                        Application.Current.Shutdown();
                        return;
                    }
                case "hooks":
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"tier={System.Windows.Media.RenderCapability.Tier >> 16} mode={(PresentationSource.FromVisual(this) as HwndSource)?.CompositionTarget.RenderMode} motion=[{Motion.DebugDrivers()}] lyricsHooked={LyricsPane.DebugHooked} musicSpectrum={MusicSpectrum.DebugHooked} videoSpectrum={VideoSpectrum.DebugHooked}{Environment.NewLine}");
                    break;
                case "lyricsstate":
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), LyricsPane.DebugState() + Environment.NewLine);
                    break;
                case "wait": await Task.Delay(TimeSpan.FromSeconds(double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture))); break;
                case "hotkeys" or "onboarding" or "about" or "dialog":
                    _ = Dispatcher.BeginInvoke(async () =>
                    {
                        await Task.Delay(1500);
                        var dlg = Application.Current.Windows.OfType<Window>().First(w => w != this && w.IsVisible && w != _overlayWindow);
                        if (arg.StartsWith("update-") && dlg is DialogWindow dw) { dw.SaveBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(2500); }
                        Snap(dlg, outFile);
                        Application.Current.Shutdown();
                    });
                    switch (cmd, arg)
                    {
                        case (_, "interface-diagnostics"): DialogWindow.InterfaceDiagnostics(this); break;
                        case (_, "report-preview"): DialogWindow.ReportPreview(this, BugReport.Sanitize("Pylon report\nEngine: libVLC\nFile C:\\Users\\private\\song.mp3\nhttps://example.com/play?token=hidden\n")); break;
                        case (_, "background-tasks"): DialogWindow.BackgroundTasks(this); break;
                        case (_, "audio-output"): DialogWindow.RecoverAudioOutput(this, ["Speakers", "Headphones"]); break;
                        case (_, "playback-error"):
                            if (_lastPlaybackFailure is { } failure) DialogWindow.PlaybackError(this, _lastFailedName, failure);
                            break;
                        case (_, "filetypes"): DialogWindow.FileTypes(this); break;
                        case ("hotkeys", _): DialogWindow.EditHotkeys(this); break;
                        case ("onboarding", _): new OnboardingWindow(this, arg.Length > 0 ? int.Parse(arg) : 0).ShowDialog(); break;
                        case ("about", _): ShowAbout(); break;
                        case (_, "stream"): DialogWindow.NetworkStream(this); break;
                        case (_, "convert"): DialogWindow.Convert(this, [CurrentPath!]); break;
                        case (_, "cast"): DialogWindow.Cast(this, _cast ??= new CastServer { Title = _title }); break;
                        case (_, "clip"): DialogWindow.Convert(this, [CurrentPath!], 12_000, 27_500); break;
                        case (_, "subs"): DialogWindow.AddSubtitles(this, CurrentPath ?? "C:\\video.mp4"); break;
                        case (_, "update"):
                            var next = new Version(Updater.Current.Major, Updater.Current.Minor, Updater.Current.Build + 1);
                            DialogWindow.UpdateAvailable(this, new Updater.Release(next, $"v{next}",
                                L.Ru ? "## Исправления\n* Более надёжный поиск текстов и распознавание музыки\n* Новые пресеты конвертации\n* Режим совместимости интерфейса"
                                    : "## Fixes\n* More reliable lyrics search and music recognition\n* New conversion presets\n* Interface compatibility mode", "velopack", "https://github.com/MeowKusTrue/Pylon/releases"), true,
                                (_, cancel) => Task.Delay(60_000, cancel));
                            break;
                        case (_, "update-dl" or "update-asm"): // the download stages, faked: sizes and speed, then assembling
                            DialogWindow.UpdateAvailable(this, new Updater.Release(new Version(1, 9, 4), "v1.9.4", "Fixes", "velopack", "https://github.com/x"), true,
                                async (p, c) =>
                                {
                                    for (var k = 0; k <= (arg == "update-dl" ? 40 : 100); k += 5) { p.Report(new Updater.Progress(Updater.Stage.Downloading, k / 100.0, k * 11_000, 1_100_000, 2_400_000)); await Task.Delay(60, c); }
                                    if (arg == "update-asm") p.Report(new Updater.Progress(Updater.Stage.Assembling, 1, 0, 0, 0));
                                    await Task.Delay(60_000, c);
                                });
                            break;
                        default: DialogWindow.NewPlaylist(this); break;
                    }
                    return;
            }
            await Task.Delay(400);
        }
        await Task.Delay(1500);
        Snap(shot ?? this, outFile);
        // menus and popups live in their own windows: save each one next to the main shot
        var popups = FindPopups();
        for (int k = 0; k < popups.Count; k++) Snap(popups[k], Path.ChangeExtension(outFile, $".popup{k}.png"));
        Application.Current.Shutdown();
    }

    static List<FrameworkElement> FindPopups() =>
        PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual).OfType<FrameworkElement>()
            .Where(v => v.GetType().Name == "PopupRoot" && v.ActualWidth > 0).ToList();

    static void Snap(FrameworkElement v, string file)
    {
        var dpi = VisualTreeHelper.GetDpi(v);
        var rtb = new RenderTargetBitmap((int)(v.ActualWidth * dpi.DpiScaleX), (int)(v.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
#endif
}
