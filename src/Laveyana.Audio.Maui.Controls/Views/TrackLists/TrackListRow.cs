using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Provides the card geometry, cover, and action slots shared by track list rows.</summary>
internal class TrackListRow : Border
{
    private const int CoverSize = 38;
    private const int DeleteWidth = 42;
    private const int DeleteSlot = DeleteWidth + 5;
    private static readonly BindableProperty ArtworkColorsProperty = BindableProperty.Create("ArtworkColors",
        typeof(PlayerColors), typeof(TrackListRow), MixerTheme.Default.Artwork,
        propertyChanged: (bindable, _, _) => ((TrackListRow)bindable).UpdateCoverBackground());

    private readonly Image _art = new() { Aspect = Aspect.AspectFill };
    private readonly Border _cover;
    private readonly Grid _leading;
    private readonly Grid _content;
    private ImageButton? _delete;
    private bool _inlineDelete;
    private double _deleteReveal;
    private string? _artworkFile;
    private ReadOnlyMemory<byte> _artwork;

    internal TrackListRow()
    {
        HeightRequest = TrackListLayout.RowHeight;
        Padding = new Thickness(7, 5);
        StrokeThickness = 1;
        Stroke = Colors.Transparent;
        StrokeShape = new RoundRectangle { CornerRadius = 8 };
        _cover = new Border
        {
            WidthRequest = CoverSize, HeightRequest = CoverSize, VerticalOptions = LayoutOptions.Center,
            StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 5 }, Content = _art
        };
        _leading = new Grid { Children = { _cover } };
        Body = new Grid
        {
            BackgroundColor = Colors.Transparent,
            ColumnSpacing = 6, ColumnDefinitions = [new(CoverSize), new(GridLength.Star)]
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
            _art.Source = TrackCover.FromArtwork(artwork);
        }
        UpdateCoverBackground();
    }

    private void UpdateCoverBackground() =>
        _cover.Background = _artworkFile is { } file && _artwork.IsEmpty
            ? TrackCover.CreateGradient((PlayerColors)GetValue(ArtworkColorsProperty), file)
            : null;
}
