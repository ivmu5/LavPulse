namespace Laveyana.Audio.Playback;

/// <summary>Shapes the master across a native output run, without replacing or seeking either deck.</summary>
internal sealed class TransportFadeSource(IPcmSource source, int startMilliseconds) : IPcmSource
{
    private const int NoPause = -1;
    private readonly int _startFrames = ToFrames(startMilliseconds);
    private int _pauseFrames = NoPause;
    private int _startedFrames;
    private int _pausedFrames;
    private float _lastGain = startMilliseconds == 0 ? 1 : 0;
    private float _pauseStartGain;

    public int SampleRate => source.SampleRate;
    public int LastReadFrames { get; private set; }

    /// <summary>Requests an outgoing envelope once. Zero duration stops at the next block boundary.</summary>
    public bool RequestPause(int milliseconds) =>
        Interlocked.CompareExchange(ref _pauseFrames, ToFrames(milliseconds), NoPause) == NoPause;

    /// <summary>Applies the startup or pause envelope and returns only the frames that were consumed.</summary>
    public int Read(Span<float> destination)
    {
        int pause = Volatile.Read(ref _pauseFrames);
        int capacity = destination.Length / PlaybackFormat.Channels;
        int requested = pause == NoPause ? capacity : Math.Min(capacity, pause - _pausedFrames);
        if (requested <= 0 || (pause != NoPause && _pausedFrames == 0 && _lastGain == 0))
        {
            return LastReadFrames = 0;
        }

        int read = source.Read(destination[..(requested * PlaybackFormat.Channels)]);
        if((uint)read>(uint)requested)
            throw new InvalidDataException("Invalid PCM frame count before transport fade.");
        if (pause == NoPause)
        {
            int fade = Math.Min(read, _startFrames - _startedFrames);
            for (int frame = 0; frame < fade; frame++)
            {
                float progress = (float)(_startedFrames + frame) / Math.Max(1, _startFrames - 1);
                ApplyGain(destination, frame, HyperbolicFade.Gains(progress).Incoming);
            }
            _startedFrames += fade;
            if (read > fade)
            {
                _lastGain = 1;
            }
        }
        else
        {
            if (_pausedFrames == 0)
            {
                _pauseStartGain = _lastGain;
            }
            float firstProgress = (float)_pausedFrames / Math.Max(1, pause - 1);
            for (int frame = 0; frame < read; frame++)
            {
                float progress = read < requested
                    ? 1 - (1 - firstProgress) * (read - 1 - frame) / Math.Max(1, read - 1)
                    : (float)(_pausedFrames + frame) / Math.Max(1, pause - 1);
                ApplyGain(destination, frame, _pauseStartGain * HyperbolicFade.Gains(progress).Outgoing);
            }
            _pausedFrames += read;
        }

        return LastReadFrames = read;
    }

    private void ApplyGain(Span<float> destination, int frame, float gain)
    {
        int sample = frame * PlaybackFormat.Channels;
        destination[sample] *= gain;
        destination[sample + 1] *= gain;
        _lastGain = gain;
    }

    private static int ToFrames(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, 3000);
        return PlaybackFormat.SampleRate * milliseconds / 1000;
    }
}
