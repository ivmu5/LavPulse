namespace Laveyana.Audio.Playback;

/// <summary>Describes the encoded audio before it is converted to the mixer format.</summary>
public sealed record TrackAudioInfo(
    string Codec, int SampleRate, int BitrateKbps, bool IsVariableBitrate, TrackChannelMode ChannelMode);

public enum TrackChannelMode
{
    Stereo,
    JointStereo,
    DualChannel,
    Mono,
    Mixed
}
