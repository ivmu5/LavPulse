using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Shows a detailed window and an overview of one deck, with commit-on-release scrubbing.</summary>
public sealed class DeckView : Border
{
    private readonly NumericSettingInput _trackSpeed = new("Скорость трека", "×", new(.75, 1.25, 1, .125, 3), 1, showSlider: false);
    private readonly Label _name = MixerStyle.Label("", 13, bold: true);
    private readonly Label _time = MixerStyle.Label("", 11, MixerStyle.Muted);
    private readonly Label _previewLabel = MixerStyle.Label("", 12);
    private readonly WaveformDrawable _detailDrawable = new() { Detailed = true };
    private readonly WaveformDrawable _overviewDrawable = new() { UseProgressBar = true };
    private readonly GraphicsView _detail;
    private readonly GraphicsView _overview;
    private readonly IDispatcherTimer _timer;
    private CachedTrack? _track;
    private GraphicsView? _gesture;
    private long _position;
    private long _shownSecond = -1;
    private double _preview;
    private double _speed;
    private double _startX;
    private long _lastTick;
    private bool _moved;
    private bool _canSeek;
    private Deck _deck;

    public DeckView()
    {
        _detail = new GraphicsView { Drawable = _detailDrawable, HeightRequest = 64 };
        _overview = new GraphicsView { Drawable = _overviewDrawable, HeightRequest = 44 };
        SemanticProperties.SetDescription(_detail, "Детальная волна. Удерживай и веди в сторону. Жест вверх отменяет перемотку.");
        SemanticProperties.SetDescription(_overview, "Обзорная волна. Выбери позицию на всём треке. Жест вверх отменяет перемотку.");
        _previewLabel.HorizontalOptions = LayoutOptions.Center;
        _previewLabel.VerticalOptions = LayoutOptions.Center;
        _previewLabel.Padding = new Thickness(7, 3);
        _previewLabel.BackgroundColor = MixerStyle.Background;
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
        header.Add(_trackSpeed, 2);
        var detailLayer = new Grid { Children = { _detail, _previewLabel } };
        MixerStyle.StyleCard(this, new VerticalStackLayout { Spacing = 5, Children = { header, detailLayer, _overview } });
        Padding = new Thickness(10, 7);

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(25);
        _timer.Tick += (_, _) =>
        {
            if (_gesture == _detail)
            {
                AdvancePreview();
                Redraw();
            }
        };
        TimelineInput.Attach(_detail, p => Begin(_detail, p), Move, End, CancelScrub);
        TimelineInput.Attach(_overview, p => Begin(_overview, p), Move, End, CancelScrub);
    }

    public event Action<CachedTrack, long>? SeekRequested;
    public event Action<CachedTrack, double>? SpeedRequested;
    internal CachedTrack? DisplayedTrack => _track;

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
        _trackSpeed.IsEnabled = canSeek;
        _detail.InputTransparent = _overview.InputTransparent = !canSeek;
        bool revealOverview = !_overview.IsVisible && showOverview;
        _overview.IsVisible = _trackSpeed.IsVisible = showOverview;
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
        if (trackChanged || deck.Speed != _trackSpeed.Value)
        {
            _trackSpeed.Value = deck.Speed;
        }

        if (trackChanged)
        {
            _track = deck.Track;
            _shownSecond = -1;
            _detailDrawable.Waveform = _overviewDrawable.Waveform = deck.Track.Waveform;
            _name.Text = deck.Track.Title;
            _deck = deck.Deck;
            UpdateTitleColor();
        }

        if (trackChanged || _position != deck.Position || revealOverview)
        {
            _position = deck.Position;
            Redraw();
        }
    }

    /// <summary>Restyles both timelines while retaining position and seek gestures.</summary>
    internal void SetAppearance(PlayerSettings settings, PlayerColors colors)
    {
        _overviewDrawable.UseProgressBar = !settings.WaveformOverview;
        _detailDrawable.DetailWindowSeconds = _overviewDrawable.DetailWindowSeconds = Math.Clamp(settings.WaveformDetailSeconds,
            PlayerSettings.MinimumWaveformDetailSeconds, PlayerSettings.MaximumWaveformDetailSeconds);
        _detailDrawable.Colors = _overviewDrawable.Colors = colors;
        SemanticProperties.SetDescription(_overview, settings.WaveformOverview
            ? "Обзорная волна. Жест вверх отменяет перемотку."
            : "Полоса прогресса. Жест вверх отменяет перемотку.");
        UpdateTitleColor();
        Redraw();
    }

    private void UpdateTitleColor() => _name.TextColor = _deck == Deck.A
        ? _detailDrawable.Colors.Accent : _detailDrawable.Colors.Alternate;

    /// <summary>Begins a preview with the underlying playback position intact.</summary>
    private bool Begin(GraphicsView view, PointF point)
    {
        if (!_canSeek || _track is null || _gesture is not null)
        {
            return false;
        }

        _gesture = view;
        _preview = _position;
        _speed = 0;
        _moved = false;
        _startX = point.X;
        _lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
        if (view == _overview)
        {
            UpdatePreview(point.X);
        }
        else
        {
            _timer.Start();
        }
        Redraw();
        return true;
    }

    /// <summary>Updates a preview or cancels as soon as the finger leaves above the waveform.</summary>
    private void Move(PointF point)
    {
        if (_gesture is null)
        {
            return;
        }

        if (point.Y < 0)
        {
            CancelScrub();
            return;
        }

        UpdatePreview(point.X);
        Redraw();
    }

    /// <summary>Commits only when the pointer is released inside the gesture's original waveform.</summary>
    private void End(PointF point)
    {
        if (!_canSeek || _gesture is not { } view || _track is not { } track)
        {
            return;
        }

        bool inside = point.X >= 0 && point.X <= view.Width && point.Y >= 0 && point.Y <= view.Height;
        if (inside && (_moved || view == _overview))
        {
            UpdatePreview(point.X);
            SeekRequested?.Invoke(track, (long)Math.Round(_preview));
        }

        CancelScrub();
    }

    /// <summary>Removes the preview without changing the real track cursor.</summary>
    public void CancelScrub()
    {
        if (_gesture is null)
        {
            return;
        }

        _gesture = null;
        _timer.Stop();
        Redraw();
    }

    /// <summary>Uses absolute positioning for the overview and signed velocity for the detailed wave.</summary>
    private void UpdatePreview(float x)
    {
        var fraction = Math.Clamp(x / Math.Max(1, _gesture!.Width), 0, 1);
        if (_gesture == _overview)
        {
            _preview = fraction * _track!.Waveform.TotalFrames;
            return;
        }

        AdvancePreview();
        _moved |= Math.Abs(x - _startX) >= 6;
        double offset = 2 * fraction - 1;
        double strength = Math.Clamp((Math.Abs(offset) - .08) / .92, 0, 1);
        _speed = _moved ? Math.Sign(offset) * 30 * strength : 0;
    }

    /// <summary>Integrates preview velocity using a monotonic clock.</summary>
    private void AdvancePreview()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        _lastTick = now;
        _preview = Math.Clamp(_preview + _speed * PlaybackFormat.SampleRate * seconds, 0, _track!.Waveform.TotalFrames);
    }

    /// <summary>Draws both the real cursor and the pending seek with distinct markers.</summary>
    private void Redraw()
    {
        if (_track is null)
        {
            return;
        }
        long? preview = _gesture is null ? null : (long)Math.Round(_preview);
        _detailDrawable.Position = _overviewDrawable.Position = _position;
        _detailDrawable.Preview = _overviewDrawable.Preview = preview;
        long second = _position / PlaybackFormat.SampleRate;
        if (second != _shownSecond)
        {
            _shownSecond = second;
            _time.Text = $"{FormatTime(_position)} / {FormatTime(_track.Waveform.TotalFrames)}";
        }
        _previewLabel.IsVisible = preview.HasValue;
        if (preview is long frame)
        {
            _previewLabel.Text = _gesture == _detail ? $"{FormatTime(frame)}   {_speed:+0;-0;0} с/с" : FormatTime(frame);
        }
        _detail.Invalidate();
        if (_overview.IsVisible)
        {
            _overview.Invalidate();
        }
    }

    private static string FormatTime(long frames)
    {
        long seconds = frames / PlaybackFormat.SampleRate;
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
