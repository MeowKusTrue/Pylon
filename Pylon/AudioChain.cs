using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// libVLC 3 gives every media player its own, non-inheriting "audio-filter" variable, so --audio-filter on the command
/// line never reaches playback (compressor, spatializer, widener, pitch, chorus, karaoke and normalizer silently did
/// nothing), and its equalizer call overwrites that variable with just "equalizer". Here the variable is set on the
/// player through libvlccore, and the player's audio output is reset so the next one is built with the whole chain.
/// Filter parameters (--compressor-ratio etc.) still come from the command line: those variables do inherit.
/// </summary>
public static class AudioChain
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int VarSetChecked(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int VarName(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int VarCreate(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr ListChildren(IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ListRelease(IntPtr list);

    const int VarString = 0x0040, VarFloat = 0x0050;
    static IntPtr _lib;
    static VarSetChecked? _set;

    static T? Export<T>(string name) where T : Delegate
    {
        if (_lib == IntPtr.Zero)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "libvlccore.dll");
            if (!NativeLibrary.TryLoad(path, out _lib) && !NativeLibrary.TryLoad("libvlccore.dll", out _lib)) return null;
        }
        return NativeLibrary.TryGetExport(_lib, name, out var fn) ? Marshal.GetDelegateForFunctionPointer<T>(fn) : null;
    }

    static VarSetChecked? Setter() => _set ??= Export<VarSetChecked>("var_SetChecked");

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MediaPlayer, object> GainVar = [];
    static VarCreate? _create;
    static VarName? _trigger;
    static ListChildren? _children;
    static ListRelease? _release;

    /// <summary>
    /// This player's own loudness, 0…1 (crossfades). Its volume can't do that: below 100 % libVLC sets the Windows session
    /// volume, which every player in the process shares. libVLC's audio output multiplies its replay-gain factor by the
    /// "gain" variable it inherits from the player, so that is set on the player (a starting output reads it at once) and a
    /// running output is asked to recompute. Returns false when libvlccore isn't reachable.
    /// </summary>
    public static bool SetGain(MediaPlayer mp, float gain)
    {
        if (Setter() is not { } set || (_create ??= Export<VarCreate>("var_Create")) is not { } create
            || (_trigger ??= Export<VarName>("var_TriggerCallback")) is not { } trigger
            || (_children ??= Export<ListChildren>("vlc_list_children")) is not { } children
            || (_release ??= Export<ListRelease>("vlc_list_release")) is not { } release) return false;
        if (!GainVar.TryGetValue(mp, out _))
        {
            create(mp.NativeReference, "gain", VarFloat);
            GainVar.Add(mp, new());
        }
        set(mp.NativeReference, "gain", VarFloat, (IntPtr)BitConverter.SingleToInt32Bits(gain));
        // the output is a child object of the player: struct vlc_list_t { int i_type; int i_count; vlc_value_t *p_values; }
        var list = children(mp.NativeReference);
        if (list == IntPtr.Zero) return true;
        try
        {
            var count = Marshal.ReadInt32(list, 4);
            var values = Marshal.ReadIntPtr(list, 8);
            for (var i = 0; i < count; i++)
            {
                var obj = Marshal.ReadIntPtr(values, i * 8);
                if (obj != IntPtr.Zero && Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(obj)) == "audio output") // object_type comes first
                    trigger(obj, "audio-replay-gain-mode");
            }
        }
        finally { release(list); }
        return true;
    }

    /// <summary>
    /// waveOut, or DirectSound (Settings → Audio). Both take any sample rate and Windows' engine converts it, so libVLC
    /// never resamples: WASAPI (mmdevice) only takes the device's rate, and libVLC's converters in this build either sound
    /// bad (ugly) or break playback after a seek (speex; 2950 sync errors and a busy core over 7 seeks vs 2 and 2 %).
    /// waveOut by default since 2.0.2: libVLC's DirectSound can't drain, so the end of the file came while 0.5-1.2 s was
    /// still buffered and that was thrown away; every song and video lost its end (heard with marker tones recorded off a
    /// virtual cable). waveOut plays to the last sample, as cleanly (65.8 dB SNR on a 1 kHz tone either way).
    /// </summary>
    public static string Output => Settings.Str("audio.output") == "DirectSound" ? "directsound" : "waveout";

    /// <summary>
    /// libVLC's volume for a slider level (1 = 100 %). DirectSound turns volume v into v³ and sits 12 dB below waveOut at
    /// 100 %; waveOut is linear (both measured on a recorded tone: DirectSound 50 % -18 dB, 200 % +18 dB; waveOut 50 % -6 dB,
    /// 200 % +6 dB). So waveOut gets 25·v³ and every slider position sounds as it did before 2.0.2.
    /// </summary>
    public static int VlcVolume(double level) =>
        Output == "waveout" ? level <= 0 ? 0 : Math.Clamp((int)Math.Round(25 * level * level * level), 1, 200) : (int)Math.Round(level * 100);

    /// <summary>A device's name without waveOut's " ($1,$64)" (waveOut also cuts names to 31 characters).</summary>
    public static string DeviceName(string description) => System.Text.RegularExpressions.Regex.Replace(description, @" \(\$\d+,\$\d+\)$", "");

    /// <summary>The saved device among the player's: the same name, or one that starts like it (saved from the other output).</summary>
    public static string? DeviceId(MediaPlayer mp, string saved)
    {
        var list = mp.AudioOutputDeviceEnum.Select(d => (Name: DeviceName(d.Description), d.DeviceIdentifier)).ToList();
        return list.FirstOrDefault(d => d.Name == saved).DeviceIdentifier ?? list.FirstOrDefault(d => SameDevice(d.Name, saved)).DeviceIdentifier;
    }

    /// <summary>One device under both outputs' names: "CABLE Input (VB-Audio Virtual Cable)" is "CABLE Input (VB-Audio Virtual C" in waveOut.</summary>
    public static bool SameDevice(string a, string b) =>
        a == b || Math.Min(a.Length, b.Length) >= 12 && (a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    /// <summary>Audio filters other than the equalizer, in chain order.</summary>
    public static List<string> Filters() => Settings.AudioFilterNames();

    /// <summary>Builds the chain into a freshly created player (before it plays). Returns false when libvlccore isn't reachable.</summary>
    public static bool Apply(MediaPlayer mp, string? audioOutput)
    {
        var set = Setter();
        if (set == null) return false;
        var names = Filters();
        if (Settings.Bool("audio.eq") && Effects.Build() is { } eq)
        {
            using (eq)
            {
                // the equalizer reads its bands from the player's variables when the output builds it
                var bands = string.Join(" ", Enumerable.Range(0, (int)eq.BandCount).Select(i => eq.Amp((uint)i).ToString("0.0", CultureInfo.InvariantCulture)));
                SetString(set, mp, "equalizer-bands", bands);
                set(mp.NativeReference, "equalizer-preamp", VarFloat, (IntPtr)BitConverter.SingleToInt32Bits(eq.Preamp));
            }
            names.Insert(0, "equalizer");
        }
        SetString(set, mp, "audio-filter", string.Join(":", names));
        Built.AddOrUpdate(mp, ChainKey(Settings.EngineArgs()));
        // drops the kept output so the next one inherits the chain above; must name a real module ("any" is refused)
        return mp.SetAudioOutput(audioOutput ?? Output);
    }

    /// <summary>True when the chain holds more than the equalizer: then equalizer edits go through an engine restart.</summary>
    public static bool HasOthers => Filters().Count > 0;

    static readonly string[] FilterParams = ["--compressor-", "--spatializer-", "--norm-max-level=", "--pitch-shift=", "--equalizer-2pass", "--no-audio-time-stretch"];

    /// <summary>
    /// What the running chain was built from, per player. The equalizer, compressor and spatializer follow their variables
    /// while they run (callbacks on the output); anything else (which filters, the normalizer level, pitch, 2-pass) only
    /// takes effect through a rebuild, and a rebuild drops a moment of sound, so it happens only when one of those changed.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MediaPlayer, string> Built = [];

    /// <summary>Whether the last ApplyLive had to rebuild the chain (checks).</summary>
    public static bool LastRebuilt { get; private set; }

    static string ChainKey(IEnumerable<string> args) =>
        (Settings.Bool("audio.eq") ? "equalizer:" : "") + string.Join(":", Filters()) + "|"
        + string.Join(" ", args.Where(a => FilterParams.Any(a.StartsWith) && !a.StartsWith("--compressor-") && !a.StartsWith("--spatializer-")));

    /// <summary>
    /// Effects change while playing, without restarting the engine (which stuttered picture and sound on every slider
    /// move). The filters' parameters (--compressor-ratio and friends) are set as variables on the player and on its running
    /// audio output: filters create theirs on the output once and never read the command line again. Running filters with
    /// callbacks pick the new values up at once; when the chain itself has to change, setting "audio-filter" on the output
    /// makes libVLC rebuild just the filter chain (its own callback), without restarting the engine.
    /// Returns false when libvlccore isn't reachable; the caller restarts the engine instead.
    /// </summary>
    public static bool ApplyLive(MediaPlayer mp)
    {
        if (Setter() is not { } set || (_create ??= Export<VarCreate>("var_Create")) is not { } create
            || (_children ??= Export<ListChildren>("vlc_list_children")) is not { } children
            || (_release ??= Export<ListRelease>("vlc_list_release")) is not { } release) return false;
        var args = Settings.EngineArgs();
        var floats = args.Where(a => FilterParams.Any(a.StartsWith) && a.Contains('='))
            .Select(a => (Name: a[2..a.IndexOf('=')], Value: float.TryParse(a[(a.IndexOf('=') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN))
            .Where(p => !float.IsNaN(p.Value)).ToList();
        var stretch = !args.Contains("--no-audio-time-stretch");
        var twoPass = args.Contains("--equalizer-2pass");
        var names = Filters();
        string? bands = null;
        var preamp = 0f;
        if (Settings.Bool("audio.eq") && Effects.Build() is { } eq)
            using (eq)
            {
                bands = string.Join(" ", Enumerable.Range(0, (int)eq.BandCount).Select(i => eq.Amp((uint)i).ToString("0.0", CultureInfo.InvariantCulture)));
                preamp = eq.Preamp;
                names.Insert(0, "equalizer");
            }

        void Write(IntPtr obj, bool restart)
        {
            foreach (var (name, value) in floats)
            {
                create(obj, name, VarFloat);
                set(obj, name, VarFloat, (IntPtr)BitConverter.SingleToInt32Bits(value));
            }
            create(obj, "audio-time-stretch", VarBool);
            set(obj, "audio-time-stretch", VarBool, stretch ? 1 : 0);
            create(obj, "equalizer-2pass", VarBool);
            set(obj, "equalizer-2pass", VarBool, twoPass ? 1 : 0);
            if (bands != null)
            {
                create(obj, "equalizer-bands", VarString);
                SetString(set, obj, "equalizer-bands", bands);
                create(obj, "equalizer-preamp", VarFloat);
                set(obj, "equalizer-preamp", VarFloat, (IntPtr)BitConverter.SingleToInt32Bits(preamp));
            }
            if (restart || obj == mp.NativeReference) SetString(set, obj, "audio-filter", string.Join(":", names)); // on the output: rebuilds the chain
        }

        var key = ChainKey(args);
        var rebuild = LastRebuilt = !Built.TryGetValue(mp, out var was) || was != key;
        Built.AddOrUpdate(mp, key);
        Write(mp.NativeReference, false);
        var list = children(mp.NativeReference);
        if (list == IntPtr.Zero) return true; // not playing: the next output inherits all of it
        try
        {
            var count = Marshal.ReadInt32(list, 4);
            var values = Marshal.ReadIntPtr(list, 8);
            for (var i = 0; i < count; i++)
                if (Marshal.ReadIntPtr(values, i * 8) is var obj && obj != IntPtr.Zero && Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(obj)) == "audio output")
                    Write(obj, rebuild);
        }
        finally { release(list); }
        return true;
    }

    const int VarBool = 0x0020;

    static void SetString(VarSetChecked set, MediaPlayer mp, string name, string value) => SetString(set, mp.NativeReference, name, value);

    static void SetString(VarSetChecked set, IntPtr obj, string name, string value)
    {
        var p = Marshal.StringToCoTaskMemUTF8(value);
        try { set(obj, name, VarString, p); } // libVLC copies the string
        finally { Marshal.FreeCoTaskMem(p); }
    }
}
