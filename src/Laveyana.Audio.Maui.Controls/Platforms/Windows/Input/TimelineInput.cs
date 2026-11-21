using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Laveyana.Audio.Maui.Controls;

internal static partial class TimelineInput
{
    /// <summary>Captures mouse, pen and touch pointers until the gesture commits or is cancelled.</summary>
    public static void Attach(GraphicsView view, Func<PointF, bool> begin, Action<PointF> move, Action<PointF> end, Action cancel)
    {
        uint? pointer = null;
        static PointF Position(UIElement native, PointerRoutedEventArgs args)
        {
            var pos = args.GetCurrentPoint(native).Position;
            return new((float)pos.X, (float)pos.Y);
        }

        void Press(object sender, PointerRoutedEventArgs args)
        {
            if (sender is not UIElement native || pointer.HasValue ||
                !args.GetCurrentPoint(native).Properties.IsLeftButtonPressed || !begin(Position(native, args)))
            {
                return;
            }

            if (!native.CapturePointer(args.Pointer))
            {
                cancel();
                return;
            }

            pointer = args.Pointer.PointerId;
            args.Handled = true;
        }

        void Move(object sender, PointerRoutedEventArgs args)
        {
            if (sender is UIElement native && pointer == args.Pointer.PointerId)
            {
                move(Position(native, args));
                args.Handled = true;
            }
        }

        void Release(object sender, PointerRoutedEventArgs args)
        {
            if (sender is UIElement native && pointer == args.Pointer.PointerId)
            {
                pointer = null;
                end(Position(native, args));
                native.ReleasePointerCapture(args.Pointer);
                args.Handled = true;
            }
        }

        void Cancel(object sender, PointerRoutedEventArgs args)
        {
            if (pointer == args.Pointer.PointerId)
            {
                pointer = null;
                cancel();
            }
        }

        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is UIElement native)
            {
                native.PointerPressed += Press;
                native.PointerMoved += Move;
                native.PointerReleased += Release;
                native.PointerCanceled += Cancel;
                native.PointerCaptureLost += Cancel;
            }
        };
        view.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is UIElement native)
            {
                native.PointerPressed -= Press;
                native.PointerMoved -= Move;
                native.PointerReleased -= Release;
                native.PointerCanceled -= Cancel;
                native.PointerCaptureLost -= Cancel;
                native.ReleasePointerCaptures();
                pointer = null;
                cancel();
            }
        };
    }
}
