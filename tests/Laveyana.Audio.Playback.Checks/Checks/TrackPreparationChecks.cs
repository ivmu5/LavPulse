using Laveyana.Audio.Playback;

internal static class TrackPreparationChecks
{
    public static async Task RunAsync(string directory)
    {
        CheckNativeRate();
        CheckRhythmAnalysis();
        CheckHighResolutionRhythmAnalysis();
        CheckPercussiveFiltering();
        CheckTempoSectionsAndReport();
        CheckDenseTechnoOnsetRhythm();
        CheckRhythmPhaseChanges();
        CheckSlowRhythmAndBreaks();
        CheckTempoFamilyRanking();
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

        var random = new Random(731);
        var noise = new RhythmEnergy[9000];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = new RhythmEnergy(.03f + .12f * (float)random.NextDouble(),
                .03f + .12f * (float)random.NextDouble(), .03f + .12f * (float)random.NextDouble());
        var chaotic = TrackRhythmAnalyzer.AnalyzeEnergy(noise, hop, (long)noise.Length * hop);
        Require(!chaotic.HasBeatGrid && chaotic.TransientFrames.Length < 90,
            "Irregular non-rhythmic energy was presented as continuous beat and accent markers.");

        Console.WriteLine("OK: percussive accents, hi-hat rejection, quiet beats and irregular-noise suppression.");
    }

    private static void CheckTempoSectionsAndReport()
    {
        const int hop = PlaybackFormat.SampleRate / 100;
        const int seconds = 110;
        var energy = new RhythmEnergy[seconds * 100];
        Array.Fill(energy, new RhythmEnergy(.035f, .025f, .018f));

        static void AddRhythm(RhythmEnergy[] values, double start, double end, double bpm)
        {
            for (double second = start + .25; second < end - .6; second += 60 / bpm)
            {
                int index = (int)Math.Round(second * 100);
                values[index] = new RhythmEnergy(.95f, .47f, .28f);
            }
        }
        AddRhythm(energy, 0, 30, 120);
        // An unmetered 23-second breakdown must not invalidate two reliable techno sections.
        AddRhythm(energy, 53, seconds, 140);
        var grid = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, (long)energy.Length * hop);
        Require(grid.HasBeatGrid && grid.Sections.Length >= 2 &&
            Math.Abs(grid.BpmAt(12 * PlaybackFormat.SampleRate) - 120) < 3 &&
            Math.Abs(grid.BpmAt(82 * PlaybackFormat.SampleRate) - 140) < 3 &&
            grid.BpmAt(40 * PlaybackFormat.SampleRate) == 0,
            "Separate 120/140 BPM regions were lost or an artificial grid was drawn in the breakdown.");
        Require(grid.OnsetDiagnostics.Length > 30 && grid.CandidateOnsets >= grid.OnsetDiagnostics.Length &&
            grid.Windows.Length >= 40,
            "The report omitted the per-window diagnostic evidence.");

        var waveform = new TrackWaveform(
            Enumerable.Repeat(new WaveformPoint(.5f, .28f, .19f, .09f), seconds * 25).ToArray(),
            (long)seconds * PlaybackFormat.SampleRate, PlaybackFormat.SampleRate / 25)
        { Rhythm = grid };
        string text = TrackAnalysisReport.Create("techno-example.mp3", waveform,
            new TrackAudioInfo("MP3", 48000, 320, false, TrackChannelMode.Stereo),
            "Example techno", "Test artist");
        Require(text.Contains("SECTIONS:") && text.Contains("ALL TEMPO WINDOWS:") &&
            text.Contains("ONSET CANDIDATES") && text.Contains("WAVEFORM BANDS:") &&
            text.Contains("140") && text.Contains("120") &&
            !text.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase),
            "The copyable report omitted sections, timing evidence or accidentally exposed a local path.");
        Console.WriteLine("OK: independent tempo sections, breakdown gaps and copyable analysis report.");
    }

    private static void CheckDenseTechnoOnsetRhythm()
    {
        // An approximately 150 BPM four-on-the-floor kick is mixed with dense,
        // unaligned percussion and synth notes. Uniform onset phase coherence would
        // dilute the actual kicks and reject the whole section.
        const int hop = PlaybackFormat.SampleRate / 100;
        var energy = new RhythmEnergy[80 * 100];
        Array.Fill(energy, new RhythmEnergy(.04f, .03f, .025f));
        var random = new Random(3925);
        for (double second = .27; second < 79; second += .4)
        {
            int index = (int)Math.Round(second * 100);
            energy[index] = new RhythmEnergy(.90f, .50f, .27f);
        }
        for (int i = 0; i < 1100; i++)
        {
            int index = random.Next(2, energy.Length - 2);
            if (energy[index].Low > .6f) continue;
            energy[index] = new RhythmEnergy(.04f + .02f * (float)random.NextDouble(),
                .03f + .22f * (float)random.NextDouble(), .20f + .52f * (float)random.NextDouble());
        }
        var result = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, (long)energy.Length * hop);
        Require(result.HasBeatGrid && result.Sections.Length > 0 &&
            result.Sections.Any(section => Math.Abs(section.Bpm - 150) < 2) &&
            result.BeatFrames.Length > 80,
            "Dense techno accompaniment concealed the repeating 150 BPM kick.");
        Require(result.Windows.Any(window => window.Accepted &&
            Math.Abs(window.Bpm - 150) < 2) &&
            result.Windows.All(window => window.Bpm >= 0),
            "Rejected tempo windows concealed their best BPM candidate in the diagnostic report.");

        var noBeat = new RhythmEnergy[energy.Length];
        Array.Fill(noBeat, new RhythmEnergy(.04f, .03f, .025f));
        for (int i = 0; i < 1100; i++)
        {
            int index = random.Next(2, noBeat.Length - 2);
            noBeat[index] = new RhythmEnergy(.06f + .1f * (float)random.NextDouble(),
                .05f + .2f * (float)random.NextDouble(), .1f + .4f * (float)random.NextDouble());
        }
        Require(!TrackRhythmAnalyzer.AnalyzeEnergy(noBeat, hop,
            (long)noBeat.Length * hop).HasBeatGrid,
            "Random transients were mistaken for a regular techno beat.");
        Console.WriteLine("OK: prominent 150 BPM kicks survive dense unrelated onsets without accepting noise.");
    }

    private static void CheckRhythmPhaseChanges()
    {
        // Generated music-like envelope, not a copy of a user's MP3. Repeated
        // phrase boundaries change the kick phase while the pulse stays near 142 BPM.
        const int hop = PlaybackFormat.SampleRate / 100;
        var energy = new RhythmEnergy[12000];
        Array.Fill(energy, new RhythmEnergy(.04f, .03f, .025f));
        for (int phrase = 0; phrase < 10; phrase++)
        {
            double start = 12 * phrase + .2 + (phrase % 2) * .18;
            for (double time = start; time < 12 * (phrase + 1) - .1; time += 60d / 142)
            {
                int index = (int)Math.Round(time * 100);
                energy[index] = new RhythmEnergy(.85f, .48f, .28f);
                int offbeat = index + (int)Math.Round(3000d / 142);
                if (offbeat < energy.Length)
                    energy[offbeat] = new RhythmEnergy(.38f, .26f, .28f);
            }
        }
        for (int i = 0; i < 900; i++)
        {
            int index = 1 + (i * 7919 + i * i * 13) % 11997;
            if (energy[index].Low <= .1f) energy[index] = new RhythmEnergy(.04f, .12f, .55f);
        }
        long total = (long)energy.Length * hop;
        var grid = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, total);
        Require(grid.HasBeatGrid && TrackRhythmAnalyzer.IsValid(grid, total) &&
            Math.Abs(grid.Bpm - 142) < 4 && grid.BeatFrames.Length > 180,
            "Phrase phase changes destroyed a locally supported rhythm.");
        Require(grid.Sections.Zip(grid.Sections.Skip(1), (a, b) => a.EndFrame <= b.StartFrame).All(value => value),
            "Recovered tempo regions overlapped.");
        Console.WriteLine("OK: sustained tempo with changing phrase phase and independent rhythmic regions.");
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
    private static void CheckSlowRhythmAndBreaks()
    {
        const int hop = PlaybackFormat.SampleRate / 100;
        var energy = Enumerable.Repeat(new RhythmEnergy(.04f, .03f, .02f), 8000).ToArray();
        // A slow pulse represented by mid-band claps, not a bass drum.
        for (double t = .25; t < 79; t += 60d / 58)
        {
            int i = (int)Math.Round(t * 100);
            if (t >= 30 && t < 43) continue;
            energy[i] = new RhythmEnergy(.04f, .9f, .2f);
        }
        var grid = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, (long)energy.Length * hop);
        Require(grid.HasBeatGrid && TrackRhythmAnalyzer.IsValid(grid, (long)energy.Length * hop),
            "Slow clap-driven pulse did not produce a valid rhythm.");
        Require(grid.BpmAt(36L * PlaybackFormat.SampleRate) == 0 &&
                !grid.BeatFrames.Any(frame => frame >= 34L * PlaybackFormat.SampleRate && frame < 39L * PlaybackFormat.SampleRate),
            "A long unsupported breakdown retained confirmed beats.");
        Require(grid.BeatFrames.All(frame => grid.Sections.Any(section => frame >= section.StartFrame && frame < section.EndFrame)),
            "A confirmed beat escaped its verified section.");
        Console.WriteLine("OK: slow rhythmic claps and unsupported breakdown suppression.");
    }

    private static void CheckTempoFamilyRanking()
    {
        const int n = 401, windows = 20;
        var raw = new float[windows, n];
        var periodic = new float[windows, n];
        var phase = new float[windows, n];
        void Set(double bpm, float score, float correlation, float coherence)
        {
            int c = (int)((bpm - 40) * 2);
            for (int w = 0; w < windows; w++)
            {
                raw[w, c] = score;
                periodic[w, c] = correlation;
                phase[w, c] = coherence;
            }
        }
        Set(40, .39f, .34f, .22f);
        Set(80, .31f, .26f, .18f);
        Set(160, .34f, .28f, .25f);
        var result = RhythmTempoFamily.Resolve(raw, periodic, phase, 40, .5);
        Require(result.Ranking[0, 240] > result.Ranking[0, 0] &&
            result.Ranking[0, 240] > result.Ranking[0, 80] && raw[0, 0] == .39f,
            "A bar-length alias displaced a corroborated faster pulse or mutated raw evidence.");
        Array.Clear(raw); Array.Clear(periodic); Array.Clear(phase);
        Set(83.5, .22f, .15f, .19f);
        Set(167, .38f, .19f, .40f);
        result = RhythmTempoFamily.Resolve(raw, periodic, phase, 40, .5);
        Require(result.Ranking[0, 87] > result.Ranking[0, 254] &&
            result.Confidence[0, 87] <= .38f,
            "A corroborated half-time pulse could not compete or ranking inflated confidence.");
        Array.Clear(raw); Array.Clear(periodic); Array.Clear(phase);
        Set(48, .9f, .8f, .9f);
        result = RhythmTempoFamily.Resolve(raw, periodic, phase, 40, .5);
        Require(result.Ranking[0, 16] > result.Ranking[0, 112],
            "A genuine slow pulse was suppressed without evidence of a faster alias.");
        Console.WriteLine("OK: independent octave decisions, bar aliases, true slow pulse and separate confidence.");
    }

}
