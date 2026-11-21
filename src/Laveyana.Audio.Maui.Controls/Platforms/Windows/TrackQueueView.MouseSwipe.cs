using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using NativePoint = Windows.Foundation.Point;

namespace Laveyana.Audio.Maui.Controls;

internal sealed partial class TrackQueueView
{
    partial void AttachMouseSwipe(QueueTrackRow row)
    {
        uint? pointer = null;
        NativePoint start = default;
        var file = row.File;

        void Press(object sender, PointerRoutedEventArgs args)
        {
            if (sender is not UIElement native || pointer is not null || !CanSwipe ||
                args.Pointer.PointerDeviceType != PointerDeviceType.Mouse || row.File is null)
                return;

            var point = args.GetCurrentPoint(native);
            if (!point.Properties.IsLeftButtonPressed || !native.CapturePointer(args.Pointer)) return;
            start = point.Position;
            file = row.File;
            pointer = args.Pointer.PointerId;
            args.Handled = true;
        }

        void Release(object sender, PointerRoutedEventArgs args)
        {
            if (sender is not UIElement native || pointer != args.Pointer.PointerId) return;
            pointer = null;
            var finish = args.GetCurrentPoint(native).Position;
            native.ReleasePointerCapture(args.Pointer);
            double left = start.X - finish.X;
            if (ReferenceEquals(file, row.File) && left >= SwipeThreshold && left > Math.Abs(finish.Y - start.Y))
                BeginSwipe(row);
            else
                DismissDeleteAction();
            args.Handled = true;
        }

        void Cancel(object sender, PointerRoutedEventArgs args)
        {
            if (pointer == args.Pointer.PointerId) pointer = null;
        }

        row.TrackBody.HandlerChanged += (_, _) =>
        {
            if (row.TrackBody.Handler?.PlatformView is not UIElement native) return;
            native.PointerPressed += Press;
            native.PointerReleased += Release;
            native.PointerCaptureLost += Cancel;
            native.PointerCanceled += Cancel;
        };
        row.TrackBody.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is not UIElement native) return;
            native.PointerPressed -= Press;
            native.PointerReleased -= Release;
            native.PointerCaptureLost -= Cancel;
            native.PointerCanceled -= Cancel;
            if (pointer is not null) native.ReleasePointerCaptures();
            pointer = null;
        };
    }
}
