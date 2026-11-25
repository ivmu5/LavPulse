using System.IO.Compression;
using Laveyana.LavPulse.Tracks;
using Laveyana.Audio.Playback;
using System.Text.Json;

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
        bool registeredAgain = await reports.RegisterAsync(id, "Original.mp3", "Tempo Mix", "DJ",
            "LAVPULSE TRACK DIAGNOSTICS v6", default);
        Require(first && !registeredAgain, "Opening the manual BPM editor created duplicate track reports.");
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
        // First press on a fresh track must auto-register the original MP3 without
        // navigating to any report editor. A series of taps is preserved verbatim.
        string tapId = new string('B', 64);
        byte[] tappedAudio = [2, 5, 9, 10, 40];
        await File.WriteAllBytesAsync(Path.Combine(root, tapId + ".mp3"), tappedAudio);
        DateTimeOffset origin = DateTimeOffset.UtcNow;
        var referenceTaps = new List<ManualBeatTap>();
        for (int index = 0; index < 20; index++)
        {
            // 150 BPM and up to 3 omitted hits; preserve original frame positions.
            int beatIndex = index < 12 ? index : index + 3;
            double second = .2 + beatIndex * .4;
            long frame = (long)Math.Round(second * Rate);
            var tap = new ManualBeatTap(frame, origin.AddSeconds(second), 180, 240);
            referenceTaps.Add(tap);
            await reports.AppendBeatTapAsync(tapId, "Tapped.mp3", "Tapped techno", "DJ",
                "SAVED MACHINE ANALYSIS", tap, default);
        }
        var withTaps = await new TrackReportStore(storage).ReadAllAsync(default);
        var enrolled = withTaps.Single(r => r.Id == tapId);
        Require(enrolled.Marks.Count == 0 && enrolled.BeatTaps.Count == referenceTaps.Count &&
            enrolled.BeatTaps[0].VisualDelayMilliseconds == 240 &&
            enrolled.BeatTaps.Select(t => t.Frame).SequenceEqual(referenceTaps.Select(t => t.Frame)),
            "Button presses did not auto-enroll and persist individual heard bass events.");
        var tempo = ManualTapTempoAnalyzer.Analyze(enrolled.BeatTaps);
        Require(tempo.Length > 0 && tempo.Any(s => s.EstimatedBpm is double bpm && Math.Abs(bpm - 150) < 1) &&
            tempo.Sum(s => s.InferredMissedBeats) >= 3,
            "Tap-tempo estimate did not tolerate three intentionally skipped beats.");

        // Explicit pauses and jumps start a new analysis region, not a fictitious
        // continuous tempo over unrelated sections.
        var secondRegion = Enumerable.Range(0, 12).Select(index =>
        {
            double sec = 24 + index * .5;
            return new ManualBeatTap((long)Math.Round(sec * Rate),
                origin.AddSeconds(sec), 180, 0);
        }).ToArray();
        var regions = ManualTapTempoAnalyzer.Analyze([.. referenceTaps, .. secondRegion]);
        Require(regions.Any(s => s.EstimatedBpm is double bpm && Math.Abs(bpm - 120) < 1) &&
            regions.Any(s => s.EstimatedBpm is double bpm && Math.Abs(bpm - 150) < 1),
            "The manual reporter merged separate 120 and 150 BPM sections.");

        string tappedZip = Path.Combine(root, "manual-beats.zip");
        await TrackReportArchive.CreateAsync(withTaps, tappedZip,
            key => Path.Combine(root, key + ".mp3"), default);
        using (var archive = ZipFile.OpenRead(tappedZip))
        {
            var tapEntry = archive.Entries.Single(e => e.FullName.Contains(tapId[..12]) &&
                e.FullName.EndsWith("manual-beat-taps.txt"));
            using var reader = new StreamReader(tapEntry.Open());
            string text = await reader.ReadToEndAsync();
            Require(text.Contains("150") && text.Contains("±180 ms") &&
                text.Contains("240 ms") && text.Contains("20 presses"),
                "Manual bass onset timings and uncertainty were missing from the exported ZIP.");
            using var audio = archive.Entries.Single(e => e.Name == "Tapped.mp3").Open();
            using var bytes = new MemoryStream();
            await audio.CopyToAsync(bytes);
            Require(bytes.ToArray().SequenceEqual(tappedAudio), "Tapping changed the original MP3.");
        }

        // Reproduce exact numeric traces without a real MP3 or a platform decoder.
        const int hop = Rate / 100;
        var energy = Enumerable.Repeat(new RhythmEnergy(.02f, .03f, .01f), 2400).ToArray();
        for (int i = 20; i < energy.Length; i += 40)
            energy[i] = new RhythmEnergy(.9f, .48f, .2f);
        var trace = new TrackRhythmTrace { Decoder = "synthetic" };
        long total = (long)energy.Length * hop;
        var traced = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, total, default, trace);
        var untraced = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, total);
        Require(traced.BeatFrames.SequenceEqual(untraced.BeatFrames) &&
            traced.Sections.SequenceEqual(untraced.Sections) &&
            trace.Energy.Count == energy.Length && trace.Onsets.Length == traced.CandidateOnsets &&
            trace.TempoScores is not null && trace.Attempts.Any(a => a.Decision is "accepted" or "accepted_with_gaps"),
            "Opt-in trace lost evidence or changed the actual analysis.");
        var wave = new TrackWaveform([], total, hop) { Rhythm = traced };
        var captured = ManualBeatTap.Capture(traced.BeatFrames[0] + 13, origin, 240, wave, "test-version");
        Require(captured.NearestBeatFrame == traced.BeatFrames[0] && captured.AppVersion == "test-version",
            "Manual taps did not preserve frame-accurate observed grid context.");
        await reports.AppendBeatTapAsync(tapId, "Tapped.mp3", "Tapped techno", "DJ",
            "SAVED MACHINE ANALYSIS", captured, default);
        var detailedReports = await reports.ReadAllAsync(default);
        var diagnosticReport = detailedReports.Single(r => r.Id == tapId);
        string detailedZip = Path.Combine(root, "detailed.zip");
        await TrackReportArchive.CreateAsync([diagnosticReport], detailedZip,
            key => Path.Combine(root, key + ".mp3"), default,
            (_, _) => Task.FromResult(new TrackDiagnosticSnapshot(wave, null, trace)),
            "test-version", "test-platform");
        using (var detailed = ZipFile.OpenRead(detailedZip))
        {
            string Read(string name)
            {
                using var reader = new StreamReader(detailed.Entries.Single(e => e.FullName.EndsWith("/" + name)).Open());
                return reader.ReadToEnd();
            }
            var rows = Read("energy.csv").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Require(rows.Length == energy.Length + 1 && rows[1].StartsWith("0,0.02,0.03,0.01"),
                "Energy envelope was reduced, rounded or exported using locale-dependent numbers.");
            Require(Read("tempo-scores.csv").StartsWith("window,40,40.5,41", StringComparison.Ordinal) &&
                Read("periodicity-scores.csv").StartsWith("window,40,40.5,41", StringComparison.Ordinal) &&
                Read("predicted-beats.csv").StartsWith("frame", StringComparison.Ordinal) &&
                Read("raw-tempo-scores.csv").StartsWith("window,40,40.5,41", StringComparison.Ordinal) &&
                Read("confidence-scores.csv").StartsWith("window,40,40.5,41", StringComparison.Ordinal) &&
                Read("phase-scores.csv").StartsWith("window,40,40.5,41", StringComparison.Ordinal) &&
                Read("beat-evidence.csv").StartsWith("frame,confidence,correction_frames", StringComparison.Ordinal) &&
                Read("phase-hypotheses.csv").StartsWith("first_window,end_window_exclusive,bpm", StringComparison.Ordinal) &&
                Read("bridge-attempts.csv").StartsWith("start_frame,end_frame,bpm", StringComparison.Ordinal) &&
                Read("section-phase-path.csv").StartsWith("start_frame,end_frame,bpm", StringComparison.Ordinal) &&
                Read("metrical-pulses.csv").StartsWith("window,beat_bpm,subdivision_factor", StringComparison.Ordinal) &&
                Read("metric-root.csv").StartsWith("bpm,triple,strength", StringComparison.Ordinal) &&
                Read("section-attempts.csv").Contains("accepted") &&
                Read("tap-comparison.csv").Contains(captured.Frame.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "Full tempo alternatives, section decisions or tap comparison are missing.");
            var raw = JsonSerializer.Deserialize(Read("annotations.json"), TrackReportJson.Default.ListTrackIssueReport);
            Require(raw?.Single().BeatTaps[^1] == captured &&
                Read("analysis.txt") == "SAVED MACHINE ANALYSIS",
                "Detailed export changed the recorded analysis or raw manual tap.");
        }
        string failedZip = Path.Combine(root, "diagnostic-failure.zip");
        await TrackReportArchive.CreateAsync([diagnosticReport], failedZip,
            key => Path.Combine(root, key + ".mp3"), default,
            (_, _) => Task.FromException<TrackDiagnosticSnapshot>(new InvalidDataException("/private/path")));
        using (var failed = ZipFile.OpenRead(failedZip))
        {
            using var reader = new StreamReader(failed.Entries.Single(e => e.Name == "error.txt").Open());
            Require(!reader.ReadToEnd().Contains("/private/path") && failed.Entries.Any(e => e.Name == "Tapped.mp3"),
                "A failed diagnostic pass exposed a path or discarded the original audio.");
        }

        await reports.ClearAsync(default);
        Require((await new TrackReportStore(storage).ReadAllAsync(default)).Length == 0 &&
            File.Exists(mp3), "Clearing reports deleted the original audio or failed to clear marks.");
        Console.WriteLine("OK: automatic manual-BPM enrollment, persistent sections and original-MP3 ZIP export.");
    }
}
