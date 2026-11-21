using Laveyana.Audio.Maui.AudioOutput;
using Laveyana.Audio.Maui.Controls;
using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Maui.Settings;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui;

/// <summary>Coordinates the native player, its local library and one continuous playback queue.</summary>
public sealed partial class MainPage : ContentPage
{
    private static readonly TimeSpan VisibleRefreshInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan HiddenRefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly PlayerView _view = new();
    private readonly QueuePlaybackSession _playback = new();
    private readonly ISystemPlaybackIntegration _systemPlayback = new PlatformSystemPlaybackIntegration();
    private volatile bool _windowVisible = true;
    private long _lastSystemUpdate;
    private SystemPlaybackState? _lastSystemState;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LocalTrackStore _trackStore = new(Path.Combine(FileSystem.AppDataDirectory, "library"));
    private readonly PlayerLibrary _library;
    private readonly IDispatcherTimer _positions;
    private readonly PlaybackQueue _queue = new();
    private readonly QueueTrackAnalysis _queueAnalysis;
    private Task _analysisWorker = Task.CompletedTask;
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private Task? _coverAnimation;
    private TrackFile? _selected;
    private TrackFile? _armed;
    private CancellationTokenSource? _selectionLoad;
    private bool _selectedByUser;
    private CachedTrack? _shownCurrent;
    private CachedTrack? _shownIncoming;
    private PlayerSettings _settings = PlayerPreferences.Load();
    private EqualizerSettings _equalizer = PlayerPreferences.LoadEqualizer();
    private bool _repeatOne;
    private bool _recommendations;
    private bool _pausing;
    private bool _pickerOpen;
    private CancellationTokenSource? _firstLoad;
    private TrackFile? _firstLoadFile;
    private bool _queueRefreshPending;
    private bool _closed;

    private bool IsLoadingFirst => _firstLoad is not null;
    private bool IsStopping => _pausing || _stop?.IsCancellationRequested == true;
    private bool IsPlayerVisible => _windowVisible && _view.Panel is null;
#if ANDROID
    internal bool ShowOnLockScreen => _settings.ShowOnLockScreen;
#endif

    public MainPage()
    {
#if ANDROID
        var preparation = new TrackPreparation(_trackStore.PcmDirectory, AndroidMp3Decoder.Open);
#else
        var preparation = new TrackPreparation(_trackStore.PcmDirectory);
#endif
        _library = new PlayerLibrary(preparation, _trackStore, _lifetime.Token);
        _queueAnalysis = new QueueTrackAnalysis(file => _library.GetPrepared(file) is not null,
            _library.PrepareAsync, OnQueueAnalysisProgress, _lifetime.Token, _library.HasAnalysis, _library.AnalyzeAsync);
#if IOS
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container);
#else
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.None, SafeAreaRegions.Container, SafeAreaRegions.None, SafeAreaRegions.Container);
#endif
        BackgroundColor = _view.BackgroundColor;
        Content = _view;

        _view.PlayRequested += async () => await TogglePlaybackAsync();
        _view.NextRequested += async () => await StartMixAsync();
        _view.PreviousRequested += async () => await PreviousAsync();
        _view.AutoMixChanged += ToggleAutoMix;
        _view.RepeatOneChanged += ToggleRepeatOne;
        _view.EqualizerRequested += () => OpenEqualizer();
        _view.EqualizerToggled += ToggleEqualizer;
        _view.SettingsRequested += () => OpenSettings();
        _view.LibraryRequested += () => OpenTrackLibrary();
        _view.QueueEditingChanged += OnQueueEditingChanged;
        _view.RecommendationsSelected += SelectRecommendations;
        _view.QueueRequested += async file => await ChooseAsync(file);
        _view.HistoryRequested += async file => await ReplayHistoryAsync(file);
        _view.QueueMoveRequested += async (from, to, after) => await MoveQueueAsync(from, to, after);
        _view.QueueRemoveRequested += async file => await RemoveQueueTrackAsync(file);
        _view.BindFileDrops(() => !_closed && IsPlayerVisible, AddFilesAsync);
        _systemPlayback.CommandReceived += OnSystemPlaybackCommand;
        _view.CurrentDeck.SeekRequested += Seek;
        _view.IncomingDeck.SeekRequested += Seek;
        _view.CurrentDeck.SpeedRequested += SetTrackSpeed;
        _view.IncomingDeck.SpeedRequested += SetTrackSpeed;

        _positions = Dispatcher.CreateTimer();
        _positions.Interval = VisibleRefreshInterval;
        _positions.Tick += (_, _) =>
        {
            UpdatePositions();
            ProcessSystemTrackRequests();
        };
        ApplyEqualizer(_equalizer);
        ApplySettings(_settings, save: false);
    }

}
