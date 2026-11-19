using AVFoundation;
using Foundation;
using Laveyana.Audio.Playback;
using MediaPlayer;
using UIKit;

namespace Laveyana.Audio.Maui;

/// <summary>Connects the mixer to Now Playing and the system's media transport.</summary>
internal sealed class PlatformSystemPlaybackIntegration : ISystemPlaybackIntegration
{
    private readonly MPRemoteCommandCenter _commands = MPRemoteCommandCenter.Shared;
    private readonly List<(MPRemoteCommand Command, NSObject Token)> _targets = [];
    private readonly NSObject _interruptions;
    private readonly NSObject _routeChanges;
    private CachedTrack? _track;
    private MPMediaItemArtwork? _artwork;
    private bool _playing;

    public PlatformSystemPlaybackIntegration()
    {
        Add(_commands.PlayCommand, () => SystemPlaybackCommand.Play);
        Add(_commands.PauseCommand, () => SystemPlaybackCommand.Pause);
        Add(_commands.TogglePlayPauseCommand, () => Volatile.Read(ref _playing) ? SystemPlaybackCommand.Pause : SystemPlaybackCommand.Play);
        Add(_commands.NextTrackCommand, () => SystemPlaybackCommand.Next);
        Add(_commands.PreviousTrackCommand, () => SystemPlaybackCommand.Previous);

        _interruptions = AVAudioSession.Notifications.ObserveInterruption((_, args) =>
        {
            if (args.InterruptionType == AVAudioSessionInterruptionType.Began)
                CommandReceived?.Invoke(SystemPlaybackCommand.Interrupted);
        });
        _routeChanges = AVAudioSession.Notifications.ObserveRouteChange((_, args) =>
        {
            if (args.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable)
                CommandReceived?.Invoke(SystemPlaybackCommand.Interrupted);
        });
    }

    public event Action<SystemPlaybackCommand>? CommandReceived;

    public void Publish(SystemPlaybackState state)
    {
        if (!ReferenceEquals(_track, state.Track))
        {
            _track = state.Track;
            _artwork = ReadArtwork(state.Track);
        }

        Volatile.Write(ref _playing, state.Playing);
        _commands.PlayCommand.Enabled = !state.Playing;
        _commands.PauseCommand.Enabled = state.Playing;
        _commands.TogglePlayPauseCommand.Enabled = true;
        _commands.NextTrackCommand.Enabled = state.CanNext;
        _commands.PreviousTrackCommand.Enabled = state.CanPrevious;
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo
        {
            Title = state.Track.Title,
            Artist = state.Track.Artist,
            Artwork = _artwork,
            PlaybackDuration = state.DurationMilliseconds / 1000d,
            ElapsedPlaybackTime = Math.Clamp(state.PositionMilliseconds, 0, state.DurationMilliseconds) / 1000d,
            PlaybackRate = state.Playing ? state.Speed : 0
        };
    }

    // The audio output activates AVAudioSession when rendering begins.
    public void EnsureRunning() { }

    public void Stop()
    {
        foreach (var (command, _) in _targets) command.Enabled = false;
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null!;
        Volatile.Write(ref _playing, false);
        _track = null;
        _artwork = null;
    }

    public void Dispose()
    {
        Stop();
        foreach (var (command, target) in _targets)
        {
            command.RemoveTarget(target);
            target.Dispose();
        }
        _targets.Clear();
        _interruptions.Dispose();
        _routeChanges.Dispose();
    }

    private void Add(MPRemoteCommand command, Func<SystemPlaybackCommand> resolve)
    {
        command.Enabled = false;
        var token = command.AddTarget(_ =>
        {
            if (CommandReceived is not { } handler) return MPRemoteCommandHandlerStatus.NoSuchContent;
            handler(resolve());
            return MPRemoteCommandHandlerStatus.Success;
        });
        _targets.Add((command, token));
    }

    private static MPMediaItemArtwork? ReadArtwork(CachedTrack track)
    {
        if (track.Artwork.IsEmpty) return null;
        using var data = NSData.FromArray(track.Artwork.ToArray());
        var image = UIImage.LoadFromData(data);
        return image is null ? null : new MPMediaItemArtwork(image.Size, _ => image);
    }
}
