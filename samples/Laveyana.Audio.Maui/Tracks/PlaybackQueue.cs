using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Maintains one ordered playlist with playback anchored to a track, even when it moves.</summary>
internal sealed class PlaybackQueue
{
    private readonly List<TrackFile> _tracks = [];
    private int CurrentIndex => Current is { } track ? _tracks.IndexOf(track) : -1;

    public IReadOnlyList<TrackFile> Tracks => _tracks;
    public TrackFile? Current { get; private set; }
    public TrackFile? Next => At(CurrentIndex + 1);
    public TrackFile? Previous => CurrentIndex is > 0 and var index ? _tracks[index - 1] : null;

    public void AddRange(IEnumerable<TrackFile> files) => _tracks.AddRange(files);
    public void Remove(TrackFile file) => _tracks.Remove(file);
    public void SetCurrent(TrackFile file) => Current = file;

    public bool IsBeforeCurrent(TrackFile file)
    {
        int index = _tracks.IndexOf(file);
        return index >= 0 && index < CurrentIndex;
    }

    /// <summary>Moves an explicitly chosen upcoming track directly after the active track.</summary>
    public void SelectNext(TrackFile file)
    {
        _tracks.Remove(file);
        _tracks.Insert(CurrentIndex + 1, file);
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
        _tracks.RemoveAt(source);
        _tracks.Insert(position, from);
        var next = Next;
        if (ReferenceEquals(previousNext, next) || changeNext(next))
        {
            return true;
        }

        _tracks.RemoveAt(position);
        _tracks.Insert(source, from);
        return false;
    }

    private TrackFile? At(int index) => (uint)index < (uint)_tracks.Count ? _tracks[index] : null;
}
