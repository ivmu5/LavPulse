using Laveyana.Audio.Playback;
using Android.Content;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace Laveyana.Audio.Maui.Tracks;

internal static class PlatformTrackFolder
{
    /// <summary>Opens the native folder picker and enumerates the selected document tree.</summary>
    public static async Task<TrackFolder?> PickAsync(Window owner, CancellationToken token)
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity
            ?? throw new InvalidOperationException("Окно LavPulse ещё не готово.");
        AndroidUri? tree = await activity.PickTrackTreeAsync(token);
        if (tree is null)
        {
            return null;
        }

        var resolver = Android.App.Application.Context.ContentResolver
            ?? throw new InvalidOperationException("Android не открыл доступ к папке.");
        return await Task.Run(() => ReadFolder(resolver, tree, token), token);
    }

    /// <summary>Reads file names and builds stream openers for MP3 documents.</summary>
    private static TrackFolder ReadFolder(ContentResolver resolver, AndroidUri tree, CancellationToken token)
    {
        string rootId = DocumentsContract.GetTreeDocumentId(tree)
            ?? throw new IOException("Не удалось определить выбранную папку.");
        AndroidUri root = DocumentsContract.BuildDocumentUriUsingTree(tree, rootId)
            ?? throw new IOException("Не удалось открыть выбранную папку.");

        const string displayName = DocumentsContract.Document.ColumnDisplayName;
        const string documentId = DocumentsContract.Document.ColumnDocumentId;
        const string mimeType = DocumentsContract.Document.ColumnMimeType;
        using var rootName = resolver.Query(root, [displayName], null, null, null);
        string folderName = rootName?.MoveToFirst() == true
            ? rootName.GetString(0) ?? "Выбранная папка" : "Выбранная папка";

        AndroidUri children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, rootId)
            ?? throw new IOException("Не удалось получить список файлов.");
        using var cursor = resolver.Query(children, [documentId, displayName, mimeType], null, null, null)
            ?? throw new IOException("Android не вернул список файлов.");
        var files = new List<TrackFile>();
        while (cursor.MoveToNext())
        {
            token.ThrowIfCancellationRequested();
            string? id = cursor.GetString(0);
            string? name = cursor.GetString(1);
            if (id is null || name is null || !TrackFile.IsMp3(name) ||
                cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir)
            {
                continue;
            }

            if (DocumentsContract.BuildDocumentUriUsingTree(tree, id) is { } uri)
            {
                files.Add(TrackFile.FromStream(name, () => Task.FromResult(
                    resolver.OpenInputStream(uri) ?? throw new IOException($"Android не открыл {name}."))));
            }
        }

        return TrackFolder.FromFiles(folderName, files);
    }
}
