using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FFmpeg.AutoGen;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11VideoDevice = Vortice.Direct3D11.ID3D11VideoDevice;

namespace Pylon;

/// <summary>
/// Pylon's own video path for local files: FFmpeg demux and decode (D3D11VA, software as the fallback), shown with the
/// D3D11 video processor in a flip swap chain. Opens in tens of ms where libVLC takes ~300. Threads: demux → packets
/// → decode → frames → present, and demux → sound (VideoEngine.Audio.cs). A seek raises _serial: every thread drops what is older.
/// </summary>
public sealed unsafe partial class VideoEngine : IDisposable
{
    /// <summary>PYLON_FFMPEG_DLLS points at another set of FFmpeg 8 DLLs.</summary>
    public static string DllDir => Environment.GetEnvironmentVariable("PYLON_FFMPEG_DLLS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pylon", "tools", "ffmpeg");

    /// <summary>Not shipped: Ffmpeg.InstallEngineLibsAsync brings them when the setting is turned on.</summary>
    public static IEnumerable<string> DllNames => new[] { "avutil", "swresample", "swscale", "avcodec", "avformat", "avfilter" }.Select(lib => $"{lib}-{ffmpeg.LibraryVersionMap[lib]}.dll");

    public static bool LibsInstalled => DllNames.All(dll => File.Exists(Path.Combine(DllDir, dll)));

    static bool _loaded;

    /// <summary>Loads FFmpeg's DLLs; false when they aren't there or don't match the bindings.</summary>
    public static bool Load()
    {
        lock (DeviceGate) return LoadOnce();
    }

    static bool LoadOnce()
    {
        if (_loaded) return true;
        try
        {
            // an absolute path also makes Windows look for each DLL's dependencies next to it
            foreach (var dll in DllNames) NativeLibrary.Load(Path.Combine(DllDir, dll));
            ffmpeg.RootPath = DllDir;
            ffmpeg.LibraryVersionMap.TryAdd("postproc", 0); // the bindings look for it with avfilter; FFmpeg 8 has none, and a missing one is no error
            DynamicallyLoadedBindings.Initialize();
            return _loaded = true;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or NotSupportedException) { return false; }
    }

    const int FrameQueue = 6;
    // queue items that aren't pointers (those are even): (serial << 2) | kind
    const int FlushMark = 1, EndMark = 3;
    const long OvershootUs = 100_000;

    static bool IsMark(IntPtr item) => (item & 1) != 0;
    static IntPtr Mark(int serial, int kind) => (serial << 2) | kind;

    readonly IntPtr _hwnd;
    readonly CancellationTokenSource _stop = new();
    readonly BlockingCollection<IntPtr> _packets = [];
    readonly BlockingCollection<IntPtr> _frames = new(FrameQueue);
    readonly List<Thread> _threads = [];
    readonly AVCodecContext_get_format _getFormat; // kept: FFmpeg calls it from native code
    readonly List<IntPtr> _kept = []; // packets until the first frame: all of them again if the hardware decoder turns out unable

    AVFormatContext* _format;
    AVCodecContext* _codec;
    AVBufferRef* _hwDevice;
    SwsContext* _sws;
    AVStream* _stream;
    bool _hw, _strict;
    long _openTicks;

    ID3D11Device _device = null!;
    ID3D11DeviceContext _context = null!;
    ID3D11VideoDevice _videoDevice = null!;
    ID3D11VideoContext1 _video = null!;
    IDXGISwapChain1? _swap;
    ID3D11VideoProcessorOutputView? _output;
    ID3D11VideoProcessorEnumerator? _enumerator;
    ID3D11VideoProcessor? _processor;
    ID3D11Texture2D? _upload, _uploadDefault;
    readonly Dictionary<(IntPtr, int), ID3D11VideoProcessorInputView> _views = [];
    bool _p010;
    int _swapW, _swapH, _inW, _inH;
    Vortice.DXGI.Format _uploadFormat;
    ColorSpaceType _space = (ColorSpaceType)(-1);
    AVFrame* _shown;

    readonly object _clockGate = new();
    long _clockBaseUs, _clockBaseTicks;
    double _speed = 1;
    volatile bool _paused, _wantPaused; // asked; with sound the clock stands only when the sound's fade is out
    volatile bool _behind;
    int _stepAsked;
    bool _stepped;
    TaskCompletionSource<(byte[] Pixels, int Width, int Height)?>? _snapshotAsked;
    int _snapshotW, _snapshotH;
    IntPtr _timer;

    volatile int _serial; // raised by a seek; written under _clockGate
    int _decodeSerial;
    long _seekAimUs, _seekTargetUs, _seekShowFromUs = long.MinValue, _seekTicks, _skipBeforeUs = long.MinValue, _backoffUs;
    bool _seekHasFrame; // the decoder gave something since the last seek
    AVFrame* _lastSkipped;

    /// <summary>The last frame has been on screen for its time.</summary>
    public event Action? Ended;
    /// <summary>A thread stopped on an error (on that thread).</summary>
    public event Action<Exception>? Failed;
    /// <summary>The first picture is on its way to the screen (on the present thread).</summary>
    public event Action? FirstFrame;

    public bool Hardware => _hw;
    public int VideoWidth => _stream->codecpar->width;
    public int VideoHeight => _stream->codecpar->height;
    public long Decoded { get; private set; }
    public long Presented { get; private set; }
    public long Dropped { get; private set; }
    public double LateAvgMs => Presented == 0 ? 0 : _lateSumUs / 1000.0 / Presented;
    public double LateMaxMs => _lateMaxUs / 1000.0;
    public double FirstFrameMs { get; private set; }
    /// <summary>From asking for the last seek to its picture.</summary>
    public double SeekMs { get; private set; }
    public long TimeMs => DurationMs > 0 ? Math.Min(DurationMs, (NowUs - _startUs) / 1000) : (NowUs - _startUs) / 1000; // MPEG-TS timestamps don't start at 0; the clock runs on after the end
    long _startUs;
    public long DurationMs => _format == null ? 0 // without find_stream_info only the stream may know
        : _format->duration > 0 ? _format->duration / 1000
        : _stream->duration > 0 ? ffmpeg.av_rescale_q(_stream->duration, _stream->time_base, new AVRational { num = 1, den = 1000 }) : 0;
    long _lateSumUs, _lateMaxUs;

    /// <param name="audioDevice">Start of the output device's name; null for the Windows default.</param>
    public VideoEngine(IntPtr hwnd, string? audioDevice = null)
    {
        _hwnd = hwnd;
        _audioDevice = audioDevice;
        _getFormat = (codec, formats) =>
        {
            for (var f = formats; *f != AVPixelFormat.AV_PIX_FMT_NONE; f++)
                if (*f == AVPixelFormat.AV_PIX_FMT_D3D11) return *f;
            return ffmpeg.avcodec_default_get_format(codec, formats);
        };
    }

    /// <summary>
    /// Opens the file and starts playing; throws when it can't be opened or has no video. <paramref name="strict"/>: also
    /// when it needs what only libVLC has so far (see Unsupported).
    /// </summary>
    public void Open(string path, long startMs = 0, bool paused = false, bool strict = false)
    {
        _strict = strict;
        _openTicks = Stopwatch.GetTimestamp();
        CreateDevice();
        Perf.Mark("engine: device");
        fixed (AVFormatContext** format = &_format)
            Check(ffmpeg.avformat_open_input(format, path, null, null), "open");
        var index = BestVideo();
        // find_stream_info reads and decodes a part of the file: only what a header doesn't tell (MPEG-TS, MPEG-PS) needs it
        if (index < 0 || _format->streams[index]->codecpar->width == 0)
        {
            Check(ffmpeg.avformat_find_stream_info(_format, null), "stream info");
            index = BestVideo();
        }
        if (index < 0) throw new InvalidDataException("no video stream");
        _stream = _format->streams[index];
        if (_stream->start_time != ffmpeg.AV_NOPTS_VALUE) _startUs = ffmpeg.av_rescale_q(_stream->start_time, _stream->time_base, new AVRational { num = 1, den = 1_000_000 });
        Perf.Mark("engine: format");
        OpenDecoder(hardware: true);
        OpenAudio();
        if (strict && Unsupported() is { } why) throw new NotSupportedException(why);
        Perf.Mark("engine: decoder");
        if (paused) Paused = true;
        if (startMs > 0) Seek(startMs);
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x1F0003); // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS
        foreach (var loop in _audioStream != null ? new ThreadStart[] { DemuxLoop, DecodeLoop, PresentLoop, AudioLoop } : [DemuxLoop, DecodeLoop, PresentLoop])
        {
            var thread = new Thread(loop) { IsBackground = true, Name = "Pylon engine " + loop.Method.Name };
            _threads.Add(thread);
            thread.Start();
        }
    }

    /// <summary>What the player needs libVLC for in this file: picking tracks and subtitles (5.5), chapters, 360°.</summary>
    string? Unsupported()
    {
        var audio = 0;
        for (var i = 0; i < _format->nb_streams; i++)
        {
            var kind = _format->streams[i]->codecpar->codec_type;
            if (kind == AVMediaType.AVMEDIA_TYPE_SUBTITLE) return "subtitles";
            if (kind == AVMediaType.AVMEDIA_TYPE_AUDIO && ++audio > 1) return "several audio tracks";
        }
        if (_format->nb_chapters > 0) return "chapters";
        var video = _stream->codecpar;
        if (video->field_order is not (AVFieldOrder.AV_FIELD_UNKNOWN or AVFieldOrder.AV_FIELD_PROGRESSIVE)) return "interlaced";
        if (ffmpeg.av_packet_side_data_get(video->coded_side_data, video->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_SPHERICAL) != null) return "360°";
        return null;
    }

    int BestVideo()
    {
        var index = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        return index >= 0 && (_format->streams[index]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0 ? -1 : index; // a cover isn't a video
    }

    // one device for the whole process: making one takes ~30 ms each time, and the first ~250 ms (Warm)
    static readonly object DeviceGate = new();
    static ID3D11Device? _sharedDevice;
    static ID3D11DeviceContext? _sharedContext;
    static ID3D11VideoDevice? _sharedVideoDevice;
    static ID3D11VideoContext1? _sharedVideo;

    /// <summary>Loads the DLLs and starts the graphics driver ahead of the first file, on any thread.</summary>
    public static void Warm()
    {
        try
        {
            lock (DeviceGate)
                if (Load()) SharedDevice();
        }
        catch (SharpGen.Runtime.SharpGenException) { } // no such device: the first file will say so
    }

    static void SharedDevice()
    {
        if (_sharedDevice != null && _sharedDevice.DeviceRemovedReason.Success) return;
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using (var protect = device.QueryInterface<ID3D11Multithread>()) protect.SetMultithreadProtected(true); // the decoder shares it
        using (var dxgi = device.QueryInterface<IDXGIDevice1>()) dxgi.MaximumFrameLatency = 1;
        _sharedDevice = device;
        _sharedContext = context;
        _sharedVideoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _sharedVideo = context.QueryInterface<ID3D11VideoContext1>();
    }

    void CreateDevice()
    {
        lock (DeviceGate)
        {
            SharedDevice();
            _device = _sharedDevice!;
            _context = _sharedContext!;
            _videoDevice = _sharedVideoDevice!;
            _video = _sharedVideo!;
        }
        _p010 = _device.CheckFormatSupport(Vortice.DXGI.Format.P010).HasFlag(FormatSupport.VideoProcessorInput);
    }
    void OpenDecoder(bool hardware)
    {
        var decoder = ffmpeg.avcodec_find_decoder(_stream->codecpar->codec_id);
        if (decoder == null) throw new NotSupportedException("no decoder for " + ffmpeg.avcodec_get_name(_stream->codecpar->codec_id));
        fixed (AVCodecContext** codec = &_codec) ffmpeg.avcodec_free_context(codec);
        _codec = ffmpeg.avcodec_alloc_context3(decoder);
        Check(ffmpeg.avcodec_parameters_to_context(_codec, _stream->codecpar), "codec parameters");
        _codec->pkt_timebase = _stream->time_base;
        _hw = hardware && SupportsD3D11(decoder);
        if (_hw)
        {
            if (_hwDevice == null)
            {
                _hwDevice = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                _device.AddRef(); // FFmpeg releases it with its context
                ((AVD3D11VADeviceContext*)((AVHWDeviceContext*)_hwDevice->data)->hwctx)->device = (FFmpeg.AutoGen.ID3D11Device*)_device.NativePointer;
                Check(ffmpeg.av_hwdevice_ctx_init(_hwDevice), "D3D11VA");
            }
            _codec->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
            _codec->get_format = _getFormat;
            _codec->extra_hw_frames = FrameQueue + 3; // queued, shown, being presented and the last skipped frames all hold a surface
            _codec->thread_count = 1; // more threads only delay the first frame here
        }
        else _codec->thread_count = 0;
        Check(ffmpeg.avcodec_open2(_codec, decoder, null), "decoder");
    }

    static bool SupportsD3D11(AVCodec* decoder)
    {
        for (var i = 0; ffmpeg.avcodec_get_hw_config(decoder, i) is var config && config != null; i++)
            if (config->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11 && (config->methods & 1) != 0) return true; // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
        return false;
    }

    /// <summary>Goes to <paramref name="ms"/>: exactly, or to the keyframe before it (quicker, for dragging).</summary>
    public void Seek(long ms, bool precise = true)
    {
        var target = Math.Max(0, ms) * 1000 + _startUs;
        _backoffUs = 0;
        _seekTargetUs = target;
        RequestSeek(target, precise ? target : long.MinValue);
    }

    /// <summary>The demuxer landed past the target (MPEG-TS has no index): again from further back, the target stays. False when that's been tried enough.</summary>
    bool SeekFurtherBack()
    {
        if (_seekTargetUs <= _startUs || _backoffUs >= 16_000_000 || _decodeSerial != _serial) return false;
        _backoffUs = _backoffUs == 0 ? 1_000_000 : _backoffUs * 2;
        long showFrom;
        lock (_clockGate) showFrom = _seekShowFromUs;
        RequestSeek(_seekTargetUs - _backoffUs, showFrom);
        return true;
    }

    void RequestSeek(long aimUs, long showFromUs)
    {
        lock (_clockGate)
        {
            _seekAimUs = aimUs;
            _seekShowFromUs = showFromUs;
            _seekTicks = Stopwatch.GetTimestamp();
            _firstPts = long.MinValue;
            _clockFree = false;
            _serial++;
            if (HasSound)
            {
                _audioStarted.Reset();
                _audioDone.Reset();
            }
        }
    }

    public bool Paused
    {
        get => _wantPaused;
        set
        {
            lock (_clockGate)
            {
                if (_wantPaused == value) return;
                _wantPaused = value;
                if (!value || !HasSound)
                {
                    _clockBaseUs = ClockNow();
                    _clockBaseTicks = Stopwatch.GetTimestamp();
                    _paused = value;
                }
                if (!value && _stepped) // sound stands where the steps began: everything goes on from the frame shown
                {
                    _stepped = false;
                    _backoffUs = 0;
                    _seekTargetUs = _clockBaseUs;
                    RequestSeek(_clockBaseUs, _clockBaseUs);
                }
            }
        }
    }
    /// <summary>Pauses if needed and shows the next frame.</summary>
    public void Step()
    {
        Paused = true;
        Volatile.Write(ref _stepAsked, 1);
    }

    /// <summary>Playback speed; sound keeps its pitch.</summary>
    public double Speed
    {
        get { lock (_clockGate) return _speed; }
        set
        {
            lock (_clockGate)
            {
                _clockBaseUs = ClockNow();
                _clockBaseTicks = Stopwatch.GetTimestamp();
                _speed = value;
            }
        }
    }

    void DemuxLoop() => Guard(() =>
    {
        var demuxSerial = 0;
        var ended = false;
        while (!_stop.IsCancellationRequested)
        {
            if (_serial != demuxSerial)
            {
                long aim;
                lock (_clockGate)
                {
                    demuxSerial = _serial;
                    aim = _seekAimUs;
                }
                ffmpeg.avformat_seek_file(_format, -1, long.MinValue, aim, aim, 0); // on failure it just plays on from here
                ClearPackets(_packets);
                ClearPackets(_audioPackets);
                _packets.Add(Mark(demuxSerial, FlushMark));
                _audioPackets.Add(Mark(demuxSerial, FlushMark));
                ended = false;
                continue;
            }
            // waits only when both streams have plenty: stopping at one full queue would starve the other in a badly interleaved file
            if (ended || (_packets.Count >= 64 && (_audioStream == null || _audioPackets.Count >= 128)))
            {
                Thread.Sleep(2);
                continue;
            }
            var packet = ffmpeg.av_packet_alloc();
            var read = ffmpeg.av_read_frame(_format, packet);
            if (read >= 0 && packet->stream_index == _stream->index) _packets.Add((IntPtr)packet);
            else if (read >= 0 && _audioStream != null && packet->stream_index == _audioStream->index) _audioPackets.Add((IntPtr)packet);
            else
            {
                ffmpeg.av_packet_free(&packet);
                if (read < 0)
                {
                    _packets.Add(Mark(demuxSerial, EndMark));
                    _audioPackets.Add(Mark(demuxSerial, EndMark));
                    ended = true;
                }
            }
        }
    });

    static void ClearPackets(BlockingCollection<IntPtr> queue)
    {
        while (queue.TryTake(out var item))
        {
            if (IsMark(item)) continue;
            var packet = (AVPacket*)item;
            ffmpeg.av_packet_free(&packet);
        }
    }

    void DecodeLoop() => Guard(() =>
    {
        var frame = ffmpeg.av_frame_alloc();
        _lastSkipped = ffmpeg.av_frame_alloc();
        var errors = 0;
        try
        {
            while (true)
            {
                var item = _packets.Take(_stop.Token);
                if (IsMark(item))
                {
                    var serial = (int)(item >> 2);
                    if ((item & 3) == EndMark) DrainDecoder(frame);
                    else if (serial == _serial) // an older seek's mark: the newer one is on its way
                    {
                        ffmpeg.avcodec_flush_buffers(_codec);
                        FreePackets();
                        ffmpeg.av_frame_unref(_lastSkipped);
                        _decodeSerial = serial;
                        _seekHasFrame = false;
                        lock (_clockGate) _skipBeforeUs = _seekShowFromUs;
                    }
                    continue;
                }
                var packet = (AVPacket*)item;
                if (Decoded == 0) _kept.Add((IntPtr)ffmpeg.av_packet_clone(packet));
                _codec->skip_frame = _behind ? AVDiscard.AVDISCARD_NONREF : AVDiscard.AVDISCARD_DEFAULT; // can't keep up (4x of 60 fps): frames nothing refers to go undecoded
                var sent = ffmpeg.avcodec_send_packet(_codec, packet);
                ffmpeg.av_packet_free(&packet);
                if (sent < 0) errors++;
                if (!Receive(frame) && Decoded == 0 && _hw && errors >= 3) Fallback(frame);
                if (Decoded > 0) FreePackets();
            }
        }
        finally
        {
            FreePackets();
            ffmpeg.av_frame_free(&frame);
        }
    });

    void DrainDecoder(AVFrame* frame)
    {
        for (var i = 0; i < 2; i++) // what the decoder still holds; twice, as the first round may have changed decoders
        {
            ffmpeg.avcodec_send_packet(_codec, null);
            Receive(frame);
        }
        if (_skipBeforeUs != long.MinValue && _lastSkipped->data[0] != null) // a seek past the last frame shows the last frame
        {
            _skipBeforeUs = long.MinValue;
            Deliver(_lastSkipped);
        }
        if (!_seekHasFrame && SeekFurtherBack()) return; // no keyframe between the landing and the end
        _frames.Add(Mark(_decodeSerial, EndMark), _stop.Token);
    }
    /// <summary>The same packets again in software, on all the cores (the hardware setup runs one thread).</summary>
    void Fallback(AVFrame* frame)
    {
        OpenDecoder(hardware: false);
        Perf.Mark("engine: software decoder");
        foreach (var item in _kept)
        {
            ffmpeg.avcodec_send_packet(_codec, (AVPacket*)item);
            Receive(frame);
        }
    }

    void FreePackets()
    {
        foreach (var item in _kept)
        {
            var packet = (AVPacket*)item;
            ffmpeg.av_packet_free(&packet);
        }
        _kept.Clear();
    }

    /// <summary>Moves every frame the decoder has into the queue; false when it had none.</summary>
    bool Receive(AVFrame* frame)
    {
        var any = false;
        while (ffmpeg.avcodec_receive_frame(_codec, frame) >= 0)
        {
            any = true;
            if (Decoded == 0 && _hw && frame->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11) // no hardware for this profile (Hi10P)
            {
                ffmpeg.av_frame_unref(frame);
                Fallback(frame);
                return true;
            }
            if (_skipBeforeUs != long.MinValue && frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE)
            {
                var pts = PtsUs(frame);
                if (pts < _skipBeforeUs) // a precise seek decodes from the keyframe before its target
                {
                    _seekHasFrame = true;
                    ffmpeg.av_frame_unref(_lastSkipped);
                    ffmpeg.av_frame_move_ref(_lastSkipped, frame);
                    continue;
                }
                if (_lastSkipped->data[0] == null && pts - _skipBeforeUs > OvershootUs && SeekFurtherBack())
                {
                    _skipBeforeUs = long.MinValue;
                    ffmpeg.av_frame_unref(frame);
                    return true;
                }
                _skipBeforeUs = long.MinValue;
            }
            Deliver(frame);
        }
        return any;
    }

    /// <summary>Into the queue, tagged with the seek it belongs to; <paramref name="frame"/> ends up empty.</summary>
    void Deliver(AVFrame* frame)
    {
        if (_strict && (frame->flags & ffmpeg.AV_FRAME_FLAG_INTERLACED) != 0) throw new NotSupportedException("interlaced"); // containers often don't tell
        var item = ffmpeg.av_frame_alloc();
        if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            ffmpeg.av_frame_move_ref(item, frame);
        else
        {
            Convert(frame, item);
            ffmpeg.av_frame_unref(frame);
        }
        item->opaque = (void*)(nint)_decodeSerial;
        _seekHasFrame = true;
        Decoded++;
        try { _frames.Add((IntPtr)item, _stop.Token); }
        catch (OperationCanceledException) { ffmpeg.av_frame_free(&item); throw; }
    }

    long PtsUs(AVFrame* frame) => ffmpeg.av_rescale_q(frame->best_effort_timestamp, _stream->time_base, new AVRational { num = 1, den = 1_000_000 });
    /// <summary>Software frame → NV12 / P010, what the video processor reads. The color matrix stays for it to apply.</summary>
    void Convert(AVFrame* src, AVFrame* dst)
    {
        var depth = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)src->format)->comp[0].depth;
        var format = depth > 8 && _p010 ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12;
        dst->format = (int)format;
        dst->width = (src->width + 1) & ~1; // both formats need even sizes
        dst->height = (src->height + 1) & ~1;
        Check(ffmpeg.av_frame_get_buffer(dst, 0), "frame buffer");
        _sws = ffmpeg.sws_getCachedContext(_sws, src->width, src->height, (AVPixelFormat)src->format, dst->width, dst->height, format, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        ffmpeg.sws_scale(_sws, src->data, src->linesize, 0, src->height, dst->data, dst->linesize);
        ffmpeg.av_frame_copy_props(dst, src);
        if (ffmpeg.av_get_pix_fmt_name((AVPixelFormat)src->format).StartsWith("yuvj", StringComparison.Ordinal))
            dst->color_range = AVColorRange.AVCOL_RANGE_MPEG; // swscale converts these to studio range
    }

    void PresentLoop() => Guard(() =>
    {
        long previous = long.MinValue, frameUs = 33_333;
        var shownSerial = -1;
        while (true)
        {
            IntPtr item;
            while (!_frames.TryTake(out item, 20, _stop.Token)) ServeSnapshot(); // nothing comes after the end of a file
            if (IsMark(item)) // the end of the file
            {
                var ending = (int)(item >> 2);
                lock (_clockGate)
                    if (ending == _serial && _firstSerial != ending) // a seek that found no frame at all: sound isn't to wait for one
                    {
                        _firstPts = previous;
                        _firstSerial = ending;
                    }
                if (ending == _serial && WaitUntil(previous + frameUs, ending) && WaitForSound(ending)) Ended?.Invoke(); // sound often runs a little longer
                continue;
            }
            var frame = (AVFrame*)item;
            var serial = (int)(nint)frame->opaque;
            if (serial != _serial)
            {
                ffmpeg.av_frame_free(&frame);
                continue;
            }
            var pts = frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? previous + frameUs : PtsUs(frame);
            var fresh = serial != shownSerial; // the first picture after opening or a seek shows at once
            if (fresh)
            {
                shownSerial = serial;
                Rebase(pts);
                lock (_clockGate)
                    if (serial == _serial)
                    {
                        _firstPts = pts;
                        _firstSerial = serial;
                    }
            }
            else
            {
                if (pts > previous) frameUs = pts - previous;
                var wait = pts - NowUs;
                _behind = wait < (_behind ? -20_000 : -100_000);
                // the timestamps jump (joined recordings) or a long stall; forward allows for a screen recording's still seconds
                if (!_paused && (wait > 10_000_000 || wait < -2_000_000)) { Rebase(pts); wait = 0; }
                if (wait > 0 || _paused)
                {
                    if (!WaitUntil(pts, serial))
                    {
                        ffmpeg.av_frame_free(&frame);
                        continue;
                    }
                }
                else if (-wait > Math.Max(frameUs, 20_000) && _frames.Count > 0)
                {
                    ffmpeg.av_frame_free(&frame);
                    Dropped++;
                    previous = pts;
                    continue;
                }
            }
            previous = pts;
            var late = Math.Max(0, NowUs - pts);
            Draw(frame);
            var first = Presented == 0;
            _swap?.Present(1, PresentFlags.None).CheckError();
            Presented++;
            var old = _shown; // kept: a resized window redraws it while paused
            _shown = frame;
            ffmpeg.av_frame_free(&old);
            if (!fresh)
            {
                _lateSumUs += late;
                _lateMaxUs = Math.Max(_lateMaxUs, late);
                continue;
            }
            if (!_audioStarted.IsSet) Rebase(pts); // the picture took its time to set up: the timeline starts when it's shown, or with sound
            if (first)
            {
                FirstFrameMs = (Stopwatch.GetTimestamp() - _openTicks) * 1000.0 / Stopwatch.Frequency;
                Perf.Mark("engine: first picture");
                FirstFrame?.Invoke();
            }
            else SeekMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref _seekTicks)) * 1000.0 / Stopwatch.Frequency;
            if (HasSound && !_wantPaused) HoldForSound(pts, serial);
        }
    });

    /// <summary>The picture stands until sound starts, which then sets the clock; a file whose sound comes late doesn't wait for it.</summary>
    void HoldForSound(long pts, int serial)
    {
        var since = Stopwatch.GetTimestamp();
        while (!_audioStarted.Wait(10, _stop.Token))
            if (serial != _serial) return;
            else if (Stopwatch.GetTimestamp() - since > Stopwatch.Frequency / 2)
            {
                _clockFree = true;
                Rebase(pts);
                return;
            }
    }

    bool WaitForSound(int serial)
    {
        while (HasSound && !_audioDone.Wait(10, _stop.Token))
            if (serial != _serial) return false;
        return serial == _serial;
    }

    long ClockNow() => _paused ? _clockBaseUs : _clockBaseUs + (long)((Stopwatch.GetTimestamp() - _clockBaseTicks) * 1_000_000.0 / Stopwatch.Frequency * _speed);

    long NowUs
    {
        get { lock (_clockGate) return ClockNow(); }
    }

    void Rebase(long pts) => Stamp(pts, Stopwatch.GetTimestamp());

    /// <summary>At the moment <paramref name="ticks"/> the timeline was at <paramref name="us"/>.</summary>
    void Stamp(long us, long ticks)
    {
        lock (_clockGate)
        {
            _clockBaseUs = us;
            _clockBaseTicks = ticks;
        }
    }

    /// <summary>In slices, so that stopping or seeking doesn't wait for a long frame; false when that's what happened.</summary>
    bool WaitUntil(long pts, int serial)
    {
        while (true)
        {
            if (_stop.IsCancellationRequested || serial != _serial) return false;
            long slice;
            ServeSnapshot();
            if (_paused)
            {
                if (Interlocked.Exchange(ref _stepAsked, 0) != 0)
                {
                    _stepped = true;
                    Stamp(pts, Stopwatch.GetTimestamp());
                    return true;
                }
                slice = 10_000;
                RedrawIfResized();
            }
            else
            {
                var left = pts - NowUs;
                if (left <= 0) return true;
                slice = Math.Min((long)(left / Speed), 50_000);
                if (slice <= 0) return true;
            }
            var due = -slice * 10;
            if (_timer != IntPtr.Zero && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(_timer, 100);
            else Thread.Sleep(Math.Max(1, (int)(slice / 1000)));
        }
    }

    void RedrawIfResized()
    {
        if (_shown == null) return;
        GetClientRect(_hwnd, out var client);
        if (client.Right == _swapW && client.Bottom == _swapH) return;
        Draw(_shown);
        _swap?.Present(1, PresentFlags.None).CheckError();
    }
    void Draw(AVFrame* frame)
    {
        GetClientRect(_hwnd, out var client);
        if (client.Right <= 0 || client.Bottom <= 0) return;
        var first = Presented == 0;
        if (first) Perf.Mark("engine: first frame decoded");
        EnsureSwapChain(client.Right, client.Bottom);
        if (first) Perf.Mark("engine: swap chain");
        EnsureProcessor(frame);
        if (first) Perf.Mark("engine: video processor");
        var input = InputView(frame);
        var space = PickColorSpace(frame->colorspace, frame->color_range, frame->color_trc, frame->width, frame->height);
        if (space != _space)
        {
            _video.VideoProcessorSetStreamColorSpace1(_processor!, 0, space);
            _space = space;
        }
        var left = (int)frame->crop_left;
        var top = (int)frame->crop_top;
        var (x, y, w, h) = Letterbox(frame->width, frame->height, frame->sample_aspect_ratio.num, frame->sample_aspect_ratio.den, client.Right, client.Bottom);
        _video.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new Vortice.RawRect(left, top, left + frame->width, top + frame->height));
        _video.VideoProcessorSetStreamDestRect(_processor!, 0, true, new Vortice.RawRect(x, y, x + w, y + h));
        _video.VideoProcessorBlt(_processor!, _output!, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = input }]).CheckError();
    }

    void EnsureSwapChain(int width, int height)
    {
        if (_swap == null)
        {
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            _swap = factory.CreateSwapChainForHwnd(_device, _hwnd, new SwapChainDescription1((uint)width, (uint)height, Vortice.DXGI.Format.B8G8R8A8_UNorm)
            {
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SwapEffect = SwapEffect.FlipDiscard,
            });
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll); // Alt+Enter is Pylon's business
        }
        else if (width != _swapW || height != _swapH)
        {
            _output?.Dispose();
            _output = null;
            _swap.ResizeBuffers(0, (uint)width, (uint)height).CheckError();
        }
        _swapW = width;
        _swapH = height;
    }

    void EnsureProcessor(AVFrame* frame)
    {
        if (_processor == null || frame->width != _inW || frame->height != _inH)
        {
            _inW = frame->width;
            _inH = frame->height;
            _output?.Dispose();
            _output = null;
            _processor?.Dispose();
            _enumerator?.Dispose();
            foreach (var view in _views.Values) view.Dispose();
            _views.Clear();
            _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive, // ponytail: interlaced files stay with libVLC (5.4)
                InputWidth = (uint)_inW,
                InputHeight = (uint)_inH,
                OutputWidth = (uint)_swapW,
                OutputHeight = (uint)_swapH,
                Usage = VideoUsage.PlaybackNormal,
            });
            _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
            _video.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false); // no driver "enhancements"
            _video.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor());
            _video.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.RgbFullG22NoneP709);
            _space = (ColorSpaceType)(-1);
        }
        if (_output == null)
        {
            using var back = _swap!.GetBuffer<ID3D11Texture2D>(0); // in D3D11 buffer 0 is always the one to draw on
            _output = _videoDevice.CreateVideoProcessorOutputView(back, _enumerator!, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        }
    }

    ID3D11VideoProcessorInputView InputView(AVFrame* frame)
    {
        var (texture, slice) = Surface(frame);
        if (_views.TryGetValue((texture, slice), out var view)) return view;
        view = NewInputView(texture, slice, _enumerator!);
        _views[(texture, slice)] = view;
        return view;
    }

    (IntPtr Texture, int Slice) Surface(AVFrame* frame)
    {
        var hardware = frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;
        return (hardware ? (IntPtr)frame->data[0] : Upload(frame), hardware ? (int)(IntPtr)frame->data[1] : 0);
    }

    ID3D11VideoProcessorInputView NewInputView(IntPtr texture, int slice, ID3D11VideoProcessorEnumerator enumerator)
    {
        Marshal.AddRef(texture); // the wrapper's Dispose releases it
        using var wrapped = new ID3D11Texture2D(texture);
        return _videoDevice.CreateVideoProcessorInputView(wrapped, enumerator, new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { ArraySlice = (uint)slice },
        });
    }

    /// <summary>The picture on screen as 32-bit BGR; null when there's none or the engine doesn't get to it within a second.</summary>
    public (byte[] Pixels, int Width, int Height)? Snapshot(int width, int height)
    {
        var asked = new TaskCompletionSource<(byte[], int, int)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshotW = width;
        _snapshotH = height;
        Volatile.Write(ref _snapshotAsked, asked);
        return asked.Task.Wait(1000) ? asked.Task.Result : null;
    }

    /// <summary>On the present thread, the only one that may touch the frame on screen.</summary>
    void ServeSnapshot()
    {
        if (Interlocked.Exchange(ref _snapshotAsked, null) is not { } asked) return;
        if (_shown == null)
        {
            asked.SetResult(null);
            return;
        }
        try { asked.SetResult(GrabShown()); }
        catch (SharpGen.Runtime.SharpGenException) { asked.SetResult(null); }
    }

    (byte[], int, int) GrabShown()
    {
        var frame = _shown;
        var sar = frame->sample_aspect_ratio is { num: > 0, den: > 0 } r ? (double)r.num / r.den : 1;
        var w = _snapshotW > 0 ? _snapshotW : (int)Math.Round(frame->width * sar);
        var h = _snapshotH > 0 ? _snapshotH : (int)Math.Round(w * frame->height / (frame->width * sar));
        using var enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)frame->width,
            InputHeight = (uint)frame->height,
            OutputWidth = (uint)w,
            OutputHeight = (uint)h,
            Usage = VideoUsage.PlaybackNormal,
        });
        using var processor = _videoDevice.CreateVideoProcessor(enumerator, 0);
        _video.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
        _video.VideoProcessorSetOutputColorSpace1(processor, ColorSpaceType.RgbFullG22NoneP709);
        _video.VideoProcessorSetStreamColorSpace1(processor, 0, PickColorSpace(frame->colorspace, frame->color_range, frame->color_trc, frame->width, frame->height));
        var left = (int)frame->crop_left;
        var top = (int)frame->crop_top;
        _video.VideoProcessorSetStreamSourceRect(processor, 0, true, new Vortice.RawRect(left, top, left + frame->width, top + frame->height));
        var (texture, slice) = Surface(frame);
        using var input = NewInputView(texture, slice, enumerator);
        using var target = _device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None));
        using var output = _videoDevice.CreateVideoProcessorOutputView(target, enumerator, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        using var staging = _device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _video.VideoProcessorBlt(processor, output, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = input }]).CheckError();
        _context.CopyResource(staging, target);
        var map = _context.Map(staging, 0, MapMode.Read);
        var pixels = new byte[w * h * 4];
        for (var row = 0; row < h; row++) Marshal.Copy((IntPtr)((byte*)map.DataPointer + row * map.RowPitch), pixels, row * w * 4, w * 4);
        _context.Unmap(staging, 0);
        return (pixels, w, h);
    }

    /// <summary>A software frame into a texture the video processor can read.</summary>
    IntPtr Upload(AVFrame* frame)
    {
        var format = frame->format == (int)AVPixelFormat.AV_PIX_FMT_P010LE ? Vortice.DXGI.Format.P010 : Vortice.DXGI.Format.NV12;
        if (_upload == null || format != _uploadFormat || _upload.Description.Width != frame->width || _upload.Description.Height != frame->height)
        {
            _upload?.Dispose();
            _uploadDefault?.Dispose();
            _uploadFormat = format;
            var desc = new Texture2DDescription(format, (uint)frame->width, (uint)frame->height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            _upload = _device.CreateTexture2D(desc);
            desc.Usage = ResourceUsage.Default;
            desc.CPUAccessFlags = CpuAccessFlags.None;
            desc.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
            _uploadDefault = _device.CreateTexture2D(desc);
        }
        var map = _context.Map(_upload, 0, MapMode.WriteDiscard);
        var bytes = format == Vortice.DXGI.Format.P010 ? frame->width * 2 : frame->width;
        var dst = (byte*)map.DataPointer;
        for (var row = 0; row < frame->height; row++)
            Buffer.MemoryCopy(frame->data[0] + row * frame->linesize[0], dst + row * map.RowPitch, map.RowPitch, bytes);
        dst += map.RowPitch * (uint)frame->height;
        for (var row = 0; row < frame->height / 2; row++)
            Buffer.MemoryCopy(frame->data[1] + row * frame->linesize[1], dst + row * map.RowPitch, map.RowPitch, bytes);
        _context.Unmap(_upload, 0);
        _context.CopyResource(_uploadDefault!, _upload);
        return _uploadDefault!.NativePointer;
    }

    /// <summary>Where the picture goes in the window: as large as fits, pixel aspect honored, centered.</summary>
    public static (int X, int Y, int W, int H) Letterbox(int srcW, int srcH, int sarNum, int sarDen, int dstW, int dstH)
    {
        var aspect = (double)srcW / srcH * (sarNum > 0 && sarDen > 0 ? (double)sarNum / sarDen : 1);
        var w = dstW;
        var h = (int)Math.Round(dstW / aspect);
        if (h > dstH)
        {
            h = dstH;
            w = (int)Math.Round(dstH * aspect);
        }
        return ((dstW - w) / 2, (dstH - h) / 2, w, h);
    }

    /// <summary>What the frame's samples mean. Untagged: 709 from HD up, 601 below, the usual players' guess.</summary>
    public static ColorSpaceType PickColorSpace(AVColorSpace space, AVColorRange range, AVColorTransferCharacteristic transfer, int width, int height)
    {
        var full = range == AVColorRange.AVCOL_RANGE_JPEG;
        if (space is AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL)
            return transfer switch
            {
                // ponytail: HDR goes to an 8-bit SDR swap chain and the driver tone-maps; own tone mapping if it looks bad
                AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => ColorSpaceType.YcbcrStudioG2084LeftP2020,
                AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => ColorSpaceType.YcbcrStudioGhlgTopLeftP2020,
                _ => full ? ColorSpaceType.YcbcrFullG22LeftP2020 : ColorSpaceType.YcbcrStudioG22LeftP2020,
            };
        var sd = space is AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M
            || (space != AVColorSpace.AVCOL_SPC_BT709 && width < 1280 && height <= 576);
        return sd ? (full ? ColorSpaceType.YcbcrFullG22LeftP601 : ColorSpaceType.YcbcrStudioG22LeftP601)
            : (full ? ColorSpaceType.YcbcrFullG22LeftP709 : ColorSpaceType.YcbcrStudioG22LeftP709);
    }

    void Guard(Action loop)
    {
        try { loop(); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _stop.Cancel(); // the other threads must not stay blocked on queues nobody serves
            Failed?.Invoke(e);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var thread in _threads) thread.Join();
        ClearPackets(_packets);
        while (_frames.TryTake(out var item))
        {
            if (IsMark(item)) continue;
            var frame = (AVFrame*)item;
            ffmpeg.av_frame_free(&frame);
        }
        _packets.Dispose();
        _frames.Dispose();
        CloseAudio();
        var shown = _shown;
        ffmpeg.av_frame_free(&shown);
        fixed (AVFrame** skipped = &_lastSkipped) ffmpeg.av_frame_free(skipped);
        foreach (var view in _views.Values) view.Dispose();
        _output?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _upload?.Dispose();
        _uploadDefault?.Dispose();
        _swap?.Dispose();
        _context?.ClearState();
        _context?.Flush(); // destruction is deferred, and the window takes a new swap chain only after the old one is gone
        fixed (AVCodecContext** codec = &_codec) ffmpeg.avcodec_free_context(codec);
        fixed (AVBufferRef** device = &_hwDevice) ffmpeg.av_buffer_unref(device);
        fixed (AVFormatContext** format = &_format) ffmpeg.avformat_close_input(format);
        ffmpeg.sws_freeContext(_sws);

        if (_timer != IntPtr.Zero) CloseHandle(_timer);
    }

    static void Check(int result, string what)
    {
        if (result >= 0) return;
        var text = stackalloc byte[256];
        ffmpeg.av_strerror(result, text, 256);
        throw new InvalidDataException($"{what}: {Marshal.PtrToStringUTF8((IntPtr)text)}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out ClientRect rect);
    [StructLayout(LayoutKind.Sequential)] struct ClientRect { public int Left, Top, Right, Bottom; }
}

/// <summary>The child window the engine draws in. A plain "Static": mouse messages pass through to the parent (HTTRANSPARENT).</summary>
public sealed class VideoHost : HwndHost
{
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        return new HandleRef(this, CreateWindowExW(0, "Static", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(int exStyle, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
}

/// <summary>`Pylon.exe --engine file [report.txt] [runs]`: plays one file in a bare window, no libVLC, then writes the numbers. Until 5.4 this is the only way in.</summary>
public static class EngineTest
{
    public static void Run(string file, string? report, int runs)
    {
        void Write(string text)
        {
            if (report != null) File.AppendAllText(report, text + Environment.NewLine);
        }
        if (!VideoEngine.Load())
        {
            Write("FFmpeg DLLs not found");
            return;
        }
        var app = new Application();
        var host = new VideoHost();
        var window = new Window
        {
            Content = host,
            Width = 960,
            Height = 540,
            Title = "Pylon engine test",
            Background = System.Windows.Media.Brushes.Black,
            ShowActivated = false,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        VideoEngine? current = null;
        // PYLON_ENGINE_SCRIPT="1000 pause; 2000 resume; 3000 seek 5000; 4000 fast 1000; 5000 speed 1.5; 6000 volume 0.5; 7000 log; 8000 quit": ms since opening
        var script = Environment.GetEnvironmentVariable("PYLON_ENGINE_SCRIPT");
        async Task RunScript(VideoEngine engine)
        {
            var clock = Stopwatch.StartNew();
            foreach (var step in script!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                await Task.Delay(Math.Max(0, int.Parse(parts[0]) - (int)clock.ElapsedMilliseconds));
                var arg = parts.Length > 2 ? double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
                var before = engine.TimeMs;
                switch (parts[1])
                {
                    case "pause": engine.Paused = true; break;
                    case "resume": engine.Paused = false; break;
                    case "seek": engine.Seek((long)arg); break;
                    case "fast": engine.Seek((long)arg, precise: false); break;
                    case "speed": engine.Speed = arg; break;
                    case "volume": engine.Volume = (float)arg; break;
                    case "quit":
                        await app.Dispatcher.BeginInvoke(() =>
                        {
                            Finish();
                            app.Shutdown();
                        });
                        return;
                }
                Write($"{clock.ElapsedMilliseconds} ms: {step}; clock {before} ms, last seek took {engine.SeekMs:0} ms, shown {engine.Presented}, dropped {engine.Dropped}");
            }
        }
        void Finish()
        {
            if (current is not { } engine) return;
            current = null;
            var clock = engine.TimeMs;
            engine.Dispose();
            Write($"{Path.GetFileName(file)}: hw {engine.Hardware}, first picture {engine.FirstFrameMs:0} ms, decoded {engine.Decoded}, shown {engine.Presented}, dropped {engine.Dropped}, late avg {engine.LateAvgMs:0.0} max {engine.LateMaxMs:0.0} ms, clock {clock} ms, sound {engine.AudioInfo}, underruns {engine.Underruns}");
        }
        void Next()
        {
            try
            {
                var engine = current = new VideoEngine(host.Handle, Environment.GetEnvironmentVariable("PYLON_ENGINE_AUDIO"));
                engine.Ended += () => app.Dispatcher.BeginInvoke(() =>
                {
                    Write($"ended at clock {engine.TimeMs} ms, length {engine.DurationMs} ms");
                    if (script != null) return; // the script says when to leave: it may seek back
                    Finish();
                    if (--runs > 0) Next();
                    else app.Shutdown();
                });
                engine.Failed += e =>
                {
                    Write("failed: " + e);
                    app.Dispatcher.BeginInvoke(() => app.Shutdown());
                };
                engine.Open(file);
                if (script != null) _ = RunScript(engine);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or SharpGen.Runtime.SharpGenException)
            {
                Write("failed: " + e);
                app.Shutdown();
            }
        }
        window.Loaded += (_, _) => Next();
        window.Closing += (_, _) => Finish(); // closed in the middle: the way a stop goes
        app.Run(window);
    }
}
