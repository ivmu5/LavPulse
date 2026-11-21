using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Laveyana.Audio.Maui;

/// <summary>Keeps a user-initiated queue preparation batch alive while the activity is stopped.</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
internal sealed class AndroidTrackAnalysisService : Service
{
    private const string Channel = "lavpulse_track_analysis";
    private const int NotificationId = 1035;
    private static AndroidTrackAnalysisService? _active;
    private static bool _requested;
    private PowerManager.WakeLock? _wakeLock;

    internal static void Start()
    {
        if (_requested) return;
        _requested = true;
        var context = Android.App.Application.Context;
        using var intent = new Intent(context, typeof(AndroidTrackAnalysisService));
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
            else context.StartService(intent);
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.SecurityException)
        {
            _requested = false;
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    internal static void Stop()
    {
        _requested = false;
        _active?.Finish();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        using var notification = BuildNotification();
        try
        {
            StartForeground(NotificationId, notification);
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.SecurityException)
        {
            _requested = false;
            System.Diagnostics.Debug.WriteLine(ex);
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }
        _active = this;
        if (_requested)
        {
            var power = (PowerManager?)GetSystemService(PowerService);
            _wakeLock ??= power?.NewWakeLock(WakeLockFlags.Partial, "LavPulse:track_analysis");
            if (_wakeLock?.IsHeld == false) _wakeLock.Acquire();
        }
        else
        {
            // A short batch can finish before the system delivers its start request.
            Finish();
        }
        return StartCommandResult.NotSticky;
    }

    public override void OnTimeout(int startId, ForegroundService fgsType) => Stop();

    public override void OnDestroy()
    {
        ReleaseWakeLock();
        _wakeLock?.Dispose();
        if (ReferenceEquals(_active, this))
        {
            _active = null;
            _requested = false;
        }
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        using var launch = new Intent(this, typeof(MainActivity));
        launch.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var contentIntent = PendingIntent.GetActivity(this, 0, launch,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        using var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, Channel)
            : new Notification.Builder(this);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            using var channel = new NotificationChannel(Channel, "LavPulse — анализ треков", NotificationImportance.Low);
            channel.SetShowBadge(false);
            ((NotificationManager?)GetSystemService(NotificationService))?.CreateNotificationChannel(channel);
        }
        builder.SetSmallIcon(Android.Resource.Drawable.IcMediaPlay);
        builder.SetContentTitle("LavPulse");
        builder.SetContentText("Подготовка следующих треков");
        builder.SetContentIntent(contentIntent);
        builder.SetProgress(0, 0, true);
        builder.SetOngoing(true);
        return builder.Build();
    }

    private void Finish()
    {
        _active = null;
        ReleaseWakeLock();
        StopSelf();
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
    }
}
