using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using NativePoint=Windows.Foundation.Point;

namespace Laveyana.LavPulse.Controls;

internal partial class TrackListRow
{
    static partial void AttachDesktopPan(View view,Func<bool> enabled,Action<GestureStatus,double,double> pan)
    {
        uint? pointer=null;
        NativePoint start=default;
        bool captured=false;

        void Press(object sender,PointerRoutedEventArgs args)
        {
            if(pointer is not null||!enabled()) return;
            var point=args.GetCurrentPoint(null);
            if(!point.Properties.IsLeftButtonPressed) return;
            pointer=args.Pointer.PointerId;
            start=point.Position;
        }

        void Move(object sender,PointerRoutedEventArgs args)
        {
            if(sender is not UIElement native||pointer!=args.Pointer.PointerId) return;
            var point=args.GetCurrentPoint(null).Position;
            double x=point.X-start.X,y=point.Y-start.Y;
            if(!captured)
            {
                if(Math.Abs(y)>12&&Math.Abs(y)>Math.Abs(x)) {pointer=null;return;}
                if(Math.Abs(x)<Math.Max(12,Math.Abs(y)*1.3)) return;
                captured=native.CapturePointer(args.Pointer);
                if(!captured) {pointer=null;return;}
                pan(GestureStatus.Started,0,0);
            }
            pan(GestureStatus.Running,x,y);
            args.Handled=true;
        }

        void Release(object sender,PointerRoutedEventArgs args)
        {
            if(sender is not UIElement native||pointer!=args.Pointer.PointerId) return;
            bool wasCaptured=captured;
            captured=false;pointer=null;
            if(!wasCaptured) return;
            var point=args.GetCurrentPoint(null).Position;
            pan(GestureStatus.Running,point.X-start.X,point.Y-start.Y);
            native.ReleasePointerCapture(args.Pointer);
            pan(GestureStatus.Completed,0,0);
            args.Handled=true;
        }

        void Cancel(object sender,PointerRoutedEventArgs args)
        {
            if(pointer!=args.Pointer.PointerId) return;
            if(captured)pan(GestureStatus.Canceled,0,0);
            captured=false;pointer=null;
        }

        void Exit(object sender,PointerRoutedEventArgs args)
        {
            if(!captured&&pointer==args.Pointer.PointerId)pointer=null;
        }

        view.HandlerChanged+=(_,_) =>
        {
            if(view.Handler?.PlatformView is not UIElement native)return;
            native.PointerPressed+=Press;native.PointerMoved+=Move;native.PointerReleased+=Release;
            native.PointerCaptureLost+=Cancel;native.PointerCanceled+=Cancel;native.PointerExited+=Exit;
        };
        view.HandlerChanging+=(_,args) =>
        {
            if(args.OldHandler?.PlatformView is not UIElement native)return;
            native.PointerPressed-=Press;native.PointerMoved-=Move;native.PointerReleased-=Release;
            native.PointerCaptureLost-=Cancel;native.PointerCanceled-=Cancel;native.PointerExited-=Exit;
            if(captured) {pan(GestureStatus.Canceled,0,0);native.ReleasePointerCaptures();}
            captured=false;pointer=null;
        };
    }
}
