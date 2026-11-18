using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Coordinates the native player, its local library and one continuous playback queue.</summary>
public sealed partial class MainPage : ContentPage
{
    private readonly PlayerView _view = new();
#if WINDOWS
    private readonly Grid _pageRoot = new();
    private ContentView? _overlay;
#endif
    private readonly QueuePlaybackSession _playback = new();
    private readonly ISystemPlaybackIntegration _systemPlayback = new PlatformSystemPlaybackIntegration();
#if WINDOWS
    private bool _windowVisible = true;
#endif
    private long _lastSystemUpdate;
    private CachedTrack? _lastSystemTrack;
    private bool _lastSystemPlaying;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PlayerLibrary _library;
    private readonly IDispatcherTimer _positions;
    private readonly PlaybackQueue _queue = new();
    private readonly Queue<TrackFile> _analysisPending = new();
    private readonly HashSet<TrackFile> _analysisSeen = [];
    private Task _analysisWorker = Task.CompletedTask;
    private bool _analysisRunning;
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private Task? _coverAnimation;
    private TrackFile? _selected;
    private TrackFile? _armed;
    private CachedTrack? _shownCurrent;
    private CachedTrack? _shownIncoming;
    private PlayerSettings _settings = PlayerPreferences.Load();
    private EqualizerSettings _equalizer = PlayerPreferences.LoadEqualizer();
    private bool _repeatOne;
    private bool _recommendations;
    private bool _openingSettings;
    private bool _pausing;
    private bool _pickerOpen;
    private bool _loadingFirst;
    private bool _queueRefreshPending;
    private bool _closed;

    private bool IsStopping => _pausing || _stop?.IsCancellationRequested == true;

    public MainPage()
    {
        _library = new PlayerLibrary(new TrackPreparation(FileSystem.CacheDirectory), _lifetime.Token);
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.None, SafeAreaRegions.Container, SafeAreaRegions.None, SafeAreaRegions.Container);
        BackgroundColor = _view.BackgroundColor;
#if WINDOWS
        _pageRoot.Children.Add(_view);
        Content = _pageRoot;
#else
        Content = _view;
#endif

        _view.PlayRequested += async () => await TogglePlaybackAsync();
        _view.NextRequested += async () => await StartMixAsync();
        _view.PreviousRequested += async () => await PreviousAsync();
        _view.AutoMixChanged += ToggleAutoMix;
        _view.RepeatOneChanged += ToggleRepeatOne;
        _view.EqualizerRequested += async () => await OpenEqualizerAsync();
        _view.SettingsRequested += async () => await OpenSettingsAsync();
        _view.RecommendationsSelected += SelectRecommendations;
        _view.QueueRequested += async file => await ChooseAsync(file);
        _view.HistoryRequested += async file => await ReplayHistoryAsync(file);
        _view.QueueMoveRequested += async (from, to, after) => await MoveQueueAsync(from, to, after);
        _view.QueueRemoveRequested += async file => await RemoveQueueTrackAsync(file);
        _view.BindFileDrops(() => !_closed, AddFilesAsync);
        _systemPlayback.CommandReceived += OnSystemPlaybackCommand;
        _view.CurrentDeck.SeekRequested += Seek;
        _view.IncomingDeck.SeekRequested += Seek;
        _view.CurrentDeck.SpeedRequested += SetTrackSpeed;
        _view.IncomingDeck.SpeedRequested += SetTrackSpeed;

        _positions = Dispatcher.CreateTimer();
        _positions.Interval = TimeSpan.FromMilliseconds(33);
        _positions.Tick += (_, _) => UpdatePositions();
        ApplyEqualizer(_equalizer);
        ApplySettings(_settings, save: false);
    }

}
