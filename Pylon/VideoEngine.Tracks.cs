using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen;

namespace Pylon;

/// <summary>A sound or subtitle stream of the file; Id is its stream index.</summary>
public sealed record EngineTrack(int Id, string Name, string? Language, bool Default, bool Forced);

/// <summary>
/// The engine's tracks. All text subtitles are decoded by the demux thread as their packets come by (a little ahead of
/// the picture; it costs next to nothing) and handed out as cues: the window draws the chosen one, so switching is
/// instant. Styled (ASS) and picture subtitles are libVLC's: see Unsupported.
/// </summary>
public sealed unsafe partial class VideoEngine
{
    static readonly Regex AssTags = new(@"\{[^}]*\}", RegexOptions.Compiled);
    static readonly Dictionary<string, string> BibliographicCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ger"] = "de", ["fre"] = "fr", ["chi"] = "zh", ["dut"] = "nl", ["cze"] = "cs", ["gre"] = "el", ["rum"] = "ro", ["slo"] = "sk", ["per"] = "fa", ["alb"] = "sq", ["arm"] = "hy", ["baq"] = "eu", ["bur"] = "my", ["geo"] = "ka", ["ice"] = "is", ["mac"] = "mk", ["mao"] = "mi", ["may"] = "ms", ["tib"] = "bo", ["wel"] = "cy",
    };

    volatile int _audioWanted = -1;
    readonly Dictionary<int, IntPtr> _subCodecs = []; // by stream; the demux thread's
    AVStream* _audioOpenFor;

    public IReadOnlyList<EngineTrack> AudioTracks { get; private set; } = [];
    public IReadOnlyList<EngineTrack> SubtitleTracks { get; private set; } = [];
    public int AudioTrack => _audioStream == null ? -1 : _audioStream->index;

    /// <summary>A cue of a subtitle stream: id, start and end in seconds of the timeline shown to the user, text. On the demux thread.</summary>
    public event Action<int, double, double, string>? SubtitleCue;

    void ListTracks()
    {
        var (audio, subs) = (new List<EngineTrack>(), new List<EngineTrack>());
        for (var i = 0; i < _format->nb_streams; i++)
        {
            var stream = _format->streams[i];
            var kind = stream->codecpar->codec_type;
            if (kind != AVMediaType.AVMEDIA_TYPE_AUDIO && kind != AVMediaType.AVMEDIA_TYPE_SUBTITLE) continue;
            var list = kind == AVMediaType.AVMEDIA_TYPE_AUDIO ? audio : subs;
            var language = Tag(stream, "language");
            var name = Tag(stream, "title") ?? $"Track {list.Count + 1}";
            if (LanguageName(language) is { } shown && !name.Contains(shown, StringComparison.OrdinalIgnoreCase)) name += $" - [{shown}]";
            list.Add(new EngineTrack(i, name, language, (stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0, (stream->disposition & ffmpeg.AV_DISPOSITION_FORCED) != 0));
        }
        (AudioTracks, SubtitleTracks) = (audio, subs);
    }

    static string? Tag(AVStream* stream, string key)
    {
        var entry = ffmpeg.av_dict_get(stream->metadata, key, null, 0);
        return entry == null ? null : Marshal.PtrToStringUTF8((IntPtr)entry->value);
    }

    static string? LanguageName(string? code)
    {
        if (TwoLetters(code) is not { } two) return null;
        try { return new CultureInfo(two).DisplayName; }
        catch (CultureNotFoundException) { return code; }
    }

    /// <summary>"eng", "en", "ger", "deu" as "en", "en", "de", "de"; null when it isn't a language ("und").</summary>
    public static string? TwoLetters(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Equals("und", StringComparison.OrdinalIgnoreCase) || code.Equals("mul", StringComparison.OrdinalIgnoreCase)) return null;
        if (code.Length == 2) return code.ToLowerInvariant();
        if (BibliographicCodes.TryGetValue(code, out var two)) return two;
        return CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase))?.TwoLetterISOLanguageName;
    }

    /// <summary>
    /// The track libVLC would start with: the setting's number, else the preferred language, else (when
    /// <paramref name="flagged"/>) one the file marks forced or default. -1 for none of these.
    /// </summary>
    public static int PickTrack(IReadOnlyList<EngineTrack> tracks, int number, string? language, bool flagged)
    {
        if (number > 0 && number <= tracks.Count) return tracks[number - 1].Id;
        if (TwoLetters(language) is { } wanted && tracks.FirstOrDefault(t => TwoLetters(t.Language) == wanted) is { } match) return match.Id;
        return flagged && tracks.FirstOrDefault(t => t.Forced) is { } forced ? forced.Id
            : flagged && tracks.FirstOrDefault(t => t.Default) is { } main ? main.Id : -1;
    }

    /// <summary>The words of a decoded subtitle: its ASS event line without the eight fields before the text and without tags.</summary>
    public static string AssText(string line)
    {
        var at = 0;
        for (var i = 0; i < 8 && at >= 0; i++)
        {
            at = line.IndexOf(',', at);
            if (at >= 0) at++;
        }
        var text = at < 0 ? line : line[at..];
        return AssTags.Replace(text, "").Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ").Trim();
    }

    /// <summary>To another sound track, from where the picture is.</summary>
    public void SelectAudio(int id)
    {
        if (_audioStream == null || id == AudioTrack) return;
        _audioWanted = id;
        SeekHere();
    }

    void SeekHere()
    {
        var now = NowUs;
        _backoffUs = 0;
        _seekTargetUs = now;
        RequestSeek(now, now);
    }

    AVCodecContext* SubtitleDecoder(AVStream* stream)
    {
        if (_subCodecs.TryGetValue(stream->index, out var known)) return (AVCodecContext*)known;
        var decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        var codec = ffmpeg.avcodec_alloc_context3(decoder);
        if (decoder == null || ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar) < 0 || ffmpeg.avcodec_open2(codec, decoder, null) < 0) ffmpeg.avcodec_free_context(&codec);
        else codec->pkt_timebase = stream->time_base;
        _subCodecs[stream->index] = (IntPtr)codec;
        return codec;
    }

    void CloseSubtitles()
    {
        foreach (var item in _subCodecs.Values)
        {
            var codec = (AVCodecContext*)item;
            ffmpeg.avcodec_free_context(&codec);
        }
    }
    void DecodeSubtitle(AVPacket* packet)
    {
        var stream = _format->streams[packet->stream_index];
        if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE || packet->pts == ffmpeg.AV_NOPTS_VALUE) return;
        var codec = SubtitleDecoder(stream);
        if (codec == null) return;
        AVSubtitle sub;
        int got;
        if (ffmpeg.avcodec_decode_subtitle2(codec, &sub, &got, packet) < 0 || got == 0) return;
        var timeBase = stream->time_base;
        var start = ffmpeg.av_rescale_q(packet->pts, timeBase, new AVRational { num = 1, den = 1_000_000 }) + sub.start_display_time * 1000L - _startUs;
        var length = packet->duration > 0 ? ffmpeg.av_rescale_q(packet->duration, timeBase, new AVRational { num = 1, den = 1_000_000 })
            : sub.end_display_time is > 0 and < uint.MaxValue ? sub.end_display_time * 1000L : 7_000_000; // ponytail: a cue without a length stays 7 s, however close the next one
        var lines = new List<string>();
        for (var i = 0; i < sub.num_rects; i++)
            if (sub.rects[i]->ass != null && Marshal.PtrToStringUTF8((IntPtr)sub.rects[i]->ass) is { } ass && AssText(ass) is { Length: > 0 } text) lines.Add(text);
        ffmpeg.avsubtitle_free(&sub);
        if (lines.Count > 0) SubtitleCue?.Invoke(stream->index, start / 1e6, (start + length) / 1e6, string.Join("\n", lines));
    }
}
