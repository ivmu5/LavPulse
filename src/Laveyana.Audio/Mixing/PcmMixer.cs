namespace Laveyana.Audio;

/// <summary>Renders a stereo Master from sources with a shared sample rate.</summary>
public sealed class PcmMixer
{
    public const int ChannelCount = 2;
    private const int BlockSamples = 1024 * ChannelCount;

    private readonly PcmInput[] _inputs;
    private readonly float[] _sourceBuffer;

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

        _sourceBuffer = _inputs.Length > 1 ? new float[BlockSamples] : [];
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
            int samples = Math.Min(destination.Length, BlockSamples);
            Span<float> block = destination[..samples];
            MixBlock(block);
            Spectrum.Capture(block);
            destination = destination[samples..];
        }
    }

    /// <summary>Applies gains, mixes stereo samples and clamps the finished master.</summary>
    private void MixBlock(Span<float> block)
    {
        if (_inputs.Length == 1)
        {
            var input = _inputs[0];
            float gain = input.Gain;
            int samples = ReadSamples(input.Source, block);
            var signal = block[..samples];
            if (gain == 1)
            {
                Clamp(signal);
            }
            else
            {
                for (int i = 0; i < signal.Length; i++)
                {
                    signal[i] = Math.Clamp(signal[i] * gain, -1f, 1f);
                }
            }
            block[samples..].Clear();
            return;
        }

        block.Clear();
        Span<float> sourceBuffer = _sourceBuffer.AsSpan(0, block.Length);
        foreach (PcmInput input in _inputs)
        {
            float gain = input.Gain;
            int samplesRead = ReadSamples(input.Source, sourceBuffer);
            for (int sample = 0; sample < samplesRead; sample++)
            {
                block[sample] += sourceBuffer[sample] * gain;
            }
        }
        Clamp(block);
    }

    private static int ReadSamples(IPcmSource source, Span<float> buffer)
    {
        int frames = source.Read(buffer);
        if ((uint)frames > (uint)(buffer.Length / ChannelCount))
        {
            throw new InvalidOperationException("The PCM source returned an invalid frame count.");
        }
        return frames * ChannelCount;
    }

    private static void Clamp(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i], -1f, 1f);
        }
    }
}
