using System.Text.Json.Serialization;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Human-reported issue or an approximate section where the beat is clearly audible.</summary>
internal enum TrackReportMarkKind { Problem, StrongRhythm }

/// <summary>
/// Start/end refer to the 48 kHz playback timeline. User taps/sliders are approximate:
/// human timing uncertainty and one-to-three omitted beats at a tempo transition are
/// diagnostic hints, NEVER authoritative beat-grid anchors.
/// </summary>
internal sealed record TrackIssueMark(long StartFrame, long EndFrame, string Comment,
    DateTimeOffset CreatedAt, TrackReportMarkKind Kind = TrackReportMarkKind.Problem,
    int TimingToleranceMilliseconds = 250, int PossibleMissingBeats = 3, double? SuggestedBpm = null);

/// <summary>Persistent report for one imported MP3, identified by its SHA-256 library filename.</summary>
internal sealed record TrackIssueReport(
    string Id, string FileName, string Title, string? Artist, string Analysis,
    List<TrackIssueMark> Marks)
{
    /// <summary>Individual user-tapped bass attacks in the order they were heard.</summary>
    public List<ManualBeatTap> BeatTaps { get; init; } = [];
}

/// <summary>
/// A single physical press while hearing a bass/kick beat. Position already follows
/// the compensated presentation-time playhead. No automatic beat-grid mutation.
/// </summary>
internal sealed record ManualBeatTap(long Frame, DateTimeOffset PressedAtUtc,
    int TimingToleranceMilliseconds = 180, int VisualDelayMilliseconds = 0);

[JsonSerializable(typeof(List<TrackIssueReport>))]
internal sealed partial class TrackReportJson : JsonSerializerContext;
