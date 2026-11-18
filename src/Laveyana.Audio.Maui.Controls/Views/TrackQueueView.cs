using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Shows the current cover next to the queue and animates the handoff between both halves.</summary>
internal sealed class TrackQueueView : Grid
{
    private readonly TrackCard _current = new(TextAlignment.Start);
    private readonly TrackCard _incoming = new(TextAlignment.End);
    private readonly VerticalStackLayout _rows = new() { Spacing = 5 };
    private readonly VerticalStackLayout _queue;
    private readonly Grid _right;
    private readonly Button _queueTab = MixerStyle.Button("Очередь");
    private readonly Button _recommendationsTab = MixerStyle.Button("Рекомендации");
    private readonly ScrollView _queueScroll;
    private readonly Label _recommendations = MixerStyle.Label("В разработке", 13, MixerStyle.Muted);
    private readonly Dictionary<TrackFile, Button> _buttons = new();
    private bool _showRecommendations;
    private int _animation;
    private bool _handoff;

    public TrackQueueView()
    {
        ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)];
        ColumnSpacing = 16;
        MinimumHeightRequest = 208;
        _queueScroll = new ScrollView { Content = _rows, MaximumHeightRequest = 176 };
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
    public event Action<bool>? RecommendationsSelected;
    public Color Accent { get; set; } = MixerStyle.Accent;

    /// <summary>Groups the two playback sources in a single segmented control.</summary>
    private Border CreateFeedSelector()
    {
        _queueTab.FontSize = _recommendationsTab.FontSize = 10;
        _queueTab.Padding = _recommendationsTab.Padding = new Thickness(4, 2);
        _queueTab.CornerRadius = _recommendationsTab.CornerRadius = 7;
        _queueTab.LineBreakMode = _recommendationsTab.LineBreakMode = LineBreakMode.CharacterWrap;
        _queueTab.Clicked += (_, _) => SelectRecommendations(false);
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
        SemanticProperties.SetDescription(_queueTab, _showRecommendations ? "Очередь" : "Очередь, выбрана");
        SemanticProperties.SetDescription(_recommendationsTab, _showRecommendations ? "Рекомендации, выбраны" : "Рекомендации");
    }

    /// <summary>Updates the visible queue and marks the item that will play next.</summary>
    public void ShowQueue(IReadOnlyList<TrackFile> tracks, TrackFile? selected, Func<TrackFile, string> getName)
    {
        _rows.Clear();
        _buttons.Clear();
        foreach (var track in tracks)
        {
            var button = MixerStyle.Button(getName(track));
            if (ReferenceEquals(track, selected ?? tracks.FirstOrDefault()))
            {
                button.BackgroundColor = MixerStyle.SelectedBackground;
                button.BorderColor = Accent.WithAlpha(.55f);
                button.BorderWidth = 1;
                SemanticProperties.SetDescription(button, getName(track) + ". Следующий трек. Повторное нажатие запускает сведение.");
            }

            button.Clicked += (_, _) => TrackRequested?.Invoke(track);
            _buttons[track] = button;
            _rows.Add(button);
        }

        if (tracks.Count == 0)
        {
            _rows.Add(MixerStyle.Label("Очередь пуста", 12, MixerStyle.Muted));
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
        var row = file is not null && _buttons.TryGetValue(file, out var button) ? button : null;
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
