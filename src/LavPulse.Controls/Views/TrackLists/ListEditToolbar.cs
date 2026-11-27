namespace Laveyana.LavPulse.Controls;

/// <summary>Direct selection operations; independent of the persisted swipe enum.</summary>
public enum ListEditAction
{
    PlayNow, PlayNext, AddToQueue, RemoveFromQueue, ClearAnalysis, Rescan,
    RemoveFromLibrary, RemoveFromStorage, Reveal
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
        Add(ListEditAction.PlayNow, "Воспроизвести сейчас", "player_play.png");
        Add(ListEditAction.PlayNext, "Воспроизвести следующими", "player_next.png");
        if (library) Add(ListEditAction.AddToQueue, "Добавить в очередь", "player_queue_add.png");
        Add(ListEditAction.RemoveFromQueue, "Убрать из очереди", "player_queue.png");
        Add(ListEditAction.ClearAnalysis, "Очистить данные анализа", "player_reset.png");
        Add(ListEditAction.Rescan, "Очистить и пересканировать", "player_automix.png");
        Add(ListEditAction.RemoveFromLibrary, "Убрать из медиатеки", "player_folder.png");
        if (library) Add(ListEditAction.RemoveFromStorage, "Удалить копию из хранилища", "player_delete.png");
#if WINDOWS
        Add(ListEditAction.Reveal, "Показать в проводнике", "player_file.png");
#endif

        void Add(ListEditAction action, string label, string icon)
        {
            var button = MixerStyle.IconButton(icon, 40);
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
