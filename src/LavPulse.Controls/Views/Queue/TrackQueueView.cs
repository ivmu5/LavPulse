using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.LavPulse.Controls;

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
    private readonly Label _editorHint = MixerStyle.Label("За три полоски можно передвигать треки. Корзина удаляет строку.", 11, MixerColor.Muted);
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
            HeightRequest = TrackListLayout.RowHeight,
            IsVisible = false
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
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 6,
            IsVisible = false
        };
        _editorHeader.Add(title);
        _editorHeader.Add(done, 1);
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
}
