using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Laveyana.Audio.Maui.Controls;

internal static class NumericInputGestures
{
    /// <summary>Handles the wheel even when the native slider consumes it, and resets on double tap.</summary>
    public static void Attach(View view, Func<double, bool> scroll, Action? reset = null)
    {
        PointerEventHandler wheel = (sender, args) =>
        {
            var point = args.GetCurrentPoint((UIElement)sender);
            if (!point.Properties.IsHorizontalMouseWheel && scroll(point.Properties.MouseWheelDelta / 120d))
            {
                args.Handled = true;
            }
        };
        DoubleTappedEventHandler doubleTap = (sender, args) =>
        {
            args.Handled = true;
            ((UIElement)sender).DispatcherQueue.TryEnqueue(() => reset?.Invoke());
        };

        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is UIElement native)
            {
                native.AddHandler(UIElement.PointerWheelChangedEvent, wheel, handledEventsToo: true);
                if (reset is not null)
                {
                    native.IsDoubleTapEnabled = true;
                    native.AddHandler(UIElement.DoubleTappedEvent, doubleTap, handledEventsToo: true);
                }
            }
        };
        view.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is UIElement native)
            {
                native.RemoveHandler(UIElement.PointerWheelChangedEvent, wheel);
                if (reset is not null)
                {
                    native.RemoveHandler(UIElement.DoubleTappedEvent, doubleTap);
                }
            }
        };
    }
}
