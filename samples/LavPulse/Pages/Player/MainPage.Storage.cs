using Laveyana.LavPulse.Controls;

namespace Laveyana.LavPulse;

/// <summary>Provides Windows volumes/folders and Android shared-memory/SD-card destinations.</summary>
public sealed partial class MainPage
{
    private static IReadOnlyList<LibraryStorageOption> GetStorageOptions()
    {
        var choices = new List<LibraryStorageOption> { new("", "Память приложения (по умолчанию)") };
#if WINDOWS
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                    choices.Add(new(drive.RootDirectory.FullName, $"Диск {drive.Name}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
#elif ANDROID
        // Android's scoped-storage rules allow direct FileStream access only in per-app
        // directories on shared/internal and removable external volumes.
        var locations = Android.App.Application.Context.GetExternalFilesDirs(null);
        for (int index = 0; index < locations.Length; index++)
        {
            string? path = locations[index]?.AbsolutePath;
            if (string.IsNullOrEmpty(path)) continue;
            bool removable = Android.OS.Environment.IsExternalStorageRemovable(locations[index]!);
            choices.Add(new(path, removable ? "SD-карта (каталог LavPulse)" : "Общая память телефона (каталог LavPulse)"));
        }
#endif
        return choices;
    }

    private async Task<string?> PickStorageFolderAsync()
    {
#if WINDOWS
        if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return null;
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(native));
        var selected = await picker.PickSingleFolderAsync();
        return selected?.Path;
#else
        return await Task.FromResult<string?>(null);
#endif
    }
}
