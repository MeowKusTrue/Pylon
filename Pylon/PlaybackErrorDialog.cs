using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pylon;

public partial class DialogWindow
{
    public static void PlaybackError(Window owner, string name, PlaybackFailure failure)
    {
        var details = new TextBox { Text = failure.Details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 230, FontSize = 12, FontFamily = new FontFamily("Consolas"),
            Foreground = R("FgA2"), Background = R("BgB2"), BorderThickness = new Thickness(0), Padding = new Thickness(12) };
        var feedback = new TextBlock { Foreground = R("FgA3"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
        var dlg = new DialogWindow(owner, "Playback error", Stack(
            Section(L.F(failure.Reason.StartsWith("Playback continues") ? "Some tracks could not be played in {0}" : "Can't open {0}", name), L.T(failure.Reason)),
            new Expander { Header = L.T("Technical details"), Foreground = R("FgA1"), Content = details }, feedback)) { Width = 700 };
        dlg.CancelBtn.Content = L.T("Close");
        dlg.SaveBtn.Content = L.T("Copy report");
        dlg._work = () =>
        {
            try { Clipboard.SetText(L.T(failure.Reason) + "\n\n" + failure.Details); feedback.Text = L.T("Report copied. You can paste it into a message."); }
            catch (System.Runtime.InteropServices.COMException) { feedback.Text = L.T("Clipboard is busy. Try again."); }
            return Task.CompletedTask;
        };
        dlg.Run(() => true);
    }
}
