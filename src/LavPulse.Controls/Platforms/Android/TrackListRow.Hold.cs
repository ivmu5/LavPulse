using NativeView = global::Android.Views.View;
using MotionAction = global::Android.Views.MotionEventActions;
using ViewConfiguration = global::Android.Views.ViewConfiguration;

namespace Laveyana.LavPulse.Controls;

internal partial class TrackListRow
{
    // One listener owns both horizontal swipe and native long-click.
    // Use screen-space deltas, because the foreground itself translates.
    static partial void AttachAndroidGestures(View view, Func<object?> item, Func<bool> enabled,
        Action<GestureStatus,double,double> pan, Action<Point?> hold)
    {
        NativeView? attached = null;
        object? pressedItem = null;
        Point down = default;
        double density = 1, threshold = 12;
        bool tracking = false, captured = false, suppress = false, interceptHeld = false;

        void Intercept(NativeView native, bool disallow)
        {
            if (interceptHeld == disallow) return;
            interceptHeld = disallow;
            native.Parent?.RequestDisallowInterceptTouchEvent(disallow);
        }

        void Cancel(NativeView native)
        {
            if (captured) pan(GestureStatus.Canceled, 0, 0);
            captured = tracking = false;
            pressedItem = null;
            native.CancelLongPress();
            Intercept(native, false);
        }

        void Touch(object? sender, NativeView.TouchEventArgs args)
        {
            if (sender is not NativeView native || args.Event is not { } motion) return;
            args.Handled = false;
            switch (motion.ActionMasked)
            {
                case MotionAction.Down:
                    Cancel(native);
                    suppress = false;
                    if (!enabled() || item() is not { } current) return;
                    density = Math.Max(.1, native.Resources?.DisplayMetrics?.Density ?? 1);
                    threshold = Math.Max(12, (ViewConfiguration.Get(native.Context!)?.ScaledTouchSlop ?? 12) / density);
                    pressedItem = current;
                    down = new Point(motion.RawX, motion.RawY);
                    tracking = true;
                    // Give the child the first movement; immediately hand a
                    // vertical gesture back to the native ScrollView.
                    Intercept(native, true);
                    break;

                case MotionAction.Move:
                    if (!tracking) { args.Handled = suppress; return; }
                    if (!enabled() || !ReferenceEquals(pressedItem, item()))
                    {
                        suppress |= captured;
                        Cancel(native);
                        args.Handled = suppress;
                        return;
                    }
                    double x = (motion.RawX - down.X) / density;
                    double y = (motion.RawY - down.Y) / density;
                    if (!captured)
                    {
                        switch (TrackListLayout.ClassifyTouch(x, y, threshold))
                        {
                            case ListTouchIntent.Pending: return;
                            case ListTouchIntent.Vertical:
                                Cancel(native);
                                return;
                        }
                        captured = suppress = true;
                        native.CancelLongPress();
                        native.Pressed = false;
                        pan(GestureStatus.Started, 0, 0);
                    }
                    pan(GestureStatus.Running, x, y);
                    args.Handled = true;
                    break;

                case MotionAction.Up:
                    bool complete = captured && enabled() && ReferenceEquals(pressedItem, item());
                    bool wasCaptured = captured;
                    captured = tracking = false;
                    pressedItem = null;
                    Intercept(native, false);
                    if (wasCaptured)
                    {
                        native.CancelLongPress();
                        native.Pressed = false;
                        if (complete)
                        {
                            pan(GestureStatus.Running, (motion.RawX - down.X) / density, (motion.RawY - down.Y) / density);
                            pan(GestureStatus.Completed, 0, 0);
                        }
                        else pan(GestureStatus.Canceled, 0, 0);
                    }
                    args.Handled = suppress;
                    suppress = false;
                    break;

                case MotionAction.PointerDown:
                case MotionAction.Cancel:
                    suppress |= captured;
                    Cancel(native);
                    args.Handled = suppress;
                    if (motion.ActionMasked == MotionAction.Cancel) suppress = false;
                    break;

                default:
                    args.Handled = suppress;
                    break;
            }
        }

        void Hold(object? sender, NativeView.LongClickEventArgs args)
        {
            if (sender is not NativeView native || !tracking || captured || !enabled() ||
                !ReferenceEquals(pressedItem, item())) return;
            args.Handled = true;
            Point? anchor = null;
            if (FindPlayer(view)?.Handler?.PlatformView is NativeView root)
            {
                var location = new int[2];
                root.GetLocationOnScreen(location);
                double rootDensity = Math.Max(.1, root.Resources?.DisplayMetrics?.Density ?? 1);
                anchor = new Point((down.X - location[0]) / rootDensity, (down.Y - location[1]) / rootDensity);
            }
            suppress = true;
            Cancel(native);
            native.Pressed = false;
            hold(anchor);
        }

        void Detach()
        {
            if (attached is not { } native) return;
            Cancel(native);
            suppress = false;
            native.Touch -= Touch;
            native.LongClick -= Hold;
            native.LongClickable = false;
            attached = null;
        }

        void Attach()
        {
            if (view.Handler?.PlatformView is not NativeView native || ReferenceEquals(attached,native)) return;
            Detach();
            attached = native;
            native.LongClickable = true;
            native.LongClick += Hold;
            native.Touch += Touch;
        }
        view.HandlerChanged += (_, _) => Attach();
        view.HandlerChanging += (_, _) => Detach();
        Attach();
    }
}
