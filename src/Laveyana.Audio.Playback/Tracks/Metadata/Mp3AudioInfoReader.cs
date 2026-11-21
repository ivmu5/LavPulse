using System.Buffers;
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
        using var headers = new HeaderReader(source, length);
        long position = SkipId3Tags(headers, 0, length, token);
        if (!FindFirstFrame(headers, length, token, ref position, out Frame first, out int freeFormatSize))
        {
            return null;
        }

        var seekHeader = ReadSeekHeader(headers, position, first, freeFormatSize);
        if (seekHeader != SeekHeader.None) position += first.GetLength(freeFormatSize);

        int initialBitrate = 0;
        TrackChannelMode channelMode = first.ChannelMode;
        bool variableBitrate = seekHeader == SeekHeader.Variable;
        long frameCount = 0;
        long bitrateSum = 0;
        while (position <= length - HeaderSize)
        {
            token.ThrowIfCancellationRequested();
            if (!TryReadFrame(headers, position, out Frame frame) || !first.IsCompatibleWith(frame))
            {
                long next = SkipId3Tags(headers, position, length, token);
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
    private static bool FindFirstFrame(HeaderReader source, long length, CancellationToken token,
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
    private static int FindFreeFormatSize(HeaderReader source, long position, long length, Frame first)
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
    private static long SkipId3Tags(HeaderReader source, long position, long length, CancellationToken token)
    {
        const int tagHeaderSize = 10;
        while (position <= length - tagHeaderSize)
        {
            token.ThrowIfCancellationRequested();
            var header = source.ReadAt(position, tagHeaderSize);
            if (header.Length != tagHeaderSize ||
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
    private static SeekHeader ReadSeekHeader(HeaderReader source, long position, Frame frame, int freeFormatSize)
    {
        int sideInfoSize = frame.SampleCount == 1152
            ? (frame.ChannelMode == TrackChannelMode.Mono ? 17 : 32)
            : (frame.ChannelMode == TrackChannelMode.Mono ? 9 : 17);
        int offset = HeaderSize + sideInfoSize;
        int size = frame.GetLength(freeFormatSize);
        SeekHeader xing = ReadXingMarker(source, position, size, offset);
        if (xing == SeekHeader.None && frame.HasCrc)
        {
            xing = ReadXingMarker(source, position, size, offset + 2);
        }
        if (xing != SeekHeader.None) return xing;
        return ReadMarker(source, position, size, 36).SequenceEqual("VBRI"u8)
            ? SeekHeader.Variable : SeekHeader.None;
    }

    private static SeekHeader ReadXingMarker(HeaderReader source, long position, int size, int offset)
    {
        var marker = ReadMarker(source, position, size, offset);
        if (marker.SequenceEqual("Xing"u8)) return SeekHeader.Variable;
        if (marker.SequenceEqual("Info"u8)) return SeekHeader.Constant;
        return SeekHeader.None;
    }

    private static ReadOnlySpan<byte> ReadMarker(HeaderReader source, long position, int size, int offset) =>
        offset + 4 <= size ? source.ReadAt(position + offset, 4) : [];

    /// <summary>Validates the MPEG sync word, Layer III marker and bitrate and sample rate indices.</summary>
    private static bool TryReadFrame(HeaderReader source, long position, out Frame frame)
    {
        frame = default;
        var bytes = source.ReadAt(position, HeaderSize);
        if (bytes.Length != HeaderSize) return false;

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

    /// <summary>Reads a window of bytes once for many nearby frame headers and tag probes.</summary>
    private sealed class HeaderReader(Stream source, long length) : IDisposable
    {
        private const int WindowSize = 32 * 1024;
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(WindowSize);
        private long _start;
        private int _available;
        private long? _logicalPosition;

        public ReadOnlySpan<byte> ReadAt(long position, int count)
        {
            long offset = position - _start;
            if (offset < 0 || offset > _available - count)
            {
                source.Position = position;
                int requested = (int)Math.Min(WindowSize, length - position);
                _available = source.ReadAtLeast(_buffer.AsSpan(0, requested), requested, false);
                _start = position;
                offset = 0;
            }

            int read = Math.Min(count, _available - (int)offset);
            _logicalPosition = position + read;
            return _buffer.AsSpan((int)offset, read);
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            if (_logicalPosition is { } position && source.Position != position) source.Position = position;
        }
    }

    private readonly record struct Frame(
        int SampleRate, int SampleCount, int Bitrate, int Padding, TrackChannelMode ChannelMode, bool HasCrc)
    {
        public bool IsCompatibleWith(Frame frame) => SampleRate == frame.SampleRate && SampleCount == frame.SampleCount &&
            (Bitrate == 0) == (frame.Bitrate == 0);

        public int GetLength(int freeFormatSize) =>
            (Bitrate == 0 ? freeFormatSize : SampleCount / 8 * Bitrate * 1000 / SampleRate) + Padding;
    }
}
