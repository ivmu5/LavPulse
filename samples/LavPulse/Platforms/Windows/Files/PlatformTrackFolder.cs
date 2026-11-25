using Laveyana.Audio.Playback;
using Windows.Storage.Pickers;

namespace Laveyana.LavPulse.Tracks;

internal static class PlatformTrackFolder
{
    /// <summary>Initializes the folder picker with the desktop window handle and lists its MP3 files.</summary>
    public static async Task<TrackFolderSource?> PickAsync(Window owner, CancellationToken token)
    {
        var native = owner.Handler?.PlatformView as Microsoft.UI.Xaml.Window
            ?? throw new InvalidOperationException("Окно LavPulse ещё не готово.");
        var picker = new FolderPicker();
        foreach (string extension in TrackFile.SupportedExtensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(native));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return null;
        }

        token.ThrowIfCancellationRequested();
        return TrackFolderReader.FromDirectory(folder.Path, folder.DisplayName, TrackFile.FromPath);
    }
}
