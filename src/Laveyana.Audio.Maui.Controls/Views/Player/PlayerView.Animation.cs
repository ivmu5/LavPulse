using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class PlayerView
{
    private DeckView? _leavingDeck;
    private DeckSlide? _deckEntrance;
    private DeckSlide? _deckExit;
    private bool _playheadsAnimating;
    private bool _queueAnimating;
    private bool _uiClockActive;
    private bool _uiFramesSuspended;
    private PlayingTrack? _latestCurrent;
    private PlayingTrack? _latestIncoming;
    private bool _latestCanSeek;
    private bool _latestOverview;

    /// <summary>Starts both playhead animation and spectrum rendering on the same display clock.</summary>
    public void StartPlaybackVisuals(Func<ISpectrumSource?> getSpectrum)
    {
        SetPlayheadAnimation(Panel is null);
        Spectrum.Start(getSpectrum);
    }

    public void StopPlaybackVisuals()
    {
        Spectrum.Stop();
        SetPlayheadAnimation(false);
    }

    private void SetPlayheadAnimation(bool enabled)
    {
        CurrentDeck.SetPlayheadAnimation(enabled);
        IncomingDeck.SetPlayheadAnimation(enabled);
        _playheadsAnimating = enabled;
        SyncUiFrames();
    }

    public void SnapPlayheadsOnNextPosition()
    {
        CurrentDeck.SnapOnNextPosition();
        IncomingDeck.SnapOnNextPosition();
    }

    public void RestartDisplayClock()
    {
        _uiFramesSuspended = false;
        SyncUiFrames();
        Spectrum.RestartDisplayClock();
    }

    private void SyncUiFrames()
    {
        bool needed = !_uiFramesSuspended && (Panel switch
        {
            TrackLibraryPanel library => library.NeedsScanFrames,
            null => _queueAnimating || _deckEntrance is not null || _deckExit is not null ||
                (!_queue.IsEditing && (_playheadsAnimating || CurrentDeck.IsScrubbing || IncomingDeck.IsScrubbing)),
            _ => false
        });
        if (_uiClockActive == needed) return;
        _uiClockActive = needed;
        Spectrum.SetUiCallback(this, needed ? DrawUiFrame : null);
    }

    private void DrawUiFrame(double time)
    {
        if (Panel is TrackLibraryPanel library)
        {
            library.DrawScanFrame(time);
            return;
        }
        if (_queueAnimating) _queue.DrawFrame(time);
        if (_deckEntrance is { } entrance && entrance.Advance(time)) _deckEntrance = null;
        if (_deckExit is { } exit && exit.Advance(time))
        {
            CompleteDeckExit(exit.Deck);
            CurrentDeck.Show(_latestCurrent, _latestCanSeek, _latestOverview);
        }
        if (!_queue.IsEditing)
        {
            if (_playheadsAnimating)
            {
                double now = DeckPlayhead.Now;
                CurrentDeck.DrawPlayhead(now);
                if (!ReferenceEquals(IncomingDeck, _leavingDeck)) IncomingDeck.DrawPlayhead(now);
            }
            CurrentDeck.DrawScrubFrame();
            if (!ReferenceEquals(IncomingDeck, _leavingDeck)) IncomingDeck.DrawScrubFrame();
        }
        SyncUiFrames();
    }

    /// <summary>Moves physical deck views without replacing the outgoing waveform during its exit.</summary>
    public void ShowDecks(QueueSnapshot snapshot, bool canSeek, bool showOverview)
    {
        RememberDecks(snapshot, canSeek, showOverview);
        if (_queue.IsEditing) return;
        if (snapshot.Current is { } current && CurrentDeck.DisplayedTrack is { } previous && !ReferenceEquals(previous, current.Track))
        {
            ShowEnteringDeck(current);
            (CurrentDeck, IncomingDeck) = (IncomingDeck, CurrentDeck);
            IncomingDeck.FreezeForExit();
            _leavingDeck = IncomingDeck;
            _deckExit = new(IncomingDeck, -SlideDistance, .26, Easing.CubicIn);
            SyncUiFrames();
        }

        CurrentDeck.Show(snapshot.Current, canSeek && _leavingDeck is null, showOverview && _leavingDeck is null);
        if (snapshot.Incoming is { } incoming)
        {
            ShowEnteringDeck(incoming);
        }
        else if (_leavingDeck is null)
        {
            IncomingDeck.Show(null, false, showOverview: false);
        }
    }

    private void ShowEnteringDeck(PlayingTrack track)
    {
        if (_leavingDeck is { } previous)
        {
            CompleteDeckExit(previous);
        }
        var entering = IncomingDeck;
        bool firstFrame = !ReferenceEquals(entering.DisplayedTrack, track.Track);
        if (firstFrame)
        {
            entering.TranslationX = SlideDistance;
        }
        entering.Show(track, false, showOverview: false);
        if (firstFrame)
        {
            if (_deckEntrance is { } previousEntrance && !ReferenceEquals(previousEntrance.Deck, entering))
                previousEntrance.Deck.TranslationX = 0;
            _deckEntrance = new(entering, 0, .28, Easing.CubicOut);
            SyncUiFrames();
        }
    }

    private double SlideDistance => Math.Max(280, Window?.Width ?? _body.Width);

    private void CompleteDeckExit(DeckView deck)
    {
        _deckExit = null;
        _leavingDeck = null;
        deck.Show(null, false, showOverview: false);
        deck.TranslationX = 0;
        _decks.Children.Remove(deck);
        _decks.Children.Add(deck);
    }

    private sealed class DeckSlide(DeckView deck, double to, double duration, Easing easing)
    {
        private readonly double _from = deck.TranslationX;
        private double? _start;
        public DeckView Deck { get; } = deck;

        public bool Advance(double time)
        {
            _start ??= time;
            double progress = Math.Clamp((time - _start.Value) / duration, 0, 1);
            Deck.TranslationX = _from + (to - _from) * easing.Ease(progress);
            return progress == 1;
        }
    }

    public void ResetAnimations()
    {
        _uiFramesSuspended = true;
        Spectrum.Stop();
        SuspendPlayerAnimations();
    }

    /// <summary>Stops animations belonging to the player controls while a panel is open.</summary>
    public void SuspendPlayerAnimations()
    {
        SetPlayheadAnimation(false);
        ResetDeckMotion();
        _queue.Reset();
        SyncUiFrames();
    }

    private void ResetDeckMotion()
    {
        if (_leavingDeck is { } leaving) CompleteDeckExit(leaving);
        CurrentDeck.CancelScrub();
        IncomingDeck.CancelScrub();
        _deckEntrance = null;
        CurrentDeck.TranslationX = IncomingDeck.TranslationX = 0;
    }

    private void RememberDecks(QueueSnapshot snapshot, bool canSeek, bool showOverview)
    {
        _latestCurrent = snapshot.Current;
        _latestIncoming = snapshot.Incoming;
        _latestCanSeek = canSeek;
        _latestOverview = showOverview;
    }

    private void ShowDecksAtRest()
    {
        CurrentDeck.Show(_latestCurrent, _latestCanSeek, _latestOverview);
        IncomingDeck.Show(_latestIncoming, false, showOverview: false);
    }

    /// <summary>Restores the visible player straight to the mixer's latest position.</summary>
    public void ResumeAnimations(QueueSnapshot snapshot, bool canSeek, bool showOverview, bool animatePlayheads = false)
    {
        RememberDecks(snapshot, canSeek, showOverview);
        ShowDecksAtRest();
        _queue.ShowCurrent(snapshot.Current?.Track);
        if (snapshot.Incoming is { } incoming) _queue.ShowIncoming(incoming.Track);
        _queue.ResumeAnimations();
        SetPlayheadAnimation(animatePlayheads);
    }
}
