namespace Laveyana.Audio.Playback;

/// <summary>Adapts a platform audio device to the platform-independent playback session.</summary>
public interface IAudioOutput
{
    /// <summary>Renders the mix until the device has played the final samples or the operation is cancelled.</summary>
    Task PlayAsync(IAudioRenderSource source, CancellationToken cancellationToken);
}

/// <summary>Exposes the mixer to one audio writer.</summary>
public interface IAudioRenderSource
{
    /// <summary>Fills a stereo PCM block and returns the number of frames preceding the end of the mix.</summary>
    int Render(Span<float> buffer);

    /// <summary>Publishes the number of frames consumed by the device, when the backend exposes a playback head.</summary>
    void ReportOutputPosition(long playedFrames);
}

/// <summary>Exposes frequency levels independently of the audio writer.</summary>
public interface ISpectrumSource
{
    /// <summary>Copies the latest eligible levels and advances the caller's version when new data is available.</summary>
    bool TryReadSpectrum(Span<float> destination, ref long version);
}
