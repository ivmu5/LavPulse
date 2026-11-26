using System.Text.Json.Serialization;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

internal sealed record StoredTrack(string Id, string Name);
internal sealed record StoredFolder(string Id, string Name);
internal sealed record StoredLibraryTrack(StoredTrack Track, StoredFolder[] Folders);
internal sealed record StoredQueue(StoredTrack[] Tracks, string? Current, StoredLibraryTrack[]? Library = null);
internal sealed record StoredPcmReference(string? PcmFile);
internal sealed record StoredAnalysis(long TotalFrames, int FramesPerPoint, WaveformPoint[] Points,
    TrackAudioInfo? AudioInfo, string? PcmFile)
{
    public TrackBeatGrid? Rhythm { get; init; }
    public TrackMusicalKey? MusicalKey { get; init; }
    public TrackMusicProfile? MusicProfile { get; init; }

    [JsonIgnore]
    public TrackWaveform Waveform => new(Array.AsReadOnly(Points), TotalFrames, FramesPerPoint)
    { Rhythm = Rhythm, MusicalKey = MusicalKey, MusicProfile = MusicProfile };
}

[JsonSerializable(typeof(StoredQueue))]
[JsonSerializable(typeof(StoredAnalysis))]
[JsonSerializable(typeof(StoredPcmReference))]
internal sealed partial class TrackStoreJson : JsonSerializerContext;
