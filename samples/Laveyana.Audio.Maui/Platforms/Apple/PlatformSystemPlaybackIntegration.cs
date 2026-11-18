namespace Laveyana.Audio.Maui;

/// <summary>Apple media controls require a separate AVAudioSession integration.</summary>
internal sealed class PlatformSystemPlaybackIntegration : ISystemPlaybackIntegration
{
    public event Action<SystemPlaybackCommand>? CommandReceived { add { } remove { } }
    public void Publish(SystemPlaybackState state) { }
    public void EnsureRunning() { }
    public void Stop() { }
    public void Dispose() { }
}
