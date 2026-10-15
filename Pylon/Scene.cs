using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pylon;

/// <summary>
/// The full-screen visualizer behind the music view (it replaced libVLC's Goom / scope): Radial (rays round the cover),
/// Aurora (soft ribbons across the stage) and Horizon (wide bars with a reflection), in the cover's colors. Kept cheap:
/// 30 updates a second while playing, a quiet "breathing" at 20 when paused, nothing while minimized; a few frozen
/// brushes and one geometry per layer, no effects.
/// </summary>
public class SceneView : FrameworkElement
{
    const int Bands = 48;
    readonly double[] _levels = new double[Bands];
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    bool _active, _idle, _hooked, _tapped;
    double _last, _phase, _bass;
    Color _a = Colors.Orange, _b = Colors.DeepPink;
    Brush? _fillA, _fillB, _glow, _ribbon0, _ribbon1, _ribbon2, _bar, _reflection;

    /// <summary>Where the cover is, in this element's coordinates (Radial draws round it); null when it isn't shown.</summary>
    public Func<Rect?>? Cover;

    public static string Style => Settings.Str("ui.scene");
    public static bool On => Style is "Radial" or "Aurora" or "Horizon";

    public SceneView()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Sync();
        Motion.AsleepChanged += Sync;
    }

    public bool Active { get => _active; set { if (_active != value) { _active = value; Sync(); } } }
    public bool Idle { get => _idle; set { if (_idle != value) { _idle = value; Sync(); } } }

    void Sync()
    {
        var shown = IsVisible && !Motion.Asleep && On;
        var tap = _active && shown;
        if (tap != _tapped)
        {
            _tapped = tap;
            if (tap) AudioTap.Subscribe();
            else AudioTap.Unsubscribe();
        }
        var on = (_active || _idle) && shown;
        if (on == _hooked) { if (!on) InvalidateVisual(); return; }
        _hooked = on;
        if (on) CompositionTarget.Rendering += Tick;
        else
        {
            CompositionTarget.Rendering -= Tick;
            Array.Clear(_levels);
            _bass = 0;
            InvalidateVisual();
        }
    }

    /// <summary>The style changed in Settings or the menu.</summary>
    public void Restyle() { Sync(); InvalidateVisual(); }

    /// <summary>Colors from the cover: its most colorful tone and a second, different one; the accent without a cover.</summary>
    public void SetCover(BitmapSource? cover)
    {
        var accent = (TryFindResource("Accent") as SolidColorBrush)?.Color ?? Colors.Orange;
        (_a, _b) = cover == null ? (accent, Shift(accent)) : Palette(cover, accent);
        var clear = Color.FromArgb(0, _a.R, _a.G, _a.B);
        _fillA = Frozen(new SolidColorBrush(_a));
        _fillB = Frozen(new SolidColorBrush(_b));
        _glow = Frozen(new RadialGradientBrush(Color.FromArgb(140, _a.R, _a.G, _a.B), clear));
        _ribbon0 = Frozen(Ribbon(_a, 120));
        _ribbon1 = Frozen(Ribbon(_b, 90));
        _ribbon2 = Frozen(Ribbon(Mix(_a, _b), 70));
        _bar = Frozen(new LinearGradientBrush(_a, _b, 90));
        _reflection = Frozen(new LinearGradientBrush(Color.FromArgb(70, _b.R, _b.G, _b.B), Color.FromArgb(0, _b.R, _b.G, _b.B), 90));
        InvalidateVisual();
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Brush Ribbon(Color c, byte alpha) => new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(0, c.R, c.G, c.B), 0), new(Color.FromArgb(alpha, c.R, c.G, c.B), 0.5), new(Color.FromArgb(0, c.R, c.G, c.B), 1),
    }, 90);
    static Color Mix(Color a, Color b) => Color.FromRgb((byte)((a.R + b.R) / 2), (byte)((a.G + b.G) / 2), (byte)((a.B + b.B) / 2));
    /// <summary>A neighbouring hue (40° on), when the cover has only one color.</summary>
    static Color Shift(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        var h = d == 0 ? 0 : max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h = ((h * 60 + 40) % 360 + 360) % 360 / 60;
        double s = max == 0 ? 0 : d / max, v = max, x = v * s * (1 - Math.Abs(h % 2 - 1)), m = v - v * s;
        var (r1, g1, b1) = (int)h switch { 0 => (v * s, x, 0.0), 1 => (x, v * s, 0.0), 2 => (0.0, v * s, x), 3 => (0.0, x, v * s), 4 => (x, 0.0, v * s), _ => (v * s, 0.0, x) };
        return Color.FromRgb((byte)((r1 + m) * 255), (byte)((g1 + m) * 255), (byte)((b1 + m) * 255));
    }

    /// <summary>The cover at 16×16: the most saturated pixel, and the most saturated one that's a different hue.</summary>
    static (Color, Color) Palette(BitmapSource cover, Color fallback)
    {
        try
        {
            var small = new FormatConvertedBitmap(new TransformedBitmap(cover, new ScaleTransform(16.0 / cover.PixelWidth, 16.0 / cover.PixelHeight)), PixelFormats.Bgra32, null, 0);
            var px = new byte[16 * 16 * 4];
            small.CopyPixels(px, 16 * 4, 0);
            var colors = Enumerable.Range(0, 256).Select(i => Color.FromRgb(px[i * 4 + 2], px[i * 4 + 1], px[i * 4])).ToList();
            static (double H, double S, double V) Hsv(Color c)
            {
                double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                var h = d == 0 ? 0 : max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                return (h * 60, max == 0 ? 0 : d / max, max);
            }
            double Score(Color c) { var (_, s, v) = Hsv(c); return s * (0.3 + v) * (v > 0.15 ? 1 : 0.1); }
            var first = colors.MaxBy(Score);
            if (Score(first) < 0.08) return (fallback, Shift(fallback)); // a grey cover: the accent reads better
            var hue = Hsv(first).H;
            var second = colors.Where(c => Math.Abs(((Hsv(c).H - hue + 540) % 360) - 180) > 40).DefaultIfEmpty(Shift(first)).MaxBy(Score);
            return (Lift(first), Lift(second));
        }
        catch (Exception e) when (e is NotSupportedException or ArgumentException or InvalidOperationException) { return (fallback, Shift(fallback)); }
    }

    // dark covers still give a visible glow
    static Color Lift(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max >= 170 || max == 0) return c;
        var k = 170.0 / max;
        return Color.FromRgb((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k));
    }

    void Tick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _last;
        if (dt < (_active && !Motion.Lite ? 1 / 31.0 : 1 / 21.0)) return;
        _last = now;
        dt = Math.Min(dt, 0.25);
        if (_active)
        {
            var bands = AudioTap.Bands(Bands);
            var fall = 0.02 * dt * 60;
            for (var i = 0; i < Bands; i++)
                _levels[i] = bands[i] > _levels[i] ? _levels[i] + (bands[i] - _levels[i]) * 0.55 : Math.Max(bands[i], _levels[i] - fall);
        }
        else
            for (var i = 0; i < Bands; i++) // paused: a slow breathing, levels ease into it
            {
                var x = i / (double)Bands;
                var goal = 0.10 + 0.06 * Math.Sin(now * 1.1 + x * 6) + 0.04 * Math.Sin(now * 0.6 - x * 11);
                _levels[i] += (goal - _levels[i]) * Math.Min(1, dt * 2.5);
            }
        var bass = (_levels[0] + _levels[1] + _levels[2] + _levels[3]) / 4;
        _bass += (bass - _bass) * Math.Min(1, dt * (bass > _bass ? 12 : 3));
        _phase += dt * (0.25 + 1.2 * _bass);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_hooked || ActualWidth <= 0 || !On) return;
        if (_fillA == null) SetCover(null);
        double w = ActualWidth, h = ActualHeight;
        switch (Style)
        {
            case "Radial": Radial(dc, w, h); break;
            case "Aurora": Aurora(dc, w, h); break;
            case "Horizon": Horizon(dc, w, h); break;
        }
    }

    /// <summary>Rays from a circle just outside the cover, mirrored left and right; a glow behind that swells with the bass.</summary>
    void Radial(DrawingContext dc, double w, double h)
    {
        var cover = Cover?.Invoke() ?? new Rect(w / 2 - 150, h / 2 - 150, 300, 300);
        var c = new Point(cover.X + cover.Width / 2, cover.Y + cover.Height / 2);
        var r0 = Math.Max(cover.Width, cover.Height) * 0.56;
        var reach = Math.Min(w, h) * 0.15;
        var glow = r0 * (1.35 + 0.35 * _bass);
        dc.DrawEllipse(_glow, null, c, glow, glow);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            const int rays = Bands * 2;
            for (var k = 0; k < rays; k++)
            {
                var band = k < Bands ? k : rays - 1 - k; // mirrored: bass at the top, treble at the bottom on both sides
                var len = 4 + _levels[band] * reach * (1 + 0.25 * _bass);
                var ang = -Math.PI / 2 + 2 * Math.PI * k / rays + _phase * 0.15;
                double cos = Math.Cos(ang), sin = Math.Sin(ang);
                g.BeginFigure(new Point(c.X + cos * r0, c.Y + sin * r0), false, false);
                g.LineTo(new Point(c.X + cos * (r0 + len), c.Y + sin * (r0 + len)), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(_bar, Math.Max(2, r0 * 2 * Math.PI / (Bands * 2) * 0.45)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }

    /// <summary>Three soft ribbons drifting across the stage; their swell follows the music across the spectrum.</summary>
    void Aurora(DrawingContext dc, double w, double h)
    {
        Brush?[] fills = [_ribbon0, _ribbon1, _ribbon2];
        for (var r = 0; r < 3; r++)
        {
            var mid = h * (0.38 + 0.14 * r);
            var thick = h * (0.16 + 0.05 * r) * (1 + 0.6 * _bass);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                const int steps = 64;
                Point P(int i, double side)
                {
                    var x = w * i / steps;
                    var band = _levels[Math.Min(Bands - 1, i * Bands / (steps + 1))];
                    var y = mid + Math.Sin(i * 0.11 + _phase * (1 + r * 0.4) + r * 2) * h * 0.06 + Math.Sin(i * 0.05 - _phase * 0.7) * h * 0.04;
                    return new Point(x, y + side * thick * (0.35 + band));
                }
                g.BeginFigure(P(0, -1), true, true);
                for (var i = 1; i <= steps; i++) g.LineTo(P(i, -1), false, false);
                for (var i = steps; i >= 0; i--) g.LineTo(P(i, 1), false, false);
            }
            geo.Freeze();
            dc.DrawGeometry(fills[r], null, geo);
        }
    }

    /// <summary>Wide rounded bars along the bottom of the stage, with a faint reflection under the line.</summary>
    void Horizon(DrawingContext dc, double w, double h)
    {
        var floor = h * 0.78;
        var slot = w / Bands;
        var bw = slot * 0.62;
        var bars = new StreamGeometry();
        var mirror = new StreamGeometry();
        using (var g = bars.Open())
        using (var m = mirror.Open())
            for (var i = 0; i < Bands; i++)
            {
                var bh = 3 + _levels[i] * h * 0.42;
                var x = i * slot + (slot - bw) / 2;
                Box(g, new Rect(x, floor - bh, bw, bh));
                Box(m, new Rect(x, floor + 4, bw, bh * 0.35));
            }
        bars.Freeze();
        mirror.Freeze();
        dc.DrawGeometry(_bar, null, bars);
        dc.DrawGeometry(_reflection, null, mirror);
    }

#if DEBUG
    /// <summary>A fixed pattern instead of captured sound (snapshot tests have no audio to capture).</summary>
    public void DebugFill()
    {
        for (var i = 0; i < Bands; i++) _levels[i] = 0.25 + 0.55 * Math.Abs(Math.Sin(i * 0.21 + 1.1)) * (1 - i / (double)Bands * 0.5);
        _bass = 0.6;
        _phase = 1.7;
        CompositionTarget.Rendering -= Tick;
        _hooked = true;
        InvalidateVisual();
    }
#endif

    static void Box(StreamGeometryContext g, Rect r)
    {
        g.BeginFigure(r.TopLeft, true, true);
        g.LineTo(r.TopRight, false, false);
        g.LineTo(r.BottomRight, false, false);
        g.LineTo(r.BottomLeft, false, false);
    }
}
