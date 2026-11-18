namespace Laveyana.Audio.Maui.Controls;

/// <summary>Edits one set of preferences, applying the changes together on save.</summary>
public sealed class PlayerSettingsPage : ContentPage
{
    private bool _closing;
    private bool _loading;
    private PlayerSettings _settings;

    public PlayerSettingsPage(PlayerSettings settings, Action<PlayerSettings> apply)
    {
        _settings = settings;
        BackgroundColor = MixerStyle.Background;
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container);

        var heading = MixerStyle.Label("Настройки", 23, bold: true);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
        var reset = MixerStyle.Button("Сбросить");
        reset.HorizontalOptions = LayoutOptions.End;
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        header.Add(heading);
        header.Add(reset, 1);

        var fadeIn = Duration("Плавный старт", [0, 150, 300, 500, 750, 1000, 1500, 2000, 3000],
            settings.FadeInMilliseconds, value => _settings = _settings with { FadeInMilliseconds = value });
        var fadeOut = Duration("Затухание при паузе", [0, 150, 300, 500, 750, 1000, 1500, 2000, 3000],
            settings.FadeOutMilliseconds, value => _settings = _settings with { FadeOutMilliseconds = value });
        var crossfade = Duration("Длительность перехода", [0, 2000, 4000, 6000, 8000, 10000, 12000],
            settings.CrossfadeMilliseconds, value => _settings = _settings with { CrossfadeMilliseconds = value });
        var autoMix = Toggle("Автомикс", settings.AutoMix, value => _settings = _settings with { AutoMix = value });
        var overview = Select("Полная дорожка", ["Полоса прогресса", "Цветная волна"], settings.WaveformOverview ? 1 : 0,
            index => _settings = _settings with { WaveformOverview = index == 1 });
        var palette = Select("Цвета", ["LavPulse", "Океан", "Тёплая"], (int)settings.Palette,
            index => _settings = _settings with { Palette = (PlayerPalette)index });
        var spectrum = Toggle("Показывать мастер-спектр", settings.ShowSpectrum, value => _settings = _settings with { ShowSpectrum = value });
        var peaks = Toggle("Показывать пиковые метки", settings.ShowPeaks, value => _settings = _settings with { ShowPeaks = value });

        View[] sections =
        [
            Section("Воспроизведение", fadeIn.Row, fadeOut.Row),
            Section("Сведение", autoMix.Row, crossfade.Row),
            Section("Дорожки", overview.Row),
            Section("Оформление и спектр", palette.Row, spectrum.Row, peaks.Row)
        ];
        reset.Clicked += (_, _) =>
        {
            var defaults = new PlayerSettings();
            _loading = true;
            fadeIn.Picker.SelectedIndex = fadeOut.Picker.SelectedIndex = 2;
            crossfade.Picker.SelectedIndex = 3;
            autoMix.Switch.IsToggled = defaults.AutoMix;
            overview.Picker.SelectedIndex = defaults.WaveformOverview ? 1 : 0;
            palette.Picker.SelectedIndex = (int)defaults.Palette;
            spectrum.Switch.IsToggled = defaults.ShowSpectrum;
            peaks.Switch.IsToggled = defaults.ShowPeaks;
            _settings = defaults;
            _loading = false;
        };

        var save = MixerStyle.Button("Сохранить", primary: true);
        var cancel = MixerStyle.Button("Отмена");
        save.Clicked += async (_, _) => await CloseAsync(() => apply(_settings));
        cancel.Clicked += async (_, _) => await CloseAsync(null);
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        actions.Add(cancel);
        actions.Add(save, 1);
        var stack = new VerticalStackLayout { Spacing = 12 };
        foreach (var section in sections)
        {
            stack.Children.Add(section);
        }

        var layout = new Grid
        {
            Padding = 16, RowSpacing = 12, MaximumWidthRequest = 660, HorizontalOptions = LayoutOptions.Center,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        layout.Add(header);
        layout.Add(new ScrollView { Content = stack }, row: 1);
        layout.Add(actions, row: 2);
        SizeChanged += (_, _) => { if (Width > 0) layout.WidthRequest = Math.Min(Width, 660); };
        Content = layout;
    }

    private async Task CloseAsync(Action? apply)
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        apply?.Invoke();
        await Navigation.PopModalAsync();
    }

    private (View Row, Picker Picker) Duration(string name, int[] values, int selected, Action<int> update)
    {
        var labels = values.Select(ms => ms switch
        {
            0 => "Выключено",
            < 1000 => $"{ms} мс",
            _ => $"{ms / 1000d:0.##} с"
        }).ToArray();
        return Select(name, labels, Array.IndexOf(values, selected), index => update(values[index]));
    }

    private (View Row, Picker Picker) Select(string name, string[] labels, int selected, Action<int> update)
    {
        var picker = new Picker
        {
            ItemsSource = labels, TextColor = MixerStyle.TextColor, BackgroundColor = MixerStyle.Overlay,
            FontSize = 14, SelectedIndex = Math.Max(0, selected)
        };
        SemanticProperties.SetDescription(picker, name);
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && picker.SelectedIndex >= 0)
            {
                update(picker.SelectedIndex);
            }
        };
        return (new VerticalStackLayout { Spacing = 3, Children = { MixerStyle.Label(name, 13), picker } }, picker);
    }

    private (View Row, Switch Switch) Toggle(string name, bool selected, Action<bool> update)
    {
        var toggle = new Switch { IsToggled = selected, OnColor = MixerStyle.Accent };
        SemanticProperties.SetDescription(toggle, name);
        toggle.Toggled += (_, args) => { if (!_loading) update(args.Value); };
        var row = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        var label = MixerStyle.Label(name, 13);
        label.VerticalOptions = LayoutOptions.Center;
        row.Add(label);
        row.Add(toggle, 1);
        return (row, toggle);
    }

    private static Border Section(string title, params View[] rows)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        var heading = MixerStyle.Label(title, 15, MixerStyle.Accent, bold: true);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level2);
        stack.Children.Add(heading);
        foreach (var row in rows)
        {
            stack.Children.Add(row);
        }
        var section = new Border();
        MixerStyle.StyleCard(section, stack);
        return section;
    }
}
