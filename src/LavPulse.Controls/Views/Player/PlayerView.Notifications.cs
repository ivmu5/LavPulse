#if WINDOWS
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.LavPulse.Controls;

public sealed record NotificationAction(string Text, Action Invoke);

/// <summary>One UI-thread-owned card. Active operations cannot be dismissed accidentally.</summary>
public sealed class PlayerNotification
{
    private readonly PlayerView _owner;
    private readonly Label _title = MixerStyle.Label("", 15, bold: true);
    private readonly Label _message = MixerStyle.Label("", 12);
    private readonly ProgressBar _progress = new() { HeightRequest = 5, IsVisible = false };
    private readonly VerticalStackLayout _actions = new() { Spacing = 4 };
    private readonly Button _dismiss = MixerStyle.Button("Закрыть");
    private int _actionGeneration;
    internal Border Card { get; }
    internal bool Retained { get; private set; }
    internal bool Removed { get; set; }
    public bool IsRunning { get; private set; }

    internal PlayerNotification(PlayerView owner, string title, string message, bool running)
    {
        _owner = owner;
        IsRunning = running;
        _title.Text = title;
        _title.LineBreakMode = LineBreakMode.WordWrap;
        _message.Text = message;
        _message.LineBreakMode = LineBreakMode.WordWrap;
        _dismiss.IsVisible = !running;
        _dismiss.MinimumHeightRequest = 30;
        _dismiss.Padding = new Thickness(8, 4);
        _dismiss.FontSize = 12;
        _dismiss.Clicked += (_, _) => _owner.RemoveNotification(this);
        MixerStyle.UseColor(_progress, ProgressBar.ProgressColorProperty, MixerColor.Accent);
        var body = new VerticalStackLayout { Spacing = 6, Children = { _title, _message, _progress, _actions, _dismiss } };
        Card = new Border { StrokeThickness = 1, Padding = 12 };
        MixerStyle.StyleCard(Card, body);
        Card.StrokeShape = new RoundRectangle { CornerRadius = 12 };
    }

    public void Update(string message, double? progress = null, IReadOnlyList<NotificationAction>? actions = null)
    {
        if (Removed || !IsRunning) return;
        _message.Text = message;
        _progress.IsVisible = progress.HasValue;
        if (progress.HasValue) _progress.Progress = Math.Clamp(progress.Value, 0, 1);
        if (actions is not null) SetActions(actions);
    }

    public void Complete(string title, string message, IReadOnlyList<NotificationAction>? actions = null, bool retain = false)
    {
        if (Removed || !IsRunning) return;
        IsRunning = false;
        Retained = retain;
        _title.Text = title;
        _message.Text = message;
        _progress.IsVisible = false;
        _dismiss.IsVisible = true;
        SetActions(actions ?? []);
        _owner.NotificationCompleted(this);
    }

    private void SetActions(IReadOnlyList<NotificationAction> actions)
    {
        int generation = ++_actionGeneration;
        _actions.Children.Clear();
        foreach (var action in actions)
        {
            var button = MixerStyle.Button(action.Text);
            button.FontSize = 12;
            button.MinimumHeightRequest = 30;
            button.Padding = new Thickness(8, 4);
            button.LineBreakMode = LineBreakMode.WordWrap;
            button.Clicked += (_, _) => { if (!Removed && generation == _actionGeneration) action.Invoke(); };
            _actions.Children.Add(button);
        }
    }
}

public sealed partial class PlayerView
{
    private readonly VerticalStackLayout _notificationCards = new() { Spacing = 8 };
    private readonly List<PlayerNotification> _notifications = [];
    private ScrollView? _notificationOverlay;

    private void InitializeNotifications()
    {
        // The host occupies only the top-left card area, leaving the rest of
        // the player interactive. Modal action menus retain their higher ZIndex.
        _notificationOverlay = new ScrollView
        {
            ZIndex = 90, IsVisible = false, WidthRequest = 350,
            HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(12, WindowsWindowChrome.ContentInset + 8, 12, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Default,
            Content = _notificationCards
        };
        this.Add(_notificationOverlay);
        SizeChanged += (_, _) =>
        {
            if (Width > 0 && Height > 0)
            {
                _notificationOverlay.WidthRequest = Math.Min(350, Math.Max(1, Width - 24));
                _notificationOverlay.MaximumHeightRequest = Math.Max(1, Height - WindowsWindowChrome.ContentInset - 16);
            }
        };
    }

    public PlayerNotification ShowNotification(string title, string message, bool running = false)
    {
        var item = new PlayerNotification(this, title, message, running);
        _notifications.Insert(0, item);
        _notificationCards.Children.Insert(0, item.Card);
        if (_notificationOverlay is not null) _notificationOverlay.IsVisible = true;
        if (!running) NotificationCompleted(item);
        return item;
    }

    internal void NotificationCompleted(PlayerNotification item)
    {
        // Bound terminal history; running operations are never evicted.
        foreach (var old in _notifications.Where(n => !n.IsRunning && !n.Retained).Skip(4).ToArray())
            RemoveNotification(old);
        foreach (var old in _notifications.Where(n => !n.IsRunning && n.Retained).Skip(3).ToArray())
            RemoveNotification(old);
        if (!item.Retained)
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(8), () => RemoveNotification(item));
    }

    internal void RemoveNotification(PlayerNotification item)
    {
        if (item.Removed || item.IsRunning) return;
        RemoveCard(item);
    }

    private void RemoveCard(PlayerNotification item)
    {
        item.Removed = true;
        _notifications.Remove(item);
        _notificationCards.Children.Remove(item.Card);
        if (_notificationOverlay is not null) _notificationOverlay.IsVisible = _notifications.Count > 0;
    }

    public void ClearNotifications()
    {
        foreach (var item in _notifications.ToArray()) RemoveCard(item);
    }
}
#endif
