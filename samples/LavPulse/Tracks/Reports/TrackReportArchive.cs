using System.IO.Compression;
using System.Text;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Exports annotated diagnostics and original MP3 bytes into a user-requested ZIP.</summary>
internal static class TrackReportArchive
{
    internal static string RenderText(IReadOnlyList<TrackIssueReport> reports)
    {
        var text = new StringBuilder();
        text.AppendLine("LAVPULSE USER TRACK REPORT");
        text.AppendLine($"Tracks: {reports.Count}; comments: {reports.Sum(x => x.Marks.Count)}");
        foreach (var item in reports)
        {
            text.AppendLine();
            text.AppendLine("==================================================");
            text.AppendLine(item.Title + (string.IsNullOrWhiteSpace(item.Artist) ? "" : " — " + item.Artist));
            text.AppendLine("Source: " + item.FileName);
            text.AppendLine("USER COMMENTS (mm:ss.mmm)");
            foreach (var note in item.Marks)
                text.AppendLine(RenderMark(note));
            text.AppendLine();
            text.AppendLine(item.Analysis);
        }
        return text.ToString();
    }

    internal static string RenderMark(TrackIssueMark mark)
    {
        string type = mark.Kind == TrackReportMarkKind.StrongRhythm
            ? "STRONG RHYTHM (human estimate)" : "PROBLEM";
        string bpm = mark.SuggestedBpm is double value ? $"; user BPM≈{value:0.##}" : "";
        string transition = mark.Kind == TrackReportMarkKind.StrongRhythm
            ? $"; approximate boundary ±{mark.TimingToleranceMilliseconds} ms; " +
              $"up to {mark.PossibleMissingBeats} missing beats across tempo changes" : "";
        return $"[{Time(mark.StartFrame)} - {Time(mark.EndFrame)}] {type}{bpm}{transition}: " +
            $"{mark.Comment} ({mark.CreatedAt:O})";
    }

    internal static async Task CreateAsync(IReadOnlyList<TrackIssueReport> reports, string destination,
        Func<string, string> mp3Path, CancellationToken token)
    {
        if (reports.Count == 0) throw new InvalidOperationException("Добавь хотя бы одно замечание в репорт.");
        // Fail before writing the ZIP if a track disappeared or the selected SD card was removed.
        var audioFiles = reports.Select(report =>
        {
            if (report.Id.Length != 64 || !report.Id.All(char.IsAsciiHexDigitUpper))
                throw new InvalidDataException("Некорректный идентификатор трека в отчёте.");
            string path = mp3Path(report.Id);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Оригинал MP3 для «{report.Title}» недоступен.", path);
            return path;
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteText(zip, "README.txt",
                    "LavPulse diagnostic bundle. Includes the original MP3 files explicitly selected by the user.\n" +
                    "Each track folder contains comments.txt, analysis.txt and original MP3 bytes.\n" +
                    "No file-system paths are included; this ZIP is not uploaded automatically.\n", token);
                for (int i = 0; i < reports.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var report = reports[i];
                    string baseName = $"tracks/{i + 1:D2}-{report.Id[..12]}/";
                    await WriteText(zip, baseName + "analysis.txt", report.Analysis, token);
                    await WriteText(zip, baseName + "comments.txt",
                        string.Join(Environment.NewLine, report.Marks.Select(RenderMark)), token);
                    string originalName = SafeName(report.FileName);
                    var entry = zip.CreateEntry(baseName + originalName, CompressionLevel.NoCompression);
                    await using var input = new FileStream(audioFiles[i], FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                }
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
    }

    private static string SafeName(string fileName)
    {
        string name = Path.GetFileName(fileName);
        if (!name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) name += ".mp3";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' or ':' || char.IsControl(ch)
            ? '_' : ch).ToArray();
        string safe = new string(chars).Trim('.', ' ');
        if (safe.Length > 110) safe = safe[..106] + ".mp3";
        return string.IsNullOrWhiteSpace(safe) ? "original.mp3" : safe;
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
