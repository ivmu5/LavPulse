using Microsoft.Maui.Platform;
using Microsoft.Maui.ApplicationModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;
using MauiWindow = Microsoft.Maui.Controls.Window;
using NativeAutomation = Microsoft.UI.Xaml.Automation.AutomationProperties;
using NativeButton = Microsoft.UI.Xaml.Controls.Button;
using NativeBorder = Microsoft.UI.Xaml.Controls.Border;
using NativePanel = Microsoft.UI.Xaml.Controls.Panel;
using NativeStack = Microsoft.UI.Xaml.Controls.StackPanel;
using NativeWindow = Microsoft.UI.Xaml.Window;
using NativeThickness = Microsoft.UI.Xaml.Thickness;
using NativeColor = Windows.UI.Color;
using NativeGrid = Microsoft.UI.Xaml.Controls.Grid;
using NativeImage = Microsoft.UI.Xaml.Controls.Image;
using NativeText = Microsoft.UI.Xaml.Controls.TextBlock;
using NativeKeyboardAccelerator = Microsoft.UI.Xaml.Input.KeyboardAccelerator;
using NativeKeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode;
using NativeSolidBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Owns the Windows caption overlay, resize limits and fullscreen state.</summary>
public sealed partial class WindowsWindowChrome
{
    public const int ContentInset = 44;
    private const int MinimumWidth = 720;
    private const int MinimumHeight = 660;
    private const int MaximumWidth = 1600;
    private const int MaximumHeight = 1100;
    private const int ButtonWidth = 36;
    private const int ButtonSpacing = 3;
    private const int ToolbarPadding = 3;
    private const int ToolbarRight = 8;
    private const int ToolbarWidth = ButtonWidth * 4 + ButtonSpacing * 3 + ToolbarPadding * 2 + ToolbarRight;

    private static readonly UISettings SystemColors = new();

    internal static AppTheme SystemTheme
    {
        get
        {
            var background = SystemColors.GetColorValue(UIColorType.Background);
            return (5 * background.G + 2 * background.R + background.B) > 8 * 128 ? AppTheme.Light : AppTheme.Dark;
        }
    }

    private readonly MauiWindow _window;
    private readonly NativeWindow _native;
    private readonly NativePanel _root;
    private readonly AppWindow _appWindow;
    private readonly OverlappedPresenter _windowed;
    private readonly nint _hwnd;
    private readonly NativeGrid _caption;
    private readonly NativeGrid _dragRegion;
    private readonly NativeBorder _toolbar;
    private readonly NativeStack _captionButtons;
    private readonly NativeText _brandText;
    private readonly PlayerView? _player;
    private MixerTheme _theme = MixerTheme.Default;
    private readonly NativeButton _maximize;
    private readonly NativeButton _fullscreen;
    private readonly NativeKeyboardAccelerator _fullscreenKey;
    private readonly NativeKeyboardAccelerator _escapeKey;
    private readonly WindowsClientFrame? _legacyFrame;
    private RectInt32? _restoreBounds;
    private bool _restoreMaximized;
    private bool IsFullscreen => _appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>Configures the native caption before MAUI measures its navigation root.</summary>
    public static void PrepareNativeCaption(NativeWindow native)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        native.ExtendsContentIntoTitleBar = true;
        // WinUI uses our explicit title region and leaves the buttons to the LavPulse overlay.
        native.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
    }

    /// <summary>Sets logical window sizes before activation and installs native controls when the root is ready.</summary>
    public static void Configure(MauiWindow window, Action? systemThemeChanged = null)
    {
        window.Width = 1120;
        window.Height = 790;
        window.MinimumWidth = MinimumWidth;
        window.MinimumHeight = MinimumHeight;
        window.MaximumWidth = MaximumWidth;
        window.MaximumHeight = MaximumHeight;
        window.Created += (_, _) =>
        {
            if (window.Handler?.PlatformView is NativeWindow native && native.Content is NativePanel root &&
                native.AppWindow.Presenter is OverlappedPresenter windowed)
            {
                _ = new WindowsWindowChrome(window, native, root, windowed);
                if (systemThemeChanged is not null)
                {
                    void OnSystemColorsChanged(UISettings sender, object args) =>
                        native.DispatcherQueue.TryEnqueue(() => systemThemeChanged());
                    SystemColors.ColorValuesChanged += OnSystemColorsChanged;
                    window.Destroying += (_, _) => SystemColors.ColorValuesChanged -= OnSystemColorsChanged;
                }
            }
        };
    }

    private WindowsWindowChrome(MauiWindow window, NativeWindow native, NativePanel root, OverlappedPresenter windowed)
    {
        _window = window;
        _native = native;
        _root = root;
        _appWindow = native.AppWindow;
        _windowed = windowed;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            _legacyFrame = new WindowsClientFrame(_hwnd, ContentInset, ToolbarWidth);
        }

        _captionButtons = new NativeStack { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal, Spacing = ButtonSpacing };
        _captionButtons.Children.Add(CreateButton("\uE921", "Свернуть", Minimize));
        _maximize = CreateButton("\uE922", "Развернуть", ToggleMaximize);
        _fullscreen = CreateButton("\uE92D", "На весь экран (F11)", ToggleFullscreen);
        _captionButtons.Children.Add(_maximize);
        _captionButtons.Children.Add(_fullscreen);
        var close = CreateButton("\uE8BB", "Закрыть", _native.Close);
        close.Resources["ButtonBackgroundPointerOver"] = Brush(220, 180, 36, 53);
        _captionButtons.Children.Add(close);
        close.Resources["ButtonForegroundPointerOver"] = Brush(255, 255, 255, 255);
        _toolbar = new NativeBorder
        {
            Child = _captionButtons,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Margin = new NativeThickness(0, 0, ToolbarRight, 0),
            Padding = new NativeThickness(ToolbarPadding),
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(8)
        };
        _caption = new NativeGrid
        {
            Height = ContentInset,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top,
            RenderTransform = _captionTranslation
        };
        // The system consumes pointer input in the registered title area. Keep the toolbar outside it.
        _dragRegion = new NativeGrid
        {
            Background = Brush(0, 0, 0, 0),
            Margin = new NativeThickness(0, 0, ToolbarWidth, 0)
        };
        _dragRegion.Children.Add(CreateBrand(window.Title, out _brandText));
        _caption.Children.Add(_dragRegion);
        _caption.Children.Add(_toolbar);
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_caption, 1000);
        // MAUI's root panel keeps overlays above the page and any modal pages.
        _player = (window.Page as ContentPage)?.Content as PlayerView;
        if (_player is not null) _player.ThemeChanged += ApplyTheme;
        ApplyTheme(_player?.Theme ?? _theme);
        _root.Children.Add(_caption);
        _dragRegion.Loaded += OnDragRegionLoaded;
        AttachFullscreenCaption();

        _fullscreenKey = new NativeKeyboardAccelerator { Key = VirtualKey.F11 };
        _fullscreenKey.Invoked += (_, args) => { ToggleFullscreen(); args.Handled = true; };
        _escapeKey = new NativeKeyboardAccelerator { Key = VirtualKey.Escape };
        _escapeKey.Invoked += (_, args) =>
        {
            if (IsFullscreen)
            {
                ExitFullscreen();
                args.Handled = true;
            }
        };
        // The fullscreen button has its own tooltip. Root shortcuts should not label the content area.
        _root.KeyboardAcceleratorPlacementMode = NativeKeyboardAcceleratorPlacementMode.Hidden;
        _root.KeyboardAccelerators.Add(_fullscreenKey);
        _root.KeyboardAccelerators.Add(_escapeKey);

        ConfigureWindowedCaption();
        ApplyMinimumSize();
        FitInitialWindow();
        _appWindow.Changed += OnWindowChanged;
        _window.DisplayDensityChanged += OnDensityChanged;
        _native.Closed += OnClosed;
        UpdateButtons();
    }

    /// <summary>Displays the app ribbon on the same line as the window controls.</summary>
    private static NativeStack CreateBrand(string? title, out NativeText text)
    {
        var brand = new NativeStack
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Margin = new NativeThickness(14, 0, 0, 0),
            Spacing = 8
        };
        brand.Children.Add(new NativeImage
        {
            Source = new BitmapImage(new Uri("ms-appx:///lavpulse_mark.png")),
            Width = 44, Height = 26,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
        });
        text = new NativeText
        {
            Text = title ?? "LavPulse", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };
        brand.Children.Add(text);
        return brand;
    }

    private void ApplyTheme(MixerTheme theme)
    {
        _theme = theme;
        _root.RequestedTheme = theme.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        _caption.Background = Brush(theme[MixerColor.Background], 146);
        _toolbar.Background = Brush(theme[MixerColor.Card], 230);
        var foreground = Brush(theme[MixerColor.Text]);
        _brandText.Foreground = foreground;
        foreach (var button in _captionButtons.Children.OfType<NativeButton>())
            button.Foreground = foreground;
        if (!IsFullscreen) WindowsClientFrame.ApplyBorderColor(_hwnd, theme[MixerColor.Border]);
    }

    private void OnDragRegionLoaded(object sender, RoutedEventArgs args)
    {
        if (!IsFullscreen) ConfigureWindowedCaption();
    }

    private void ConfigureWindowedCaption()
    {
        if (_legacyFrame is not null)
        {
            if (!_windowed.HasBorder || _windowed.HasTitleBar)
            {
                _windowed.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            }
            _legacyFrame.Refresh(_theme[MixerColor.Border]);
            return;
        }

        PrepareNativeCaption(_native);
        if (_dragRegion.IsLoaded) _native.SetTitleBar(_dragRegion);
        WindowsClientFrame.ApplyBorderColor(_hwnd, _theme[MixerColor.Border]);
    }

    private void Minimize()
    {
        if (IsFullscreen) ExitFullscreen();
        _windowed.Minimize();
    }

    private void ToggleMaximize()
    {
        if (IsFullscreen)
        {
            ExitFullscreen();
            _windowed.Maximize();
        }
        else if (_windowed.State == OverlappedPresenterState.Maximized)
        {
            _windowed.Restore();
        }
        else
        {
            _windowed.Maximize();
        }
        UpdateButtons();
    }

    private void ToggleFullscreen()
    {
        if (IsFullscreen)
        {
            ExitFullscreen();
            return;
        }

        _restoreMaximized = _windowed.State == OverlappedPresenterState.Maximized;
        var position = _appWindow.Position;
        var size = _appWindow.Size;
        _restoreBounds = _restoreMaximized ? null : new RectInt32(position.X, position.Y, size.Width, size.Height);
        if (_legacyFrame is null) _native.SetTitleBar(null);
        else _legacyFrame.IsFullscreen = true;
        _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        SetCaptionVisible(false, animate: true);
        UpdateButtons();
    }

    private void ExitFullscreen()
    {
        if (_legacyFrame is not null) _legacyFrame.IsFullscreen = false;
        _appWindow.SetPresenter(_windowed);
        if (_restoreMaximized)
        {
            _windowed.Maximize();
        }
        else
        {
            _windowed.Restore();
            if (_restoreBounds is { } bounds) _appWindow.MoveAndResize(bounds);
        }
        SetCaptionVisible(true, animate: false);
        ConfigureWindowedCaption();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool fullscreen = IsFullscreen;
        bool maximized = !fullscreen && _windowed.State == OverlappedPresenterState.Maximized;
        UpdateButton(_maximize, maximized ? "\uE923" : "\uE922", maximized ? "Восстановить размер окна" : "Развернуть");
        UpdateButton(_fullscreen, fullscreen ? "\uE92C" : "\uE92D",
            fullscreen ? "Выйти из полноэкранного режима (F11 / Esc)" : "На весь экран (F11)");
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidSizeChange) UpdateButtons();
        if (args.DidPositionChange) ApplyMinimumSize();
    }

    private void OnDensityChanged(object? sender, DisplayDensityChangedEventArgs args)
    {
        ApplyMinimumSize();
        _window.Handler?.UpdateValue(nameof(MauiWindow.MinimumWidth));
        _window.Handler?.UpdateValue(nameof(MauiWindow.MaximumWidth));
    }

    /// <summary>Allows a smaller minimum on monitors with less logical space than the preferred size.</summary>
    private void ApplyMinimumSize()
    {
        if (DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest) is not { } display) return;
        double scale = _native.GetDisplayDensity();
        var area = display.WorkArea;
        _window.MinimumWidth = Math.Min(MinimumWidth, Math.Floor(area.Width / scale));
        _window.MinimumHeight = Math.Min(MinimumHeight, Math.Floor(area.Height / scale));
    }

    private void FitInitialWindow()
    {
        if (DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest) is not { } display) return;
        var area = display.WorkArea;
        var size = _appWindow.Size;
        if (size.Width <= area.Width && size.Height <= area.Height) return;
        int width = Math.Min(size.Width, area.Width);
        int height = Math.Min(size.Height, area.Height);
        _appWindow.MoveAndResize(new RectInt32((area.Width - width) / 2, (area.Height - height) / 2, width, height), display);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_player is not null) _player.ThemeChanged -= ApplyTheme;
        DetachFullscreenCaption();
        _legacyFrame?.Dispose();
        _appWindow.Changed -= OnWindowChanged;
        _window.DisplayDensityChanged -= OnDensityChanged;
        _native.Closed -= OnClosed;
        _dragRegion.Loaded -= OnDragRegionLoaded;
        _root.KeyboardAccelerators.Remove(_fullscreenKey);
        _root.KeyboardAccelerators.Remove(_escapeKey);
        _root.Children.Remove(_caption);
    }

    private static NativeButton CreateButton(string glyph, string description, Action action)
    {
        var button = new NativeButton
        {
            Width = ButtonWidth,
            Height = 32,
            Padding = new NativeThickness(0),
            BorderThickness = new NativeThickness(0),
            HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Background = Brush(0, 0, 0, 0),
            Content = new Microsoft.UI.Xaml.Controls.FontIcon { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 }
        };
        button.Click += (_, _) => action();
        UpdateButton(button, glyph, description);
        return button;
    }

    private static void UpdateButton(NativeButton button, string glyph, string description)
    {
        ((Microsoft.UI.Xaml.Controls.FontIcon)button.Content).Glyph = glyph;
        NativeAutomation.SetName(button, description);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, description);
    }

    private static NativeSolidBrush Brush(Microsoft.Maui.Graphics.Color color, byte alpha = 255)
    {
        color.ToRgb(out byte red, out byte green, out byte blue);
        return Brush(alpha, red, green, blue);
    }

    private static NativeSolidBrush Brush(byte a, byte r, byte g, byte b) => new(NativeColor.FromArgb(a, r, g, b));
}
