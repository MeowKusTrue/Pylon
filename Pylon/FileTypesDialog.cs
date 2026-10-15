using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pylon;

public partial class DialogWindow
{
    /// <summary>
    /// "Open your media with Pylon": the formats Pylon registered, shown with their icons; the button opens Pylon's page in
    /// Windows' default apps (only the user may change defaults). Below, Explorer previews through Icaros.
    /// </summary>
    public static void FileTypes(Window owner)
    {
        var tiles = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var ext in MainWindow.VideoExt.Concat(MainWindow.AudioExt))
        {
            var tile = new StackPanel { Width = 64, Margin = new Thickness(0, 0, 6, 10) };
            if (FileIcon(ext) is { } icon) tile.Children.Add(new Image { Source = icon, Width = 40, Height = 40 });
            tile.Children.Add(new TextBlock { Text = ext.TrimStart('.').ToUpperInvariant(), FontSize = 11, Foreground = R("FgA3"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
            tiles.Children.Add(tile);
        }
        var icaros = new Button { Content = L.T("Install Icaros"), Style = (Style)Application.Current.FindResource("SecondaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        var body = Stack(
            Section(L.T("Open your media with Pylon"),
                L.F("Pylon registered {0} formats with their own icons. Windows lets only you pick the default player: the button opens Pylon's page in Windows settings, where \"Set default\" takes them all.", tiles.Children.Count)),
            new ScrollViewer { Content = tiles, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Spacer(8),
            Section(L.T("Previews in Explorer"),
                L.T("Windows already shows frames for most videos and album covers for music. For FLV, RMVB, VOB, MKV with rare codecs and other formats it can't read, the free Icaros extension adds them (its installer asks for administrator rights).")),
            icaros, Spacer(10));
        var dlg = new DialogWindow(owner, "File types", body) { Width = 680 };
        icaros.Click += async (_, _) => { icaros.IsEnabled = false; await ShellRegistration.InstallIcarosAsync(dlg); icaros.IsEnabled = true; };
        dlg.CancelBtn.Content = L.T("Not now");
        dlg.SaveBtn.Content = L.T("Make Pylon the default…");
        if (dlg.Run(() => true)) ShellRegistration.OpenDefaultApps();
    }

    /// <summary>The largest frame of Pylon's icon for that format (the ones Explorer shows).</summary>
    static ImageSource? FileIcon(string ext)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "FileIcons", ext.TrimStart('.') + ".ico");
        if (!File.Exists(path)) return null;
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException) { return null; }
    }
}

public static partial class ShellRegistration
{
    /// <summary>Pylon's page in Settings → Apps → Default apps (Windows 11 / recent Windows 10).</summary>
    public static void OpenDefaultApps() =>
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Pylon") { UseShellExecute = true });

    /// <summary>
    /// Icaros (github.com/Xanashi/Icaros): after the user agrees, its latest installer is downloaded and started as it is;
    /// the user goes through it (and the administrator prompt) themselves.
    /// </summary>
    public static async Task InstallIcarosAsync(Window owner)
    {
        if (!DialogWindow.OfferTool(owner, "Icaros", "Previews for every format",
                "Icaros is a free shell extension (by Xanashi) that lets Explorer show frames from FLV, RMVB, VOB and many other formats. Pylon can download its installer now (about 13 MB from github.com/Xanashi/Icaros) and start it.")) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3), DefaultRequestHeaders = { { "User-Agent", "Pylon" } } };
            using var doc = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/Xanashi/Icaros/releases/latest"));
            var url = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString()!)
                .First(u => u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && u.StartsWith("https://github.com/Xanashi/Icaros/"));
            var setup = Path.Combine(Path.GetTempPath(), Path.GetFileName(url));
            await File.WriteAllBytesAsync(setup, await http.GetByteArrayAsync(url));
            Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(owner, L.T("Couldn't download Icaros. Try again later or get it from github.com/Xanashi/Icaros."), "Icaros");
        }
    }
}
