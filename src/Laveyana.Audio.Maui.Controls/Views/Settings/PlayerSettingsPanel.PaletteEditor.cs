using System.Globalization;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class PlayerSettingsPanel
{
    public event Action<bool>? PaletteEditingChanged;

    public bool TryOpenPaletteEditor()
    {
        if (_paletteEditorHost.IsVisible) return false;
        _paletteEditorHost.Content = new PaletteEditor(_settings, colors =>
        {
            _settings = _settings with { Palette = PlayerPalette.Custom, CustomColors = colors };
            _previewAppearance?.Invoke(_settings);
            TryClosePaletteEditor();
            ShowSection(_activeSection, commit: false);
        }, () => TryClosePaletteEditor());
        _settingsLayout.IsEnabled = false;
        _paletteEditorHost.IsVisible = true;
        PaletteEditingChanged?.Invoke(true);
        return true;
    }

    /// <summary>Dismisses the palette editor first when the platform's Back action is used.</summary>
    public bool TryClosePaletteEditor()
    {
        if (!_paletteEditorHost.IsVisible) return false;
        _paletteEditorHost.IsVisible = false;
        _paletteEditorHost.Content = null;
        _settingsLayout.IsEnabled = true;
        PaletteEditingChanged?.Invoke(false);
        return true;
    }

    private sealed class PaletteEditor : ContentView
    {
        private enum Role { Accent, Alternate, Low, Mid }

        private static readonly (Role Value, string Label)[] Roles =
            [(Role.Accent, "Акцент"), (Role.Alternate, "Доп. цвет"), (Role.Low, "Низкие"), (Role.Mid, "Средние")];
        private static readonly string[] Channels = ["R", "G", "B"];
        private readonly BoxView[] _swatches = new BoxView[Roles.Length];
        private readonly Slider[] _channels = new Slider[Channels.Length];
        private readonly Label[] _channelValues = new Label[Channels.Length];
        private readonly PaletteColorPicker _picker = new();
        private readonly Entry _hex = new()
        {
            Placeholder = "#RRGGBB", MaxLength = 7, FontFamily = "Inter", MinimumWidthRequest = 110
        };
        private readonly Label _validation = MixerStyle.Label("Введите цвет в формате #RRGGBB.", 12, MixerColor.Preview);
        private readonly Button _apply = MixerStyle.Button("Применить", primary: true);
        private readonly PlayerSettings _spectrumAppearance;
        private readonly PlayerSettings _appearance;
        private readonly SpectrumDrawable _spectrum;
        private readonly GraphicsView _preview;
        private PlayerColors _colors;
        private Role _role;
        private bool _updatingInputs;

        public PaletteEditor(PlayerSettings settings, Action<PlayerColors> apply, Action dismiss)
        {
            _colors = settings.CustomColors;
            _appearance = settings with { Palette = PlayerPalette.Custom };
            var previewSettings = settings with { SpectrumLevelBrightness = false };
            _spectrumAppearance = SpectrumPreviewSettings(previewSettings, settings.SpectrumColors);
            var theme = CreatePreviewTheme();
            _preview = CreateSpectrumPreview(previewSettings, theme.Visualization, settings.SpectrumColors, out _spectrum, theme.IsLight);
            _preview.HeightRequest = 48;

            var heading = MixerStyle.Label("Своя палитра", 20, bold: true);
            SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
            MixerStyle.StyleEntry(_hex);
            SemanticProperties.SetDescription(_hex, "Цвет в формате RGB HEX");
            _hex.TextChanged += (_, args) =>
            {
                if (_updatingInputs) return;
                var text = (args.NewTextValue ?? string.Empty).AsSpan().Trim();
                if (text.Length > 0 && text[0] == '#') text = text[1..];
                if (text.Length == 6 && int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
                {
                    SetColor(Color.FromInt(unchecked((int)0xFF000000) | rgb), syncHex: false);
                    return;
                }
                _validation.IsVisible = true;
                _apply.IsEnabled = false;
            };

            var inputs = new VerticalStackLayout { Spacing = 7 };
            inputs.Add(Select("Цвет для редактирования", Roles, Role.Accent, role =>
            {
                var swatch = new BoxView { Color = ReadColor(role), CornerRadius = 4 };
                _swatches[(int)role] = swatch;
                return swatch;
            }, role =>
            {
                _role = role;
                SyncInputs(ReadColor(role), syncHex: true);
            }));
            _picker.ColorPicked += color => SetColor(color, syncHex: true, syncPicker: false);
            inputs.Add(_picker);
            inputs.Add(ControlRow("HEX", _hex));
            inputs.Add(_validation);
            for (int channel = 0; channel < Channels.Length; channel++)
            {
                var slider = new Slider { Minimum = 0, Maximum = 255 };
                MixerStyle.StyleSlider(slider);
                SemanticProperties.SetDescription(slider, $"Канал {Channels[channel]}");
                _channels[channel] = slider;
                slider.ValueChanged += (_, _) =>
                {
                    if (_updatingInputs) return;
                    SetColor(Color.FromRgb((int)Math.Round(_channels[0].Value), (int)Math.Round(_channels[1].Value),
                        (int)Math.Round(_channels[2].Value)), syncHex: true);
                };
                var row = new Grid { ColumnDefinitions = [new(22), new(GridLength.Star), new(28)], ColumnSpacing = 4 };
                var label = MixerStyle.Label(Channels[channel], 13);
                label.VerticalOptions = LayoutOptions.Center;
                row.Add(label);
                row.Add(slider, 1);
                var value = MixerStyle.Label("00", 13, MixerColor.Muted);
                value.VerticalOptions = LayoutOptions.Center;
                value.HorizontalTextAlignment = TextAlignment.End;
                _channelValues[channel] = value;
                row.Add(value, 2);
                inputs.Add(row);
            }
            inputs.Add(MixerStyle.Label("Мастер-спектр", 13));
            inputs.Add(_preview);

            var cancel = MixerStyle.Button("Отмена");
            cancel.Clicked += (_, _) => dismiss();
            _apply.Clicked += (_, _) => apply(_colors);
            var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
            actions.Add(cancel);
            actions.Add(_apply, 1);

            var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], RowSpacing = 10 };
            layout.Add(heading);
            layout.Add(new ScrollView { Content = inputs }, row: 1);
            layout.Add(actions, row: 2);
            var dialog = new Border { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
            MixerStyle.StyleCard(dialog, layout);
            MixerStyle.UseColor(dialog, BackgroundColorProperty, MixerColor.Overlay);
            var overlay = new Grid
            {
                Padding = 16,
                Children = { dialog }
            };
            MixerStyle.UseColor(overlay, BackgroundColorProperty, MixerColor.Scrim);
            overlay.SizeChanged += (_, _) =>
            {
                if (overlay.Width > 32) dialog.WidthRequest = Math.Min(480, overlay.Width - 32);
                if (overlay.Height > 32) dialog.MaximumHeightRequest = overlay.Height - 32;
            };
            SyncInputs(ReadColor(_role), syncHex: true);
            Content = overlay;
        }

        private Color ReadColor(Role role) => role switch
        {
            Role.Accent => _colors.Accent,
            Role.Alternate => _colors.Alternate,
            Role.Low => _colors.WaveLow,
            _ => _colors.WaveMid
        };

        private void SetColor(Color color, bool syncHex, bool syncPicker = true)
        {
            _colors = _role switch
            {
                Role.Accent => _colors with { Accent = color },
                Role.Alternate => _colors with { Alternate = color },
                Role.Low => _colors with { WaveLow = color },
                _ => _colors with { WaveMid = color }
            };
            _swatches[(int)_role].Color = color;
            var theme = CreatePreviewTheme();
            if (_spectrum.SetAppearance(theme.Visualization, _spectrumAppearance, theme.IsLight)) _preview.Invalidate();
            SyncInputs(color, syncHex, syncPicker);
        }

        private MixerTheme CreatePreviewTheme() =>
            MixerTheme.Create(_appearance with { CustomColors = _colors }, MixerTheme.SystemTheme);

        private void SyncInputs(Color color, bool syncHex, bool syncPicker = true)
        {
            _updatingInputs = true;
            color.ToRgb(out byte red, out byte green, out byte blue);
            _channels[0].Value = red;
            _channels[1].Value = green;
            _channels[2].Value = blue;
            _channelValues[0].Text = red.ToString("X2");
            _channelValues[1].Text = green.ToString("X2");
            _channelValues[2].Text = blue.ToString("X2");
            if (syncHex) _hex.Text = $"#{red:X2}{green:X2}{blue:X2}";
            if (syncPicker) _picker.SetColor(color);
            _validation.IsVisible = false;
            _apply.IsEnabled = true;
            _updatingInputs = false;
        }
    }
}
