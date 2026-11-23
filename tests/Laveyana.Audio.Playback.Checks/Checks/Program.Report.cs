using System.IO.Compression;
using Laveyana.LavPulse.Tracks;

internal static partial class Program
{
    private static async Task CheckTrackReportBundleAsync(string directory)
    {
        string root = Path.Combine(directory, "report-check");
        Directory.CreateDirectory(root);
        string id = new string('A', 64);
        string mp3 = Path.Combine(root, id + ".mp3");
        byte[] original = Enumerable.Range(0, 8192).Select(n => (byte)(n % 251)).ToArray();
        await File.WriteAllBytesAsync(mp3, original);
        string storage = Path.Combine(root, "reports.json");
        var reports = new TrackReportStore(storage);
        bool first = await reports.RegisterAsync(id, "Original.mp3", "Tempo Mix", "DJ",
            "LAVPULSE TRACK DIAGNOSTICS v5", default);
        bool second = await reports.RegisterAsync(id, "Original.mp3", "Tempo Mix", "DJ",
            "LAVPULSE TRACK DIAGNOSTICS v6", default);
        Require(first && !second, "Opening the manual BPM editor created duplicate track reports.");
        var registered = await new TrackReportStore(storage).ReadAllAsync(default);
        Require(registered.Length == 1 && registered[0].Marks.Count == 0 &&
                registered[0].Analysis.EndsWith("v6"),
            "A manually enrolled track disappeared after restarting before a section was saved.");

        string zipBeforeMarks = Path.Combine(root, "empty-marks.zip");
        await TrackReportArchive.CreateAsync(registered, zipBeforeMarks, key => Path.Combine(root, key + ".mp3"),
            default);
        using (var archive = ZipFile.OpenRead(zipBeforeMarks))
        {
            using var copy = archive.Entries.Single(e => e.Name == "Original.mp3").Open();
            using var memory = new MemoryStream();
            await copy.CopyToAsync(memory);
            Require(memory.ToArray().SequenceEqual(original),
                "A track enrolled without saved BPM ranges lost its original MP3 in ZIP export.");
        }

        var section = new TrackIssueMark(5L * Rate, 24L * Rate, "steady kick",
            DateTimeOffset.UtcNow, TrackReportMarkKind.StrongRhythm, 250, 3, 148.5);
        await reports.AddAsync(id, "Original.mp3", "Tempo Mix", "DJ", "analysis", section, default);
        var issue = new TrackIssueMark(10L * Rate, 11L * Rate, "BeatGrid offset",
            DateTimeOffset.UtcNow);
        await reports.AddAsync(id, "Original.mp3", "Tempo Mix", "DJ", "analysis", issue, default);
        var updated = await new TrackReportStore(storage).ReadAllAsync(default);
        Require(updated.Length == 1 && updated[0].Marks.Count == 2 &&
            updated[0].Marks[0].SuggestedBpm == 148.5 &&
            updated[0].Marks[0].PossibleMissingBeats == 3,
            "Multiple hand-defined tempo sections were duplicated or lost on disk.");
        string copyText = TrackReportArchive.RenderText(updated);
        Require(copyText.Contains("user BPM≈148.5") &&
            copyText.Contains("up to 3 missing beats") && copyText.Contains("BeatGrid offset"),
            "Diagnostic report omitted human timing uncertainty or user-supplied BPM.");
        string bundle = Path.Combine(root, "full-report.zip");
        await TrackReportArchive.CreateAsync(updated, bundle, key => Path.Combine(root, key + ".mp3"), default);
        using (var archive = ZipFile.OpenRead(bundle))
        {
            Require(archive.Entries.Any(e => e.FullName == "full-report.txt") &&
                archive.Entries.Any(e => e.FullName.EndsWith("/analysis.txt")) &&
                archive.Entries.Any(e => e.FullName.EndsWith("/comments.txt")),
                "ZIP is missing diagnostic analysis or manual comments.");
            using var originalEntry = archive.Entries.Single(e => e.Name == "Original.mp3").Open();
            using var memory = new MemoryStream();
            await originalEntry.CopyToAsync(memory);
            Require(original.SequenceEqual(memory.ToArray()), "Original MP3 bytes were modified during export.");
        }
        await reports.ClearAsync(default);
        Require((await new TrackReportStore(storage).ReadAllAsync(default)).Length == 0 &&
            File.Exists(mp3), "Clearing reports deleted the original audio or failed to clear marks.");
        Console.WriteLine("OK: automatic manual-BPM enrollment, persistent sections and original-MP3 ZIP export.");
    }
}
