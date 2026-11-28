#if WINDOWS
using System.Diagnostics;
using Laveyana.Audio.Playback;
using Laveyana.LavPulse.Controls;
using Laveyana.LavPulse.Tracks;
using Microsoft.Maui.ApplicationModel;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    private Task _libraryNotificationScan = Task.CompletedTask;
    private Task _reportExportWorker = Task.CompletedTask;
    private bool _reportExportInProgress;

    private void StartLibraryNotificationScan(TrackLibrarySelection selection, bool replace)
    {
        if (_closed) return;
        if (!_libraryNotificationScan.IsCompleted)
        {
            _view.ShowNotification("Анализ медиатеки", "Анализ уже выполняется. Используйте кнопки в уведомлении.");
            return;
        }
        _libraryNotificationScan = RunLibraryNotificationScanAsync(selection, replace);
    }

    private async Task RunLibraryNotificationScanAsync(TrackLibrarySelection selection, bool replace)
    {
        using var batch = new TrackScanBatch(PlayerSettings.ResolveAnalysisParallelism(_settings.AnalysisParallelism), _lifetime.Token);
        var notice = _view.ShowNotification(replace ? "Повторный анализ" : "Анализ медиатеки",
            "Подготовка списка треков…", running: true);
        bool stopping = false;
        void Stop()
        {
            if (stopping || !notice.IsRunning) return;
            stopping = true;
            notice.Update("Остановка анализа…", actions: []);
            batch.Stop();
        }
        notice.Update("Подготовка списка треков…", 0, [new("Остановить", Stop)]);
        var recent = new Queue<string>();
        try
        {
            var files = await ListLibraryScanTracksAsync(selection, batch.Token);
            batch.Token.ThrowIfCancellationRequested();
            if (!replace) files = files.Where(file => !_library.HasAnalysis(file)).ToArray();
            if (files.Count == 0)
            {
                notice.Complete("Анализ медиатеки", "Нет треков для анализа.");
                return;
            }
            var result = await batch.RunAsync(files, (file, token) => ScanLibraryTrackAsync(file, replace, token),
                (active, finished, total) =>
                {
                    if (_closed || stopping) return;
                    var actions = new List<NotificationAction> { new("Остановить", Stop) };
                    foreach (var file in active)
                    {
                        var current = file;
                        actions.Add(new("Пропустить: " + current.Name, () =>
                        {
                            if (!stopping && notice.IsRunning) batch.Skip(current);
                        }));
                    }
                    string names = string.Join("\n", active.Select(file => file.Name));
                    notice.Update($"Обработано {finished} / {total} · В работе: {active.Count}\n{names}" +
                        (recent.Count == 0 ? "" : "\nПоследние результаты:\n" + string.Join("\n", recent)),
                        finished / (double)total, actions);
                },
                file =>
                {
                    if (!_closed && file.LocalPath is { } path && _view.Panel is TrackLibraryPanel panel)
                        panel.RefreshCompletedAnalysis(path);
                }, _trackStore.ReadTrackStorageSizeAsync, row =>
                {
                    string last = $"{row.File.Name}: {row.Outcome switch
                    {
                        TrackScanOutcome.Scanned => "готово",
                        TrackScanOutcome.Skipped => "пропущен",
                        TrackScanOutcome.Stopped => "остановлен",
                        _ => "ошибка"
                    }} · {row.Elapsed.TotalSeconds:F1} с";
                    if (row.BeforeBytes is long before && row.AfterBytes is long after)
                        last += $" · {before / 1048576d:F1} → {after / 1048576d:F1} МБ";
                    recent.Enqueue(last);
                    if (recent.Count > 3) recent.Dequeue();
                });
            if (!_closed)
                notice.Complete(batch.Token.IsCancellationRequested ? "Анализ остановлен" : "Анализ завершён",
                    $"Проанализировано: {result.Scanned} · Пропущено: {result.Skipped} · Ошибок: {result.Failed}" +
                    (batch.Token.IsCancellationRequested ? $" · Не завершено: {files.Count - result.Scanned - result.Skipped - result.Failed}" : "") +
                    (recent.Count == 0 ? "" : "\nПоследние результаты:\n" + string.Join("\n", recent)),
                    retain: true);
        }
        catch (OperationCanceledException) when (batch.Token.IsCancellationRequested)
        {
            if (!_closed) notice.Complete("Анализ остановлен", "Операция отменена.");
        }
        catch (Exception ex)
        {
            if (!_closed) notice.Complete("Ошибка анализа", ex.Message, retain: true);
        }
    }

    private async Task<string?> ExportWindowsReportAsync(IReadOnlyList<TrackIssueReport> reports, string suggested, bool includeAudio)
    {
        if (_closed || _pickerOpen || _reportExportInProgress)
        {
            if (!_closed) _view.ShowNotification("Экспорт отчёта", "Другая операция выбора файла или экспорта уже выполняется.");
            return null;
        }
        _reportExportInProgress = true;
        try
        {
            if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
                throw new InvalidOperationException("Системное окно сохранения недоступно.");
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggested)
            };
            picker.FileTypeChoices.Add("ZIP", new List<string> { ".zip" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(native));
            Windows.Storage.StorageFile? picked;
            _pickerOpen = true;
            try { picked = await picker.PickSaveFileAsync(); }
            finally { _pickerOpen = false; }
            if (_closed || picked is null) return null;
            string path = picked.Path;
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("Путь сохранения недоступен.");
            var work = SaveWindowsReportAsync(reports, path, includeAudio);
            _reportExportWorker = work;
            return await work;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_closed) _view.ShowNotification("Ошибка экспорта", ex.Message);
            throw;
        }
        finally { _reportExportInProgress = false; }
    }

    private async Task<string?> SaveWindowsReportAsync(IReadOnlyList<TrackIssueReport> reports, string path, bool includeAudio)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var notice = _view.ShowNotification("Экспорт отчёта", $"Подготовка {reports.Count} треков…", running: true);
        bool stopping = false;
        var actions = new NotificationAction[] { new("Отменить", () =>
        {
            if (stopping || !notice.IsRunning) return;
            stopping = true;
            notice.Update("Отмена экспорта…", actions: []);
            cancel.Cancel();
        }) };
        notice.Update($"Подготовка {reports.Count} треков…", 0, actions);
        var progress = new Progress<ReportExportProgress>(state =>
        {
            if (_closed || stopping || !notice.IsRunning) return;
            int errors = state.DiagnosticErrors;
            string stage = state.Stage switch
            {
                "analyzing" => "Анализ: " + state.Track,
                "packing" => "Упаковка: " + state.Track,
                "finalizing" => "Завершение архива…",
                _ => "Подготовка…"
            };
            notice.Update($"{state.Completed} / {state.Total} треков\n{stage}" +
                (errors > 0 ? $"\nНедоступна свежая диагностика: {errors}" : ""),
                state.Stage == "finalizing" ? .99 : .99 * state.Completed / state.Total);
        });
        try
        {
            string platform = DeviceInfo.Current.Platform.ToString();
            int errors = await Task.Run(() => TrackReportArchive.CreateAsync(reports, path, _trackStore.ReportAudioPath,
                cancel.Token, _library.AnalyzeDiagnosticAsync, DiagnosticAppVersion, platform, progress, includeAudio: includeAudio), cancel.Token);
            string message = "ZIP сохранён: " + Path.GetFileName(path) +
                (includeAudio ? "\nС аудио" : "\nБез аудио");
            if (!_closed)
                notice.Complete("Отчёт сохранён", message + (errors > 0 ? $"\nБез свежей диагностики: {errors}" : ""),
                    [new("Открыть папку", () => OpenReportLocation(path, folder: true)),
                     new("Открыть отчёт", () => OpenReportLocation(path, folder: false))], retain: true);
            return message;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!_closed) notice.Complete("Экспорт отменён", "Архив не был опубликован.");
            return null;
        }
        catch (Exception ex)
        {
            if (!_closed) notice.Complete("Ошибка экспорта", ex.Message, retain: true);
            return "Ошибка экспорта: " + ex.Message;
        }
    }

    private void OpenReportLocation(string path, bool folder)
    {
        try
        {
            string target = folder ? Path.GetDirectoryName(path)! : path;
            if (folder ? !Directory.Exists(target) : !File.Exists(target))
                throw new FileNotFoundException("Файл или папка больше недоступны.");
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { NotifyStatus("Не удалось открыть", ex.Message); }
    }
}
#endif
