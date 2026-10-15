using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Pylon;

/// <summary>
/// The mouse wheel glides every scrollable list instead of jumping 48 px at a time: each notch moves a target, and the
/// offset eases toward it frame by frame (fast start, soft stop), so quick notches add up into one smooth run.
/// Installed once for every ScrollViewer in the app; Windows' "Animation effects" off keeps the plain jump.
/// </summary>
public static class SmoothScroll
{
    sealed class State { public double Target; }
    static readonly ConditionalWeakTable<ScrollViewer, State> States = [];
    static readonly HashSet<ScrollViewer> Moving = [];
    static TimeSpan _last;

    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv || !Motion.Enabled || e.Delta == 0) return;
        // the event tunnels from the outer list in: only the innermost list that can still move that way takes it
        if (Innermost(e.OriginalSource as DependencyObject, e.Delta) != sv) return;
        if (sv.CanContentScroll && VirtualizingPanel.GetScrollUnit(sv.TemplatedParent as ItemsControl ?? (DependencyObject)sv) == ScrollUnit.Item) return; // item steps: leave them
        var state = States.GetOrCreateValue(sv);
        if (!Moving.Contains(sv)) state.Target = sv.VerticalOffset;
        state.Target = Math.Clamp(state.Target - e.Delta * 0.8, 0, sv.ScrollableHeight);
        if (Moving.Add(sv) && Moving.Count == 1) CompositionTarget.Rendering += Tick;
        e.Handled = true;
    }

    static ScrollViewer? Innermost(DependencyObject? from, int delta)
    {
        for (var d = from; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer sv && sv.ScrollableHeight > 0 && (delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight))
                return sv;
        return null;
    }

    static void Tick(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = _last == default ? 1 / 60.0 : Math.Clamp((now - _last).TotalSeconds, 0, 0.05);
        _last = now;
        var k = 1 - Math.Exp(-dt * 16); // about 0.25 of the way each 60 Hz frame
        foreach (var sv in Moving.ToArray())
        {
            if (!States.TryGetValue(sv, out var s) || !sv.IsLoaded) { Moving.Remove(sv); continue; }
            var left = s.Target - sv.VerticalOffset;
            if (Math.Abs(left) < 0.5) { sv.ScrollToVerticalOffset(s.Target); Moving.Remove(sv); continue; }
            sv.ScrollToVerticalOffset(sv.VerticalOffset + left * k);
        }
        if (Moving.Count == 0) { CompositionTarget.Rendering -= Tick; _last = default; }
    }
}
