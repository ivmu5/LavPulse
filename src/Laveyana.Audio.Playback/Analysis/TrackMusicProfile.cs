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
    public const int CurrentMeterVersion = 1, CurrentEnergyVersion = 2, CurrentStructureVersion = 1,
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
        key = null;
        if (frame < 0 || LocalKeys is not { } keys) return false;
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            int mid = lo + (hi-lo)/2;
            if (keys[mid] is not { } candidate) return false;
            if (candidate.EndFrame <= frame) lo = mid + 1;
            else hi = mid;
        }
        if (lo < keys.Length && keys[lo] is { } section &&
            section.StartFrame >= 0 && section.StartFrame <= frame && frame < section.EndFrame)
        {
            key = section.Key is { IsValid: true } valid ? valid : null;
            return true;
        }
        key = null;
        return false;
    }

    public bool IsValid(long total)
    {
        if (total < 0 ||
            MeterVersion is < 0 or > CurrentMeterVersion ||
            EnergyVersion is < 0 or > CurrentEnergyVersion ||
            StructureVersion is < 0 or > CurrentStructureVersion ||
            LocalKeyVersion is < 0 or > CurrentLocalKeyVersion ||
            LoudnessVersion is < 0 or > TrackLoudness.Version ||
            Meter is not { Length: <= 4096 } || Energy is not { Length: <= 8192 } ||
            Structure is not { Length: <= 8192 } || LocalKeys is not { Length: <= 8192 })
            return false;

        // Validate the interval and its fields together; no projected iterator
        // or second traversal of every cached timeline is needed.
        long previous = 0;
        foreach (var m in Meter)
            if (m is null || !AdvanceRange(ref previous,m.StartFrame,m.EndFrame,total) ||
                m.PulsesPerBar is not (3 or 4) || !Unit(m.Confidence) ||
                m.Downbeats is not { Length: <= 32768 } || !StrictFrames(m.Downbeats,m.StartFrame,m.EndFrame))
                return false;
        previous = 0;
        foreach (var e in Energy)
            if (e is null || !AdvanceRange(ref previous,e.StartFrame,e.EndFrame,total) || !ValidEnergy(e))
                return false;
        previous = 0;
        foreach (var s in Structure)
            if (s is null || !AdvanceRange(ref previous,s.StartFrame,s.EndFrame,total) ||
                !Unit(s.Energy) || !Enum.IsDefined(s.Kind))
                return false;
        previous = 0;
        foreach (var k in LocalKeys)
            if (k is null || !AdvanceRange(ref previous,k.StartFrame,k.EndFrame,total) ||
                k.Key is { IsValid: false })
                return false;
        return Loudness is null || Loudness.IsValid(total);
    }

    private static bool AdvanceRange(ref long previous,long start,long end,long total)
    {
        if (start < previous || end <= start || end > total) return false;
        previous = end;
        return true;
    }

    private static bool Unit(float value) => float.IsFinite(value) && value is >= 0 and <= 1;
    private static bool ValidEnergy(TrackEnergyPoint point) =>
        Unit(point.Energy) && Unit(point.BassShare) &&
        float.IsFinite(point.AttacksPerSecond) && point.AttacksPerSecond >= 0;

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
        if (frame < 0 || Energy is not { } energy) return null;
        int lo = 0, hi = energy.Length;
        while (lo < hi)
        {
            int mid = lo + (hi-lo)/2;
            if (energy[mid] is not { } candidate) return null;
            if (candidate.EndFrame <= frame) lo = mid+1;
            else hi = mid;
        }
        return lo < energy.Length && energy[lo] is { } point &&
            point.StartFrame >= 0 && point.StartFrame <= frame && frame < point.EndFrame &&
            ValidEnergy(point) ? point : null;
    }
}
