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

/// <summary>The player: lyrics (the pane, timing, looking them up ahead).</summary>
public partial class MainWindow
{
    // ───────── lyrics ─────────

    string? _lyricsFor;
    (string Title, string? Artist, string? Album, double Duration) _track;
    string _tags = "";
    long _clockMs, _clockStamp;
    long _lyricsRequest;
    CancellationTokenSource? _lyricsCancel, _prefetchCancel;
    string? _prefetchPath;
    Task<Lyrics.Result>? _prefetch;

    CancellationToken BeginLyricsRequest()
    {
        _lyricsRequest++;
        _lyricsCancel?.Cancel();
        _lyricsCancel?.Dispose();
        _lyricsCancel = new();
        return _lyricsCancel.Token;
    }

    void CancelLyricsWork(bool prefetch = true)
    {
        _lyricsRequest++;
        _lyricsCancel?.Cancel();
        _lyricsCancel?.Dispose();
        _lyricsCancel = null;
        if (prefetch)
        {
            _prefetchCancel?.Cancel(); _prefetchCancel?.Dispose(); _prefetchCancel = null;
            _prefetch = null; _prefetchPath = null;
        }
    }

    void Lyrics_Click(object sender, RoutedEventArgs e) => Settings.Set("ui.lyrics", !Settings.Bool("ui.lyrics"));

    void ApplyLyricsLayout()
    {
        var on = Settings.Bool("ui.lyrics") && _isAudio && _index >= 0 && !_mini;
        LyricsBtn.IsChecked = Settings.Bool("ui.lyrics");
        if (Motion.IsShown(LyricsPane) != on)
        {
            if (on)
            {
                LyricsPane.IsHitTestVisible = true;
                Motion.Relayout([ArtFrame, MusicCaption], () =>
                {
                    LyricsColumn.Width = new GridLength(1.5, GridUnitType.Star);
                    Motion.In(LyricsPane, dx: 24, spring: Spring.Snappy, animate: Motion.ByPointer);
                    MusicLayout_SizeChanged(MusicLayout, null!);
                }, Motion.ByPointer);
            }
            else
            {
                LyricsPane.IsHitTestVisible = false;
                Motion.Out(LyricsPane, () =>
                {
                    Motion.Relayout([ArtFrame, MusicCaption], () =>
                    {
                        LyricsPane.Visibility = Visibility.Collapsed;
                        LyricsColumn.Width = new GridLength(0);
                        MusicLayout_SizeChanged(MusicLayout, null!);
                    }, Motion.ByPointer && _isAudio && !_mini);
                }, dx: 24, spring: Spring.Snappy, animate: Motion.ByPointer && _isAudio && !_mini);
            }
            Dispatcher.BeginInvoke(() => MusicLayout_SizeChanged(MusicLayout, null!), DispatcherPriority.Loaded);
        }
        // every song's lyrics are looked up as it starts (with its real tags), shown or not: opening the panel, the
        // miniplayer or the remote then finds them there
        if (_isAudio && _index >= 0 && !_metaPending && CurrentPath is { } path && _lyricsFor != path) LoadLyrics(path);
    }

    async void LoadLyrics(string path)
    {
        var cancel = BeginLyricsRequest();
        var request = _lyricsRequest;
        var track = _track;
        var duration = Library.Data.Meta.GetValueOrDefault(path) is { DurationMs: > 0 } m ? m.DurationMs / 1000.0 : track.Duration;
        try
        {
            _lyricsFor = path;
            LyricsPane.Clock ??= () => PlaybackClock() + LyricsOffset;
            if (LyricsPane.Tag == null)
            {
                LyricsPane.Tag = true;
                LyricsPane.Seek += s => SeekTo((long)(s * 1000));
            }
            LyricsPane.ShowMessage(L.T("Looking for lyrics…"));
            var tags = Library.Data.Meta.GetValueOrDefault(path)?.SpotifyId is { } spotifyId ? _tags + "\nspotify:track:" + spotifyId : _tags;
            Lyrics.Result? result = null;
            if (_prefetchPath == path && _prefetch is { } early)
            {
                try { result = await early.WaitAsync(cancel); }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                if (ReferenceEquals(_prefetch, early)) { _prefetch = null; _prefetchPath = null; }
            }
            cancel.ThrowIfCancellationRequested();
            if (request != _lyricsRequest || CurrentPath != path) return;
            if (result?.Document == null)
                result = await Lyrics.FindDetailedAsync(path, track.Title, track.Artist, track.Album, duration, tags, cancel);
            if (request != _lyricsRequest || CurrentPath != path || cancel.IsCancellationRequested) return;
            if (result.Document != null) LyricsPane.Show(result.Document);
            else
            {
                LyricsPane.ShowMessage(L.T(result.ServiceFailed ? "Lyrics services could not be reached. Check your connection and try again."
                    : "No lyrics for this track.\nPut a .ttml or .lrc file with the same name next to it."));
                LyricsPane.MessageActions(() => LoadLyrics(path), FindLyrics);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// The next song's lyrics are looked up while this one plays (by its library tags), so they are there the moment it
    /// starts. A miss isn't kept: the song asks again with its real tags when it plays.
    /// </summary>
    void LyricsAhead()
    {
        if (NextIndex(+1) is not (>= 0 and var n) || n >= _queue.Count || _queue[n] is var next && _prefetchPath == next) return;
        if (Library.Data.Meta.GetValueOrDefault(next) is not { Title: { Length: > 0 } title } m) return; // untagged: looked up when it plays
        var seconds = m.DurationMs > 0 ? m.DurationMs / 1000.0 : (Library.HistoryOf(next)?.Length ?? 0) / 1000.0;
        _prefetchCancel?.Cancel();
        _prefetchCancel?.Dispose();
        _prefetchCancel = new();
        _prefetchPath = next;
        _prefetch = Lyrics.FindDetailedAsync(next, title, m.Artist, m.Album, seconds,
            m.SpotifyId is { } id ? "\nspotify:track:" + id : "", _prefetchCancel.Token);
    }

    /// <summary>Seconds this track's lyrics are moved ahead (Lyrics → Timing); negative = later.</summary>
    double LyricsOffset => CurrentPath is { } p ? Library.Data.LyricOffsets.GetValueOrDefault(p) : 0;

    public void NudgeLyrics(double by)
    {
        if (CurrentPath is not { } p) return;
        var v = by == 0 ? 0 : Math.Round(LyricsOffset + by, 2);
        if (v == 0) Library.Data.LyricOffsets.Remove(p); else Library.Data.LyricOffsets[p] = v;
        Library.Touch();
        Osd(L.F("Lyrics timing {0} s", v.ToString("+0.00;-0.00;0")));
    }

    async void FindLyrics()
    {
        if (CurrentPath is not { } path) return;
        var guess = string.IsNullOrWhiteSpace(_track.Artist) ? _track.Title : $"{_track.Artist} - {_track.Title}";
        if (DialogWindow.AskName(this, "Find Lyrics", "Artist - Title", "Search Spicy Lyrics and LRCLIB under another name.", "e.g., Daft Punk - Get Lucky", guess) is not { } q) return;
        if (CurrentPath != path) return;
        var track = _track;
        var dash = q.IndexOf(" - ", StringComparison.Ordinal);
        var (artist, title) = dash > 0 ? (q[..dash].Trim(), q[(dash + 3)..].Trim()) : ("", q.Trim());
        LyricsPane.ShowMessage(L.T("Looking for lyrics…"));
        if (!Settings.Bool("ui.lyrics")) Settings.Set("ui.lyrics", true);
        var cancel = BeginLyricsRequest();
        _prefetchCancel?.Cancel();
        var request = _lyricsRequest;
        try
        {
            var result = await Lyrics.SearchDetailedAsync(track.Title, track.Artist, track.Album, track.Duration, artist, title, cancel);
            var doc = result.Document;
            if (request != _lyricsRequest || CurrentPath != path || cancel.IsCancellationRequested) return;
            if (doc != null) { Library.Data.LyricFiles.Remove(path); Library.Touch(); LyricsPane.Show(doc); }
            else
            {
                LyricsPane.ShowMessage(result.ServiceFailed ? L.T("Lyrics services could not be reached. Check your connection and try again.") : L.F("Nothing found for “{0}”.", q));
                LyricsPane.MessageActions(FindLyrics, FindLyrics);
            }
        }
        catch (OperationCanceledException) { }
    }

    void OpenLyricsFile()
    {
        if (CurrentPath is not { } path) return;
        var dlg = new OpenFileDialog { Filter = L.T("Lyrics") + " (*.lrc;*.ttml)|*.lrc;*.ttml" };
        if (dlg.ShowDialog(this) != true) return;
        if (CurrentPath != path) return;
        CancelLyricsWork();
        Library.Data.LyricFiles[path] = dlg.FileName;
        Library.Touch();
        _lyricsFor = null;
        if (!Settings.Bool("ui.lyrics")) Settings.Set("ui.lyrics", true);
        else ApplyLyricsLayout();
    }

    object LyricsMenu() => Sub("Lyrics", null,
    [
        Item("Show Lyrics", null, null, () => Settings.Set("ui.lyrics", !Settings.Bool("ui.lyrics")), check: Settings.Bool("ui.lyrics")),
        Item("Find Lyrics…", null, null, FindLyrics, enabled: CurrentPath != null),
        Item("Open Lyrics File…", null, null, OpenLyricsFile, enabled: CurrentPath is { } p && !p.Contains("://")),
        new Separator(),
        Item("Show Lyrics Sooner", null, null, () => NudgeLyrics(+0.25)),
        Item("Show Lyrics Later", null, null, () => NudgeLyrics(-0.25)),
        Item(L.F("Reset Timing ({0} s)", LyricsOffset.ToString("+0.00;-0.00;0")), null, null, () => NudgeLyrics(0), enabled: LyricsOffset != 0),
        // the recording found isn't the original release: the likely fix is the difference in length (a clip's intro)
        .. _lengthGap >= 1 ? new object[] { Item(L.F("Lyrics Later by {0} s (the recording's intro)", _lengthGap.ToString("0.0")), null, null, () => NudgeLyrics(-_lengthGap - LyricsOffset)) } : [],
    ]);

    string? _lengthFor;
    double _lengthGap;

    /// <summary>
    /// A song found on the web by name is checked against the original's length (Spotify's) once it plays: when the
    /// recording runs longer or shorter, it's another release or a clip, and the word timing may be off by that much. A
    /// line says so and how to shift the lyrics; the Lyrics menu offers the difference itself.
    /// </summary>
    void CheckRecordingLength(long lengthMs)
    {
        if (_lengthFor == CurrentPath || lengthMs <= 0 || CurrentPath is not { } path) return;
        _lengthFor = path;
        _lengthGap = 0;
        if (!path.StartsWith("ytsearch://") || Library.Data.Meta.GetValueOrDefault(path) is not { DurationMs: > 0 } m) return;
        var gap = (lengthMs - m.DurationMs) / 1000.0;
        if (Math.Abs(gap) < 2.5) return;
        _lengthGap = gap;
        Osd(gap > 0 ? L.F("This recording is {0} s longer than the original. If the lyrics run early: Shift+G, or right-click → Lyrics.", Math.Round(gap))
                    : L.F("This recording is {0} s shorter than the original. If the lyrics run late: Shift+H, or right-click → Lyrics.", Math.Round(-gap)), 7);
    }

    /// <summary>libVLC reports time in steps; between them the lyrics clock runs on its own so the fill sweeps smoothly.</summary>
    double PlaybackClock()
    {
        var t = _mp.Time;
        var now = Stopwatch.GetTimestamp();
        if (t != _clockMs) { _clockMs = t; _clockStamp = now; }
        var ahead = _mp.IsPlaying ? Math.Min(0.5, (now - _clockStamp) / (double)Stopwatch.Frequency * _mp.Rate) : 0;
        return t / 1000.0 + ahead;
    }
}
