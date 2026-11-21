namespace Laveyana.Audio.Maui.Controls;

/// <summary>Edits one preference set with one expanded, lazily created section at a time.</summary>
public sealed partial class PlayerSettingsPanel : ContentView
{
    private readonly Action<PlayerSettings>? _previewAppearance;
    private readonly ContentView[] _sectionHosts = new ContentView[6];
    private readonly Button[] _sectionButtons = new Button[6];
    private readonly List<NumericSettingInput> _visibleNumbers = [];
    private readonly Grid _settingsLayout;
    private readonly ContentView _paletteEditorHost = new() { IsVisible = false };
    private PlayerSettings _settings;
    private int _activeSection = -1;
    private bool _loading;

    public PlayerSettingsPanel(PlayerSettings settings, Action<PlayerSettings> apply, Action dismiss,
        Action<PlayerSettings>? previewAppearance = null)
    {
        _settings = settings;
        _previewAppearance = previewAppearance;
        var heading = MixerStyle.Label("Настройки", MixerStyle.PhoneFont(23, 20), bold: true);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
        var reset = MixerStyle.Button("Сбросить", icon: "player_reset.png");
        reset.Clicked += (_, _) =>
        {
            _settings = new PlayerSettings();
            _previewAppearance?.Invoke(_settings);
            ShowSection(_activeSection, commit: false);
        };
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        header.Add(heading);
        header.Add(reset, 1);

        var sections = new VerticalStackLayout { Spacing = 8 };
        string[] names = ["Воспроизведение", "Деки", "Цвета", "Интерфейс и FPS", "Мастер-спектр", "Яркость спектра"];
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
        save.Clicked += (_, _) =>
        {
            CommitVisibleNumbers();
            apply(_settings);
            dismiss();
        };
        cancel.Clicked += (_, _) => dismiss();
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        actions.Add(cancel);
        actions.Add(save, 1);

        int topInset = 16;
#if WINDOWS
        topInset += WindowsWindowChrome.ContentInset;
#endif
        _settingsLayout = new Grid
        {
            Padding = new Thickness(16, topInset, 16, 16),
            RowSpacing = 12,
            MaximumWidthRequest = 660,
            HorizontalOptions = LayoutOptions.Center,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        _settingsLayout.Add(header);
        _settingsLayout.Add(new ScrollView { Content = sections }, row: 1);
        _settingsLayout.Add(actions, row: 2);
        var host = new Grid { Children = { _settingsLayout, _paletteEditorHost } };
        host.SizeChanged += (_, _) =>
        {
            if (host.Width > 0) _settingsLayout.WidthRequest = Math.Min(host.Width, 660);
        };
        Content = host;

        // Initial UI creates only the playback controls. Other sections are opened on demand.
        ShowSection(0, commit: false);
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
            0 => CreatePlaybackSection(),
            1 => CreateDeckSection(),
            2 => CreateColorSection(),
            3 => CreateFrameRateSection(),
            4 => CreateSpectrumSection(),
            _ => CreateBrightnessSection()
        };
        for (int section = 0; section < _sectionButtons.Length; section++)
        {
            bool selected = section == index;
            MixerStyle.UseColor(_sectionButtons[section], BackgroundColorProperty, selected ? MixerColor.Selected : MixerColor.Button);
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
        var sections = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Section("Плавный старт и пауза", Pair(fadeIn.Row, fadeOut.Row)),
                Section("Переход между треками", autoMix, crossfade.Row)
            }
        };
#if ANDROID
        var lockScreen = Toggle("Показывать плеер поверх экрана блокировки", _settings.ShowOnLockScreen,
            value => _settings = _settings with { ShowOnLockScreen = value });
        sections.Add(Section("Экран блокировки", lockScreen));
#endif
        return sections;
    }

    private View CreateDeckSection()
    {
        var defaults = new PlayerSettings();
        var detail = Number("Длительность верхней волны", "с",
            new(PlayerSettings.MinimumWaveformDetailSeconds, PlayerSettings.MaximumWaveformDetailSeconds,
                defaults.WaveformDetailSeconds, 1), _settings.WaveformDetailSeconds,
            value => _settings = _settings with { WaveformDetailSeconds = value });
        var overview = Select("Вид нижней дорожки", [(false, "Полоса прогресса"), (true, "Цветная волна")],
            _settings.WaveformOverview, CreateTimelinePreview,
            waveform => _settings = _settings with { WaveformOverview = waveform });
        return Section("Дорожки деки", detail.Row, overview);
    }

    private View CreateColorSection()
    {
        var samples = new List<Action>();
        var previews = new List<(SpectrumColorMode Mode, SpectrumDrawable Drawable, GraphicsView View)>();
        void Preview(PlayerSettings settings)
        {
            _settings = settings;
            _previewAppearance?.Invoke(settings);
            var theme = MixerTheme.Create(settings, MixerTheme.SystemTheme);
            foreach (var sample in samples) sample();
            foreach (var preview in previews)
            {
                if (preview.Drawable.SetAppearance(theme.Visualization, SpectrumPreviewSettings(settings, preview.Mode), theme.IsLight))
                    preview.View.Invalidate();
            }
        }

        var themeOptions = Select("Режим", [(PlayerTheme.System, "Системная"), (PlayerTheme.Dark, "Тёмная"),
            (PlayerTheme.Light, "Светлая")], _settings.Theme,
            mode => ThemePreview(() => _settings with { Theme = mode }, mode == PlayerTheme.System, samples),
            mode => Preview(_settings with { Theme = mode }));
        var backgrounds = Select("Фон тёмной темы", [(DarkBackground.Classic, "Обычный"), (DarkBackground.Amoled, "AMOLED")],
            _settings.DarkBackground,
            background => ThemePreview(() => _settings with { Theme = PlayerTheme.Dark, DarkBackground = background }, false, samples),
            background => Preview(_settings with { DarkBackground = background }));
        var spectrum = Select("Цвет мастер-спектра",
            [(SpectrumColorMode.Split, "Два цвета"), (SpectrumColorMode.FrequencyGradient, "Градиент по частотам"),
             (SpectrumColorMode.Monochrome, "Однотонный")],
            _settings.SpectrumColors, mode =>
            {
                var theme = MixerTheme.Create(_settings, MixerTheme.SystemTheme);
                var view = CreateSpectrumPreview(_settings, theme.Visualization, mode, out var drawable, theme.IsLight);
                previews.Add((mode, drawable, view));
                return view;
            }, mode => Preview(_settings with { SpectrumColors = mode }));
        var palette = Select("Палитра", [(PlayerPalette.LavPulse, "LavPulse"), (PlayerPalette.Ocean, "Океан"),
            (PlayerPalette.Warm, "Тёплая"), (PlayerPalette.Custom, "Своя")], _settings.Palette,
            choice => CreatePalettePreview(PlayerColors.For(_settings with { Palette = choice })),
            choice => Preview(_settings with { Palette = choice }));
        var edit = MixerStyle.Button("Настроить свою палитру");
        edit.HorizontalOptions = LayoutOptions.Fill;
        edit.Clicked += (_, _) => TryOpenPaletteEditor();
        var sections = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Section("Тема интерфейса", themeOptions, backgrounds) }
        };
#if ANDROID || IOS
        sections.Add(Section("Экран", Toggle("Скрывать строку состояния", _settings.HideStatusBar, value =>
        {
            _settings = _settings with { HideStatusBar = value };
            _previewAppearance?.Invoke(_settings);
        })));
#endif
        sections.Add(Section("Цветовая палитра", palette, edit, spectrum));
        return sections;
    }

    private View CreateFrameRateSection()
    {
        var frameRate = Select("Частота анимаций (FPS)",
            [(SpectrumFrameRate.Automatic, "Авто"), (SpectrumFrameRate.Fps30, "30"), (SpectrumFrameRate.Fps60, "60"),
             (SpectrumFrameRate.Fps90, "90"), (SpectrumFrameRate.Fps120, "120"),
#if WINDOWS
             (SpectrumFrameRate.Fps144, "144"), (SpectrumFrameRate.Fps165, "165"),
             (SpectrumFrameRate.Fps240, "240"), (SpectrumFrameRate.Fps360, "360"),
#endif
            ], _settings.SpectrumFrameRate, preview: null, value =>
            {
                _settings = _settings with { SpectrumFrameRate = value };
                _previewAppearance?.Invoke(_settings);
            }, maxColumns: 5);
        var hint = MixerStyle.Label("Мастер-спектр, деки и очередь. В режиме «Авто» частоту задаёт дисплей.", 12, MixerColor.Muted);
        return Section("Интерфейс и FPS", frameRate, hint);
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
        var adaptive = new CheckBox { IsChecked = _settings.SpectrumLevelBrightness };
        MixerStyle.UseColor(adaptive, CheckBox.ColorProperty, MixerColor.Accent);
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

    private View Toggle(string name, bool selected, Action<bool> update)
    {
        var toggle = new Switch { IsToggled = selected };
        MixerStyle.StyleSwitch(toggle);
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
        var heading = MixerStyle.Label(title, 15, MixerColor.Accent, bold: true);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level2);
        stack.Children.Add(heading);
        foreach (var row in rows) stack.Children.Add(row);
        var section = new Border();
        MixerStyle.StyleCard(section, stack);
        return section;
    }
}
