using Android.Views;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class MasterSpectrumView
{
    private DisplayFrameCallback? _displayCallback;

    partial void ObserveNativeGraphicsView() =>
        _graphics.HandlerChanged += (_, _) => ApplyNativeFrameRateHint();

    partial void ApplyNativeFrameRateHint()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(35) &&
            _graphics.Handler?.PlatformView is global::Android.Views.View view)
        {
            // Android uses NaN for its default policy. Request high refresh only while the spectrum is active.
            int requested = ActiveSpectrumFrameRate;
            float preferred = requested > 60 ? requested : float.NaN;
            if (!view.RequestedFrameRate.Equals(preferred)) view.RequestedFrameRate = preferred;
        }
    }

    /// <summary>Registers an Android display callback at the system-selected refresh rate.</summary>
    partial void StartFrameLoop()
    {
        var choreographer = Choreographer.Instance;
        if (choreographer is null)
        {
            return;
        }

        _displayCallback = new DisplayFrameCallback(choreographer, DrawFrame);
        _displayCallback.Start();
    }

    /// <summary>Removes the display callback and releases its Java peer.</summary>
    partial void StopFrameLoop()
    {
        _displayCallback?.Stop();
        _displayCallback?.Dispose();
        _displayCallback = null;
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
                if (_running) choreographer.PostFrameCallback(this);
            }
        }
    }
}
