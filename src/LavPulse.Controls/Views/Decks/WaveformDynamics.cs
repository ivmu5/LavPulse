using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

/// <summary>
/// Precomputes a robust amplitude reference and bass onsets from the existing waveform cache.
/// This is visual transient emphasis, not a BPM estimate or a beat grid.
/// </summary>
internal sealed class WaveformDynamics
{
    private readonly float[] _bassAttacks;
    private readonly float _referencePeak;

    public WaveformDynamics(IReadOnlyList<WaveformPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var peaks = new float[points.Count];
        _bassAttacks = new float[points.Count];
        for (int i = 0; i < points.Count; i++) peaks[i] = Math.Max(0, points[i].Peak);
        Array.Sort(peaks);
        // A single unusually loud sample must not flatten all the other columns.
        _referencePeak = points.Count == 0 ? 1 : Math.Max(.08f,
            Math.Max(peaks[(int)Math.Ceiling(peaks.Length * .95) - 1], peaks[^1] * .65f));

        // Comparing bass amplitude against the preceding ~200 ms highlights the attack of
        // a kick while leaving a sustained bass note at its natural height.
        float recent = 0;
        const int history = 5;
        for (int i = 0; i < points.Count; i++)
        {
            int count = Math.Min(i, history);
            float baseline = count == 0 ? Math.Max(0, points[i].Low) : recent / count;
            float rise = Math.Max(0, points[i].Low - baseline - .025f);
            _bassAttacks[i] = Math.Clamp(rise / Math.Max(.08f, baseline), 0, 1);
            recent += Math.Max(0, points[i].Low);
            if (i >= history) recent -= Math.Max(0, points[i - history].Low);
        }
    }

    public float BassAttack(int index) => _bassAttacks[index];

    public float Height(float peak, float bassAttack)
    {
        float normalized = Math.Clamp(Math.Max(0, peak) / _referencePeak, 0, 1);
        return Math.Clamp(MathF.Pow(normalized, 1.16f) * (.84f + .24f * Math.Clamp(bassAttack, 0, 1)), 0, 1);
    }
}
