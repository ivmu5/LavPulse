namespace Laveyana.Audio.Playback;

/// <summary>Describes a deck's track, position and physical input.</summary>
public readonly record struct PlayingTrack(CachedTrack Track, long Position, Deck Deck, double Speed = 1);

/// <summary>Provides a consistent snapshot of the two decks and their pending transition.</summary>
public readonly record struct QueueSnapshot(PlayingTrack? Current, PlayingTrack? Incoming);

/// <summary>Owns the current and next PCM readers and mixes transitions on the audio timeline.</summary>
internal sealed class QueueSource : IPcmSource, IDisposable
{
    private readonly object _gate = new();
    private readonly float[] _other = new float[PlaybackFormat.BlockFrames * PlaybackFormat.Channels];
    private Voice? _current;
    private Voice? _next;
    private int _fadeLength;
    private int _fadePosition;
    private bool _manual;
    private bool _autoMix = true;
    private bool _queueEnabled = true;
    private int _crossfadeFrames = 6 * PlaybackFormat.SampleRate;

    public int SampleRate => PlaybackFormat.SampleRate;

    /// <summary>Sets the first track and opens its seekable PCM cache.</summary>
    public void SetFirst(CachedTrack track)
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                return;
            }

            _current = new Voice(track, Deck.A);
        }
    }

    /// <summary>Preloads the candidate reader so the audio writer can switch without opening a file.</summary>
    public bool SetNext(CachedTrack? track)
    {
        lock (_gate)
        {
            if (_current is null || _fadeLength != 0 || ReferenceEquals(_current.Track, track))
            {
                return false;
            }

            if (ReferenceEquals(_next?.Track, track))
            {
                return true;
            }

            var replacement = track is null ? null : new Voice(track, _current.Deck == Deck.A ? Deck.B : Deck.A);
            _next?.Dispose();
            _next = replacement;
            _manual = false;
            return true;
        }
    }

    /// <summary>Arms the selected track for a transition on the next audio block.</summary>
    public bool RequestMix()
    {
        lock (_gate)
        {
            if (!_queueEnabled || _next is null || _fadeLength != 0)
            {
                return false;
            }

            _manual = true;
            return true;
        }
    }

    /// <summary>Enables or disables automatic transitions at the tail of a track.</summary>
    public void SetAutoMix(bool enabled)
    {
        lock (_gate)
        {
            _autoMix = enabled;
        }
    }

    /// <summary>Changes which feed can advance the transport without touching the decks in a running mix.</summary>
    public void SetQueueEnabled(bool enabled)
    {
        lock (_gate)
        {
            _queueEnabled = enabled;
            if (!enabled)
            {
                _manual = false;
            }
        }
    }

    /// <summary>Stores the maximum overlap for future transitions.</summary>
    public void SetCrossfadeDuration(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, 12000);
        lock (_gate)
        {
            _crossfadeFrames = PlaybackFormat.SampleRate * milliseconds / 1000;
        }
    }

    /// <summary>Changes the current deck's speed outside a transition, leaving the next deck at its original speed.</summary>
    public bool TrySetSpeed(CachedTrack track, double rate)
    {
        if (!double.IsFinite(rate) || rate < .75 || rate > 1.25)
        {
            throw new ArgumentOutOfRangeException(nameof(rate));
        }

        lock (_gate)
        {
            if (_current is not { } current || !ReferenceEquals(track, current.Track) || _fadeLength != 0)
            {
                return false;
            }

            current.Speed = rate;
            return true;
        }
    }

    /// <summary>Seeks only the current track, outside a transition.</summary>
    public bool TrySeek(CachedTrack track, long frame)
    {
        lock (_gate)
        {
            if (_current is not { } current || !ReferenceEquals(track, current.Track) || _fadeLength != 0)
            {
                return false;
            }

            current.Cursor.Seek(frame);
            return true;
        }
    }

    /// <summary>Reads the deck positions together so the UI can follow the same transition.</summary>
    public QueueSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new(_current?.Snapshot, _fadeLength != 0 ? _next?.Snapshot : null);
            }
        }
    }

    /// <summary>Reads the next output segment and starts the fade at the exact frame, even within a block.</summary>
    public int Read(Span<float> destination)
    {
        lock (_gate)
        {
            int written = 0;
            while (_current is { } current && written < destination.Length / PlaybackFormat.Channels)
            {
                int requested = destination.Length / PlaybackFormat.Channels - written;
                var output = destination[(written * PlaybackFormat.Channels)..];
                long remaining = current.Source.RemainingAtRate(current.Speed);
                if ((_queueEnabled || _fadeLength > 0) && _next is { } next)
                {
                    if (_fadeLength == 0 && (_manual || _autoMix && remaining <= _crossfadeFrames))
                    {
                        _manual = false;
                        int length = (int)Math.Min(_crossfadeFrames,
                            Math.Min(remaining, Math.Max(0, next.Source.RemainingAtRate(next.Speed) - 1)));
                        if (length < 2)
                        {
                            Promote();
                            continue;
                        }

                        _fadeLength = length;
                    }

                    if (_fadeLength > 0)
                    {
                        int frames = Math.Min(requested, _fadeLength - _fadePosition);
                        MixFade(current, next, output[..(frames * PlaybackFormat.Channels)]);
                        written += frames;
                        _fadePosition += frames;
                        if (_fadePosition == _fadeLength)
                        {
                            Promote();
                        }
                        continue;
                    }

                    if (_autoMix)
                    {
                        requested = (int)Math.Min(requested, remaining - _crossfadeFrames);
                    }
                }

                int read = current.Source.ReadAtRate(output[..(requested * PlaybackFormat.Channels)], current.Speed);
                written += read;
                if (read == 0)
                {
                    break;
                }
            }

            return written;
        }
    }

    /// <summary>Applies a mirrored hyperbolic gain curve, normalized to keep the combined gain power constant.</summary>
    private void MixFade(Voice outgoing, Voice incoming, Span<float> buffer)
    {
        var second = _other.AsSpan(0, buffer.Length);
        outgoing.Source.ReadAtRate(buffer, outgoing.Speed);
        incoming.Source.ReadAtRate(second, incoming.Speed);
        for (int frame = 0; frame < buffer.Length / PlaybackFormat.Channels; frame++)
        {
            float progress = (float)(_fadePosition + frame) / (_fadeLength - 1);
            (float enter, float leave) = HyperbolicFade.Gains(progress);
            int index = frame * PlaybackFormat.Channels;
            buffer[index] = buffer[index] * leave + second[index] * enter;
            buffer[index + 1] = buffer[index + 1] * leave + second[index + 1] * enter;
        }
    }

    /// <summary>Transfers ownership of the incoming reader to the current deck.</summary>
    private void Promote()
    {
        _current?.Dispose();
        _current = _next;
        _next = null;
        _fadeLength = _fadePosition = 0;
    }

    /// <summary>Releases the cache readers after the output has stopped.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _next?.Dispose();
            _current = _next = null;
        }
    }

    private sealed class Voice : IDisposable
    {
        public Voice(CachedTrack track, Deck deck)
        {
            Track = track;
            Deck = deck;
            Cursor = new DeckCursor(track.Waveform.TotalFrames, 0);
            Source = new CachedPcmSource(track.Path, Cursor);
        }

        public CachedTrack Track { get; }
        public Deck Deck { get; }
        public DeckCursor Cursor { get; }
        public CachedPcmSource Source { get; }
        public double Speed { get; set; } = 1;
        public PlayingTrack Snapshot => new(Track, Cursor.Position, Deck, Speed);

        public void Dispose() => Source.Dispose();
    }
}
