using System.Globalization;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Pairs a numeric text field with a slider and keeps their values in sync.</summary>
internal sealed class NumericSettingInput : Grid
{
    private readonly Entry _entry;
    private readonly Slider _slider;
    private readonly int _decimalPlaces;
    private double _value;
    private bool _updating;

    public NumericSettingInput(string name, string unit, double minimum, double maximum, double value, int decimalPlaces = 0)
    {
        _decimalPlaces = decimalPlaces;
        ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)];
        ColumnSpacing = 8;

        _slider = new Slider
        {
            Maximum = maximum, Minimum = minimum, MinimumTrackColor = MixerStyle.Accent,
            MaximumTrackColor = MixerStyle.BorderColor, ThumbColor = MixerStyle.Accent,
            VerticalOptions = LayoutOptions.Center
        };
        _entry = new Entry
        {
            Keyboard = Keyboard.Numeric, ReturnType = ReturnType.Done, HorizontalTextAlignment = TextAlignment.End,
            FontFamily = "Inter", FontSize = 14, WidthRequest = 76, TextColor = MixerStyle.TextColor,
            BackgroundColor = MixerStyle.Overlay
        };
        var unitLabel = MixerStyle.Label(unit, 12, MixerStyle.Muted);
        unitLabel.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetDescription(_slider, $"{name}, {unit}");
        SemanticProperties.SetDescription(_entry, $"{name}, {unit}, от {minimum} до {maximum}");
        this.Add(_slider);
        this.Add(_entry, 1);
        this.Add(unitLabel, 2);

        Value = value;
        _slider.ValueChanged += (_, args) =>
        {
            if (!_updating && IsEnabled)
            {
                SetFromUser(args.NewValue, moveSlider: false);
            }
        };
        _entry.Completed += (_, _) => CommitText();
        _entry.Unfocused += (_, _) => CommitText();
    }

    /// <summary>Updates the displayed value without requesting a settings or playback change.</summary>
    public double Value
    {
        get => _value;
        set => SetValue(value, moveSlider: true);
    }

    public event Action<double>? ValueChanged;

    /// <summary>Accepts either decimal separator and keeps the last valid value for incomplete input.</summary>
    public void CommitText()
    {
        if (!IsEnabled)
        {
            return;
        }

        string? text = _entry.Text?.Trim().Replace(',', '.');
        bool valid = double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out double value) && double.IsFinite(value);
        SetFromUser(valid ? value : _value, moveSlider: true);
    }

    private void SetFromUser(double value, bool moveSlider)
    {
        double previous = _value;
        SetValue(value, moveSlider);
        if (_value != previous)
        {
            ValueChanged?.Invoke(_value);
        }
    }

    private void SetValue(double value, bool moveSlider)
    {
        _value = Math.Round(Math.Clamp(value, _slider.Minimum, _slider.Maximum), _decimalPlaces, MidpointRounding.AwayFromZero);
        if (moveSlider)
        {
            _updating = true;
            _slider.Value = _value;
            _updating = false;
        }
        _entry.Text = _value.ToString(CultureInfo.CurrentCulture);
    }
}
