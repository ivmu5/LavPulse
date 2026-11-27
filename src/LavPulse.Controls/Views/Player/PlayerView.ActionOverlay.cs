namespace Laveyana.LavPulse.Controls;

public sealed partial class PlayerView
{
    private readonly ContentView _actionOverlayHost = new() { IsVisible = false, ZIndex = 100 };
    private TaskCompletionSource<string?>? _actionOverlayResult;
    private bool _bodyWasEnabled, _panelWasEnabled, _bodyWasTransparent, _panelWasTransparent;

    /// <summary>Displays themed actions within the player window, without an OS dialog.</summary>
    public Task<string?> ChooseActionAsync(string title, IReadOnlyList<string> choices, Point? anchor = null) =>
        ShowActionOverlayAsync(title, null, choices, null, null, anchor);

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
        IReadOnlyList<string> choices, string? accept, string? cancel, Point? anchor = null)
    {
        // Keep one owner; concurrent actions must not replace an awaiting confirmation.
        if (_actionOverlayResult is not null) return Task.FromResult<string?>(null);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heading = MixerStyle.Label(title, 18, bold: true);
        heading.LineBreakMode = LineBreakMode.WordWrap;
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
        var body = new VerticalStackLayout { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(message))
            body.Add(MixerStyle.Label(message, 13, MixerColor.Muted));
        foreach (string choice in choices)
        {
            var button = MixerStyle.Button(choice);
            button.HorizontalOptions = LayoutOptions.Fill;
            button.LineBreakMode = LineBreakMode.WordWrap;
            button.Clicked += (_, _) => CompleteOverlay(result, choice);
            body.Add(button);
        }
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

        var layout = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            RowSpacing = 10
        };
        layout.Add(heading);
        var scroll = new ScrollView { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Default };
        layout.Add(scroll, row: 1);
        layout.Add(footer, row: 2);

        bool anchored = anchor is { } p && double.IsFinite(p.X) && double.IsFinite(p.Y);
        var card = new Border
        {
            HorizontalOptions = anchored ? LayoutOptions.Start : LayoutOptions.Center,
            VerticalOptions = anchored ? LayoutOptions.Start : LayoutOptions.Center,
            Opacity = anchored ? 0 : 1
        };
        MixerStyle.StyleCard(card, layout);
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
        void Place()
        {
            if (!ReferenceEquals(_actionOverlayResult, result) || overlay.Width <= 32 || overlay.Height <= 32) return;
            double width = Math.Min(anchored ? 320 : 520, overlay.Width - 32);
            if (card.WidthRequest != width) card.WidthRequest = width;
            double maximumHeight = overlay.Height - 32;
            if (card.MaximumHeightRequest != maximumHeight) card.MaximumHeightRequest = maximumHeight;
            double bodyHeight = Math.Max(24, overlay.Height - 160);
            if (scroll.MaximumHeightRequest != bodyHeight) scroll.MaximumHeightRequest = bodyHeight;
            if (!anchored || card.Width <= 0 || card.Height <= 0) return;
            var point = anchor!.Value;
            var (x, y) = TrackListLayout.ContextMenuPosition(point.X, point.Y,
                overlay.Width, overlay.Height, card.Width, card.Height);
            card.TranslationX = x;
            card.TranslationY = y;
            if (revealed) return;
            revealed = true;
            card.AnchorX = Math.Clamp((point.X - x) / card.Width, 0, 1);
            card.AnchorY = Math.Clamp((point.Y - y) / card.Height, 0, 1);
            new Animation(v => { card.Opacity = v; card.Scale = .96 + .04 * v; }, 0, 1)
                .Commit(card, "ContextMenuOpen", 16, 160, Easing.CubicOut);
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

    private void CompleteOverlay(TaskCompletionSource<string?> result, string? choice)
    {
        if (!ReferenceEquals(_actionOverlayResult, result)) return;
        _actionOverlayResult = null;
        _actionOverlayHost.IsVisible = false;
        _actionOverlayHost.Content = null;
        _body.InputTransparent = _bodyWasTransparent;
        _panelHost.InputTransparent = _panelWasTransparent;
        _body.IsEnabled = _bodyWasEnabled;
        _panelHost.IsEnabled = _panelWasEnabled;
        result.TrySetResult(choice);
    }
}
