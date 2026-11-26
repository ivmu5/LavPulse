using NVorbis;

namespace Laveyana.Audio.Playback;

/// <summary>Managed Ogg Vorbis decoder. Read requests are clamped to the
/// known logical stream length to avoid NVorbis 0.10.x end-of-stream overreads.</summary>
internal sealed class VorbisSampleDecoder : IMp3SampleDecoder
{
    private readonly VorbisReader _reader;
    private long _remaining;

    public VorbisSampleDecoder(Stream source)
    {
        var reader = new VorbisReader(source, closeOnDispose: false);
        try
        {
            if (reader.Channels is not (1 or 2) || reader.SampleRate <= 0 ||
                reader.SampleRate > 192_000 || reader.TotalSamples <= 0)
                throw new InvalidDataException("Неподдерживаемый поток Ogg Vorbis.");
            _remaining = checked(reader.TotalSamples * reader.Channels);
            _reader = reader;
        }
        catch { reader.Dispose(); throw; }
    }

    public int Channels => _reader.Channels;
    public int SampleRate => _reader.SampleRate;
    public string Codec => "Ogg Vorbis";
    public int ReadSamples(float[] destination)
    {
        int requested = (int)Math.Min(_remaining, destination.Length);
        requested -= requested % Channels;
        if (requested == 0) return 0;
        int read = _reader.ReadSamples(destination, 0, requested);
        if (read < 0 || read > requested || read % Channels != 0 ||
            read == 0 && _remaining > 0)
            throw new InvalidDataException("Ogg Vorbis завершился до объявленной длины.");
        _remaining -= read;
        return read;
    }

    public void Dispose() => _reader.Dispose();

    public static TrackMetadata ReadTags(Stream source, string fallback)
    {
        using var reader = new VorbisReader(source, closeOnDispose: false);
        var tags = reader.Tags;
        return new TrackMetadata(string.IsNullOrWhiteSpace(tags.Title) ? fallback : tags.Title,
            string.IsNullOrWhiteSpace(tags.Artist) ? null : tags.Artist, ReadOnlyMemory<byte>.Empty)
        { Album = string.IsNullOrWhiteSpace(tags.Album) ? null : tags.Album };
    }
}
