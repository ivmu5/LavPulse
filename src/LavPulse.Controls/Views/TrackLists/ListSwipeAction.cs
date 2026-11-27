namespace Laveyana.LavPulse.Controls;

public enum ListSwipeAction { None, AddToQueue, PlayNext, RemoveFromQueue, RemoveFromStorage, ClearAnalysis, Rescan, Menu }

internal static class ListActionText
{
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
