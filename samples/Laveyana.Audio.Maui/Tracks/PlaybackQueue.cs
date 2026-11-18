using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Tracks;

/// <summary>Maintains upcoming order and a reversible boundary between played and upcoming tracks.</summary>
internal sealed class PlaybackQueue
{
    private readonly List<TrackFile> _upcoming = [];
    private readonly List<TrackFile> _played = [];

    public IReadOnlyList<TrackFile> Upcoming => _upcoming;
    public IReadOnlyList<TrackFile> Played => _played;
    public TrackFile? Next => _upcoming.FirstOrDefault();

    public void AddRange(IEnumerable<TrackFile> files) => _upcoming.AddRange(files);
    public void Remove(TrackFile file) => _upcoming.Remove(file);

    /// <summary>Makes a selected track next without changing the order of the other tracks.</summary>
    public void Select(TrackFile file)
    {
        _upcoming.Remove(file);
        _upcoming.Insert(0, file);
    }

    public void MarkPlayed(TrackFile file)
    {
        _played.Remove(file);
        _played.Add(file);
    }

    /// <summary>Requeues the selected history entry and all later entries in listening order.</summary>
    public bool ReplayFrom(TrackFile file)
    {
        int from = _played.IndexOf(file);
        if (from < 0)
        {
            return false;
        }
        var replay = _played.GetRange(from, _played.Count - from);
        _played.RemoveRange(from, replay.Count);
        foreach (var replayed in replay)
        {
            _upcoming.Remove(replayed);
        }
        _upcoming.InsertRange(0, replay);
        return true;
    }

    /// <summary>Moves an upcoming row only after the player agrees to replace the next PCM reader.</summary>
    public bool Move(TrackFile from, TrackFile to, bool after, Func<TrackFile, bool> changeNext)
    {
        int source = _upcoming.IndexOf(from);
        int target = _upcoming.IndexOf(to);
        if (source < 0 || target < 0 || source == target)
        {
            return false;
        }
        int position = target + (after ? 1 : 0) - (source < target ? 1 : 0);
        if (position == source)
        {
            return false;
        }
        if (position == 0 && !changeNext(from) || source == 0 && !changeNext(_upcoming[1]))
        {
            return false;
        }
        _upcoming.RemoveAt(source);
        _upcoming.Insert(position, from);
        return true;
    }
}
