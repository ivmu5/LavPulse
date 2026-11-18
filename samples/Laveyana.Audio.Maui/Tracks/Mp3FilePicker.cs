using Laveyana.Audio.Playback;
namespace Laveyana.Audio.Maui.Tracks;

internal static class Mp3FilePicker
{
    private static readonly FilePickerFileType Mp3Files = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.Android] = ["audio/mpeg", "audio/mp3", "audio/x-mp3"],
        [DevicePlatform.WinUI] = [".mp3"],
        [DevicePlatform.iOS] = ["public.mp3"],
        [DevicePlatform.MacCatalyst] = ["public.mp3"]
    });

    /// <summary>Adds a batch of MP3 files from the native picker.</summary>
    public static async Task<IReadOnlyList<TrackFile>?> PickMultipleAsync()
    {
        var files = await FilePicker.Default.PickMultipleAsync(new PickOptions
        {
            PickerTitle = "Выбери MP3",
            FileTypes = Mp3Files
        });

        return files?.OfType<FileResult>()
            .Select(file => TrackFile.FromStream(file.FileName, file.OpenReadAsync)).ToArray();
    }
}
