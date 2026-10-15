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
    public static void Main()
    {
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
