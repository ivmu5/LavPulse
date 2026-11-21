using Microsoft.UI.Xaml.Media;

namespace Laveyana.LavPulse.Controls;

public sealed partial class MasterSpectrumView
{
    /// <summary>Subscribes to the Windows compositor's display-frame event.</summary>
    partial void StartFrameLoop() => CompositionTarget.Rendering += OnDisplayFrame;

    /// <summary>Unsubscribes from the Windows compositor when playback stops.</summary>
    partial void StopFrameLoop() => CompositionTarget.Rendering -= OnDisplayFrame;

    /// <summary>Passes the frame's compositor timestamp to the spectrum renderer.</summary>
    private void OnDisplayFrame(object? sender, object args)
    {
        if (args is RenderingEventArgs frame)
        {
            DrawFrame(frame.RenderingTime.TotalSeconds);
        }
    }
}
