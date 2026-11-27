namespace Laveyana.LavPulse.Controls;

public sealed partial class PlayerView
{
    private readonly ContentView _actionOverlayHost = new() { IsVisible = false, ZIndex = 100 };
    private TaskCompletionSource<string?>? _actionOverlayResult;

    /// <summary>Displays themed actions within the player window, without an OS dialog.</summary>
    public Task<string?> ChooseActionAsync(string title, IReadOnlyList<string> choices) =>
        ShowActionOverlayAsync(title, null, choices, null, null);

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
        IReadOnlyList<string> choices, string? accept, string? cancel)
    {
        // Keep one owner; concurrent actions must not replace an awaiting confirmation.
        if (_actionOverlayResult is not null) return Task.FromResult<string?>(null);
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _actionOverlayResult = result;
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

        var card = new Border { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        MixerStyle.StyleCard(card, layout);
        MixerStyle.UseColor(card, BackgroundColorProperty, MixerColor.Overlay);
        var overlay = new Grid { Padding = 16, Children = { card } };
        MixerStyle.UseColor(overlay, BackgroundColorProperty, MixerColor.Scrim);
        overlay.SizeChanged += (_, _) =>
        {
            if (overlay.Width > 32) card.WidthRequest = Math.Min(520, overlay.Width - 32);
            if (overlay.Height > 32)
            {
                card.MaximumHeightRequest = overlay.Height - 32;
                scroll.MaximumHeightRequest = Math.Max(44, overlay.Height - 160);
            }
        };
        _actionOverlayHost.Content = overlay;
        _body.InputTransparent = _panelHost.InputTransparent = true;
        _actionOverlayHost.IsVisible = true;
        return result.Task;
    }

    private void CompleteOverlay(TaskCompletionSource<string?> result, string? choice)
    {
        if (!ReferenceEquals(_actionOverlayResult, result)) return;
        _actionOverlayResult = null;
        _actionOverlayHost.IsVisible = false;
        _actionOverlayHost.Content = null;
        _body.InputTransparent = _panelHost.InputTransparent = false;
        result.TrySetResult(choice);
    }
}
