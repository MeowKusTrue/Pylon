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
/// → decode → frames → present, and demux → sound (VideoEngine.Audio.cs). Seeking and the place next to libVLC are the next stages.
/// </summary>
public sealed unsafe partial class VideoEngine : IDisposable
{
    /// <summary>PYLON_FFMPEG_DLLS points at another set of FFmpeg 8 DLLs.</summary>
    static string DllDir => Environment.GetEnvironmentVariable("PYLON_FFMPEG_DLLS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pylon", "tools", "ffmpeg");

    static bool _loaded;

    /// <summary>Loads FFmpeg's DLLs; false when they aren't there or don't match the bindings.</summary>
    public static bool Load()
    {
        if (_loaded) return true;
        try
        {
            // an absolute path also makes Windows look for each DLL's dependencies next to it
            foreach (var lib in new[] { "avutil", "swresample", "swscale", "avcodec", "avformat" })
                NativeLibrary.Load(Path.Combine(DllDir, $"{lib}-{ffmpeg.LibraryVersionMap[lib]}.dll"));
            ffmpeg.RootPath = DllDir;
            DynamicallyLoadedBindings.Initialize();
            return _loaded = true;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or NotSupportedException) { return false; }
    }

    const int FrameQueue = 6;

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
    bool _hw;
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
    IntPtr _timer;

    /// <summary>The last frame has been on screen for its time.</summary>
    public event Action? Ended;
    /// <summary>A thread stopped on an error (on that thread).</summary>
    public event Action<Exception>? Failed;
    /// <summary>The first picture is on its way to the screen (on the present thread).</summary>
    public event Action? FirstFrame;

    public bool Hardware => _hw;
    public long Decoded { get; private set; }
    public long Presented { get; private set; }
    public long Dropped { get; private set; }
    public double LateAvgMs => Presented == 0 ? 0 : _lateSumUs / 1000.0 / Presented;
    public double LateMaxMs => _lateMaxUs / 1000.0;
    public double FirstFrameMs { get; private set; }
    public long TimeMs => (NowUs - _startUs) / 1000; // MPEG-TS timestamps don't start at 0
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

    /// <summary>Opens the file and starts playing; throws when it can't be opened or has no video.</summary>
    public void Open(string path)
    {
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
        Perf.Mark("engine: decoder");
        _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x1F0003); // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS
        foreach (var loop in _audioStream != null ? new ThreadStart[] { DemuxLoop, DecodeLoop, PresentLoop, AudioLoop } : [DemuxLoop, DecodeLoop, PresentLoop])
        {
            var thread = new Thread(loop) { IsBackground = true, Name = "Pylon engine " + loop.Method.Name };
            _threads.Add(thread);
            thread.Start();
        }
    }

    int BestVideo()
    {
        var index = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        return index >= 0 && (_format->streams[index]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0 ? -1 : index; // a cover isn't a video
    }

    void CreateDevice()
    {
        D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.VideoSupport | DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;
        using (var protect = _device.QueryInterface<ID3D11Multithread>()) protect.SetMultithreadProtected(true); // the decoder shares it
        using (var dxgi = _device.QueryInterface<IDXGIDevice1>()) dxgi.MaximumFrameLatency = 1;
        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _video = _context.QueryInterface<ID3D11VideoContext1>();
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
            _codec->extra_hw_frames = FrameQueue + 2; // queued, shown and being presented frames all hold a surface
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

    void DemuxLoop() => Guard(() =>
    {
        while (!_stop.IsCancellationRequested)
        {
            // waits only when both streams have plenty: stopping at one full queue would starve the other in a badly interleaved file
            if (_packets.Count >= 64 && (_audioStream == null || _audioPackets.Count >= 128))
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
                if (read < 0) break;
            }
        }
        _packets.CompleteAdding();
        _audioPackets.CompleteAdding();
    });

    void DecodeLoop() => Guard(() =>
    {
        var frame = ffmpeg.av_frame_alloc();
        var errors = 0;
        try
        {
            foreach (var item in _packets.GetConsumingEnumerable(_stop.Token))
            {
                var packet = (AVPacket*)item;
                if (Decoded == 0) _kept.Add((IntPtr)ffmpeg.av_packet_clone(packet));
                var sent = ffmpeg.avcodec_send_packet(_codec, packet);
                ffmpeg.av_packet_free(&packet);
                if (sent < 0) errors++;
                if (!Receive(frame) && _hw && errors >= 3) Fallback(frame);
                if (Decoded > 0) FreePackets();
            }
            for (var i = 0; i < 2; i++) // the end: what the decoder still holds; twice, as the first round may have changed decoders
            {
                ffmpeg.avcodec_send_packet(_codec, null);
                Receive(frame);
            }
            _frames.CompleteAdding();
        }
        finally
        {
            FreePackets();
            ffmpeg.av_frame_free(&frame);
        }
    });

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
            var item = ffmpeg.av_frame_alloc();
            if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                ffmpeg.av_frame_move_ref(item, frame);
            else
            {
                Convert(frame, item);
                ffmpeg.av_frame_unref(frame);
            }
            Decoded++;
            try { _frames.Add((IntPtr)item, _stop.Token); }
            catch (OperationCanceledException) { ffmpeg.av_frame_free(&item); throw; }
        }
        return any;
    }

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
        foreach (var item in _frames.GetConsumingEnumerable(_stop.Token))
        {
            var frame = (AVFrame*)item;
            var pts = frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? previous + frameUs
                : ffmpeg.av_rescale_q(frame->best_effort_timestamp, _stream->time_base, new AVRational { num = 1, den = 1_000_000 });
            if (previous != long.MinValue && pts > previous) frameUs = pts - previous;
            previous = pts;
            if (_clockBaseTicks == 0)
            {
                Rebase(pts);
                Volatile.Write(ref _firstPts, pts);
            }
            var wait = pts - NowUs;
            // the timestamps jump (joined recordings) or a long stall; forward allows for a screen recording's still seconds
            if (wait > 10_000_000 || wait < -2_000_000) { Rebase(pts); wait = 0; }
            if (wait > 0) WaitUntil(pts);
            else if (-wait > Math.Max(frameUs, 20_000) && _frames.Count > 0)
            {
                ffmpeg.av_frame_free(&frame);
                Dropped++;
                continue;
            }
            var late = Math.Max(0, NowUs - pts);
            Draw(frame);
            var first = Presented == 0;
            _swap?.Present(1, PresentFlags.None).CheckError();
            Presented++;
            _lateSumUs += late;
            _lateMaxUs = Math.Max(_lateMaxUs, late);
            if (first)
            {
                if (!_audioStarted.IsSet) Rebase(pts); // the first picture took its time to set up: the timeline starts when it's shown, or with sound
                FirstFrameMs = (Stopwatch.GetTimestamp() - _openTicks) * 1000.0 / Stopwatch.Frequency;
                Perf.Mark("engine: first picture");
                FirstFrame?.Invoke();
                // the picture stands until sound starts, which then sets the clock; a file whose sound comes late doesn't wait for it
                if (_audioStream != null && !_audioStarted.Wait(500, _stop.Token))
                {
                    _clockFree = true;
                    Rebase(pts);
                }
            }
            var old = _shown; // kept for 5.3: a resized window redraws it while paused
            _shown = frame;
            ffmpeg.av_frame_free(&old);
        }
        WaitUntil(previous + frameUs);
        if (_audioStream != null) _audioDone.Wait(_stop.Token); // sound often runs a little longer than the picture
        if (!_stop.IsCancellationRequested) Ended?.Invoke();
    });

    long NowUs
    {
        get
        {
            lock (_clockGate) return _clockBaseUs + (Stopwatch.GetTimestamp() - _clockBaseTicks) * 1_000_000 / Stopwatch.Frequency;
        }
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

    /// <summary>In slices, so that stopping doesn't wait for a long frame.</summary>
    void WaitUntil(long pts)
    {
        for (long left; (left = pts - NowUs) > 0 && !_stop.IsCancellationRequested;)
        {
            var slice = Math.Min(left, 50_000);
            var due = -slice * 10;
            if (_timer != IntPtr.Zero && SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(_timer, 100);
            else Thread.Sleep((int)(slice / 1000));
        }
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
        var hardware = frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;
        var texture = hardware ? (IntPtr)frame->data[0] : Upload(frame);
        var slice = hardware ? (int)(IntPtr)frame->data[1] : 0;
        if (_views.TryGetValue((texture, slice), out var view)) return view;
        Marshal.AddRef(texture); // the wrapper's Dispose releases it
        using var wrapped = new ID3D11Texture2D(texture);
        view = _videoDevice.CreateVideoProcessorInputView(wrapped, _enumerator!, new VideoProcessorInputViewDescription
        {
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { ArraySlice = (uint)slice },
        });
        _views[(texture, slice)] = view;
        return view;
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
        while (_packets.TryTake(out var item))
        {
            var packet = (AVPacket*)item;
            ffmpeg.av_packet_free(&packet);
        }
        while (_frames.TryTake(out var item))
        {
            var frame = (AVFrame*)item;
            ffmpeg.av_frame_free(&frame);
        }
        _packets.Dispose();
        _frames.Dispose();
        CloseAudio();
        var shown = _shown;
        ffmpeg.av_frame_free(&shown);
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
        _video?.Dispose();
        _videoDevice?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
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
                    Write($"length {engine.DurationMs} ms");
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
