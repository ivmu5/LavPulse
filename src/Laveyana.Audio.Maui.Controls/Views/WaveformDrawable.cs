using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

internal sealed class WaveformDrawable : IDrawable
{
    private static readonly Color WindowColor = MixerStyle.Accent.WithAlpha(.16f);

    public TrackWaveform? Waveform { get; set; }
    public bool Detailed { get; init; }
    public long Position { get; set; }
    public long? Preview { get; set; }

    /// <summary>Scales waveform points to the view and draws their three frequency bands.</summary>
    public void Draw(ICanvas canvas, RectF bounds)
    {
        canvas.FillColor = MixerStyle.Surface;
        canvas.FillRoundedRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, 6);

        if (bounds.Width < 8 || bounds.Height < 8)
        {
            return;
        }

        float x = bounds.X + 4;
        float width = bounds.Width - 8;
        float middle = bounds.Y + bounds.Height / 2;
        canvas.StrokeColor = MixerStyle.BorderColor;
        canvas.StrokeSize = 1;
        canvas.DrawLine(x, middle, x + width, middle);

        if (Waveform is not { } waveform)
        {
            return;
        }

        if (waveform.TotalFrames == 0 || waveform.Points.Count == 0)
        {
            return;
        }

        long length = Detailed ? Math.Min(waveform.TotalFrames, 15L * PlaybackFormat.SampleRate) : waveform.TotalFrames;
        long windowStart = Detailed ? Math.Clamp((Preview ?? Position) - length / 2, 0, waveform.TotalFrames - length) : 0;
        if (!Detailed)
        {
            long span = Math.Min(waveform.TotalFrames, 15L * PlaybackFormat.SampleRate);
            long start = Math.Clamp((Preview ?? Position) - span / 2, 0, waveform.TotalFrames - span);
            canvas.FillColor = WindowColor;
            canvas.FillRectangle(x + width * start / waveform.TotalFrames, bounds.Y + 4,
                width * span / waveform.TotalFrames, bounds.Height - 8);
        }

        int columns = Math.Max(1, (int)(width / 1.65f));
        float spacing = width / columns;
        float scale = bounds.Height / 2 - 4;
        canvas.StrokeSize = Math.Clamp(spacing * 0.78f, 1, 2);

        for (int column = 0; column < columns; column++)
        {
            long fromFrame = windowStart + length * column / columns;
            long toFrame = windowStart + length * (column + 1) / columns;
            int from = (int)(fromFrame / waveform.FramesPerPoint);
            int to = (int)(Math.Max(fromFrame, toFrame - 1) / waveform.FramesPerPoint);
            WaveformPoint point = waveform.Points[from];
            for (int index = from + 1; index <= to; index++)
            {
                point = point.Combine(waveform.Points[index]);
            }

            if (point.Peak < 0.001f)
            {
                continue;
            }

            float height = MathF.Sqrt(point.Peak) * scale;
            float sum = point.Low + point.Mid + point.High;
            if (sum <= 0)
            {
                continue;
            }

            float low = height * point.Low / sum;
            float mid = height * point.Mid / sum;
            float bar = x + spacing * (column + 0.5f);
            DrawBand(canvas, bar, middle, 0, low, MixerStyle.Blue);
            DrawBand(canvas, bar, middle, low, low + mid, MixerStyle.Violet);
            DrawBand(canvas, bar, middle, low + mid, height, MixerStyle.Alternate);
        }

        if (Preview is long preview)
        {
            float marker = x + width * (preview - windowStart) / length;
            canvas.FillColor = MixerStyle.Preview;
            for (float y = bounds.Y + 3; y < bounds.Bottom - 5; y += 7)
            {
                canvas.FillRectangle(marker - 1, y, 2, 4);
            }
        }

        if (Position >= windowStart && Position <= windowStart + length)
        {
            float marker = x + width * (Position - windowStart) / length;
            canvas.StrokeColor = MixerStyle.TextColor;
            canvas.StrokeSize = 2;
            canvas.DrawLine(marker, bounds.Y + 3, marker, bounds.Bottom - 3);
            canvas.FillColor = MixerStyle.TextColor;
            using var triangle = new PathF();
            triangle.MoveTo(marker - 4, bounds.Y + 3);
            triangle.LineTo(marker + 4, bounds.Y + 3);
            triangle.LineTo(marker, bounds.Y + 10);
            triangle.Close();
            canvas.FillPath(triangle);
        }
    }

    /// <summary>Draws one symmetric colored portion of a waveform column.</summary>
    private static void DrawBand(ICanvas canvas, float x, float middle, float start, float end, Color color)
    {
        if (end <= start)
        {
            return;
        }

        canvas.StrokeColor = color;
        canvas.DrawLine(x, middle - end, x, middle - start);
        canvas.DrawLine(x, middle + start, x, middle + end);
    }
}
