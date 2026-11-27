using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

public sealed record TrackLibrarySelection(IReadOnlyList<TrackFolderSource> Folders, TrackFile? File,
    bool Local);

/// <summary>Browses saved imports or a selected source and displays the tracks in expanded folders.</summary>
public sealed partial class TrackLibraryPanel : ContentView
{
    private bool _editing;
    private readonly HashSet<TreeRow> _checkedRows = [];
    private readonly Button _bulk = MixerStyle.Button("Действия (0)");
    private Func<IReadOnlyList<TrackLibrarySelection>, Point?, Task>? _listActions;
    private Func<TrackLibrarySelection,ListSwipeAction,Task>? _swipeAction;
    private PlayerSettings _listSettings = new();

    public void ConfigureListActions(PlayerSettings settings,
        Func<IReadOnlyList<TrackLibrarySelection>,Point?,Task> actions,
        Func<TrackLibrarySelection,ListSwipeAction,Task> swipe)
    {
        _listSettings = settings;
        _listActions = actions;
        _swipeAction = swipe;
    }

    private Task ExecuteListActionAsync(TreeRow row, ListSwipeAction action, Point? anchor = null) =>
        RunAsync(async () =>
        {
            if (action == ListSwipeAction.Menu)
            {
                if (_listActions is { } menu) await menu([SelectionFor(row)], anchor);
            }
            else if (_swipeAction is { } swipe) await swipe(SelectionFor(row),action);
            if (!_token.IsCancellationRequested && _local)
                await ShowRootAsync(await _openLibrary(),local:true,preservePosition:true);
        }, "Не удалось выполнить действие");

    private void ToggleChecked(TreeRow row)
    {
        if (!_checkedRows.Add(row)) _checkedRows.Remove(row);
        UpdateActions();
    }
    private readonly TrackListView<TreeRow, LibraryTreeItem> _tree;
    private readonly Func<Task<TrackFolderSource?>> _pickFolder;
    private readonly Func<Task<TrackFolderSource>> _openLibrary;
    private readonly Func<TrackLibrarySelection, bool, Task<int>> _import;
    private readonly Func<TrackFile, CancellationToken, Task<TrackMetadata>> _readMetadata;
    private readonly Func<TrackLibrarySelection, Task<int>> _remove;
    private readonly CancellationTokenSource _session;
    private readonly CancellationToken _token;
    private readonly List<TreeRow> _rows = [];
    private readonly Label _message = MixerStyle.Label("", 11, MixerColor.Muted);
    private readonly Label _heading = MixerStyle.Label("Медиатека", MixerStyle.PhoneFont(23, 20), bold: true);
    private readonly Button _pick = MixerStyle.Button("Открыть папку", icon: "player_folder.png");
    private readonly Button _edit = MixerStyle.Button("Редактирование", icon: "player_edit.png");
    private readonly Button _back = MixerStyle.Button("Назад", primary: true);
    private readonly TapGestureRecognizer _outsideSwipe = new();
    private LibraryTreeItem? _revealed;
    private bool _local;
    private bool _busy;
    private bool _closed;
    private double _scrollOffset;

    public TrackLibraryPanel(Func<Task<TrackFolderSource?>> pickFolder,
        Func<Task<TrackFolderSource>> openLibrary, Func<TrackLibrarySelection, bool, Task<int>> import,
        Func<TrackFile, CancellationToken, Task<TrackMetadata>> readMetadata,
        Func<TrackLibrarySelection, CancellationToken, Task<IReadOnlyList<TrackFile>>> listScanTracks,
        Func<TrackFile, bool, CancellationToken, Task<bool>> scanTrack, Func<TrackFile, bool> hasAnalysis,
        int analysisParallelism, Func<TrackLibrarySelection, Task<int>> remove,
        Action close, CancellationToken lifetime,
        Func<TrackFile, CancellationToken, ValueTask<long?>>? readLocalStorageSize = null,
        int scanReturnSeconds = PlayerSettings.DefaultScanReturnSeconds)
    {
        _pickFolder = pickFolder;
        _openLibrary = openLibrary;
        _import = import;
        _readMetadata = readMetadata;
        _readLocalStorageSize = readLocalStorageSize;
        _listScanTracks = listScanTracks;
        _scanTrack = scanTrack;
        _hasAnalysis = hasAnalysis;
        _analysisParallelism = PlayerSettings.ResolveAnalysisParallelism(analysisParallelism);
        _scanReturnSeconds = Math.Clamp(scanReturnSeconds, 0, PlayerSettings.MaximumScanReturnSeconds);
        _remove = remove;
        _session = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _token = _session.Token;

        _back.Clicked += (_, _) =>
        {
            if (CloseScanModeMenu()) return;
            if (_editing) { SetEditing(false); return; }
            if (TryReturnToSavedLibrary()) return;
            close();
        };
        _pick.Clicked += async (_, _) => await RunAsync(PickAsync, "Не удалось открыть папку");
        _edit.Clicked += (_, _) => SetEditing(!_editing);
        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        _heading.VerticalOptions = LayoutOptions.Center;
        SemanticProperties.SetHeadingLevel(_heading, SemanticHeadingLevel.Level1);
#if WINDOWS
        _heading.IsVisible=false;
#endif
        header.Add(_heading);
        header.IsVisible = _heading.IsVisible;
        // Editing belongs to the list, independent of the global library actions.
        var actions = new HorizontalStackLayout
        {
            HorizontalOptions = LayoutOptions.End,
            Spacing = 8,
            Children = { _edit, _bulk }
        };
        _edit.HorizontalOptions = LayoutOptions.End;
        _bulk.HorizontalOptions = LayoutOptions.End;
        _bulk.Clicked += async (_, _) =>
        {
            if (_checkedRows.Count == 0 || _listActions is not { } menu) return;
            var selections = _checkedRows.Select(SelectionFor).ToArray();
            await RunAsync(async () =>
            {
                await menu(selections, null);
                _checkedRows.Clear();
                if (!_token.IsCancellationRequested) await ShowRootAsync(await _openLibrary(),local:true,preservePosition:true);
            }, "Не удалось выполнить действие");
        };

        _message.MaxLines = 2;
        _message.LineBreakMode = LineBreakMode.TailTruncation;
        _message.VerticalOptions = LayoutOptions.Center;
        var footer = new VerticalStackLayout { Spacing = 6, Children = { _message, _back } };
        _scanActions = CreateScanActions();
        _scanPanel = CreateScanPanel();
        var operations = new VerticalStackLayout
        {
            Spacing = 8,
            VerticalOptions = LayoutOptions.Start,
            Children = { _pick, _scanActions }
        };

        _tree = new TrackListView<TreeRow, LibraryTreeItem>(() => new LibraryTreeItem(this),
            (view, item, _) => view.Bind(item), ReleaseRow);
        _tree.Scrolled += (_, args) =>
        {
            double previous = _scrollOffset;
            if (args.ScrollY != previous) DismissDeleteAction();
            _scrollOffset = Math.Max(0, args.ScrollY);
            if (!_busy && _scrollOffset < previous && _tree.EndPadding > 0)
                KeepScrollOffset(_rows.Count);
        };
        _outsideSwipe.Tapped += (_, args) =>
        {
            if (_revealed is { } row && args.GetPosition(row) is { } point &&
                (point.X < 0 || point.X >= row.Width || point.Y < 0 || point.Y >= row.Height))
                DismissDeleteAction();
        };
        int top = 16;
#if WINDOWS
        top += WindowsWindowChrome.ContentInset;
#endif
        var layout = new Grid
        {
            Padding = new Thickness(AdaptivePanelLayout.Padding, top, AdaptivePanelLayout.Padding, 16),
            RowSpacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            MaximumWidthRequest = AdaptivePanelLayout.MaximumWidth,
            ColumnDefinitions = [new(0), new(GridLength.Star), new(0)],
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        layout.Add(header, 1, 0);
        layout.Add(operations, 1, 1);
        layout.Add(actions, 1, 2);
        layout.Add(new Grid { Children = { _tree, _scanPanel } }, 1, 3);
        layout.Add(footer, 1, 4);
        var host = new Grid { Children = { layout } };
        bool wide = false;
        host.SizeChanged += (_, _) =>
        {
            var size = AdaptivePanelLayout.Measure(host.Width, host.Height);
            if (size.Width <= 0) return;
            layout.WidthRequest = size.Width;
            layout.ColumnDefinitions[0].Width = new GridLength(size.SideWidth);
            layout.ColumnDefinitions[2].Width = new GridLength(size.SideWidth);
            layout.ColumnSpacing = size.IsWide ? AdaptivePanelLayout.Gap : 0;
            if (wide == size.IsWide) return;
            wide = size.IsWide;
            // Move the existing controls without recreating the list, scan
            // session, current selection or expanded analysis mode menu.
            Grid.SetColumn(operations, wide ? 2 : 1);
            Grid.SetRow(operations, wide ? 2 : 1);
            Grid.SetRowSpan(operations, wide ? 2 : 1);
            layout.RowDefinitions[1].Height = wide ? new GridLength(0) : GridLength.Auto;
        };
        Content = host;
        UpdateActions();
    }

    public Task InitializeAsync() => RunAsync(OpenLibraryAsync, "Не удалось открыть медиатеку");

    public event Action? ImportFolderOpened;
    public event Action<TrackFolderSource>? ImportFolderClosed;

    public bool TryReturnToSavedLibrary()
    {
        if (_local || _rows.Count == 0) return false;
        if (_busy) return true;
        var root = _rows[0].Folders[0];
        _ = RunAsync(async () =>
        {
            await OpenLibraryAsync();
            _token.ThrowIfCancellationRequested();
            ImportFolderClosed?.Invoke(root);
        }, "Не удалось открыть медиатеку");
        return true;
    }

    public bool TryReopenImportFolder(TrackFolderSource root)
    {
        if (!_local || _busy || _closed) return false;
        _ = RunAsync(() => ShowRootAsync(root, local: false), "Не удалось открыть папку");
        return true;
    }

    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        _session.Cancel();
        _metadataCache.Clear();
        ResetFolderSizes();
        ResetEditing();
        ClearScanRows();
        _tree.SetItems([]);
        _session.Dispose();
    }

    private Task ImportItemAsync(TreeRow row, bool addToQueue)
    {
        if (!addToQueue && _local) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var selection = SelectionFor(row);
            _message.Text = addToQueue && _local ? "Добавление в очередь…" : "Импорт аудиофайлов…";
            int count = await _import(selection, addToQueue);
            _token.ThrowIfCancellationRequested();
            _message.Text = addToQueue
                ? count > 0 ? $"Добавлено в очередь: {count}" : "Новых треков для добавления нет."
                : count > 0 ? $"Сохранено в медиатеке: {count}" : "Новых треков для импорта нет.";
        }, addToQueue ? "Не удалось добавить треки" : "Не удалось импортировать треки");
    }

    private bool CanRemove(TreeRow? row) => _local && row is not null && (row.File is not null || row.Folders.Length > 1);

    private async Task RemoveItemAsync(LibraryTreeItem view, TreeRow row)
    {
        if (!CanRemove(row) || (!_editing && !ReferenceEquals(_revealed, view))) return;
        await RunAsync(async () =>
        {
            int count = await _remove(SelectionFor(row));
            if (count == 0 || _token.IsCancellationRequested) return;
            await ShowRootAsync(await _openLibrary(), local: true, preservePosition: true);
            _message.Text = $"Удалено из медиатеки: {count}";
        }, "Не удалось удалить из медиатеки");
    }

    private void RevealDelete(LibraryTreeItem view)
    {
        if (ReferenceEquals(view, _revealed)) return;
        DismissDeleteAction();
        _revealed = view;
        GestureRecognizers.Add(_outsideSwipe);
        view.SetDeleteReveal(1);
    }

    private bool DismissDeleteAction()
    {
        bool revealed=false;
        foreach (var realized in _tree.RealizedRows)
        { revealed|=realized.HasRevealedSwipe;realized.ResetSwipe(); }
        if (_revealed is not { } row) return revealed;
        _revealed = null;
        GestureRecognizers.Remove(_outsideSwipe);
        row.SetDeleteReveal(0);
        return true;
    }

    private void ReleaseRow(LibraryTreeItem row)
    {
        if (ReferenceEquals(_revealed, row)) DismissDeleteAction();
        row.Bind(null);
    }

    private void SetEditing(bool editing)
    {
        if (!_local || _busy) return;
        DismissDeleteAction();
        _editing = editing;
        _checkedRows.Clear();
        UpdateActions();
    }

    public void ResetEditing()
    {
        DismissDeleteAction();
        if (!_editing) return;
        _editing = false;
        _checkedRows.Clear();
        if (!_closed) UpdateActions();
    }

    public bool TryCloseEditing()
    {
        if (CloseScanModeMenu()) return true;
        if (DismissDeleteAction()) return true;
        if (!_editing || _busy) return false;
        SetEditing(false);
        return true;
    }

    private async Task RunAsync(Func<Task> action, string error)
    {
        if (_busy || _token.IsCancellationRequested) return;
        DismissDeleteAction();
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
        _pick.IsEnabled = _edit.IsEnabled = !_busy;
        _edit.IsVisible = _local && _selected is not null && !_editing;
        _back.Text = _editing ? "Готово" : "Назад";
        _back.IsEnabled = !_busy;
        _back.IsVisible = _scanBatch is null;
        _bulk.IsVisible = _editing;
        _bulk.IsEnabled = !_busy && _checkedRows.Count > 0;
        _bulk.Text = $"Действия ({_checkedRows.Count})";
        MixerStyle.SetListEditButton(_edit, _editing);
        _scanActions.IsVisible = _local && _scanBatch is null;
        _scanActions.IsEnabled = !_busy && _selected is not null;
        if (!_scanActions.IsVisible || !_scanActions.IsEnabled) CloseScanModeMenu();
        UpdateScanModeDescription();
        foreach (var view in _tree.RealizedRows) view.UpdateRow();
    }
}
