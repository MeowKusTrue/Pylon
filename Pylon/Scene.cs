using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pylon;

/// <summary>
/// The full-screen visualizer behind the music view (it replaced libVLC's Goom / scope): Bio (living, overlapping blobs
/// round the cover), Wave (thin bending rings, after Yandex Music's "My Wave"), Bloom (turning petals), Ripple (rings sent
/// out by the bass), Radial (rays round the cover), Aurora (soft ribbons across the stage)
/// and Horizon (wide bars with a reflection), in the cover's colors. Kept cheap: at most "ui.scenefps" updates a second
/// while playing (30 in light mode), a quiet "breathing" at up to 30 when paused, nothing while minimized; a few frozen
/// brushes and one geometry per layer, no effects (softness comes from gradients, not blur).
/// </summary>
public class SceneView : FrameworkElement
{
    const int Bands = 48;
    readonly double[] _levels = new double[Bands], _smooth = new double[Bands];
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    bool _active, _idle, _hooked, _tapped;
    double _last, _phase, _bass, _mid, _high, _fps = 60;
    Color _a = Colors.Orange, _b = Colors.DeepPink;
    Brush? _fillA, _fillB, _glow, _ribbon0, _ribbon1, _ribbon2, _bar, _reflection, _dot;
    Brush[] _bio = [];
    Pen? _line0, _line1;

    /// <summary>Where the cover is, in this element's coordinates (Radial draws round it); null when it isn't shown.</summary>
    public Func<Rect?>? Cover;

    /// <summary>The cover's most colorful tone (the accent without a cover).</summary>
    public Color Main => _a;

    public static string Style => Settings.Str("ui.scene");
    public static bool On => Style is "Bio" or "Wave" or "Bloom" or "Ripple" or "Radial" or "Aurora" or "Horizon";

    public SceneView()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Sync();
        Motion.AsleepChanged += Sync;
        _fps = Math.Clamp(Settings.Num("ui.scenefps"), 15, 240);
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

    /// <summary>The style or the frame rate changed in Settings or the menu.</summary>
    public void Restyle()
    {
        _fps = Math.Clamp(Settings.Num("ui.scenefps"), 15, 240);
        Sync();
        InvalidateVisual();
    }

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
        // Bio: outer layers fainter; each fades toward its rim, so the overlaps glow and the edges stay soft without blur
        Color[] tones = [_a, Mix(_a, _b), _b, _a, _b];
        double[] alpha = [0.46, 0.32, 0.24, 0.16, 0.10];
        _bio = [.. tones.Select((c, k) => Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb((byte)(alpha[k] * 255), c.R, c.G, c.B), 0),
            new(Color.FromArgb((byte)(alpha[k] * 230), c.R, c.G, c.B), 0.7),
            new(Color.FromArgb((byte)(alpha[k] * 160), c.R, c.G, c.B), 1),
        })))];
        _line0 = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(190, Mix(_a, Colors.White).R, Mix(_a, Colors.White).G, Mix(_a, Colors.White).B))), 2);
        _line1 = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(140, _b.R, _b.G, _b.B))), 1.5);
        _line0.Freeze();
        _line1.Freeze();
        _dot = Frozen(new SolidColorBrush(Color.FromArgb(170, Mix(_a, Colors.White).R, Mix(_a, Colors.White).G, Mix(_a, Colors.White).B)));
        _wavePens = [.. Enumerable.Range(0, 14).Select(k =>
        {
            var q = k / 14.0;
            var p = new Pen(Frozen(new SolidColorBrush(WithAlpha(Lerp(_a, _b, q), 0.85 * (1 - q) + 0.08))), 1.8 - q);
            p.Freeze();
            return p;
        })];
        Color[] petals = [_b, Mix(_a, _b), _a];
        double[] petalAlpha = [0.14, 0.2, 0.3];
        _bloomFills = [.. petals.Select((c, j) => Frozen(new RadialGradientBrush(WithAlpha(c, petalAlpha[j] * 1.6), WithAlpha(c, petalAlpha[j] * 0.5))))];
        _bloomPens = [.. petals.Select(c => { var p = new Pen(Frozen(new SolidColorBrush(WithAlpha(Lerp(c, Colors.White, 0.35), 0.55))), 1.4); p.Freeze(); return p; })];
        _rippleFill = Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(WithAlpha(_a, 0.6), 0), new(WithAlpha(Mix(_a, _b), 0.35), 0.7), new(WithAlpha(_b, 0.15), 1),
        }));
        InvalidateVisual();
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Brush Ribbon(Color c, byte alpha) => new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(0, c.R, c.G, c.B), 0), new(Color.FromArgb(alpha, c.R, c.G, c.B), 0.5), new(Color.FromArgb(0, c.R, c.G, c.B), 1),
    }, 90);
    static Color Lerp(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
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
        // Rendering fires at the monitor's rate (180 Hz here): only every frame-rate step does the work
        var fps = Motion.Lite ? 30 : _active ? _fps : Math.Min(_fps, 30);
        if (dt < 1 / (fps + 1)) return;
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
        double mid = 0, high = 0;
        for (var i = 6; i < 22; i++) mid += _levels[i] / 16;
        for (var i = 22; i < Bands; i++) high += _levels[i] / (Bands - 22);
        _mid += (mid - _mid) * Math.Min(1, dt * 4);
        _high += (high - _high) * Math.Min(1, dt * 4);
        for (var i = 0; i < Bands; i++) // neighbours averaged: rims swell in waves, not spikes
            _smooth[i] = (_levels[Math.Max(0, i - 2)] + 2 * _levels[Math.Max(0, i - 1)] + 3 * _levels[i] + 2 * _levels[Math.Min(Bands - 1, i + 1)] + _levels[Math.Min(Bands - 1, i + 2)]) / 9;
        switch (Style)
        {
            case "Bio": MoveMotes(dt); break;
            case "Ripple": MoveRings(dt, now); break;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_hooked || ActualWidth <= 0 || !On) return;
        if (_fillA == null) SetCover(null);
        double w = ActualWidth, h = ActualHeight;
        switch (Style)
        {
            case "Bio": Bio(dc, w, h); break;
            case "Wave": Wave(dc, w, h); break;
            case "Bloom": Bloom(dc, w, h); break;
            case "Ripple": Ripple(dc, w, h); break;
            case "Radial": Radial(dc, w, h); break;
            case "Aurora": Aurora(dc, w, h); break;
            case "Horizon": Horizon(dc, w, h); break;
        }
    }

    // ───────── Bio ─────────

    struct Mote { public double Angle, Dist, Speed, Size; }
    readonly Mote[] _motes = new Mote[26];
    readonly Random _rnd = new(7);
    readonly List<Point> _rim = new(97);

    /// <summary>Specks drifting out from the blob, faster when the music is busy; each starts again near the middle.</summary>
    void MoveMotes(double dt)
    {
        for (var i = 0; i < _motes.Length; i++)
        {
            ref var m = ref _motes[i];
            m.Dist += dt * m.Speed * (0.06 + 0.35 * (_mid + _bass));
            if (m.Speed > 0 && m.Dist < 1) continue;
            m = new Mote { Angle = _rnd.NextDouble() * 2 * Math.PI, Dist = m.Speed == 0 ? _rnd.NextDouble() : 0, Speed = 0.5 + _rnd.NextDouble(), Size = 1.2 + _rnd.NextDouble() * 2.2 };
        }
    }

    /// <summary>
    /// Five overlapping, slowly morphing blobs round the cover (outer ones larger and fainter), turning against each other;
    /// the bass swells them all, mids and highs ruffle their rims, and the spectrum runs along the rim (bass at the top,
    /// mirrored left and right). Two thin outlines and drifting specks on top, a wide glow behind.
    /// </summary>
    void Bio(DrawingContext dc, double w, double h)
    {
        var (c, r0) = Center(w, h);
        var glow = r0 * (1.7 + 0.5 * _bass);
        dc.DrawEllipse(_glow, null, c, glow, glow);
        for (var k = _bio.Length - 1; k >= 0; k--)
            dc.DrawGeometry(_bio[k], null, Blob(c, r0 * (1 + 0.11 * k) * (1 + (0.12 - 0.015 * k) * _bass), k, 1));
        dc.DrawGeometry(null, _line0, Blob(c, r0 * 1.06 * (1 + 0.1 * _bass), 7, 1.4));
        dc.DrawGeometry(null, _line1, Blob(c, r0 * 1.3 * (1 + 0.08 * _bass), 9, 1.2));
        foreach (var m in _motes)
        {
            var d = r0 * (1.05 + m.Dist * 1.7);
            dc.DrawEllipse(_dot, null, new Point(c.X + Math.Cos(m.Angle) * d, c.Y + Math.Sin(m.Angle) * d), m.Size * (1 - m.Dist), m.Size * (1 - m.Dist));
        }
    }

    /// <summary>The cover's middle and a base radius just past its edge; the stage's middle when the cover isn't shown.</summary>
    (Point C, double R0) Center(double w, double h) => Cover?.Invoke() is { } r
        ? (new Point(r.X + r.Width / 2, r.Y + r.Height / 2), Math.Max(r.Width, r.Height) * 0.56)
        : (new Point(w / 2, h / 2), Math.Min(w, h) * 0.2);

    /// <summary>One closed blob: a circle whose radius waves over the angle (three slow harmonics plus the spectrum).</summary>
    Geometry Blob(Point c, double radius, int k, double ruffle)
    {
        double t = _phase, dir = k % 2 == 0 ? 1 : -1;
        double a3 = (0.045 + 0.06 * _mid) * ruffle, a5 = (0.03 + 0.05 * _high) * ruffle, a2 = 0.035 * ruffle, spec = 0.11 / (1 + 0.25 * k);
        return Rim(c, radius, (th, band) => 1 + a3 * Math.Sin(3 * th + dir * t * 0.9 + k * 1.3) + a5 * Math.Sin(5 * th - dir * t * 1.3 + k * 0.7)
            + a2 * Math.Sin(2 * th + t * 0.5 + k * 2.1) + spec * _smooth[band]);
    }

    /// <summary>
    /// A closed outline round <paramref name="c"/>: radius × shape(angle from the top, clockwise; the spectrum band at that
    /// angle, bass at the top, mirrored left and right). n points of straight lines are smooth at the sizes drawn here.
    /// </summary>
    Geometry Rim(Point c, double radius, Func<double, int, double> shape, int n = 96)
    {
        _rim.Clear();
        for (var i = 0; i < n; i++)
        {
            var th = 2 * Math.PI * i / n;
            var f = i / (double)n;
            var rr = radius * shape(th, (int)((f < 0.5 ? f * 2 : (1 - f) * 2) * (Bands - 1)));
            var ang = th - Math.PI / 2;
            _rim.Add(new Point(c.X + Math.Cos(ang) * rr, c.Y + Math.Sin(ang) * rr));
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(_rim[0], true, true);
            g.PolyLineTo(_rim, true, true); // stroked: the outlines (Wave, Bloom, Bio) need it; fills ignore it
        }
        geo.Freeze();
        return geo;
    }

    // ───────── Wave, Bloom, Ripple ─────────

    Pen[] _wavePens = [];
    Brush[] _bloomFills = [];
    Pen[] _bloomPens = [];
    Brush? _rippleFill;

    /// <summary>
    /// Like Yandex Music's "My Wave" (their article: bass sets how deep the color fills, mids how much the wave bends):
    /// fourteen thin rings round the cover, bent by the mids and turning both ways, over a fill that deepens with the bass.
    /// </summary>
    void Wave(DrawingContext dc, double w, double h)
    {
        var (c, r0) = Center(w, h);
        var fill = new RadialGradientBrush(new GradientStopCollection
        {
            new(WithAlpha(_a, 0.15 + 0.55 * _bass), 0),
            new(WithAlpha(Mix(_a, _b), 0.12 + 0.25 * _bass), 0.35 + 0.4 * _bass),
            new(WithAlpha(_b, 0), 1),
        });
        fill.Freeze();
        var fr = r0 * (1.25 + 0.7 * _bass);
        dc.DrawEllipse(fill, null, c, fr, fr);
        for (var k = 0; k < _wavePens.Length; k++)
        {
            double q = k / (double)_wavePens.Length, dir = k % 2 == 0 ? 1 : -1, kk = k;
            double amp = (0.015 + 0.11 * _mid) * (1 - q * 0.45), rot = _phase * (0.35 + 0.06 * k) * dir;
            dc.DrawGeometry(null, _wavePens[k], Rim(c, r0 * (0.95 + q * 1.05) * (1 + 0.06 * _bass * (1 - q)),
                (th, band) => 1 + amp * Math.Sin(4 * th + rot + kk * 0.45) + amp * 0.6 * Math.Sin(7 * th - rot * 1.3 + kk) + 0.06 * _smooth[band] * (1 - q), 128));
        }
    }

    /// <summary>Three layers of petals (11, 8, 6), turning against each other; highs, mids and bass open them in turn.</summary>
    void Bloom(DrawingContext dc, double w, double h)
    {
        var (c, r0) = Center(w, h);
        (int N, double Scale)[] layers = [(11, 1.55), (8, 1.32), (6, 1.12)];
        for (var j = 0; j < layers.Length; j++)
        {
            var (n, scale) = layers[j];
            double amp = 0.06 + 0.22 * (j == 0 ? _high : j == 1 ? _mid : _bass), rot = _phase * (0.25 + 0.1 * j) * (j % 2 == 0 ? 1 : -1);
            var geo = Rim(c, r0 * scale * (1 + 0.14 * _bass),
                (th, band) => 1 - amp + amp * Math.Pow(Math.Abs(Math.Cos(n * th / 2 + rot)), 1.5) + 0.05 * _smooth[band], 160);
            dc.DrawGeometry(_bloomFills[j], _bloomPens[j], geo);
        }
    }

    struct Ring { public double R, Life; public bool B; }
    readonly List<Ring> _rings = [];
    double _bassAvg, _lastHit;

    /// <summary>Ripple: each bass hit (well above its recent average) sends a ring out; they widen and fade.</summary>
    void MoveRings(double dt, double now)
    {
        _bassAvg += (_bass - _bassAvg) * Math.Min(1, dt * 1.5);
        var r0 = Center(ActualWidth, ActualHeight).R0;
        if (_bass > _bassAvg * 1.25 && _bass > 0.22 && now - _lastHit > 0.22 && _rings.Count < 12)
        {
            _rings.Add(new Ring { R = r0 * 0.95, Life = 1, B = _rings.Count % 2 == 1 });
            _lastHit = now;
        }
        for (var i = _rings.Count - 1; i >= 0; i--)
        {
            var g = _rings[i];
            g.R += dt * r0 * (0.9 + _bass);
            g.Life -= dt * 0.55;
            if (g.Life <= 0) _rings.RemoveAt(i);
            else _rings[i] = g;
        }
    }

    void Ripple(DrawingContext dc, double w, double h)
    {
        var (c, r0) = Center(w, h);
        foreach (var g in _rings)
        {
            var brush = new SolidColorBrush(WithAlpha(g.B ? _b : _a, g.Life * 0.8));
            brush.Freeze();
            dc.DrawEllipse(null, new Pen(brush, 1.5 + 5 * g.Life), c, g.R, g.R);
        }
        dc.DrawGeometry(_rippleFill, null, Blob(c, r0 * (1.02 + 0.14 * _bass), 2, 0.8));
    }

    static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

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
        Array.Copy(_levels, _smooth, Bands);
        (_bass, _mid, _high) = (0.6, 0.45, 0.3);
        _phase = 1.7;
        MoveMotes(0);
        _rings.Clear();
        var r0 = Center(ActualWidth, ActualHeight).R0;
        for (var k = 0; k < 4; k++) _rings.Add(new Ring { R = r0 * (1.1 + 0.35 * k), Life = 1 - 0.22 * k, B = k % 2 == 1 });
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
