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
                audit = RhythmJointDecoder.Analyze(rawAudit,total,default);
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
            foreach (var (name,path) in new[] { ("audit-baseline",audit.Baseline),("joint",audit.Joint),("joint-alternative",audit.Alternative) })
            {
                if (path.States.Length==0 || taps.Length==0) continue;
                long[] frames=path.Beats.Select(b=>b.Frame).ToArray();
                foreach (int ms in new[]{50,70,100,180})
                {
                    var metric=SeriesMetrics(frames,taps,PlaybackFormat.SampleRate*ms/1000);
                    Console.WriteLine(FormattableString.Invariant(
                        $"  {name} {ms}ms: P/R/F1 {metric.P:F3}/{metric.R:F3}/{metric.F1:F3}, beats {frames.Length}, observed {path.Beats.Count(b=>b.Observed)}"));
                }
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
            matches+=MatchOneToOne(beats[first..cursor],taps[start..end],tolerance);
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
        int i=0,j=0,matches=0;
        double error=0;
        while(i<span.Length&&j<taps.Length)
        {
            if(span[i]<taps[j]-tolerance)i++;
            else if(taps[j]<span[i]-tolerance)j++;
            else{error+=Math.Abs(span[i]-taps[j]);i++;j++;matches++;}
        }
        double p=span.Length==0?0:matches/(double)span.Length,r=matches/(double)taps.Length;
        return(p,r,p+r==0?0:2*p*r/(p+r),matches==0?double.NaN:error*1000/(matches* (double)PlaybackFormat.SampleRate));
    }

    private static int MatchOneToOne(long[] beats, long[] taps, long tolerance)
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
