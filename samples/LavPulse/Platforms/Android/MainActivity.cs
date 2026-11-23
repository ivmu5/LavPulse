using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.Core.View;
using Laveyana.LavPulse.Tracks;
using AndroidUri = Android.Net.Uri;

namespace Laveyana.LavPulse;

// Keep the launcher component registered by previous versions after the assembly rename.
[Activity(Name = "crc645d4c19df811ab518.MainActivity", Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault], DataSchemes = ["content", "file"],
    DataMimeTypes = ["audio/mpeg", "audio/mp3", "audio/x-mp3"])]
public class MainActivity : MauiAppCompatActivity
{
    private ActivityResultLauncher? _folderLauncher;
    private ActivityResultLauncher? _reportSaveLauncher;
    private TaskCompletionSource<AndroidUri?>? _reportSaveResult;
    private TaskCompletionSource<AndroidUri?>? _folderResult;
    private ScreenStateReceiver? _screenReceiver;
    private bool? _hideStatusBar;
    private bool _resumed;

    internal bool IsPlayerVisible { get; private set; }
    internal event Action<bool>? PlayerVisibilityChanged;
    internal event Action? DisplayClockRefreshRequested;

    /// <summary>Initializes the Android activity, media volume controls and system folder picker.</summary>
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        VolumeControlStream = Android.Media.Stream.Music;
        _folderLauncher = RegisterForActivityResult(new ActivityResultContracts.OpenDocumentTree(),
            new FolderResult(result => _folderResult?.TrySetResult(result)));
        _reportSaveLauncher = RegisterForActivityResult(new ActivityResultContracts.CreateDocument("application/zip"),
            new FolderResult(result => _reportSaveResult?.TrySetResult(result)));
        if (savedInstanceState is null) AndroidTrackActivation.Receive(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        AndroidTrackActivation.Receive(intent);
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

        UpdatePlayerVisibility(IsScreenInteractive);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _resumed = true;
        UpdatePlayerVisibility(IsScreenInteractive);
    }

    protected override void OnPause()
    {
        _resumed = false;
        UpdatePlayerVisibility(false);
        base.OnPause();
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

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (!hasFocus) return;
        if (_hideStatusBar is { } hide && GetInsetsController() is { } controller)
            ApplyStatusBarVisibility(controller, hide);
        UpdatePlayerVisibility(IsScreenInteractive);
        if (IsPlayerVisible) DisplayClockRefreshRequested?.Invoke();
    }

    internal void ApplySystemBarAppearance(bool light, bool hideStatusBar)
    {
        _hideStatusBar = hideStatusBar;
        if (GetInsetsController() is not { } controller) return;
        controller.AppearanceLightStatusBars = light;
        controller.AppearanceLightNavigationBars = light;
        ApplyStatusBarVisibility(controller, hideStatusBar);
    }

    private WindowInsetsControllerCompat? GetInsetsController() =>
        Window is { } window ? WindowCompat.GetInsetsController(window, window.DecorView) : null;

    private static void ApplyStatusBarVisibility(WindowInsetsControllerCompat controller, bool hide)
    {
        int statusBars = WindowInsetsCompat.Type.StatusBars();
        controller.SystemBarsBehavior = hide ? WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe
            : WindowInsetsControllerCompat.BehaviorDefault;
        if (hide) controller.Hide(statusBars);
        else controller.Show(statusBars);
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

    private bool IsScreenInteractive => GetSystemService(PowerService) is not PowerManager { IsInteractive: false };

    private void UpdatePlayerVisibility(bool screenInteractive)
    {
        bool visible = _resumed && _screenReceiver is not null && screenInteractive;
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

    /// <summary>Opens the native system document picker to save a ZIP in shared storage or SD.</summary>
    internal async Task<AndroidUri?> PickReportDocumentAsync(string suggestedName, CancellationToken token)
    {
        if (_reportSaveLauncher is null || _reportSaveResult is not null)
            throw new InvalidOperationException("Экспорт ZIP сейчас недоступен.");
        var result = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reportSaveResult = result;
        try
        {
            token.ThrowIfCancellationRequested();
            _reportSaveLauncher.Launch(suggestedName);
            return await result.Task.WaitAsync(token);
        }
        finally { _reportSaveResult = null; }
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
