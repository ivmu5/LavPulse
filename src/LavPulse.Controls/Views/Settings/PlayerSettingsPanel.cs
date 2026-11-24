namespace Laveyana.LavPulse.Controls;

/// <summary>Edits one preference set with one expanded, lazily created section at a time.</summary>
public sealed partial class PlayerSettingsPanel : ContentView
{
    private readonly Action<PlayerSettings>? _previewAppearance;
    private readonly ContentView[] _sectionHosts = new ContentView[8];
    private readonly Button[] _sectionButtons = new Button[8];
    private readonly IReadOnlyList<LibraryStorageOption> _storageOptions;
    private readonly Func<Task<string?>>? _pickStorageFolder;
    private readonly Func<Task<string>>? _copyReport;
    private readonly Func<Task<string?>>? _exportReportZip;
    private readonly Func<Task<string>>? _reportSummary;
    private readonly Func<Task<string>>? _clearReports;
    private readonly List<NumericSettingInput> _visibleNumbers = [];
    private readonly Grid _settingsLayout;
    private readonly ContentView _paletteEditorHost = new() { IsVisible = false };
    private PlayerSettings _settings;
    private int _activeSection = -1;
    private bool _loading;

    public PlayerSettingsPanel(PlayerSettings settings, Action<PlayerSettings> apply, Action dismiss,
        Action<PlayerSettings>? previewAppearance = null,
        IReadOnlyList<LibraryStorageOption>? storageOptions = null,
        Func<Task<string?>>? pickStorageFolder = null,
        Func<Task<string>>? copyReport = null,
        Func<Task<string?>>? exportReportZip = null,
        Func<Task<string>>? reportSummary = null,
        Func<Task<string>>? clearReports = null,
        string? appVersion = null)
    {
        _settings = settings;
        _copyReport = copyReport;
        _exportReportZip = exportReportZip;
        _reportSummary = reportSummary;
        _clearReports = clearReports;
        _storageOptions = storageOptions ?? [];
        _pickStorageFolder = pickStorageFolder;
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
        string[] names = ["Воспроизведение", "Деки и волны", "Темы и цвета", "Интерфейс и FPS", "Мастер-спектр",
            "Анализ треков", "Хранилище", "Отчёты"];
        for (int index = 0; index < names.Length; index++)
        {
            int section = index;
            string? group = index switch
            {
                0 => "Плеер",
                2 => "Внешний вид",
                5 => "Медиатека и диагностика",
                _ => null
            };
            if (group is not null)
            {
                var groupHeading = MixerStyle.Label(group, 12, MixerColor.Muted, bold: true);
                groupHeading.Margin = new Thickness(0, index == 0 ? 0 : 8, 0, 0);
                SemanticProperties.SetHeadingLevel(groupHeading, SemanticHeadingLevel.Level2);
                sections.Children.Add(groupHeading);
            }
            var button = MixerStyle.Button(names[index]);
            button.HorizontalOptions = LayoutOptions.Fill;
            button.Clicked += (_, _) => ShowSection(section, commit: true);
            _sectionButtons[index] = button;
            var content = new ContentView();
            _sectionHosts[index] = content;
            sections.Children.Add(button);
            sections.Children.Add(content);
        }

        if (!string.IsNullOrWhiteSpace(appVersion))
        {
            var version = MixerStyle.Label("LavPulse v" + appVersion, 11, MixerColor.Muted);
            version.HorizontalTextAlignment = TextAlignment.Center;
            sections.Children.Add(version);
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
            5 => CreateAnalysisSection(),
            6 => CreateStorageSection(),
            _ => CreateReportSection()
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
            ColumnSpacing = 12,
            RowSpacing = 8
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
