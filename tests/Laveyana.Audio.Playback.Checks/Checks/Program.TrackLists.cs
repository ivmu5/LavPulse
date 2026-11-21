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
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayed = new List<int>();
        var completed = new List<TrackFile>();
        var pass = skip.RunAsync(files, async (file, token) =>
        {
            if (file != files[1]) return;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, (_, position, _) => displayed.Add(position), completed.Add);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        skip.Skip();
        Require(await pass == (2, 1, 0) && displayed.SequenceEqual([1, 2, 3]) &&
            completed.SequenceEqual([files[0], files[2]]), "Skipping did not resume with the following track.");

        using var stop = new TrackScanBatch(default);
        int work = 0;
        var stopped = stop.RunAsync(files, (_, token) =>
        {
            work++;
            return Task.Delay(Timeout.Infinite, token);
        }, (_, _, _) => { }, _ => throw new InvalidOperationException("Cancelled scan was counted."));
        stop.Stop();
        Require(await stopped == (0, 0, 0) && work == 1, "Stopping started more tracks or counted a skip.");
        Console.WriteLine("OK: skipping and stopping a manual track scan.");
    }
}
