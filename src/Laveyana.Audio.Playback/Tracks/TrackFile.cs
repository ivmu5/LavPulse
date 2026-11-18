namespace Laveyana.Audio.Playback;

public sealed record TrackFile(string Name, Func<Stream, CancellationToken, Task> CopyToAsync, string? LocalPath = null)
{
    /// <summary>Checks the extension without opening a file or depending on case.</summary>
    public static bool IsMp3(string name)
        => string.Equals(Path.GetExtension(name), ".mp3", StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates a selection whose backing stream can be opened lazily.</summary>
    public static TrackFile FromStream(string name, Func<Task<Stream>> open)
        => new(name, async (destination, token) =>
        {
            await using Stream source = await open().ConfigureAwait(false);
            await source.CopyToAsync(destination, token).ConfigureAwait(false);
        });

    /// <summary>Creates a selection for a local path usable by playback and desktop drag.</summary>
    public static TrackFile FromPath(string path)
    {
        var file = FromStream(Path.GetFileName(path), () => Task.FromResult<Stream>(File.OpenRead(path)));
        return file with { LocalPath = path };
    }
}

public sealed record TrackFolder(string Name, IReadOnlyList<TrackFile> Files)
{
    /// <summary>Sorts the selected folder's tracks by their user-visible names.</summary>
    public static TrackFolder FromFiles(string name, IEnumerable<TrackFile> files)
        => new(name, files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
}
