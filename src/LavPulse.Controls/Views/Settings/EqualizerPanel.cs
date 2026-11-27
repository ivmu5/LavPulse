using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

/// <summary>Edits the master EQ live, with an independent bypass and a flat reset.</summary>
public sealed class EqualizerPanel : ContentView
{
    private readonly NumericSettingInput[] _bands;
    private readonly Action<EqualizerSettings> _apply;

    public EqualizerSettings Settings { get; private set; }

    public EqualizerPanel(EqualizerSettings settings, Action<EqualizerSettings> apply, Action dismiss)
    {
        Settings = settings;
        _apply = apply;
        var title = MixerStyle.Label("Эквалайзер", 23, bold: true);
#if WINDOWS
        title.IsVisible=false;
#endif
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        var toggle = new Switch { IsToggled = settings.Enabled };
        MixerStyle.StyleSwitch(toggle);
        SemanticProperties.SetDescription(toggle, "Включить эквалайзер");
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        header.Add(new VerticalStackLayout { Children = { title, MixerStyle.Label("Усиление, дБ", 12, MixerColor.Muted) } });
        header.Add(toggle, 1);

        var bands = new Grid { RowSpacing = 12, ColumnSpacing = 4 };
        const int compactColumns = 5;
        int visibleColumns = compactColumns;
        var frequencies = EqualizerSettings.Frequencies;
        _bands = new NumericSettingInput[frequencies.Count + 1];
        var rows = new View[_bands.Length];
        for (int i = 0; i < _bands.Length; i++)
        {
            int band = i;
            bool output = i == frequencies.Count;
            int hz = output ? 0 : frequencies[i];
            string frequency = output ? "Уровень" : hz < 1000 ? $"{hz}" : $"{hz / 1000}к";
            var input = new NumericSettingInput(output ? "Общий уровень" : $"{hz} Гц", "дБ",
                new(EqualizerSettings.MinimumGainDb, EqualizerSettings.MaximumGainDb, 0, 1),
                output ? settings.OutputGainDb : settings.GainAt(band), vertical: true);
            input.ValueChanged += value =>
            {
                Settings = output ? Settings.WithOutputGain(value) : Settings.WithGain(band, value);
                _apply(Settings);
            };
            _bands[i] = input;
            var label = MixerStyle.Label(frequency, 12, output ? MixerColor.Accent : MixerColor.Text);
            label.HorizontalTextAlignment = TextAlignment.Center;
            rows[i] = new VerticalStackLayout { Children = { label, input } };
            bands.Children.Add(rows[i]);
        }
        ArrangeFaders(compactColumns);
        bands.SizeChanged += (_, _) =>
        {
            int columns = bands.Width >= 704 ? rows.Length : compactColumns;
            if (columns == visibleColumns) return;
            visibleColumns = columns;
            ArrangeFaders(columns);
        };

        void ArrangeFaders(int columns)
        {
            bands.ColumnDefinitions.Clear();
            bands.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++) bands.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (int i = 0; i < (rows.Length + columns - 1) / columns; i++) bands.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int i = 0; i < rows.Length; i++)
            {
                bool singleOutput = i == frequencies.Count && i % columns == 0;
                Grid.SetColumn(rows[i], singleOutput ? columns / 2 : i % columns);
                Grid.SetRow(rows[i], i / columns);
            }
        }
        toggle.Toggled += (_, args) =>
        {
            Settings = Settings.WithEnabled(args.Value);
            _apply(Settings);
        };

        var reset = MixerStyle.Button("Сбросить", icon: "player_reset.png");
        reset.Clicked += (_, _) =>
        {
            foreach (var band in _bands) band.Value = 0;
            Settings = EqualizerSettings.Flat.WithEnabled(Settings.Enabled);
            _apply(Settings);
        };
        var close = MixerStyle.Button("Готово", primary: true, icon: "player_confirm.png");
        close.Clicked += (_, _) => dismiss();
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        actions.Add(reset);
        actions.Add(close, 1);
        int topInset = 16;
#if WINDOWS
        topInset += WindowsWindowChrome.ContentInset;
#endif
        var layout = new Grid
        {
            Padding = new Thickness(16, topInset, 16, 16),
            RowSpacing = 12,
            HorizontalOptions = LayoutOptions.Center,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        layout.Add(header);
        layout.Add(new ScrollView { Content = bands }, row: 1);
        layout.Add(actions, row: 2);
        var host = new Grid { Children = { layout } };
        host.SizeChanged += (_, _) => { if (host.Width > 0) layout.WidthRequest = Math.Min(host.Width, 848); };
        Content = host;
    }

    public void CommitPendingEdits()
    {
        foreach (var band in _bands) band.CommitText();
    }
}
