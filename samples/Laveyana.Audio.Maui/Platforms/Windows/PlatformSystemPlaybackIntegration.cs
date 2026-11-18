using System.Diagnostics;
using Laveyana.Audio.Playback;
using Windows.Media;
using Windows.Storage.Streams;

namespace Laveyana.Audio.Maui;

/// <summary>Exposes the already-rendered WaveOut master to the Windows transport flyout.</summary>
internal sealed class PlatformSystemPlaybackIntegration : ISystemPlaybackIntegration
{
    private SystemMediaTransportControls? _controls;
    private CachedTrack? _lastTrack;
    private InMemoryRandomAccessStream? _artStream;
    private int _artVersion;

    public event Action<SystemPlaybackCommand>? CommandReceived;

    public void EnsureRunning() => Initialize();

    private void Initialize()
    {
        if (_controls is not null) return;
        var native = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (native is null) return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
            _controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _controls.ButtonPressed += ButtonPressed;
            _controls.IsEnabled = true;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"LavPulse Windows media controls: {e}");
            _controls = null;
        }
    }

    public void Publish(SystemPlaybackState state)
    {
        Initialize();
        if (_controls is not { } controls) return;
        try
        {
            controls.IsPlayEnabled = true;
            controls.IsPauseEnabled = true;
            controls.IsNextEnabled = state.CanNext;
            controls.IsPreviousEnabled = state.CanPrevious;
            if (!ReferenceEquals(_lastTrack, state.Track))
            {
                _lastTrack = state.Track;
                var updater = controls.DisplayUpdater;
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = state.Track.Title;
                updater.MusicProperties.Artist = state.Track.Artist ?? "";
                updater.Update();
                _ = PublishArtworkAsync(state.Track, ++_artVersion);
            }

            controls.PlaybackStatus = state.Playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromMilliseconds(state.DurationMilliseconds),
                Position = TimeSpan.FromMilliseconds(Math.Clamp(state.PositionMilliseconds, 0, state.DurationMilliseconds))
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine($"LavPulse Windows media update: {e}");
        }
    }

    /// <summary>Loads the current cover into SMTC without blocking the playback callback.</summary>
    private async Task PublishArtworkAsync(CachedTrack track, int revision)
    {
        if (track.Artwork.IsEmpty || _controls is null) return;
        InMemoryRandomAccessStream? stream = null;
        try
        {
            stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(track.Artwork.ToArray());
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            if (revision != _artVersion || !ReferenceEquals(_lastTrack, track) || _controls is null) return;
            var prior = _artStream;
            _artStream = stream;
            stream = null;
            _controls.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(_artStream);
            _controls.DisplayUpdater.Update();
            prior?.Dispose();
        }
        catch (Exception e) { Debug.WriteLine($"LavPulse cover: {e}"); }
        finally { stream?.Dispose(); }
    }

    private void ButtonPressed(SystemMediaTransportControls sender,
        SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => SystemPlaybackCommand.Play,
            SystemMediaTransportControlsButton.Pause => SystemPlaybackCommand.Pause,
            SystemMediaTransportControlsButton.Next => SystemPlaybackCommand.Next,
            SystemMediaTransportControlsButton.Previous => SystemPlaybackCommand.Previous,
            _ => (SystemPlaybackCommand?)null
        };
        if (command.HasValue) CommandReceived?.Invoke(command.Value);
    }

    public void Stop()
    {
        _lastTrack = null;
        _artVersion++;
        if (_controls is not { } controls) return;
        controls.ButtonPressed -= ButtonPressed;
        controls.PlaybackStatus = MediaPlaybackStatus.Closed;
        controls.IsEnabled = false;
        _controls = null;
        _artStream?.Dispose();
        _artStream = null;
    }

    public void Dispose() => Stop();
}
