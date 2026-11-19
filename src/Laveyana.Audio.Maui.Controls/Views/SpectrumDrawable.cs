using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

internal sealed class SpectrumDrawable : IDrawable
{
    private const float PeakHoldSeconds = .28f;
    private const float PeakFallPerSecond = 1.8f;
    private const int BrightnessIntervals = 100;
    private readonly BandPaint[][] _paint;
    private readonly float[] _bars;
    private readonly float[] _peaks;
    private readonly float[] _peakTimers;
    private bool _showPeaks;
    private float _fullBrightnessLevel;
    private int _topPaddingRows;
    private int _bottomPaddingRows;
    private float _widthFraction;
    private bool _showSpectrum;
    private (PlayerColors Colors, SpectrumColorMode Mode, float Minimum, bool Light)? _paintSettings;

    public SpectrumDrawable() : this(PlaybackFormat.SpectrumBars, PlayerColors.For(PlayerPalette.LavPulse), new PlayerSettings())
    {
    }

    internal SpectrumDrawable(int bandCount, PlayerColors colors, PlayerSettings settings, bool light = false)
    {
        _paint = new BandPaint[bandCount][];
        _bars = new float[bandCount];
        _peaks = new float[bandCount];
        _peakTimers = new float[bandCount];
        SetAppearance(colors, settings, light);
    }

    /// <summary>Updates the edge margins and caches brightness steps for allocation-free drawing.</summary>
    public bool SetAppearance(PlayerColors colors, PlayerSettings settings, bool light = false)
    {
        float fullBrightness = Math.Clamp(settings.SpectrumFullBrightnessPercent, 1, 100) / 100f;
        int top = Math.Clamp(settings.SpectrumTopPaddingRows, 0, PlayerSettings.MaximumSpectrumPaddingRows);
        int bottom = Math.Clamp(settings.SpectrumBottomPaddingRows, 0, PlayerSettings.MaximumSpectrumPaddingRows);
        float width = Math.Clamp(settings.SpectrumWidthPercent, 0, PlayerSettings.MaximumSpectrumWidthPercent) / 100f;
        bool geometryChanged = _showPeaks != settings.ShowPeaks || _showSpectrum != settings.ShowSpectrum ||
            _fullBrightnessLevel != fullBrightness || _topPaddingRows != top || _bottomPaddingRows != bottom ||
            _widthFraction != width;
        _showPeaks = settings.ShowPeaks;
        _showSpectrum = settings.ShowSpectrum;
        _fullBrightnessLevel = fullBrightness;
        _topPaddingRows = top;
        _bottomPaddingRows = bottom;
        _widthFraction = width;
        float minimum = settings.SpectrumLevelBrightness ? Math.Clamp(settings.SpectrumMinimumBrightnessPercent, 0, 100) / 100f : 1;
        var mode = settings.SpectrumColors;
        var paintSettings = (colors, mode, minimum, light);
        if (_paintSettings == paintSettings) return geometryChanged;

        _paintSettings = paintSettings;
        if (mode != SpectrumColorMode.FrequencyGradient)
        {
            var right = mode == SpectrumColorMode.Split ? colors.Alternate : colors.Accent;
            Array.Fill(_paint, CreatePaint(colors.Accent, right, minimum, light));
            return true;
        }

        for (int band = 0; band < _paint.Length; band++)
        {
            // Band zero is the lowest frequency and is drawn at the bottom.
            float position = band / (float)Math.Max(1, _paint.Length - 1);
            var color = position <= .5f
                ? Interpolate(colors.WaveLow, colors.WaveMid, position * 2)
                : Interpolate(colors.WaveMid, colors.Alternate, position * 2 - 1);
            _paint[band] = CreatePaint(color, color, minimum, light);
        }
        return true;
    }

    /// <summary>Paints the same audible bands as a subdued background in the active queue row.</summary>
    public void DrawQueueBackground(ICanvas canvas, RectF bounds)
    {
        if (!_showSpectrum || _widthFraction <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        float spacing = bounds.Width / _bars.Length;
        float width = spacing * .62f;
        canvas.SaveState();
        canvas.ClipRectangle(bounds);
        canvas.Alpha = .48f;
        for (int band = 0; band < _bars.Length; band++)
        {
            float level = _bars[band];
            if (level <= 0) continue;
            float height = bounds.Height * level;
            canvas.FillColor = PaintAtLevel(_paint[band], level).LeftBar;
            canvas.FillRoundedRectangle(bounds.Left + band * spacing, bounds.Bottom - height, width, height, Math.Min(width, height) / 2);
        }
        canvas.RestoreState();
    }

    private static Color Interpolate(Color from, Color to, float progress) => new(
        from.Red + (to.Red - from.Red) * progress,
        from.Green + (to.Green - from.Green) * progress,
        from.Blue + (to.Blue - from.Blue) * progress);

    private static BandPaint[] CreatePaint(Color left, Color right, float minimum, bool light)
    {
        if (minimum >= 1)
        {
            return [BandPaint.AtBrightness(left, right, 1, light)];
        }

        var steps = new BandPaint[BrightnessIntervals + 1];
        for (int i = 0; i < steps.Length; i++)
        {
            float brightness = minimum + (1 - minimum) * i / BrightnessIntervals;
            steps[i] = BandPaint.AtBrightness(left, right, brightness, light);
        }
        return steps;
    }

    private readonly record struct BandPaint(Color LeftBar, Color RightBar, Color LeftPeak, Color RightPeak)
    {
        public static BandPaint AtBrightness(Color left, Color right, float brightness, bool light)
        {
            // Dimming by darkening only works on a dark canvas. Fade towards a light surface instead.
            float intensity = light ? 1 : brightness;
            float opacity = light ? brightness : 1;
            var leftBar = new Color(left.Red * intensity, left.Green * intensity, left.Blue * intensity, .6f * opacity);
            var leftPeak = leftBar.WithAlpha(.9f * opacity);
            if (ReferenceEquals(left, right))
            {
                return new(leftBar, leftBar, leftPeak, leftPeak);
            }
            var rightBar = new Color(right.Red * intensity, right.Green * intensity, right.Blue * intensity, .6f * opacity);
            return new(leftBar, rightBar, leftPeak, rightBar.WithAlpha(.9f * opacity));
        }
    }

    private BandPaint PaintAtLevel(BandPaint[] steps, float level) =>
        steps[(int)MathF.Round(Math.Clamp(level / _fullBrightnessLevel, 0, 1) * (steps.Length - 1))];

    /// <summary>Smooths each band and holds its peak briefly before it falls.</summary>
    public bool Advance(ReadOnlySpan<float> levels, float elapsedSeconds, out bool barsChanged)
    {
        float attack = 1 - MathF.Exp(-elapsedSeconds / .008f);
        float release = 1 - MathF.Exp(-elapsedSeconds / .055f);
        barsChanged = false;
        bool peaksChanged = false;
        for (int i = 0; i < _bars.Length; i++)
        {
            float prior = _bars[i];
            float target = levels[i];
            float level = prior + (target - prior) * (target >= prior ? attack : release);
            if (MathF.Abs(level - target) < .003f)
            {
                level = target;
            }

            float peak = _peaks[i];
            if (target > 0 && target >= peak)
            {
                _peaks[i] = target;
                _peakTimers[i] = PeakHoldSeconds;
            }
            else
            {
                float hold = _peakTimers[i];
                _peakTimers[i] = Math.Max(0, hold - elapsedSeconds);
                float fallTime = Math.Max(0, elapsedSeconds - hold);
                _peaks[i] = Math.Max(level, peak - PeakFallPerSecond * fallTime);
            }

            barsChanged |= level != prior;
            peaksChanged |= _peaks[i] != peak;
            _bars[i] = level;
        }

        return barsChanged || (_showPeaks && peaksChanged);
    }

    /// <summary>Clears the bars and peak markers when playback stops.</summary>
    public void Reset()
    {
        Array.Clear(_bars);
        Array.Clear(_peaks);
        Array.Clear(_peakTimers);
    }

    /// <summary>Mirrors the same master bands and peak positions across the screen.</summary>
    public void Draw(ICanvas canvas, RectF bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || _widthFraction <= 0)
        {
            return;
        }

        float spacing = bounds.Height / (_bars.Length + _topPaddingRows + _bottomPaddingRows);
        float thickness = Math.Min(4, spacing * .52f);
        float maximum = bounds.Width * _widthFraction;
        float markerWidth = Math.Min(7, maximum);
        for (int band = 0; band < _bars.Length; band++)
        {
            float y = bounds.Bottom - (_bottomPaddingRows + band + .5f) * spacing - thickness / 2;
            float level = _bars[band];
            float length = maximum * level;
            var steps = _paint[band];
            if (length > 0)
            {
                var paint = PaintAtLevel(steps, level);
                DrawMirrored(canvas, bounds, 0, length, y, thickness, paint.LeftBar, paint.RightBar);
            }

            float peak = maximum * _peaks[band];
            if (_showPeaks && peak - length > markerWidth + 2)
            {
                var paint = PaintAtLevel(steps, _peaks[band]);
                DrawMirrored(canvas, bounds, peak - markerWidth, markerWidth, y, thickness, paint.LeftPeak, paint.RightPeak);
            }
        }
    }

    /// <summary>Draws a rounded bar or a peak marker the same distance from both edges.</summary>
    private static void DrawMirrored(ICanvas canvas, RectF bounds, float offset, float length,
        float y, float thickness, Color left, Color right)
    {
        float radius = Math.Min(length, thickness) / 2;
        canvas.FillColor = left;
        canvas.FillRoundedRectangle(bounds.Left + offset, y, length, thickness, radius);
        canvas.FillColor = right;
        canvas.FillRoundedRectangle(bounds.Right - offset - length, y, length, thickness, radius);
    }
}
