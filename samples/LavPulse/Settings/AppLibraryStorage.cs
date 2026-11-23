using Laveyana.LavPulse.Tracks;

namespace Laveyana.LavPulse.Settings;

/// <summary>Tracks the active library separately from the location selected for the next launch.</summary>
internal static class AppLibraryStorage
{
    private const string ActivePreference = "library-storage-active";
    private const string DesiredPreference = "library-storage-requested";

    public static string? StartupError { get; private set; }

    public static string Initialize()
    {
        StartupError = null;
        string defaultRoot = Path.Combine(FileSystem.AppDataDirectory, "library");
        string activeDirectory = Preferences.Default.Get(ActivePreference, "");
        string requestedDirectory = Preferences.Default.Get(DesiredPreference, "");
        string activeRoot;
        try { activeRoot = LibraryStorageMigration.ResolveRoot(defaultRoot, activeDirectory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            StartupError = $"Некорректный ранее выбранный путь: {ex.Message}";
            return defaultRoot;
        }

        string nextRoot;
        try { nextRoot = LibraryStorageMigration.ResolveRoot(defaultRoot, requestedDirectory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            StartupError = $"Некорректный путь нового расположения: {ex.Message}";
            return activeRoot;
        }

        if (!LibraryStorageMigration.SamePath(activeRoot, nextRoot))
        {
            try
            {
                if (!Directory.Exists(activeRoot) && !string.IsNullOrWhiteSpace(activeDirectory))
                    throw new DirectoryNotFoundException("Предыдущий накопитель недоступен. Подключи его для переноса.");
                // There are no open playback handles or concurrent library writers here:
                // MainPage and PlayerLibrary are created only after this completes.
                if (Directory.Exists(activeRoot))
                    Task.Run(() => LibraryStorageMigration.MigrateAsync(activeRoot, nextRoot))
                        .GetAwaiter().GetResult();
                else if (!Directory.Exists(nextRoot))
                    Directory.CreateDirectory(nextRoot);
                Preferences.Default.Set(ActivePreference, requestedDirectory);
                activeRoot = nextRoot;
                try { LibraryStorageMigration.Confirm(nextRoot); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { System.Diagnostics.Debug.WriteLine($"LavPulse transfer marker cleanup: {ex}"); }
                string oldRoot = LibraryStorageMigration.ResolveRoot(defaultRoot, activeDirectory);
                try { if (Directory.Exists(oldRoot)) Directory.Delete(oldRoot, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"Old LavPulse library cleanup: {ex}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                         ArgumentException or NotSupportedException)
            {
                StartupError = $"Перенос не выполнен: {ex.Message}. Данные оставлены на старом месте.";
            }
        }

        if (!Directory.Exists(activeRoot) && !string.IsNullOrWhiteSpace(activeDirectory))
            StartupError ??= "Выбранный накопитель недоступен. Подключи его, прежде чем импортировать треки.";
        return activeRoot;
    }
}
