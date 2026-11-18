using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;
using System.Diagnostics;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Shows the current cover next to the queue and animates the handoff between both halves.</summary>
internal sealed class TrackQueueView : Grid
{
    private const int TrackSpacing = 5;
    private readonly TrackCard _current = new(TextAlignment.Start);
    private readonly TrackCard _incoming = new(TextAlignment.End);
    private readonly VerticalStackLayout _rows = new() { Spacing = TrackSpacing };
    private readonly VerticalStackLayout _queue;
    private readonly Grid _right;
    private readonly Button _queueTab = MixerStyle.Button("Очередь", icon: "player_queue.png", stacked: true);
    private readonly Button _recommendationsTab = MixerStyle.Button("Рекомендации", icon: "player_recommendations.png", stacked: true);
    private readonly ScrollView _queueScroll;
    private readonly Label _recommendations = MixerStyle.Label("В разработке", 13, MixerStyle.Muted);
    private readonly Dictionary<TrackFile, QueueTrackRow> _trackRows = new();
    private readonly Label _empty = MixerStyle.Label("Очередь пуста", 12, MixerStyle.Muted);
    private readonly IDispatcherTimer _rowClock;
    private readonly long _start = Stopwatch.GetTimestamp();
    private TrackFile? _currentTrack;
    private bool _focusCurrent;
    private TrackFile? _dragging;
    private QueueTrackRow? _dropTarget;
    private int _dragScrollDirection;
    private long _lastDragScroll;
    private long _lastDrag;
    private bool _showRecommendations;
    private int _animation;
    private bool _handoff;

    public TrackQueueView()
    {
        ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)];
        ColumnSpacing = 16;
        MinimumHeightRequest = 208;
        _queueScroll = new ScrollView { Content = _rows, MaximumHeightRequest = 236 };
        _rowClock = Dispatcher.CreateTimer();
        _rowClock.Interval = TimeSpan.FromMilliseconds(40);
        _rowClock.Tick += (_, _) => AnimateRows();
        Loaded += (_, _) =>
        {
            _rowClock.Start();
            FocusCurrent();
        };
        Unloaded += (_, _) => _rowClock.Stop();
        _recommendations.IsVisible = false;
        _queue = new VerticalStackLayout { Spacing = 7, Children = { CreateFeedSelector(), _queueScroll, _recommendations } };
        SetTabColors();
        _right = new Grid { Children = { _queue, _incoming } };
        _incoming.IsVisible = false;
        this.Add(_current);
        this.Add(_right, 1);
        SizeChanged += (_, _) =>
        {
            double size = Math.Min(Math.Max(0, (Width - ColumnSpacing) / 2), Math.Clamp(Width * .29, 116, 202));
            _current.ResizeCover(size);
            _incoming.ResizeCover(size);
        };
    }

    public event Action<TrackFile>? TrackRequested;
    public event Action<TrackFile>? HistoryRequested;
    public event Action<TrackFile, TrackFile, bool>? MoveRequested;
    public event Action<bool>? RecommendationsSelected;
    public Color Accent { get; set; } = MixerStyle.Accent;

    /// <summary>Groups the two playback sources in a single segmented control.</summary>
    private Border CreateFeedSelector()
    {
        _queueTab.CornerRadius = _recommendationsTab.CornerRadius = 7;
        _queueTab.Clicked += (_, _) =>
        {
            if (_showRecommendations)
            {
                SelectRecommendations(false);
            }
            FocusCurrent();
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
        RecommendationsSelected?.Invoke(show);
    }

    private void SetTabColors()
    {
        _queueTab.BackgroundColor = _showRecommendations ? Colors.Transparent : MixerStyle.SelectedBackground;
        _recommendationsTab.BackgroundColor = _showRecommendations ? MixerStyle.SelectedBackground : Colors.Transparent;
        _queueTab.TextColor = _showRecommendations ? MixerStyle.Muted : MixerStyle.TextColor;
        _recommendationsTab.TextColor = _showRecommendations ? MixerStyle.TextColor : MixerStyle.Muted;
        SemanticProperties.SetDescription(_queueTab, _showRecommendations ? "Очередь" :
            "Очередь. Нажми, чтобы перейти к текущему треку.");
        SemanticProperties.SetDescription(_recommendationsTab, _showRecommendations ? "Рекомендации, выбраны" : "Рекомендации");
    }

    /// <summary>Keeps row views stable while background analysis updates their contents.</summary>
    public void ShowQueue(IReadOnlyList<TrackFile> tracks, TrackFile? current, TrackFile? next, Func<TrackFile, QueueTrackItem> read)
    {
        _focusCurrent |= !ReferenceEquals(_currentTrack, current);
        _currentTrack = current;
        var ordered = new List<View>(tracks.Count);
        var visible = new HashSet<TrackFile>();
        bool beforeCurrent = current is not null;
        foreach (var file in tracks)
        {
            bool playing = ReferenceEquals(file, current);
            if (playing)
            {
                beforeCurrent = false;
            }
            if (!_trackRows.TryGetValue(file, out var row))
            {
                row = CreateRow(file);
                _trackRows[file] = row;
            }
            visible.Add(file);
            row.Show(read(file), playing, beforeCurrent, ReferenceEquals(next, file), Accent);
            ordered.Add(row);
        }
        foreach (var file in _trackRows.Keys.Where(file => !visible.Contains(file)).ToArray())
        {
            _trackRows.Remove(file);
        }
        if (ordered.Count == 0)
        {
            ordered.Add(_empty);
        }
        if (!_rows.Children.SequenceEqual(ordered))
        {
            _rows.Clear();
            foreach (var row in ordered)
            {
                _rows.Add(row);
            }
        }

        if (_focusCurrent && !_showRecommendations && _queue.Opacity > 0)
        {
            FocusCurrent();
        }
    }

    private void FocusCurrent()
    {
        if (_currentTrack is { } track && _trackRows.TryGetValue(track, out var row))
        {
            _focusCurrent = false;
            _ = _queueScroll.ScrollToAsync(row, ScrollToPosition.MakeVisible, false);
        }
    }

    private QueueTrackRow CreateRow(TrackFile file)
    {
        var row = new QueueTrackRow();
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (_dragging is null && (_lastDrag == 0 || Stopwatch.GetElapsedTime(_lastDrag).TotalMilliseconds >= 300))
            {
                if (row.IsPlayed) HistoryRequested?.Invoke(file);
                else if (!ReferenceEquals(_currentTrack, file)) TrackRequested?.Invoke(file);
            }
        };
        row.GestureRecognizers.Add(tap);
        var drag = new DragGestureRecognizer { CanDrag = true };
        drag.DragStarting += (_, args) =>
        {
            if (_queue.InputTransparent || _showRecommendations)
            {
                args.Cancel = true;
                return;
            }
            _dragging = file;
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
            if (!CanReorder() || _dragging is not { } source)
            {
                return;
            }
            bool after = args.GetPosition(row) is { } point ? point.Y >= row.Height / 2
                : _rows.Children.IndexOf(_trackRows[source]) < _rows.Children.IndexOf(row);
            args.Handled = true;
            EndDrag();
            Dispatcher.Dispatch(() => MoveRequested?.Invoke(source, file, after));
        };
        row.GestureRecognizers.Add(drop);
        return row;

        bool CanReorder() => !_queue.InputTransparent && !_showRecommendations && _dragging is { } source &&
            !ReferenceEquals(source, file) && _trackRows.ContainsKey(source);
    }

    private void EndDrag()
    {
        _dragging = null;
        _lastDrag = Stopwatch.GetTimestamp();
        _dragScrollDirection = 0;
        ClearDropTarget();
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
        if (_showRecommendations || _queue.Opacity == 0 || _queueScroll.Height <= 0)
        {
            return;
        }
        const int stride = QueueTrackRow.RowHeight + TrackSpacing;
        if (_dragScrollDirection != 0 && Stopwatch.GetElapsedTime(_lastDragScroll).TotalMilliseconds >= 120)
        {
            _lastDragScroll = Stopwatch.GetTimestamp();
            double y = Math.Clamp(_queueScroll.ScrollY + _dragScrollDirection * stride / 2d, 0, Math.Max(0, _rows.Height - _queueScroll.Height));
            if (y != _queueScroll.ScrollY) _ = _queueScroll.ScrollToAsync(0, y, false);
        }
        int first = Math.Max(0, (int)(_queueScroll.ScrollY / stride));
        int end = Math.Min(_rows.Count, (int)Math.Ceiling((_queueScroll.ScrollY + _queueScroll.Height) / stride));
        double seconds = Stopwatch.GetElapsedTime(_start).TotalSeconds;
        for (int i = first; i < end; i++)
        {
            if (_rows[i] is QueueTrackRow row) row.Animate(seconds);
        }
    }

    /// <summary>Updates the stable left-side cover outside of a handoff.</summary>
    public void ShowCurrent(CachedTrack? track) => _current.Show(track);

    /// <summary>Expands the selected row into the full incoming card over the queue.</summary>
    public async Task BeginMixAsync(CachedTrack track, TrackFile? file)
    {
        _animation++;
        _queue.InputTransparent = true;
        _queue.Opacity = 0;
        _incoming.Show(track, "Сведение");
        EndDrag();
        var row = file is not null && _trackRows.TryGetValue(file, out var item) ? item : null;
        double originY = row is null ? 0 : Math.Clamp(_queueScroll.Y + row.Y - _queueScroll.ScrollY, 0, Math.Max(0, _right.Height - 44));
        double rowHeight = Math.Max(44, row?.Height ?? 0);
        _incoming.TranslationY = originY;
        _incoming.ScaleX = .96;
        _incoming.ScaleY = Math.Clamp(rowHeight / Math.Max(1, _right.Height), .15, 1);
        _incoming.AnchorY = 0;
        _incoming.Opacity = 1;
        _incoming.IsVisible = true;
        await Task.WhenAll(
            _incoming.TranslateToAsync(0, 0, 260, Easing.CubicOut),
            _incoming.ScaleXToAsync(1, 260, Easing.CubicOut),
            _incoming.ScaleYToAsync(1, 260, Easing.CubicOut));
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

    /// <summary>Clears animations and restores a stable screen when the window is closing.</summary>
    public void Reset()
    {
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
        _rowClock.Stop();
    }

    public bool IsHandingOff => _handoff;

    private sealed class TrackCard : VerticalStackLayout
    {
        private readonly Image _art = new() { Aspect = Aspect.AspectFill };
        private readonly Label _title = MixerStyle.Label("", 22, bold: true);
        private readonly Label _artist = MixerStyle.Label("", 13, MixerStyle.Muted);
        private readonly Label _state = MixerStyle.Label("", 11);
        private readonly Border _cover;
        private CachedTrack? _displayed;

        public TrackCard(TextAlignment align)
        {
            Spacing = 7;
            bool right = align == TextAlignment.End;
            _cover = new Border
            {
                WidthRequest = 116, HeightRequest = 116,
                HorizontalOptions = right ? LayoutOptions.End : LayoutOptions.Start,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Background = new LinearGradientBrush(
                    [new(MixerStyle.Blue.WithAlpha(.65f), 0), new(MixerStyle.Violet.WithAlpha(.5f), .5f), new(MixerStyle.Alternate.WithAlpha(.7f), 1)],
                    new Point(0, 0), new Point(1, 1)),
                Content = new Grid { Children = { _art, _state } }
            };
            _title.HorizontalTextAlignment = _artist.HorizontalTextAlignment = align;
            _title.HorizontalOptions = _artist.HorizontalOptions = right ? LayoutOptions.End : LayoutOptions.Start;
            _title.MaxLines = 2;
            _title.LineBreakMode = LineBreakMode.TailTruncation;
            _artist.MaxLines = 1;
            _artist.LineBreakMode = LineBreakMode.TailTruncation;
            _state.HorizontalOptions = LayoutOptions.End;
            _state.VerticalOptions = LayoutOptions.End;
            _state.BackgroundColor = MixerStyle.Background;
            _state.Margin = 6;
            _state.Padding = new Thickness(5, 2);
            Children.Add(_cover);
            Children.Add(_title);
            Children.Add(_artist);
        }

        /// <summary>Shows imported tags and artwork, or a gradient for a file without cover art.</summary>
        public void Show(CachedTrack? track, string state = "")
        {
            if (!ReferenceEquals(_displayed, track))
            {
                _displayed = track;
                byte[]? art = track is not null && !track.Artwork.IsEmpty ? track.Artwork.ToArray() : null;
                _art.Source = art is null ? null : ImageSource.FromStream(() => new System.IO.MemoryStream(art, writable: false));
            }
            _title.Text = track?.Title ?? "Выбери трек";
            _artist.Text = track is null ? "" : track.Artist ?? "Неизвестный исполнитель";
            _artist.IsVisible = track is not null;
            _state.Text = state;
            _state.IsVisible = state.Length > 0;
        }

        /// <summary>Keeps a square cover and aligns long names within its width.</summary>
        public void ResizeCover(double side)
        {
            if (side <= 0)
            {
                return;
            }
            _cover.WidthRequest = _cover.HeightRequest = side;
            _title.WidthRequest = _artist.WidthRequest = side;
        }

        public double CoverWidth => _cover.Width > 0 ? _cover.Width : _cover.WidthRequest;
    }
}
