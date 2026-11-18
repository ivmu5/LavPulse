namespace Laveyana.Audio.Playback;

/// <summary>Owns a seekable PCM file and the waveform generated from it.</summary>
public sealed class CachedTrack
{
    /// <summary>Creates the cache entry after the complete PCM data has been written.</summary>
    internal CachedTrack(string name, string path, TrackWaveform waveform, TrackMetadata? tags = null, TrackAudioInfo? audioInfo = null)
    {
        Name = name;
        Path = path;
        Waveform = waveform;
        Title = tags?.Title ?? System.IO.Path.GetFileNameWithoutExtension(name);
        Artist = tags?.Artist;
        Artwork = tags?.Artwork ?? ReadOnlyMemory<byte>.Empty;
        AudioInfo = audioInfo;
    }

    public string Name { get; }
    public string Title { get; }
    public string? Artist { get; }
    public ReadOnlyMemory<byte> Artwork { get; }
    public TrackAudioInfo? AudioInfo { get; }
    internal string Path { get; }
    public TrackWaveform Waveform { get; }
    public long PcmSizeBytes => Waveform.TotalFrames * PlaybackFormat.Pcm16FrameBytes;

    /// <summary>Removes this cache entry after active playback has stopped.</summary>
    public void Delete() => DeleteCacheFile(Path);

    /// <summary>Attempts to remove a temporary file that the OS may already have cleared.</summary>
    internal static void DeleteCacheFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The OS can manage app cache files independently.
        }
        catch (UnauthorizedAccessException)
        {
            // The OS can manage app cache files independently.
        }
    }

}
