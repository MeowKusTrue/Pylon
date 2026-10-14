using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>A discovered or browsed network entry. Plain strings only: libVLC owns the native media.</summary>
public record NetItem(string Mrl, string Title, bool IsFolder, string? Image = null);

/// <summary>Live list fed by libVLC service discovery (UPnP / mDNS servers, Icecast radio directory).</summary>
public sealed class DiscoveredList : IDisposable
{
    readonly List<MediaDiscoverer> _mds = [];
    readonly DispatcherTimer _debounce;
    public List<NetItem> Items { get; } = [];
    public bool Available => _mds.Count > 0;
    public event Action? Changed;

    public DiscoveredList(LibVLC vlc, Dispatcher ui, params string[] services)
    {
        // big directories (Icecast) add thousands of items: coalesce UI refreshes
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _debounce!.Stop(); Changed?.Invoke(); }, ui) { IsEnabled = false };
        foreach (var name in services)
        {
            MediaDiscoverer md;
            try { md = new MediaDiscoverer(vlc, name); }
            catch (Exception) { continue; } // module missing in this libVLC build
            if (md.MediaList is { } list)
            {
                list.ItemAdded += (_, e) =>
                {
                    var item = Net.ToItem(e.Media); // read on libVLC's thread, while the media is guaranteed alive
                    ui.BeginInvoke(() => { Items.Add(item); Kick(); });
                };
                list.ItemDeleted += (_, e) =>
                {
                    var mrl = e.Media.Mrl;
                    ui.BeginInvoke(() => { Items.RemoveAll(i => i.Mrl == mrl); Kick(); });
                };
            }
            if (md.Start()) _mds.Add(md);
            else md.Dispose();
        }
    }

    void Kick()
    {
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    public void Dispose()
    {
        foreach (var md in _mds) { md.Stop(); md.Dispose(); }
        _mds.Clear();
    }
}

public static class Net
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15), DefaultRequestHeaders = { { "User-Agent", "Pylon/1.0" } } };

    /// <summary>
    /// Internet radio from radio-browser.info (the Icecast/Xiph directory VLC 3 ships a module for now returns an empty list).
    /// country: ISO code, or null for worldwide.
    /// </summary>
    public static async Task<List<NetItem>> RadioStations(string? country, int limit = 100)
    {
        var url = country == null
            ? $"https://all.api.radio-browser.info/json/stations/topclick/{limit}?hidebroken=true"
            : $"https://all.api.radio-browser.info/json/stations/search?countrycode={country}&order=clickcount&reverse=true&hidebroken=true&limit={limit}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        return doc.RootElement.EnumerateArray()
            .Select(s => new NetItem(Str(s, "url_resolved") ?? Str(s, "url") ?? "", (Str(s, "name") ?? "").Trim(), false, Str(s, "favicon") is { Length: > 0 } f ? f : null))
            .Where(s => s.Mrl.Length > 0 && s.Title.Length > 0)
            .DistinctBy(s => s.Title)
            .ToList();

        static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static string? HomeCountry() => CultureInfo.CurrentCulture.Name.Contains('-') ? new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName : null;

    public static NetItem ToItem(Media m)
    {
        var title = m.Meta(MetadataType.Title);
        var mrl = m.Mrl;
        if (string.IsNullOrWhiteSpace(title)) title = Uri.UnescapeDataString(mrl.TrimEnd('/').Split('/').Last());
        return new NetItem(mrl, title, m.Type is MediaType.Directory or MediaType.Playlist);
    }

    /// <summary>Lists a server folder or podcast feed (RSS is expanded by libVLC's playlist demuxer).</summary>
    public static async Task<List<NetItem>> Browse(LibVLC vlc, string mrl, int timeoutMs = 15000)
    {
        var media = new Media(vlc, mrl, FromType.FromLocation);
        try
        {
            await media.Parse(MediaParseOptions.ParseNetwork, timeoutMs);
            return media.SubItems.Select(ToItem).ToList();
        }
        finally { Library.DisposeLater(media); }
    }
}
