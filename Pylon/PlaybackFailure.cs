using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Pylon;

public record PlaybackFailure(string Reason, string Details);

/// <summary>A short, bounded diagnostic trail; saving the full VLC log is optional.</summary>
public sealed class PlaybackDiagnostics
{
    readonly Queue<string> _lines = [];

    public void Clear() { lock (_lines) _lines.Clear(); }
    public void Add(string module, string message)
    {
        var line = Redact($"{module}: {message}");
        lock (_lines)
        {
            if (_lines.Count == 32) _lines.Dequeue();
            _lines.Enqueue(line.Length > 2000 ? line[..2000] : line);
        }
    }
    public string[] Snapshot() { lock (_lines) return [.. _lines]; }

    public static string Redact(string text) => Regex.Replace(text, @"https?://[^\s""<>]+", m =>
        Uri.TryCreate(m.Value, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}/<redacted>" : "<redacted URL>");

    public static PlaybackFailure Inspect(string path, IEnumerable<string> messages)
    {
        var lines = messages.ToArray();
        var detail = new StringBuilder($"Pylon {Updater.Current.ToString(3)}\nMedia: {Redact(path)}\n");
        string? reason = null;
        if (!path.Contains("://"))
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                detail.AppendLine($"Size: {file.Length} bytes");
                if (file.Length == 0) reason = "The file is empty.";
                else
                {
                    var head = new byte[512];
                    var read = file.Read(head);
                    var text = Encoding.UTF8.GetString(head, 0, read).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                    if (text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                        reason = "This file contains a web page, not playable media. Download the original media file.";
                    detail.AppendLine("Header: " + Convert.ToHexString(head.AsSpan(0, Math.Min(read, 16))));
                }
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            { reason = "The file was moved or deleted, or its drive is unavailable."; detail.AppendLine(e.Message); }
            catch (UnauthorizedAccessException e)
            { reason = "Access to the file was denied."; detail.AppendLine(e.Message); }
            catch (IOException e)
            { reason = "The file could not be read. Check its drive and whether another program has locked it."; detail.AppendLine(e.Message); }
        }
        reason ??= Classify(lines);
        detail.AppendLine("\nRecent engine messages:");
        detail.AppendLine(lines.Length == 0 ? "The engine did not provide additional details." : string.Join("\n", lines.Select(Redact)));
        return new(reason, detail.ToString());
    }

    public static string Classify(IEnumerable<string> messages)
    {
        var log = string.Join("\n", messages).ToLowerInvariant();
        if (log.Contains("401 unauthorized") || log.Contains("403 forbidden")) return "The server denied access. Sign-in or a new playback link may be required.";
        if (log.Contains("404 not found")) return "The server could not find this media. Its link may have expired.";
        if (log.Contains("no suitable decoder") || log.Contains("codec") && log.Contains("not supported")) return "The playback engine has no suitable decoder for a track in this file.";
        if (log.Contains("no suitable demux") || log.Contains("cannot peek") || log.Contains("cannot find moov")) return "The engine could not read the media container. The file may be incomplete, damaged or unsupported.";
        if (log.Contains("connection failed") || log.Contains("connection timed out") || log.Contains("cannot resolve")) return "The media server could not be reached. Check the connection and address.";
        return "The engine could not open this media. The exact cause is unknown; see the technical details.";
    }
}
