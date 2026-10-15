using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Pylon;

/// <summary>
/// Heavily blurred artwork behind music or video. New images cross-fade in; "live" mode slowly turns and breathes the
/// picture (Apple Music style) together with a mirrored counter-rotating copy. Each blurred layer is bitmap-cached,
/// so the motion is only the GPU moving a texture, not a re-blur every frame.
/// </summary>
public class Backdrop : Grid
{
    static readonly Spring Fade = new(1.1, 1.0); // slow, calm cross-fade between pictures

    readonly Image _a = Layer(), _b = Layer(), _mirror = Layer();
    readonly Grid _spin = new(), _counter = new();
    readonly RotateTransform _rot = new(), _rot2 = new();
    readonly ScaleTransform _scale = new(1.25, 1.25), _scale2 = new(-2.2, 2.2);
    bool _aFront = true, _live, _want;

    public Backdrop()
    {
        Motion.AsleepChanged += () => Live = _want; // a minimized window stops turning, and starts again when shown
        ClipToBounds = true;
        IsHitTestVisible = false;
        _spin.RenderTransformOrigin = _counter.RenderTransformOrigin = new Point(0.5, 0.5);
        _spin.RenderTransform = new TransformGroup { Children = { _scale, _rot } };
        _counter.RenderTransform = new TransformGroup { Children = { _scale2, _rot2 } };
        _spin.Children.Add(Cached(_a));
        _spin.Children.Add(Cached(_b));
        _counter.Children.Add(Cached(_mirror));
        _counter.Opacity = 0;
        _b.Opacity = 0;
        Children.Add(_spin);
        Children.Add(_counter);
    }

    // the picture is blurred at 160×90 and the cached result stretched over the window: the same soft look as a radius-90
    // blur at full size, at a fraction of the cost (a full-window blur was redrawn whenever anything over it changed)
    static Image Layer() => new() { Stretch = Stretch.UniformToFill, Effect = new BlurEffect { Radius = 11, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } };
    static Viewbox Cached(Image i) => new() { Stretch = Stretch.UniformToFill, Child = new Border { Width = 160, Height = 90, Child = i, CacheMode = new BitmapCache() } };

    /// <summary>Shows a new picture, cross-fading from the current one (instant when <paramref name="fade"/> is off).</summary>
    public void SetImage(ImageSource? source, bool fade = true)
    {
        var (front, back) = _aFront ? (_a, _b) : (_b, _a);
        _mirror.Source = source;
        if (!fade || !Motion.Enabled || front.Source == null)
        {
            front.Source = source;
            Motion.Set(front, OpacityProperty, 1);
            Motion.Set(back, OpacityProperty, 0);
            back.Source = null;
            return;
        }
        back.Source = source;
        Motion.Animate(back, OpacityProperty, 1, Fade, from: 0);
        Motion.Animate(front, OpacityProperty, 0, Fade);
        _aFront = !_aFront;
    }

    /// <summary>Slow constant motion (linear rotation, sine breathing); off when Windows animations are off.</summary>
    public bool Live
    {
        get => _live;
        set
        {
            _want = value;
            var on = value && Motion.Enabled && !Motion.Asleep;
            if (on == _live) return;
            _live = on;
            if (on)
            {
                // big enough that the turning picture always covers the corners of a wide stage
                _rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(90)) { RepeatBehavior = RepeatBehavior.Forever });
                _rot2.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(360, 0, TimeSpan.FromSeconds(130)) { RepeatBehavior = RepeatBehavior.Forever });
                var breathe = new DoubleAnimation(2.1, 2.45, TimeSpan.FromSeconds(17)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
                Motion.Animate(_counter, OpacityProperty, 0.45, Fade);
            }
            else
            {
                _rot.BeginAnimation(RotateTransform.AngleProperty, null);
                _rot2.BeginAnimation(RotateTransform.AngleProperty, null);
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                Motion.Set(_counter, OpacityProperty, 0);
            }
        }
    }
}
