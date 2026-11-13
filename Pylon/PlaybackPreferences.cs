using System.IO;
using System.Text.RegularExpressions;

namespace Pylon;

public record TrackPreference(string Name, string Language, bool Disabled = false);
public record TrackCandidate(int Id, string Name, string Language);
public record PlaybackPreference(TrackPreference? Audio, TrackPreference? Subtitles, double Speed = 1, double AudioDelay = 0, double SubtitleDelay = 0);

public static class PlaybackPreferences
{
    static readonly Dictionary<string, string> Languages = System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.NeutralCultures)
        .SelectMany(c => new[] { (Key: c.TwoLetterISOLanguageName, Value: c.TwoLetterISOLanguageName), (Key: c.ThreeLetterISOLanguageName, Value: c.TwoLetterISOLanguageName) })
        .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
    static string Language(string text) => Languages.GetValueOrDefault(text) ?? text.ToLowerInvariant();
    static string Name(string text) => Regex.Replace(text.ToLowerInvariant(), @"\b(?:track|дорожка)\s*\d+\b|[^\p{L}\p{N}]+", " ").Trim();
    static bool Forced(string text) => Regex.IsMatch(text, "forced|signs|songs|надписи|форс", RegexOptions.IgnoreCase);

    public static int? Match(TrackPreference? choice, IEnumerable<TrackCandidate> source)
    {
        if (choice == null) return null;
        if (choice.Disabled) return -1;
        var all = source.Where(t => t.Id >= 0).ToArray();
        var name = Name(choice.Name);
        var exact = all.Where(t => name.Length > 0 && Name(t.Name) == name
            && (choice.Language.Length == 0 || Language(t.Language) == Language(choice.Language))).ToArray();
        if (exact.Length == 1) return exact[0].Id;
        var byLanguage = all.Where(t => choice.Language.Length > 0 && Language(t.Language) == Language(choice.Language)
            && Forced(t.Name) == Forced(choice.Name)).ToArray();
        return byLanguage.Length == 1 ? byLanguage[0].Id : null;
    }

    public static string? Folder(string path) => path.Contains("://") ? null : Path.GetDirectoryName(path);
    static bool ValidTrack(TrackPreference? t) => t == null || t.Name != null && t.Language != null && t.Name.Length <= 4096 && t.Language.Length <= 128;
    public static bool Valid(PlaybackPreference p) => ValidTrack(p.Audio) && ValidTrack(p.Subtitles) && double.IsFinite(p.Speed) && p.Speed is >= 0.25 and <= 4
        && double.IsFinite(p.AudioDelay) && Math.Abs(p.AudioDelay) <= 10
        && double.IsFinite(p.SubtitleDelay) && Math.Abs(p.SubtitleDelay) <= 600;
}
