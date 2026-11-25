using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Laveyana.Audio.Playback;

/// <summary>Replays private diagnostic envelopes locally; never uploads audio or reports.</summary>
internal static class RhythmReportReplay
{
    public static void Run(string path)
    {
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
            var grid = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, total);
            if (!TrackRhythmAnalyzer.IsValid(grid, total)) throw new InvalidDataException("Invalid new grid.");
            using var annotations = JsonDocument.Parse(zip.GetEntry(prefix + "annotations.json")!.Open());
            var report = annotations.RootElement[0];
            var taps = report.GetProperty("BeatTaps").EnumerateArray().Select(t => t.GetProperty("Frame").GetInt64()).ToArray();
            var oldBeats = ReadBeats(zip.GetEntry(prefix + "diagnostics/beats.csv")!);
            int Close(long[] beats) => taps.Count(t => NearestError(beats, t) <= PlaybackFormat.SampleRate / 10);
            long covered = grid.Sections.Sum(s => s.EndFrame - s.StartFrame);
            Console.WriteLine(FormattableString.Invariant(
                $"{report.GetProperty("Title").GetString()}: BPM={grid.Bpm:F1}; coverage={covered / (double)total:P1}; beats {oldBeats.Length}->{grid.BeatFrames.Length}; taps within 100ms {Close(oldBeats)}->{Close(grid.BeatFrames)}/{taps.Length}"));
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

    private static long NearestError(long[] beats, long frame)
    {
        int i = Array.BinarySearch(beats, frame);
        if (i >= 0) return 0;
        i = ~i;
        return Math.Min(i > 0 ? frame - beats[i - 1] : long.MaxValue,
            i < beats.Length ? beats[i] - frame : long.MaxValue);
    }
}
