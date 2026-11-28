using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Laveyana.Audio.Playback;

/// <summary>Replays private diagnostic envelopes locally; never uploads audio or reports.</summary>
internal static class RhythmReportReplay
{
    public static void Run(string path)
    {
        Console.WriteLine($"Report: {Path.GetFileName(path)}");
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith("/diagnostics/energy.csv", StringComparison.Ordinal)))
        {
            string prefix = entry.FullName[..^"diagnostics/energy.csv".Length];
            using var reader = new StreamReader(entry.Open());
            if (reader.ReadLine() != "frame,low,mid,high") throw new InvalidDataException("Unexpected energy schema.");
            var energy = new List<RhythmEnergy>();
            int hop = 0;
            while (reader.ReadLine() is { } line)
            {
                string[] cells = line.Split(',');
                long frame = long.Parse(cells[0], CultureInfo.InvariantCulture);
                if (energy.Count == 1) hop = checked((int)frame);
                if (energy.Count > 0 && (hop <= 0 || frame != (long)energy.Count * hop))
                    throw new InvalidDataException("Nonuniform envelope.");
                energy.Add(new(float.Parse(cells[1], CultureInfo.InvariantCulture),
                    float.Parse(cells[2], CultureInfo.InvariantCulture), float.Parse(cells[3], CultureInfo.InvariantCulture)));
            }
            if (hop <= 0) continue;
            long total = (long)energy.Count * hop;
            var metadata = zip.GetEntry(prefix + "diagnostics/metadata.txt");
            if (metadata is not null)
            {
                using var metaReader = new StreamReader(metadata.Open());
                string meta = metaReader.ReadToEnd();
                var match = System.Text.RegularExpressions.Regex.Match(meta, @"total PCM frames: (\d+)");
                if (match.Success && long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out long exact) &&
                    exact > total - hop && exact <= total) total = exact;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var grid = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, total);
            if (zip.GetEntry(prefix + "diagnostics/spectral-flux.csv") is { } spectrum)
            {
                using var spectralReader = new StreamReader(spectrum.Open());
                if (spectralReader.ReadLine() != "frame,sub_20_60,bass_60_150,low_mid_150_400,mid_400_2000,high_2000_16000")
                    throw new InvalidDataException("Unexpected spectral flux schema.");
                var flux = new List<RhythmSpectrumFrame>();
                while (spectralReader.ReadLine() is { } row)
                {
                    string[] cells = row.Split(',');
                    if (cells.Length != 6 || flux.Count >= RhythmSpectralFlux.MaxFrames)
                        throw new InvalidDataException("Invalid spectral flux row.");
                    long frame = long.Parse(cells[0], CultureInfo.InvariantCulture);
                    float Band(int i) => float.Parse(cells[i], CultureInfo.InvariantCulture);
                    var item = new RhythmSpectrumFrame(frame, Band(1), Band(2), Band(3), Band(4), Band(5));
                    if (frame < 0 || frame >= total ||
                        flux.Count > 0 && frame - flux[^1].Frame != RhythmSpectralFlux.Hop ||
                        Enumerable.Range(0,5).Any(b => !float.IsFinite(item.Band(b)) || item.Band(b) < 0))
                        throw new InvalidDataException("Invalid spectral flux sequence.");
                    flux.Add(item);
                }
                grid = grid with { RhythmHypotheses = RhythmHypothesisAnalyzer.Analyze(flux, grid, total, default) };
            }
            RhythmAuditResult audit = RhythmAuditResult.Empty;
            if (zip.GetEntry(prefix+"diagnostics/audit-flux.csv") is {} auditEntry)
            {
                using var auditReader = new StreamReader(auditEntry.Open());
                if (auditReader.ReadLine()!="frame,band_30_100,band_100_220,band_220_600,band_600_2000,band_2000_7900")
                    throw new InvalidDataException("Unknown audit flux schema.");
                var rawAudit = new List<RhythmSpectrumFrame>();
                while (auditReader.ReadLine() is {} row)
                {
                    string[] fields = row.Split(',');
                    if (fields.Length!=6 || rawAudit.Count>=RhythmAuditFlux.MaxFrames)
                        throw new InvalidDataException("Invalid audit flux.");
                    float Band(int i)=>float.Parse(fields[i],CultureInfo.InvariantCulture);
                    rawAudit.Add(new(long.Parse(fields[0],CultureInfo.InvariantCulture),Band(1),Band(2),Band(3),Band(4),Band(5)));
                }
                audit = RhythmJointDecoder.Analyze(rawAudit,total,default,energy,hop);
            }
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (!TrackRhythmAnalyzer.IsValid(grid, total)) throw new InvalidDataException("Invalid new grid.");
            using var annotations = JsonDocument.Parse(zip.GetEntry(prefix + "annotations.json")!.Open());
            var report = annotations.RootElement[0];
            var taps = report.GetProperty("BeatTaps").EnumerateArray().Select(t => t.GetProperty("Frame").GetInt64()).ToArray();
            var oldBeats = ReadBeats(zip.GetEntry(prefix + "diagnostics/beats.csv")!);
            Array.Sort(taps);
            int Close(long[] beats) => MatchOneToOne(beats, taps, PlaybackFormat.SampleRate / 10);
            int Close180(long[] beats) => MatchOneToOne(beats, taps, PlaybackFormat.SampleRate * 180 / 1000);
            long oldCovered = 0;
            if (zip.GetEntry(prefix + "diagnostics/sections.csv") is { } sectionEntry)
            {
                using var sectionReader = new StreamReader(sectionEntry.Open());
                string[] header = (sectionReader.ReadLine() ?? "").Split(',');
                int startColumn = Array.IndexOf(header, "start_frame"), endColumn = Array.IndexOf(header, "end_frame_exclusive");
                if (startColumn >= 0 && endColumn >= 0)
                    while (sectionReader.ReadLine() is { } sectionLine)
                    {
                        var columns = sectionLine.Split(',');
                        oldCovered += long.Parse(columns[endColumn], CultureInfo.InvariantCulture) -
                            long.Parse(columns[startColumn], CultureInfo.InvariantCulture);
                    }
            }
            if (grid.RhythmHypotheses.Length > 0)
            {
                var preferred = grid.RhythmHypotheses.Where(h => h.IsPreferred).SelectMany(h => h.CandidateFrames)
                    .Order().Distinct().ToArray();
                var familyPreferred = grid.RhythmHypotheses.Where(h => h.IsFamilyPreferred)
                    .SelectMany(h => h.CandidateFrames).Order().Distinct().ToArray();
                var localPreferred = grid.RhythmHypotheses.Where(h => h.IsLocalPreferred && !h.IsRecoveryCandidate)
                    .SelectMany(h => h.CandidateFrames).Order().Distinct().ToArray();
                Console.WriteLine(FormattableString.Invariant(
                    $"  spectral hypotheses {grid.RhythmHypotheses.Length}, local candidate taps 100/180ms {Close(localPreferred)}/{Close180(localPreferred)}, global {Close(preferred)}/{Close180(preferred)} of {taps.Length} (unconfirmed)"));
                Console.WriteLine(FormattableString.Invariant(
                    $"  selected blocks {grid.RhythmHypotheses.Count(h => h.IsPreferred)}, recovered weak blocks {grid.RhythmHypotheses.Count(h => h.IsPreferred && h.IsRecoveryCandidate)}"));
                Console.WriteLine(FormattableString.Invariant(
                    $"  harmonic family taps 100/180ms {Close(familyPreferred)}/{Close180(familyPreferred)} of {taps.Length}; reference blocks {grid.RhythmHypotheses.Count(h=>h.IsFamilyPreferred && h.IsReferenceCandidate)}"));
                if (taps.Length >= 2)
                {
                    long spectralTolerance = PlaybackFormat.SampleRate * 180 / 1000;
                    var localMetrics = Metrics(localPreferred, taps, spectralTolerance);
                    var globalMetrics = Metrics(preferred, taps, spectralTolerance);
                    var familyMetrics = Metrics(familyPreferred, taps, spectralTolerance);
                    Console.WriteLine(FormattableString.Invariant(
                        $"  spectral annotated-span P/R/F1 at 180ms: local {localMetrics.P:F3}/{localMetrics.R:F3}/{localMetrics.F1:F3}, global {globalMetrics.P:F3}/{globalMetrics.R:F3}/{globalMetrics.F1:F3}, family {familyMetrics.P:F3}/{familyMetrics.R:F3}/{familyMetrics.F1:F3}"));
                }
            }
            else Console.WriteLine("  No spectral hypotheses; old report or insufficient evidence.");
            var comparisons = new List<(string Name,long[] Frames)>
            {
                ("exported-confirmed",oldBeats),("replayed-confirmed",grid.BeatFrames),
                ("local",grid.RhythmHypotheses.Where(h=>h.IsLocalPreferred&&!h.IsRecoveryCandidate)
                    .SelectMany(h=>h.CandidateFrames).Order().Distinct().ToArray()),
                ("global",grid.RhythmHypotheses.Where(h=>h.IsPreferred)
                    .SelectMany(h=>h.CandidateFrames).Order().Distinct().ToArray()),
                ("family",grid.RhythmHypotheses.Where(h=>h.IsFamilyPreferred)
                    .SelectMany(h=>h.CandidateFrames).Order().Distinct().ToArray())
            };
            foreach (var (name,auditPath) in new[] { ("audit-baseline",audit.Baseline),("joint",audit.Joint),("joint-alternative",audit.Alternative) }
                .Concat(audit.EnvelopePaths.Select(p=>(p.Name,p.Path))))
            {
                if (auditPath.States.Length==0) continue;
                comparisons.Add((name,auditPath.Beats.Select(b=>b.Frame).ToArray()));
                Console.WriteLine($"  {name} observed low-band attacks {auditPath.Beats.Count(b=>b.Observed)}/{auditPath.Beats.Length}");
            }
            foreach(var lane in audit.MetricPaths)
            {
                string name=$"metric-{lane.Numerator}-{lane.Denominator}-lane{lane.Lane}";
                comparisons.Add((name,lane.Path.Beats.Select(b=>b.Frame).ToArray()));
                Console.WriteLine(FormattableString.Invariant(
                    $"  {name} tempo/phase evidence {lane.MeanTempoEvidence:F3}/{lane.MeanPhaseEvidence:F3}"));
            }
            foreach(var lane in audit.RefinedMetricPaths)
            {
                string name=$"refined-metric-{lane.Numerator}-{lane.Denominator}-lane{lane.Lane}";
                comparisons.Add((name,lane.Path.Beats.Select(b=>b.Frame).ToArray()));
            }
            if(audit.MetricBudgetReached||audit.RefinedMetricBudgetReached)
                Console.WriteLine("  Metric path budget reached; whole remaining families omitted.");
            if(audit.RoleSelection.Decisions.Length>0)
            {
                comparisons.Add(("role-selector",audit.RoleSelection.Beats.Select(b=>b.Beat.Frame).ToArray()));
                Console.WriteLine($"  role events {audit.RoleSelection.Events.Length}; switched blocks {audit.RoleSelection.Decisions.Count(d=>d.SelectedPathId>=0)}; ambiguous blocks {audit.RoleSelection.Decisions.Count(d=>d.Ambiguous)}");
            }
            foreach(var pair in RhythmPhaseAgreement.Analyze(audit,grid.BeatFrames,total,default).GroupBy(r=>(r.Left,r.Right)))
                Console.WriteLine($"  phase {pair.Key.Left}/{pair.Key.Right}: "+
                    string.Join(", ",pair.GroupBy(r=>r.Status).Select(g=>$"{g.Key}={g.Count()}")));
            var templates=RhythmTemplateAnalyzer.Analyze(audit,grid.BeatFrames,default);
            foreach(var evidence in templates.Evidence)
                Console.WriteLine(FormattableString.Invariant(
                    $"  template {evidence.Name} fold {evidence.TestFold} coverage {evidence.Coverage:F3} phase/2/3/4 gain {evidence.PhaseGain:F3}/{evidence.TwoGain:F3}/{evidence.ThreeGain:F3}/{evidence.FourGain:F3}; conditional 4 {evidence.ConditionalFourGain:F3}; soft 4 {evidence.SoftFourGain:F3}"));
            foreach(var e in templates.TransitionEvidence)
                Console.WriteLine(FormattableString.Invariant(
                    $"  transition {e.Name} fold {e.TestFold}: gain {e.Gain:F4}, increment {e.Increment:F4}, pairs {e.TestPairs}/{e.TestEvents}"));
            if(templates.SoftBlockComparisons.Length>0)
                Console.WriteLine($"  soft paired blocks: {templates.SoftBlockComparisons.Count(e=>e.Difference>0)}/{templates.SoftBlockComparisons.Length} favor ranked");
            if(templates.Reference.Length>0)
            {
                comparisons.Add(("template-ranked",templates.RankedBeats));
                comparisons.Add(("template-guarded",templates.GuardedBeats));
                comparisons.Add(("template-conditional-ranked",templates.ConditionalRankedBeats));
                comparisons.Add(("template-conditional-guarded",templates.ConditionalGuardedBeats));
                comparisons.Add(("template-soft-ranked",templates.SoftRankedBeats));
                comparisons.Add(("template-soft-guarded",templates.SoftGuardedBeats));
                Console.WriteLine($"  soft rank {templates.SoftRanked}; guarded {templates.SoftGuarded}");
                Console.WriteLine($"  conditional rank {templates.ConditionalRanked}; guarded {templates.ConditionalGuarded}");
                Console.WriteLine($"  template reference {templates.Reference}; rank {templates.Ranked}; guarded {templates.Guarded}");
            }
            if (taps.Length>0)
                foreach (var (name,frames) in comparisons)
                    foreach (int ms in new[]{30,50,70,100,180})
                    {
                        long allowed=PlaybackFormat.SampleRate*ms/1000;
                        var metric=SeriesMetrics(frames,taps,allowed);
                        double nullF1=PhaseNull(frames,taps,allowed);
                        Console.WriteLine(FormattableString.Invariant(
                            $"  {name} {ms}ms series: P/R/F1 {metric.P:F3}/{metric.R:F3}/{metric.F1:F3}; phase-null {nullF1:F3}, beats {frames.Length}"));
                    }
            int ClosePredicted = Close(grid.PredictedBeatFrames);
            long covered = grid.Sections.Sum(s => s.EndFrame - s.StartFrame);
            Console.WriteLine(FormattableString.Invariant(
                $"{report.GetProperty("Title").GetString()}: BPM={grid.Bpm:F1}; coverage={covered / (double)total:P1}; beats {oldBeats.Length}->{grid.BeatFrames.Length}; one-to-one taps within 100ms {Close(oldBeats)}->{Close(grid.BeatFrames)}/{taps.Length}; predictions {ClosePredicted}/{taps.Length}"));
            Console.WriteLine(FormattableString.Invariant($"  coverage {oldCovered/(double)total:P1}->{covered/(double)total:P1}; one-to-one taps within 180ms {Close180(oldBeats)}->{Close180(grid.BeatFrames)}/{taps.Length}"));
            Console.WriteLine(FormattableString.Invariant($"  rhythm replay {watch.Elapsed.TotalMilliseconds:F1} ms; allocated {allocated / 1048576d:F2} MiB"));
            if (taps.Length >= 2)
            {
                // Manual taps cover excerpts, not necessarily the whole song.
                // Restrict prediction precision to the annotated span, and
                // label it as such: sparse/subdivision taps can still bias it.
                long tolerance = PlaybackFormat.SampleRate / 10;
                var oldMetrics = Metrics(oldBeats, taps, tolerance);
                var newMetrics = Metrics(grid.BeatFrames, taps, tolerance);
                Console.WriteLine(FormattableString.Invariant($"  annotated-span P/R/F1: {oldMetrics.P:F3}/{oldMetrics.R:F3}/{oldMetrics.F1:F3} -> {newMetrics.P:F3}/{newMetrics.R:F3}/{newMetrics.F1:F3}; matched MAE ms {oldMetrics.MaeMs:F1}->{newMetrics.MaeMs:F1}"));
            }
            foreach (var section in grid.Sections)
                Console.WriteLine(FormattableString.Invariant($"  {section.StartFrame / (double)PlaybackFormat.SampleRate:F2}–{section.EndFrame / (double)PlaybackFormat.SampleRate:F2}s: {section.Bpm:F1} BPM"));
            foreach (var mark in report.GetProperty("Marks").EnumerateArray())
            {
                long start = mark.GetProperty("StartFrame").GetInt64(), end = mark.GetProperty("EndFrame").GetInt64();
                Console.WriteLine($"  Mark: {mark.GetProperty("Comment").GetString()} | beats {oldBeats.Count(f => f >= start && f < end)}->{grid.BeatFrames.Count(f => f >= start && f < end)}");
            }
        }
    }

    // Shift every beat by the same offset over one typical period, retaining
    // density, interval pattern and the original annotated runs.
    internal static double PhaseNull(long[] beats,long[] taps,long tolerance)
    {
        if(beats.Length<2||taps.Length==0)return 0;
        var intervals=new List<long>();
        for(int i=1;i<beats.Length;i++)
        {
            long gap=beats[i]-beats[i-1];
            if(gap>=PlaybackFormat.SampleRate/4&&gap<=PlaybackFormat.SampleRate*3/2)
                intervals.Add(gap);
        }
        if(intervals.Count==0)return 0;
        intervals.Sort();
        double period=intervals[intervals.Count/2],sum=0;
        var shifted=new long[beats.Length];
        for(int i=0;i<32;i++)
        {
            long offset=(long)Math.Round((i/32d-.5)*period);
            for(int j=0;j<beats.Length;j++)shifted[j]=beats[j]+offset;
            sum+=SeriesMetrics(shifted,taps,tolerance).F1;
        }
        return sum/32;
    }

    internal static (double P,double R,double F1) SeriesMetrics(long[] beats,long[] taps,long tolerance)
    {
        if (taps.Length==0) return default;
        int matches=0,predictions=0,cursor=0;
        for (int start=0;start<taps.Length;)
        {
            int end=start+1;
            while (end<taps.Length && taps[end]-taps[end-1]<=2L*PlaybackFormat.SampleRate) end++;
            long lo=taps[start]-tolerance,hi=taps[end-1]+tolerance;
            while (cursor<beats.Length && beats[cursor]<lo) cursor++;
            int first=cursor;
            while (cursor<beats.Length && beats[cursor]<=hi) cursor++;
            predictions+=cursor-first;
            matches+=MatchOneToOne(beats.AsSpan(first,cursor-first),taps.AsSpan(start,end-start),tolerance);
            start=end;
        }
        return (predictions==0?0:matches/(double)predictions,matches/(double)taps.Length,
            2d*matches/(predictions+taps.Length));
    }

    private static long[] ReadBeats(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        reader.ReadLine();
        var result = new List<long>();
        while (reader.ReadLine() is { } line) result.Add(long.Parse(line, CultureInfo.InvariantCulture));
        return result.ToArray();
    }

    internal static (double P,double R,double F1,double MaeMs) Metrics(long[] beats,long[] taps,long tolerance)
    {
        if(taps.Length==0)return (0,0,0,0);
        var span=beats.Where(b=>b>=taps[0]-tolerance&&b<=taps[^1]+tolerance).ToArray();
        var (matches,error)=MinimumTimingError(span,taps,tolerance);
        double p=span.Length==0?0:matches/(double)span.Length,r=matches/(double)taps.Length;
        return(p,r,p+r==0?0:2*p*r/(p+r),matches==0?double.NaN:error*1000d/(matches* (double)PlaybackFormat.SampleRate));
    }

    // Longest chronological chain of feasible pairs, minimizing total timing
    // error among chains with the maximum number of matches.
    private static (int Count,long Error) MinimumTimingError(
        ReadOnlySpan<long> beats,ReadOnlySpan<long> taps,long tolerance)
    {
        var tree=new (int Count,long Error)[taps.Length+1];
        static bool Better((int Count,long Error) a,(int Count,long Error) b)=>
            a.Count>b.Count || a.Count==b.Count && a.Error<b.Error;

        (int Count,long Error) Prefix(int length)
        {
            (int Count,long Error) best=default;
            for(int i=length;i>0;i-=i&-i)
                if(Better(tree[i],best))best=tree[i];
            return best;
        }

        int first=0,end=0;
        foreach(long beat in beats)
        {
            while(first<taps.Length && taps[first]<beat-tolerance)first++;
            end=Math.Max(end,first);
            while(end<taps.Length && taps[end]<=beat+tolerance)end++;
            // Descending taps prevent a second pair from using this beat.
            for(int j=end-1;j>=first;j--)
            {
                var previous=Prefix(j);
                var next=(previous.Count+1,previous.Error+Math.Abs(taps[j]-beat));
                for(int k=j+1;k<tree.Length;k+=k&-k)
                    if(Better(next,tree[k]))tree[k]=next;
            }
        }
        return Prefix(taps.Length);
    }

    private static int MatchOneToOne(ReadOnlySpan<long> beats, ReadOnlySpan<long> taps, long tolerance)
    {
        int i = 0, j = 0, matched = 0;
        while (i < beats.Length && j < taps.Length)
        {
            if (beats[i] < taps[j] - tolerance) i++;
            else if (taps[j] < beats[i] - tolerance) j++;
            else { matched++; i++; j++; }
        }
        return matched;
    }
}
