namespace Laveyana.Audio;

/// <summary>An input and its linear gain.</summary>
public sealed class PcmInput
{
    private float _gain;

    public PcmInput(IPcmSource source, float gain = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        Gain = gain;
    }

    public IPcmSource Source { get; }

    /// <summary>Linear gain from 0 to 1. UI updates take effect on the next mix block.</summary>
    public float Gain
    {
        get => Volatile.Read(ref _gain);
        set
        {
            if (!float.IsFinite(value) || value < 0 || value > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Gain must be between 0 and 1.");
            }

            Volatile.Write(ref _gain, value);
        }
    }
}
