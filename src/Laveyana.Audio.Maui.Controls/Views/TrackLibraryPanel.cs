using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

public sealed record TrackLibrarySelection(IReadOnlyList<TrackFolderSource> Folders, TrackFile? File,
    bool IncludeChildren, bool Local);

/// <summary>Browses saved imports or a selected source without opening collapsed directories.</summary>
public sealed class TrackLibraryPanel : ContentView
{
    private readonly Func<Task<TrackFolderSource?>> _pickFolder;
    private readonly Func<Task<TrackFolderSource>> _openLibrary;
    private readonly Func<TrackLibrarySelection, Task<int>> _add;
    private readonly CancellationToken _lifetime;
    private readonly CollectionView _tree = new()
    {
        SelectionMode = SelectionMode.None,
        ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
        ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset
    };
    private readonly Label _message = MixerStyle.Label("", 12, MixerColor.Muted);
    private readonly Label _heading = MixerStyle.Label("Медиатека", MixerStyle.PhoneFont(23, 20), bold: true);
    private readonly Button _pick = MixerStyle.Button("Открыть папку", icon: "player_folder.png");
    private readonly Button _saved = MixerStyle.Button("Медиатека");
    private readonly Button _addButton = MixerStyle.Button("Добавить папку в очередь", primary: true);
    private readonly CheckBox _nested = new() { VerticalOptions = LayoutOptions.Center };
    private readonly View _nestedOption;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackFolder> _listings = new(StringComparer.Ordinal);
    private TrackFolderSource? _root;
    private TreeRow? _selected;
    private bool _local;
    private bool _busy;

    public TrackLibraryPanel(Func<Task<TrackFolderSource?>> pickFolder, Func<Task<TrackFolderSource>> openLibrary,
        Func<TrackLibrarySelection, Task<int>> add, Action close, CancellationToken lifetime)
    {
        _pickFolder = pickFolder;
        _openLibrary = openLibrary;
        _add = add;
        _lifetime = lifetime;
        var back = MixerStyle.Button("Назад");
        back.Clicked += (_, _) => close();
        _pick.Clicked += async (_, _) => await PickAsync();
        _saved.Clicked += async (_, _) => await RunAsync(OpenLibraryAsync);
        _addButton.Clicked += async (_, _) => await RunAsync(AddAsync);
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        _heading.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetHeadingLevel(_heading, SemanticHeadingLevel.Level1);
        header.Add(_heading);
        header.Add(back, 1);
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 8 };
        actions.Add(_pick);
        actions.Add(_saved, 1);

        MixerStyle.UseColor(_nested, CheckBox.ColorProperty, MixerColor.Accent);
        var nestedLabel = MixerStyle.Label("Включая вложенные папки", 13);
        nestedLabel.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetDescription(_nested, nestedLabel.Text);
        var nestedTap = new TapGestureRecognizer();
        nestedTap.Tapped += (_, _) => _nested.IsChecked = !_nested.IsChecked;
        nestedLabel.GestureRecognizers.Add(nestedTap);
        _nestedOption = new HorizontalStackLayout { Children = { _nested, nestedLabel } };
        var footer = new VerticalStackLayout { Spacing = 4, Children = { _message, _nestedOption, _addButton } };
        _tree.ItemTemplate = new DataTemplate(CreateTreeItem);

        int top = 16;
#if WINDOWS
        top += WindowsWindowChrome.ContentInset;
#endif
        var layout = new Grid
        {
            Padding = new Thickness(16, top, 16, 16), RowSpacing = 8,
            HorizontalOptions = LayoutOptions.Center, MaximumWidthRequest = 660,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        layout.Add(header);
        layout.Add(actions, row: 1);
        layout.Add(_tree, row: 2);
        layout.Add(footer, row: 3);
        var host = new Grid { Children = { layout } };
        host.SizeChanged += (_, _) =>
        {
            if (host.Width > 0) layout.WidthRequest = Math.Min(host.Width, 660);
        };
        Content = host;
        UpdateActions();
    }

    public async Task InitializeAsync(bool pickSource)
    {
        if (pickSource) await PickAsync();
        if (_root is null) await RunAsync(OpenLibraryAsync);
    }

    private Task OpenLibraryAsync() => ShowRootAsync(_openLibrary(), local: true);

    private Task PickAsync() => RunAsync(async () =>
    {
        if (await _pickFolder() is { } root) await ShowRootAsync(Task.FromResult(root), local: false);
    });

    private async Task ShowRootAsync(Task<TrackFolderSource> source, bool local)
    {
        var root = await source;
        var listing = await root.ReadAsync(_lifetime);
        _root = root;
        _listings.Clear();
        _listings.Add(root.Id, listing);
        _expanded.Clear();
        _expanded.Add(root.Id);
        _local = local;
        _selected = new([root], null, listing.Children.Count > 0, Expanded: true);
        _nested.IsChecked = false;
        _heading.Text = local ? "Медиатека" : "Импорт папки";
        _message.Text = listing is { Files.Count: 0, Children.Count: 0 } ? "Здесь пока нет MP3." : "";
        UpdateTree();
    }

    private View CreateTreeItem()
    {
        var item = new Grid { ColumnDefinitions = [new(GridLength.Star)], HeightRequest = 46 };
        var button = MixerStyle.Button("");
        item.Add(button);
        button.HorizontalOptions = LayoutOptions.Start;
        button.LineBreakMode = LineBreakMode.TailTruncation;
        button.HeightRequest = 44;
        button.BindingContextChanged += (_, _) =>
        {
            if (button.BindingContext is not TreeRow row) return;
            button.Text = row.File?.Name ?? $"{(row.Expanded ? "▾" : "▸")}  {row.Folders[^1].Name}";
            item.Margin = new Thickness(Math.Min(8, row.Folders.Length - (row.File is null ? 1 : 0)) * 12, 0, 0, 0);
            MixerStyle.UseIcon(button, Button.ImageSourceProperty, row.File is null ? "player_folder.png" : "player_file.png");
            MixerStyle.UseColor(item, BackgroundColorProperty, row.Selected ? MixerColor.Selected : MixerColor.Surface);
            MixerStyle.UseColor(button, BackgroundColorProperty, row.Selected ? MixerColor.Selected : MixerColor.Surface);
        };
        button.Clicked += async (_, _) =>
        {
            if (button.BindingContext is TreeRow row) await RunAsync(() => SelectAsync(row));
        };
        return item;
    }

    private async Task SelectAsync(TreeRow row)
    {
        var folder = row.Folders[^1];
        if (row.File is null)
        {
            bool expanding = !_expanded.Contains(folder.Id);
            if (expanding && !_listings.ContainsKey(folder.Id))
                _listings.Add(folder.Id, await folder.ReadAsync(_lifetime));
            if (expanding) _expanded.Add(folder.Id);
            else _expanded.Remove(folder.Id);
        }
        _selected = row with { HasChildren = _listings.TryGetValue(folder.Id, out var listing) && listing.Children.Count > 0 };
        _message.Text = "";
        UpdateTree();
    }

    private void UpdateTree()
    {
        var rows = new List<TreeRow>();
        if (_root is { } root) AddFolder([root]);
        int selectedIndex = rows.FindIndex(row => row.Selected);
        _tree.ItemsSource = rows;
        if (selectedIndex > 0)
        {
            Dispatcher.Dispatch(() =>
            {
                if (ReferenceEquals(_tree.ItemsSource, rows))
                    _tree.ScrollTo(selectedIndex, position: ScrollToPosition.MakeVisible, animate: false);
            });
        }

        void AddFolder(TrackFolderSource[] path)
        {
            var folder = path[^1];
            var listing = _listings.GetValueOrDefault(folder.Id);
            bool expanded = listing is not null && _expanded.Contains(folder.Id);
            bool selected = _selected?.File is null && _selected?.Folders[^1].Id == folder.Id;
            rows.Add(new(path, null, listing?.Children.Count > 0, expanded, selected));
            if (!expanded || listing is null) return;
            foreach (var child in listing.Children)
            {
                if (!path.Any(parent => parent.Id == child.Id)) AddFolder([.. path, child]);
            }
            foreach (var file in listing.Files)
                rows.Add(new(path, file, false, false, ReferenceEquals(_selected?.File, file)));
        }
    }

    private async Task AddAsync()
    {
        if (_selected is not { } selected) return;
        _message.Text = _local ? "Добавляю в очередь…" : "Сохраняю MP3…";
        int count = await _add(new(selected.Folders, selected.File, selected.File is null && _nested.IsChecked, _local));
        if (count > 0 && !_local) await OpenLibraryAsync();
        _message.Text = count > 0 ? $"Добавлено треков: {count}" : "Нет новых треков для добавления.";
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        _busy = true;
        UpdateActions();
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _message.Text = $"Не удалось открыть папку. {ex.Message}"; }
        finally
        {
            _busy = false;
            UpdateActions();
        }
    }

    private void UpdateActions()
    {
        _pick.IsEnabled = _saved.IsEnabled = !_busy;
        _saved.IsVisible = !_local && _root is not null;
        _nestedOption.IsVisible = _selected is { File: null, HasChildren: true };
        _nestedOption.IsEnabled = !_busy;
        _addButton.IsEnabled = !_busy && _selected is not null;
        _addButton.Text = _selected?.File is null ? "Добавить папку в очередь" : "Добавить трек в очередь";
    }

    private sealed record TreeRow(TrackFolderSource[] Folders, TrackFile? File, bool HasChildren, bool Expanded,
        bool Selected = false);
}
