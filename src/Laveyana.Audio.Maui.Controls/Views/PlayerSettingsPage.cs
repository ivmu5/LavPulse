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
        var detail = Number("Длительность верхней волны", "с",
            new(PlayerSettings.MinimumWaveformDetailSeconds, PlayerSettings.MaximumWaveformDetailSeconds, defaults.WaveformDetailSeconds, 1),
            settings.WaveformDetailSeconds, value => _settings = _settings with { WaveformDetailSeconds = value });
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
            IsEnabled = settings.SpectrumLevelBrightness, Children = { Pair(brightness.Row, fullBrightness.Row) }
        };
        var topPadding = Number("Отступ сверху", "полосы", new(0, PlayerSettings.MaximumSpectrumPaddingRows, defaults.SpectrumTopPaddingRows, 1),
            settings.SpectrumTopPaddingRows, value => _settings = _settings with { SpectrumTopPaddingRows = value });
        var bottomPadding = Number("Отступ снизу", "полосы", new(0, PlayerSettings.MaximumSpectrumPaddingRows, defaults.SpectrumBottomPaddingRows, 1),
            settings.SpectrumBottomPaddingRows, value => _settings = _settings with { SpectrumBottomPaddingRows = value });
        var width = Number("Ширина с каждой стороны", "%", new(0, PlayerSettings.MaximumSpectrumWidthPercent, defaults.SpectrumWidthPercent, 1),
            settings.SpectrumWidthPercent, value => _settings = _settings with { SpectrumWidthPercent = value });
        NumericSettingInput[] numbers = [fadeIn.Input, fadeOut.Input, crossfade.Input, detail.Input, brightness.Input, fullBrightness.Input,
            topPadding.Input, bottomPadding.Input, width.Input];
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

        var (tabs, content) = CreateSettingsPages(
            ("Воспроизведение", Page(
                Section("Плавный старт и пауза", Pair(fadeIn.Row, fadeOut.Row)),
                Section("Переход между треками", autoMix.Row, crossfade.Row))),
            ("Оформление", Page(Section("Плеер и деки", palette.Row, Pair(detail.Row, overview.Row)))),
            ("Спектр", Page(
                Section("Геометрия и видимость", Pair(spectrum.Row, peaks.Row), width.Row, Pair(topPadding.Row, bottomPadding.Row)),
                Section("Цвет и яркость", spectrumColors.Row, brightnessToggle, brightnessOptions))),
            ("Медиатека", Page(CreateLibrarySection(pickFiles, pickFolder))));
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
        var layout = new Grid
        {
            Padding = 16, RowSpacing = 12, MaximumWidthRequest = 660, HorizontalOptions = LayoutOptions.Center,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        layout.Add(header);
        layout.Add(tabs, row: 1);
        layout.Add(content, row: 2);
        layout.Add(actions, row: 3);
        SizeChanged += (_, _) => { if (Width > 0) layout.WidthRequest = Math.Min(Width, 660); };
        Content = layout;
    }

    /// <summary>Gives each category its own scroll position while preserving one draft across all pages.</summary>
    private static (Grid Tabs, Grid Content) CreateSettingsPages(params (string Title, ScrollView View)[] pages)
    {
        var tabs = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)], ColumnSpacing = 6, RowSpacing = 6
        };
        var content = new Grid();
        var buttons = new Button[pages.Length];
        for (int i = 0; i < pages.Length; i++)
        {
            int index = i;
            var (title, page) = pages[i];
            var button = MixerStyle.Button(title);
            button.Clicked += (_, _) => SelectPage(index);
            buttons[i] = button;
            tabs.Add(button, i % 2, i / 2);
            content.Add(page);
        }
        SelectPage(0);
        return (tabs, content);

        void SelectPage(int selected)
        {
            for (int i = 0; i < pages.Length; i++)
            {
                bool active = i == selected;
                pages[i].View.IsVisible = active;
                buttons[i].BackgroundColor = active ? MixerStyle.SelectedBackground : MixerStyle.ButtonBackground;
                SemanticProperties.SetDescription(buttons[i], active ? $"{pages[i].Title}. Выбрано." : pages[i].Title);
            }
        }
    }

    private static ScrollView Page(params View[] sections)
    {
        var content = new VerticalStackLayout { Spacing = 10 };
        foreach (var section in sections)
        {
            content.Add(section);
        }
        return new ScrollView { Content = content };
    }

    /// <summary>Places related controls side by side as soon as the window has enough room.</summary>
    private static Grid Pair(View first, View second)
    {
        var pair = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto)],
            ColumnSpacing = 12, RowSpacing = 8
        };
        pair.Add(first);
        pair.Add(second, row: 1);
        Grid.SetColumnSpan(first, 2);
        Grid.SetColumnSpan(second, 2);
        bool wide = false;
        pair.SizeChanged += (_, _) =>
        {
            bool next = pair.Width >= 480;
            if (wide == next)
            {
                return;
            }
            wide = next;
            Grid.SetColumnSpan(first, wide ? 1 : 2);
            Grid.SetColumnSpan(second, wide ? 1 : 2);
            Grid.SetRow(second, wide ? 0 : 1);
            Grid.SetColumn(second, wide ? 1 : 0);
        };
        return pair;
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
