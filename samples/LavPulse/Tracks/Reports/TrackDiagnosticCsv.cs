using System.Globalization;
using System.IO.Compression;
using System.Reflection;
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
            "Playback assembly informational version: " + (typeof(TrackWaveform).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"),
            $"Joint configuration: beam={RhythmJointDecoder.BeamWidth}; stride_hops={RhythmJointDecoder.Stride}; phase_bins={RhythmJointDecoder.PhaseCount}; min_bpm={RhythmJointDecoder.MinBpm}; max_bpm={RhythmJointDecoder.MaxBpm}",
            FormattableString.Invariant($"Sample rate: {PlaybackFormat.SampleRate}; total PCM frames: {waveform.TotalFrames}; envelope hop frames: {trace.HopFrames}"),
            "Track SHA-256 library ID: " + report.Id,
            $"Audit feature version: {RhythmAuditFlux.Version}; joint decoder version: {RhythmJointDecoder.Version}; hop frames: {RhythmAuditFlux.HopFrames}; truncated: {trace.AuditTruncated}",
            FormattableString.Invariant($"Audit features ms: {trace.AuditFeatureMs:R}; audit path ms: {trace.AuditDecodeMs:R}"),
            $"Metric phase lanes: {trace.AuditResult.MetricPaths.Length}; budget reached: {trace.AuditResult.MetricBudgetReached}",
            TrackAnalysisReport.Create(report.FileName, waveform, snapshot.Info, report.Title, report.Artist)
        ], token).ConfigureAwait(false);

        await Write(zip, prefix + "analysis-timings.csv", Rows("stage,wall_ms",
            trace.StageTimings.Select(t=>FormattableString.Invariant($"{t.Stage},{t.Milliseconds:R}"))), token).ConfigureAwait(false);
        await Write(zip, prefix + "energy.csv",
            Rows("frame,low,mid,high", trace.Energy.Select((e, i) =>
                FormattableString.Invariant($"{(long)i * trace.HopFrames},{e.Low:R},{e.Mid:R},{e.High:R}"))), token).ConfigureAwait(false);
        await Write(zip, prefix + "spectral-flux.csv",
            Rows("frame,sub_20_60,bass_60_150,low_mid_150_400,mid_400_2000,high_2000_16000",
                trace.SpectralFlux.Select(f => FormattableString.Invariant(
                    $"{f.Frame},{f.Sub:R},{f.Bass:R},{f.LowMid:R},{f.Mid:R},{f.High:R}"))), token).ConfigureAwait(false);
        await Write(zip, prefix + "audit-flux.csv", Rows("frame,band_30_100,band_100_220,band_220_600,band_600_2000,band_2000_7900",
            trace.AuditFlux.Select(f => FormattableString.Invariant(
                $"{f.Frame},{f.Sub:R},{f.Bass:R},{f.LowMid:R},{f.Mid:R},{f.High:R}"))), token).ConfigureAwait(false);
        foreach (var (name,path) in new[] { ("audit-baseline",trace.AuditResult.Baseline),
            ("joint",trace.AuditResult.Joint), ("joint-alternative",trace.AuditResult.Alternative) })
        {
            await Write(zip,prefix+name+"-states.csv",Rows("frame,bpm,phase_cycles,tempo_evidence,phase_evidence,prefix_margin,inactive,inactive_boundary_frame",
                path.States.Select(s=>FormattableString.Invariant(
                    $"{s.Frame},{s.Bpm:R},{s.PhaseCycles:R},{s.TempoEvidence:R},{s.PhaseEvidence:R},{s.Margin:R},{(s.Inactive?1:0)},{s.InactiveBoundaryFrame}"))),token).ConfigureAwait(false);
            await Write(zip,prefix+name+"-beats.csv",Rows("frame,index,observed,low_evidence,high_evidence,activity_region",
                path.Beats.Select(b=>FormattableString.Invariant($"{b.Frame},{b.Index},{(b.Observed?1:0)},{b.LowEvidence:R},{b.HighEvidence:R},{b.ActivityRegion}"))),token).ConfigureAwait(false);
        }
        var metricPaths=trace.AuditResult.MetricPaths;
        await Write(zip,prefix+"metric-paths.csv",Rows(
            "id,numerator,denominator,lane,lane_count,mean_tempo_evidence,mean_phase_evidence,beat_count",
            metricPaths.Select((p,i)=>FormattableString.Invariant(
                $"{i},{p.Numerator},{p.Denominator},{p.Lane},{p.LaneCount},{p.MeanTempoEvidence:R},{p.MeanPhaseEvidence:R},{p.Path.Beats.Length}"))),token).ConfigureAwait(false);
        await Write(zip,prefix+"metric-states.csv",Rows(
            "id,frame,bpm,phase_cycles,tempo_evidence,phase_evidence,inactive,inactive_boundary_frame",
            metricPaths.SelectMany((p,i)=>p.Path.States.Select(s=>FormattableString.Invariant(
                $"{i},{s.Frame},{s.Bpm:R},{s.PhaseCycles:R},{s.TempoEvidence:R},{s.PhaseEvidence:R},{(s.Inactive?1:0)},{s.InactiveBoundaryFrame}")))),token).ConfigureAwait(false);
        await Write(zip,prefix+"metric-beats.csv",Rows(
            "id,frame,index,observed,low_evidence,high_evidence,activity_region",
            metricPaths.SelectMany((p,i)=>p.Path.Beats.Select(b=>FormattableString.Invariant(
                $"{i},{b.Frame},{b.Index},{(b.Observed?1:0)},{b.LowEvidence:R},{b.HighEvidence:R},{b.ActivityRegion}")))),token).ConfigureAwait(false);
        await Write(zip, prefix + "onsets.csv",
            Rows("frame,strength,accent", trace.Onsets.Select(o =>
                FormattableString.Invariant($"{o.Frame},{o.Strength:R},{o.Accent:R}"))), token).ConfigureAwait(false);
        await Write(zip, prefix + "waveform.csv",
            Rows("frame,peak,low,mid,high", waveform.Points.Select((p, i) =>
                FormattableString.Invariant($"{(long)i * waveform.FramesPerPoint},{p.Peak:R},{p.Low:R},{p.Mid:R},{p.High:R}"))), token).ConfigureAwait(false);
        if (waveform.MusicProfile is { } profile)
        {
            await Write(zip, prefix + "music-energy.csv", Rows("start_frame,end_frame_exclusive,relative_energy,bass_share,attacks_per_second",
                profile.Energy.Select(e => FormattableString.Invariant($"{e.StartFrame},{e.EndFrame},{e.Energy:R},{e.BassShare:R},{e.AttacksPerSecond:R}"))), token).ConfigureAwait(false);
            await Write(zip, prefix + "music-structure.csv", Rows("start_frame,end_frame_exclusive,kind,relative_energy",
                profile.Structure.Select(s => FormattableString.Invariant($"{s.StartFrame},{s.EndFrame},{s.Kind},{s.Energy:R}"))), token).ConfigureAwait(false);
            await Write(zip, prefix + "local-keys.csv", Rows("start_frame,end_frame_exclusive,tonic,mode,confidence,correlation,margin",
                profile.LocalKeys.Select(k => k.Key is { } key ?
                    FormattableString.Invariant($"{k.StartFrame},{k.EndFrame},{key.Tonic},{key.Mode},{key.Confidence:R},{key.Correlation:R},{key.Margin:R}") :
                    FormattableString.Invariant($"{k.StartFrame},{k.EndFrame},,,,,"))), token).ConfigureAwait(false);
            await Write(zip, prefix + "downbeats.csv", Rows("frame,pulses_per_bar,confidence",
                profile.Meter.SelectMany(m => m.Downbeats.Select(f => FormattableString.Invariant($"{f},{m.PulsesPerBar},{m.Confidence:R}")))), token).ConfigureAwait(false);
            if (profile.Loudness is { } loudness)
                await Write(zip, prefix + "loudness.csv", Rows("end_frame,short_term_lufs",
                    loudness.ShortTerm.Select(s => FormattableString.Invariant($"{s.EndFrame},{s.Lufs:R}"))), token).ConfigureAwait(false);
        }
        if (grid is not null)
        {
            await Write(zip, prefix + "rhythm-hypotheses.csv", Rows(
                "id,start_frame,end_frame_exclusive,bpm,reference_bpm,tempo_confidence,phase_confidence,stability,coverage,score,preferred,local_preferred,recovery_candidate,attack_coverage,reference_agreement,phase_offset,relative_pulse,family_preferred,family_only,reference_candidate,family_score,harmonic_support,bass_attack_recall,bass_beat_precision,percussion_attack_recall,percussion_beat_precision",
                grid.RhythmHypotheses.Select((h,i) => FormattableString.Invariant(
                    $"{i},{h.StartFrame},{h.EndFrame},{h.Bpm:R},{h.ReferenceBpm:R},{h.TempoConfidence:R},{h.PhaseConfidence:R},{h.Stability:R},{h.Coverage:R},{h.Score:R},{(h.IsPreferred ? 1 : 0)},{(h.IsLocalPreferred ? 1 : 0)},{(h.IsRecoveryCandidate ? 1 : 0)},{h.AttackCoverage:R},{h.ReferenceAgreement:R},{h.PhaseOffset:R},{h.RelativePulse},{(h.IsFamilyPreferred ? 1 : 0)},{(h.IsFamilyOnly ? 1 : 0)},{(h.IsReferenceCandidate ? 1 : 0)},{h.FamilyScore:R},{h.HarmonicSupport:R},{h.BassAttackRecall:R},{h.BassBeatPrecision:R},{h.PercussionAttackRecall:R},{h.PercussionBeatPrecision:R}"))), token).ConfigureAwait(false);
            await Write(zip, prefix + "hypothesis-beats.csv", Rows("id,frame",
                grid.RhythmHypotheses.SelectMany((h,i) => h.CandidateFrames.Select(f =>
                    FormattableString.Invariant($"{i},{f}")))), token).ConfigureAwait(false);
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

        await Write(zip, prefix + "phase-hypotheses.csv", Rows(
            "first_window,end_window_exclusive,bpm,original_phase_hops,selected_phase_hops,original_score,best_score,subdivision,alternate",
            trace.PhaseAttempts.Select(a => FormattableString.Invariant(
                $"{a.FirstWindow},{a.EndWindowExclusive},{a.Bpm:R},{a.OriginalPhase:R},{a.SelectedPhase:R},{a.OriginalScore:R},{a.SelectedScore:R},{(a.Subdivision ? 1 : 0)},{(a.Alternate ? 1 : 0)}"))), token).ConfigureAwait(false);
        await Write(zip, prefix + "bridge-attempts.csv", Rows(
            "start_frame,end_frame,bpm,phase_error_beats,periodicity,predictions,supported,accepted,decision",
            trace.BridgeAttempts.Select(a => FormattableString.Invariant(
                $"{a.StartFrame},{a.EndFrame},{a.Bpm:R},{a.PhaseErrorBeats:R},{a.Periodicity:R},{a.Predictions},{a.Supported},{(a.Accepted ? 1 : 0)},{a.Decision}"))), token).ConfigureAwait(false);

        await Write(zip, prefix + "section-phase-path.csv", Rows(
            "start_frame,end_frame,bpm,candidates,original_score,selected_score,shift_beats",
            trace.SectionPhasePath.OrderBy(a => a.StartFrame).Select(a => FormattableString.Invariant(
                $"{a.StartFrame},{a.EndFrame},{a.Bpm:R},{a.Candidates},{a.OriginalScore:R},{a.SelectedScore:R},{a.ShiftBeats:R}"))), token).ConfigureAwait(false);
        if (grid is not null)
            await Write(zip, prefix + "metrical-pulses.csv", Rows(
                "window,beat_bpm,subdivision_factor,subdivision_bpm,accepted",
                grid.Windows.Select((w, i) => FormattableString.Invariant(
                    $"{i},{w.Bpm:R},{w.SubdivisionFactor},{w.SubdivisionBpm:R},{(w.Accepted ? 1 : 0)}"))), token).ConfigureAwait(false);
        await Write(zip, prefix + "metric-root.csv", Rows("bpm,triple,strength",
            trace.MetricRoot is { } root
                ? new[] { FormattableString.Invariant($"{root.Bpm:R},{(root.Triple ? 1 : 0)},{root.Strength:R}") }
                : Array.Empty<string>()), token).ConfigureAwait(false);

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
