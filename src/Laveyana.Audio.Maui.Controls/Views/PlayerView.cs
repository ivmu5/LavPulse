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
    private readonly Button _automix = MixerStyle.Button("Автомикс");
    private readonly TrackQueueView _queue = new();
    private readonly Grid _body = new() { IsClippedToBounds = true };

    public PlayerView()
    {
        BackgroundColor = MixerStyle.Background;
        IsClippedToBounds = true;
        this.Add(Spectrum);
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
            Content = new VerticalStackLayout { Spacing = 10, Children = { CreateHeader(), CreateImportActions(), _queue } }
        };
        var deckScroll = new ScrollView
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            Content = new VerticalStackLayout { Spacing = 8, Children = { CurrentDeck, IncomingDeck } }
        };
        deckScroll.SetBinding(IsVisibleProperty, new Binding(nameof(IsVisible), source: CurrentDeck));
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
    public DeckView CurrentDeck { get; } = new();
    public DeckView IncomingDeck { get; } = new();
    public Label Status { get; } = MixerStyle.Label("", 12, MixerStyle.Muted);
    public Button AddFiles { get; } = MixerStyle.Button("Добавить MP3", primary: true);
    public Button AddFolder { get; } = MixerStyle.Button("Выбрать папку");

    public event Action? PlayRequested;
    public event Action? PreviousRequested;
    public event Action? NextRequested;
    public event Action? AutoMixChanged;
    public event Action<TrackFile>? QueueRequested
    {
        add => _queue.TrackRequested += value;
        remove => _queue.TrackRequested -= value;
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
        var settings = MixerStyle.Button("Настройки");
        settings.Clicked += (_, _) => SettingsRequested?.Invoke();
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        header.Add(new HorizontalStackLayout { Spacing = 9, Children = { mark, name } });
        header.Add(settings, 1);
        return header;
    }

    /// <summary>Creates the centered transport and its accessible actions.</summary>
    private Grid CreateTransport()
    {
        var controls = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star)]
        };
        _automix.HorizontalOptions = LayoutOptions.Start;
        _automix.Padding = new Thickness(5, 5);
        _automix.FontSize = 11;
        _automix.Clicked += (_, _) => AutoMixChanged?.Invoke();
        _play.Clicked += (_, _) => PlayRequested?.Invoke();
        _previous.Clicked += (_, _) => PreviousRequested?.Invoke();
        _next.Clicked += (_, _) => NextRequested?.Invoke();
        SemanticProperties.SetDescription(_next, "Свести со следующим треком");
        SemanticProperties.SetDescription(_previous, "Предыдущий трек");
        controls.Add(_automix);
        controls.Add(new HorizontalStackLayout { Spacing = 5, Children = { _previous, _play, _next } }, 1);
        return controls;
    }

    /// <summary>Keeps file and folder import above the queue on both platforms.</summary>
    private View CreateImportActions()
    {
        var row = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        row.Add(AddFiles);
        row.Add(AddFolder, 1);
        return row;
    }

    /// <summary>Applies the saved palette and timeline settings to the player.</summary>
    public void ApplyAppearance(PlayerSettings settings)
    {
        var colors = PlayerColors.For(settings.Palette);
        _play.BackgroundColor = AddFiles.BackgroundColor = colors.Accent;
        _queue.Accent = colors.Accent;
        CurrentDeck.SetAppearance(settings.WaveformOverview, colors);
        IncomingDeck.SetAppearance(settings.WaveformOverview, colors);
        Spectrum.SetAppearance(colors, settings.ShowSpectrum, settings.ShowPeaks);
    }

    public void ShowQueue(IReadOnlyList<TrackFile> files, TrackFile? selected, Func<TrackFile, string> name) => _queue.ShowQueue(files, selected, name);
    public void ShowCurrent(CachedTrack? track) => _queue.ShowCurrent(track);
    public Task BeginMixAsync(CachedTrack track, TrackFile? file) => _queue.BeginMixAsync(track, file);
    public Task FinishMixAsync(CachedTrack track, Action refreshQueue) => _queue.FinishMixAsync(track, refreshQueue);
    public void ResetAnimations() => _queue.Reset();

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
