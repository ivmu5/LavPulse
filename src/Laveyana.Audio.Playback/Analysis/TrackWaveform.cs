using System.Numerics;

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
    /// <summary>Estimated musical rhythm and detected transients, produced by the analysis pass.</summary>
    public TrackBeatGrid? Rhythm { get; init; }

    /// <summary>Reads PCM in blocks and reduces it to a bounded set of display points.</summary>
    public static TrackWaveform Analyze(IPcmSource source, CancellationToken token) =>
        Analyze(source, token, null);

    internal static TrackWaveform Analyze(IPcmSource source, CancellationToken token, TrackRhythmTrace? trace)
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
        Vector4 leftPeak = Vector4.Zero;
        Vector4 rightPeak = Vector4.Zero;
        int framesInPoint = 0;
        // Rhythm uses 10 ms energy windows even when the 25 Hz display waveform is reduced.
        // Keep this envelope only during analysis; the persisted result contains beat/onset positions.
        int rhythmHopFrames = Math.Max(1, source.SampleRate / 100);
        int framesInRhythmHop = 0;
        Vector4 rhythmSum = Vector4.Zero;
        var rhythmEnergy = new List<RhythmEnergy>();
        long totalFrames = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            int frames = source.Read(pcm);
            if (frames == 0)
            {
                break;
            }

            if ((uint)frames > (uint)(pcm.Length / channels))
                throw new InvalidDataException("Аудиоисточник вернул некорректное количество кадров.");
            totalFrames += frames;
            int offset = 0;
            while (offset < frames)
            {
                int count = Math.Min(frames - offset, framesPerPoint - framesInPoint);
                int end = (offset + count) * channels;
                for (int sample = offset * channels; sample < end; sample += channels)
                {
                    var leftBands = left.Process(pcm[sample], bass, treble);
                    var rightBands = right.Process(pcm[sample + 1], bass, treble);
                    leftPeak = Vector4.Max(leftPeak, leftBands);
                    rightPeak = Vector4.Max(rightPeak, rightBands);
                    rhythmSum += Vector4.Max(leftBands, rightBands);
                    if (++framesInRhythmHop == rhythmHopFrames)
                    {
                        float inverse = 1f / framesInRhythmHop;
                        rhythmEnergy.Add(new RhythmEnergy(rhythmSum.Y * inverse, rhythmSum.Z * inverse,
                            rhythmSum.W * inverse));
                        rhythmSum = Vector4.Zero;
                        framesInRhythmHop = 0;
                    }
                }
                offset += count;
                framesInPoint += count;
                if (framesInPoint != framesPerPoint) continue;

                points.Add(ToPoint(Vector4.Max(leftPeak, rightPeak)));
                leftPeak = rightPeak = Vector4.Zero;
                framesInPoint = 0;
                if (points.Count != maxPoints) continue;

                for (int i = 0; i < maxPoints / 2; i++)
                {
                    points[i] = points[2 * i].Combine(points[2 * i + 1]);
                }
                points.RemoveRange(maxPoints / 2, maxPoints / 2);
                framesPerPoint = checked(framesPerPoint * 2);
            }
        }

        if (framesInPoint > 0)
        {
            points.Add(ToPoint(Vector4.Max(leftPeak, rightPeak)));
        }
        if (framesInRhythmHop > 0)
        {
            float inverse = 1f / framesInRhythmHop;
            rhythmEnergy.Add(new RhythmEnergy(rhythmSum.Y * inverse, rhythmSum.Z * inverse,
                rhythmSum.W * inverse));
        }

        if (totalFrames == 0)
        {
            throw new InvalidDataException("В файле нет аудиоданных.");
        }

        var waveform = new TrackWaveform(points.AsReadOnly(), totalFrames, framesPerPoint);
        return waveform with
        {
            Rhythm = TrackRhythmAnalyzer.AnalyzeEnergy(rhythmEnergy, rhythmHopFrames, totalFrames, token, trace)
        };
    }

    private static WaveformPoint ToPoint(Vector4 peak) => new(peak.X, peak.Y, peak.Z, peak.W);

    /// <summary>Computes the smoothing coefficient for a one-pole frequency split.</summary>
    private static float FilterCoefficient(int sampleRate, int cutoffHz)
        => 1 - MathF.Exp(-MathF.Tau * cutoffHz / sampleRate);

    private struct BandFilter
    {
        private float _low;
        private float _lowAndMid;

        /// <summary>Updates the filter state and returns the signal and band amplitudes.</summary>
        public Vector4 Process(float sample, float bass, float treble)
        {
            _low += bass * (sample - _low);
            _lowAndMid += treble * (sample - _lowAndMid);
            return Vector4.Abs(new Vector4(sample, _low, _lowAndMid - _low, sample - _lowAndMid));
        }
    }
}
