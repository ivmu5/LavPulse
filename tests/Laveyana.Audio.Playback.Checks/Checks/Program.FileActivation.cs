using Laveyana.LavPulse.Tracks;

internal static partial class Program
{
    private static void CheckSystemAudioFileNames()
    {
        Require(SystemAudioFileName.Resolve("Музыка.WAV", "123", "application/octet-stream") == "Музыка.WAV",
            "An explicit supported extension must survive a generic MIME type.");
        Require(SystemAudioFileName.Resolve("Recording", "123", "audio/vnd.wave") == "Recording.wav",
            "WAV MIME alias did not resolve.");
        Require(SystemAudioFileName.Resolve(null, "123", "application/octet-stream", "audio/x-flac") == "123.flac",
            "Provider MIME fallback failed.");
        Require(SystemAudioFileName.Resolve(null, "primary:Music/song.oga", null) == "song.oga",
            "Opaque provider path was not reduced to a filename.");
        Require(SystemAudioFileName.Resolve("video.mp4", "123", "audio/mp4") is null &&
            SystemAudioFileName.Resolve("document.pdf", "123", "audio/mpeg") is null &&
            SystemAudioFileName.Resolve("unknown", null, "application/octet-stream") is null,
            "Unsupported names must not be disguised as audio.");
        Require(SystemAudioFileName.Resolve("track", null, "audio/x-opus") == "track.opus",
            "Opus MIME alias was not recognized.");
        Console.WriteLine("OK: system audio file names and MIME aliases.");
    }
}
