using System.Buffers.Binary;

namespace Laveyana.Audio.Playback;

/// <summary>Reads the technical properties of an MP3 bitstream from its MPEG Layer III frames.</summary>
internal static class Mp3AudioInfoReader
{
    private const int HeaderSize = 4;
    private const int MaxFreeFormatFrameSize = 4096;
    private static ReadOnlySpan<int> SampleRates => [44100, 48000, 32000];
    private static ReadOnlySpan<int> Mpeg1Bitrates => [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static ReadOnlySpan<int> Mpeg2Bitrates => [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    /// <summary>Uses the mean encoded bitrate for VBR and ignores leading tag and seek-table data.</summary>
    public static TrackAudioInfo? Read(Stream source, CancellationToken token)
    {
        long length = source.Length;
        long position = SkipId3Tags(source, 0, length, token);
        if (!FindFirstFrame(source, length, token, ref position, out Frame first, out int freeFormatSize))
        {
            return null;
        }

        var seekHeader = ReadSeekHeader(source, position, first, freeFormatSize);
        if (seekHeader != SeekHeader.None) position += first.GetLength(freeFormatSize);

        int initialBitrate = 0;
        TrackChannelMode channelMode = first.ChannelMode;
        bool variableBitrate = seekHeader == SeekHeader.Variable;
        long frameCount = 0;
        long bitrateSum = 0;
        while (position <= length - HeaderSize)
        {
            token.ThrowIfCancellationRequested();
            if (!TryReadFrame(source, position, out Frame frame) || !first.IsCompatibleWith(frame))
            {
                long next = SkipId3Tags(source, position, length, token);
                if (next == position) break;
                position = next;
                continue;
            }

            int size = frame.GetLength(freeFormatSize);
            if (size > length - position) break;

            int bitrate = frame.Bitrate == 0
                ? (int)Math.Round(freeFormatSize * 8d * frame.SampleRate / frame.SampleCount / 1000)
                : frame.Bitrate;
            if (frameCount == 0)
            {
                initialBitrate = bitrate;
                channelMode = frame.ChannelMode;
            }
            variableBitrate |= bitrate != initialBitrate;
            if (channelMode != frame.ChannelMode) channelMode = TrackChannelMode.Mixed;

            frameCount++;
            bitrateSum += bitrate;
            position += size;
        }

        // Compatible frames have identical sample rates and durations.
        return frameCount == 0 ? null : new TrackAudioInfo("MP3", first.SampleRate,
            (int)Math.Round((double)bitrateSum / frameCount, MidpointRounding.AwayFromZero), variableBitrate, channelMode);
    }

    /// <summary>Confirms sync against the next frame so stray bytes cannot masquerade as audio.</summary>
    private static bool FindFirstFrame(Stream source, long length, CancellationToken token,
        ref long position, out Frame first, out int freeFormatSize)
    {
        for (; position <= length - HeaderSize; position++)
        {
            if ((position & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!TryReadFrame(source, position, out first)) continue;

            freeFormatSize = first.Bitrate == 0 ? FindFreeFormatSize(source, position, length, first) : 0;
            if (first.Bitrate == 0 && freeFormatSize == 0) continue;

            long next = position + first.GetLength(freeFormatSize);
            if (next <= length - HeaderSize && TryReadFrame(source, next, out Frame candidate) &&
                first.IsCompatibleWith(candidate) && candidate.GetLength(freeFormatSize) <= length - next)
            {
                return true;
            }
        }
        first = default;
        freeFormatSize = 0;
        return false;
    }

    /// <summary>Recovers the unpadded length of free-format frames from their sync spacing.</summary>
    private static int FindFreeFormatSize(Stream source, long position, long length, Frame first)
    {
        long limit = Math.Min(position + MaxFreeFormatFrameSize, length - HeaderSize);
        for (long next = position + HeaderSize; next <= limit; next++)
        {
            if (!TryReadFrame(source, next, out Frame frame) || !first.IsCompatibleWith(frame)) continue;

            int size = (int)(next - position) - first.Padding;
            long third = next + frame.GetLength(size);
            if (third == length || (third <= length - HeaderSize &&
                TryReadFrame(source, third, out Frame following) && first.IsCompatibleWith(following)))
            {
                return size;
            }
        }
        return 0;
    }

    /// <summary>Skips ID3v2 payloads, including an optional v2.4 footer, by their declared byte size.</summary>
    private static long SkipId3Tags(Stream source, long position, long length, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[10];
        while (position <= length - header.Length)
        {
            token.ThrowIfCancellationRequested();
            source.Position = position;
            if (source.ReadAtLeast(header, header.Length, false) != header.Length ||
                !header[..3].SequenceEqual("ID3"u8) || header[3] is < 2 or > 4 ||
                ((header[6] | header[7] | header[8] | header[9]) & 0x80) != 0) break;

            int tagSize = header[6] << 21 | header[7] << 14 | header[8] << 7 | header[9];
            int footer = header[3] == 4 && (header[5] & 0x10) != 0 ? header.Length : 0;
            long next = position + header.Length + tagSize + footer;
            if (next > length) break;
            position = next;
        }
        return position;
    }

    /// <summary>Identifies the optional first frame carrying encoder navigation data.</summary>
    private static SeekHeader ReadSeekHeader(Stream source, long position, Frame frame, int freeFormatSize)
    {
        Span<byte> marker = stackalloc byte[4];
        int sideInfoSize = frame.SampleCount == 1152
            ? (frame.ChannelMode == TrackChannelMode.Mono ? 17 : 32)
            : (frame.ChannelMode == TrackChannelMode.Mono ? 9 : 17);
        int offset = HeaderSize + sideInfoSize;
        int size = frame.GetLength(freeFormatSize);
        SeekHeader xing = ReadXingMarker(source, marker, position, size, offset);
        if (xing == SeekHeader.None && frame.HasCrc)
        {
            xing = ReadXingMarker(source, marker, position, size, offset + 2);
        }
        if (xing != SeekHeader.None) return xing;
        return TryReadMarker(source, marker, position, size, 36) && marker.SequenceEqual("VBRI"u8)
            ? SeekHeader.Variable : SeekHeader.None;
    }

    private static SeekHeader ReadXingMarker(Stream source, Span<byte> marker, long position, int size, int offset)
    {
        if (!TryReadMarker(source, marker, position, size, offset)) return SeekHeader.None;
        if (marker.SequenceEqual("Xing"u8)) return SeekHeader.Variable;
        if (marker.SequenceEqual("Info"u8)) return SeekHeader.Constant;
        return SeekHeader.None;
    }

    private static bool TryReadMarker(Stream source, Span<byte> marker, long position, int size, int offset)
    {
        if (offset + marker.Length > size) return false;
        source.Position = position + offset;
        return source.ReadAtLeast(marker, marker.Length, false) == marker.Length;
    }

    /// <summary>Validates the MPEG sync word, Layer III marker and bitrate and sample rate indices.</summary>
    private static bool TryReadFrame(Stream source, long position, out Frame frame)
    {
        frame = default;
        Span<byte> bytes = stackalloc byte[HeaderSize];
        source.Position = position;
        if (source.ReadAtLeast(bytes, bytes.Length, false) != bytes.Length) return false;

        uint header = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if ((header & 0xFFE60000) != 0xFFE20000) return false;
        int version = (int)(header >> 19) & 3;
        int bitrateIndex = (int)(header >> 12) & 15;
        int sampleRateIndex = (int)(header >> 10) & 3;
        if (version == 1 || bitrateIndex == 15 || sampleRateIndex == 3) return false;

        int rateDivisor = version switch { 3 => 1, 2 => 2, _ => 4 };
        int bitrate = (version == 3 ? Mpeg1Bitrates : Mpeg2Bitrates)[bitrateIndex];
        frame = new Frame(SampleRates[sampleRateIndex] / rateDivisor, version == 3 ? 1152 : 576, bitrate,
            (int)(header >> 9) & 1, (TrackChannelMode)((header >> 6) & 3), (header & 0x10000) == 0);
        return true;
    }

    private enum SeekHeader { None, Constant, Variable }

    private readonly record struct Frame(
        int SampleRate, int SampleCount, int Bitrate, int Padding, TrackChannelMode ChannelMode, bool HasCrc)
    {
        public bool IsCompatibleWith(Frame frame) => SampleRate == frame.SampleRate && SampleCount == frame.SampleCount &&
            (Bitrate == 0) == (frame.Bitrate == 0);

        public int GetLength(int freeFormatSize) =>
            (Bitrate == 0 ? freeFormatSize : SampleCount / 8 * Bitrate * 1000 / SampleRate) + Padding;
    }
}
