using Laveyana.Audio.Playback;

internal static class TrackPreparationChecks
{
    public static async Task RunAsync(string directory)
    {
        CheckNativeRate();
        CheckDecoderContracts();
        await CheckWaveFormatsAsync(directory);
        await CheckCustomCopyContractAsync(directory);
        CheckRhythmAnalysis();
        CheckHighResolutionRhythmAnalysis();
        CheckPercussiveFiltering();
        CheckTempoSectionsAndReport();
        CheckDenseTechnoOnsetRhythm();
        CheckRhythmPhaseChanges();
        CheckSlowRhythmAndBreaks();
        CheckTempoFamilyRanking();
        CheckAlternatePhaseEvidence();
        CheckContinuityAcrossBreaks();
        CheckMetricHierarchy();
        CheckWeakOpeningEvidence();
        CheckPhasePrefix();
        CheckLocalTempoAndGridBounds();
        CheckTempoPathEquivalence();
        CheckMalformedRhythmEnergy();
        CheckPcmSaturation();
        MusicalKeyChecks.Run();
        await ParallelAnalysisChecks.RunAsync();
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

    private static async Task CheckCustomCopyContractAsync(string directory)
    {
        int copies=0;
        var file = TrackFile.FromStream("custom.wav", () =>
            throw new InvalidOperationException("Replaced stream factory was used.")) with
        {
            CopyToAsync = async (target, token) =>
            {
                copies++;
                using var wave = CreateWave();
                await wave.CopyToAsync(target,token);
            }
        };
        var preparation=new TrackPreparation(directory);
        var metadata=await preparation.ReadMetadataAsync(file,default);
        var cached=await preparation.ImportAsync(file,default);
        try
        {
            Require(copies==2 && metadata.Title=="custom" && cached.Waveform.TotalFrames==24000,
                "Preparation and metadata ignored the custom copy contract.");
        }
        finally { cached.Delete(); }
        using var cancelled=new CancellationTokenSource();
        cancelled.Cancel();
        try { TrackRhythmAnalyzer.Analyze(new TrackWaveform([],0,1),cancelled.Token);
            throw new Exception("Cancelled fallback rhythm analysis succeeded."); }
        catch (OperationCanceledException) { }
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

    private static void CheckDecoderContracts()
    {
        float[] samples=Enumerable.Range(0,64).Select(i=>(i%8) switch
        {
            0=>float.NaN,1=>float.PositiveInfinity,2=>float.NegativeInfinity,
            3=>2f,4=>-2f,_=>i/64f
        }).ToArray();
        using(var source=new Mp3PcmSource(new BlockDecoder(2,[samples])))
        {
            var output=new float[samples.Length+1];
            output[^1]=10;
            Require(source.Read(output)==samples.Length/2&&
                output[..samples.Length].SequenceEqual(samples.Select(v=>float.IsFinite(v)?Math.Clamp(v,-1,1):0))&&
                output[^1]==10,"Native normalization or unused buffer tail changed.");
        }
        var throwing=new MetadataFailureDecoder();
        try { using var invalid=new Mp3PcmSource(throwing);throw new InvalidOperationException("Invalid decoder constructed."); }
        catch(IOException) { }
        Require(throwing.Disposals==1,"Decoder constructor failure leaked native resources.");
        Require(new DeckCursor(10,-1).Position==0&&new DeckCursor(10,12).Position==10,
            "Initial cursor position bypassed seek bounds.");
        try { _=new DeckCursor(-1,0);throw new InvalidOperationException("Negative cursor length accepted."); }
        catch(ArgumentOutOfRangeException) { }
        var decoder=new BlockDecoder(1,[[.25f,.5f]],44100);
        var resampled=new Mp3PcmSource(decoder);
        Require(resampled.Read([])==0&&resampled.Read(new float[1])==0&&decoder.Reads==0,
            "Empty resampler read consumed decoder frames.");
        Require(resampled.Read(new float[8])==3,"Resampler lost a final fractional frame.");
        resampled.Dispose();
        resampled.Dispose();
        Require(decoder.Disposals==1,"Decoder disposed more than once.");
        try { resampled.Read(new float[2]);throw new InvalidOperationException("Disposed decoder accepted read."); }
        catch(ObjectDisposedException) { }
        float[] tooSmall=[7];
        try { Pcm16.Decode([1,2],tooSmall);throw new InvalidOperationException("Short PCM destination accepted."); }
        catch(ArgumentException) { }
        Require(tooSmall[0]==7,"PCM decoding wrote before validating destination.");
        // RIFF PCM8 mono with one odd-sized data byte and mandatory padding.
        byte[] wav=new byte[46];
        using(var memory=new MemoryStream(wav))
        using(var writer=new BinaryWriter(memory))
        {
            writer.Write("RIFF"u8);writer.Write(38);writer.Write("WAVEfmt "u8);
            writer.Write(16);writer.Write((short)1);writer.Write((short)1);
            writer.Write(48000);writer.Write(48000);writer.Write((short)1);writer.Write((short)8);
            writer.Write("data"u8);writer.Write(1);writer.Write((byte)160);writer.Write((byte)0);
        }
        using(var valid=new WaveSampleDecoder(new MemoryStream(wav)))
        {
            float[] value=new float[1];
            Require(valid.ReadSamples(value)==1&&value[0]==.25f,"Padded PCM8 data was rejected.");
        }
        Array.Resize(ref wav,45);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4),37);
        try { using var invalid=new WaveSampleDecoder(new MemoryStream(wav));throw new InvalidOperationException("Unpadded odd WAV chunk accepted."); }
        catch(InvalidDataException) { }
        Console.WriteLine("OK: decoder empty reads, disposal, PCM validation and RIFF padding.");
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

    private sealed class MetadataFailureDecoder : IMp3SampleDecoder
    {
        public int Channels=>throw new IOException("metadata failure");
        public int SampleRate=>48000;
        public int Disposals{get;private set;}
        public int ReadSamples(float[] destination)=>0;
        public void Dispose()=>Disposals++;
    }

    private sealed class BlockDecoder(int channels, float[][] blocks,int sampleRate=PlaybackFormat.SampleRate) : IMp3SampleDecoder
    {
        public int Channels => channels;
        public int SampleRate => sampleRate;
        public int Reads { get; private set; }
        public int Disposals { get; private set; }

        public int ReadSamples(float[] destination)
        {
            int block = Reads++;
            if (block >= blocks.Length) return 0;
            blocks[block].CopyTo(destination, 0);
            return blocks[block].Length;
        }

        public void Dispose() => Disposals++;
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
        Array.Clear(raw); Array.Clear(periodic); Array.Clear(phase);
        Set(160,.4f,.2f,.3f);
        result=RhythmTempoFamily.Resolve(raw,periodic,phase,40,.5);
        // .4 local + .12 global + .04 family + .12 periodic, no unsupported quarter prior.
        Require(Math.Abs(result.Ranking[0,240]-.488f)<.0001f,
            "An empty quarter-rate candidate promoted a tempo.");
        Console.WriteLine("OK: independent octave decisions, bar aliases, true slow pulse and separate confidence.");
    }

    private static void CheckAlternatePhaseEvidence()
    {
        const int rate = 100;
        var energy = Enumerable.Repeat(new RhythmEnergy(.04f, .03f, .02f), 2400).ToArray();
        // Two parity candidates: weak bass events on the first, recurring
        // mid/high accents on the second. No user taps enter the decision.
        for (int i = 20; i < energy.Length - 40; i += 72)
        {
            energy[i] = new(.30f, .03f, .02f);
            energy[i + 36] = new(.30f, .88f, .72f);
        }
        var bands = new RhythmBandEvidence(energy, rate, CancellationToken.None);
        var choice = bands.ChoosePhase(20, 72, 60d * rate / 72, 0, 2400, false);
        Require(choice.Subdivision && choice.Alternate &&
            Math.Abs(choice.Phase - 56) <= 8,
            "Corroborated alternative parity was not evaluated correctly.");
        var protectedAnchor = bands.ChoosePhase(20, 72, 60d * rate / 72, 0, 2400, true);
        Require(!protectedAnchor.Alternate && protectedAnchor.Phase == 20,
            "A strongly coherent anchor was overwritten by an alternate parity.");
        Console.WriteLine("OK: independent phase parity and strong-anchor preservation.");
    }

    private static void CheckContinuityAcrossBreaks()
    {
        const int hop = PlaybackFormat.SampleRate / 100;
        var energy = Enumerable.Repeat(new RhythmEnergy(.04f, .03f, .02f), 9000).ToArray();
        for (int i = 25; i < energy.Length - 25; i += 50)
        {
            if (i >= 4000 && i < 4900) continue;
            energy[i] = i >= 1900 && i < 2300
                ? new RhythmEnergy(.08f, .34f, .12f)
                : new RhythmEnergy(.88f, .45f, .23f);
        }
        var trace = new TrackRhythmTrace();
        var grid = TrackRhythmAnalyzer.AnalyzeEnergy(energy, hop, (long)energy.Length * hop, trace: trace);
        int sampleStep = Math.Max(1,(int)Math.Ceiling(trace.Onsets.Length / 8192d));
        Require(grid.OnsetDiagnostics.SequenceEqual(trace.Onsets.Where((_,i)=>i%sampleStep==0)),
            "Direct diagnostic sampling changed onset order or count.");
        Require(TrackRhythmAnalyzer.IsValid(grid, (long)energy.Length * hop) &&
            grid.HasBeatGrid && !grid.BeatFrames.Any(f => f >= 42L * PlaybackFormat.SampleRate &&
                f < 47L * PlaybackFormat.SampleRate),
            "Continuity recovery accepted a real silent breakdown or produced invalid predictions.");
        Require(grid.BeatFrames.Any(f => f >= 19L * PlaybackFormat.SampleRate &&
                f < 23L * PlaybackFormat.SampleRate),
            "A weaker but periodic region lost every confirmed beat.");
        Console.WriteLine("OK: continuity across weaker attacks without filling a true breakdown.");
    }

    private static void CheckMetricHierarchy()
    {
        const int windows = 12, candidates = 401;
        var raw = new float[windows, candidates];
        var periodic = new float[windows, candidates];
        var phase = new float[windows, candidates];
        void Set(double bpm, float a, float q, float f)
        {
            int c = (int)((bpm - 40) * 2);
            for (int w = 0; w < windows; w++)
            { raw[w, c] = a; periodic[w, c] = q; phase[w, c] = f; }
        }
        Set(66, .23f, .18f, .16f);
        Set(132, .25f, .20f, .20f);
        Set(198, .26f, .22f, .20f);
        var rank = (float[,])raw.Clone();
        var root = RhythmMetricHierarchy.Apply(raw, periodic, phase, rank, 40, .5, CancellationToken.None);
        Require(root is { Triple: true } && Math.Abs(root.Value.Bpm - 66) <= .5 &&
            rank[0, 52] > rank[0, 184] && rank[0, 52] > rank[0, 316] &&
            raw[0, 52] == .23f, "The metric hierarchy failed to preserve a supported slow root.");
        Require(RhythmMetricHierarchy.SubdivisionAt(raw, periodic, 0, 66, 40, .5) is 2 or 3,
            "A corroborated subdivision was not represented separately.");
        Array.Clear(raw); Array.Clear(periodic); Array.Clear(phase);
        Set(65, .25f, .29f, .12f);
        Set(130, .85f, .70f, .90f);
        rank = (float[,])raw.Clone();
        root = RhythmMetricHierarchy.Apply(raw, periodic, phase, rank, 40, .5, CancellationToken.None);
        Require(root is null && rank[0, 180] > rank[0, 50],
            "A clear 130 BPM kick grid was incorrectly interpreted as a slow root.");
        Console.WriteLine("OK: corroborated metric roots, subdivisions and strong tempo preservation.");
    }

    private static void CheckWeakOpeningEvidence()
    {
        const int rate = 100;
        var energy = Enumerable.Repeat(new RhythmEnergy(.02f, .015f, .01f), 4000).ToArray();
        for (int i = 25; i < energy.Length - 1; i += 50)
            energy[i] = i < 600 ? new(.022f, .017f, .011f) : new(.8f, .45f, .2f);
        var bands = new RhythmBandEvidence(energy, rate, CancellationToken.None);
        Require(bands.IsWeakOpening(250) && !bands.IsWeakOpening(950),
            "Relative opening gate did not distinguish very weak attacks from established rhythm.");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { bands.Measure(0,4000,cancelled.Token); throw new InvalidOperationException("Periodicity ignored cancellation."); }
            catch (OperationCanceledException) { }
        }
        // Fixed-seed equivalence tests for the selective autocorrelation path.
        // These are test cases, not independent source-review iterations.
        var random = new Random(1973);
        for(int trial=0;trial<100;trial++)
        {
            int first=random.Next(-10,3900);
            int end=first+random.Next(1,500);
            double a=40+random.NextDouble()*200,b=40+random.NextDouble()*440;
            var all=bands.Measure(first,end);
            var pair=bands.MeasurePair(first,end,a,b);
            Require(Math.Abs(pair.First-all.AtBpm(a))<1e-12 &&
                Math.Abs(pair.Second-all.AtBpm(b))<1e-12,
                "Selective lag calculation changed periodicity.");
        }
        var quiet = energy.Select(e => new RhythmEnergy(e.Low * .004f, e.Mid * .004f, e.High * .004f)).ToArray();
        var quietBands = new RhythmBandEvidence(quiet, rate, CancellationToken.None);
        Require(quietBands.IsWeakOpening(250) && !quietBands.IsWeakOpening(950),
            "Relative opening gate depended on absolute track gain.");
        Console.WriteLine("OK: relative opening salience and quiet-master invariance.");
    }

    internal static MemoryStream CreateWave(int rate = 48_000, int bits = 24, int channels = 2)
    {
        var stream = new MemoryStream();
        int frames = rate / 2, bytes = bits / 8, length = frames * channels * bytes;
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + length);
            writer.Write("WAVE"u8); writer.Write("fmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)channels); writer.Write(rate);
            writer.Write(rate * channels * bytes); writer.Write((short)(channels * bytes));
            writer.Write((short)bits); writer.Write("data"u8); writer.Write(length);
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < channels; c++)
                    if (bits == 16) writer.Write((short)(c == 0 ? 8192 : -8192));
                    else
                    {
                        int sample = c == 0 ? 0x200000 : -0x200000;
                        writer.Write((byte)sample); writer.Write((byte)(sample >> 8)); writer.Write((byte)(sample >> 16));
                    }
        }
        stream.Position = 0;
        return stream;
    }

    private static async Task CheckWaveFormatsAsync(string directory)
    {
        foreach (var format in new[] { (48000, 24, 2), (44100, 16, 1), (96000, 24, 2) })
        {
            var input = TrackFile.FromStream("fixture.wav", () => Task.FromResult<Stream>(
                CreateWave(format.Item1, format.Item2, format.Item3)));
            var cached = await new TrackPreparation(directory).ImportAsync(input, default);
            try
            {
                Require(cached.Waveform.TotalFrames == 24000 &&
                    cached.AudioInfo?.SampleRate == format.Item1 &&
                    cached.PcmSizeBytes == new FileInfo(cached.Path).Length,
                    "WAV preparation did not preserve duration or original audio information.");
                using var reader = new CachedPcmSource(cached.Path, new DeckCursor(24000, 12000));
                var samples = new float[PlaybackFormat.BlockFrames * PlaybackFormat.Channels];
                Require(reader.Read(samples) == PlaybackFormat.BlockFrames &&
                    Math.Abs(samples[0] - .25f) < .005f &&
                    Math.Abs(samples[1] - (format.Item3 == 1 ? .25f : -.25f)) < .005f,
                    "PCM WAV samples or mono/downsampling conversion are incorrect.");
            }
            finally { cached.Delete(); }
        }

        var truncated = TrackFile.FromStream("broken.wav", () => Task.FromResult<Stream>(
            new MemoryStream(CreateWave().ToArray()[..60])));
        try
        {
            var invalid = await new TrackPreparation(directory).ImportAsync(truncated, default);
            invalid.Delete();
            throw new InvalidOperationException("A truncated WAV was accepted.");
        }
        catch (InvalidDataException) { }
        Require(!Directory.EnumerateFiles(directory, "lavpulse-*").Any(),
            "Malformed WAV left a temporary PCM file.");
        Console.WriteLine("OK: PCM WAV import, mono, 24-bit, 96 kHz downsampling and malformed-file cleanup.");
    }

    private static void CheckPhasePrefix()
    {
        int[] indices = Enumerable.Range(0, 900).Select(i => i * 13 + i % 7).ToArray();
        float[] strengths = indices.Select(i => .15f + (i % 91) / 100f).ToArray();
        double[] accents = strengths.Select(s => Math.Pow(Math.Max(0, s - .8), 2)).ToArray();
        var prefix = new RhythmPhasePrefix(indices.Length);
        foreach (double bpm in new[] { 40d, 66.5, 120, 166.5, 240 })
        {
            double angle = Math.Tau * bpm / 6000;
            prefix.Build(indices, strengths, accents, angle, default);
            for (int first = 0; first < indices.Length - 70; first += 23)
            {
                int end = first + 70;
                double r = 0, im = 0, ar = 0, ai = 0;
                for (int k = first; k < end; k++)
                {
                    var (sin, cos) = Math.SinCos(indices[k] * angle);
                    r += strengths[k] * cos; im += strengths[k] * sin;
                    ar += accents[k] * cos; ai += accents[k] * sin;
                }
                var actual = prefix.Range(first, end);
                Require(Math.Abs(actual.Real - r) < 1e-9 && Math.Abs(actual.Imaginary - im) < 1e-9 &&
                    Math.Abs(actual.AccentReal - ar) < 1e-9 && Math.Abs(actual.AccentImaginary - ai) < 1e-9,
                    "Reusable phase sums diverged from the direct estimator.");
            }
        }
    }

    private static void CheckLocalTempoAndGridBounds()
    {
        long[] beats = Enumerable.Range(0, 12).Select(i => i * 24000L)
            .Concat(Enumerable.Range(0, 12).Select(i => 288000L + i * 22000L)).ToArray();
        var section = new TrackTempoSection(0, 550000, 125, .8f, 24, 24);
        var grid = new TrackBeatGrid(125, 0, .8f, []) { BeatFrames = beats, Sections = [section] };
        Require(TrackRhythmAnalyzer.IsValid(grid, 600000) &&
            Math.Abs(grid.BpmAt(96000) - 120) < .01 &&
            Math.Abs(grid.BpmAt(450000) - 60d * PlaybackFormat.SampleRate / 22000) < .01 &&
            grid.BpmAt(570000) == 0, "Local tempo used the section median or extrapolated across a gap.");
        Require(grid.DisplayBpmAt(96000)==125 && grid.DisplayBpmAt(450000)==125 &&
            grid.DisplayBpmAt(570000)==0 &&
            (grid with {Sections=[section with {Bpm=124.6}]}).DisplayBpmAt(96000)==125,
            "Display tempo flickered with local intervals or crossed a rhythmic gap.");
        Require(!TrackRhythmAnalyzer.IsValid(grid with { Sections = [section with { StartFrame = 1 }] }, 600000),
            "A beat outside its confirmed section was accepted.");
        Require(!TrackRhythmAnalyzer.IsValid(grid with { Sections = [section, section] }, 600000),
            "Overlapping sections were accepted.");
        Require(!TrackRhythmAnalyzer.IsValid(grid with { Sections = [null!] }, 600000) &&
            !TrackRhythmAnalyzer.IsValid(grid with { Windows = [null!] }, 600000),
            "Null cache records were not rejected.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            TrackRhythmAnalyzer.AnalyzeEnergy([],480,0,cancelled.Token);
            throw new InvalidOperationException("Empty analysis ignored cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private static void CheckTempoPathEquivalence()
    {
        var random=new Random(376);
        for(int trial=0;trial<50;trial++)
        {
            int windows=random.Next(1,20), candidates=random.Next(1,90);
            var ranking=new float[windows,candidates];
            for(int w=0;w<windows;w++)
                for(int c=0;c<candidates;c++)
                    ranking[w,c]=random.Next(-5,20)*.05f;
            // Independent direct reference with the original transition cost.
            var back=new int[windows,candidates];
            var previous=new double[candidates];
            for(int c=0;c<candidates;c++)previous[c]=ranking[0,c];
            for(int w=1;w<windows;w++)
            {
                var next=new double[candidates];
                int global=Array.IndexOf(previous,previous.Max());
                for(int c=0;c<candidates;c++)
                {
                    int best=global;
                    double value=previous[global]-.45;
                    for(int earlier=Math.Max(0,c-24);earlier<=Math.Min(candidates-1,c+24);earlier++)
                    {
                        double score=previous[earlier]-.022*Math.Abs(c-earlier)*.5;
                        if(score<=value)continue;
                        value=score;best=earlier;
                    }
                    back[w,c]=best;
                    next[c]=value+ranking[w,c];
                }
                previous=next;
            }
            var expected=new int[windows];
            int selected=Array.IndexOf(previous,previous.Max());
            for(int w=windows-1;w>=0;w--)
            {expected[w]=selected;if(w>0)selected=back[w,selected];}
            Require(expected.SequenceEqual(RhythmTempoPath.Select(ranking,.5,default)),
                "Refactored tempo path changed a transition or tie.");
        }
        Require(TrackRhythmAnalyzer.WindowBoundary(1,5,480,10000)==1440 &&
            TrackRhythmAnalyzer.WindowBoundary(2,5,480,10000)==3840,
            "Odd-stride half-open boundaries are inconsistent.");
    }

    private static void CheckMalformedRhythmEnergy()
    {
        var energy=Enumerable.Repeat(new RhythmEnergy(.1f,.1f,.1f),32).ToArray();
        try
        {
            TrackRhythmAnalyzer.AnalyzeEnergy(energy,480,480*31L);
            throw new InvalidOperationException("Malformed envelope length accepted.");
        }
        catch(InvalidDataException) { }
        energy[0]=new(float.NaN,.1f,.1f);
        try
        {
            TrackRhythmAnalyzer.AnalyzeEnergy(energy,480,480*32L);
            throw new InvalidOperationException("Nonfinite energy accepted.");
        }
        catch(InvalidDataException) { }
        var grid=new TrackBeatGrid(120,0,.9f,[480]);
        Require(!TrackRhythmAnalyzer.IsValid(grid,480),"End-exclusive transient was accepted.");
        var prefix=new RhythmPhasePrefix(1);
        try
        {
            prefix.Build([],[],[],.1,default);
            throw new InvalidOperationException("Mismatched phase-prefix capacity accepted.");
        }
        catch(ArgumentException) { }
    }

    private static void CheckPcmSaturation()
    {
        float[] input = [2, -2, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1, -1, 0];
        var repeated = Enumerable.Range(0, 8).SelectMany(_ => input).ToArray();
        var encoded = new short[repeated.Length];
        Pcm16.Encode(repeated, encoded);
        short[] expected = [short.MaxValue, short.MinValue, 0, 0, 0, short.MaxValue, short.MinValue, 0];
        Require(encoded.SequenceEqual(Enumerable.Range(0, 8).SelectMany(_ => expected)),
            "PCM clipping overflowed or non-finite samples were preserved.");
        using var decoder = new Mp3PcmSource(new BlockDecoder(2, [input]));
        var output = new float[input.Length];
        Require(decoder.Read(output) == 4 && output.SequenceEqual(new float[] { 1, -1, 0, 0, 0, 1, -1, 0 }),
            "A non-finite sample reached the waveform or resampling filter.");
        Console.WriteLine("OK: PCM saturation and finite decoder samples.");
    }

}
