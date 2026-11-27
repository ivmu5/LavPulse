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
    public TrackMusicalKey? MusicalKey { get; init; }
    public TrackMusicProfile? MusicProfile { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public TrackAnalysisTiming? Timing { get; init; }

    /// <summary>Reads normalized interleaved stereo PCM at 48 kHz and reduces it to a bounded set of display points.
    /// Nonfinite samples become zero and samples outside [-1,1] are clamped.</summary>
    public static TrackWaveform Analyze(IPcmSource source, CancellationToken token) =>
        Analyze(source, token, null);

    internal static TrackWaveform Analyze(IPcmSource source, CancellationToken token, TrackRhythmTrace? trace,
        bool parallelKey = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        token.ThrowIfCancellationRequested();
        // All cached frame coordinates and rhythm/profile calculations use the
        // playback rate. Reject an incompatible custom source instead of
        // producing silently mis-scaled BPM and timestamps.
        if (source.SampleRate != PlaybackFormat.SampleRate)
            throw new ArgumentException("Analysis requires 48 kHz stereo PCM.", nameof(source));
        using var pipeline = parallelKey ? new ParallelKeyAnalysis(source.SampleRate, token) : null;
        try
        {
            return AnalyzeCore(source, pipeline?.Token ?? token, trace, pipeline);
        }
        catch (OperationCanceledException) when (pipeline is not null && !token.IsCancellationRequested)
        {
            // The consumer may have cancelled the producer because it failed.
            // Await its original exception instead of reporting a user cancellation.
            pipeline.Join();
            throw;
        }
    }

    private static TrackWaveform AnalyzeCore(IPcmSource source, CancellationToken token,
        TrackRhythmTrace? trace, ParallelKeyAnalysis? pipeline)
    {
        const int maxPoints = 8192;
        const int channels = PcmMixer.ChannelCount;
        int framesPerPoint = Math.Max(1, source.SampleRate / 25);
        float bass = FilterCoefficient(source.SampleRate, 220);
        float treble = FilterCoefficient(source.SampleRate, 2400);
        var left = new BandFilter();
        var right = new BandFilter();
        var pcm = new float[4096 * channels];
        var points = new List<WaveformPoint>();
        Vector4 peak = Vector4.Zero;
        int framesInPoint = 0;
        // Rhythm uses 10 ms energy windows even when the 25 Hz display waveform is reduced.
        // Keep this envelope only during analysis; the persisted result contains beat/onset positions.
        int rhythmHopFrames = Math.Max(1, source.SampleRate / 100);
        int framesInRhythmHop = 0;
        Vector4 rhythmSum = Vector4.Zero;
        var rhythmEnergy = new List<RhythmEnergy>();
        var spectralFlux = new RhythmSpectralFlux();
        long totalFrames = 0;
        var key = pipeline is null ? new MusicalKeyAnalyzer(source.SampleRate) : null;
        var loudness = pipeline is null && source.SampleRate == PlaybackFormat.SampleRate
            ? new TrackLoudnessAnalyzer() : null;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            token.ThrowIfCancellationRequested();
            int frames = source.Read(pcm);
            token.ThrowIfCancellationRequested();
            if (frames == 0)
            {
                break;
            }

            if ((uint)frames > (uint)(pcm.Length / channels))
                throw new InvalidDataException("Аудиоисточник вернул некорректное количество кадров.");
            totalFrames += frames;
            // Custom PCM sources can bypass decoder validation. Prevent one
            // nonfinite sample from poisoning both recursive band filters.
            var block = pcm.AsSpan(0, frames * channels);
            PcmNormalization.Copy(block,block);
            spectralFlux.Add(block, token);
            loudness?.Add(block);
            if (pipeline is null) key!.Add(block, token);
            else pipeline.Submit(block);
            int offset = 0;
            while (offset < frames)
            {
                int count = Math.Min(frames - offset, framesPerPoint - framesInPoint);
                int end = (offset + count) * channels;
                for (int sample = offset * channels; sample < end; sample += channels)
                {
                    var leftBands = left.Process(pcm[sample], bass, treble);
                    var rightBands = right.Process(pcm[sample + 1], bass, treble);
                    var bands = Vector4.Max(leftBands, rightBands);
                    peak = Vector4.Max(peak, bands);
                    rhythmSum += bands;
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

                points.Add(ToPoint(peak));
                peak = Vector4.Zero;
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
            points.Add(ToPoint(peak));
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

        pipeline?.CompleteInput();
        double readMs = watch.Elapsed.TotalMilliseconds;
        var waveform = new TrackWaveform(points.AsReadOnly(), totalFrames, framesPerPoint);
        var rhythm = TrackRhythmAnalyzer.AnalyzeEnergy(rhythmEnergy, rhythmHopFrames, totalFrames, token, trace);
        if (trace is not null) trace.SpectralFlux = spectralFlux.Frames;
        rhythm = rhythm with { RhythmHypotheses = RhythmHypothesisAnalyzer.Analyze(spectralFlux.Frames, rhythm, totalFrames, token) };
        double rhythmMs = watch.Elapsed.TotalMilliseconds - readMs;
        var musicalKey = pipeline is null ? key!.Finish() : pipeline.Join();
        var localKeys = pipeline is null ? key!.FinishLocal() : pipeline.LocalKeys;
        // The incomplete final FFT window has no local tonal evidence.
        // Represent it explicitly rather than borrowing the global estimate.
        long keyEnd = localKeys.Length == 0 ? 0 : localKeys[^1].EndFrame;
        if (keyEnd < totalFrames && localKeys.Length < 8192)
            localKeys = [..localKeys, new TrackKeySection(keyEnd,totalFrames,null)];
        var measuredLoudness = pipeline is null ? loudness?.Finish() : pipeline.Loudness;
        var profile = new TrackMusicProfile
        {
            LocalKeyVersion = TrackMusicProfile.CurrentLocalKeyVersion,
            LocalKeys = localKeys,
            LoudnessVersion = measuredLoudness is null ? 0 : TrackLoudness.Version,
            Loudness = measuredLoudness
        };
        waveform = waveform with { Rhythm = rhythm, MusicalKey = musicalKey };
        profile = TrackMusicProfileAnalyzer.Derive(waveform, profile, token);
        token.ThrowIfCancellationRequested();
        double totalMs = watch.Elapsed.TotalMilliseconds;
        return waveform with { MusicProfile = profile, Timing = new(readMs, rhythmMs,
            totalMs - readMs - rhythmMs, totalMs) };
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
