namespace Laveyana.Audio.Playback;

internal sealed class MixedPlayback : IAudioRenderSource, ISpectrumSource
{
    private const int SpectrumWindow = 2048;
    private const int SpectrumHistorySize = 192;

    private readonly DeckVolumes? _levels;
    private readonly TransportFadeSource? _queueOutput;
    private readonly CachedPcmSource? _deckA;
    private readonly CachedPcmSource? _deckB;
    private readonly PcmInput? _inputA;
    private readonly PcmInput? _inputB;
    private readonly PcmMixer _mixer;
    private readonly object _spectrumGate = new();
    private readonly float[] _spectrum = new float[SpectrumHistorySize * PlaybackFormat.SpectrumBars];
    private readonly long[] _spectrumCenters = new long[SpectrumHistorySize];
    private long _spectrumVersion;
    private long _minimumSpectrumVersion;
    private int _analysisGeneration;
    private TaskCompletionSource? _resumeAnalysis;
    private long _outputPosition = long.MaxValue;
    private long _visualDelayFrames;
    private readonly Func<QueueSnapshot>? _snapshotProvider;
    private readonly AudioPresentationHistory? _presentation;

    public MixedPlayback(CachedPcmSource? deckA, CachedPcmSource? deckB, DeckVolumes levels)
    {
        _deckA = deckA;
        _deckB = deckB;
        _levels = levels;
        var inputs = new List<PcmInput>(2);
        if (deckA is not null)
        {
            _inputA = new PcmInput(deckA, levels.Get(Deck.A));
            inputs.Add(_inputA);
        }

        if (deckB is not null)
        {
            _inputB = new PcmInput(deckB, levels.Get(Deck.B));
            inputs.Add(_inputB);
        }

        _mixer = new PcmMixer(inputs, PlaybackFormat.SpectrumBars, SpectrumWindow);
    }

    /// <summary>Analyzes the finished queue mix with the same spectrum pipeline as standalone decks.</summary>
    public MixedPlayback(IPcmSource queue, int startMilliseconds,
        QueueSnapshot initialSnapshot, Func<QueueSnapshot> getSnapshot)
    {
        _queueOutput = new TransportFadeSource(queue, startMilliseconds);
        _mixer = new PcmMixer([new PcmInput(_queueOutput, 1)], PlaybackFormat.SpectrumBars, SpectrumWindow);
        _snapshotProvider = getSnapshot;
        _presentation = new AudioPresentationHistory(initialSnapshot);
    }

    /// <summary>Returns the decks at the audio-output head, not at the next rendered block.</summary>
    public QueueSnapshot PresentedSnapshot => _presentation?.Read() ?? default;

    /// <summary>Adds an optional user-calibrated delay beyond the platform output counter.</summary>
    public void SetVisualDelay(int milliseconds)
    {
        long frames = PlaybackFormat.SampleRate * (long)Math.Clamp(milliseconds, 0, 1500) / 1000;
        Volatile.Write(ref _visualDelayFrames, frames);
        _presentation?.SetDelay(frames);
    }

    /// <summary>Starts a fade and lets the device drain normally once the fade is complete.</summary>
    public bool RequestPause(int milliseconds) => _queueOutput?.RequestPause(milliseconds) == true;

    /// <summary>Updates deck gains, mixes the next block and returns its real frame count.</summary>
    public int Render(Span<float> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length > PlaybackFormat.BlockFrames * PlaybackFormat.Channels || buffer.Length % PlaybackFormat.Channels != 0)
        {
            throw new ArgumentException("Ожидался буфер из полных стереокадров до размера блока.", nameof(buffer));
        }

        if (_inputA is not null)
        {
            _inputA.Gain = _levels!.Get(Deck.A);
        }

        if (_inputB is not null)
        {
            _inputB.Gain = _levels!.Get(Deck.B);
        }

        _mixer.Render(buffer);
        int frames = _queueOutput?.LastReadFrames ?? Math.Max(_deckA?.LastReadFrames ?? 0, _deckB?.LastReadFrames ?? 0);
        if (frames > 0 && _presentation is { } clock && _snapshotProvider is { } provider)
            clock.Append(frames, provider());
        return frames;
    }

    // Audio backends that expose a playback head can align drawings with output.
    /// <summary>Publishes the device position for synchronizing spectrum presentation.</summary>
    public void ReportOutputPosition(long playedFrames)
    {
        Volatile.Write(ref _outputPosition, playedFrames);
        _presentation?.ReportOutputPosition(playedFrames);
    }

    public void SetSpectrumEnabled(bool enabled)
    {
        lock (_spectrumGate)
        {
            if (enabled == (_resumeAnalysis is null)) return;

            _mixer.Spectrum.SetEnabled(enabled);
            _minimumSpectrumVersion = _spectrumVersion;
            Interlocked.Increment(ref _analysisGeneration);
            if (enabled)
            {
                _resumeAnalysis!.TrySetResult();
                _resumeAnalysis = null;
            }
            else
            {
                _resumeAnalysis = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>Processes FFT snapshots off the audio writer and sleeps while visualization is off.</summary>
    public async Task RunSpectrumAsync(CancellationToken token)
    {
        var levels = new float[PlaybackFormat.SpectrumBars];
        while (!token.IsCancellationRequested)
        {
            int generation;
            Task? resume;
            lock (_spectrumGate)
            {
                generation = _analysisGeneration;
                resume = _resumeAnalysis?.Task;
            }
            if (resume is not null)
            {
                await resume.WaitAsync(token).ConfigureAwait(false);
                continue;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _analysisGeneration) != generation) break;
                if (!_mixer.Spectrum.TryReadLevels(levels, out long endFrame)) continue;

                lock (_spectrumGate)
                {
                    if (_analysisGeneration != generation) break;

                    int slot = (int)(_spectrumVersion % SpectrumHistorySize);
                    levels.CopyTo(_spectrum, slot * PlaybackFormat.SpectrumBars);
                    _spectrumCenters[slot] = endFrame - SpectrumWindow / 2;
                    _spectrumVersion++;
                }
            }
        }
    }

    /// <summary>Presents the newest analyzed window whose center has reached the audio output.</summary>
    public bool TryReadSpectrum(Span<float> destination, ref long version)
    {
        long played = _presentation?.PresentedFrame ?? Volatile.Read(ref _outputPosition);
        if (_presentation is null && played != long.MaxValue)
            played = Math.Max(0, played - Volatile.Read(ref _visualDelayFrames));
        lock (_spectrumGate)
        {
            if (_resumeAnalysis is not null) return false;
            long oldest = Math.Max(Math.Max(version, _minimumSpectrumVersion), _spectrumVersion - SpectrumHistorySize);
            if (oldest >= _spectrumVersion) return false;
            long low = oldest + 1, high = _spectrumVersion;
            int first = (int)((low - 1) % SpectrumHistorySize);
            if (_spectrumCenters[first] > played) return false;
            // FFT centers increase with rendered frames, including after capture resumes.
            while (low < high)
            {
                long mid = low + (high - low + 1) / 2;
                if (_spectrumCenters[(int)((mid - 1) % SpectrumHistorySize)] <= played) low = mid;
                else high = mid - 1;
            }
            int slot = (int)((low - 1) % SpectrumHistorySize);
            _spectrum.AsSpan(slot * PlaybackFormat.SpectrumBars, PlaybackFormat.SpectrumBars).CopyTo(destination);
            version = low;
            return true;
        }
    }
}
