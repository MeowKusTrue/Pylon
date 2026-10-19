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

/// <summary>The player: Last.fm scrobbles and the Discord status.</summary>
public partial class MainWindow
{
    // ───────── Last.fm ─────────

    string? _scrobbleFor;
    DateTime _scrobbleStart;
    long _playedMs, _scrobbleTick;
    bool _scrobbled;

    /// <summary>Now Playing when a song starts; a scrobble once half of it (or 4 minutes) has really been played.</summary>
    void UpdateScrobble()
    {
        if (!LastFm.Possible || !LastFm.SignedIn || !Settings.Bool("lastfm.scrobble") || !_isAudio || _index < 0 || _metaPending
            || CurrentPath is not { } path || Library.Excluded(path) || string.IsNullOrWhiteSpace(_artist)) return;
        var now = Environment.TickCount64;
        if (path != _scrobbleFor)
        {
            (_scrobbleFor, _scrobbleStart, _playedMs, _scrobbled, _scrobbleTick) = (path, DateTime.UtcNow, 0, false, now);
            _ = LastFm.NowPlayingAsync(_artist!, _title, _track.Album, _mp.Length / 1000.0);
            return;
        }
        if (_mp.IsPlaying) _playedMs += now - _scrobbleTick;
        _scrobbleTick = now;
        var len = _mp.Length;
        if (_scrobbled || len <= 30_000 || _playedMs < Math.Min(len / 2, 240_000)) return;
        _scrobbled = true;
        _ = LastFm.ScrobbleAsync(_artist!, _title, _track.Album, len / 1000.0, _scrobbleStart);
    }

    // ───────── Discord status ─────────

    Discord? _discord;
    string? _discordSig;
    long _discordPos, _discordAt;

    /// <summary>Sends the track to Discord when it, play / pause or the position (a seek) changes.</summary>
    void UpdateDiscord()
    {
        if (!Settings.Bool("privacy.discord") || !Discord.Possible)
        {
            if (_discordSig != null) { _ = _discord?.SetAsync(null, null, false, 0, 0, false); _discordSig = null; }
            return;
        }
        var playing = _mp.IsPlaying;
        var sig = _index >= 0 && CurrentPath is { } p && !Library.Excluded(p) ? $"{p}|{playing}|{_relay?.RoomUrl}" : "";
        var now = Environment.TickCount64;
        var expected = _discordPos + (playing ? now - _discordAt : 0);
        if (sig == _discordSig && (!playing || Math.Abs(expected - _mp.Time) < 3000)) return;
        _discordSig = sig;
        _discordPos = _mp.Time;
        _discordAt = now;
        _discord ??= new Discord();
        _ = sig.Length == 0 ? _discord.SetAsync(null, null, false, 0, 0, false)
                            : _discord.SetAsync(_title, _artist, _isAudio, Math.Max(0, _mp.Time), _mp.Length, playing, _relay?.RoomUrl, DiscordCover(CurrentPath!), _track.Album);
    }

    readonly Dictionary<string, string?> _coverUrls = [];

    /// <summary>
    /// The cover Discord shows: it takes only web addresses. A song from the web has one (its cover or thumbnail); a local
    /// song's cover is looked up on Deezer by artist, title and length, once, and sent when it arrives.
    /// </summary>
    string? DiscordCover(string path)
    {
        if (Library.Data.Meta.GetValueOrDefault(path)?.Art is { } art && art.StartsWith("http")) return art;
        if (_mp.Media?.Meta(MetadataType.ArtworkURL) is { } web && web.StartsWith("http")) return web;
        if (_coverUrls.TryGetValue(path, out var found)) return found;
        _coverUrls[path] = null; // asked once per song
        if (!_isAudio || string.IsNullOrWhiteSpace(_artist)) return null;
        _ = Find(_title, _artist, _mp.Length / 1000.0);
        return null;

        async Task Find(string title, string artist, double seconds)
        {
            if (await Deezer.FindAsync(title, artist, seconds) is not { Cover: { } cover }) return;
            _coverUrls[path] = cover;
            if (CurrentPath == path) _discordSig = null; // sent with the next update
        }
    }
}
