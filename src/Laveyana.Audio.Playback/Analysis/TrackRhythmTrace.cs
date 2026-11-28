namespace Laveyana.Audio.Playback;

/// <summary>Opt-in, export-only data from the same decoder and rhythm analysis pass.
/// Not retained by playback, saved waveform caches or ordinary background scans.</summary>
internal sealed class TrackRhythmTrace
{
    public const int SchemaVersion = 8;
    public int HopFrames { get; set; }
    public string Decoder { get; set; } = "unknown";
    public IReadOnlyList<RhythmEnergy> Energy { get; set; } = [];
    public IReadOnlyList<RhythmSpectrumFrame> SpectralFlux { get; set; } = [];
    public TrackOnsetDiagnostic[] Onsets { get; set; } = [];
    public float[,]? TempoScores { get; set; }
    public float[,]? RawTempoScores { get; set; }
    public float[,]? PhaseScores { get; set; }
    public float[,]? ConfidenceScores { get; set; }
    public float[,]? PeriodicityScores { get; set; }
    public double MinimumBpm { get; set; }
    public double BpmResolution { get; set; }
    public List<RhythmSectionAttempt> Attempts { get; } = [];
    public List<RhythmPhaseAttempt> PhaseAttempts { get; } = [];
    public List<RhythmBridgeAttempt> BridgeAttempts { get; } = [];
    public RhythmMetricHierarchy.Root? MetricRoot { get; set; }
    public List<RhythmSectionPhaseDecision> SectionPhasePath { get; } = [];
}

internal readonly record struct RhythmSectionAttempt(int FirstWindow, int EndWindowExclusive,
    bool Recovery, double MeanConfidence, double Bpm, double AnchorBpm, double PhaseInHops,
    long StartFrame, long EndFrame, int BeatCount, int MatchedBeats, bool UsesStrongOnsets,
    string Decision);

internal readonly record struct RhythmPhaseAttempt(int FirstWindow, int EndWindowExclusive,
    double Bpm, double OriginalPhase, double SelectedPhase, double OriginalScore, double SelectedScore,
    bool Subdivision, bool Alternate);

internal readonly record struct RhythmBridgeAttempt(long StartFrame, long EndFrame, double Bpm,
    double PhaseErrorBeats, double Periodicity, int Predictions, int Supported, bool Accepted, string Decision);

internal readonly record struct RhythmSectionPhaseDecision(long StartFrame, long EndFrame,
    double Bpm, int Candidates, double OriginalScore, double SelectedScore, double ShiftBeats);

internal sealed record TrackDiagnosticSnapshot(TrackWaveform Waveform, TrackAudioInfo? Info,
    TrackRhythmTrace Trace);
