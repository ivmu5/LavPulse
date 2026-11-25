using System.Globalization;
using System.IO.Compression;
using System.Text;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Lossless numeric export of an opt-in analysis trace. Invariant culture,
/// exact PCM frame positions, no sampling beyond the analyzer's own limits.</summary>
internal static class TrackDiagnosticCsv
{
    internal static async Task WriteAsync(ZipArchive zip, string prefix, TrackIssueReport report,
        TrackDiagnosticSnapshot snapshot, CancellationToken token)
    {
        var waveform = snapshot.Waveform;
        var trace = snapshot.Trace;
        var grid = waveform.Rhythm;
        await Write(zip, prefix + "metadata.txt", [
            "Trace schema: " + TrackRhythmTrace.SchemaVersion,
            "Origin: fresh analysis at export, not the saved report analysis.txt",
            "Decoder: " + trace.Decoder,
            FormattableString.Invariant($"Sample rate: {PlaybackFormat.SampleRate}; total PCM frames: {waveform.TotalFrames}; envelope hop frames: {trace.HopFrames}"),
            "Track SHA-256 library ID: " + report.Id,
            TrackAnalysisReport.Create(report.FileName, waveform, snapshot.Info, report.Title, report.Artist)
        ], token).ConfigureAwait(false);

        await Write(zip, prefix + "energy.csv",
            Rows("frame,low,mid,high", trace.Energy.Select((e, i) =>
                FormattableString.Invariant($"{(long)i * trace.HopFrames},{e.Low:R},{e.Mid:R},{e.High:R}"))), token).ConfigureAwait(false);
        await Write(zip, prefix + "onsets.csv",
            Rows("frame,strength,accent", trace.Onsets.Select(o =>
                FormattableString.Invariant($"{o.Frame},{o.Strength:R},{o.Accent:R}"))), token).ConfigureAwait(false);
        await Write(zip, prefix + "waveform.csv",
            Rows("frame,peak,low,mid,high", waveform.Points.Select((p, i) =>
                FormattableString.Invariant($"{(long)i * waveform.FramesPerPoint},{p.Peak:R},{p.Low:R},{p.Mid:R},{p.High:R}"))), token).ConfigureAwait(false);
        if (grid is not null)
        {
            await Write(zip, prefix + "beats.csv", Rows("frame",
                grid.BeatFrames.Select(Frame)), token).ConfigureAwait(false);
            await Write(zip, prefix + "predicted-beats.csv", Rows("frame",
                grid.PredictedBeatFrames.Select(Frame)), token).ConfigureAwait(false);
            await Write(zip, prefix + "beat-evidence.csv", Rows(
                "frame,confidence,correction_frames,matched_onset,energy_break,confirmed",
                grid.BeatEstimates.Select(e => FormattableString.Invariant(
                    $"{e.Frame},{e.Confidence:R},{e.CorrectionFrames},{(e.MatchedOnset ? 1 : 0)},{(e.EnergyBreak ? 1 : 0)},{(e.Confirmed ? 1 : 0)}"))), token).ConfigureAwait(false);
            await Write(zip, prefix + "transients.csv", Rows("frame",
                grid.TransientFrames.Select(Frame)), token).ConfigureAwait(false);
            await Write(zip, prefix + "sections.csv",
                Rows("start_frame,end_frame_exclusive,bpm,confidence,onsets,matched_beats", grid.Sections.Select(s =>
                    FormattableString.Invariant($"{s.StartFrame},{s.EndFrame},{s.Bpm:R},{s.Confidence:R},{s.OnsetCount},{s.MatchedBeats}"))), token).ConfigureAwait(false);
            await Write(zip, prefix + "windows.csv",
                Rows("window,start_frame,end_frame,bpm,confidence,onsets,accepted", grid.Windows.Select((w, i) =>
                    FormattableString.Invariant($"{i},{w.StartFrame},{w.EndFrame},{w.Bpm:R},{w.Confidence:R},{w.OnsetCount},{(w.Accepted ? 1 : 0)}"))), token).ConfigureAwait(false);
        }
        if (trace.TempoScores is { } scores)
        {
            await Write(zip, prefix + "tempo-scores.csv", TempoRows(trace, scores), token).ConfigureAwait(false);
        }
        if (trace.RawTempoScores is { } rawScores)
            await Write(zip, prefix + "raw-tempo-scores.csv", TempoRows(trace, rawScores), token).ConfigureAwait(false);
        if (trace.PhaseScores is { } phaseScores)
            await Write(zip, prefix + "phase-scores.csv", TempoRows(trace, phaseScores), token).ConfigureAwait(false);
        if (trace.ConfidenceScores is { } confidenceScores)
            await Write(zip, prefix + "confidence-scores.csv", TempoRows(trace, confidenceScores), token).ConfigureAwait(false);
        if (trace.PeriodicityScores is { } periodicity)
            await Write(zip, prefix + "periodicity-scores.csv", TempoRows(trace, periodicity), token).ConfigureAwait(false);
        await Write(zip, prefix + "section-attempts.csv", Rows(
            "first_window,end_window_exclusive,recovery,mean_confidence,bpm,anchor_bpm,phase_hops,start_frame,end_frame_exclusive,beats,matched,strong_onsets,decision",
            trace.Attempts.Select(a => FormattableString.Invariant(
                $"{a.FirstWindow},{a.EndWindowExclusive},{(a.Recovery ? 1 : 0)},{a.MeanConfidence:R},{a.Bpm:R},{a.AnchorBpm:R},{a.PhaseInHops:R},{a.StartFrame},{a.EndFrame},{a.BeatCount},{a.MatchedBeats},{(a.UsesStrongOnsets ? 1 : 0)},{a.Decision}"))), token).ConfigureAwait(false);

        // The tap timestamp, uncertainty, original displayed grid context and
        // optional version remain in annotations.json. Compare the raw frame
        // to the newly analyzed grid without modifying the tap itself.
        await Write(zip, prefix + "tap-comparison.csv", Rows(
            "tap_frame,tolerance_ms,visual_delay_ms,observed_nearest_beat_frame,observed_nearest_transient_frame,observed_bpm,fresh_nearest_beat_frame,fresh_offset_ms,fresh_bpm",
            (report.BeatTaps ?? []).Select(tap =>
            {
                long? nearest = Nearest(grid?.BeatFrames, tap.Frame);
                double? delta = nearest is long b ? (tap.Frame - b) * 1000d / PlaybackFormat.SampleRate : null;
                return string.Join(",", Frame(tap.Frame), tap.TimingToleranceMilliseconds.ToString(CultureInfo.InvariantCulture),
                    tap.VisualDelayMilliseconds.ToString(CultureInfo.InvariantCulture), NullableFrame(tap.NearestBeatFrame),
                    NullableFrame(tap.NearestTransientFrame), Number(tap.LocalBpm), NullableFrame(nearest),
                    Number(delta), Number(grid?.BpmAt(tap.Frame)));
            })), token).ConfigureAwait(false);
    }

    private static IEnumerable<string> TempoRows(TrackRhythmTrace trace, float[,] scores)
    {
        int columns = scores.GetLength(1);
        yield return "window," + string.Join(",", Enumerable.Range(0, columns)
            .Select(i => (trace.MinimumBpm + i * trace.BpmResolution).ToString("R", CultureInfo.InvariantCulture)));
        var row = new StringBuilder();
        for (int i = 0; i < scores.GetLength(0); i++)
        {
            row.Clear();
            row.Append(i.ToString(CultureInfo.InvariantCulture));
            for (int j = 0; j < columns; j++)
                row.Append(',').Append(scores[i, j].ToString("R", CultureInfo.InvariantCulture));
            yield return row.ToString();
        }
    }

    private static IEnumerable<string> Rows(string header, IEnumerable<string> rows)
    {
        yield return header;
        foreach (string row in rows) yield return row;
    }

    private static string Frame(long frame) => frame.ToString(CultureInfo.InvariantCulture);
    private static string NullableFrame(long? frame) => frame is long value ? Frame(value) : "";
    private static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";

    private static long? Nearest(long[]? frames, long frame)
    {
        if (frames is not { Length: > 0 }) return null;
        int i = Array.BinarySearch(frames, frame);
        if (i >= 0) return frames[i];
        i = ~i;
        if (i == 0) return frames[0];
        if (i == frames.Length) return frames[^1];
        return frame - frames[i - 1] <= frames[i] - frame ? frames[i - 1] : frames[i];
    }

    private static async Task Write(ZipArchive zip, string name, IEnumerable<string> lines,
        CancellationToken token)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 32 * 1024, leaveOpen: true);
        foreach (string line in lines)
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
        }
    }
}
