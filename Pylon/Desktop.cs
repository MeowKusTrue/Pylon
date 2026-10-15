using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Pylon;

/// <summary>Pylon's icon in the notification area (Shell_NotifyIcon; WinForms' NotifyIcon would add 6 MB): click and right-click.</summary>
public sealed class TrayIcon : IDisposable
{
    const int CallbackMessage = 0x8000 + 42, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    const int NimAdd = 0, NimModify = 1, NimDelete = 2, NifMessage = 1, NifIcon = 2, NifTip = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[] small, int count);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);

    static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    readonly HwndSource _window = new(new HwndSourceParameters("PylonTray") { Width = 0, Height = 0, WindowStyle = 0 });
    NotifyIconData _data;

    public event Action? Click, RightClick;

    public TrayIcon(string tip)
    {
        var small = new IntPtr[1];
        ExtractIconEx(Environment.ProcessPath!, 0, null, small, 1);
        _data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _window.Handle, uID = 1, uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage, hIcon = small[0], szTip = tip, szInfo = "", szInfoTitle = "",
        };
        _window.AddHook(Hook);
        Added = Shell_NotifyIcon(NimAdd, ref _data);
    }

    public bool Added { get; }

    /// <summary>Hover text (what's playing).</summary>
    public string Tip
    {
        set
        {
            _data.szTip = value.Length > 127 ? value[..127] : value;
            Shell_NotifyIcon(NimModify, ref _data);
        }
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarCreated) Shell_NotifyIcon(NimAdd, ref _data); // Explorer restarted
        else if (msg == CallbackMessage)
            switch ((int)lParam & 0xFFFF)
            {
                case WmLButtonUp: Click?.Invoke(); break;
                case WmRButtonUp: RightClick?.Invoke(); break;
            }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Shell_NotifyIcon(NimDelete, ref _data);
        DestroyIcon(_data.hIcon);
        _window.Dispose();
    }
}

/// <summary>A small card in the corner of the screen when the track changes: cover, title, artist. Never takes focus.</summary>
public static class TrackPopup
{
    static Window? _window;
    static DispatcherTimer? _timer;

    public static void Show(ImageSource? art, string title, string? artist, double seconds, Action click)
    {
        _timer?.Stop();
        _window?.Close();
        if (seconds <= 0) return;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(art != null ? 12 : 0, 0, 4, 0), MaxWidth = 260 };
        var name = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "FgA0");
        text.Children.Add(name);
        if (!string.IsNullOrWhiteSpace(artist))
        {
            var by = new TextBlock { Text = artist, FontSize = 13, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            by.SetResourceReference(TextBlock.ForegroundProperty, "FgA2");
            text.Children.Add(by);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (art != null)
            row.Children.Add(new Border
            {
                Width = 56, Height = 56, CornerRadius = new CornerRadius(6), ClipToBounds = true,
                Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill },
            });
        row.Children.Add(text);
        var card = new Border { Child = row, Padding = new Thickness(10), CornerRadius = new CornerRadius(12), Margin = new Thickness(16), Cursor = System.Windows.Input.Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "MenuBg");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.45 };
        var w = _window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
            Content = card, FontFamily = Application.Current.TryFindResource("Inter") as FontFamily ?? new FontFamily("Segoe UI"),
        };
        card.MouseLeftButtonUp += (_, _) => { w.Close(); click(); };
        w.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            w.Left = area.Right - w.ActualWidth;
            w.Top = area.Bottom - w.ActualHeight;
            Motion.Pop(card, dy: 24, scale: 0.94, spring: Spring.Smooth);
        };
        w.Show();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.Normal, (_, _) =>
        {
            _timer!.Stop();
            Motion.Animate(card, UIElement.OpacityProperty, 0, Spring.Smooth, done: () => { if (_window == w) _window = null; w.Close(); });
            if (!Motion.Enabled) { _window = null; w.Close(); }
        }, w.Dispatcher);
    }
}

/// <summary>Whether another app is making sound right now on the default output (its peak meter, not just an open stream).</summary>
public static class OtherAudio
{
    public static bool Playing()
    {
        try
        {
            var devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            devices.GetDefaultAudioEndpoint(0 /*render*/, 1 /*multimedia*/, out var device);
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var manager);
            ((IAudioSessionManager2)manager).GetSessionEnumerator(out var sessions);
            sessions.GetCount(out var count);
            var me = (uint)Environment.ProcessId;
            for (var i = 0; i < count; i++)
            {
                sessions.GetSession(i, out var session);
                if (session.IsSystemSoundsSession() == 0) continue; // S_OK: the Windows sounds
                session.GetProcessId(out var pid);
                if (pid == me || pid == 0) continue;
                ((IAudioMeterInformation)session).GetPeakValue(out var peak);
                if (peak > 0.01f) return true;
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        return false;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, int state, out IntPtr devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        void GetAudioSessionControl(IntPtr group, int flags, out IntPtr control); // IAudioSessionManager
        void GetSimpleAudioVolume(IntPtr group, int flags, out IntPtr volume);
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        void GetState(out int state); // IAudioSessionControl
        void GetDisplayName(out IntPtr name);
        void SetDisplayName(IntPtr name, IntPtr context);
        void GetIconPath(out IntPtr path);
        void SetIconPath(IntPtr path, IntPtr context);
        void GetGroupingParam(out Guid group);
        void SetGroupingParam(ref Guid group, IntPtr context);
        void RegisterAudioSessionNotification(IntPtr client);
        void UnregisterAudioSessionNotification(IntPtr client);
        void GetSessionIdentifier(out IntPtr id); // IAudioSessionControl2
        void GetSessionInstanceIdentifier(out IntPtr id);
        void GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }
}
