using System.IO;
using Velopack;

namespace Pylon;

/// <summary>Entry point: Velopack's install / update / uninstall hooks run first (and exit), then the WPF app.</summary>
public static class Program
{
    /// <summary>First start after Velopack installed Pylon (replace an older Inno Setup copy then).</summary>
    public static bool FirstRun { get; private set; }

    /// <summary>PYLON_VELOPACK_TEST=log file: install / update / uninstall test runs that leave the user's system alone.</summary>
    static readonly string? Probe = Environment.GetEnvironmentVariable("PYLON_VELOPACK_TEST");

    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--export-strings", var template]) { L.ExportTemplate(template); return; } // a starting point for a new language
        if (args is ["--add-to-music", var folder, .. var files]) // Explorer's "Add to Music": a copy per selected file, no window
        {
            try { Library.CopyToMusic(files, folder.EndsWith(':') ? folder + "\\" : folder); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Windows.MessageBox.Show(e.Message, "Pylon"); }
            return;
        }
        Perf.Mark("main");
        // a Pylon is already running (in the background, usually): hand it the files before Velopack and WPF even load
        // (~0.1 s instead of ~0.4 s); the autostart copy just leaves. Velopack's own hooks (--veloapp-…) go on below.
        if (Environment.GetEnvironmentVariable("PYLON_SNAP") == null && !(args is [var first, ..] && first.StartsWith("--veloapp", StringComparison.Ordinal))
            && (args is ["--background"] ? SingleInstance.IsRunning() : SingleInstance.ForwardToRunning(args)))
        {
            Perf.Mark("forwarded");
            return;
        }
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => { if (Probe == null) ShellRegistration.Register(); })
            .OnAfterUpdateFastCallback(_ => { if (Probe == null) ShellRegistration.Register(); }) // the files moved: refresh paths
            .OnBeforeUninstallFastCallback(_ => { if (Probe == null) ShellRegistration.Unregister(); })
            .OnFirstRun(_ => FirstRun = true)
            .Run();
        if (Probe != null)
        {
            RunProbe(Probe);
            return;
        }
        Perf.Mark("velopack done");
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>No window: log the running version, and if the feed has a newer one, download it (delta) and apply it.</summary>
    static void RunProbe(string log)
    {
        File.AppendAllText(log, $"started {Updater.Current.ToString(3)} from {AppContext.BaseDirectory} firstRun={FirstRun}{Environment.NewLine}");
        var update = Updater.CheckAsync().GetAwaiter().GetResult();
        if (update == null) return;
        File.AppendAllText(log, $"update found {update.Version.ToString(3)}{Environment.NewLine}");
        Updater.DownloadAndRunAsync(update, null).GetAwaiter().GetResult();
        File.AppendAllText(log, $"downloaded, applying on exit{Environment.NewLine}");
    }
}
