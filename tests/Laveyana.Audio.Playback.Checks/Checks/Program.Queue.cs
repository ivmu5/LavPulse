using Laveyana.Audio.Maui.Tracks;
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

        await analysis.Update([file], allowed: true)!.WaitAsync(stop.Token);
        Require(prepared && attempts == 2, "A superseded foreground decode prevented the background retry.");
        Console.WriteLine("OK: background analysis retries after a selection is superseded.");
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

        var first = analysis.Update(queue.GetUpcoming(10), allowed: true)!;
        await started.Task.WaitAsync(stop.Token);
        Require(queue.TryRemove(files[2], _ => true), "Could not remove a pending track.");
        Require(analysis.Update(queue.GetUpcoming(10), allowed: false) is null, "A second worker started.");
        release.SetResult();
        await first.WaitAsync(stop.Token);
        analysis.Finish();
        Require(order.SequenceEqual(files[1..2]), "A new decode started after the background execution window expired.");

        var resumed = analysis.Update(queue.GetUpcoming(10), allowed: true)!;
        await resumed.WaitAsync(stop.Token);
        analysis.Finish();
        Require(order.SequenceEqual(new[] { files[1] }.Concat(files[3..12])),
            "Lookahead ignored a removal, skipped viable tracks after an error, or exceeded ten positions.");
        Require(analysis.Update(queue.GetUpcoming(10), allowed: true) is null, "Prepared or failed tracks were scheduled again.");

        queue.SetCurrent(files[3]);
        Require(queue.TryRemove(files[3], _ => true), "Could not remove the playing row.");
        Require(queue.GetUpcoming(10).SequenceEqual(files[4..14]), "Lookahead lost the detached playback boundary.");
        await analysis.Update(queue.GetUpcoming(10), allowed: true)!.WaitAsync(stop.Token);
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
        int checks = 0;
        var analysis = new QueueTrackAnalysis(_ => false, _ => Task.CompletedTask, () => { }, stop.Token,
            file =>
            {
                checks++;
                return analyzed.Contains(file);
            },
            file =>
            {
                order.Add(file);
                analyzed.Add(file);
                return Task.CompletedTask;
            });

        await analysis.Update([], allowed: true, queue)!.WaitAsync(stop.Token);
        analysis.Finish();
        int firstPassChecks = checks;
        Require(order.SequenceEqual(queue[^3..]) && firstPassChecks <= queue.Length + 10,
            "The compact scan repeatedly traversed the already analyzed tracks.");

        // A saved file can be stale or removed after the previous pass.
        analyzed.Remove(queue[0]);
        await analysis.Update([], allowed: true, queue)!.WaitAsync(stop.Token);
        analysis.Finish();
        Require(ReferenceEquals(order[^1], queue[0]), "A fresh pass skipped invalidated analysis.");
        Console.WriteLine($"OK: compact analysis ({firstPassChecks} status checks across {queue.Length} tracks).");
    }
}
