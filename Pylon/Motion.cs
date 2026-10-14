using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pylon;

/// <summary>
/// UI motion, Apple / Emil Kowalski style: transform + opacity only, strong ease-out, under 300 ms,
/// enter and exit along the same path, every animation starts from the current on-screen value (interruptible).
/// Keyboard-triggered changes stay instant; Windows' "Animation effects" switch turns everything off.
/// </summary>
public static class Motion
{
    static readonly KeySpline EaseOut = new(0.23, 1, 0.32, 1);

    sealed class State { public int Gen; public bool Leaving; }
    static readonly ConditionalWeakTable<UIElement, State> States = new();

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>The change came from the mouse (or pen/touch). Hotkey-driven changes don't animate.</summary>
    public static bool ByPointer => InputManager.Current.MostRecentInputDevice is not KeyboardDevice;

    /// <summary>Visible and not on its way out.</summary>
    public static bool IsShown(UIElement el) => el.Visibility == Visibility.Visible && !States.GetOrCreateValue(el).Leaving;

    /// <summary>Makes the element visible, sliding from (dx, dy) and scaling from <paramref name="scale"/>.</summary>
    public static void In(UIElement el, double dx = 0, double dy = 0, double scale = 1, int ms = 220, bool animate = true)
    {
        var st = States.GetOrCreateValue(el);
        var resume = st.Leaving; // reversing an exit: continue from where it is
        st.Gen++;
        st.Leaving = false;
        var wasShown = el.Visibility == Visibility.Visible && !resume;
        el.Visibility = Visibility.Visible;
        if (!Enabled || !animate) { Reset(el); return; }
        if (wasShown) return;
        var (s, t) = Transforms(el);
        Run(el, UIElement.OpacityProperty, resume ? null : 0, 1, ms);
        Run(t, TranslateTransform.XProperty, resume ? null : dx, 0, ms);
        Run(t, TranslateTransform.YProperty, resume ? null : dy, 0, ms);
        Run(s, ScaleTransform.ScaleXProperty, resume ? null : scale, 1, ms);
        Run(s, ScaleTransform.ScaleYProperty, resume ? null : scale, 1, ms);
    }

    /// <summary>Plays the way out (mirror of <see cref="In"/>), then calls <paramref name="done"/> unless it was interrupted.</summary>
    public static void Out(UIElement el, Action done, double dx = 0, double dy = 0, double scale = 1, int ms = 160, bool animate = true)
    {
        var st = States.GetOrCreateValue(el);
        if (!Enabled || !animate || el.Visibility != Visibility.Visible) { st.Gen++; st.Leaving = false; done(); Reset(el); return; }
        var gen = ++st.Gen;
        st.Leaving = true;
        var (s, t) = Transforms(el);
        Run(t, TranslateTransform.XProperty, null, dx, ms);
        Run(t, TranslateTransform.YProperty, null, dy, ms);
        Run(s, ScaleTransform.ScaleXProperty, null, scale, ms);
        Run(s, ScaleTransform.ScaleYProperty, null, scale, ms);
        Run(el, UIElement.OpacityProperty, null, 0, ms, () =>
        {
            if (st.Gen != gen) return; // shown again meanwhile
            st.Leaving = false;
            done();
            Reset(el);
        });
    }

    /// <summary>Entrance for something that just appeared on its own (menu, popup, window content).</summary>
    public static void Pop(UIElement el, double dy = 0, double scale = 0.97, int ms = 180, Point? origin = null, double dx = 0)
    {
        if (!Enabled) return;
        el.RenderTransformOrigin = origin ?? new Point(0.5, 0.5);
        var (s, t) = Transforms(el);
        Run(el, UIElement.OpacityProperty, 0, 1, ms);
        Run(t, TranslateTransform.XProperty, dx, 0, ms);
        Run(t, TranslateTransform.YProperty, dy, 0, ms);
        Run(s, ScaleTransform.ScaleXProperty, scale, 1, ms);
        Run(s, ScaleTransform.ScaleYProperty, scale, 1, ms);
    }

    /// <summary>Fade to transparent; set Opacity again (after <c>BeginAnimation(OpacityProperty, null)</c>) to show it.</summary>
    public static void FadeOut(UIElement el, int ms = 240)
    {
        if (Enabled) Run(el, UIElement.OpacityProperty, null, 0, ms);
        else el.Opacity = 0;
    }

    /// <summary>Fades every context menu in from its anchor corner.</summary>
    public static void HookMenus() =>
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            Pop((ContextMenu)s, dy: -4, scale: 0.96, ms: 150, origin: new Point(0, 0));
        }));

    /// <summary>Icon and play buttons shrink a little the moment they're pressed and spring back on release.</summary>
    public static void HookPress()
    {
        static bool Pressable(object s) => s is ButtonBase { Style: { } st } && Application.Current is { } app && // null while shutting down
            (st == app.TryFindResource("IconButton") || st == app.TryFindResource("PlayButton"));
        static void Scale(UIElement el, double to, int ms)
        {
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            var (s, _) = Transforms(el);
            Run(s, ScaleTransform.ScaleXProperty, null, to, ms);
            Run(s, ScaleTransform.ScaleYProperty, null, to, ms);
        }
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((s, _) => { if (Enabled && Pressable(s)) Scale((UIElement)s, 0.92, 100); }));
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.LostMouseCaptureEvent,
            new MouseEventHandler((s, _) => { if (Enabled && Pressable(s)) Scale((UIElement)s, 1, 160); }));
    }

    static void Reset(UIElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        if (el.RenderTransform is TransformGroup { Children: [ScaleTransform s, TranslateTransform t] })
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    static (ScaleTransform, TranslateTransform) Transforms(UIElement el)
    {
        if (el.RenderTransform is TransformGroup { Children: [ScaleTransform s, TranslateTransform t] }) return (s, t);
        s = new ScaleTransform();
        t = new TranslateTransform();
        el.RenderTransform = new TransformGroup { Children = { s, t } };
        return (s, t);
    }

    static void Run(IAnimatable target, DependencyProperty property, double? from, double to, int ms, Action? completed = null)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        if (from is { } f) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(f, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), EaseOut));
        if (completed != null) a.Completed += (_, _) => completed();
        target.BeginAnimation(property, a);
    }
}
