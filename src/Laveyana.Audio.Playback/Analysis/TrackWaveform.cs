namespace Laveyana.Audio.Playback;

public readonly record struct WaveformPoint(float Peak, float Low, float Mid, float High)
{
    /// <summary>Merges the peaks of adjacent analysis intervals.</summary>
    public WaveformPoint Combine(WaveformPoint other) => new(
        Math.Max(Peak, other.Peak), Math.Max(Low, other.Low),
        Math.Max(Mid, other.Mid), Math.Max(High, other.High));
}

public sealed record TrackWaveform(IReadOnlyList<WaveformPoint> Points, long TotalFrames, int FramesPerPoint)
{
    /// <summary>Reads PCM in blocks and reduces it to a bounded set of display points.</summary>
    public static TrackWaveform Analyze(IPcmSource source, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        const int maxPoints = 8192;
        const int channels = PcmMixer.ChannelCount;
        int framesPerPoint = Math.Max(1, source.SampleRate / 25);
        float bass = FilterCoefficient(source.SampleRate, 220);
        float treble = FilterCoefficient(source.SampleRate, 2400);
        var left = new BandFilter();
        var right = new BandFilter();
        var pcm = new float[4096 * channels];
        var points = new List<WaveformPoint>();
        WaveformPoint point = default;
        int framesInPoint = 0;
        long totalFrames = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            int frames = source.Read(pcm);
            if (frames == 0)
            {
                break;
            }

            totalFrames += frames;
            for (int frame = 0; frame < frames; frame++)
            {
                int sample = frame * channels;
                point = point.Combine(left.Process(pcm[sample], bass, treble))
                    .Combine(right.Process(pcm[sample + 1], bass, treble));
                if (++framesInPoint == framesPerPoint)
                {
                    points.Add(point);
                    point = default;
                    framesInPoint = 0;
                    if (points.Count == maxPoints)
                    {
                        for (int i = 0; i < maxPoints / 2; i++)
                        {
                            points[i] = points[2 * i].Combine(points[2 * i + 1]);
                        }

                        points.RemoveRange(maxPoints / 2, maxPoints / 2);
                        framesPerPoint = checked(framesPerPoint * 2);
                    }
                }
            }
        }

        if (framesInPoint > 0)
        {
            points.Add(point);
        }

        if (totalFrames == 0)
        {
            throw new InvalidDataException("В MP3 нет аудиоданных.");
        }

        return new TrackWaveform(points.AsReadOnly(), totalFrames, framesPerPoint);
    }

    /// <summary>Computes the smoothing coefficient for a one-pole frequency split.</summary>
    private static float FilterCoefficient(int sampleRate, int cutoffHz)
        => 1 - MathF.Exp(-MathF.Tau * cutoffHz / sampleRate);

    private struct BandFilter
    {
        private float _low;
        private float _lowAndMid;

        /// <summary>Updates the filter state and returns the signal and band amplitudes.</summary>
        public WaveformPoint Process(float sample, float bass, float treble)
        {
            _low += bass * (sample - _low);
            _lowAndMid += treble * (sample - _lowAndMid);
            return new WaveformPoint(Math.Abs(sample), Math.Abs(_low),
                Math.Abs(_lowAndMid - _low), Math.Abs(sample - _lowAndMid));
        }
    }
}
