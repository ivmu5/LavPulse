using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

internal sealed class WaveformDrawable : IDrawable
{
    private PlayerColors _colors = MixerTheme.Default.Visualization;
    private Color _overviewHighlight = MixerTheme.Default.Visualization.Accent.WithAlpha(.16f);

    public PlayerColors Colors
    {
        get => _colors;
        set
        {
            _colors = value;
            _overviewHighlight = value.Accent.WithAlpha(.16f);
        }
    }
    internal MixerTheme Theme { get; set; } = MixerTheme.Default;
    private DeckWaveformMode _mode;
    public DeckWaveformMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            ReleaseBands();
        }
    }
    public bool UseProgressBar { get; set; }

    public TrackWaveform? Waveform
    {
        get => _waveform;
        set
        {
            if (ReferenceEquals(_waveform, value)) return;
            _waveform = value;
            _dynamics = value is { Points.Count: > 0 } ? new WaveformDynamics(value.Points) : null;
            ReleaseBands();
        }
    }
    private TrackWaveform? _waveform;
    private WaveformDynamics? _dynamics;
    public bool Detailed { get; init; }
    public int DetailWindowSeconds { get; set; } = PlayerSettings.DefaultWaveformDetailSeconds;
    public long Position { get; set; }
    public long? Preview { get; set; }
    /// <summary>Manual human bass taps, not machine-generated beat-grid anchors.</summary>
    public long[] ManualBeatFrames { get; set; } = [];

    private RectF _cachedBounds;
    private long _cachedStart;
    private long _cachedLength;
    private PathF[]? _bands;
    private readonly PathF _cursorTip = CreateCursorTip();

    public IDrawable CreateCursor() => new Playhead(this);

    /// <summary>Centers the playhead in the cached detail wave, using a frame-accurate offset.</summary>
    public long WindowStart() => Detailed && Waveform is { TotalFrames: > 0 } waveform
        ? Math.Clamp((Preview ?? Position) - DetailLength(waveform) / 2, 0, waveform.TotalFrames - DetailLength(waveform))
        : 0;

    private long DetailLength(TrackWaveform waveform) =>
        Math.Min(waveform.TotalFrames, (long)DetailWindowSeconds * PlaybackFormat.SampleRate);

    private float HorizontalInset => UseProgressBar ? 6 : 4;

    internal double OverviewPosition(double x, double width)
    {
        if (Waveform is not { TotalFrames: > 0 } waveform) return 0;
        double fraction = Math.Clamp((x - HorizontalInset) / Math.Max(1, width - 2 * HorizontalInset), 0, 1);
        return fraction * waveform.TotalFrames;
    }

    internal bool OverviewCursorMoved(long paintedPosition, double width) =>
        Waveform is { TotalFrames: > 0 } waveform &&
        Math.Abs(Position - paintedPosition) * Math.Max(0, width - 2 * HorizontalInset)
            >= .5 * waveform.TotalFrames;

    private static int Columns(float width) => Math.Max(1, (int)(width / 1.65f));

    /// <summary>Draws cached three-band geometry, independently of the playhead overlay.</summary>
    public void Draw(ICanvas canvas, RectF bounds)
    {
        if (UseProgressBar)
        {
            if (bounds.Width > 12 && bounds.Height >= 12)
            {
                canvas.FillColor = Theme[MixerColor.Border];
                canvas.FillRoundedRectangle(bounds.Left + HorizontalInset, bounds.Center.Y - 3, bounds.Width - 2 * HorizontalInset, 6, 3);
                if (Waveform is { TotalFrames: > 0 } overview)
                    DrawManualBeatMarks(canvas, bounds, 0, overview.TotalFrames,
                        bounds.X + HorizontalInset, bounds.Width - 2 * HorizontalInset);
            }
            return;
        }

        canvas.FillColor = Theme[MixerColor.Surface];
        canvas.FillRoundedRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, 6);
        if (bounds.Width < 8 || bounds.Height < 8) return;

        float x = bounds.X + HorizontalInset;
        float width = bounds.Width - 2 * HorizontalInset;
        float middle = bounds.Center.Y;
        canvas.StrokeColor = Theme[MixerColor.Border];
        canvas.StrokeSize = 1;
        canvas.DrawLine(x, middle, x + width, middle);

        if (Waveform is not { TotalFrames: > 0 } waveform || waveform.Points.Count == 0) return;

        long start = WindowStart();
        long length = Detailed ? DetailLength(waveform) : waveform.TotalFrames;
        int columns = Columns(width);
        float spacing = width / columns;
        long meshStart = start / length * length;
        if (_cachedBounds != bounds || _cachedStart != meshStart || _cachedLength != length || _bands is null)
        {
            ReleaseBands();
            // Two adjacent detail windows cover the viewport until the playhead crosses the next chunk.
            int meshWindows = Detailed ? 2 : 1;
            _bands = BuildBands(waveform, _dynamics!, meshStart, length * meshWindows, columns * meshWindows,
                x, spacing, middle, bounds.Height / 2 - 4);
            _cachedBounds = bounds;
            _cachedStart = meshStart;
            _cachedLength = length;
        }
        canvas.SaveState();
        canvas.ClipRectangle(x, bounds.Top, width, bounds.Height);
        canvas.Translate((float)((meshStart - start) * (double)width / length), 0);
        canvas.StrokeSize = Math.Clamp(spacing * 0.78f, 1, 2);
        canvas.StrokeColor = Colors.WaveLow;
        canvas.DrawPath(_bands[0]);
        canvas.StrokeColor = Colors.WaveMid;
        canvas.DrawPath(_bands[1]);
        canvas.StrokeColor = Colors.Alternate;
        canvas.DrawPath(_bands[2]);
        canvas.RestoreState();

        // Overlay the grid and accents after the colored waveform: previously the bars
        // obscured the faint grid and made valid beat positions nearly invisible.
        if (Mode != DeckWaveformMode.Color && waveform.Rhythm is { HasBeatGrid: true } rhythm)
            DrawBeatGrid(canvas, bounds, rhythm, start, length, x, width);
        if (Mode == DeckWaveformMode.Transients && Detailed && waveform.Rhythm is { } transients)
            DrawOnsetMarks(canvas, bounds, transients.TransientFrames, start, length, x, width);
        DrawManualBeatMarks(canvas, bounds, start, length, x, width);
    }

    private void DrawManualBeatMarks(ICanvas canvas, RectF bounds, long start, long length,
        float x, float width)
    {
        var beats = ManualBeatFrames;
        if (beats.Length == 0 || length <= 0 || width <= 0) return;
        int index = Array.BinarySearch(beats, start);
        if (index < 0) index = ~index;
        float lastX = float.NegativeInfinity;
        canvas.SaveState();
        canvas.ClipRectangle(x, bounds.Top, width, bounds.Height);
        canvas.StrokeColor = Colors.Orange.WithAlpha(.95f);
        canvas.StrokeSize = 2;
        for (int drawn = 0; index < beats.Length && beats[index] <= start + length &&
             drawn < 1000; index++)
        {
            float marker = x + (float)((beats[index] - start) * (double)width / length);
            if (marker - lastX < (Detailed ? 2 : 3)) continue;
            // Distinct short orange ticks are user presses, never an inferred full grid.
            canvas.DrawLine(marker, bounds.Top + 1, marker,
                bounds.Top + (Detailed ? 12 : 10));
            lastX = marker;
            drawn++;
        }
        canvas.RestoreState();
    }

    /// <summary>Draws real rhythmic ticks, downsampled for a full-track overview to avoid visual noise.</summary>
    private void DrawBeatGrid(ICanvas canvas, RectF bounds, TrackBeatGrid grid, long start,
        long length, float x, float width)
    {
        long[] beats = grid.BeatFrames;
        if (beats.Length < 4) return;
        int first = Array.BinarySearch(beats, start);
        if (first < 0) first = ~first;
        if (first >= beats.Length) return;
        int stride = 1;
        if (!Detailed)
        {
            double averagePeriod = (beats[^1] - beats[0]) / (double)(beats.Length - 1);
            while (stride < 1024 && averagePeriod * stride * width / length < 11) stride *= 2;
            first = (first + stride - 1) / stride * stride;
        }
        canvas.SaveState();
        canvas.ClipRectangle(x, bounds.Top, width, bounds.Height);
        canvas.StrokeColor = Colors.Accent.WithAlpha(Detailed ? .72f : .58f);
        canvas.StrokeSize = Detailed ? 1.2f : 1.5f;
        for (int index = first, drawn = 0;
             index < beats.Length && beats[index] <= start + length && drawn < 300;
             index += stride, drawn++)
        {
            // BeatFrames includes local tempo corrections; never reconstruct a constant-period grid.
            float px = x + (float)((beats[index] - start) * (double)width / length);
            canvas.DrawLine(px, bounds.Top + 3, px, bounds.Bottom - 3);
        }
        canvas.RestoreState();
    }

    private void DrawOnsetMarks(ICanvas canvas, RectF bounds, long[] onsets, long start,
        long length, float x, float width)
    {
        if (onsets.Length == 0) return;
        int first = Array.BinarySearch(onsets, start);
        if (first < 0) first = ~first;
        canvas.SaveState();
        canvas.ClipRectangle(x, bounds.Top, width, bounds.Height);
        canvas.StrokeColor = Colors.Alternate.WithAlpha(.94f);
        canvas.StrokeSize = 1.5f;
        for (int i = first; i < onsets.Length && onsets[i] <= start + length; i++)
        {
            float px = x + (float)((onsets[i] - start) * width / length);
            canvas.DrawLine(px, bounds.Top + 1, px, bounds.Top + 7);
        }
        canvas.RestoreState();
    }

    private void ReleaseBands()
    {
        if (_bands is null) return;
        foreach (var band in _bands) band.Dispose();
        _bands = null;
    }

    private static PathF[] BuildBands(TrackWaveform waveform, WaveformDynamics dynamics, long start, long length,
        int columns, float x, float spacing, float middle, float scale)
    {
        PathF[] bands = [new(), new(), new()];
        for (int column = 0; column < columns; column++)
        {
            long fromFrame = start + length * column / columns;
            if (fromFrame >= waveform.TotalFrames) break;
            long toFrame = Math.Min(waveform.TotalFrames, start + length * (column + 1) / columns);
            int from = (int)(fromFrame / waveform.FramesPerPoint);
            int to = (int)(Math.Max(fromFrame, toFrame - 1) / waveform.FramesPerPoint);
            WaveformPoint point = waveform.Points[from];
            float bassAttack = dynamics.BassAttack(from);
            for (int index = from + 1; index <= to; index++)
            {
                point = point.Combine(waveform.Points[index]);
                bassAttack = Math.Max(bassAttack, dynamics.BassAttack(index));
            }

            // Preserve the differences between quiet material and real transients. The former
            // sqrt(Peak) curve raised weak intervals so much that drum hits looked almost flat.
            float sum = point.Low * 1.35f + point.Mid + point.High * .8f;
            if (point.Peak < 0.001f || sum <= 0) continue;

            float height = dynamics.Height(point.Peak, bassAttack) * scale;
            float low = height * point.Low * 1.35f / sum;
            float mid = height * point.Mid / sum;
            float bar = x + spacing * (column + 0.5f);
            AddBand(bands[0], bar, middle, 0, low);
            AddBand(bands[1], bar, middle, low, low + mid);
            AddBand(bands[2], bar, middle, low + mid, height);
        }
        return bands;
    }

    private static void AddBand(PathF path, float x, float middle, float start, float end)
    {
        if (end <= start) return;
        path.MoveTo(x, middle - end);
        path.LineTo(x, middle - start);
        path.MoveTo(x, middle + start);
        path.LineTo(x, middle + end);
    }

    private void DrawCursor(ICanvas canvas, RectF bounds)
    {
        if (Waveform is not { TotalFrames: > 0 } waveform || bounds.Width <= 12 || bounds.Height < 12) return;

        if (UseProgressBar)
        {
            DrawProgress(canvas, bounds, waveform.TotalFrames);
            return;
        }

        if (waveform.Points.Count == 0) return;
        float x = bounds.Left + HorizontalInset;
        float width = bounds.Width - 2 * HorizontalInset;
        long detailLength = DetailLength(waveform);
        long start = WindowStart();
        long length = Detailed ? detailLength : waveform.TotalFrames;
        if (!Detailed)
        {
            long detailStart = Math.Clamp((Preview ?? Position) - detailLength / 2, 0, waveform.TotalFrames - detailLength);
            canvas.FillColor = _overviewHighlight;
            canvas.FillRectangle(x + width * detailStart / waveform.TotalFrames, bounds.Y + 4,
                width * detailLength / waveform.TotalFrames, bounds.Height - 8);
        }

        if (Preview is long preview)
        {
            float marker = x + width * (preview - start) / length;
            canvas.FillColor = Theme[MixerColor.Preview];
            for (float y = bounds.Y + 3; y < bounds.Bottom - 5; y += 7)
            {
                canvas.FillRectangle(marker - 1, y, 2, 4);
            }
        }

        if (Position >= start && Position <= start + length)
        {
            float marker = x + width * (Position - start) / length;
            canvas.StrokeColor = Theme[MixerColor.Text];
            canvas.StrokeSize = 2;
            canvas.DrawLine(marker, bounds.Y + 3, marker, bounds.Bottom - 3);
            canvas.FillColor = Theme[MixerColor.Text];
            canvas.SaveState();
            canvas.Translate(marker, bounds.Y + 3);
            canvas.FillPath(_cursorTip);
            canvas.RestoreState();
        }
    }

    private static PathF CreateCursorTip()
    {
        var triangle = new PathF();
        triangle.MoveTo(-4, 0);
        triangle.LineTo(4, 0);
        triangle.LineTo(0, 7);
        triangle.Close();
        return triangle;
    }

    private void DrawProgress(ICanvas canvas, RectF bounds, long frames)
    {
        float start = bounds.Left + HorizontalInset;
        float width = bounds.Width - 2 * HorizontalInset;
        float played = width * Math.Clamp((float)Position / frames, 0, 1);
        if (played > 0)
        {
            canvas.FillColor = Colors.Accent;
            canvas.FillRoundedRectangle(start, bounds.Center.Y - 3, played, 6, Math.Min(3, played / 2));
        }

        canvas.FillColor = Theme[MixerColor.Text];
        canvas.FillCircle(start + played, bounds.Center.Y, 4);
        if (Preview is long frame)
        {
            canvas.FillColor = Theme[MixerColor.Preview];
            canvas.FillCircle(start + width * Math.Clamp((float)frame / frames, 0, 1), bounds.Center.Y, 5);
        }
    }

    private sealed class Playhead(WaveformDrawable waveform) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF bounds) => waveform.DrawCursor(canvas, bounds);
    }
}
