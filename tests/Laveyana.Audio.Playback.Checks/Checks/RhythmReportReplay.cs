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
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (!TrackRhythmAnalyzer.IsValid(grid, total)) throw new InvalidDataException("Invalid new grid.");
            using var annotations = JsonDocument.Parse(zip.GetEntry(prefix + "annotations.json")!.Open());
            var report = annotations.RootElement[0];
            var taps = report.GetProperty("BeatTaps").EnumerateArray().Select(t => t.GetProperty("Frame").GetInt64()).ToArray();
            var oldBeats = ReadBeats(zip.GetEntry(prefix + "diagnostics/beats.csv")!);
            Array.Sort(taps);
            int Close(long[] beats) => MatchOneToOne(beats, taps, PlaybackFormat.SampleRate / 10);
            int ClosePredicted = Close(grid.PredictedBeatFrames);
            long covered = grid.Sections.Sum(s => s.EndFrame - s.StartFrame);
            Console.WriteLine(FormattableString.Invariant(
                $"{report.GetProperty("Title").GetString()}: BPM={grid.Bpm:F1}; coverage={covered / (double)total:P1}; beats {oldBeats.Length}->{grid.BeatFrames.Length}; one-to-one taps within 100ms {Close(oldBeats)}->{Close(grid.BeatFrames)}/{taps.Length}; predictions {ClosePredicted}/{taps.Length}"));
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
