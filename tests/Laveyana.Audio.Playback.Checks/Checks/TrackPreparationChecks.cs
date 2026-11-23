using Laveyana.Audio.Playback;

internal static class TrackPreparationChecks
{
    public static async Task RunAsync(string directory)
    {
        CheckNativeRate();
        CheckRhythmAnalysis();
        CheckHighResolutionRhythmAnalysis();
        CheckPercussiveFiltering();
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

        var fastPoints = Enumerable.Repeat(new WaveformPoint(.17f, .09f, .09f, .035f), 1500).ToArray();
        double fastStep = pointsPerSecond * 60d / 140;
        for (int beat = 0; beat < 140; beat++)
        {
            int index = (int)Math.Round(6 + beat * fastStep);
            fastPoints[index] = new(.91f, .8f, .5f, .2f);
        }
        var fast = TrackRhythmAnalyzer.Analyze(new TrackWaveform(fastPoints, track.TotalFrames, framesPerPoint));
        Require(fast.HasBeatGrid && Math.Abs(fast.Bpm - 140) <= 1,
            "A 140 BPM track was mistaken for a 70 BPM half-time rhythm.");

        var steady = new TrackWaveform(Enumerable.Repeat(new WaveformPoint(.5f, .2f, .15f, .1f), 1500).ToArray(),
            track.TotalFrames, framesPerPoint);
        var noRhythm = TrackRhythmAnalyzer.Analyze(steady);
        Require(!noRhythm.HasBeatGrid && noRhythm.TransientFrames.Length == 0,
            "A continuous signal created fictitious beats.");
        Console.WriteLine("OK: BPM, onset positions, beat phase and silent beat-grid suppression.");
    }


    private static void CheckHighResolutionRhythmAnalysis()
    {
        static (TrackBeatGrid Grid, List<long> Hits) Measure(Func<double, double> tempo,
            bool syncopated = false, double seconds = 90)
        {
            const int hop = PlaybackFormat.SampleRate / 100;
            var energy = new RhythmEnergy[(int)(seconds * 100)];
            Array.Fill(energy, new RhythmEnergy(.045f, .035f, .025f));
            var hits = new List<long>();
            double time = .24;
            while (time < seconds - .5)
            {
                double bpm = tempo(time);
                int sample = (int)Math.Round(time * 100);
                energy[sample] = new RhythmEnergy(.9f, .5f, .3f);
                hits.Add((long)sample * hop);
                if (syncopated)
                {
                    int offbeat = (int)Math.Round((time + 30 / bpm) * 100);
                    if (offbeat < energy.Length) energy[offbeat] = new RhythmEnergy(.045f, .18f, .8f);
                }
                time += 60 / bpm;
            }
            return (TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, (long)energy.Length * hop), hits);
        }

        static void CheckAlignment(TrackBeatGrid grid, List<long> expected,
            double skipBeforeSeconds, double toleranceMs)
        {
            Require(grid.HasBeatGrid && grid.BeatFrames.Length > 20,
                "A strongly periodic track must produce a beat sequence.");
            int checkedCount = 0;
            foreach (long position in expected)
            {
                if (position < skipBeforeSeconds * PlaybackFormat.SampleRate) continue;
                int nearest = Array.BinarySearch(grid.BeatFrames, position);
                if (nearest < 0) nearest = ~nearest;
                long error = long.MaxValue;
                if (nearest < grid.BeatFrames.Length)
                    error = Math.Min(error, Math.Abs(grid.BeatFrames[nearest] - position));
                if (nearest > 0)
                    error = Math.Min(error, Math.Abs(grid.BeatFrames[nearest - 1] - position));
                Require(error <= toleranceMs * PlaybackFormat.SampleRate / 1000,
                    "Tracked beat drifted away from a measured kick.");
                checkedCount++;
            }
            Require(checkedCount > 30, "The beat test did not cover enough of the track.");
        }

        var (steady, steadyHits) = Measure(_ => 120, syncopated: true);
        Require(Math.Abs(steady.Bpm - 120) < 1.5 && Math.Abs(steady.BpmAt(50 * PlaybackFormat.SampleRate) - 120) < 1.5,
            "Syncopated hi-hats confused the steady beat detector.");
        CheckAlignment(steady, steadyHits, 0, 40);

        var (accelerando, changingHits) = Measure(time => 120 + 20 * time / 90);
        Require(Math.Abs(accelerando.BpmAt(8 * PlaybackFormat.SampleRate) - 122) < 4 &&
            Math.Abs(accelerando.BpmAt(78 * PlaybackFormat.SampleRate) - 137) < 4,
            "The beat grid did not follow a gradual change in tempo.");
        CheckAlignment(accelerando, changingHits, 0, 65);

        var (change, changedHits) = Measure(time => time < 30 ? 120 : 140);
        Require(Math.Abs(change.BpmAt(20 * PlaybackFormat.SampleRate) - 120) < 3 &&
            Math.Abs(change.BpmAt(70 * PlaybackFormat.SampleRate) - 140) < 3,
            "The local-tempo model missed a sudden tempo change.");
        CheckAlignment(change, changedHits, 37, 65);

        var (silent, _) = Measure(_ => 120, seconds: 10);
        Require(silent.HasBeatGrid, "Short rhythmic tracks were not analyzed.");
        var flat = new RhythmEnergy[6000];
        Array.Fill(flat, new RhythmEnergy(.15f, .15f, .15f));
        Require(!TrackRhythmAnalyzer.AnalyzeEnergy(flat, PlaybackFormat.SampleRate / 100,
            (long)flat.Length * PlaybackFormat.SampleRate / 100).HasBeatGrid,
            "Steady noise must not generate an invented beat grid.");
        Console.WriteLine("OK: 100 Hz onset analysis, syncopation, variable tempo, and beat-position accuracy.");
    }

    private static void CheckPercussiveFiltering()
    {
        const int hopsPerSecond = 100;
        const int hop = PlaybackFormat.SampleRate / hopsPerSecond;
        const int seconds = 35;
        var energy = new RhythmEnergy[hopsPerSecond * seconds];
        Array.Fill(energy, new RhythmEnergy(.045f, .035f, .025f));
        int kicks = 0;
        for (int step = 0; step < seconds * 8 - 4; step++)
        {
            int tick = 20 + step * 12 + step / 2;
            if (tick >= energy.Length - 1) break;
            if ((step & 3) == 0)
            {
                energy[tick] = new RhythmEnergy(.92f, .52f, .29f);
                kicks++;
            }
            else
            {
                // Three hi-hat subdivisions per kick must not become three extra marks.
                energy[tick] = new RhythmEnergy(.045f, .055f, .96f);
            }
        }
        var analyzed = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, (long)energy.Length * hop);
        Require(analyzed.HasBeatGrid && Math.Abs(analyzed.Bpm - 120) < 2,
            "Kick-driven beat detection disappeared under loud hi-hats.");
        Require(analyzed.TransientFrames.Length >= kicks * .85 &&
                analyzed.TransientFrames.Length <= kicks * 1.1,
            "Visual transient marks included hi-hat subdivisions or missed the kicks.");

        // A consistent volume reduction must not disable tempo or transient estimation.
        var quieter = energy.Select(e => new RhythmEnergy(e.Low * .004f, e.Mid * .004f, e.High * .004f)).ToArray();
        var quiet = TrackRhythmAnalyzer.AnalyzeEnergy(quieter, hop, (long)quieter.Length * hop);
        Require(quiet.HasBeatGrid && Math.Abs(quiet.Bpm - analyzed.Bpm) <= 1 &&
                Math.Abs(quiet.TransientFrames.Length - analyzed.TransientFrames.Length) <= 2,
            "Quietly mastered music was rejected by an absolute amplitude threshold.");

        var trebleOnly = Enumerable.Repeat(new RhythmEnergy(.045f, .035f, .025f), 2000).ToArray();
        for (int tick = 20; tick < trebleOnly.Length; tick += 12)
            trebleOnly[tick] = new RhythmEnergy(.045f, .035f, .9f);
        var treble = TrackRhythmAnalyzer.AnalyzeEnergy(trebleOnly, hop, (long)trebleOnly.Length * hop);
        Require(treble.TransientFrames.Length == 0,
            "High-frequency clicks were incorrectly labeled as strong drum attacks.");

        Console.WriteLine("OK: percussive accents, hi-hat rejection and quiet-beat detection.");
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
