using System.Text.Json.Serialization;

namespace Laveyana.Audio.Playback;

public enum MusicalMode { Major, Minor }

/// <summary>Estimated global key, not a chord sequence or verified musical annotation.</summary>
public sealed record TrackMusicalKey(int Tonic, MusicalMode Mode, float Confidence, float Correlation,
    float Margin, int TonalWindows, int TotalWindows)
{
    private static readonly string[] Names = ["C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B"];
    private static readonly int[] MajorNumbers = [8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1];
    private static readonly int[] MinorNumbers = [5, 12, 7, 2, 9, 4, 11, 6, 1, 8, 3, 10];

    [JsonIgnore]
    public bool IsValid => Tonic is >= 0 and < 12 && Mode is MusicalMode.Major or MusicalMode.Minor &&
        float.IsFinite(Confidence) && Confidence is >= 0 and <= 1 &&
        float.IsFinite(Correlation) && Correlation is >= -1 and <= 1 &&
        float.IsFinite(Margin) && Margin is >= 0 and <= 2 &&
        TonalWindows >= 3 && TotalWindows >= TonalWindows;

    [JsonIgnore]
    public string Name => IsValid ? Names[Tonic] + (Mode == MusicalMode.Minor ? "m" : "") : "—";
    internal int CamelotNumber => IsValid ? (Mode == MusicalMode.Minor ? MinorNumbers[Tonic] : MajorNumbers[Tonic]) : 0;

    [JsonIgnore]
    public string Camelot => IsValid ? CamelotNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        (Mode == MusicalMode.Minor ? "A" : "B") : "—";
}
