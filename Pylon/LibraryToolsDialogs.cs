using System.Windows;
using System.Windows.Controls;

namespace Pylon;

public partial class DialogWindow
{
    public static bool ConfirmRelocation(Window owner, IReadOnlyDictionary<string, string> map)
    {
        var preview = new TextBox { IsReadOnly = true, Text = string.Join("\n\n", map.Take(200).Select(p => p.Key + "\n→ " + p.Value)),
            TextWrapping = TextWrapping.Wrap, Height = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = R("BgB2"), Foreground = R("FgA1"), Padding = new Thickness(12), BorderThickness = new Thickness(0) };
        var dialog = new DialogWindow(owner, "Find moved files", Stack(Section(L.F("Files to relink: {0}", map.Count),
            "Review the replacements. Files are matched by their relative path; no files are moved or deleted."), preview)) { Width = 760 };
        dialog.SaveBtn.Content = L.T("Apply");
        return dialog.Run(() => true);
    }
}
