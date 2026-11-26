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
    /// <summary>Fills interleaved stereo PCM, at most PlaybackFormat.BlockFrames frames.
    /// Returns the number of real frames; zero ends this output run.</summary>
    /// <remarks>One render owner only. Supply a complete stereo frame for every requested frame.</remarks>
    int Render(Span<float> buffer);

    /// <summary>Publishes cumulative frames consumed since this output run started.
    /// Use long.MaxValue only when the device counter is unavailable.</summary>
    void ReportOutputPosition(long playedFrames);
}

/// <summary>Exposes frequency levels independently of the audio writer.</summary>
public interface ISpectrumSource
{
    /// <summary>Copies the latest eligible levels and advances the caller's version when new data is available.</summary>
    /// <remarks>Provide at least PlaybackFormat.SpectrumBars entries. Initialize the version
    /// to zero for each new source; a false return leaves the destination and version unchanged.</remarks>
    bool TryReadSpectrum(Span<float> destination, ref long version);
}
