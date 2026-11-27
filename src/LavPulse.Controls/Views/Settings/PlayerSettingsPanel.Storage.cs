using System.IO;

namespace Laveyana.LavPulse.Controls;

public sealed partial class PlayerSettingsPanel
{
    private View CreateStorageSection()
    {
        var options = new List<LibraryStorageOption>(_storageOptions);
        if (!options.Any(option => string.Equals(option.Directory, _settings.StorageDirectory,
                StringComparison.OrdinalIgnoreCase)))
            options.Add(new(_settings.StorageDirectory, "Выбранная папка"));

        var picker = MixerStyle.Button("", icon: "player_folder.png");
        picker.HorizontalOptions = LayoutOptions.Fill;
        picker.LineBreakMode = LineBreakMode.WordWrap;
        int current = Math.Max(0, options.FindIndex(option => string.Equals(option.Directory, _settings.StorageDirectory,
            StringComparison.OrdinalIgnoreCase)));
        var location = MixerStyle.Label("Расположение: " + StorageDescription(_settings.StorageDirectory),
            11, MixerColor.Muted);
        picker.Text = options[current].Label + "  ▾";
        SemanticProperties.SetDescription(picker, "Расположение медиатеки: " + options[current].Label);

        void SelectStorage(int index)
        {
            if (index < 0 || index >= options.Count) return;
            current = index;
            _settings = _settings with { StorageDirectory = options[index].Directory };
            picker.Text = options[index].Label + "  ▾";
            SemanticProperties.SetDescription(picker, "Расположение медиатеки: " + options[index].Label);
            location.Text = "Расположение: " + StorageDescription(_settings.StorageDirectory);
        }
        picker.Clicked += async (_, _) =>
        {
            var menu = options.Select((option, index) => new ActionMenuOption(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture), option.Label,
                "player_folder.png", index == current)).ToArray();
            string? value = await PlayerView.ChooseDropdownAsync(picker, "Расположение медиатеки", menu);
            if (!_loading && int.TryParse(value, out int index)) SelectStorage(index);
        };

        var container = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { MixerStyle.Label("Хранение музыки, очереди, анализа и PCM-кэша", 13), picker, location }
        };
#if WINDOWS
        if (_pickStorageFolder is not null)
        {
            var choose = MixerStyle.Button("Выбрать папку");
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
                    }
                    SelectStorage(existing);
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
            "Смена расположения применяется при следующем запуске. " +
            "Исходные файлы удаляются после проверки переноса.", 11, MixerColor.Muted);
        container.Add(hint);
#if ANDROID
        container.Add(MixerStyle.Label(
            "Хранение в каталоге приложения Android/data. " +
            "При удалении приложения данные могут быть удалены. " +
            "Извлечение SD-карты во время работы не поддерживается.", 11, MixerColor.Muted));
#endif
        return Section("Хранение данных", container);
    }

    private static string StorageDescription(string selected) => string.IsNullOrWhiteSpace(selected)
        ? "системная память приложения"
        : Path.Combine(selected, "LavPulse", "library");
}
