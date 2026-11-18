using Android.App;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidUri = Android.Net.Uri;
using Android.Content.PM;

namespace Laveyana.Audio.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private ActivityResultLauncher? _folderLauncher;
    private TaskCompletionSource<AndroidUri?>? _folderResult;

    /// <summary>Initializes the Android activity, media volume controls and system folder picker.</summary>
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        VolumeControlStream = Android.Media.Stream.Music;
        _folderLauncher = RegisterForActivityResult(new ActivityResultContracts.OpenDocumentTree(),
            new FolderResult(result => _folderResult?.TrySetResult(result)));
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
}
