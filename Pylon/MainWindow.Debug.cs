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
                case "state":
                    File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), Environment.NewLine + $"osd={StageOsd.Opacity:0.00}/{OsdBorder.Opacity:0.00} hwnd={_mp.Hwnd} overlay={_overlayWindow?.IsVisible} video={Video.IsVisible} time={_mp.Time} playing={_mp.IsPlaying} vol={_mp.Volume} rate={_mp.Rate} index={_index} audio={_isAudio} snapdir={Settings.Str("video.snapdir")} rec={_recording} file={_recordFile} party={_party} playingPath={CurrentPath} osdtext=[{StageOsdText.Text}] osdvisible={StageOsd.Opacity + OsdBorder.Opacity > 0.5} pausedBy={_pausedBy}");
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
                        var ok = await Converter.RunAsync(CurrentPath!, dst, preset, 1000, 5000);
                        File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), $"{preset.Name}: ok={ok} {(File.Exists(dst) ? new FileInfo(dst).Length : -1)} bytes {sw.ElapsedMilliseconds} ms{Environment.NewLine}");
                    }
                    break;
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
                case "tracks": File.AppendAllText(Path.ChangeExtension(outFile, ".state.txt"), string.Join(" | ", _mp.AudioTrackDescription.Select(AudioTrackName)) + Environment.NewLine); break;
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
                        var (ok, error) = await Ffmpeg.JoinAsync(_queue.ToList(), dst, arg == "copy", null, CancellationToken.None);
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
                        case ("hotkeys", _): DialogWindow.EditHotkeys(this); break;
                        case ("onboarding", _): new OnboardingWindow(this, arg.Length > 0 ? int.Parse(arg) : 0).ShowDialog(); break;
                        case ("about", _): ShowAbout(); break;
                        case (_, "stream"): DialogWindow.NetworkStream(this); break;
                        case (_, "convert"): DialogWindow.Convert(this, [CurrentPath!]); break;
                        case (_, "cast"): DialogWindow.Cast(this, _cast ??= new CastServer { Title = _title }); break;
                        case (_, "clip"): DialogWindow.Convert(this, [CurrentPath!], 12_000, 27_500); break;
                        case (_, "subs"): DialogWindow.AddSubtitles(this, CurrentPath ?? "C:\\video.mp4"); break;
                        case (_, "update"):
                            DialogWindow.UpdateAvailable(this, new Updater.Release(new Version(1, 1, 0), "v1.1.0",
                                "## What's Changed\n* Russian interface\n* In-app updates from GitHub\n* Miniplayer fixes", "https://github.com/x", "https://github.com/x"), true);
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
