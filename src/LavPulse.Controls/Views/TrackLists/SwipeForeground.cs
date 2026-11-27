namespace Laveyana.LavPulse.Controls;

/// <summary>Crossfades existing icon variants without loading images as the finger moves.</summary>
internal sealed class SwipeForeground : Grid
{
    private readonly Image _light = new() { Aspect = Aspect.AspectFit, WidthRequest = 24, HeightRequest = 24 };
    private readonly Image _dark = new() { Aspect = Aspect.AspectFit, WidthRequest = 24, HeightRequest = 24 };
    private readonly Label _caption = new() { FontSize = 10, VerticalOptions = LayoutOptions.Center,
        LineBreakMode = LineBreakMode.TailTruncation, IsVisible = false };
    private ListSwipeAction? _action;
    private int _textShade = -1;
    private static readonly Color[] TextShades = Enumerable.Range(0, 65)
        .Select(i => new Color(i / 64f, i / 64f, i / 64f)).ToArray();

    public SwipeForeground()
    {
        InputTransparent = true;
        IsClippedToBounds = true;
        var icons = new Grid { WidthRequest = 24, HeightRequest = 24, Children = { _light, _dark } };
        Children.Add(new HorizontalStackLayout
        {
            Spacing = 3, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center,
            Children = { icons, _caption }
        });
    }

    public void Update(ListSwipeAction action, double luminance, bool showCaption)
    {
        string file = ListActionText.Icon(action);
        if (_action != action)
        {
            _action = action;
            _light.Source = Variant(file, false);
            _dark.Source = Variant(file, true);
            _caption.Text = ListActionText.Label(action);
        }
        double weight = IconContrast.DarkIconWeight(luminance, file);
        _light.Opacity = 1 - weight;
        _dark.Opacity = weight;
        _caption.IsVisible = showCaption;
        if (!showCaption) return;
        int shade = (int)Math.Round((1 - IconContrast.DarkTextWeight(luminance)) * 64);
        if (_textShade != shade)
        {
            _textShade = shade;
            _caption.TextColor = TextShades[shade];
        }
    }

    private static ImageSource Variant(string file, bool dark) => new FileImageSource
    {
        File = IconContrast.UseLightSuffix(file, dark) ? file[..^4] + "_light.png" : file
    };
}
