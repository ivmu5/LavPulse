using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

public sealed partial class TrackLibraryPanel
{
    private readonly Func<TrackLibrarySelection, CancellationToken, Task<IReadOnlyList<TrackFile>>> _listScanTracks;
    private readonly Func<TrackFile, bool, CancellationToken, Task<bool>> _scanTrack;
    private readonly Func<TrackFile, bool> _hasAnalysis;
    private readonly int _analysisParallelism;
    private readonly Grid _scanActions;
    private readonly Grid _scanPanel;
    private readonly Button _rescan = MixerStyle.Button("Повторный анализ");
    private readonly Button _scanNew = MixerStyle.Button("Анализ новых");
    private readonly Label _scanPosition = MixerStyle.Label("", 13, MixerColor.Accent, bold: true);
    private readonly ProgressBar _scanProgress = new() { HeightRequest = 6 };
    private readonly Button _stopScan = MixerStyle.Button("Остановить", icon: "player_cancel.png");
    private readonly VerticalStackLayout _scanRows = new() { Spacing = TrackListLayout.Stride - TrackListLayout.RowHeight };
    private readonly ScrollView _scanScroll = new();
    // Bound the number of realized covers and labels even for very large libraries.
    private const int CompletedHistoryLimit = 6;
    private readonly Queue<LibraryTreeItem> _completedScans = new();
    private readonly Dictionary<LibraryTreeItem, ScanMotion> _scanMotions = new();
    private TaskCompletionSource? _scanDone;
    private sealed record ScanMotion(double Offset, double? Start = null);
    private readonly Dictionary<TrackFile, LibraryTreeItem> _activeScans = new(ReferenceEqualityComparer.Instance);
    private TrackScanBatch? _scanBatch;
    private double? _scanPulseStart;

    internal event Action? ScanAnimationChanged;
    internal bool NeedsScanFrames => _activeScans.Count > 0 || _scanMotions.Count > 0;

    internal void DrawScanFrame(double time)
    {
        if (!NeedsScanFrames) return;
        _scanPulseStart ??= time;
        double elapsed = time - _scanPulseStart.Value;
        foreach (var row in _activeScans.Values) row.DrawLoadingPulse(elapsed);
        bool hadMotion = _scanMotions.Count > 0;
        foreach (var row in _scanMotions.Keys.ToArray())
        {
            var motion = _scanMotions[row];
            double start = motion.Start ?? time;
            double progress = Math.Clamp((time - start) / .24, 0, 1);
            row.TranslationY = motion.Offset * (1 - PlayerMotion.Ease(progress));
            if (progress >= 1) _scanMotions.Remove(row);
            else if (motion.Start is null) _scanMotions[row] = motion with { Start = start };
        }
        if (hadMotion && !NeedsScanFrames) ScanAnimationChanged?.Invoke();
    }

    private Grid CreateScanActions()
    {
        _rescan.Clicked += async (_, _) => await RunAsync(() => ScanAsync(replaceAnalysis: true), "Не удалось повторить анализ");
        _scanNew.Clicked += async (_, _) => await RunAsync(() => ScanAsync(replaceAnalysis: false), "Не удалось выполнить анализ");
        SemanticProperties.SetDescription(_scanNew, "Анализ треков без сохранённых результатов");
        var actions = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 6 };
        actions.Add(_rescan);
        actions.Add(_scanNew, 1);
        return actions;
    }

    private Grid CreateScanPanel()
    {
        MixerStyle.UseColor(_scanProgress, ProgressBar.ProgressColorProperty, MixerColor.Accent);
        _stopScan.Clicked += (_, _) =>
        {
            if (_scanDone is { } done)
            {
                done.TrySetResult();
                return;
            }
            _scanRows.IsEnabled = _stopScan.IsEnabled = false;
            _scanPosition.Text = "Остановка анализа…";
            _scanBatch?.Stop();
        };
        var panel = new Grid
        {
            IsVisible = false,
            RowSpacing = 8,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        panel.Add(new VerticalStackLayout { Spacing = 6, Children = { _scanPosition, _scanProgress } });
        _scanScroll.Content = _scanRows;
        panel.Add(_scanScroll, row: 1);
        panel.Add(_stopScan, row: 2);
        return panel;
    }

    private async Task ScanAsync(bool replaceAnalysis)
    {
        if (!_local || _selected is not { } selected) return;
        using var batch = new TrackScanBatch(_analysisParallelism, _token);
        _scanBatch = batch;
        ClearScanRows();
        _stopScan.Text = "Остановить";
        _scanActions.IsVisible = _tree.IsVisible = false;
        _scanPanel.IsVisible = true;
        _scanRows.IsEnabled = _stopScan.IsEnabled = true;
        _scanPosition.Text = "Подготовка к анализу…";
        _scanProgress.Progress = 0;
        _message.Text = "";
        var completed = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var files = await _listScanTracks(SelectionFor(selected), batch.Token);
            batch.Token.ThrowIfCancellationRequested();
            if (files.Count == 0)
            {
                _message.Text = "В выбранной папке нет музыкальных файлов.";
                return;
            }
            if (!replaceAnalysis)
            {
                files = files.Where(file => !_hasAnalysis(file)).ToArray();
                if (files.Count == 0)
                {
                    _message.Text = "Все выбранные треки уже проанализированы.";
                    return;
                }
            }

            var result = await batch.RunAsync(files, (file, token) => _scanTrack(file, replaceAnalysis, token),
                ShowScanProgress, file =>
                {
                    if (file.LocalPath is { } path) completed.Add(path);
                }, _readLocalStorageSize, CompleteScanRow);
            _token.ThrowIfCancellationRequested();
            string summary = $"Проанализировано: {result.Scanned} · Пропущено: {result.Skipped} · Ошибок: {result.Failed}";
            _message.Text = batch.Token.IsCancellationRequested ? $"Остановлено. {summary}" : summary;
            _scanPosition.Text = _message.Text;
            _scanRows.IsEnabled = true;
            _stopScan.IsEnabled = true;
            _stopScan.Text = "Готово";
            // Keep the final results visible until dismissed. Closing the panel
            // cancels this wait without keeping views or work alive.
            _scanDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _scanDone.Task.WaitAsync(_token);
        }
        catch (OperationCanceledException) when (batch.Token.IsCancellationRequested)
        {
            if (!_token.IsCancellationRequested) _message.Text = "Анализ остановлен.";
        }
        finally
        {
            _scanDone = null;
            ClearScanRows();
            _scanPanel.IsVisible = false;
            _scanBatch = null;
            if (!_token.IsCancellationRequested)
            {
                _tree.IsVisible = true;
                if (completed.Count > 0)
                {
                    _metadataCache.Invalidate(completed);
                    foreach (var row in _rows)
                    {
                        if (row.File?.LocalPath is { } path && completed.Contains(path)) row.RefreshMetadata();
                    }
                }
            }
        }
    }

    private void ShowScanProgress(IReadOnlyList<TrackFile> active, int finished, int total)
    {
        _scanPosition.Text = $"Обработано {finished} / {total} · В работе: {active.Count}";
        _scanProgress.Progress = (double)finished / total;
        SemanticProperties.SetDescription(_scanProgress, _scanPosition.Text);
        ShowActiveScans(active);
    }

    private void ShowActiveScans(IReadOnlyList<TrackFile> active)
    {
        bool animated = NeedsScanFrames;
        foreach (var file in active)
        {
            if (_activeScans.ContainsKey(file)) continue;
            var row = new LibraryTreeItem(this, track => _scanBatch?.Skip(track));
            row.Bind(new TreeRow([], file));
            _activeScans.Add(file, row);
            _scanRows.Add(row);
            MoveScanRow(row, TrackListLayout.Stride);
        }
        UpdateScanAnimation(animated);
        FollowScanTail();
    }

    private void CompleteScanRow(TrackScanResult result)
    {
        if (_closed || _token.IsCancellationRequested) return;
        bool animated = NeedsScanFrames;
        if (!_activeScans.Remove(result.File, out var row))
        {
            row = new LibraryTreeItem(this, _ => { });
            row.Bind(new TreeRow([], result.File));
            _scanRows.Add(row);
        }
        row.CompleteScan(result);
        // Preserve the row, its metadata and artwork; move finished rows ahead
        // of active tasks. Animate from the previous visual position.
        var previous = _scanRows.Children.OfType<LibraryTreeItem>()
            .Select((item, index) => (item, offset: index * TrackListLayout.Stride + item.TranslationY))
            .ToDictionary(item => item.item, item => item.offset);
        _scanRows.Children.Remove(row);
        _scanRows.Children.Insert(_completedScans.Count, row);
        _completedScans.Enqueue(row);
        while (_completedScans.Count > CompletedHistoryLimit)
        {
            var old = _completedScans.Dequeue();
            _scanMotions.Remove(old);
            old.Bind(null);
            _scanRows.Children.Remove(old);
        }
        for (int index = 0; index < _scanRows.Children.Count; index++)
        {
            if (_scanRows.Children[index] is LibraryTreeItem item && previous.TryGetValue(item, out double offset))
                MoveScanRow(item, offset - index * TrackListLayout.Stride);
        }
        UpdateScanAnimation(animated);
        FollowScanTail();
    }

    private void MoveScanRow(LibraryTreeItem row, double offset)
    {
        row.TranslationY = offset;
        if (Math.Abs(offset) < .1)
        {
            row.TranslationY = 0;
            _scanMotions.Remove(row);
        }
        else _scanMotions[row] = new(offset);
    }

    private void FollowScanTail()
    {
        // Follow incoming work only if the user is already near the end.
        // Coalesce naturally via the dispatcher, without additional animations.
        if (_scanScroll.ScrollY + _scanScroll.Height + TrackListLayout.Stride < _scanRows.Height) return;
        Dispatcher.Dispatch(() =>
        {
            if (_closed || !_scanPanel.IsVisible || _scanRows.Children.Count == 0) return;
            _ = _scanScroll.ScrollToAsync(0, Math.Max(0, TrackListLayout.RowsHeight(_scanRows.Children.Count) -
                _scanScroll.Height), false);
        });
    }

    private void ClearScanRows()
    {
        bool animated = NeedsScanFrames;
        foreach (var row in _scanRows.Children.OfType<LibraryTreeItem>()) row.Bind(null);
        _scanRows.Children.Clear();
        _activeScans.Clear();
        _completedScans.Clear();
        _scanMotions.Clear();
        UpdateScanAnimation(animated);
    }

    private void UpdateScanAnimation(bool wasAnimated)
    {
        if (wasAnimated == NeedsScanFrames) return;
        _scanPulseStart = null;
        ScanAnimationChanged?.Invoke();
    }
}
