using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Laveyana.Audio.Playback;
using NAudio.Wave;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Media Foundation is used only while preparing a nonportable format,
/// never on the render thread. MP3 and WAV retain their established decoders.</summary>
internal sealed class WindowsAudioDecoder : IMp3SampleDecoder
{
    private readonly MediaFoundationReader _reader;
    private readonly bool _floating;
    private byte[] _bytes = [];
    public int Channels { get; }
    public int SampleRate { get; }
    public string Codec { get; }

    private WindowsAudioDecoder(MediaFoundationReader reader, string extension)
    {
        _reader = reader;
        var format = reader.WaveFormat;
        Channels = format.Channels;
        SampleRate = format.SampleRate;
        _floating = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        if (Channels is not (1 or 2) || SampleRate <= 0 || SampleRate > 192_000 ||
            !(_floating || format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16))
            throw new InvalidDataException("Media Foundation вернула неподдерживаемый PCM.");
        Codec = extension == ".flac" ? "FLAC" : $"Media Foundation ({extension})";
    }

    public static IMp3SampleDecoder? Open(Stream source, CancellationToken token)
    {
        if (source is not FileStream file) return null;
        string extension = Path.GetExtension(file.Name).ToLowerInvariant();
        if (extension is not (".flac" or ".aac" or ".m4a")) return null;
        token.ThrowIfCancellationRequested();
        MediaFoundationReader? reader = null;
        try
        {
            reader = new StreamMediaFoundationReader(source,
                new MediaFoundationReader.MediaFoundationReaderSettings
                { RequestFloatOutput = true, SingleReaderObject = true });
            var decoder = new WindowsAudioDecoder(reader, extension);
            reader = null;
            return decoder;
        }
        catch (Exception ex) when (ex is COMException or IOException or NotSupportedException
            or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("Media Foundation не открыла аудиофайл.", ex);
        }
        finally { reader?.Dispose(); }
    }

    public int ReadSamples(float[] destination)
    {
        int size = _floating ? 4 : 2;
        int count = destination.Length - destination.Length % Channels;
        int bytes = count * size;
        if (_bytes.Length < bytes) _bytes = new byte[bytes];
        int read = _reader.Read(_bytes, 0, bytes);
        if (read < 0 || read > bytes || read % (size * Channels) != 0)
            throw new InvalidDataException("Media Foundation вернула неполный кадр.");
        var input = _bytes.AsSpan(0, read);
        for (int i = 0; i < read / size; i++)
        {
            float value = _floating
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(input[(i * size)..]))
                : BinaryPrimitives.ReadInt16LittleEndian(input[(i * size)..]) / 32768f;
            destination[i] = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0;
        }
        return read / size;
    }

    public void Dispose() => _reader.Dispose();
}
