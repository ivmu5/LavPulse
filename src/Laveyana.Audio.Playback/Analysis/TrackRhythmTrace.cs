namespace Laveyana.Audio.Playback;

/// <summary>Opt-in, export-only data from the same decoder and rhythm analysis pass.
/// Not retained by playback, saved waveform caches or ordinary background scans.</summary>
internal sealed class TrackRhythmTrace
{
    public const int SchemaVersion = 1;
    public int HopFrames { get; set; }
    public string Decoder { get; set; } = "unknown";
    public IReadOnlyList<RhythmEnergy> Energy { get; set; } = [];
    public TrackOnsetDiagnostic[] Onsets { get; set; } = [];
    public float[,]? TempoScores { get; set; }
    public double MinimumBpm { get; set; }
    public double BpmResolution { get; set; }
    public List<RhythmSectionAttempt> Attempts { get; } = [];
}

internal readonly record struct RhythmSectionAttempt(int FirstWindow, int EndWindowExclusive,
    bool Recovery, double MeanConfidence, double Bpm, double AnchorBpm, double PhaseInHops,
    long StartFrame, long EndFrame, int BeatCount, int MatchedBeats, bool UsesStrongOnsets,
    string Decision);

internal sealed record TrackDiagnosticSnapshot(TrackWaveform Waveform, TrackAudioInfo? Info,
    TrackRhythmTrace Trace);
