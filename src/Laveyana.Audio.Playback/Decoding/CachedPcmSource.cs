using System.Runtime.InteropServices;

namespace Laveyana.Audio.Playback;

internal sealed class CachedPcmSource(string path, DeckCursor cursor) : IPcmSource, IDisposable
{
    private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 32 * 1024);
    private readonly short[] _buffer = new short[(PlaybackFormat.BlockFrames * 5 / 4 + 2) * PcmMixer.ChannelCount];
    private DeckCursor.SeekPoint? _applied;
    private long _position;
    private double _fraction;

    public int SampleRate => PlaybackFormat.SampleRate;
    public int LastReadFrames { get; private set; }

    /// <summary>Returns the number of output frames remaining at the chosen speed.</summary>
    public long RemainingAtRate(double rate)
    {
        ApplyPendingSeek();
        return (long)Math.Ceiling((cursor.Length - _position - _fraction) / rate);
    }

    /// <summary>Reads the PCM cache at its original rate.</summary>
    public int Read(Span<float> destination) => ReadAtRate(destination, 1);

    /// <summary>Reads stereo samples at the requested speed, keeping the cursor in source frames.</summary>
    public int ReadAtRate(Span<float> destination, double rate)
    {
        const int channels = PlaybackFormat.Channels;
        int frames = (int)Math.Min(destination.Length / channels, RemainingAtRate(rate));
        if (frames == 0)
        {
            LastReadFrames = 0;
            cursor.Report(_position, _applied);
            return 0;
        }

        long byteOffset = _position * PlaybackFormat.Pcm16FrameBytes;
        if (_file.Position != byteOffset)
        {
            _file.Position = byteOffset;
        }

        bool direct = rate == 1 && _fraction == 0;
        int inputFrames = direct ? frames : (int)Math.Min(cursor.Length - _position,
            (long)(_fraction + (frames - 1) * rate) + 2);
        var samples = _buffer.AsSpan(0, inputFrames * channels);
        _file.ReadExactly(MemoryMarshal.AsBytes(samples));
        if (direct)
        {
            Pcm16.Decode(samples, destination);
        }
        else
        {
            for (int frame = 0; frame < frames; frame++)
            {
                double sourceFrame = _fraction + frame * rate;
                int index = (int)sourceFrame;
                float weight = (float)(sourceFrame - index);
                int next = Math.Min(index + 1, inputFrames - 1) * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    int first = samples[index * channels + channel];
                    int last = samples[next + channel];
                    destination[frame * channels + channel] = (first + (last - first) * weight) / 32768f;
                }
            }
        }

        double advanced = _fraction + frames * rate;
        _position = Math.Min(cursor.Length, _position + (long)advanced);
        _fraction = _position == cursor.Length ? 0 : advanced - (long)advanced;
        LastReadFrames = frames;
        cursor.Report(_position, _applied);
        return frames;
    }

    private void ApplyPendingSeek()
    {
        var pending = cursor.Pending;
        if (pending != _applied)
        {
            _position = pending.Frame;
            _fraction = 0;
            _applied = pending;
        }
    }

    /// <summary>Releases the underlying cache file after output stops.</summary>
    public void Dispose() => _file.Dispose();
}
