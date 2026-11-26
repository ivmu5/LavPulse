using CoreAnimation;
using Foundation;

namespace Laveyana.LavPulse.Controls;

public sealed partial class MasterSpectrumView
{
    private CADisplayLink? _displayLink;

    partial void StartFrameLoop()
    {
        if (_displayLink is not null) return;
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

    partial void ApplyNativeFrameRateHint(bool force)
    {
        if (_displayLink is not { } link) return;

        int preferred = Math.Clamp(FramesPerSecond == 0 ? 120 : FramesPerSecond, 30, 120);
        link.PreferredFrameRateRange = CAFrameRateRange.Create(30, preferred, preferred);
    }
}
