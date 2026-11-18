using Laveyana.Audio.Playback;

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
    private readonly Button _settings = MixerStyle.Button("Настройки", icon: "player_settings.png", stacked: true);
    private readonly TrackQueueView _queue = new();
    private readonly Grid _body = new() { IsClippedToBounds = true };
    private readonly VerticalStackLayout _decks = new() { Spacing = 8 };
    private DeckView? _leavingDeck;
    private int _deckExit;
    private PlayingTrack? _latestCurrent;
    private bool _latestCanSeek;
    private bool _latestOverview;

    public PlayerView()
    {
        BackgroundColor = MixerStyle.Background;
        IsClippedToBounds = true;
        this.Add(Spectrum);
        _decks.Children.Add(CurrentDeck);
        _decks.Children.Add(IncomingDeck);
        var player = CreatePlayerLayout();
        _body.Add(player);
        this.Add(_body);

        _queue.ShowCurrent(null);
        CurrentDeck.Show(null, false);
        IncomingDeck.Show(null, false);
    }

    /// <summary>Keeps playback controls at the bottom and gives the waves room to scroll in short windows.</summary>
    private View CreatePlayerLayout()
    {
        var upper = new ScrollView
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = new VerticalStackLayout { Spacing = 10, Children = { CreateHeader(), _queue } }
        };
        var deckScroll = new ScrollView
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = _decks
        };
        var player = new Grid
        {
            HorizontalOptions = LayoutOptions.Center,
            MaximumWidthRequest = 760,
            Padding = new Thickness(16, 14, 16, 12),
            RowSpacing = 8,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)]
        };
        player.Add(upper);
        player.Add(deckScroll, row: 1);
        player.Add(new VerticalStackLayout { Spacing = 5, Children = { Status, CreateTransport() } }, row: 2);
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
                deckScroll.MaximumHeightRequest = _body.Height * .56;
            }
        };
        return player;
    }

    public MasterSpectrumView Spectrum { get; } = new();
    public DeckView CurrentDeck { get; private set; } = new();
    public DeckView IncomingDeck { get; private set; } = new();
    public Label Status { get; } = MixerStyle.Label("", 12, MixerStyle.Muted);

    public event Action? PlayRequested;
    public event Action? PreviousRequested;
    public event Action? NextRequested;
    public event Action? AutoMixChanged;
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
    public event Action? SettingsRequested;
    public event Action<bool>? RecommendationsSelected
    {
        add => _queue.RecommendationsSelected += value;
        remove => _queue.RecommendationsSelected -= value;
    }

    public bool IsHandingOff => _queue.IsHandingOff;

    /// <summary>Displays the ribbon and the application name at the top of the player.</summary>
    private View CreateHeader()
    {
        var mark = new Image
        {
            Source = "lavpulse_mark.png", WidthRequest = 64, HeightRequest = 38,
            Aspect = Aspect.AspectFit, VerticalOptions = LayoutOptions.Center
        };
        var name = MixerStyle.Label("LavPulse", 20, bold: true);
        name.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetHeadingLevel(name, SemanticHeadingLevel.Level1);
        return new HorizontalStackLayout { IsVisible = false, Spacing = 9, Children = { mark, name } };
    }

    /// <summary>Creates the centered transport and its accessible actions.</summary>
    private Grid CreateTransport()
    {
        var controls = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star)]
        };
        _automix.HorizontalOptions = LayoutOptions.Start;
        _settings.HorizontalOptions = LayoutOptions.End;
        _automix.VerticalOptions = _settings.VerticalOptions = LayoutOptions.Center;
        _automix.Clicked += (_, _) => AutoMixChanged?.Invoke();
        _settings.Clicked += (_, _) => SettingsRequested?.Invoke();
        _play.Clicked += (_, _) => PlayRequested?.Invoke();
        _previous.Clicked += (_, _) => PreviousRequested?.Invoke();
        _next.Clicked += (_, _) => NextRequested?.Invoke();
        SemanticProperties.SetDescription(_next, "Свести со следующим треком");
        SemanticProperties.SetDescription(_previous, "Предыдущий трек");
        controls.Add(_automix);
        controls.Add(new HorizontalStackLayout { Spacing = 4, Children = { _previous, _play, _next } }, 1);
        controls.Add(_settings, 2);
        return controls;
    }

    /// <summary>Applies the saved palette and timeline settings to the player.</summary>
    public void ApplyAppearance(PlayerSettings settings)
    {
        var colors = PlayerColors.For(settings.Palette);
        _play.BackgroundColor = colors.Accent;
        _queue.Accent = colors.Accent;
        CurrentDeck.SetAppearance(settings.WaveformOverview, colors);
        IncomingDeck.SetAppearance(settings.WaveformOverview, colors);
        Spectrum.SetAppearance(colors, settings);
    }

    public void ShowQueue(IReadOnlyList<TrackFile> upcoming, IReadOnlyList<TrackFile> played, Func<TrackFile, QueueTrackItem> read)
        => _queue.ShowQueue(upcoming, played, read);

    /// <summary>Moves physical deck views without replacing the outgoing waveform during its exit.</summary>
    public void ShowDecks(QueueSnapshot snapshot, bool canSeek, bool showOverview)
    {
        _latestCurrent = snapshot.Current;
        _latestCanSeek = canSeek;
        _latestOverview = showOverview;
        if (snapshot.Current is { } current && CurrentDeck.DisplayedTrack is { } previous && !ReferenceEquals(previous, current.Track))
        {
            ShowEnteringDeck(current);
            (CurrentDeck, IncomingDeck) = (IncomingDeck, CurrentDeck);
            IncomingDeck.FreezeForExit();
            _leavingDeck = IncomingDeck;
            _ = SlideOutAsync(IncomingDeck, ++_deckExit);
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
            entering.CancelAnimations();
            entering.TranslationX = SlideDistance;
        }
        entering.Show(track, false, showOverview: false);
        if (firstFrame)
        {
            _ = entering.TranslateToAsync(0, 0, 280, Easing.CubicOut);
        }
    }

    private double SlideDistance => Math.Max(280, Window?.Width ?? _body.Width);

    private async Task SlideOutAsync(DeckView deck, int exit)
    {
        await deck.TranslateToAsync(-SlideDistance, 0, 260, Easing.CubicIn);
        if (exit == _deckExit)
        {
            CompleteDeckExit(deck);
            CurrentDeck.Show(_latestCurrent, _latestCanSeek, _latestOverview);
        }
    }

    private void CompleteDeckExit(DeckView deck)
    {
        _deckExit++;
        _leavingDeck = null;
        deck.CancelAnimations();
        deck.Show(null, false, showOverview: false);
        deck.TranslationX = 0;
        _decks.Children.Remove(deck);
        _decks.Children.Add(deck);
    }

    public void ShowCurrent(CachedTrack? track) => _queue.ShowCurrent(track);
    public Task BeginMixAsync(CachedTrack track, TrackFile? file) => _queue.BeginMixAsync(track, file);
    public Task FinishMixAsync(CachedTrack track, Action refreshQueue) => _queue.FinishMixAsync(track, refreshQueue);
    public void ResetAnimations()
    {
        if (_leavingDeck is { } leaving)
        {
            CompleteDeckExit(leaving);
        }
        CurrentDeck.CancelAnimations();
        IncomingDeck.CancelAnimations();
        _queue.Reset();
    }

    /// <summary>Reflects playback state and the availability of a prepared incoming track.</summary>
    public void SetTransport(bool canPlay, bool playing, bool canMix, bool canGoBack, bool automix)
    {
        string image = playing ? PauseIcon : PlayIcon;
        if (_play.Source is not FileImageSource source || source.File != image)
        {
            _play.Source = image;
        }
        SemanticProperties.SetDescription(_play, playing ? "Пауза" : "Воспроизвести");
        _play.IsEnabled = canPlay;
        _next.IsEnabled = canMix;
        _previous.IsEnabled = canGoBack;
        _automix.BackgroundColor = automix ? MixerStyle.SelectedBackground : MixerStyle.Card;
        SemanticProperties.SetDescription(_automix, automix ? "Автомикс включён" : "Автомикс выключен");
    }

    /// <summary>Enables direct Explorer drops through the app's native Windows drag router.</summary>
    public void BindFileDrops(Func<bool> canDrop, Func<IReadOnlyList<TrackFile>, Task> receive)
    {
#if WINDOWS
        WindowsTrackDrag.AddTarget(this, canDrop, receive);
#endif
    }
}
