using System.Globalization;
using LibVLCSharp.Shared;

namespace Pylon;

/// <summary>
/// Audio/video effect definitions for the effects panel (mockup: Audio - Equalizer / Compressor / Spatializer,
/// Video - Essential / Crop / Colors). Values live in Settings ("fx.*", "fxv.*"); filter modules are applied through libVLC arguments.
/// </summary>
public static class Effects
{
    public const int Bands = 10;

    static string[]? _presets;
    public static string[] PresetNames
    {
        get
        {
            if (_presets != null) return _presets;
            using var eq = new Equalizer();
            return _presets = [.. Enumerable.Range(0, (int)eq.PresetCount).Select(i => eq.PresetName((uint)i) ?? ""), "Custom"];
        }
    }

    public static string BandLabel(int band)
    {
        using var eq = new Equalizer();
        var f = eq.BandFrequency((uint)band);
        return f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";
    }

    /// <summary>Copies a libVLC preset into the band settings.</summary>
    public static void ApplyPreset(string name)
    {
        var index = Array.IndexOf(PresetNames, name);
        if (index < 0 || name == "Custom") return;
        using var eq = new Equalizer((uint)index);
        _applying = true;
        Settings.Set("fx.eq.preamp", Math.Round(eq.Preamp, 1));
        for (var b = 0; b < Bands; b++) Settings.Set($"fx.eq.b{b}", Math.Round(eq.Amp((uint)b), 1));
        _applying = false;
    }

    static bool _applying;

    /// <summary>Touching a band by hand switches the preset to "Custom".</summary>
    public static void BandEdited()
    {
        if (!_applying) Settings.Set("audio.eqpreset", "Custom");
    }

    /// <summary>The equalizer to hand to the player, or null when it's off.</summary>
    public static Equalizer? Build()
    {
        if (!Settings.Bool("audio.eq")) return null;
        var eq = new Equalizer();
        eq.SetPreamp((float)Settings.Num("fx.eq.preamp"));
        for (var b = 0; b < Bands; b++) eq.SetAmp((float)Settings.Num($"fx.eq.b{b}"), (uint)b);
        return eq;
    }

    static Opt S(string key, string label, double def, double min, double max, double step, string unit = "", bool engine = true) => new()
    {
        Kind = OptKind.Slider, Key = key, Label = label, Default = def.ToString(CultureInfo.InvariantCulture), Min = min, Max = max, Step = step, Unit = unit, Engine = engine,
    };

    public static readonly List<Section> Audio =
    [
        new("Compressor",
        [
            S("fx.comp.rmspeak", "RMS / peak", 0.2, 0, 1, 0.05),
            S("fx.comp.attack", "Attack", 25, 1.5, 400, 0.5, " ms"),
            S("fx.comp.release", "Release", 100, 2, 800, 1, " ms"),
            S("fx.comp.threshold", "Threshold", -11, -30, 0, 0.5, " dB"),
            S("fx.comp.ratio", "Ratio", 4, 1, 20, 0.5, ":1"),
            S("fx.comp.knee", "Knee radius", 5, 1, 10, 0.5, " dB"),
            S("fx.comp.makeup", "Makeup gain", 7, 0, 24, 0.5, " dB"),
        ]),
        new("Spatializer",
        [
            S("fx.spat.size", "Room size", 0.85, 0, 1.1, 0.05),
            S("fx.spat.width", "Width", 1, 0, 1, 0.05),
            S("fx.spat.wet", "Wet", 0.4, 0, 1, 0.05),
            S("fx.spat.dry", "Dry", 0.5, 0, 1, 0.05),
            S("fx.spat.damp", "Damping", 0.5, 0, 1, 0.05),
        ]),
    ];

    public static readonly List<Section> Video =
    [
        new("Essential",
        [
            new() { Kind = OptKind.Choice, Key = "fxv.rotate", Label = "Rotate / flip", Default = "None", Options = ["None", "90°", "180°", "270°", "Flip horizontally", "Flip vertically"], Engine = true },
        ]),
        new("Crop",
        [
            S("fxv.crop.t", "Top", 0, 0, 500, 2, " px", engine: false),
            S("fxv.crop.b", "Bottom", 0, 0, 500, 2, " px", engine: false),
            S("fxv.crop.l", "Left", 0, 0, 500, 2, " px", engine: false),
            S("fxv.crop.r", "Right", 0, 0, 500, 2, " px", engine: false),
        ]),
    ];

    /// <summary>Band / preamp settings (not shown as rows; the panel draws its own vertical sliders).</summary>
    public static readonly List<Opt> EqOpts =
    [
        S("fx.eq.preamp", "Preamp", 12, -20, 20, 0.5, " dB", engine: false),
        .. Enumerable.Range(0, Bands).Select(b => S($"fx.eq.b{b}", $"Band {b}", 0, -20, 20, 0.5, " dB", engine: false)),
    ];

    public static IEnumerable<Opt> AllOpts => Audio.Concat(Video).SelectMany(s => s.Opts).Concat(EqOpts);

    public static string? TransformArg() => Settings.Str("fxv.rotate") switch
    {
        "90°" => "90", "180°" => "180", "270°" => "270", "Flip horizontally" => "hflip", "Flip vertically" => "vflip", _ => null,
    };
}
