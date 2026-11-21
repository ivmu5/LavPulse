using Foundation;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

internal static class PlatformTrackActivation
{
    internal static bool Open(NSUrl url)
    {
        if (!url.IsFileUrl || url.Path is not { } path || !TrackFile.IsMp3(path)) return false;

        var file = TrackFile.FromStream(Path.GetFileName(path), () =>
            Task.FromResult(PlatformTrackFolder.OpenScopedFile(url, path))) with
        {
            LocalPath = path
        };
        SystemTrackRequests.Open([file]);
        return true;
    }
}
