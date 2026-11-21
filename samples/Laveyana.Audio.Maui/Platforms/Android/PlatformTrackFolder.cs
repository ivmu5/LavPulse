using Laveyana.Audio.Playback;
using Android.Content;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace Laveyana.Audio.Maui.Tracks;

internal static class PlatformTrackFolder
{
    /// <summary>Opens the native folder picker and enumerates the selected document tree.</summary>
    public static async Task<TrackFolderSource?> PickAsync(Window owner, CancellationToken token)
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
        return await Task.Run(() => OpenTree(resolver, tree), token);
    }

    /// <summary>Reads file names and builds stream openers for MP3 documents.</summary>
    private static TrackFolderSource OpenTree(ContentResolver resolver, AndroidUri tree)
    {
        string rootId = DocumentsContract.GetTreeDocumentId(tree)
            ?? throw new IOException("Не удалось определить выбранную папку.");
        AndroidUri root = DocumentsContract.BuildDocumentUriUsingTree(tree, rootId)
            ?? throw new IOException("Не удалось открыть выбранную папку.");

        using var rootName = resolver.Query(root, [DocumentsContract.Document.ColumnDisplayName], null, null, null);
        string name = rootName?.MoveToFirst() == true
            ? rootName.GetString(0) ?? "Выбранная папка" : "Выбранная папка";
        return OpenFolder(resolver, tree, root, rootId, name);
    }

    private static TrackFolderSource OpenFolder(ContentResolver resolver, AndroidUri tree, AndroidUri document,
        string id, string name) => new($"{document.Authority}:{id}", name, token => Task.Run(() =>
            ReadFolder(resolver, tree, id, name, token), token));

    private static TrackFolder ReadFolder(ContentResolver resolver, AndroidUri tree, string folderId, string folderName,
        CancellationToken token)
    {
        const string displayName = DocumentsContract.Document.ColumnDisplayName;
        const string documentId = DocumentsContract.Document.ColumnDocumentId;
        const string mimeType = DocumentsContract.Document.ColumnMimeType;
        AndroidUri children = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, folderId)
            ?? throw new IOException("Не удалось получить список файлов.");
        using var cursor = resolver.Query(children, [documentId, displayName, mimeType,
            DocumentsContract.Document.ColumnSize], null, null, null)
            ?? throw new IOException("Android не вернул список файлов.");
        int sizeColumn = cursor.GetColumnIndex(DocumentsContract.Document.ColumnSize);
        var files = new List<TrackFile>();
        var folders = new List<TrackFolderSource>();
        while (cursor.MoveToNext())
        {
            token.ThrowIfCancellationRequested();
            string? id = cursor.GetString(0);
            string? name = cursor.GetString(1);
            if (id is null || name is null || DocumentsContract.BuildDocumentUriUsingTree(tree, id) is not { } uri)
                continue;

            if (cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir)
                folders.Add(OpenFolder(resolver, tree, uri, id, name));
            else if (TrackFile.IsMp3(name))
            {
                long? size = sizeColumn >= 0 && !cursor.IsNull(sizeColumn) ? cursor.GetLong(sizeColumn) : null;
                files.Add(TrackFile.FromStream(name, () => Task.FromResult(
                    resolver.OpenInputStream(uri) ?? throw new IOException($"Android не открыл {name}.")))
                    with { SizeBytes = size is >= 0 ? size : null });
            }
        }

        return TrackFolder.FromFiles(folderName, files) with
        {
            Children = folders.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
        };
    }
}
