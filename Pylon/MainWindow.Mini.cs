using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Pylon;

/// <summary>The player: the miniplayer.</summary>
public partial class MainWindow
{
    // ───────── miniplayer (mockup "Miniplayer", 431px wide) ─────────

    Rect _miniRestore;
    WindowState _miniRestoreState;

    public void ToggleMiniplayer()
    {
        if (_fullscreen) ToggleFullscreen();
        _mini = !_mini;
        if (_mini)
        {
            _miniRestoreState = WindowState;
            _miniRestore = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            WindowState = WindowState.Normal;
            MinWidth = 320;
            MinHeight = 200;
            Width = 431;
            SizeMini();
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 24;
            Top = work.Bottom - Height - 24;
            Topmost = true;
        }
        else
        {
            MinWidth = 820;
            MinHeight = 460;
            Left = _miniRestore.Left; Top = _miniRestore.Top; Width = _miniRestore.Width; Height = _miniRestore.Height;
            WindowState = _miniRestoreState;
            Topmost = Settings.Bool("ui.ontop");
        }
        ApplyLayout();
    }

    /// <summary>Miniplayer height: video at its aspect ratio (16:9 until known), a square-ish cover for music. Keeps the bottom edge in place.</summary>
    void SizeMini()
    {
        double stageH = 300;
        if (_index >= 0 && !_isAudio)
        {
            uint w = 0, h = 0;
            stageH = _mp.Size(0, ref w, ref h) && w > 0 && h > 0 ? Math.Clamp(Width * h / w, 160, 480) : Width * 9 / 16;
        }
        var bottom = Top + Height;
        Height = stageH + MiniBarHeight;
        if (!double.IsNaN(bottom) && IsLoaded) Top = bottom - Height;
    }

    const double MiniBarHeight = 118;

    /// <summary>Without the top bar, the stage and the mini controls move the window.</summary>
    void DragArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // a click on the music view plays / pauses like a click on video does (not on lyrics lines, which seek)
        if (MusicView.IsVisible && !_mini && !_minimal && e.ChangedButton == MouseButton.Left && Settings.Str("controls.click") == "Play / Pause"
            && !(e.OriginalSource is DependencyObject src && (FindAncestor<LyricsView>(src) != null || FindAncestor<ButtonBase>(src) != null)))
        {
            _clickTimer.Stop();
            if (e.ClickCount == 1) _clickTimer.Start();
        }
        if (e.ClickCount == 2 && Video.IsVisible && !_mini && e.ChangedButton == MouseButton.Left && Settings.Str("controls.dblclick") == "Toggle fullscreen")
        {
            ToggleFullscreen(); // double click on the blurred area around the video
            e.Handled = true;
            return;
        }
        if (!(_mini || _minimal) || _fullscreen || e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null) return;
        if (e.OriginalSource is DependencyObject d2 && FindAncestor<SeekBar>(d2) != null) return;
        if (e.ClickCount == 2 && _mini) { ToggleMiniplayer(); e.Handled = true; return; }
        try { DragMove(); } catch (InvalidOperationException) { }
        e.Handled = true;
    }

    static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    void FitMaximized()
    {
        // WindowChrome lets a maximized borderless window spill past the screen edge by the resize frame.
        if (_fullscreen) return;
        if (WindowState != WindowState.Maximized) { Root.Margin = new Thickness(0); return; }
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var px = GetSystemMetrics(32 /*SM_CXFRAME*/) + GetSystemMetrics(92 /*SM_CXPADDEDBORDER*/);
        Root.Margin = new Thickness(px / dpi);
    }

    Rect MonitorBounds(bool primary)
    {
        var mon = primary ? MonitorFromPoint(default, 1 /*PRIMARY*/) : MonitorFromWindow(new WindowInteropHelper(this).Handle, 2 /*NEAREST*/);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref info);
        var dpi = VisualTreeHelper.GetDpi(this);
        var r = info.rcMonitor;
        return new Rect(r.left / dpi.DpiScaleX, r.top / dpi.DpiScaleY, (r.right - r.left) / dpi.DpiScaleX, (r.bottom - r.top) / dpi.DpiScaleY);
    }

    void SaveBounds()
    {
        if (!Settings.Bool("ui.remember")) return;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        Settings.Set("app.bounds", FormattableString.Invariant($"{b.Left},{b.Top},{b.Width},{b.Height},{(WindowState == WindowState.Maximized ? 1 : 0)}"));
    }

    void RestoreSavedBounds()
    {
        if (SnapMode || !Settings.Bool("ui.remember")) return;
        var p = Settings.Str("app.bounds").Split(',');
        if (p.Length != 5 || !p.Take(4).All(x => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))) return;
        var v = p.Take(4).Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var r = new Rect(v[0], v[1], v[2], v[3]);
        if (!r.IntersectsWith(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
        if (p[4] == "1") WindowState = WindowState.Maximized;
    }

    void MusicLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = LyricsPane.Visibility == Visibility.Visible ? MusicLayout.ActualWidth * 0.4 : MusicLayout.ActualWidth; // cover column next to the lyrics
        var size = Math.Max(80, Math.Min(738, Math.Min(width, MusicLayout.ActualHeight - (_mini ? 0 : 70))));
        ArtFrame.Width = ArtFrame.Height = size;
        ArtClip.Rect = new Rect(0, 0, size, size);
    }
}
