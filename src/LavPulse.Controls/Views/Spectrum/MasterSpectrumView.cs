using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

public sealed partial class MasterSpectrumView : ContentView
{
    private readonly SpectrumDrawable _drawable = new();
    private readonly float[] _levels = new float[PlaybackFormat.SpectrumBars];
    private readonly GraphicsView _graphics;
    private long _version;
    private SpectrumFrameClock _frameClock;
    private int _framesPerSecond;
    private Func<ISpectrumSource?>? _getSource;
    private Action<double>? _drawUi;
    private VisualElement? _uiFrameTarget;
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

    internal int FramesPerSecond => _framesPerSecond;
    private bool NeedsFrames => IsSpectrumActive || _drawUi is not null;

    internal void SetUiCallback(VisualElement target, Action<double>? drawUi)
    {
        _uiFrameTarget = target;
        _drawUi = drawUi;
        SyncFrameLoop();
    }

    internal void RestartDisplayClock()
    {
        _frameClock = default;
        if (_frameLoopRunning)
        {
            StopFrameLoop();
            _frameLoopRunning = false;
        }
        SyncFrameLoop(forceFrameRateHint: true);
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

    private void SyncFrameLoop(bool forceFrameRateHint = false)
    {
        bool run = NeedsFrames;
        if (run != _frameLoopRunning)
        {
            _frameLoopRunning = run;
            _frameClock = default;
            if (run) StartFrameLoop();
            else StopFrameLoop();
        }
        ApplyNativeFrameRateHint(forceFrameRateHint);
    }

    private void ResetLevels()
    {
        _version = 0;
        Array.Clear(_levels);
        _drawable.Reset();
    }

    /// <summary>Reads the next audible spectrum and applies elapsed-time smoothing.</summary>
    private void DrawFrame(double time)
    {
        if (!_frameClock.TryAdvance(time, _framesPerSecond, out float elapsed)) return;
        _drawUi?.Invoke(time);
        if (!IsVisible || _getSource?.Invoke() is not { } mix)
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
    partial void ApplyNativeFrameRateHint(bool force);

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
