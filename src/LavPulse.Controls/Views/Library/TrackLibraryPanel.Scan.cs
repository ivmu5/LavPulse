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
    private readonly Dictionary<TrackFile, LibraryTreeItem> _activeScans = new(ReferenceEqualityComparer.Instance);
    private TrackScanBatch? _scanBatch;
    private double? _scanPulseStart;

    internal event Action? ScanAnimationChanged;
    internal bool NeedsScanFrames => _activeScans.Count > 0;

    internal void DrawScanFrame(double time)
    {
        if (!NeedsScanFrames) return;
        _scanPulseStart ??= time;
        double elapsed = time - _scanPulseStart.Value;
        foreach (var row in _activeScans.Values) row.DrawLoadingPulse(elapsed);
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
        panel.Add(new ScrollView { Content = _scanRows }, row: 1);
        panel.Add(_stopScan, row: 2);
        return panel;
    }

    private async Task ScanAsync(bool replaceAnalysis)
    {
        if (!_local || _selected is not { } selected) return;
        using var batch = new TrackScanBatch(_analysisParallelism, _token);
        _scanBatch = batch;
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
                _message.Text = "В выбранной папке нет MP3.";
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
                });
            _token.ThrowIfCancellationRequested();
            string summary = $"Проанализировано: {result.Scanned} · Пропущено: {result.Skipped} · Ошибок: {result.Failed}";
            _message.Text = batch.Token.IsCancellationRequested ? $"Остановлено. {summary}" : summary;
        }
        catch (OperationCanceledException) when (batch.Token.IsCancellationRequested)
        {
            if (!_token.IsCancellationRequested) _message.Text = "Анализ остановлен.";
        }
        finally
        {
            ShowActiveScans([]);
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
        foreach (var file in _activeScans.Keys.Where(file => !active.Contains(file, ReferenceEqualityComparer.Instance)).ToArray())
        {
            var row = _activeScans[file];
            row.Bind(null);
            _scanRows.Children.Remove(row);
            _activeScans.Remove(file);
        }
        foreach (var file in active)
        {
            if (_activeScans.ContainsKey(file)) continue;
            var row = new LibraryTreeItem(this, track => _scanBatch?.Skip(track));
            row.Bind(new TreeRow([], file));
            _activeScans.Add(file, row);
            _scanRows.Add(row);
        }
        if (animated == NeedsScanFrames) return;
        _scanPulseStart = null;
        ScanAnimationChanged?.Invoke();
    }
}
