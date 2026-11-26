using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Resolves names from document providers, including opaque content URIs.
/// Never relabel an explicitly unsupported filename.</summary>
internal static class SystemAudioFileName
{
    internal static string? ExtensionForMime(string? mime) => mime?.Trim().ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" or "audio/x-mp3" or "audio/mpeg3" or "audio/x-mpeg" => ".mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" or "audio/x-pn-wav" => ".wav",
        "audio/ogg" or "application/ogg" or "audio/x-ogg" or "audio/vorbis" or "audio/x-vorbis" => ".ogg",
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/aac" or "audio/aacp" or "audio/x-aac" => ".aac",
        "audio/mp4" or "audio/x-m4a" or "audio/m4a" => ".m4a",
        "audio/opus" or "audio/x-opus" => ".opus",
        _ => null
    };

    internal static string? Resolve(string? displayName, string? uriSegment,
        string? declaredMime, string? providerMime = null)
    {
        string name = string.IsNullOrWhiteSpace(displayName)
            ? (string.IsNullOrWhiteSpace(uriSegment) ? "Трек" : uriSegment)
            : displayName;
        name = Path.GetFileName(name.Replace('\\', '/'));
        if (TrackFile.IsKnownAudio(name)) return name;
        if (Path.HasExtension(name)) return null;
        string? extension = ExtensionForMime(declaredMime) ?? ExtensionForMime(providerMime);
        return extension is null ? null : (string.IsNullOrWhiteSpace(name) ? "Трек" : name) + extension;
    }
}
