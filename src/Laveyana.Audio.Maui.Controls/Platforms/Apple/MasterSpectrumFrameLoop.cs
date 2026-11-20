using CoreAnimation;
using Foundation;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class MasterSpectrumView
{
    private CADisplayLink? _displayLink;

    partial void StartFrameLoop()
    {
        var link = CADisplayLink.Create(() =>
        {
            if (_displayLink is { } active) DrawFrame(active.Timestamp);
        });
        _displayLink = link;
        link.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
    }

    partial void StopFrameLoop()
    {
        var link = _displayLink;
        _displayLink = null;
        link?.Invalidate();
        link?.Dispose();
    }

    partial void ApplyNativeFrameRateHint()
    {
        if (_displayLink is not { } link) return;

        // Keep the shared playhead clock at its normal rate for low spectrum limits.
        int requested = ActiveSpectrumFrameRate;
        int maximum = IsSpectrumActive && (requested == 0 || requested > 60) ? 120 : 60;
        int preferred = Math.Clamp(requested, 60, maximum);
        link.PreferredFrameRateRange = CAFrameRateRange.Create(30, maximum, preferred);
    }
}
