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

    const int VarString = 0x0040, VarFloat = 0x0050;
    static VarSetChecked? _set;

    static VarSetChecked? Setter()
    {
        if (_set != null) return _set;
        var path = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "libvlccore.dll");
        if (!NativeLibrary.TryLoad(path, out var lib) && !NativeLibrary.TryLoad("libvlccore.dll", out lib)) return null;
        if (!NativeLibrary.TryGetExport(lib, "var_SetChecked", out var fn)) return null;
        return _set = Marshal.GetDelegateForFunctionPointer<VarSetChecked>(fn);
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
        mp.SetAudioOutput(audioOutput ?? "any"); // drops the kept output: the next one inherits the chain above
        return true;
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
