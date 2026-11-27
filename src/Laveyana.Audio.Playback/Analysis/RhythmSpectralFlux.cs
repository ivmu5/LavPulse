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
    private static readonly Complex[] Twiddle = Enumerable.Range(0, Size / 2)
        .Select(i => Complex.FromPolarCoordinates(1, -Math.Tau * i / Size)).ToArray();
    private static readonly int[] Reverse = Enumerable.Range(0, Size).Select(i =>
    {
        int r = 0;
        for (int j = 1; j < Size; j <<= 1) { r = (r << 1) | (i & 1); i >>= 1; }
        return r;
    }).ToArray();
    private static readonly int[] BinBand = Enumerable.Range(0, Size / 2).Select(i =>
    {
        double hz = i * (double)PlaybackFormat.SampleRate / Size;
        return hz < 20 || hz >= 16000 ? -1 : hz < 60 ? 0 : hz < 150 ? 1 :
            hz < 400 ? 2 : hz < 2000 ? 3 : 4;
    }).ToArray();
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
            _fft[Reverse[i]] = new Complex(_left[source] * Window[i], _right[source] * Window[i]);
        }
        for (int length = 2; length <= Size; length <<= 1)
        {
            int half = length / 2, step = Size / length;
            for (int start = 0; start < Size; start += length)
                for (int j = 0; j < half; j++)
                {
                    Complex even = _fft[start + j], odd = _fft[start + j + half] * Twiddle[j * step];
                    _fft[start + j] = even + odd; _fft[start + j + half] = even - odd;
                }
        }
        Span<double> flux = stackalloc double[5];
        Span<int> count = stackalloc int[5];
        flux.Clear(); count.Clear();
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
            count[band]++;
        }
        for (int b = 0; b < 5; b++) flux[b] /= Math.Max(1, count[b]);
        _frames.Add(new(_count - Size / 2, (float)flux[0], (float)flux[1],
            (float)flux[2], (float)flux[3], (float)flux[4]));
        _hasPrevious = true;
    }

    private static double Power(Complex c) => c.Real * c.Real + c.Imaginary * c.Imaginary;
}
