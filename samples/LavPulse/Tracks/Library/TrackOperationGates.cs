using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Serializes work by actual local storage path, including files
/// reconstructed independently by the library and the playback queue.</summary>
internal sealed class TrackOperationGates
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _paths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Dictionary<TrackFile, Entry> _files = new(ReferenceEqualityComparer.Instance);

    internal int ActiveKeys { get { lock (_gate) return _paths.Count + _files.Count; } }

    public async ValueTask<IDisposable> AcquireAsync(TrackFile file, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(file);
        token.ThrowIfCancellationRequested();
        string? path = file.LocalPath is { } local ? Path.GetFullPath(local) : null;
        Entry entry;
        lock (_gate)
        {
            if (path is not null)
            {
                if (!_paths.TryGetValue(path, out entry!)) _paths.Add(path, entry = new());
            }
            else if (!_files.TryGetValue(file, out entry!)) _files.Add(file, entry = new());
            entry.Users++;
        }

        bool acquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            token.ThrowIfCancellationRequested();
            return new Lease(this, file, path, entry);
        }
        catch
        {
            Release(file, path, entry, acquired);
            throw;
        }
    }

    private void Release(TrackFile file, string? path, Entry entry, bool acquired)
    {
        if (acquired) entry.Semaphore.Release();
        lock (_gate)
        {
            if (--entry.Users != 0) return;
            if (path is null) _files.Remove(file);
            else _paths.Remove(path);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1);
        public int Users;
    }

    private sealed class Lease(TrackOperationGates owner, TrackFile file, string? path, Entry entry) : IDisposable
    {
        private TrackOperationGates? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(file, path, entry, acquired: true);
    }
}
