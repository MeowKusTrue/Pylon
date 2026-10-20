using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Pylon;

/// <summary>Text subtitles (SRT, WebVTT, ASS / SSA): cues for the second subtitle and auto-sync, and a time-shifted copy.</summary>
public static class SubText
{
    public record Cue(double Start, double End, string Text);

    public static readonly string[] Extensions = [".srt", ".vtt", ".ass", ".ssa"];

    static readonly Regex SrtTime = new(@"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})", RegexOptions.Compiled);
    static readonly Regex SrtLine = new(@"(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})", RegexOptions.Compiled);
    static readonly Regex VttShort = new(@"(?<![\d:])(\d{2}):(\d{2})\.(\d{3})(?=\s*-->|\s*$)", RegexOptions.Compiled); // "mm:ss.mmm" without hours
    static readonly Regex AssTime = new(@"(\d):(\d{2}):(\d{2})\.(\d{2})", RegexOptions.Compiled);
    static readonly Regex Tags = new(@"<[^>]+>|\{[^}]*\}", RegexOptions.Compiled);

    /// <summary>Reads a subtitle file (UTF-8, UTF-16 or the system's legacy code page), sorted by start; empty when unreadable.</summary>
    public static List<Cue> Load(string path)
    {
        string text;
        try { text = Read(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        return Parse(text, Path.GetExtension(path).ToLowerInvariant());
    }

    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) // not UTF-8: old Russian / Western subtitles are usually in the ANSI code page
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }

    public static List<Cue> Parse(string text, string ext)
    {
        var cues = new List<Cue>();
        if (ext is ".ass" or ".ssa")
        {
            int startCol = 1, endCol = 2, textCol = 9;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    var cols = line[7..].Split(',').Select(c => c.Trim().ToLowerInvariant()).ToList();
                    (startCol, endCol, textCol) = (cols.IndexOf("start"), cols.IndexOf("end"), cols.IndexOf("text"));
                }
                else if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) && startCol >= 0 && textCol >= 0)
                {
                    var parts = line[9..].Split(',', textCol + 1);
                    if (parts.Length <= textCol) continue;
                    var body = Tags.Replace(parts[textCol], "").Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ").Trim();
                    if (Time(parts[startCol]) is { } a && Time(parts[endCol]) is { } b && body.Length > 0) cues.Add(new Cue(a, b, body));
                }
            }
        }
        else
        {
            text = VttShort.Replace(text.Replace("\r", ""), "00:$1:$2.$3");
            foreach (var block in Regex.Split(text, @"\n\s*\n"))
            {
                var lines = block.Split('\n').Where(l => l.Length > 0).ToList();
                var at = lines.FindIndex(l => SrtLine.IsMatch(l));
                if (at < 0) continue;
                var m = SrtLine.Match(lines[at]);
                var body = Tags.Replace(string.Join("\n", lines.Skip(at + 1)), "").Trim();
                if (Time(m.Groups[1].Value) is { } a && Time(m.Groups[2].Value) is { } b && body.Length > 0) cues.Add(new Cue(a, b, body));
            }
        }
        cues.Sort((x, y) => x.Start.CompareTo(y.Start));
        return cues;
    }

    static double? Time(string s)
    {
        var m = SrtTime.Match(s);
        if (m.Success) return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) + int.Parse(m.Groups[4].Value.PadRight(3, '0')) / 1000.0;
        m = AssTime.Match(s);
        if (m.Success) return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) + int.Parse(m.Groups[4].Value) / 100.0;
        return null;
    }

    /// <summary>
    /// A copy with every time t written as t × scale + offset (seconds), in the same format; everything else as it was.
    /// </summary>
    public static string Shift(string text, string ext, double offset, double scale)
    {
        string Fix(double t) => TimeSpan.FromSeconds(Math.Max(0, t * scale + offset)).ToString(@"hh\:mm\:ss");
        if (ext is ".ass" or ".ssa")
            return AssTime.Replace(text, m =>
            {
                var t = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) + int.Parse(m.Groups[4].Value) / 100.0;
                var ts = TimeSpan.FromSeconds(Math.Max(0, t * scale + offset));
                return $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
            });
        var sep = ext == ".vtt" ? "." : ",";
        return SrtTime.Replace(text, m =>
        {
            var t = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value) + int.Parse(m.Groups[4].Value.PadRight(3, '0')) / 1000.0;
            var ts = TimeSpan.FromSeconds(Math.Max(0, t * scale + offset));
            return $"{Fix(t)}{sep}{ts.Milliseconds:000}";
        });
    }

    /// <summary>Subtitle files that belong to a video: the ones added in Pylon, then "Name*.srt" etc. beside it.</summary>
    public static List<string> Beside(string video)
    {
        var found = new List<string>(Library.Data.Subtitles.GetValueOrDefault(video) ?? []);
        try
        {
            var dir = Path.GetDirectoryName(video)!;
            var name = Path.GetFileNameWithoutExtension(video);
            found.AddRange(Directory.EnumerateFiles(dir, name + "*")
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return [.. found.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
