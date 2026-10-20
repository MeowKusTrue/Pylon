using System.Runtime.InteropServices;

namespace Pylon;

/// <summary>
/// The first Direct3D 11 device in a process starts the graphics driver, about 0.6–1 s on an NVIDIA card. libVLC does
/// that only when the first video opens, after the window is built; starting it on another thread at launch lets it run
/// while WPF builds the window, so the picture comes sooner when Pylon is opened with a video.
/// </summary>
static class GpuWarmup
{
    static IntPtr _device; // kept: releasing the last device may let the driver unload again

    public static void Start() => Task.Run(() =>
    {
        try
        {
            // D3D_DRIVER_TYPE_HARDWARE, BGRA support (what libVLC's direct3d11 output asks for too)
            if (D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out _device, out _, out var context) >= 0 && context != IntPtr.Zero)
                Marshal.Release(context);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    });

    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint levels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
}
