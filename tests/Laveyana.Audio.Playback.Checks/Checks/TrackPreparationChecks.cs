using Laveyana.Audio.Playback;

internal static class TrackPreparationChecks
{
    public static async Task RunAsync(string directory)
    {
        CheckNativeRate();
        CheckRhythmAnalysis();
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

    private static void CheckRhythmAnalysis()
    {
        const int pointsPerSecond = 25;
        int framesPerPoint = PlaybackFormat.SampleRate / pointsPerSecond;
        var points = Enumerable.Repeat(new WaveformPoint(.17f, .09f, .09f, .035f), 1500).ToArray();
        for (int beat = 0; beat < 120; beat++)
        {
            int index = (int)Math.Round(6 + beat * 12.5);
            points[index] = new(.91f, .8f, .5f, .2f);
        }
        var track = new TrackWaveform(points, (long)points.Length * framesPerPoint, framesPerPoint);
        var result = TrackRhythmAnalyzer.Analyze(track);
        Require(result.HasBeatGrid && Math.Abs(result.Bpm - 120) <= 1 && result.Confidence > .8f &&
            result.TransientFrames.Length >= 100 &&
            Math.Abs(result.FirstBeatFrame - 6L * framesPerPoint) <= framesPerPoint,
            "A periodic 120 BPM kick pattern did not produce a stable beat grid and transients.");

        var steady = new TrackWaveform(Enumerable.Repeat(new WaveformPoint(.5f, .2f, .15f, .1f), 1500).ToArray(),
            track.TotalFrames, framesPerPoint);
        var noRhythm = TrackRhythmAnalyzer.Analyze(steady);
        Require(!noRhythm.HasBeatGrid && noRhythm.TransientFrames.Length == 0,
            "A continuous signal created fictitious beats.");
        Console.WriteLine("OK: BPM, onset positions, beat phase and silent beat-grid suppression.");
    }

    private static void CheckNativeRate()
    {
        var decoder = new BlockDecoder(2, [[.25f, -.5f, 2, -2], [.75f, -.25f]]);
        using var stereo = new Mp3PcmSource(decoder);
        Span<float> block = stackalloc float[5];
        block[4] = 10;
        Require(stereo.Read(block[..2]) == 1 && block[..2].SequenceEqual([.25f, -.5f]),
            "Native-rate reading changed stereo samples.");
        Require(stereo.Read(block) == 2 && block.SequenceEqual([1, -1, .75f, -.25f, 10]),
            "Native-rate reading lost a frame across a decoder block boundary.");
        Require(stereo.Read(block) == 0 && stereo.Read(block) == 0 && decoder.Reads == 3,
            "Native-rate reading did not stop at the end of the stream.");

        using var mono = new Mp3PcmSource(new BlockDecoder(1, [[-.75f], [2]]));
        Require(mono.Read(block[..4]) == 2 && block[..4].SequenceEqual([-.75f, -.75f, 1, 1]),
            "Native-rate mono samples were not copied into both channels.");
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

    private sealed class BlockDecoder(int channels, float[][] blocks) : IMp3SampleDecoder
    {
        public int Channels => channels;
        public int SampleRate => PlaybackFormat.SampleRate;
        public int Reads { get; private set; }

        public int ReadSamples(float[] destination)
        {
            int block = Reads++;
            if (block >= blocks.Length) return 0;
            blocks[block].CopyTo(destination, 0);
            return blocks[block].Length;
        }

        public void Dispose() { }
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
