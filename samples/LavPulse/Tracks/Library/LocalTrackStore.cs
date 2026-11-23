using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Owns the imported MP3 copies, queue manifest and versioned analysis data.</summary>
internal sealed partial class LocalTrackStore(string root)
{
    internal const long PcmBudgetBytes = 256L * 1024 * 1024;
    private readonly ConcurrentDictionary<string, byte> _analyzed = new();
    private readonly Dictionary<(string Id, string Name, string Folder), StoredLibraryTrack> _library = new();
    private readonly SemaphoreSlim _analysisWrites = new(1);
    private string MediaDirectory => Path.Combine(root, "media");
    private string AnalysisDirectory => Path.Combine(root, "analysis-v5");
    private string QueuePath => Path.Combine(root, "queue.json");
    public string PcmDirectory => Path.Combine(root, "pcm-v1");
    public event Action<string>? TrackStorageChanged;

    public async Task<TrackFile> CopyAsync(TrackFile file, CancellationToken token)
    {
        Directory.CreateDirectory(MediaDirectory);
        string temp = Path.Combine(MediaDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            string id;
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                id = Convert.ToHexString(await CopyAndHashAsync(file, stream, token).ConfigureAwait(false));
            }

            token.ThrowIfCancellationRequested();
            string path = MediaPath(id);
            if (!File.Exists(path))
            {
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            return TrackFile.FromPath(path) with { Name = file.Name };
        }
        finally
        {
            CachedTrack.DeleteCacheFile(temp);
        }
    }

    private static async Task<byte[]> CopyAndHashAsync(TrackFile file, FileStream output, CancellationToken token)
    {
        // Custom copy delegates still get a seekable destination and are hashed after writing.
        if (file.OpenCopySource is not { } open)
        {
            await file.CopyToAsync(output, token).ConfigureAwait(false);
            output.Position = 0;
            return await SHA256.HashDataAsync(output, token).ConfigureAwait(false);
        }

        await using var source = await open().ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
            }
            return hash.GetHashAndReset();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task<(TrackFile[] Tracks, TrackFile? Current)> ReadQueueAsync(CancellationToken token)
    {
        // Version 5 stores independent tempo sections and per-window confidence diagnostics.
        // MP3s, queue and PCM cache remain intact; only previous analysis manifests are removed.
        foreach (string version in new[] { "analysis-v1", "analysis-v2", "analysis-v3", "analysis-v4" })
        {
            try { Directory.Delete(Path.Combine(root, version), recursive: true); }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var manifest = await ReadAsync(QueuePath, TrackStoreJson.Default.StoredQueue, token).ConfigureAwait(false);
        foreach (var entry in manifest?.Library ?? manifest?.Tracks?.Select(track => new StoredLibraryTrack(track, [])).ToArray() ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (entry?.Track is not { } track || RestoreTrack(track) is not { } file ||
                entry.Folders is null || entry.Folders.Any(folder => string.IsNullOrEmpty(folder?.Id) || string.IsNullOrEmpty(folder.Name)))
                continue;
            _library.TryAdd((track.Id, track.Name, FolderKey(entry.Folders)), entry);
            if (File.Exists(AnalysisPath(file))) _analyzed[file.LocalPath!] = 0;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TrackFile>();
        TrackFile? current = null;
        foreach (var entry in manifest?.Tracks ?? [])
        {
            if (entry is null || RestoreTrack(entry) is not { } file || !seen.Add(entry.Id)) continue;
            result.Add(file);
            if (entry.Id == manifest!.Current) current = file;
            if (File.Exists(AnalysisPath(file))) _analyzed[file.LocalPath!] = 0;
        }
        string? activePcm = null;
        if ((current ?? result.FirstOrDefault()) is { } active &&
            await ReadAnalysisAsync(active, token).ConfigureAwait(false) is { } analysis)
        {
            activePcm = FindPcm(analysis);
        }
        PrunePcm(activePcm, token);
        return (result.ToArray(), current);
    }

    public Task SaveQueueAsync(StoredQueue queue) => WriteAsync(QueuePath, queue,
        TrackStoreJson.Default.StoredQueue, CancellationToken.None);

    public StoredQueue Snapshot(IReadOnlyList<TrackFile> tracks, TrackFile? current) =>
        new(tracks.Select(file => new StoredTrack(Id(file), file.Name)).ToArray(),
            current is null ? null : Id(current), _library.Values.ToArray());

    public bool Remember(TrackFile file, StoredFolder[] folders)
    {
        var track = new StoredTrack(Id(file), file.Name);
        if (File.Exists(AnalysisPath(file))) _analyzed.TryAdd(file.LocalPath!, 0);
        return _library.TryAdd((track.Id, track.Name, FolderKey(folders)), new(track, folders));
    }

    public int ForgetLibraryItem(string folderId, TrackFile? file)
    {
        if (file is not null)
            return _library.Remove((Id(file), file.Name, folderId)) ? 1 : 0;
        if (folderId.Length == 0) return 0;

        var entries = _library.Keys.Where(key => key.Folder.StartsWith(folderId, StringComparison.Ordinal)).ToArray();
        foreach (var entry in entries) _library.Remove(entry);
        return entries.Length;
    }

    public StoredLibraryTrack[] LibrarySnapshot() => _library.Values.ToArray();

    public TrackFolderSource OpenLibrary(IReadOnlyList<StoredLibraryTrack> entries)
    {
        var root = new LibraryFolder("", "Локальная медиатека");
        foreach (var entry in entries)
        {
            if (RestoreTrack(entry.Track) is not { } file) continue;
            var folder = root;
            foreach (var segment in entry.Folders)
            {
                if (!folder.Children.TryGetValue(segment.Id, out var child))
                    folder.Children.Add(segment.Id, child = new(folder.Id + FolderPart(segment.Id), segment.Name));
                folder = child;
            }
            folder.Files.Add(file);
        }
        return root.AsSource();
    }

    private TrackFile? RestoreTrack(StoredTrack track)
    {
        if (!ValidId(track.Id) || string.IsNullOrEmpty(track.Name) || !TrackFile.IsMp3(track.Name)) return null;
        string path = MediaPath(track.Id);
        var info = new FileInfo(path);
        return info.Exists ? TrackFile.FromPath(path) with { Name = track.Name, SizeBytes = info.Length } : null;
    }

    private static string FolderKey(IEnumerable<StoredFolder> folders) =>
        string.Concat(folders.Select(folder => FolderPart(folder.Id)));

    private static string FolderPart(string id) => $"{id.Length}:{id}";

    private sealed class LibraryFolder(string id, string name)
    {
        private Task<TrackFolder>? _listing;
        public string Id { get; } = id;
        private string Name { get; } = name;
        public List<TrackFile> Files { get; } = [];
        public Dictionary<string, LibraryFolder> Children { get; } = new(StringComparer.Ordinal);

        public TrackFolderSource AsSource() => new(Id, Name, ReadAsync);

        private Task<TrackFolder> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // The catalog snapshot is complete before AsSource is called. Size refreshes
            // and folder expansion can share the same sorted listing.
            return Volatile.Read(ref _listing) ?? LazyInitializer.EnsureInitialized(ref _listing, CreateListing);
        }

        private Task<TrackFolder> CreateListing() =>
            Task.FromResult(new TrackFolder(Name, Array.AsReadOnly(
                Files.OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()))
            {
                Children = Array.AsReadOnly(Children.Values.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(folder => folder.AsSource()).ToArray())
            });
    }

    private string MediaPath(string id) => Path.Combine(MediaDirectory, id + ".mp3");
    private string AnalysisPath(TrackFile file) => Path.Combine(AnalysisDirectory, Id(file) + ".json");
    private static string Id(TrackFile file) => Path.GetFileNameWithoutExtension(file.LocalPath!);
    private static bool ValidId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigitUpper);

    private static async Task<T?> ReadAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken token) where T : class
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                16 * 1024, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync(stream, type, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteAsync<T>(string path, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, type, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { CachedTrack.DeleteCacheFile(temporary); }
    }
}
