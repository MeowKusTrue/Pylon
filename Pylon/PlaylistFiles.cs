using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Pylon;

public static class PlaylistFiles
{
    public static List<string> Read(string file)
    {
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new IOException("Playlist is too large.");
        IEnumerable<string> entries;
        if (Path.GetExtension(file).Equals(".xspf", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            entries = XDocument.Load(reader).Descendants().Where(e => e.Name.LocalName == "location").Select(e => e.Value).ToArray();
        }
        else
        {
            var lines = File.ReadAllLines(file);
            if (lines.Any(l => l.StartsWith("#EXT-X-", StringComparison.Ordinal))) throw new IOException("HLS playlists must be opened as a stream.");
            entries = lines.Where(l => !l.TrimStart().StartsWith('#'));
        }
        var root = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var encoded = Path.GetExtension(file).Equals(".xspf", StringComparison.OrdinalIgnoreCase);
        var result = entries.Select(e => Resolve(root, e.Trim(), encoded)).Where(e => e != null).Cast<string>().Take(100001).ToList();
        if (result.Count > 100000) throw new IOException("Playlist contains too many entries.");
        return result;
    }

    static string? Resolve(string root, string entry, bool encoded)
    {
        if (entry.Length == 0) return null;
        if (Uri.TryCreate(entry, UriKind.Absolute, out var uri) && entry.Contains("://"))
            return uri.IsFile ? uri.LocalPath : uri.Scheme is "http" or "https" or "rtsp" or "rtmp" or "mms" or "udp" or "rtp" or "smb" ? entry : null;
        return Path.GetFullPath(Path.Combine(root, encoded ? Uri.UnescapeDataString(entry) : entry));
    }

    public static void Write(string file, IEnumerable<string> items, bool relative = true)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(file))!;
        string Entry(string path) => path.Contains("://") ? path : relative ? Path.GetRelativePath(root, path) : Path.GetFullPath(path);
        var paths = items.Select(Entry).ToArray();
        var xspf = Path.GetExtension(file).Equals(".xspf", StringComparison.OrdinalIgnoreCase);
        XNamespace ns = "http://xspf.org/ns/0/";
        var text = xspf ? new XDocument(new XElement(ns + "playlist", new XAttribute("version", "1"),
            new XElement(ns + "trackList", paths.Select(p => new XElement(ns + "track", new XElement(ns + "location",
                p.Contains("://") ? p : Path.IsPathFullyQualified(p) ? new Uri(p).AbsoluteUri
                    : string.Join('/', p.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString)))))))).ToString()
            : "#EXTM3U\n" + string.Join('\n', paths.Where(p => !p.Contains('\n') && !p.Contains('\r'))
                .Select(p => p.StartsWith('#') || p.Length > 0 && char.IsWhiteSpace(p[0]) ? ".\\" + p : p));
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
