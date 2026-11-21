using Laveyana.Audio.Playback;
using Foundation;
using UIKit;
using UniformTypeIdentifiers;

namespace Laveyana.LavPulse.Tracks;

internal static class PlatformTrackFolder
{
    /// <summary>Opens the document picker and resolves the selected security-scoped directory.</summary>
    public static async Task<TrackFolderSource?> PickAsync(Window owner, CancellationToken token)
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
            return folder is null ? null : OpenFolder(folder);
        }
        finally
        {
            if (token.IsCancellationRequested && picker.PresentingViewController is not null)
            {
                picker.DismissViewController(false, null);
            }
        }
    }

    private static TrackFolderSource OpenFolder(NSUrl folder)
    {
        string path = folder.Path ?? throw new IOException("Не удалось прочитать путь папки.");
        return TrackFolderReader.FromDirectory(path, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            file => TrackFile.FromStream(Path.GetFileName(file), () => Task.FromResult(OpenScopedFile(folder, file))),
            read =>
            {
                bool scoped = folder.StartAccessingSecurityScopedResource();
                try { return read(); }
                finally { if (scoped) folder.StopAccessingSecurityScopedResource(); }
            });
    }

    /// <summary>Holds the folder's security grant for the lifetime of the seekable file.</summary>
    internal static Stream OpenScopedFile(NSUrl url, string path)
    {
        bool scoped = url.StartAccessingSecurityScopedResource();
        try
        {
            return new ScopedFileStream(path, scoped ? url : null);
        }
        catch
        {
            if (scoped) url.StopAccessingSecurityScopedResource();
            throw;
        }
    }

    private sealed class ScopedFileStream(string path, NSUrl? scope) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        private NSUrl? _scope = scope;

        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { Interlocked.Exchange(ref _scope, null)?.StopAccessingSecurityScopedResource(); }
        }

        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync().ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _scope, null)?.StopAccessingSecurityScopedResource(); }
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
