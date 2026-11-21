using System.Diagnostics;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

internal sealed partial class TrackQueueView
{
    private long? _lastPlayerInteraction;
    private long? _lastEditorInteraction;
    private double _playerOffset;
    private TrackFile? _playerTrackAtEditorOpen;
    private double? _restorePlayerOffset;
    private double? _programmaticOffset;
    private bool _programmaticScroll;
    private bool _followingTrack;
    private bool _switchingViewport;
    private bool _positionScheduled;
    private double _lastQueueScrollY;

    private bool PlayerQueueIdle => QueueViewport.CanFollow(_lastPlayerInteraction, Stopwatch.GetTimestamp());
    private bool CanFollowTrack => _dragging is null && (IsEditing
        ? QueueViewport.CanFollow(_lastEditorInteraction, Stopwatch.GetTimestamp()) : PlayerQueueIdle);

    private double TrackRowsHeight => TrackListLayout.RowsHeight(_tracks.Count);

    private void OnQueueScrolled(object? sender, ScrolledEventArgs args)
    {
        if (args.ScrollY != _lastQueueScrollY)
        {
            DismissDeleteAction();
            bool reachedTarget = _programmaticOffset is double y && Math.Abs(args.ScrollY - y) <= 1;
            if (reachedTarget) _programmaticOffset = null;
            if (!_programmaticScroll && !reachedTarget && !_switchingViewport &&
                _rowAnimationsEnabled && !_showRecommendations && !_queue.InputTransparent)
                RecordQueueInteraction();
            _lastQueueScrollY = args.ScrollY;
        }
    }

    private void RecordQueueInteraction()
    {
        long time = Stopwatch.GetTimestamp();
        if (IsEditing) _lastEditorInteraction = time;
        else _lastPlayerInteraction = time;
        _followingTrack = false;
        _restorePlayerOffset = null;
        _programmaticOffset = null;
        _switchingViewport = false;
    }

    private void PrepareViewportChange(bool editing)
    {
        _switchingViewport = true;
        if (editing)
        {
            _playerOffset = _queueScroll.ScrollY;
            _playerTrackAtEditorOpen = _currentTrack;
            _lastEditorInteraction = null;
            _followingTrack = _currentTrack is not null;
            _restorePlayerOffset = _followingTrack ? null : 0;
        }
        else
        {
            _followingTrack = !ReferenceEquals(_currentTrack, _playerTrackAtEditorOpen) &&
                _currentTrack is not null && PlayerQueueIdle;
            _restorePlayerOffset = _followingTrack ? null : _playerOffset;
            _playerTrackAtEditorOpen = null;
        }
    }

    private void ScheduleQueuePosition()
    {
        if (_positionScheduled || (!_followingTrack && _restorePlayerOffset is null) || !IsLoaded) return;
        _positionScheduled = true;
        if (!Dispatcher.Dispatch(() =>
        {
            _positionScheduled = false;
            PositionQueue();
        }))
            _positionScheduled = false;
    }

    private void PositionQueue()
    {
        if ((!_followingTrack && _restorePlayerOffset is null) || !_rowAnimationsEnabled || _showRecommendations ||
            _queue.Opacity == 0 || _queueScroll.Height <= 0 || _programmaticScroll || _dragging is not null)
            return;

        double viewport = _queueScroll.Height;
        // A new editor/player layout can still report its previous viewport on the first dispatch.
        if (_switchingViewport && Math.Abs(viewport - _queueScroll.HeightRequest) > 1) return;

        double contentHeight = _queueScroll.ContentHeight;
        int index = _followingTrack && _currentTrack is { } track ? IndexOf(track) : -1;
        if (index < 0 && _restorePlayerOffset is null)
        {
            _followingTrack = false;
            _switchingViewport = false;
            return;
        }
        double offset = index >= 0
            ? QueueViewport.TrackOffset(index, RowStride, TrackListLayout.RowHeight, viewport, contentHeight, IsEditing)
            : Math.Clamp(_restorePlayerOffset.GetValueOrDefault(), 0, Math.Max(0, contentHeight - viewport));

        // Wait for the native scroll content to include any new space after the last row.
        if (offset > 0 && _queueScroll.MeasuredContentHeight + 1 < offset + viewport) return;

        _followingTrack = false;
        _restorePlayerOffset = null;
        _switchingViewport = false;
        if (Math.Abs(offset - _queueScroll.ScrollY) > 1) _ = ApplyQueuePositionAsync(offset);
    }

    private async Task ApplyQueuePositionAsync(double offset)
    {
        _programmaticOffset = offset;
        _programmaticScroll = true;
        try { await _queueScroll.ScrollToAsync(0, offset, animated: false); }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            _programmaticScroll = false;
            ScheduleQueuePosition();
        }
    }
}
