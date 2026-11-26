namespace Laveyana.Audio.Playback;

/// <summary>Owns the audio sources and the spectrum reader for one playback run.</summary>
/// <remarks>The caller keeps the cached tracks alive until <see cref="RunAsync"/> completes.</remarks>
public sealed class PlaybackSession
{
    private readonly IAudioOutput _output;
    private readonly CachedTrack? _trackA;
    private readonly CachedTrack? _trackB;
    private readonly DeckCursor? _cursorA;
    private readonly DeckCursor? _cursorB;
    private readonly DeckVolumes _volumes;
    private MixedPlayback? _mix;
    private int _started;

    /// <summary>Creates independent cursors for the selected tracks and their starting positions.</summary>
    public PlaybackSession(IAudioOutput output, DeckVolumes volumes,
        CachedTrack? trackA, long cueA, CachedTrack? trackB, long cueB)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(volumes);
        if (trackA is null && trackB is null)
        {
            throw new ArgumentException("At least one prepared track is required.");
        }

        _output = output;
        _volumes = volumes;
        _trackA = trackA;
        _trackB = trackB;
        _cursorA = CreateCursor(trackA, cueA);
        _cursorB = CreateCursor(trackB, cueB);
    }

    /// <summary>Gets the spectrum published by the mixer after its sources have opened.</summary>
    public ISpectrumSource? Spectrum => Volatile.Read(ref _mix);

    /// <summary>Returns the rendered or pending seek position for the requested deck.</summary>
    public long GetPosition(Deck deck) => GetCursor(deck)?.Position ?? 0;

    /// <summary>Queues a seek on just the selected deck for the next render block.</summary>
    public void Seek(Deck deck, long frame) => GetCursor(deck)?.Seek(frame);

    /// <summary>Starts one background playback run and releases its streams when playback ends.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("A playback session can only run once.");
        }

        return Task.Run(() => RunWorkerAsync(cancellationToken), cancellationToken);
    }

    /// <summary>Starts the device and stops its associated analyzer before disposing the PCM sources.</summary>
    private async Task RunWorkerAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var audioA = _trackA is null ? null : new CachedPcmSource(_trackA.Path, _cursorA!);
        using var audioB = _trackB is null ? null : new CachedPcmSource(_trackB.Path, _cursorB!);
        var playback = new MixedPlayback(audioA, audioB, _volumes);
        Volatile.Write(ref _mix, playback);

        using var spectrumStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var spectrum = playback.RunSpectrumAsync(spectrumStop.Token);
        try
        {
            await _output.PlayAsync(playback, token).ConfigureAwait(false);
        }
        finally
        {
            spectrumStop.Cancel();
            try
            {
                await spectrum.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (spectrumStop.IsCancellationRequested)
            {
                // The spectrum reader ends with the native output.
            }
            finally
            {
                // Clear the published mixer even when its analyzer fails unexpectedly.
                Volatile.Write(ref _mix, null);
            }
        }
    }

    /// <summary>Clamps the initial cue to the track before opening a reader.</summary>
    private static DeckCursor? CreateCursor(CachedTrack? track, long cue) => track is null
        ? null
        : new DeckCursor(track.Waveform.TotalFrames, Math.Clamp(cue, 0, track.Waveform.TotalFrames));

    /// <summary>Maps a deck to its own worker cursor.</summary>
    private DeckCursor? GetCursor(Deck deck) => deck switch
    {
        Deck.A => _cursorA, Deck.B => _cursorB, _ => throw new ArgumentOutOfRangeException(nameof(deck))
    };
}
