using Laveyana.LavPulse.Tracks;
using Laveyana.Audio.Playback;

internal static partial class Program
{
    private static void CheckQueueChangesDuringPreparation()
    {
        var files = Enumerable.Range(0, 4).Select(i => TrackFile.FromPath($"queue-{i}.mp3")).ToArray();
        var queue = new PlaybackQueue();
        queue.AddRange(files);

        Require(queue.Move(files[2], files[0], after: false, _ => true), "Could not reorder before a track was ready.");
        Require(queue.TryRemove(files[3], _ => true), "Could not remove a track before playback started.");
        Require(queue.Tracks.SequenceEqual(new[] { files[2], files[0], files[1] }), "Queue edits changed the wrong rows.");

        queue.SetCurrent(files[2]);
        Require(!queue.TryRemove(files[1], _ => false, refreshSuccessor: true) && queue.Tracks.Contains(files[1]),
            "A selected row was removed even though its replacement could not be set.");
        TrackFile? replacement = null;
        Require(queue.TryRemove(files[1], next =>
        {
            replacement = next;
            return true;
        }, refreshSuccessor: true) && replacement == files[0], "Removing a selected row did not restore the default successor.");
        Console.WriteLine("OK: queue edits during preparation and replacing a removed selection.");
    }

    private static void CheckQueueClear()
    {
        var files = Enumerable.Range(0, 3).Select(i => TrackFile.FromPath($"clear-{i}.mp3")).ToArray();
        var queue = new PlaybackQueue();
        queue.AddRange(files);
        queue.SetCurrent(files[0]);
        queue.Clear();
        Require(queue.Tracks.Count == 0 && files.All(file => !queue.Contains(file)) &&
            queue.Current == files[0] && queue.Next is null && queue.Previous is null && queue.GetUpcoming(10).Length == 0,
            "Clearing the queue left rows or lost the active track.");

        queue.AddRange([files[2]]);
        queue.SetCurrent(files[1]); // A mix that was already playing finishes after clear.
        Require(queue.Current == files[1] && !queue.Contains(files[1]) &&
            queue.Next == files[2] && queue.GetUpcoming(10).SequenceEqual(files[2..]),
            "Completing a mix after clear skipped the first newly queued track.");
        queue.AddRange([files[1]]);
        Require(queue.Tracks.SequenceEqual([files[1], files[2]]) && queue.Next == files[2],
            "A detached track could not be added back before its successor.");

        var unloaded = new PlaybackQueue();
        unloaded.AddRange(files);
        unloaded.Clear();
        unloaded.AddRange(files);
        Require(unloaded.Current is null && unloaded.Next == files[0] && unloaded.Tracks.SequenceEqual(files),
            "Clearing before playback left an invalid boundary or stale membership.");
        Console.WriteLine("OK: queue clear, detached playback boundary, and adding tracks again.");
    }

    private static async Task CheckInterruptedQueueAnalysis()
    {
        var file = TrackFile.FromPath("interrupted.mp3");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int attempts = 0;
        bool prepared = false;
        var analysis = new QueueTrackAnalysis(_ => prepared, _ =>
        {
            if (++attempts == 1) return Task.FromCanceled(new CancellationToken(canceled: true));
            prepared = true;
            return Task.CompletedTask;
        }, () => { }, stop.Token);

        await analysis.Update([file], allowed: true, pcmLookahead: 1, analysisLookahead: 0)!.WaitAsync(stop.Token);
        Require(prepared && attempts == 2, "A superseded foreground decode prevented the background retry.");
        attempts = 0;
        var cancelled = new QueueTrackAnalysis(_ => false, _ =>
        {
            attempts++;
            return Task.FromCanceled(new CancellationToken(true));
        }, () => { }, stop.Token);
        await cancelled.Update([file], true, 1, 0)!.WaitAsync(stop.Token);
        cancelled.Finish();
        Require(attempts == 3 && cancelled.Update([file], true, 1, 0) is null,
            "Persistent cancellation caused unbounded retry.");
        var missing = new QueueTrackAnalysis(_ => false, _ => Task.CompletedTask, () => { }, stop.Token);
        await missing.Update([file], true, 1, 0)!.WaitAsync(stop.Token);
        missing.Finish();
        Require(missing.Update([file], true, 1, 0) is null,
            "A missing published preparation caused unbounded retry.");
        Console.WriteLine("OK: background analysis retries, bounded cancellation and missing result isolation.");
    }

    private static async Task CheckQueueAnalysisLookahead()
    {
        var files = Enumerable.Range(0, 15).Select(i => TrackFile.FromPath($"queue-{i}.mp3")).ToArray();
        var queue = new PlaybackQueue();
        queue.AddRange(files);
        queue.SetCurrent(files[0]);
        Require(queue.GetUpcoming(10).SequenceEqual(files[1..11]), "Lookahead must start after the current track.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new System.Collections.Concurrent.ConcurrentDictionary<TrackFile, bool>();
        var order = new List<TrackFile>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var analysis = new QueueTrackAnalysis(prepared.ContainsKey, async file =>
        {
            order.Add(file);
            if (file == files[1])
            {
                started.SetResult();
                await release.Task.WaitAsync(stop.Token);
            }
            if (file == files[3]) throw new InvalidDataException("Damaged MP3.");
            prepared[file] = true;
        }, () => { }, stop.Token);

        var first = analysis.Update(queue.GetUpcoming(10), allowed: true, pcmLookahead: 10, analysisLookahead: 0)!;
        await started.Task.WaitAsync(stop.Token);
        Require(queue.TryRemove(files[2], _ => true), "Could not remove a pending track.");
        Require(analysis.Update(queue.GetUpcoming(10), allowed: false, pcmLookahead: 10, analysisLookahead: 0) is null, "A second worker started.");
        release.SetResult();
        await first.WaitAsync(stop.Token);
        analysis.Finish();
        Require(order.SequenceEqual(files[1..2]), "A new decode started after the background execution window expired.");

        var resumed = analysis.Update(queue.GetUpcoming(10), allowed: true, pcmLookahead: 10, analysisLookahead: 0)!;
        await resumed.WaitAsync(stop.Token);
        analysis.Finish();
        Require(order.SequenceEqual(new[] { files[1] }.Concat(files[3..12])),
            "Lookahead ignored a removal, skipped viable tracks after an error, or exceeded ten positions.");
        Require(analysis.Update(queue.GetUpcoming(10), allowed: true, pcmLookahead: 10, analysisLookahead: 0) is null, "Prepared or failed tracks were scheduled again.");

        queue.SetCurrent(files[3]);
        Require(queue.TryRemove(files[3], _ => true), "Could not remove the playing row.");
        Require(queue.GetUpcoming(10).SequenceEqual(files[4..14]), "Lookahead lost the detached playback boundary.");
        await analysis.Update(queue.GetUpcoming(10), allowed: true, pcmLookahead: 10, analysisLookahead: 0)!.WaitAsync(stop.Token);
        analysis.Finish();
        Require(order.TakeLast(2).SequenceEqual(files[12..14]) && !prepared.ContainsKey(files[14]),
            "Advancing the queue did not refill only the new lookahead positions.");
        Console.WriteLine("OK: rolling queue analysis, background pause and resume, and failed-track isolation.");
    }

    /// <summary>Switching directly to any track changes the history boundary, not the playlist order.</summary>
    private static void CheckQueuePreservesOrderOnDirectSelection()
    {
        var files = Enumerable.Range(0, 6).Select(i =>
            TrackFile.FromPath(Path.Combine("unused", $"queue-{i}.mp3"))).ToArray();
        var queue = new PlaybackQueue();
        queue.AddRange(files);
        queue.SetCurrent(files[0]);
        Require(queue.Next == files[1], "The initial successor is incorrect.");

        // Simulate clicking a later row and completing the transition.
        queue.SetCurrent(files[4]);
        Require(queue.Tracks.SequenceEqual(files), "Selecting a later track reordered the queue.");
        Require(queue.PlayedCount == 4 && queue.Next == files[5], "History boundary moved incorrectly.");

        // Clicking a gray row plays it again, leaving every later row upcoming.
        queue.SetCurrent(files[1]);
        Require(queue.Tracks.SequenceEqual(files), "Replaying history reordered the queue.");
        Require(queue.PlayedCount == 1 && queue.Previous == files[0] && queue.Next == files[2],
            "Replaying history did not restore the proper gray/upcoming boundary.");
    }

    private static async Task CheckCompactAnalysisPassAsync()
    {
        var queue = Enumerable.Range(0, 515).Select(i => TrackFile.FromPath($"compact-{i}.mp3")).ToArray();
        var analyzed = queue[..512].ToHashSet();
        var order = new List<TrackFile>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var analysis = new QueueTrackAnalysis(_ => false, _ => Task.CompletedTask, () => { }, stop.Token,
            analyzed.Contains, file => { order.Add(file); analyzed.Add(file); return Task.CompletedTask; });

        Require(analysis.Update(queue[..100], true, 0, 100) is null,
            "Compact analysis escaped its configured horizon.");
        await analysis.Update(queue[510..], true, 0, 3)!.WaitAsync(stop.Token);
        analysis.Finish();
        Require(order.SequenceEqual(new[] { queue[512] }) && !analyzed.Contains(queue[513]),
            "Compact analysis did not stop at the horizon.");
        await analysis.Update(queue[510..], true, 0, 5)!.WaitAsync(stop.Token);
        analysis.Finish();
        Require(order.SequenceEqual(queue[^3..]), "Expanding the horizon did not resume analysis.");
        analyzed.Remove(queue[510]);
        await analysis.Update(queue[510..], true, 0, 3)!.WaitAsync(stop.Token);
        analysis.Finish();
        Require(ReferenceEquals(order[^1], queue[510]), "A fresh pass skipped invalidated analysis.");
        Console.WriteLine("OK: bounded compact analysis, horizon changes and invalidation.");
    }
}
