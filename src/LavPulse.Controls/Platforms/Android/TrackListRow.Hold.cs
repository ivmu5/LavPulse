using NativeView = Android.Views.View;

namespace Laveyana.LavPulse.Controls;

internal partial class TrackListRow
{
    static partial void AttachHold(View view, Action invoke)
    {
        void Hold(object? sender, NativeView.LongClickEventArgs args)
        {
            args.Handled = true;
            invoke();
        }
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is NativeView native)
            {
                native.LongClickable = true;
                native.LongClick += Hold;
            }
        };
        view.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is NativeView native)
            {
                native.LongClick -= Hold;
                native.LongClickable = false;
            }
        };
    }
}
