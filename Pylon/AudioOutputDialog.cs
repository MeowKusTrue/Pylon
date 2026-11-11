using System.Windows;
using System.Windows.Controls;

namespace Pylon;

public partial class DialogWindow
{
    public static string? RecoverAudioOutput(Window owner, string[] names)
    {
        var choices = new ComboBox { MinWidth = 320, Style = (Style)Application.Current.FindResource("Dropdown") };
        foreach (var name in names.Length > 0 ? new[] { "System default" }.Concat(names) : [])
            choices.Items.Add(new ComboBoxItem { Content = L.T(name), Tag = name });
        if (choices.Items.Count > 0) choices.SelectedIndex = 0;
        var dlg = new DialogWindow(owner, "Audio output", Stack(
            Section("Choose an audio output", "The previous output changed or disconnected. Your volume is kept. Playback resumes only after you choose an output."), choices));
        dlg.SaveBtn.Content = L.T("Use this output");
        dlg.CancelBtn.Content = L.T("Stay stopped");
        return dlg.Run(() => choices.SelectedItem != null) ? (string)((ComboBoxItem)choices.SelectedItem).Tag : null;
    }
}
