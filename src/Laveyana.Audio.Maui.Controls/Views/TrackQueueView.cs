using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;
using System.Diagnostics;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Shows the current cover next to the queue and animates the handoff between both halves.</summary>
internal sealed partial class TrackQueueView : Grid
{
    private const int RowStride = TrackListLayout.Stride;
    private const int ViewportHeight = 236;
    private const int CoverInfoGap = 8;
    private readonly TrackCard _current = new(TextAlignment.Start, showAudioInfo: true);
    private readonly TrackCard _incoming = new(TextAlignment.End);
    private readonly VerticalStackLayout _queue;
    private readonly View _feedSelector;
    private readonly Grid _right;
    private readonly Grid _editorHeader;
    private readonly TapGestureRecognizer _outsideSwipeTap = new();
    private readonly Label _editorHint = MixerStyle.Label("За три полоски можно передвигать треки. Корзина удаляет строку.", 11, MixerColor.Muted);
    private QueueTrackRow? _swipeRow;
    private double? _swipeStart;
    private bool _revealingDelete;
    private Window? _focusWindow;
    private readonly Button _queueTab = MixerStyle.Button("Очередь", icon: "player_queue.png", stacked: true);
    private readonly Button _recommendationsTab = MixerStyle.Button("Рекомендации", icon: "player_recommendations.png", stacked: true);
    private readonly TrackListView<TrackFile, QueueTrackRow> _queueScroll;
    private readonly TrackListView<TrackFile, QueueTrackRow> _recommendations;
    private IReadOnlyList<TrackFile> _tracks = [];
    private Func<TrackFile, QueueTrackItem>? _readRow;
    private int _playedCount;
    private readonly long _start = Stopwatch.GetTimestamp();
    private TrackFile? _currentTrack;
    private IDrawable? _masterSpectrum;
    private TrackFile? _dragging;
    private QueueTrackRow? _dropTarget;
    private Action? _pendingDrop;
    private int _dragScrollDirection;
    private double? _lastDragScroll;
    private Task? _dragScroll;
    private long _lastDrag;
    private bool _showRecommendations;
    private int _animation;
    private bool _handoff;
    private bool _rowAnimationsEnabled = true;
    private bool _framesRequested;
    private QueueTransition? _transition;

    internal event Action<bool>? AnimationChanged;
    internal event Action<bool>? EditingChanged;
    internal event Action? LibraryRequested;
    internal bool IsEditing { get; private set; }

    public TrackQueueView()
    {
        ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)];
        ColumnSpacing = MixerStyle.IsPhone ? 12 : 16;
        IsClippedToBounds = true;
        _queueScroll = new TrackListView<TrackFile, QueueTrackRow>(CreateRow, BindRow, ReleaseRow,
            // Keep views referenced by the native drag attached until the platform finishes.
            (file, row) => ReferenceEquals(file, _dragging) || ReferenceEquals(row, _dropTarget))
        {
            MaximumHeightRequest = ViewportHeight
        };
        _recommendations = new TrackListView<TrackFile, QueueTrackRow>(CreateRow, BindRow, ReleaseRow)
        {
            HeightRequest = TrackListLayout.RowHeight, IsVisible = false
        };
        _recommendations.SetItems([], "В разработке");
        Loaded += (_, _) =>
        {
            if (_focusWindow is null && Window is { } window)
            {
                _focusWindow = window;
                window.Deactivated += OnWindowDeactivated;
            }
            if (!_rowAnimationsEnabled) return;
            ScheduleQueuePosition();
            SyncRowClock();
        };
        Unloaded += (_, _) =>
        {
            if (_focusWindow is { } window)
            {
                _focusWindow = null;
                window.Deactivated -= OnWindowDeactivated;
            }
            bool animationsEnabled = _rowAnimationsEnabled;
            Reset();
            _rowAnimationsEnabled = animationsEnabled;
        };
        _feedSelector = CreateFeedSelector();
        var done = MixerStyle.Button("Готово", icon: "player_confirm.png");
        MixerStyle.SetListEditButton(done, editing: true);
        done.Clicked += (_, _) => CloseEditor();
        var title = MixerStyle.Label("Редактор очереди", MixerStyle.PhoneFont(19, 16), bold: true);
        title.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        _editorHeader = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 6, IsVisible = false
        };
        _editorHeader.Add(title);
        _editorHeader.Add(done, 1);
        _editorHint.IsVisible = false;
        _queue = new VerticalStackLayout
        {
            Spacing = 7, Children = { _feedSelector, _editorHeader, _editorHint, _queueScroll, _recommendations }
        };
        _editorHeader.SizeChanged += (_, _) => UpdateQueueViewport();
        _editorHint.SizeChanged += (_, _) => UpdateQueueViewport();
        _feedSelector.SizeChanged += (_, _) => UpdateQueueViewport();
        _queueScroll.Scrolled += OnQueueScrolled;
        _queueScroll.ContentSizeChanged += ScheduleQueuePosition;
        _queueScroll.RowsRefreshed += SyncRowClock;
        _outsideSwipeTap.Tapped += (_, args) =>
        {
            if (_swipeRow is { } row && args.GetPosition(row) is { } point &&
                (point.X < 0 || point.X >= row.Width || point.Y < 0 || point.Y >= row.Height))
                DismissDeleteAction();
        };
        _queueScroll.SizeChanged += (_, _) => ScheduleQueuePosition();
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
        double header = IsEditing ? Math.Max(44, _editorHeader.Height) + Math.Max(16, _editorHint.Height) + _queue.Spacing
            : Math.Max(0, _feedSelector.Height);
        double available = Height > 0 ? Math.Max(0, Height - header - _queue.Spacing) : ViewportHeight;
        double maximum = IsEditing ? available : Math.Min(available, ViewportHeight);
        _queueScroll.MaximumHeightRequest = maximum;
        double viewport = Math.Min(TrackRowsHeight, maximum);
        _queueScroll.HeightRequest = viewport;
        _queueScroll.SetEndPadding(!IsEditing && _tracks.Count > 0 ? QueueViewport.EndPadding(viewport, TrackListLayout.RowHeight) : 0);
        ScheduleQueuePosition();
    }

    public event Action<TrackFile>? TrackRequested;
    public event Action<TrackFile>? HistoryRequested;
    public event Action<TrackFile, TrackFile, bool>? MoveRequested;
    public event Action<TrackFile>? RemoveRequested;
    public event Action<bool>? RecommendationsSelected;
    private MixerTheme _theme = MixerTheme.Default;

    internal void SetTheme(MixerTheme theme)
    {
        _theme = theme;
        _current.ApplyPalette(theme.Artwork);
        _incoming.ApplyPalette(theme.Artwork);
        _queueScroll.Refresh(force: true);
    }

    /// <summary>Groups the two playback sources in a single segmented control.</summary>
    private View CreateFeedSelector()
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
            if (_showRecommendations) SelectRecommendations(false);
            else OpenEditor();
        };
        _recommendationsTab.Clicked += (_, _) => SelectRecommendations(true);
        var library = MixerStyle.IconButton("player_folder.png", 44);
        library.CornerRadius = _queueTab.CornerRadius;
        SemanticProperties.SetDescription(library, "Открыть медиатеку");
        library.Clicked += (_, _) => LibraryRequested?.Invoke();
        var segments = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Auto)] };
        segments.Add(_recommendationsTab);
        segments.Add(_queueTab, 1);
        segments.Add(library, 2);
        var selector = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Padding = 2,
            Content = segments
        };
        MixerStyle.UseColor(selector, BackgroundColorProperty, MixerColor.Surface);
        MixerStyle.UseBrush(selector, Border.StrokeProperty, MixerColor.Border);
        return selector;
    }

    /// <summary>Switches the visible feed and tells playback which source to use.</summary>
    internal void SelectRecommendations(bool show)
    {
        if (_showRecommendations == show)
        {
            return;
        }
        DismissDeleteAction();
        if (show) CloseEditor();
        _showRecommendations = show;
        _queueScroll.IsVisible = !show;
        _recommendations.IsVisible = show;
        SetTabColors();
        if (!show)
        {
            _queueScroll.Refresh(force: true);
            ScheduleQueuePosition();
        }
        else
        {
            _recommendations.Refresh(force: true);
            SyncRowClock();
        }
        RecommendationsSelected?.Invoke(show);
    }

    private void SetTabColors()
    {
        StyleTab(_queueTab, !_showRecommendations);
        StyleTab(_recommendationsTab, _showRecommendations);
        SemanticProperties.SetDescription(_queueTab, _showRecommendations ? "Открыть очередь" : "Очередь. Нажми для редактирования.");
        SemanticProperties.SetDescription(_recommendationsTab, _showRecommendations ? "Рекомендации, выбраны" : "Рекомендации");
    }

    private static void StyleTab(Button button, bool selected)
    {
        MixerStyle.UseColor(button, BackgroundColorProperty, selected ? MixerColor.Selected : MixerColor.Surface);
        MixerStyle.UseColor(button, Button.TextColorProperty, selected ? MixerColor.Text : MixerColor.Muted);
    }

    /// <summary>Updates the queue model and rebinds only the visible window of rows.</summary>
    public void ShowQueue(IReadOnlyList<TrackFile> tracks, TrackFile? current, int playedCount, Func<TrackFile, QueueTrackItem> read)
    {
        if (!ReferenceEquals(_currentTrack, current))
            _followingTrack = current is not null && ((_switchingViewport && IsEditing) || CanFollowTrack);
        _tracks = tracks;
        _currentTrack = current;
        _playedCount = playedCount;
        _readRow = read;
        UpdateQueueViewport();
        _queueScroll.SetItems(_tracks, "Очередь пуста");
    }

    private void BindRow(QueueTrackRow row, TrackFile file, int index)
    {
        row.Bind(file, index);
        row.SetEditing(IsEditing);
        if (_readRow is { } read)
            row.Show(read(file), ReferenceEquals(file, _currentTrack), index < _playedCount, _theme);
    }

    private void ReleaseRow(QueueTrackRow row)
    {
        if (ReferenceEquals(row, _swipeRow)) DismissDeleteAction();
        row.Unbind();
    }

    public void SetMasterSpectrum(IDrawable drawable) => _masterSpectrum = drawable;

    public void InvalidateMasterSpectrum()
    {
        if (_showRecommendations || _queue.Opacity == 0 || _currentTrack is not { } file ||
            _queueScroll.FindRow(file) is not { } row) return;
        if (_queueScroll.IsInViewport(row.Index)) row.InvalidateSpectrum();
    }

    internal bool OpenEditor()
    {
        if (IsEditing || !_rowAnimationsEnabled || !_queueTab.IsVisible || _queue.InputTransparent) return false;
        SelectRecommendations(false);
        SetEditing(true);
        return true;
    }

    internal bool CloseEditor()
    {
        if (!IsEditing) return false;
        EndDrag();
        DismissDeleteAction();
        SetEditing(false);
        return true;
    }

    private void SetEditing(bool editing)
    {
        DismissDeleteAction();
        PrepareViewportChange(editing);
        IsEditing = editing;
        _feedSelector.IsVisible = !editing;
        _editorHeader.IsVisible = _editorHint.IsVisible = editing;
        _current.IsVisible = !editing;
        Grid.SetColumn(_right, editing ? 0 : 1);
        Grid.SetColumnSpan(_right, editing ? 2 : 1);
        EditingChanged?.Invoke(editing);
        UpdateQueueViewport();
        _queueScroll.Refresh(force: true);
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
        DismissDeleteAction();
        CloseEditor();
        CancelTransition();
        RequestFrames(false);
        _animation++;
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
        ScheduleQueuePosition();
        SyncRowClock();
    }

    public bool IsHandingOff => _handoff;

}
