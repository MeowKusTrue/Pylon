using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// Real waveforms for the music seek bar. libVLC transcodes the track to 8 kHz mono WAV (stream output runs
/// faster than real time), we reduce it to <see cref="Bins"/> loudness values and cache them on disk.
/// </summary>
public static class Waveform
{
    public const int Bins = 400;
    const int Rate = 8000;
    static readonly string CacheDir = Path.Combine(Library.Dir, "waves");
    static readonly ConcurrentDictionary<string, byte[]> Memory = new(StringComparer.OrdinalIgnoreCase);
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
        if (File.Exists(cached) && File.ReadAllBytes(cached) is { Length: Bins } c) return Memory[path] = c;

        var wav = Path.Combine(Path.GetTempPath(), $"pylon-wave-{Guid.NewGuid():N}.wav");
        try
        {
            if (!Decode(path, wav)) return null;
            var peaks = Reduce(File.ReadAllBytes(wav));
            if (peaks == null) return null;
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(cached, peaks);
            return Memory[path] = peaks;
        }
        catch (IOException) { return null; }
        finally { try { File.Delete(wav); } catch (IOException) { } }
    }

    static bool Decode(string path, string wav)
    {
        _vlc ??= new LibVLC("--quiet", "--no-video", "--no-sout-video", "--no-spu");
        using var media = new Media(_vlc, path, FromType.FromPath);
        media.AddOption($":sout=#transcode{{acodec=s16l,channels=1,samplerate={Rate}}}:std{{access=file,mux=wav,dst='{wav.Replace("'", "")}'}}");
        media.AddOption(":sout-keep");
        using var player = new MediaPlayer(media);
        var done = new ManualResetEventSlim();
        var ok = true;
        player.EndReached += (_, _) => done.Set();
        player.EncounteredError += (_, _) => { ok = false; done.Set(); };
        if (!player.Play()) return false;
        var finished = done.Wait(TimeSpan.FromMinutes(2)); // ponytail: very long files (audiobooks) just keep the decorative bars
        player.Stop(); // not on a libVLC event thread, so this is safe
        return ok && finished && File.Exists(wav);
    }

    /// <summary>RMS per bin from a 16-bit mono WAV, normalized to the loudest bin.</summary>
    static byte[]? Reduce(byte[] wav)
    {
        var data = FindChunk(wav, "data");
        if (data.Length < Bins * 4) return null;
        var samples = data.Length / 2;
        var rms = new double[Bins];
        for (var b = 0; b < Bins; b++)
        {
            long from = (long)samples * b / Bins, to = (long)samples * (b + 1) / Bins;
            double sum = 0;
            for (var i = from; i < to; i++)
            {
                double s = BitConverter.ToInt16(data.Slice((int)i * 2, 2));
                sum += s * s;
            }
            rms[b] = Math.Sqrt(sum / Math.Max(1, to - from));
        }
        var max = rms.Max();
        if (max <= 0) return null;
        return rms.Select(r => (byte)Math.Round(255 * Math.Pow(r / max, 0.7))).ToArray(); // slight lift so quiet passages stay visible
    }

    static ReadOnlySpan<byte> FindChunk(byte[] wav, string id)
    {
        for (var p = 12; p + 8 <= wav.Length;)
        {
            var size = BitConverter.ToInt32(wav, p + 4);
            if (Encoding.ASCII.GetString(wav, p, 4) == id)
                return wav.AsSpan(p + 8, size <= 0 || size > wav.Length - p - 8 ? wav.Length - p - 8 : size); // an unfinished WAV may carry a bogus size
            p += 8 + Math.Max(0, size) + (size & 1);
        }
        return default;
    }
}
