using System.Numerics;

namespace Laveyana.Audio.Playback;

/// <summary>Streaming chroma estimator. An independent FFT of each ~0.68 s interval
/// uses packed stereo (no cancellation of anti-phase channels). Only a bounded
/// window and a 12-bin aggregate are retained. No second decode or PCM copy.</summary>
internal sealed class MusicalKeyAnalyzer
{
    private const int Size = 8192;
    private const int Decimation = 4;
    private static readonly double[] Major = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] Minor = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];
    private static readonly double[][] Profiles = [Normalize(Major), Normalize(Minor)];
    private static readonly double[] Hann = Enumerable.Range(0, Size)
        .Select(i => .5 - .5 * Math.Cos(Math.Tau * i / Size)).ToArray();
    private static readonly int[] Reversed = BuildReversed();
    private static readonly Complex[] Twiddles = Enumerable.Range(0, Size / 2)
        .Select(i => Complex.FromPolarCoordinates(1, -Math.Tau * i / Size)).ToArray();

    private readonly int _rate;
    private readonly double _coefficient;
    private readonly Complex[] _fft = new Complex[Size];
    private readonly double[] _power = new double[Size / 2];
    private readonly double[] _chroma = new double[12];
    private readonly double[] _aggregate = new double[12];
    private readonly double[] _local = new double[12];
    private readonly List<TrackKeySection> _localKeys = new();
    private const int LocalWindows = 12;
    private int _localTotal, _localTonal;
    private long _localStart;
    private double _left1, _left2, _right1, _right2;
    private int _skip, _count, _total, _tonal;

    public MusicalKeyAnalyzer(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _rate = sampleRate;
        _coefficient = 1 - Math.Exp(-Math.Tau * Math.Min(2200, sampleRate / 12d) / sampleRate);
    }

    public void Add(ReadOnlySpan<float> stereo, CancellationToken token)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            float left = stereo[i], right = stereo[i + 1];
            double a = float.IsFinite(left) ? left : 0, b = float.IsFinite(right) ? right : 0;
            _left1 += _coefficient * (a - _left1);
            _left2 += _coefficient * (_left1 - _left2);
            _right1 += _coefficient * (b - _right1);
            _right2 += _coefficient * (_right1 - _right2);
            if (++_skip != Decimation) continue;
            _skip = 0;
            _fft[Reversed[_count]] = new Complex(_left2 * Hann[_count], _right2 * Hann[_count]);
            if (++_count == Size)
            {
                token.ThrowIfCancellationRequested();
                AnalyzeWindow();
                _localTotal++;
                if (_localTotal == LocalWindows) FlushLocal();
                _count = 0;
            }
        }
    }

    private void AnalyzeWindow()
    {
        _total++;
        Transform();
        double binHz = _rate / (double)(Decimation * Size);
        int first = Math.Max(2, (int)Math.Ceiling(55 / binHz));
        int end = Math.Min(Size / 2 - 1, (int)Math.Floor(2200 / binHz));
        if (end <= first) return;
        double total = 0, maximum = 0;
        for (int i = first - 1; i <= end; i++)
        {
            double p = (Power(_fft[i]) + Power(_fft[Size - i])) * .5;
            _power[i] = p;
            if (i >= first && i < end) { total += p; maximum = Math.Max(maximum, p); }
        }
        if (total < .001 || maximum < total * .012) return;
        Array.Clear(_chroma);
        double concentrated = 0, sum = 0;
        for (int i = first; i < end; i++)
        {
            double p = _power[i];
            if (p < maximum * .005 || p <= _power[i - 1] || p < _power[i + 1]) continue;
            double a = Math.Log(Math.Max(1e-30, _power[i - 1]));
            double b = Math.Log(Math.Max(1e-30, p));
            double c = Math.Log(Math.Max(1e-30, _power[i + 1]));
            double denominator = a - 2 * b + c;
            double offset = Math.Abs(denominator) > 1e-12 ? Math.Clamp(.5 * (a - c) / denominator, -.5, .5) : 0;
            double hz = (i + offset) * binHz;
            double note = 69 + 12 * Math.Log2(hz / 440);
            int nearest = (int)Math.Round(note);
            double proximity = 1 - 2 * Math.Abs(note - nearest);
            if (proximity < .25) continue;
            int pitch = ((nearest % 12) + 12) % 12;
            double weight = Math.Sqrt(p) * proximity * (hz < 220 ? 1.2 : 1);
            _chroma[pitch] += weight;
            sum += weight;
            concentrated += _power[i - 1] + p + _power[i + 1];
        }
        if (sum <= 0 || concentrated < total * .35 || !double.IsFinite(sum)) return;
        _tonal++;
        _localTonal++;
        for (int i = 0; i < 12; i++)
        {
            double value = _chroma[i] / sum;
            _aggregate[i] += value;
            _local[i] += value;
        }
    }

    public TrackMusicalKey? Finish() => Estimate(_aggregate, _tonal, _total);

    private void FlushLocal()
    {
        long end = (long)_total * Size * Decimation;
        if (_localKeys.Count < 8192 && end > _localStart)
            _localKeys.Add(new(_localStart, end, Estimate(_local, _localTonal, _localTotal)));
        _localStart = end;
        _localTotal = _localTonal = 0;
        Array.Clear(_local);
    }

    public TrackKeySection[] FinishLocal()
    {
        if (_localTotal > 0) FlushLocal();
        return _localKeys.ToArray();
    }

    private static TrackMusicalKey? Estimate(double[] chroma, int tonal, int total)
    {
        if (tonal < 3 || tonal < total * .15) return null;
        double largest = chroma.Max();
        if (chroma.Count(v => v >= largest * .12) < 3) return null;
        var normalized = Normalize(chroma);
        double best = -2, second = -2;
        int tonic = 0, mode = 0;
        for (int m = 0; m < 2; m++)
            for (int root = 0; root < 12; root++)
            {
                double score = 0;
                for (int i = 0; i < 12; i++) score += normalized[(root + i) % 12] * Profiles[m][i];
                if (score > best) { second = best; best = score; tonic = root; mode = m; }
                else if (score > second) second = score;
            }
        double margin = best - second;
        // Conservative gates: an unknown key is preferable to false certainty.
        if (best < .55 || margin < .045) return null;
        float confidence = (float)(Math.Clamp((best - .45) / .55, 0, 1) *
            Math.Clamp(margin / .20, 0, 1) * Math.Min(1, tonal / 8d));
        return new(tonic, (MusicalMode)mode, confidence, (float)Math.Clamp(best, -1, 1),
            (float)Math.Clamp(margin, 0, 2), tonal, total);
    }

    private static double[] Normalize(double[] values)
    {
        double mean = values.Average();
        var result = values.Select(v => v - mean).ToArray();
        double scale = Math.Sqrt(result.Sum(v => v * v));
        if (scale > 0) for (int i = 0; i < result.Length; i++) result[i] /= scale;
        return result;
    }

    private static int[] BuildReversed()
    {
        var result = new int[Size];
        for (int i = 1; i < Size; i++) result[i] = (result[i >> 1] >> 1) | ((i & 1) * (Size / 2));
        return result;
    }

    private void Transform()
    {
        for (int size = 2; size <= Size; size *= 2)
        {
            int half = size / 2, step = Size / size;
            for (int start = 0; start < Size; start += size)
                for (int i = 0; i < half; i++)
                {
                    Complex even = _fft[start + i], odd = _fft[start + i + half] * Twiddles[i * step];
                    _fft[start + i] = even + odd;
                    _fft[start + i + half] = even - odd;
                }
        }
    }

    private static double Power(Complex value) => value.Real * value.Real + value.Imaginary * value.Imaginary;
}
