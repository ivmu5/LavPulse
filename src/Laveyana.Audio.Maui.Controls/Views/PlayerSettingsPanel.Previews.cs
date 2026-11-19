using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class PlayerSettingsPanel
{
    private const int PreviewHeight = 36;
    private const int FramesPerSample = 8 * PlaybackFormat.SampleRate;
    private static readonly float[] SampleLevels = [.82f, .65f, .45f, .62f, .36f, .53f, .78f, .57f, .42f, .69f];
    private static readonly TrackWaveform TimelineSample = new(
        SampleLevels.Select(level => new WaveformPoint(level, level * .85f, level * .53f, level * .27f)).ToArray(),
        (long)SampleLevels.Length * FramesPerSample, FramesPerSample);
    private static readonly ControlTemplate PreviewChoiceTemplate = new(CreateChoiceBorder);

    // Each row is one radio group. Star columns keep every preview visible together on phones and desktops.
    private static View Select<T>(string name, (T Value, string Label)[] options, T selected, Func<T, View> preview, Action<T> update)
    {
        var row = new Grid { ColumnSpacing = 6 };
        foreach (var (value, label) in options)
        {
            var caption = MixerStyle.Label(label, MixerStyle.PhoneFont(12, 11));
            caption.HorizontalTextAlignment = TextAlignment.Center;
            caption.VerticalTextAlignment = TextAlignment.Center;
            caption.LineBreakMode = LineBreakMode.WordWrap;
            var content = new Grid
            {
                RowDefinitions = [new(new GridLength(PreviewHeight)), new(GridLength.Star)],
                RowSpacing = 5
            };
            content.Add(preview(value));
            content.Add(caption, row: 1);

            var choice = new RadioButton
            {
                Content = content, ControlTemplate = PreviewChoiceTemplate, Padding = 0,
                HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill
            };
            SemanticProperties.SetDescription(choice, $"{name}. {label}");
            int column = row.ColumnDefinitions.Count;
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.Add(choice, column);
            choice.IsChecked = EqualityComparer<T>.Default.Equals(selected, value);
            choice.CheckedChanged += (_, args) =>
            {
                if (args.Value) update(value);
            };
        }
        return new VerticalStackLayout { Spacing = 6, Children = { MixerStyle.Label(name, 13), row } };
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
                InputTransparent = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill
            }
        };
        // The native presenter can measure the template at its desired width. Match the assigned column instead.
        border.SetBinding(WidthRequestProperty, new Binding(nameof(RadioButton.Width), source: RelativeBindingSource.TemplatedParent));
        var states = new VisualStateGroup { Name = "CheckedStates" };
        states.States.Add(new VisualState
        {
            Name = "Unchecked",
            Setters =
            {
                new Setter { Property = Border.BackgroundColorProperty, Value = MixerStyle.Surface },
                new Setter { Property = Border.StrokeProperty, Value = new SolidColorBrush(MixerStyle.BorderColor) }
            }
        });
        states.States.Add(new VisualState
        {
            Name = "Checked",
            Setters =
            {
                new Setter { Property = Border.BackgroundColorProperty, Value = MixerStyle.SelectedBackground },
                new Setter { Property = Border.StrokeProperty, Value = new SolidColorBrush(MixerStyle.Accent) }
            }
        });
        VisualStateManager.SetVisualStateGroups(border, new VisualStateGroupList { states });
        return border;
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
        SpectrumColorMode mode, out SpectrumDrawable drawable)
    {
        drawable = new SpectrumDrawable(SampleLevels.Length, colors, SpectrumPreviewSettings(settings, mode));
        drawable.Advance(SampleLevels, .1f);
        return new GraphicsView { Drawable = drawable, BackgroundColor = MixerStyle.Overlay };
    }

    private static PlayerSettings SpectrumPreviewSettings(PlayerSettings settings, SpectrumColorMode mode) => settings with
    {
        SpectrumColors = mode, ShowSpectrum = true, ShowPeaks = false, SpectrumWidthPercent = 42,
        SpectrumTopPaddingRows = 0, SpectrumBottomPaddingRows = 0
    };

    private View CreateTimelinePreview(PlayerColors colors, bool waveform)
    {
        var drawable = new WaveformDrawable
        {
            Colors = colors,
            UseProgressBar = !waveform,
            Waveform = TimelineSample,
            DetailWindowSeconds = _settings.WaveformDetailSeconds,
            Position = TimelineSample.TotalFrames * 3 / 5
        };
        return new Grid
        {
            BackgroundColor = MixerStyle.Overlay,
            Children = { new GraphicsView { Drawable = drawable }, new GraphicsView { Drawable = drawable.CreateCursor() } }
        };
    }
}
