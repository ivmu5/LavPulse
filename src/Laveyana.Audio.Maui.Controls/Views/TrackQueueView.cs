using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using System.Diagnostics;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Shows the current cover next to the queue and animates the handoff between both halves.</summary>
internal sealed partial class TrackQueueView : Grid
{
    private const int RowStride = QueueTrackRow.RowHeight + 5;
    private const int ViewportHeight = 236;
    private const int OverscanRows = 2;
    private const int CoverInfoGap = 8;
    private readonly TrackCard _current = new(TextAlignment.Start, showAudioInfo: true);
    private readonly TrackCard _incoming = new(TextAlignment.End);
    private readonly AbsoluteLayout _rows = new();
    private readonly VerticalStackLayout _queue;
    private readonly View _feedSelector;
    private readonly Grid _right;
    private readonly Button _queueTab = MixerStyle.Button("Очередь", icon: "player_queue.png", stacked: true);
    private readonly Button _recommendationsTab = MixerStyle.Button("Рекомендации", icon: "player_recommendations.png", stacked: true);
    private readonly ScrollView _queueScroll;
    private readonly Label _recommendations = MixerStyle.Label("В разработке", MixerStyle.PhoneFont(13, 11), MixerStyle.Muted);
    private readonly Dictionary<TrackFile, QueueTrackRow> _trackRows = new();
    private readonly Stack<QueueTrackRow> _availableRows = new();
    private readonly List<TrackFile> _leavingRows = new();
    private IReadOnlyList<TrackFile> _tracks = [];
    private Func<TrackFile, QueueTrackItem>? _readRow;
    private TrackFile? _nextTrack;
    private int _playedCount;
    private int _visibleStart;
    private bool _showPlayedTracks = true;
    private int VisibleTrackCount => Math.Max(0, _tracks.Count - _visibleStart);
    private int _firstRow = -1;
    private int _endRow = -1;
    private readonly Label _empty = MixerStyle.Label("Очередь пуста", MixerStyle.PhoneFont(12, 11), MixerStyle.Muted);
    private readonly IDispatcherTimer _rowClock;
    private readonly long _start = Stopwatch.GetTimestamp();
    private TrackFile? _currentTrack;
    private IDrawable? _masterSpectrum;
    private View? _removeTarget;
    private double? _dragStartX;
    private bool _focusCurrent;
    private TrackFile? _dragging;
    private QueueTrackRow? _dropTarget;
    private int _dragScrollDirection;
    private long _lastDragScroll;
    private long _lastDrag;
    private bool _showRecommendations;
    private int _animation;
    private bool _handoff;
    private bool _rowAnimationsEnabled = true;

    public TrackQueueView()
    {
        ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)];
        ColumnSpacing = MixerStyle.IsPhone ? 12 : 16;
        IsClippedToBounds = true;
        _queueScroll = new ScrollView { Content = _rows, MaximumHeightRequest = ViewportHeight };
        _rowClock = Dispatcher.CreateTimer();
        _rowClock.Interval = TimeSpan.FromMilliseconds(40);
        _rowClock.Tick += (_, _) => AnimateRows();
        Loaded += (_, _) =>
        {
            if (!_rowAnimationsEnabled) return;
            _rowClock.Start();
            FocusCurrent();
        };
        Unloaded += (_, _) => _rowClock.Stop();
        _recommendations.IsVisible = false;
        _feedSelector = CreateFeedSelector();
        _queue = new VerticalStackLayout { Spacing = 7, Children = { _feedSelector, _queueScroll, _recommendations } };
        _feedSelector.SizeChanged += (_, _) => UpdateQueueViewport();
        _queueScroll.Scrolled += (_, _) => RefreshRows();
        _queueScroll.SizeChanged += (_, _) =>
        {
            RefreshRows();
            if (_focusCurrent && !_showRecommendations && _queue.Opacity > 0) FocusCurrent();
        };
        SetTabColors();
        _right = new Grid { Children = { _queue, _incoming } };
        _incoming.IsVisible = false;
        this.Add(_current);
        this.Add(_right, 1);
        SizeChanged += (_, _) =>
        {
            UpdateQueueViewport();
            double columnWidth = Math.Max(0, (Width - ColumnSpacing) / 2);
            if (columnWidth <= 0 || Height <= 0) return;

            double preferred = Math.Clamp(Width * .29, 116, 202) * 1.1;
            double infoWidth = Math.Min(columnWidth * .48, MixerStyle.PhoneFont(94, 70));
            double side = Math.Max(1, Math.Min(preferred, Math.Min(columnWidth - infoWidth - CoverInfoGap, Height - 52)));
            _current.ResizeCover(side, columnWidth);
            _incoming.ResizeCover(side, columnWidth);
        };
    }

    private void UpdateQueueViewport()
    {
        if (_rows.HeightRequest < 0) return;
        double available = Height > 0 && _feedSelector.Height > 0
            ? Math.Max(0, Height - _feedSelector.Height - _queue.Spacing) : ViewportHeight;
        _queueScroll.HeightRequest = Math.Min(_rows.HeightRequest, Math.Min(available, ViewportHeight));
    }

    public event Action<TrackFile>? TrackRequested;
    public event Action<TrackFile>? HistoryRequested;
    public event Action<TrackFile, TrackFile, bool>? MoveRequested;
    public event Action<TrackFile>? RemoveRequested;
    public event Action<bool>? RecommendationsSelected;
    public Color Accent { get; set; } = MixerStyle.Accent;

    /// <summary>Groups the two playback sources in a single segmented control.</summary>
    private Border CreateFeedSelector()
    {
        _queueTab.CornerRadius = _recommendationsTab.CornerRadius = 7;
        if (MixerStyle.IsPhone)
        {
            _queueTab.FontSize = _recommendationsTab.FontSize = 10;
            _queueTab.Padding = _recommendationsTab.Padding = new Thickness(1, 4);
            _queueTab.LineBreakMode = _recommendationsTab.LineBreakMode = LineBreakMode.TailTruncation;
        }
        _queueTab.Clicked += (_, _) =>
        {
            if (_showRecommendations)
            {
                SelectRecommendations(false);
                FocusCurrent();
            }
            else
            {
                _showPlayedTracks = !_showPlayedTracks;
                _focusCurrent = true;
                UpdateVisibleRows();
                SetTabColors();
                if (!_showPlayedTracks) _ = _queueScroll.ScrollToAsync(0, 0, false);
            }
        };
        _recommendationsTab.Clicked += (_, _) => SelectRecommendations(true);
        var segments = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)] };
        segments.Add(_queueTab);
        segments.Add(_recommendationsTab, 1);
        return new Border
        {
            BackgroundColor = MixerStyle.Surface,
            Stroke = MixerStyle.BorderColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = 2,
            Content = segments
        };
    }

    /// <summary>Switches the visible feed and tells playback which source to use.</summary>
    private void SelectRecommendations(bool show)
    {
        if (_showRecommendations == show)
        {
            return;
        }
        _showRecommendations = show;
        _queueScroll.IsVisible = !show;
        _recommendations.IsVisible = show;
        SetTabColors();
        if (!show) RefreshRows(force: true);
        RecommendationsSelected?.Invoke(show);
    }

    private void SetTabColors()
    {
        _queueTab.BackgroundColor = _showRecommendations ? Colors.Transparent : MixerStyle.SelectedBackground;
        _recommendationsTab.BackgroundColor = _showRecommendations ? MixerStyle.SelectedBackground : Colors.Transparent;
        _queueTab.TextColor = _showRecommendations ? MixerStyle.Muted : MixerStyle.TextColor;
        _recommendationsTab.TextColor = _showRecommendations ? MixerStyle.TextColor : MixerStyle.Muted;
        SemanticProperties.SetDescription(_queueTab, _showRecommendations ? "Очередь" :
            _showPlayedTracks ? "Очередь. Нажми ещё раз, чтобы скрыть прослушанные треки." :
            "Очередь. Нажми ещё раз, чтобы показать прослушанные треки.");
        SemanticProperties.SetDescription(_recommendationsTab, _showRecommendations ? "Рекомендации, выбраны" : "Рекомендации");
    }

    /// <summary>Updates the queue model and rebinds only the visible window of rows.</summary>
    public void ShowQueue(IReadOnlyList<TrackFile> tracks, TrackFile? current, TrackFile? next, int playedCount, Func<TrackFile, QueueTrackItem> read)
    {
        _focusCurrent |= !ReferenceEquals(_currentTrack, current);
        _tracks = tracks;
        _currentTrack = current;
        _nextTrack = next;
        _playedCount = playedCount;
        _readRow = read;
        UpdateVisibleRows();
    }

    /// <summary>Projects the same queue with or without played entries, without modifying track order.</summary>
    private void UpdateVisibleRows()
    {
        _visibleStart = _showPlayedTracks ? 0 : Math.Clamp(_playedCount, 0, _tracks.Count);
        int count = VisibleTrackCount;
        _rows.HeightRequest = count == 0 ? QueueTrackRow.RowHeight : (double)count * RowStride - 5;
        UpdateQueueViewport();

        if (count == 0 && !_rows.Children.Contains(_empty))
        {
            AbsoluteLayout.SetLayoutFlags(_empty, AbsoluteLayoutFlags.WidthProportional);
            AbsoluteLayout.SetLayoutBounds(_empty, new Rect(0, 0, 1, QueueTrackRow.RowHeight));
            _rows.Children.Add(_empty);
        }
        else if (count > 0)
        {
            _rows.Children.Remove(_empty);
        }

        RefreshRows(force: true);
        if (_focusCurrent && !_showRecommendations && _queue.Opacity > 0) FocusCurrent();
    }

    private void RefreshRows(bool force = false)
    {
        if (_showRecommendations || _readRow is not { } read) return;

        double height = _queueScroll.Height > 0 ? _queueScroll.Height : _queueScroll.HeightRequest;
        double scroll = Math.Clamp(_queueScroll.ScrollY, 0, Math.Max(0, _rows.HeightRequest - height));
        int first = _visibleStart + Math.Max(0, (int)(scroll / RowStride) - OverscanRows);
        int end = Math.Min(_tracks.Count, _visibleStart +
            (int)Math.Ceiling((scroll + height) / RowStride) + OverscanRows);
        if (!force && first == _firstRow && end == _endRow) return;

        _firstRow = first;
        _endRow = end;
        foreach (var (file, row) in _trackRows)
        {
            // The native drag still owns its source view when it scrolls out of sight.
            if (ReferenceEquals(file, _dragging)) continue;
            if (row.Index < first || row.Index >= end || !ReferenceEquals(_tracks[row.Index], file))
            {
                if (ReferenceEquals(_dropTarget, row)) ClearDropTarget();
                _leavingRows.Add(file);
            }
        }
        foreach (var file in _leavingRows)
        {
            var row = _trackRows[file];
            _trackRows.Remove(file);
            _rows.Children.Remove(row);
            row.Unbind();
            _availableRows.Push(row);
        }
        _leavingRows.Clear();

        for (int i = first; i < end; i++)
        {
            var file = _tracks[i];
            if (!_trackRows.TryGetValue(file, out var row))
            {
                row = _availableRows.TryPop(out var recycled) ? recycled : CreateRow();
                row.Bind(file, i);
                _rows.Children.Add(row);
                _trackRows.Add(file, row);
            }
            // Existing visible rows also move when history is hidden or shown.
            AbsoluteLayout.SetLayoutBounds(row,
                new Rect(0, (double)(i - _visibleStart) * RowStride, 1, QueueTrackRow.RowHeight));
            row.Show(read(file), ReferenceEquals(file, _currentTrack), i < _playedCount, ReferenceEquals(file, _nextTrack), Accent);
        }
    }

    public void SetMasterSpectrum(IDrawable drawable) => _masterSpectrum = drawable;

    public void InvalidateMasterSpectrum()
    {
        if (_showRecommendations || _queue.Opacity == 0 || _currentTrack is not { } file ||
            !_trackRows.TryGetValue(file, out var row)) return;
        double scroll = _queueScroll.ScrollY;
        double top = (double)(row.Index - _visibleStart) * RowStride;
        if (top + QueueTrackRow.RowHeight >= scroll && top <= scroll + _queueScroll.Height)
        {
            row.InvalidateSpectrum();
        }
    }

    public void AttachRemovalTarget(View target)
    {
        _removeTarget = target;
        var drop = new DropGestureRecognizer { AllowDrop = true };
        drop.DragOver += (_, args) =>
        {
            bool accepted = CanRemove(args.GetPosition(this));
            args.AcceptedOperation = accepted ? DataPackageOperation.Copy : DataPackageOperation.None;
            target.Opacity = accepted ? 1 : .65;
        };
        drop.DragLeave += (_, _) => target.Opacity = .65;
        drop.Drop += (_, args) =>
        {
            if (!CanRemove(args.GetPosition(this)) || _dragging is not { } file) return;
            args.Handled = true;
            EndDrag();
            Dispatcher.Dispatch(() => RemoveRequested?.Invoke(file));
        };
        target.GestureRecognizers.Add(drop);

        bool CanRemove(Point? point) =>
            _dragging is not null && (_dragStartX is not double start || point is null || point.Value.X >= start + 14);
    }

    private void FocusCurrent()
    {
        if (_currentTrack is not { } track || _queueScroll.Height <= 0)
        {
            return;
        }

        int index = IndexOf(track);
        if (index < _visibleStart) return;
        double top = (double)(index - _visibleStart) * RowStride;
        double bottom = top + QueueTrackRow.RowHeight;
        double scroll = _queueScroll.ScrollY;
        double y = Math.Clamp(scroll, Math.Clamp(bottom - _queueScroll.Height, 0, top), top);
        _focusCurrent = false;
        if (y != scroll) _ = _queueScroll.ScrollToAsync(0, y, false);
    }

    private int IndexOf(TrackFile file)
    {
        for (int i = 0; i < _tracks.Count; i++)
        {
            if (ReferenceEquals(_tracks[i], file)) return i;
        }
        return -1;
    }

    private QueueTrackRow CreateRow()
    {
        var row = new QueueTrackRow();
        row.SetSpectrum(_masterSpectrum);
        AbsoluteLayout.SetLayoutFlags(row, AbsoluteLayoutFlags.WidthProportional);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (row.File is { } file && _dragging is null &&
                (_lastDrag == 0 || Stopwatch.GetElapsedTime(_lastDrag).TotalMilliseconds >= 300))
            {
                if (row.IsPlayed) HistoryRequested?.Invoke(file);
                else if (!ReferenceEquals(_currentTrack, file)) TrackRequested?.Invoke(file);
            }
        };
        row.GestureRecognizers.Add(tap);
        var drag = new DragGestureRecognizer { CanDrag = true };
        drag.DragStarting += (_, args) =>
        {
            if (_queue.InputTransparent || _showRecommendations || row.File is not { } file)
            {
                args.Cancel = true;
                return;
            }
            _dragging = file;
            _dragStartX = args.GetPosition(this)?.X;
            if (_removeTarget is { } target)
            {
                target.Opacity = .65;
                target.InputTransparent = false;
            }
            args.Data.Text = "LavPulse.QueueTrack";
        };
        drag.DropCompleted += (_, _) => EndDrag();
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
                _dragScrollDirection = point.Y < 26 ? -1 : point.Y > _queueScroll.Height - 26 ? 1 : 0;
            }
        };
        drop.DragLeave += (_, _) =>
        {
            if (ReferenceEquals(_dropTarget, row))
            {
                ClearDropTarget();
                _dragScrollDirection = 0;
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
                : _trackRows[source].Index < row.Index;
            args.Handled = true;
            EndDrag();
            Dispatcher.Dispatch(() => MoveRequested?.Invoke(source, file, after));
        };
        row.GestureRecognizers.Add(drop);
        return row;

        bool CanReorder() => !_queue.InputTransparent && !_showRecommendations && _dragging is { } source &&
            row.File is { } file && !ReferenceEquals(source, file) && _trackRows.ContainsKey(source);
    }

    private void EndDrag()
    {
        bool wasDragging = _dragging is not null;
        _dragging = null;
        _dragStartX = null;
        if (_removeTarget is { } target)
        {
            target.Opacity = 0;
            target.InputTransparent = true;
        }
        _lastDrag = Stopwatch.GetTimestamp();
        _dragScrollDirection = 0;
        ClearDropTarget();
        if (wasDragging && _rowAnimationsEnabled)
        {
            Dispatcher.Dispatch(() =>
            {
                if (_rowAnimationsEnabled) RefreshRows(force: true);
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

    /// <summary>Animates only rows inside the scroll viewport, including the slow text marquee.</summary>
    private void AnimateRows()
    {
        if (!_rowAnimationsEnabled || _showRecommendations || _queue.Opacity == 0 || _queueScroll.Height <= 0)
        {
            return;
        }
        if (_focusCurrent) FocusCurrent();
        if (_dragScrollDirection != 0 && Stopwatch.GetElapsedTime(_lastDragScroll).TotalMilliseconds >= 120)
        {
            _lastDragScroll = Stopwatch.GetTimestamp();
            double y = Math.Clamp(_queueScroll.ScrollY + _dragScrollDirection * RowStride / 2d, 0,
                Math.Max(0, _rows.HeightRequest - _queueScroll.Height));
            if (y != _queueScroll.ScrollY) _ = _queueScroll.ScrollToAsync(0, y, false);
        }

        double top = _queueScroll.ScrollY;
        double bottom = top + _queueScroll.Height;
        double seconds = Stopwatch.GetElapsedTime(_start).TotalSeconds;
        foreach (var row in _trackRows.Values)
        {
            double rowTop = (double)(row.Index - _visibleStart) * RowStride;
            if (rowTop < bottom && rowTop + QueueTrackRow.RowHeight > top) row.Animate(seconds);
        }
    }

    /// <summary>Updates the stable left-side cover outside of a handoff.</summary>
    public void ShowCurrent(CachedTrack? track) => _current.Show(track);

    /// <summary>Expands the selected row into the full incoming card over the queue.</summary>
    public async Task BeginMixAsync(CachedTrack track, TrackFile? file)
    {
        var row = file is not null && _trackRows.TryGetValue(file, out var item) ? item : null;
        double originY = row is null ? 0 : Math.Clamp(_queueScroll.Y + (double)(row.Index - _visibleStart) * RowStride - _queueScroll.ScrollY, 0, Math.Max(0, _right.Height - 44));
        double rowHeight = Math.Max(44, row?.Height ?? 0);
        ShowIncoming(track);
        _incoming.TranslationY = originY;
        _incoming.ScaleX = .96;
        _incoming.ScaleY = Math.Clamp(rowHeight / Math.Max(1, _right.Height), .15, 1);
        await Task.WhenAll(
            _incoming.TranslateToAsync(0, 0, 260, Easing.CubicOut),
            _incoming.ScaleXToAsync(1, 260, Easing.CubicOut),
            _incoming.ScaleYToAsync(1, 260, Easing.CubicOut));
    }

    /// <summary>Shows the incoming card at rest, including when the app returns during a mix.</summary>
    public void ShowIncoming(CachedTrack track)
    {
        _animation++;
        _queue.InputTransparent = true;
        _queue.Opacity = 0;
        _incoming.Show(track, "Сведение");
        EndDrag();
        _incoming.TranslationX = _incoming.TranslationY = 0;
        _incoming.ScaleX = _incoming.ScaleY = 1;
        _incoming.AnchorY = 0;
        _incoming.Opacity = 1;
        _incoming.IsVisible = true;
    }

    /// <summary>Replaces the outgoing cover, then brings the next list in from the right edge.</summary>
    public async Task FinishMixAsync(CachedTrack track, Action refreshQueue)
    {
        int animation = ++_animation;
        _handoff = true;
        _incoming.CancelAnimations();
        _incoming.Show(track);
        _incoming.ScaleX = _incoming.ScaleY = 1;
        _incoming.TranslationY = 0;
        _incoming.IsVisible = true;
        _queue.Opacity = 0;
        // The right-aligned artwork has its own inset within the second column.
        double offset = -(_right.X - _current.X + Math.Max(0, _right.Width - _incoming.CoverWidth));
        await Task.WhenAll(
            _current.ScaleToAsync(.7, 210, Easing.CubicIn),
            _current.FadeToAsync(0, 210, Easing.CubicIn),
            _incoming.TranslateToAsync(offset, 0, 300, Easing.CubicOut));
        if (animation != _animation)
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
        _queue.TranslationX = Math.Max(0, (Window?.Width ?? Width) - _right.X);
        _queue.Opacity = 1;
        await _queue.TranslateToAsync(0, 0, 340, Easing.CubicOut);
        if (animation == _animation)
        {
            _queue.InputTransparent = false;
            _handoff = false;
            if (_focusCurrent && !_showRecommendations)
            {
                FocusCurrent();
            }
        }
    }

    /// <summary>Clears running animations while the window is hidden or closing.</summary>
    public void Reset()
    {
        _rowAnimationsEnabled = false;
        _rowClock.Stop();
        _animation++;
        _current.CancelAnimations();
        _incoming.CancelAnimations();
        _queue.CancelAnimations();
        _current.Scale = _current.Opacity = 1;
        _incoming.IsVisible = false;
        _queue.Opacity = 1;
        _queue.TranslationX = 0;
        _queue.InputTransparent = false;
        _handoff = false;
        EndDrag();
    }

    public void ResumeAnimations()
    {
        _rowAnimationsEnabled = true;
        if (IsLoaded) _rowClock.Start();
    }

    public bool IsHandingOff => _handoff;

}
