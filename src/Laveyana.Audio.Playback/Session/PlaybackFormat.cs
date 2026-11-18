namespace Laveyana.Audio.Playback;

/// <summary>Defines the common sample format used by the cache, mixer and output devices.</summary>
public static class PlaybackFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = PcmMixer.ChannelCount;
    public const int Pcm16FrameBytes = Channels * sizeof(short);
    public const int BlockFrames = 1024;
    public const int SpectrumBars = 56;
}

/// <summary>Identifies an independently controllable input deck.</summary>
public enum Deck { A, B }
