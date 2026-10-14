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
}

/// <summary>Everything the library sidebar shows, persisted as JSON in %APPDATA%\Pylon.</summary>
public class LibraryData
{
    public List<string> VideoFolders { get; set; } = [];
    public List<string> MusicFolders { get; set; } = [];
    public List<SavedStream> Streams { get; set; } = [];
    public List<SavedStream> Podcasts { get; set; } = []; // RSS feeds
    public List<Playlist> Playlists { get; set; } = [];
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
    }

    public static void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
        File.Move(tmp, FilePath, true); // atomic replace: a crash mid-write can't eat the library
    }

    public static void Touch()
    {
        Save();
        Changed?.Invoke();
    }

    // ───────── media helpers ─────────

    public static bool IsAudio(string path) => MainWindow.AudioExt.Contains(Path.GetExtension(path));
    public static bool IsVideo(string path) => MainWindow.VideoExt.Contains(Path.GetExtension(path));
    public static bool IsMedia(string path) => IsAudio(path) || IsVideo(path);

    public static IEnumerable<string> MediaFiles(string dir, bool recursive = false)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(IsMedia).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Sub-folders that contain media somewhere below them.</summary>
    static readonly ConcurrentDictionary<string, List<string>> FolderCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops the remembered subfolder scans (folder page "Refresh").</summary>
    public static void ForgetFolders() => FolderCache.Clear();

    /// <summary>Subfolders that contain media somewhere below. Recursive, so the result is cached per folder.</summary>
    public static IEnumerable<string> MediaFolders(string dir) => FolderCache.GetOrAdd(dir, ScanFolders);

    static List<string> ScanFolders(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir)
                .Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.Hidden))
                .Where(d => { try { return Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any(IsMedia); } catch { return false; } })
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
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
        Data.History[path] = new HistoryEntry { Title = title, Artist = artist, Time = time, Length = length, IsAudio = isAudio, LastPlayed = DateTime.Now };
    }

    /// <summary>"Title - Artist" from tags (library index, then what playback saw), else the file name.</summary>
    public static string TitleOf(string path)
    {
        if (Data.Meta.GetValueOrDefault(path) is { Title: { Length: > 0 } t } m) return m.Artist is { Length: > 0 } a ? $"{t} - {a}" : t;
        if (HistoryOf(path) is { Title: { Length: > 0 } ht } h && ht != "Pylon") // "Pylon": entries saved by 1.0.0 with the title bar text
            return h.Artist is { Length: > 0 } ha ? $"{ht} - {ha}" : ht;
        return DisplayName(path);
    }

    public static HistoryEntry? HistoryOf(string path) => Data.History.GetValueOrDefault(path);

    // ───────── music metadata index ─────────

    static int _indexing;

    /// <summary>Parses tags of every song in the music folders (once per file version), off the UI thread.</summary>
    public static void IndexMusic(Dispatcher ui)
    {
        if (Interlocked.Exchange(ref _indexing, 1) == 1) return;
        var folders = Data.MusicFolders.ToList();
        Task.Run(async () =>
        {
            var vlc = new LibVLC("--no-video", "--no-audio", "--quiet"); // own instance: keeps tag parsing away from playback
            try
            {
                var dirty = 0;
                foreach (var file in folders.SelectMany(f => MediaFiles(f, true)).Where(IsAudio))
                {
                    var stamp = File.GetLastWriteTimeUtc(file).Ticks;
                    if (Data.Meta.TryGetValue(file, out var m) && m.Stamp == stamp) continue;
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

    // ───────── thumbnails ─────────

    static readonly ConcurrentDictionary<string, ImageSource?> Thumbs = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim ThumbGate = new(2);

    /// <summary>Song artwork from tags, otherwise the Windows Explorer thumbnail. Null when there is none.</summary>
    public static async Task<ImageSource?> ThumbnailAsync(string path)
    {
        if (Thumbs.TryGetValue(path, out var cached)) return cached;
        var art = Data.Meta.GetValueOrDefault(path)?.Art; // read on the UI thread; the indexer writes Meta there
        await ThumbGate.WaitAsync();
        try
        {
            var img = await Task.Run(() => art != null ? LoadImage(art) : path.Contains("://") ? null : ShellThumbnail(path, 96));
            return Thumbs[path] = img;
        }
        finally { ThumbGate.Release(); }
    }

    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Small decoded image from a file path, file:// or http(s) URL. Call off the UI thread for web images.</summary>
    public static ImageSource? LoadImage(string uri)
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
            bmp.DecodePixelWidth = 96;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; } // missing/corrupt art: caller shows a placeholder
    }

    static ImageSource? ShellThumbnail(string path, int size)
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
