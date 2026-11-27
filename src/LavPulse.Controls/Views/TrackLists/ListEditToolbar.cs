namespace Laveyana.LavPulse.Controls;

/// <summary>Direct selection operations; independent of the persisted swipe enum.</summary>
public enum ListEditAction
{
    PlayNow, PlayNext, AddToQueue, RemoveFromQueue, ClearAnalysis, Rescan,
    RemoveFromLibrary, RemoveFromStorage, Reveal
}

public static class ListEditActionPresentation
{
    public static string Icon(ListEditAction action) => action switch
    {
        ListEditAction.PlayNow => "player_play.png",
        ListEditAction.PlayNext => "player_next.png",
        ListEditAction.AddToQueue => "player_queue_add.png",
        ListEditAction.RemoveFromQueue => "player_queue.png",
        ListEditAction.ClearAnalysis => "player_reset.png",
        ListEditAction.Rescan => "player_automix.png",
        ListEditAction.RemoveFromLibrary => "player_folder.png",
        ListEditAction.RemoveFromStorage => "player_delete.png",
        ListEditAction.Reveal => "player_file.png",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}

internal sealed class ListEditToolbar : Grid
{
    private readonly ImageButton _cancel = MixerStyle.IconButton("player_cancel.png", 44);
    private readonly Label _count = MixerStyle.Label("", 11, MixerColor.Muted);
    private readonly Dictionary<ListEditAction, ImageButton> _buttons = new();

    internal ListEditToolbar(bool library, Action cancel, Func<ListEditAction, Task> execute)
    {
        ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)];
        ColumnSpacing = 6;
        VerticalOptions = LayoutOptions.Start;
        IsVisible = false;
        SemanticProperties.SetDescription(_cancel, "Отменить редактирование");
        ToolTipProperties.SetText(_cancel, "Отменить редактирование");
        _cancel.Clicked += (_, _) => cancel();
        _count.HorizontalTextAlignment = TextAlignment.Center;
        var left = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Start,
            Children = { _cancel, _count } };
        this.Add(left);
        var right = new FlexLayout
        {
            VerticalOptions = LayoutOptions.Start,
            Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.End,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
            AlignContent = Microsoft.Maui.Layouts.FlexAlignContent.Start
        };
        this.Add(right, 1);
        Add(ListEditAction.PlayNow, "Воспроизвести сейчас");
        Add(ListEditAction.PlayNext, "Воспроизвести следующими");
        if (library) Add(ListEditAction.AddToQueue, "Добавить в очередь");
        Add(ListEditAction.RemoveFromQueue, "Убрать из очереди");
        Add(ListEditAction.ClearAnalysis, "Очистить данные анализа");
        Add(ListEditAction.Rescan, "Очистить и пересканировать");
        Add(ListEditAction.RemoveFromLibrary, "Убрать из медиатеки");
        if (library) Add(ListEditAction.RemoveFromStorage, "Удалить копию из хранилища");
#if WINDOWS
        Add(ListEditAction.Reveal, "Показать в проводнике");
#endif

        void Add(ListEditAction action, string label)
        {
            var button = MixerStyle.IconButton(ListEditActionPresentation.Icon(action), 40);
            button.CornerRadius = 8;
            button.Margin = new Thickness(2);
            SemanticProperties.SetDescription(button, label);
            ToolTipProperties.SetText(button, label);
            button.Clicked += async (_, _) =>
            {
                try { await execute(action); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            };
            _buttons.Add(action, button);
            right.Children.Add(button);
        }
    }

    internal void Update(bool editing, bool busy, int count, bool singleTrack, bool canReveal, bool actionsAvailable)
    {
        IsVisible = editing;
        _cancel.IsEnabled = !busy;
        _count.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SemanticProperties.SetDescription(_count, $"Выбрано: {count}");
        foreach (var (action, button) in _buttons)
            button.IsEnabled = actionsAvailable && !busy && count > 0 && (action switch
            {
                ListEditAction.PlayNow => singleTrack,
                ListEditAction.Reveal => canReveal,
                _ => true
            });
    }
}
