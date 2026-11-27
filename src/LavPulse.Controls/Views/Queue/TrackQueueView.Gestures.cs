using System.Diagnostics;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

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
        row.SelectionToggled += () =>
        {
            if(IsEditing && CanEditRows && row.File is { } file) ToggleChecked(row,file);
        };
        row.DeleteRequested += () =>
        {
            bool canDelete = IsEditing ? _deletingTracks : ReferenceEquals(row, _swipeRow);
            if (!CanEditRows || !canDelete || _dragging is not null || row.File is not { } file) return;
            RecordQueueInteraction();
            DismissDeleteAction();
            Dispatcher.Dispatch(() => RemoveRequested?.Invoke(file));
        };
        row.DeleteUnfocused += () =>
        {
            if (ReferenceEquals(row, _swipeRow)) DismissDeleteAction();
        };
        row.SetSpectrum(_masterSpectrum);
        row.AttachPrimaryTap(() =>
        {
            RecordQueueInteraction();
            if (row.ConsumedGesture || DismissDeleteAction() || !CanEditRows) return;
            if (row.File is { } file && _dragging is null &&
                (_lastDrag == 0 || Stopwatch.GetElapsedTime(_lastDrag).TotalMilliseconds >= 300))
            {
                if (IsEditing)
                {
                    ToggleChecked(row,file);
                }
                else if (row.IsPlayed) HistoryRequested?.Invoke(file);
                else if (!ReferenceEquals(_currentTrack, file)) TrackRequested?.Invoke(file);
            }
        });
        row.AttachActions(() => row.File, () => CanEditRows && !IsEditing && _dragging is null,
            right => right ? ListSettings.QueueSwipeRight : ListSettings.QueueSwipeLeft,
            action => row.File is { } file && SwipeRequested is { } swipe ? swipe(file,action) : Task.CompletedTask,
            point => row.File is { } file && ActionsRequested is { } menu ? menu([file], point) : Task.CompletedTask,
            () => _theme.Artwork,
            () => { RecordQueueInteraction(); foreach(var other in _queueScroll.RealizedRows) if(!ReferenceEquals(other,row)) other.ResetSwipe(); });
        var drag = new DragGestureRecognizer { CanDrag = true };
        drag.DragStarting += (_, args) =>
        {
            if (!CanDragRows || row.File is not { } file)
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

        bool CanReorder() => CanDragRows && _dragging is { } source &&
            row.File is { } file && !ReferenceEquals(source, file) && _queueScroll.FindRow(source) is not null;
    }

    private void ToggleChecked(QueueTrackRow row,TrackFile file)
    {
        if(!_checkedTracks.Add(file))_checkedTracks.Remove(file);
        row.SetSelectionMark(true,_checkedTracks.Contains(file));
        UpdateEditorActions();
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

    private bool CanEditRows => !_selectionActionRunning && !_showRecommendations && !_queue.InputTransparent && _rowAnimationsEnabled;
    private bool CanDragRows => IsEditing && !_deletingTracks && CanEditRows;
    private bool CanSwipe => !IsEditing && CanEditRows;

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
        bool revealed=false;
        foreach (var realized in _queueScroll.RealizedRows)
        { revealed|=realized.HasRevealedSwipe;realized.ResetSwipe(); }
        if (_swipeRow is not { } row) return revealed;
        _swipeRow = null;
        GestureRecognizers.Remove(_outsideSwipeTap);
        _revealingDelete = false;
        row.SetDeleteReveal(0);
        SyncRowClock();
        return true;
    }
}
