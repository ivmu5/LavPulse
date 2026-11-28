namespace Laveyana.LavPulse.Controls;

/// <summary>Edits one preference set with one expanded, lazily created section at a time.</summary>
public sealed partial class PlayerSettingsPanel : ContentView
{
    private readonly Action<PlayerSettings>? _previewAppearance;
#if WINDOWS
    private const int SectionCount = 9;
#else
    private const int SectionCount = 8;
#endif
    private readonly ContentView[] _sectionHosts = new ContentView[SectionCount];
    private readonly Button[] _sectionButtons = new Button[SectionCount];
    private readonly IReadOnlyList<LibraryStorageOption> _storageOptions;
    private readonly Func<Task<string?>>? _pickStorageFolder;
    private readonly Func<Task<string>>? _copyReport;
    private readonly Func<Task<string?>>? _exportReportZip;
    private readonly Func<Task<string>>? _reportSummary;
    private readonly Func<Task<string>>? _clearReports;
    private readonly Func<Task<string?>>? _openDefaultApps;
    private readonly List<NumericSettingInput> _visibleNumbers = [];
    private readonly Grid _settingsLayout;
    private readonly ContentView _wideSectionHost = new();
    private bool _wide;
    private readonly ScrollView _detailScroll = new() { IsVisible = false };
    private ContentView ActiveSectionHost => _wide ? _wideSectionHost : _sectionHosts[_activeSection];
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
        string? appVersion = null,
        Func<Task<string?>>? openDefaultApps = null)
    {
        _settings = settings;
        _copyReport = copyReport;
        _exportReportZip = exportReportZip;
        _reportSummary = reportSummary;
        _clearReports = clearReports;
        _openDefaultApps = openDefaultApps;
        _storageOptions = storageOptions ?? [];
        _pickStorageFolder = pickStorageFolder;
        _previewAppearance = previewAppearance;
        var heading = MixerStyle.Label("Настройки", MixerStyle.PhoneFont(23, 20), bold: true);
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level1);
#if WINDOWS
        heading.IsVisible=false;
#endif
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
        var navigation = new ScrollView { Content = sections };
        _detailScroll.Content = _wideSectionHost;
        var detail = _detailScroll;
        string[] names = ["Воспроизведение", "Деки и волны", "Темы и цвета", "Интерфейс и FPS", "Мастер-спектр",
            "Анализ треков", "Хранилище", "Отчёты"
#if WINDOWS
            , "Уведомления"
#endif
            ];
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
            var content = new ContentView { IsVisible = false };
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
        var cancel = MixerStyle.IconButton("player_cancel.png", 44);
        SemanticProperties.SetDescription(cancel, "Отмена");
        ToolTipProperties.SetText(cancel, "Отмена");
        save.Clicked += (_, _) =>
        {
            CommitVisibleNumbers();
            apply(_settings);
            dismiss();
        };
        cancel.Clicked += (_, _) => dismiss();
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 8 };
        actions.Add(cancel);
        actions.Add(save, 1);

        int topInset = 16;
#if WINDOWS
        topInset += WindowsWindowChrome.ContentInset;
#endif
        _settingsLayout = new Grid
        {
            Padding = new Thickness(AdaptivePanelLayout.Padding, topInset, AdaptivePanelLayout.Padding, 16),
            RowSpacing = 12,
            MaximumWidthRequest = AdaptivePanelLayout.MaximumWidth,
            HorizontalOptions = LayoutOptions.Center,
            ColumnDefinitions = [new(0), new(GridLength.Star), new(0)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        _settingsLayout.Add(header, 1, 0);
        _settingsLayout.Add(navigation, 1, 1);
        _settingsLayout.Add(detail, 1, 1);
        _settingsLayout.Add(actions, 1, 2);
        var host = new Grid { Children = { _settingsLayout, _paletteEditorHost } };
        double navFontSize = _sectionButtons[0].FontSize;
        double resetFontSize = reset.FontSize;
        var navPadding = _sectionButtons[0].Padding;
        bool resetOnRail = false;
        host.SizeChanged += (_, _) =>
        {
            var size = AdaptivePanelLayout.Measure(host.Width, host.Height);
            if (size.Width <= 0) return;
            bool shortWindow = AdaptivePanelLayout.IsShortLandscape(host.Width, host.Height);
            _settingsLayout.Padding = new Thickness(AdaptivePanelLayout.Padding,
                shortWindow ? Math.Max(4, topInset - 8) : topInset, AdaptivePanelLayout.Padding, shortWindow ? 6 : 16);
            _settingsLayout.RowSpacing = shortWindow ? 5 : 12;
            _settingsLayout.WidthRequest = size.Width;
            _settingsLayout.ColumnDefinitions[0].Width = new GridLength(size.LeftColumnWidth(navigation: true));
            _settingsLayout.ColumnDefinitions[2].Width = new GridLength(size.RightColumnWidth(navigation: true));
            _settingsLayout.ColumnSpacing = size.ColumnSpacing;
            navigation.Margin = new Thickness(0, 0, size.RailGutter, 0);
            foreach (var button in _sectionButtons)
            {
                button.FontSize = size.HasCompactRails ? 11 : navFontSize;
                button.LineBreakMode = LineBreakMode.WordWrap;
                button.Padding = size.HasCompactRails ? new Thickness(4, 5) : navPadding;
            }
            reset.FontSize = size.HasCompactRails ? 11 : resetFontSize;
            reset.LineBreakMode = LineBreakMode.WordWrap;
            if (_wide != size.IsWide)
            {
                // Reparent only the current section. Two/three-column changes
                // do not rebuild controls or discard pending edits.
                View? current = null;
                if (_activeSection >= 0)
                {
                    CommitVisibleNumbers();
                    current = ActiveSectionHost.Content;
                    ActiveSectionHost.Content = null;
                    _sectionHosts[_activeSection].IsVisible = false;
                }
                _wide = size.IsWide;
                Grid.SetColumn(navigation, _wide ? 0 : 1);
                detail.IsVisible = _wide;
                if (_activeSection >= 0)
                {
                    _sectionHosts[_activeSection].IsVisible = !_wide;
                    ActiveSectionHost.Content = current;
                }
            }
            // In two columns, the additional area is navigation. Reset
            // stays above the main content, not in a third column.
            if (resetOnRail != size.IsThreeColumn)
            {
                resetOnRail = size.IsThreeColumn;
                if (resetOnRail)
                {
                    header.Children.Remove(reset);
                    reset.VerticalOptions = LayoutOptions.Start;
                    _settingsLayout.Add(reset, 2, 1);
                }
                else
                {
                    _settingsLayout.Children.Remove(reset);
                    reset.VerticalOptions = LayoutOptions.Center;
                    header.Add(reset, 1);
                }
            }
            header.IsVisible = !resetOnRail || heading.IsVisible;
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
        if (_activeSection >= 0)
        {
            ActiveSectionHost.Content = null;
            _sectionHosts[_activeSection].IsVisible = false;
        }
        _visibleNumbers.Clear();
        _activeSection = index;
        _sectionHosts[index].IsVisible = !_wide;
        ActiveSectionHost.Content = index switch
        {
            0 => CreatePlaybackSection(),
            1 => CreateDeckSection(),
            2 => CreateColorSection(),
            3 => CreateFrameRateSection(),
            4 => CreateSpectrumSection(),
            5 => CreateAnalysisSection(),
            6 => CreateStorageSection(),
#if WINDOWS
            8 => CreateNotificationSection(),
#endif
            _ => CreateReportSection()
        };
        for (int section = 0; section < _sectionButtons.Length; section++)
        {
            bool selected = section == index;
            MixerStyle.UseColor(_sectionButtons[section], BackgroundColorProperty, selected ? MixerColor.Selected : MixerColor.Button);
            _sectionButtons[section].FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
        }
        _loading = false;
        if (_wide) _ = ScrollSectionToStartAsync();
    }

    private async Task ScrollSectionToStartAsync()
    {
        try { await _detailScroll.ScrollToAsync(0, 0, false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
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
