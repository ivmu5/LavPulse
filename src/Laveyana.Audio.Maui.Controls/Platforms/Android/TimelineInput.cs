using Android.Views;
using NativeView = Android.Views.View;

namespace Laveyana.Audio.Maui.Controls;

internal static partial class TimelineInput
{
    /// <summary>Uses local coordinates even when a touch moves beyond the waveform's bounds.</summary>
    public static void Attach(GraphicsView view, Func<PointF, bool> begin, Action<PointF> move, Action<PointF> end, Action cancel)
    {
        bool tracking = false;
        void Touch(object? sender, NativeView.TouchEventArgs args)
        {
            if (sender is not NativeView native || args.Event is not { } motion)
            {
                return;
            }

            float density = native.Resources?.DisplayMetrics?.Density ?? 1;
            var point = new PointF(motion.GetX() / density, motion.GetY() / density);
            switch (motion.ActionMasked)
            {
                case MotionEventActions.Down:
                    tracking = begin(point);
                    if (tracking)
                    {
                        native.Parent?.RequestDisallowInterceptTouchEvent(true);
                    }
                    args.Handled = tracking;
                    break;
                case MotionEventActions.Move when tracking:
                    move(point);
                    args.Handled = true;
                    break;
                case MotionEventActions.Up when tracking:
                    tracking = false;
                    end(point);
                    native.Parent?.RequestDisallowInterceptTouchEvent(false);
                    args.Handled = true;
                    break;
                case MotionEventActions.Cancel or MotionEventActions.PointerDown when tracking:
                    tracking = false;
                    cancel();
                    native.Parent?.RequestDisallowInterceptTouchEvent(false);
                    args.Handled = true;
                    break;
            }
        }

        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is NativeView native)
            {
                native.Touch += Touch;
            }
        };
        view.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is NativeView native)
            {
                native.Touch -= Touch;
                if (tracking)
                {
                    native.Parent?.RequestDisallowInterceptTouchEvent(false);
                }
                tracking = false;
                cancel();
            }
        };
    }
}
