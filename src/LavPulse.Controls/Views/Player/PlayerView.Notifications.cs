#if WINDOWS
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Dispatching;
using System.Diagnostics;

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
    private readonly GraphicsView _progress = new() { HeightRequest = 7, IsVisible = false, InputTransparent = true };
    private readonly GraphicsView _ring = new() { InputTransparent = true };
    private readonly Button _edgeButton = MixerStyle.Button("");
    private readonly Button _minimize = MixerStyle.Button("−");
    private readonly Grid _mini = new() { IsVisible = false };
    private readonly Grid _shell = new();
    private readonly NotificationProgressDrawable _barDrawing, _ringDrawing;
    private double _targetProgress, _displayProgress, _progressFrom, _progressStarted;
    private bool _hasProgress, _miniature;
    private int _expiryGeneration;
    internal bool IsMiniature => _mini.IsVisible;
    internal bool NeedsProgressAnimation => !Removed && !Removing && IsRunning && _hasProgress;
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
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
        CompactButton(_minimize);
        _minimize.WidthRequest = 32;
        SemanticProperties.SetDescription(_minimize, "Свернуть в значок");
        ToolTipProperties.SetText(_minimize, "Свернуть в значок");
        _minimize.Clicked += (_, _) => SetMiniature(true);
        _barDrawing = new NotificationProgressDrawable(() => _percent.TextColor, ring: false);
        _ringDrawing = new NotificationProgressDrawable(() => _percent.TextColor, ring: true);
        _progress.Drawable = _barDrawing;
        _ring.Drawable = _ringDrawing;
        _edgeButton.BackgroundColor = Colors.Transparent;
        _edgeButton.Padding = 0;
        _edgeButton.FontSize = 12;
        _edgeButton.MinimumHeightRequest = 0;
        _edgeButton.MinimumWidthRequest = 0;
        _edgeButton.Clicked += (_, _) => SetMiniature(false);
        _mini.Add(_ring);
        _mini.Add(_edgeButton);
        var header = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 5
        };
        header.Add(_title);
        header.Add(_percent, 1);
        header.Add(_collapse, 2);
        header.Add(_minimize, 3);
        _summary = new VerticalStackLayout { Spacing = 5, Children = { header, _progress } };
        _detailContent = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(0, 5), Children = { _message, _actions, _dismiss } };
        _details.Add(_detailContent);
        _body = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)] };
        _body.Add(_summary);
        _body.Add(_details, row: 1);
        Card = new Border { StrokeThickness = 1, Padding = 12 };
        _shell.Add(_body);
        _shell.Add(_mini);
        MixerStyle.StyleCard(Card, _shell);
        Card.StrokeShape = new RoundRectangle { CornerRadius = 12 };
        RefreshMiniLabel();
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
        Card.HorizontalOptions = _right ? LayoutOptions.End : LayoutOptions.Start;
        _minimize.Text = _right ? "›" : "‹";
        UpdateCollapseGlyph();
        FinishDetailMotion();
        if (!Removing)
        {
            ++_cardGeneration;
            Card.AbortAnimation(CardMotion);
            ApplyMiniLayout();
            ResetCardTransform();
            Card.InputTransparent = false;
        }
    }

    private void RefreshMiniLabel()
    {
        _edgeButton.Text = IsRunning && _hasProgress ? $"{_targetProgress:P0}" :
            IsRunning ? "…" : Retained ? "•" : "i";
        string description = _title.Text + " — " + _message.Text + ". Развернуть";
        SemanticProperties.SetDescription(_edgeButton, description);
        ToolTipProperties.SetText(_edgeButton, description);
    }

    internal void ScheduleExpiry()
    {
        int generation = ++_expiryGeneration;
        if (Removed || Removing || _miniature && (IsRunning || Retained)) return;
        bool pinned = IsRunning || Retained;
        _owner.Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(pinned ? _owner.PinnedNotificationSeconds : _owner.TransientNotificationSeconds), () =>
        {
            if (Removed || Removing || generation != _expiryGeneration) return;
            if (IsRunning || Retained) SetMiniature(true);
            else _owner.RemoveNotification(this);
        });
    }

    private void SetMiniature(bool miniature)
    {
        if (Removed || Removing || miniature == _miniature) return;
        ++_expiryGeneration;
        _miniature = miniature;
        int generation = ++_cardGeneration;
        Card.AbortAnimation(CardMotion);
        FinishDetailMotion();
        Card.InputTransparent = true;
        // Shrink towards the selected edge, swap layout at the compact
        // endpoint, and ease the new representation into place.
        double scale = Card.Scale, opacity = Card.Opacity;
        double target = Math.Clamp(52 / Math.Max(52, Card.Width), .15, 1);
        var first = new Animation(t =>
        {
            if (generation != _cardGeneration || Removed) return;
            Card.Scale = scale + (target - scale) * t;
            Card.Opacity = opacity + (.35 - opacity) * t;
        });
        first.Commit(Card, CardMotion, 16, 150, Easing.CubicIn,
            (_, _) =>
            {
                if (generation != _cardGeneration || Removed) return;
                ApplyMiniLayout();
                Card.Scale = .85;
                Card.Opacity = .35;
                var second = new Animation(t =>
                {
                    if (generation != _cardGeneration || Removed) return;
                    Card.Scale = .85 + .15 * t;
                    Card.Opacity = .35 + .65 * t;
                });
                second.Commit(Card, CardMotion, 16, 140, Easing.CubicOut,
                    (_, _) =>
                    {
                        if (generation != _cardGeneration || Removed) return;
                                    ResetCardTransform();
                        Card.InputTransparent = false;
                        _owner.RevealNotification(this);
                    });
            });
        ScheduleExpiry();
    }

    private void ApplyMiniLayout()
    {
        _body.IsVisible = !_miniature;
        _mini.IsVisible = _miniature;
        Card.Padding = _miniature ? new Thickness(3) : new Thickness(12);
        Card.WidthRequest = _miniature ? 52 : -1;
        Card.HeightRequest = _miniature ? 52 : -1;
        Card.HorizontalOptions = _miniature ? (_right ? LayoutOptions.End : LayoutOptions.Start) : LayoutOptions.Fill;
        Card.StrokeShape = new RoundRectangle { CornerRadius = _miniature ? 15 : 12 };
        RefreshMiniLabel();
        _owner.RefreshNotificationLayout();
    }

    internal void TickProgress(double now)
    {
        if (!NeedsProgressAnimation) return;
        double t = Math.Clamp((now - _progressStarted) / .35, 0, 1);
        _displayProgress = _progressFrom + (_targetProgress - _progressFrom) * (1 - Math.Pow(1 - t, 3));
        var drawing = _miniature ? _ringDrawing : _barDrawing;
        drawing.Value = _displayProgress;
        drawing.Motion = now % 1.6 / 1.6;
        drawing.Active = true;
        (_miniature ? _ring : _progress).Invalidate();
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
        if (Removed || Removing || _miniature || collapsed == _collapsed) return;
        ScheduleExpiry();
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
        ++_expiryGeneration;
        Removing = true;
        Card.InputTransparent = true;
        int generation = ++_cardGeneration;
        Card.AbortAnimation(CardMotion);
        FinishDetailMotion();
        double opacity = Card.Opacity, scale = Card.Scale, x = Card.TranslationX, y = Card.TranslationY;
        double exitX = (_right ? 1 : -1) * (Math.Max(52, Card.Width) + 24);
        var animation = new Animation(t =>
        {
            if (generation != _cardGeneration || Removed) return;
            Card.Opacity = opacity * (1 - t);
            Card.Scale = scale + (.96 - scale) * t;
            Card.TranslationX = x + (exitX - x) * t;
            Card.TranslationY = y + ((_bottom ? 16 : -16) - y) * t;
        });
        animation.Commit(Card, CardMotion, 16, 180, Easing.CubicIn,
            (_, _) => { if (generation == _cardGeneration && !Removed) finished(); });
    }

    internal void CancelMotions()
    {
        ++_cardGeneration;
        ++_detailGeneration;
        ++_expiryGeneration;
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
        if (progress.HasValue && double.IsFinite(progress.Value))
        {
            _hasProgress = true;
            _progress.IsVisible = _percent.IsVisible = true;
            double target = Math.Clamp(progress.Value, 0, 1);
            if (target != _targetProgress)
            {
                _progressFrom = _displayProgress;
                _progressStarted = Now;
                _targetProgress = target;
            }
            _percent.Text = $"{_targetProgress:P0}";
            _owner.EnsureNotificationProgressTimer();
        }
        RefreshMiniLabel();
        if (actions is not null) SetActions(actions);
    }

    public void Complete(string title, string message, IReadOnlyList<NotificationAction>? actions = null, bool retain = false)
    {
        if (Removed || !IsRunning) return;
        IsRunning = false;
        Retained = retain;
        _title.Text = title;
        _message.Text = message;
        _hasProgress = false;
        _progress.IsVisible = _percent.IsVisible = false;
        _ringDrawing.Active = false;
        _ring.Invalidate();
        _dismiss.IsVisible = true;
        SetActions(actions ?? []);
        RefreshMiniLabel();
        if (retain)
        {
            _collapsed = false;
            FinishDetailMotion();
            UpdateCollapseGlyph();
            if (_miniature) SetMiniature(false);
        }
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
    private IDispatcherTimer? _notificationProgressTimer;
    internal int PinnedNotificationSeconds { get; private set; } = 5;
    internal int TransientNotificationSeconds { get; private set; } = 8;

    public void SetNotificationTimeouts(int pinned, int transient)
    {
        pinned = Math.Clamp(pinned, 3, 60);
        transient = Math.Clamp(transient, 3, 60);
        if (PinnedNotificationSeconds == pinned && TransientNotificationSeconds == transient) return;
        PinnedNotificationSeconds = pinned;
        TransientNotificationSeconds = transient;
        foreach (var item in _notifications) item.ScheduleExpiry();
    }

    internal void EnsureNotificationProgressTimer()
    {
        if (_notificationProgressTimer is null)
        {
            _notificationProgressTimer = Dispatcher.CreateTimer();
            _notificationProgressTimer.Interval = TimeSpan.FromMilliseconds(33);
            _notificationProgressTimer.Tick += (_, _) =>
            {
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                bool active = false;
                foreach (var item in _notifications)
                {
                    if (!item.NeedsProgressAnimation) continue;
                    active = true;
                    item.TickProgress(now);
                }
                if (!active) _notificationProgressTimer.Stop();
            };
        }
        if (!_notificationProgressTimer.IsRunning) _notificationProgressTimer.Start();
    }

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
        host.WidthRequest = Math.Min(_notifications.Count > 0 && _notifications.All(n => n.IsMiniature) ? 58 : 350,
            Math.Max(1, Width - 24));
        host.MaximumHeightRequest = Math.Max(1, Height - WindowsWindowChrome.ContentInset - 16);
    }

    public PlayerNotification ShowNotification(string title, string message, bool running = false)
    {
        var item = new PlayerNotification(this, title, message, running);
        _notifications.Insert(0, item);
        if (NotificationsAtBottom) _notificationCards.Children.Add(item.Card);
        else _notificationCards.Children.Insert(0, item.Card);
        if (_notificationOverlay is not null) _notificationOverlay.IsVisible = true;
        RefreshNotificationLayout();
        item.AnimateEntrance();
        RevealNotification(item);
        if (!running) NotificationCompleted(item);
        else item.ScheduleExpiry();
        return item;
    }

    internal void RefreshNotificationLayout() => UpdateNotificationBounds();

    internal void RevealNotification(PlayerNotification item)
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
        item.ScheduleExpiry();
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
        RefreshNotificationLayout();
    }

    public void ClearNotifications()
    {
        foreach (var item in _notifications.ToArray()) RemoveCard(item);
        _notificationProgressTimer?.Stop();
    }
}
#endif
