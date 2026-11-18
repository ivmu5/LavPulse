namespace Laveyana.Audio.Maui.Controls;

/// <summary>Edits one set of preferences, applying the changes together on save.</summary>
public sealed class PlayerSettingsPage : ContentPage
{
    private bool _closing;
    private bool _loading;
    private PlayerSettings _settings;

    public PlayerSettingsPage(PlayerSettings settings, Action<PlayerSettings> apply, Func<Task> pickFiles, Func<Task> pickFolder)
    {
        _settings = settings;
        var defaults = new PlayerSettings();
        BackgroundColor = MixerStyle.Background;
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container);

        var heading = MixerStyle.Label("Настройки", 23, bold: true);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
        var reset = MixerStyle.Button("Сбросить", icon: "player_reset.png");
        reset.HorizontalOptions = LayoutOptions.End;
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        header.Add(heading);
        header.Add(reset, 1);

        var fadeIn = Number("Плавный старт", "мс", new(0, 3000, defaults.FadeInMilliseconds, 50),
            settings.FadeInMilliseconds, value => _settings = _settings with { FadeInMilliseconds = value });
        var fadeOut = Number("Затухание при паузе", "мс", new(0, 3000, defaults.FadeOutMilliseconds, 50),
            settings.FadeOutMilliseconds, value => _settings = _settings with { FadeOutMilliseconds = value });
        var crossfade = Number("Длительность перехода", "мс", new(0, 12000, defaults.CrossfadeMilliseconds, 100),
            settings.CrossfadeMilliseconds, value => _settings = _settings with { CrossfadeMilliseconds = value });
        var autoMix = Toggle("Автомикс", settings.AutoMix, value => _settings = _settings with { AutoMix = value });
        var overview = Select("Полная дорожка", ["Полоса прогресса", "Цветная волна"], settings.WaveformOverview ? 1 : 0,
            index => _settings = _settings with { WaveformOverview = index == 1 });
        var palette = Select("Цвета", ["LavPulse", "Океан", "Тёплая"], (int)settings.Palette,
            index => _settings = _settings with { Palette = (PlayerPalette)index });
        var spectrum = Toggle("Показывать мастер-спектр", settings.ShowSpectrum, value => _settings = _settings with { ShowSpectrum = value });
        var spectrumColors = Select("Цвет мастер-спектра", ["Два цвета", "Градиент по частотам", "Однотонный"],
            (int)settings.SpectrumColors, index => _settings = _settings with { SpectrumColors = (SpectrumColorMode)index });
        var brightness = Number("Минимальная яркость", "%", new(0, 100, defaults.SpectrumMinimumBrightnessPercent, 1),
            settings.SpectrumMinimumBrightnessPercent,
            value => _settings = _settings with { SpectrumMinimumBrightnessPercent = value });
        var fullBrightness = Number("Уровень сигнала для полной яркости", "%", new(1, 100, defaults.SpectrumFullBrightnessPercent, 1),
            settings.SpectrumFullBrightnessPercent, value => _settings = _settings with { SpectrumFullBrightnessPercent = value });
        var brightnessOptions = new VerticalStackLayout
        {
            Spacing = 8, IsEnabled = settings.SpectrumLevelBrightness, Children = { brightness.Row, fullBrightness.Row }
        };
        var topPadding = Number("Отступ сверху", "полосы", new(0, PlayerSettings.MaximumSpectrumPaddingRows, defaults.SpectrumTopPaddingRows, 1),
            settings.SpectrumTopPaddingRows, value => _settings = _settings with { SpectrumTopPaddingRows = value });
        var bottomPadding = Number("Отступ снизу", "полосы", new(0, PlayerSettings.MaximumSpectrumPaddingRows, defaults.SpectrumBottomPaddingRows, 1),
            settings.SpectrumBottomPaddingRows, value => _settings = _settings with { SpectrumBottomPaddingRows = value });
        NumericSettingInput[] numbers = [fadeIn.Input, fadeOut.Input, crossfade.Input, brightness.Input, fullBrightness.Input,
            topPadding.Input, bottomPadding.Input];
        var adaptiveBrightness = new CheckBox { IsChecked = settings.SpectrumLevelBrightness, Color = MixerStyle.Accent };
        SemanticProperties.SetDescription(adaptiveBrightness, "Яркость по уровню частоты");
        adaptiveBrightness.CheckedChanged += (_, args) =>
        {
            if (!_loading)
            {
                brightness.Input.CommitText();
                fullBrightness.Input.CommitText();
                _settings = _settings with { SpectrumLevelBrightness = args.Value };
                brightnessOptions.IsEnabled = args.Value;
            }
        };
        var brightnessToggle = ControlRow("Яркость по уровню частоты", adaptiveBrightness);
        var peaks = Toggle("Показывать пиковые метки", settings.ShowPeaks, value => _settings = _settings with { ShowPeaks = value });

        View[] sections =
        [
            CreateLibrarySection(pickFiles, pickFolder),
            Section("Воспроизведение", fadeIn.Row, fadeOut.Row),
            Section("Сведение", autoMix.Row, crossfade.Row),
            Section("Дорожки", overview.Row),
            Section("Оформление и спектр", palette.Row, spectrum.Row, spectrumColors.Row, brightnessToggle, brightnessOptions,
                topPadding.Row, bottomPadding.Row, peaks.Row)
        ];
        reset.Clicked += (_, _) =>
        {
            _loading = true;
            foreach (var input in numbers)
            {
                input.Value = input.DefaultValue;
            }
            autoMix.Switch.IsToggled = defaults.AutoMix;
            overview.Picker.SelectedIndex = defaults.WaveformOverview ? 1 : 0;
            palette.Picker.SelectedIndex = (int)defaults.Palette;
            spectrumColors.Picker.SelectedIndex = (int)defaults.SpectrumColors;
            adaptiveBrightness.IsChecked = defaults.SpectrumLevelBrightness;
            brightnessOptions.IsEnabled = defaults.SpectrumLevelBrightness;
            spectrum.Switch.IsToggled = defaults.ShowSpectrum;
            peaks.Switch.IsToggled = defaults.ShowPeaks;
            _settings = defaults;
            _loading = false;
        };

        var save = MixerStyle.Button("Сохранить", primary: true, icon: "player_confirm.png");
        var cancel = MixerStyle.Button("Отмена", icon: "player_cancel.png");
        save.Clicked += async (_, _) => await CloseAsync(() =>
        {
            foreach (var input in numbers)
            {
                input.CommitText();
            }
            apply(_settings);
        });
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

    /// <summary>Keeps native file and folder import available from settings.</summary>
    private static Border CreateLibrarySection(Func<Task> pickFiles, Func<Task> pickFolder)
    {
        var files = MixerStyle.Button("Открыть файлы", icon: "player_file.png");
        var folder = MixerStyle.Button("Открыть папку", icon: "player_folder.png");
        files.Clicked += async (_, _) => await ImportAsync(pickFiles);
        folder.Clicked += async (_, _) => await ImportAsync(pickFolder);
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        actions.Add(files);
        actions.Add(folder, 1);
        return Section("Медиатека", actions);

        async Task ImportAsync(Func<Task> pick)
        {
            files.IsEnabled = folder.IsEnabled = false;
            try
            {
                await pick();
            }
            finally
            {
                files.IsEnabled = folder.IsEnabled = true;
            }
        }
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

    private static (View Row, NumericSettingInput Input) Number(string name, string unit, NumericRange range, int selected, Action<int> update)
    {
        var input = new NumericSettingInput(name, unit, range, selected);
        input.ValueChanged += value => update((int)value);
        return (new VerticalStackLayout { Spacing = 3, Children = { MixerStyle.Label(name, 13), input } }, input);
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
        return (ControlRow(name, toggle), toggle);
    }

    private static Grid ControlRow(string name, View control)
    {
        var row = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        var label = MixerStyle.Label(name, 13);
        label.VerticalOptions = LayoutOptions.Center;
        row.Add(label);
        row.Add(control, 1);
        return row;
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
