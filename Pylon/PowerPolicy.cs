using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Pylon;

public static class PowerPolicy
{
    public static bool OnBattery { get; private set; }
    public static bool Economy => Settings.Bool("ui.autoeconomy") && OnBattery;
    public static event Action? Changed;
    public static bool BatteryActive(byte acLineStatus) => acLineStatus == 0;
    public static void Initialize()
    {
        Refresh();
        SystemEvents.PowerModeChanged += PowerChanged;
        Application.Current.Exit += (_, _) => SystemEvents.PowerModeChanged -= PowerChanged;
    }
    static void PowerChanged(object sender, PowerModeChangedEventArgs e) => Application.Current?.Dispatcher.BeginInvoke(Refresh);
    static void Refresh()
    {
        if (!GetSystemPowerStatus(out var status)) return;
        var battery = BatteryActive(status.Ac);
        if (battery == OnBattery) return;
        OnBattery = battery;
        Changed?.Invoke();
    }
    [StructLayout(LayoutKind.Sequential)] struct Status { public byte Ac, Flags, Percent, Saver; public uint Life, FullLife; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out Status status);
}
