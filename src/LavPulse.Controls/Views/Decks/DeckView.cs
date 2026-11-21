using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

/// <summary>Shows a detailed window and an overview of one deck with tap-to-seek on the overview.</summary>
public sealed partial class DeckView : Border
{
    private const double DragThreshold = 6;
    private const double MaxScrubSpeed = 30;
    private const int SpeedDecimalPlaces = 3;
    private static readonly double[] SpeedPresets = Enumerable.Range(15, 11).Select(step => step * 5 / 100d).ToArray();
    private readonly NumericSettingInput _trackSpeed = new("Скорость трека", "×", new(.75, 1.25, 1, .01, SpeedDecimalPlaces), 1, showSlider: false);
    private readonly HorizontalStackLayout _speedControls = new() { Spacing = 4, VerticalOptions = LayoutOptions.Center };
    private bool _choosingSpeed;
    private readonly Label _name = MixerStyle.Label("", MixerStyle.PhoneFont(13, 12), bold: true);
    private readonly Label _time = MixerStyle.Label("", MixerStyle.PhoneFont(11, 10), MixerColor.Muted);
    private readonly Label _previewLabel = MixerStyle.Label("", MixerStyle.PhoneFont(12, 11));
    private readonly WaveformDrawable _detailDrawable = new() { Detailed = true };
    private readonly WaveformDrawable _overviewDrawable = new() { UseProgressBar = true };
    private readonly GraphicsView _detail;
    private readonly GraphicsView _overview;
    private readonly GraphicsView _detailCursor;
    private readonly GraphicsView _overviewCursor;
    private readonly Grid _overviewLayer;
    private long _detailStart = -1;
    private long _overviewPaintedPosition;
    private CachedTrack? _track;
    private long _position;
    private long _displayPosition;
    private DeckPlayhead _playhead;
    private bool _animatePlayhead;
    private bool _snapPlayhead;
    private long _shownSecond = -1;
    private bool _canSeek;
    private Deck _deck;

    public DeckView()
    {
        _detail = new GraphicsView { Drawable = _detailDrawable, HeightRequest = 64 };
        _overview = new GraphicsView { Drawable = _overviewDrawable, HeightRequest = 44 };
        _detailCursor = new GraphicsView { Drawable = _detailDrawable.CreateCursor(), InputTransparent = true };
        _overviewCursor = new GraphicsView { Drawable = _overviewDrawable.CreateCursor(), InputTransparent = true };
        _overviewLayer = new Grid { Children = { _overview, _overviewCursor } };
        SemanticProperties.SetDescription(_detail, "Детальная волна. Удерживай и веди в сторону. Жест вверх отменяет перемотку.");
        SemanticProperties.SetDescription(_overview, "Полоса прогресса. Нажми или перетащи к нужной позиции. Жест вверх отменяет перемотку.");
        _previewLabel.HorizontalOptions = LayoutOptions.Center;
        _previewLabel.VerticalOptions = LayoutOptions.Center;
        _previewLabel.Padding = new Thickness(7, 3);
        MixerStyle.UseColor(_previewLabel, BackgroundColorProperty, MixerColor.Background);
        _previewLabel.InputTransparent = true;
        _previewLabel.IsVisible = false;

        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 8 };
        _name.MaxLines = 1;
        _name.LineBreakMode = LineBreakMode.TailTruncation;
        _name.VerticalTextAlignment = _time.VerticalTextAlignment = TextAlignment.Center;
        _trackSpeed.ValueChanged += speed =>
        {
            if (_canSeek && _track is { } track)
            {
                SpeedRequested?.Invoke(track, speed);
            }
        };
        header.Add(_name);
        header.Add(_time, 1);
        var presets = MixerStyle.Button("▾");
        presets.WidthRequest = 34;
        presets.MinimumWidthRequest = 0;
        presets.Padding = 0;
        SemanticProperties.SetDescription(presets, "Выбрать скорость трека");
        presets.Clicked += async (_, _) => await ChooseSpeedAsync();
        _speedControls.Children.Add(_trackSpeed);
        _speedControls.Children.Add(presets);
        header.Add(_speedControls, 2);
        var detailLayer = new Grid { Children = { _detail, _detailCursor, _previewLabel } };
        MixerStyle.StyleCard(this, new VerticalStackLayout { Spacing = 5, Children = { header, detailLayer, _overviewLayer } });
        Padding = new Thickness(10, 7);

        TimelineInput.Attach(_detail, p => Begin(_detail, p), Move, End, CancelScrub);
        TimelineInput.Attach(_overview, p => Begin(_overview, p), Move, End, CancelScrub);
    }

    public event Action<CachedTrack, long>? SeekRequested;
    public event Action<CachedTrack, double>? SpeedRequested;
    internal CachedTrack? DisplayedTrack => _track;
    private async Task ChooseSpeedAsync()
    {
        if (_choosingSpeed || !_canSeek || _track is not { } track ||
            Window?.Page is not Page page) return;

        _choosingSpeed = true;
        try
        {
            string[] labels = SpeedPresets.Select(speed => $"{speed:0.00}×").ToArray();
            var selected = await page.DisplayActionSheetAsync("Скорость трека", "Отмена", null, labels);
            int index = Array.IndexOf(labels, selected);
            if (index >= 0 && _canSeek && IsVisible && _speedControls.IsVisible && ReferenceEquals(_track, track))
                _trackSpeed.SelectValue(SpeedPresets[index]);
        }
        finally
        {
            _choosingSpeed = false;
        }
    }

    internal void SetPlayheadAnimation(bool enabled)
    {
        _animatePlayhead = enabled;
        _snapPlayhead = true;
        if (!enabled && _displayPosition != _position)
        {
            _displayPosition = _position;
            Redraw();
        }
    }

    internal void SnapOnNextPosition() => _snapPlayhead = true;

    /// <summary>Redraws the timeline from the visual position without updating the deck controls.</summary>
    internal void DrawPlayhead(double time)
    {
        if (!_animatePlayhead || _snapPlayhead || _track is not { } track) return;
        long position = _playhead.Advance(time, track.Waveform.TotalFrames);
        if (_displayPosition == position) return;
        _displayPosition = position;
        Redraw(forceOverview: false);
    }

    /// <summary>Keeps the last visible playhead while the deck leaves the screen.</summary>
    internal void FreezeForExit()
    {
        if (_track is { } track)
        {
            Show(new PlayingTrack(track, _position, _deck, _trackSpeed.Value), false, showOverview: false);
        }
    }

    /// <summary>Updates the playhead and gesture availability with the overview visible.</summary>
    public void Show(PlayingTrack? playing, bool canSeek) => Show(playing, canSeek, showOverview: true);

    /// <summary>Updates the playhead and suppresses the overview and gestures during a mix.</summary>
    public void Show(PlayingTrack? playing, bool canSeek, bool showOverview)
    {
        _canSeek = canSeek;
        _speedControls.IsEnabled = canSeek;
        _detail.InputTransparent = _overview.InputTransparent = !canSeek;
        bool revealOverview = !_overviewLayer.IsVisible && showOverview;
        _overviewLayer.IsVisible = _speedControls.IsVisible = showOverview;
        if (!_canSeek || !ReferenceEquals(_track, playing?.Track))
        {
            CancelScrub();
        }

        IsVisible = playing.HasValue;
        if (playing is not { } deck)
        {
            _track = null;
            _detailDrawable.Waveform = _overviewDrawable.Waveform = null;
            return;
        }

        bool trackChanged = !ReferenceEquals(_track, deck.Track);
        if (trackChanged || Math.Round(deck.Speed, SpeedDecimalPlaces, MidpointRounding.AwayFromZero) != _trackSpeed.Value)
        {
            _trackSpeed.Value = deck.Speed;
        }

        if (trackChanged)
        {
            _track = deck.Track;
            _shownSecond = -1;
            _detailDrawable.Waveform = _overviewDrawable.Waveform = deck.Track.Waveform;
            InvalidateWaveGeometry();
            _name.Text = deck.Track.Title;
            _deck = deck.Deck;
            UpdateTitleColor();
        }

        bool snap = trackChanged || _snapPlayhead || !_animatePlayhead;
        _snapPlayhead = false;
        _position = deck.Position;
        double time = DeckPlayhead.Now;
        _playhead.Observe(_position, deck.Speed, time, snap);
        long display = snap ? _position : _playhead.DisplayPosition;
        if (trackChanged || revealOverview || _displayPosition != display)
        {
            _displayPosition = display;
            Redraw();
        }
    }

    /// <summary>Restyles both timelines while retaining position and seek gestures.</summary>
    internal void SetAppearance(PlayerSettings settings, MixerTheme theme)
    {
        bool progressBar = !settings.WaveformOverview;
        int detailSeconds = Math.Clamp(settings.WaveformDetailSeconds,
            PlayerSettings.MinimumWaveformDetailSeconds, PlayerSettings.MaximumWaveformDetailSeconds);
        bool themeChanged = !ReferenceEquals(_detailDrawable.Theme, theme);
        bool overviewChanged = _overviewDrawable.UseProgressBar != progressBar;
        if (!themeChanged && !overviewChanged && _detailDrawable.DetailWindowSeconds == detailSeconds) return;

        _detailDrawable.DetailWindowSeconds = _overviewDrawable.DetailWindowSeconds = detailSeconds;
        if (themeChanged)
        {
            _detailDrawable.Colors = _overviewDrawable.Colors = theme.Visualization;
            _detailDrawable.Theme = _overviewDrawable.Theme = theme;
            UpdateTitleColor();
        }
        if (overviewChanged)
        {
            _overviewDrawable.UseProgressBar = progressBar;
            SemanticProperties.SetDescription(_overview, progressBar
                ? "Полоса прогресса. Нажми или перетащи к нужной позиции. Жест вверх отменяет перемотку."
                : "Обзорная волна. Нажми или перетащи к нужной позиции. Жест вверх отменяет перемотку.");
        }
        InvalidateWaveGeometry();
        Redraw();
    }

    private void UpdateTitleColor()
    {
        _name.RemoveDynamicResource(Label.TextColorProperty);
        _name.TextColor = _deck == Deck.A ? _detailDrawable.Theme[MixerColor.Accent] : _detailDrawable.Theme.AlternateForeground;
    }

    /// <summary>Draws both the real cursor and the pending seek with distinct markers.</summary>
    private void Redraw(bool forceOverview = true)
    {
        if (_track is null)
        {
            return;
        }
        long? preview = _gesture is not null && _moved ? (long)Math.Round(_preview) : null;
        _detailDrawable.Position = _overviewDrawable.Position = _displayPosition;
        _detailDrawable.Preview = _overviewDrawable.Preview = preview;
        long second = _displayPosition / PlaybackFormat.SampleRate;
        if (second != _shownSecond)
        {
            _shownSecond = second;
            _time.Text = $"{FormatTime(_displayPosition)} / {FormatTime(_track.Waveform.TotalFrames)}";
        }
        _previewLabel.IsVisible = preview.HasValue;
        if (preview is long frame)
        {
            _previewLabel.Text = _gesture == _detail ? $"{FormatTime(frame)}   {_speed:+0;-0;0} с/с" : FormatTime(frame);
        }
        long start = _detailDrawable.WindowStart();
        if (start != _detailStart)
        {
            _detailStart = start;
            _detail.Invalidate();
        }
        _detailCursor.Invalidate();
        if (_overviewLayer.IsVisible && (forceOverview || _overviewDrawable.OverviewCursorMoved(
            _overviewPaintedPosition, _overview.Width)))
        {
            _overviewPaintedPosition = _displayPosition;
            _overviewCursor.Invalidate();
        }
    }

    private void InvalidateWaveGeometry()
    {
        _detailStart = -1;
        _detail.Invalidate();
        _overview.Invalidate();
    }

    private static string FormatTime(long frames)
    {
        long seconds = frames / PlaybackFormat.SampleRate;
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
