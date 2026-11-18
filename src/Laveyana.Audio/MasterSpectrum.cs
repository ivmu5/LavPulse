using System.Numerics;

namespace Laveyana.Audio;

/// <summary>Frequency levels of the actual stereo Master for a spectrum display.</summary>
public sealed class MasterSpectrum
{
    public const int WindowFrames = 4096;
    public const int HopFrames = 1024;
    public const float FloorDb = -75f;

    private const int Channels = PcmMixer.ChannelCount;

    private readonly object _snapshotGate = new();
    private readonly int _windowFrames;
    private readonly float[] _history;
    private readonly float[] _snapshot;
    private readonly float[] _hann;
    private readonly Complex[] _stereo;
    private readonly Complex[] _twiddles;
    private readonly int[] _reversed;
    private readonly (int First, int End)[] _bandBins;
    private readonly double _magnitudeScale;

    private int _writeOffset;
    private int _untilSnapshot;
    private long _renderedFrames;
    private long _publishedFrame;
    private long _readFrame;
    private int _requestedGeneration = 1;
    private int _capturingGeneration;
    private int _publishedGeneration;

    internal MasterSpectrum(int sampleRate, int barCount, int windowFrames)
    {
        if (windowFrames < HopFrames || !BitOperations.IsPow2(windowFrames))
        {
            throw new ArgumentOutOfRangeException(nameof(windowFrames), "The FFT window must be a power of two at least as large as the hop.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(barCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(barCount, windowFrames / 2);

        _windowFrames = windowFrames;
        _untilSnapshot = windowFrames;
        _history = new float[checked(windowFrames * Channels)];
        _snapshot = new float[_history.Length];
        _hann = new float[windowFrames];
        _stereo = new Complex[windowFrames];
        _twiddles = new Complex[windowFrames / 2];
        _reversed = new int[windowFrames];
        double windowSum = 0;
        for (int i = 0; i < _windowFrames; i++)
        {
            float coefficient = (float)(0.5 - 0.5 * Math.Cos(Math.Tau * i / _windowFrames));
            _hann[i] = coefficient;
            windowSum += coefficient;

            _reversed[i] = (_reversed[i >> 1] >> 1) | ((i & 1) * (_windowFrames / 2));
        }

        for (int i = 0; i < _twiddles.Length; i++)
        {
            _twiddles[i] = Complex.FromPolarCoordinates(1, -Math.Tau * i / _windowFrames);
        }

        _magnitudeScale = 2.0 / windowSum;
        _bandBins = new (int First, int End)[barCount];
        var bands = new FrequencyBand[barCount];

        double binWidth = (double)sampleRate / _windowFrames;
        double upperHz = Math.Min(Math.Max(20_000, 2 * binWidth), (double)sampleRate / 2);
        double lowerHz = Math.Max(binWidth, Math.Min(32, upperHz / 2));
        double ratio = upperHz / lowerHz;

        double startHz = lowerHz;
        for (int i = 0; i < barCount; i++)
        {
            double endHz = i == barCount - 1
                ? upperHz
                : lowerHz * Math.Pow(ratio, (double)(i + 1) / barCount);
            bands[i] = new FrequencyBand(startHz, endHz);

            // Narrow low-frequency bands can share the nearest FFT bin.
            int first = Math.Max(1, (int)Math.Ceiling(startHz / binWidth));
            int end = Math.Min(_windowFrames / 2, (int)Math.Ceiling(endHz / binWidth));
            if (end <= first)
            {
                first = Math.Clamp((int)Math.Round(Math.Sqrt(startHz * endHz) / binWidth), 1, _windowFrames / 2 - 1);
                end = first + 1;
            }

            _bandBins[i] = (first, end);
            startHz = endHz;
        }

        Bands = Array.AsReadOnly(bands);
    }

    /// <summary>Enables audio capture and FFT frames without interrupting the render clock.</summary>
    public void SetEnabled(bool enabled)
    {
        int generation;
        do
        {
            generation = Volatile.Read(ref _requestedGeneration);
            if ((generation & 1) == (enabled ? 1 : 0)) return;
        }
        while (Interlocked.CompareExchange(ref _requestedGeneration, unchecked(generation + 1), generation) != generation);
    }

    /// <summary>Logarithmically spaced frequency bands, from lowest to highest.</summary>
    public IReadOnlyList<FrequencyBand> Bands { get; }

    /// <summary>
    /// Copies levels for the newest available window into the first <c>Bands.Count</c>
    /// entries. The range 0–1 corresponds to the display scale FloorDb–0 dBFS.
    /// </summary>
    /// <remarks>
    /// Call from one consumer, such as a UI timer. A false result leaves levels unchanged.
    /// The frame position is the exclusive end of the window, measured from the start of rendering.
    /// The FFT runs on the calling thread.
    /// </remarks>
    public bool TryReadLevels(Span<float> levels, out long endFrame)
    {
        if (levels.Length < Bands.Count)
        {
            throw new ArgumentException("Provide one level per band.", nameof(levels));
        }

        lock (_snapshotGate)
        {
            endFrame = _publishedFrame;
            if (endFrame == _readFrame || _publishedGeneration != Volatile.Read(ref _requestedGeneration))
            {
                return false;
            }

            for (int frame = 0; frame < _windowFrames; frame++)
            {
                int sample = frame * Channels;
                int fftIndex = _reversed[frame];
                _stereo[fftIndex] = new Complex(_snapshot[sample] * _hann[frame],
                    _snapshot[sample + 1] * _hann[frame]);
            }

            _readFrame = endFrame;
        }

        Transform(_stereo);
        for (int band = 0; band < _bandBins.Length; band++)
        {
            (int first, int end) = _bandBins[band];
            double peakPower = 0;
            for (int bin = first; bin < end; bin++)
            {
                // A packed real stereo FFT stores the sum of channel powers in paired, mirrored bins.
                double power = (MagnitudeSquared(_stereo[bin]) + MagnitudeSquared(_stereo[_windowFrames - bin])) / 4;
                peakPower = Math.Max(peakPower, power);
            }

            double amplitude = Math.Sqrt(peakPower) * _magnitudeScale;
            double db = 20 * Math.Log10(amplitude);
            levels[band] = (float)Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
        }

        return true;
    }

    /// <summary>Keeps a rolling history of the master and publishes a window at each analysis hop.</summary>
    internal void Capture(ReadOnlySpan<float> master)
    {
        int generation = Volatile.Read(ref _requestedGeneration);
        if ((generation & 1) == 0)
        {
            _renderedFrames += master.Length / Channels;
            return;
        }
        if (_capturingGeneration != generation)
        {
            _capturingGeneration = generation;
            _writeOffset = 0;
            _untilSnapshot = _windowFrames;
        }

        while (!master.IsEmpty)
        {
            int frames = Math.Min(master.Length / Channels, _untilSnapshot);
            int samples = frames * Channels;
            int tail = Math.Min(samples, _history.Length - _writeOffset);

            master[..tail].CopyTo(_history.AsSpan(_writeOffset));
            master[tail..samples].CopyTo(_history);
            _writeOffset = (_writeOffset + samples) % _history.Length;
            _renderedFrames += frames;
            _untilSnapshot -= frames;
            master = master[samples..];

            if (_untilSnapshot == 0)
            {
                PublishSnapshot();
                _untilSnapshot = HopFrames;
            }
        }
    }

    /// <summary>Copies a complete window if the spectrum reader is ready without blocking the audio writer.</summary>
    private void PublishSnapshot()
    {
        // An active reader may skip a snapshot. Rendering keeps its own continuous history.
        if (!Monitor.TryEnter(_snapshotGate))
        {
            return;
        }

        try
        {
            _history.AsSpan(_writeOffset).CopyTo(_snapshot);
            _history.AsSpan(0, _writeOffset).CopyTo(_snapshot.AsSpan(_history.Length - _writeOffset));
            _publishedGeneration = _capturingGeneration;
            _publishedFrame = _renderedFrames;
        }
        finally
        {
            Monitor.Exit(_snapshotGate);
        }
    }

    /// <summary>Runs the in-place radix-two FFT on an input already ordered by reversed bits.</summary>
    private void Transform(Span<Complex> values)
    {
        // Input is already in bit-reversed order.
        for (int size = 2; size <= values.Length; size *= 2)
        {
            int half = size / 2;
            int step = values.Length / size;
            for (int start = 0; start < values.Length; start += size)
            {
                for (int offset = 0; offset < half; offset++)
                {
                    int evenIndex = start + offset;
                    int oddIndex = evenIndex + half;
                    Complex even = values[evenIndex];
                    Complex odd = values[oddIndex] * _twiddles[offset * step];

                    values[evenIndex] = even + odd;
                    values[oddIndex] = even - odd;
                }
            }
        }
    }

    /// <summary>Computes bin power without performing a square root.</summary>
    private static double MagnitudeSquared(Complex value)
    {
        return value.Real * value.Real + value.Imaginary * value.Imaginary;
    }
}
