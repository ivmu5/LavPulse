namespace Laveyana.LavPulse.Controls;

public sealed partial class PlayerSettingsPanel
{
    private View CreateReportSection()
    {
        var summary = MixerStyle.Label("Загрузка сохранённых отметок…", 12, MixerColor.Muted);
        var message = MixerStyle.Label("", 12, MixerColor.Accent);
        message.LineBreakMode = LineBreakMode.WordWrap;
        var clipboard = MixerStyle.Button("Скопировать текстовый отчёт");
        var archive = MixerStyle.Button("Экспортировать ZIP с оригиналами MP3", primary: true);
        var clear = MixerStyle.Button("Очистить собранные отметки");
        foreach (var control in new[] { clipboard, archive, clear })
            control.HorizontalOptions = LayoutOptions.Fill;
        clipboard.IsEnabled = _copyReport is not null;
        archive.IsEnabled = _exportReportZip is not null;
        clear.IsEnabled = _clearReports is not null;

        async Task Refresh()
        {
            if (_reportSummary is null) return;
            try { summary.Text = await _reportSummary(); }
            catch (Exception ex) { summary.Text = "Не удалось загрузить отчёты: " + ex.Message; }
        }

        async Task RunAsync(Button button, Func<Task<string?>> action)
        {
            clipboard.IsEnabled = archive.IsEnabled = clear.IsEnabled = false;
            message.Text = "Подготовка отчёта…";
            try
            {
                var result = await action();
                message.Text = result ?? "Экспорт отменён.";
                await Refresh();
            }
            catch (OperationCanceledException) { message.Text = "Операция отменена."; }
            catch (Exception ex) { message.Text = "Ошибка отчёта: " + ex.Message; }
            finally
            {
                clipboard.IsEnabled = _copyReport is not null;
                archive.IsEnabled = _exportReportZip is not null;
                clear.IsEnabled = _clearReports is not null;
            }
        }
        clipboard.Clicked += async (_, _) =>
            await RunAsync(clipboard, async () => _copyReport is null ? null : await _copyReport());
        archive.Clicked += async (_, _) =>
            await RunAsync(archive, async () => _exportReportZip is null ? null : await _exportReportZip());
        clear.Clicked += async (_, _) =>
            await RunAsync(clear, async () => _clearReports is null ? null : await _clearReports());

        var help = MixerStyle.Label("При открытии «Ритм» трек автоматически появляется в отчёте, " +
            "даже если первый диапазон ещё не сохранён. «В отчёт» добавляет замечания. " +
            "ZIP содержит диагностические данные, выбранные интервалы, комментарии и оригинальные MP3. " +
            "Ничего не отправляется в интернет автоматически. На Windows выбирается папка/имя ZIP, " +
            "на Android — место сохранения через системный проводник.", 11, MixerColor.Muted);
        var result = Section("Диагностический репорт", summary, help, clipboard, archive, clear, message);
        // Open only this section on demand, so no background reads happen during playback.
        _ = Refresh();
        return result;
    }
}
