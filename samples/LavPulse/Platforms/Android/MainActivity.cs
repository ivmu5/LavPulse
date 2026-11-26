using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using Laveyana.Maui.UI.Android.Display;
using Laveyana.Maui.UI.Android.Windowing;
using Laveyana.LavPulse.Tracks;
using AndroidUri = Android.Net.Uri;

namespace Laveyana.LavPulse;

// Keep the launcher component registered by previous versions after the assembly rename.
[Activity(Name = "crc645d4c19df811ab518.MainActivity", Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault], DataSchemes = ["content", "file"],
    DataMimeTypes = ["audio/mpeg", "audio/mp3", "audio/x-mp3", "audio/wav", "audio/x-wav",
        "audio/wave", "audio/ogg", "application/ogg", "audio/flac", "audio/x-flac", "audio/aac",
        "audio/aacp", "audio/mp4", "audio/x-m4a", "audio/opus", "audio/mpeg3", "audio/x-mpeg",
        "audio/vnd.wave", "audio/x-pn-wav", "audio/x-ogg", "audio/vorbis", "audio/x-vorbis",
        "audio/x-aac", "audio/m4a", "audio/x-opus"])]
public class MainActivity : MauiAppCompatActivity
{
    private ActivityResultLauncher? _folderLauncher;
    private ActivityResultLauncher? _reportSaveLauncher;
    private TaskCompletionSource<AndroidUri?>? _reportSaveResult;
    private TaskCompletionSource<AndroidUri?>? _folderResult;
    private UiAndroidScreenMonitor? _screenMonitor;
    private bool? _hideStatusBar;
    private bool _lightSystemBars;

    internal bool IsPlayerVisible => _screenMonitor?.State.CanRender == true;
    internal event Action<bool>? PlayerVisibilityChanged;
    internal event Action? DisplayClockRefreshRequested;

    /// <summary>Initializes the Android activity, media volume controls and system folder picker.</summary>
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _screenMonitor = new UiAndroidScreenMonitor(this);
        _screenMonitor.State.RenderAvailabilityChanged += OnRenderAvailabilityChanged;
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
        _screenMonitor?.Start();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _screenMonitor?.SetResumed(true);
    }

    protected override void OnPause()
    {
        _screenMonitor?.SetResumed(false);
        base.OnPause();
    }

    protected override void OnStop()
    {
        _screenMonitor?.Stop();
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        if (_screenMonitor is { } monitor)
        {
            monitor.Dispose();
            monitor.State.RenderAvailabilityChanged -= OnRenderAvailabilityChanged;
            _screenMonitor = null;
        }
        base.OnDestroy();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (!hasFocus) return;
        if (_hideStatusBar is { } hide)
            UiAndroidWindow.ApplySystemBars(this, _lightSystemBars, hideStatusBar: hide);
        if (_screenMonitor is { } monitor)
            monitor.SetResumed(monitor.State.IsActive);
        if (IsPlayerVisible) DisplayClockRefreshRequested?.Invoke();
    }

    internal void ApplySystemBarAppearance(bool light, bool hideStatusBar)
    {
        _lightSystemBars = light;
        _hideStatusBar = hideStatusBar;
        UiAndroidWindow.ApplySystemBars(this, light, hideStatusBar: hideStatusBar);
    }

    /// <summary>Allows the already open player to remain visible above the keyguard.</summary>
    internal void SetPlayerOnLockScreen(bool enabled) =>
        UiAndroidWindow.SetShowWhenLocked(this, enabled);

    private void OnRenderAvailabilityChanged(bool visible) =>
        PlayerVisibilityChanged?.Invoke(visible);

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

}
