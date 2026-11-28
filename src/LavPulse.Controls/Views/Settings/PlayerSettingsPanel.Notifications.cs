#if WINDOWS
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.LavPulse.Controls;

public sealed partial class PlayerSettingsPanel
{
    private View CreateNotificationSection()
    {
        var position = Select("Расположение уведомлений",
            new (NotificationCorner Value, string Label)[]
            {
                (NotificationCorner.TopLeft, "Слева сверху"),
                (NotificationCorner.TopRight, "Справа сверху"),
                (NotificationCorner.BottomLeft, "Слева снизу"),
                (NotificationCorner.BottomRight, "Справа снизу")
            },
            _settings.NotificationPosition, NotificationPreview, value =>
            {
                _settings = _settings with { NotificationPosition = value };
                _previewAppearance?.Invoke(_settings);
            }, maxColumns: 2);
        var hint = MixerStyle.Label("Стрелка сворачивает детали, боковая стрелка — всю карточку в значок. Фоновая операция продолжает работать; кольцо и процент показывают прогресс. Нажмите значок, чтобы развернуть карточку.", 12, MixerColor.Muted);
        (int Value,string Label)[] intervals = [(3,"3 с"),(5,"5 с"),(8,"8 с"),(12,"12 с"),(20,"20 с"),(30,"30 с"),(60,"60 с")];
        var pinned = Select("Сворачивать фоновые уведомления через", intervals,
            _settings.PinnedNotificationSeconds, null,
            value => _settings = _settings with { PinnedNotificationSeconds = value }, maxColumns: 4);
        var transient = Select("Закрывать обычные уведомления через", intervals,
            _settings.TransientNotificationSeconds, null,
            value => _settings = _settings with { TransientNotificationSeconds = value }, maxColumns: 4);
        return Section("Уведомления Windows", position, pinned, transient, hint);
    }

    private static View NotificationPreview(NotificationCorner position)
    {
        bool right = position is NotificationCorner.TopRight or NotificationCorner.BottomRight;
        bool bottom = position is NotificationCorner.BottomLeft or NotificationCorner.BottomRight;
        var sample = new Grid { Padding = 5, HeightRequest = 48 };
        MixerStyle.UseColor(sample, BackgroundColorProperty, MixerColor.Surface);
        var card = new Border
        {
            WidthRequest = 34, HeightRequest = 17,
            HorizontalOptions = right ? LayoutOptions.End : LayoutOptions.Start,
            VerticalOptions = bottom ? LayoutOptions.End : LayoutOptions.Start,
            StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 4 },
            InputTransparent = true
        };
        MixerStyle.UseBrush(card, Border.StrokeProperty, MixerColor.Accent);
        MixerStyle.UseColor(card, BackgroundColorProperty, MixerColor.Card);
        sample.Add(card);
        return sample;
    }
}
#endif
