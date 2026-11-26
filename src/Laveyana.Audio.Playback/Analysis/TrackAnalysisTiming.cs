namespace Laveyana.Audio.Playback;

/// <summary>Wall times; decode includes streaming features and bounded producer waits.
/// Rhythm can overlap the tail of the tonal worker. Finalize includes joining that worker.</summary>
public sealed record TrackAnalysisTiming(double DecodeAndFeaturesMs, double RhythmMs, double FinalizeMs, double TotalMs);
