using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using NativePoint = Windows.Foundation.Point;

namespace Laveyana.LavPulse.Controls;

internal static partial class TrackListSwipe
{
    static partial void AttachMouse(View view, Func<object?> item, Func<bool> enabled, Action reveal)
    {
        uint? pointer = null;
        NativePoint start = default;
        object? pressedItem = null;
        bool captured = false;

        void Press(object sender, PointerRoutedEventArgs args)
        {
            if (sender is not UIElement native || pointer is not null || !enabled() ||
                args.Pointer.PointerDeviceType != PointerDeviceType.Mouse || item() is not { } value)
                return;
            var point = args.GetCurrentPoint(native);
            if (!point.Properties.IsLeftButtonPressed) return;
            start = point.Position;
            pressedItem = value;
            pointer = args.Pointer.PointerId;
        }

        void Move(object sender, PointerRoutedEventArgs args)
        {
            if (sender is not UIElement native || pointer != args.Pointer.PointerId || captured) return;
            var position = args.GetCurrentPoint(native).Position;
            double left = start.X - position.X;
            if (left >= 10 && left > Math.Abs(position.Y - start.Y))
            {
                captured = native.CapturePointer(args.Pointer);
                args.Handled = captured;
            }
        }

        void Release(object sender, PointerRoutedEventArgs args)
        {
            if (sender is not UIElement native || pointer != args.Pointer.PointerId) return;
            var end = args.GetCurrentPoint(native).Position;
            bool wasCaptured = captured;
            var value = pressedItem;
            Reset();
            if (wasCaptured) native.ReleasePointerCapture(args.Pointer);
            double left = start.X - end.X;
            if (enabled() && ReferenceEquals(value, item()) && left >= Threshold && left > Math.Abs(end.Y - start.Y))
            {
                reveal();
                args.Handled = true;
            }
            else if (wasCaptured) args.Handled = true;
        }

        void Exit(object sender, PointerRoutedEventArgs args)
        {
            if (!captured && pointer == args.Pointer.PointerId) Reset();
        }

        void Cancel(object sender, PointerRoutedEventArgs args)
        {
            if (pointer == args.Pointer.PointerId) Reset();
        }

        void Reset()
        {
            pointer = null;
            pressedItem = null;
            captured = false;
        }

        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is not UIElement native) return;
            native.PointerPressed += Press;
            native.PointerMoved += Move;
            native.PointerReleased += Release;
            native.PointerExited += Exit;
            native.PointerCaptureLost += Cancel;
            native.PointerCanceled += Cancel;
        };
        view.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is not UIElement native) return;
            native.PointerPressed -= Press;
            native.PointerMoved -= Move;
            native.PointerReleased -= Release;
            native.PointerExited -= Exit;
            native.PointerCaptureLost -= Cancel;
            native.PointerCanceled -= Cancel;
            if (captured) native.ReleasePointerCaptures();
            Reset();
        };
    }
}
