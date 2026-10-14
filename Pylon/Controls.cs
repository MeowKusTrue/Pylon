using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Pylon;

/// <summary>
/// Phosphor icon from Assets/Icons/{Glyph}.svg ("house", "house-fill", "file-duotone"), 24px by default.
/// Tint replaces currentColor: a hex color or "@Key" for a theme brush (default "@FgA2").
/// Duotone layers get the accent color, like the mockup's two-tone icons. Re-renders when the theme changes.
/// </summary>
public class Icon : Image
{
    static readonly Dictionary<string, DrawingImage?> Cache = new();

    public static void ClearCache() => Cache.Clear();

    /// <summary>"@Key" → current theme color as #RRGGBB.</summary>
    public static string Resolve(string tint)
    {
        if (!tint.StartsWith('@')) return tint;
        var c = (Application.Current.TryFindResource(tint[1..]) as SolidColorBrush)?.Color ?? Colors.Gray;
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(Icon), new(null, (d, _) => ((Icon)d).Update()));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(string), typeof(Icon), new(null, (d, _) => ((Icon)d).Update()));

    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string? Tint { get => (string?)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    public Icon()
    {
        Width = Height = 24;
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => { ThemeManager.Changed += Update; Update(); };
        Unloaded += (_, _) => ThemeManager.Changed -= Update;
    }

    void Update() => Source = string.IsNullOrEmpty(Glyph) ? null : Load(Glyph, Tint);

    public static DrawingImage? Load(string glyph, string? tint = null)
    {
        var key = glyph + "|" + tint;
        if (Cache.TryGetValue(key, out var img)) return img;
        string svg;
        try
        {
            using var res = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Icons/{glyph}.svg")).Stream;
            svg = new StreamReader(res).ReadToEnd();
        }
        catch (IOException)
        {
            return Cache[key] = null; // unknown glyph: render nothing rather than crash (run tools/sync-icons.py)
        }
        svg = svg.Replace("currentColor", Resolve(tint ?? "@FgA2"))
                 .Replace("accentColor", Resolve("@Accent"))
                 .Replace("opacity=\"0.2\"", $"fill=\"{Resolve("@Accent")}\" opacity=\"0.55\"");
        var reader = new FileSvgReader(new WpfDrawingSettings
        {
            IncludeRuntime = false, TextAsGeometry = false, EnsureViewboxSize = true, EnsureViewboxPosition = true,
        });
        img = new DrawingImage(reader.Read(new MemoryStream(Encoding.UTF8.GetBytes(svg))));
        img.Freeze();
        return Cache[key] = img;
    }
}

/// <summary>Seek bar: video style (filled track + 8px thumb) or music style (waveform bars + 4px thumb).</summary>
public class SeekBar : FrameworkElement
{
    static Brush B(string key) => (Brush)Application.Current.FindResource(key); // theme brush, looked up per frame
    static readonly Brush Hint = Frozen("#22808080");

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WaveformProperty = DependencyProperty.Register(nameof(Waveform), typeof(bool), typeof(SeekBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Waveform { get => (bool)GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public bool IsDragging => IsMouseCaptured;

    /// <summary>Raised while the user clicks/drags, with the new position 0..1.</summary>
    public event Action<double>? Seek;

    // decorative pseudo-waveform seeded from the file name (like the mockup) until the real one is decoded
    byte[] _bars = [];
    byte[]? _peaks;
    public void SetWaveSeed(string seed)
    {
        _peaks = null;
        var heights = new byte[] { 2, 4, 6, 8, 10, 12, 14, 14, 14, 8, 8 };
        var rnd = new Random(seed.Aggregate(17, (h, c) => unchecked(h * 31 + c))); // stable across runs
        _bars = Enumerable.Range(0, 400).Select(_ => heights[rnd.Next(heights.Length)]).ToArray();
        InvalidateVisual();
    }

    /// <summary>Real loudness per bin (0..255) from <see cref="Pylon.Waveform"/>; replaces the decorative bars.</summary>
    public void SetPeaks(byte[]? peaks)
    {
        _peaks = peaks;
        InvalidateVisual();
    }

    double _loopA = double.NaN, _loopB = double.NaN;

    /// <summary>A-B loop range as positions 0..1 (NaN = not set); B alone stays NaN while only A is marked.</summary>
    public void SetLoop(double a, double b)
    {
        _loopA = a;
        _loopB = b;
        InvalidateVisual();
    }

    double[] _chapters = [], _bookmarks = [];

    /// <summary>Chapter starts and bookmarks as positions 0..1.</summary>
    public void SetMarks(double[] chapters, double[] bookmarks)
    {
        _chapters = chapters;
        _bookmarks = bookmarks;
        InvalidateVisual();
    }

    double _hover = -1;

    public SeekBar()
    {
        Height = 28;
        Cursor = Cursors.Hand;
        SetWaveSeed("");
        ThemeManager.Changed += InvalidateVisual;
    }

    static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        Brush Track = B("BgB2"), Fill = B("AccentBright"), Thumb = B("Accent"), Played = B("AccentDim"), Unplayed = B("FgA4"), FgHover = B("FgA3");
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), 6, 6);
        var v = Math.Clamp(Value, 0, 1);
        if (!double.IsNaN(_loopA))
        {
            var accent = (Thumb as SolidColorBrush)?.Color ?? Colors.Orange;
            double xa = 2 + _loopA * (w - 4), xb = double.IsNaN(_loopB) ? xa : 2 + _loopB * (w - 4);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B)), null, new Rect(xa, 0, Math.Max(2, xb - xa), h), 4, 4);
            dc.DrawRectangle(Thumb, null, new Rect(xa - 1, 0, 2, h));
            if (!double.IsNaN(_loopB)) dc.DrawRectangle(Thumb, null, new Rect(xb - 1, 0, 2, h));
        }
        if (!Waveform)
        {
            var x = 2 + v * (w - 12);
            if (_hover >= 0) dc.DrawRoundedRectangle(Hint, null, new Rect(2, 2, Math.Max(0, _hover - 2), h - 4), 4, 4);
            dc.DrawRoundedRectangle(Fill, null, new Rect(2, 2, x + 6, h - 4), 4, 4);
            var gap = B("BgA3"); // window color: reads as a gap on both the played and the unplayed part
            foreach (var c in _chapters) dc.DrawRectangle(gap, null, new Rect(2 + c * (w - 4) - 1.5, 0, 3, h)); // chapters split the bar into segments
            DrawBookmarks(dc, w, Thumb);
            dc.DrawRoundedRectangle(Thumb, null, new Rect(x, 2, 8, h - 4), 4, 4);
            return;
        }
        var tx = 2 + v * (w - 8);
        int i = 0, count = Math.Max(1, (int)Math.Ceiling((w - 10) / 5));
        for (double bx = 6; bx < w - 4; bx += 5, i++)
        {
            double bh = _peaks is { Length: > 0 } p ? 2 + p[Math.Min(p.Length - 1, i * p.Length / count)] / 255.0 * (h - 10) : _bars[i % _bars.Length];
            var brush = bx + 2 < tx ? Played : (_hover >= 0 && bx < _hover ? FgHover : Unplayed);
            dc.DrawRoundedRectangle(brush, null, new Rect(bx, (h - bh) / 2, 2, bh), 1, 1);
        }
        foreach (var c in _chapters) dc.DrawRectangle(FgHover, null, new Rect(2 + c * (w - 4) - 0.5, 3, 1, h - 6));
        DrawBookmarks(dc, w, Thumb);
        dc.DrawRoundedRectangle(Thumb, null, new Rect(tx, 2, 4, h - 4), 2, 2);
    }

    void DrawBookmarks(DrawingContext dc, double w, Brush brush)
    {
        foreach (var b in _bookmarks) // small accent drop hanging from the top edge
            dc.DrawRoundedRectangle(brush, null, new Rect(2 + b * (w - 4) - 3, 0, 6, 7), 3, 3);
    }

    double PosAt(MouseEventArgs e) => Math.Clamp((e.GetPosition(this).X - 2) / Math.Max(1, ActualWidth - 4), 0, 1);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        Value = PosAt(e);
        Seek?.Invoke(Value);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = e.GetPosition(this).X;
        if (IsMouseCaptured)
        {
            Value = PosAt(e);
            Seek?.Invoke(Value);
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();
}

/// <summary>Wedge volume control from the mockup: green→orange→red gradient, 0..1 maps to 0..125%.</summary>
public class VolumeBar : FrameworkElement
{
    static readonly Geometry Bg = Geometry.Parse("M3.59756 19.0454C1.55468 18.8388 0 17.119 0 15.0657V12.2975C0 10.2442 1.55467 8.52441 3.59756 8.31783L85.6982 0.0155207C87.4654 -0.163183 89 1.2241 89 3.0003V24.363C89 26.1392 87.4654 27.5264 85.6982 27.3477L3.59756 19.0454Z");
    static readonly Geometry Wedge = Geometry.Parse("M1.79183 15.9675C0.773415 15.8609 0 15.0023 0 13.9783V10.5816C0 9.55761 0.773416 8.69903 1.79183 8.59245L83.7918 0.0110748C84.9723 -0.112463 86 0.813294 86 2.00021V22.5597C86 23.7467 84.9723 24.6724 83.7918 24.5489L1.79183 15.9675Z");

    static readonly Brush Shade = new SolidColorBrush(Color.FromArgb(51, 0, 0, 0));
    static readonly Brush Gradient = new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromRgb(0x95, 0xD3, 0x18), 0), new(Color.FromRgb(0xB2, 0xA9, 0x18), 0.5788),
        new(Color.FromRgb(0xD3, 0x79, 0x18), 0.7924), new(Color.FromRgb(0xFF, 0x37, 0x37), 1),
    }, new Point(-2.5 / 86, 0), new Point(87.5 / 86, 0));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(VolumeBar),
        new FrameworkPropertyMetadata(0.8, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MutedProperty = DependencyProperty.Register(nameof(Muted), typeof(bool), typeof(VolumeBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Muted { get => (bool)GetValue(MutedProperty); set => SetValue(MutedProperty, value); }
    public event Action<double>? Changed;

    public VolumeBar()
    {
        Width = 88;
        Height = 28;
        Cursor = Cursors.Hand;
        ThemeManager.Changed += InvalidateVisual;
        ToolTipService.SetInitialShowDelay(this, 300);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable
        dc.PushTransform(new TranslateTransform(-0.5, 0.32));
        dc.DrawGeometry((Brush)Application.Current.FindResource("BgB2"), null, Bg);
        dc.Pop();
        dc.PushTransform(new TranslateTransform(1.5, 1.72));
        dc.PushClip(new RectangleGeometry(new Rect(0, -2, 86 * Math.Clamp(Value, 0, 1), 30)));
        dc.PushOpacity(Muted ? 0.25 : 1);
        dc.DrawGeometry(Gradient, null, Wedge);
        dc.DrawGeometry(Shade, null, Wedge);
        dc.Pop(); dc.Pop(); dc.Pop();
    }

    void Set(MouseEventArgs e)
    {
        Value = Math.Clamp((e.GetPosition(this).X - 1.5) / 86, 0, 1);
        ToolTip = $"{Math.Round(Value * 125)}%";
        Changed?.Invoke(Value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { CaptureMouse(); Set(e); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Set(e); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value = Math.Clamp(Value + Math.Sign(e.Delta) * 0.04, 0, 1);
        ToolTip = $"{Math.Round(Value * 125)}%";
        Changed?.Invoke(Value);
        e.Handled = true;
    }
}
