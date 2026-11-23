using System.Globalization;
using System.Text;

namespace Laveyana.Audio.Playback;

/// <summary>Text-only, user-triggered diagnostic export; no audio, filesystem paths or uploads.</summary>
public static class TrackAnalysisReport
{
    public const int Version = 5;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Create(string fileName, TrackWaveform waveform, TrackAudioInfo? audio = null,
        string? title = null, string? artist = null, string? album = null)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        var builder = new StringBuilder(16_384);
        void Add(string text) => builder.AppendLine(text);
        string Number(double value, string format = "0.###") => value.ToString(format, Invariant);
        string Time(long frames) => Number(frames / (double)PlaybackFormat.SampleRate, "0.00") + "s";
        var rhythm = waveform.Rhythm;
        Add("LAVPULSE TRACK DIAGNOSTICS v" + Version);
        Add("This text contains analysis data, not an audio recording or separate instrument stems.");
        Add("File: " + fileName);
        if (!string.IsNullOrEmpty(title)) Add("Title: " + title);
        if (!string.IsNullOrEmpty(artist)) Add("Artist: " + artist);
        if (!string.IsNullOrEmpty(album)) Add("Album: " + album);
        Add($"Duration: {Time(waveform.TotalFrames)}; mixer sample rate: {PlaybackFormat.SampleRate} Hz");
        if (audio is not null)
            Add($"Audio: {audio.Codec}, {audio.SampleRate} Hz, {audio.BitrateKbps} kbps, " +
                $"VBR={audio.IsVariableBitrate}, channel mode={audio.ChannelMode}");
        Add($"Waveform: {waveform.Points.Count} points; {waveform.FramesPerPoint} PCM frames/point");
        if (rhythm is null)
        {
            Add("Rhythm: no saved result. Rescan the track.");
            return builder.ToString();
        }
        Add($"Rhythm: {(rhythm.HasBeatGrid ? "grid detected" : "grid not detected")}; status={rhythm.AnalysisStatus}");
        Add($"Global BPM estimate: {Number(rhythm.Bpm)}; confidence: {Number(rhythm.Confidence)}");
        Add($"Tempo sections: {rhythm.Sections.Length}; beat positions: {rhythm.BeatFrames.Length}; " +
            $"accepted visual transients: {rhythm.TransientFrames.Length}; onset candidates: {rhythm.CandidateOnsets}");
        Add("");
        Add("SECTIONS: start..end | BPM | confidence | onset candidates | matched beats");
        foreach (var section in rhythm.Sections)
            Add($"{Time(section.StartFrame)}..{Time(section.EndFrame)} | {Number(section.Bpm)} | " +
                $"{Number(section.Confidence)} | {section.OnsetCount} | {section.MatchedBeats}");

        Add("");
        Add("ALL TEMPO WINDOWS: start..end | BPM (0 = rejected) | phase confidence | onsets");
        foreach (var window in rhythm.Windows)
            Add($"{Time(window.StartFrame)}..{Time(window.EndFrame)} | {Number(window.Bpm)} | " +
                $"{Number(window.Confidence)} | {window.OnsetCount}");

        Add("");
        Add("BEAT POSITIONS (seconds; locally tracked, max 2048 printed):");
        Add(FormatTimes(rhythm.BeatFrames, 2048));
        Add("");
        Add("VISIBLE TRANSIENT POSITIONS (seconds; max 2048 printed):");
        Add(FormatTimes(rhythm.TransientFrames, 2048));
        Add("");
        Add("ONSET CANDIDATES (time seconds: strength / percussive accent, max 2048 printed):");
        foreach (var onset in Sample(rhythm.OnsetDiagnostics, 2048))
            Add($"{Time(onset.Frame)}: {Number(onset.Strength)} / {Number(onset.Accent)}");

        Add("");
        Add("WAVEFORM BANDS: per-bin peak values of LOW (<220 Hz), MID (220-2400 Hz), HIGH (>2400 Hz).");
        Add("These are filter estimates, not separated kick, bass or vocal instrument tracks.");
        Add("Time | peak | low | mid | high (up to 600 bins)");
        int count = waveform.Points.Count;
        int binSize = Math.Max(1, (int)Math.Ceiling(count / 600d));
        for (int i = 0; i < count; i += binSize)
        {
            var peak = waveform.Points[i];
            for (int j = i + 1; j < Math.Min(count, i + binSize); j++)
                peak = peak.Combine(waveform.Points[j]);
            Add($"{Time(Math.Min(waveform.TotalFrames, (long)i * waveform.FramesPerPoint))} | " +
                $"{Number(peak.Peak)} | {Number(peak.Low)} | {Number(peak.Mid)} | {Number(peak.High)}");
        }
        Add("");
        Add("END REPORT; attach the original audio file separately when spectral verification is required.");
        return builder.ToString();
    }

    private static IEnumerable<T> Sample<T>(IReadOnlyList<T> values, int max)
    {
        int step = Math.Max(1, (int)Math.Ceiling(values.Count / (double)max));
        for (int i = 0; i < values.Count; i += step) yield return values[i];
    }

    private static string FormatTimes(long[] frames, int max)
    {
        var builder = new StringBuilder();
        foreach (var frame in Sample(frames, max))
        {
            if (builder.Length > 0) builder.Append(", ");
            builder.Append((frame / (double)PlaybackFormat.SampleRate).ToString("0.00", Invariant));
        }
        if (frames.Length > max) builder.Append(" ... (sampled)");
        return builder.ToString();
    }
}
