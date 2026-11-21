using System.Collections.ObjectModel;
using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

public sealed record TrackLibrarySelection(IReadOnlyList<TrackFolderSource> Folders, TrackFile? File,
    bool IncludeChildren, bool Local);

/// <summary>Browses saved imports or a selected source without opening collapsed directories.</summary>
public sealed partial class TrackLibraryPanel : ContentView
{
    private const int RowHeight = 62;
    private readonly CollectionView _tree;
    private readonly BoxView _scrollSpacer = new() { HeightRequest = 0, InputTransparent = true };
    private readonly Func<Task<TrackFolderSource?>> _pickFolder;
    private readonly Func<Task<TrackFolderSource>> _openLibrary;
    private readonly Func<TrackLibrarySelection, bool, Task<int>> _import;
    private readonly Func<TrackFile, CancellationToken, Task<TrackMetadata>> _readMetadata;
    private readonly Func<TrackLibrarySelection, Action<int, int>, CancellationToken, Task<(int Scanned, int Failed)>> _scan;
    private readonly CancellationTokenSource _session;
    private readonly CancellationToken _token;
    private readonly ObservableCollection<TreeRow> _rows = [];
    private readonly Label _message = MixerStyle.Label("", 11, MixerColor.Muted);
    private readonly Label _heading = MixerStyle.Label("Медиатека", MixerStyle.PhoneFont(23, 20), bold: true);
    private readonly Button _pick = MixerStyle.Button("Папка", icon: "player_folder.png");
    private readonly Button _saved = MixerStyle.Button("К сохранённым");
    private readonly Button _scanButton = MixerStyle.Button("Пересканировать", icon: "player_reset.png");
    private readonly CheckBox _nested = new() { VerticalOptions = LayoutOptions.Center };
    private readonly View _nestedOption;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackFolder> _listings = new(StringComparer.Ordinal);
    private TreeRow? _selected;
    private bool _local;
    private bool _busy;
    private bool _closed;
    private double _scrollOffset;

    public TrackLibraryPanel(Func<Task<TrackFolderSource?>> pickFolder,
        Func<Task<TrackFolderSource>> openLibrary, Func<TrackLibrarySelection, bool, Task<int>> import,
        Func<TrackFile, CancellationToken, Task<TrackMetadata>> readMetadata,
        Func<TrackLibrarySelection, Action<int, int>, CancellationToken, Task<(int Scanned, int Failed)>> scan,
        Action close, CancellationToken lifetime)
    {
        _pickFolder = pickFolder;
        _openLibrary = openLibrary;
        _import = import;
        _readMetadata = readMetadata;
        _scan = scan;
        _session = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _token = _session.Token;

        var back = MixerStyle.Button("Назад");
        back.Clicked += (_, _) => close();
        _pick.Clicked += async (_, _) => await RunAsync(PickAsync, "Не удалось открыть папку");
        _saved.Clicked += async (_, _) => await InitializeAsync();
        _scanButton.Clicked += async (_, _) => await RunAsync(ScanAsync, "Не удалось пересканировать");
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        _heading.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetHeadingLevel(_heading, SemanticHeadingLevel.Level1);
        header.Add(_heading);
        header.Add(back, 1);
        var actions = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            ColumnSpacing = 8
        };
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
        _message.MaxLines = 2;
        _message.LineBreakMode = LineBreakMode.TailTruncation;
        _message.VerticalOptions = LayoutOptions.Center;
        var footer = new Grid
        {
            RowDefinitions = [new(32), new(40), new(GridLength.Auto)],
            ColumnDefinitions = [new(GridLength.Star)]
        };
        footer.Add(_message);
        footer.Add(_nestedOption, row: 1);
        footer.Add(_scanButton, row: 2);

        _tree = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
            ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
            ItemTemplate = new DataTemplate(() => new LibraryTreeItem(this)),
            Footer = _scrollSpacer,
            ItemsSource = _rows
        };
        _tree.Scrolled += (_, args) =>
        {
            double previous = _scrollOffset;
            _scrollOffset = Math.Max(0, args.VerticalOffset);
            if (!_busy && _scrollOffset < previous && _scrollSpacer.HeightRequest > 0)
                KeepScrollOffset(_rows.Count);
        };
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

    public Task InitializeAsync() => RunAsync(OpenLibraryAsync, "Не удалось открыть медиатеку");

    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        _session.Cancel();
        _session.Dispose();
    }

    private async Task OpenLibraryAsync() => await ShowRootAsync(await _openLibrary(), local: true);

    private async Task PickAsync()
    {
        if (await _pickFolder() is { } root) await ShowRootAsync(root, local: false);
    }

    private async Task ShowRootAsync(TrackFolderSource root, bool local)
    {
        var listing = await root.ReadAsync(_token);
        _token.ThrowIfCancellationRequested();
        _listings.Clear();
        _listings.Add(root.Id, listing);
        _expanded.Clear();
        _expanded.Add(root.Id);
        _scrollOffset = _scrollSpacer.HeightRequest = 0;
        _rows.Clear();
        _local = local;
        var row = new TreeRow([root], null) { Expanded = true, Selected = true };
        _selected = row;
        _rows.Add(row);
        InsertChildren(row, listing, 1);
        _nested.IsChecked = false;
        _heading.Text = local ? "Медиатека" : "Импорт папки";
        _message.Text = listing is { Files.Count: 0, Children.Count: 0 } ? "Здесь пока нет MP3." : "";
    }

    private async Task SelectAsync(TreeRow row)
    {
        if (row.File is null)
        {
            var folder = row.Folders[^1];
            int index = _rows.IndexOf(row);
            if (index < 0) return;
            if (row.Expanded)
            {
                int end = index + 1;
                while (end < _rows.Count && _rows[end].Depth > row.Depth) end++;
                KeepScrollOffset(_rows.Count - (end - index - 1));
                for (int i = end - 1; i > index; i--) _rows.RemoveAt(i);
                _expanded.Remove(folder.Id);
            }
            else
            {
                if (!_listings.TryGetValue(folder.Id, out var listing))
                    _listings.Add(folder.Id, listing = await folder.ReadAsync(_token));
                _token.ThrowIfCancellationRequested();
                _expanded.Add(folder.Id);
                InsertChildren(row, listing, index + 1);
                if (_scrollSpacer.HeightRequest > 0) KeepScrollOffset(_rows.Count);
            }
            row.Expanded = !row.Expanded;
        }
        if (_selected is { } previous && previous != row)
        {
            previous.Selected = false;
            previous.Refresh();
        }
        _selected = row;
        row.Selected = true;
        row.Refresh();
        _message.Text = "";
    }

    private void KeepScrollOffset(int remainingRows)
    {
        // A short list would otherwise clamp the offset and move the collapsed folder.
        double space = _scrollOffset > 0 ? _scrollOffset + _tree.Height - (double)remainingRows * RowHeight : 0;
        _scrollSpacer.HeightRequest = Math.Ceiling(Math.Max(0, space));
    }

    private int InsertChildren(TreeRow row, TrackFolder listing, int index)
    {
        foreach (var child in listing.Children)
        {
            if (row.Folders.Any(parent => parent.Id == child.Id)) continue;
            var visible = new TreeRow([.. row.Folders, child], null) { Expanded = _expanded.Contains(child.Id) };
            _rows.Insert(index++, visible);
            if (visible.Expanded && _listings.TryGetValue(child.Id, out var contents))
                index = InsertChildren(visible, contents, index);
        }
        foreach (var file in listing.Files)
            _rows.Insert(index++, new TreeRow(row.Folders, file));
        return index;
    }

    private TrackLibrarySelection? Selection => _selected is { } row
        ? new(row.Folders, row.File, row.File is null && _nested.IsChecked && AllowsNested(row), _local) : null;

    private bool AllowsNested(TreeRow row) =>
        _listings.GetValueOrDefault(row.Folders[^1].Id)?.Children.Count > 0;

    private Task ImportSelectedAsync(TreeRow row, bool addToQueue)
    {
        if (!ReferenceEquals(row, _selected)) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            if (Selection is not { } selection || (!addToQueue && selection.Local)) return;
            _message.Text = addToQueue && _local ? "Добавляю в очередь…" : "Сохраняю MP3…";
            int count = await _import(selection, addToQueue);
            _token.ThrowIfCancellationRequested();
            _message.Text = addToQueue
                ? count > 0 ? $"Добавлено в очередь: {count}" : "Нет новых треков для очереди."
                : count > 0 ? $"Сохранено в медиатеке: {count}" : "Нет новых треков для импорта.";
        }, addToQueue ? "Не удалось добавить треки" : "Не удалось импортировать треки");
    }

    private async Task ScanAsync()
    {
        if (Selection is not { Local: true } selection) return;
        _message.Text = "Подготовка к сканированию…";
        var (scanned, failed) = await _scan(selection, (current, total) =>
            _message.Text = $"Сканирование: {current} из {total}", _token);
        _token.ThrowIfCancellationRequested();
        // Only the cells bound to these rows will request fresh tags and artwork.
        foreach (var row in _rows)
        {
            if (row.File is null) continue;
            if (selection.File is { } file ? row.File == file :
                selection.IncludeChildren ? row.Folders.Contains(selection.Folders[^1]) :
                row.Folders[^1] == selection.Folders[^1])
                row.RefreshMetadata();
        }
        _message.Text = failed > 0 ? $"Обработано: {scanned}. Ошибок: {failed}." :
            scanned > 0 ? $"Пересканировано треков: {scanned}" : "В выбранной папке нет MP3.";
    }

    private async Task RunAsync(Func<Task> action, string error)
    {
        if (_busy || _token.IsCancellationRequested) return;
        _busy = true;
        UpdateActions();
        try { await action(); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex) { _message.Text = $"{error}. {ex.Message}"; }
        finally
        {
            _busy = false;
            if (!_token.IsCancellationRequested) UpdateActions();
        }
    }

    private void UpdateActions()
    {
        _pick.IsEnabled = _saved.IsEnabled = !_busy;
        _saved.IsVisible = !_local && _selected is not null;
        bool folder = _selected?.File is null;
        _nestedOption.IsVisible = folder && _selected is { } row && AllowsNested(row);
        _nestedOption.IsEnabled = !_busy;
        _scanButton.IsVisible = _local;
        _scanButton.IsEnabled = !_busy && _selected is not null;
        _selected?.Refresh();
        SemanticProperties.SetDescription(_scanButton, folder ? "Пересканировать папку" : "Пересканировать трек");
    }

    private sealed class TreeRow(TrackFolderSource[] folders, TrackFile? file)
    {
        public TrackFolderSource[] Folders { get; } = folders;
        public TrackFile? File { get; } = file;
        public int Depth => Folders.Length - (File is null ? 1 : 0);
        public bool Expanded { get; set; }
        public bool Selected { get; set; }
        public event Action? Changed;
        public event Action? MetadataChanged;
        public void Refresh() => Changed?.Invoke();
        public void RefreshMetadata() => MetadataChanged?.Invoke();
    }
}
