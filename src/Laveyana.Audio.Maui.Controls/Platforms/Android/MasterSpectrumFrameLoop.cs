using Android.Views;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class MasterSpectrumView
{
    private DisplayFrameCallback? _displayCallback;

    partial void ObserveNativeGraphicsView() =>
        _graphics.HandlerChanged += (_, _) => ApplyNativeFrameRateHint(force: true);

    partial void ApplyNativeFrameRateHint(bool force)
    {
        int requested = NeedsFrames ? (FramesPerSecond == 0 ? 120 : FramesPerSecond) : 0;
        if (OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            float hint = requested == 0 ? float.NaN : requested;
            SetHint(_graphics, IsSpectrumActive ? hint : float.NaN, force);
            SetHint(_uiFrameTarget, _drawUi is null ? float.NaN : hint, force);
        }
        else if (Window?.Handler?.PlatformView is global::Android.App.Activity activity && activity.Window is { } window &&
                 window.Attributes is { } attributes)
        {
            float hint = requested;
            if (hint > 0 && !OperatingSystem.IsAndroidVersionAtLeast(34))
            {
#pragma warning disable CS0618, CA1422 // Legacy Android requires a rate advertised by this window's display.
                var display = window.DecorView.Display;
                hint = display?.GetSupportedRefreshRates()?.MinBy(rate => (Math.Abs(rate - requested), -rate)) ?? 0;
#pragma warning restore CS0618, CA1422
            }
            if (force && hint > 0 || !attributes.PreferredRefreshRate.Equals(hint))
            {
                attributes.PreferredRefreshRate = hint;
                window.Attributes = attributes;
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android35.0")]
    private static void SetHint(VisualElement? target, float preferred, bool force)
    {
        if (target?.Handler?.PlatformView is global::Android.Views.View view &&
            (force && !float.IsNaN(preferred) || !view.RequestedFrameRate.Equals(preferred))) view.RequestedFrameRate = preferred;
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
