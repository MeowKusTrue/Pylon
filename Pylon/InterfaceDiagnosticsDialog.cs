using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pylon;

public partial class DialogWindow
{
    public static void InterfaceDiagnostics(Window owner)
    {
        var report = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 230, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Background = R("BgB2"), Foreground = R("FgA1"), BorderThickness = new Thickness(0), Padding = new Thickness(14),
        };
        var compatibility = new CheckBox
        {
            Style = (Style)Application.Current.FindResource("ToggleSwitch"), IsChecked = Settings.Bool("perf.softwareui"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        System.Windows.Automation.AutomationProperties.SetName(compatibility, L.T("Interface compatibility mode"));
        var compatibilityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12), Children =
            { compatibility, new TextBlock { Text = L.T("Interface compatibility mode"), Foreground = R("FgA1"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center } } };
        var refresh = new Button { Content = L.T("Refresh"), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        var log = new Button { Content = L.T("Open diagnostic log"), Style = (Style)Application.Current.FindResource("SecondaryButton"), Margin = new Thickness(10, 0, 0, 0) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Children = { refresh, log } };
        var feedback = new TextBlock { Foreground = R("FgA3"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        var dlg = new DialogWindow(owner, "Interface diagnostics", Stack(
            Section("Interface diagnostics", "Check the drawing mode and display information. If the interface looks broken, try compatibility mode and send the report."),
            compatibilityRow, report, tools, feedback)) { Width = 720 };
        dlg.CancelBtn.Content = L.T("Close");
        dlg.SaveBtn.Content = L.T("Copy report");
        dlg.SaveBtn.IsEnabled = false;
        var closed = false;
        dlg.Closed += (_, _) => closed = true;
        async void Refresh()
        {
            var text = await InterfaceRendering.ReportAsync(owner);
            if (closed) return;
            report.Text = text;
            dlg.SaveBtn.IsEnabled = true;
            feedback.Text = "";
        }
        dlg.Loaded += (_, _) => Refresh();
        refresh.Click += (_, _) => Refresh();
        compatibility.Checked += (_, _) => { Settings.Set("perf.softwareui", true); Refresh(); };
        compatibility.Unchecked += (_, _) => { Settings.Set("perf.softwareui", false); Refresh(); };
        log.Click += (_, _) =>
        {
            ErrorLog.Note("interface diagnostics", "Diagnostic log opened");
            var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
            start.ArgumentList.Add(Path.Combine(Library.Dir, "errors.log"));
            try { Process.Start(start); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
            { feedback.Text = L.T("Couldn't open the log. Use the configurations folder."); }
        };
        dlg._work = () =>
        {
            try { Clipboard.SetText(report.Text); feedback.Text = L.T("Report copied. You can paste it into a message."); }
            catch (System.Runtime.InteropServices.COMException) { feedback.Text = L.T("Clipboard is busy. Try again."); }
            return Task.CompletedTask;
        };
        dlg.Run(() => report.Text.Length > 0);
    }
}
