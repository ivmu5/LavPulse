using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Edits the master EQ live, with an independent bypass and a flat reset.</summary>
public sealed class EqualizerPage : ContentPage
{
    private readonly NumericSettingInput[] _bands;
    private readonly Action<EqualizerSettings> _apply;
    private readonly Action<EqualizerSettings> _save;
    private bool _closing;

    public EqualizerSettings Settings { get; private set; }

    public EqualizerPage(EqualizerSettings settings, Action<EqualizerSettings> apply, Action<EqualizerSettings> save)
    {
        Settings = settings;
        _apply = apply;
        _save = save;
        BackgroundColor = MixerStyle.Background;
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container);

        var title = MixerStyle.Label("Эквалайзер", 23, bold: true);
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        var toggle = new Switch { IsToggled = settings.Enabled, OnColor = MixerStyle.Accent };
        SemanticProperties.SetDescription(toggle, "Включить эквалайзер");
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        header.Add(title);
        header.Add(toggle, 1);

        var bands = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], RowSpacing = 9, ColumnSpacing = 16 };
        var frequencies = EqualizerSettings.Frequencies;
        _bands = new NumericSettingInput[frequencies.Count];
        var rows = new View[_bands.Length];
        for (int i = 0; i < frequencies.Count; i++)
        {
            int band = i;
            int hz = frequencies[i];
            string frequency = hz < 1000 ? $"{hz} Гц" : $"{hz / 1000} кГц";
            var input = new NumericSettingInput(frequency, "дБ", new(EqualizerSettings.MinimumGainDb, EqualizerSettings.MaximumGainDb, 0, 1),
                settings.GainAt(band));
            input.ValueChanged += value =>
            {
                Settings = Settings.WithGain(band, value);
                _apply(Settings);
            };
            _bands[i] = input;
            rows[i] = new VerticalStackLayout { Children = { MixerStyle.Label(frequency, 13), input } };
            bands.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            bands.Add(rows[i], row: i);
            Grid.SetColumnSpan(rows[i], 2);
        }
        bool wide = false;
        bands.SizeChanged += (_, _) =>
        {
            bool next = bands.Width >= 510;
            if (wide == next) return;
            wide = next;
            for (int i = 0; i < rows.Length; i++)
            {
                Grid.SetColumnSpan(rows[i], wide ? 1 : 2);
                Grid.SetColumn(rows[i], wide ? i % 2 : 0);
                Grid.SetRow(rows[i], wide ? i / 2 : i);
            }
        };
        toggle.Toggled += (_, args) =>
        {
            Settings = Settings.WithEnabled(args.Value);
            _apply(Settings);
        };

        var reset = MixerStyle.Button("Сбросить полосы", icon: "player_reset.png");
        reset.Clicked += (_, _) =>
        {
            foreach (var band in _bands) band.Value = 0;
            Settings = EqualizerSettings.Flat.WithEnabled(Settings.Enabled);
            _apply(Settings);
        };
        var close = MixerStyle.Button("Готово", primary: true, icon: "player_confirm.png");
        close.Clicked += async (_, _) =>
        {
            if (_closing) return;
            _closing = true;
            await Navigation.PopModalAsync();
        };
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        actions.Add(reset);
        actions.Add(close, 1);
        int topInset = 16;
#if WINDOWS
        topInset += WindowsWindowChrome.ContentInset;
#endif
        var layout = new Grid
        {
            Padding = new Thickness(16, topInset, 16, 16), RowSpacing = 12, HorizontalOptions = LayoutOptions.Center,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        layout.Add(header);
        layout.Add(new ScrollView { Content = bands }, row: 1);
        layout.Add(actions, row: 2);
        SizeChanged += (_, _) => { if (Width > 0) layout.WidthRequest = Math.Min(Width, 680); };
        Content = layout;
    }

    protected override void OnDisappearing()
    {
        foreach (var band in _bands) band.CommitText();
        _save(Settings);
        base.OnDisappearing();
    }
}
