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

    void Lyrics_Click(object sender, RoutedEventArgs e) => Settings.Set("ui.lyrics", !Settings.Bool("ui.lyrics"));

    void ApplyLyricsLayout()
    {
        var on = Settings.Bool("ui.lyrics") && _isAudio && _index >= 0 && !_mini;
        LyricsBtn.IsChecked = Settings.Bool("ui.lyrics");
        if ((LyricsPane.Visibility == Visibility.Visible) != on)
        {
            LyricsColumn.Width = on ? new GridLength(1.5, GridUnitType.Star) : new GridLength(0);
            if (on) Motion.In(LyricsPane, dx: 40, animate: Motion.ByPointer);
            else LyricsPane.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(() => MusicLayout_SizeChanged(MusicLayout, null!), DispatcherPriority.Loaded);
        }
        // every song's lyrics are looked up as it starts (with its real tags), shown or not: opening the panel, the
        // miniplayer or the remote then finds them there
        if (_isAudio && _index >= 0 && !_metaPending && CurrentPath is { } path && _lyricsFor != path) LoadLyrics(path);
    }

    async void LoadLyrics(string path)
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
        var doc = _lyricsAhead.Remove(path, out var early) && await early is { } found ? found
            : await Lyrics.FindAsync(path, _track.Title, _track.Artist, _track.Album, _track.Duration, tags);
        if (CurrentPath != path) return;
        if (doc != null) LyricsPane.Show(doc);
        else LyricsPane.ShowMessage(L.T("No lyrics for this track.\nPut a .ttml or .lrc file with the same name next to it."));
    }

    readonly Dictionary<string, Task<LyricsDoc?>> _lyricsAhead = [];

    /// <summary>
    /// The next song's lyrics are looked up while this one plays (by its library tags), so they are there the moment it
    /// starts. A miss isn't kept: the song asks again with its real tags when it plays.
    /// </summary>
    void LyricsAhead()
    {
        if (NextIndex(+1) is not (>= 0 and var n) || n >= _queue.Count || _queue[n] is var next && _lyricsAhead.ContainsKey(next)) return;
        if (Library.Data.Meta.GetValueOrDefault(next) is not { Title: { Length: > 0 } title } m) return; // untagged: looked up when it plays
        var seconds = m.DurationMs > 0 ? m.DurationMs / 1000.0 : (Library.HistoryOf(next)?.Length ?? 0) / 1000.0;
        if (_lyricsAhead.Count > 6) _lyricsAhead.Clear();
        _lyricsAhead[next] = Lyrics.FindAsync(next, title, m.Artist, m.Album, seconds, m.SpotifyId is { } id ? "\nspotify:track:" + id : "");
    }

    /// <summary>Seconds this track's lyrics are moved ahead (Lyrics → Timing); negative = later.</summary>
    double LyricsOffset => CurrentPath is { } p ? Library.Data.LyricOffsets.GetValueOrDefault(p) : 0;

    void NudgeLyrics(double by)
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
        var dash = q.IndexOf(" - ", StringComparison.Ordinal);
        var (artist, title) = dash > 0 ? (q[..dash].Trim(), q[(dash + 3)..].Trim()) : ("", q.Trim());
        LyricsPane.ShowMessage(L.T("Looking for lyrics…"));
        if (!Settings.Bool("ui.lyrics")) Settings.Set("ui.lyrics", true);
        var doc = await Lyrics.SearchAsync(_track.Title, _track.Artist, _track.Album, _track.Duration, artist, title);
        if (CurrentPath != path) return;
        if (doc != null) { Library.Data.LyricFiles.Remove(path); Library.Touch(); LyricsPane.Show(doc); }
        else LyricsPane.ShowMessage(L.F("Nothing found for “{0}”.", q));
    }

    void OpenLyricsFile()
    {
        if (CurrentPath is not { } path) return;
        var dlg = new OpenFileDialog { Filter = L.T("Lyrics") + " (*.lrc;*.ttml)|*.lrc;*.ttml" };
        if (dlg.ShowDialog(this) != true) return;
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
    ]);

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
