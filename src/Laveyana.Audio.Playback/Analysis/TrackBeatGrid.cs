namespace Laveyana.Audio.Playback;

/// <summary>Band energy averaged over a small, fixed PCM window; used only while analyzing a track.</summary>
internal readonly record struct RhythmEnergy(float Low, float Mid, float High);

/// <summary>An independently evaluated rhythmic region; gaps are deliberately left without grid marks.</summary>
public sealed record TrackTempoSection(long StartFrame, long EndFrame, double Bpm, float Confidence,
    int OnsetCount, int MatchedBeats)
{
    /// <summary>Observed faster pulse rate, not a verified time signature.</summary>
    public int SubdivisionFactor { get; init; } = 1;
    public double SubdivisionBpm => Bpm * SubdivisionFactor;
}

/// <summary>One local analysis window, including rejected windows for diagnostic export.</summary>
public sealed record TrackTempoWindow(long StartFrame, long EndFrame, double Bpm, float Confidence, int OnsetCount)
{
    /// <summary>A rejected window still reports its strongest tempo hypothesis for diagnostics.</summary>
    public bool Accepted { get; init; }
    public int SubdivisionFactor { get; init; } = 1;
    public double SubdivisionBpm => Bpm * SubdivisionFactor;
}

/// <summary>A percussive candidate preserved for diagnosing false detections and missing beats.</summary>
public readonly record struct TrackOnsetDiagnostic(long Frame, float Strength, float Accent);

/// <summary>Local pulse evidence. Confidence is a heuristic score, not a calibrated probability.</summary>
public readonly record struct TrackBeatEstimate(long Frame, float Confidence, int CorrectionFrames,
    bool MatchedOnset, bool EnergyBreak, bool Confirmed);

/// <summary>Estimated tempo, measured attacks and a variable-tempo sequence of beat positions.</summary>
/// <remarks>Beats are estimated musical pulses, not verified bar downbeats or phrase boundaries.</remarks>
public sealed record TrackBeatGrid(double Bpm, long FirstBeatFrame, float Confidence, long[] TransientFrames)
{
    /// <summary>Individually tracked beat positions (48 kHz PCM frame coordinates).</summary>
    public long[] BeatFrames { get; init; } = [];
    /// <summary>Unverified pulse predictions; never use as confirmed visual or mixing beats.</summary>
    public long[] PredictedBeatFrames { get; init; } = [];
    /// <summary>Evidence for individual predictions, including rejected regions.</summary>
    public TrackBeatEstimate[] BeatEstimates { get; init; } = [];
    public TrackTempoSection[] Sections { get; init; } = [];
    public TrackTempoWindow[] Windows { get; init; } = [];
    public int CandidateOnsets { get; init; }
    public TrackOnsetDiagnostic[] OnsetDiagnostics { get; init; } = [];
    public string AnalysisStatus { get; init; } = "";

    // A track can contain trustworthy rhythmic sections even if its intro/breakdown is unmetered.
    public bool HasBeatGrid => BeatFrames.Length >= 4 &&
        (Sections.Length > 0 || (Bpm >= 40 && Confidence >= .40f));

    /// <summary>Estimates the local BPM from neighboring beats instead of the track-wide median.</summary>
    public double BpmAt(long position)
    {
        if (!HasBeatGrid) return Bpm;
        // Binary-search disjoint rhythmic regions, then estimate tempo from
        // nearby intervals within the same section. Never cross a breakdown.
        int lo = 0, hi = Sections.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (Sections[mid].EndFrame <= position) lo = mid + 1;
            else hi = mid;
        }
        if (Sections.Length > 0)
        {
            if (lo == Sections.Length || position < Sections[lo].StartFrame) return 0;
            var section = Sections[lo];
            int sectionFirst = BeatIndex(section.StartFrame), end = BeatIndex(section.EndFrame);
            if (end - sectionFirst < 4) return section.Bpm;
            int center = Math.Clamp(BeatIndex(position), sectionFirst, end - 1);
            int from = Math.Clamp(center - 4, sectionFirst, Math.Max(sectionFirst, end - 9));
            int to = Math.Min(end - 1, from + 8);
            Span<long> intervals = stackalloc long[8];
            int count = 0;
            for (int n = from; n < to; n++)
            {
                long delta = BeatFrames[n + 1] - BeatFrames[n];
                if (delta > 0) intervals[count++] = delta;
            }
            if (count < 3) return section.Bpm;
            var valid = intervals[..count];
            valid.Sort();
            double median = (valid[(count - 1) / 2] + (double)valid[count / 2]) / 2;
            double total = 0;
            int inliers = 0;
            foreach (long interval in valid)
                if (Math.Abs(interval - median) <= .18 * median)
                { total += interval; inliers++; }
            if (inliers < 3 || total <= 0) return section.Bpm;
            double bpm = 60d * PlaybackFormat.SampleRate * inliers / total;
            return bpm >= 40 && bpm <= 240 && Math.Abs(bpm - section.Bpm) <= .18 * section.Bpm
                ? bpm : section.Bpm;
        }
        int index = Array.BinarySearch(BeatFrames, position);
        if (index < 0) index = ~index;
        int span = Math.Min(4, BeatFrames.Length - 1);
        int first = Math.Clamp(index - span / 2, 0, BeatFrames.Length - 1 - span);
        long distance = BeatFrames[first + span] - BeatFrames[first];
        return distance > 0 ? Math.Clamp(span * 60d * PlaybackFormat.SampleRate / distance, 40, 240) : Bpm;
    }

    /// <summary>Stable rounded display tempo: use the entire estimated section, not individual intervals.</summary>
    public int DisplayBpmAt(long position)
    {
        if (!HasBeatGrid) return 0;
        int lo=0,hi=Sections.Length;
        while(lo<hi)
        {
            int mid=lo+(hi-lo)/2;
            if(Sections[mid].EndFrame<=position) lo=mid+1;
            else hi=mid;
        }
        double bpm=Sections.Length==0 ? Bpm :
            lo<Sections.Length && Sections[lo].StartFrame<=position ? Sections[lo].Bpm : 0;
        return double.IsFinite(bpm) && bpm>=40 && bpm<=240 ? (int)Math.Round(bpm,MidpointRounding.AwayFromZero) : 0;
    }

    private int BeatIndex(long frame)
    {
        int index = Array.BinarySearch(BeatFrames, frame);
        return index < 0 ? ~index : index;
    }
}
