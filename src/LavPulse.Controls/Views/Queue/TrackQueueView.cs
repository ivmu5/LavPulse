using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.LavPulse.Controls;

/// <summary>Shows the current cover next to the queue and animates the handoff between both halves.</summary>
internal sealed partial class TrackQueueView : Grid
{
    private const int RowStride = TrackListLayout.Stride;
    private const int ViewportHeight = 236;
    private const int CoverInfoGap = 8;
    private TrackCard _current = new(TextAlignment.Start, showAudioInfo: true);
    private TrackCard _incoming = new(TextAlignment.Start, showAudioInfo: true);
    private readonly Grid _coverHost = new() { IsClippedToBounds = true };
    private View? _centerDeck;
    private readonly Grid _centerHost = new()
    {
        RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
        RowSpacing = 6,
        IsClippedToBounds = true
    };
    private readonly Label _centerAudioInfo = MixerStyle.Label("", MixerStyle.PhoneFont(12, 11), MixerColor.Muted);
    private CachedTrack? _centerInfoTrack;
    private readonly VerticalStackLayout _queue;
    private readonly View _feedSelector;
    private readonly Grid _right;
    private readonly Grid _editorHeader;
    private readonly VerticalStackLayout _editorSideActions = new()
    {
        IsVisible = false,
        Spacing = 8,
        VerticalOptions = LayoutOptions.Start
    };
    private bool _wideEditor;
    private bool _doneOnRail;
    private readonly Button _editorDone = MixerStyle.Button("Готово", primary: true, icon: "player_confirm.png");
    private readonly Label _editorHint = MixerStyle.Label("", 11, MixerColor.Muted);
    private readonly Button _clearQueue = MixerStyle.Button("Очистить");
    private readonly ListEditToolbar _editToolbar;
    private bool _selectionActionRunning;
    internal Func<IReadOnlyList<TrackFile>, ListEditAction, Task>? SelectionActionRequested { get; set; }
    private readonly Button _queueTab = MixerStyle.Button("Очередь", icon: "player_queue.png", stacked: true);
    private readonly Button _recommendationsTab = MixerStyle.Button("Рекомендации", icon: "player_recommendations.png", stacked: true);
    private readonly TrackListView<TrackFile, QueueTrackRow> _queueScroll;
    private readonly TrackListView<TrackFile, QueueTrackRow> _recommendations;
    private IReadOnlyList<TrackFile> _tracks = [];
    private Func<TrackFile, QueueTrackItem>? _readRow;
    private int _playedCount;
    private TrackFile? _currentTrack;
    private IDrawable? _masterSpectrum;
    private bool _showRecommendations;
    private bool _deletingTracks;
    private readonly HashSet<TrackFile> _checkedTracks = [];
    internal Func<IReadOnlyList<TrackFile>, Point?, Task>? ActionsRequested { get; set; }
    internal Func<TrackFile, ListSwipeAction, Task>? SwipeRequested { get; set; }
    internal PlayerSettings ListSettings { get; set; } = new();

    internal event Action<bool>? AnimationChanged;
    internal event Action<bool>? EditingChanged;
    internal event Action? LibraryRequested;
    internal bool IsEditing { get; private set; }

    public TrackQueueView()
    {
        ColumnDefinitions = [new(GridLength.Star), new(0), new(GridLength.Star)];
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
            HeightRequest = TrackListLayout.RowHeight,
            IsVisible = false
        };
        _recommendations.SetItems([], "Рекомендации пока недоступны");
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
        _editorDone.IsVisible = false;
        _editorDone.HorizontalOptions = LayoutOptions.Fill;
        _editorDone.Clicked += (_, _) => CloseEditor();
        _clearQueue.HorizontalOptions = LayoutOptions.End;
        _clearQueue.IsEnabled = false;
        SemanticProperties.SetDescription(_clearQueue, "Очистить очередь");
        _clearQueue.Clicked += (_, _) =>
        {
            if (IsEditing && CanEditRows && _dragging is null && _tracks.Count > 0)
                ClearRequested?.Invoke();
        };
        _editToolbar = new ListEditToolbar(false, () => CloseEditor(), ExecuteSelectionActionAsync);
        _editToolbar.SizeChanged += (_, _) => UpdateQueueViewport();
        UpdateEditorActions();
        var title = MixerStyle.Label("Редактор очереди", MixerStyle.PhoneFont(19, 16), bold: true);
        title.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
#if WINDOWS
        title.IsVisible=false;
#endif
        _editorHeader = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 6,
            RowSpacing = 4,
            IsVisible = false
        };
        _editorHeader.Add(title);
        _editorHeader.Add(_clearQueue, column: 1);
        _editorHeader.Add(_editToolbar, row: 1);
        Grid.SetColumnSpan(_editToolbar, 2);
        _editorHint.IsVisible = false;
        _queue = new VerticalStackLayout
        {
            Spacing = 7,
            Children = { _feedSelector, _editorHeader, _editorHint, _queueScroll, _recommendations }
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
        _right = new Grid
        {
            ColumnDefinitions = [new(0), new(GridLength.Star), new(0)],
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            RowSpacing = 0
        };
        _right.Add(_queue, 1, 0);
        _right.Add(_editorDone, 1, 1);
        _right.Add(_editorSideActions, 2, 0);
        _editorDone.SizeChanged += (_, _) => UpdateQueueViewport();
        _incoming.IsVisible = false;
        _coverHost.Children.Add(_current);
        _coverHost.Children.Add(_incoming);
        this.Add(_coverHost);
        this.Add(_right, 2);
        SizeChanged += (_, _) =>
        {
            UpdatePlaybackColumns();
            UpdateQueueViewport();
        };
        _right.SizeChanged += (_, _) =>
        {
            if (IsEditing || _right.Width <= 0) return;
            bool iconOnly = _right.Width < 205;
            _queueTab.Text = iconOnly ? "" : "Очередь";
            _recommendationsTab.Text = iconOnly ? "" : "Рекомендации";
        };
        _coverHost.SizeChanged += (_, _) => ResizePlaybackCovers();
        _centerAudioInfo.IsVisible = false;
        _centerAudioInfo.LineBreakMode = LineBreakMode.WordWrap;
        _centerAudioInfo.MaxLines = 3;
        // Keep file details beside the artwork, not centered over the deck.
        // The metadata does not participate in horizontal waveform sizing.
        _centerAudioInfo.HorizontalTextAlignment = TextAlignment.Start;
        _centerAudioInfo.HorizontalOptions = LayoutOptions.Start;
        _centerAudioInfo.VerticalOptions = LayoutOptions.Start;
        _centerHost.Add(_centerAudioInfo);
        _centerHost.SizeChanged += (_, _) => UpdateCenterDeckViewport();
        _centerAudioInfo.SizeChanged += (_, _) => UpdateCenterDeckViewport();
    }

    internal void SetCenterDeck(View? deck)
    {
        if (ReferenceEquals(_centerDeck, deck)) return;
        if (_centerDeck is not null)
        {
            _centerHost.Children.Remove(_centerDeck);
            _centerDeck.VerticalOptions = LayoutOptions.Fill;
            Children.Remove(_centerHost);
        }
        _centerDeck = deck;
        if (deck is not null)
        {
            // The waveform scroll view fills the width of the bounded star
            // row, anchored at its lower edge above the transport controls.
            deck.HorizontalOptions = LayoutOptions.Fill;
            deck.VerticalOptions = LayoutOptions.End;
            _centerHost.Add(deck, row: 1);
            this.Add(_centerHost, 1);
            UpdateCenterAudioInfo();
        }
        UpdatePlaybackColumns();
    }

    private void UpdateCenterAudioInfo()
    {
        var track = _current.Displayed;
        if (!ReferenceEquals(_centerInfoTrack, track))
        {
            _centerInfoTrack = track;
            _centerAudioInfo.Text = track is null ? "" : track.AudioInfo is { } audio
                ? TrackCard.FormatAudioInfo(audio, compact: true)
                : "Параметры аудиофайла недоступны";
            _centerAudioInfo.IsVisible = track is not null;
        }
        UpdateCenterDeckViewport();
    }

    private void UpdateCenterDeckViewport()
    {
        if (_centerDeck is not { } deck || _centerHost.Height <= 0) return;
        double header = _centerAudioInfo.IsVisible ? Math.Max(0, _centerAudioInfo.Height) : 0;
        double available = Math.Max(0, _centerHost.Height - header - _centerHost.RowSpacing);
        if (deck.MaximumHeightRequest != available) deck.MaximumHeightRequest = available;
    }

    private void UpdatePlaybackColumns()
    {
        if (IsEditing) return;
        bool split = _centerDeck is not null;
        ColumnSpacing = split ? 10 : 0;
        // Equal outer rails guarantee that the center of both waveforms
        // coincides with the center of the player at every window width.
        double rail = split ? AdaptivePanelLayout.PlaybackSideWidth(Width) : 0;
        SetColumnWidth(0, split ? new GridLength(rail) : GridLength.Star);
        SetColumnWidth(1, split ? GridLength.Star : new GridLength(0));
        SetColumnWidth(2, split ? new GridLength(rail) : GridLength.Star);
        // With a collapsed middle column, add only one real gutter.
        _right.Margin = split ? new Thickness(0) : new Thickness(MixerStyle.IsPhone ? 12 : 16, 0, 0, 0);
        ResizePlaybackCovers();
    }

    private void SetColumnWidth(int column, GridLength width)
    {
        if (!ColumnDefinitions[column].Width.Equals(width)) ColumnDefinitions[column].Width = width;
    }

    private void ResizePlaybackCovers()
    {
        if (IsEditing || _coverHost.Width <= 0 || _coverHost.Height <= 0) return;
        double width = _coverHost.Width;
        bool compact = _centerDeck is not null;
        double infoWidth = compact ? 0 : Math.Min(width * .48, MixerStyle.PhoneFont(94, 70));
        double preferred = compact ? Math.Min(202, width) : Math.Clamp(Width * .29, 116, 202) * 1.1;
        double side = Math.Max(1, Math.Min(preferred,
            Math.Min(width - infoWidth - (compact ? 0 : CoverInfoGap), _coverHost.Height - (compact ? 64 : 52))));
        _current.ResizeCover(side, width, compact);
        _incoming.ResizeCover(side, width, compact);
    }

    /// <summary>Uses the shared panel rails only while editing.</summary>
    internal void SetEditorLayout(AdaptivePanelLayout layout, bool shortWindow = false)
    {
        bool wide = IsEditing && layout.IsWide;
        double left = wide ? layout.LeftColumnWidth(navigation: false) : 0;
        double right = wide ? layout.RightColumnWidth(navigation: false) : 0;
        double gap = wide ? layout.ColumnSpacing : 0;
        _clearQueue.LineBreakMode = LineBreakMode.WordWrap;
        bool geometryChanged = _right.ColumnDefinitions[0].Width.Value != left ||
            _right.ColumnDefinitions[2].Width.Value != right || _right.ColumnSpacing != gap;
        if (geometryChanged)
        {
            EndDrag();
            _right.ColumnDefinitions[0].Width = new GridLength(left);
            _right.ColumnDefinitions[2].Width = new GridLength(right);
            _right.ColumnSpacing = gap;
        }
        _editorSideActions.Margin = new Thickness(wide ? layout.RailGutter : 0, 0, 0, 0);
        bool doneOnRail = wide && shortWindow;
        if (_doneOnRail != doneOnRail)
        {
            _doneOnRail = doneOnRail;
            if (doneOnRail)
            {
                _right.Children.Remove(_editorDone);
                _editorSideActions.Children.Add(_editorDone);
            }
            else
            {
                _editorSideActions.Children.Remove(_editorDone);
                _right.Add(_editorDone, 1, 1);
            }
        }
        _editorDone.LineBreakMode = LineBreakMode.WordWrap;
        _editorHint.IsVisible = IsEditing && !shortWindow;
        if (_wideEditor == wide)
        {
            if (IsEditing) UpdateQueueViewport();
            return;
        }

        // A native drag can outlive its old geometry during a window resize.
        // Cancel it rather than applying a drop against a different viewport.
        EndDrag();
        _wideEditor = wide;
        if (wide)
        {
            _editorHeader.Children.Remove(_clearQueue);
            _clearQueue.HorizontalOptions = LayoutOptions.Fill;
            Grid.SetColumn(_clearQueue, 0);
            Grid.SetRow(_clearQueue, 0);
            _editorSideActions.Children.Insert(0, _clearQueue);
        }
        else
        {
            _editorSideActions.Children.Remove(_clearQueue);
            _clearQueue.HorizontalOptions = LayoutOptions.End;
            _editorHeader.Add(_clearQueue, column: 1);
        }
        _editorSideActions.IsVisible = wide;
        _editorHeader.IsVisible = IsEditing;
        UpdateQueueViewport();
    }

    private void UpdateQueueViewport()
    {
        double header = IsEditing
            ? (_editorHeader.IsVisible ? Math.Max(_wideEditor ? 20 : 44, _editorHeader.Height) + _queue.Spacing : 0)
                + (_editorHint.IsVisible ? Math.Max(16, _editorHint.Height) + _queue.Spacing : 0)
            : Math.Max(0, _feedSelector.Height);
        double footer = IsEditing && !_doneOnRail ? Math.Max(44, _editorDone.Height) : 0;
        double available = Height > 0 ? Math.Max(0, Height - header - footer - _queue.Spacing) : ViewportHeight;
        double maximum = IsEditing ? available : Math.Min(available, ViewportHeight);
        _queueScroll.MaximumHeightRequest = maximum;
        double viewport = Math.Min(TrackRowsHeight, maximum);
        _queueScroll.HeightRequest = viewport;
        _queueScroll.SetEndPadding(!IsEditing && _tracks.Count > 0 ? QueueViewport.EndPadding(viewport, TrackListLayout.RowHeight) : 0);
        ScheduleQueuePosition();
    }

    public event Action<TrackFile>? TrackRequested;
    public event Action<TrackFile>? HistoryRequested;
    public event Action<TrackFile>? PlayRequested;
    public event Action? ClearRequested;
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
        ToolTipProperties.SetText(_queueTab, "Очередь");
        ToolTipProperties.SetText(_recommendationsTab, "Рекомендации");
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
        SemanticProperties.SetDescription(_queueTab, _showRecommendations ? "Открыть очередь" : "Редактировать очередь");
        SemanticProperties.SetDescription(_recommendationsTab, _showRecommendations ? "Рекомендации (выбраны)" : "Рекомендации");
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
        _checkedTracks.RemoveWhere(file => !tracks.Contains(file));
        UpdateEditorActions();
        _clearQueue.IsEnabled = !_selectionActionRunning && tracks.Count > 0;
        _currentTrack = current;
        _playedCount = playedCount;
        _readRow = read;
        UpdateQueueViewport();
        _queueScroll.SetItems(_tracks, "Очередь пуста");
    }

    private void BindRow(QueueTrackRow row, TrackFile file, int index)
    {
        row.Bind(file, index);
        row.SetEditorActions(IsEditing, false);
        row.SetSelectionMark(IsEditing, _checkedTracks.Contains(file));
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
        if (_showRecommendations || _queue.Opacity == 0) return;
        foreach(var row in _queueScroll.RealizedRows)
            if(row.IsActivePlayback && _queueScroll.IsInViewport(row.Index)) row.InvalidateSpectrum();
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

    internal bool ExitDeleteMode() => SetDeleteMode(false);

    private bool SetDeleteMode(bool deleting)
    {
        if (!IsEditing || _deletingTracks == deleting || _dragging is not null || (deleting && !CanEditRows)) return false;
        _deletingTracks = deleting;
        UpdateEditorActions();
        _queueScroll.Refresh(force: true);
        return true;
    }

    private async Task ExecuteSelectionActionAsync(ListEditAction action)
    {
        if (!IsEditing || !CanEditRows || _dragging is not null || _selectionActionRunning ||
            _checkedTracks.Count == 0 || SelectionActionRequested is not { } execute) return;
        var files = _tracks.Where(_checkedTracks.Contains).ToArray();
        _selectionActionRunning = true;
        UpdateEditorActions();
        try { await execute(files, action); }
        finally
        {
            _selectionActionRunning = false;
            UpdateEditorActions();
        }
    }

    private void UpdateEditorActions()
    {
        _editToolbar.Update(IsEditing, _selectionActionRunning, _checkedTracks.Count,
            _checkedTracks.Count == 1,
            _checkedTracks.Count == 1 && _checkedTracks.First().LocalPath is not null,
            SelectionActionRequested is not null);
        _editorDone.IsEnabled = !_selectionActionRunning;
        SemanticProperties.SetDescription(_editToolbar, "Отметьте треки. Для изменения порядка используйте ручку перетаскивания.");
        _clearQueue.IsEnabled = !_selectionActionRunning && _tracks.Count > 0;
        _editorHint.Text = "Отметьте треки. Перетаскивание за ручку — изменение порядка.";
    }

    private void SetEditing(bool editing)
    {
        DismissDeleteAction();
        PrepareViewportChange(editing);
        IsEditing = editing;
        _checkedTracks.Clear();
        _deletingTracks = false;
        UpdateEditorActions();
        _feedSelector.IsVisible = !editing;
        _editorHeader.IsVisible = editing;
        _editorHint.IsVisible = _editorDone.IsVisible = editing;
        if (editing) ResetCoverTransition();
        _coverHost.IsVisible = !editing;
        Grid.SetColumn(_right, editing ? 0 : 2);
        Grid.SetColumnSpan(_right, editing ? 3 : 1);
        if (editing) _right.Margin = new Thickness(0);
        else UpdatePlaybackColumns();
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
}
