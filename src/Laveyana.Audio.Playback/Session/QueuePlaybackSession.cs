namespace Laveyana.Audio.Playback;

/// <summary>Keeps the playback queue and deck cursors across repeated starts of the native audio output.</summary>
public sealed class QueuePlaybackSession : IDisposable
{
    private readonly QueueSource _queue = new();
    private MixedPlayback? _mix;
    private int _running;

    public QueueSnapshot Snapshot => _queue.Snapshot;
    public ISpectrumSource? Spectrum => Volatile.Read(ref _mix);

    /// <summary>Sets the initial track if the playback queue is empty.</summary>
    public void SetFirst(CachedTrack track) => _queue.SetFirst(track);

    /// <summary>Sets the next prepared track without disturbing the playing deck.</summary>
    public bool SetNext(CachedTrack? track) => _queue.SetNext(track);

    /// <summary>Starts a transition on the next rendered block.</summary>
    public bool RequestMix() => _queue.RequestMix();

    /// <summary>Configures whether to mix into the next track at the end of the current one.</summary>
    public void SetAutoMix(bool enabled) => _queue.SetAutoMix(enabled);

    /// <summary>Queues a seek if the track is still current and outside a transition.</summary>
    public bool Seek(CachedTrack track, long frame) => _queue.TrySeek(track, frame);

    /// <summary>Fades the current output to silence while preserving the deck cursors for resume.</summary>
    public bool RequestPause() => Volatile.Read(ref _mix)?.RequestPause() == true;

    /// <summary>Runs the native output and the spectrum reader until paused or the queue stops.</summary>
    public async Task RunAsync(IAudioOutput output, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("The audio output is already running.");
        }

        var mix = new MixedPlayback(_queue);
        using var stopSpectrum = CancellationTokenSource.CreateLinkedTokenSource(token);
        Volatile.Write(ref _mix, mix);
        var analysis = mix.RunSpectrumAsync(stopSpectrum.Token);
        try
        {
            await Task.Run(() => output.PlayAsync(mix, token), token).ConfigureAwait(false);
        }
        finally
        {
            stopSpectrum.Cancel();
            try
            {
                await analysis.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopSpectrum.IsCancellationRequested)
            {
                // Closing the output also ends analysis.
            }
            finally
            {
                Volatile.Write(ref _mix, null);
                Volatile.Write(ref _running, 0);
            }
        }
    }

    /// <summary>Closes the cache readers. Stop the output before calling this method.</summary>
    public void Dispose() => _queue.Dispose();
}
