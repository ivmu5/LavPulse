using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class DeckView
{
    private bool _scrubDirty;
    private GraphicsView? _gesture;
    private double _preview;
    private double _speed;
    private double _startX;
    private long _lastTick;
    private bool _moved;

    internal bool IsScrubbing => _gesture is not null && _moved;
    internal event Action? ScrubAnimationChanged;

    internal void DrawScrubFrame()
    {
        if (!IsScrubbing || (!_scrubDirty && (_gesture != _detail || _speed == 0))) return;
        _scrubDirty = false;
        if (_gesture == _detail) AdvancePreview();
        Redraw();
    }

    /// <summary>Begins a preview with the underlying playback position intact.</summary>
    private bool Begin(GraphicsView view, PointF point)
    {
        if (!_canSeek || _track is null || _gesture is not null)
        {
            return false;
        }

        _gesture = view;
        _speed = 0;
        _moved = false;
        _startX = point.X;
        return true;
    }

    /// <summary>Updates a preview or cancels as soon as the finger leaves above the waveform.</summary>
    private void Move(PointF point)
    {
        if (_gesture is not { } view)
        {
            return;
        }

        if (point.Y < 0)
        {
            CancelScrub();
            return;
        }

        bool started = !_moved;
        if (started)
        {
            if (Math.Abs(point.X - _startX) < DragThreshold) return;

            _moved = true;
            _preview = _displayPosition;
            _lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        UpdatePreview(view, point.X);
        _scrubDirty = true;
        if (started) ScrubAnimationChanged?.Invoke();
    }

    /// <summary>Commits only when the pointer is released inside the gesture's original waveform.</summary>
    private void End(PointF point)
    {
        if (!_canSeek || _gesture is not { } view || _track is not { } track)
        {
            return;
        }

        bool inside = point.X >= 0 && point.X <= view.Width && point.Y >= 0 && point.Y <= view.Height;
        if (inside && (view == _overview || _moved))
        {
            UpdatePreview(view, point.X);
            long target = (long)Math.Round(_preview);
            CancelScrub();
            SeekRequested?.Invoke(track, target);
            return;
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
        _scrubDirty = false;
        ScrubAnimationChanged?.Invoke();
        Redraw();
    }

    /// <summary>Uses absolute positioning for the overview and signed velocity for the detailed wave.</summary>
    private void UpdatePreview(GraphicsView view, float x)
    {
        if (view == _overview)
        {
            _preview = _overviewDrawable.OverviewPosition(x, view.Width);
            return;
        }

        AdvancePreview();
        _speed = ScrubSpeed(_startX, x, view.Width);
    }

    /// <summary>Uses the press point as neutral and reaches full speed at the edge in either direction.</summary>
    internal static double ScrubSpeed(double startX, double x, double width)
    {
        double offset = x - startX;
        double available = offset < 0 ? startX : width - startX;
        double deadZone = Math.Min(3, available / 2);
        double strength = Math.Clamp((Math.Abs(offset) - deadZone) / Math.Max(1, available - deadZone), 0, 1);
        return Math.Sign(offset) * MaxScrubSpeed * strength;
    }

    /// <summary>Integrates preview velocity using a monotonic clock.</summary>
    private void AdvancePreview()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds;
        _lastTick = now;
        _preview = Math.Clamp(_preview + _speed * PlaybackFormat.SampleRate * seconds, 0, _track!.Waveform.TotalFrames);
    }
}
