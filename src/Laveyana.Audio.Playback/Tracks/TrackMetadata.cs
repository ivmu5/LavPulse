namespace Laveyana.Audio.Playback;

public sealed record TrackMetadata(string Title, string? Artist, ReadOnlyMemory<byte> Artwork)
{
    public string? Album { get; init; }
}
