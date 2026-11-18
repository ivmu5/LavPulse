namespace Laveyana.Audio.Playback;

internal sealed class MixedPlayback : IAudioRenderSource, ISpectrumSource
{
    private const int SpectrumWindow = 2048;
    private const int SpectrumHistorySize = 64;

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
    private long _outputPosition = long.MaxValue;

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
    public MixedPlayback(IPcmSource queue, int startMilliseconds)
    {
        _queueOutput = new TransportFadeSource(queue, startMilliseconds);
        _mixer = new PcmMixer([new PcmInput(_queueOutput, 1)], PlaybackFormat.SpectrumBars, SpectrumWindow);
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
        return _queueOutput?.LastReadFrames ?? Math.Max(_deckA?.LastReadFrames ?? 0, _deckB?.LastReadFrames ?? 0);
    }

    // Audio backends that expose a playback head can align drawings with output.
    /// <summary>Publishes the device position for synchronizing spectrum presentation.</summary>
    public void ReportOutputPosition(long playedFrames) => Volatile.Write(ref _outputPosition, playedFrames);

    /// <summary>Processes FFT snapshots off the audio writer and records their center frames.</summary>
    public async Task RunSpectrumAsync(CancellationToken token)
    {
        var levels = new float[PlaybackFormat.SpectrumBars];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (!_mixer.Spectrum.TryReadLevels(levels, out long endFrame))
            {
                continue;
            }

            lock (_spectrumGate)
            {
                int slot = (int)(_spectrumVersion % SpectrumHistorySize);
                levels.CopyTo(_spectrum, slot * PlaybackFormat.SpectrumBars);
                _spectrumCenters[slot] = endFrame - SpectrumWindow / 2;
                _spectrumVersion++;
            }
        }
    }

    /// <summary>Presents the newest analyzed window whose center has reached the audio output.</summary>
    public bool TryReadSpectrum(Span<float> destination, ref long version)
    {
        long played = Volatile.Read(ref _outputPosition);
        lock (_spectrumGate)
        {
            long oldest = Math.Max(version, _spectrumVersion - SpectrumHistorySize);
            for (long snapshot = _spectrumVersion; snapshot > oldest; snapshot--)
            {
                int slot = (int)((snapshot - 1) % SpectrumHistorySize);
                if (_spectrumCenters[slot] <= played)
                {
                    _spectrum.AsSpan(slot * PlaybackFormat.SpectrumBars, PlaybackFormat.SpectrumBars).CopyTo(destination);
                    version = snapshot;
                    return true;
                }
            }
        }

        return false;
    }
}
