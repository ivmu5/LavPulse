using NLayer;

namespace Laveyana.Audio.Playback;

/// <summary>Reads interleaved mono or stereo samples at the encoded audio's native rate.</summary>
internal interface IMp3SampleDecoder : IDisposable
{
    int Channels { get; }
    int SampleRate { get; }
    int ReadSamples(float[] destination);
    string? Codec => null;
}

// Used during import to prepare the waveform and PCM cache.
internal sealed class Mp3PcmSource : IPcmSource, IDisposable
{
    private readonly IMp3SampleDecoder _decoder;
    private readonly float[] _samples;
    private readonly int _channels;
    private readonly int _sourceRate;
    private readonly DownsampleFilter? _downsample;

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
        if (_channels is not (1 or 2) || _sourceRate <= 0 || _sourceRate > 192_000)
        {
            decoder.Dispose();
            throw new InvalidDataException("Ожидался аудиофайл с одним или двумя каналами до 192 кГц.");
        }

        _samples = new float[4096 * _channels];
        _decoder = decoder;
        if (_sourceRate > PlaybackFormat.SampleRate)
            _downsample = new DownsampleFilter(_sourceRate);
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
            while (_phase >= PlaybackFormat.SampleRate && _hasCurrent)
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
                    output[i] = Normalize(samples[i]);
            }
            else
            {
                for (int i = 0; i < count; i++)
                    output[i * 2] = output[i * 2 + 1] = Normalize(samples[i]);
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

        float left = Normalize(_samples[_offset]);
        float right = Normalize(_samples[_offset + _channels - 1]);
        _offset += _channels;
        frame = _downsample is null ? new StereoFrame(left, right) : _downsample.Process(left, right);
        return true;
    }

    private static float Normalize(float value) => float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0;

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

    // Fourth-order low pass before rate reduction. The mixer remains at 48 kHz.
    private sealed class DownsampleFilter(int sourceRate)
    {
        private readonly Biquad _left1 = new(sourceRate, .5411961);
        private readonly Biquad _left2 = new(sourceRate, 1.306563);
        private readonly Biquad _right1 = new(sourceRate, .5411961);
        private readonly Biquad _right2 = new(sourceRate, 1.306563);

        public StereoFrame Process(float left, float right) => new(
            (float)Math.Clamp(_left2.Process(_left1.Process(left)), -1, 1),
            (float)Math.Clamp(_right2.Process(_right1.Process(right)), -1, 1));

        private sealed class Biquad
        {
            private readonly double _b0, _b1, _b2, _a1, _a2;
            private double _z1, _z2;

            public Biquad(int rate, double q)
            {
                double omega = 2 * Math.PI * (PlaybackFormat.SampleRate * .42) / rate;
                double c = Math.Cos(omega);
                double alpha = Math.Sin(omega) / (2 * q);
                double a0 = 1 + alpha;
                _b0 = (1 - c) / (2 * a0);
                _b1 = (1 - c) / a0;
                _b2 = _b0;
                _a1 = -2 * c / a0;
                _a2 = (1 - alpha) / a0;
            }

            public double Process(double input)
            {
                double output = _b0 * input + _z1;
                _z1 = _b1 * input - _a1 * output + _z2;
                _z2 = _b2 * input - _a2 * output;
                return output;
            }
        }
    }

    private sealed class NLayerDecoder(MpegFile file) : IMp3SampleDecoder
    {
        public int Channels => file.Channels;
        public int SampleRate => file.SampleRate;
        public int ReadSamples(float[] destination) => file.ReadSamples(destination, 0, destination.Length);
        public void Dispose() => file.Dispose();
    }
}
