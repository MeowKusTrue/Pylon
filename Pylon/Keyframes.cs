using System.IO;

namespace Pylon;

/// <summary>
/// Seeking in containers without an index of keyframes: MPEG-TS / M2TS (TV and IPTV recordings, camcorders) and
/// MPEG-PS (.mpg, DVD .vob). A precise seek there starts decoding between keyframes and the picture waits for the next
/// one (0.6–1.8 s measured with 2 s GOPs). Like MPC-HC by default, Pylon seeks to a keyframe instead, found by reading a
/// few megabytes around the target (5–50 ms): 0.16–0.2 s in TS / M2TS, 0.2–0.5 s in MPEG-PS. How the seek is made is in
/// MainWindow.SeekToKeyframe.
/// </summary>
public static class Keyframes
{
    static readonly string[] TsExt = [".ts", ".m2ts", ".mts", ".m2t"], PsExt = [".mpg", ".mpeg", ".vob"];
    const long Wrap = 1L << 33;      // the 90 kHz timestamp counter wraps after ~26.5 h
    const long MaxMove = 3 * 90000;  // a keyframe further than 3 s from the target: the seek stays precise

    public readonly record struct Pes(long Pts, bool Video, bool Key, long Offset);

    /// <summary>A keyframe: its time in ms of the media's own clock, and where its PES starts in the file.</summary>
    public readonly record struct Hit(long Ms, long Offset);

    public static bool IsTs(string path) => TsExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool Handles(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return TsExt.Contains(ext) || PsExt.Contains(ext);
    }

    /// <summary>
    /// The keyframe at or after the target (forward) or at or before it (its time rounded up, so a seek there lands on
    /// it); null when there is none within 3 s or the file can't be read.
    /// </summary>
    public static Hit? Near(string path, long targetMs, bool forward, long lengthMs)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.RandomAccess);
            if (Scanner(path, f) is not { } scan) return null;
            // media time 0 is the earliest timestamp at the start (FFmpeg's start_time)
            long? first = null;
            foreach (var p in scan(0, Math.Min(f.Length, 4 << 20)))
                if (first == null || Rel(p.Pts, first.Value) > Wrap / 2) first = p.Pts;
            if (first is not { } start) return null;
            long T(long pts) => Rel(pts, start);
            Hit At(Pes p) => new((T(p.Pts) + 89) / 90, p.Offset);
            var target = targetMs * 90;

            // the last offset whose first video timestamp is at or before the target
            long lo = 0, hi = f.Length;
            while (hi - lo > 1 << 20)
            {
                var mid = lo + (hi - lo) / 2;
                var v = scan(mid, Math.Min(f.Length, mid + (2 << 20))).FirstOrDefault(p => p.Video);
                if (v.Video && T(v.Pts) <= target) lo = mid; else hi = mid;
            }

            if (forward)
            {
                foreach (var p in scan(lo, f.Length))
                {
                    if (!p.Video) continue;
                    var t = T(p.Pts);
                    if (t > target + MaxMove) return null;
                    if (p.Key && t >= target) return At(p);
                }
                return null;
            }
            // backwards a second's worth of bytes at a time
            var chunk = Math.Clamp(lengthMs > 0 ? f.Length * 1000 / lengthMs : 4 << 20, 1 << 20, 32 << 20);
            for (long end = Math.Min(f.Length, lo + chunk); end > 0; end -= chunk)
            {
                var from = Math.Max(0, end - chunk);
                Pes? best = null;
                long? earliest = null;
                foreach (var p in scan(from, end))
                {
                    if (!p.Video) continue;
                    var t = T(p.Pts);
                    if (t <= target && (earliest == null || t < earliest)) earliest = t;
                    if (p.Key && t <= target && (best == null || t > T(best.Value.Pts))) best = p;
                }
                if (best is { } b) return target - T(b.Pts) <= MaxMove ? At(b) : null;
                if (earliest is { } e && target - e > MaxMove) return null;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>How far b is after a, on the wrapping 33-bit clock.</summary>
    static long Rel(long b, long a) => ((b - a) % Wrap + Wrap) % Wrap;

    /// <summary>A reader of PES starts (timestamp, video, keyframe) whose headers begin in [from, to).</summary>
    static Func<long, long, IEnumerable<Pes>>? Scanner(string path, FileStream f)
    {
        if (PsExt.Contains(Path.GetExtension(path).ToLowerInvariant())) return (a, b) => Ps(f, a, b);
        var head = new byte[8192];
        var n = f.ReadAtLeast(head, head.Length, false);
        foreach (var (size, lead) in new[] { (188, 0), (192, 4) }) // M2TS puts a 4-byte arrival stamp before every packet
            for (var i = 0; i + lead + 3 * size < n && i < 1024; i++)
                if (head[i + lead] == 0x47 && head[i + lead + size] == 0x47 && head[i + lead + 2 * size] == 0x47 && head[i + lead + 3 * size] == 0x47)
                    return (a, b) => Ts(f, size, lead, a, b);
        return null;
    }

    static IEnumerable<Pes> Ts(FileStream f, int size, int lead, long from, long to)
    {
        var buf = new byte[size * 4096];
        for (var pos = from; pos < to;)
        {
            f.Position = pos;
            var n = f.ReadAtLeast(buf, buf.Length, false);
            if (n < size * 3) yield break;
            var i = 0; // the first packet in this block: three sync bytes a packet apart
            while (i + lead + 2 * size < n && !(buf[i + lead] == 0x47 && buf[i + lead + size] == 0x47 && buf[i + lead + 2 * size] == 0x47)) i++;
            for (; i + size <= n && pos + i < to; i += size)
            {
                var p = i + lead;
                if (buf[p] != 0x47) break; // lost sync (a damaged recording): the next block finds it again
                if ((buf[p + 1] & 0x40) == 0) continue; // not the start of a PES
                var afc = (buf[p + 3] >> 4) & 3;
                var at = p + 4;
                var rai = false; // random_access_indicator: muxers mark keyframes with it
                if ((afc & 2) != 0) { int al = buf[at]; rai = al > 0 && (buf[at + 1] & 0x40) != 0; at += 1 + al; }
                if ((afc & 1) == 0 || at + 14 > p + 188 || buf[at] != 0 || buf[at + 1] != 0 || buf[at + 2] != 1) continue;
                if ((buf[at + 6] & 0xC0) != 0x80 || (buf[at + 7] & 0x80) == 0) continue; // no timestamp
                var video = buf[at + 3] is >= 0xE0 and <= 0xEF;
                yield return new Pes(Pts(buf, at + 9), video, video && (rai || KeyStart(buf, at + 9 + buf[at + 8], p + 188)), pos + i);
            }
            pos += Math.Max(i, 1);
        }
    }

    static IEnumerable<Pes> Ps(FileStream f, long from, long to)
    {
        var buf = new byte[1 << 20];
        Pes? video = null; // the last video PES: whether it starts a GOP shows in its payload
        var mpeg = false;  // MPEG-1/2 video (picture start codes seen)
        for (var pos = from; pos < to;)
        {
            f.Position = pos;
            var n = f.ReadAtLeast(buf, buf.Length, false);
            if (n < 64) break;
            var i = 0;
            for (; i + 32 < n && pos + i < to; i++)
            {
                if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 1) continue;
                int id = buf[i + 3];
                if (id is >= 0xC0 and <= 0xEF or 0xBD)
                {
                    if (video is { } v) { yield return v; video = null; }
                    var pts = PesPts(buf, i, n);
                    if (id >= 0xE0) { if (pts is { } t) video = new Pes(t, true, false, pos + i); i += 5; } // the payload is read on: its start codes tell a keyframe
                    else
                    {
                        if (pts is { } t) yield return new Pes(t, false, false, pos + i);
                        i += 5 + (buf[i + 4] << 8 | buf[i + 5]); // audio: skipped whole, its data may look like start codes
                    }
                }
                else if (id is 0xBB or 0xBE or 0xBF) i += 5 + (buf[i + 4] << 8 | buf[i + 5]); // system header, padding, private 2
                else if (id is 0x00 or 0xB3 or 0xB5) { mpeg = true; if (id == 0xB3 && video is { } v) video = v with { Key = true }; } // 0xB5: extensions, in every picture
                else if (video is { } v && KeyCode(buf, i + 3, mpeg)) video = v with { Key = true };
            }
            pos += Math.Max(i, 1);
        }
        if (video is { } last) yield return last;
    }

    /// <summary>A PES header's PTS (MPEG-2, or MPEG-1 as on Video CDs).</summary>
    static long? PesPts(byte[] b, int i, int n)
    {
        var j = i + 6;
        if ((b[j] & 0xC0) == 0x80) return (b[j + 1] & 0x80) != 0 ? Pts(b, j + 3) : null;
        while (j < n - 6 && b[j] == 0xFF) j++; // MPEG-1 stuffing
        if ((b[j] & 0xC0) == 0x40) j += 2;     // buffer size
        return (b[j] & 0xE0) == 0x20 ? Pts(b, j) : null;
    }

    static long Pts(byte[] b, int j) =>
        (long)(b[j] >> 1 & 7) << 30 | (long)b[j + 1] << 22 | (long)(b[j + 2] >> 1) << 15 | (long)b[j + 3] << 7 | (long)(b[j + 4] >> 1);

    /// <summary>A start code in b[from..to) that only a keyframe's data has.</summary>
    static bool KeyStart(byte[] b, int from, int to)
    {
        var mpeg = false;
        for (var i = from; i + 4 < to; i++)
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1 && b[i + 3] is 0x00 or 0xB3 or 0xB5) { mpeg = true; break; }
        for (var i = from; i + 4 < to; i++)
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1 && KeyCode(b, i + 3, mpeg)) return true;
        return false;
    }

    /// <summary>
    /// The byte after a start code marks a keyframe: MPEG-1/2 sequence or GOP header (in MPEG video only those: its slice
    /// codes take the same values as H.264's); H.264 IDR slice or SPS; HEVC IDR, CRA, VPS or SPS (their second header
    /// byte is 0x01, which keeps them apart from H.264 bytes of the same value).
    /// </summary>
    static bool KeyCode(byte[] b, int i, bool mpeg) =>
        b[i] is 0xB3 or 0xB8 || !mpeg && ((b[i] & 0x80) == 0 && (b[i] & 0x60) != 0 && (b[i] & 0x1F) is 5 or 7
            || b[i + 1] == 0x01 && b[i] is 0x26 or 0x28 or 0x2A or 0x40 or 0x42);
}
