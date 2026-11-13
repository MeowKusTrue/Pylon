using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Pylon;

public static class Accessibility
{
    public static void Install() => EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
    {
        if (sender is not ButtonBase button || AutomationProperties.GetName(button).Length > 0) return;
        var name = button.ToolTip as string ?? button.Content as string;
        name ??= button.Name switch
        {
            "PlayBtn" or "MiniPlay" => L.T("Play / pause"), "MuteBtn" or "MiniMute" => L.T("Mute"),
            "SidebarBtn" => L.T("Show / hide file browser"), "ListBtn" => L.T("Show / hide queue"),
            "LyricsBtn" => L.T("Show Lyrics"), "MenuBtn" => L.T("Menu"), "MaxBtn" => L.T("Maximize"), _ => null,
        };
        if (!string.IsNullOrWhiteSpace(name)) AutomationProperties.SetName(button, name);
    }));
}

public abstract class AccessibleRange : FrameworkElement
{
    protected AccessibleRange() { Focusable = true; }
    public double AutomationMaximum { get; set; } = 100;
    protected abstract double RangeValue { get; }
    protected abstract void ChangeRange(double value);
    internal double AccessibleValue => RangeValue * AutomationMaximum;
    internal void SetAccessibleValue(double value)
    {
        if (!IsEnabled) throw new ElementNotEnabledException();
        if (!double.IsFinite(value) || value < 0 || value > AutomationMaximum) throw new ArgumentOutOfRangeException(nameof(value));
        ChangeRange(value / AutomationMaximum);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new RangePeer(this);
    protected void DrawKeyboardFocus(System.Windows.Media.DrawingContext dc)
    {
        if (IsKeyboardFocused && ActualWidth > 2 && ActualHeight > 2)
            dc.DrawRoundedRectangle(null, new System.Windows.Media.Pen((System.Windows.Media.Brush)FindResource("Accent"), 1),
                new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), 3, 3);
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var next = e.Key switch
        {
            Key.Left or Key.Down => RangeValue - 0.01, Key.Right or Key.Up => RangeValue + 0.01,
            Key.PageDown => RangeValue - 0.1, Key.PageUp => RangeValue + 0.1, Key.Home => 0, Key.End => 1, _ => double.NaN,
        };
        if (double.IsFinite(next)) { ChangeRange(Math.Clamp(next, 0, 1)); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected static void AccessibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var range = (AccessibleRange)d;
        if (UIElementAutomationPeer.FromElement(range) is { } peer)
            peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, (double)e.OldValue * range.AutomationMaximum, (double)e.NewValue * range.AutomationMaximum);
    }

    sealed class RangePeer(AccessibleRange owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => owner.GetType().Name;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => !owner.IsEnabled;
        public double LargeChange => owner.AutomationMaximum * 0.1;
        public double SmallChange => owner.AutomationMaximum * 0.01;
        public double Maximum => owner.AutomationMaximum;
        public double Minimum => 0;
        public double Value => owner.AccessibleValue;
        public void SetValue(double value) => owner.Dispatcher.Invoke(() => owner.SetAccessibleValue(value));
    }
}
