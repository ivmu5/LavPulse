using System.Threading;
using System.Diagnostics;
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
    private readonly TrackScanHistory _scanHistory = new();
    private TrackListView<TrackScanHistory.Entry, LibraryTreeItem> _scanScroll = null!;
    private readonly int _scanReturnSeconds;
    private readonly Dictionary<LibraryTreeItem, ScanMotion> _scanMotions = new();
    private readonly List<LibraryTreeItem> _motionKeys = new();
    private readonly Dictionary<TrackScanHistory.Entry, (double Y, bool Active)> _previousScanPositions = new();
    private long? _lastScanInteraction;
    private bool _scanFollowQueued, _scanProgrammaticScroll, _scanUpdating;
    private double? _scanTarget;
    private double _lastScanScrollY;
    private int _scanGeneration;
    private TaskCompletionSource? _scanDone;
    private sealed record ScanMotion(double Offset, double? Start = null);
    private TrackScanBatch? _scanBatch;
    private double? _scanPulseStart;

    internal event Action? ScanAnimationChanged;
    internal bool NeedsScanFrames => _scanHistory.ActiveCount > 0 || _scanMotions.Count > 0;

    internal void DrawScanFrame(double time)
    {
        if (!NeedsScanFrames) return;
        _scanPulseStart ??= time;
        double elapsed = time - _scanPulseStart.Value;
        foreach (var row in _scanScroll.RealizedRows)
            if (row.IsActiveScan) row.DrawLoadingPulse(elapsed);
        if (_scanHistory.ActiveCount > 0 && CanFollowScan)
            FollowScanHead();
        bool hadMotion = _scanMotions.Count > 0;
        _motionKeys.Clear();
        _motionKeys.AddRange(_scanMotions.Keys);
        foreach (var row in _motionKeys)
        {
            var motion = _scanMotions[row];
            double start = motion.Start ?? time;
            double progress = Math.Clamp((time - start) / .24, 0, 1);
            row.TranslationY = motion.Offset * (1 - PlayerMotion.Ease(progress));
            if (progress >= 1) _scanMotions.Remove(row);
            else if (motion.Start is null) _scanMotions[row] = motion with { Start = start };
        }
        _motionKeys.Clear();
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
            _scanScroll.IsEnabled = _stopScan.IsEnabled = false;
            _scanPosition.Text = "Остановка анализа…";
            _scanBatch?.Stop();
        };
        var panel = new Grid
        {
            IsVisible = false,
            RowSpacing = 8,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)]
        };
        var scanHeader = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 6 };
        scanHeader.Add(new VerticalStackLayout { Spacing = 6, Children = { _scanPosition, _scanProgress } });
        scanHeader.Add(_stopScan, 1);
        panel.Add(scanHeader);
        _scanScroll = new TrackListView<TrackScanHistory.Entry, LibraryTreeItem>(
            () => new LibraryTreeItem(this, track => _scanBatch?.Skip(track)),
            (row, entry, _) => row.BindScan(entry),
            row =>
            {
                _scanMotions.Remove(row);
                row.TranslationY = 0;
                row.BindScan(null);
            });
        _scanScroll.Scrolled += (_, args) =>
        {
            if (Math.Abs(args.ScrollY - _lastScanScrollY) <= .1) return;
            _lastScanScrollY = args.ScrollY;
            bool reached = _scanTarget is double target && Math.Abs(args.ScrollY - target) <= 1;
            if (reached) _scanTarget = null;
            if (!_scanUpdating && !_scanProgrammaticScroll && !reached && _scanHistory.ActiveCount > 0)
            {
                _lastScanInteraction = Stopwatch.GetTimestamp();
                _scanTarget = null;
                _previousScanPositions.Clear();
                foreach (var row in _scanMotions.Keys) row.TranslationY = 0;
                _scanMotions.Clear();
            }
        };
        _scanScroll.ContentSizeChanged += FollowScanHead;
        _scanScroll.SizeChanged += (_, _) => FollowScanHead();
        panel.Add(_scanScroll, row: 1);
        var done = MixerStyle.Button("Готово", primary: true, icon: "player_confirm.png");
        done.IsVisible = false;
        done.Clicked += (_, _) => _scanDone?.TrySetResult();
        _stopScan.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(Button.Text)) return;
            bool finished = _stopScan.Text == "Готово";
            _stopScan.IsVisible = !finished;
            done.IsVisible = finished;
        };
        panel.Add(done, row: 2);
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
        _back.IsVisible = false;
        _scanPanel.IsVisible = true;
        _scanScroll.IsEnabled = _stopScan.IsEnabled = true;
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
            _scanScroll.IsEnabled = true;
            _scanScroll.SetEndPadding(0);
            _previousScanPositions.Clear();
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

    private bool CanFollowScan => TrackScanHistory.CanFollow(_lastScanInteraction,
        Stopwatch.GetTimestamp(), _scanReturnSeconds);

    private void ShowActiveScans(IReadOnlyList<TrackFile> active)
    {
        bool animated = NeedsScanFrames;
        CaptureScanPositions();
        _scanHistory.AddActive(active);
        RefreshScanHistory();
        UpdateScanAnimation(animated);
    }

    private void CompleteScanRow(TrackScanResult result)
    {
        if (_closed || _token.IsCancellationRequested) return;
        bool animated = NeedsScanFrames;
        CaptureScanPositions();
        _scanHistory.Complete(result);
        RefreshScanHistory();
        UpdateScanAnimation(animated);
    }

    private void CaptureScanPositions()
    {
        // Completion and replacement notifications can arrive before the
        // native layout has caught up. Keep the earliest visual positions.
        if (!CanFollowScan || _previousScanPositions.Count > 0) return;
        foreach (var row in _scanScroll.RealizedRows)
            if (row.ScanEntry is { } entry)
                _previousScanPositions[entry] = (_scanScroll.GetRowTop(entry) + row.TranslationY - _scanScroll.ScrollY, row.IsActiveScan);
    }

    private void RefreshScanHistory()
    {
        _scanUpdating = true;
        try
        {
            _scanScroll.SetItems(_scanHistory.Items);
            // Keep the padding between completion and scheduling the next
            // task even with parallelism=1. Removing it here would briefly
            // clamp the native scroll position back into history.
            _scanScroll.SetEndPadding(QueueViewport.EndPadding(_scanScroll.Height, TrackListLayout.RowHeight));
        }
        finally { _scanUpdating = false; }
        if (CanFollowScan) FollowScanHead();
        else _previousScanPositions.Clear();
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

    private double ScanHeadOffset() => QueueViewport.TrackOffset(_scanHistory.CompletedCount,
        TrackListLayout.Stride, TrackListLayout.RowHeight, _scanScroll.Height,
        _scanScroll.ContentHeight, center: false);

    private void FollowScanHead()
    {
        if (_closed || _scanScroll is null || !_scanPanel.IsVisible || _scanHistory.ActiveCount == 0 ||
            _scanScroll.Height <= 0 || _scanUpdating || _scanFollowQueued || _scanProgrammaticScroll ||
            !CanFollowScan) return;
        double padding = QueueViewport.EndPadding(_scanScroll.Height, TrackListLayout.RowHeight);
        if (Math.Abs(_scanScroll.EndPadding - padding) > .1)
        {
            _scanUpdating = true;
            try { _scanScroll.SetEndPadding(padding); }
            finally { _scanUpdating = false; }
        }
        double offset = ScanHeadOffset();
        if (Math.Abs(offset - _scanScroll.ScrollY) <= 1 && _previousScanPositions.Count == 0) return;
        _scanFollowQueued = true;
        int generation = _scanGeneration;
        if (!Dispatcher.Dispatch(() =>
        {
            if (generation != _scanGeneration) return;
            _scanFollowQueued = false;
            _ = ApplyScanHeadAsync(generation);
        })) _scanFollowQueued = false;
    }

    private async Task ApplyScanHeadAsync(int generation)
    {
        if (_closed || _scanHistory.ActiveCount == 0 || _scanProgrammaticScroll || !CanFollowScan) return;
        double offset = ScanHeadOffset();
        if (offset > 0 && _scanScroll.MeasuredContentHeight + 1 < offset + _scanScroll.Height) return;
        _scanProgrammaticScroll = true;
        _scanTarget = offset;
        try
        {
            if (Math.Abs(offset - _scanScroll.ScrollY) > 1)
                await _scanScroll.ScrollToAsync(0, offset, false);
            if (generation != _scanGeneration || _closed) return;
            _scanScroll.Refresh();
            bool animated = NeedsScanFrames;
            foreach (var row in _scanScroll.RealizedRows)
            {
                if (row.ScanEntry is not { } entry) continue;
                double y = _scanScroll.GetRowTop(entry) - _scanScroll.ScrollY;
                if (_previousScanPositions.TryGetValue(entry, out var previous))
                {
                    if (previous.Active)
                        MoveScanRow(row, Math.Clamp(previous.Y - y, -2 * TrackListLayout.Stride,
                            _scanScroll.Height + TrackListLayout.Stride));
                }
                else if (row.IsActiveScan && _previousScanPositions.Count > 0)
                    MoveScanRow(row, Math.Max(TrackListLayout.Stride, _scanScroll.Height - y));
            }
            _previousScanPositions.Clear();
            UpdateScanAnimation(animated);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            if (generation == _scanGeneration)
            {
                _scanProgrammaticScroll = false;
                _scanTarget = null;
            }
        }
    }

    private void ClearScanRows()
    {
        bool animated = NeedsScanFrames;
        _scanGeneration++;
        _scanHistory.Clear();
        _scanMotions.Clear();
        _motionKeys.Clear();
        _previousScanPositions.Clear();
        _lastScanInteraction = null;
        _scanFollowQueued = _scanProgrammaticScroll = _scanUpdating = false;
        _scanTarget = null;
        _lastScanScrollY = 0;
        if (_scanScroll is not null)
        {
            _scanUpdating = true;
            try
            {
                _scanScroll.SetItems([]);
                _scanScroll.SetEndPadding(0);
            }
            finally { _scanUpdating = false; }
        }
        UpdateScanAnimation(animated);
    }

    private void UpdateScanAnimation(bool wasAnimated)
    {
        if (wasAnimated == NeedsScanFrames) return;
        _scanPulseStart = null;
        ScanAnimationChanged?.Invoke();
    }
}
