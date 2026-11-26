namespace Laveyana.Audio.Playback;

public sealed record TrackFile(string Name, Func<Stream, CancellationToken, Task> CopyToAsync, string? LocalPath = null)
{
    /// <summary>The compressed file size in bytes when supplied by the track source.</summary>
    public long? SizeBytes { get; init; }
    internal Func<Task<Stream>>? OpenReadAsync { get; init; }
    private Func<Stream, CancellationToken, Task>? StreamingCopy { get; init; }
    internal Func<Task<Stream>>? OpenCopySource => CopyToAsync == StreamingCopy ? OpenReadAsync : null;

    /// <summary>Checks the extension without opening a file or depending on case.</summary>
    public static bool IsMp3(string name)
        => string.Equals(Path.GetExtension(name), ".mp3", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] PortableExtensions = [".mp3", ".wav", ".ogg", ".oga"];
    private static readonly string[] NativeExtensions = [".flac", ".aac", ".m4a"];
    private static readonly string[] AllExtensions = [.. PortableExtensions, .. NativeExtensions, ".opus"];
    private static readonly IReadOnlyList<string> CurrentExtensions = Array.AsReadOnly(
        OperatingSystem.IsAndroid() ? AllExtensions :
        OperatingSystem.IsWindows() ? new string[] { ".mp3", ".wav", ".ogg", ".oga", ".flac", ".aac", ".m4a" } : PortableExtensions);

    /// <summary>Importable extensions on this runtime. Containers and actual decoder
    /// capabilities are validated during preparation, not by the filename.</summary>
    public static IReadOnlyList<string> SupportedExtensions => CurrentExtensions;
    public static bool IsSupportedAudio(string name) =>
        CurrentExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    public static bool IsKnownAudio(string name) =>
        AllExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> StorageExtensions => AllExtensions;
    internal static string StorageExtension(string name)
    {
        string extension = Path.GetExtension(name).ToLowerInvariant();
        if (!AllExtensions.Contains(extension)) throw new InvalidDataException("Неподдерживаемое расширение аудио.");
        return extension == ".oga" ? ".ogg" : extension;
    }

    /// <summary>Creates a selection whose backing stream can be opened lazily.</summary>
    public static TrackFile FromStream(string name, Func<Task<Stream>> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(open);
        async Task CopyAsync(Stream destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await using Stream source = await open().ConfigureAwait(false);
            await source.CopyToAsync(destination, token).ConfigureAwait(false);
        }

        Func<Stream, CancellationToken, Task> copy = CopyAsync;
        return new TrackFile(name, copy)
        {
            OpenReadAsync = open,
            StreamingCopy = copy
        };
    }

    /// <summary>Creates a selection for a local path usable by playback and desktop drag.</summary>
    public static TrackFile FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = FromStream(Path.GetFileName(path), () => Task.FromResult<Stream>(File.OpenRead(path)));
        return file with { LocalPath = path };
    }
}

public sealed record TrackFolder(string Name, IReadOnlyList<TrackFile> Files)
{
    public IReadOnlyList<TrackFolderSource> Children { get; init; } = [];

    /// <summary>Sorts the selected folder's tracks by their user-visible names.</summary>
    public static TrackFolder FromFiles(string name, IEnumerable<TrackFile> files)
        => new(name, files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
}

/// <summary>A folder whose immediate contents can be read without traversing its subfolders.</summary>
public sealed record TrackFolderSource(string Id, string Name, Func<CancellationToken, Task<TrackFolder>> ReadAsync);
