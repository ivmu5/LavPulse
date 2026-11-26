using System.Numerics;
using System.Collections.Concurrent;

namespace Laveyana.Audio.Playback;

/// <summary>Describes a deck's track, position and physical input.</summary>
public readonly record struct PlayingTrack(CachedTrack Track, long Position, Deck Deck, double Speed = 1);

/// <summary>Provides a consistent snapshot of the two decks and their pending transition.</summary>
public readonly record struct QueueSnapshot(PlayingTrack? Current, PlayingTrack? Incoming)
{
    /// <summary>Indicates that a requested transition is waiting for the next audio block.</summary>
    public bool MixPending { get; init; }

    public bool IsMixing => MixPending || Incoming.HasValue;
}

/// <summary>Single render owner with atomic state publication. Control edits may wait
/// for the current block; the renderer never acquires a control-thread monitor.</summary>
internal sealed class QueueSource : IPcmSource, IDisposable
{
    private struct State
    {
        public Voice? Current, Next;
        public int FadeLength, FadePosition, CrossfadeFrames;
        public bool Manual, AutoMix, RepeatOne, QueueEnabled, Disposed, NormalizeLoudness;
        public double Speed;
        public long? SeekFrame;
    }

    // An immutable publication owns its state value. A render lease prevents
    // control CAS from replacing/disposing a reader while it is being used.
    private sealed class Publication(State state)
    {
        public readonly State State = state;
        public readonly QueueSnapshot Snapshot = new(
            state.Current is { } current ? new PlayingTrack(current.Track,
                state.SeekFrame ?? current.Cursor.Position, current.Deck, state.Speed) : null,
            state.FadeLength != 0 && state.Next is { } next
                ? new PlayingTrack(next.Track, next.Cursor.Position, next.Deck) : null)
        { MixPending = state.Manual };
    }

    // Reusable lease marker; only the single render owner publishes its predecessor.
    private static readonly Publication RenderLease = new(default);
    private Publication? _renderSnapshot;
    private int _renderOwner;
    private Publication _published = new(new State
    {
        AutoMix = true, QueueEnabled = true, Speed = 1,
        CrossfadeFrames = 6 * PlaybackFormat.SampleRate
    });
    private readonly ConcurrentQueue<Voice> _retired = new();
    private readonly float[] _other = new float[PlaybackFormat.BlockFrames * PlaybackFormat.Channels];

    public int SampleRate => PlaybackFormat.SampleRate;
    public QueueSnapshot Snapshot
    {
        get
        {
            var publication = Volatile.Read(ref _published);
            return ReferenceEquals(publication, RenderLease)
                ? Volatile.Read(ref _renderSnapshot)!.Snapshot : publication.Snapshot;
        }
    }

    private Publication WaitForIdle()
    {
        var spin = new SpinWait();
        while (true)
        {
            var publication = Volatile.Read(ref _published);
            if (!ReferenceEquals(publication, RenderLease)) return publication;
            spin.SpinOnce();
        }
    }

    private bool Publish(Publication expected, State state) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _published, new Publication(state), expected), expected);

    internal void DrainRetired()
    {
        while (_retired.TryDequeue(out var voice)) voice.Dispose();
    }

    public void SetFirst(CachedTrack track)
    {
        DrainRetired();
        var initial = WaitForIdle().State;
        if (initial.Disposed || initial.Current is not null) return;
        Voice? replacement = new(track, Deck.A);
        try
        {
            while (true)
            {
                var expected = WaitForIdle();
                var state = expected.State;
                if (state.Disposed || state.Current is not null) return;
                state.Current = replacement;
                if (!Publish(expected, state)) continue;
                replacement = null;
                return;
            }
        }
        finally { replacement?.Dispose(); }
    }

    public bool SetNext(CachedTrack? track, CachedTrack? expectedCurrent = null)
    {
        DrainRetired();
        var initial = WaitForIdle().State;
        if (!CanSetNext(initial, track, expectedCurrent)) return false;
        if (ReferenceEquals(initial.Next?.Track, track)) return true;
        Voice? replacement = track is null ? null : new Voice(track, Deck.A);
        try
        {
            while (true)
            {
                var expected = WaitForIdle();
                var state = expected.State;
                if (!CanSetNext(state, track, expectedCurrent)) return false;
                if (ReferenceEquals(state.Next?.Track, track)) return true;
                if (replacement is not null)
                    replacement.Deck = state.Current!.Deck == Deck.A ? Deck.B : Deck.A;
                var previous = state.Next;
                state.Next = replacement;
                state.Manual = false;
                if (!Publish(expected, state)) continue;
                replacement = null;
                previous?.Dispose();
                return true;
            }
        }
        finally { replacement?.Dispose(); }
    }

    private static bool CanSetNext(State state, CachedTrack? track, CachedTrack? expectedCurrent) =>
        !state.Disposed && state.Current is not null && state.FadeLength == 0 &&
        !ReferenceEquals(state.Current.Track, track) &&
        (expectedCurrent is null || !state.Manual && ReferenceEquals(state.Current.Track, expectedCurrent));

    private bool Edit(Func<State, State?> edit)
    {
        DrainRetired();
        while (true)
        {
            var expected = WaitForIdle();
            if (expected.State.Disposed) return false;
            var changed = edit(expected.State);
            if (changed is not { } state) return false;
            if (Publish(expected, state)) return true;
        }
    }

    public bool RequestMix() => Edit(state =>
    {
        if (!state.QueueEnabled || state.Next is null || state.FadeLength != 0) return null;
        state.Manual = true;
        return state;
    });

    public void SetLoudnessNormalization(bool enabled) => Edit(state => { state.NormalizeLoudness = enabled; return state; });
    public void SetAutoMix(bool enabled) => Edit(state => { state.AutoMix = enabled; return state; });
    public void SetRepeatOne(bool enabled) => Edit(state => { state.RepeatOne = enabled; return state; });
    public void SetQueueEnabled(bool enabled) => Edit(state =>
    {
        state.QueueEnabled = enabled;
        if (!enabled) state.Manual = false;
        return state;
    });
    public void SetCrossfadeDuration(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(milliseconds, 12000);
        Edit(state => { state.CrossfadeFrames = PlaybackFormat.SampleRate * milliseconds / 1000; return state; });
    }

    public bool TrySetSpeed(CachedTrack track, double rate)
    {
        if (!double.IsFinite(rate) || rate < .75 || rate > 1.25)
            throw new ArgumentOutOfRangeException(nameof(rate));
        return Edit(state =>
        {
            if (state.Current is not { } current || !ReferenceEquals(track, current.Track) ||
                state.Manual || state.FadeLength != 0) return null;
            state.Speed = rate;
            return state;
        });
    }

    public bool TrySeek(CachedTrack track, long frame) => Edit(state =>
    {
        if (state.Current is not { } current || !ReferenceEquals(track, current.Track) ||
            state.Manual || state.FadeLength != 0) return null;
        // Only the render owner applies this seek to the cursor.
        state.SeekFrame = Math.Clamp(frame, 0, current.Cursor.Length);
        return state;
    });

    /// <summary>Claims one render block using CAS; no monitor or control-side
    /// waiting, and reader retirement never closes a file on the render path.</summary>
    public int Read(Span<float> destination)
    {
        if (Interlocked.CompareExchange(ref _renderOwner, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent queue render.");
        try
        {
            Publication expected;
            while (true)
            {
                expected = Volatile.Read(ref _published);
                if (expected.State.Disposed) return 0;
                Volatile.Write(ref _renderSnapshot, expected);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _published, RenderLease, expected), expected)) break;
            }
            var state = expected.State;
            Voice? retired = null;
            try
            {
                if (state.SeekFrame is long frame && state.Current is { } current)
                {
                    current.Cursor.Seek(frame);
                    state.SeekFrame = null;
                }
                return ReadBlock(destination, ref state, ref retired);
            }
            finally
            {
                if (retired is not null) _retired.Enqueue(retired);
                Volatile.Write(ref _published, new Publication(state));
            }
        }
        finally { Volatile.Write(ref _renderOwner, 0); }
    }

    private int ReadBlock(Span<float> destination, ref State state, ref Voice? retired)
    {
        int written = 0;
        while (state.Current is { } current && written < destination.Length / PlaybackFormat.Channels)
        {
            int requested = Math.Min(PlaybackFormat.BlockFrames,
                destination.Length / PlaybackFormat.Channels - written);
            var output = destination[(written * PlaybackFormat.Channels)..];
            long remaining = current.Source.RemainingAtRate(state.Speed);
            bool autoAdvance = state.AutoMix && !state.RepeatOne;
            if ((state.QueueEnabled || state.FadeLength > 0) && state.Next is { } next)
            {
                if (state.FadeLength == 0 && (state.Manual || autoAdvance && remaining <= state.CrossfadeFrames))
                {
                    state.Manual = false;
                    int length = (int)Math.Min(state.CrossfadeFrames,
                        Math.Min(remaining, Math.Max(0, next.Source.RemainingAtRate(1) - 1)));
                    if (length < 2)
                    {
                        retired = Promote(ref state);
                        continue;
                    }

                    state.FadeLength = length;
                }

                if (state.FadeLength > 0)
                {
                    // Callers may request more than the native block size.
                    int frames = Math.Min(Math.Min(requested, _other.Length / PlaybackFormat.Channels),
                        state.FadeLength - state.FadePosition);
                    MixFade(current, next, output[..(frames * PlaybackFormat.Channels)], state.Speed, state.FadePosition, state.FadeLength, state.NormalizeLoudness);
                    written += frames;
                    state.FadePosition += frames;
                    if (state.FadePosition == state.FadeLength)
                    {
                        retired = Promote(ref state);
                    }
                    continue;
                }

                if (autoAdvance)
                {
                    requested = (int)Math.Min(requested, remaining - state.CrossfadeFrames);
                }
            }

            if (state.RepeatOne && remaining == 0 && current.Cursor.Length > 0)
            {
                current.Cursor.Seek(0);
                continue;
            }
            int read = current.Source.ReadAtRate(output[..(requested * PlaybackFormat.Channels)], state.Speed);
            current.ApplyGain(output[..(read * PlaybackFormat.Channels)], state.NormalizeLoudness);
            written += read;
            if (read == 0)
            {
                break;
            }
        }

        return written;
    }

    /// <summary>Applies a mirrored hyperbolic gain curve, normalized to keep the combined gain power constant.</summary>
    private void MixFade(Voice outgoing, Voice incoming, Span<float> buffer, double speed, int fadePosition, int fadeLength, bool normalize)
    {
        var second = _other.AsSpan(0, buffer.Length);
        int outFrames = outgoing.Source.ReadAtRate(buffer, speed);
        int inFrames = incoming.Source.ReadAtRate(second, 1);
        buffer[(outFrames * PlaybackFormat.Channels)..].Clear();
        second[(inFrames * PlaybackFormat.Channels)..].Clear();
        outgoing.ApplyGain(buffer, normalize);
        incoming.ApplyGain(second, normalize);
        for (int frame = 0; frame < buffer.Length / PlaybackFormat.Channels; frame++)
        {
            float progress = (float)(fadePosition + frame) / (fadeLength - 1);
            (float enter, float leave) = HyperbolicFade.Gains(progress);
            int index = frame * PlaybackFormat.Channels;
            buffer[index] = buffer[index] * leave + second[index] * enter;
            buffer[index + 1] = buffer[index + 1] * leave + second[index + 1] * enter;
        }
    }

    /// <summary>Transfers ownership of the incoming reader to the current deck.</summary>
    private static Voice? Promote(ref State state)
    {
        var previous = state.Current;
        state.Current = state.Next;
        state.Next = null;
        state.FadeLength = state.FadePosition = 0;
        state.Speed = 1;
        state.SeekFrame = null;
        return previous;
    }

    public void Dispose()
    {
        while (true)
        {
            var expected = WaitForIdle();
            var state = expected.State;
            if (state.Disposed) break;
            var current = state.Current;
            var next = state.Next;
            state.Disposed = true;
            state.Current = state.Next = null;
            state.Manual = false;
            state.SeekFrame = null;
            state.FadeLength = state.FadePosition = 0;
            if (!Publish(expected, state)) continue;
            current?.Dispose();
            next?.Dispose();
            break;
        }
        DrainRetired();
    }

    private sealed class Voice : IDisposable
    {
        public Voice(CachedTrack track, Deck deck)
        {
            Track = track;
            Deck = deck;
            Cursor = new DeckCursor(track.Waveform.TotalFrames, 0);
            Source = new CachedPcmSource(track.Path, Cursor);
            float? db = track.Waveform.MusicProfile?.Loudness?.RecommendedGainDb();
            _normalizedGain = db is float v && float.IsFinite(v) ? MathF.Pow(10, Math.Clamp(v,-60,6)/20) : 1;
        }
        private readonly float _normalizedGain;
        private float _gain = 1;
        private bool _hasRendered;

        // Only the render owner mutates the ramp. No allocations or gain
        // conversion during reading. Newly audible decks start at target gain.
        public void ApplyGain(Span<float> samples, bool enabled)
        {
            if (samples.IsEmpty) return;
            float target = enabled ? _normalizedGain : 1;
            if (!_hasRendered) { _gain = target; _hasRendered = true; }
            int i=0;
            for (; i<samples.Length && _gain!=target; i+=2)
            {
                _gain += (target-_gain)*.001f;
                if (Math.Abs(target-_gain)<.00001f) _gain=target;
                samples[i]*=_gain;
                samples[i+1]*=_gain;
            }
            if (_gain==1) return;
            if (Vector.IsHardwareAccelerated)
            {
                var gain = new Vector<float>(_gain);
                int width = Vector<float>.Count;
                for (; i<=samples.Length-width; i+=width)
                    (new Vector<float>(samples[i..])*gain).CopyTo(samples[i..]);
            }
            for (;i<samples.Length;i++) samples[i]*=_gain;
        }

        public CachedTrack Track { get; }
        public Deck Deck { get; set; }
        public DeckCursor Cursor { get; }
        public CachedPcmSource Source { get; }
        public void Dispose() => Source.Dispose();
    }
}
