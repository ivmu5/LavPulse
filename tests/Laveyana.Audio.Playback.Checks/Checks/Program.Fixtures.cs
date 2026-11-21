using Laveyana.Audio;
using Laveyana.Audio.Playback;

internal static partial class Program
{
    private static CachedTrack MakeTrack(string directory, string name, short left, short right)
    {
        int frames = 8 * Rate;
        string path = Path.Combine(directory, name + ".pcm");
        using var stream = new BinaryWriter(File.Create(path));
        for (int frame = 0; frame < frames; frame++)
        {
            stream.Write(left);
            stream.Write(right);
        }
        return new CachedTrack(name, path, new TrackWaveform([], frames, Rate));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class InMemoryOutput(Action<IAudioRenderSource> render) : IAudioOutput
    {
        public Task PlayAsync(IAudioRenderSource source, CancellationToken cancellationToken)
        {
            render(source);
            return Task.CompletedTask;
        }
    }

    private sealed class ShortSource(float sample = 1f) : IPcmSource
    {
        private bool _read;
        public int SampleRate => PlaybackFormat.SampleRate;

        public int Read(Span<float> destination)
        {
            if (_read)
            {
                return 0;
            }
            _read = true;
            destination[..8].Fill(sample);
            return 4;
        }
    }

    private sealed class ContinuousPcm : IPcmSource
    {
        public int SampleRate => PlaybackFormat.SampleRate;

        public int Read(Span<float> destination)
        {
            destination.Fill(.25f);
            return destination.Length / PlaybackFormat.Channels;
        }
    }
}
