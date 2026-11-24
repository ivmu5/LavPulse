using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using System.Runtime.InteropServices;

namespace Laveyana.LavPulse;

/// <summary>Exposes the PCM DJ master as a MediaSession with an Android media foreground service.</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
internal sealed class AndroidMediaPlaybackService : Service
{
    private const string Channel = "lavpulse_media";
    private const int NotificationId = 1034;
    private const string Play = "com.laveyana.lavpulse.media.PLAY";
    private const string Pause = "com.laveyana.lavpulse.media.PAUSE";
    private const string Next = "com.laveyana.lavpulse.media.NEXT";
    private const string Previous = "com.laveyana.lavpulse.media.PREVIOUS";

    private MediaSession? _session;
    private MediaSession.Callback? _callback;
    private PowerManager.WakeLock? _wakeLock;
    private Bitmap? _artwork;
    private object? _artworkTrack;
    private bool _foreground;
    private bool _notificationPlaying;
    private bool _closing;

    internal static AndroidMediaPlaybackService? Instance { get; private set; }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            using var channel = new NotificationChannel(Channel, "LavPulse — воспроизведение", NotificationImportance.Low);
            channel.SetShowBadge(false);
            ((NotificationManager?)GetSystemService(NotificationService))?.CreateNotificationChannel(channel);
        }

        _session = new MediaSession(this, "LavPulse");
        _callback = new TransportCallback();
        _session.SetCallback(_callback);
        _session.Active = true;
        var power = (PowerManager?)GetSystemService(PowerService);
        _wakeLock = power?.NewWakeLock(WakeLockFlags.Partial, "LavPulse:audio");
        _wakeLock?.SetReferenceCounted(false);
    }

    /// <summary>Creates a notification immediately, as required by StartForegroundService.</summary>
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        SystemPlaybackCommand? command = intent?.Action switch
        {
            Play => SystemPlaybackCommand.Play,
            Pause => SystemPlaybackCommand.Pause,
            Next => SystemPlaybackCommand.Next,
            Previous => SystemPlaybackCommand.Previous,
            _ => null
        };

        Refresh();
        if (command is { } value) PlatformSystemPlaybackIntegration.Dispatch(value);
        return StartCommandResult.NotSticky;
    }

    internal void Refresh()
    {
        if (_closing || _session is null || PlatformSystemPlaybackIntegration.Latest is not { } state)
            return;

        bool trackChanged = !ReferenceEquals(_artworkTrack, state.Track);
        if (trackChanged)
        {
            _artworkTrack = state.Track;
            _artwork?.Dispose();
            _artwork = null;
            if (!state.Track.Artwork.IsEmpty)
            {
                try
                {
                    var artwork = state.Track.Artwork;
                    var bytes = MemoryMarshal.TryGetArray(artwork, out var segment)
                        ? segment : new ArraySegment<byte>(artwork.ToArray());
                    using var info = new BitmapFactory.Options { InJustDecodeBounds = true };
                    BitmapFactory.DecodeByteArray(bytes.Array!, bytes.Offset, bytes.Count, info)?.Dispose();
                    int side = Math.Max(info.OutWidth, info.OutHeight);
                    using var options = new BitmapFactory.Options { InSampleSize = Math.Max(1, (side + 383) / 384) };
                    _artwork = BitmapFactory.DecodeByteArray(bytes.Array!, bytes.Offset, bytes.Count, options);
                }
                catch (Exception)
                {
                    _artwork = null;
                }
            }

            using var metadata = new MediaMetadata.Builder();
            metadata.PutString("android.media.metadata.TITLE", state.Track.Title);
            metadata.PutString("android.media.metadata.ARTIST", state.Track.Artist ?? "");
            metadata.PutLong("android.media.metadata.DURATION", state.DurationMilliseconds);
            if (_artwork is not null)
                metadata.PutBitmap("android.media.metadata.ALBUM_ART", _artwork);
            using var built = metadata.Build();
            _session.SetMetadata(built);
        }

        using var builder = new PlaybackState.Builder();
        long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause;
        if (state.CanNext) actions |= PlaybackState.ActionSkipToNext;
        if (state.CanPrevious) actions |= PlaybackState.ActionSkipToPrevious;
        builder.SetActions(actions);
        builder.SetState(state.Playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused,
            state.PositionMilliseconds, state.Playing ? (float)state.Speed : 0f);
        using var playbackState = builder.Build();
        _session.SetPlaybackState(playbackState);

        if (state.Playing && _wakeLock?.IsHeld == false) _wakeLock.Acquire();
        if (!state.Playing && _wakeLock?.IsHeld == true) _wakeLock.Release();

        // The media session reports progress. Only the track and play/pause button affect the notification itself.
        if (_foreground && !trackChanged && _notificationPlaying == state.Playing) return;

        using var notification = BuildNotification(state);
        if (!_foreground)
        {
            StartForeground(NotificationId, notification);
            _foreground = true;
        }
        else
            ((NotificationManager?)GetSystemService(NotificationService))?.Notify(NotificationId, notification);
        _notificationPlaying = state.Playing;
    }

    private Notification BuildNotification(SystemPlaybackState state)
    {
        using var launch = new Intent(this, typeof(MainActivity));
        launch.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var contentIntent = PendingIntent.GetActivity(this, 0, launch,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        using var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, Channel)
            : new Notification.Builder(this);
        using var style = new Notification.MediaStyle();

        style.SetMediaSession(_session!.SessionToken);
        style.SetShowActionsInCompactView(0, 1, 2);
        builder.SetSmallIcon(Android.Resource.Drawable.IcMediaPlay);
        builder.SetContentTitle(state.Track.Title);
        builder.SetContentText(state.Track.Artist ?? "LavPulse");
        builder.SetContentIntent(contentIntent);
        builder.SetVisibility(NotificationVisibility.Public);
        builder.SetOngoing(state.Playing);
        if (_artwork is not null) builder.SetLargeIcon(_artwork);
        builder.SetStyle(style);

        using var previous = CreateAction(Previous, 1);
        using var pause = CreateAction(state.Playing ? Pause : Play, 2);
        using var next = CreateAction(Next, 3);
        builder.AddAction(Android.Resource.Drawable.IcMediaPrevious, "Предыдущий", previous);
        builder.AddAction(state.Playing ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay,
            state.Playing ? "Пауза" : "Воспроизвести", pause);
        builder.AddAction(Android.Resource.Drawable.IcMediaNext, "Следующий", next);
        return builder.Build();
    }

    private PendingIntent? CreateAction(string action, int id)
    {
        using var intent = new Intent(this, typeof(AndroidMediaPlaybackService));
        intent.SetAction(action);
        return PendingIntent.GetService(this, id, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
    }

    internal void Shutdown()
    {
        _closing = true;
        if (_foreground)
        {
            StopForeground(StopForegroundFlags.Remove);
            _foreground = false;
        }
        StopSelf();
    }

    public override void OnDestroy()
    {
        _closing = true;
        if (_wakeLock?.IsHeld == true) _wakeLock.Release();
        _wakeLock?.Dispose();
        if (_session is not null) _session.Active = false;
        _session?.Release();
        _session?.Dispose();
        _callback?.Dispose();
        _artwork?.Dispose();
        if (ReferenceEquals(Instance, this)) Instance = null;
        base.OnDestroy();
    }

    private sealed class TransportCallback : MediaSession.Callback
    {
        public override void OnPlay() => PlatformSystemPlaybackIntegration.Dispatch(SystemPlaybackCommand.Play);
        public override void OnPause() => PlatformSystemPlaybackIntegration.Dispatch(SystemPlaybackCommand.Pause);
        public override void OnSkipToNext() => PlatformSystemPlaybackIntegration.Dispatch(SystemPlaybackCommand.Next);
        public override void OnSkipToPrevious() => PlatformSystemPlaybackIntegration.Dispatch(SystemPlaybackCommand.Previous);
    }
}
