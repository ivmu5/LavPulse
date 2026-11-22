using Android.Content;
using Microsoft.Maui.Graphics.Platform;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Platform;
using NativeCanvas = Android.Graphics.Canvas;
using NativePath = Android.Graphics.Path;

namespace Laveyana.LavPulse.Controls;

public static class AndroidPlaybackGraphics
{
    /// <summary>Uses the playback renderer for animated surfaces while retaining MAUI input handling.</summary>
    public static void Register(IMauiHandlersCollection handlers) =>
        handlers.AddHandler<PlaybackGraphicsView>(_ => new PlaybackGraphicsHandler());

    private sealed class PlaybackGraphicsHandler : GraphicsViewHandler
    {
        protected override PlatformTouchGraphicsView CreatePlatformView() => new PlaybackSurface(Context);
    }

    private sealed class PlaybackSurface(Context context) : PlatformTouchGraphicsView(context)
    {
        private readonly PlaybackCanvas _canvas = new(context);
        private RectF _bounds;
        private float _scaleX;
        private float _scaleY;

        protected override void OnSizeChanged(int width, int height, int oldWidth, int oldHeight)
        {
            base.OnSizeChanged(width, height, oldWidth, oldHeight);
            float density = Context.GetDisplayDensity();
            float logicalWidth = MathF.Max(1, MathF.Round(width / density));
            float logicalHeight = MathF.Max(1, MathF.Round(height / density));
            _bounds = new RectF(0, 0, logicalWidth, logicalHeight);
            _scaleX = width / logicalWidth;
            _scaleY = height / logicalHeight;
        }

        public override void Draw(NativeCanvas canvas)
        {
            if (Drawable is not { } drawable || _scaleX <= 0 || _scaleY <= 0) return;

            int save = canvas.Save();
            try
            {
                if (LayoutDirection == Android.Views.LayoutDirection.Rtl)
                {
                    canvas.Translate(Width, 0);
                    canvas.Scale(-1, 1);
                }

                // Scale the native canvas once. Paths can then stay cached in logical units.
                canvas.ClipRect(0, 0, Width, Height);
                canvas.Scale(_scaleX, _scaleY);
                _canvas.Canvas = canvas;
                if (BackgroundColor is { } background)
                {
                    _canvas.FillColor = background;
                    _canvas.FillRectangle(_bounds);
                    _canvas.FillColor = Colors.White;
                }
                drawable.Draw(_canvas, _bounds);
            }
            finally
            {
                canvas.RestoreToCount(save);
                _canvas.Canvas = null!;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _canvas.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class PlaybackCanvas(Context context) : PlatformCanvas(context)
    {
        public override void FillRoundedRectangle(float x, float y, float width, float height, float radius) =>
            Canvas.DrawRoundRect(x, y, x + width, y + height, radius, radius, CurrentState.FillPaintWithAlpha);

        public override void FillEllipse(float x, float y, float width, float height) =>
            Canvas.DrawOval(x, y, x + width, y + height, CurrentState.FillPaintWithAlpha);

        protected override void PlatformDrawLine(float x1, float y1, float x2, float y2) =>
            Canvas.DrawLine(x1, y1, x2, y2, CurrentState.StrokePaintWithAlpha);

        protected override void PlatformDrawPath(PathF path) =>
            Canvas.DrawPath(GetNativePath(path), CurrentState.StrokePaintWithAlpha);

        public override void FillPath(PathF path, WindingMode windingMode)
        {
            var native = GetNativePath(path);
            native.SetFillType(windingMode == WindingMode.NonZero ? NativePath.FillType.Winding! : NativePath.FillType.EvenOdd!);
            Canvas.DrawPath(native, CurrentState.FillPaintWithAlpha);
        }

        // PathF releases the native path when the waveform mesh changes or is disposed.
        private static NativePath GetNativePath(PathF path)
        {
            if (path.PlatformPath is NativePath native) return native;
            native = path.AsAndroidPath();
            path.PlatformPath = native;
            return native;
        }
    }
}
