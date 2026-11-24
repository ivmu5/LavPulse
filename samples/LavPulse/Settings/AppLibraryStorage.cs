using Laveyana.LavPulse.Tracks;

namespace Laveyana.LavPulse.Settings;

/// <summary>Tracks the active library separately from the location selected for the next launch.</summary>
internal static class AppLibraryStorage
{
    private const string ActivePreference = "library-storage-active";
    private const string DesiredPreference = "library-storage-requested";
    private const string CleanupPreference = "library-storage-cleanup";

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
                else
                {
                    if (Directory.Exists(nextRoot) && Directory.EnumerateFileSystemEntries(nextRoot).Any())
                        throw new IOException("В выбранном расположении уже есть данные. Выбери пустую папку.");
                    Directory.CreateDirectory(nextRoot);
                }
                // Durable cleanup intention is stored before switching the active library.
                Preferences.Default.Set(CleanupPreference, activeRoot);
                Preferences.Default.Set(ActivePreference, requestedDirectory);
                activeRoot = nextRoot;
                try { LibraryStorageMigration.Confirm(nextRoot); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { System.Diagnostics.Debug.WriteLine($"LavPulse transfer marker cleanup: {ex}"); }
                CleanupPreviousLibrary(activeRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                         ArgumentException or NotSupportedException or InvalidOperationException)
            {
                StartupError = $"Перенос не выполнен: {ex.Message}. Данные оставлены на старом месте.";
            }
        }

        if (!Directory.Exists(activeRoot) && !string.IsNullOrWhiteSpace(activeDirectory))
            StartupError ??= "Выбранный накопитель недоступен. Подключи его, прежде чем импортировать треки.";
        if (Directory.Exists(activeRoot)) CleanupPreviousLibrary(activeRoot);
        return activeRoot;
    }

    private static void CleanupPreviousLibrary(string activeRoot)
    {
        string pending = Preferences.Default.Get(CleanupPreference, "");
        if (string.IsNullOrWhiteSpace(pending)) return;
        try
        {
            // The previous library can only be removed after the active destination exists.
            if (Directory.Exists(activeRoot) && !LibraryStorageMigration.PathsOverlap(pending, activeRoot))
            {
                if (Directory.Exists(pending))
                {
                    LibraryStorageMigration.ValidateRegularTree(pending);
                    Directory.Delete(pending, recursive: true);
                }
                Preferences.Default.Remove(CleanupPreference);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine($"Old LavPulse library cleanup: {ex}");
        }
    }
}
