namespace Laveyana.Audio.Playback;

/// <summary>Keeps the playback queue and deck cursors across repeated starts of the native audio output.</summary>
public sealed class QueuePlaybackSession : IDisposable
{
    public const int DefaultFadeMilliseconds = 300;

    private readonly QueueSource _queue = new();
    private readonly MasterEqualizerSource _equalizer;
    private readonly object _spectrumSettings = new();
    private bool _spectrumEnabled = true;
    private MixedPlayback? _mix;
    private int _running;
    private int _visualDelayMilliseconds;

    public QueuePlaybackSession() => _equalizer = new(_queue);

    public QueueSnapshot Snapshot => _queue.Snapshot;

    /// <summary>Closes retired readers on the control thread before evicting PCM files.</summary>
    public void ReleaseRetiredReaders() => _queue.DrainRetired();
    /// <summary>Presentation-time snapshot; use for drawing only, never for audio transport decisions.</summary>
    public QueueSnapshot PresentedSnapshot => Volatile.Read(ref _mix)?.PresentedSnapshot ?? _queue.Snapshot;
    public ISpectrumSource? Spectrum => Volatile.Read(ref _mix);

    /// <summary>Sets the initial track if the playback queue is empty.</summary>
    public void SetFirst(CachedTrack track) => _queue.SetFirst(track);

    /// <summary>Sets the next prepared track without disturbing the playing deck.</summary>
    public bool SetNext(CachedTrack? track) => _queue.SetNext(track);

    /// <summary>Changes the successor only while the expected track is active and no mix has been requested.</summary>
    public bool SetNext(CachedTrack? track, CachedTrack expectedCurrent) => _queue.SetNext(track, expectedCurrent);

    /// <summary>Starts a transition on the next rendered block.</summary>
    public bool RequestMix() => _queue.RequestMix();

    /// <summary>Configures whether to mix into the next track at the end of the current one.</summary>
    public void SetAutoMix(bool enabled) => _queue.SetAutoMix(enabled);

    /// <summary>Applies the measured per-deck gain, with a short render-owned ramp on changes.</summary>
    public void SetLoudnessNormalization(bool enabled) => _queue.SetLoudnessNormalization(enabled);

    /// <summary>Repeats the current track at its end while allowing a manual track change.</summary>
    public void SetRepeatOne(bool enabled) => _queue.SetRepeatOne(enabled);

    /// <summary>Changes the master sound profile for output and spectrum analysis together.</summary>
    public void SetEqualizer(EqualizerSettings settings) => _equalizer.Configure(settings);

    /// <summary>Enables spectrum analysis for the active and subsequent output runs.</summary>
    public void SetSpectrumEnabled(bool enabled)
    {
        lock (_spectrumSettings)
        {
            _spectrumEnabled = enabled;
            Volatile.Read(ref _mix)?.SetSpectrumEnabled(enabled);
        }
    }

    /// <summary>Allows the queue to supply the next track, without interrupting a mix already under way.</summary>
    public void SetQueueEnabled(bool enabled) => _queue.SetQueueEnabled(enabled);

    /// <summary>Sets the maximum transition time. Zero switches at the end or when requested.</summary>
    public void SetCrossfadeDuration(int milliseconds) => _queue.SetCrossfadeDuration(milliseconds);

    /// <summary>Delays spectrum and presented deck state without touching audio playback.</summary>
    public void SetVisualDelay(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, 1500);
        Volatile.Write(ref _visualDelayMilliseconds, milliseconds);
        Volatile.Read(ref _mix)?.SetVisualDelay(milliseconds);
    }

    /// <summary>Sets playback speed by resampling the source PCM, which also changes pitch.</summary>
    public bool TrySetSpeed(CachedTrack track, double rate) => _queue.TrySetSpeed(track, rate);

    /// <summary>Queues a seek if the track is still current and outside a transition.</summary>
    public bool Seek(CachedTrack track, long frame) => _queue.TrySeek(track, frame);

    /// <summary>Fades the current output to silence while preserving the deck cursors for resume.</summary>
    public bool RequestPause(int milliseconds = DefaultFadeMilliseconds) => Volatile.Read(ref _mix)?.RequestPause(milliseconds) == true;

    /// <summary>Runs the native output and the spectrum reader until paused or the queue stops.</summary>
    public Task RunAsync(IAudioOutput output, CancellationToken token) =>
        RunAsync(output, DefaultFadeMilliseconds, token);

    public async Task RunAsync(IAudioOutput output, int startMilliseconds, CancellationToken token)
    {
        var mix = new MixedPlayback(_equalizer, startMilliseconds, _queue.Snapshot, () => _queue.Snapshot);
        mix.SetVisualDelay(Volatile.Read(ref _visualDelayMilliseconds));
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("The audio output is already running.");
        }

        _equalizer.Reset();
        using var stopSpectrum = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_spectrumSettings)
        {
            mix.SetSpectrumEnabled(_spectrumEnabled);
            Volatile.Write(ref _mix, mix);
        }
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
