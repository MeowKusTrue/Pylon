using System.IO;
using System.Numerics;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// Automatic subtitle sync: where people speak (three 10-minute stretches of the sound, decoded to 8 kHz mono and
/// marked by voice-band energy) against when the subtitles show, matched by cross-correlation for a shift up to ±2 min
/// and the usual frame-rate mix-ups (23.976 ↔ 25, 24 ↔ 25). The result is a corrected copy of the subtitle file.
/// </summary>
public static class SubSync
{
    const int Rate = 8000, Hop = 160;            // 20 ms frames
    const double FrameSec = Hop / (double)Rate;  // 0.02 s
    const double MaxShift = 120;                 // seconds either way
    static readonly double[] Scales = [1, 25 / 23.976, 23.976 / 25, 25 / 24.0, 24 / 25.0, 24 / 23.976, 23.976 / 24];

    public record Result(double Offset, double Scale, double Confidence);

    /// <summary>The best shift and speed for the subtitle file against the video's speech; null when nothing stands out.</summary>
    public static async Task<Result?> FindAsync(string video, string subs, double duration, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var cues = SubText.Load(subs);
        if (cues.Count < 10 || duration < 60) return null;
        var frames = (int)(duration / FrameSec) + 1;
        var speech = new float[frames]; // +1 speech, -1 quiet, 0 not analysed
        var span = Math.Min(600, duration / 3);
        double[] starts = duration <= 1800 ? [0] : [0, duration / 2 - span / 2, duration - span];
        if (duration <= 1800) span = duration;
        for (var k = 0; k < starts.Length; k++)
        {
            cancel.ThrowIfCancellationRequested();
            var pcm = await Task.Run(() => Decode(video, starts[k], span), cancel);
            if (pcm == null) return null;
            Mark(pcm, speech, (int)(starts[k] / FrameSec));
            progress?.Report((k + 1.0) / (starts.Length + 1));
        }
        return await Task.Run(() => Match(speech, cues, frames), cancel);
    }

    /// <summary>The best (offset, scale) for these marks and cues (also used by the checks).</summary>
    public static Result? Match(float[] speech, List<SubText.Cue> cues, int frames)
    {
        var n = 1;
        while (n < frames * 2) n <<= 1;
        var a = new Complex[n];
        for (var i = 0; i < frames; i++) a[i] = speech[i];
        AudioTap.Fft(a);
        Result? best = null;
        double bestScore = double.MinValue;
        foreach (var scale in Scales)
        {
            // subtitles at the trial speed: +1 while a line shows, -1 between lines
            var s = new Complex[n];
            for (var i = 0; i < frames; i++) s[i] = -1;
            foreach (var c in cues)
                for (int i = (int)(c.Start * scale / FrameSec), e = Math.Min(frames, (int)(c.End * scale / FrameSec)); i < e; i++)
                    if (i >= 0) s[i] = 1;
            AudioTap.Fft(s);
            // corr[d] = Σ speech[t]·subs[t-d]: speech spectrum × conjugate of the subtitles', back through the same FFT
            var c2 = new Complex[n];
            for (var i = 0; i < n; i++) c2[i] = Complex.Conjugate(a[i] * Complex.Conjugate(s[i]));
            AudioTap.Fft(c2);
            var max = (int)(MaxShift / FrameSec);
            double peak = double.MinValue, sum = 0, sum2 = 0;
            var at = 0;
            var count = 0;
            for (var d = -max; d <= max; d++)
            {
                var v = c2[(d + n) % n].Real / n;
                sum += v; sum2 += v * v; count++;
                if (v > peak) { peak = v; at = d; }
            }
            var mean = sum / count;
            var sd = Math.Sqrt(Math.Max(1e-9, sum2 / count - mean * mean));
            var z = (peak - mean) / sd;
            if (z > bestScore) { bestScore = z; best = new Result(at * FrameSec, scale, z); }
        }
        return best is { Confidence: >= 6 } ? best : null;
    }

    /// <summary>Speech marks for one decoded stretch: frames whose voice-band energy is in the loud part of this stretch.</summary>
    static void Mark(short[] pcm, float[] speech, int first)
    {
        var count = pcm.Length / Hop;
        var energy = new double[count];
        // a cheap voice band: a one-pole high-pass (~250 Hz) after a one-pole low-pass (~3 kHz)
        double lp = 0, hpIn = 0, hpOut = 0;
        const double aLp = 0.70, aHp = 0.82;
        for (var f = 0; f < count; f++)
        {
            double e = 0;
            for (var i = f * Hop; i < (f + 1) * Hop; i++)
            {
                lp += aLp * (pcm[i] - lp);
                hpOut = aHp * (hpOut + lp - hpIn);
                hpIn = lp;
                e += hpOut * hpOut;
            }
            energy[f] = Math.Log10(e / Hop + 1);
        }
        var sorted = energy.Order().ToArray();
        if (sorted.Length == 0) return;
        double floor = sorted[(int)(sorted.Length * 0.2)], top = sorted[(int)(sorted.Length * 0.95)];
        var threshold = floor + (top - floor) * 0.45;
        for (var f = 0; f < count && first + f < speech.Length; f++)
        {
            // a little hold: speech doesn't stop between words
            var on = energy[f] > threshold || f > 0 && energy[f - 1] > threshold || f + 1 < count && energy[f + 1] > threshold;
            speech[first + f] = on ? 1 : -1;
        }
    }

    static LibVLC? _vlc;

    /// <summary>A stretch of the video's sound as 8 kHz mono samples (libVLC transcodes to a temporary WAV, faster than real time).</summary>
    static short[]? Decode(string video, double start, double length)
    {
        _vlc ??= new LibVLC("--quiet", "--no-video", "--no-sout-video", "--no-spu");
        var wav = Path.Combine(Path.GetTempPath(), $"pylon-sync-{Guid.NewGuid():N}.wav");
        try
        {
            using var media = new Media(_vlc, video, FromType.FromPath);
            media.AddOption($":start-time={start.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            media.AddOption($":stop-time={(start + length).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            media.AddOption($":sout=#transcode{{acodec=s16l,channels=1,samplerate={Rate}}}:std{{access=file,mux=wav,dst='{wav.Replace("'", "")}'}}");
            media.AddOption(":sout-keep");
            using var player = new MediaPlayer(media);
            var done = new ManualResetEventSlim();
            player.EndReached += (_, _) => done.Set();
            player.EncounteredError += (_, _) => done.Set();
            if (!player.Play()) return null;
            done.Wait(TimeSpan.FromSeconds(Math.Max(60, length / 10)));
            player.Stop();
            var bytes = File.ReadAllBytes(wav);
            var data = Waveform.DataOffset(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
            if (data < 0) return null;
            var pcm = new short[(bytes.Length - data) / 2];
            Buffer.BlockCopy(bytes, data, pcm, 0, pcm.Length * 2);
            return pcm;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        finally { try { File.Delete(wav); } catch (IOException) { } }
    }

    /// <summary>Writes the corrected copy beside the original ("Name.synced.srt") and returns its path.</summary>
    public static string WriteFixed(string subs, Result r)
    {
        var ext = Path.GetExtension(subs).ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(subs);
        if (name.EndsWith(".synced", StringComparison.OrdinalIgnoreCase)) name = name[..^7];
        var target = Path.Combine(Path.GetDirectoryName(subs)!, name + ".synced" + ext);
        File.WriteAllText(target, SubText.Shift(SubText.Read(subs), ext, r.Offset, r.Scale), new System.Text.UTF8Encoding(true));
        return target;
    }
}
