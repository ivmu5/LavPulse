using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

internal sealed class SpectrumDrawable : IDrawable
{
    private const float PeakHoldSeconds = .28f;
    private const float PeakFallPerSecond = 1.8f;
    private const int BrightnessIntervals = 100;
    private readonly BandPaint[]?[] _paint;
    private readonly (Color Left, Color Right)[] _bandColors;
    private readonly float[] _bars;
    private readonly float[] _peaks;
    private readonly float[] _peakTimers;
    private bool _showPeaks;
    private float _fullBrightnessLevel;
    private int _topPaddingRows;
    private int _bottomPaddingRows;
    private float _widthFraction;
    private bool _showSpectrum;
    private RampSettings _paintSettings;

    public SpectrumDrawable() : this(PlaybackFormat.SpectrumBars, PlayerColors.For(PlayerPalette.LavPulse), new PlayerSettings())
    {
    }

    internal SpectrumDrawable(int bandCount, PlayerColors colors, PlayerSettings settings, bool light = false)
    {
        _paint = new BandPaint[bandCount][];
        _bandColors = new (Color Left, Color Right)[bandCount];
        _bars = new float[bandCount];
        _peaks = new float[bandCount];
        _peakTimers = new float[bandCount];
        SetAppearance(colors, settings, light);
    }

    /// <summary>Updates the layout and invalidates paint only when the spectrum colors change.</summary>
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
        var (start, middle, end) = mode switch
        {
            SpectrumColorMode.FrequencyGradient => (colors.WaveLow, colors.WaveMid, colors.Alternate),
            SpectrumColorMode.Split => (colors.Accent, colors.Accent, colors.Alternate),
            _ => (colors.Accent, colors.Accent, colors.Accent)
        };
        var paintSettings = new RampSettings(start, middle, end, mode, minimum, light);
        if (_paintSettings == paintSettings) return geometryChanged;

        _paintSettings = paintSettings;
        foreach (var steps in _paint)
        {
            if (steps is not null) Array.Clear(steps);
        }

        if (mode != SpectrumColorMode.FrequencyGradient)
        {
            _bandColors[0] = (start, end);
            Array.Clear(_paint, 1, _paint.Length - 1);
            return true;
        }

        for (int band = 0; band < _paint.Length; band++)
        {
            // Band zero is the lowest frequency and is drawn at the bottom.
            float position = band / (float)Math.Max(1, _paint.Length - 1);
            var color = position <= .5f
                ? Interpolate(start, middle, position * 2)
                : Interpolate(middle, end, position * 2 - 1);
            _bandColors[band] = (color, color);
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
            canvas.FillColor = PaintAtLevel(band, level).LeftBar;
            canvas.FillRoundedRectangle(bounds.Left + band * spacing, bounds.Bottom - height, width, height, Math.Min(width, height) / 2);
        }
        canvas.RestoreState();
    }

    private static Color Interpolate(Color from, Color to, float progress) => new(
        from.Red + (to.Red - from.Red) * progress,
        from.Green + (to.Green - from.Green) * progress,
        from.Blue + (to.Blue - from.Blue) * progress);

    private readonly record struct RampSettings(
        Color Start, Color Middle, Color End, SpectrumColorMode Mode, float Minimum, bool Light);

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

    private BandPaint PaintAtLevel(int band, float level)
    {
        int index = _paintSettings.Mode == SpectrumColorMode.FrequencyGradient ? band : 0;
        float minimum = _paintSettings.Minimum;
        int lastStep = minimum >= 1 ? 0 : BrightnessIntervals;
        var steps = _paint[index];
        if (steps?.Length != lastStep + 1)
        {
            _paint[index] = steps = new BandPaint[lastStep + 1];
        }
        int step = (int)MathF.Round(Math.Clamp(level / _fullBrightnessLevel, 0, 1) * lastStep);
        ref var paint = ref steps[step];
        if (paint.LeftBar is null)
        {
            var (left, right) = _bandColors[index];
            float brightness = minimum + (1 - minimum) * step / BrightnessIntervals;
            paint = BandPaint.AtBrightness(left, right, brightness, _paintSettings.Light);
        }
        return paint;
    }

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
            if (length > 0)
            {
                var paint = PaintAtLevel(band, level);
                DrawMirrored(canvas, bounds, 0, length, y, thickness, paint.LeftBar, paint.RightBar);
            }

            float peak = maximum * _peaks[band];
            if (_showPeaks && peak - length > markerWidth + 2)
            {
                var paint = PaintAtLevel(band, _peaks[band]);
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
