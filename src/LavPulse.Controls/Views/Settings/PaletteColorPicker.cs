namespace Laveyana.LavPulse.Controls;

/// <summary>Edits hue on a strip and saturation and value on a two-dimensional field.</summary>
internal sealed class PaletteColorPicker : VerticalStackLayout
{
    private const float Inset = 8;
    private readonly GraphicsView _field;
    private readonly GraphicsView _hueStrip;
    private float _hue;
    private float _saturation;
    private float _value;

    public event Action<Color>? ColorPicked;

    public PaletteColorPicker()
    {
        Spacing = 2;
        _field = new GraphicsView { HeightRequest = 120, Drawable = new PickerDrawable(DrawField) };
        _hueStrip = new GraphicsView { HeightRequest = 34, Drawable = new PickerDrawable(DrawHue) };
        SemanticProperties.SetDescription(_field, "Насыщенность и яркость цвета");
        SemanticProperties.SetDescription(_hueStrip, "Оттенок цвета");
        Children.Add(_field);
        Children.Add(_hueStrip);
        Attach(_field, PickField);
        Attach(_hueStrip, PickHue);
    }

    public void SetColor(Color color)
    {
        float max = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
        float min = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
        float chroma = max - min;
        if (chroma > 0) _hue = color.GetHue();
        _saturation = max > 0 ? chroma / max : 0;
        _value = max;
        InvalidatePicker();
    }

    private static void Attach(GraphicsView view, Action<PointF> update) =>
        TimelineInput.Attach(view, point => { update(point); return true; }, update, update, () => { });

    private void PickField(PointF point)
    {
        _saturation = Fraction(point.X, (float)_field.Width);
        _value = 1 - Fraction(point.Y, (float)_field.Height);
        PublishColor();
    }

    private void PickHue(PointF point)
    {
        float hue = Fraction(point.X, (float)_hueStrip.Width);
        if (_hue == hue) return;
        _hue = hue;
        PublishColor();
    }

    private void PublishColor()
    {
        InvalidatePicker();
        var color = Color.FromHsv(_hue % 1, _saturation, _value);
        ColorPicked?.Invoke(Color.FromRgb((int)Math.Round(color.Red * 255), (int)Math.Round(color.Green * 255),
            (int)Math.Round(color.Blue * 255)));
    }

    private void InvalidatePicker()
    {
        _field.Invalidate();
        _hueStrip.Invalidate();
    }

    private static float Fraction(float coordinate, float length) =>
        Math.Clamp((coordinate - Inset) / Math.Max(1, length - 2 * Inset), 0, 1);

    private static RectF Track(RectF bounds) =>
        new(bounds.X + Inset, bounds.Y + Inset, bounds.Width - 2 * Inset, bounds.Height - 2 * Inset);

    private static readonly LinearGradientPaint FadeToBlack = new(
        [new(0, Colors.Black.WithAlpha(0)), new(1, Colors.Black)], new(0, 0), new(0, 1));
    private static readonly LinearGradientPaint Rainbow = new(
        Enumerable.Range(0, 7).Select(index => new PaintGradientStop(index / 6f, Color.FromHsv(index % 6 / 6f, 1, 1))).ToArray(),
        new(0, 0), new(1, 0));

    private void DrawField(ICanvas canvas, RectF bounds)
    {
        var rect = Track(bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var horizontal = new LinearGradientPaint(
            [new(0, Colors.White), new(1, Color.FromHsv(_hue % 1, 1, 1))], new(0, 0), new(1, 0));
        canvas.SetFillPaint(horizontal, rect);
        canvas.FillRectangle(rect);
        canvas.SetFillPaint(FadeToBlack, rect);
        canvas.FillRectangle(rect);
        DrawMarker(canvas, rect.X + _saturation * rect.Width, rect.Y + (1 - _value) * rect.Height);
    }

    private void DrawHue(ICanvas canvas, RectF bounds)
    {
        var rect = Track(bounds);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        canvas.SetFillPaint(Rainbow, rect);
        canvas.FillRoundedRectangle(rect, 5);
        DrawMarker(canvas, rect.X + _hue * rect.Width, rect.Center.Y);
    }

    private static void DrawMarker(ICanvas canvas, float x, float y)
    {
        canvas.StrokeColor = Colors.Black;
        canvas.StrokeSize = 3.5f;
        canvas.DrawCircle(x, y, 5);
        canvas.StrokeColor = Colors.White;
        canvas.StrokeSize = 1.5f;
        canvas.DrawCircle(x, y, 5);
    }

    private sealed class PickerDrawable(Action<ICanvas, RectF> draw) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect) => draw(canvas, dirtyRect);
    }
}
