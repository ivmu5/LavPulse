using UIKit;

namespace Laveyana.LavPulse.Controls;

internal partial class TrackListRow
{
    static partial void AttachHold(View view, Action invoke)
    {
        UILongPressGestureRecognizer? hold = null;
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is not UIView native) return;
            hold = new UILongPressGestureRecognizer(gesture =>
            {
                if (gesture.State == UIGestureRecognizerState.Began) invoke();
            }) { MinimumPressDuration = .55, CancelsTouchesInView = true };
            native.AddGestureRecognizer(hold);
        };
        view.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is UIView native && hold is not null)
            {
                native.RemoveGestureRecognizer(hold);
                hold.Dispose();
                hold = null;
            }
        };
    }
}
