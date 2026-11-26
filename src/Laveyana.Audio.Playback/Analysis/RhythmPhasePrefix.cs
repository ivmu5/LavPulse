namespace Laveyana.Audio.Playback;

/// <summary>One reusable set of cumulative complex sums per tempo candidate.
/// Overlapping windows then require only four differences, not new trigonometry.</summary>
internal sealed class RhythmPhasePrefix(int count)
{
    private readonly double[] _real = new double[count + 1];
    private readonly double[] _imaginary = new double[count + 1];
    private readonly double[] _accentReal = new double[count + 1];
    private readonly double[] _accentImaginary = new double[count + 1];

    public void Build(ReadOnlySpan<int> indices, ReadOnlySpan<float> strengths,
        ReadOnlySpan<double> accents, double anglePerHop, CancellationToken token)
    {
        for (int i = 0; i < indices.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var (sin, cos) = Math.SinCos(indices[i] * anglePerHop);
            _real[i + 1] = _real[i] + strengths[i] * cos;
            _imaginary[i + 1] = _imaginary[i] + strengths[i] * sin;
            _accentReal[i + 1] = _accentReal[i] + accents[i] * cos;
            _accentImaginary[i + 1] = _accentImaginary[i] + accents[i] * sin;
        }
    }

    public (double Real, double Imaginary, double AccentReal, double AccentImaginary) Range(int first, int end) =>
        (_real[end] - _real[first], _imaginary[end] - _imaginary[first],
         _accentReal[end] - _accentReal[first], _accentImaginary[end] - _accentImaginary[first]);
}
