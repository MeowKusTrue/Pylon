using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// Real waveforms for the music seek bar. libVLC transcodes the track to 8 kHz mono WAV (2 kHz past an hour: audiobooks;
/// stream output runs faster than real time), we reduce it to <see cref="Bins"/> loudness values and cache them on disk.
/// </summary>
public static class Waveform
{
    public const int Bins = 400;
    static readonly string CacheDir = Path.Combine(Library.Dir, "waves");
    static readonly ConcurrentDictionary<string, byte[]> Memory = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, double> Loud = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The track's average loudness (RMS, dBFS) once it has been analyzed, for "Replay gain: Measured".</summary>
    public static double? LoudnessOf(string path) => Loud.TryGetValue(path, out var db) ? db : null;
    static readonly SemaphoreSlim Gate = new(1);
    static LibVLC? _vlc;
    static string? _wanted;

    /// <summary>0..255 per bin, or null when the file can't be decoded. Only the latest request is worked on.</summary>
    public static async Task<byte[]?> GetAsync(string path)
    {
        if (Memory.TryGetValue(path, out var known)) return known;
        _wanted = path;
        await Gate.WaitAsync();
        try
        {
            if (_wanted != path) return null; // the user already moved on to another track
            return await Task.Run(() => Load(path));
        }
        finally { Gate.Release(); }
    }

    static byte[]? Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{path.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..20];
        var cached = Path.Combine(CacheDir, key + ".bin");
        var level = Path.ChangeExtension(cached, ".db"); // waveforms cached before 1.6 have no loudness: analyzed again once
        if (File.Exists(cached) && File.ReadAllBytes(cached) is { Length: Bins } c
            && double.TryParse(File.Exists(level) ? File.ReadAllText(level) : "", System.Globalization.CultureInfo.InvariantCulture, out var known))
        {
            Loud[path] = known;
            return Memory[path] = c;
        }

        var wav = Path.Combine(Path.GetTempPath(), $"pylon-wave-{Guid.NewGuid():N}.wav");
        try
        {
            if (!Decode(path, wav)) return null;
            byte[]? peaks;
            double db;
            using (var f = File.OpenRead(wav)) (peaks, db) = Reduce(f);
            if (peaks == null) return null;
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(cached, peaks);
            File.WriteAllText(level, db.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            Loud[path] = db;
            return Memory[path] = peaks;
        }
        catch (IOException) { return null; }
        finally { try { File.Delete(wav); } catch (IOException) { } }
    }

    static bool Decode(string path, string wav)
    {
        _vlc ??= new LibVLC("--quiet", "--no-video", "--no-sout-video", "--no-spu");
        using var media = new Media(_vlc, path, FromType.FromPath);
        media.Parse(MediaParseOptions.ParseLocal).Wait(5000);
        var seconds = media.Duration / 1000.0;
        var rate = seconds > 3600 ? 2000 : 8000; // a loudness outline needs no more; keeps a 10-hour book at 144 MB of temp WAV
        media.AddOption($":sout=#transcode{{acodec=s16l,channels=1,samplerate={rate}}}:std{{access=file,mux=wav,dst='{wav.Replace("'", "")}'}}");
        media.AddOption(":sout-keep");
        using var player = new MediaPlayer(media);
        var done = new ManualResetEventSlim();
        var ok = true;
        player.EndReached += (_, _) => done.Set();
        player.EncounteredError += (_, _) => { ok = false; done.Set(); };
        if (!player.Play()) return false;
        var finished = done.Wait(TimeSpan.FromSeconds(Math.Max(120, seconds / 40))); // decoding runs well over 40× real time
        player.Stop(); // not on a libVLC event thread, so this is safe
        return ok && finished && File.Exists(wav);
    }

    /// <summary>RMS per bin from a 16-bit mono WAV (read as a stream: long books make big files), normalized to the loudest bin.</summary>
    static (byte[]? Peaks, double Db) Reduce(Stream wav)
    {
        var head = new byte[4096];
        var got = wav.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var start = DataOffset(head.AsSpan(0, got));
        if (start < 0) return (null, 0);
        var samples = (wav.Length - start) / 2; // an unfinished WAV may carry a bogus size: trust the file length
        if (samples < Bins * 2) return (null, 0);
        wav.Position = start;
        using var reader = new BinaryReader(new BufferedStream(wav, 1 << 16));
        var rms = new double[Bins];
        for (var b = 0; b < Bins; b++)
        {
            long from = samples * b / Bins, to = samples * (b + 1) / Bins;
            double sum = 0;
            for (var i = from; i < to; i++)
            {
                double s = reader.ReadInt16();
                sum += s * s;
            }
            rms[b] = Math.Sqrt(sum / Math.Max(1, to - from));
        }
        var db = 20 * Math.Log10(Math.Sqrt(rms.Average(r => r * r)) / 32768 + 1e-9);
        var max = rms.Max();
        if (max <= 0) return (null, 0);
        return (rms.Select(r => (byte)Math.Round(255 * Math.Pow(r / max, 0.7))).ToArray(), db); // slight lift so quiet passages stay visible
    }

    /// <summary>Where the samples of the "data" chunk start, -1 if the header doesn't show one.</summary>
    internal static int DataOffset(ReadOnlySpan<byte> head)
    {
        for (var p = 12; p + 8 <= head.Length;)
        {
            var size = BitConverter.ToInt32(head.Slice(p + 4, 4));
            if (Encoding.ASCII.GetString(head.Slice(p, 4)) == "data") return p + 8;
            p += 8 + Math.Max(0, size) + (size & 1);
        }
        return -1;
    }
}
