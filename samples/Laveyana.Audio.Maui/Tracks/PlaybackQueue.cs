using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Maintains one ordered playlist with playback anchored to a track, even when it moves.</summary>
internal sealed class PlaybackQueue
{
    private readonly List<TrackFile> _tracks = [];
    private int? _detachedIndex;
    private int NextIndex => _detachedIndex ?? (Current is null ? 0 : PlayedCount + 1);

    public IReadOnlyList<TrackFile> Tracks => _tracks;
    public TrackFile? Current { get; private set; }
    public int PlayedCount => _detachedIndex ?? (Current is { } track ? Math.Max(0, _tracks.IndexOf(track)) : 0);
    public TrackFile? Next => At(NextIndex);
    public TrackFile? Previous => At(PlayedCount - 1);

    public void AddRange(IEnumerable<TrackFile> files)
    {
        foreach (var file in files)
        {
            if (Current == file && _detachedIndex is int index)
            {
                _tracks.Insert(index, file);
                _detachedIndex = null;
            }
            else _tracks.Add(file);
        }
    }
    public void Remove(TrackFile file) => _tracks.Remove(file);
    public void SetCurrent(TrackFile file)
    {
        Current = file;
        _detachedIndex = null;
    }

    public bool IsBeforeCurrent(TrackFile file)
    {
        int index = _tracks.IndexOf(file);
        return index >= 0 && index < PlayedCount;
    }

    /// <summary>Moves an explicitly chosen upcoming track directly after the active track.</summary>
    public void SelectNext(TrackFile file)
    {
        int index = _tracks.IndexOf(file);
        if (index >= 0) RemoveAt(index);
        _tracks.Insert(NextIndex, file);
    }

    /// <summary>Removes a row. Removing the active row keeps its successor anchored while the track plays out.</summary>
    public bool TryRemove(TrackFile file, Func<TrackFile?, bool> changeNext)
    {
        int index = _tracks.IndexOf(file);
        if (index < 0) return false;
        var previousNext = Next;
        var previousBoundary = _detachedIndex;
        RemoveAt(index);
        if (Current == file) _detachedIndex = index;
        if (ReferenceEquals(previousNext, Next) || changeNext(Next)) return true;

        _tracks.Insert(index, file);
        _detachedIndex = previousBoundary;
        return false;
    }

    /// <summary>Moves any row without restarting playback. Commits only if a changed successor can be armed.</summary>
    public bool Move(TrackFile from, TrackFile to, bool after, Func<TrackFile?, bool> changeNext)
    {
        int source = _tracks.IndexOf(from);
        int target = _tracks.IndexOf(to);
        if (source < 0 || target < 0 || source == target)
        {
            return false;
        }
        int position = target + (after ? 1 : 0) - (source < target ? 1 : 0);
        if (position == source)
        {
            return false;
        }

        var previousNext = Next;
        var previousBoundary = _detachedIndex;
        RemoveAt(source);
        _tracks.Insert(position, from);
        if (_detachedIndex is int anchor &&
            (position < anchor || position == anchor && target < previousBoundary))
        {
            _detachedIndex = anchor + 1;
        }
        var next = Next;
        if (ReferenceEquals(previousNext, next) || changeNext(next))
        {
            return true;
        }

        _tracks.RemoveAt(position);
        _tracks.Insert(source, from);
        _detachedIndex = previousBoundary;
        return false;
    }

    private void RemoveAt(int index)
    {
        _tracks.RemoveAt(index);
        if (_detachedIndex is int anchor && index < anchor) _detachedIndex = anchor - 1;
    }

    private TrackFile? At(int index) => (uint)index < (uint)_tracks.Count ? _tracks[index] : null;
}
