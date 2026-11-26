using System.Diagnostics;
using Laveyana.LavPulse.Controls;
using Laveyana.Audio.Playback;
using Laveyana.LavPulse.Tracks;

internal static partial class Program
{
    private static void CheckQueueViewport()
    {
        long last = Stopwatch.Frequency;
        Require(QueueViewport.CanFollow(null, last) &&
            !QueueViewport.CanFollow(last, last + 9 * Stopwatch.Frequency) &&
            QueueViewport.CanFollow(last, last + 10 * Stopwatch.Frequency),
            "Queue follow must respect the full idle interval.");

        const int rowHeight = TrackListLayout.RowHeight;
        const int stride = TrackListLayout.Stride;
        const int viewport = 236;
        double tenRows = TrackListLayout.RowsHeight(10);
        double content = tenRows + QueueViewport.EndPadding(viewport, rowHeight);
        Require(QueueViewport.TrackOffset(9, stride, rowHeight, viewport, content, center: false) == 9 * stride,
            "The last track could not appear first while retaining the played history.");
        Require(QueueViewport.TrackOffset(5, stride, rowHeight, viewport, tenRows, center: true) ==
            5 * stride - (viewport - rowHeight) / 2,
            "The queue editor did not center the current track.");
        Require(QueueViewport.TrackOffset(0, stride, rowHeight, viewport, rowHeight, center: true) == 0 &&
            QueueViewport.TrackOffset(9, stride, rowHeight, viewport, tenRows, center: true) == tenRows - viewport,
            "The queue editor scrolled beyond the available rows.");
        Require(TrackScanHistory.CanFollow(null, last, 0) &&
            !TrackScanHistory.CanFollow(last, last + 60 * Stopwatch.Frequency, 0) &&
            !TrackScanHistory.CanFollow(last, last + 4 * Stopwatch.Frequency, 5) &&
            TrackScanHistory.CanFollow(last, last + 5 * Stopwatch.Frequency, 5),
            "Scan history did not respect its independent return interval.");
        var history = new TrackScanHistory();
        var files = Enumerable.Range(0, 2000).Select(i => TrackFile.FromPath($"scan-{i}.mp3")).ToArray();
        history.AddActive(files[..3]);
        for (int i = 0; i < files.Length; i++)
        {
            history.Complete(new(files[i], TrackScanOutcome.Scanned, 1000, 1200, TimeSpan.FromSeconds(1)));
            if (i + 3 < files.Length) history.AddActive([files[i + 3]]);
        }
        Require(history.CompletedCount == 2000 && history.ActiveCount == 0 &&
            history.Items.Select(entry => entry.File).SequenceEqual(files) &&
            history.Items.All(entry => entry.Result is { BeforeBytes: 1000, AfterBytes: 1200 }),
            "Completed scan history was truncated or lost its statistics.");
        history.Clear();
        history.AddActive(files[..3]);
        history.Complete(new(files[1], TrackScanOutcome.Scanned, 1000, 1200, TimeSpan.Zero));
        Require(history.Items.Select(entry => entry.File).SequenceEqual(new[] { files[1], files[0], files[2] }) &&
            history.CompletedCount == 1 && history.ActiveCount == 2,
            "Out-of-order completion did not move to the history prefix.");
        double scanContent = TrackListLayout.RowsHeight(3) + QueueViewport.EndPadding(viewport, rowHeight);
        Require(QueueViewport.TrackOffset(history.CompletedCount, stride, rowHeight, viewport, scanContent, false)
            == stride, "The first active scan could not align with the viewport top.");
        Console.WriteLine("OK: queue follow, independent scan idle interval, complete history and active alignment.");
    }

    private static void CheckTrackListLayout()
    {
        const int stride = TrackListLayout.Stride;
        const int viewport = 4 * stride;
        Require(TrackListLayout.RealizedRange(1000, 100 * stride, viewport) == (98, 106),
            "A track list attached rows outside the viewport and its overscan.");
        Require(TrackListLayout.RealizedRange(0, 0, viewport) == (0, 0) &&
            TrackListLayout.RealizedRange(3, 2 * stride, viewport).End == 3,
            "An empty or short track list realized missing rows.");

        const int offset = 8 * stride;
        double survivingRows = TrackListLayout.RowsHeight(10);
        double padding = TrackListLayout.PaddingToKeepOffset(10, offset, viewport);
        Require(survivingRows + padding - viewport == offset &&
            TrackListLayout.PaddingToKeepOffset(50, offset, viewport) == 0,
            "Collapsing a folder changed its scroll offset or retained unnecessary space.");
        Console.WriteLine("OK: shared track list virtualization and folder collapse offset.");
    }

    private static void CheckQueueMarqueeMotion()
    {
        const float overflow = 180;
        const float centerY = 32;
        const float fontSize = 13;
        float baseline = QueueMarqueeMotion.Baseline(centerY, fontSize);
        Require(QueueMarqueeMotion.Offset(0, 10) == 0 &&
            QueueMarqueeMotion.Offset(-1, 10) == 0 &&
            QueueMarqueeMotion.Offset(overflow, 0) == 0,
            "A non-overflowing queue label tried to scroll.");

        double travel = overflow / 18d;
        double cycle = 2 * travel + 2.4;
        Require(QueueMarqueeMotion.Offset(overflow, 1.2) == 0 &&
            Math.Abs(QueueMarqueeMotion.Offset(overflow, 1.2 + travel) - overflow) < .001 &&
            Math.Abs(QueueMarqueeMotion.Offset(overflow, cycle) - 0) < .001,
            "The queue marquee did not pause at both ends.");

        for (double time = 0; time <= cycle * 3; time += 1d / 120)
        {
            float x = QueueMarqueeMotion.Offset(overflow, time);
            Require(float.IsFinite(x) && x >= 0 && x <= overflow &&
                QueueMarqueeMotion.Baseline(centerY, fontSize) == baseline,
                "Queue marquee motion changed the vertical baseline or exceeded the horizontal clip.");
        }
        string title = "Robbe, NIKSTER & ExtraGirl - Melody (Extended Mix)";
        var chunks = QueueTextFragments.Split(title);
        Require(chunks.Length > 2 && string.Concat(chunks) == title &&
            chunks.All(chunk => chunk.Length > 0 && !chunk.Contains('\n') && !chunk.Contains('\r')),
            "Long names must be rendered as short unbreakable fragments on the same baseline.");
        string withEmoji = "Test 👩‍🎤🎧 on Windows and Android";
        Require(string.Concat(QueueTextFragments.Split(withEmoji)) == withEmoji,
            "Marquee segmentation corrupted a multi-codepoint Unicode grapheme.");
        Console.WriteLine("OK: queue marquee does not wrap long titles or Unicode text.");
    }

    private static async Task CheckManualScanControlsAsync()
    {
        Require(TrackAnalysisParallelism.Resolve(8, processors: 2) == 1 &&
            TrackAnalysisParallelism.Resolve(8, processors: 4) == 3 &&
            TrackAnalysisParallelism.Resolve(8, processors: 64) == 8,
            "A saved manual limit exceeded the app or CPU capacity.");
        Require(TrackAnalysisParallelism.Resolve(0, processors: 1) == 1 &&
            TrackAnalysisParallelism.Resolve(0, processors: 2) == 1 &&
            TrackAnalysisParallelism.Resolve(0, processors: 4) == 2 &&
            TrackAnalysisParallelism.Resolve(0, processors: 8) == 4 &&
            TrackAnalysisParallelism.Resolve(0, processors: 64) == 8 &&
            TrackAnalysisParallelism.Resolve(2, processors: 8) == 2,
            "Automatic scans used the wrong CPU limit or overrode a valid manual selection.");

        TrackFile[] files = [TrackFile.FromPath("first.mp3"), TrackFile.FromPath("second.mp3"),
            TrackFile.FromPath("third.mp3"), TrackFile.FromPath("fourth.mp3")];
        using var skip = new TrackScanBatch(2, default);
        var timeout = TimeSpan.FromSeconds(5);
        var activeTracks = new TaskCompletionSource<IReadOnlyList<TrackFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new List<TrackFile>();
        var results = new List<TrackScanResult>();
        var pass = skip.RunAsync(files, async (file, token) =>
        {
            if (file == files[0]) await finishFirst.Task.WaitAsync(token);
            else if (file == files[1]) await Task.Delay(Timeout.Infinite, token);
            else if (file == files[2])
            {
                thirdStarted.SetResult();
                throw new InvalidDataException("Simulated scan error.");
            }
            return file != files[3]; // Its analysis became available between listing and execution.
        }, (active, _, _) => activeTracks.TrySetResult(active), completed.Add,
            (file, _) => ValueTask.FromResult<long?>(file == files[0] && finishFirst.Task.IsCompleted ? 1600 : 1000),
            results.Add);
        Require((await activeTracks.Task.WaitAsync(timeout)).SequenceEqual(files[..2]) && !thirdStarted.Task.IsCompleted,
            "The scan did not show both active tracks or started a third track too early.");
        skip.Skip(files[1]);
        await thirdStarted.Task.WaitAsync(timeout);
        finishFirst.SetResult();
        Require(await pass.WaitAsync(timeout) == (1, 2, 1) && completed.SequenceEqual([files[0]]),
            "Skipping the second row interrupted another active scan or reported already analyzed work as new.");
        var success = results.Single(result => ReferenceEquals(result.File, files[0]));
        Require(results.Count == 4 && success.Outcome == TrackScanOutcome.Scanned &&
            success.BeforeBytes == 1000 && success.AfterBytes == 1600 &&
            success.Elapsed >= TimeSpan.Zero &&
            results.Count(result => result.Outcome == TrackScanOutcome.Skipped) == 2 &&
            results.Count(result => result.Outcome == TrackScanOutcome.Failed) == 1,
            "Scan results lost per-track sizes, elapsed time or completion status.");

        using var stop = new TrackScanBatch(3, default);
        int work = 0;
        var stoppedResults = new List<TrackScanResult>();
        var stopped = stop.RunAsync(files, async (_, token) =>
        {
            work++;
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, (_, _, _) => { }, _ => throw new InvalidOperationException("Cancelled scan was counted."),
            finishedRow: stoppedResults.Add);
        stop.Stop();
        Require(await stopped.WaitAsync(timeout) == (0, 0, 0) && work == 3,
            "Stopping a parallel scan started a queued track or counted a skip.");
        Require(stoppedResults.Count == 3 && stoppedResults.All(result =>
            result.Outcome == TrackScanOutcome.Stopped && result.Elapsed >= TimeSpan.Zero),
            "Cancelled scan rows were not finalized.");
        using var unavailableSize = new TrackScanBatch(1, default);
        var measured = new List<TrackScanResult>();
        var sizeFailure = await unavailableSize.RunAsync([files[0]], (_, _) => Task.FromResult(true),
            (_, _, _) => { }, _ => { },
            (_, _) => throw new IOException("Simulated unavailable storage"), measured.Add);
        Require(sizeFailure == (1, 0, 0) && measured.Single() is
            { BeforeBytes: null, AfterBytes: null, Outcome: TrackScanOutcome.Scanned },
            "Failed storage measurement changed a successful analysis outcome.");

        var slots = new TrackAnalysisSlots(2);
        Require(!slots.TryAcquireHelper(1) && slots.TryAcquireHelper(4) && !slots.TryAcquireHelper(4),
            "The optional analysis lane exceeded its resource limit.");
        slots.ReleaseHelper();
        Require(slots.TryAcquireHelper(4), "The analysis lane was not released.");
        slots.ReleaseHelper();
        await slots.AcquireAsync(scan: true, default);
        await slots.AcquireAsync(scan: true, default);
        var waitingScan = slots.AcquireAsync(scan: true, default).AsTask();
        Require(!waitingScan.IsCompleted && slots.AcquireAsync(scan: false, default).IsCompletedSuccessfully,
            "Scans exceeded their limit or blocked the reserved PCM slot.");
        slots.SetParallelism(3);
        await waitingScan.WaitAsync(timeout);
        slots.SetParallelism(1);
        var waitingAfterDecrease = slots.AcquireAsync(scan: true, default).AsTask();
        slots.Release(scan: true);
        slots.Release(scan: true);
        Require(!waitingAfterDecrease.IsCompleted, "The decreased limit allowed an extra scan.");
        slots.Release(scan: true);
        await waitingAfterDecrease.WaitAsync(timeout);
        slots.Release(scan: true);
        slots.Release(scan: false);
        Console.WriteLine("OK: configurable parallel scanning, active tracks, targeted skips, and cancellation.");
    }
}
