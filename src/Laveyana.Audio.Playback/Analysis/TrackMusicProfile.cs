using System.Text.Json.Serialization;

namespace Laveyana.Audio.Playback;

public sealed record TrackKeySection(long StartFrame, long EndFrame, TrackMusicalKey? Key);
public sealed record TrackEnergyPoint(long StartFrame, long EndFrame, float Energy, float BassShare, float AttacksPerSecond);
public enum MusicSectionKind { Silence, Intro, Outro, BuildUp, Breakdown, HighEnergy, Steady }
public sealed record TrackMusicSection(long StartFrame, long EndFrame, MusicSectionKind Kind, float Energy);
/// <summary>Number of observed pulses per repeating accent cycle; not a notated time signature.</summary>
public sealed record TrackMeterSection(long StartFrame, long EndFrame, int PulsesPerBar, float Confidence, long[] Downbeats);

/// <summary>Version each result independently. Missing audio-derived features remain unknown on older caches.
/// Derived results can be rebuilt from saved waveform/rhythm without opening the audio.</summary>
public sealed record TrackMusicProfile
{
    public const int CurrentMeterVersion = 1, CurrentEnergyVersion = 1, CurrentStructureVersion = 1,
        CurrentLocalKeyVersion = 1;
    public int MeterVersion { get; init; }
    public int EnergyVersion { get; init; }
    public int StructureVersion { get; init; }
    public int LocalKeyVersion { get; init; }
    public int LoudnessVersion { get; init; }
    public TrackMeterSection[] Meter { get; init; } = [];
    public TrackEnergyPoint[] Energy { get; init; } = [];
    public TrackMusicSection[] Structure { get; init; } = [];
    public TrackKeySection[] LocalKeys { get; init; } = [];
    public TrackLoudness? Loudness { get; init; }

    [JsonIgnore]
    public bool HasCurrentDerived => MeterVersion == CurrentMeterVersion &&
        EnergyVersion == CurrentEnergyVersion && StructureVersion == CurrentStructureVersion;

    public TrackMusicalKey? KeyAt(long frame) =>
        TryGetLocalKey(frame, out var key) ? key : null;

    public bool TryGetLocalKey(long frame, out TrackMusicalKey? key)
    {
        int lo = 0, hi = LocalKeys.Length;
        while (lo < hi)
        {
            int mid = lo + (hi-lo)/2;
            if (LocalKeys[mid].EndFrame <= frame) lo = mid + 1;
            else hi = mid;
        }
        if (lo < LocalKeys.Length && LocalKeys[lo].StartFrame <= frame)
        {
            key = LocalKeys[lo].Key;
            return true;
        }
        key = null;
        return false;
    }

    public bool IsValid(long total)
    {
        bool Range(long start, long end) => start >= 0 && end > start && end <= total;
        bool Unit(float v) => float.IsFinite(v) && v >= 0 && v <= 1;
        return MeterVersion is >= 0 and <= CurrentMeterVersion &&
            EnergyVersion is >= 0 and <= CurrentEnergyVersion &&
            StructureVersion is >= 0 and <= CurrentStructureVersion &&
            LocalKeyVersion is >= 0 and <= CurrentLocalKeyVersion &&
            LoudnessVersion is >= 0 and <= TrackLoudness.Version &&
            Meter is { Length: <= 4096 } && Energy is { Length: <= 8192 } &&
            Structure is { Length: <= 8192 } && LocalKeys is { Length: <= 8192 } &&
            Meter.All(m => m is not null && Range(m.StartFrame,m.EndFrame) && m.PulsesPerBar is 3 or 4 &&
                Unit(m.Confidence) && m.Downbeats is { Length: <= 32768 } &&
                StrictFrames(m.Downbeats, m.StartFrame, m.EndFrame)) &&
            Energy.All(e => e is not null && Range(e.StartFrame,e.EndFrame) && Unit(e.Energy) &&
                Unit(e.BassShare) && float.IsFinite(e.AttacksPerSecond) && e.AttacksPerSecond >= 0) &&
            Structure.All(s => s is not null && Range(s.StartFrame,s.EndFrame) && Unit(s.Energy) && Enum.IsDefined(s.Kind)) &&
            LocalKeys.All(k => k is not null && Range(k.StartFrame,k.EndFrame) && (k.Key is null || k.Key.IsValid)) &&
            Ordered(Meter.Select(m => (m.StartFrame,m.EndFrame))) &&
            Ordered(Energy.Select(e => (e.StartFrame,e.EndFrame))) &&
            Ordered(Structure.Select(s => (s.StartFrame,s.EndFrame))) &&
            Ordered(LocalKeys.Select(k => (k.StartFrame,k.EndFrame))) &&
            (Loudness is null || Loudness.IsValid(total));
    }

    private static bool StrictFrames(long[] frames, long start, long end)
    {
        long previous = start - 1;
        foreach (long frame in frames)
        {
            if (frame <= previous || frame >= end) return false;
            previous = frame;
        }
        return true;
    }

    public TrackEnergyPoint? EnergyAt(long frame)
    {
        int lo = 0, hi = Energy.Length;
        while (lo < hi)
        {
            int mid = lo + (hi-lo)/2;
            if (Energy[mid].EndFrame <= frame) lo = mid+1;
            else hi = mid;
        }
        return lo < Energy.Length && Energy[lo].StartFrame <= frame ? Energy[lo] : null;
    }

    private static bool Ordered(IEnumerable<(long Start,long End)> spans)
    {
        long previous = 0;
        foreach (var span in spans)
        {
            if (span.Start < previous) return false;
            previous = span.End;
        }
        return true;
    }
}
