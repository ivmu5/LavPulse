using Android.Views;
using MauiView = Microsoft.Maui.Controls.View;
using NativeView = Android.Views.View;

namespace Laveyana.LavPulse.Controls;

internal static class NumericInputGestures
{
    /// <summary>Supports a hardware mouse and distinguishes a slider double tap from two drags.</summary>
    public static void Attach(MauiView view, Func<double, bool> scroll, Action? reset = null, bool vertical = false)
    {
        PointF? press = null;
        PointF? previousTap = null;
        long previousTapTime = 0;
        int touchSlop = 0;
        int tapSlop = 0;
        int timeout = ViewConfiguration.DoubleTapTimeout;

        void Wheel(object? sender, NativeView.GenericMotionEventArgs args)
        {
            if (args.Event is { ActionMasked: MotionEventActions.Scroll } motion && scroll(motion.GetAxisValue(Axis.Vscroll)))
            {
                args.Handled = true;
            }
        }

        void Touch(object? sender, NativeView.TouchEventArgs args)
        {
            args.Handled = false;
            if (sender is not NativeView native || args.Event is not { } motion)
            {
                return;
            }

            var position = new PointF(motion.GetX(), motion.GetY());
            if (vertical)
            {
                if (motion.ActionMasked == MotionEventActions.Down) native.Parent?.RequestDisallowInterceptTouchEvent(true);
                if (motion.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
                    native.Parent?.RequestDisallowInterceptTouchEvent(false);
            }
            switch (motion.ActionMasked)
            {
                case MotionEventActions.Down:
                    press = position;
                    break;
                case MotionEventActions.Move when press is { } start && !Within(start, position, touchSlop):
                case MotionEventActions.Cancel:
                case MotionEventActions.PointerDown:
                    press = previousTap = null;
                    break;
                case MotionEventActions.Up:
                    bool tap = press is { } down && Within(down, position, touchSlop) && motion.EventTime - motion.DownTime <= timeout;
                    press = null;
                    if (tap && previousTap is { } first && motion.EventTime - previousTapTime <= timeout && Within(first, position, tapSlop))
                    {
                        previousTap = null;
                        // Let SeekBar finish updating its thumb before resetting the value.
                        native.Post(() => reset?.Invoke());
                    }
                    else
                    {
                        previousTap = tap ? position : null;
                        previousTapTime = motion.EventTime;
                    }
                    break;
            }
        }

        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is NativeView native)
            {
                native.GenericMotion += Wheel;
                if (reset is not null)
                {
                    var configuration = native.Context is { } context ? ViewConfiguration.Get(context) : null;
                    touchSlop = configuration?.ScaledTouchSlop ?? 8;
                    tapSlop = configuration?.ScaledDoubleTapSlop ?? 32;
                    native.Touch += Touch;
                }
            }
        };
        view.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is NativeView native)
            {
                native.GenericMotion -= Wheel;
                if (reset is not null)
                {
                    if (vertical) native.Parent?.RequestDisallowInterceptTouchEvent(false);
                    native.Touch -= Touch;
                    press = previousTap = null;
                }
            }
        };
    }

    private static bool Within(PointF first, PointF second, int slop)
    {
        float dx = first.X - second.X;
        float dy = first.Y - second.Y;
        return dx * dx + dy * dy <= slop * slop;
    }
}
