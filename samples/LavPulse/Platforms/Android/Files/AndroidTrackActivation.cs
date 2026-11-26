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
            string? mime = intent.Type;
            SystemTrackRequests.BeginResolve();
            _ = Task.Run(() =>
            {
                try { OpenContent(resolver, uri, mime); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LavPulse file activation: {ex}"); }
                finally { SystemTrackRequests.EndResolve(); }
            });
        }
    }

    private static void OpenContent(ContentResolver resolver, AndroidUri uri, string? mime)
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

        string? providerMime = null;
        try { providerMime = resolver.GetType(uri); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LavPulse document MIME: {ex.Message}"); }

        string? resolved = SystemAudioFileName.Resolve(name, uri.LastPathSegment, mime, providerMime);
        if (resolved is null || !TrackFile.IsSupportedAudio(resolved)) return;

        SystemTrackRequests.Open([TrackFile.FromStream(resolved, () => Task.FromResult(
            resolver.OpenInputStream(uri) ?? throw new IOException($"Android не открыл {resolved}.")))]);
    }
}
