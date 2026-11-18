using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.Audio.Maui.Controls;

internal static class MixerStyle
{
    // The dark surfaces are derived from the blue and violet tones of the ribbon.
    public static readonly Color Background = Color.FromArgb("#0B1020");
    public static readonly Color Card = Color.FromArgb("#D1162040");
    public static readonly Color Surface = Color.FromArgb("#BA111831");
    public static readonly Color Overlay = Color.FromArgb("#151C36");
    public static readonly Color BorderColor = Color.FromArgb("#4C618D");
    public static readonly Color ButtonBackground = Color.FromArgb("#C4253458");
    public static readonly Color SelectedBackground = Color.FromArgb("#28416D");
    public static readonly Color TextColor = Color.FromArgb("#F4F6FF");
    public static readonly Color Muted = Color.FromArgb("#B3BEE0");
    public static readonly Color OnAccent = Color.FromArgb("#0B2137");
    public static readonly Color Accent = Color.FromArgb("#58E5FC");
    public static readonly Color Blue = Color.FromArgb("#419DFB");
    public static readonly Color Violet = Color.FromArgb("#BF89E7");
    public static readonly Color Alternate = Color.FromArgb("#FF9B65");
    public static readonly Color Preview = Color.FromArgb("#FFD0A0");
    private const double ButtonFontSize = 12;

    /// <summary>Creates a text control with the mixer font and color palette.</summary>
    public static Label Label(string text, double size, Color? color = null, bool bold = false)
        => new()
        {
            Text = text, FontFamily = "Inter", FontSize = size, TextColor = color ?? TextColor,
            FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None
        };

    /// <summary>Creates a transport or picker button with a consistent disabled state.</summary>
    public static Button Button(string text, bool primary = false, string? icon = null, bool stacked = false)
    {
        var button = new Button
        {
            Text = text, FontFamily = "Inter", FontSize = ButtonFontSize, CornerRadius = 9,
            Padding = stacked ? new Thickness(2, 4) : new Thickness(8, 8), MinimumHeightRequest = stacked ? 52 : 44,
            BackgroundColor = primary ? Accent : ButtonBackground,
            TextColor = primary ? OnAccent : TextColor,
            FontAttributes = primary ? FontAttributes.Bold : FontAttributes.None
        };
        if (icon is not null)
        {
            button.ImageSource = icon;
            button.ContentLayout = new(stacked ? Microsoft.Maui.Controls.Button.ButtonContentLayout.ImagePosition.Top
                : Microsoft.Maui.Controls.Button.ButtonContentLayout.ImagePosition.Left, 3);
        }
        if (stacked)
        {
            button.LineBreakMode = LineBreakMode.CharacterWrap;
        }
        ApplyDisabledOpacity(button);
        return button;
    }

    /// <summary>Creates a focusable transport button from a local vector asset.</summary>
    public static ImageButton IconButton(string image, int size, bool primary = false)
    {
        var button = new ImageButton
        {
            Source = image, Aspect = Aspect.AspectFit, Padding = (size - 24) / 2,
            WidthRequest = size, HeightRequest = size, CornerRadius = size / 2,
            BackgroundColor = primary ? Accent : ButtonBackground,
            VerticalOptions = LayoutOptions.Center
        };
        ApplyDisabledOpacity(button);
        return button;
    }

    private static void ApplyDisabledOpacity(VisualElement button)
    {
        var states = new VisualStateGroup { Name = "CommonStates" };
        states.States.Add(new VisualState { Name = "Normal" });
        states.States.Add(new VisualState
        {
            Name = "Disabled",
            Setters = { new Setter { Property = VisualElement.OpacityProperty, Value = 0.42 } }
        });
        VisualStateManager.SetVisualStateGroups(button, new VisualStateGroupList { states });
    }

    /// <summary>Applies the common panel styling and installs its content.</summary>
    public static void StyleCard(Border border, View content)
    {
        border.Padding = new Thickness(15, 13);
        border.Stroke = new SolidColorBrush(BorderColor);
        border.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(15) };
        border.BackgroundColor = Card;
        border.Content = content;
    }
}
