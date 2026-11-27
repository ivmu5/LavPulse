namespace Laveyana.LavPulse.Controls;

public enum ListSwipeAction { None, AddToQueue, PlayNext, RemoveFromQueue, RemoveFromStorage, ClearAnalysis, Rescan, Menu }

internal static class ListActionText
{
    // One mapping for the list gesture and settings previews.
    public static string Icon(ListSwipeAction action) => action switch
    {
        ListSwipeAction.AddToQueue => "player_queue_add.png",
        ListSwipeAction.PlayNext => "player_next.png",
        ListSwipeAction.RemoveFromQueue => "player_delete.png",
        ListSwipeAction.RemoveFromStorage => "player_delete.png",
        ListSwipeAction.ClearAnalysis => "player_reset.png",
        ListSwipeAction.Rescan => "player_automix.png",
        ListSwipeAction.Menu => "player_settings.png",
        _ => "player_cancel.png"
    };

    public static Color Tint(ListSwipeAction action, PlayerColors palette) => action switch
    {
        ListSwipeAction.RemoveFromQueue or ListSwipeAction.RemoveFromStorage or ListSwipeAction.ClearAnalysis => palette.Alternate,
        ListSwipeAction.Rescan => palette.WaveMid,
        ListSwipeAction.None => Colors.Transparent,
        _ => palette.Accent
    };

    public static string Label(ListSwipeAction action) => action switch
    {
        ListSwipeAction.AddToQueue => "В очередь",
        ListSwipeAction.PlayNext => "Следующим",
        ListSwipeAction.RemoveFromQueue => "Из очереди",
        ListSwipeAction.RemoveFromStorage => "Удалить",
        ListSwipeAction.ClearAnalysis => "Очистить анализ",
        ListSwipeAction.Rescan => "Переанализ",
        ListSwipeAction.Menu => "Действия",
        _ => "Нет действия"
    };
}
