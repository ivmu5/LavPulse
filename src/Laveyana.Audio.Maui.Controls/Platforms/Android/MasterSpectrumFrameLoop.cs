using Android.Views;
using NativeView = Android.Views.View;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class MasterSpectrumView
{
    private DisplayFrameCallback? _displayCallback;
    private NativeView? _frameRateView;
    private float _previousFrameRate;

    /// <summary>Registers an Android display callback and requests 120 Hz on supported versions.</summary>
    partial void StartFrameLoop()
    {
        var choreographer = Choreographer.Instance;
        if (choreographer is null)
        {
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(35) && _graphics.Handler?.PlatformView is NativeView view)
        {
            _previousFrameRate = view.RequestedFrameRate;
            _frameRateView = view;
            view.RequestedFrameRate = 120;
        }

        _displayCallback = new DisplayFrameCallback(choreographer, DrawFrame);
        _displayCallback.Start();
    }

    /// <summary>Removes the display callback and restores the previous view frame-rate preference.</summary>
    partial void StopFrameLoop()
    {
        _displayCallback?.Stop();
        _displayCallback?.Dispose();
        _displayCallback = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(35) && _frameRateView is { } view)
        {
            view.RequestedFrameRate = _previousFrameRate;
            _frameRateView = null;
        }
    }

    private sealed class DisplayFrameCallback(Choreographer choreographer, Action<double> draw)
        : Java.Lang.Object, Choreographer.IFrameCallback
    {
        private bool _running;

        /// <summary>Posts the first callback to the Android display scheduler.</summary>
        public void Start()
        {
            _running = true;
            choreographer.PostFrameCallback(this);
        }

        /// <summary>Cancels the callback before its Java peer is disposed.</summary>
        public void Stop()
        {
            _running = false;
            choreographer.RemoveFrameCallback(this);
        }

        /// <summary>Uses the display timestamp to render one frame and request the next callback.</summary>
        public void DoFrame(long frameTimeNanos)
        {
            if (_running)
            {
                draw(frameTimeNanos / 1_000_000_000.0);
                choreographer.PostFrameCallback(this);
            }
        }
    }
}
