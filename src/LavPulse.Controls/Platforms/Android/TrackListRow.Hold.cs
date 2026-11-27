using NativeView = Android.Views.View;

namespace Laveyana.LavPulse.Controls;

internal partial class TrackListRow
{
    static partial void AttachHold(View view, Action<Point?> invoke)
    {
        Point? down = null;
        void Touch(object? sender, NativeView.TouchEventArgs args)
        {
            if (args.Event is not { } motion) return;
            if (motion.ActionMasked == Android.Views.MotionEventActions.Down)
                down = new Point(motion.RawX, motion.RawY);
            else if (motion.ActionMasked is Android.Views.MotionEventActions.Cancel or Android.Views.MotionEventActions.Up)
                down = null;
            // Observe only; native scrolling and the existing long-click
            // recognizer retain ownership of the gesture.
        }
        void Hold(object? sender, NativeView.LongClickEventArgs args)
        {
            args.Handled = true;
            Point? anchor = null;
            if (FindPlayer(view)?.Handler?.PlatformView is NativeView root)
            {
                var location = new int[2];
                root.GetLocationOnScreen(location);
                double density = root.Resources?.DisplayMetrics?.Density ?? 1;
                if (density > 0 && down is { } p)
                    anchor = new Point((p.X - location[0]) / density, (p.Y - location[1]) / density);
                else if (density > 0 && sender is NativeView source)
                {
                    var sourceLocation = new int[2];
                    source.GetLocationOnScreen(sourceLocation);
                    anchor = new Point((sourceLocation[0] + source.Width / 2d - location[0]) / density,
                        (sourceLocation[1] + source.Height / 2d - location[1]) / density);
                }
            }
            invoke(anchor);
        }
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is NativeView native)
            {
                native.LongClickable = true;
                native.LongClick += Hold;
                native.Touch += Touch;
            }
        };
        view.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is NativeView native)
            {
                native.LongClick -= Hold;
                native.Touch -= Touch;
                down = null;
                native.LongClickable = false;
            }
        };
    }
}
