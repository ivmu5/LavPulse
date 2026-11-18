namespace Laveyana.Audio.Maui.Controls;

/// <summary>Edits one preference set with one expanded, lazily created section at a time.</summary>
public sealed class PlayerSettingsPage : ContentPage
{
    private readonly Action<PlayerSettings> _apply;
    private readonly Func<Task> _pickFiles;
    private readonly Func<Task> _pickFolder;
    private readonly ContentView[] _sectionHosts = new ContentView[6];
    private readonly Button[] _sectionButtons = new Button[6];
    private readonly List<NumericSettingInput> _visibleNumbers = [];
    private PlayerSettings _settings;
    private int _activeSection = -1;
    private bool _loading;
    private bool _closing;

    public PlayerSettingsPage(PlayerSettings settings, Action<PlayerSettings> apply, Func<Task> pickFiles,
        Func<Task> pickFolder, Func<Task>? dismiss = null)
    {
        _settings = settings;
        _apply = apply;
        _pickFiles = pickFiles;
        _pickFolder = pickFolder;
        BackgroundColor = MixerStyle.Background;
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container);

        var heading = MixerStyle.Label("Настройки", MixerStyle.PhoneFont(23, 20), bold: true);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
        var reset = MixerStyle.Button("Сбросить", icon: "player_reset.png");
        reset.Clicked += (_, _) =>
        {
            _settings = new PlayerSettings();
            ShowSection(_activeSection, commit: false);
        };
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        header.Add(heading);
        header.Add(reset, 1);

        var sections = new VerticalStackLayout { Spacing = 8 };
        string[] names = ["Медиатека", "Воспроизведение", "Деки", "Цвета", "Мастер-спектр", "Яркость спектра"];
        for (int index = 0; index < names.Length; index++)
        {
            int section = index;
            var button = MixerStyle.Button(names[index]);
            button.HorizontalOptions = LayoutOptions.Fill;
            button.Clicked += (_, _) => ShowSection(section, commit: true);
            _sectionButtons[index] = button;
            var content = new ContentView();
            _sectionHosts[index] = content;
            sections.Children.Add(button);
            sections.Children.Add(content);
        }

        var save = MixerStyle.Button("Сохранить", primary: true, icon: "player_confirm.png");
        var cancel = MixerStyle.Button("Отмена", icon: "player_cancel.png");
        save.Clicked += async (_, _) => await CloseAsync(dismiss, () =>
        {
            CommitVisibleNumbers();
            _apply(_settings);
        });
        cancel.Clicked += async (_, _) => await CloseAsync(dismiss, null);
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        actions.Add(cancel);
        actions.Add(save, 1);

        int topInset = 16;
#if WINDOWS
        topInset += WindowsWindowChrome.ContentInset;
#endif
        var layout = new Grid
        {
            Padding = new Thickness(16, topInset, 16, 16),
            RowSpacing = 12,
            MaximumWidthRequest = 660,
            HorizontalOptions = LayoutOptions.Center,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        layout.Add(header);
        layout.Add(new ScrollView { Content = sections }, row: 1);
        layout.Add(actions, row: 2);
        var host = new Grid { Children = { layout } };
        host.SizeChanged += (_, _) =>
        {
            if (host.Width > 0) layout.WidthRequest = Math.Min(host.Width, 660);
        };
        Content = host;

        // Initial UI creates only the playback controls. Other sections are opened on demand.
        ShowSection(1, commit: false);
    }

    /// <summary>Changes the expanded section while keeping edits made in earlier sections.</summary>
    private void ShowSection(int index, bool commit)
    {
        if (index < 0 || index >= _sectionHosts.Length) return;
        if (index == _activeSection && commit) return;
        if (commit) CommitVisibleNumbers();
        _loading = true;
        if (_activeSection >= 0) _sectionHosts[_activeSection].Content = null;
        _visibleNumbers.Clear();
        _activeSection = index;
        _sectionHosts[index].Content = index switch
        {
            0 => CreateLibrarySection(_pickFiles, _pickFolder),
            1 => CreatePlaybackSection(),
            2 => CreateDeckSection(),
            3 => CreateColorSection(),
            4 => CreateSpectrumSection(),
            _ => CreateBrightnessSection()
        };
        for (int section = 0; section < _sectionButtons.Length; section++)
        {
            bool selected = section == index;
            _sectionButtons[section].BackgroundColor = selected ? MixerStyle.SelectedBackground : MixerStyle.ButtonBackground;
            _sectionButtons[section].FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
        }
        _loading = false;
    }

    private void CommitVisibleNumbers()
    {
        foreach (var number in _visibleNumbers) number.CommitText();
    }

    private View CreatePlaybackSection()
    {
        var defaults = new PlayerSettings();
        var fadeIn = Number("Плавный старт", "мс", new(0, 3000, defaults.FadeInMilliseconds, 50),
            _settings.FadeInMilliseconds, value => _settings = _settings with { FadeInMilliseconds = value });
        var fadeOut = Number("Затухание при паузе", "мс", new(0, 3000, defaults.FadeOutMilliseconds, 50),
            _settings.FadeOutMilliseconds, value => _settings = _settings with { FadeOutMilliseconds = value });
        var crossfade = Number("Длительность перехода", "мс", new(0, 12000, defaults.CrossfadeMilliseconds, 100),
            _settings.CrossfadeMilliseconds, value => _settings = _settings with { CrossfadeMilliseconds = value });
        var autoMix = Toggle("Автомикс", _settings.AutoMix, value => _settings = _settings with { AutoMix = value });
        return new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Section("Плавный старт и пауза", Pair(fadeIn.Row, fadeOut.Row)),
                Section("Переход между треками", autoMix, crossfade.Row)
            }
        };
    }

    private View CreateDeckSection()
    {
        var defaults = new PlayerSettings();
        var detail = Number("Длительность верхней волны", "с",
            new(PlayerSettings.MinimumWaveformDetailSeconds, PlayerSettings.MaximumWaveformDetailSeconds,
                defaults.WaveformDetailSeconds, 1), _settings.WaveformDetailSeconds,
            value => _settings = _settings with { WaveformDetailSeconds = value });
        var overview = Select("Вид нижней дорожки", ["Полоса прогресса", "Цветная волна"],
            _settings.WaveformOverview ? 1 : 0,
            index => _settings = _settings with { WaveformOverview = index == 1 });
        return Section("Дорожки деки", Pair(detail.Row, overview));
    }

    private View CreateColorSection()
    {
        var palette = Select("Цвета", ["LavPulse", "Океан", "Тёплая"], (int)_settings.Palette,
            index => _settings = _settings with { Palette = (PlayerPalette)index });
        var colors = Select("Цвет мастер-спектра", ["Два цвета", "Градиент по частотам", "Однотонный"],
            (int)_settings.SpectrumColors, index => _settings = _settings with { SpectrumColors = (SpectrumColorMode)index });
        return Section("Цветовая палитра", palette, colors);
    }

    private View CreateSpectrumSection()
    {
        var defaults = new PlayerSettings();
        var spectrum = Toggle("Показывать мастер-спектр", _settings.ShowSpectrum,
            value => _settings = _settings with { ShowSpectrum = value });
        var peaks = Toggle("Показывать пиковые метки", _settings.ShowPeaks,
            value => _settings = _settings with { ShowPeaks = value });
        var width = Number("Ширина с каждой стороны", "%",
            new(0, PlayerSettings.MaximumSpectrumWidthPercent, defaults.SpectrumWidthPercent, 1),
            _settings.SpectrumWidthPercent, value => _settings = _settings with { SpectrumWidthPercent = value });
        var top = Number("Отступ сверху", "полосы",
            new(0, PlayerSettings.MaximumSpectrumPaddingRows, defaults.SpectrumTopPaddingRows, 1),
            _settings.SpectrumTopPaddingRows, value => _settings = _settings with { SpectrumTopPaddingRows = value });
        var bottom = Number("Отступ снизу", "полосы",
            new(0, PlayerSettings.MaximumSpectrumPaddingRows, defaults.SpectrumBottomPaddingRows, 1),
            _settings.SpectrumBottomPaddingRows, value => _settings = _settings with { SpectrumBottomPaddingRows = value });
        return Section("Мастер-спектр", Pair(spectrum, peaks), width.Row, Pair(top.Row, bottom.Row));
    }

    private View CreateBrightnessSection()
    {
        var defaults = new PlayerSettings();
        var brightness = Number("Минимальная яркость", "%",
            new(0, 100, defaults.SpectrumMinimumBrightnessPercent, 1), _settings.SpectrumMinimumBrightnessPercent,
            value => _settings = _settings with { SpectrumMinimumBrightnessPercent = value });
        var full = Number("Уровень сигнала для полной яркости", "%",
            new(1, 100, defaults.SpectrumFullBrightnessPercent, 1), _settings.SpectrumFullBrightnessPercent,
            value => _settings = _settings with { SpectrumFullBrightnessPercent = value });
        var brightnessOptions = new VerticalStackLayout
        {
            IsEnabled = _settings.SpectrumLevelBrightness,
            Children = { Pair(brightness.Row, full.Row) }
        };
        var adaptive = new CheckBox { IsChecked = _settings.SpectrumLevelBrightness, Color = MixerStyle.Accent };
        SemanticProperties.SetDescription(adaptive, "Яркость по уровню частоты");
        adaptive.CheckedChanged += (_, args) =>
        {
            if (_loading) return;
            brightness.Input.CommitText();
            full.Input.CommitText();
            _settings = _settings with { SpectrumLevelBrightness = args.Value };
            brightnessOptions.IsEnabled = args.Value;
        };
        return Section("Яркость мастер-спектра",
            ControlRow("Яркость по уровню частоты", adaptive), brightnessOptions);
    }

    private (View Row, NumericSettingInput Input) Number(
        string name, string unit, NumericRange range, int selected, Action<int> update)
    {
        var input = new NumericSettingInput(name, unit, range, selected);
        input.ValueChanged += value => update((int)value);
        _visibleNumbers.Add(input);
        return (new VerticalStackLayout { Spacing = 3, Children = { MixerStyle.Label(name, 13), input } }, input);
    }

    private View Select(string name, string[] labels, int selected, Action<int> update)
    {
        var picker = new Picker
        {
            ItemsSource = labels, TextColor = MixerStyle.TextColor, BackgroundColor = MixerStyle.Overlay,
            FontSize = 14, SelectedIndex = Math.Max(0, selected)
        };
        SemanticProperties.SetDescription(picker, name);
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && picker.SelectedIndex >= 0) update(picker.SelectedIndex);
        };
        return new VerticalStackLayout { Spacing = 3, Children = { MixerStyle.Label(name, 13), picker } };
    }

    private View Toggle(string name, bool selected, Action<bool> update)
    {
        var toggle = new Switch { IsToggled = selected, OnColor = MixerStyle.Accent };
        SemanticProperties.SetDescription(toggle, name);
        toggle.Toggled += (_, args) => { if (!_loading) update(args.Value); };
        return ControlRow(name, toggle);
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

    /// <summary>Places related controls side by side on large screens.</summary>
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
        if (MixerStyle.IsPhone) return pair;

        bool wide = false;
        pair.SizeChanged += (_, _) =>
        {
            bool next = pair.Width >= 480;
            if (wide == next) return;
            wide = next;
            Grid.SetColumnSpan(first, wide ? 1 : 2);
            Grid.SetColumnSpan(second, wide ? 1 : 2);
            Grid.SetRow(second, wide ? 0 : 1);
            Grid.SetColumn(second, wide ? 1 : 0);
        };
        return pair;
    }

    private static Border Section(string title, params View[] rows)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        var heading = MixerStyle.Label(title, 15, MixerStyle.Accent, bold: true);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level2);
        stack.Children.Add(heading);
        foreach (var row in rows) stack.Children.Add(row);
        var section = new Border();
        MixerStyle.StyleCard(section, stack);
        return section;
    }

    /// <summary>Uses the existing file pickers from the library section.</summary>
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
            try { await pick(); }
            finally { files.IsEnabled = folder.IsEnabled = true; }
        }
    }

    private async Task CloseAsync(Func<Task>? dismiss, Action? apply)
    {
        if (_closing) return;
        _closing = true;
        apply?.Invoke();
        if (dismiss is null) await Navigation.PopModalAsync();
        else await dismiss();
    }
}
