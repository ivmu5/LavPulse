namespace Laveyana.LavPulse.Controls;

public sealed partial class PlayerSettingsPanel
{
    private View CreateReportSection()
    {
        var summary = MixerStyle.Label("Загрузка отчётов…", 12, MixerColor.Muted);
        var message = MixerStyle.Label("", 12, MixerColor.Accent);
        message.LineBreakMode = LineBreakMode.WordWrap;
        var includeAudio = new CheckBox { IsChecked = _includeReportAudio };
        MixerStyle.UseColor(includeAudio, CheckBox.ColorProperty, MixerColor.Accent);
        SemanticProperties.SetDescription(includeAudio, "Экспортировать вместе с аудио");
        includeAudio.CheckedChanged += (_, args) => _includeReportAudio = args.Value;
        var audioOption = ControlRow("Экспортировать вместе с аудио", includeAudio);
        var archive = MixerStyle.Button("Экспорт ZIP", primary: true);
        var clear = MixerStyle.Button("Очистить отчёты");
        foreach (var control in new[] { archive, clear })
            control.HorizontalOptions = LayoutOptions.Fill;
        includeAudio.IsEnabled = _exportReportZip is not null;
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
            includeAudio.IsEnabled = archive.IsEnabled = clear.IsEnabled = false;
#if WINDOWS
            message.Text = ReferenceEquals(button, archive) ? "Выберите место сохранения. Прогресс появится в уведомлении." : "Подготовка отчёта…";
#else
            message.Text = "Подготовка отчёта…";
#endif
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
                includeAudio.IsEnabled = _exportReportZip is not null;
                archive.IsEnabled = _exportReportZip is not null;
                clear.IsEnabled = _clearReports is not null;
            }
        }
        archive.Clicked += async (_, _) =>
        {
            bool withAudio = includeAudio.IsChecked;
            await RunAsync(archive, async () => _exportReportZip is null ? null : await _exportReportZip(withAudio));
        };
        clear.Clicked += async (_, _) =>
            await RunAsync(clear, async () => _clearReports is null ? null : await _clearReports());

        var help = MixerStyle.Label("«Ритм» — ручная отметка басового удара и автоматическое добавление трека в отчёт. " +
            "«В отчёт» — комментарий к выбранному участку. " +
            "ZIP всегда включает результаты анализа, ручные отметки и комментарии. " +
            "Галочка добавляет оригинальные аудиофайлы; без неё архив значительно меньше. " +
            "Экспорт выполняется локально, без автоматической отправки данных.", 11, MixerColor.Muted);
        var result = Section("Диагностические отчёты", summary, help, audioOption, archive, clear, message);
        // Open only this section on demand, so no background reads happen during playback.
        _ = Refresh();
        return result;
    }
}
