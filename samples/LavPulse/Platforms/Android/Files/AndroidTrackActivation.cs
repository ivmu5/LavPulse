using Android.Content;
using Android.Provider;
using Laveyana.Audio.Playback;
using AndroidUri = Android.Net.Uri;

namespace Laveyana.LavPulse.Tracks;

internal static class AndroidTrackActivation
{
    internal static void Receive(Intent? intent)
    {
        if (intent?.Action != Intent.ActionView || intent.Data is not { } uri) return;

        if (uri.Scheme == "file" && uri.Path is { } path && TrackFile.IsSupportedAudio(path))
        {
            SystemTrackRequests.Open([TrackFile.FromPath(path)]);
        }
        else if (uri.Scheme == "content" && Android.App.Application.Context.ContentResolver is { } resolver)
        {
            string? extension = intent.Type switch
            {
                "audio/mpeg" or "audio/mp3" or "audio/x-mp3" => ".mp3",
                "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
                "audio/ogg" or "application/ogg" => ".ogg",
                "audio/flac" or "audio/x-flac" => ".flac",
                "audio/aac" or "audio/aacp" => ".aac",
                "audio/mp4" or "audio/x-m4a" => ".m4a",
                "audio/opus" => ".opus",
                _ => null
            };
            _ = Task.Run(() => OpenContent(resolver, uri, extension));
        }
    }

    private static void OpenContent(ContentResolver resolver, AndroidUri uri, string? extension)
    {
        string name = string.Empty;
        try
        {
            using var cursor = resolver.Query(uri, [DocumentsContract.Document.ColumnDisplayName], null, null, null);
            if (cursor?.MoveToFirst() == true) name = cursor.GetString(0) ?? string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LavPulse document name: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(name)) name = uri.LastPathSegment ?? "Трек";
        if (!TrackFile.IsSupportedAudio(name))
        {
            if (extension is null || Path.HasExtension(name)) return;
            name += extension;
        }

        SystemTrackRequests.Open([TrackFile.FromStream(name, () => Task.FromResult(
            resolver.OpenInputStream(uri) ?? throw new IOException($"Android не открыл {name}.")))]);
    }
}
