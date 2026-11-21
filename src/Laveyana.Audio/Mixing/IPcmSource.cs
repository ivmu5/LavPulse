namespace Laveyana.Audio;

/// <summary>Supplies decoded, interleaved stereo PCM as normalized, finite floats.</summary>
public interface IPcmSource
{
    /// <summary>The fixed sample rate, in audio frames per second.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Writes as many complete frames as are ready, up to the destination capacity.
    /// Returns the number of frames written. Zero indicates no frames available now.
    /// </summary>
    /// <remarks>
    /// One frame contains a left sample and a right sample. A short read leaves the
    /// remainder of the block silent in the mixer. Implementations used by a real-time
    /// output should read from prepared buffers without blocking or allocating.
    /// </remarks>
    int Read(Span<float> destination);
}
