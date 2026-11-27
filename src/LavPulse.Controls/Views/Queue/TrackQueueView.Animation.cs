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

    private bool _playingCards;
    private CachedTrack? _preparedCover;

    public void ShowCurrent(CachedTrack? track)
    {
        // Direct skips use the same local handoff as completed mixes.
        _ = ChangeCoverAsync(track, null);
    }

    internal void SetPlaybackState(bool playing)
    {
        if (_playingCards == playing) return;
        _playingCards = playing;
        UpdateCoverState();
    }

    private void UpdateCoverState() => _current.SetState(_playingCards
        ? _preparedCover is not null ? "Сведение" : "Воспроизводится" : "");

    /// <summary>Prepares the next artwork without hiding or moving the queue.</summary>
    public Task BeginMixAsync(CachedTrack track, TrackFile? file)
    {
        ShowIncoming(track);
        return Task.CompletedTask;
    }

    public void ShowIncoming(CachedTrack track)
    {
        if (IsEditing || ReferenceEquals(_preparedCover, track)) return;
        ResetCoverTransition();
        _preparedCover = track;
        _incoming.Show(track);
        UpdateCoverState();
    }

    public Task FinishMixAsync(CachedTrack track, Action refreshQueue) =>
        ChangeCoverAsync(track, refreshQueue);

    private async Task ChangeCoverAsync(CachedTrack? track, Action? refreshQueue)
    {
        if (ReferenceEquals(_current.Displayed, track))
        {
            refreshQueue?.Invoke();
            return;
        }
        bool animate = track is not null && _current.Displayed is not null &&
            !IsEditing && IsLoaded && _rowAnimationsEnabled && _coverHost.Width > 0;
        ResetCoverTransition();
        _preparedCover = null;
        if (!animate)
        {
            _current.Show(track);
            UpdateCoverState();
            refreshQueue?.Invoke();
            return;
        }

        // Both cards are in the same clipped artwork rail and share geometry.
        // Swap ownership at the start, so cancellation always keeps the latest
        // track. No artwork travels across the decks or covers the queue.
        (_current, _incoming) = (_incoming, _current);
        _current.Show(track);
        UpdateCoverState();
        _current.ZIndex = 1;
        _incoming.ZIndex = 0;
        _current.Opacity = 0;
        _current.TranslationX = 12;
        _current.Scale = .98;
        _current.IsVisible = true;
        _incoming.IsVisible = true;
        _handoff = true;
        int generation = _animation;
        refreshQueue?.Invoke();

        bool completed = await AnimateAsync(PlayerMotion.TrackHandoffMilliseconds, progress =>
        {
            double eased = PlayerMotion.Ease(progress);
            _incoming.Opacity = 1 - eased;
            _incoming.TranslationX = -12 * eased;
            _current.Opacity = eased;
            _current.TranslationX = 12 * (1 - eased);
            _current.Scale = .98 + .02 * eased;
        });
        if (completed && generation == _animation)
        {
            ResetCoverTransition();
            ScheduleQueuePosition();
        }
    }

    private void ResetCoverTransition()
    {
        _animation++;
        CancelTransition();
        _handoff = false;
        _preparedCover = null;
        _current.IsVisible = true;
        _incoming.IsVisible = false;
        _current.Opacity = _incoming.Opacity = 1;
        _current.Scale = _incoming.Scale = 1;
        _current.TranslationX = _incoming.TranslationX = 0;
        _current.TranslationY = _incoming.TranslationY = 0;
        _queue.Opacity = 1;
        _queue.TranslationX = 0;
        _queue.InputTransparent = false;
        UpdateCoverState();
        SyncRowClock();
    }

    /// <summary>Clears running animations while the window is hidden or closing.</summary>
    public void Reset()
    {
        _rowAnimationsEnabled = false;
        // A temporary unload or loss of visibility is not an editor navigation action.
        DismissDeleteAction();
        EndDrag();
        ExitDeleteMode();
        ResetCoverTransition();
        RequestFrames(false);
    }

    public void ResumeAnimations()
    {
        _rowAnimationsEnabled = true;
        ScheduleQueuePosition();
        SyncRowClock();
    }

    public bool IsHandingOff => _handoff;
}
