using System.Diagnostics;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

internal sealed partial class TrackQueueView
{
    private readonly TapGestureRecognizer _outsideSwipeTap = new();
    private Window? _focusWindow;

    private QueueTrackRow? _swipeRow;
    private double? _swipeStart;
    private bool _revealingDelete;

    private TrackFile? _dragging;
    private QueueTrackRow? _dropTarget;
    private Action? _pendingDrop;
    private int _dragScrollDirection;
    private double? _lastDragScroll;
    private Task? _dragScroll;
    private long _lastDrag;

    private QueueTrackRow CreateRow()
    {
        var row = new QueueTrackRow();
        row.AnimationStateChanged += SyncRowClock;
        row.DeleteRequested += () =>
        {
            if ((!IsEditing && !ReferenceEquals(row, _swipeRow)) || row.File is not { } file) return;
            RecordQueueInteraction();
            DismissDeleteAction();
            Dispatcher.Dispatch(() => RemoveRequested?.Invoke(file));
        };
        row.DeleteUnfocused += () =>
        {
            if (ReferenceEquals(row, _swipeRow)) DismissDeleteAction();
        };
        row.SetSpectrum(_masterSpectrum);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            RecordQueueInteraction();
            if (DismissDeleteAction() || IsEditing) return;
            if (row.File is { } file && _dragging is null &&
                (_lastDrag == 0 || Stopwatch.GetElapsedTime(_lastDrag).TotalMilliseconds >= 300))
            {
                if (row.IsPlayed) HistoryRequested?.Invoke(file);
                else if (!ReferenceEquals(_currentTrack, file)) TrackRequested?.Invoke(file);
            }
        };
        row.TouchBody.GestureRecognizers.Add(tap);
        TrackListSwipe.Attach(row, () => row.File, () => CanSwipe, () => BeginSwipe(row));
        var drag = new DragGestureRecognizer { CanDrag = true };
        drag.DragStarting += (_, args) =>
        {
            if (!IsEditing || _queue.InputTransparent || _showRecommendations || row.File is not { } file)
            {
                args.Cancel = true;
                return;
            }
            RecordQueueInteraction();
            DismissDeleteAction();
            _dragging = file;
            args.Data.Text = "LavPulse.QueueTrack";
        };
        drag.DropCompleted += (_, _) => EndDrag(commit: true);
        row.DragHandle.GestureRecognizers.Add(drag);

        var drop = new DropGestureRecognizer { AllowDrop = true };
        drop.DragOver += (_, args) =>
        {
            bool accepted = CanReorder();
            args.AcceptedOperation = accepted ? DataPackageOperation.Copy : DataPackageOperation.None;
            if (accepted && !ReferenceEquals(_dropTarget, row))
            {
                ClearDropTarget();
                _dropTarget = row;
                row.Scale = .97;
            }
            if (_dragging is not null && args.GetPosition(_queueScroll) is { } point)
            {
                int direction = point.Y < 26 ? -1 : point.Y > _queueScroll.Height - 26 ? 1 : 0;
                if (_dragScrollDirection != direction)
                {
                    _dragScrollDirection = direction;
                    SyncRowClock();
                }
            }
        };
        drop.DragLeave += (_, _) =>
        {
            if (ReferenceEquals(_dropTarget, row))
            {
                ClearDropTarget();
                _dragScrollDirection = 0;
                SyncRowClock();
            }
        };
        drop.Drop += (_, args) =>
        {
            row.Scale = 1;
            if (!CanReorder() || _dragging is not { } source || row.File is not { } file)
            {
                return;
            }
            bool after = args.GetPosition(row) is { } point ? point.Y >= row.Height / 2
                : _queueScroll.FindRow(source)?.Index < row.Index;
            args.Handled = true;
            DeferDrop(() => MoveRequested?.Invoke(source, file, after));
        };
        row.GestureRecognizers.Add(drop);
        return row;

        bool CanReorder() => !_queue.InputTransparent && !_showRecommendations && _dragging is { } source &&
            row.File is { } file && !ReferenceEquals(source, file) && _queueScroll.FindRow(source) is not null;
    }

    private void DeferDrop(Action action)
    {
        _pendingDrop ??= action;
        _dragScrollDirection = 0;
        _lastDragScroll = null;
        SyncRowClock();
    }

    private void EndDrag(bool commit = false)
    {
        bool wasDragging = _dragging is not null;
        var drop = commit ? _pendingDrop : null;
        _pendingDrop = null;
        _dragging = null;
        _dragScrollDirection = 0;
        _lastDragScroll = null;
        ClearDropTarget();
        SyncRowClock();
        if (wasDragging)
        {
            _lastDrag = Stopwatch.GetTimestamp();
            Dispatcher.Dispatch(() =>
            {
                drop?.Invoke();
                if (_rowAnimationsEnabled) _queueScroll.Refresh(force: true);
            });
        }
    }

    private void ClearDropTarget()
    {
        if (_dropTarget is { } row)
        {
            row.Scale = 1;
            _dropTarget = null;
        }
    }

    private bool CanSwipe => !IsEditing && !_showRecommendations && !_queue.InputTransparent && _rowAnimationsEnabled;

    private void OnWindowDeactivated(object? sender, EventArgs args) => DismissDeleteAction();

    private void BeginSwipe(QueueTrackRow row)
    {
        if (!CanSwipe || row.File is null || ReferenceEquals(row, _swipeRow)) return;
        RecordQueueInteraction();
        DismissDeleteAction();
        _swipeRow = row;
        GestureRecognizers.Add(_outsideSwipeTap);
        _swipeStart = null;
        _revealingDelete = true;
        SyncRowClock();
    }

    internal bool DismissDeleteAction()
    {
        if (_swipeRow is not { } row) return false;
        _swipeRow = null;
        GestureRecognizers.Remove(_outsideSwipeTap);
        _revealingDelete = false;
        row.SetDeleteReveal(0);
        SyncRowClock();
        return true;
    }
}
