using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pylon;

public partial class DialogWindow
{
    static Border QrBox(string url)
    {
        var qr = new Border
        {
            Width = 216, Height = 216, Padding = new Thickness(8), CornerRadius = new CornerRadius(12), Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 24, 0),
            Child = new Image { Source = CastServer.Qr(url, new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)), Brushes.White), Stretch = Stretch.Uniform },
        };
        RenderOptions.SetEdgeMode(qr, EdgeMode.Aliased); // crisp modules
        return qr;
    }

    /// <summary>The phone remote: QR code and address. True = turn the remote off.</summary>
    public static bool Remote(Window owner, string url)
    {
        var qr = QrBox(url);
        var info = Stack(
            new TextBlock { Text = L.T("Scan the code with a phone on the same Wi-Fi: play, pause, skip and volume from its browser. The address has a private key; only those you show it to can use it."), TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = R("FgA2"), LineHeight = 21 },
            Spacer(14),
            Address(url));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(qr, Dock.Left);
        row.Children.Add(qr);
        row.Children.Add(info);
        var dlg = new DialogWindow(owner, "Phone Remote", Stack(Section(L.T("Phone Remote"), L.T("Control Pylon from your phone")), row)) { Width = 720 };
        dlg.CancelBtn.Content = L.T("Keep it on");
        dlg.SaveBtn.Content = L.T("Turn off");
        return dlg.Run(() => true);
    }

    static FrameworkElement Address(string text)
    {
        var copy = new Button { Content = L.T("Copy"), Style = (Style)Application.Current.FindResource("SecondaryButton"), Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => { Clipboard.SetText(text); copy.Content = L.T("Copied"); };
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 14) };
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        row.Children.Add(new Border
        {
            Background = R("BgB2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 9),
            Child = new TextBlock { Text = text, FontSize = 14, Foreground = R("FgA1"), TextTrimming = TextTrimming.CharacterEllipsis },
        });
        return row;
    }

    /// <summary>The running network stream: a QR code to the landing page and the addresses. True = stop streaming.</summary>
    public static bool Cast(Window owner, CastServer cast)
    {
        var qr = QrBox(cast.PageUrl);
        var info = Stack(
            new TextBlock { Text = L.T("Scan the code with a phone, or type the stream address into VLC on the TV (Network stream). Both devices need to be on the same Wi-Fi or network."), TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = R("FgA2"), LineHeight = 21 },
            Spacer(14),
            new TextBlock { Text = L.T("Stream address"), FontSize = 13, Foreground = R("FgA3") },
            Address(cast.StreamUrl),
            new TextBlock { Text = L.T("Page for phones"), FontSize = 13, Foreground = R("FgA3") },
            Address(cast.PageUrl));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(qr, Dock.Left);
        row.Children.Add(qr);
        row.Children.Add(info);
        var body = Stack(Section(L.T("Streaming to TV / Phone"), L.F("Now playing: {0}", cast.Title)), row);
        var dlg = new DialogWindow(owner, "Stream to TV / Phone", body) { Width = 720 };
        dlg.CancelBtn.Content = L.T("Keep streaming");
        dlg.SaveBtn.Content = L.T("Stop streaming");
        return dlg.Run(() => true);
    }
}
