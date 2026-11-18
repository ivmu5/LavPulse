using NLayer;

namespace Laveyana.Audio.Playback;

// Used during import to prepare the waveform and PCM cache.
internal sealed class Mp3PcmSource : IPcmSource, IDisposable
{
    private readonly FileStream _file;
    private readonly MpegFile _mpeg;
    private readonly float[] _samples;
    private readonly int _channels;
    private readonly int _sourceRate;

    private int _offset;
    private int _available;
    private int _phase;
    private bool _initialized;
    private bool _hasCurrent;
    private bool _hasNext;
    private StereoFrame _current;
    private StereoFrame _next;

    public Mp3PcmSource(string path)
    {
        _file = File.OpenRead(path);
        try
        {
            var mpeg = new MpegFile(_file);
            if (mpeg.Channels is not (1 or 2) ||
                mpeg.SampleRate <= 0 || mpeg.SampleRate > PlaybackFormat.SampleRate)
            {
                throw new InvalidDataException("Ожидался MP3 с одним или двумя каналами до 48 кГц.");
            }

            _channels = mpeg.Channels;
            _sourceRate = mpeg.SampleRate;
            _samples = new float[4096 * _channels];
            _mpeg = mpeg;
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public int SampleRate => PlaybackFormat.SampleRate;

    /// <summary>Interpolates decoded frames onto the mixer format with no per-block allocation.</summary>
    public int Read(Span<float> destination)
    {
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

    /// <summary>Refills the decoder buffer when it runs out and returns one normalized stereo frame.</summary>
    private bool ReadFrame(out StereoFrame frame)
    {
        if (_offset == _available)
        {
            _available = _mpeg.ReadSamples(_samples, 0, _samples.Length);
            _offset = 0;
            if (_available == 0)
            {
                frame = default;
                return false;
            }

            if (_available % _channels != 0)
            {
                throw new InvalidDataException("Декодер вернул неполный аудиокадр.");
            }
        }

        float left = Math.Clamp(_samples[_offset], -1f, 1f);
        float right = Math.Clamp(_samples[_offset + _channels - 1], -1f, 1f);
        _offset += _channels;
        frame = new StereoFrame(left, right);
        return true;
    }

    /// <summary>Releases the decoder and its input file.</summary>
    public void Dispose()
    {
        _mpeg.Dispose();
        _file.Dispose();
    }

    private readonly record struct StereoFrame(float Left, float Right);
}
