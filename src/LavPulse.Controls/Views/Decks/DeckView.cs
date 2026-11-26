using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

/// <summary>Shows a detailed window and an overview of one deck with tap-to-seek on the overview.</summary>
public sealed partial class DeckView : Border
{
    private const double DragThreshold = 6;
    private const double MaxScrubSpeed = 30;
    private const double OverviewHeight = 44;
    private const double OverviewRevealSeconds = .42;
    private readonly Label _name = MixerStyle.Label("", MixerStyle.PhoneFont(13, 12), bold: true);
    private readonly Label _time = MixerStyle.Label("", MixerStyle.PhoneFont(11, 10), MixerColor.Muted);
    private readonly Label _bpm = MixerStyle.Label("", MixerStyle.PhoneFont(11, 10), MixerColor.Accent);
    // Temporary diagnostic controls; exporting reports lives in Settings.
    private readonly Button _markProblem = new()
    {
        Text = "В отчёт", FontFamily = "Inter", FontSize = 10,
        HeightRequest = 28, MinimumHeightRequest = 28, Padding = new Thickness(5, 0),
        CornerRadius = 6, VerticalOptions = LayoutOptions.Center, IsVisible = false
    };
    private readonly Button _markRhythm = new()
    {
        Text = "Ритм", FontFamily = "Inter", FontSize = 10, WidthRequest = 50,
        HeightRequest = 28, MinimumHeightRequest = 28, Padding = new Thickness(5, 0),
        CornerRadius = 6, VerticalOptions = LayoutOptions.Center, IsVisible = false
    };
    private readonly Label _previewLabel = MixerStyle.Label("", MixerStyle.PhoneFont(12, 11));
    private readonly WaveformDrawable _detailDrawable = new() { Detailed = true };
    private readonly WaveformDrawable _overviewDrawable = new() { UseProgressBar = true };
    private readonly GraphicsView _detail;
    private readonly GraphicsView _overview;
    private readonly GraphicsView _detailCursor;
    private readonly GraphicsView _overviewCursor;
    private readonly Grid _overviewLayer;
    private double _overviewProgress = 1;
    private double _overviewFrom = 1;
    private double _overviewTarget = 1;
    private double? _overviewStart;
    private bool _overviewAnimating;
    private long _detailStart = -1;
    private long _overviewPaintedPosition;
    private CachedTrack? _track;
    private readonly List<long> _manualTaps = [];
    private double _lastBeatPressTime = double.NegativeInfinity;
    private bool _canTapBeats;
    private long _position;
    private double _shownBpm = double.NaN;
    private TrackMusicalKey? _shownKey;
    private bool _shownUnknownKey;
    private long _displayPosition;
    private DeckPlayhead _playhead;
    private bool _animatePlayhead;
    private bool _snapPlayhead;
    private long _shownSecond = -1;
    private bool _canSeek;
    private Deck _deck;

    public DeckView()
    {
        _detail = new PlaybackGraphicsView { Drawable = _detailDrawable, HeightRequest = 64 };
        _overview = new GraphicsView { Drawable = _overviewDrawable, HeightRequest = OverviewHeight };
        _detailCursor = new PlaybackGraphicsView { Drawable = _detailDrawable.CreateCursor(), InputTransparent = true };
        _overviewCursor = new PlaybackGraphicsView { Drawable = _overviewDrawable.CreateCursor(), InputTransparent = true };
        _overviewLayer = new Grid { IsClippedToBounds = true, Children = { _overview, _overviewCursor } };
        SemanticProperties.SetDescription(_detail, "Детальная волна. Горизонтальный жест — перемотка, жест вверх — отмена.");
        SemanticProperties.SetDescription(_overview, "Полоса прогресса. Касание или перетаскивание — выбор позиции, жест вверх — отмена.");
        _previewLabel.HorizontalOptions = LayoutOptions.Center;
        _previewLabel.VerticalOptions = LayoutOptions.Center;
        _previewLabel.Padding = new Thickness(7, 3);
        MixerStyle.UseColor(_previewLabel, BackgroundColorProperty, MixerColor.Background);
        _previewLabel.InputTransparent = true;
        _previewLabel.IsVisible = false;

        foreach (var button in new[] { _markProblem, _markRhythm })
        {
            MixerStyle.UseColor(button, VisualElement.BackgroundColorProperty, MixerColor.Button);
            MixerStyle.UseColor(button, Button.TextColorProperty, MixerColor.Text);
        }
        SemanticProperties.SetDescription(_markProblem, "Добавить комментарий к участку трека");
        SemanticProperties.SetDescription(_markRhythm,
            "Ручная отметка басового удара в отчёте");
        _markProblem.Clicked += (_, _) =>
        {
            if (_track is { } track) ReportMarkRequested?.Invoke(track, _displayPosition, TrackMarkMode.Problem);
        };
        // Pressed is timestamped at contact/down, not on release after a human-held click.
        _markRhythm.Pressed += (_, _) => TapBeatNow();
        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 5
        };
        _name.MaxLines = 1;
        _name.LineBreakMode = LineBreakMode.TailTruncation;
        _name.VerticalTextAlignment = _time.VerticalTextAlignment = _bpm.VerticalTextAlignment = TextAlignment.Center;
        _bpm.IsVisible = false;
        header.Add(_name);
        header.Add(_bpm, 1);
        header.Add(_time, 2);
        header.Add(_markProblem, 3);
        header.Add(_markRhythm, 4);
        var detailLayer = new Grid { Children = { _detail, _detailCursor, _previewLabel } };
        MixerStyle.StyleCard(this, new VerticalStackLayout { Spacing = 5, Children = { header, detailLayer, _overviewLayer } });
        Padding = new Thickness(10, 7);

        TimelineInput.Attach(_detail, p => Begin(_detail, p), Move, End, CancelScrub);
        TimelineInput.Attach(_overview, p => Begin(_overview, p), Move, End, CancelScrub);
    }

    public event Action<CachedTrack, long>? SeekRequested;
    public event Action<CachedTrack, long, TrackMarkMode>? ReportMarkRequested;
    public event Action<CachedTrack, long>? BassBeatTapped;

    /// <summary>Manual timing is allowed only while the output is actually playing.</summary>
    public void SetBassTapEnabled(bool enabled)
    {
        _canTapBeats = enabled;
        bool active = enabled && _track is not null;
        if (_markRhythm.IsEnabled != active) _markRhythm.IsEnabled = active;
    }

    private void TapBeatNow()
    {
        if (!_canTapBeats || _track is not { } track) return;
        double now = DeckPlayhead.Now;
        // Debounce accidental double contacts/key repeat, not musical subdivisions.
        if (now - _lastBeatPressTime < .075) return;
        _lastBeatPressTime = now;
        long frame = _animatePlayhead
            ? _playhead.Advance(now, track.Waveform.TotalFrames)
            : _displayPosition;
        frame = Math.Clamp(frame, 0, track.Waveform.TotalFrames);
        AddVisibleTap(frame);
        BassBeatTapped?.Invoke(track, frame);
    }

    /// <summary>Restore already saved taps without losing presses made during the async read.</summary>
    public void RestoreBassTaps(CachedTrack track, IReadOnlyList<long> frames)
    {
        if (!ReferenceEquals(track, _track)) return;
        var existing = new HashSet<long>(_manualTaps);
        foreach (long frame in frames)
        {
            if (existing.Add(frame)) _manualTaps.Add(frame);
        }
        UpdateVisibleTapMarks();
    }

    private void AddVisibleTap(long frame)
    {
        _manualTaps.Add(frame);
        UpdateVisibleTapMarks();
    }

    /// <summary>Undo an optimistic UI mark if persisting that press failed.</summary>
    public void DiscardFailedBassTap(CachedTrack track, long frame)
    {
        if (!ReferenceEquals(track, _track)) return;
        if (_manualTaps.Remove(frame)) UpdateVisibleTapMarks();
    }

    public void ClearBassTaps()
    {
        if (_manualTaps.Count == 0) return;
        _manualTaps.Clear();
        UpdateVisibleTapMarks();
    }

    private void UpdateVisibleTapMarks()
    {
        _manualTaps.Sort();
        long[] marks = [.. _manualTaps];
        _detailDrawable.ManualBeatFrames = _overviewDrawable.ManualBeatFrames = marks;
        _detail.Invalidate();
        _overview.Invalidate();
    }
    internal CachedTrack? DisplayedTrack => _track;
    internal bool IsOverviewAnimating => _overviewAnimating;
    internal bool IsOverviewCollapsed => !_overviewAnimating && _overviewProgress <= .00001;

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
            Show(new PlayingTrack(track, _position, _deck, _playhead.Speed), false, showOverview: false);
        }
    }

    /// <summary>Updates the playhead and gesture availability with the overview visible.</summary>
    public void Show(PlayingTrack? playing, bool canSeek) => Show(playing, canSeek, showOverview: true);

    /// <summary>Updates the playhead and suppresses the overview and gestures during a mix.</summary>
    public void Show(PlayingTrack? playing, bool canSeek, bool showOverview, bool animateOverview = false)
    {
        _canSeek = canSeek;
        _detail.InputTransparent = _overview.InputTransparent = !canSeek;
        bool revealOverview = _overviewProgress < 1 && showOverview;
        if (!_canSeek || !ReferenceEquals(_track, playing?.Track))
        {
            CancelScrub();
        }

        IsVisible = playing.HasValue;
        _markProblem.IsVisible = _markRhythm.IsVisible = playing.HasValue;
        bool active = playing.HasValue && _canTapBeats;
        if (_markRhythm.IsEnabled != active) _markRhythm.IsEnabled = active;
        if (playing is not { } deck)
        {
            SetOverview(false, animate: false);
            _track = null;
            _manualTaps.Clear();
            _detailDrawable.ManualBeatFrames = _overviewDrawable.ManualBeatFrames = [];
            _detailDrawable.Waveform = _overviewDrawable.Waveform = null;
            return;
        }

        bool trackChanged = !ReferenceEquals(_track, deck.Track);
        if (trackChanged)
        {
            _track = deck.Track;
            _manualTaps.Clear();
            _lastBeatPressTime = double.NegativeInfinity;
            _detailDrawable.ManualBeatFrames = _overviewDrawable.ManualBeatFrames = [];
            _shownSecond = -1;
            _detailDrawable.Waveform = _overviewDrawable.Waveform = deck.Track.Waveform;
            InvalidateWaveGeometry();
            _name.Text = deck.Track.Title;
            _deck = deck.Deck;
            UpdateTitleColor();
        }

        // Resize the overview smoothly after a handoff without creating an independent timer.
        SetOverview(showOverview, animateOverview && !trackChanged);

        bool snap = trackChanged || _snapPlayhead || !_animatePlayhead;
        _snapPlayhead = false;
        _position = deck.Position;
        double localBpm = deck.Track.Waveform.Rhythm is { HasBeatGrid: true } rhythm
            ? rhythm.BpmAt(_position) : 0;
        localBpm = Math.Round(localBpm, 1);
        var localKey = deck.Track.Waveform.MusicalKey;
        bool localUnknown = false;
        if (deck.Track.Waveform.MusicProfile is { } profile && profile.TryGetLocalKey(_position, out var estimated))
        {
            localKey = estimated;
            localUnknown = estimated is null;
        }
        if (localBpm != _shownBpm || trackChanged || localKey != _shownKey || localUnknown != _shownUnknownKey)
        {
            _shownBpm = localBpm;
            _shownKey = localKey;
            _shownUnknownKey = localUnknown;
            string tempo = localBpm > 0 ? $"≈{localBpm:0.#} BPM" : "";
            if (localKey is { IsValid: true } key)
                tempo += (tempo.Length == 0 ? "" : " · ") + $"{key.Name} / {key.Camelot}";
            if (localUnknown) tempo += (tempo.Length == 0 ? "" : " · ") + "Key ?";
            _bpm.Text = tempo;
            _bpm.IsVisible = tempo.Length != 0;
        }
        double time = DeckPlayhead.Now;
        _playhead.Observe(_position, deck.Speed, time, snap);
        long display = snap ? _position : _playhead.DisplayPosition;
        // The position timer supplies observations only while the display clock is
        // animating. Drawing both here and in DrawPlayhead violates the FPS limit.
        // Track changes, explicit seeks and paused positions still update immediately.
        if (trackChanged || snap || (!_animatePlayhead && (revealOverview || _displayPosition != display)))
        {
            _displayPosition = display;
            Redraw();
        }
    }

    /// <summary>Advances a height-and-opacity reveal on the shared, FPS-limited display clock.</summary>
    internal void AdvanceOverview(double time)
    {
        if (!_overviewAnimating) return;
        _overviewStart ??= time;
        double progress = Math.Clamp((time - _overviewStart.Value) / OverviewRevealSeconds, 0, 1);
        SetOverviewProgress(_overviewFrom + (_overviewTarget - _overviewFrom) * PlayerMotion.Ease(progress));
        if (progress >= 1) _overviewAnimating = false;
    }

    internal void CancelOverviewAnimation()
    {
        _overviewAnimating = false;
        _overviewStart = null;
        SetOverviewProgress(_overviewTarget);
    }

    private void SetOverview(bool visible, bool animate)
    {
        double target = visible ? 1 : 0;
        if (!animate)
        {
            _overviewTarget = target;
            CancelOverviewAnimation();
            return;
        }
        if (target == _overviewTarget && (_overviewAnimating || _overviewProgress == target)) return;
        _overviewFrom = _overviewProgress;
        _overviewTarget = target;
        _overviewStart = null;
        _overviewAnimating = _overviewFrom != target;
        if (target > 0)
        {
            // A newly revealed overview needs its waveform geometry even at zero initial height.
            _overviewLayer.IsVisible = true;
            _overview.Invalidate();
        }
    }

    private void SetOverviewProgress(double progress)
    {
        if (Math.Abs(_overviewProgress - progress) < .00001) return;
        _overviewProgress = progress;
        _overviewLayer.HeightRequest = OverviewHeight * progress;
        _overviewLayer.Opacity = progress;
        _overviewLayer.IsVisible = progress > 0;
    }

    /// <summary>Restyles both timelines while retaining position and seek gestures.</summary>
    internal void SetAppearance(PlayerSettings settings, MixerTheme theme)
    {
        bool progressBar = !settings.WaveformOverview;
        int detailSeconds = Math.Clamp(settings.WaveformDetailSeconds,
            PlayerSettings.MinimumWaveformDetailSeconds, PlayerSettings.MaximumWaveformDetailSeconds);
        bool themeChanged = !ReferenceEquals(_detailDrawable.Theme, theme);
        bool overviewChanged = _overviewDrawable.UseProgressBar != progressBar;
        var overviewMode = settings.WaveformOverviewBeatGrid ? DeckWaveformMode.BeatGrid : DeckWaveformMode.Color;
        bool modeChanged = _detailDrawable.Mode != settings.DetailedWaveform || _overviewDrawable.Mode != overviewMode;
        if (!themeChanged && !overviewChanged && !modeChanged && _detailDrawable.DetailWindowSeconds == detailSeconds) return;

        _detailDrawable.DetailWindowSeconds = _overviewDrawable.DetailWindowSeconds = detailSeconds;
        _detailDrawable.Mode = settings.DetailedWaveform;
        _overviewDrawable.Mode = overviewMode;
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
                ? "Полоса прогресса. Касание или перетаскивание — выбор позиции, жест вверх — отмена."
                : "Обзорная волна. Касание или перетаскивание — выбор позиции, жест вверх — отмена.");
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
