using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace Pylon;

/// <summary>Event-driven output checks; no polling of the audio engine during playback.</summary>
public sealed class AudioOutputWatch : IDisposable
{
    readonly DeviceWatcher _watcher = DeviceInformation.CreateWatcher(DeviceClass.AudioRender);
    readonly Dispatcher _ui;
    readonly Action<string[], bool> _changed;
    string _default = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
    bool _ready, _disposed;
    int _request;

    public AudioOutputWatch(Dispatcher ui, Action<string[], bool> changed)
    {
        (_ui, _changed) = (ui, changed);
        _watcher.Added += (_, _) => { if (_ready) Queue(); };
        _watcher.Removed += (_, _) => Queue();
        _watcher.Updated += (_, _) => { if (_ready) Queue(); };
        _watcher.EnumerationCompleted += (_, _) => _ready = true;
        MediaDevice.DefaultAudioRenderDeviceChanged += DefaultChanged;
        _watcher.Start();
    }

    void DefaultChanged(object sender, DefaultAudioRenderDeviceChangedEventArgs args) => Queue();

    void Queue()
    {
        if (_disposed || _ui.HasShutdownStarted) return;
        _ui.BeginInvoke(async () =>
        {
            var request = ++_request;
            await Task.Delay(250);
            if (_disposed || request != _request) return;
            try
            {
                var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioRender);
                var current = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
                var changed = _default != current;
                _default = current;
                if (!_disposed && request == _request) _changed(devices.Where(d => d.IsEnabled).Select(d => d.Name).Distinct().ToArray(), changed);
            }
            catch (Exception e) { ErrorLog.Note("audio devices", e.GetType().Name); }
        });
    }

    public static bool IsMissing(string selected, IEnumerable<string> names) => selected != "System default"
        && !names.Any(name => AudioChain.SameDevice(AudioChain.DeviceName(name), selected));

    public void Dispose()
    {
        _disposed = true;
        MediaDevice.DefaultAudioRenderDeviceChanged -= DefaultChanged;
        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _watcher.Stop();
    }
}
