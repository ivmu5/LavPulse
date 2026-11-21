using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

internal static class TrackFolderReader
{
    public static TrackFolderSource FromDirectory(string path, string name, Func<string, TrackFile> open,
        Func<Func<TrackFolder>, TrackFolder>? readScoped = null)
    {
        return new TrackFolderSource(path, name, token => Task.Run(() =>
            readScoped is null ? Read(token) : readScoped(() => Read(token)), token));

        TrackFolder Read(CancellationToken token)
        {
            var files = new List<TrackFile>();
            var folders = new List<TrackFolderSource>();
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
            {
                token.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo)
                    folders.Add(FromDirectory(entry.FullName, entry.Name, open, readScoped));
                else if (TrackFile.IsMp3(entry.Name))
                    files.Add(open(entry.FullName));
            }
            return TrackFolder.FromFiles(name, files) with
            {
                Children = folders.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()
            };
        }
    }

    public static async Task<IReadOnlyList<FolderTrack>> ReadTracksAsync(TrackFolderSource root, CancellationToken token,
        IReadOnlyList<TrackFolderSource>? parents = null)
    {
        var tracks = new List<FolderTrack>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(TrackFolderSource Folder, StoredFolder[] Path)>();
        var path = parents?.Select(folder => new StoredFolder(folder.Id, folder.Name)) ?? [];
        pending.Push((root, [.. path, new(root.Id, root.Name)]));
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(item.Folder.Id)) continue;
            var contents = await item.Folder.ReadAsync(token).ConfigureAwait(false);
            foreach (var file in contents.Files)
                tracks.Add(new FolderTrack(file, item.Path));
            for (int i = contents.Children.Count - 1; i >= 0; i--)
            {
                var child = contents.Children[i];
                pending.Push((child, [.. item.Path, new StoredFolder(child.Id, child.Name)]));
            }
        }
        return tracks;
    }
}

internal readonly record struct FolderTrack(TrackFile File, StoredFolder[] Folders);
