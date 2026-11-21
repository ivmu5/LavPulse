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

        if (uri.Scheme == "file" && uri.Path is { } path && TrackFile.IsMp3(path))
        {
            SystemTrackRequests.Open([TrackFile.FromPath(path)]);
        }
        else if (uri.Scheme == "content" && Android.App.Application.Context.ContentResolver is { } resolver)
        {
            bool mp3Mime = intent.Type is "audio/mpeg" or "audio/mp3" or "audio/x-mp3";
            _ = Task.Run(() => OpenContent(resolver, uri, mp3Mime));
        }
    }

    private static void OpenContent(ContentResolver resolver, AndroidUri uri, bool mp3Mime)
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
        if (!TrackFile.IsMp3(name))
        {
            if (!mp3Mime || Path.HasExtension(name)) return;
            name += ".mp3";
        }

        SystemTrackRequests.Open([TrackFile.FromStream(name, () => Task.FromResult(
            resolver.OpenInputStream(uri) ?? throw new IOException($"Android не открыл {name}.")))]);
    }
}
