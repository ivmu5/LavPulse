using Laveyana.Maui.UI.Themes;
using SharedUi = Laveyana.Maui.UI.Controls.Ui;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;

namespace Laveyana.LavPulse.Controls;

internal static partial class MixerStyle
{
    private static readonly MixerColor[] ColorRoles = Enum.GetValues<MixerColor>();
    private static readonly string[] Icons =
    [
        "automix", "cancel", "confirm", "delete", "edit", "equalizer", "file", "folder", "import", "next", "pause",
        "play", "previous", "queue", "queue_add", "recommendations", "reorder", "repeat_one", "reset", "settings"
    ];
    private const double ButtonFontSize = 12;
    internal const string ArtworkColorsResource = "LavPulse.ArtworkColors";
    private const int ButtonIconSize = 24;

    internal static bool IsPhone { get; } = DeviceInfo.Idiom == DeviceIdiom.Phone;
    internal static double PhoneFont(double regular, double compact) => IsPhone ? compact : regular;

    internal static void ApplyTheme(ResourceDictionary resources, MixerTheme theme, bool refreshIcons)
    {
        resources[ArtworkColorsResource] = theme.Artwork;
        foreach (var role in ColorRoles)
        {
            resources[ColorKey(role)] = theme[role];
            resources[BrushKey(role)] = new SolidColorBrush(theme[role]);
        }
        if (!refreshIcons) return;
        foreach (string glyph in Icons)
        {
            string name = $"player_{glyph}";
            resources[IconKey($"{name}.png")] = new FileImageSource
            {
                File = theme.IsLight ? $"{name}_light.png" : $"{name}.png"
            };
        }
    }

    // Keep the player-specific preview color local; all standard roles use shared keys.
    internal static string ColorKey(MixerColor role) => role == MixerColor.Preview
        ? "LavPulse.Color.Preview" : UiThemeManager.ColorKey(MixerTheme.SharedRole(role));
    internal static string BrushKey(MixerColor role) => role == MixerColor.Preview
        ? "LavPulse.Brush.Preview" : UiThemeManager.BrushKey(MixerTheme.SharedRole(role));
    private static string IconKey(string file) => $"LavPulse.Icon.{file}";

    internal static void UseColor(VisualElement view, BindableProperty property, MixerColor role) =>
        view.SetDynamicResource(property, ColorKey(role));

    internal static void UseBrush(VisualElement view, BindableProperty property, MixerColor role) =>
        view.SetDynamicResource(property, BrushKey(role));

    internal static void UseIcon(VisualElement view, BindableProperty property, string file) =>
        view.SetDynamicResource(property, IconKey(file));

    internal static void StyleSwitch(Switch toggle)
    {
#if ANDROID || IOS
        UseColor(toggle, Switch.OnColorProperty, MixerColor.SwitchTrack);
        toggle.Toggled += (_, args) => UpdateThumb(args.Value);
        UpdateThumb(toggle.IsToggled);

        void UpdateThumb(bool isOn) =>
            UseColor(toggle, Switch.ThumbColorProperty, isOn ? MixerColor.Accent : MixerColor.Muted);
#else
        UseColor(toggle, Switch.OnColorProperty, MixerColor.Accent);
#endif
    }

    internal static void StyleSlider(Slider slider)
    {
        UseColor(slider, Slider.MinimumTrackColorProperty, MixerColor.Accent);
        UseColor(slider, Slider.MaximumTrackColorProperty, MixerColor.Border);
        UseColor(slider, Slider.ThumbColorProperty, MixerColor.Accent);
    }

    internal static void StyleEntry(Entry entry)
    {
        UseColor(entry, Entry.TextColorProperty, MixerColor.Text);
        UseColor(entry, Entry.PlaceholderColorProperty, MixerColor.Muted);
        UseColor(entry, VisualElement.BackgroundColorProperty, MixerColor.Overlay);
    }

    /// <summary>Creates a text control with the mixer font and color palette.</summary>
    public static Label Label(string text, double size, MixerColor color = MixerColor.Text, bool bold = false)
    {
        var label = SharedUi.Text(text, size, bold: bold, font: "Inter");
        UseColor(label, Microsoft.Maui.Controls.Label.TextColorProperty, color);
        return label;
    }

    /// <summary>Creates a transport or picker button with a consistent disabled state.</summary>
    public static Button Button(string text, bool primary = false, string? icon = null, bool stacked = false)
    {
        var button = SharedUi.Button(text, primary, font: "Inter");
        button.FontSize = ButtonFontSize;
        button.Padding = stacked ? new Thickness(2, 4) : new Thickness(8, 8);
        button.MinimumHeightRequest = stacked ? 52 : 44;
        UseColor(button, VisualElement.BackgroundColorProperty, primary ? MixerColor.Primary : MixerColor.Button);
        UseColor(button, Microsoft.Maui.Controls.Button.TextColorProperty, primary ? MixerColor.OnPrimary : MixerColor.Text);
        if (icon is not null)
        {
            UseIcon(button, Microsoft.Maui.Controls.Button.ImageSourceProperty, icon);
            button.ContentLayout = new(stacked ? Microsoft.Maui.Controls.Button.ButtonContentLayout.ImagePosition.Top
                : Microsoft.Maui.Controls.Button.ButtonContentLayout.ImagePosition.Left, 3);
            SetNativeButtonIconSize(button);
        }
        if (stacked)
        {
            button.LineBreakMode = LineBreakMode.CharacterWrap;
        }
        ApplyDisabledOpacity(button);
        return button;
    }

    public static void SetListEditButton(Button button, bool editing)
    {
        button.Text = editing ? "Готово" : "Редактирование";
        UseIcon(button, Microsoft.Maui.Controls.Button.ImageSourceProperty,
            editing ? "player_confirm.png" : "player_edit.png");
        SemanticProperties.SetDescription(button, editing ? "Закончить редактирование" : "Редактировать список");
    }

    /// <summary>Creates a focusable transport button from a local vector asset.</summary>
    public static ImageButton IconButton(string image, int size, bool primary = false)
    {
        var button = new ImageButton
        {
            Aspect = Aspect.AspectFit,
            Padding = (size - ButtonIconSize) / 2,
            WidthRequest = size,
            HeightRequest = size,
            CornerRadius = size / 2,
            VerticalOptions = LayoutOptions.Center
        };
        UseColor(button, VisualElement.BackgroundColorProperty, primary ? MixerColor.Primary : MixerColor.Button);
        UseIcon(button, ImageButton.SourceProperty, image);
        SetNativeButtonIconSize(button);
        ApplyDisabledOpacity(button);
        return button;
    }

    static partial void SetNativeButtonIconSize(View button);

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
        UseBrush(border, Border.StrokeProperty, MixerColor.Border);
        border.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(15) };
        UseColor(border, VisualElement.BackgroundColorProperty, MixerColor.Card);
        border.Content = content;
    }
}
