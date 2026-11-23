using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Internals;

namespace Laveyana.LavPulse.Controls;

public sealed partial class PlayerSettingsPanel
{
    private const int PreviewHeight = 36;
    private const int FramesPerSample = 8 * PlaybackFormat.SampleRate;
    private static readonly float[] SampleLevels = [.82f, .65f, .45f, .62f, .36f, .53f, .78f, .57f, .42f, .69f];
    private static readonly TrackWaveform TimelineSample = new(
        SampleLevels.Select(level => new WaveformPoint(level, level * .85f, level * .53f, level * .27f)).ToArray(),
        (long)SampleLevels.Length * FramesPerSample, FramesPerSample)
    {
        Rhythm = new TrackBeatGrid(120, 0, 1, [])
        {
            BeatFrames = Enumerable.Range(0, 160).Select(i => (long)i * PlaybackFormat.SampleRate / 2).ToArray()
        }
    };
    private static readonly TrackWaveform DetailedTimelineSample = CreateDetailedTimelineSample();

    private static TrackWaveform CreateDetailedTimelineSample()
    {
        const int count = 320;
        int interval = PlaybackFormat.SampleRate / 25;
        var points = new WaveformPoint[count];
        var onsets = new List<long>();
        for (int i = 0; i < count; i++)
        {
            bool kick = i % 25 == 0 || i % 25 == 12;
            float level = kick ? .92f : .18f + .08f * MathF.Abs(MathF.Sin(i * .27f));
            points[i] = new(level, kick ? .85f : level * .38f, level * .47f, level * .18f);
            if (kick) onsets.Add((long)i * interval);
        }
        return new TrackWaveform(points, (long)count * interval, interval)
        {
            Rhythm = new TrackBeatGrid(120, 0, .98f, onsets.ToArray())
            {
                BeatFrames = Enumerable.Range(0, 26).Select(i => (long)i * PlaybackFormat.SampleRate / 2).ToArray()
            }
        };
    }
    private static readonly ControlTemplate PreviewChoiceTemplate = new(CreateChoiceBorder);

    // Each row is one radio group. Star columns keep every preview visible together on phones and desktops.
    private static View Select<T>(string name, (T Value, string Label)[] options, T selected, Func<T, View>? preview,
        Action<T> update, int maxColumns = int.MaxValue)
    {
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        int columns = Math.Min(maxColumns, options.Length);
        for (int column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        for (int index = 0; index < options.Length; index++)
        {
            var (value, label) = options[index];
            int row = index / columns;
            if (index % columns == 0)
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var caption = MixerStyle.Label(label, MixerStyle.PhoneFont(12, 11));
            caption.HorizontalTextAlignment = TextAlignment.Center;
            caption.VerticalTextAlignment = TextAlignment.Center;
            caption.LineBreakMode = LineBreakMode.WordWrap;
            View content = caption;
            if (preview is not null)
            {
                var sample = new Grid
                {
                    RowDefinitions = [new(new GridLength(PreviewHeight)), new(GridLength.Star)],
                    RowSpacing = 5
                };
                sample.Add(preview(value));
                sample.Add(caption, row: 1);
                content = sample;
            }
            else
            {
                caption.MinimumHeightRequest = 30;
            }

            var choice = new RadioButton
            {
                Content = content,
                ControlTemplate = PreviewChoiceTemplate,
                Padding = 0,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            };
            SemanticProperties.SetDescription(choice, $"{name}. {label}");
            grid.Add(choice, index % columns, row);
            choice.IsChecked = EqualityComparer<T>.Default.Equals(selected, value);
            choice.CheckedChanged += (_, args) =>
            {
                if (args.Value) update(value);
            };
        }
        return new VerticalStackLayout { Spacing = 6, Children = { MixerStyle.Label(name, 13), grid } };
    }

    private static Border CreateChoiceBorder()
    {
        var border = new Border
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Padding = 6,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = new ContentPresenter
            {
                InputTransparent = true,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill
            }
        };
        // The native presenter can measure the template at its desired width. Match the assigned column instead.
        border.SetBinding(WidthRequestProperty, new Binding(nameof(RadioButton.Width), source: RelativeBindingSource.TemplatedParent));
        var states = new VisualStateGroup { Name = "CheckedStates" };
        states.States.Add(ChoiceState("Unchecked", MixerColor.Surface, MixerColor.Border));
        states.States.Add(ChoiceState("Checked", MixerColor.Selected, MixerColor.Accent));
        VisualStateManager.SetVisualStateGroups(border, new VisualStateGroupList { states });
        return border;
    }

    private static VisualState ChoiceState(string name, MixerColor background, MixerColor stroke) => new()
    {
        Name = name,
        Setters =
        {
            new Setter { Property = BackgroundColorProperty, Value = new DynamicResource(MixerStyle.ColorKey(background)) },
            new Setter { Property = Border.StrokeProperty, Value = new DynamicResource(MixerStyle.BrushKey(stroke)) }
        }
    };

    private static GraphicsView ThemePreview(Func<PlayerSettings> settings, bool split, List<Action> samples)
    {
        var drawable = new ThemeSample();
        var view = new GraphicsView { Drawable = drawable, InputTransparent = true };
        void Refresh()
        {
            var options = settings();
            drawable.First = MixerTheme.Create(options, AppTheme.Dark);
            drawable.Second = split ? MixerTheme.Create(options, AppTheme.Light) : null;
            view.Invalidate();
        }
        samples.Add(Refresh);
        Refresh();
        return view;
    }

    private sealed class ThemeSample : IDrawable
    {
        public MixerTheme First { get; set; } = MixerTheme.Default;
        public MixerTheme? Second { get; set; }

        public void Draw(ICanvas canvas, RectF rect)
        {
            float width = Second is null ? rect.Width : rect.Width / 2;
            DrawTheme(canvas, new(rect.X, rect.Y, width, rect.Height), First);
            if (Second is not null) DrawTheme(canvas, new(rect.X + width, rect.Y, width, rect.Height), Second);
        }

        private static void DrawTheme(ICanvas canvas, RectF rect, MixerTheme theme)
        {
            canvas.FillColor = theme[MixerColor.Background];
            canvas.FillRectangle(rect);
            if (rect.Width < 10 || rect.Height < 12) return;
            canvas.FillColor = theme[MixerColor.Card];
            canvas.FillRoundedRectangle(rect.X + 3, rect.Y + 5, rect.Width - 6, rect.Height - 10, 4);
            canvas.FillColor = theme[MixerColor.Primary];
            canvas.FillRoundedRectangle(rect.X + rect.Width * .26f, rect.Y + rect.Height * .57f, rect.Width * .48f, 4, 2);
            canvas.FillColor = theme[MixerColor.Text];
            canvas.FillRoundedRectangle(rect.X + rect.Width * .26f, rect.Y + rect.Height * .34f, rect.Width * .33f, 2, 1);
        }
    }

    private static View CreatePalettePreview(PlayerColors colors)
    {
        var swatches = new Grid { ColumnSpacing = 2 };
        Color[] fills = [colors.Accent, colors.WaveLow, colors.WaveMid, colors.Alternate];
        for (int column = 0; column < fills.Length; column++)
        {
            swatches.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            swatches.Add(new BoxView { Color = fills[column], CornerRadius = 3 }, column);
        }
        return swatches;
    }

    private static GraphicsView CreateSpectrumPreview(PlayerSettings settings, PlayerColors colors,
        SpectrumColorMode mode, out SpectrumDrawable drawable, bool light = false)
    {
        drawable = new SpectrumDrawable(SampleLevels.Length, colors, SpectrumPreviewSettings(settings, mode), light);
        drawable.Advance(SampleLevels, .1f, out _);
        var view = new GraphicsView { Drawable = drawable };
        MixerStyle.UseColor(view, BackgroundColorProperty, MixerColor.Overlay);
        return view;
    }

    private static PlayerSettings SpectrumPreviewSettings(PlayerSettings settings, SpectrumColorMode mode) => settings with
    {
        SpectrumColors = mode,
        ShowSpectrum = true,
        ShowPeaks = false,
        SpectrumWidthPercent = 42,
        SpectrumTopPaddingRows = 0,
        SpectrumBottomPaddingRows = 0
    };

    private View CreateTimelinePreview(int option)
    {
        var theme = MixerTheme.Create(_settings, MixerTheme.SystemTheme);
        var drawable = new WaveformDrawable
        {
            Theme = theme,
            Colors = theme.Visualization,
            UseProgressBar = option == 0,
            Mode = option == 2 ? DeckWaveformMode.BeatGrid : DeckWaveformMode.Color,
            Waveform = TimelineSample,
            DetailWindowSeconds = _settings.WaveformDetailSeconds,
            Position = TimelineSample.TotalFrames * 3 / 5
        };
        var view = new Grid
        {
            Children = { new GraphicsView { Drawable = drawable }, new GraphicsView { Drawable = drawable.CreateCursor() } }
        };
        MixerStyle.UseColor(view, BackgroundColorProperty, MixerColor.Overlay);
        return view;
    }

    private View CreateDetailedTimelinePreview(DeckWaveformMode mode)
    {
        var theme = MixerTheme.Create(_settings, MixerTheme.SystemTheme);
        var drawable = new WaveformDrawable
        {
            Theme = theme,
            Colors = theme.Visualization,
            Mode = mode,
            Detailed = true,
            DetailWindowSeconds = 12,
            Waveform = DetailedTimelineSample,
            Position = DetailedTimelineSample.TotalFrames / 2
        };
        var view = new Grid
        {
            Children = { new GraphicsView { Drawable = drawable }, new GraphicsView { Drawable = drawable.CreateCursor() } }
        };
        MixerStyle.UseColor(view, BackgroundColorProperty, MixerColor.Overlay);
        return view;
    }
}
