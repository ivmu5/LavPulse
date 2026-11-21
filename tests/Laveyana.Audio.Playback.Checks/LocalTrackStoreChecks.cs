using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Playback;

internal static class LocalTrackStoreChecks
{
    public static async Task RunAsync(string directory)
    {
        string root = Path.Combine(directory, "persistent-library");
        var store = new LocalTrackStore(root);
        var first = await store.CopyAsync(Source("first.mp3", 1), default);
        var second = await store.CopyAsync(Source("second.mp3", 2), default);
        var duplicate = await store.CopyAsync(Source("copy.mp3", 1), default);
        Require(first.LocalPath == duplicate.LocalPath, "Identical imports duplicated local media.");
        await store.SaveQueueAsync(LocalTrackStore.Snapshot([second, first], first));

        var preparation = new TrackPreparation(store.PcmDirectory);
        var scanned = await preparation.AnalyzeAsync(second.LocalPath!, default);
        await store.SaveAnalysisAsync(second, scanned.Waveform, scanned.Info, null, default);
        Require(!Directory.Exists(store.PcmDirectory), "Compact analysis allocated a PCM directory.");
        var ready = await preparation.ImportAsync(first, default);
        await store.SaveAnalysisAsync(first, ready.Waveform, ready.AudioInfo, ready.Path, default);
        await store.SaveAnalysisAsync(first, scanned.Waveform, scanned.Info, null, default);

        var reopened = new LocalTrackStore(root);
        var (tracks, current) = await reopened.ReadQueueAsync(default);
        Require(tracks.Select(file => file.Name).SequenceEqual(["second.mp3", "first.mp3"]) &&
            ReferenceEquals(current, tracks[1]), "Queue order or current track changed after reopening.");
        Require(reopened.HasAnalysis(tracks[0]), "Compact analysis was not indexed after reopening.");
        var cached = await reopened.ReadAnalysisAsync(tracks[1], default);
        Require(cached is not null && reopened.FindPcm(cached) == ready.Path,
            "Completed PCM was lost or replaced by a compact scan.");

        // After PCM eviction the saved waveform remains usable when rebuilding the playback cache.
        await File.WriteAllBytesAsync(ready.Path, [1, 2]);
        Require(reopened.FindPcm(cached!) is null, "An incomplete cache was treated as playable.");
        ready.Delete();
        var waveform = cached!.Waveform;
        var rebuilt = await preparation.ImportAsync(tracks[1], null, (waveform, cached.AudioInfo), default);
        Require(ReferenceEquals(rebuilt.Waveform, waveform), "Rebuilding PCM recomputed a valid waveform.");
        rebuilt.Delete();

        await CheckScanOrderAsync(tracks);
        Console.WriteLine("OK: durable track copies, queue restore, saved waveform reuse and ordered analysis.");
    }

    private static async Task CheckScanOrderAsync(TrackFile[] tracks)
    {
        var order = new List<string>();
        var prepared = new HashSet<TrackFile>();
        var analyzed = new HashSet<TrackFile>();
        Task Prepare(TrackFile file)
        {
            order.Add("pcm:" + file.Name);
            prepared.Add(file);
            analyzed.Add(file);
            return Task.CompletedTask;
        }
        Task Analyze(TrackFile file)
        {
            order.Add("scan:" + file.Name);
            analyzed.Add(file);
            return Task.CompletedTask;
        }

        var lookahead = new QueueTrackAnalysis(prepared.Contains, Prepare, () => { }, default, analyzed.Contains, Analyze);
        await lookahead.Update([tracks[0]], allowed: true, tracks)!;
        lookahead.Finish();
        Require(order.SequenceEqual(["pcm:second.mp3", "scan:first.mp3"]) &&
            lookahead.Update([tracks[0]], allowed: true, tracks) is null,
            "The worker skipped queue priority or repeated completed analysis.");
    }

    private static TrackFile Source(string name, byte marker) => new(name, async (output, token) =>
    {
        using var source = TrackPreparationChecks.CreateMp3();
        await source.CopyToAsync(output, token);
        output.WriteByte(marker);
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
