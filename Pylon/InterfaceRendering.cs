using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Pylon;

/// <summary>UI rendering is separate from the video decoder: driver faults can also break lyrics and sliders.</summary>
public static class InterfaceRendering
{
    static Task<string> _displayReport = Task.FromResult("");

    public static void Apply()
    {
        RenderOptions.ProcessRenderMode = Settings.Bool("perf.softwareui")
            || Environment.GetEnvironmentVariable("PYLON_SOFTWARE_RENDERING") == "1"
            ? RenderMode.SoftwareOnly : RenderMode.Default;
    }

    public static void NoteStartup()
    {
        // Always create the log, including when only music or another app's track is shown.
        ErrorLog.Note("interface setup", $"WPF tier {RenderCapability.Tier >> 16}; mode {RenderOptions.ProcessRenderMode}; OS {Environment.OSVersion.VersionString}; "
            + $"64-bit {Environment.Is64BitProcess}; d3d9 {System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "d3d9.dll"))}");
        _displayReport = Task.Run(() =>
        {
            var report = new List<string>();
            try
            {
                var composition = DwmIsCompositionEnabled(out var enabled) == 0 && enabled;
                report.Add($"DWM composition {composition}; GPUs {string.Join("; ", BugReport.Gpus())}");
                ErrorLog.Note("display setup", report[^1]);
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
                for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                    using (adapter)
                        for (uint n = 0; adapter.EnumOutputs(n, out var output).Success; n++)
                            using (output)
                            using (var modern = output.QueryInterfaceOrNull<Vortice.DXGI.IDXGIOutput6>())
                                if (modern != null)
                                {
                                    var d = modern.Description1;
                                    report.Add($"output {i}:{n}; space {d.ColorSpace}; bits {d.BitsPerColor}; luminance {d.MinLuminance:0.###}-{d.MaxLuminance:0.###}");
                                    ErrorLog.Note("display color", report[^1]);
                                }
            }
            catch (Exception e)
            {
                report.Add("Display information unavailable: " + e.GetType().Name);
                ErrorLog.Note("display setup unavailable", e.GetType().Name);
            }
            return string.Join(Environment.NewLine, report);
        });
    }

    public static async Task<string> ReportAsync(System.Windows.Window owner)
    {
        NoteStartup();
        var dpi = VisualTreeHelper.GetDpi(owner);
        var header = $"Pylon {Updater.Current.ToString(3)}; {RuntimeInformation.FrameworkDescription}\n"
            + $"OS {Environment.OSVersion.VersionString}; 64-bit {Environment.Is64BitProcess}\n"
            + $"WPF tier {RenderCapability.Tier >> 16}; mode {RenderOptions.ProcessRenderMode}; DPI {dpi.PixelsPerDip:0.##}\n"
            + $"Compatibility setting {Settings.Bool("perf.softwareui")}; forced by environment {Environment.GetEnvironmentVariable("PYLON_SOFTWARE_RENDERING") == "1"}\n"
            + $"d3d9.dll present: {System.IO.File.Exists(System.IO.Path.Combine(Environment.SystemDirectory, "d3d9.dll"))}\n";
        return header + await _displayReport + "\n\nLog: " + System.IO.Path.Combine(Library.Dir, "errors.log");
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}
