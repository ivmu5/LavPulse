using System.Text.Json.Serialization;
using Laveyana.Audio.Playback;

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
    int TimingToleranceMilliseconds = 180, int VisualDelayMilliseconds = 0)
{
    // These values describe the grid visible at press time, not a later rescan.
    // Older reports omit them and deserialize with null values.
    public string? AppVersion { get; init; }
    public long? NearestBeatFrame { get; init; }
    public long? NearestTransientFrame { get; init; }
    public double? LocalBpm { get; init; }

    internal static ManualBeatTap Capture(long frame, DateTimeOffset pressedAt, int delay,
        TrackWaveform waveform, string? version)
    {
        frame = Math.Clamp(frame, 0, waveform.TotalFrames);
        var grid = waveform.Rhythm;
        return new(frame, pressedAt, 180, delay)
        {
            AppVersion = version,
            NearestBeatFrame = Nearest(grid?.BeatFrames, frame),
            NearestTransientFrame = Nearest(grid?.TransientFrames, frame),
            LocalBpm = grid?.BpmAt(frame)
        };
    }

    private static long? Nearest(long[]? frames, long frame)
    {
        if (frames is not { Length: > 0 }) return null;
        int index = Array.BinarySearch(frames, frame);
        if (index >= 0) return frames[index];
        index = ~index;
        if (index == 0) return frames[0];
        if (index == frames.Length) return frames[^1];
        return frame - frames[index - 1] <= frames[index] - frame
            ? frames[index - 1] : frames[index];
    }
}

[JsonSerializable(typeof(List<TrackIssueReport>))]
internal sealed partial class TrackReportJson : JsonSerializerContext;
