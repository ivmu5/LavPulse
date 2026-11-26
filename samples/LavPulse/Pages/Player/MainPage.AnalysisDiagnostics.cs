using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using Laveyana.Audio.Playback;
using Laveyana.LavPulse.Controls;
using Laveyana.LavPulse.Tracks;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    // Only the "В отчёт" action opens a range/comment editor. Manual rhythm taps are
    // recorded directly by pressing "Ритм" without opening or replacing the player.
    private void OpenReportMarker(CachedTrack track, long position, TrackMarkMode mode)
    {
        if (_closed || _view.Panel is not null) return;
        OpenPanel(() => new TrackReportMarkPanel(track, position, mode,
            mark => AddReportMarkAsync(track, mark), ClosePanel));
    }

    // Weak keys prevent diagnostic reports from keeping evicted PCM/waveform/artwork objects alive.
    private readonly ConditionalWeakTable<CachedTrack, Task<string>> _tapAnalysisReports = new();
    private Task _pendingBeatWrites = Task.CompletedTask;
    private static readonly string? DiagnosticAppVersion =
        typeof(MainPage).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private void RecordBassTap(CachedTrack track, long frame)
    {
        if (_closed || _worker is null || IsStopping || _view.Panel is not null)
        {
            _view.CurrentDeck.DiscardFailedBassTap(track, frame);
            _view.IncomingDeck.DiscardFailedBassTap(track, frame);
            return;
        }
        // Capture the timestamp and calibration before any asynchronous work or queueing.
        var tap = ManualBeatTap.Capture(frame, DateTimeOffset.UtcNow,
            _settings.VisualDelayMilliseconds, track.Waveform, DiagnosticAppVersion);
        _pendingBeatWrites = SaveBassTapAsync(_pendingBeatWrites, track, frame, tap);
    }

    private async Task SaveBassTapAsync(Task previous, CachedTrack track, long frame, ManualBeatTap tap)
    {
        try
        {
            if (_library.Find(track) is not { LocalPath: { } path })
                throw new FileNotFoundException("Оригинальный аудиофайл недоступен.");
            string id = Path.GetFileNameWithoutExtension(path);
            await previous;
            if (!File.Exists(path))
                throw new FileNotFoundException("Оригинальный аудиофайл недоступен.");
            var analysisTask = _tapAnalysisReports.GetValue(track, key => Task.Run(() =>
                TrackAnalysisReport.Create(key.Name, key.Waveform, key.AudioInfo, key.Title, key.Artist, key.Album)));
            string analysis = await analysisTask;
            // Do not serialize the growing report on the UI thread. Accepted taps are
            // drained on normal shutdown, even after the playback lifetime is cancelled.
            int count = await Task.Run(() => _reports.AppendBeatTapAsync(id, track.Name, track.Title, track.Artist,
                analysis, tap, CancellationToken.None));
            if (!_closed && count == 1) _view.Status.Text = $"«{track.Title}» добавлен в отчёт.";
        }
        catch (Exception ex)
        {
            if (_tapAnalysisReports.TryGetValue(track, out var task) && task.IsFaulted)
                _tapAnalysisReports.Remove(track);
            if (!_closed)
            {
                _view.CurrentDeck.DiscardFailedBassTap(track, frame);
                _view.IncomingDeck.DiscardFailedBassTap(track, frame);
                _view.Status.Text = "Не удалось сохранить удар: " + ex.Message;
            }
        }
    }

    private async Task RestoreBassTapsAsync(CachedTrack track, DeckView deck)
    {
        try
        {
            var file = _library.Find(track);
            if (file?.LocalPath is not { } path) return;
            string id = Path.GetFileNameWithoutExtension(path);
            var reports = await _reports.ReadAllAsync(_lifetime.Token);
            if (_closed) return;
            var saved = reports.FirstOrDefault(item => item.Id == id);
            if (saved is not null)
                deck.RestoreBassTaps(track, (saved.BeatTaps ?? []).Select(item => item.Frame).ToArray());
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) System.Diagnostics.Debug.WriteLine("Could not restore manual beats: " + ex);
        }
    }

    private async Task<bool> AddReportMarkAsync(CachedTrack track, TrackReportSelection selection)
    {
        if (_closed) return false;
        try
        {
            if (_library.Find(track) is not { LocalPath: { } path } || !File.Exists(path))
                throw new FileNotFoundException("Оригинальный аудиофайл недоступен.");
            string id = Path.GetFileNameWithoutExtension(path);
            long end = track.Waveform.TotalFrames;
            long startFrame = Math.Clamp(selection.StartFrame, 0, end);
            long endFrame = Math.Clamp(selection.EndFrame, startFrame, end);
            var kind = selection.Mode == TrackMarkMode.StrongRhythm
                ? TrackReportMarkKind.StrongRhythm : TrackReportMarkKind.Problem;
            string analysis = await Task.Run(() => TrackAnalysisReport.Create(
                track.Name, track.Waveform, track.AudioInfo, track.Title, track.Artist, track.Album),
                _lifetime.Token);
            await _reports.AddAsync(id, track.Name, track.Title, track.Artist, analysis,
                new TrackIssueMark(startFrame, endFrame, selection.Comment, DateTimeOffset.UtcNow, kind,
                    selection.TimingToleranceMilliseconds, selection.PossibleMissingBeats,
                    selection.SuggestedBpm), _lifetime.Token);
            _view.Status.Text = selection.Mode == TrackMarkMode.StrongRhythm
                ? $"Отмечен сильный ритм «{track.Title}»"
                : $"Комментарий добавлен в отчёт «{track.Title}»";
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            _view.Status.Text = "Не удалось сохранить отметку: " + ex.Message;
            return false;
        }
    }

    private async Task<string> ReportSummaryAsync()
    {
        await _pendingBeatWrites;
        var reports = await _reports.ReadAllAsync(_lifetime.Token);
        return $"Треков: {reports.Length}; комментариев и участков: {reports.Sum(r => r.Marks.Count)}; " +
            $"ручных ударов: {reports.Sum(r => r.BeatTaps?.Count ?? 0)}";
    }

    private async Task<string> CopyReportTextAsync()
    {
        await _pendingBeatWrites;
        var reports = await _reports.ReadAllAsync(_lifetime.Token);
        if (reports.Length == 0) throw new InvalidOperationException("Отчёт пуст. Нет сохранённых отметок или комментариев.");
        string report = await Task.Run(() => TrackReportArchive.RenderText(reports), _lifetime.Token);
        await Clipboard.Default.SetTextAsync(report);
        return $"Скопировано {report.Length:N0} символов из {reports.Length} треков.";
    }

    private async Task<string?> ExportReportZipAsync()
    {
        await _pendingBeatWrites;
        var reports = await _reports.ReadAllAsync(_lifetime.Token);
        if (reports.Length == 0) throw new InvalidOperationException("Отчёт пуст. Нет сохранённых отметок или комментариев.");
        string suggested = "LavPulse_Report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".zip";
        string temporary = Path.Combine(FileSystem.CacheDirectory, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            string platform = DeviceInfo.Current.Platform.ToString();
            await Task.Run(() => TrackReportArchive.CreateAsync(reports, temporary,
                _trackStore.ReportAudioPath, _lifetime.Token, _library.AnalyzeDiagnosticAsync,
                DiagnosticAppVersion, platform), _lifetime.Token);
#if WINDOWS
            if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                throw new InvalidOperationException("Системное окно сохранения недоступно.");
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggested)
            };
            picker.FileTypeChoices.Add("ZIP", new List<string> { ".zip" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(native));
            var picked = await picker.PickSaveFileAsync();
            if (picked is null) return null;
            await using var input = File.OpenRead(temporary);
            await using var output = new FileStream(picked.Path, FileMode.Create, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous);
            await input.CopyToAsync(output, _lifetime.Token);
            return "ZIP сохранён: " + picked.Name;
#elif ANDROID
            if (Window?.Handler?.PlatformView is not MainActivity activity)
                throw new InvalidOperationException("Выбор файла недоступен.");
            var uri = await activity.PickReportDocumentAsync(suggested, _lifetime.Token);
            if (uri is null) return null;
            await using var source = File.OpenRead(temporary);
            await using var target = Android.App.Application.Context.ContentResolver?.OpenOutputStream(uri, "w")
                ?? throw new IOException("Не удалось открыть файл назначения.");
            await source.CopyToAsync(target, _lifetime.Token);
            return "ZIP-архив сохранён.";
#else
            // Apple platforms export the ZIP through the native Share sheet / Save to Files.
            await Share.Default.RequestAsync(new ShareFileRequest("Сохранить отчёт LavPulse",
                new ShareFile(temporary)));
            return "ZIP передан в системное меню экспорта.";
#endif
        }
        finally
        {
#if IOS || MACCATALYST
            // Share sheets may still be reading the temporary file when the call completes.
#else
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
#endif
        }
    }

    private async Task<string> ClearReportsAsync()
    {
        if (!await DisplayAlertAsync("Очистить отчёты?",
            "Будут удалены все комментарии и ручные отметки. Аудиофайлы сохранятся.",
            "Очистить", "Отмена"))
            return "Очистка отменена.";
        await _pendingBeatWrites;
        await _reports.ClearAsync(_lifetime.Token);
        _view.CurrentDeck.ClearBassTaps();
        _view.IncomingDeck.ClearBassTaps();
        return "Сохранённые замечания и ручные басовые удары удалены.";
    }
}
