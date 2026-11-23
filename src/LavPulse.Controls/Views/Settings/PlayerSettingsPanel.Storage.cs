namespace Laveyana.LavPulse.Controls;

public sealed partial class PlayerSettingsPanel
{
    private View CreateStorageSection()
    {
        var options = new List<LibraryStorageOption>(_storageOptions);
        if (!options.Any(option => string.Equals(option.Directory, _settings.StorageDirectory,
                StringComparison.OrdinalIgnoreCase)))
            options.Add(new(_settings.StorageDirectory, "Выбранная папка"));

        var picker = new Picker
        {
            Title = "Расположение медиатеки",
            HorizontalOptions = LayoutOptions.Fill
        };
        foreach (var option in options) picker.Items.Add(option.Label);
        int current = options.FindIndex(option => string.Equals(option.Directory, _settings.StorageDirectory,
            StringComparison.OrdinalIgnoreCase));
        picker.SelectedIndex = Math.Max(0, current);
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && picker.SelectedIndex >= 0)
                _settings = _settings with { StorageDirectory = options[picker.SelectedIndex].Directory };
        };

        var location = MixerStyle.Label("Расположение: " + StorageDescription(_settings.StorageDirectory),
            11, MixerColor.Muted);
        picker.SelectedIndexChanged += (_, _) =>
            location.Text = "Расположение: " + StorageDescription(_settings.StorageDirectory);

        var container = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { MixerStyle.Label("Где сохранять MP3, очередь, анализ и PCM-кэш", 13), picker, location }
        };
#if WINDOWS
        if (_pickStorageFolder is not null)
        {
            var choose = MixerStyle.Button("Выбрать произвольную папку");
            choose.HorizontalOptions = LayoutOptions.Fill;
            choose.Clicked += async (_, _) =>
            {
                try
                {
                    string? folder = await _pickStorageFolder();
                    if (string.IsNullOrWhiteSpace(folder)) return;
                    folder = Path.GetFullPath(folder);
                    int existing = options.FindIndex(option =>
                        string.Equals(option.Directory, folder, StringComparison.OrdinalIgnoreCase));
                    if (existing < 0)
                    {
                        existing = options.Count;
                        options.Add(new(folder, "Папка: " + folder));
                        picker.Items.Add(options[^1].Label);
                    }
                    picker.SelectedIndex = existing;
                    _settings = _settings with { StorageDirectory = folder };
                    location.Text = "Расположение: " + StorageDescription(folder);
                }
                catch (Exception ex)
                {
                    location.Text = $"Не удалось выбрать папку: {ex.Message}";
                }
            };
            container.Add(choose);
        }
#endif
        var hint = MixerStyle.Label(
            "Изменение применяется после перезапуска LavPulse. Существующая медиатека переносится целиком; " +
            "старые файлы удаляются только после проверки переноса.", 11, MixerColor.Muted);
        container.Add(hint);
#if ANDROID
        container.Add(MixerStyle.Label(
            "Общая память и SD-карта используют каталог приложения в Android/data. " +
            "Доступ к нему ограничен Android; при удалении приложения файлы также могут удалиться. " +
            "Не извлекай SD-карту во время работы LavPulse.", 11, MixerColor.Muted));
#endif
        return Section("Хранение данных", container);
    }

    private static string StorageDescription(string selected) => string.IsNullOrWhiteSpace(selected)
        ? "системная память приложения"
        : Path.Combine(selected, "LavPulse", "library");
}
