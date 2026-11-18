using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Shows a detailed window and an overview of one deck, with commit-on-release scrubbing.</summary>
public sealed class DeckView : Border
{
    private readonly Label _name = MixerStyle.Label("", 13, bold: true);
    private readonly Label _time = MixerStyle.Label("", 11, MixerStyle.Muted);
    private readonly Label _previewLabel = MixerStyle.Label("", 12);
    private readonly WaveformDrawable _detailDrawable = new() { Detailed = true };
    private readonly WaveformDrawable _overviewDrawable = new();
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

        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        _name.MaxLines = 1;
        _name.LineBreakMode = LineBreakMode.TailTruncation;
        header.Add(_name);
        header.Add(_time, 1);
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

    /// <summary>Updates the playhead, track and permission to begin a gesture.</summary>
    public void Show(PlayingTrack? playing, bool canSeek)
    {
        _canSeek = canSeek;
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
        if (trackChanged)
        {
            _track = deck.Track;
            _shownSecond = -1;
            _detailDrawable.Waveform = _overviewDrawable.Waveform = deck.Track.Waveform;
            _name.Text = deck.Track.Title;
            _name.TextColor = deck.Deck == Deck.A ? MixerStyle.Accent : MixerStyle.Alternate;
        }

        if (trackChanged || _position != deck.Position)
        {
            _position = deck.Position;
            Redraw();
        }
    }

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
        if (_gesture is not { } view || _track is not { } track)
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
        _overview.Invalidate();
    }

    private static string FormatTime(long frames)
    {
        long seconds = frames / PlaybackFormat.SampleRate;
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
