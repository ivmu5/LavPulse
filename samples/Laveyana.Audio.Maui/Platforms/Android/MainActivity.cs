using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.Core.View;
using AndroidUri = Android.Net.Uri;

namespace Laveyana.Audio.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private ActivityResultLauncher? _folderLauncher;
    private TaskCompletionSource<AndroidUri?>? _folderResult;
    private ScreenStateReceiver? _screenReceiver;

    internal bool IsPlayerVisible { get; private set; }
    internal event Action<bool>? PlayerVisibilityChanged;

    /// <summary>Initializes the Android activity, media volume controls and system folder picker.</summary>
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        VolumeControlStream = Android.Media.Stream.Music;
        _folderLauncher = RegisterForActivityResult(new ActivityResultContracts.OpenDocumentTree(),
            new FolderResult(result => _folderResult?.TrySetResult(result)));
    }

    protected override void OnStart()
    {
        base.OnStart();
        using var filter = new IntentFilter(Intent.ActionScreenOn);
        filter.AddAction(Intent.ActionScreenOff);
        _screenReceiver = new ScreenStateReceiver(UpdatePlayerVisibility);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterReceiver(_screenReceiver, filter, ReceiverFlags.NotExported);
        else
            RegisterReceiver(_screenReceiver, filter);

        UpdatePlayerVisibility(GetSystemService(PowerService) is not PowerManager { IsInteractive: false });
    }

    protected override void OnStop()
    {
        if (_screenReceiver is { } receiver)
        {
            _screenReceiver = null;
            UnregisterReceiver(receiver);
            receiver.Dispose();
        }

        UpdatePlayerVisibility(false);
        base.OnStop();
    }

    internal void ApplySystemBarAppearance(bool light)
    {
        if (Window is not { } window) return;
        if (WindowCompat.GetInsetsController(window, window.DecorView) is not { } controller) return;
        controller.AppearanceLightStatusBars = light;
        controller.AppearanceLightNavigationBars = light;
    }

    /// <summary>Allows the already open player to remain visible above the keyguard.</summary>
    internal void SetPlayerOnLockScreen(bool enabled)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(27))
            SetShowWhenLocked(enabled);
        else if (enabled)
            Window?.AddFlags(WindowManagerFlags.ShowWhenLocked);
        else
            Window?.ClearFlags(WindowManagerFlags.ShowWhenLocked);
    }

    private void UpdatePlayerVisibility(bool screenInteractive)
    {
        bool visible = _screenReceiver is not null && screenInteractive;
        if (IsPlayerVisible == visible) return;
        IsPlayerVisible = visible;
        PlayerVisibilityChanged?.Invoke(visible);
    }

    /// <summary>Requests a Storage Access Framework tree URI and awaits the selection or cancellation.</summary>
    internal async Task<AndroidUri?> PickTrackTreeAsync(CancellationToken token)
    {
        if (_folderLauncher is null || _folderResult is not null)
        {
            throw new InvalidOperationException("Выбор папки недоступен.");
        }

        var result = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _folderResult = result;
        try
        {
            token.ThrowIfCancellationRequested();
            _folderLauncher.Launch(null);
            return await result.Task.WaitAsync(token);
        }
        finally
        {
            _folderResult = null;
        }
    }

    private sealed class FolderResult(Action<AndroidUri?> picked) : Java.Lang.Object, IActivityResultCallback
    {
        /// <summary>Completes the pending folder selection with the returned URI.</summary>
        public void OnActivityResult(Java.Lang.Object? result) => picked(result as AndroidUri);
    }

    private sealed class ScreenStateReceiver(Action<bool> screenChanged) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == Intent.ActionScreenOn) screenChanged(true);
            else if (intent?.Action == Intent.ActionScreenOff) screenChanged(false);
        }
    }
}
