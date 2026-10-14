using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Pylon;

/// <summary>A sung unit (word or syllable). <see cref="Joined"/>: no space before the next one (syllables of one word).</summary>
public record Syllable(double Start, double End, string Text, bool Joined);

/// <summary>One lyric line; <see cref="Words"/> is empty for line-synced lyrics. <see cref="Opposite"/>: second singer (right-aligned).</summary>
public record LyricLine(double Start, double End, string Text, Syllable[] Words, bool Opposite, Syllable[] Background);

public record LyricsDoc(LyricLine[] Lines, bool Synced, string Source);

/// <summary>Finds lyrics for a track: a .ttml / .lrc next to the file, then the cache, then LRCLIB (if allowed).</summary>
public static class Lyrics
{
    static readonly string CacheDir = Path.Combine(Library.Dir, "lyrics");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10), DefaultRequestHeaders = { { "User-Agent", "Pylon (https://github.com/MeowKusTrue/Pylon)" } } };

    public static async Task<LyricsDoc?> FindAsync(string path, string title, string? artist, string? album, double duration)
    {
        if (!path.Contains("://"))
        {
            // sidecar files win: TTML from spicy-translate / Apple, LRC from anywhere
            foreach (var ext in new[] { ".ttml", ".lrc" })
            {
                var side = Path.ChangeExtension(path, ext);
                if (File.Exists(side) && Parse(await File.ReadAllTextAsync(side), Path.GetFileName(side)) is { } doc) return doc;
            }
        }
        if (string.IsNullOrWhiteSpace(title)) return null;
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{artist}|{title}|{album}|{Math.Round(duration)}".ToLowerInvariant())))[..20];
        var cached = Path.Combine(CacheDir, key + ".lrc");
        if (File.Exists(cached)) return Parse(await File.ReadAllTextAsync(cached), "LRCLIB");
        if (!Settings.Bool("lyrics.online") || string.IsNullOrWhiteSpace(artist)) return null;
        try
        {
            var lrc = await FromLrclib(title, artist!, album, duration);
            if (lrc == null) return null;
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(cached, lrc);
            return Parse(lrc, "LRCLIB");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException) { return null; }
    }

    /// <summary>Exact match first (LRCLIB tolerates ±2 s), then search: the synced version closest in length wins.</summary>
    static async Task<string?> FromLrclib(string title, string artist, string? album, double duration)
    {
        static string Q(params (string k, string? v)[] p) => string.Join("&", p.Where(x => !string.IsNullOrEmpty(x.v)).Select(x => $"{x.k}={Uri.EscapeDataString(x.v!)}"));
        using (var r = await Http.GetAsync("https://lrclib.net/api/get?" + Q(("track_name", title), ("artist_name", artist), ("album_name", album), ("duration", duration > 0 ? Math.Round(duration).ToString(CultureInfo.InvariantCulture) : null))))
            if (r.IsSuccessStatusCode && JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement is var hit && Text(hit, "syncedLyrics") is { } synced)
                return synced;
        using var s = await Http.GetAsync("https://lrclib.net/api/search?" + Q(("track_name", title), ("artist_name", artist)));
        if (!s.IsSuccessStatusCode) return null;
        var list = JsonDocument.Parse(await s.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
        double Off(JsonElement e) => e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? Math.Abs(d.GetDouble() - duration) : 999;
        var best = list.Where(e => Text(e, "syncedLyrics") != null).OrderBy(Off).FirstOrDefault();
        if (best.ValueKind != JsonValueKind.Undefined && (duration <= 0 || Off(best) <= 5)) return Text(best, "syncedLyrics");
        var plain = list.OrderBy(Off).Select(e => Text(e, "plainLyrics")).FirstOrDefault(t => t != null);
        return plain; // unsynced text still reads fine as a static page
    }

    static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } t ? t : null;

    public static LyricsDoc? Parse(string text, string source) =>
        text.TrimStart().StartsWith('<') ? ParseTtml(text, source) : ParseLrc(text, source);

    // ───────── TTML (Apple Music / Spicy Lyrics / spicy-translate) ─────────

    public static LyricsDoc? ParseTtml(string xml, string source)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return null; }
        XNamespace ttm = "http://www.w3.org/ns/ttml#metadata";
        var lines = new List<LyricLine>();
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            var start = Time((string?)p.Attribute("begin"));
            var end = Time((string?)p.Attribute("end"));
            var lead = new List<Syllable>();
            var bg = new List<Syllable>();
            CollectSpans(p, lead, bg, ttm, inBackground: false);
            var words = lead.ToArray();
            var lineText = words.Length > 0 ? Join(words) : p.Value.Trim();
            if (lineText.Length == 0) continue;
            if (start < 0) start = words.Length > 0 ? words[0].Start : 0;
            if (end <= start) end = words.Length > 0 ? words[^1].End : start + 3;
            var opposite = (string?)p.Attribute(ttm + "agent") is { } agent && agent != "v1" && agent != "v1000";
            lines.Add(new LyricLine(start, end, lineText, words, opposite, [.. bg]));
        }
        var timed = lines.Any(l => l.End > 0);
        return lines.Count == 0 ? null : new LyricsDoc([.. lines.OrderBy(l => l.Start)], timed, source);
    }

    static void CollectSpans(XElement parent, List<Syllable> lead, List<Syllable> bg, XNamespace ttm, bool inBackground)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is not XElement span || span.Name.LocalName != "span") continue;
            var isBg = inBackground || (string?)span.Attribute(ttm + "role") == "x-bg";
            if (span.Elements().Any(e => e.Name.LocalName == "span")) { CollectSpans(span, lead, bg, ttm, isBg); continue; }
            var text = span.Value.Trim().Trim('(', ')');
            if (text.Length == 0) continue;
            // no whitespace right after this span = the next span continues the same word
            var next = span.NextNode;
            var joined = next is XElement || (next is XText nt && nt.Value.Length == 0);
            var target = isBg ? bg : lead;
            target.Add(new Syllable(Time((string?)span.Attribute("begin")), Time((string?)span.Attribute("end")), text, joined));
        }
        for (var list = 0; list < 2; list++) // the last unit of a part always ends a word
        {
            var l = list == 0 ? lead : bg;
            if (l.Count > 0) l[^1] = l[^1] with { Joined = false };
        }
    }

    static string Join(IEnumerable<Syllable> s) => string.Concat(s.Select(x => x.Joined ? x.Text : x.Text + " ")).Trim();

    /// <summary>"1:02:03.456", "02:03.456", "3.456", "3.456s" or "500ms" → seconds (-1 when missing).</summary>
    static double Time(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return -1;
        t = t.Trim();
        if (t.EndsWith("ms")) return double.TryParse(t[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms / 1000 : -1;
        if (t.EndsWith('s')) t = t[..^1];
        double total = 0;
        foreach (var part in t.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return -1;
            total = total * 60 + v;
        }
        return total;
    }

    // ───────── LRC, including "enhanced" LRC with <mm:ss.xx> word tags ─────────

    static readonly Regex LineTag = new(@"\[(\d+):(\d+(?:[.:]\d+)?)\]", RegexOptions.Compiled);
    static readonly Regex WordTag = new(@"<(\d+):(\d+(?:[.:]\d+)?)>", RegexOptions.Compiled);
    static readonly Regex OffsetTag = new(@"^\[offset:\s*([+-]?\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    static double Clock(Match m) => int.Parse(m.Groups[1].Value) * 60 + double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);

    public static LyricsDoc? ParseLrc(string text, string source)
    {
        var offset = OffsetTag.Match(text) is { Success: true } o ? int.Parse(o.Groups[1].Value) / 1000.0 : 0;
        var raw = new List<(double Start, string Body)>();
        var plain = new List<string>();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var tags = LineTag.Matches(line);
            if (tags.Count == 0)
            {
                if (!line.TrimStart().StartsWith('[') && line.Trim().Length > 0) plain.Add(line.Trim());
                continue;
            }
            var body = line[(tags[^1].Index + tags[^1].Length)..];
            foreach (Match tag in tags) raw.Add((Clock(tag) - offset, body));
        }
        if (raw.Count == 0)
            return plain.Count == 0 ? null : new LyricsDoc([.. plain.Select(p => new LyricLine(0, 0, p, [], false, []))], false, source);

        raw.Sort((a, b) => a.Start.CompareTo(b.Start));
        var lines = new List<LyricLine>();
        for (var i = 0; i < raw.Count; i++)
        {
            var (start, body) = raw[i];
            var end = i + 1 < raw.Count ? raw[i + 1].Start : start + 5;
            var words = WordTimes(body, start, end, offset);
            var lineText = WordTag.Replace(body, "").Trim();
            if (lineText.Length == 0) { if (lines.Count > 0) lines[^1] = lines[^1] with { End = Math.Min(lines[^1].End, start) }; continue; } // empty line = end of the previous one
            lines.Add(new LyricLine(start, end, lineText, words, false, []));
        }
        return new LyricsDoc([.. lines], true, source);
    }

    static Syllable[] WordTimes(string body, double lineStart, double lineEnd, double offset)
    {
        var tags = WordTag.Matches(body);
        if (tags.Count == 0) return [];
        var words = new List<Syllable>();
        for (var i = 0; i < tags.Count; i++)
        {
            var from = tags[i].Index + tags[i].Length;
            var to = i + 1 < tags.Count ? tags[i + 1].Index : body.Length;
            var piece = body[from..to];
            if (piece.Trim().Length == 0) continue;
            var start = Clock(tags[i]) - offset;
            var end = i + 1 < tags.Count ? Clock(tags[i + 1]) - offset : lineEnd;
            words.Add(new Syllable(start, Math.Max(start + 0.05, end), piece.Trim(), !piece.EndsWith(' ') && i + 1 < tags.Count && !body[to..].StartsWith(' ')));
        }
        return [.. words];
    }
}
