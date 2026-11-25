using System.Buffers.Binary;

namespace Laveyana.Audio.Playback;

/// <summary>Bounded RIFF/WAVE reader for mono/stereo integer PCM and IEEE float.
/// Unknown chunks are skipped; the input stream remains owned by the importer.</summary>
internal sealed class WaveSampleDecoder : IMp3SampleDecoder
{
    private readonly Stream _source;
    private readonly int _bytesPerSample;
    private readonly bool _floating;
    private readonly byte[] _buffer = new byte[4096 * 2 * sizeof(double)];
    private long _remaining;

    public int Channels { get; }
    public int SampleRate { get; }
    public int BitsPerSample => _bytesPerSample * 8;
    public string Codec => _floating ? "WAV IEEE float" : "WAV PCM";

    public WaveSampleDecoder(Stream source)
    {
        _source = source;
        source.Position = 0;
        Span<byte> header = stackalloc byte[12];
        source.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Ожидался RIFF/WAVE.");
        long end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        if (end < 12 || end > source.Length)
            throw new InvalidDataException("Повреждён размер WAV.");
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> format = stackalloc byte[40];
        int formatTag = 0, channels = 0, rate = 0, bits = 0, align = 0;
        long dataOffset = -1, dataSize = 0;
        while (source.Position <= end - 8)
        {
            source.ReadExactly(chunk);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long start = source.Position;
            if (size > end - start)
                throw new InvalidDataException("Повреждён WAV-чанк.");
            if (chunk[..4].SequenceEqual("fmt "u8) && formatTag == 0)
            {
                if (size < 16) throw new InvalidDataException("Неполный WAV fmt.");
                format.Clear();
                source.ReadExactly(format[..(int)Math.Min(size, format.Length)]);
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
                align = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
                if (formatTag == 0xfffe)
                {
                    if (size < 40 || BinaryPrimitives.ReadUInt16LittleEndian(format[16..]) < 22)
                        throw new InvalidDataException("Неполный WAVE_FORMAT_EXTENSIBLE.");
                    Guid sub = new(format[24..40]);
                    formatTag = sub == new Guid("00000001-0000-0010-8000-00aa00389b71") ? 1 :
                        sub == new Guid("00000003-0000-0010-8000-00aa00389b71") ? 3 : 0;
                    if (formatTag == 0) throw new InvalidDataException("Неподдерживаемый WAV subtype.");
                }
            }
            else if (chunk[..4].SequenceEqual("data"u8) && dataOffset < 0)
            {
                dataOffset = start;
                dataSize = size;
            }
            if (formatTag != 0 && dataOffset >= 0) break;
            long next = start + size + (size & 1);
            if (next > end) throw new InvalidDataException("Некорректное выравнивание WAV.");
            source.Position = next;
        }
        bool floating = formatTag == 3;
        if (channels is not (1 or 2) || rate <= 0 || rate > 192_000 ||
            !(formatTag == 1 && bits is 8 or 16 or 24 or 32 ||
                floating && bits is 32 or 64) ||
            align != channels * (bits / 8) || dataOffset < 0 || dataSize <= 0 ||
            dataSize % align != 0)
            throw new InvalidDataException("Неподдерживаемый или неполный WAV PCM/float.");
        Channels = channels;
        SampleRate = rate;
        _bytesPerSample = bits / 8;
        _floating = floating;
        _remaining = dataSize;
        source.Position = dataOffset;
    }

    public int ReadSamples(float[] destination)
    {
        int count = (int)Math.Min(Math.Min(destination.Length, _buffer.Length / _bytesPerSample),
            _remaining / _bytesPerSample);
        count -= count % Channels;
        if (count == 0) return 0;
        int bytes = count * _bytesPerSample;
        var input = _buffer.AsSpan(0, bytes);
        _source.ReadExactly(input);
        _remaining -= bytes;
        for (int i = 0; i < count; i++)
        {
            var sample = input.Slice(i * _bytesPerSample, _bytesPerSample);
            double value = _floating
                ? _bytesPerSample == 4
                    ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(sample))
                    : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(sample))
                : _bytesPerSample switch
                {
                    1 => (sample[0] - 128) / 128d,
                    2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768d,
                    3 => ((sample[0] << 8) | (sample[1] << 16) | (sample[2] << 24)) / 2147483648d,
                    4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648d,
                    _ => throw new InvalidDataException("Неверный формат PCM.")
                };
            destination[i] = double.IsFinite(value) ? (float)Math.Clamp(value, -1, 1) : 0;
        }
        return count;
    }

    public void Dispose() { }
}
