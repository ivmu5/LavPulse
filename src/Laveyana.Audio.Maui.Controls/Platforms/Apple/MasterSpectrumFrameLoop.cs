using CoreAnimation;
using Foundation;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class MasterSpectrumView
{
    private CADisplayLink? _displayLink;

    partial void StartFrameLoop()
    {
        var link = CADisplayLink.Create(() => DrawFrame(DeckPlayhead.Now));
        link.PreferredFrameRateRange = CAFrameRateRange.Create(30, 60, 60);
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
}
