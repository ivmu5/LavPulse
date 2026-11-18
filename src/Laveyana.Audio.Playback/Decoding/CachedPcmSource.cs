using System.Runtime.InteropServices;

namespace Laveyana.Audio.Playback;

internal sealed class CachedPcmSource(string path, DeckCursor cursor) : IPcmSource, IDisposable
{
    private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024);
    private readonly short[] _buffer = new short[PlaybackFormat.BlockFrames * PcmMixer.ChannelCount];
    private DeckCursor.SeekPoint? _applied;
    private long _position;

    public int SampleRate => PlaybackFormat.SampleRate;
    public int LastReadFrames { get; private set; }

    /// <summary>Applies the latest seek and reads complete stereo frames from the PCM cache.</summary>
    public int Read(Span<float> destination)
    {
        if (cursor.Pending is { } pending && pending != _applied)
        {
            _file.Position = pending.Frame * PlaybackFormat.Pcm16FrameBytes;
            _position = pending.Frame;
            _applied = pending;
        }

        int frames = (int)Math.Min(destination.Length / PcmMixer.ChannelCount, cursor.Length - _position);
        var samples = _buffer.AsSpan(0, frames * PcmMixer.ChannelCount);
        _file.ReadExactly(MemoryMarshal.AsBytes(samples));
        for (int i = 0; i < samples.Length; i++)
        {
            destination[i] = samples[i] / 32768f;
        }

        _position += frames;
        LastReadFrames = frames;
        cursor.Report(_position, _applied);
        return frames;
    }

    /// <summary>Releases the underlying cache file after output stops.</summary>
    public void Dispose() => _file.Dispose();
}
