using System.IO;
using System.Text.Json;

namespace Pylon;

public static class LibraryRelocation
{
    public static IEnumerable<string> KnownFiles(LibraryData d) => d.Meta.Keys.Concat(d.History.Keys).Concat(d.Bookmarks.Keys)
        .Concat(d.Playback.Keys).Concat(d.LyricFiles.Keys).Concat(d.LyricOffsets.Keys).Concat(d.Subtitles.Keys)
        .Concat(d.Playlists.SelectMany(p => p.Items)).Concat(d.LyricFiles.Values).Concat(d.Subtitles.Values.SelectMany(s => s));

    public static string? Under(string path, string oldRoot, string newRoot)
    {
        if (path.Contains("://")) return null;
        var root = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (full.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(newRoot);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.Combine(Path.GetFullPath(newRoot), full[root.Length..]) : null;
    }

    public static Dictionary<string, string> Find(IEnumerable<string> paths, string oldRoot, string newRoot, CancellationToken cancel)
    {
        if (!Path.IsPathFullyQualified(oldRoot) || !Path.IsPathFullyQualified(newRoot)) throw new ArgumentException("Enter an absolute folder path.");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancel.ThrowIfCancellationRequested();
            if (Under(path, oldRoot, newRoot) is { } next && !File.Exists(path) && File.Exists(next)) map[path] = next;
        }
        return map;
    }

    public static LibraryData Apply(LibraryData source, IReadOnlyDictionary<string, string> map, string oldRoot, string newRoot)
    {
        var data = Library.ParseProfile(JsonSerializer.Serialize(source));
        string FilePath(string path) => map.GetValueOrDefault(path) ?? path;
        string Folder(string path) => Under(path, oldRoot, newRoot) is { } next && Directory.Exists(next) ? next : path;
        Dictionary<string, T> Keys<T>(Dictionary<string, T> dictionary, Func<string, string> rename)
        {
            var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in dictionary)
                if (!result.TryAdd(rename(pair.Key), pair.Value)) throw new IOException("Relocation conflicts with an existing library entry.");
            return result;
        }
        data.History = Keys(data.History, FilePath); data.Meta = Keys(data.Meta, FilePath);
        data.Bookmarks = Keys(data.Bookmarks, FilePath); data.Playback = Keys(data.Playback, FilePath);
        data.LyricOffsets = Keys(data.LyricOffsets, FilePath);
        data.LyricFiles = Keys(data.LyricFiles, FilePath).ToDictionary(p => p.Key, p => FilePath(p.Value), StringComparer.OrdinalIgnoreCase);
        data.Subtitles = Keys(data.Subtitles, FilePath).ToDictionary(p => p.Key, p => p.Value.Select(FilePath).ToList(), StringComparer.OrdinalIgnoreCase);
        data.IntroEnds = Keys(data.IntroEnds, Folder); data.SeriesTracks = Keys(data.SeriesTracks, Folder);
        data.MusicFolders = data.MusicFolders.Select(Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        data.VideoFolders = data.VideoFolders.Select(Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        data.Pinned = data.Pinned.Select(Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var playlist in data.Playlists) { playlist.Items = playlist.Items.Select(FilePath).ToList(); if (playlist.Thumb != null) playlist.Thumb = FilePath(playlist.Thumb); }
        return data;
    }
}
