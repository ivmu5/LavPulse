using System.Globalization;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

/// <summary>Temporary annotation modes, independent from the audio analyzer and beat tracker.</summary>
public enum TrackMarkMode { Problem, StrongRhythm }

/// <summary>Hand-selected range, with uncertainty metadata rather than invented beat positions.</summary>
public readonly record struct TrackReportSelection(
    long StartFrame, long EndFrame, string Comment, TrackMarkMode Mode,
    int TimingToleranceMilliseconds, int PossibleMissingBeats, double? SuggestedBpm);

/// <summary>Marks a range directly on the overview and allows fine tuning with start/end sliders.</summary>
public sealed class TrackReportMarkPanel : ContentView
{
    private readonly long _total;
    private readonly Slider _start = new();
    private readonly Slider _end = new();
    private readonly GraphicsView _selectionView;
    private readonly SelectionDrawable _selection = new();
    private readonly Label _interval = MixerStyle.Label("", 12, MixerColor.Accent);
    private readonly Entry _bpm = new() { Keyboard = Keyboard.Numeric, Placeholder = "150" };
    private readonly Editor _comment = new()
    {
        AutoSize = EditorAutoSizeOption.TextChanges,
        HeightRequest = 90,
        Placeholder = "Комментарий"
    };
    private bool _setting;
    private bool _nextTapSetsEnd;

    public TrackReportMarkPanel(CachedTrack track, long position, TrackMarkMode mode,
        Func<TrackReportSelection, Task<bool>> save, Action dismiss)
    {
        _total = Math.Max(1, track.Waveform.TotalFrames);
        double totalSeconds = _total / (double)PlaybackFormat.SampleRate;
        _start.Minimum = _end.Minimum = 0;
        _start.Maximum = _end.Maximum = totalSeconds;
        _start.Value = Math.Clamp(position / (double)PlaybackFormat.SampleRate - 4, 0, totalSeconds);
        _end.Value = Math.Clamp(position / (double)PlaybackFormat.SampleRate + 8, _start.Value, totalSeconds);
        MixerStyle.StyleSlider(_start);
        MixerStyle.StyleSlider(_end);
        MixerStyle.StyleEntry(_bpm);
        MixerStyle.UseColor(_comment, Editor.TextColorProperty, MixerColor.Text);
        MixerStyle.UseColor(_comment, VisualElement.BackgroundColorProperty, MixerColor.Overlay);
        _comment.Placeholder = mode == TrackMarkMode.StrongRhythm
            ? "Особенности ритма (необязательно)"
            : "Описание проблемы: BPM, сетка долей, лишние удары";
        _selection.From = (float)(_start.Value / totalSeconds);
        _selection.To = (float)(_end.Value / totalSeconds);
        var drawable = new WaveformDrawable
        {
            Waveform = track.Waveform,
            Detailed = false,
            UseProgressBar = false,
            Mode = DeckWaveformMode.Color
        };
        var waveform = new GraphicsView { Drawable = drawable, HeightRequest = 90, InputTransparent = true };
        _selectionView = new GraphicsView { Drawable = _selection, HeightRequest = 90, BackgroundColor = Colors.Transparent };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, args) =>
        {
            var point = args.GetPosition(_selectionView);
            if (point is null || _selectionView.Width <= 0) return;
            double seconds = Math.Clamp(point.Value.X / _selectionView.Width, 0, 1) * totalSeconds;
            if (!_nextTapSetsEnd)
            {
                _start.Value = seconds;
                if (_end.Value < seconds) _end.Value = seconds;
                _nextTapSetsEnd = true;
            }
            else
            {
                if (seconds < _start.Value) _start.Value = seconds;
                _end.Value = seconds;
                _nextTapSetsEnd = false;
            }
            RefreshRange();
        };
        _selectionView.GestureRecognizers.Add(tap);
        var waveformLayer = new Grid { HeightRequest = 90, IsClippedToBounds = true };
        waveformLayer.Add(waveform);
        waveformLayer.Add(_selectionView);
        MixerStyle.UseColor(waveformLayer, VisualElement.BackgroundColorProperty, MixerColor.Overlay);
        _start.ValueChanged += (_, _) => RefreshRange();
        _end.ValueChanged += (_, _) => RefreshRange();
        RefreshRange();

        var main = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                MixerStyle.Label(mode == TrackMarkMode.StrongRhythm ? "Сильный ритм" : "Замечание по треку",
                    MixerStyle.PhoneFont(21, 19), MixerColor.Accent, bold: true),
                MixerStyle.Label(track.Title + (track.Artist is { } artist ? " — " + artist : ""), 13),
                MixerStyle.Label("Два касания волны задают начало и конец участка. " +
                    "Ползунки уточняют границы.", 11, MixerColor.Muted),
                waveformLayer,
                _interval,
                MixerStyle.Label("Начало", 12),
                _start,
                MixerStyle.Label("Конец", 12),
                _end
            }
        };
        if (mode == TrackMarkMode.StrongRhythm)
        {
            main.Add(MixerStyle.Label("Ручные участки сохраняются в отчёте трека.", 11, MixerColor.Muted));
            main.Add(MixerStyle.Label("Предполагаемый BPM (необязательно)", 12));
            main.Add(_bpm);
            main.Add(MixerStyle.Label("Допуск границ: ±250 мс. Допустимый пропуск при смене темпа: до 3 ударов. " +
                "Ручные отметки не изменяют автоматическую сетку долей.", 11, MixerColor.Muted));
        }
        main.Add(MixerStyle.Label(mode == TrackMarkMode.Problem ? "Комментарий" : "Наблюдение", 12));
        main.Add(_comment);
        var saveButton = MixerStyle.Button("Добавить в отчёт", primary: true);
        var cancel = MixerStyle.IconButton("player_cancel.png", 44);
            SemanticProperties.SetDescription(cancel, "Отмена");
            ToolTipProperties.SetText(cancel, "Отмена");
        cancel.Clicked += (_, _) => dismiss();
        saveButton.Clicked += async (_, _) =>
        {
            if (saveButton.IsEnabled == false) return;
            double? bpm = null;
            if (mode == TrackMarkMode.StrongRhythm && !string.IsNullOrWhiteSpace(_bpm.Text))
            {
                if (!double.TryParse(_bpm.Text.Trim().Replace(',', '.'),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
                    !double.IsFinite(parsed) || parsed is < 40 or > 300)
                {
                    await ShowErrorAsync("Допустимый BPM: 40–300. Поле необязательное.");
                    return;
                }
                bpm = parsed;
            }
            var comment = (_comment.Text ?? "").Trim();
            if (mode == TrackMarkMode.Problem && comment.Length == 0)
            {
                await ShowErrorAsync("Комментарий обязателен.");
                return;
            }
            if (comment.Length > 3000)
            {
                await ShowErrorAsync("Максимальная длина комментария — 3000 символов.");
                return;
            }
            saveButton.IsEnabled = false;
            try
            {
                var mark = new TrackReportSelection(
                    (long)Math.Round(_start.Value * PlaybackFormat.SampleRate),
                    (long)Math.Round(_end.Value * PlaybackFormat.SampleRate),
                    comment, mode, mode == TrackMarkMode.StrongRhythm ? 250 : 0,
                    mode == TrackMarkMode.StrongRhythm ? 3 : 0, bpm);
                if (await save(mark)) dismiss();
            }
            finally { saveButton.IsEnabled = true; }
        };
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        actions.Add(saveButton);
        actions.Add(cancel, 1);
        var container = new Grid
        {
            Padding = new Thickness(16, 18),
            MaximumWidthRequest = 680,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)]
        };
        container.Add(new ScrollView { Content = main });
        container.Add(actions, row: 1);
        Content = container;
    }

    private static async Task ShowErrorAsync(string message)
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Page is Page page)
            await page.DisplayAlertAsync("Отчёт", message, "OK");
    }

    private void RefreshRange()
    {
        if (_setting) return;
        _setting = true;
        try
        {
            // Two sliders must always describe a valid nonnegative span.
            if (_start.Value > _end.Value) _end.Value = _start.Value;
            double duration = _total / (double)PlaybackFormat.SampleRate;
            _selection.From = (float)(_start.Value / duration);
            _selection.To = (float)(_end.Value / duration);
            _interval.Text = $"{Time(_start.Value)} — {Time(_end.Value)}";
            _selectionView.Invalidate();
        }
        finally { _setting = false; }
    }

    private static string Time(double seconds)
    {
        long ms = (long)Math.Round(seconds * 1000);
        return $"{ms / 60000:D2}:{ms / 1000 % 60:D2}.{ms % 1000:D3}";
    }

    private sealed class SelectionDrawable : IDrawable
    {
        public float From { get; set; }
        public float To { get; set; }
        public void Draw(ICanvas canvas, RectF bounds)
        {
            float left = bounds.Left + bounds.Width * From;
            float right = bounds.Left + bounds.Width * To;
            canvas.FillColor = Colors.Orange.WithAlpha(.22f);
            canvas.FillRectangle(left, bounds.Top, Math.Max(2, right - left), bounds.Height);
            canvas.StrokeColor = Colors.Orange;
            canvas.StrokeSize = 2;
            canvas.DrawLine(left, bounds.Top, left, bounds.Bottom);
            canvas.DrawLine(right, bounds.Top, right, bounds.Bottom);
        }
    }
}
