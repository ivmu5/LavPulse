using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class MasterSpectrumView : ContentView
{
    private readonly SpectrumDrawable _drawable = new();
    private readonly float[] _levels = new float[PlaybackFormat.SpectrumBars];
    private readonly GraphicsView _graphics;
    private long _version;
    private double _lastFrame = double.NaN;
    private Func<ISpectrumSource?>? _getSource;

    public MasterSpectrumView()
    {
        InputTransparent = true;
        _graphics = new GraphicsView { Drawable = _drawable, InputTransparent = true };
        Content = _graphics;
    }

    internal void SetAppearance(PlayerColors colors, PlayerSettings settings)
    {
        _drawable.SetAppearance(colors, settings.SpectrumColors, settings.ShowPeaks,
            settings.SpectrumLevelBrightness ? settings.SpectrumMinimumBrightnessPercent : null);
        IsVisible = settings.ShowSpectrum;
        _graphics.Invalidate();
    }

    /// <summary>Subscribes to a display clock and obtains spectrum snapshots during playback.</summary>
    public void Start(Func<ISpectrumSource?> getSource)
    {
        if (_getSource is not null)
        {
            return;
        }

        _getSource = getSource;
        StartFrameLoop();
    }

    /// <summary>Releases the display clock and clears the visual state.</summary>
    public void Stop()
    {
        if (_getSource is null)
        {
            return;
        }

        _getSource = null;
        StopFrameLoop();
        _version = 0;
        _lastFrame = double.NaN;
        Array.Clear(_levels);
        _drawable.Reset();
        _graphics.Invalidate();
    }

    /// <summary>Reads the next audible spectrum and applies elapsed-time smoothing.</summary>
    private void DrawFrame(double time)
    {
        if (_getSource?.Invoke() is not { } mix)
        {
            return;
        }

        float elapsed = double.IsNaN(_lastFrame) ? 1 / 60f : (float)Math.Clamp(time - _lastFrame, 0, 0.1);
        if (elapsed <= 0)
        {
            return;
        }

        _lastFrame = time;
        mix.TryReadSpectrum(_levels, ref _version);
        if (_drawable.Advance(_levels, elapsed))
        {
            _graphics.Invalidate();
        }
    }

    /// <summary>Starts the display clock for the current platform.</summary>
    partial void StartFrameLoop();
    /// <summary>Stops the display clock and releases its callback.</summary>
    partial void StopFrameLoop();

#if !ANDROID && !WINDOWS
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
