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
        // drops the kept output so the next one inherits the chain above; must name a real module ("any" is refused)
        return mp.SetAudioOutput(audioOutput ?? "mmdevice");
    }

    /// <summary>True when the chain holds more than the equalizer: then equalizer edits go through an engine restart.</summary>
    public static bool HasOthers => Filters().Count > 0;

    static void SetString(VarSetChecked set, MediaPlayer mp, string name, string value)
    {
        var p = Marshal.StringToCoTaskMemUTF8(value);
        try { set(mp.NativeReference, name, VarString, p); } // libVLC copies the string
        finally { Marshal.FreeCoTaskMem(p); }
    }
}
