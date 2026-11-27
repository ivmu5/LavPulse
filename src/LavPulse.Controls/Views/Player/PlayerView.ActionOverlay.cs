namespace Laveyana.LavPulse.Controls;

/// <summary>A themed menu entry with a stable value, separate from the visible caption.</summary>
public sealed record ActionMenuOption(string Value, string Label, string? Icon = null,
    bool Selected = false, Color? Tint = null);

public sealed partial class PlayerView
{
    private readonly ContentView _actionOverlayHost = new() { IsVisible = false, ZIndex = 100 };
    private TaskCompletionSource<string?>? _actionOverlayResult;
    private OverlayRevealMotion? _overlayReveal;
    private bool _bodyWasEnabled, _panelWasEnabled, _bodyWasTransparent, _panelWasTransparent;

    /// <summary>Displays themed actions within the player window, without an OS dialog.</summary>
    public Task<string?> ChooseActionAsync(string title, IReadOnlyList<string> choices, Point? anchor = null) =>
        ChooseActionAsync(title, choices.Select(choice => new ActionMenuOption(choice, choice)).ToArray(), anchor);

    public Task<string?> ChooseActionAsync(string title, IReadOnlyList<ActionMenuOption> choices, Point? anchor = null) =>
        ShowActionOverlayAsync(title, null, choices, null, null, anchor);

    // Use the same overlay host as context menus; dropdowns never expand the
    // underlying panel or create a separate native window.
    internal static Task<string?> ChooseDropdownAsync(View trigger, string title, IReadOnlyList<ActionMenuOption> choices)
    {
        for (Element? element = trigger; element is not null; element = element.Parent)
            if (element is PlayerView player)
            {
                Rect? anchor = null;
                try { anchor = player.GetDropdownAnchor(trigger); }
                catch (Exception ex)
                {
                    // The native visual tree may be rearranged during rotation.
                    // Keep the menu usable with the centered fallback.
                    System.Diagnostics.Debug.WriteLine(ex);
                }
                return player.ShowActionOverlayAsync(title, null, choices, null, null, dropdownAnchor: anchor);
            }
        return Task.FromResult<string?>(null);
    }

    private Rect? GetDropdownAnchor(View trigger)
    {
        // Native coordinates account for scrolling, translated layers and
        // platform density; a missing handler falls back to a centered menu.
        if (trigger.Width <= 0 || trigger.Height <= 0) return null;
#if WINDOWS
        if (trigger.Handler?.PlatformView is global::Microsoft.UI.Xaml.UIElement source &&
            Handler?.PlatformView is global::Microsoft.UI.Xaml.UIElement root)
        {
            var bounds = source.TransformToVisual(root).TransformBounds(
                new global::Windows.Foundation.Rect(0, 0, trigger.Width, trigger.Height));
            return new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
#elif ANDROID
        if (trigger.Handler?.PlatformView is global::Android.Views.View source &&
            Handler?.PlatformView is global::Android.Views.View root)
        {
            var sourceLocation = new int[2];
            var rootLocation = new int[2];
            source.GetLocationOnScreen(sourceLocation);
            root.GetLocationOnScreen(rootLocation);
            double density = root.Resources?.DisplayMetrics?.Density ?? 1;
            if (density > 0)
                return new Rect((sourceLocation[0] - rootLocation[0]) / density,
                    (sourceLocation[1] - rootLocation[1]) / density, source.Width / density, source.Height / density);
        }
#elif IOS || MACCATALYST
        if (trigger.Handler?.PlatformView is global::UIKit.UIView source &&
            Handler?.PlatformView is global::UIKit.UIView root)
        {
            var bounds = source.ConvertRectToView(source.Bounds, root);
            return new Rect((double)bounds.X, (double)bounds.Y, (double)bounds.Width, (double)bounds.Height);
        }
#endif
        return null;
    }

    public async Task<bool> ConfirmActionAsync(string title, string message, string accept, string cancel = "Отмена") =>
        await ShowActionOverlayAsync(title, message, [], accept, cancel) == accept;

    public async Task NotifyActionAsync(string title, string message, string accept = "OK") =>
        await ShowActionOverlayAsync(title, message, [], accept, null);

    public bool HasActionOverlay => _actionOverlayResult is not null;

    public bool TryDismissActionOverlay()
    {
        if (_actionOverlayResult is not { } result) return false;
        CompleteOverlay(result, null);
        return true;
    }

    private Task<string?> ShowActionOverlayAsync(string title, string? message,
        IReadOnlyList<ActionMenuOption> choices, string? accept, string? cancel,
        Point? anchor = null, Rect? dropdownAnchor = null)
    {
        // Keep one owner; concurrent actions must not replace an awaiting confirmation.
        if (_actionOverlayResult is not null) return Task.FromResult<string?>(null);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool compact = choices.Count > 0;
        bool dropdown = dropdownAnchor is { } r && double.IsFinite(r.X) && double.IsFinite(r.Y) &&
            double.IsFinite(r.Width) && double.IsFinite(r.Height) && r.Width > 0 && r.Height > 0;
        bool anchored = dropdown || anchor is { } p && double.IsFinite(p.X) && double.IsFinite(p.Y);
        var heading = MixerStyle.Label(title, compact ? 14 : 18, bold: true);
        heading.LineBreakMode = compact ? LineBreakMode.TailTruncation : LineBreakMode.WordWrap;
        if (compact) heading.MaxLines = 2;
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
        var body = new VerticalStackLayout { Spacing = compact ? 2 : 6 };
        if (!string.IsNullOrWhiteSpace(message))
            body.Add(MixerStyle.Label(message, 13, MixerColor.Muted));
        foreach (var choice in choices)
        {
            var button = MixerStyle.Button(choice.Selected ? "✓  " + choice.Label : choice.Label, icon: choice.Icon);
            button.HorizontalOptions = LayoutOptions.Fill;
            button.LineBreakMode = LineBreakMode.WordWrap;
            button.FontSize = MixerStyle.PhoneFont(13, 12);
            button.MinimumHeightRequest = MixerStyle.IsPhone ? 40 : 34;
            button.Padding = new Thickness(7, 3);
            button.CornerRadius = 5;
            if (choice.Selected)
                MixerStyle.UseColor(button, BackgroundColorProperty, MixerColor.Selected);
            else if (choice.Tint is { } tint && tint.Alpha > 0)
            {
                button.RemoveDynamicResource(BackgroundColorProperty);
                button.BackgroundColor = tint;
            }
            MixerStyle.UseReadableButtonText(button);
            SemanticProperties.SetDescription(button, choice.Selected ? choice.Label + " (выбрано)" : choice.Label);
            button.Clicked += (_, _) => CompleteOverlay(result, choice.Value);
            body.Add(button);
        }
        bool showFooter = !compact || !anchored;
        var layout = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
            RowSpacing = compact ? 5 : 10
        };
        layout.Add(heading);
        var scroll = new ScrollView { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Default };
        layout.Add(scroll, row: 1);
        // An anchored menu is dismissed by tapping outside or navigating back.
        // Centered fallback menus retain an explicit Back button.
        if (showFooter)
        {
            layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var footer = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
            var primary = MixerStyle.Button(accept ?? "Назад", primary: true);
            primary.HorizontalOptions = LayoutOptions.Fill;
            primary.Clicked += (_, _) => CompleteOverlay(result, accept);
            footer.Add(primary);
            if (accept is not null && cancel is not null)
            {
                var dismiss = MixerStyle.IconButton("player_cancel.png", 44);
                SemanticProperties.SetDescription(dismiss, cancel);
                ToolTipProperties.SetText(dismiss, cancel);
                dismiss.Clicked += (_, _) => CompleteOverlay(result, null);
                footer.Add(dismiss, 1);
            }
            layout.Add(footer, row: 2);
        }

        var card = new Border
        {
            HorizontalOptions = anchored ? LayoutOptions.Start : LayoutOptions.Center,
            VerticalOptions = anchored ? LayoutOptions.Start : LayoutOptions.Center,
            Opacity = anchored ? 0 : 1,
            InputTransparent = anchored
        };
        MixerStyle.StyleCard(card, layout);
        if (compact) card.Padding = new Thickness(7, 6);
        MixerStyle.UseColor(card, BackgroundColorProperty, MixerColor.Overlay);
        var overlay = new Grid { Padding = anchored ? new Thickness(0) : new Thickness(16) };
        if (anchored)
        {
            var backdrop = new BoxView { Color = Colors.Transparent };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => CompleteOverlay(result, null);
            backdrop.GestureRecognizers.Add(tap);
            overlay.Add(backdrop);
        }
        else MixerStyle.UseColor(overlay, BackgroundColorProperty, MixerColor.Scrim);
        overlay.Add(card);
        bool revealed = false;
        double placedWidth = 0, placedHeight = 0;
        void Place()
        {
            if (!ReferenceEquals(_actionOverlayResult, result) || overlay.Width <= 32 || overlay.Height <= 32) return;
            // An anchored menu owns coordinates in the old visual tree.
            // Dismiss on rotation/resize rather than showing it at a stale row.
            if (anchored && placedWidth > 0 &&
                (Math.Abs(overlay.Width - placedWidth) > 2 || Math.Abs(overlay.Height - placedHeight) > 2))
            {
                CompleteOverlay(result, null);
                return;
            }
            placedWidth = overlay.Width;
            placedHeight = overlay.Height;
            double preferredWidth = dropdown ? Math.Clamp(dropdownAnchor!.Value.Width, 230, 320) :
                anchored ? 300 : compact ? 320 : 520;
            double width = Math.Min(preferredWidth, overlay.Width - 32);
            if (card.WidthRequest != width) card.WidthRequest = width;
            double maximumHeight = overlay.Height - 32;
            if (card.MaximumHeightRequest != maximumHeight) card.MaximumHeightRequest = maximumHeight;
            // Reserve actual header/footer sizes, including wrapped titles
            // and large text, instead of allowing the footer to be clipped.
            double fixedHeight = Math.Max(heading.Height, compact ? 22 : 28) +
                (showFooter ? Math.Max(44, layout.Children.OfType<Grid>().LastOrDefault()?.Height ?? 0) : 0) +
                card.Padding.VerticalThickness + layout.RowSpacing * (showFooter ? 2 : 1) + 4;
            double bodyHeight = Math.Max(24, maximumHeight - fixedHeight);
            if (scroll.MaximumHeightRequest != bodyHeight) scroll.MaximumHeightRequest = bodyHeight;
            if (!anchored || card.Width <= 0 || card.Height <= 0) return;
            var point = dropdown ? new Point(dropdownAnchor!.Value.X, dropdownAnchor.Value.Bottom) : anchor!.Value;
            var (x, y) = dropdown
                ? TrackListLayout.DropdownPosition(dropdownAnchor!.Value.X, dropdownAnchor.Value.Y,
                    dropdownAnchor.Value.Height, overlay.Width, overlay.Height, card.Width, card.Height)
                : TrackListLayout.ContextMenuPosition(point.X, point.Y,
                    overlay.Width, overlay.Height, card.Width, card.Height);
            card.TranslationX = x;
            card.TranslationY = y;
            if (revealed) return;
            revealed = true;
            bool upward = dropdown ? y + card.Height <= dropdownAnchor!.Value.Top :
                y + card.Height <= point.Y;
            _overlayReveal = new OverlayRevealMotion(card, layout, upward);
            if (_uiFramesSuspended) FinishOverlayReveal();
            else SyncUiFrames();
        }
        overlay.SizeChanged += (_, _) => Place();
        card.SizeChanged += (_, _) => Place();
        _actionOverlayResult = result;
        _bodyWasEnabled = _body.IsEnabled;
        _panelWasEnabled = _panelHost.IsEnabled;
        _bodyWasTransparent = _body.InputTransparent;
        _panelWasTransparent = _panelHost.InputTransparent;
        _actionOverlayHost.Content = overlay;
        _body.InputTransparent = _panelHost.InputTransparent = true;
        _body.IsEnabled = _panelHost.IsEnabled = false;
        _actionOverlayHost.IsVisible = true;
        Place();
        return result.Task;
    }

    private void AdvanceOverlayReveal(double time)
    {
        if (_overlayReveal is not { } motion || !motion.Advance(time)) return;
        _overlayReveal = null;
        SyncUiFrames();
    }

    private void FinishOverlayReveal()
    {
        if (_overlayReveal is not { } motion) return;
        _overlayReveal = null;
        motion.Finish();
    }

    // Full-size layout throughout the animation: only the clip and inner
    // translation change, so menu items do not remeasure or stretch per frame.
    private sealed class OverlayRevealMotion
    {
        private readonly Border _card;
        private readonly View _content;
        private readonly bool _upward;
        private readonly Microsoft.Maui.Controls.Shapes.RectangleGeometry _clip = new();
        private double? _start;

        public OverlayRevealMotion(Border card, View content, bool upward)
        {
            _card = card;
            _content = content;
            _upward = upward;
            card.Clip = _clip;
            Apply(0);
        }

        public bool Advance(double time)
        {
            _start ??= time;
            double progress = PlayerMotion.OverlayReveal(time - _start.Value);
            if (progress >= 1)
            {
                Finish();
                return true;
            }
            Apply(progress);
            return false;
        }

        private void Apply(double progress)
        {
            double height = Math.Max(0, _card.Height);
            double visible = height * progress;
            _clip.Rect = new Rect(0, _upward ? height - visible : 0, Math.Max(0, _card.Width), visible);
            _content.TranslationY = (_upward ? 1 : -1) * Math.Min(16, height * .15) * (1 - progress);
            _card.Opacity = Math.Min(1, progress * 2.5);
        }

        public void Finish()
        {
            _card.Clip = null;
            _card.Opacity = 1;
            _card.InputTransparent = false;
            _content.TranslationY = 0;
        }
    }

    private void CompleteOverlay(TaskCompletionSource<string?> result, string? choice)
    {
        if (!ReferenceEquals(_actionOverlayResult, result)) return;
        _actionOverlayResult = null;
        FinishOverlayReveal();
        _actionOverlayHost.IsVisible = false;
        _actionOverlayHost.Content = null;
        _body.InputTransparent = _bodyWasTransparent;
        _panelHost.InputTransparent = _panelWasTransparent;
        _body.IsEnabled = _bodyWasEnabled;
        _panelHost.IsEnabled = _panelWasEnabled;
        SyncUiFrames();
        result.TrySetResult(choice);
    }
}
