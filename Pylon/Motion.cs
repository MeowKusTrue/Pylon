using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Pylon;

/// <summary>Apple's spring, described the way UIKit/SwiftUI do: response (seconds, ≈ how fast) and damping ratio (1 = no overshoot).</summary>
public readonly record struct Spring(double Response, double Damping)
{
    public static readonly Spring Smooth = new(0.38, 1.0);  // panels, pages: graceful, no bounce
    public static readonly Spring Snappy = new(0.28, 1.0);  // menus, popovers, toasts
    public static readonly Spring Press = new(0.12, 1.0);   // finger-down feedback, near instant
    public static readonly Spring Release = new(0.36, 0.62); // finger-up: a small, physical bounce back
    public static readonly Spring Toggle = new(0.32, 0.78);  // switch knob
}

/// <summary>
/// UI motion in the iOS manner (Designing Fluid Interfaces, WWDC 2018): springs instead of fixed curves, every animation
/// starts from the live on-screen value and keeps its velocity when retargeted, so anything can be interrupted or
/// reversed mid-flight without a jump. Only transform and opacity move. Keyboard-triggered changes stay instant;
/// Windows' "Animation effects" switch turns motion off.
/// </summary>
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>The change came from the mouse (or pen/touch). Hotkey-driven changes don't animate.</summary>
    public static bool ByPointer => InputManager.Current.MostRecentInputDevice is not KeyboardDevice;

    // ───────── spring engine ─────────

    sealed class Driver
    {
        public required DependencyObject Target;
        public required DependencyProperty Property;
        public double Value, Velocity, To, Epsilon;
        public Spring Spring;
        public Action? Done;
    }

    static readonly Dictionary<(DependencyObject, DependencyProperty), Driver> Drivers = new();
    static readonly Stopwatch Clock = new();
    static bool _hooked;

    /// <summary>Springs <paramref name="property"/> to <paramref name="to"/>. A running spring is retargeted and keeps its velocity.</summary>
    public static void Animate(DependencyObject target, DependencyProperty property, double to, Spring spring, double? from = null, Action? done = null)
    {
        if (!Enabled)
        {
            Stop(target, property);
            target.SetValue(property, to);
            done?.Invoke();
            return;
        }
        var key = (target, property);
        if (!Drivers.TryGetValue(key, out var d))
            Drivers[key] = d = new Driver { Target = target, Property = property, Value = (double)target.GetValue(property) };
        if (from is { } f)
        {
            d.Value = f;
            d.Velocity = 0;
            target.SetValue(property, f);
        }
        d.To = to;
        d.Spring = spring;
        d.Epsilon = Math.Max(0.0004, Math.Abs(to - d.Value) * 0.0015);
        d.Done = done; // an interrupted animation never reports completion
        if (!_hooked)
        {
            _hooked = true;
            Clock.Restart();
            CompositionTarget.Rendering += Tick;
        }
    }

    /// <summary>Stops any spring on the property and sets it right away.</summary>
    public static void Set(DependencyObject target, DependencyProperty property, double value)
    {
        Stop(target, property);
        target.SetValue(property, value);
    }

    static void Stop(DependencyObject target, DependencyProperty property) => Drivers.Remove((target, property));

    static void Tick(object? sender, EventArgs e)
    {
        var dt = Math.Min(Clock.Elapsed.TotalSeconds, 1 / 20.0); // after a stall, don't jump
        Clock.Restart();
        List<Driver>? finished = null;
        foreach (var d in Drivers.Values.ToArray()) // SetValue can start other springs
        {
            double k = Math.Pow(2 * Math.PI / d.Spring.Response, 2), c = 4 * Math.PI * d.Spring.Damping / d.Spring.Response;
            for (double left = dt; left > 0; left -= 1 / 480.0) // fixed sub-steps keep stiff springs stable
            {
                var h = Math.Min(left, 1 / 480.0);
                d.Velocity += (-k * (d.Value - d.To) - c * d.Velocity) * h;
                d.Value += d.Velocity * h;
            }
            if (Math.Abs(d.Value - d.To) < d.Epsilon && Math.Abs(d.Velocity) < d.Epsilon * 20)
            {
                d.Value = d.To;
                (finished ??= []).Add(d);
            }
            d.Target.SetValue(d.Property, d.Value);
        }
        if (finished != null)
            foreach (var d in finished)
            {
                Drivers.Remove((d.Target, d.Property));
                d.Done?.Invoke();
            }
        if (Drivers.Count == 0)
        {
            CompositionTarget.Rendering -= Tick;
            _hooked = false;
        }
    }

    // ───────── enter / exit ─────────

    sealed class State { public int Gen; public bool Leaving; }
    static readonly ConditionalWeakTable<UIElement, State> States = new();

    /// <summary>Visible and not on its way out.</summary>
    public static bool IsShown(UIElement el) => el.Visibility == Visibility.Visible && !States.GetOrCreateValue(el).Leaving;

    /// <summary>Makes the element visible, gliding in from (dx, dy) and <paramref name="scale"/>. Reverses a running exit from where it is.</summary>
    public static void In(UIElement el, double dx = 0, double dy = 0, double scale = 1, Spring? spring = null, bool animate = true)
    {
        var st = States.GetOrCreateValue(el);
        var resume = st.Leaving;
        st.Gen++;
        st.Leaving = false;
        var wasShown = el.Visibility == Visibility.Visible && !resume;
        el.Visibility = Visibility.Visible;
        if (!Enabled || !animate) { Reset(el); return; }
        if (wasShown) return;
        var sp = spring ?? Spring.Smooth;
        var (s, t) = Transforms(el);
        Animate(el, UIElement.OpacityProperty, 1, sp, resume ? null : 0);
        Animate(t, TranslateTransform.XProperty, 0, sp, resume ? null : dx);
        Animate(t, TranslateTransform.YProperty, 0, sp, resume ? null : dy);
        Animate(s, ScaleTransform.ScaleXProperty, 1, sp, resume ? null : scale);
        Animate(s, ScaleTransform.ScaleYProperty, 1, sp, resume ? null : scale);
    }

    /// <summary>Leaves along the path it came in on, then calls <paramref name="done"/> unless it was shown again meanwhile.</summary>
    public static void Out(UIElement el, Action done, double dx = 0, double dy = 0, double scale = 1, Spring? spring = null, bool animate = true)
    {
        var st = States.GetOrCreateValue(el);
        if (!Enabled || !animate || el.Visibility != Visibility.Visible) { st.Gen++; st.Leaving = false; done(); Reset(el); return; }
        var gen = ++st.Gen;
        st.Leaving = true;
        var sp = spring ?? Spring.Snappy; // exits are quicker than entrances
        var (s, t) = Transforms(el);
        Animate(t, TranslateTransform.XProperty, dx, sp);
        Animate(t, TranslateTransform.YProperty, dy, sp);
        Animate(s, ScaleTransform.ScaleXProperty, scale, sp);
        Animate(s, ScaleTransform.ScaleYProperty, scale, sp);
        Animate(el, UIElement.OpacityProperty, 0, sp, done: () =>
        {
            if (st.Gen != gen) return;
            st.Leaving = false;
            done();
            Reset(el);
        });
    }

    /// <summary>Entrance for something that just appeared on its own (menu, popup, page, window content).</summary>
    public static void Pop(UIElement el, double dy = 0, double scale = 0.96, Point? origin = null, double dx = 0, Spring? spring = null)
    {
        if (!Enabled) return;
        var sp = spring ?? Spring.Snappy;
        el.RenderTransformOrigin = origin ?? new Point(0.5, 0.5);
        var (s, t) = Transforms(el);
        Animate(el, UIElement.OpacityProperty, 1, sp, 0);
        Animate(t, TranslateTransform.XProperty, 0, sp, dx);
        Animate(t, TranslateTransform.YProperty, 0, sp, dy);
        Animate(s, ScaleTransform.ScaleXProperty, 1, sp, scale);
        Animate(s, ScaleTransform.ScaleYProperty, 1, sp, scale);
    }

    /// <summary>Fades to transparent; <see cref="Set"/> the opacity to show it again.</summary>
    public static void FadeOut(UIElement el) => Animate(el, UIElement.OpacityProperty, 0, Spring.Smooth);

    static void Reset(UIElement el)
    {
        Set(el, UIElement.OpacityProperty, 1);
        if (el.RenderTransform is TransformGroup { Children: [ScaleTransform s, TranslateTransform t] })
        {
            Set(t, TranslateTransform.XProperty, 0);
            Set(t, TranslateTransform.YProperty, 0);
            Set(s, ScaleTransform.ScaleXProperty, 1);
            Set(s, ScaleTransform.ScaleYProperty, 1);
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

    // ───────── app-wide hooks ─────────

    /// <summary>Context menus and their submenus pop from their anchor corner.</summary>
    public static void HookMenus()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) =>
            Pop((ContextMenu)s, dy: -6, scale: 0.95, origin: new Point(0, 0))));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((s, e) =>
        {
            if (e.OriginalSource != s || s is not MenuItem mi || mi.Template?.FindName("PART_Popup", mi) is not Popup { Child: UIElement child }) return;
            Pop(child, dx: -6, scale: 0.97, origin: new Point(0, 0));
        }));
    }

    /// <summary>Icon and play buttons give way the instant they're pressed and spring back on release.</summary>
    public static void HookPress()
    {
        static bool Pressable(object s) => s is ButtonBase { Style: { } st } && Application.Current is { } app && // null while shutting down
            (st == app.TryFindResource("IconButton") || st == app.TryFindResource("PlayButton"));
        static void Scale(UIElement el, double to, Spring spring)
        {
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            var (s, _) = Transforms(el);
            Animate(s, ScaleTransform.ScaleXProperty, to, spring);
            Animate(s, ScaleTransform.ScaleYProperty, to, spring);
        }
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((s, _) => { if (Pressable(s)) Scale((UIElement)s, 0.9, Spring.Press); }));
        EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.LostMouseCaptureEvent,
            new MouseEventHandler((s, _) => { if (Pressable(s)) Scale((UIElement)s, 1, Spring.Release); }));
    }

    /// <summary>The knob of every ToggleSwitch slides across on a spring instead of jumping.</summary>
    public static void HookSwitches()
    {
        static void Place(CheckBox box, bool animate)
        {
            if (box.Template?.FindName("knob", box) is not FrameworkElement knob) return;
            if (knob.RenderTransform is not TranslateTransform t) knob.RenderTransform = t = new TranslateTransform();
            var to = box.IsChecked == true ? 36 : 0; // 68 wide - 2 × 4 padding - 24 knob
            if (animate) Animate(t, TranslateTransform.XProperty, to, Spring.Toggle);
            else Set(t, TranslateTransform.XProperty, to);
        }
        EventManager.RegisterClassHandler(typeof(CheckBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Place((CheckBox)s, false)));
        EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.CheckedEvent, new RoutedEventHandler((s, _) => Place((CheckBox)s, ((CheckBox)s).IsLoaded)));
        EventManager.RegisterClassHandler(typeof(CheckBox), ToggleButton.UncheckedEvent, new RoutedEventHandler((s, _) => Place((CheckBox)s, ((CheckBox)s).IsLoaded)));
    }
}
