namespace Laveyana.LavPulse.Controls;

public sealed partial class PlayerSettingsPanel
{
    private View CreateAnalysisSection()
    {
        int processors = Environment.ProcessorCount;
        int maximum = TrackAnalysisParallelism.MaximumFor(processors);
        int automatic = TrackAnalysisParallelism.Resolve(TrackAnalysisParallelism.Automatic, processors);
        (int Value, string Label)[] options =
        [
            (TrackAnalysisParallelism.Automatic, $"Авто ({automatic})"),
            .. Enumerable.Range(TrackAnalysisParallelism.Minimum, maximum).Select(count => (count, count.ToString()))
        ];
        int selected = Math.Clamp(_settings.AnalysisParallelism, TrackAnalysisParallelism.Automatic, maximum);
        var parallelism = Select("Параллельные задачи анализа", options, selected, preview: null,
            value => _settings = _settings with { AnalysisParallelism = value }, maxColumns: 4);
        return Section("Производительность анализа", parallelism);
    }

    private View CreatePlaybackSection()
    {
        var defaults = new PlayerSettings();
        var fadeIn = Number("Нарастание громкости при запуске", "мс", new(0, 3000, defaults.FadeInMilliseconds, 50),
            _settings.FadeInMilliseconds, value => _settings = _settings with { FadeInMilliseconds = value });
        var fadeOut = Number("Затухание громкости при паузе", "мс", new(0, 3000, defaults.FadeOutMilliseconds, 50),
            _settings.FadeOutMilliseconds, value => _settings = _settings with { FadeOutMilliseconds = value });
        var crossfade = Number("Длительность перехода", "мс", new(0, 12000, defaults.CrossfadeMilliseconds, 100),
            _settings.CrossfadeMilliseconds, value => _settings = _settings with { CrossfadeMilliseconds = value });
        var autoMix = Toggle("Автомикс", _settings.AutoMix, value => _settings = _settings with { AutoMix = value });
        var visualDelay = Number("Задержка визуализации", "мс", new(0, 1500, defaults.VisualDelayMilliseconds, 10),
            _settings.VisualDelayMilliseconds, value => _settings = _settings with { VisualDelayMilliseconds = value });
        var delayHint = MixerStyle.Label("Дополнительная задержка волн и спектра для синхронизации со звуком, в том числе через Bluetooth. " +
            "Не влияет на воспроизведение аудио.", 11, MixerColor.Muted);
        var sections = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Section("Плавное воспроизведение", Pair(fadeIn.Row, fadeOut.Row)),
                Section("Переход между треками", autoMix, crossfade.Row),
                Section("Синхронизация со звуком", visualDelay.Row, delayHint)
            }
        };
#if ANDROID
        var lockScreen = Toggle("Плеер на экране блокировки", _settings.ShowOnLockScreen,
            value => _settings = _settings with { ShowOnLockScreen = value });
        sections.Add(Section("Экран блокировки", lockScreen));
#endif
        return sections;
    }

    private View CreateDeckSection()
    {
        var defaults = new PlayerSettings();
        var detail = Number("Временной диапазон детальной волны", "с",
            new(PlayerSettings.MinimumWaveformDetailSeconds, PlayerSettings.MaximumWaveformDetailSeconds,
                defaults.WaveformDetailSeconds, 1), _settings.WaveformDetailSeconds,
            value => _settings = _settings with { WaveformDetailSeconds = value });
        var detailed = Select("Детальная волна",
            [(DeckWaveformMode.Color, "Цветная"), (DeckWaveformMode.BeatGrid, "Сетка долей"),
             (DeckWaveformMode.Transients, "Удары и сетка")],
            _settings.DetailedWaveform, CreateDetailedTimelinePreview,
            mode => _settings = _settings with { DetailedWaveform = mode });
        int overviewChoice = !_settings.WaveformOverview ? 0 : _settings.WaveformOverviewBeatGrid ? 2 : 1;
        var overview = Select("Обзорная волна", [(0, "Полоса прогресса"), (1, "Цветная"), (2, "Цветная с сеткой")],
            overviewChoice, CreateTimelinePreview,
            value => _settings = _settings with { WaveformOverview = value != 0, WaveformOverviewBeatGrid = value == 2 });
        var hint = MixerStyle.Label("Сетка долей отображается на участках с определённым темпом. " +
            "Режим «Удары и сетка» дополнительно отображает обнаруженные атаки.", 11, MixerColor.Muted);
        return Section("Отображение волн", detail.Row, detailed, overview, hint);
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

        var themeOptions = Select("Тема", [(PlayerTheme.System, "Системная"), (PlayerTheme.Dark, "Тёмная"),
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
            (PlayerPalette.Warm, "Тёплая"), (PlayerPalette.Custom, "Пользовательская")], _settings.Palette,
            choice => CreatePalettePreview(PlayerColors.For(_settings with { Palette = choice })),
            choice => Preview(_settings with { Palette = choice }));
        var edit = MixerStyle.Button("Редактор палитры");
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
        var hint = MixerStyle.Label("Максимальная частота обновления анимаций плеера. Авто — частота дисплея. " +
            "Фактическая частота зависит от производительности устройства.", 12, MixerColor.Muted);
        return Section("Интерфейс и FPS", frameRate, hint);
    }

    private View CreateSpectrumSection()
    {
        var defaults = new PlayerSettings();
        var spectrum = Toggle("Мастер-спектр", _settings.ShowSpectrum,
            value => _settings = _settings with { ShowSpectrum = value });
        var peaks = Toggle("Пиковые метки", _settings.ShowPeaks,
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
        return new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Section("Отображение", Pair(spectrum, peaks)),
                Section("Размеры и отступы", width.Row, Pair(top.Row, bottom.Row)),
                CreateBrightnessSection()
            }
        };
    }

    private View CreateBrightnessSection()
    {
        var defaults = new PlayerSettings();
        var brightness = Number("Минимальная яркость", "%",
            new(0, 100, defaults.SpectrumMinimumBrightnessPercent, 1), _settings.SpectrumMinimumBrightnessPercent,
            value => _settings = _settings with { SpectrumMinimumBrightnessPercent = value });
        var full = Number("Уровень сигнала для максимальной яркости", "%",
            new(1, 100, defaults.SpectrumFullBrightnessPercent, 1), _settings.SpectrumFullBrightnessPercent,
            value => _settings = _settings with { SpectrumFullBrightnessPercent = value });
        var brightnessOptions = new VerticalStackLayout
        {
            IsEnabled = _settings.SpectrumLevelBrightness,
            Children = { Pair(brightness.Row, full.Row) }
        };
        var adaptive = new CheckBox { IsChecked = _settings.SpectrumLevelBrightness };
        MixerStyle.UseColor(adaptive, CheckBox.ColorProperty, MixerColor.Accent);
        SemanticProperties.SetDescription(adaptive, "Яркость по уровню сигнала");
        adaptive.CheckedChanged += (_, args) =>
        {
            if (_loading) return;
            brightness.Input.CommitText();
            full.Input.CommitText();
            _settings = _settings with { SpectrumLevelBrightness = args.Value };
            brightnessOptions.IsEnabled = args.Value;
        };
        return Section("Яркость мастер-спектра",
            ControlRow("Яркость по уровню сигнала", adaptive), brightnessOptions);
    }
}
