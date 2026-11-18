namespace Laveyana.Audio.Playback;

/// <summary>Stores the ten master equalizer gains in decibels.</summary>
public sealed class EqualizerSettings
{
    public const int MinimumGainDb = -12;
    public const int MaximumGainDb = 12;
    public static IReadOnlyList<int> Frequencies { get; } = Array.AsReadOnly(new[] { 60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000 });
    public static EqualizerSettings Flat { get; } = new(false, new double[Frequencies.Count]);

    private readonly double[] _gains;

    private EqualizerSettings(bool enabled, double[] gains)
    {
        Enabled = enabled;
        _gains = gains;
    }

    public bool Enabled { get; }
    public double GainAt(int band) => _gains[band];
    public EqualizerSettings WithEnabled(bool enabled) => enabled == Enabled ? this : new(enabled, _gains);

    /// <summary>Returns a new snapshot with one bounded gain adjusted.</summary>
    public EqualizerSettings WithGain(int band, double decibels)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)band, (uint)_gains.Length);
        if (!double.IsFinite(decibels)) decibels = 0;
        decibels = Math.Clamp(decibels, MinimumGainDb, MaximumGainDb);
        if (decibels == _gains[band]) return this;
        var gains = (double[])_gains.Clone();
        gains[band] = decibels;
        return new(Enabled, gains);
    }
}
