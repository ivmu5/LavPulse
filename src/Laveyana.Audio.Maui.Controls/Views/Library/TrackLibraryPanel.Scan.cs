using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class TrackLibraryPanel
{
    private readonly Func<TrackLibrarySelection, CancellationToken, Task<IReadOnlyList<TrackFile>>> _listScanTracks;
    private readonly Func<TrackFile, CancellationToken, Task> _scanTrack;
    private readonly LibraryTreeItem _scanPreview;
    private readonly View _scanPanel;
    private readonly Label _scanPosition = MixerStyle.Label("", 13, MixerColor.Accent, bold: true);
    private readonly ProgressBar _scanProgress = new() { HeightRequest = 6 };
    private readonly Button _skipScan = MixerStyle.Button("Пропустить", icon: "player_next.png");
    private readonly Button _stopScan = MixerStyle.Button("Остановить", icon: "player_cancel.png");
    private TrackScanBatch? _scanBatch;
    private double? _scanPulseStart;

    internal event Action? ScanAnimationChanged;
    internal bool NeedsScanFrames => _scanPreview.IsVisible;

    internal void DrawScanFrame(double time)
    {
        if (!NeedsScanFrames) return;
        _scanPulseStart ??= time;
        _scanPreview.DrawLoadingPulse(time - _scanPulseStart.Value);
    }

    private void SetScanPreviewVisible(bool visible)
    {
        if (_scanPreview.IsVisible == visible) return;
        _scanPreview.IsVisible = visible;
        _scanPreview.Opacity = 1;
        _scanPulseStart = null;
        ScanAnimationChanged?.Invoke();
    }

    private View CreateScanPanel()
    {
        MixerStyle.UseColor(_scanProgress, ProgressBar.ProgressColorProperty, MixerColor.Accent);
        _skipScan.Clicked += (_, _) =>
        {
            _skipScan.IsEnabled = false;
            _scanBatch?.Skip();
        };
        _stopScan.Clicked += (_, _) =>
        {
            _skipScan.IsEnabled = _stopScan.IsEnabled = false;
            _scanPosition.Text = "Останавливаю сканирование…";
            _scanBatch?.Stop();
        };
        var controls = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 6 };
        controls.Add(_skipScan);
        controls.Add(_stopScan, 1);
        return new VerticalStackLayout
        {
            IsVisible = false, Spacing = 6,
            Children = { _scanPosition, _scanProgress, _scanPreview, controls }
        };
    }

    private async Task ScanAsync()
    {
        if (!_local || _selected is not { } selected) return;
        using var batch = new TrackScanBatch(_token);
        _scanBatch = batch;
        _scanButton.IsVisible = false;
        _scanPanel.IsVisible = true;
        SetScanPreviewVisible(false);
        _skipScan.IsEnabled = false;
        _stopScan.IsEnabled = true;
        _scanPosition.Text = "Подготовка к сканированию…";
        _scanProgress.Progress = 0;
        _message.Text = "";
        var completed = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var files = await _listScanTracks(SelectionFor(selected), batch.Token);
            var result = await batch.RunAsync(files, _scanTrack, ShowScanProgress,
                file => completed.Add(file.LocalPath!));
            _token.ThrowIfCancellationRequested();
            string summary = $"Готово: {result.Scanned} · Пропущено: {result.Skipped} · Ошибок: {result.Failed}";
            _message.Text = batch.Token.IsCancellationRequested ? $"Остановлено. {summary}" :
                files.Count == 0 ? "В выбранной папке нет MP3." : summary;
        }
        catch (OperationCanceledException) when (batch.Token.IsCancellationRequested)
        {
            if (!_token.IsCancellationRequested) _message.Text = "Сканирование остановлено.";
        }
        finally
        {
            SetScanPreviewVisible(false);
            _scanPreview.Bind(null);
            _scanPanel.IsVisible = false;
            _scanBatch = null;
            if (!_token.IsCancellationRequested && completed.Count > 0)
            {
                foreach (var row in _rows)
                {
                    if (row.File?.LocalPath is { } path && completed.Contains(path)) row.RefreshMetadata();
                }
            }
        }
    }

    private void ShowScanProgress(TrackFile file, int index, int total)
    {
        _scanPosition.Text = $"Сканируется {index} / {total}";
        _scanProgress.Progress = (double)(index - 1) / total;
        SemanticProperties.SetDescription(_scanProgress, _scanPosition.Text);
        _scanPreview.Bind(new TreeRow([], file));
        SetScanPreviewVisible(true);
        _skipScan.IsEnabled = true;
    }
}
