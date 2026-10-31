using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Pylon;

/// <summary>
/// The engine's sound: one thread decodes on demand and fills WASAPI (shared). While sound really plays, its position sets
/// the clock; when it runs dry, ends or the device goes, nothing sets it and the clock just goes on, so the picture never hangs on sound.
/// </summary>
public sealed unsafe partial class VideoEngine
{
    const long SmallGapUs = 20_000, LargeGapUs = 10_000_000, BackJumpUs = 200_000;

    readonly string? _audioDevice;
    readonly BlockingCollection<IntPtr> _audioPackets = [];
    readonly ManualResetEventSlim _audioStarted = new(), _audioDone = new();
    AVStream* _audioStream;
    AVCodecContext* _audioCodec;
    SwrContext* _swr;
    AVFrame* _audioFrame, _tempoFrame;
    AVFilterGraph* _tempo;
    AVFilterContext* _tempoIn, _tempoOut;
    int _inRate, _inChannels, _inFormat = -1, _rate, _channels;
    ulong _outMask;
    byte[] _pcm = new byte[1 << 16];
    int _pcmPos, _pcmEnd; // in frames
    long _silence, _skip, _written, _originUs;
    long _firstPts = long.MinValue;
    int _firstSerial, _flushed;
    double _audioSpeed = 1;
    bool _placed, _drained, _starved, _awaitFlush, _afterSeek, _placeAtClock;
    volatile bool _clockFree; // sound was late: the clock runs without it, and sound joins at the clock's position
    volatile bool _audioDead;
    float _volume = 1, _gain, _gainTarget = 1;
    float _gainStep; // per frame: a change of level takes ~20 ms, sample by sample, so that it never clicks

    /// <summary>"48000 Hz 2 ch", or why there is no sound.</summary>
    public string AudioInfo { get; private set; } = "no audio stream";
    public long Underruns { get; private set; }
    /// <summary>No such device, or it went away; the picture goes on (on the sound's thread).</summary>
    public event Action<Exception>? SoundFailed;
    Exception? _audioError;

    /// <summary>Linear, 1 = as in the file, up to 2 (which may clip). This stream only: the Windows mixer's slider stays.</summary>
    public float Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(value, 0, 2));
    }

    bool HasSound => _audioStream != null && !_audioDead;

    void OpenAudio()
    {
        var index = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, _stream->index, null, 0);
        if (index < 0) return;
        var stream = _format->streams[index];
        var decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (decoder == null)
        {
            AudioInfo = "no decoder for " + ffmpeg.avcodec_get_name(stream->codecpar->codec_id);
            return;
        }
        _audioCodec = ffmpeg.avcodec_alloc_context3(decoder);
        Check(ffmpeg.avcodec_parameters_to_context(_audioCodec, stream->codecpar), "audio parameters");
        _audioCodec->pkt_timebase = stream->time_base;
        Check(ffmpeg.avcodec_open2(_audioCodec, decoder, null), "audio decoder");
        _audioStream = stream;
        _audioFrame = ffmpeg.av_frame_alloc();
        _tempoFrame = ffmpeg.av_frame_alloc();
    }

    void AudioLoop()
    {
        try
        {
            AvSetMmThreadCharacteristicsW("Pro Audio", out _); // little is queued: no waiting behind other threads
            using var output = new WasapiOutput(_audioDevice);
            _rate = output.Rate;
            _channels = output.Channels;
            _outMask = output.ChannelMask;
            _gainStep = 1f / (_rate / 50);
            AudioInfo = $"{_rate} Hz {_channels} ch";
            Perf.Mark("engine: audio device");
            var serial = 0;
            var buffering = true; // filling the device's queue: a start without gaps
            var running = false;
            var finished = false;
            ulong heard = 0;
            while (!_stop.IsCancellationRequested)
            {
                var speed = Speed;
                if (serial != _serial || speed != _audioSpeed)
                {
                    if (running) FadeOut(output);
                    output.Stop();
                    output.Reset(); // what's queued belongs to the old place
                    if (serial != _serial)
                    {
                        serial = _serial;
                        _awaitFlush = _flushed != serial; // the fade may have just read past the seek's mark
                        _afterSeek = true;
                        _placed = _drained = _placeAtClock = false;
                    }
                    else
                    {
                        _placed = false; // a new speed: the next block, already decoded ahead, takes its place by its time
                        _placeAtClock = true;
                    }
                    _audioSpeed = speed;
                    CloseTempo();
                    _pcmPos = _pcmEnd = 0;
                    _silence = _skip = _written = 0;
                    _starved = finished = running = false;
                    buffering = true;
                    heard = 0;
                    _gain = 0;
                }
                if (!running && _wantPaused && !_paused) Freeze(NowUs);
                if (buffering)
                {
                    if (!FirstPicture(serial))
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    var fed = Feed(output);
                    if (output.Padding >= output.Lead || AudioFinished) buffering = false;
                    else if (fed == 0) Thread.Sleep(1);
                    continue;
                }
                if (running == _wantPaused)
                {
                    if (running)
                    {
                        FadeOut(output); // sound and picture stop together when the fade is out
                        output.Stop();
                        running = false;
                        var (at, frequency0, _) = output.Position;
                        Freeze(_originUs + (long)((double)at * 1_000_000 / frequency0 * _audioSpeed));
                    }
                    else
                    {
                        _gainTarget = 1;
                        output.Start();
                        running = true;
                        Perf.Mark("engine: audio started");
                    }
                }
                if (!running)
                {
                    Thread.Sleep(5);
                    continue;
                }
                output.Ready.WaitOne(20);
                var (position, frequency, ticks) = output.Position;
                if (position > heard) // it stands still when the stream runs dry; then the clock goes on alone
                {
                    heard = position;
                    AudioMoved(serial, _originUs + (long)((double)position * 1_000_000 / frequency * _audioSpeed), ticks);
                }
                if (finished) continue;
                var more = Feed(output);
                if (output.Padding == 0 && more == 0)
                {
                    if (AudioFinished)
                    {
                        finished = true;
                        lock (_clockGate)
                            if (serial == _serial) _audioDone.Set();
                    }
                    else
                    {
                        if (!_starved) Underruns++;
                        _starved = true;
                    }
                }
            }
        }
        catch (COMException e) // no such device, unplugged: the picture goes on without sound
        {
            AudioInfo = $"no sound: {e.Message}";
            _audioError = e;
        }
        finally
        {
            _audioDead = true;
            _audioStarted.Set();
            _audioDone.Set();
            if (_audioError != null) SoundFailed?.Invoke(_audioError);
        }
    }

    /// <summary>Down to silence on the samples that come next, and until they have played: ~100 ms.</summary>
    void FadeOut(WasapiOutput output)
    {
        _gainTarget = 0;
        var since = Stopwatch.GetTimestamp();
        while (_gain > 0 && Stopwatch.GetTimestamp() - since < Stopwatch.Frequency / 10)
        {
            if (Feed(output) == 0 && output.Padding < output.Lead) break; // no more sound to fade: the queue just runs out
            output.Ready.WaitOne(5);
        }
        while (output.Padding > 0 && Stopwatch.GetTimestamp() - since < Stopwatch.Frequency / 5) Thread.Sleep(1);
        _gain = 0;
    }
    bool FirstPicture(int serial)
    {
        lock (_clockGate) return _firstSerial == serial && _firstPts != long.MinValue;
    }

    /// <summary>The clock stands from here on (sound has just stopped, or there's none to wait for).</summary>
    void Freeze(long us)
    {
        lock (_clockGate)
        {
            if (!_wantPaused) return; // asked to go on in the meantime
            _clockBaseUs = us;
            _clockBaseTicks = Stopwatch.GetTimestamp();
            _paused = true;
        }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] static extern IntPtr AvSetMmThreadCharacteristicsW(string task, out int index);

    void AudioMoved(int serial, long us, long ticks)
    {
        lock (_clockGate)
        {
            if (serial != _serial) return;
            if (!_paused) Stamp(us, ticks);
            if (_audioStarted.IsSet) return;
            _audioStarted.Set(); // not at Start: the device takes a moment before its position moves
        }
        Perf.Mark("engine: audio moves");
    }

    bool AudioFinished => _drained && _pcmPos >= _pcmEnd && _silence == 0;

    int Feed(WasapiOutput output)
    {
        var free = output.Lead - output.Padding;
        if (free <= 0) return 0;
        var fed = Fill(output.GetBuffer(free), free);
        output.Release(fed);
        return fed;
    }

    /// <summary>Up to <paramref name="frames"/> of sound into the device's buffer; how many it had.</summary>
    int Fill(byte* dst, int frames)
    {
        var size = _channels * sizeof(float);
        var done = 0;
        while (done < frames)
        {
            var count = 0;
            if (_silence > 0)
            {
                count = (int)Math.Min(_silence, frames - done);
                new Span<byte>(dst + done * size, count * size).Clear();
                _silence -= count;
            }
            else if (_pcmPos < _pcmEnd)
            {
                if (_skip > 0)
                {
                    count = (int)Math.Min(_skip, _pcmEnd - _pcmPos);
                    _skip -= count;
                    _pcmPos += count;
                    continue;
                }
                count = Math.Min(_pcmEnd - _pcmPos, frames - done);
                new ReadOnlySpan<byte>(_pcm, _pcmPos * size, count * size).CopyTo(new Span<byte>(dst + done * size, count * size));
                ApplyGain((float*)(dst + done * size), count);
                _pcmPos += count;
            }
            else if (!NextAudio()) break;
            done += count;
            _written += count;
        }
        return done;
    }

    void ApplyGain(float* samples, int frames)
    {
        var volume = Volume;
        var target = _gainTarget * volume;
        if (_gain == 1 && target == 1) return;
        for (var f = 0; f < frames; f++)
        {
            if (_gain != target) _gain = _gain < target ? Math.Min(target, _gain + _gainStep) : Math.Max(target, _gain - _gainStep);
            for (var c = 0; c < _channels; c++) samples[f * _channels + c] *= _gain;
        }
    }

    /// <summary>The next decoded block into _pcm; false when none is ready now.</summary>
    bool NextAudio()
    {
        while (true)
        {
            if (!_awaitFlush)
            {
                if (_tempo != null && ffmpeg.av_buffersink_get_frame(_tempoOut, _tempoFrame) >= 0)
                {
                    var ok = Resample(_tempoFrame);
                    ffmpeg.av_frame_unref(_tempoFrame);
                    if (ok) return true;
                    continue;
                }
                if (ffmpeg.avcodec_receive_frame(_audioCodec, _audioFrame) >= 0)
                {
                    Place(_audioFrame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? long.MinValue
                        : ffmpeg.av_rescale_q(_audioFrame->best_effort_timestamp, _audioStream->time_base, new AVRational { num = 1, den = 1_000_000 }));
                    if (_audioSpeed == 1)
                    {
                        var ok = Resample(_audioFrame);
                        ffmpeg.av_frame_unref(_audioFrame);
                        if (ok) return true;
                        continue;
                    }
                    if (_tempo == null) OpenTempo(_audioFrame);
                    Check(ffmpeg.av_buffersrc_add_frame(_tempoIn, _audioFrame), "tempo");
                    continue;
                }
                if (_drained) return false;
            }
            if (!_audioPackets.TryTake(out var item)) return false;
            if (IsMark(item))
            {
                if ((int)(item >> 2) != _serial) continue; // an older seek's
                if ((item & 3) == FlushMark)
                {
                    ffmpeg.avcodec_flush_buffers(_audioCodec);
                    _awaitFlush = false;
                    _flushed = _serial;
                }
                else if (!_awaitFlush)
                {
                    ffmpeg.avcodec_send_packet(_audioCodec, null); // the end: what the decoder still holds
                    if (_tempo != null) ffmpeg.av_buffersrc_add_frame(_tempoIn, null);
                    _drained = true;
                }
                continue;
            }
            var packet = (AVPacket*)item;
            if (!_awaitFlush) ffmpeg.avcodec_send_packet(_audioCodec, packet); // a broken packet: its sound is missing, the next gap check covers it
            ffmpeg.av_packet_free(&packet);
        }
    }

    /// <summary>atempo from libavfilter: the speed without the pitch. Under 0.5 it takes several in a row.</summary>
    void OpenTempo(AVFrame* like)
    {
        _tempo = ffmpeg.avfilter_graph_alloc();
        var layout = stackalloc byte[128];
        ffmpeg.av_channel_layout_describe(&like->ch_layout, layout, 128);
        var timeBase = _audioStream->time_base;
        var args = $"time_base={timeBase.num}/{timeBase.den}:sample_rate={like->sample_rate}:sample_fmt={ffmpeg.av_get_sample_fmt_name((AVSampleFormat)like->format)}:channel_layout={Marshal.PtrToStringUTF8((IntPtr)layout)}";
        fixed (AVFilterContext** input = &_tempoIn) Check(ffmpeg.avfilter_graph_create_filter(input, ffmpeg.avfilter_get_by_name("abuffer"), "in", args, null, _tempo), "tempo source");
        fixed (AVFilterContext** output = &_tempoOut) Check(ffmpeg.avfilter_graph_create_filter(output, ffmpeg.avfilter_get_by_name("abuffersink"), "out", null, null, _tempo), "tempo sink");
        var chain = new List<string>();
        var speed = _audioSpeed;
        for (; speed < 0.5; speed *= 2) chain.Add("atempo=0.5");
        chain.Add("atempo=" + speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var open = ffmpeg.avfilter_inout_alloc();
        var close = ffmpeg.avfilter_inout_alloc();
        open->name = ffmpeg.av_strdup("in");
        open->filter_ctx = _tempoIn;
        close->name = ffmpeg.av_strdup("out");
        close->filter_ctx = _tempoOut;
        var parsed = ffmpeg.avfilter_graph_parse_ptr(_tempo, string.Join(",", chain), &close, &open, null);
        ffmpeg.avfilter_inout_free(&open);
        ffmpeg.avfilter_inout_free(&close);
        Check(parsed, "tempo chain");
        Check(ffmpeg.avfilter_graph_config(_tempo, null), "tempo graph");
    }

    void CloseTempo()
    {
        if (_tempo == null) return;
        fixed (AVFilterGraph** graph = &_tempo) ffmpeg.avfilter_graph_free(graph);
        _tempoIn = _tempoOut = null;
    }

    bool Resample(AVFrame* frame)
    {
        if (_swr == null || frame->sample_rate != _inRate || frame->ch_layout.nb_channels != _inChannels || frame->format != _inFormat)
        {
            _inRate = frame->sample_rate;
            _inChannels = frame->ch_layout.nb_channels;
            _inFormat = frame->format;
            AVChannelLayout layout;
            if (ffmpeg.av_channel_layout_from_mask(&layout, _outMask) < 0 || layout.nb_channels != _channels) ffmpeg.av_channel_layout_default(&layout, _channels);
            fixed (SwrContext** swr = &_swr)
            {
                ffmpeg.swr_free(swr);
                Check(ffmpeg.swr_alloc_set_opts2(swr, &layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _rate, &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate, 0, null), "resampler");
            }
            Check(ffmpeg.swr_init(_swr), "resampler");
        }
        var capacity = ffmpeg.swr_get_out_samples(_swr, frame->nb_samples);
        if (capacity <= 0) return false;
        var size = capacity * _channels * sizeof(float);
        if (_pcm.Length < size) _pcm = new byte[size * 2];
        int count;
        fixed (byte* pcm = _pcm)
        {
            var output = pcm;
            count = ffmpeg.swr_convert(_swr, &output, capacity, frame->extended_data, frame->nb_samples);
        }
        if (count <= 0) return false;
        _pcmPos = 0;
        _pcmEnd = count;
        return true;
    }

    /// <summary>Keeps sound on the timeline: where it starts next to the picture, and what happens when its timestamps skip.</summary>
    void Place(long pts)
    {
        if (!_placed)
        {
            _placed = true;
            long picture;
            lock (_clockGate) picture = _firstPts;
            if (picture == long.MinValue) picture = pts; // a seek just came: this block is on its way out anyway
            if (pts == long.MinValue) pts = picture;
            // sound before the first picture of a file just plays and the picture waits for its time; after a seek the picture is where we asked to be
            var start = _clockFree || _placeAtClock ? NowUs : _afterSeek ? picture : Math.Min(pts, picture);
            _placeAtClock = false;
            _originUs = start;
            if (pts > start) _silence = (long)((pts - start) * _rate / 1_000_000.0 / _audioSpeed);
            else _skip = (long)((start - pts) * _rate / 1_000_000.0 / _audioSpeed);
            return;
        }
        if (pts == long.MinValue || _audioSpeed != 1) return; // ponytail: through atempo the blocks' times lag by its window; holes aren't filled at other speeds
        var (shift, silence) = PlaceGap(pts - (_originUs + (_written + _silence - _skip) * 1_000_000 / _rate), _starved, _rate); // what's still to skip comes before the origin
        _originUs += shift;
        _silence += silence;
        _starved = false;
    }

    /// <summary>
    /// Timestamps ahead of the samples written so far: a short hole is filled with silence so that sound keeps its place.
    /// A real jump, or sound that had run dry (nothing queued to pad), moves the timeline instead. Tiny differences are rounding.
    /// </summary>
    public static (long ShiftUs, long Silence) PlaceGap(long gapUs, bool starved, int rate) =>
        starved || gapUs > LargeGapUs || gapUs < -BackJumpUs ? (gapUs, 0)
        : gapUs > SmallGapUs ? (0, gapUs * rate / 1_000_000)
        : (0, 0);

    void CloseAudio()
    {
        ClearPackets(_audioPackets);
        _audioPackets.Dispose();
        _audioStarted.Dispose();
        _audioDone.Dispose();
        CloseTempo();
        fixed (AVCodecContext** codec = &_audioCodec) ffmpeg.avcodec_free_context(codec);
        fixed (SwrContext** swr = &_swr) ffmpeg.swr_free(swr);
        fixed (AVFrame** frame = &_audioFrame) ffmpeg.av_frame_free(frame);
        fixed (AVFrame** frame = &_tempoFrame) ffmpeg.av_frame_free(frame);
    }
}

/// <summary>A WASAPI shared stream at the device's own format (32-bit float); buffer and position. All calls from one thread.</summary>
sealed unsafe class WasapiOutput : IDisposable
{
    readonly IAudioClient _client;
    readonly IAudioRenderClient _render;
    readonly IAudioClock _clock;
    readonly uint _buffer;
    readonly ulong _frequency;

    public readonly AutoResetEvent Ready = new(false);
    public int Rate { get; }
    public int Channels { get; }
    public ulong ChannelMask { get; }

    /// <param name="deviceName">Start of the device's name; null for the Windows default. Never another device than named.</param>
    public WasapiOutput(string? deviceName)
    {
        var device = Find(deviceName);
        var iid = typeof(IAudioClient).GUID;
        device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var client);
        _client = (IAudioClient)client;
        Marshal.ReleaseComObject(device);
        _client.GetMixFormat(out var mix);
        try
        {
            var format = (WaveFormat*)mix;
            var extensible = format->Tag == 0xFFFE;
            if (format->Bits != 32 || (extensible ? *(uint*)((byte*)mix + 24) : format->Tag) != 3) throw new COMException("the device's mix format isn't 32-bit float");
            Rate = format->Rate;
            Channels = format->Channels;
            ChannelMask = extensible ? *(uint*)((byte*)mix + 20) : 0;
            _client.Initialize(0, 0x00040000 /*EVENTCALLBACK*/, 1_000_000 /*100 ms*/, 0, mix, IntPtr.Zero);
        }
        finally { Marshal.FreeCoTaskMem(mix); }
        _client.SetEventHandle(Ready.SafeWaitHandle.DangerousGetHandle());
        _client.GetBufferSize(out _buffer);
        iid = typeof(IAudioRenderClient).GUID;
        _client.GetService(ref iid, out var render);
        _render = (IAudioRenderClient)render;
        iid = typeof(IAudioClock).GUID;
        _client.GetService(ref iid, out var clock);
        _clock = (IAudioClock)clock;
        _clock.GetFrequency(out _frequency);
        _client.GetDevicePeriod(out var period, out _); // in 100 ns
        Lead = Math.Min((int)_buffer, Math.Max(Rate * 80 / 1000, (int)(period * 3 * Rate / 10_000_000)));
    }
    public int Padding
    {
        get
        {
            _client.GetCurrentPadding(out var padding);
            return (int)padding;
        }
    }

    /// <summary>How much to keep queued, in frames: 80 ms or three device periods. A reserve for a busy machine; a longer queue would delay every fade.</summary>
    public int Lead { get; }

    /// <summary>What the device has reached: position in units of frequency, and when (Stopwatch ticks).</summary>
    public (ulong Position, ulong Frequency, long Ticks) Position
    {
        get
        {
            _clock.GetPosition(out var position, out var qpc); // qpc in 100 ns
            return (position, _frequency, (long)((double)qpc * Stopwatch.Frequency / 10_000_000));
        }
    }

    public byte* GetBuffer(int frames)
    {
        _render.GetBuffer((uint)frames, out var data);
        return (byte*)data;
    }

    public void Release(int frames) => _render.ReleaseBuffer((uint)frames, 0);

    public void Start() => _client.Start();

    public void Stop() => _client.Stop();

    /// <summary>Drops what's queued, the position back at 0. Only when stopped.</summary>
    public void Reset() => _client.Reset();

    public void Dispose()
    {
        try { _client.Stop(); }
        catch (COMException) { } // the device is gone

        Marshal.ReleaseComObject(_clock);
        Marshal.ReleaseComObject(_render);
        Marshal.ReleaseComObject(_client);
        Ready.Dispose();
    }

    static IMMDevice Find(string? name)
    {
        var devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (name == null)
            {
                devices.GetDefaultAudioEndpoint(0 /*render*/, 1 /*multimedia*/, out var main);
                return main;
            }
            devices.EnumAudioEndpoints(0 /*render*/, 1 /*active*/, out var list);
            list.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                list.Item(i, out var device);
                if (FriendlyName(device).StartsWith(name, StringComparison.OrdinalIgnoreCase)) return device;
                Marshal.ReleaseComObject(device);
            }
            throw new COMException("no audio device " + name);
        }
        finally { Marshal.ReleaseComObject(devices); }
    }

    static string FriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(0 /*read*/, out var store);
        var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
        store.GetValue(ref key, out var value);
        var text = Marshal.PtrToStringUni(value.Data) ?? "";
        PropVariantClear(ref value);
        Marshal.ReleaseComObject(store);
        return text;
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat { public ushort Tag, Channels; public int Rate, BytesPerSec; public ushort BlockAlign, Bits, Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid Format; public int Id; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public ushort Type, R1, R2, R3; public IntPtr Data, Data2; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, int state, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long period, out long minimum);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        void GetBuffer(uint frames, out IntPtr data);
        void ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("CD63314F-3FBA-4A1B-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClock
    {
        void GetFrequency(out ulong frequency);
        void GetPosition(out ulong position, out ulong qpc);
    }
}
