namespace Laveyana.Audio.Playback;

/// <summary>Runs a one-shot fade on the master before ending the current output run.</summary>
internal sealed class PauseFadeSource(IPcmSource source) : IPcmSource
{
    private const int FadeFrames = PlaybackFormat.SampleRate * 300 / 1000;
    private int _pauseRequested;
    private int _fadedFrames;

    public int SampleRate => source.SampleRate;
    public int LastReadFrames { get; private set; }

    /// <summary>Requests the fade from another thread without interrupting the native output.</summary>
    public bool RequestPause() => Interlocked.Exchange(ref _pauseRequested, 1) == 0;

    /// <summary>Applies the outgoing curve and stops reading at the last faded frame.</summary>
    public int Read(Span<float> destination)
    {
        if (Volatile.Read(ref _pauseRequested) == 0)
        {
            return LastReadFrames = source.Read(destination);
        }

        int requested = Math.Min(destination.Length / PlaybackFormat.Channels, FadeFrames - _fadedFrames);
        if (requested == 0)
        {
            return LastReadFrames = 0;
        }

        int read = source.Read(destination[..(requested * PlaybackFormat.Channels)]);
        int end = FadeFrames - 1;
        // If the queue ends during a read, finish the remainder of the fade on those last samples.
        float firstProgress = (float)_fadedFrames / end;
        bool atQueueEnd = read < requested;
        for (int frame = 0; frame < read; frame++)
        {
            float progress = atQueueEnd
                ? 1 - (1 - firstProgress) * (read - 1 - frame) / Math.Max(1, read - 1)
                : (float)(_fadedFrames + frame) / end;
            float gain = HyperbolicFade.Gains(progress).Outgoing;
            int sample = frame * PlaybackFormat.Channels;
            destination[sample] *= gain;
            destination[sample + 1] *= gain;
        }

        _fadedFrames += read;
        return LastReadFrames = read;
    }
}
