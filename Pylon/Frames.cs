using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Pylon;

/// <summary>
/// Still frames from a video through a silent libVLC player: contact sheets, and library thumbnails for formats Windows
/// can't preview (FLV, RMVB, VOB…).
/// </summary>
public static class Frames
{
    static LibVLC? _vlc;
    static readonly SemaphoreSlim Gate = new(1);

    /// <summary>Frames at the given positions (0…1), <paramref name="width"/> wide in the video's own shape. Blocks: call off the UI thread.</summary>
    public static List<(long Time, BitmapSource Image)> Grab(string path, double[] positions, int width)
    {
        Gate.Wait();
        try
        {
            // exact seeks (a sheet's times must be right; keyframes can be a minute apart), never a half-decoded or skipped frame
            _vlc ??= new LibVLC("--no-audio", "--no-spu", "--no-osd", "--quiet", "--no-video-title-show",
                "--no-avcodec-hurry-up", "--no-avcodec-corrupted", "--no-drop-late-frames", "--no-skip-frames");
            using var media = new Media(_vlc, path, FromType.FromPath);
            media.Parse(MediaParseOptions.ParseLocal).Wait(5000);
            var video = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video).Data.Video;
            var height = video.Width > 0 && video.Height > 0
                ? (int)Math.Round(width * video.Height * (video.SarDen > 0 && video.SarNum > 0 ? (double)video.SarDen / video.SarNum : 1) / video.Width / 2) * 2
                : width * 9 / 16;
            var stride = width * 4;
            var buffer = Marshal.AllocHGlobal(stride * height);
            var got = new ManualResetEventSlim();
            long target = -1, now = -1;
            using var mp = new MediaPlayer(media);
            // never call libVLC from the display callback: Stop() holds the input lock while it waits for this thread
            mp.TimeChanged += (_, e) => Interlocked.Exchange(ref now, e.Time);
            mp.SetVideoFormat("RV32", (uint)width, (uint)height, (uint)stride);
            mp.SetVideoCallbacks((_, planes) => { Marshal.WriteIntPtr(planes, buffer); return IntPtr.Zero; }, null, (_, _) =>
            {
                // the first picture after a seek can still be the old one: wait for one from near the target
                if (target >= 0 && Math.Abs(Interlocked.Read(ref now) - target) <= 1000) got.Set();
            });
            var frames = new List<(long, BitmapSource)>();
            try
            {
                mp.Play();
                for (var wait = 0; mp.Length <= 0 && wait < 50; wait++) Thread.Sleep(100);
                var length = mp.Length;
                if (length <= 0) return frames;
                foreach (var p in positions)
                {
                    got.Reset();
                    target = (long)(length * p);
                    mp.Time = target;
                    if (!got.Wait(10000)) continue;
                    var bytes = new byte[stride * height];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    var img = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bytes, stride);
                    img.Freeze();
                    frames.Add((Interlocked.Read(ref now), img));
                }
                return frames;
            }
            finally
            {
                mp.Stop();
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>One frame from a third of the way in, for the library list.</summary>
    public static BitmapSource? Thumbnail(string path, int width) => Grab(path, [0.3], width).Select(f => f.Image).FirstOrDefault();

    /// <summary>
    /// A 4 × 4 sheet of frames with times, the file name, length, size and resolution on top, saved as PNG into
    /// <paramref name="folder"/>. Grabbing runs in the background, drawing on the calling (UI) thread.
    /// </summary>
    public static async Task<string?> ContactSheetAsync(string path, string folder)
    {
        const int Cols = 4, Rows = 4, Tile = 480, Gap = 8, Pad = 24, Head = 78;
        var frames = await Task.Run(() => Grab(path, [.. Enumerable.Range(0, Cols * Rows).Select(i => (i + 0.5) / (Cols * Rows))], Tile));
        if (frames.Count == 0) return null;
        var th = frames[0].Image.PixelHeight;
        var rows = (frames.Count + Cols - 1) / Cols; // frames that didn't decode in time are left out
        int w = Pad * 2 + Cols * Tile + (Cols - 1) * Gap, h = Pad * 2 + Head + rows * th + (rows - 1) * Gap;
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        FormattedText Text(string s, double size, Brush b) => new(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, b, 1.0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 17, 17)), null, new Rect(0, 0, w, h));
            dc.DrawText(Text(Path.GetFileName(path), 26, Brushes.White), new Point(Pad, Pad));
            var info = new FileInfo(path);
            var length = frames[^1].Time * Cols * Rows / (Cols * Rows - 0.5);
            var src = frames[0].Image;
            dc.DrawText(Text($"{TimeSpan.FromMilliseconds(length):h\\:mm\\:ss}   ·   {info.Length / 1048576.0:0.#} MB   ·   {Path.GetDirectoryName(path)}", 15,
                new SolidColorBrush(Color.FromRgb(160, 160, 160))), new Point(Pad, Pad + 40));
            for (var i = 0; i < frames.Count; i++)
            {
                var (time, img) = frames[i];
                double x = Pad + i % Cols * (Tile + Gap), y = Pad + Head + i / Cols * (th + Gap);
                dc.DrawImage(img, new Rect(x, y, Tile, th));
                var label = Text(TimeSpan.FromMilliseconds(time).ToString(@"h\:mm\:ss"), 14, Brushes.White);
                var box = new Rect(x + Tile - label.Width - 16, y + th - label.Height - 10, label.Width + 10, label.Height + 4);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), null, box, 4, 4);
                dc.DrawText(label, new Point(box.X + 5, box.Y + 2));
            }
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + " - contact sheet.png");
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        await using (var f = File.Create(file)) png.Save(f);
        return file;
    }
}
