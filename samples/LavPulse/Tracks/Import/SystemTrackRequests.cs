using System.Collections.Concurrent;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Holds native file activations until the player window can accept them.</summary>
internal static class SystemTrackRequests
{
    private static readonly ConcurrentQueue<IReadOnlyList<TrackFile>> Pending = new();

    internal static event Action? Received;
    internal static bool HasPending => !Pending.IsEmpty;
    internal static bool TryTake(out IReadOnlyList<TrackFile>? files) => Pending.TryDequeue(out files);

    internal static void Open(IReadOnlyList<TrackFile> files)
    {
        if (files.Count != 0) Pending.Enqueue(files);
        Received?.Invoke();
    }
}
