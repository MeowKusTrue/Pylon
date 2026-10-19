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

/// <summary>The player: radio by a song, offline playlists, songs from YouTube Music, Spotify imports.</summary>
public partial class MainWindow
{
    // ───────── radio by a song ─────────

    /// <summary>What a radio plays on from: YouTube Music's token for 50 more, the last song it brought, what it brought already.</summary>
    sealed class Radio
    {
        public string? More, Last;
        public readonly HashSet<string> Seen = [];
        public readonly List<string> Likes = []; // videos liked on this radio: every other page grows from one of them
        public int Pages;
        public bool Busy;
    }

    Radio? _radio;

    static string SongKey(string? artist, string? title)
    {
        static string N(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
        return N((artist ?? "").Split(',', '&')[0]) + "|" + N(title ?? "");
    }

    /// <summary>
    /// Radio by a song ("my wave" for one track): YouTube Music's radio for it plays on and on. Songs that are in the
    /// library play from the library, the rest from YouTube Music (the exact video, nothing to search); when five are left
    /// it asks for more. Like: more like that song comes next, and it goes to "Liked songs". Dislike: skipped now, never
    /// offered again.
    /// </summary>
    public async void StartRadio(string? src = null)
    {
        src ??= CurrentPath;
        if (src == null) return;
        if (!YtDlp.Available && (_ytDlpDeclined || !await OfferYtDlp())) return;
        Osd(L.T("Tuning the radio…"), 15);
        if (await RadioSeed(src) is not { } id) { Osd(L.T("Couldn't find this song on YouTube Music"), 4); return; }
        var (songs, more) = await YtMusic.RadioAsync(id, null);
        if (songs.Count == 0) { Osd(L.T("No radio for this song"), 4); return; }
        var radio = new Radio { More = more };
        radio.Seen.Add(id);
        radio.Seen.Add(SongKey(songs[0].Artist, songs[0].Title)); // the first one is the song itself
        var items = RadioItems(radio, songs);
        if (src == CurrentPath && _index >= 0)
        {
            // the song plays on, the radio follows it
            _queue.Clear();
            _queue.Add(src);
            _queue.AddRange(items);
            _index = 0;
            BuildShuffle();
            LookAhead();
        }
        else PlayList([src, .. items], 0);
        _radio = radio;
        Osd(L.F("Radio by “{0}”: {1} songs, more as it plays", Library.TitleOf(src), items.Count), 4);
        Sidebar.Render();
    }

    // ───────── offline playlists ─────────

    readonly HashSet<Playlist> _downloading = [];

    public bool IsDownloading(Playlist p) => _downloading.Contains(p);

    /// <summary>
    /// "Download for Offline": every web song of the list is saved into a folder named after it (Settings → Library; one at a time, as the site has
    /// it) and the list, other lists and the queue play the file from then on; it keeps the song's name, cover, album and
    /// ids, so lyrics and Discord still find it. Songs a Spotify sync adds later are downloaded too. Stopping keeps what's
    /// saved.
    /// </summary>
    public async void DownloadPlaylist(Playlist p)
    {
        if (!YtDlp.Available && (_ytDlpDeclined || !await OfferYtDlp())) return;
        p.Offline = true;
        Library.Touch();
        if (!_downloading.Add(p)) return;
        static string Safe(string s) => string.Concat(s.Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.') is { Length: > 0 } x ? x[..Math.Min(x.Length, 120)] : "_";
        var dir = Path.Combine(Settings.Str("library.offline"), Safe(p.Name));
        var todo = p.Items.Where(i => i.Contains("://")).Distinct().ToList();
        int saved = 0, missed = 0;
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var item in todo)
            {
                if (!p.Offline || !Library.Data.Playlists.Contains(p)) break; // stopped, or the list was deleted
                Osd(L.F("Saving “{0}” for offline: {1} of {2}", p.Name, saved + missed + 1, todo.Count), 4);
                var meta = Library.Data.Meta.GetValueOrDefault(item);
                var name = Safe(meta is { Title: { } t } ? meta.Artist is { } a ? $"{a} - {t}" : t : Library.TitleOf(item));
                if (await YtDlp.DownloadAudioAsync(await YtDlp.PageOf(item), Path.Combine(dir, name)) is not { } file) { missed++; continue; }
                // the file carries the song's names and cover; its stamp tells the music indexer it's read already
                Library.Data.Meta[file] = new TrackMeta
                {
                    Stamp = File.GetLastWriteTimeUtc(file).Ticks, Title = meta?.Title ?? name, Artist = meta?.Artist, Album = meta?.Album,
                    Art = meta?.Art, SpotifyId = meta?.SpotifyId, Isrc = meta?.Isrc, DurationMs = meta?.DurationMs ?? 0, YouTubeId = meta?.YouTubeId,
                };
                foreach (var list in Library.Data.Playlists)
                    for (var k = 0; k < list.Items.Count; k++) if (list.Items[k] == item) list.Items[k] = file;
                for (var k = 0; k < _queue.Count; k++) if (_queue[k] == item && k != _index) _queue[k] = file; // the playing one carries on
                saved++;
                Library.Touch();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Osd(e.Message, 5); }
        finally { _downloading.Remove(p); }
        Osd(missed == 0 ? L.F("“{0}” is saved for offline: {1} songs", p.Name, saved) : L.F("“{0}”: {1} saved, {2} not found", p.Name, saved, missed), 5);
    }

    /// <summary>
    /// A YouTube Music song (search, radio) as a queue item: the library file when you have it, else a web song that knows
    /// its exact video, cover and album.
    /// </summary>
    public static string WebSong(YtMusic.Hit h)
    {
        var item = SpotifyImport.Resolve([new(h.Title, h.Artist, (long)(h.Seconds * 1000), "", null, h.Art, h.Album)])[0];
        if (item.StartsWith("ytsearch://") && Library.Data.Meta.GetValueOrDefault(item) is { } m) m.YouTubeId = h.VideoId;
        return item;
    }

    /// <summary>The song's video on YouTube Music: from its address, from an earlier search, or found by title and artist.</summary>
    async Task<string?> RadioSeed(string src)
    {
        if (System.Text.RegularExpressions.Regex.Match(src, @"(?:[?&]v=|youtu\.be/)([\w-]{11})") is { Success: true } link) return link.Groups[1].Value;
        var meta = Library.Data.Meta.GetValueOrDefault(src);
        if (meta?.YouTubeId != null) return meta.YouTubeId;
        var (title, artist, seconds) = src == CurrentPath ? (_track.Title, _track.Artist, _track.Duration) : (meta?.Title ?? Library.TitleOf(src), meta?.Artist, (meta?.DurationMs ?? 0) / 1000.0);
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (artist != null && await YtMusic.FindAsync(title, artist, seconds, meta?.Isrc) is { } found) return found;
        try { return (await YtMusic.SearchSongsAsync($"{artist} {title}".Trim())).FirstOrDefault()?.VideoId; } // a local file's length can differ: the top hit
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Radio songs as queue items: the new ones only (not disliked, not in this radio already), library files first.</summary>
    List<string> RadioItems(Radio radio, List<YtMusic.Hit> songs)
    {
        var fresh = songs.Where(s => !Library.Data.Disliked.Contains(SongKey(s.Artist, s.Title)) & radio.Seen.Add(s.VideoId) & radio.Seen.Add(SongKey(s.Artist, s.Title))).ToList();
        if (songs.Count > 0) radio.Last = songs[^1].VideoId;
        var items = SpotifyImport.Resolve([.. fresh.Select(s => new SpotifyImport.Track(s.Title, s.Artist, (long)(s.Seconds * 1000), "", null, s.Art, s.Album))]);
        for (var k = 0; k < items.Count; k++)
            if (items[k].StartsWith("ytsearch://") && Library.Data.Meta.GetValueOrDefault(items[k]) is { } m) m.YouTubeId = fresh[k].VideoId; // the exact video: no search
        return [.. items.Distinct().Where(i => !_queue.Contains(i))];
    }

    /// <summary>Five songs left: 50 more (the next page, or a new radio from the last song when YouTube Music's runs out).</summary>
    async void TopUpRadio()
    {
        if (_radio is not { Busy: false } radio || _queue.Count - _index > 5) return;
        radio.Busy = true;
        // after a like, every other page is the radio of a liked song: the station drifts toward what you like
        var fromLike = radio.Likes.Count > 0 && radio.Pages++ % 2 == 1 ? radio.Likes[Random.Shared.Next(radio.Likes.Count)] : null;
        var (songs, more) = fromLike != null ? await YtMusic.RadioAsync(fromLike, null)
            : await YtMusic.RadioAsync(radio.More == null ? radio.Last : null, radio.More);
        radio.Busy = false;
        if (_radio != radio) return;
        if (fromLike == null || radio.More == null) radio.More = more; // a liked song's page doesn't lose the station's own place
        if (songs.Count == 0) return; // offline: tried again on the next song
        _queue.AddRange(RadioItems(radio, songs));
        BuildShuffle();
        Sidebar.Render();
    }

    /// <summary>Into "Liked songs"; on the radio, a few songs like it come next.</summary>
    public async void LikeSong()
    {
        if (CurrentPath is not { } src) return;
        var liked = Library.Data.Playlists.FirstOrDefault(p => p.Source == "liked");
        if (liked == null) Library.Data.Playlists.Add(liked = new Playlist { Name = L.T("Liked songs"), Source = "liked" });
        if (!liked.Items.Contains(src)) liked.Items.Add(src);
        Library.Touch();
        Osd(_radio != null ? L.T("Liked: more like this next") : L.T("Added to Liked songs"), 2);
        if (LastFm.SignedIn && !string.IsNullOrWhiteSpace(_track.Artist)) _ = LastFm.LoveAsync(_track.Artist!, _track.Title); // ♥ on Last.fm too
        if (_radio is not { } radio || await RadioSeed(src) is not { } id) return;
        if (!radio.Likes.Contains(id)) radio.Likes.Add(id);
        var (songs, _) = await YtMusic.RadioAsync(id, null);
        if (_radio != radio || CurrentPath != src) return;
        _queue.InsertRange(_index + 1, RadioItems(radio, songs.Skip(1).Take(6).ToList()));
        BuildShuffle();
        Sidebar.Render();
    }

    /// <summary>Skipped, and never offered by a radio again.</summary>
    public void DislikeSong()
    {
        if (CurrentPath == null) return;
        Library.Data.Disliked.Add(SongKey(_track.Artist, _track.Title));
        Library.Touch();
        Osd(L.T("Won't come up on the radio again"), 2);
        NextInQueue();
    }

    List<object> RadioMenuItems(string? path) => _radio == null
        ? [Item("Radio by This Song", "radio", KeyOf("radio"), () => StartRadio(), enabled: path != null)]
        : [
            Sub("Radio", "radio", [
                Item("Like: More Like This", null, KeyOf("like"), LikeSong),
                Item("Dislike: Never Again", null, KeyOf("dislike"), DislikeSong),
                new Separator(),
                Item("Radio by This Song", null, KeyOf("radio"), () => StartRadio(), enabled: path != null),
                Item("Stop the Radio", null, null, () => { _radio = null; Osd(L.T("The radio stops after the queue"), 2); }),
            ]),
        ];


    /// <summary>

    /// <summary>A Spotify link becomes a Pylon playlist and starts playing (yt-dlp is offered for songs not on disk).</summary>
    async void ImportSpotify(string link)
    {
        Osd(L.T("Reading the Spotify list…"), 10);
        if (await SpotifyImport.ReadAsync(link) is not { Tracks.Count: > 0 } list) { Osd(L.T("Couldn't read that Spotify link"), 3); return; }
        var items = SpotifyImport.Resolve(list.Tracks);
        var playlist = Library.Data.Playlists.FirstOrDefault(p => p.Source == link)
            ?? new Playlist { Name = list.Name, Source = link }; // the same link again: the same playlist, brought up to date
        if (!Library.Data.Playlists.Contains(playlist)) Library.Data.Playlists.Add(playlist);
        (playlist.Items, playlist.LastSync) = (items, DateTime.UtcNow);
        playlist.Thumb = await SpotifyImport.SaveCoverAsync(list.Cover) ?? playlist.Thumb;
        Library.Touch();
        var local = items.Count(i => !i.StartsWith("ytsearch://"));
        Osd(list.Partial ? L.F("{0}: the first {1} songs. Sign in to Spotify (Settings → Online) for the whole list.", list.Name, items.Count)
            : L.F("{0}: {1} songs, {2} from your library", list.Name, items.Count, local), list.Partial ? 7 : 4);
        PlayList(items, 0);
        _ = SpotifyImport.EnrichAsync(items, () => { Library.Touch(); Sidebar.Render(); });
    }

    /// <summary>
    /// Imported Spotify lists follow their source: at start and every 6 hours, each one last read more than 6 hours ago is
    /// read again (one after the other, quietly); what changed is said in a line.
    /// </summary>
    async Task SyncSpotifyLists()
    {
        foreach (var p in Library.Data.Playlists.Where(p => p.Source?.StartsWith("http") == true && (p.LastSync == null || DateTime.UtcNow - p.LastSync > TimeSpan.FromHours(6))).ToList())
        {
            if (await SpotifyImport.SyncAsync(p) is not { } change) continue;
            Library.Touch();
            if (change.Added + change.Removed > 0) Osd(L.F("“{0}” updated from Spotify: +{1} / −{2}", p.Name, change.Added, change.Removed), 4);
            if (p.Offline && change.Added > 0) DownloadPlaylist(p); // new songs of an offline list come down too
            _ = SpotifyImport.EnrichAsync(p.Items, () => { Library.Touch(); Sidebar.Render(); });
        }
        // songs imported before covers came from Deezer (1.9.8 and older) get theirs now, one list after another; songs
        // that already have a cover and an ISRC are skipped, so later runs only retry the few Deezer didn't know
        foreach (var p in Library.Data.Playlists.ToList())
            await SpotifyImport.EnrichAsync(p.Items, () => { Library.Touch(); Sidebar.Render(); });
    }
}
