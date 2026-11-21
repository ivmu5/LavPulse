using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Provides the card geometry, cover, and action slots shared by track list rows.</summary>
internal class TrackListRow : Border
{
    private const int CoverSize = 38;
    private static readonly BindableProperty ArtworkColorsProperty = BindableProperty.Create("ArtworkColors",
        typeof(PlayerColors), typeof(TrackListRow), MixerTheme.Default.Artwork,
        propertyChanged: (bindable, _, _) => ((TrackListRow)bindable).UpdateCoverBackground());

    private readonly Image _art = new() { Aspect = Aspect.AspectFill };
    private readonly Border _cover;
    private readonly Grid _leading;
    private readonly Grid _content;
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
        _content = new Grid { ColumnDefinitions = [new(GridLength.Star), new(0)] };
        _content.Add(Body);
        Foreground = new Grid { Children = { _content } };
        Layers = new Grid { IsClippedToBounds = true, Children = { Foreground } };
        Content = Layers;
        SetDynamicResource(ArtworkColorsProperty, MixerStyle.ArtworkColorsResource);
    }

    protected Grid Body { get; }
    protected Grid Foreground { get; }
    protected Grid Layers { get; }
    protected double CoverOpacity { set => _cover.Opacity = value; }

    protected void SetDetails(View details) => Body.Add(details, 1);
    protected void SetActions(View actions) => _content.Add(actions, 1);
    protected void SetActionWidth(GridLength width)
    {
        _content.ColumnDefinitions[1].Width = width;
        _content.ColumnSpacing = width.IsAbsolute && width.Value == 0 ? 0 : 6;
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
