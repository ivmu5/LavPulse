using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Progress of a user-requested diagnostic ZIP export.</summary>
internal readonly record struct ReportExportProgress(int Completed, int Total, string? Track, string Stage, int DiagnosticErrors);

internal static class TrackReportArchive
{
    internal static string RenderText(IReadOnlyList<TrackIssueReport> reports)
    {
        var text = new StringBuilder();
        text.AppendLine("LAVPULSE USER TRACK REPORT");
        text.AppendLine($"Tracks: {reports.Count}; comments: {reports.Sum(x => x.Marks.Count)}; " +
            $"manual bass taps: {reports.Sum(x => x.BeatTaps?.Count ?? 0)}");
        foreach (var item in reports)
        {
            text.AppendLine();
            text.AppendLine("==================================================");
            text.AppendLine(item.Title + (string.IsNullOrWhiteSpace(item.Artist) ? "" : " — " + item.Artist));
            text.AppendLine("Source: " + item.FileName);
            text.AppendLine("USER COMMENTS AND MANUAL BPM SECTIONS (mm:ss.mmm)");
            if (item.Marks.Count == 0)
                text.AppendLine("No text comments or range annotations yet.");
            foreach (var note in item.Marks)
                text.AppendLine(RenderMark(note));
            text.AppendLine();
            text.AppendLine(RenderBeatTaps(item.BeatTaps ?? []));
            text.AppendLine();
            text.AppendLine(item.Analysis);
        }
        return text.ToString();
    }

    internal static string RenderMark(TrackIssueMark mark)
    {
        string type = mark.Kind == TrackReportMarkKind.StrongRhythm
            ? "STRONG RHYTHM (human estimate)" : "PROBLEM";
        string bpm = mark.SuggestedBpm is double value
            ? "; user BPM≈" + value.ToString("0.##", CultureInfo.InvariantCulture) : "";
        string transition = mark.Kind == TrackReportMarkKind.StrongRhythm
            ? $"; approximate boundary ±{mark.TimingToleranceMilliseconds} ms; " +
              $"up to {mark.PossibleMissingBeats} missing beats across tempo changes" : "";
        return $"[{Time(mark.StartFrame)} - {Time(mark.EndFrame)}] {type}{bpm}{transition}: " +
            $"{mark.Comment} ({mark.CreatedAt:O})";
    }

    internal static string RenderBeatTaps(IReadOnlyList<ManualBeatTap> taps)
    {
        var text = new StringBuilder();
        text.AppendLine($"MANUALLY TAPPED BASS BEATS: {taps.Count} presses (not automatic BeatGrid)");
        text.AppendLine("Time | timing uncertainty | device visual compensation | input UTC");
        foreach (var tap in taps.OrderBy(item => item.PressedAtUtc))
            text.AppendLine($"{Time(tap.Frame)} | ±{tap.TimingToleranceMilliseconds} ms | " +
                $"{tap.VisualDelayMilliseconds} ms | {tap.PressedAtUtc:O}");

        text.AppendLine("MANUAL BPM ESTIMATES BY TAPPED REGION (1–3 missed beats tolerated):");
        foreach (var section in ManualTapTempoAnalyzer.Analyze(taps))
            text.AppendLine($"{Time(section.StartFrame)}..{Time(section.EndFrame)} | " +
                $"BPM≈{(section.EstimatedBpm is double bpm ? bpm.ToString("0.#", CultureInfo.InvariantCulture) : "unknown")} | " +
                $"taps={section.TapCount} | inferred skipped beats={section.InferredMissedBeats}");
        return text.ToString();
    }

    internal static async Task<int> CreateAsync(IReadOnlyList<TrackIssueReport> reports, string destination,
        Func<string, string> audioPath, CancellationToken token,
        Func<string, CancellationToken, Task<TrackDiagnosticSnapshot>>? analyze = null,
        string? appVersion = null, string? platform = null, IProgress<ReportExportProgress>? progress = null, bool includeAudio = false)
    {
        if (reports.Count == 0) throw new InvalidOperationException("Отчёт пуст. Нет сохранённых треков.");
        int diagnosticErrors = 0;
        progress?.Report(new(0, reports.Count, null, "preparing", diagnosticErrors));
        // Validate IDs regardless of the selected export mode. Full bundles
        // retain the strict preflight; diagnostics-only bundles preserve
        // saved annotations even if the source disappeared.
        foreach (var report in reports)
        {
            token.ThrowIfCancellationRequested();
            if (report.Id.Length != 64 || !report.Id.All(char.IsAsciiHexDigitUpper))
                throw new InvalidDataException("Некорректный идентификатор трека в отчёте.");
        }
        string ResolveAudio(TrackIssueReport report)
        {
            string path = audioPath(report.Id);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Оригинальный аудиофайл для «{report.Title}» недоступен.", path);
            return path;
        }
        var audioFiles = includeAudio ? reports.Select(ResolveAudio).ToArray() : null;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteText(zip, "README.txt",
                    "LavPulse diagnostic bundle.\n" +
                    $"Bundle schema: 3. Original audio included: {(includeAudio ? "yes" : "no")}.\n" +
                    "Each track folder contains recorded analysis, comments and exact annotations. Original audio is optional.\n" +
                    "diagnostics/ contains a fresh export-time analysis, not necessarily the historical grid.\n" +
                    "PCM frame coordinates are at 48000 Hz; the 10 ms envelope contains filter-band mean absolute amplitudes, not instrument stems.\n" +
                    "Tap positions are approximate human observations, never ground truth.\n" +
                    "No system file paths or device identifiers are intentionally included; this ZIP is not uploaded automatically.\n" +
                    $"App version: {appVersion ?? "unknown"}; platform: {platform ?? "unknown"}; exported UTC: {DateTimeOffset.UtcNow:O}\n", token);
                for (int i = 0; i < reports.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var report = reports[i];
                    progress?.Report(new(i, reports.Count, report.Title, "analyzing", diagnosticErrors));
                    string baseName = $"tracks/{i + 1:D2}-{report.Id[..12]}/";
                    await WriteText(zip, baseName + "analysis.txt", report.Analysis, token);
                    await WriteText(zip, baseName + "comments.txt",
                        report.Marks.Count == 0
                            ? "No manually written comments or selected ranges."
                            : string.Join(Environment.NewLine, report.Marks.Select(RenderMark)), token);
                    await WriteText(zip, baseName + "manual-beat-taps.txt",
                        RenderBeatTaps(report.BeatTaps ?? []), token);
                    // Preserve exact original annotations including older reports
                    // whose diagnostic snapshot fields are unavailable.
                    var annotations = JsonSerializer.Serialize(new List<TrackIssueReport>
                    {
                        report with { Analysis = "" }
                    }, TrackReportJson.Default.ListTrackIssueReport);
                    await WriteText(zip, baseName + "annotations.json", annotations, token);
                    if (analyze is not null)
                    {
                        TrackDiagnosticSnapshot? snapshot = null;
                        try
                        {
                            snapshot = await analyze(audioFiles?[i] ?? ResolveAudio(report), token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or
                            UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
                        {
                            diagnosticErrors++;
                            // Error messages can contain private absolute file paths.
                            // Preserve annotations and any requested original audio if fresh analysis fails.
                            await WriteText(zip, baseName + "diagnostics/error.txt",
                                "Fresh analysis unavailable: " + ex.GetType().Name, token);
                        }
                        if (snapshot is not null)
                            await TrackDiagnosticCsv.WriteAsync(zip, baseName + "diagnostics/", report,
                                snapshot, token).ConfigureAwait(false);
                    }
                    progress?.Report(new(i, reports.Count, report.Title, "packing", diagnosticErrors));
                    if (audioFiles is not null)
                    {
                        string originalName = SafeName(report.FileName, audioFiles[i]);
                        var entry = zip.CreateEntry(baseName + originalName, CompressionLevel.NoCompression);
                        await using var input = new FileStream(audioFiles[i], FileMode.Open, FileAccess.Read, FileShare.Read,
                            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var output = entry.Open();
                        await input.CopyToAsync(output, token).ConfigureAwait(false);
                    }
                }
                progress?.Report(new(reports.Count, reports.Count, null, "finalizing", diagnosticErrors));
                await WriteText(zip, "full-report.txt", RenderText(reports), token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return diagnosticErrors;
    }

    private static string SafeName(string fileName, string audioPath)
    {
        string name = Path.GetFileName(fileName);
        string extension = Path.GetExtension(audioPath).ToLowerInvariant();
        if (!TrackFile.IsKnownAudio("audio" + extension)) extension = ".bin";
        name = Path.GetFileNameWithoutExtension(name) + extension;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' or ':' || char.IsControl(ch)
            ? '_' : ch).ToArray();
        string safe = new string(chars).Trim('.', ' ');
        if (safe.Length > 110) safe = safe[..(110 - extension.Length)] + extension;
        return string.IsNullOrWhiteSpace(safe) ? "original" + extension : safe;
    }

    private static string Time(long frames)
    {
        long milliseconds = frames * 1000 / Laveyana.Audio.Playback.PlaybackFormat.SampleRate;
        return $"{milliseconds / 60000:D2}:{milliseconds / 1000 % 60:D2}.{milliseconds % 1000:D3}";
    }

    private static async Task WriteText(ZipArchive archive, string entryName, string content, CancellationToken token)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var writer = entry.Open();
        byte[] data = Encoding.UTF8.GetBytes(content);
        await writer.WriteAsync(data, token).ConfigureAwait(false);
    }
}
