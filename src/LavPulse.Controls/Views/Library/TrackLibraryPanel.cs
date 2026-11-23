using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

public sealed record TrackLibrarySelection(IReadOnlyList<TrackFolderSource> Folders, TrackFile? File,
    bool Local);

/// <summary>Browses saved imports or a selected source and displays the tracks in expanded folders.</summary>
public sealed partial class TrackLibraryPanel : ContentView
{
    private bool _editing;
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
    private readonly Button _pick = MixerStyle.Button("Папка", icon: "player_folder.png");
    private readonly Button _saved = MixerStyle.Button("К сохранённым");
    private readonly Button _edit = MixerStyle.Button("Редактирование", icon: "player_edit.png");
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
        Func<TrackFile, CancellationToken, ValueTask<long?>>? readLocalStorageSize = null)
    {
        _pickFolder = pickFolder;
        _openLibrary = openLibrary;
        _import = import;
        _readMetadata = readMetadata;
        _readLocalStorageSize = readLocalStorageSize;
        _listScanTracks = listScanTracks;
        _scanTrack = scanTrack;
        _hasAnalysis = hasAnalysis;
        _analysisParallelism = Math.Clamp(analysisParallelism, PlayerSettings.MinimumAnalysisParallelism,
            PlayerSettings.MaximumAnalysisParallelism);
        _remove = remove;
        _session = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _token = _session.Token;

        var back = MixerStyle.Button("Назад");
        back.Clicked += (_, _) => close();
        _pick.Clicked += async (_, _) => await RunAsync(PickAsync, "Не удалось открыть папку");
        _saved.Clicked += (_, _) => TryReturnToSavedLibrary();
        _edit.Clicked += (_, _) => SetEditing(!_editing);
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
        actions.Add(_edit, 1);

        _message.MaxLines = 2;
        _message.LineBreakMode = LineBreakMode.TailTruncation;
        _message.VerticalOptions = LayoutOptions.Center;
        var footer = new Grid
        {
            RowDefinitions = [new(32), new(GridLength.Auto)],
            ColumnDefinitions = [new(GridLength.Star)]
        };
        footer.Add(_message);
        _scanActions = CreateScanActions();
        footer.Add(_scanActions, row: 1);
        _scanPanel = CreateScanPanel();

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
            Padding = new Thickness(16, top, 16, 16),
            RowSpacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            MaximumWidthRequest = 660,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        layout.Add(header);
        layout.Add(actions, row: 1);
        layout.Add(new Grid { Children = { _tree, _scanPanel } }, row: 2);
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
        ShowActiveScans([]);
        _tree.SetItems([]);
        _session.Dispose();
    }

    private Task ImportItemAsync(TreeRow row, bool addToQueue)
    {
        if (!addToQueue && _local) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            var selection = SelectionFor(row);
            _message.Text = addToQueue && _local ? "Добавляю в очередь…" : "Сохраняю MP3…";
            int count = await _import(selection, addToQueue);
            _token.ThrowIfCancellationRequested();
            _message.Text = addToQueue
                ? count > 0 ? $"Добавлено в очередь: {count}" : "Нет новых треков для очереди."
                : count > 0 ? $"Сохранено в медиатеке: {count}" : "Нет новых треков для импорта.";
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
            _message.Text = $"Убрано из медиатеки: {count}";
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
        if (_revealed is not { } row) return false;
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
        UpdateActions();
    }

    public void ResetEditing()
    {
        DismissDeleteAction();
        if (!_editing) return;
        _editing = false;
        if (!_closed) UpdateActions();
    }

    public bool TryCloseEditing()
    {
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
        _pick.IsEnabled = _saved.IsEnabled = _edit.IsEnabled = !_busy;
        _edit.IsVisible = _local && _selected is not null;
        MixerStyle.SetListEditButton(_edit, _editing);
        _saved.IsVisible = !_local && _selected is not null;
        bool folder = _selected?.File is null;
        _scanActions.IsVisible = _local && _scanBatch is null;
        _scanActions.IsEnabled = !_busy && _selected is not null;
        foreach (var view in _tree.RealizedRows) view.UpdateRow();
        SemanticProperties.SetDescription(_rescan, folder ? "Пересканировать папку" : "Пересканировать трек");
    }
}
