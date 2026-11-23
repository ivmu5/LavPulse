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
        Console.WriteLine("OK: queue follow interval, final-row alignment, and editor centering.");
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
        Console.WriteLine("OK: one-line queue marquee with fixed baseline and horizontal-only motion.");
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
        }, (active, _, _) => activeTracks.TrySetResult(active), completed.Add);
        Require((await activeTracks.Task.WaitAsync(timeout)).SequenceEqual(files[..2]) && !thirdStarted.Task.IsCompleted,
            "The scan did not show both active tracks or started a third track too early.");
        skip.Skip(files[1]);
        await thirdStarted.Task.WaitAsync(timeout);
        finishFirst.SetResult();
        Require(await pass.WaitAsync(timeout) == (1, 2, 1) && completed.SequenceEqual([files[0]]),
            "Skipping the second row interrupted another active scan or reported already analyzed work as new.");

        using var stop = new TrackScanBatch(3, default);
        int work = 0;
        var stopped = stop.RunAsync(files, async (_, token) =>
        {
            work++;
            await Task.Delay(Timeout.Infinite, token);
            return true;
        }, (_, _, _) => { }, _ => throw new InvalidOperationException("Cancelled scan was counted."));
        stop.Stop();
        Require(await stopped.WaitAsync(timeout) == (0, 0, 0) && work == 3,
            "Stopping a parallel scan started a queued track or counted a skip.");

        var slots = new TrackAnalysisSlots(2);
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
