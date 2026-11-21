using NLayer;

namespace Laveyana.Audio.Playback;

/// <summary>Reads interleaved mono or stereo samples at the MP3's native sample rate.</summary>
internal interface IMp3SampleDecoder : IDisposable
{
    int Channels { get; }
    int SampleRate { get; }
    int ReadSamples(float[] destination);
}

// Used during import to prepare the waveform and PCM cache.
internal sealed class Mp3PcmSource : IPcmSource, IDisposable
{
    private readonly IMp3SampleDecoder _decoder;
    private readonly float[] _samples;
    private readonly int _channels;
    private readonly int _sourceRate;

    private int _offset;
    private int _available;
    private bool _decoderEnded;
    private int _phase;
    private bool _initialized;
    private bool _hasCurrent;
    private bool _hasNext;
    private StereoFrame _current;
    private StereoFrame _next;

    public Mp3PcmSource(Stream source) : this(new NLayerDecoder(new MpegFile(source))) { }

    internal Mp3PcmSource(IMp3SampleDecoder decoder)
    {
        _channels = decoder.Channels;
        _sourceRate = decoder.SampleRate;
        if (_channels is not (1 or 2) || _sourceRate <= 0 || _sourceRate > PlaybackFormat.SampleRate)
        {
            decoder.Dispose();
            throw new InvalidDataException("Ожидался MP3 с одним или двумя каналами до 48 кГц.");
        }

        _samples = new float[4096 * _channels];
        _decoder = decoder;
    }

    public int SampleRate => PlaybackFormat.SampleRate;

    /// <summary>Interpolates decoded frames onto the mixer format with no per-block allocation.</summary>
    public int Read(Span<float> destination)
    {
        if (_sourceRate == SampleRate) return ReadAtNativeRate(destination);

        if (!_initialized)
        {
            _hasCurrent = ReadFrame(out _current);
            _hasNext = _hasCurrent && ReadFrame(out _next);
            _initialized = true;
        }

        int frames = 0;
        int capacity = destination.Length / PcmMixer.ChannelCount;
        while (frames < capacity && _hasCurrent)
        {
            float fraction = (float)_phase / PlaybackFormat.SampleRate;
            StereoFrame next = _hasNext ? _next : _current;
            destination[frames * 2] = _current.Left + (next.Left - _current.Left) * fraction;
            destination[frames * 2 + 1] = _current.Right + (next.Right - _current.Right) * fraction;
            frames++;

            _phase += _sourceRate;
            if (_phase >= PlaybackFormat.SampleRate)
            {
                _phase -= PlaybackFormat.SampleRate;
                _current = _next;
                _hasCurrent = _hasNext;
                _hasNext = _hasCurrent && ReadFrame(out _next);
            }
        }

        return frames;
    }

    /// <summary>Copies complete blocks directly when the decoder already matches the mixer rate.</summary>
    private int ReadAtNativeRate(Span<float> destination)
    {
        int frames = 0;
        int capacity = destination.Length / PcmMixer.ChannelCount;
        while (frames < capacity && (_offset != _available || RefillSamples()))
        {
            int count = Math.Min(capacity - frames, (_available - _offset) / _channels);
            var samples = _samples.AsSpan(_offset, count * _channels);
            var output = destination.Slice(frames * 2, count * 2);
            if (_channels == 2)
            {
                for (int i = 0; i < output.Length; i++)
                    output[i] = Math.Clamp(samples[i], -1f, 1f);
            }
            else
            {
                for (int i = 0; i < count; i++)
                    output[i * 2] = output[i * 2 + 1] = Math.Clamp(samples[i], -1f, 1f);
            }
            _offset += samples.Length;
            frames += count;
        }
        return frames;
    }

    /// <summary>Refills the decoder buffer when it runs out and returns one normalized stereo frame.</summary>
    private bool ReadFrame(out StereoFrame frame)
    {
        if (_offset == _available && !RefillSamples())
        {
            frame = default;
            return false;
        }

        float left = Math.Clamp(_samples[_offset], -1f, 1f);
        float right = Math.Clamp(_samples[_offset + _channels - 1], -1f, 1f);
        _offset += _channels;
        frame = new StereoFrame(left, right);
        return true;
    }

    private bool RefillSamples()
    {
        if (_decoderEnded) return false;
        _available = _decoder.ReadSamples(_samples);
        _offset = 0;
        if ((uint)_available > (uint)_samples.Length || _available % _channels != 0)
            throw new InvalidDataException("Декодер вернул неполный аудиокадр.");

        _decoderEnded = _available == 0;
        return !_decoderEnded;
    }

    /// <summary>Releases the decoder. The importer owns the input stream.</summary>
    public void Dispose() => _decoder.Dispose();

    private readonly record struct StereoFrame(float Left, float Right);

    private sealed class NLayerDecoder(MpegFile file) : IMp3SampleDecoder
    {
        public int Channels => file.Channels;
        public int SampleRate => file.SampleRate;
        public int ReadSamples(float[] destination) => file.ReadSamples(destination, 0, destination.Length);
        public void Dispose() => file.Dispose();
    }
}
