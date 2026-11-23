using System.Diagnostics;
using Laveyana.LavPulse.Controls;
using Laveyana.Audio.Playback;

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

    private static async Task CheckManualScanControlsAsync()
    {
        TrackFile[] files = [TrackFile.FromPath("first.mp3"), TrackFile.FromPath("second.mp3"), TrackFile.FromPath("third.mp3")];
        using var skip = new TrackScanBatch(default);
        var timeout = TimeSpan.FromSeconds(5);
        var preview = new TaskCompletionSource<(TrackFile, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new List<TrackFile>();
        var pass = skip.RunAsync(files, async (file, token) =>
        {
            if (file == files[0]) await Task.Delay(Timeout.Infinite, token);
            else if (file == files[1]) await finishSecond.Task.WaitAsync(token);
            else
            {
                thirdStarted.SetResult();
                throw new InvalidDataException("Simulated scan error.");
            }
        }, (file, _, _, active) => preview.TrySetResult((file, active)), completed.Add);
        Require(await preview.Task.WaitAsync(timeout) == (files[0], 2) && !thirdStarted.Task.IsCompleted,
            "The scan did not start exactly two tracks and preview the first.");
        skip.Skip();
        await thirdStarted.Task.WaitAsync(timeout);
        finishSecond.SetResult();
        Require(await pass.WaitAsync(timeout) == (1, 1, 1) && completed.SequenceEqual([files[1]]),
            "Skipping the preview interrupted another active scan or miscounted a failed scan.");

        using var stop = new TrackScanBatch(default);
        int work = 0;
        var stopped = stop.RunAsync(files, (_, token) =>
        {
            work++;
            return Task.Delay(Timeout.Infinite, token);
        }, (_, _, _, _) => { }, _ => throw new InvalidOperationException("Cancelled scan was counted."));
        stop.Stop();
        Require(await stopped.WaitAsync(timeout) == (0, 0, 0) && work == 2,
            "Stopping a parallel scan started a queued track or counted a skip.");
        Console.WriteLine("OK: parallel manual scanning, targeted skips, independent failures, and stopping both workers.");
    }
}
