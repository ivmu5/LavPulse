using Laveyana.Audio.Playback;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    /// <summary>Temporary user-initiated copy action on the right side of each deck header.</summary>
    private async void CopyTrackAnalysisAsync(CachedTrack track)
    {
        if (_closed) return;
        try
        {
            string report = await Task.Run(() => TrackAnalysisReport.Create(
                track.Name, track.Waveform, track.AudioInfo, track.Title, track.Artist, track.Album),
                _lifetime.Token);
            if (_closed) return;
            await Clipboard.Default.SetTextAsync(report);
            _view.Status.Text = $"Скопирован анализ «{track.Title}» ({report.Length:N0} символов)";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Не удалось скопировать анализ: {ex.Message}";
        }
    }
}
