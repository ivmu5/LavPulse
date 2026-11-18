namespace Laveyana.Audio;

/// <summary>Renders a stereo Master from sources with a shared sample rate.</summary>
public sealed class PcmMixer
{
    public const int ChannelCount = 2;

    private readonly PcmInput[] _inputs;
    private readonly float[] _sourceBuffer = new float[1024 * ChannelCount];

    public PcmMixer(IEnumerable<PcmInput> inputs, int spectrumBarCount = 48)
        : this(inputs, spectrumBarCount, MasterSpectrum.WindowFrames)
    {
    }

    public PcmMixer(IEnumerable<PcmInput> inputs, int spectrumBarCount, int spectrumWindowFrames)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        _inputs = inputs.ToArray();

        if (_inputs.Length == 0)
        {
            throw new ArgumentException("At least one input is required.", nameof(inputs));
        }

        var sources = new HashSet<IPcmSource>(ReferenceEqualityComparer.Instance);
        foreach (PcmInput input in _inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!sources.Add(input.Source))
            {
                throw new ArgumentException("Each input must have its own PCM source.", nameof(inputs));
            }
        }

        SampleRate = _inputs[0].Source.SampleRate;
        if (SampleRate <= 0 || _inputs.Any(input => input.Source.SampleRate != SampleRate))
        {
            throw new ArgumentException("Sources must have the same positive sample rate.", nameof(inputs));
        }

        Spectrum = new MasterSpectrum(SampleRate, spectrumBarCount, spectrumWindowFrames);
    }

    public int SampleRate { get; }

    public MasterSpectrum Spectrum { get; }

    /// <summary>Fills the requested frames with the Master, using silence where no source has samples.</summary>
    /// <remarks>Call from one render thread. The destination must contain complete stereo frames.</remarks>
    public void Render(Span<float> destination)
    {
        if (destination.Length % ChannelCount != 0)
        {
            throw new ArgumentException("The buffer must hold complete stereo frames.", nameof(destination));
        }

        while (!destination.IsEmpty)
        {
            int samples = Math.Min(destination.Length, _sourceBuffer.Length);
            Span<float> block = destination[..samples];
            MixBlock(block);
            Spectrum.Capture(block);
            destination = destination[samples..];
        }
    }

    /// <summary>Applies gains, mixes stereo samples and clamps the finished master.</summary>
    private void MixBlock(Span<float> block)
    {
        block.Clear();
        Span<float> sourceBuffer = _sourceBuffer.AsSpan(0, block.Length);
        int frameCount = block.Length / ChannelCount;

        foreach (PcmInput input in _inputs)
        {
            float gain = input.Gain;
            int framesRead = input.Source.Read(sourceBuffer);
            if ((uint)framesRead > (uint)frameCount)
            {
                throw new InvalidOperationException("The PCM source returned an invalid frame count.");
            }

            int samplesRead = framesRead * ChannelCount;
            for (int sample = 0; sample < samplesRead; sample++)
            {
                block[sample] += sourceBuffer[sample] * gain;
            }
        }

        for (int sample = 0; sample < block.Length; sample++)
        {
            block[sample] = Math.Clamp(block[sample], -1f, 1f);
        }
    }
}
