using System.Diagnostics;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

/// <summary>Lightweight results in completion order, then active tracks.
/// No visual elements or decoded artwork are retained by this model.</summary>
internal sealed class TrackScanHistory
{
    internal sealed class Entry(TrackFile file)
    {
        public TrackFile File { get; } = file;
        public TrackScanResult? Result { get; set; }
    }

    private readonly List<Entry> _items = new();
    private readonly Dictionary<TrackFile, Entry> _active = new(ReferenceEqualityComparer.Instance);
    public IReadOnlyList<Entry> Items => _items;
    public int CompletedCount { get; private set; }
    public int ActiveCount => _active.Count;

    public void AddActive(IReadOnlyList<TrackFile> files)
    {
        foreach (var file in files)
        {
            if (_active.ContainsKey(file)) continue;
            var entry = new Entry(file);
            _active.Add(file, entry);
            _items.Add(entry);
        }
    }

    public void Complete(TrackScanResult result)
    {
        if (_active.Remove(result.File, out var entry))
        {
            for (int i = CompletedCount; i < _items.Count; i++)
                if (ReferenceEquals(_items[i], entry)) { _items.RemoveAt(i); break; }
        }
        else entry = new Entry(result.File);
        entry.Result = result;
        _items.Insert(CompletedCount++, entry);
    }

    public void Clear()
    {
        _items.Clear();
        _active.Clear();
        CompletedCount = 0;
    }

    // Zero keeps the manually chosen history position until a new scan.
    internal static bool CanFollow(long? interaction, long now, int seconds) =>
        interaction is null || seconds > 0 &&
        Stopwatch.GetElapsedTime(interaction.Value, now) >= TimeSpan.FromSeconds(seconds);
}
