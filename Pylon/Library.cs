using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace Pylon;

public class Playlist
{
    public string Name { get; set; } = "";
    public string? Thumb { get; set; } // optional cover image (file path)
    public List<string> Items { get; set; } = [];
    public string? Source { get; set; }    // the Spotify link it was imported from: kept in step with it; "liked": the radio's likes
    public DateTime? LastSync { get; set; }
    public bool Offline { get; set; }      // its web songs are downloaded, and so are songs a Spotify sync adds
}

public class SavedStream
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public class HistoryEntry
{
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public long Time { get; set; }
    public long Length { get; set; }
    public bool IsAudio { get; set; }
    public DateTime LastPlayed { get; set; }
    public int Plays { get; set; }
    public double Progress => Length > 0 ? Math.Clamp((double)Time / Length, 0, 1) : 0;
}

public class TrackMeta
{
    public long Stamp { get; set; } // file mtime ticks, re-index when it changes
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public string? Art { get; set; }
    public string? SpotifyId { get; set; } // from a Spotify import: lyrics by exact id
    public string? Isrc { get; set; }      // the recording's code: finds the official release on YouTube Music
    public long DurationMs { get; set; }   // Spotify's length: a YouTube hit has to match it
    public string? YouTubeId { get; set; } // the video found for it once (YouTube Music), searched no more
}

/// <summary>Everything the library sidebar shows, persisted as JSON in %APPDATA%\Pylon.</summary>
public class LibraryData
{
    public List<string> VideoFolders { get; set; } = [];
    public List<string> MusicFolders { get; set; } = [];
    public List<string> Pinned { get; set; } = []; // folders pinned to the sidebar rail
    public Dictionary<string, string> LyricFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);   // track -> lyrics file the user picked
    public Dictionary<string, double> LyricOffsets { get; set; } = new(StringComparer.OrdinalIgnoreCase); // track -> seconds the lyrics run ahead
    public Dictionary<string, long> IntroEnds { get; set; } = new(StringComparer.OrdinalIgnoreCase);      // series folder -> ms where its intro ends
    public List<SavedStream> Streams { get; set; } = [];
    public List<SavedStream> Podcasts { get; set; } = []; // RSS feeds
    public List<Playlist> Playlists { get; set; } = [];
    public HashSet<string> Disliked { get; set; } = [];   // "artist|title" (letters only): the radio never offers these
    public Dictionary<string, HistoryEntry> History { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TrackMeta> Meta { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Subtitles { get; set; } = new(StringComparer.OrdinalIgnoreCase); // video -> extra subtitle files
    public Dictionary<string, List<Bookmark>> Bookmarks { get; set; } = new(StringComparer.OrdinalIgnoreCase); // media -> marked moments
}

public class Bookmark
{
    public long Time { get; set; }
    public string Name { get; set; } = "";
}

public static class Library
{
    /// <summary>App data folder; PYLON_DATA overrides it (used by UI test runs).</summary>
    public static readonly string Dir = Environment.GetEnvironmentVariable("PYLON_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pylon");
    static readonly string FilePath = Path.Combine(Dir, "library.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static LibraryData Data { get; private set; } = new();

    /// <summary>Raised on the UI thread after any change (folders, playlists, history, metadata).</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) Data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (JsonException)
        {
            File.Copy(FilePath, FilePath + ".broken", true); // keep the user's data around, start fresh
            Data = new();
        }
        // JSON loses the case-insensitive comparer
        Data.History = new(Data.History, StringComparer.OrdinalIgnoreCase);
        Data.Meta = new(Data.Meta, StringComparer.OrdinalIgnoreCase);
        Data.Subtitles = new(Data.Subtitles, StringComparer.OrdinalIgnoreCase);
        // 1.9.9 played songs from playlists as videos and wrote them down so: back among the music
        foreach (var (path, h) in Data.History) if (YtDlp.IsSong(path)) h.IsAudio = true;
    }

    /// <summary>Writes the library now (on quitting); a save still waiting is folded into this one.</summary>
    public static void Save()
    {
        _saveTimer?.Stop();
        Write(JsonSerializer.Serialize(Data, Json), Interlocked.Increment(ref _version));
    }

    static readonly object SaveLock = new();
    static long _version, _written;
    static DispatcherTimer? _saveTimer;

    /// <summary>One write at a time, and never an older library over a newer one (a late background write after the last save).</summary>
    static void Write(string json, long version)
    {
        lock (SaveLock)
        {
            if (version <= _written) return;
            var tmp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, true); // atomic replace: a crash mid-write can't eat the library
                _written = version;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (IOException) { } // disk full: keep the last good file, the next save tries again
            }
        }
    }

    /// <summary>
    /// Something changed: lists redraw now, the file is written within 1.5 s, once for every change in between. It used to
    /// be written on the UI thread at every change: a cover found, a play counted, a radio page; with a big music folder
    /// that's megabytes each time.
    /// </summary>
    public static void Touch()
    {
        if (Application.Current?.Dispatcher is { } ui && ui.CheckAccess())
        {
            _saveTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
            {
                _saveTimer!.Stop();
                var (json, version) = (JsonSerializer.Serialize(Data, Json), Interlocked.Increment(ref _version)); // read on the UI thread, written off it
                Task.Run(() => Write(json, version));
            }, ui);
            if (!_saveTimer.IsEnabled) _saveTimer.Start();
        }
        else Save();
        Changed?.Invoke();
        ShellRegistration.MusicMenu(); // Explorer's "Add to Music" follows the music folders (nothing to do when they're the same)
    }

    // ───────── media helpers ─────────

    public static bool IsAudio(string path) => MainWindow.AudioExt.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => MainWindow.VideoExt.Contains(Path.GetExtension(path));
    public static bool IsMedia(string path) => IsAudio(path) || IsVideo(path);

    public static IEnumerable<string> MediaFiles(string dir, bool recursive = false)
    {
        try
        {
            // an unreadable subfolder used to throw and empty the whole list
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true })
                .Where(IsMedia).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Sub-folders that contain media somewhere below them.</summary>
    static readonly ConcurrentDictionary<string, List<string>> FolderCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops the remembered subfolder scans (folder page "Refresh").</summary>
    public static void ForgetFolders() => FolderCache.Clear();

    /// <summary>A background folder scan finished: folder pages show the filtered list.</summary>
    public static event Action? FoldersScanned;

    /// <summary>
    /// Subfolders that contain media somewhere below (4 levels deep at most). Checking that can take seconds in a big
    /// Downloads folder, so until the background scan is done every visible subfolder is listed, like Explorer does.
    /// </summary>
    public static IEnumerable<string> MediaFolders(string dir)
    {
        if (FolderCache.TryGetValue(dir, out var known)) return known;
        List<string> all;
        try
        {
            all = [.. Directory.EnumerateDirectories(dir).Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.Hidden)).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        _ = Task.Run(() =>
        {
            var deep = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            FolderCache[dir] = [.. all.Where(d => { try { return Directory.EnumerateFiles(d, "*", deep).Any(IsMedia); } catch (IOException) { return false; } })];
            FoldersScanned?.Invoke();
        });
        return all;
    }

    /// <summary>A file without tags: "01. Artist - Title.mp3" → (Title, Artist); the name alone when it has no " - ".</summary>
    public static (string Title, string? Artist) FromFileName(string path, string title)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (title != Path.GetFileName(path) && title != name) return (title, null); // a real title tag
        name = System.Text.RegularExpressions.Regex.Replace(name, @"^\d{1,3}[\s.\-_]+(?=\D)", "").Replace('_', ' ').Trim();
        var dash = new[] { " - ", " – ", " — " }.Select(d => name.IndexOf(d, StringComparison.Ordinal)).Where(i => i > 0).DefaultIfEmpty(-1).Min();
        return dash > 0 ? (name[(dash + 3)..].Trim(), name[..dash].Trim()) : (name, null);
    }

    public static string DisplayName(string path) =>
        path.Contains("://") ? path : Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path;

    // ───────── history ─────────

    /// <summary>Privacy settings: history off, or the path matches "Never remember items matching" (wildcards, ';'-separated).</summary>
    public static bool Excluded(string path)
    {
        if (!Settings.Bool("privacy.history")) return true;
        foreach (var pattern in Settings.Str("privacy.exclude").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch(path, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    public static void Remember(string path, string title, string? artist, long time, long length, bool isAudio)
    {
        if (Excluded(path)) return;
        if (!Settings.Bool("privacy.position")) time = 0;
        Data.History[path] = new HistoryEntry
        {
            Title = title, Artist = artist, Time = time, Length = length, IsAudio = isAudio || YtDlp.IsSong(path), LastPlayed = DateTime.Now,
            Plays = Data.History.GetValueOrDefault(path)?.Plays ?? 0,
        };
    }

    /// <summary>One more start of this item (for "Most Played").</summary>
    public static void CountPlay(string path)
    {
        if (Data.History.GetValueOrDefault(path) is { } h) h.Plays++;
    }

    /// <summary>"Title - Artist" from tags (library index, then what playback saw), else the file name.</summary>
    public static string TitleOf(string path)
    {
        if (Data.Meta.GetValueOrDefault(path) is { Title: { Length: > 0 } t } m) return m.Artist is { Length: > 0 } a ? $"{t} - {a}" : t;
        if (HistoryOf(path) is { Title: { Length: > 0 } ht } h && ht != "Pylon") // "Pylon": entries saved by 1.0.0 with the title bar text
            return h.Artist is { Length: > 0 } ha ? $"{ht} - {ha}" : ht;
        return Titles.GetValueOrDefault(path) ?? DisplayName(path);
    }

    /// <summary>Names known before an item plays (videos of a web playlist), for this session.</summary>
    public static readonly Dictionary<string, string> Titles = [];

    public static HistoryEntry? HistoryOf(string path) => Data.History.GetValueOrDefault(path);

    // ───────── music metadata index ─────────

    static int _indexing;

    /// <summary>Parses tags of every song in the music folders (once per file version), off the UI thread.</summary>
    public static void IndexMusic(Dispatcher ui)
    {
        if (Interlocked.Exchange(ref _indexing, 1) == 1) return;
        var folders = Data.MusicFolders.ToList();
        // Meta is a plain Dictionary the UI thread writes to: this thread only reads a copy of the stamps
        var stamps = Data.Meta.ToDictionary(m => m.Key, m => m.Value.Stamp);
        Task.Run(async () =>
        {
            var vlc = new LibVLC("--no-video", "--no-audio", "--quiet"); // own instance: keeps tag parsing away from playback
            try
            {
                var dirty = 0;
                foreach (var file in folders.SelectMany(f => MediaFiles(f, true)).Where(IsAudio))
                {
                    var stamp = File.GetLastWriteTimeUtc(file).Ticks;
                    if (stamps.GetValueOrDefault(file) == stamp) continue;
                    var media = new Media(vlc, file, FromType.FromPath);
                    await media.Parse(MediaParseOptions.ParseLocal | MediaParseOptions.FetchLocal, 2000);
                    var meta = new TrackMeta
                    {
                        Stamp = stamp,
                        Title = media.Meta(MetadataType.Title),
                        Artist = media.Meta(MetadataType.Artist),
                        Album = media.Meta(MetadataType.Album),
                        Genre = media.Meta(MetadataType.Genre),
                        Art = media.Meta(MetadataType.ArtworkURL),
                    };
                    DisposeLater(media);
                    ui.Invoke(() => Data.Meta[file] = meta);
                    if (++dirty % 50 == 0) ui.Invoke(Touch);
                }
                if (dirty > 0) ui.Invoke(Touch);
            }
            finally
            {
                _indexing = 0;
                await Task.Delay(5000); // outlive the deferred Media disposals
                vlc.Dispose();
            }
        });
    }

    /// <summary>
    /// Releases a Media a bit later. Disposing it right after Parse() completes frees it while libVLC is still
    /// inside its "parsed" callback (native crash in ntdll, reproduced 6/6 on a fresh library with video playing).
    /// </summary>
    public static void DisposeLater(Media media) => Task.Delay(2000).ContinueWith(_ => media.Dispose());

    // ───────── "Add to Music" and finding music ─────────

    /// <summary>Where "Add to Music" copies to: the library's music folders, else Windows' Music folder.</summary>
    public static List<string> MusicTargets() =>
        Data.MusicFolders.Count > 0 ? [.. Data.MusicFolders] : [Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)];

    /// <summary>
    /// Copies songs, with their .lrc / .ttml, into a music folder. One of the same name and size already there counts as
    /// done; a different one of that name gets " (2)". Returns how many were copied.
    /// </summary>
    public static int CopyToMusic(IEnumerable<string> files, string folder)
    {
        Directory.CreateDirectory(folder);
        var into = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var n = 0;
        foreach (var f in files.Where(File.Exists))
        {
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), into, StringComparison.OrdinalIgnoreCase)) continue; // already in it
            var dst = Path.Combine(into, Path.GetFileName(f));
            if (File.Exists(dst) && new FileInfo(dst).Length == new FileInfo(f).Length) continue;
            for (var k = 2; File.Exists(dst); k++) dst = Path.Combine(into, $"{Path.GetFileNameWithoutExtension(f)} ({k}){Path.GetExtension(f)}");
            File.Copy(f, dst);
            foreach (var side in new[] { ".lrc", ".ttml" })
                if (File.Exists(Path.ChangeExtension(f, side)) && !File.Exists(Path.ChangeExtension(dst, side)))
                    File.Copy(Path.ChangeExtension(f, side), Path.ChangeExtension(dst, side));
            n++;
        }
        return n;
    }

    static readonly HashSet<string> NotMusic = new(StringComparer.OrdinalIgnoreCase)
        { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "$Recycle.Bin", "System Volume Information", "node_modules", ".git", "steamapps", "WindowsApps" };

    /// <summary>
    /// Folders with songs on the fixed drives, outermost first: a folder holding 3+ songs (audio of 1 MB or more: game
    /// sound effects are smaller) counts, and the count moves up through parents that hold nothing but folders and
    /// covers (Music\Artist\Album → Music). Program folders (any .dll inside) and system folders are skipped, and so
    /// is anything already in the library. Off the UI thread; <paramref name="progress"/> gets the folder being read.
    /// </summary>
    public static List<(string Folder, int Songs)> FindMusicFolders(IProgress<string>? progress, CancellationToken ct)
    {
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
        var songs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // folder → songs in it and below (pure containers only)
        var known = Data.MusicFolders.Select(f => Path.TrimEndingDirectorySeparator(f) + "\\").ToList();
        var last = Environment.TickCount64;

        // returns the songs under dir if dir can be part of a music folder, -1 when it holds other things (a stop)
        int Walk(string dir)
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 - last > 150) { last = Environment.TickCount64; progress?.Report(dir); }
            int own = 0, below = 0;
            var pure = true;
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
            {
                var ext = Path.GetExtension(f);
                if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return -1; // a program (Downloads has .exe installers, not .dll)
                if (IsAudio(f)) { if (new FileInfo(f).Length >= 1 << 20) own++; }
                else if (ext.ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".lrc" or ".ttml" or ".cue" or ".m3u" or ".m3u8" or ".nfo" or ".txt" or ".log")) pure = false;
            }
            var stops = new List<(string, int)>();
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", opts))
            {
                if (NotMusic.Contains(Path.GetFileName(sub)) || known.Any(k => (sub + "\\").StartsWith(k, StringComparison.OrdinalIgnoreCase))) continue;
                var s = Walk(sub);
                if (s > 0) { below += s; stops.Add((sub, s)); }
            }
            var total = own + below;
            if (pure && Path.GetDirectoryName(dir) != null) return total; // a container: the parent decides (a drive root never is one)
            // this folder holds other things: its own songs make it a music folder, its pure subfolders are ones of their own
            if (own >= 3) songs[dir] = own;
            foreach (var (sub, s) in stops) if (s >= 3 && !songs.Keys.Any(k => k.StartsWith(sub + "\\", StringComparison.OrdinalIgnoreCase) || k == sub)) songs[sub] = s;
            return -1;
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            try { Walk(drive.RootDirectory.FullName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // a pure folder that held songs was reported by its first non-pure parent; nested ones go to the outermost
        return [.. songs.Where(s => !songs.Keys.Any(k => k != s.Key && s.Key.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.Value).Select(s => (s.Key, s.Value))];
    }

    // ───────── songs by name ─────────

    static Dictionary<string, List<string>>? _songs;
    static int _songsFor = -1;

    /// <summary>"The Beatles" and "the beatles!" alike: lower case, letters and digits only.</summary>
    public static string SongName(string s) => new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>
    /// The library file of a song (Spotify import, radio): same title, and the first named artist among its artists. Names
    /// are looked up in a table built once (the disk is asked only about a match); it used to walk the whole library for
    /// every song of an import or a radio page.
    /// </summary>
    public static string? FindSong(string title, string artist)
    {
        // ponytail: rebuilt when the number of known songs changes, not on a retag of one file (rare; a restart fixes it)
        if (_songs == null || _songsFor != Data.Meta.Count)
        {
            _songs = [];
            foreach (var (path, m) in Data.Meta)
                if (m.Title is { Length: > 0 } t && !path.Contains("://"))
                    (_songs.TryGetValue(SongName(t), out var list) ? list : _songs[SongName(t)] = []).Add(path);
            _songsFor = Data.Meta.Count;
        }
        var who = SongName(artist.Split(',')[0]);
        return _songs.GetValueOrDefault(SongName(title))?.FirstOrDefault(p => SongName(Data.Meta.GetValueOrDefault(p)?.Artist ?? "").Contains(who) && File.Exists(p));
    }

    // ───────── thumbnails ─────────

    static readonly ConcurrentDictionary<string, ImageSource?> Thumbs = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim ThumbGate = new(2);

    /// <summary>A cover found later (Deezer, YouTube): the list asks for it again instead of keeping "none".</summary>
    public static void ForgetThumb(string path) => Thumbs.TryRemove(path, out _);

    /// <summary>Song artwork from tags, otherwise the Windows Explorer thumbnail. Null when there is none.</summary>
    public static async Task<ImageSource?> ThumbnailAsync(string path)
    {
        if (Thumbs.TryGetValue(path, out var cached)) return cached;
        var art = Data.Meta.GetValueOrDefault(path)?.Art; // read on the UI thread; the indexer writes Meta there
        await ThumbGate.WaitAsync();
        try
        {
            // Windows' own thumbnail first (fast, cached); for videos it can't preview, a frame through libVLC
            var img = await Task.Run(() => art != null ? LoadImage(art) : path.Contains("://") ? null
                : ShellThumbnail(path, 96) ?? (MainWindow.VideoExt.Contains(Path.GetExtension(path)) ? Frames.Thumbnail(path, 160) : null));
            if (Thumbs.Count > 1500) Thumbs.Clear(); // ponytail: a whole-cache drop, not LRU; 1500 small covers ≈ 55 MB at most
            return Thumbs[path] = img;
        }
        finally { ThumbGate.Release(); }
    }

    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Small decoded image from a file path, file:// or http(s) URL. Call off the UI thread for web images.</summary>
    public static ImageSource? LoadImage(string uri, int width = 96)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                bmp.StreamSource = new MemoryStream(Http.GetByteArrayAsync(uri).GetAwaiter().GetResult()); // BitmapImage's own download can't be frozen
            else
                bmp.UriSource = new Uri(uri);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = width;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; } // missing/corrupt art: caller shows a placeholder
    }

    public static BitmapSource? ShellThumbnail(string path, int size)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            // THUMBNAILONLY: no generic file-type icons, we draw our own placeholder instead
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x8, out var hbmp) != 0) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hbmp); }
        }
        catch (Exception e) when (e is COMException or FileNotFoundException or ArgumentException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
}
