using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Indicative BPM from human taps, never a replacement for the audio-analysis BeatGrid.</summary>
internal readonly record struct ManualTapTempoSection(long StartFrame, long EndFrame, double? EstimatedBpm,
    int TapCount, int InferredMissedBeats);

internal static class ManualTapTempoAnalyzer
{
    private const double MaximumGapSeconds = 3.2;
    private const int MaxTapsPerWindow = 16;

    public static ManualTapTempoSection[] Analyze(IReadOnlyList<ManualBeatTap> taps)
    {
        if (taps.Count == 0) return [];
        var sections = new List<ManualTapTempoSection>();
        var run = new List<ManualBeatTap>();
        ManualBeatTap? previous = null;
        foreach (var tap in taps.OrderBy(t => t.PressedAtUtc))
        {
            bool separated = previous is { } earlier &&
                (tap.Frame <= earlier.Frame ||
                 tap.Frame - earlier.Frame > MaximumGapSeconds * PlaybackFormat.SampleRate ||
                 tap.PressedAtUtc - earlier.PressedAtUtc > TimeSpan.FromSeconds(5));
            if (separated) FinishRun();
            run.Add(tap);
            previous = tap;
        }
        FinishRun();
        return sections.ToArray();

        void FinishRun()
        {
            if (run.Count == 0) return;
            for (int from = 0; from < run.Count;)
            {
                int end = Math.Min(from + MaxTapsPerWindow, run.Count);
                var chunk = run.GetRange(from, end - from);
                var (bpm, missing) = Estimate(chunk);
                sections.Add(new(chunk[0].Frame, chunk[^1].Frame, bpm, chunk.Count, missing));
                if (end == run.Count) break;
                from = end - 1; // Preserve the connecting interval between adjacent tempo windows.
            }
            run.Clear();
        }
    }

    private static (double? Bpm, int Missing) Estimate(IReadOnlyList<ManualBeatTap> taps)
    {
        if (taps.Count < 4) return (null, 0);
        double bestScore = double.PositiveInfinity;
        double? bestBpm = null;
        int bestSkipped = 0;
        for (double bpm = 70; bpm <= 200; bpm += .5)
        {
            double period = 60d / bpm;
            double cost = 0;
            int aligned = 0, missed = 0;
            for (int i = 1; i < taps.Count; i++)
            {
                double interval = (taps[i].Frame - taps[i - 1].Frame) / (double)PlaybackFormat.SampleRate;
                int beats = Math.Clamp((int)Math.Round(interval / period), 1, 4);
                double error = Math.Abs(interval / period - beats);
                cost += Math.Min(1, error * error * 5) + (beats - 1) * .025;
                if (error <= .25) aligned++;
                missed += beats - 1;
            }
            if (aligned < Math.Max(2, (int)Math.Ceiling((taps.Count - 1) * .65))) continue;
            if (cost >= bestScore) continue;
            bestScore = cost;
            bestBpm = bpm;
            bestSkipped = missed;
        }
        if (bestBpm is null) return (null, 0);
        // Report a descriptive estimate only: human timing reaction and output codec
        // latency mean this cannot establish frame-accurate positions.
        return (bestBpm, bestSkipped);
    }
}
