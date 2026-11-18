using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Composes the native player, decks, master spectrum and track browser.</summary>
public sealed class PlayerView : Grid
{
    private const string PlayIcon = "player_play.png";
    private const string PauseIcon = "player_pause.png";
    private readonly ImageButton _play = MixerStyle.IconButton(PlayIcon, 56, primary: true);
    private readonly ImageButton _previous = MixerStyle.IconButton("player_previous.png", 44);
    private readonly ImageButton _next = MixerStyle.IconButton("player_next.png", 44);
    private readonly Button _automix = MixerStyle.Button("Автомикс");
    private readonly TrackQueueView _queue = new();
    private readonly Border _browser = new() { IsVisible = false };
    private readonly CollectionView _library = new() { SelectionMode = SelectionMode.Single };
    private readonly Grid _body = new() { IsClippedToBounds = true };
    private readonly Button _handle = MixerStyle.Button("Треки");

    public PlayerView()
    {
        BackgroundColor = MixerStyle.Background;
        IsClippedToBounds = true;
        RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)];
        this.Add(Spectrum);
        SetRowSpan((IView)Spectrum, 2);
        var player = CreatePlayerLayout();
        _body.Add(player);
        this.Add(_body);

        var dock = new HorizontalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center, Padding = new Thickness(16, 3, 16, 13),
            Spacing = 4
        };
        var grip = new GraphicsView { Drawable = new BrowserGrip(), WidthRequest = 44, HeightRequest = 44 };
        SemanticProperties.SetDescription(grip, "Ручка браузера треков. Потяни вверх.");
        float downY = 0;
        TimelineInput.Attach(grip,
            point => { downY = point.Y; return true; },
            point => { if (point.Y < downY - 22) SetBrowser(true); },
            point => { if (Math.Abs(point.Y - downY) < 7) SetBrowser(!_browser.IsVisible); },
            () => { });
        dock.Add(grip);
        SemanticProperties.SetDescription(_handle, "Открыть браузер треков");
        dock.Add(_handle);
        this.Add(dock, row: 1);
        _handle.Clicked += (_, _) => SetBrowser(!_browser.IsVisible);
        CreateBrowser();
        this.Add(_browser);
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
            Content = new VerticalStackLayout { Spacing = 8, Children = { CurrentDeck, IncomingDeck } }
        };
        deckScroll.SetBinding(IsVisibleProperty, new Binding(nameof(IsVisible), source: CurrentDeck));
        var player = new Grid
        {
            HorizontalOptions = LayoutOptions.Center,
            MaximumWidthRequest = 760,
            Padding = new Thickness(16, 14, 16, 6),
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

    private sealed class BrowserGrip : IDrawable
    {
        public void Draw(ICanvas canvas, RectF bounds)
        {
            canvas.FillColor = MixerStyle.Card;
            canvas.FillRoundedRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, 11);
            canvas.FillColor = MixerStyle.Muted;
            canvas.FillRoundedRectangle(bounds.Center.X - 12, bounds.Center.Y - 1.5f, 24, 3, 1.5f);
        }
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
    public event Action<TrackFile>? LibraryTrackRequested;

    public bool IsHandingOff => _queue.IsHandingOff;

    /// <summary>Displays the ribbon and the application name at the top of the player.</summary>
    private static View CreateHeader()
    {
        var mark = new Image
        {
            Source = "lavpulse_mark.png", WidthRequest = 64, HeightRequest = 38,
            Aspect = Aspect.AspectFit, VerticalOptions = LayoutOptions.Center
        };
        var name = MixerStyle.Label("LavPulse", 20, bold: true);
        name.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetHeadingLevel(name, SemanticHeadingLevel.Level1);
        return new HorizontalStackLayout { Spacing = 9, Children = { mark, name } };
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

    /// <summary>Builds the virtualized library sheet with file and folder import.</summary>
    private void CreateBrowser()
    {
        var close = MixerStyle.Button("Закрыть");
        close.HorizontalOptions = LayoutOptions.Center;
        close.Clicked += (_, _) => SetBrowser(false);
        var controls = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        controls.Add(AddFiles);
        controls.Add(AddFolder, 1);
        _library.ItemTemplate = new DataTemplate(() =>
        {
            var title = MixerStyle.Label("", 14);
            title.Padding = new Thickness(9, 11);
            title.SetBinding(Label.TextProperty, nameof(TrackFile.Name));
            return title;
        });
        _library.EmptyView = MixerStyle.Label("Добавь MP3 или выбери папку", 13, MixerStyle.Muted);
        _library.SelectionChanged += (_, args) =>
        {
            if (args.CurrentSelection.FirstOrDefault() is TrackFile file)
            {
                _library.SelectedItem = null;
                SetBrowser(false);
                LibraryTrackRequested?.Invoke(file);
            }
        };
        var content = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
            RowSpacing = 8
        };
        content.Add(close);
        content.Add(controls, row: 1);
        content.Add(_library, row: 2);
        MixerStyle.StyleCard(_browser, content);
        _browser.HeightRequest = 320;
        _browser.MaximumWidthRequest = 760;
        _browser.HorizontalOptions = LayoutOptions.Fill;
        _browser.VerticalOptions = LayoutOptions.End;
        _browser.Margin = new Thickness(12, 0, 12, 6);
        _browser.BackgroundColor = MixerStyle.Overlay;
    }

    /// <summary>Shows the sheet above the safe bottom dock without intercepting unrelated gestures.</summary>
    public void SetBrowser(bool open)
    {
        _browser.IsVisible = open;
        _body.InputTransparent = open;
        SemanticProperties.SetDescription(_handle, open ? "Закрыть браузер треков" : "Открыть браузер треков");
    }

    public void ShowLibrary(IReadOnlyList<TrackFile> files) => _library.ItemsSource = files;
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
