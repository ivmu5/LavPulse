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
        CheckAdaptivePanelLayout();
        const int stride = TrackListLayout.Stride;
        const int viewport = 4 * stride;
        int[] parents = TrackListLayout.ParentIndexes([0,1,2,3,4,3,2,1,2]);
        Span<int> pinned = stackalloc int[3];
        Require(parents.SequenceEqual(new[]{-1,0,1,2,3,2,1,0,7}),
            "Flattened tree lost its parent chain.");
        Require(TrackListLayout.PinnedParents(parents,0,pinned)==0 &&
            TrackListLayout.PinnedParents(parents,4*stride,pinned)==3 &&
            pinned.SequenceEqual(new[]{1,2,3}),
            "Deep hierarchy did not retain the nearest three parents.");
        Require(TrackListLayout.PinnedParents(parents,7*stride,pinned)==1 && pinned[0]==0 &&
            TrackListLayout.PinnedParents(parents,8*stride,pinned)==2 && pinned[..2].SequenceEqual(new[]{0,7}),
            "Pinned ancestors did not switch when scrolling into a sibling.");
        Require(TrackListLayout.PinnedCapacity(3 * stride + TrackListLayout.RowHeight)==3 &&
            TrackListLayout.PinnedCapacity(2 * stride + TrackListLayout.RowHeight)==2 &&
            TrackListLayout.PinnedCapacity(TrackListLayout.RowHeight)==0,
            "Sticky rows consumed the entire viewport.");
        Require(TrackListLayout.DropdownPosition(40,20,44,800,600,240,100)==(40d,68d) &&
            TrackListLayout.DropdownPosition(720,510,44,800,600,240,100)==(552d,406d) &&
            TrackListLayout.DropdownPosition(2,40,44,240,180,224,152)==(8d,20d),
            "Dropdown did not align to its trigger, flip above, or clamp.");
        Require(TrackListLayout.ContextMenuPosition(30,40,800,600,300,250)==(34d,44d) &&
            TrackListLayout.ContextMenuPosition(790,590,800,600,300,250)==(486d,336d) &&
            TrackListLayout.ContextMenuPosition(160,150,320,300,288,268)==(24d,24d),
            "Context menu did not anchor, flip or clamp within its viewport.");
        Require(TrackListLayout.PinnedAreaHeight(0)==0 &&
            TrackListLayout.PinnedAreaHeight(1)==stride &&
            TrackListLayout.PinnedAreaHeight(3)==3*stride &&
            TrackListLayout.PinnedAreaHeight(4)==3*stride,
            "Pinned mask did not cover row gaps or respect the three-parent cap.");
        for (int i=0;i<stride;i++)
        {
            Require(TrackListLayout.PinnedParents(parents,4*stride+i,pinned)==3 &&
                pinned.SequenceEqual(new[]{1,2,3}),
                "Scrolling within a row changed the pinned chain.");
        }
        Require(TrackListLayout.CollapseAnchor(10,4*stride)==7*stride &&
            TrackListLayout.CollapseAnchor(1,4*stride)==0,
            "Collapsed row can be hidden beneath pinned ancestors.");
        Require(TrackListLayout.PinnedParents([],0,pinned)==0 &&
            TrackListLayout.PinnedParents(parents,double.NaN,pinned)==0,
            "Empty or invalid scroll state produced pinned ancestors.");
        Require(TrackListLayout.PinnedParents(TrackListLayout.ParentIndexes([0,1,1,2]),3*stride,pinned)==2 &&
            pinned[..2].SequenceEqual(new[]{0,2}),"Collapsed tree retained a removed ancestor.");
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

    private static void CheckAdaptivePanelLayout()
    {
        var portrait = AdaptivePanelLayout.Measure(900, 1200);
        Require(!portrait.IsWide && portrait.Width == 660 && portrait.ContentWidth == 628,
            "Portrait unexpectedly allocated side rails.");
        Require(!AdaptivePanelLayout.Measure(820, 400).IsWide &&
            !AdaptivePanelLayout.Measure(900, 900).IsWide &&
            AdaptivePanelLayout.Measure(double.NaN, 500).Width == 0 &&
            AdaptivePanelLayout.Measure(900, 0).Width == 0,
            "Invalid, square or narrow windows activated the wide layout.");
        var boundary = AdaptivePanelLayout.Measure(AdaptivePanelLayout.MinimumWideWidth, 400);
        Require(boundary.IsWide && boundary.SideWidth == AdaptivePanelLayout.MinimumSide &&
            boundary.ContentWidth == AdaptivePanelLayout.MinimumContent,
            "Wide layout boundary compressed the main panel.");
        for (int width = 320; width <= 2400; width++)
        {
            var size = AdaptivePanelLayout.Measure(width, 400);
            Require(size.Width <= width && size.ContentWidth <= 628 &&
                (!size.IsWide || (size.SideWidth is >= 180 and <= 240 && size.ContentWidth >= 420)),
                "Adaptive layout exceeded its window or rail constraints.");
        }
        var large = AdaptivePanelLayout.Measure(1920, 1080);
        Require(large.Width == AdaptivePanelLayout.MaximumWidth && large.ContentWidth == 628 &&
            large.SideWidth == 240, "Large window did not preserve the central content width.");
    }

    private static void CheckQueueMarqueeMotion()
    {
        const float overflow = 180;
        const float centerY = 32;
        const float fontSize = 13;
        float baseline = QueueMarqueeMotion.Baseline(centerY, fontSize);
        foreach (bool status in new[] { false, true })
        foreach (bool artist in new[] { false, true })
        {
            int count = 1 + (status ? 1 : 0) + (artist ? 1 : 0);
            var (top, height) = QueueMarqueeMotion.LineLayout(4, 44, status, artist);
            Require(Math.Abs(top + count * height / 2 - 26) < .001 &&
                top >= 4 && top + count * height <= 48.001,
                "Queue text group is not vertically centered.");
        }
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
        try { using var invalid = new TrackScanBatch(0, default); throw new Exception("Zero scan parallelism accepted."); }
        catch (ArgumentOutOfRangeException) { }
        try { using var invalid = new TrackScanBatch(-1, default); throw new Exception("Negative scan parallelism accepted."); }
        catch (ArgumentOutOfRangeException) { }
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
