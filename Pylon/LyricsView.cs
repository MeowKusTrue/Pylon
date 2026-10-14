using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Pylon;

/// <summary>
/// Synced lyrics in the manner of Spicy Lyrics / Apple Music: syllables fill in as they are sung and lift a little,
/// long notes glow, lines other than the current one are dimmed and blurred by distance, the page glides on a spring to
/// keep the current line in view, a click on a line seeks there, and long instrumental gaps show breathing dots.
/// </summary>
public class LyricsView : Grid
{
    sealed class WordView
    {
        public required Syllable Syllable;
        public required TextBlock Block;
        public required GradientStop Edge0, Edge1;
        public required TranslateTransform Lift;
        public int State; // 0 not yet, 1 singing, 2 sung
    }

    sealed class LineView
    {
        public required LyricLine Line;
        public required Border Root;
        public required BlurEffect Blur;
        public required ScaleTransform Scale;
        public required TextBlock Dots;
        public readonly List<WordView> Words = [];
        public TextBlock? Plain;
        public int State = -1; // 0 future, 1 active, 2 past
        public double GapBefore;
    }

    readonly StackPanel _stack = new();
    readonly TranslateTransform _scroll = new();
    readonly TextBlock _message = new() { FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    readonly List<LineView> _lines = [];
    LyricsDoc? _doc;
    int _focus = -2;
    bool _hooked;
    DateTime _manualUntil;
    Brush _sung = Brushes.White;
    Color _sungColor = Colors.White, _unsungColor = Color.FromArgb(90, 255, 255, 255);

    /// <summary>Playback position in seconds (interpolated by the host between libVLC time updates).</summary>
    public Func<double>? Clock;

    /// <summary>The user clicked a line: seek here (seconds).</summary>
    public event Action<double>? Seek;

    public LyricsView()
    {
        ClipToBounds = true;
        _stack.RenderTransform = _scroll;
        _stack.VerticalAlignment = VerticalAlignment.Top;
        _stack.Margin = new Thickness(8, 0, 8, 0);
        Children.Add(_stack);
        Children.Add(_message);
        _message.SetResourceReference(TextBlock.ForegroundProperty, "FgA3");
        IsVisibleChanged += (_, _) => Hook();
        ThemeManager.Changed += () => { if (_doc != null) Show(_doc); };
        SizeChanged += (_, _) => { _focus = -2; };
        // a fade at the top and bottom edges instead of a hard cut (Apple's "scroll edge effect")
        OpacityMask = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.Transparent, 0), new(Colors.Black, 0.12), new(Colors.Black, 0.82), new(Colors.Transparent, 1),
        }, 90);
    }

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
        _lines.Clear();
        _stack.Children.Clear();
        _message.Visibility = Visibility.Collapsed;
        var fg = TryFindResource("FgA0") is SolidColorBrush b ? b.Color : Colors.White;
        _sungColor = fg;
        _unsungColor = Color.FromArgb(95, fg.R, fg.G, fg.B);
        _sung = new SolidColorBrush(fg);
        _sung.Freeze();

        _stack.Children.Add(new Border { Height = 40 });
        LyricLine? prev = null;
        foreach (var line in doc.Lines)
        {
            var lv = Build(line, doc.Synced);
            lv.GapBefore = prev == null ? line.Start : line.Start - prev.End;
            _lines.Add(lv);
            _stack.Children.Add(lv.Root);
            prev = line;
        }
        var credit = new TextBlock { Text = L.F("Lyrics: {0}", doc.Source), FontSize = 13, Margin = new Thickness(12, 28, 12, 0) };
        credit.SetResourceReference(TextBlock.ForegroundProperty, "FgA4");
        _stack.Children.Add(credit);
        _stack.Children.Add(new Border { Height = 400 }); // room to scroll the last line up
        Motion.Set(_scroll, TranslateTransform.YProperty, 0);
        _focus = -2;
        Hook();
        if (doc.Synced) Update();
        else foreach (var lv in _lines) lv.Root.Opacity = 0.9;
    }

    LineView Build(LyricLine line, bool synced)
    {
        var align = line.Opposite ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var panel = new StackPanel { HorizontalAlignment = align };
        var dots = new TextBlock { Text = "•  •  •", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = _sung, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = align };
        panel.Children.Add(dots);
        var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        var scale = new ScaleTransform(1, 1);
        var root = new Border
        {
            Child = panel, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent,
            Cursor = synced ? Cursors.Hand : null, Effect = synced ? blur : null, RenderTransform = scale,
            RenderTransformOrigin = new Point(line.Opposite ? 1 : 0, 0.5),
        };
        var lv = new LineView { Line = line, Root = root, Blur = blur, Scale = scale, Dots = dots };
        if (synced)
        {
            root.MouseEnter += (_, _) => root.SetResourceReference(Border.BackgroundProperty, "Hover");
            root.MouseLeave += (_, _) => root.Background = Brushes.Transparent;
            root.MouseLeftButtonUp += (_, e) => { _manualUntil = DateTime.MinValue; Seek?.Invoke(line.Start); e.Handled = true; };
        }
        if (line.Words.Length > 0)
        {
            panel.Children.Add(WordsPanel(line.Words, 34, align, lv));
            if (line.Background.Length > 0) panel.Children.Add(WordsPanel(line.Background, 22, align, lv, background: true));
        }
        else
        {
            lv.Plain = new TextBlock { Text = line.Text, FontSize = 34, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Foreground = _sung, TextAlignment = line.Opposite ? TextAlignment.Right : TextAlignment.Left };
            panel.Children.Add(lv.Plain);
        }
        return lv;
    }

    WrapPanel WordsPanel(Syllable[] words, double size, HorizontalAlignment align, LineView lv, bool background = false)
    {
        var wrap = new WrapPanel { HorizontalAlignment = align, Margin = background ? new Thickness(0, 6, 0, 0) : default, Opacity = background ? 0.85 : 1 };
        foreach (var s in words)
        {
            var e0 = new GradientStop(_sungColor, 0);
            var e1 = new GradientStop(_unsungColor, 0);
            var brush = new LinearGradientBrush(new GradientStopCollection { new(_sungColor, 0), e0, e1, new(_unsungColor, 1) }, new Point(0, 0), new Point(1, 0));
            var lift = new TranslateTransform();
            var tb = new TextBlock
            {
                Text = s.Joined ? s.Text : s.Text + " ", FontSize = size, FontWeight = background ? FontWeights.SemiBold : FontWeights.Bold,
                Foreground = brush, RenderTransform = lift,
            };
            wrap.Children.Add(tb);
            lv.Words.Add(new WordView { Syllable = s, Block = tb, Edge0 = e0, Edge1 = e1, Lift = lift });
            SetFill(lv.Words[^1], 0);
        }
        return wrap;
    }

    static void SetFill(WordView w, double p)
    {
        // a soft edge sweeps across the syllable: sung color left of it, the dim color right of it
        w.Edge0.Offset = Math.Clamp(p - 0.08, 0, 1);
        w.Edge1.Offset = Math.Clamp(p + 0.08, 0, 1);
        if (p <= 0) { w.Edge0.Offset = 0; w.Edge1.Offset = 0; }
        if (p >= 1) { w.Edge0.Offset = 1; w.Edge1.Offset = 1; }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_doc == null) return;
        // reading ahead: scroll freely, the view returns to the singer after a few seconds
        _manualUntil = DateTime.Now.AddSeconds(4);
        var max = Math.Max(0, _stack.ActualHeight - ActualHeight);
        Motion.Animate(_scroll, TranslateTransform.YProperty, Math.Clamp(_scroll.Y + e.Delta * 0.8, -max, 0), Spring.Snappy);
        e.Handled = true;
    }

    void Hook()
    {
        var on = IsVisible && _doc is { Synced: true };
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += Tick;
        else CompositionTarget.Rendering -= Tick;
    }

    void Tick(object? sender, EventArgs e) => Update();

    void Update()
    {
        if (_doc == null || Clock == null) return;
        var t = Clock();
        var focus = -1;
        for (var i = 0; i < _lines.Count; i++)
            if (_lines[i].Line.Start <= t + 0.05) focus = i; else break;

        for (var i = 0; i < _lines.Count; i++)
        {
            var lv = _lines[i];
            var l = lv.Line;
            var active = t >= l.Start - 0.05 && t < l.End;
            var state = active ? 1 : t >= l.End ? 2 : 0;
            if (state != lv.State)
            {
                lv.State = state;
                Motion.Animate(lv.Root, OpacityProperty, active ? 1 : 0.42, Spring.Smooth);
                Motion.Animate(lv.Scale, ScaleTransform.ScaleXProperty, active ? 1 : 0.96, Spring.Smooth);
                Motion.Animate(lv.Scale, ScaleTransform.ScaleYProperty, active ? 1 : 0.96, Spring.Smooth);
                if (lv.Plain != null) lv.Plain.Opacity = 1;
                if (!active) foreach (var w in lv.Words) EndGlow(w);
            }
            // blur grows with the distance from the current line
            var dist = Math.Abs(i - Math.Max(focus, 0));
            var blur = active ? 0 : Math.Min(1 + dist * 1.1, 4.5);
            if (Math.Abs(lv.Blur.Radius - blur) > 0.05) Motion.Animate(lv.Blur, BlurEffect.RadiusProperty, blur, Spring.Smooth);
            lv.Root.Effect = dist > 7 ? null : lv.Blur; // far away lines are faint anyway; skip their blur pass

            // instrumental gap before this line: breathing dots
            var inGap = lv.GapBefore >= 6 && t < l.Start && (i == 0 ? t >= 0 : t >= _lines[i - 1].Line.End) && t >= l.Start - lv.GapBefore;
            lv.Dots.Visibility = inGap ? Visibility.Visible : Visibility.Collapsed;
            if (inGap) lv.Dots.Opacity = 0.35 + 0.45 * (0.5 + 0.5 * Math.Sin(t * Math.PI * 1.6));

            if (state == 1 || (state == 2 && lv.Words.Count > 0 && lv.Words[^1].State != 2))
                foreach (var w in lv.Words)
                {
                    var s = w.Syllable;
                    var p = s.End > s.Start ? (t - s.Start) / (s.End - s.Start) : (t >= s.Start ? 1 : 0);
                    SetFill(w, p);
                    var ws = p <= 0 ? 0 : p >= 1 ? 2 : 1;
                    if (ws == w.State) continue;
                    w.State = ws;
                    // the syllable being sung rises a touch and settles once it's done
                    Motion.Animate(w.Lift, TranslateTransform.YProperty, ws == 1 ? -3 : 0, ws == 1 ? Spring.Snappy : Spring.Smooth);
                    if (ws == 1 && s.End - s.Start > 0.9) Glow(w);
                    if (ws == 2) EndGlow(w);
                }
            else if (state == 0 && lv.Words.Count > 0 && lv.Words[0].State != 0)
                foreach (var w in lv.Words) { w.State = 0; SetFill(w, 0); Motion.Set(w.Lift, TranslateTransform.YProperty, 0); } // after seeking back
        }

        if (focus != _focus && DateTime.Now >= _manualUntil)
        {
            _focus = focus;
            var target = focus < 0 ? _lines.FirstOrDefault()?.Root : _lines[focus].Root;
            if (target != null && target.IsArrangeValid)
            {
                var top = target.TranslatePoint(new Point(0, 0), _stack).Y;
                Motion.Animate(_scroll, TranslateTransform.YProperty, -(top - ActualHeight * 0.3), Spring.Smooth);
            }
        }
        else if (DateTime.Now >= _manualUntil && _manualUntil != DateTime.MinValue)
        {
            _manualUntil = DateTime.MinValue;
            _focus = -2; // back to following the song
        }
    }


    void Glow(WordView w) =>
        w.Block.Effect = new DropShadowEffect { Color = _sungColor, ShadowDepth = 0, BlurRadius = 16, Opacity = 0.55, RenderingBias = RenderingBias.Performance };

    static void EndGlow(WordView w) => w.Block.Effect = null;
}
