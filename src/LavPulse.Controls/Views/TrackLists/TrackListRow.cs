using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.LavPulse.Controls;

/// <summary>Provides the card geometry, cover, and action slots shared by track list rows.</summary>
internal partial class TrackListRow : Border
{
    private const int CoverSize = 38;
    private const int DeleteWidth = 42;
    private const int DeleteSlot = DeleteWidth + 5;
    private static readonly BindableProperty ArtworkColorsProperty = BindableProperty.Create("ArtworkColors",
        typeof(PlayerColors), typeof(TrackListRow), MixerTheme.Default.Artwork,
        propertyChanged: (bindable, _, _) => ((TrackListRow)bindable).UpdateCoverBackground());

    private readonly Image _art = new() { Aspect = Aspect.AspectFill, IsVisible = false };
    private readonly Border _cover;
    private readonly Grid _leading;
    private readonly Grid _content;
    private ImageButton? _delete;
    private bool _inlineDelete;
    private double _deleteReveal;
    private string? _artworkFile;
    private ReadOnlyMemory<byte> _artwork;
    private CheckBox? _selectionMark;
    private bool _updatingMark;
    internal event Action? SelectionToggled;
    internal bool HasRevealedSwipe => _revealedItem is not null;
    private Button? _swipeActionButton;
    private ListSwipeAction? _swipeIconAction;
    private BoxView? _swipeTint;
    private object? _swipeItem;
    private object? _revealedItem;
    private double _swipeDistance;
    private bool _horizontalSwipe;
    private long _lastGesture;

    internal bool ConsumedGesture => _lastGesture != 0 &&
        System.Diagnostics.Stopwatch.GetElapsedTime(_lastGesture).TotalMilliseconds < 350;

    internal void SetSelectionMark(bool visible, bool selected)
    {
        if (_selectionMark is null && visible)
        {
            _selectionMark = new CheckBox { VerticalOptions = LayoutOptions.Center };
            _selectionMark.CheckedChanged += (_,_) => { if(!_updatingMark) SelectionToggled?.Invoke(); };
            MixerStyle.UseColor(_selectionMark, CheckBox.ColorProperty, MixerColor.Accent);
            _content.Add(_selectionMark, 2);
        }
        if (_selectionMark is null) return;
        _selectionMark.IsVisible = visible;
        _updatingMark=true;
        try { _selectionMark.IsChecked = selected; }
        finally { _updatingMark=false; }
        _content.ColumnDefinitions[2].Width = new GridLength(visible ? 36 : 0);
    }

    internal void AttachActions(Func<object?> item, Func<bool> enabled,
        Func<bool, ListSwipeAction> resolve, Func<ListSwipeAction, Task> execute,
        Func<Task> context, Func<PlayerColors> colors, Action? closeOtherSwipes = null)
    {
        var tint = _swipeTint = new BoxView { IsVisible = false, InputTransparent = true };
        var button = _swipeActionButton = MixerStyle.Button("");
        button.IsVisible = false;
        button.RemoveDynamicResource(BackgroundColorProperty);
        button.BackgroundColor = Colors.Transparent;
        button.FontSize = 10;
        button.Padding = new Thickness(2);
        button.MinimumHeightRequest = 0;
        button.ContentLayout = new(Button.ButtonContentLayout.ImagePosition.Left, 3);
        button.LineBreakMode = LineBreakMode.TailTruncation;
        Layers.Children.Insert(0, tint);
        Layers.Children.Insert(1, button);
        ListSwipeAction action = ListSwipeAction.None;
        bool right = false;
        double first = 0, second = 0;

        async Task InvokeAsync(ListSwipeAction requested, object? expected)
        {
            if (!enabled() || expected is null || !ReferenceEquals(expected, item())) return;
            _lastGesture = System.Diagnostics.Stopwatch.GetTimestamp();
            ResetSwipe();
            try
            {
                if (requested == ListSwipeAction.Menu) await context();
                else if (requested != ListSwipeAction.None) await execute(requested);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }

        button.Clicked += async (_, _) => await InvokeAsync(action, _revealedItem);
        async void ProcessPan(GestureStatus status,double x,double y)
        {
            switch (status)
            {
                case GestureStatus.Started:
                    closeOtherSwipes?.Invoke();
                    ResetSwipe();
                    _swipeItem = enabled() ? item() : null;
                    _horizontalSwipe = false;
                    break;
                case GestureStatus.Running:
                    if (_swipeItem is null || !ReferenceEquals(_swipeItem,item()) || !enabled()) { ResetSwipe(); return; }
                    if (!_horizontalSwipe && Math.Abs(x) < Math.Max(12, Math.Abs(y)*1.3)) return;
                    _horizontalSwipe = true;
                    right = x >= 0;
                    action = resolve(right);
                    if (action == ListSwipeAction.None) { ResetSwipe(); return; }
                    double width = Math.Max(1, Width - Padding.HorizontalThickness);
                    first = Math.Min(76, width*.28);
                    second = Math.Max(first+1,width*.68);
                    _swipeDistance = Math.Min(width,Math.Abs(x));
                    DrawSwipe(_swipeDistance, right, action, first, second, colors());
                    break;
                case GestureStatus.Completed:
                    if (!_horizontalSwipe) { ResetSwipe(); return; }
                    _lastGesture = System.Diagnostics.Stopwatch.GetTimestamp();
                    var expected = _swipeItem;
                    if (_swipeDistance >= second && second > 0)
                    {
                        _swipeItem=null;
                        _horizontalSwipe=false;
                        await InvokeAsync(action, expected);
                        return;
                    }
                    else if (_swipeDistance >= first && first > 0 && ReferenceEquals(expected,item()) && enabled())
                    {
                        _revealedItem = expected;
                        DrawSwipe(first,right,action,first,second,colors());
                    }
                    else ResetSwipe();
                    _swipeItem = null;
                    _horizontalSwipe = false;
                    break;
                case GestureStatus.Canceled:
                    ResetSwipe();
                    break;
            }
        }
#if WINDOWS
        AttachDesktopPan(Body,enabled,ProcessPan);
#else
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_,args) => ProcessPan(args.StatusType,args.TotalX,args.TotalY);
        GestureRecognizers.Add(pan);
#endif
        var secondary = new TapGestureRecognizer { Buttons = ButtonsMask.Secondary };
        secondary.Tapped += async (_, _) => await InvokeAsync(ListSwipeAction.Menu,item());
        Body.GestureRecognizers.Add(secondary);
        AttachHold(Body, () => { if (enabled() && !_horizontalSwipe) _ = InvokeAsync(ListSwipeAction.Menu,item()); });
    }

    static partial void AttachHold(View view, Action invoke);
    static partial void AttachDesktopPan(View view,Func<bool> enabled,Action<GestureStatus,double,double> pan);

    private void DrawSwipe(double distance, bool right, ListSwipeAction action, double first, double second, PlayerColors colors)
    {
        if (_swipeTint is not { } tint || _swipeActionButton is not { } button) return;
        var side = right ? LayoutOptions.Start : LayoutOptions.End;
        tint.HorizontalOptions = button.HorizontalOptions = side;
        tint.WidthRequest = button.WidthRequest = Math.Max(0,distance);
        tint.Color = ListActionText.Tint(action, colors);
        tint.Opacity = Math.Clamp((distance-first)/(second-first),0,1);
        tint.IsVisible = distance >= first;
        if (_swipeIconAction != action)
        {
            _swipeIconAction = action;
            MixerStyle.UseIcon(button, Button.ImageSourceProperty, ListActionText.Icon(action));
            SemanticProperties.SetDescription(button, ListActionText.Label(action));
            ToolTipProperties.SetText(button, ListActionText.Label(action));
        }
        // At the first threshold show a clear icon even on narrow screens.
        // Longer reveals also have room for the caption.
        string caption = distance >= 130 ? ListActionText.Label(action) : "";
        if (button.Text != caption) button.Text = caption;
        button.IsVisible = distance >= first;
        button.InputTransparent = _revealedItem is null;
        Foreground.TranslationX = right ? distance : -distance;
    }

    internal void ResetSwipe()
    {
        _swipeItem = _revealedItem = null;
        _swipeDistance = 0;
        _horizontalSwipe = false;
        if (_swipeTint is not null) _swipeTint.IsVisible = false;
        if (_swipeActionButton is not null) _swipeActionButton.IsVisible = false;
        Foreground.TranslationX = 0;
    }

    internal TrackListRow()
    {
        HeightRequest = TrackListLayout.RowHeight;
        Padding = new Thickness(7, 5);
        StrokeThickness = 1;
        Stroke = Colors.Transparent;
        StrokeShape = new RoundRectangle { CornerRadius = 8 };
        _cover = new Border
        {
            WidthRequest = CoverSize,
            HeightRequest = CoverSize,
            VerticalOptions = LayoutOptions.Center,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            Content = _art
        };
        _leading = new Grid { Children = { _cover } };
        Body = new Grid
        {
            BackgroundColor = Colors.Transparent,
            ColumnSpacing = 6,
            ColumnDefinitions = [new(CoverSize), new(GridLength.Star)]
        };
        Body.Add(_leading);
        _content = new Grid { ColumnDefinitions = [new(GridLength.Star), new(0), new(0)] };
        _content.Add(Body);
        Foreground = new Grid { Children = { _content } };
        Layers = new Grid { IsClippedToBounds = true, Children = { Foreground } };
        Content = Layers;
        SetDynamicResource(ArtworkColorsProperty, MixerStyle.ArtworkColorsResource);
    }

    protected Grid Body { get; }
    internal View TouchBody => Body;
    internal event Action? DeleteRequested;
    internal event Action? DeleteUnfocused;
    protected Grid Foreground { get; }
    private Grid Layers { get; }
    protected double CoverOpacity { set => _cover.Opacity = value; }

    internal void DrawLoadingPulse(double seconds) => Opacity = .76 + .24 * Math.Cos(seconds * 4.2);

    protected void SetDetails(View details) => Body.Add(details, 1);
    protected void SetActions(View actions) => _content.Add(actions, 1);
    protected void SetActionWidth(GridLength width)
    {
        _content.ColumnDefinitions[1].Width = width;
    }

    protected void EnableDelete()
    {
        var button = MixerStyle.IconButton("player_delete.png", DeleteWidth);
        button.HorizontalOptions = LayoutOptions.End;
        MixerStyle.UseColor(button, BackgroundColorProperty, MixerColor.Selected);
        button.Clicked += (_, _) => DeleteRequested?.Invoke();
        button.Unfocused += (_, _) => DeleteUnfocused?.Invoke();
        _delete = button;
        Layers.Add(button);
        UpdateDelete();
    }

    protected void DescribeDelete(string description)
    {
        if (_delete is not { } button) return;
        SemanticProperties.SetDescription(button, description);
        ToolTipProperties.SetText(button, description);
    }

    protected void SetDeleteEnabled(bool enabled)
    {
        if (_delete is { } button) button.IsEnabled = enabled;
    }

    internal void SetInlineDelete(bool visible)
    {
        if (_inlineDelete == visible) return;
        _inlineDelete = visible;
        _content.ColumnDefinitions[2].Width = new GridLength(visible ? DeleteSlot : 0);
        UpdateDelete();
    }

    internal void SetDeleteReveal(double progress)
    {
        if (_deleteReveal == progress) return;
        _deleteReveal = progress;
        UpdateDelete();
    }

    private void UpdateDelete()
    {
        if (_delete is not { } button) return;
        double offset = _deleteReveal * DeleteSlot;
        Foreground.TranslationX = _inlineDelete ? 0 : -offset;
        button.TranslationX = _inlineDelete ? 0 : DeleteSlot - offset;
        button.IsVisible = _inlineDelete || _deleteReveal > 0;
        button.InputTransparent = !_inlineDelete && _deleteReveal < 1;
    }

    protected void AddLeading(View view)
    {
        _cover.IsVisible = false;
        _leading.Add(view);
    }

    protected void ShowCover(bool visible) => _cover.IsVisible = visible;

    protected void SetArtwork(string? file, ReadOnlyMemory<byte> artwork = default)
    {
        if (_artworkFile == file && _artwork.Equals(artwork)) return;
        _artworkFile = file;
        if (!_artwork.Equals(artwork))
        {
            _artwork = artwork;
            // A native image request can finish after a recycled row has cleared its source.
            if (artwork.IsEmpty) _art.IsVisible = false;
            _art.Source = TrackCover.FromArtwork(artwork);
            if (!artwork.IsEmpty) _art.IsVisible = true;
        }
        UpdateCoverBackground();
    }

    private void UpdateCoverBackground() =>
        _cover.Background = _artworkFile is { } file && _artwork.IsEmpty
            ? TrackCover.CreateGradient((PlayerColors)GetValue(ArtworkColorsProperty), file)
            : null;
}
