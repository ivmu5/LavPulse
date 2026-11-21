using System.Diagnostics;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

internal sealed partial class TrackQueueView
{
    private QueueTransition? _transition;
    private readonly long _start = Stopwatch.GetTimestamp();

    private int _animation;
    private bool _handoff;
    private bool _rowAnimationsEnabled = true;
    private bool _framesRequested;

    private bool CanAnimateRows => IsLoaded && _rowAnimationsEnabled && !_showRecommendations &&
        _queue.Opacity > 0 && _queueScroll.Height > 0;

    private void SyncRowClock()
    {
        bool needed = IsLoaded && _rowAnimationsEnabled && _transition is not null;
        if (!needed && CanAnimateRows)
        {
            needed = _dragScrollDirection != 0 || _revealingDelete;
            foreach (var row in _queueScroll.RealizedRows)
            {
                if (needed) break;
                needed = row.NeedsAnimation && _queueScroll.IsInViewport(row.Index);
            }
        }
        RequestFrames(needed);
    }

    private void RequestFrames(bool needed)
    {
        if (_framesRequested == needed) return;
        _framesRequested = needed;
        AnimationChanged?.Invoke(needed);
    }

    /// <summary>Advances cover transitions and visible rows on the display clock.</summary>
    internal void DrawFrame(double time)
    {
        if (_transition is { } transition)
        {
            transition.Start ??= time;
            double progress = Math.Clamp((time - transition.Start.Value) / transition.Duration, 0, 1);
            transition.Draw(progress);
            if (progress == 1)
            {
                _transition = null;
                transition.Done.TrySetResult(true);
                SyncRowClock();
            }
        }
        if (!CanAnimateRows) return;
        if (_revealingDelete && _swipeRow is { } swipe)
        {
            _swipeStart ??= time;
            double progress = Math.Clamp((time - _swipeStart.Value) / .15, 0, 1);
            swipe.SetDeleteReveal(Easing.CubicOut.Ease(progress));
            if (progress == 1)
            {
                _revealingDelete = false;
                SyncRowClock();
            }
        }
        if (_dragScrollDirection == 0)
        {
            _lastDragScroll = null;
        }
        else if (_dragScroll is null || _dragScroll.IsCompleted)
        {
            double elapsed = _lastDragScroll is double previous ? Math.Clamp(time - previous, 0, .05) : 0;
            _lastDragScroll = time;
            double y = Math.Clamp(_queueScroll.ScrollY + _dragScrollDirection * RowStride / .24 * elapsed, 0,
                Math.Max(0, _queueScroll.ContentHeight - _queueScroll.Height));
            if (y != _queueScroll.ScrollY) _dragScroll = _queueScroll.ScrollToAsync(0, y, false);
        }

        double seconds = Stopwatch.GetElapsedTime(_start).TotalSeconds;
        foreach (var row in _queueScroll.RealizedRows)
        {
            if (row.NeedsAnimation && _queueScroll.IsInViewport(row.Index)) row.Animate(seconds);
        }
    }

    private Task<bool> AnimateAsync(int milliseconds, Action<double> draw)
    {
        CancelTransition();
        if (!IsLoaded || !_rowAnimationsEnabled) return Task.FromResult(false);
        var animation = new QueueTransition(milliseconds / 1000d, draw);
        _transition = animation;
        SyncRowClock();
        return animation.Done.Task;
    }

    private void CancelTransition()
    {
        _transition?.Done.TrySetResult(false);
        _transition = null;
    }

    private sealed class QueueTransition(double duration, Action<double> draw)
    {
        public double Duration { get; } = duration;
        public Action<double> Draw { get; } = draw;
        public double? Start { get; set; }
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Updates the stable left-side cover outside of a handoff.</summary>
    public void ShowCurrent(CachedTrack? track) => _current.Show(track);

    /// <summary>Expands the selected row into the full incoming card over the queue.</summary>
    public async Task BeginMixAsync(CachedTrack track, TrackFile? file)
    {
        if (IsEditing || !PlayerQueueIdle) return;
        var row = file is null ? null : _queueScroll.FindRow(file);
        double originY = row is null ? 0 : Math.Clamp(_queueScroll.Y + (double)row.Index * RowStride - _queueScroll.ScrollY, 0, Math.Max(0, _right.Height - 44));
        double rowHeight = Math.Max(44, row?.Height ?? 0);
        ShowIncoming(track);
        _incoming.TranslationY = originY;
        _incoming.ScaleX = .96;
        _incoming.ScaleY = Math.Clamp(rowHeight / Math.Max(1, _right.Height), .15, 1);
        double startingScale = _incoming.ScaleY;
        await AnimateAsync(260, progress =>
        {
            double eased = Easing.CubicOut.Ease(progress);
            _incoming.TranslationY = originY * (1 - eased);
            _incoming.ScaleX = .96 + .04 * eased;
            _incoming.ScaleY = startingScale + (1 - startingScale) * eased;
        });
    }

    /// <summary>Shows the incoming card at rest, including when the app returns during a mix.</summary>
    public void ShowIncoming(CachedTrack track)
    {
        if (IsEditing || !PlayerQueueIdle) return;
        DismissDeleteAction();
        _animation++;
        CancelTransition();
        _queue.InputTransparent = true;
        _queue.Opacity = 0;
        _incoming.Show(track, "Сведение");
        EndDrag();
        _incoming.TranslationX = _incoming.TranslationY = 0;
        _incoming.ScaleX = _incoming.ScaleY = 1;
        _incoming.AnchorY = 0;
        _incoming.Opacity = 1;
        _incoming.IsVisible = true;
        SyncRowClock();
    }

    /// <summary>Replaces the outgoing cover, then brings the next list in from the right edge.</summary>
    public async Task FinishMixAsync(CachedTrack track, Action refreshQueue)
    {
        if (IsEditing || !_incoming.IsVisible)
        {
            _current.Show(track);
            refreshQueue();
            return;
        }
        int animation = ++_animation;
        _handoff = true;
        _incoming.Show(track);
        _incoming.ScaleX = _incoming.ScaleY = 1;
        _incoming.TranslationY = 0;
        _incoming.IsVisible = true;
        _queue.Opacity = 0;
        // The right-aligned artwork has its own inset within the second column.
        double offset = -(_right.X - _current.X + Math.Max(0, _right.Width - _incoming.CoverWidth));
        bool completed = await AnimateAsync(300, progress =>
        {
            double outgoing = Easing.CubicIn.Ease(Math.Min(1, progress * 300 / 210));
            _current.Scale = 1 - .3 * outgoing;
            _current.Opacity = 1 - outgoing;
            _incoming.TranslationX = offset * Easing.CubicOut.Ease(progress);
        });
        if (!completed || animation != _animation)
        {
            return;
        }

        _current.Show(track);
        _current.Scale = _current.Opacity = 1;
        _current.TranslationX = _current.TranslationY = 0;
        _incoming.IsVisible = false;
        _incoming.TranslationX = 0;
        refreshQueue();

        // The parent page clips the content beyond the device's right edge.
        double entrance = Math.Max(0, (Window?.Width ?? Width) - _right.X);
        _queue.TranslationX = entrance;
        _queue.Opacity = 1;
        PositionQueue();
        SyncRowClock();
        completed = await AnimateAsync(340, progress =>
            _queue.TranslationX = entrance * (1 - Easing.CubicOut.Ease(progress)));
        if (completed && animation == _animation)
        {
            _queue.InputTransparent = false;
            _handoff = false;
            ScheduleQueuePosition();
        }
    }

    /// <summary>Clears running animations while the window is hidden or closing.</summary>
    public void Reset()
    {
        _rowAnimationsEnabled = false;
        // A temporary unload or loss of visibility is not an editor navigation action.
        DismissDeleteAction();
        EndDrag();
        ExitDeleteMode();
        CancelTransition();
        RequestFrames(false);
        _animation++;
        _current.Scale = _current.Opacity = 1;
        _incoming.IsVisible = false;
        _queue.Opacity = 1;
        _queue.TranslationX = 0;
        _queue.InputTransparent = false;
        _handoff = false;
    }

    public void ResumeAnimations()
    {
        _rowAnimationsEnabled = true;
        ScheduleQueuePosition();
        SyncRowClock();
    }

    public bool IsHandingOff => _handoff;
}
