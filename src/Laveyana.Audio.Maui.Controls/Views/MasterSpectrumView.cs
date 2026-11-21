using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class MasterSpectrumView : ContentView
{
    private readonly SpectrumDrawable _drawable = new();
    private readonly float[] _levels = new float[PlaybackFormat.SpectrumBars];
    private readonly GraphicsView _graphics;
    private long _version;
    private SpectrumFrameClock _frameClock;
    private int _framesPerSecond;
    private Func<ISpectrumSource?>? _getSource;
    private Action? _drawPlayheads;
    private Action<double>? _drawQueue;
    private SpectrumFrameClock _queueFrameClock;
    private VisualElement? _queueFrameTarget;
    private bool _frameLoopRunning;
    private bool IsSpectrumActive => _getSource is not null && IsVisible;

    public MasterSpectrumView()
    {
        InputTransparent = true;
        QueueDrawable = new QueueSpectrum(_drawable);
        _graphics = new GraphicsView { Drawable = _drawable, InputTransparent = true };
        Content = _graphics;
        ObserveNativeGraphicsView();
    }

    internal IDrawable QueueDrawable { get; }
    internal event Action? VisualizationChanged;

    internal void SetPlayheadCallback(Action? drawPlayheads)
    {
        _drawPlayheads = drawPlayheads;
        SyncFrameLoop();
    }

    internal void SetQueueCallback(VisualElement target, Action<double>? drawQueue)
    {
        _queueFrameTarget = target;
        _drawQueue = drawQueue;
        _queueFrameClock = default;
        SyncFrameLoop();
    }

    internal void SetAppearance(MixerTheme theme, PlayerSettings settings)
    {
        int requested = (int)settings.SpectrumFrameRate;
        bool frameRateChanged = _framesPerSecond != requested;
        if (frameRateChanged)
        {
            _framesPerSecond = requested;
            _frameClock = default;
        }
        if (!_drawable.SetAppearance(theme.Visualization, settings, theme.IsLight))
        {
            if (frameRateChanged) SyncFrameLoop();
            return;
        }
        IsVisible = settings.ShowSpectrum && settings.SpectrumWidthPercent > 0;
        SyncFrameLoop();
        if (!IsVisible) ResetLevels();
        _graphics.Invalidate();
        VisualizationChanged?.Invoke();
    }

    /// <summary>Subscribes to a display clock and obtains spectrum snapshots during playback.</summary>
    public void Start(Func<ISpectrumSource?> getSource)
    {
        if (_getSource is not null)
        {
            return;
        }

        _getSource = getSource;
        SyncFrameLoop();
    }

    /// <summary>Releases the display clock and clears the visual state.</summary>
    public void Stop()
    {
        if (_getSource is null)
        {
            return;
        }

        _getSource = null;
        SyncFrameLoop();
        ResetLevels();
        _graphics.Invalidate();
        VisualizationChanged?.Invoke();
    }

    private void SyncFrameLoop()
    {
        bool run = IsSpectrumActive || _drawQueue is not null || (_getSource is not null && _drawPlayheads is not null);
        if (run != _frameLoopRunning)
        {
            _frameLoopRunning = run;
            if (run) StartFrameLoop();
            else StopFrameLoop();
        }
        ApplyNativeFrameRateHint();
    }

    private void ResetLevels()
    {
        _version = 0;
        _frameClock = default;
        Array.Clear(_levels);
        _drawable.Reset();
    }

    /// <summary>Reads the next audible spectrum and applies elapsed-time smoothing.</summary>
    private void DrawFrame(double time)
    {
        _drawPlayheads?.Invoke();
        if (_drawQueue is { } queue && _queueFrameClock.TryAdvance(time, 120, out _)) queue(time);
        if (!IsVisible || _getSource?.Invoke() is not { } mix ||
            !_frameClock.TryAdvance(time, _framesPerSecond, out float elapsed))
        {
            return;
        }

        mix.TryReadSpectrum(_levels, ref _version);
        if (_drawable.Advance(_levels, elapsed, out bool barsChanged))
        {
            _graphics.Invalidate();
            if (barsChanged) VisualizationChanged?.Invoke();
        }
    }

    private sealed class QueueSpectrum(SpectrumDrawable source) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect) => source.DrawQueueBackground(canvas, dirtyRect);
    }

    /// <summary>Starts the display clock for the current platform.</summary>
    partial void StartFrameLoop();
    /// <summary>Stops the display clock and releases its callback.</summary>
    partial void StopFrameLoop();
    partial void ObserveNativeGraphicsView();
    partial void ApplyNativeFrameRateHint();

#if ANDROID || IOS || MACCATALYST
    private int ActiveSpectrumFrameRate => IsSpectrumActive ? _framesPerSecond : 0;
#endif

#if !ANDROID && !WINDOWS && !IOS && !MACCATALYST
    private IDispatcherTimer? _displayTimer;

    /// <summary>Starts the display clock for the current platform.</summary>
    partial void StartFrameLoop()
    {
        _displayTimer = Dispatcher.CreateTimer();
        _displayTimer.Interval = TimeSpan.FromSeconds(1.0 / 60);
        _displayTimer.Tick += OnDisplayTimer;
        _displayTimer.Start();
    }

    /// <summary>Stops the display clock and releases its callback.</summary>
    partial void StopFrameLoop()
    {
        if (_displayTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnDisplayTimer;
            _displayTimer = null;
        }
    }

    /// <summary>Supplies a monotonic timestamp for the fallback animation.</summary>
    private void OnDisplayTimer(object? sender, EventArgs args) =>
        DrawFrame((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency);
#endif
}
