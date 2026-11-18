using Laveyana.Audio.Playback;
using Foundation;
using UIKit;
using UniformTypeIdentifiers;

namespace Laveyana.Audio.Maui.Tracks;

internal static class PlatformTrackFolder
{
    /// <summary>Opens the document picker and resolves the selected security-scoped directory.</summary>
    public static async Task<TrackFolder?> PickAsync(Window owner, CancellationToken token)
    {
        var parent = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController()
            ?? throw new InvalidOperationException("Окно LavPulse ещё не готово.");
        using var picker = new UIDocumentPickerViewController([UTTypes.Folder], asCopy: false);
        using var result = new FolderResult();
        picker.Delegate = result;
        parent.PresentViewController(picker, true, null);
        try
        {
            NSUrl? folder = await result.Selection.Task.WaitAsync(token);
            return folder is null ? null : await Task.Run(() => ReadFolder(folder, token), token);
        }
        finally
        {
            if (token.IsCancellationRequested && picker.PresentingViewController is not null)
            {
                picker.DismissViewController(false, null);
            }
        }
    }

    /// <summary>Enumerates MP3 files inside the security-scoped folder.</summary>
    private static TrackFolder ReadFolder(NSUrl folder, CancellationToken token)
    {
        bool scoped = folder.StartAccessingSecurityScopedResource();
        try
        {
            string path = folder.Path ?? throw new IOException("Не удалось прочитать путь папки.");
            var tracks = new List<TrackFile>();
            foreach (string file in Directory.EnumerateFiles(path))
            {
                token.ThrowIfCancellationRequested();
                if (TrackFile.IsMp3(file))
                {
                    tracks.Add(new TrackFile(Path.GetFileName(file),
                        (destination, copyToken) => CopyFromFolderAsync(folder, file, destination, copyToken)));
                }
            }

            return TrackFolder.FromFiles(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), tracks);
        }
        finally
        {
            if (scoped)
            {
                folder.StopAccessingSecurityScopedResource();
            }
        }
    }

    /// <summary>Opens the stream while access to the security-scoped folder is granted.</summary>
    private static async Task CopyFromFolderAsync(NSUrl folder, string path, Stream destination, CancellationToken token)
    {
        bool scoped = folder.StartAccessingSecurityScopedResource();
        try
        {
            await using Stream source = File.OpenRead(path);
            await source.CopyToAsync(destination, token).ConfigureAwait(false);
        }
        finally
        {
            if (scoped)
            {
                folder.StopAccessingSecurityScopedResource();
            }
        }
    }

    private sealed class FolderResult : UIDocumentPickerDelegate
    {
        public TaskCompletionSource<NSUrl?> Selection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes the pending picker task with the selected folder URL.</summary>
        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls)
            => Selection.TrySetResult(urls.FirstOrDefault());

        /// <summary>Completes the pending picker task without a folder.</summary>
        public override void WasCancelled(UIDocumentPickerViewController controller)
            => Selection.TrySetResult(null);
    }
}
