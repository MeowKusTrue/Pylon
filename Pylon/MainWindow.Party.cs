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

/// <summary>The player: Watch Together.</summary>
public partial class MainWindow
{
    // ───────── watch together ─────────
    // Like a split-screen co-op game: one shared timeline. Anyone's pause pauses everyone (and everyone sees who did it,
    // "waiting" until it goes on); anyone's seek moves everyone. The host's Pylon holds the timeline: guests send their
    // pause / seek to it and follow its state.

    string? _party;                     // the host's address while this copy follows one
    System.Net.Http.HttpClient? _partyHttp;
    DispatcherTimer? _partyTimer;
    string? _partyMissing;
    bool _hosting, _phoneRemote;        // the local server serves a watch party, the phone remote, or both
    string? _partyFrom;                 // host: the guest whose command is being carried out
    string? _pausedBy;                  // host: who paused the party (null while it plays, or when it stopped on its own)
    int _noteId;                        // host: the last event everyone is told about ("Ann jumped to 12:30")
    string _note = "", _noteBy = "";
    int _seenNote = -1;                 // guest: the last one shown
    bool _waiting;                      // guest: "X paused — waiting" is on screen

    /// <summary>The name the others see.</summary>
    static string PartyName => Settings.Str("party.name").Trim() is { Length: > 0 } n ? n : Environment.UserName;

    void WatchTogether()
    {
        var pick = DialogWindow.Choose(this, "Watch Together", "One timeline for everyone, like split-screen co-op: anyone's pause pauses all, anyone's seek moves all. Those who have the same file play their own copy; the others watch your stream.",
            [L.T("Invite friends: they follow you"), L.T("Join a friend")]);
        if (pick == 0) HostParty();
        if (pick != 1) return;
        if (DialogWindow.AskName(this, "Watch Together", "Your friend's address", "The link from their Pylon (⋯ → Watch Together → Invite friends), or from their room's page.", "https://…/r/…/  ·  http://192.168.1.10:51234/…/") is not { } url) return;
        JoinParty(url);
    }

    PartyRelay? _relay;

    /// <summary>
    /// Hosting: the local server answers friends (state; their pause and seek), the cast server streams what plays for
    /// those without the file, and with a Watch Together server set the room reaches friends anywhere (and Discord shows a
    /// "Watch together" button on the profile). The dialog shows the address again while it runs.
    /// </summary>
    void HostParty()
    {
        StartHosting();
        if (DialogWindow.Party(this, _relay?.RoomUrl ?? _remote!.Url, _relay != null)) StopHosting();
    }

    void StartHosting()
    {
        if (_party != null) LeaveParty(); // one party at a time
        _hosting = true;
        _remote ??= new RemoteServer(RemoteCommand);
        if (_cast == null)
        {
            _cast = new CastServer { Title = _title };
            if (_index >= 0) PlayAt(_index, startAt: _mp.Time, autoplay: _mp.IsPlaying); // reopen with the stream branch
        }
        if (Settings.Str("party.relay").Trim() is { Length: > 0 } server)
            _relay ??= new PartyRelay(server, () => RemoteCommand("state", 0, null), RelayCommand, () => _cast?.StreamUrl);
        _discordSig = null; // the profile gets its button
    }

    void StopHosting()
    {
        _hosting = false;
        _relay?.Dispose();
        _relay = null;
        _discordSig = null;
        _pausedBy = null;
        StopStreaming();
        if (!_phoneRemote) { _remote?.Dispose(); _remote = null; } // the phone remote may still use the same server
        Osd(L.T("Watch Together stopped"), 2);
    }

    /// <summary>A guest's command through the relay: "toggle::Ann", "seek:754000:Ann".</summary>
    void RelayCommand(string c)
    {
        var p = c.Split(':', 3);
        RemoteCommand(p[0], int.TryParse(p.ElementAtOrDefault(1), out var v) ? v : 0, p.ElementAtOrDefault(2) is { Length: > 0 } who ? who : null);
    }

    void JoinParty(string url)
    {
        if (_hosting) StopHosting();
        _partyTimer?.Stop();
        _party = url.Trim().EndsWith('/') ? url.Trim() : url.Trim() + "/";
        _partyHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        (_partyMissing, _seenNote, _waiting) = (null, -1, false);
        _partyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, async (_, _) => await FollowParty(), Dispatcher);
        Osd(L.T("Joined the watch party"), 3);
    }

    void LeaveParty()
    {
        _partyTimer?.Stop();
        _partyHttp?.Dispose();
        (_party, _partyHttp, _partyTimer) = (null, null, null);
        Osd(L.T("Left the watch party"), 2);
    }

    /// <summary>A guest watching the host's stream (no file of its own): it can't seek or pause the stream itself.</summary>
    bool PartyStreaming => _party != null && CurrentPath is { } cp && cp.StartsWith(_party, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every seek the user makes (seek bar, keys, lyrics, chapters, bookmarks, Windows' media panel). In a party as a
    /// guest it goes to the host, which moves everyone; this copy jumps at once too when it plays its own file.
    /// </summary>
    void SeekTo(long ms)
    {
        ms = Math.Max(0, ms);
        if (_mp.Length > 0) ms = Math.Min(ms, _mp.Length - 500); // a guest on the host's live stream knows no length: the host clamps
        _seekFrom = SeekBase;
        _seekTarget = ms;
        _seekAsked = Environment.TickCount64;
        // at most one seek per 120 ms, always to the latest position: dragging the bar asked for one per mouse move, and
        // libVLC worked through every precise seek in turn, so the picture lagged behind the pointer
        if (Environment.TickCount64 - _seekDone >= 120) ApplySeek();
        else if (_seekTimer is not { IsEnabled: true })
        {
            _seekTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Input, (_, _) => { _seekTimer!.Stop(); ApplySeek(); }, Dispatcher);
            _seekTimer.Start();
        }
    }

    long _seekTarget = -1, _seekAsked, _seekDone, _seekFrom;
    DispatcherTimer? _seekTimer;

    /// <summary>Where the next relative jump starts: just after a seek libVLC still reports the old time for a moment.</summary>
    long SeekBase => _seekTarget >= 0 && Environment.TickCount64 - _seekAsked < 1000 ? _seekTarget : _mp.Time;

    void ApplySeek()
    {
        _seekDone = Environment.TickCount64;
        var ms = _seekTarget;
        if (ms < 0) return;
        if (_party != null && _partyHttp != null)
        {
            _ = PartySend($"api/seek?v={ms}&n={Uri.EscapeDataString(PartyName)}");
            if (!PartyStreaming && _mp.IsSeekable) _mp.Time = ms;
            return;
        }
        if (!_mp.IsSeekable) return;
        if (CurrentPath is { } path && !path.Contains("://") && Keyframes.Handles(path)) { SeekToKeyframe(path, ms); return; }
        _mp.Time = ms;
        if (_hosting) Note(PartyName, L.F("{0} jumped to {1}", PartyName, Fmt(ms)));
    }

    /// <summary>
    /// TS / MPEG-PS: to the keyframe at or after the target when moving forward, at or before it when moving back (so a
    /// 5 s jump always moves), as MPC-HC does; the time shown is where it landed. No keyframe within 3 s: precise.
    /// </summary>
    async void SeekToKeyframe(string path, long ms)
    {
        var (forward, length) = (ms >= _seekFrom, _mp.Length);
        var (hit, size) = await Task.Run(() => (Keyframes.Near(path, ms, forward, length), new FileInfo(path).Length));
        if (CurrentPath != path || _seekTarget != ms) return; // a newer seek came meanwhile: that one moves
        if (hit is not { } k) _mp.Time = ms;
        else
        {
            // TS goes through FFmpeg's demuxer. Its seek lands just past the packet it finds, so aimed at the keyframe it
            // missed it and waited a whole GOP (2.2 s in M2TS); aimed 30 ms early it misses the frame before and starts
            // on the keyframe: 0.16-0.2 s in TS and M2TS. In MPEG-PS neither demuxer looks for keyframes when seeking by
            // time (0.5-0.7 s), so there it is the keyframe's bytes: libVLC's PS demuxer seeks by position, a sector early.
            if (Keyframes.IsTs(path)) _mp.Time = Math.Max(0, k.Ms - 30);
            else _mp.Position = (float)(Math.Max(0, k.Offset - 2048) / (double)size);
            _seekTarget = k.Ms; // the next relative jump starts from where this one landed
        }
        if (_hosting) Note(PartyName, L.F("{0} jumped to {1}", PartyName, Fmt(_seekTarget)));
    }

    async Task PartySend(string path)
    {
        try { if (_partyHttp != null && _party != null) await _partyHttp.GetStringAsync(_party + path); }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException) { Osd(L.T("The watch party didn't answer"), 3); }
    }

    /// <summary>Host: something happened that everyone should see; shown here too when a guest did it.</summary>
    void Note(string who, string text)
    {
        (_noteId, _note, _noteBy) = (_noteId + 1, text, who);
        if (who != PartyName) Osd(text, 3);
    }

    /// <summary>Host: the party's play / pause changed. Paused, everyone waits for whoever did it.</summary>
    void PartyPaused(bool paused, string who)
    {
        _pausedBy = paused ? who : null;
        Note(who, paused ? L.F("{0} paused — waiting for them", who) : L.F("{0} pressed play", who));
        if (paused && who != PartyName) Osd(L.F("{0} paused — waiting for them", who), 86_400); // stays until it goes on
    }

    bool _partyBusy;
    long _partyStreamAt;

    /// <summary>
    /// Follows the host: same file (by name) or the host's stream, play / pause, the position within 1.5 s, and what the
    /// others did ("Ann paused — waiting for them" stays on screen until someone presses play).
    /// </summary>
    async Task FollowParty()
    {
        if (_party == null || _partyHttp == null || _partyBusy) return;
        _partyBusy = true;
        try
        {
            var sent = Environment.TickCount64;
            using var doc = System.Text.Json.JsonDocument.Parse(await _partyHttp.GetStringAsync(_party + "api/state"));
            var host = doc.RootElement;
            var playing = host.TryGetProperty("playing", out var pl) && pl.GetBoolean();
            ShowPartyNews(host, playing);
            var file = host.TryGetProperty("file", out var f) ? f.GetString() : null;
            var mine = CurrentPath is { } p ? (p.Contains("://") ? p : Path.GetFileName(p)) : null;
            if (file == null) return;
            if (!string.Equals(file, mine, StringComparison.OrdinalIgnoreCase))
            {
                // not the same file here: the host's stream, when it sends one (a live copy: it follows the host by itself)
                if (host.TryGetProperty("stream", out var st) && st.GetString() is { Length: > 0 } s)
                {
                    var stream = s.Contains("://") ? s : _party + s;
                    if (CurrentPath != stream && Environment.TickCount64 - _partyStreamAt > 10_000) // a stream that failed is tried again later
                    {
                        _partyStreamAt = Environment.TickCount64;
                        PlayList([stream], 0);
                    }
                    return;
                }
                if (_partyMissing != file) { _partyMissing = file; Osd(L.F("Open “{0}” to watch together", file), 5); }
                return;
            }
            var time = host.GetProperty("time").GetInt64() + (playing ? (Environment.TickCount64 - sent) / 2 : 0); // half the round trip
            if (playing != _mp.IsPlaying) _mp.SetPause(!playing);
            if (Math.Abs(_mp.Time - time) > 1500 && _mp.IsSeekable) _mp.Time = time;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { }
        finally { _partyBusy = false; }
    }

    /// <summary>Guest: the others' doings, once each; the "waiting" line while the party is paused by someone.</summary>
    void ShowPartyNews(System.Text.Json.JsonElement host, bool playing)
    {
        var pausedBy = host.TryGetProperty("pausedBy", out var pb) ? pb.GetString() : null;
        if (!playing && pausedBy is { Length: > 0 } who && who != PartyName)
        {
            if (!_waiting) Osd(L.F("{0} paused — waiting for them", who), 86_400); // stays until it goes on
            _waiting = true;
        }
        else if (_waiting && playing) _waiting = false; // the "pressed play" note below replaces it
        if (host.TryGetProperty("noteId", out var ni) && ni.TryGetInt32(out var id) && id != _seenNote)
        {
            var first = _seenNote < 0;
            _seenNote = id;
            var by = host.TryGetProperty("noteBy", out var nb) ? nb.GetString() : null;
            if (!first && by != PartyName && host.TryGetProperty("note", out var nt) && nt.GetString() is { Length: > 0 } text && !(_waiting && !playing))
                Osd(text, 3);
        }
    }

    void ShowRemote()
    {
        _phoneRemote = true;
        _remote ??= new RemoteServer(RemoteCommand);
        if (!DialogWindow.Remote(this, _remote.Url)) return;
        _phoneRemote = false;
        if (!_hosting) { _remote.Dispose(); _remote = null; } // a watch party still uses it
    }

    /// <param name="who">A Watch Together guest's name (their pause or seek goes to everyone); null: the phone remote.</param>
    object RemoteCommand(string command, int arg, string? who)
    {
        switch (command)
        {
            case "toggle":
                _partyFrom = who;
                try { TogglePlay(); } finally { _partyFrom = null; }
                break;
            case "seek" when who != null:
                if (_mp.IsSeekable) _mp.Time = Math.Clamp(arg, 0, Math.Max(0, _mp.Length - 500));
                if (_hosting) Note(who, L.F("{0} jumped to {1}", who, Fmt(arg)));
                break;
            case "prev": Previous(); break;
            case "next": Next(); break;
            case "back": Jump(-15_000); break;
            case "forward": Jump(15_000); break;
            case "volume": Volume.Value = Math.Clamp(arg / 100.0, 0, 1); ApplyVolume(); break;
        }
        return new
        {
            title = _index >= 0 ? _title : "Pylon", artist = _index >= 0 ? _artist : null, playing = _mp.IsPlaying,
            file = _index >= 0 && CurrentPath is { } cp ? (cp.Contains("://") ? cp : Path.GetFileName(cp)) : null,
            time = Math.Max(0, _mp.Time), length = Math.Max(0, _mp.Length), volume = (int)Math.Round(Volume.Value * 100),
            stream = _cast?.StreamUrl, // Watch Together: friends without the file play this
            host = _hosting ? PartyName : null, pausedBy = _mp.IsPlaying ? null : _pausedBy, noteId = _noteId, note = _note, noteBy = _noteBy,
        };
    }

    public void StreamToNetwork()
    {
        if (_cast == null)
        {
            if (_index < 0) return;
            _cast = new CastServer { Title = _title };
            PlayAt(_index, startAt: _mp.Time, autoplay: true); // reopen with the stream branch
        }
        if (!DialogWindow.Cast(this, _cast)) return;
        StopStreaming();
    }

    void StopStreaming()
    {
        if (_cast == null) return;
        _cast.Dispose();
        _cast = null;
        if (_index >= 0) PlayAt(_index, startAt: _mp.Time, autoplay: _mp.IsPlaying);
        Osd(L.T("Stopped streaming"));
    }

    public void OpenConverter() => DialogWindow.Convert(this, CurrentPath is { } p && !p.Contains("://") ? [p] : []);

    /// <summary>The A-B range of the current file as a file of its own (or a GIF).</summary>
    void SaveClip()
    {
        if (CurrentPath is { } p && _loopB > _loopA && _loopA >= 0) DialogWindow.Convert(this, [p], _loopA, _loopB);
    }

    async void LoadWaveform(string src)
    {
        // a track not scanned before is decoded at 40× speed on another libVLC: started with the song, it kept the CPU
        // from the player's decoder now and then, and waveOut (less buffered than DirectSound) ran dry for 5 ms right at
        // the start (2 starts in 6 vs 0 in 4 without the scan); a moment later nothing is lost
        if (Waveform.Cached(src) == null)
        {
            await Task.Delay(1500);
            if (CurrentPath != src) return;
        }
        var peaks = await Waveform.GetAsync(src);
        if (peaks == null || CurrentPath != src) return;
        if (Settings.Bool("ui.realwave"))
        {
            Seek.SetPeaks(peaks);
            MiniSeek.SetPeaks(peaks);
        }
        if (!Measured) return;
        if (MeasuredGain(src) is { } g && Math.Abs(g - _trackGain) > 0.01) RampGain(g); // first listen: measured just now
        // the next song gets measured ahead, so it starts at its level
        if (NextIndex(+1) is var n && n >= 0 && n < _queue.Count && AudioExt.Contains(Path.GetExtension(_queue[n])) && !_queue[n].Contains("://"))
            _ = Waveform.GetAsync(_queue[n]);
    }

    void UpdateMiniLyrics()
    {
        var show = _mini && _isAudio && Settings.Bool("ui.lyrics") && LyricsPane.HasLyrics && CurrentPath == _lyricsFor;
        var (now, next) = show ? LyricsPane.LinesAt(PlaybackClock() + LyricsOffset) : (null, null);
        MiniLyrics.Visibility = show && ((object?)now ?? next) != null ? Visibility.Visible : Visibility.Collapsed;
        if (now == _miniLine) return;
        _miniLine = now;
        // word by word: one run per word, lit as it's sung (lines without word timing light up whole)
        MiniLyricNow.Inlines.Clear();
        _miniWords.Clear();
        if (now is { Words.Length: > 0 })
            foreach (var w in now.Words)
            {
                var brush = new SolidColorBrush(Color.FromArgb(105, 255, 255, 255));
                _miniWords.Add((w, brush));
                MiniLyricNow.Inlines.Add(new System.Windows.Documents.Run(w.Joined ? w.Text : w.Text + " ") { Foreground = brush });
            }
        else MiniLyricNow.Text = now?.Text ?? "";
        MiniLyricNext.Text = next ?? "";
        if (now != null) Motion.Pop(MiniLyricNow, dy: 10, scale: 0.97); // the new line rises in
        PaintMiniWords();
    }

    LyricLine? _miniLine;
    readonly List<(Syllable Word, SolidColorBrush Brush)> _miniWords = [];

    /// <summary>The miniplayer's words brighten as they're sung (30 times a second, from the seek bar's smooth clock).</summary>
    void PaintMiniWords()
    {
        if (_miniWords.Count == 0 || MiniLyrics.Visibility != Visibility.Visible) return;
        var t = PlaybackClock() + LyricsOffset;
        foreach (var (w, brush) in _miniWords)
        {
            var a = (byte)(105 + 150 * Math.Clamp((t - w.Start) / Math.Max(0.08, w.End - w.Start), 0, 1));
            if (brush.Color.A != a) brush.Color = Color.FromArgb(a, 255, 255, 255);
        }
    }
}
