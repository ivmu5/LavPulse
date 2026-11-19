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
    private View Select<T>(string name, (T Value, string Label)[] options, T selected, Func<T, View> preview, Action<T> update)
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

            var choice = new RadioButton { Content = content, ControlTemplate = PreviewChoiceTemplate };
            SemanticProperties.SetDescription(choice, $"{name}. {label}");
            int column = row.ColumnDefinitions.Count;
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.Add(choice, column);
            choice.IsChecked = EqualityComparer<T>.Default.Equals(selected, value);
            choice.CheckedChanged += (_, args) =>
            {
                if (args.Value && !_loading) update(value);
            };
        }
        return new VerticalStackLayout { Spacing = 6, Children = { MixerStyle.Label(name, 13), row } };
    }

    private static Border CreateChoiceBorder()
    {
        var border = new Border
        {
            Padding = 6,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = new ContentPresenter { InputTransparent = true }
        };
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

    private static View CreatePalettePreview(PlayerPalette palette)
    {
        var colors = PlayerColors.For(palette);
        var swatches = new Grid { ColumnSpacing = 2 };
        Color[] fills = [colors.Accent, colors.WaveLow, colors.WaveMid, colors.Alternate];
        for (int column = 0; column < fills.Length; column++)
        {
            swatches.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            swatches.Add(new BoxView { Color = fills[column], CornerRadius = 3 }, column);
        }
        return swatches;
    }

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
