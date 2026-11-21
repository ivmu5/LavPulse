using Android.App;
using Android.Content;
using Android.OS;

namespace Laveyana.LavPulse;

/// <summary>Connects the MAUI transport with the Android foreground media service.</summary>
internal sealed class PlatformSystemPlaybackIntegration : ISystemPlaybackIntegration
{
    internal static SystemPlaybackState? Latest { get; private set; }
    private static event Action<SystemPlaybackCommand>? Commands;

    public event Action<SystemPlaybackCommand>? CommandReceived
    {
        add => Commands += value;
        remove => Commands -= value;
    }

    internal static void Dispatch(SystemPlaybackCommand command) => Commands?.Invoke(command);

    public void Publish(SystemPlaybackState state)
    {
        Latest = state;
        AndroidMediaPlaybackService.Instance?.Refresh();
    }

    /// <summary>Starts the playback service while the app is foregrounded.</summary>
    public void EnsureRunning()
    {
        if (AndroidMediaPlaybackService.Instance is { } active)
        {
            active.Refresh();
            return;
        }

        var context = Android.App.Application.Context;
        using var intent = new Intent(context, typeof(AndroidMediaPlaybackService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public void Stop()
    {
        Latest = null;
        if (AndroidMediaPlaybackService.Instance is { } active)
            active.Shutdown();
        else
        {
            var context = Android.App.Application.Context;
            using var intent = new Intent(context, typeof(AndroidMediaPlaybackService));
            context.StopService(intent);
        }
    }

    public void Dispose() => Stop();
}
