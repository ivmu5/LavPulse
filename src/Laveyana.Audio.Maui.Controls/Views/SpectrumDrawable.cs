using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

internal sealed class SpectrumDrawable : IDrawable
{
    private const int EdgePaddingRows = 2;
    private const float PeakHoldSeconds = .28f;
    private const float PeakFallPerSecond = 1.8f;
    private readonly BandPaint[] _paint = new BandPaint[PlaybackFormat.SpectrumBars];
    private readonly float[] _bars = new float[PlaybackFormat.SpectrumBars];
    private readonly float[] _peaks = new float[PlaybackFormat.SpectrumBars];
    private readonly float[] _peakTimers = new float[PlaybackFormat.SpectrumBars];
    private bool _showPeaks;

    public SpectrumDrawable() => SetAppearance(PlayerColors.For(PlayerPalette.LavPulse), SpectrumColorMode.Split, true);

    /// <summary>Caches the band and peak colors when the appearance changes.</summary>
    public void SetAppearance(PlayerColors colors, SpectrumColorMode mode, bool showPeaks)
    {
        _showPeaks = showPeaks;
        if (mode != SpectrumColorMode.FrequencyGradient)
        {
            var right = mode == SpectrumColorMode.Split ? colors.Alternate : colors.Accent;
            Array.Fill(_paint, BandPaint.From(colors.Accent, right));
            return;
        }

        for (int band = 0; band < _paint.Length; band++)
        {
            // Band zero is the lowest frequency and is drawn at the bottom.
            float position = band / (float)(_paint.Length - 1);
            var color = position <= .5f
                ? Interpolate(colors.WaveLow, colors.WaveMid, position * 2)
                : Interpolate(colors.WaveMid, colors.Alternate, position * 2 - 1);
            _paint[band] = BandPaint.From(color, color);
        }
    }

    private static Color Interpolate(Color from, Color to, float progress) => new(
        from.Red + (to.Red - from.Red) * progress,
        from.Green + (to.Green - from.Green) * progress,
        from.Blue + (to.Blue - from.Blue) * progress);

    private readonly record struct BandPaint(Color LeftBar, Color RightBar, Color LeftPeak, Color RightPeak)
    {
        public static BandPaint From(Color left, Color right) =>
            new(left.WithAlpha(.6f), right.WithAlpha(.6f), left.WithAlpha(.9f), right.WithAlpha(.9f));
    }

    /// <summary>Smooths each band and holds its peak briefly before it falls.</summary>
    public bool Advance(ReadOnlySpan<float> levels, float elapsedSeconds)
    {
        float attack = 1 - MathF.Exp(-elapsedSeconds / .008f);
        float release = 1 - MathF.Exp(-elapsedSeconds / .055f);
        bool changed = false;
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

            changed |= level != prior || _peaks[i] != peak;
            _bars[i] = level;
        }

        return changed;
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
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        float spacing = bounds.Height / (_bars.Length + 2 * EdgePaddingRows);
        float thickness = Math.Min(4, spacing * .52f);
        float maximum = bounds.Width * .2f;
        float markerWidth = Math.Min(7, maximum);
        for (int band = 0; band < _bars.Length; band++)
        {
            float y = bounds.Bottom - (EdgePaddingRows + band + .5f) * spacing - thickness / 2;
            float length = maximum * _bars[band];
            var paint = _paint[band];
            if (length > 0)
            {
                DrawMirrored(canvas, bounds, 0, length, y, thickness, paint.LeftBar, paint.RightBar);
            }

            float peak = maximum * _peaks[band];
            if (_showPeaks && peak - length > markerWidth + 2)
            {
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
