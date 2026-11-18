using System.Globalization;

namespace Laveyana.Audio.Maui.Controls;

internal readonly record struct NumericRange(double Minimum, double Maximum, double DefaultValue, double WheelStep, int DecimalPlaces = 0);

/// <summary>Edits a bounded number using the keyboard, mouse wheel and an optional slider.</summary>
internal sealed class NumericSettingInput : Grid
{
    private readonly Entry _entry;
    private readonly Slider? _slider;
    private readonly NumericRange _range;
    private double _value;
    private double _wheelRemainder;
    private bool _updating;

    public NumericSettingInput(string name, string unit, NumericRange range, double value, bool showSlider = true, bool vertical = false)
    {
        _range = range;
        ColumnDefinitions = showSlider
            ? [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)]
            : [new(GridLength.Auto), new(GridLength.Auto)];
        ColumnSpacing = showSlider ? 6 : 3;
        _entry = new Entry
        {
            Keyboard = Keyboard.Numeric, ReturnType = ReturnType.Done, HorizontalTextAlignment = TextAlignment.End,
            FontFamily = "Inter", FontSize = vertical ? 12 : showSlider ? 14 : 12,
            WidthRequest = vertical ? 48 : showSlider ? 76 : 66, TextColor = MixerStyle.TextColor,
            BackgroundColor = MixerStyle.Overlay
        };
        var unitLabel = MixerStyle.Label(unit, 12, MixerStyle.Muted);
        unitLabel.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetDescription(_entry, $"{name}, {unit}, от {range.Minimum} до {range.Maximum}");
        int fieldColumn = showSlider ? 1 : 0;
        if (vertical)
        {
            ColumnDefinitions = [new(GridLength.Star)];
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)];
            _entry.HorizontalOptions = LayoutOptions.Center;
            _entry.HorizontalTextAlignment = TextAlignment.Center;
            this.Add(_entry, row: 1);
        }
        else
        {
            this.Add(_entry, fieldColumn);
            this.Add(unitLabel, fieldColumn + 1);
        }
        NumericInputGestures.Attach(_entry, Scroll);
        if (showSlider)
        {
            _slider = new Slider
            {
                Maximum = range.Maximum, Minimum = range.Minimum, MinimumTrackColor = MixerStyle.Accent,
                MaximumTrackColor = MixerStyle.BorderColor, ThumbColor = MixerStyle.Accent, VerticalOptions = LayoutOptions.Center
            };
            SemanticProperties.SetDescription(_slider, $"{name}, {unit}. Двойное нажатие сбрасывает значение.");
            var reset = MixerStyle.IconButton("player_reset.png", 32);
            SemanticProperties.SetDescription(reset, $"Сбросить. {name}");
            reset.Clicked += (_, _) => Reset();
            if (vertical)
            {
                const int travel = 116;
                var slot = new AbsoluteLayout { HeightRequest = travel };
                _slider.Rotation = -90;
                AbsoluteLayout.SetLayoutBounds(_slider, new Rect(.5, .5, travel, 40));
                AbsoluteLayout.SetLayoutFlags(_slider, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.PositionProportional);
                slot.Children.Add(_slider);
                reset.HorizontalOptions = LayoutOptions.Center;
                this.Add(slot);
                this.Add(reset, row: 2);
            }
            else
            {
                this.Add(_slider);
                this.Add(reset, 3);
            }
            NumericInputGestures.Attach(_slider, Scroll, Reset, vertical);
            _slider.ValueChanged += (_, args) =>
            {
                if (!_updating && IsEnabled)
                {
                    SetFromUser(args.NewValue, moveSlider: false);
                }
            };
        }
        Value = value;
        _entry.Completed += (_, _) => CommitText();
        _entry.Unfocused += (_, _) => CommitText();
    }

    /// <summary>Updates the displayed value without requesting a settings or playback change.</summary>
    public double Value
    {
        get => _value;
        set
        {
            _wheelRemainder = 0;
            SetValue(value, moveSlider: true);
        }
    }

    public double DefaultValue => _range.DefaultValue;
    public event Action<double>? ValueChanged;

    /// <summary>Accepts either decimal separator and keeps the last valid value for incomplete input.</summary>
    public void CommitText()
    {
        if (IsEnabled)
        {
            SetFromUser(ReadText(), moveSlider: true);
        }
    }

    private double ReadText()
    {
        string? text = _entry.Text?.Trim().Replace(',', '.');
        bool valid = double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out double value) && double.IsFinite(value);
        return valid ? value : _value;
    }

    /// <summary>Accumulates high-resolution wheel input and applies only whole steps.</summary>
    private bool Scroll(double turns)
    {
        if (!IsEnabled || !double.IsFinite(turns) || turns == 0)
        {
            return false;
        }

        _wheelRemainder += turns;
        double steps = Math.Truncate(_wheelRemainder);
        if (steps != 0)
        {
            _wheelRemainder -= steps;
            double current = Math.Clamp(ReadText(), _range.Minimum, _range.Maximum);
            SetFromUser(current + steps * _range.WheelStep, moveSlider: true);
        }
        return true;
    }

    private void Reset()
    {
        if (IsEnabled)
        {
            _wheelRemainder = 0;
            SetFromUser(_range.DefaultValue, moveSlider: true);
        }
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
        _value = Math.Round(Math.Clamp(value, _range.Minimum, _range.Maximum), _range.DecimalPlaces, MidpointRounding.AwayFromZero);
        if (moveSlider && _slider is { } slider)
        {
            _updating = true;
            slider.Value = _value;
            _updating = false;
        }
        _entry.Text = _value.ToString(CultureInfo.CurrentCulture);
    }
}
