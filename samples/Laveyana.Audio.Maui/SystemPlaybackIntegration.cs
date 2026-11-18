using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Commands accepted by both the MAUI transport and the system media controls.</summary>
internal enum SystemPlaybackCommand { Play, Pause, Next, Previous }

/// <summary>A snapshot of the actual master playback, not a second media player.</summary>
internal readonly record struct SystemPlaybackState(
    CachedTrack Track, long PositionFrames, bool Playing, bool CanNext, bool CanPrevious)
{
    public long PositionMilliseconds => Math.Max(0, PositionFrames * 1000 / PlaybackFormat.SampleRate);
    public long DurationMilliseconds => Math.Max(0, Track.Waveform.TotalFrames * 1000 / PlaybackFormat.SampleRate);
}

/// <summary>Publishes playback information and relays OS media button commands.</summary>
internal interface ISystemPlaybackIntegration : IDisposable
{
    event Action<SystemPlaybackCommand>? CommandReceived;
    void Publish(SystemPlaybackState state);
    void EnsureRunning();
    void Stop();
}
