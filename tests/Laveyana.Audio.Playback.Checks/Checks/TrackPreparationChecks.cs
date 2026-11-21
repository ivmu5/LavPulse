using Laveyana.Audio.Playback;

internal static class TrackPreparationChecks
{
    public static async Task RunAsync(string directory)
    {
        var input = TrackFile.FromStream("fixture.mp3", () => Task.FromResult<Stream>(CreateMp3()));
        var original = await new TrackPreparation(directory).ImportAsync(input, CancellationToken.None);
        byte[] expected = await File.ReadAllBytesAsync(original.Path);
        original.Delete();

        // The system decoder may reject the stream during probing or after generating some PCM.
        var interrupted = new TestDecoder(3);
        var fallback = new TrackPreparation(directory, (source, _) =>
        {
            source.Position = source.Length;
            return interrupted;
        });
        var recovered = await fallback.ImportAsync(input, CancellationToken.None);
        Require(interrupted.Reads == 4 && interrupted.Disposed, "The failed decoder was not released before retrying.");
        Require(File.ReadAllBytes(recovered.Path).SequenceEqual(expected), "Fallback did not replace the partial PCM cache.");
        recovered.Delete();

        var unsupported = new TrackPreparation(directory, (source, _) =>
        {
            source.Position = source.Length;
            return null;
        });
        var managed = await unsupported.ImportAsync(input, CancellationToken.None);
        Require(File.ReadAllBytes(managed.Path).SequenceEqual(expected), "Managed fallback did not rewind an unsupported stream.");
        managed.Delete();

        var complete = new TestDecoder(2, stopAtEnd: true);
        var copiedInput = new TrackFile("fixture.mp3", async (destination, token) =>
        {
            using var source = CreateMp3();
            await source.CopyToAsync(destination, token);
        });
        var fast = new TrackPreparation(directory, (source, _) =>
        {
            Require(source is FileStream && source.Position == 0, "The platform decoder did not receive a seekable copy.");
            return complete;
        });
        var accelerated = await fast.ImportAsync(copiedInput, CancellationToken.None);
        long frames = (long)Math.Ceiling(8192d * 48_000 / 44_100);
        Require(complete.Disposed && accelerated.Waveform.TotalFrames == frames &&
            new FileInfo(accelerated.Path).Length == frames * PlaybackFormat.Pcm16FrameBytes &&
            accelerated.Waveform.Points[0].Peak == .25f, "The mono platform decoder did not use the shared resampler and waveform.");
        accelerated.Delete();

        using var cancel = new CancellationTokenSource();
        var cancelled = new TestDecoder(2, onEnd: cancel.Cancel);
        var cancelling = new TrackPreparation(directory, (_, _) => cancelled);
        try
        {
            await cancelling.ImportAsync(input, cancel.Token);
            throw new InvalidOperationException("Cancelled decoding completed.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Require(cancelled.Disposed && !Directory.EnumerateFiles(directory, "lavpulse-*").Any(),
                "Cancellation left a partial cache file or an open decoder.");
        }
        Console.WriteLine("OK: platform MP3 decoding, cancellation and managed retry after a partial write.");
    }

    internal static Stream CreateMp3()
    {
        var frame = new byte[417];
        frame[0] = 0xff;
        frame[1] = 0xfb;
        frame[2] = 0x90;
        var stream = new MemoryStream();
        for (int i = 0; i < 10; i++) stream.Write(frame);
        stream.Position = 0;
        return stream;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestDecoder(int reads, bool stopAtEnd = false, Action? onEnd = null) : IMp3SampleDecoder
    {
        public int Channels => 1;
        public int SampleRate => 44_100;
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        public int ReadSamples(float[] destination)
        {
            if (++Reads > reads)
            {
                onEnd?.Invoke();
                if (stopAtEnd) return 0;
                throw new InvalidDataException("Platform decoder stopped producing PCM.");
            }
            destination.AsSpan().Fill(.25f);
            return destination.Length;
        }

        public void Dispose() => Disposed = true;
    }
}
