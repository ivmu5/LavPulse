using System.Buffers.Binary;
using System.Text;

namespace Laveyana.Audio.Playback;

/// <summary>Optional display tags from an MP3's ID3 header.</summary>
internal sealed record Mp3Tags(string? Title = null, string? Artist = null, byte[]? Artwork = null)
{
    private const int MaxTagSize = 8 * 1024 * 1024;
    private const int MaxArtworkSize = 3 * 1024 * 1024;

    /// <summary>Reads ID3v2.3 or ID3v2.4 tag bytes, including on non-seekable streams.</summary>
    public static Mp3Tags Read(Stream file)
    {
        Span<byte> header = stackalloc byte[10];
        if (file.ReadAtLeast(header, header.Length, false) != header.Length ||
            !header[..3].SequenceEqual("ID3"u8) || header[3] is not (3 or 4) ||
            (header[5] & 0x80) != 0)
        {
            return new();
        }

        bool version4 = header[3] == 4;
        int size = SyncSafe(header[6..]);
        if (size is < 10 or > MaxTagSize)
        {
            return new();
        }

        var bytes = new byte[size];
        if (file.ReadAtLeast(bytes, bytes.Length, false) != bytes.Length)
        {
            return new();
        }
        int offset = 0;
        if ((header[5] & 0x40) != 0)
        {
            int extension = version4 ? SyncSafe(bytes.AsSpan(0, 4)) : BinaryPrimitives.ReadInt32BigEndian(bytes);
            if (extension < 0 || extension > size - (version4 ? 0 : 4))
            {
                return new();
            }

            offset = version4 ? extension : extension + 4;
        }

        string? title = null;
        string? artist = null;
        byte[]? artwork = null;
        while (offset <= bytes.Length - 10)
        {
            var frame = bytes.AsSpan(offset, 10);
            int length = version4 ? SyncSafe(frame[4..8]) : BinaryPrimitives.ReadInt32BigEndian(frame[4..8]);
            offset += 10;
            if (length <= 0 || length > bytes.Length - offset)
            {
                break;
            }

            var data = bytes.AsSpan(offset, length);
            offset += length;
            if (frame[9] != 0)
            {
                continue;
            }

            if (title is null && frame[..4].SequenceEqual("TIT2"u8))
            {
                title = ReadText(data);
            }
            else if (artist is null && frame[..4].SequenceEqual("TPE1"u8))
            {
                artist = ReadText(data);
            }
            else if (frame[..4].SequenceEqual("APIC"u8) && ReadArtwork(data) is { } image)
            {
                if (artwork is null || image.FrontCover)
                {
                    artwork = image.Bytes;
                }
            }
        }

        return new(title, artist, artwork);
    }

    /// <summary>Decodes a text frame with its declared encoding and drops padding.</summary>
    private static string? ReadText(ReadOnlySpan<byte> data)
    {
        var text = data[1..];
        var encoding = data[0] switch
        {
            0 => Encoding.Latin1,
            1 when text.StartsWith(new byte[] { 0xfe, 0xff }) => Encoding.BigEndianUnicode,
            1 => Encoding.Unicode,
            2 => Encoding.BigEndianUnicode,
            3 => Encoding.UTF8,
            _ => null
        };
        if (encoding is null)
        {
            return null;
        }

        string result = encoding.GetString(text).Trim('\0', '\ufeff', ' ', '\t', '\r', '\n');
        int terminator = result.IndexOf('\0');
        return terminator >= 0 ? result[..terminator] : result.Length > 0 ? result : null;
    }

    /// <summary>Identifies an embedded PNG or JPEG after the picture description.</summary>
    private static (byte[] Bytes, bool FrontCover)? ReadArtwork(ReadOnlySpan<byte> frame)
    {
        int mimeEnd = frame[1..].IndexOf((byte)0) + 1;
        if (mimeEnd == 0 || mimeEnd + 2 >= frame.Length)
        {
            return null;
        }

        int start = mimeEnd + 2;
        int terminator = frame[0] is 1 or 2 ? 2 : 1;
        while (start <= frame.Length - terminator)
        {
            if (frame[start] == 0 && (terminator == 1 || frame[start + 1] == 0))
            {
                var image = frame[(start + terminator)..];
                if (image.Length > MaxArtworkSize ||
                    !(image.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) ||
                      image.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })))
                {
                    return null;
                }

                return (image.ToArray(), frame[mimeEnd + 1] == 3);
            }

            start += terminator;
        }

        return null;
    }

    /// <summary>Decodes a validated four-byte ID3 size.</summary>
    private static int SyncSafe(ReadOnlySpan<byte> size) => ((size[0] | size[1] | size[2] | size[3]) & 0x80) != 0
        ? -1
        : size[0] << 21 | size[1] << 14 | size[2] << 7 | size[3];
}
