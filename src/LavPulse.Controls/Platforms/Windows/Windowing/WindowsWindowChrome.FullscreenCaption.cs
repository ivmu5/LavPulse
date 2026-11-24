using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using NativePointerDeviceType = Microsoft.UI.Input.PointerDeviceType;
using NativeVisibility = Microsoft.UI.Xaml.Visibility;

namespace Laveyana.LavPulse.Controls;

public sealed partial class WindowsWindowChrome
{
    private const int CaptionRevealHeight = 8;
    private const int CaptionHideThreshold = ContentInset + 8;
    private const double CaptionAnimationMilliseconds = 180;

    private readonly TranslateTransform _captionTranslation = new();
    private readonly UISettings _uiSettings = new();
    private bool _captionVisible = true;
    private long _captionAnimationStart;
    private double _captionAnimationFrom;
    private SpectrumFrameClock _captionClock;
    private int _captionRate;

    private void AttachFullscreenCaption()
    {
        // The top edge needs to receive pointer input even across empty parts of the root.
        _root.Background ??= Brush(0, 0, 0, 0);
        _root.AddHandler(UIElement.PointerMovedEvent, (PointerEventHandler)OnRootPointerChanged, handledEventsToo: true);
        _root.PointerEntered += OnRootPointerChanged;
        _root.PointerExited += OnRootPointerChanged;
        _native.Activated += OnWindowActivated;
        _native.VisibilityChanged += OnWindowVisibilityChanged;
    }

    private void DetachFullscreenCaption()
    {
        CompositionTarget.Rendering -= OnCaptionAnimationFrame;
        _root.RemoveHandler(UIElement.PointerMovedEvent, (PointerEventHandler)OnRootPointerChanged);
        _root.PointerEntered -= OnRootPointerChanged;
        _root.PointerExited -= OnRootPointerChanged;
        _native.Activated -= OnWindowActivated;
        _native.VisibilityChanged -= OnWindowVisibilityChanged;
    }

    private void OnRootPointerChanged(object sender, PointerRoutedEventArgs args)
    {
        if (!IsFullscreen || args.Pointer.PointerDeviceType != NativePointerDeviceType.Mouse) return;

        var pointer = args.GetCurrentPoint(_root);
        var position = pointer.Position;
        bool inside = position.X >= 0 && position.X < _root.ActualWidth &&
            position.Y >= 0 && position.Y < _root.ActualHeight;
        if (_captionVisible)
        {
            if (!inside || position.Y >= CaptionHideThreshold) SetCaptionVisible(false, animate: true);
        }
        else if (inside && position.Y <= CaptionRevealHeight && !pointer.Properties.IsLeftButtonPressed)
        {
            SetCaptionVisible(true, animate: true);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (IsFullscreen && args.WindowActivationState == WindowActivationState.Deactivated)
        {
            SetCaptionVisible(false, animate: true);
        }
    }

    private void OnWindowVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        if (!args.Visible && IsFullscreen)
        {
            SetCaptionVisible(false, animate: false);
        }
    }

    private void SetCaptionVisible(bool visible, bool animate)
    {
        if (animate && _captionVisible == visible) return;
        _captionVisible = visible;
        CompositionTarget.Rendering -= OnCaptionAnimationFrame;
        if (visible) _caption.Visibility = NativeVisibility.Visible;

        double target = visible ? 0 : -ContentInset;
        if (!animate || !_uiSettings.AnimationsEnabled || _captionTranslation.Y == target)
        {
            CompleteCaptionAnimation(target);
            return;
        }

        _captionAnimationFrom = _captionTranslation.Y;
        _captionAnimationStart = Stopwatch.GetTimestamp();
        _captionClock = default;
        CompositionTarget.Rendering += OnCaptionAnimationFrame;
    }

    private void OnCaptionAnimationFrame(object? sender, object args)
    {
        double target = _captionVisible ? 0 : -ContentInset;
        double progress = Stopwatch.GetElapsedTime(_captionAnimationStart).TotalMilliseconds / CaptionAnimationMilliseconds;
        int rate = _player?.Spectrum.FramesPerSecond ?? 0;
        if (_captionRate != rate)
        {
            _captionRate = rate;
            _captionClock = default;
        }
        if (args is not RenderingEventArgs frame ||
            !_captionClock.TryAdvance(frame.RenderingTime.TotalSeconds, rate, out _)) return;

        if (progress >= 1)
        {
            CompleteCaptionAnimation(target);
            return;
        }
        double remaining = 1 - progress;
        _captionTranslation.Y = target + (_captionAnimationFrom - target) * remaining * remaining * remaining;
    }

    private void CompleteCaptionAnimation(double target)
    {
        CompositionTarget.Rendering -= OnCaptionAnimationFrame;
        _captionTranslation.Y = target;
        _caption.Visibility = _captionVisible ? NativeVisibility.Visible : NativeVisibility.Collapsed;
    }
}
