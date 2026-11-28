#if WINDOWS
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.LavPulse.Controls;

public sealed record NotificationAction(string Text, Action Invoke);

/// <summary>UI-thread-owned card. Collapsing only hides details, not the operation.</summary>
public sealed class PlayerNotification
{
    private const string DetailMotion = "LavPulse.Notification.Detail";
    private const string CardMotion = "LavPulse.Notification.Card";
    private readonly PlayerView _owner;
    private readonly Label _title = MixerStyle.Label("", 15, bold: true);
    private readonly Label _message = MixerStyle.Label("", 12);
    private readonly Label _percent = MixerStyle.Label("", 11, MixerColor.Accent, bold: true);
    private readonly ProgressBar _progress = new() { HeightRequest = 5, IsVisible = false };
    private readonly VerticalStackLayout _actions = new() { Spacing = 4 };
    private readonly Button _dismiss = MixerStyle.Button("Закрыть");
    private readonly Button _collapse = MixerStyle.Button("⌃");
    private readonly Grid _details = new() { IsClippedToBounds = true };
    private readonly VerticalStackLayout _detailContent;
    private readonly VerticalStackLayout _summary;
    private readonly Grid _body;
    private int _actionGeneration, _detailGeneration, _cardGeneration;
    private bool _collapsed, _bottom, _right;
    internal Border Card { get; }
    internal bool Retained { get; private set; }
    internal bool Removed { get; set; }
    internal bool Removing { get; private set; }
    public bool IsRunning { get; private set; }

    internal PlayerNotification(PlayerView owner, string title, string message, bool running)
    {
        _owner = owner;
        IsRunning = running;
        _title.Text = title;
        _title.LineBreakMode = LineBreakMode.WordWrap;
        _title.VerticalOptions = LayoutOptions.Center;
        _message.Text = message;
        _message.LineBreakMode = LineBreakMode.WordWrap;
        _percent.IsVisible = false;
        _percent.VerticalOptions = LayoutOptions.Center;
        _dismiss.IsVisible = !running;
        CompactButton(_dismiss);
        CompactButton(_collapse);
        _collapse.WidthRequest = 32;
        _collapse.Clicked += (_, _) => SetCollapsed(!_collapsed);
        _dismiss.Clicked += (_, _) => _owner.RemoveNotification(this);
        MixerStyle.UseColor(_progress, ProgressBar.ProgressColorProperty, MixerColor.Accent);
        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 5
        };
        header.Add(_title);
        header.Add(_percent, 1);
        header.Add(_collapse, 2);
        _summary = new VerticalStackLayout { Spacing = 5, Children = { header, _progress } };
        _detailContent = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(0, 5), Children = { _message, _actions, _dismiss } };
        _details.Add(_detailContent);
        _body = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)] };
        _body.Add(_summary);
        _body.Add(_details, row: 1);
        Card = new Border { StrokeThickness = 1, Padding = 12 };
        MixerStyle.StyleCard(Card, _body);
        Card.StrokeShape = new RoundRectangle { CornerRadius = 12 };
        SetCorner(owner.NotificationPosition);
    }

    private static void CompactButton(Button button)
    {
        button.MinimumHeightRequest = 30;
        button.Padding = new Thickness(6, 3);
        button.FontSize = 12;
    }

    internal void SetCorner(NotificationCorner position)
    {
        _bottom = position is NotificationCorner.BottomLeft or NotificationCorner.BottomRight;
        _right = position is NotificationCorner.TopRight or NotificationCorner.BottomRight;
        // Keep the compact header at the anchored edge, with details growing
        // into the available window instead of towards the window border.
        Grid.SetRow(_summary, _bottom ? 1 : 0);
        Grid.SetRow(_details, _bottom ? 0 : 1);
        Card.AnchorX = _right ? 1 : 0;
        Card.AnchorY = _bottom ? 1 : 0;
        _details.AnchorY = _bottom ? 1 : 0;
        UpdateCollapseGlyph();
        FinishDetailMotion();
        if (!Removing)
        {
            ++_cardGeneration;
            Card.AbortAnimation(CardMotion);
            ResetCardTransform();
        }
    }

    private void UpdateCollapseGlyph()
    {
        _collapse.Text = _collapsed == _bottom ? "⌃" : "⌄";
        string description = _collapsed ? "Развернуть уведомление" : "Свернуть уведомление";
        SemanticProperties.SetDescription(_collapse, description);
        ToolTipProperties.SetText(_collapse, description);
    }

    private void SetCollapsed(bool collapsed)
    {
        if (Removed || Removing || collapsed == _collapsed) return;
        double from = _details.IsVisible ? Math.Max(0, _details.Height) : 0;
        if (_details.HeightRequest >= 0 && _details.IsVisible) from = _details.HeightRequest;
        _collapsed = collapsed;
        UpdateCollapseGlyph();
        int generation = ++_detailGeneration;
        _details.AbortAnimation(DetailMotion);
        double width = _body.Width;
        if (width <= 0 || !double.IsFinite(width))
        {
            FinishDetailMotion();
            return;
        }
        _details.IsVisible = true;
        _details.InputTransparent = true;
        _details.HeightRequest = -1;
        // Measure the latest content, including progress changes that
        // arrived while collapsed. The clipping wrapper owns the height.
        double natural = ((Microsoft.Maui.IView)_detailContent).Measure(width, double.PositiveInfinity).Height;
        if (!double.IsFinite(natural) || natural <= 0)
        {
            FinishDetailMotion();
            return;
        }
        double to = collapsed ? 0 : natural;
        _details.HeightRequest = from;
        var animation = new Animation(t =>
        {
            if (generation != _detailGeneration || Removed) return;
            double height = Math.Max(0, from + (to - from) * t);
            double fraction = Math.Clamp(height / Math.Max(1, natural), 0, 1);
            _details.HeightRequest = height;
            _detailContent.Opacity = fraction;
            _detailContent.TranslationY = (_bottom ? 1 : -1) * 9 * (1 - fraction);
        });
        animation.Commit(_details, DetailMotion, 16, 220, Easing.CubicInOut,
            (_, _) => { if (generation == _detailGeneration && !Removed) FinishDetailMotion(); });
    }

    private void FinishDetailMotion()
    {
        ++_detailGeneration;
        _details.AbortAnimation(DetailMotion);
        _details.IsVisible = !_collapsed;
        _details.HeightRequest = -1;
        _details.InputTransparent = _collapsed;
        _detailContent.Opacity = 1;
        _detailContent.TranslationY = 0;
    }

    internal void AnimateEntrance()
    {
        if (Removed || Removing) return;
        int generation = ++_cardGeneration;
        Card.AbortAnimation(CardMotion);
        Card.Opacity = 0;
        Card.Scale = .96;
        Card.TranslationX = _right ? 42 : -42;
        Card.TranslationY = _bottom ? 16 : -16;
        var animation = new Animation(t =>
        {
            if (generation != _cardGeneration || Removed) return;
            double rest = 1 - t;
            Card.Opacity = t;
            Card.Scale = .96 + .04 * t;
            Card.TranslationX = (_right ? 42 : -42) * rest;
            Card.TranslationY = (_bottom ? 16 : -16) * rest;
        });
        animation.Commit(Card, CardMotion, 16, 260, Easing.CubicOut,
            (_, _) => { if (generation == _cardGeneration && !Removed) ResetCardTransform(); });
    }

    internal void AnimateExit(Action finished)
    {
        if (Removing || Removed) return;
        Removing = true;
        Card.InputTransparent = true;
        int generation = ++_cardGeneration;
        Card.AbortAnimation(CardMotion);
        FinishDetailMotion();
        double opacity = Card.Opacity, scale = Card.Scale, x = Card.TranslationX, y = Card.TranslationY;
        var animation = new Animation(t =>
        {
            if (generation != _cardGeneration || Removed) return;
            Card.Opacity = opacity * (1 - t);
            Card.Scale = scale + (.96 - scale) * t;
            Card.TranslationX = x + ((_right ? 42 : -42) - x) * t;
            Card.TranslationY = y + ((_bottom ? 16 : -16) - y) * t;
        });
        animation.Commit(Card, CardMotion, 16, 180, Easing.CubicIn,
            (_, _) => { if (generation == _cardGeneration && !Removed) finished(); });
    }

    internal void CancelMotions()
    {
        ++_cardGeneration;
        ++_detailGeneration;
        Card.AbortAnimation(CardMotion);
        _details.AbortAnimation(DetailMotion);
    }

    private void ResetCardTransform()
    {
        Card.Opacity = Card.Scale = 1;
        Card.TranslationX = Card.TranslationY = 0;
    }

    public void Update(string message, double? progress = null, IReadOnlyList<NotificationAction>? actions = null)
    {
        if (Removed || !IsRunning) return;
        _message.Text = message;
        if (progress.HasValue)
        {
            _progress.IsVisible = _percent.IsVisible = true;
            _progress.Progress = Math.Clamp(progress.Value, 0, 1);
            _percent.Text = $"{_progress.Progress:P0}";
        }
        if (actions is not null) SetActions(actions);
    }

    public void Complete(string title, string message, IReadOnlyList<NotificationAction>? actions = null, bool retain = false)
    {
        if (Removed || !IsRunning) return;
        IsRunning = false;
        Retained = retain;
        _title.Text = title;
        _message.Text = message;
        _progress.IsVisible = _percent.IsVisible = false;
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
            CompactButton(button);
            button.LineBreakMode = LineBreakMode.WordWrap;
            button.Clicked += (_, _) => { if (!Removed && !Removing && generation == _actionGeneration) action.Invoke(); };
            _actions.Children.Add(button);
        }
    }
}

public sealed partial class PlayerView
{
    private readonly VerticalStackLayout _notificationCards = new() { Spacing = 8 };
    private readonly List<PlayerNotification> _notifications = [];
    private ScrollView? _notificationOverlay;
    internal NotificationCorner NotificationPosition { get; private set; } = NotificationCorner.TopLeft;
    private bool NotificationsAtBottom => NotificationPosition is NotificationCorner.BottomLeft or NotificationCorner.BottomRight;

    private void InitializeNotifications()
    {
        _notificationOverlay = new ScrollView
        {
            ZIndex = 90, IsVisible = false, WidthRequest = 350,
            VerticalScrollBarVisibility = ScrollBarVisibility.Default,
            Content = _notificationCards
        };
        this.Add(_notificationOverlay);
        ApplyNotificationPosition();
        SizeChanged += (_, _) => UpdateNotificationBounds();
    }

    public void SetNotificationPosition(NotificationCorner position)
    {
        if (!Enum.IsDefined(position)) position = NotificationCorner.TopLeft;
        if (NotificationPosition == position) return;
        NotificationPosition = position;
        // Reorder existing cards only when the corner changes. Jobs and their
        // collapsed state stay alive; progress callbacks keep the same card.
        foreach (var item in _notifications.Where(n => n.Removing).ToArray()) RemoveCard(item);
        _notificationCards.Children.Clear();
        IEnumerable<PlayerNotification> ordered = NotificationsAtBottom ? _notifications.AsEnumerable().Reverse() : _notifications;
        foreach (var item in ordered)
        {
            item.SetCorner(position);
            _notificationCards.Children.Add(item.Card);
        }
        ApplyNotificationPosition();
        if (_notifications.Count > 0) RevealNotification(_notifications[0]);
    }

    private void ApplyNotificationPosition()
    {
        if (_notificationOverlay is not { } host) return;
        bool right = NotificationPosition is NotificationCorner.TopRight or NotificationCorner.BottomRight;
        host.HorizontalOptions = right ? LayoutOptions.End : LayoutOptions.Start;
        host.VerticalOptions = NotificationsAtBottom ? LayoutOptions.End : LayoutOptions.Start;
        host.Margin = new Thickness(12, WindowsWindowChrome.ContentInset + 8, 12, 8);
        UpdateNotificationBounds();
    }

    private void UpdateNotificationBounds()
    {
        if (_notificationOverlay is not { } host || Width <= 0 || Height <= 0) return;
        host.WidthRequest = Math.Min(350, Math.Max(1, Width - 24));
        host.MaximumHeightRequest = Math.Max(1, Height - WindowsWindowChrome.ContentInset - 16);
    }

    public PlayerNotification ShowNotification(string title, string message, bool running = false)
    {
        var item = new PlayerNotification(this, title, message, running);
        _notifications.Insert(0, item);
        if (NotificationsAtBottom) _notificationCards.Children.Add(item.Card);
        else _notificationCards.Children.Insert(0, item.Card);
        if (_notificationOverlay is not null) _notificationOverlay.IsVisible = true;
        item.AnimateEntrance();
        RevealNotification(item);
        if (!running) NotificationCompleted(item);
        return item;
    }

    private void RevealNotification(PlayerNotification item)
    {
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(20), async () =>
        {
            if (item.Removed || item.Removing || _notificationOverlay is not { } host) return;
            try { await host.ScrollToAsync(item.Card, ScrollToPosition.MakeVisible, false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        });
    }

    internal void NotificationCompleted(PlayerNotification item)
    {
        // Evictions are immediate to bound the number of live views even
        // during a burst. User dismissals and expiry use the exit motion.
        foreach (var old in _notifications.Where(n => !n.IsRunning && !n.Retained).Skip(4).ToArray())
            RemoveCard(old);
        foreach (var old in _notifications.Where(n => !n.IsRunning && n.Retained).Skip(3).ToArray())
            RemoveCard(old);
        if (!item.Retained)
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(8), () => RemoveNotification(item));
    }

    internal void RemoveNotification(PlayerNotification item)
    {
        if (item.Removed || item.Removing || item.IsRunning) return;
        item.AnimateExit(() => RemoveCard(item));
    }

    private void RemoveCard(PlayerNotification item)
    {
        if (item.Removed) return;
        item.Removed = true;
        item.CancelMotions();
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
