using System.Numerics;

namespace Laveyana.Audio.Playback;

/// <summary>Streaming stereo spectral flux. No PCM is retained beyond one FFT window.</summary>
internal readonly record struct RhythmSpectrumFrame(long Frame, float Sub, float Bass,
    float LowMid, float Mid, float High)
{
    internal float Band(int band) => band switch
    {
        0 => Sub, 1 => Bass, 2 => LowMid, 3 => Mid, 4 => High,
        _ => throw new ArgumentOutOfRangeException(nameof(band))
    };
}

internal sealed class RhythmSpectralFlux
{
    internal const int Size = 2048, Hop = 960, MaxFrames = 1_000_000;
    private static readonly float[] Window = Enumerable.Range(0, Size)
        .Select(i => .5f - .5f * MathF.Cos(MathF.Tau * i / (Size - 1))).ToArray();
    private static readonly RhythmFftPlan Fft=new(Size);
    private static readonly int[] BinBand = Enumerable.Range(0, Size / 2).Select(i =>
    {
        double hz = i * (double)PlaybackFormat.SampleRate / Size;
        return hz < 20 || hz >= 16000 ? -1 : hz < 60 ? 0 : hz < 150 ? 1 :
            hz < 400 ? 2 : hz < 2000 ? 3 : 4;
    }).ToArray();
    // Band membership is constant; avoid recounting the same FFT bins every hop.
    private static readonly int[] BandCounts=Enumerable.Range(0,5)
        .Select(b=>Math.Max(1,BinBand.Skip(1).Count(v=>v==b))).ToArray();
    private readonly float[] _left = new float[Size], _right = new float[Size];
    private readonly Complex[] _fft = new Complex[Size];
    private readonly double[] _previous = new double[Size / 2];
    private readonly List<RhythmSpectrumFrame> _frames = [];
    private long _count;
    private int _write;
    private bool _hasPrevious;
    internal IReadOnlyList<RhythmSpectrumFrame> Frames => _frames;

    internal void Add(ReadOnlySpan<float> stereo, CancellationToken token)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            if (_frames.Count >= MaxFrames) return;
            _left[_write] = stereo[i]; _right[_write] = stereo[i + 1];
            _write = (_write + 1) & (Size - 1);
            _count++;
            if (_count < Size || (_count - Size) % Hop != 0) continue;
            token.ThrowIfCancellationRequested();
            AnalyzeFrame();
        }
    }

    private void AnalyzeFrame()
    {
        for (int i = 0; i < Size; i++)
        {
            int source = (_write + i) & (Size - 1);
            _fft[Fft.Reverse(i)] = new Complex(_left[source] * Window[i], _right[source] * Window[i]);
        }
        Fft.Transform(_fft);
        Span<double> flux = stackalloc double[5];
        flux.Clear();
        for (int i = 1; i < Size / 2; i++)
        {
            int band = BinBand[i];
            if (band < 0) continue;
            // Packing L/R into complex input preserves stereo energy even in
            // anti-phase material: mirrored power sums cancel cross terms.
            double magnitude = Math.Sqrt(Power(_fft[i]) + Power(_fft[Size - i])) / Size;
            double value = Math.Log(1 + 100 * magnitude);
            if (_hasPrevious) flux[band] += Math.Max(0, value - _previous[i]);
            _previous[i] = value;
        }
        for (int b = 0; b < 5; b++) flux[b] /= BandCounts[b];
        _frames.Add(new(_count - Size / 2, (float)flux[0], (float)flux[1],
            (float)flux[2], (float)flux[3], (float)flux[4]));
        _hasPrevious = true;
    }

    private static double Power(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
}
