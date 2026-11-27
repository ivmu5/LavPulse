using System.Threading;
using Laveyana.Audio.Playback;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.LavPulse.Controls;

/// <summary>Composes the native player, decks, master spectrum and track feed.</summary>
public sealed partial class PlayerView : Grid
{
    private bool _syncingEqualizerToggle;
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
        WidthRequest = 48,
        VerticalOptions = LayoutOptions.Center
    };
    private readonly Border _equalizerGroup = new()
    {
        StrokeThickness = 0,
        HeightRequest = 44,
        VerticalOptions = LayoutOptions.Center,
        StrokeShape = new RoundRectangle { CornerRadius = 9 }
    };
    private readonly Button _settings = MixerStyle.Button("Настройки", icon: "player_settings.png", stacked: true);
    private readonly TrackQueueView _queue = new();
    private readonly Grid _body = new() { IsClippedToBounds = true };
    private readonly ContentView _panelHost = new() { IsClippedToBounds = true, IsVisible = false };
    private readonly VerticalStackLayout _decks = new() { Spacing = 8 };
    private bool? _playIconPlaying;
    private bool? _autoMixAppearance;
    private bool? _repeatAppearance;
    private bool? _equalizerAppearance;
    private CancellationTokenSource? _previousHold;
    private bool _previousWasHeld;
    private const int PreviousHoldMilliseconds = 550;

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
        this.Add(_actionOverlayHost);
        Unloaded += (_, _) => TryDismissActionOverlay();

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
            MaximumWidthRequest = AdaptivePanelLayout.MaximumWidth,
            Padding = new Thickness(sidePadding, topPadding, sidePadding, 12),
            RowSpacing = 8,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)]
        };
        player.Add(_queue);
        player.Add(deckScroll, row: 1);
        var transport = new VerticalStackLayout { Spacing = 5, Children = { Status, CreateTransport() } };
        player.Add(transport, row: 2);
        void UpdatePlayerWidth()
        {
            if (_body.Width <= 0 || _body.Height <= 0) return;
            if (_queue.IsEditing)
            {
                var layout = AdaptivePanelLayout.Measure(_body.Width, _body.Height);
                player.Padding = new Thickness(AdaptivePanelLayout.Padding, topPadding, AdaptivePanelLayout.Padding, 12);
                player.WidthRequest = layout.Width;
                _queue.SetEditorLayout(layout);
            }
            else
            {
                player.Padding = new Thickness(sidePadding, topPadding, sidePadding, 12);
                player.WidthRequest = Math.Min(760, _body.Width);
                _queue.SetEditorLayout(default);
            }
        }
        _queue.EditingChanged += editing =>
        {
            UpdatePlayerWidth();
            SectionTitleChanged?.Invoke();
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
                UpdatePlayerWidth();
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
    internal string SectionTitle => Panel switch
    {
        TrackLibraryPanel => "Медиатека",
        PlayerSettingsPanel => "Настройки",
        EqualizerPanel => "Эквалайзер",
        null when _queue.IsEditing => "Редактор очереди",
        null => "",
        _ => "LavPulse"
    };
    internal event Action? SectionTitleChanged;

    public event Action? PlayRequested;
    public event Action? PreviousRequested;
    public event Action? RestartCurrentRequested;
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
    public event Action<TrackFile>? QueuePlayRequested
    {
        add => _queue.PlayRequested += value;
        remove => _queue.PlayRequested -= value;
    }
    public event Action? QueueClearRequested
    {
        add => _queue.ClearRequested += value;
        remove => _queue.ClearRequested -= value;
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
    public Func<IReadOnlyList<TrackFile>,Point?,Task>? QueueActionsRequested
    {
        get => _queue.ActionsRequested;
        set => _queue.ActionsRequested = value;
    }
    public Func<TrackFile,ListSwipeAction,Task>? QueueSwipeRequested
    {
        get => _queue.SwipeRequested;
        set => _queue.SwipeRequested = value;
    }
    public event Action? SettingsRequested;
    public event Action? LibraryRequested;
    public event Action<bool>? QueueEditingChanged
    {
        add => _queue.EditingChanged += value;
        remove => _queue.EditingChanged -= value;
    }

    public bool TryOpenQueueEditor() => Panel is null && _queue.OpenEditor();
    public bool TryNavigateQueueBack() => _queue.ExitDeleteMode() || TryCloseQueueEditor();
    public bool TryCloseQueueEditor() => _queue.CloseEditor() || _queue.DismissDeleteAction();
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
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
            RowSpacing = 4
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
        _previous.Pressed += (_, _) =>
        {
            CancelPreviousHold();
            _previousWasHeld = false;
            var hold = new CancellationTokenSource();
            _previousHold = hold;
            _ = DetectPreviousHoldAsync(hold);
        };
        _previous.Released += (_, _) => CancelPreviousHold();
        _previous.Clicked += (_, _) =>
        {
            if (!_previousWasHeld) PreviousRequested?.Invoke();
        };
        _next.Clicked += (_, _) => NextRequested?.Invoke();
        SemanticProperties.SetDescription(_next, "Переход к следующему треку");
        SemanticProperties.SetDescription(_previous, "Предыдущий трек. Долгое нажатие — начало текущего трека.");
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
        var left = new HorizontalStackLayout
        {
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
            Spacing = 4,
            Children = { _automix, _repeatOne }
        };
        var right = new HorizontalStackLayout
        {
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Spacing = 4,
            Children = { _equalizerGroup, _settings }
        };
        var primary = new HorizontalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center,
            Spacing = 4,
            Children = { _previous, _play, _next }
        };
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

    private async Task DetectPreviousHoldAsync(CancellationTokenSource hold)
    {
        try
        {
            await Task.Delay(PreviousHoldMilliseconds, hold.Token);
            if (!ReferenceEquals(_previousHold, hold) || !_previous.IsEnabled) return;
            _previousWasHeld = true;
            RestartCurrentRequested?.Invoke();
        }
        catch (OperationCanceledException) when (hold.IsCancellationRequested) { }
    }

    private void CancelPreviousHold()
    {
        var hold = _previousHold;
        _previousHold = null;
        if (hold is null) return;
        hold.Cancel();
        hold.Dispose();
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
        TryDismissActionOverlay();
        if (Panel is TrackLibraryPanel previous) previous.ScanAnimationChanged -= SyncUiFrames;
        _panelHost.Content = panel;
        if (panel is not null) _queue.CloseEditor();
        _panelHost.IsVisible = panel is not null;
        _body.IsVisible = panel is null;
        if (panel is TrackLibraryPanel library) library.ScanAnimationChanged += SyncUiFrames;
        SectionTitleChanged?.Invoke();
        SyncUiFrames();
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
        _queue.ListSettings = settings;
        CurrentDeck.SetAppearance(settings, theme);
        IncomingDeck.SetAppearance(settings, theme);
        Spectrum.SetAppearance(theme, settings);
        if (themeChanged) ThemeChanged?.Invoke(theme);
    }

    public void ShowQueue(IReadOnlyList<TrackFile> tracks, TrackFile? current, TrackFile? next, int playedCount, Func<TrackFile, QueueTrackItem> read)
        => ShowQueue(tracks, current, playedCount, read);

    public void ShowQueue(IReadOnlyList<TrackFile> tracks, TrackFile? current, int playedCount, Func<TrackFile, QueueTrackItem> read)
        => _queue.ShowQueue(tracks, current, playedCount, read);

    public void ShowCurrent(CachedTrack? track) => _queue.ShowCurrent(track);
    public void ShowQueueFeed() => _queue.SelectRecommendations(false);
    public Task BeginMixAsync(CachedTrack track, TrackFile? file) => _queue.BeginMixAsync(track, file);
    public Task FinishMixAsync(CachedTrack track, Action refreshQueue) => _queue.FinishMixAsync(track, refreshQueue);

    /// <summary>Reflects playback state and the availability of a prepared incoming track.</summary>
    public void SetTransport(bool canPlay, bool playing, bool canMix, bool canGoBack, bool automix)
    {
        _queue.SetPlaybackState(playing);
        if (_playIconPlaying != playing)
        {
            _playIconPlaying = playing;
            MixerStyle.UseIcon(_play, ImageButton.SourceProperty, playing ? PauseIcon : PlayIcon);
            SemanticProperties.SetDescription(_play, playing ? "Пауза" : "Воспроизвести");
        }
        _play.IsEnabled = canPlay;
        _next.IsEnabled = canMix;
        _previous.IsEnabled = canGoBack;
        if (!canGoBack) CancelPreviousHold();
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
