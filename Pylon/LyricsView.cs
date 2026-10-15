using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Pylon;

/// <summary>
/// Synced lyrics that move like Spicy Lyrics (the same model as the spicy-translate preview, reimplemented, no code copied):
/// - every word rides three springs (scale, lift, glow) whose goals follow curves over the word's progress, and a soft
///   gradient sweeps through it;
/// - a syllable of 1 s or more is sung letter by letter: the sung letter swells, its neighbours a little less;
/// - a gap of 3 s or more between lines shows three breathing dots;
/// - other lines are dimmed and blurred by their distance from the current one; hovering one brings it back;
/// - the page glides to keep the current line in the middle, the wheel scrolls freely for a while, a click seeks.
/// </summary>
public class LyricsView : Grid
{
    /// <summary>Damped spring (frequency in Hz, damping ratio &lt; 1), solved exactly per step, so any frame time is stable.</summary>
    sealed class Sp(double p, double f, double d)
    {
        public double P = p, V, G = p;

        public double Step(double dt)
        {
            double w = f * 2 * Math.PI, wd = w * Math.Sqrt(1 - d * d), o = P - G, e = Math.Exp(-d * w * dt), cs = Math.Cos(wd * dt), sn = Math.Sin(wd * dt);
            P = G + e * (o * cs + (V + d * w * o) / wd * sn);
            V = e * (V * cs - (w * w * o + d * w * V) / wd * sn);
            return P;
        }
    }

    /// <summary>A word, a letter of a long syllable, or an interlude dot.</summary>
    sealed class Part
    {
        public required FrameworkElement El;
        public double A, B;
        public GradientStop? E0, E1;          // fill edge; null on a letter container
        public readonly ScaleTransform Scale = new(1, 1);
        public readonly TranslateTransform Lift = new();
        public Sp? S, Y, G, O;
        public Part[]? Letters;
        public bool Dot, Plain;
        public double Gp = double.NaN, Sc = double.NaN, Ly = double.NaN, Op = double.NaN, Gb = double.NaN, Go = double.NaN; // last written
    }

    sealed class LineView
    {
        public LyricLine? Line;             // null: interlude dots
        public required Border Root;
        public required BlurEffect Blur;
        public readonly List<Part> Parts = [];
        public ScaleTransform? DotGroup;
        public double A, B, Top;
        public int State = -1;              // 0 not yet, 1 being sung, 2 sung
        public bool Hover, Hidden;
        public int Painted = -1;            // the state an off-screen line was last settled in (-1: paint every frame)
    }

    // goals over a word's progress (0 = not sung, 1 = sung); lifts are in font sizes
    static readonly Func<double, double>
        CScale = Curve((0, 0.95), (0.7, 1.0505), (1, 1)), CLift = Curve((0, 0.01), (0.9, -1 / 60.0), (1, 0)), CGlow = Curve((0, 0), (0.15, 1), (0.6, 1), (1, 0)),
        LScale = Curve((0, 0.95), (0.7, 1.175), (1, 1)), LLift = Curve((0, 0.01), (0.9, -1 / 56.0), (1, 0)),
        DScale = Curve((0, 0.75), (0.7, 1.05), (1, 1)), DLift = Curve((0, 0), (0.9, -0.12), (1, 0)), DGlow = Curve((0, 0), (0.6, 1), (1, 1)), DOp = Curve((0, 0.35), (0.6, 1), (1, 1));

    const double LetterMin = 1, LetterEarly = 0.25, GapDots = 3, DotPad = 0.55, DotHide = 0.5, BlurStep = 1.25, Look = 0.3;
    const double Sung = 0.85, Unsung = 0.5, BgSung = 0.6, BgUnsung = 0.3; // fill alphas

    readonly StackPanel _stack = new();
    readonly TranslateTransform _scroll = new();
    readonly TextBlock _message = new() { FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    readonly List<LineView> _lines = [];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    LyricsDoc? _doc;
    LineView? _target;
    int _blurFor = -2;
    bool _hooked;
    internal bool DebugHooked => _hooked;
    double _lastFrame, _ls = 34;
    DateTime _manualUntil;
    Color _fg = Colors.White;

    /// <summary>Playback position in seconds (interpolated by the host between libVLC time updates).</summary>
    public Func<double>? Clock;

    /// <summary>The user clicked a line: seek here (seconds).</summary>
    public event Action<double>? Seek;

    public LyricsView()
    {
        ClipToBounds = true;
        _stack.RenderTransform = _scroll;
        // on a Canvas the stack keeps its full height; arranged by a Grid it was cut to the visible height *before*
        // scrolling, so every line further down came out empty
        var canvas = new Canvas();
        canvas.Children.Add(_stack);
        Children.Add(canvas);
        Children.Add(_message);
        Children.Add(Scrollbar());
        _message.SetResourceReference(TextBlock.ForegroundProperty, "FgA3");
        IsVisibleChanged += (_, _) => Hook();
        Motion.AsleepChanged += Hook;
        ThemeManager.Changed += () => { if (_doc != null) Show(_doc); };
        Background = Brushes.Transparent; // the wheel works over the gaps between lines too
        // No OpacityMask for the edge fade: WPF maps it over the content's bounds, which scroll, so it slid along and
        // hid most lines. Each line fades by its own on-screen position instead (see Update).
        SizeChanged += (_, _) =>
        {
            _stack.Width = Math.Max(0, ActualWidth - 16);
            Canvas.SetLeft(_stack, 8);
            _target = null;
            if (_doc != null && Math.Abs(FontFor(ActualWidth) - _ls) > 1.5) Show(_doc); // type follows the column width
        };
        _stack.SizeChanged += (_, _) => _topsDirty = true;
    }

    // ───────── scrollbar: a slim thumb like the app's others; shows on hover and while scrolling, can be dragged ─────────

    readonly Border _bar = new() { Width = 4, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 4, 0), Opacity = 0, Cursor = Cursors.Arrow };
    readonly TranslateTransform _barY = new();
    readonly DispatcherTimer _barHide = new() { Interval = TimeSpan.FromSeconds(1.2) };
    double _dragFrom = double.NaN, _dragScroll;

    Border Scrollbar()
    {
        _bar.RenderTransform = _barY;
        _bar.SetResourceReference(Border.BackgroundProperty, "FgA3");
        _scroll.Changed += (_, _) => PlaceBar(); // follows the springs without a frame hook of its own
        SizeChanged += (_, _) => PlaceBar();
        _stack.SizeChanged += (_, _) => PlaceBar();
        MouseEnter += (_, _) => ShowBar(true);
        MouseLeave += (_, _) => { if (double.IsNaN(_dragFrom)) ShowBar(false); };
        _barHide.Tick += (_, _) => { _barHide.Stop(); if (!IsMouseOver && double.IsNaN(_dragFrom)) ShowBar(false); };
        _bar.MouseEnter += (_, _) => Motion.Animate(_bar, WidthProperty, 8, Spring.Snappy);
        _bar.MouseLeave += (_, _) => { if (double.IsNaN(_dragFrom)) Motion.Animate(_bar, WidthProperty, 4, Spring.Snappy); };
        _bar.MouseLeftButtonDown += (_, e) => { _dragFrom = e.GetPosition(this).Y; _dragScroll = _scroll.Y; _bar.CaptureMouse(); e.Handled = true; };
        _bar.MouseMove += (_, e) =>
        {
            if (double.IsNaN(_dragFrom)) return;
            double content = _stack.ActualHeight, view = ActualHeight, travel = view - _bar.Height;
            if (travel <= 0) return;
            _manualUntil = DateTime.Now.AddSeconds(3); // reading ahead, as with the wheel
            Motion.Set(_scroll, TranslateTransform.YProperty, Math.Clamp(_dragScroll - (e.GetPosition(this).Y - _dragFrom) * (content - view) / travel, -(content - view), 0));
        };
        _bar.MouseLeftButtonUp += (_, e) =>
        {
            _dragFrom = double.NaN;
            _bar.ReleaseMouseCapture();
            if (!_bar.IsMouseOver) Motion.Animate(_bar, WidthProperty, 4, Spring.Snappy);
            if (!IsMouseOver) ShowBar(false);
            e.Handled = true;
        };
        return _bar;
    }

    void ShowBar(bool on) => Motion.Animate(_bar, OpacityProperty, on && _doc != null && _stack.ActualHeight > ActualHeight ? 0.75 : 0, Spring.Smooth);

    void PlaceBar()
    {
        double content = _stack.ActualHeight, view = ActualHeight;
        if (content <= view || view <= 0) return;
        _bar.Height = Math.Max(32, view * view / content);
        var y = Math.Clamp(-_scroll.Y, 0, content - view) / (content - view) * (view - _bar.Height);
        if (Math.Abs(_barY.Y - y) > 0.5) _barY.Y = y;
    }

    /// <summary>The line being sung at <paramref name="t"/> (or just sung) and the one after it, for the miniplayer.</summary>
    public (string? Now, string? Next) LinesAt(double t)
    {
        if (_doc is not { Synced: true } doc) return (null, null);
        var lines = doc.Lines;
        var i = Array.FindLastIndex(lines, l => l.Start <= t + 0.1);
        var now = i >= 0 && t < Math.Max(lines[i].End, lines[i].Start + 2) + 1.5 ? lines[i].Text : null;
        var next = i + 1 < lines.Length ? lines[i + 1].Text : null;
        return (now, next);
    }

    public bool HasLyrics => _doc != null;

    /// <summary>Light mode switched: the blur of every line is worked out again on the next frame.</summary>
    public void Reblur() => _blurFor = -2;

    /// <summary>Animation style: Spicy (springs, glow, blur), Karaoke (fill only) or Line by line (whole lines light up).</summary>
    string _style = "Spicy";
    bool Light => Motion.Lite || _style != "Spicy";

    /// <summary>The style setting changed: the lines are built again.</summary>
    public void Restyle() { if (_doc != null) Show(_doc); }

    static double FontFor(double width) => Math.Clamp(width * 0.07, 30, 56);

    public void ShowMessage(string text)
    {
        _doc = null;
        _lines.Clear();
        _stack.Children.Clear();
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        Hook();
    }

    public void Show(LyricsDoc doc)
    {
        _doc = doc;
        _topsDirty = true;
        _style = Settings.Str("lyrics.style");
        _lines.Clear();
        _stack.Children.Clear();
        _message.Visibility = Visibility.Collapsed;
        _fg = TryFindResource("FgA0") is SolidColorBrush b ? b.Color : Colors.White;
        _ls = FontFor(ActualWidth > 0 ? ActualWidth : 600);

        _stack.Children.Add(new Border { Height = 40 });
        double prevEnd = 0;
        foreach (var line in doc.Lines)
        {
            var (a, z) = Range(line);
            if (doc.Synced && a - prevEnd >= GapDots) Add(Dots(prevEnd, a, line.Opposite));
            Add(Build(line, doc.Synced, a, z));
            prevEnd = Math.Max(prevEnd, z);
        }
        var credit = new TextBlock { FontSize = 13, Margin = new Thickness(12, 28, 12, 0), TextWrapping = TextWrapping.Wrap };
        credit.SetResourceReference(TextBlock.ForegroundProperty, "FgA3");
        credit.Inlines.Add(new System.Windows.Documents.Run(L.T("Lyrics:") + " "));
        var parts = doc.Credits ?? [(doc.Source, null)];
        for (var k = 0; k < parts.Count; k++)
        {
            if (k > 0) credit.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
            var (text, url) = parts[k];
            if (url == null) { credit.Inlines.Add(new System.Windows.Documents.Run(text)); continue; }
            // the provider asks for its name and the uploader / maker to be shown and linked wherever the lyrics are
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text)) { NavigateUri = new Uri(url), Cursor = Cursors.Hand };
            link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "FgA2");
            link.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
            credit.Inlines.Add(link);
        }
        _stack.Children.Add(credit);
        _stack.Children.Add(new Border { Height = 400 }); // room to scroll the last line up
        Motion.Set(_scroll, TranslateTransform.YProperty, 0);
        _target = null;
        _blurFor = -2;
        Hook();
        if (doc.Synced) Update();
        else foreach (var lv in _lines) { lv.Root.Opacity = 0.9; foreach (var p in lv.Parts) Fill(p, 1); }

        void Add(LineView lv) { _lines.Add(lv); _stack.Children.Add(lv.Root); }
    }

    /// <summary>A line's time span: its own, widened by any word (background vocals may run past it).</summary>
    static (double A, double B) Range(LyricLine l)
    {
        var words = l.Words.Concat(l.Background).ToArray();
        return words.Length == 0 ? (l.Start, l.End) : (Math.Min(l.Start, words.Min(w => w.Start)), Math.Max(l.End, words.Max(w => w.End)));
    }

    LineView Shell(Panel content, bool opposite, Action click)
    {
        var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        var root = new Border
        {
            Child = content, Padding = new Thickness(12, _ls * 0.12, 12, _ls * 0.12), Background = Brushes.Transparent,
            HorizontalAlignment = opposite ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        var lv = new LineView { Root = root, Blur = blur };
        if (_doc?.Synced == true)
        {
            root.Cursor = Cursors.Hand;
            root.Effect = blur;
            root.MouseEnter += (_, _) => { lv.Hover = true; _blurFor = -2; lv.State = -1; };
            root.MouseLeave += (_, _) => { lv.Hover = false; _blurFor = -2; lv.State = -1; };
            root.MouseLeftButtonUp += (_, e) => { _manualUntil = DateTime.MinValue; click(); e.Handled = true; };
        }
        return lv;
    }

    LineView Build(LyricLine line, bool synced, double a, double z)
    {
        var align = line.Opposite ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var panel = new StackPanel { HorizontalAlignment = align };
        var lv = Shell(panel, line.Opposite, () => Seek?.Invoke(line.Start));
        lv.Line = line;
        lv.A = a;
        lv.B = z;
        if (line.Words.Length > 0 || line.Background.Length > 0)
        {
            if (line.Words.Length > 0) panel.Children.Add(Words(line.Words, _ls, FontWeights.Bold, Sung, Unsung, align, lv));
            if (line.Background.Length > 0)
            {
                // background vocals: smaller and fainter, word by word like the lead (also when they are the whole line)
                var bg = Words(line.Background, _ls * 0.75, FontWeights.SemiBold, BgSung, BgUnsung, align, lv);
                if (line.Words.Length > 0) bg.Margin = new Thickness(0, -_ls * 0.1, 0, 0);
                panel.Children.Add(bg);
            }
        }
        else
        {
            // line-synced (LRC) or unsynced: the whole line lights up when it starts
            var p = Text(line.Text, _ls, FontWeights.Bold, Sung, Unsung);
            ((TextBlock)p.El).TextWrapping = TextWrapping.Wrap;
            ((TextBlock)p.El).TextAlignment = line.Opposite ? TextAlignment.Right : TextAlignment.Left;
            p.A = p.B = line.Start;
            p.Plain = true;
            panel.Children.Add(p.El);
            lv.Parts.Add(p);
        }
        if (line.Translation != null && Settings.Bool("lyrics.translation"))
            panel.Children.Add(new TextBlock
            {
                Text = line.Translation, FontSize = _ls * 0.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(WithAlpha(0.6)), Margin = new Thickness(0, _ls * 0.08, 0, 0),
                TextAlignment = line.Opposite ? TextAlignment.Right : TextAlignment.Left,
            });
        return lv;
    }

    WrapPanel Words(Syllable[] words, double size, FontWeight weight, double ga, double ge, HorizontalAlignment align, LineView lv)
    {
        var wrap = new WrapPanel { HorizontalAlignment = align };
        foreach (var s in words)
        {
            var tail = s.Joined ? "" : " ";
            Part w;
            if (s.End - s.Start >= LetterMin && s.Text.Length > 0)
            {
                // long syllable: the letters share its time, ending a little early
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                double b = s.End - LetterEarly, step = (b - s.Start) / s.Text.Length;
                var letters = s.Text.Select((ch, k) =>
                {
                    var l = Text(ch.ToString(), size, weight, ga, ge);
                    l.A = s.Start + k * step;
                    l.B = s.Start + (k + 1) * step;
                    row.Children.Add(l.El);
                    return l;
                }).ToArray();
                if (tail.Length > 0) row.Children.Add(new TextBlock { Text = tail, FontSize = size, FontWeight = weight });
                w = new Part { El = row, A = s.Start, B = b, Letters = letters };
                Transforms(w);
            }
            else
            {
                w = Text(s.Text + tail, size, weight, ga, ge);
                w.A = s.Start;
                w.B = s.End;
            }
            wrap.Children.Add(w.El);
            lv.Parts.Add(w);
        }
        return wrap;
    }

    Part Text(string text, double size, FontWeight weight, double ga, double ge)
    {
        var e0 = new GradientStop(WithAlpha(ga), -0.2);
        var e1 = new GradientStop(WithAlpha(ge), 0);
        var tb = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, LineHeight = size * 1.1818, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = new LinearGradientBrush(new GradientStopCollection { e0, e1 }, new Point(0, 0), new Point(1, 0)),
        };
        var p = new Part { El = tb, E0 = e0, E1 = e1 };
        Transforms(p);
        return p;
    }

    static void Transforms(Part p)
    {
        p.El.RenderTransformOrigin = new Point(0.5, 0.5);
        p.El.RenderTransform = new TransformGroup { Children = { p.Scale, p.Lift } };
    }

    Color WithAlpha(double a) => Color.FromArgb((byte)(a * 255), _fg.R, _fg.G, _fg.B);

    /// <summary>Three dots filling a long gap, each taking a third of it; they shrink away just before the singing resumes.</summary>
    LineView Dots(double a, double b, bool opposite)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, RenderTransformOrigin = new Point(opposite ? 1 : 0, 0.5) };
        var scale = new ScaleTransform(1, 1);
        group.RenderTransform = scale;
        var lv = Shell(group, opposite, () => Seek?.Invoke(a));
        lv.A = a;
        lv.B = b;
        lv.DotGroup = scale;
        lv.Root.Visibility = Visibility.Collapsed;
        double T = b - a;
        double[] ends = [a + T / 3 - DotPad / 3, a + 2 * T / 3 - 2 * DotPad / 3, b - DotPad];
        for (var k = 0; k < 3; k++)
        {
            var dot = Text("•", _ls * 1.3, FontWeights.Bold, Sung, Sung);
            dot.El.Margin = new Thickness(0, 0, _ls * 0.12, 0);
            ((TextBlock)dot.El).LineHeight = _ls * 0.9;
            dot.A = k == 0 ? a : ends[k - 1];
            dot.B = ends[k];
            dot.Dot = true;
            group.Children.Add(dot.El);
            lv.Parts.Add(dot);
        }
        return lv;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_doc == null) return;
        // reading ahead: scroll freely, the view returns to the singer after a few seconds
        _manualUntil = DateTime.Now.AddSeconds(3);
        ShowBar(true);
        _barHide.Stop();
        _barHide.Start();
        var max = Math.Max(0, _stack.ActualHeight - ActualHeight);
        Motion.Animate(_scroll, TranslateTransform.YProperty, Math.Clamp(_scroll.Y + e.Delta * 0.8, -max, ActualHeight / 2), Spring.Snappy);
        e.Handled = true;
    }

    void Hook()
    {
        var on = IsVisible && _doc is { Synced: true } && !Motion.Asleep;
        if (on == _hooked) return;
        _hooked = on;
        _lastFrame = _clock.Elapsed.TotalSeconds;
        if (on) CompositionTarget.Rendering += Tick;
        else CompositionTarget.Rendering -= Tick;
    }

    // 60 updates a second look the same as 180 and cost a third: Rendering fires at the monitor's rate
    void Tick(object? sender, EventArgs e)
    {
        if (_clock.Elapsed.TotalSeconds - _lastFrame < (Light ? 1 / 31.0 : 1 / 62.0)) return;
        Update();
    }

#if DEBUG
    internal void DebugFrame() => Update();
#endif

    public string DebugState() => $"bar={_bar.Opacity:0.00}/{_bar.Height:0}@{_barY.Y:0} target={_lines.IndexOf(_target!)} scrollY={_scroll.Y:0} height={ActualHeight:0} stack={_stack.ActualHeight:0} lines={_lines.Count} " +
        string.Join(" ", _lines.Take(16).Select((l, i) => $"{i}:{l.Top:0} st={l.State} op={l.Root.Opacity:0.00}/{l.Root.Child.Opacity:0.00} blur={l.Blur.Radius:0.0}"));

    // where each line sits in the stack, worked out after layout. Only on a size change wasn't enough: the same lyrics built
    // again (same height, so no SizeChanged) left every new line at 0, and the edge fade hid them all
    bool _topsDirty = true;

    void Update()
    {
        if (_doc == null || Clock == null) return;
        if (_topsDirty && _stack.IsMeasureValid && _lines.Count > 0 && _lines[0].Root.IsArrangeValid && _lines[^1].Root.IsArrangeValid) // new lines laid out
        {
            foreach (var lv in _lines) lv.Top = lv.Root.TranslatePoint(new Point(0, 0), _stack).Y;
            _topsDirty = false;
            _target = null;
        }
        var t = Clock();
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.25, now - _lastFrame);
        _lastFrame = now;

        // the line being sung (the first one, if lines overlap), else the last one started
        var cur = _lines.FindIndex(l => t >= l.A && t < l.B);
        if (cur < 0) cur = _lines.FindLastIndex(l => l.A <= t);

        for (var i = 0; i < _lines.Count; i++)
        {
            var lv = _lines[i];
            var state = t < lv.A ? 0 : t >= lv.B ? 2 : 1;
            if (lv.DotGroup != null)
            {
                lv.Root.Visibility = state == 1 ? Visibility.Visible : Visibility.Collapsed; // collapsed unless it's their turn
                var hide = state == 1 && t > lv.B - DotHide;
                if (hide != lv.Hidden)
                {
                    lv.Hidden = hide;
                    Motion.Animate(lv.DotGroup, ScaleTransform.ScaleXProperty, hide ? 0 : 1, hide ? Spring.Snappy : Spring.Smooth);
                    Motion.Animate(lv.DotGroup, ScaleTransform.ScaleYProperty, hide ? 0 : 1, hide ? Spring.Snappy : Spring.Smooth);
                }
            }
            if (state != lv.State)
            {
                lv.State = state;
                Motion.Animate(lv.Root, OpacityProperty, state == 1 || lv.Hover ? 1 : state == 0 ? 0.51 : 0.497, Spring.Snappy);
            }

            // soft fade near the top and bottom edges
            var y = lv.Top + _scroll.Y;
            var edge = Math.Clamp(Math.Min(y + lv.Root.ActualHeight, ActualHeight - y) / 70, 0, 1);
            var fade = edge * edge * (3 - 2 * edge);
            if (Math.Abs(lv.Root.Child.Opacity - fade) > 0.005) lv.Root.Child.Opacity = fade;

            var visible = y + lv.Root.ActualHeight > -200 && y < ActualHeight + 200;
            // off-screen lines are settled once per state change, not repainted every frame
            if (visible) lv.Painted = -1;
            else if (lv.Painted == state) continue;
            else lv.Painted = state;
            foreach (var p in lv.Parts) Paint(p, t, dt, state == 1, visible);
        }

        if (cur != _blurFor)
        {
            _blurFor = cur;
            for (var k = 0; k < _lines.Count; k++)
            {
                var d = Math.Abs(k - cur);
                var line = _lines[k];
                var r = line.Hover || cur < 0 || d == 0 || Light ? 0 : Math.Min(BlurStep * d, BlurStep * 5.465) * 1.6;
                // an attached effect renders the line off-screen every frame even at radius 0: only lines that are blurred
                // (or still un-blurring) carry it, and far lines are faint anyway
                if (d > 8) line.Root.Effect = null;
                else if (r > 0 || line.Blur.Radius > 0.05) line.Root.Effect = line.Blur;
                if (Math.Abs(line.Blur.Radius - r) > 0.05)
                    Motion.Animate(line.Blur, BlurEffect.RadiusProperty, r, Spring.Smooth, done: r == 0 ? () => { if (line.Blur.Radius < 0.05) line.Root.Effect = null; } : null);
                else if (r == 0) line.Root.Effect = null;
            }
        }

        // follow the song: the line about to be sung (slightly early), or the interlude dots, in the middle
        if (DateTime.Now < _manualUntil) return;
        var dots = cur >= 0 && _lines[cur].DotGroup != null && _lines[cur].State == 1 ? _lines[cur] : null;
        var next = dots ?? _lines.LastOrDefault(l => l.Line != null && l.A <= t + Look) ?? _lines.FirstOrDefault(l => l.Line != null);
        if (next == null || (next == _target && _manualUntil == DateTime.MinValue) || !next.Root.IsArrangeValid) return;
        _target = next;
        _manualUntil = DateTime.MinValue;
        var top = next.Root.TranslatePoint(new Point(0, 0), _stack).Y;
        Motion.Animate(_scroll, TranslateTransform.YProperty, -(top + next.Root.ActualHeight / 2 - ActualHeight / 2), Spring.Smooth);
    }

    static int StateOf(double t, double a, double b) => t < a ? 0 : t >= b ? 2 : 1;
    static double Prog(double t, double a, double b) => t <= a ? 0 : t >= b ? 1 : (t - a) / (b - a);

    /// <summary>One frame of a word / letter / dot. Off-screen ones jump to where they belong instead of animating.</summary>
    void Paint(Part w, double t, double dt, bool active, bool visible)
    {
        int st = StateOf(t, w.A, w.B);
        double p = Prog(t, w.A, w.B), q = st == 1 ? p : st / 2.0; // rest = curve(0), sung = curve(1)
        if (w.Plain) { Fill(w, st == 0 ? -0.2 : 1); return; }
        if (!visible) dt = 10; // settles at once
        if (w.Dot)
        {
            var (s, y, g, o) = Springs(w, DScale(q), DLift(q), DGlow(q), DOp(q), dt, dot: true);
            Move(w, s, y * _ls);
            if (Changed(ref w.Op, o, 1e-3)) w.El.Opacity = o;
            Glow(w, 4 + 6 * g, g * 0.9);
            return;
        }
        if (_style != "Spicy")
        {
            // light styles: no springs, no glow, just the fill. Karaoke sweeps word by word (long syllables letter by
            // letter); Line by line lights the whole line as it starts
            var whole = _style == "Line by line";
            var lit = whole ? (active || st == 2 ? 1 : -0.2) : st == 0 ? -0.2 : st == 2 ? 1 : -0.2 + 1.2 * p;
            if (w.Letters == null) Fill(w, lit);
            else
                foreach (var l in w.Letters)
                {
                    var ls = StateOf(t, l.A, l.B);
                    Fill(l, whole ? lit : ls == 0 ? -0.2 : ls == 2 ? 1 : -0.2 + 1.2 * Prog(t, l.A, l.B));
                }
            return;
        }
        var v = Springs(w, CScale(q), CLift(q), CGlow(q), null, dt);
        Move(w, v.s, v.y * _ls);
        if (w.Letters == null)
        {
            Fill(w, st == 0 ? -0.2 : st == 2 ? 1 : -0.2 + 1.2 * p);
            Glow(w, 4 + 2 * v.g, active ? Math.Min(v.g * 0.35, 1) : 0);
            return;
        }
        var letters = w.Letters;
        var act = st == 1 ? Array.FindIndex(letters, l => t >= l.A && t < l.B) : -1;
        var ap = act >= 0 ? Prog(t, letters[act].A, letters[act].B) : 0;
        for (var k = 0; k < letters.Length; k++)
        {
            var l = letters[k];
            var ls = StateOf(t, l.A, l.B);
            double s = LScale(0), y = LLift(0), g = CGlow(0), gp = -0.2;
            if (st == 2) { s = LScale(1); y = LLift(1); g = CGlow(1); gp = 1; }
            else if (st == 1)
            {
                if (act >= 0)
                {
                    // the sung letter swells, its neighbours follow with a steep falloff
                    double d = Math.Abs(k - act), f = 1 / (1 + Math.Pow(d, 2.8)), gf = 1 / (1 + d * 0.9);
                    s += (LScale(ap) - s) * f;
                    y += (LLift(ap) - y) * f;
                    g += (CGlow(ap) - g) * gf;
                }
                if (ls == 0) { s = LScale(0); y = LLift(0); g = CGlow(0); }
                else if (ls == 2 && act < 0) g = CGlow(0.2);
                gp = ls == 0 ? -0.2 : ls == 2 ? 1 : k == act ? -0.2 + 1.2 * Math.Sin(ap * Math.PI / 2) : -0.2;
            }
            var lv = Springs(l, s, y, g, null, visible ? dt : 10);
            Fill(l, gp);
            Move(l, lv.s, lv.y * 2 * _ls);
            Glow(l, 4 + 12 * lv.g, active ? Math.Min(lv.g * 1.85, 1) : 0);
        }
    }

    static (double s, double y, double g, double o) Springs(Part p, double s, double y, double g, double? o, double dt, bool dot = false)
    {
        if (p.S == null) // start where it belongs: no jump after a rebuild
        {
            p.S = dot ? new Sp(s, 0.7, 0.6) : new Sp(s, 0.88, 0.64);
            p.Y = dot ? new Sp(y, 1.25, 0.4) : new Sp(y, 1.45, 0.4);
            p.G = dot ? new Sp(g, 1, 0.5) : new Sp(g, 1.18, 0.56);
            if (o is { } o0) p.O = new Sp(o0, 1, 0.5);
        }
        p.S.G = s; p.Y!.G = y; p.G!.G = g;
        if (p.O != null && o is { } og) p.O.G = og;
        return (p.S.Step(dt), p.Y.Step(dt), p.G.Step(dt), p.O?.Step(dt) ?? 1);
    }

    static bool Changed(ref double last, double v, double eps)
    {
        if (!double.IsNaN(last) && Math.Abs(last - v) <= eps) return false;
        last = v;
        return true;
    }

    static void Move(Part p, double scale, double lift)
    {
        if (Changed(ref p.Sc, scale, 1e-3)) { p.Scale.ScaleX = scale; p.Scale.ScaleY = scale; }
        if (Changed(ref p.Ly, lift, 0.02)) p.Lift.Y = lift;
    }

    /// <summary>Sung color left of <paramref name="gp"/>, the dim one from 20 % further on (gp −0.2 … 1 across the word).</summary>
    static void Fill(Part p, double gp)
    {
        if (p.E0 == null || !Changed(ref p.Gp, gp, 0.0005)) return;
        p.E0.Offset = gp;
        p.E1!.Offset = gp + 0.2;
    }

    void Glow(Part p, double radius, double opacity)
    {
        if (!Changed(ref p.Go, opacity, 0.01) & !Changed(ref p.Gb, radius, 0.5)) return;
        if (opacity < 0.02 || Light) { p.El.Effect = null; return; } // a glow is a shadow effect per word: the light styles skip it
        if (p.El.Effect is not DropShadowEffect glow)
            p.El.Effect = glow = new DropShadowEffect { ShadowDepth = 0, Color = _fg, RenderingBias = RenderingBias.Performance };
        glow.BlurRadius = radius * 1.5;
        glow.Opacity = opacity;
    }

    /// <summary>Natural cubic spline through the points (x ascending).</summary>
    static Func<double, double> Curve(params (double X, double Y)[] pts)
    {
        int n = pts.Length;
        double[] x = [.. pts.Select(p => p.X)], y = [.. pts.Select(p => p.Y)], m = new double[n], c = new double[n], d = new double[n];
        var h = new double[n - 1];
        for (var i = 0; i < n - 1; i++) h[i] = x[i + 1] - x[i];
        for (var i = 1; i < n - 1; i++) // tridiagonal solve for the second derivatives
        {
            double a = h[i - 1], b = 2 * (h[i - 1] + h[i]) - a * c[i - 1], r = 6 * ((y[i + 1] - y[i]) / h[i] - (y[i] - y[i - 1]) / h[i - 1]) - a * d[i - 1];
            c[i] = h[i] / b;
            d[i] = r / b;
        }
        for (var i = n - 2; i > 0; i--) m[i] = d[i] - c[i] * m[i + 1];
        return t =>
        {
            var i = 0;
            while (i < n - 2 && t > x[i + 1]) i++;
            double a = x[i + 1] - t, b = t - x[i], w = h[i];
            return (m[i] * a * a * a + m[i + 1] * b * b * b) / (6 * w) + (y[i] / w - m[i] * w / 6) * a + (y[i + 1] / w - m[i + 1] * w / 6) * b;
        };
    }
}
