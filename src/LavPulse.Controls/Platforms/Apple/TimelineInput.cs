#if IOS
using UIKit;

namespace Laveyana.LavPulse.Controls;

internal static partial class TimelineInput
{
    /// <summary>Captures a touch on the waveform or color field before a surrounding scroll view pans.</summary>
    public static void Attach(GraphicsView view, Func<PointF, bool> begin, Action<PointF> move, Action<PointF> end, Action cancel)
    {
        UILongPressGestureRecognizer? recognizer = null;
        bool tracking = false;

        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is not UIView native) return;

            PointF Position(UIGestureRecognizer gesture)
            {
                var point = gesture.LocationInView(native);
                return new PointF((float)point.X, (float)point.Y);
            }

            recognizer = new UILongPressGestureRecognizer(gesture =>
            {
                if (!tracking) return;
                switch (gesture.State)
                {
                    case UIGestureRecognizerState.Changed:
                        move(Position(gesture));
                        break;
                    case UIGestureRecognizerState.Ended:
                        tracking = false;
                        end(Position(gesture));
                        break;
                    case UIGestureRecognizerState.Cancelled:
                    case UIGestureRecognizerState.Failed:
                        tracking = false;
                        cancel();
                        break;
                }
            })
            {
                MinimumPressDuration = 0,
                CancelsTouchesInView = true,
                ShouldBegin = gesture => tracking = view.IsEnabled && !view.InputTransparent && begin(Position(gesture))
            };
            native.AddGestureRecognizer(recognizer);
        };

        view.HandlerChanging += (_, args) =>
        {
            if (recognizer is not { } gesture) return;
            if (args.OldHandler?.PlatformView is UIView native) native.RemoveGestureRecognizer(gesture);
            gesture.Dispose();
            recognizer = null;
            if (tracking)
            {
                tracking = false;
                cancel();
            }
        };
    }
}
#endif
