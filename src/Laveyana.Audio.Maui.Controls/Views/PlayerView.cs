using Laveyana.Audio.Playback;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Composes the native player, decks, master spectrum and track feed.</summary>
public sealed class PlayerView : Grid
{
    private const string PlayIcon = "player_play.png";
    private const string PauseIcon = "player_pause.png";
    private readonly ImageButton _play = MixerStyle.IconButton(PlayIcon, 56, primary: true);
    private readonly ImageButton _previous = MixerStyle.IconButton("player_previous.png", 44);
    private readonly ImageButton _next = MixerStyle.IconButton("player_next.png", 44);
    private readonly Button _automix = MixerStyle.Button("Автомикс", icon: "player_automix.png", stacked: true);
    private readonly ImageButton _repeatOne = MixerStyle.IconButton("player_repeat_one.png", 44);
    private readonly ImageButton _equalizer = MixerStyle.IconButton("player_equalizer.png", 44);
    private readonly Switch _equalizerToggle = new()
    {
        WidthRequest = 48, VerticalOptions = LayoutOptions.Center
    };
    private readonly Border _equalizerGroup = new()
    {
        StrokeThickness = 0,
        HeightRequest = 44, VerticalOptions = LayoutOptions.Center,
        StrokeShape = new RoundRectangle { CornerRadius = 9 }
    };
    private readonly Button _settings = MixerStyle.Button("Настройки", icon: "player_settings.png", stacked: true);
    private readonly TrackQueueView _queue = new();
    private readonly Grid _body = new() { IsClippedToBounds = true };
    private readonly ContentView _panelHost = new() { IsClippedToBounds = true, IsVisible = false };
    private readonly VerticalStackLayout _decks = new() { Spacing = 8 };
    private DeckView? _leavingDeck;
    private DeckSlide? _deckEntrance;
    private DeckSlide? _deckExit;
    private bool _playheadsAnimating;
    private bool _queueAnimating;
    private bool _uiClockActive;
    private PlayingTrack? _latestCurrent;
    private PlayingTrack? _latestIncoming;
    private bool _latestCanSeek;
    private bool _latestOverview;
    private bool _syncingEqualizerToggle;
    private bool? _playIconPlaying;
    private bool? _autoMixAppearance;
    private bool? _repeatAppearance;
    private bool? _equalizerAppearance;

    public PlayerView()
    {
        MixerStyle.ApplyTheme(Resources, Theme, refreshIcons: true);
        BackgroundColor = Theme[MixerColor.Background];
        MixerStyle.StyleSwitch(_equalizerToggle);
        MixerStyle.UseColor(_equalizerGroup, BackgroundColorProperty, MixerColor.Button);
        IsClippedToBounds = true;
        this.Add(Spectrum);
        _automix.FontSize = _settings.FontSize = MixerStyle.PhoneFont(12, 10.5);
        _decks.Children.Add(CurrentDeck);
        _decks.Children.Add(IncomingDeck);
        var player = CreatePlayerLayout();
        _body.Add(player);
        this.Add(_body);

        _queue.SetMasterSpectrum(Spectrum.QueueDrawable);
        _queue.AnimationChanged += active =>
        {
            _queueAnimating = active;
            SyncUiFrames();
        };
        CurrentDeck.ScrubAnimationChanged += SyncUiFrames;
        IncomingDeck.ScrubAnimationChanged += SyncUiFrames;
        Spectrum.VisualizationChanged += () =>
        {
            if (Panel is null) _queue.InvalidateMasterSpectrum();
        };
        _queue.LibraryRequested += () => LibraryRequested?.Invoke();
        this.Add(_panelHost);

        _queue.ShowCurrent(null);
        CurrentDeck.Show(null, false);
        IncomingDeck.Show(null, false);
    }

    /// <summary>Keeps playback controls at the bottom and gives the waves room to scroll in short windows.</summary>
    private View CreatePlayerLayout()
    {
        var deckScroll = new ScrollView
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = _decks
        };
        int topPadding = 14;
#if WINDOWS
        topPadding += WindowsWindowChrome.ContentInset;
#endif
        int sidePadding = MixerStyle.IsPhone ? 12 : 16;
#if IOS
        if (MixerStyle.IsPhone)
        {
            topPadding = 8;
            sidePadding = 10;
        }
#endif
        var player = new Grid
        {
            HorizontalOptions = LayoutOptions.Center,
            MaximumWidthRequest = 760,
            Padding = new Thickness(sidePadding, topPadding, sidePadding, 12),
            RowSpacing = 8,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)]
        };
        player.Add(_queue);
        player.Add(deckScroll, row: 1);
        var transport = new VerticalStackLayout { Spacing = 5, Children = { Status, CreateTransport() } };
        player.Add(transport, row: 2);
        _queue.EditingChanged += editing =>
        {
            Grid.SetRowSpan(_queue, editing ? 3 : 1);
            _queue.ZIndex = editing ? 1 : 0;
            deckScroll.IsVisible = transport.IsVisible = !editing;
            if (editing) ResetDeckMotion();
            else ShowDecksAtRest();
            SyncUiFrames();
        };
        Status.IsVisible = false;
        Status.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Label.Text))
            {
                Status.IsVisible = !string.IsNullOrWhiteSpace(Status.Text);
            }
        };

        _body.SizeChanged += (_, _) =>
        {
            if (_body.Width > 0 && _body.Height > 0)
            {
                player.WidthRequest = Math.Min(760, _body.Width);
#if IOS
                bool shortPhone = MixerStyle.IsPhone && _body.Height < 430;
                deckScroll.MaximumHeightRequest = _body.Height * (shortPhone ? .43 : .56);
#else
                deckScroll.MaximumHeightRequest = _body.Height * .56;
#endif
            }
        };
        return player;
    }

    public MasterSpectrumView Spectrum { get; } = new();
    public DeckView CurrentDeck { get; private set; } = new();
    public DeckView IncomingDeck { get; private set; } = new();
    public Label Status { get; } = MixerStyle.Label("", 12, MixerColor.Muted);
    internal MixerTheme Theme { get; private set; } = MixerTheme.Default;
    internal event Action<MixerTheme>? ThemeChanged;
    public bool IsLightAppearance => Theme.IsLight;
    public View? Panel => _panelHost.Content;

    public event Action? PlayRequested;
    public event Action? PreviousRequested;
    public event Action? NextRequested;
    public event Action? AutoMixChanged;
    public event Action? RepeatOneChanged;
    public event Action? EqualizerRequested;
    public event Action<bool>? EqualizerToggled;
    public event Action<TrackFile>? QueueRequested
    {
        add => _queue.TrackRequested += value;
        remove => _queue.TrackRequested -= value;
    }
    public event Action<TrackFile>? HistoryRequested
    {
        add => _queue.HistoryRequested += value;
        remove => _queue.HistoryRequested -= value;
    }
    public event Action<TrackFile, TrackFile, bool>? QueueMoveRequested
    {
        add => _queue.MoveRequested += value;
        remove => _queue.MoveRequested -= value;
    }
    public event Action<TrackFile>? QueueRemoveRequested
    {
        add => _queue.RemoveRequested += value;
        remove => _queue.RemoveRequested -= value;
    }
    public event Action? SettingsRequested;
    public event Action? LibraryRequested;

    public bool TryCloseQueueEditor() => _queue.CloseEditor();
    public event Action<bool>? RecommendationsSelected
    {
        add => _queue.RecommendationsSelected += value;
        remove => _queue.RecommendationsSelected -= value;
    }

    public bool IsHandingOff => _queue.IsHandingOff;

    /// <summary>Creates the centered transport and its accessible actions.</summary>
    private Grid CreateTransport()
    {
        var controls = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(0), new(GridLength.Star)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)], RowSpacing = 4
        };
#if IOS
        // Let the two groups take their natural widths on small iPhones, with flexible space between.
        _equalizerToggle.WidthRequest = 52;
        controls.ColumnDefinitions[0].Width = controls.ColumnDefinitions[2].Width = GridLength.Auto;
        controls.ColumnDefinitions[1].Width = GridLength.Star;
#endif
        _automix.Clicked += (_, _) => AutoMixChanged?.Invoke();
        _repeatOne.Clicked += (_, _) => RepeatOneChanged?.Invoke();
        _equalizer.Clicked += (_, _) => EqualizerRequested?.Invoke();
        _equalizerToggle.Toggled += (_, args) =>
        {
            if (!_syncingEqualizerToggle) EqualizerToggled?.Invoke(args.Value);
        };
        _settings.Clicked += (_, _) => SettingsRequested?.Invoke();
        _play.Clicked += (_, _) => PlayRequested?.Invoke();
        _previous.Clicked += (_, _) => PreviousRequested?.Invoke();
        _next.Clicked += (_, _) => NextRequested?.Invoke();
        SemanticProperties.SetDescription(_next, "Свести со следующим треком");
        SemanticProperties.SetDescription(_previous, "Предыдущий трек");
        SemanticProperties.SetDescription(_equalizer, "Открыть эквалайзер");
        SemanticProperties.SetDescription(_equalizerToggle, "Эквалайзер");
        _equalizer.RemoveDynamicResource(BackgroundColorProperty);
        _equalizer.BackgroundColor = Colors.Transparent;
        _equalizer.BorderWidth = 0;
        _equalizer.CornerRadius = 8;
        _equalizerGroup.Content = new HorizontalStackLayout
        {
            Children = { _equalizer, _equalizerToggle }
        };
        var left = new HorizontalStackLayout { HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center,
            Spacing = 4, Children = { _automix, _repeatOne } };
        var right = new HorizontalStackLayout { HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center,
            Spacing = 4, Children = { _equalizerGroup, _settings } };
        var primary = new HorizontalStackLayout { HorizontalOptions = LayoutOptions.Center,
            Spacing = 4, Children = { _previous, _play, _next } };
        controls.Add(left);
        controls.Add(right, 2);
        controls.Add(primary, row: 1);
        Grid.SetColumnSpan(primary, 3);
        bool wide = false;
        controls.SizeChanged += (_, _) =>
        {
            bool next = controls.Width >= 520;
            if (next == wide) return;
            wide = next;
#if IOS
            controls.ColumnDefinitions[0].Width = controls.ColumnDefinitions[2].Width = wide ? GridLength.Star : GridLength.Auto;
            controls.ColumnDefinitions[1].Width = wide ? GridLength.Auto : GridLength.Star;
#else
            controls.ColumnDefinitions[1].Width = wide ? GridLength.Auto : new GridLength(0);
#endif
            Grid.SetColumnSpan(primary, wide ? 1 : 3);
            Grid.SetColumn(primary, wide ? 1 : 0);
            Grid.SetRow(primary, wide ? 0 : 1);
            controls.RowSpacing = wide ? 0 : 4;
        };
        return controls;
    }

    public void SetRepeatOne(bool enabled)
    {
        if (_repeatAppearance == enabled) return;
        _repeatAppearance = enabled;
        MixerStyle.UseColor(_repeatOne, BackgroundColorProperty, enabled ? MixerColor.Selected : MixerColor.Card);
        SemanticProperties.SetDescription(_repeatOne, enabled ? "Повтор одного трека включён" : "Повтор одного трека выключен");
    }

    public void SetEqualizerEnabled(bool enabled)
    {
        if (_equalizerAppearance != enabled)
        {
            _equalizerAppearance = enabled;
            MixerStyle.UseColor(_equalizerGroup, BackgroundColorProperty, enabled ? MixerColor.Selected : MixerColor.Button);
        }
        if (_equalizerToggle.IsToggled == enabled) return;
        _syncingEqualizerToggle = true;
        try { _equalizerToggle.IsToggled = enabled; }
        finally { _syncingEqualizerToggle = false; }
    }

    /// <summary>Replaces the player controls above the shared spectrum.</summary>
    public void ShowPanel(View? panel)
    {
        _panelHost.Content = panel;
        _panelHost.IsVisible = panel is not null;
        _body.IsVisible = panel is null;
    }

    /// <summary>Applies the saved palette and timeline settings to the player.</summary>
    public void ApplyAppearance(PlayerSettings settings) =>
        ApplyAppearance(settings, MixerTheme.SystemTheme);

    public void ApplyAppearance(PlayerSettings settings, AppTheme systemTheme)
    {
        var theme = MixerTheme.Create(settings, systemTheme, Theme);
        bool themeChanged = !ReferenceEquals(Theme, theme);
        if (themeChanged)
        {
            bool refreshIcons = theme.IsLight != Theme.IsLight;
            Theme = theme;
            MixerStyle.ApplyTheme(Resources, theme, refreshIcons);
            BackgroundColor = theme[MixerColor.Background];
            _queue.SetTheme(theme);
        }
        CurrentDeck.SetAppearance(settings, theme);
        IncomingDeck.SetAppearance(settings, theme);
        Spectrum.SetAppearance(theme, settings);
        if (themeChanged) ThemeChanged?.Invoke(theme);
    }

    public void ShowQueue(IReadOnlyList<TrackFile> tracks, TrackFile? current, TrackFile? next, int playedCount, Func<TrackFile, QueueTrackItem> read)
        => ShowQueue(tracks, current, playedCount, read);

    public void ShowQueue(IReadOnlyList<TrackFile> tracks, TrackFile? current, int playedCount, Func<TrackFile, QueueTrackItem> read)
        => _queue.ShowQueue(tracks, current, playedCount, read);

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

    public void RestartDisplayClock() => Spectrum.RestartDisplayClock();

    private void SyncUiFrames()
    {
        bool needed = _queueAnimating || _deckEntrance is not null || _deckExit is not null ||
            (!_queue.IsEditing && (_playheadsAnimating || CurrentDeck.IsScrubbing || IncomingDeck.IsScrubbing));
        if (_uiClockActive == needed) return;
        _uiClockActive = needed;
        Spectrum.SetUiCallback(this, needed ? DrawUiFrame : null);
    }

    private void DrawUiFrame(double time)
    {
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

    public void ShowCurrent(CachedTrack? track) => _queue.ShowCurrent(track);
    public void ShowQueueFeed() => _queue.SelectRecommendations(false);
    public Task BeginMixAsync(CachedTrack track, TrackFile? file) => _queue.BeginMixAsync(track, file);
    public Task FinishMixAsync(CachedTrack track, Action refreshQueue) => _queue.FinishMixAsync(track, refreshQueue);

    public void ResetAnimations()
    {
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

    /// <summary>Reflects playback state and the availability of a prepared incoming track.</summary>
    public void SetTransport(bool canPlay, bool playing, bool canMix, bool canGoBack, bool automix)
    {
        if (_playIconPlaying != playing)
        {
            _playIconPlaying = playing;
            MixerStyle.UseIcon(_play, ImageButton.SourceProperty, playing ? PauseIcon : PlayIcon);
            SemanticProperties.SetDescription(_play, playing ? "Пауза" : "Воспроизвести");
        }
        _play.IsEnabled = canPlay;
        _next.IsEnabled = canMix;
        _previous.IsEnabled = canGoBack;
        if (_autoMixAppearance != automix)
        {
            _autoMixAppearance = automix;
            MixerStyle.UseColor(_automix, BackgroundColorProperty, automix ? MixerColor.Selected : MixerColor.Card);
            SemanticProperties.SetDescription(_automix, automix ? "Автомикс включён" : "Автомикс выключен");
        }
    }

    /// <summary>Enables direct Explorer drops through the app's native Windows drag router.</summary>
    public void BindFileDrops(Func<bool> canDrop, Func<IReadOnlyList<TrackFile>, Task> receive)
    {
#if WINDOWS
        WindowsTrackDrag.AddTarget(this, canDrop, receive);
#endif
    }
}
