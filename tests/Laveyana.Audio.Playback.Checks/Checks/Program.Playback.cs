using Laveyana.Audio;
using Laveyana.Audio.Playback;

internal static partial class Program
{
    private static async Task CheckPauseAndResume(CachedTrack first, CachedTrack second, bool duringMix)
    {
        using var session = new QueuePlaybackSession();
        session.SetFirst(first);
        session.SetAutoMix(false);
        if (duringMix)
        {
            Require(session.SetNext(second) && session.RequestMix(), "The crossfade could not start.");
            Require(session.Snapshot is { MixPending: true, IsMixing: true, Incoming: null }, "The pending mix was not exposed.");
            Require(!session.Seek(first, Rate) && !session.TrySetSpeed(first, 1.25), "The pending mix allowed edits to the outgoing deck.");
        }

        var block = new float[BlockSize * PlaybackFormat.Channels];
        int rendered = 0;
        await session.RunAsync(new InMemoryOutput(source =>
        {
            Require(source.Render(block) == BlockSize, "Initial PCM block is missing.");
            if (duringMix)
            {
                Require(session.Snapshot is { MixPending: false, IsMixing: true, Incoming: not null }, "The active mix was not exposed.");
                Require(!session.Seek(first, Rate) && !session.Seek(second, Rate) && !session.TrySetSpeed(first, 1.25),
                    "The active mix allowed edits to a deck.");
            }
            Require(session.RequestPause(), "The fade request was not accepted.");
            Require(!session.RequestPause(), "A duplicate request restarted the fade.");

            int frames;
            float previousGain = 1;
            while ((frames = source.Render(block)) > 0)
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    int index = frame * PlaybackFormat.Channels;
                    float left = block[index] * 4;
                    float right = block[index + 1] * 4;
                    float gain = MathF.Sqrt(left * left + right * right);
                    Require(float.IsFinite(gain) && gain <= previousGain + .00001f, "The output gain increased during the fade.");
                    float expected = HyperbolicFade.Gains((float)rendered / (PauseFrames - 1)).Outgoing;
                    Require(Math.Abs(expected - gain) < .00001f, "The output did not follow the expected hyperbolic curve.");
                    if (rendered == PauseFrames - 1)
                    {
                        Require(left == 0 && right == 0, "The final frame was not silent.");
                    }
                    previousGain = gain;
                    rendered++;
                }
            }
            Require(source.Render(block) == 0, "The output continued after the fade.");
        }), startMilliseconds: 0, CancellationToken.None);

        long pausedAt = BlockSize + PauseFrames;
        Require(rendered == PauseFrames, "The fade length was not 300 ms.");
        Require(session.Snapshot.Current?.Position == pausedAt, "The outgoing cursor did not stop at the last faded frame.");
        if (duringMix)
        {
            Require(session.Snapshot.Incoming?.Position == pausedAt, "The incoming deck did not pause with the outgoing deck.");
        }

        await session.RunAsync(new InMemoryOutput(source =>
        {
            Require(source.Render(block) == BlockSize, "The next output run inherited the paused state.");
            Require(block[0] == 0 && block[block.Length - 2] > 0, "Resume did not start with a fade.");
        }), CancellationToken.None);
        Require(session.Snapshot.Current?.Position == pausedAt + BlockSize, "Resume did not continue from the saved cursor.");
        if (duringMix)
        {
            Require(session.Snapshot.Incoming?.Position == pausedAt + BlockSize, "The paused crossfade did not resume.");
        }
    }

    /// <summary>Playback-head measurements must gate both deck and spectrum presentation.</summary>
    private static void CheckAudioPresentationHistory(CachedTrack first, CachedTrack second)
    {
        var start = new QueueSnapshot(new PlayingTrack(first, 0, Deck.A), null);
        var timeline = new AudioPresentationHistory(start);
        timeline.Append(Rate / 10, new QueueSnapshot(new PlayingTrack(first, Rate / 10, Deck.A), null));
        timeline.Append(Rate / 10, new QueueSnapshot(new PlayingTrack(second, Rate / 10, Deck.B), null));
        Require(timeline.Read().Current?.Track == second,
            "A backend without a playback counter must use the last rendered state.");
        timeline.ReportOutputPosition(0);
        Require(timeline.Read().Current?.Position == 0 && timeline.PresentedFrame == 0,
            "The playhead moved before the output began playing.");
        timeline.ReportOutputPosition(Rate / 10);
        Require(timeline.Read().Current?.Track == first && timeline.PresentedFrame == Rate / 10,
            "The deck jumped to a successor still buffered by the device.");
        timeline.ReportOutputPosition(Rate / 5);
        Require(timeline.Read().Current?.Track == second,
            "The deck failed to advance after the output actually reached the next track.");
        timeline.SetDelay(Rate / 10);
        Require(timeline.Read().Current?.Track == first && timeline.PresentedFrame == Rate / 10,
            "Optional Bluetooth latency compensation did not delay the visual timeline.");
        timeline.SetDelay(0);
        Require(timeline.Read().Current?.Track == second,
            "Resetting visual compensation did not restore the latest audible deck.");
        var wrapped = new AudioPresentationHistory(start);
        for (int block = 1; block <= 750; block++)
            wrapped.Append(100, new QueueSnapshot(new PlayingTrack(first, block * 100, Deck.A), null));
        foreach (long frame in new long[] { 0, 23900, 24001, 36099, 70000, 74999, 75000, 80000 })
        {
            wrapped.ReportOutputPosition(frame);
            long expected = Math.Clamp(frame / 100 * 100, 23900, 75000);
            Require(wrapped.Read().Current?.Position == expected,
                "The wrapped presentation ring did not return the preceding snapshot.");
        }
        Console.WriteLine("OK: device-head synchronized deck states, wrapped history and optional Bluetooth latency compensation.");
    }

    private static async Task CheckAudibleQueueSnapshot(CachedTrack first, CachedTrack second)
    {
        using var session = new QueuePlaybackSession();
        session.SetFirst(first);
        session.SetAutoMix(false);
        session.SetCrossfadeDuration(0);
        Require(session.SetNext(second), "Cannot queue a track for presentation test.");
        var block = new float[BlockSize * PlaybackFormat.Channels];
        await session.RunAsync(new InMemoryOutput(source =>
        {
            source.ReportOutputPosition(0);
            Require(source.Render(block) == BlockSize && session.Snapshot.Current?.Position == BlockSize,
                "Playback did not render ahead of the hardware clock.");
            Require(session.PresentedSnapshot.Current?.Position == 0,
                "Rendered PCM advanced the deck before the speaker consumed it.");
            source.ReportOutputPosition(BlockSize);
            Require(session.PresentedSnapshot.Current?.Position == BlockSize,
                "Presented deck did not follow the native playback head.");
            session.SetVisualDelay((int)Math.Round(BlockSize * 1000d / Rate));
            Require(session.PresentedSnapshot.Current?.Position == 0,
                "Queue presentation did not follow the changed latency setting.");
            session.SetVisualDelay(0);
            Require(session.RequestMix() && source.Render(block) == BlockSize && session.Snapshot.Current?.Track == second,
                "Audio did not complete the immediate track transition.");
            Require(session.PresentedSnapshot.Current?.Track == first,
                "Visual presentation changed tracks while the successor was only buffered.");
            source.ReportOutputPosition(2 * BlockSize);
            Require(session.PresentedSnapshot.Current?.Track == second,
                "Visual deck did not change after the output reached the next block.");
        }), 0, CancellationToken.None);
    }

    private static void CheckShortTrack()
    {
        var output = new TransportFadeSource(new ShortSource(), startMilliseconds: 0);
        Span<float> block = stackalloc float[PlaybackFormat.Channels * 10];
        Require(output.RequestPause(300) && output.Read(block) == 4, "Could not render the short tail.");
        Require(block[0] == 1 && block[6] == 0 && output.Read(block) == 0, "The short tail did not fade to zero.");
    }

    private static async Task CheckStartupAndRate(CachedTrack first, CachedTrack second)
    {
        using var session = new QueuePlaybackSession();
        session.SetFirst(first);
        session.SetCrossfadeDuration(0);
        Require(session.TrySetSpeed(first, 1.25), "Could not set outgoing rate.");
        Require(session.SetNext(second), "Could not queue incoming track.");

        var block = new float[BlockSize * PlaybackFormat.Channels];
        int rendered = 0;
        await session.RunAsync(new InMemoryOutput(source =>
        {
            while (rendered < PauseFrames)
            {
                int frames = source.Render(block);
                Require(frames > 0, "Track ended during the startup envelope.");
                for (int frame = 0; frame < frames; frame++)
                {
                    float progress = Math.Min(1, (float)(rendered + frame) / (PauseFrames - 1));
                    float expected = HyperbolicFade.Gains(progress).Incoming / 4;
                    Require(Math.Abs(expected - block[frame * 2]) < .00001f, "Invalid startup sample.");
                }
                rendered += frames;
            }

            Require(session.Snapshot.Current?.Position == rendered * 5L / 4, "The outgoing cursor did not follow the faster rate.");
            Require(session.RequestMix(), "Could not start the immediate transition.");
            Require(source.Render(block) == BlockSize && block[1] == .25f, "The next track did not take over.");
            Require(session.Snapshot.Current is { Speed: 1, Position: BlockSize }, "The next track inherited the outgoing rate.");
            Require(!session.Snapshot.IsMixing && session.Seek(second, Rate) && session.TrySetSpeed(second, .925),
                "Seek and fine speed adjustments did not return after promotion.");
            Require(session.Snapshot.Current is { Speed: .925, Position: Rate }, "The promoted deck ignored the adjustment.");
        }), CancellationToken.None);
    }

    private static async Task CheckFeedSelection(CachedTrack first, CachedTrack second)
    {
        using var session = new QueuePlaybackSession();
        session.SetFirst(first);
        Require(session.SetNext(second), "Could not queue the next track.");
        session.SetQueueEnabled(false);
        Require(!session.RequestMix(), "Recommendations fell back to the queue.");
        Require(session.Seek(first, first.Waveform.TotalFrames - 1), "Could not seek to the end.");
        var block = new float[BlockSize * PlaybackFormat.Channels];
        await session.RunAsync(new InMemoryOutput(source =>
        {
            Require(source.Render(block) == 1 && source.Render(block) == 0, "The inactive queue advanced after the track ended.");
            Require(session.Snapshot.Current?.Track == first && session.Snapshot.Incoming is null, "Inactive queue replaced the track.");
        }), 0, CancellationToken.None);

        session.SetQueueEnabled(true);
        await session.RunAsync(new InMemoryOutput(source =>
        {
            Require(source.Render(block) == BlockSize && session.Snapshot.Current?.Track == second, "Re-enabling the queue did not restore the next track.");
        }), 0, CancellationToken.None);
    }

    /// <summary>Checks that moving the active row to the end clears its successor without seeking.</summary>
    private static async Task CheckReplaceSuccessor(CachedTrack first, CachedTrack second)
    {
        using var session = new QueuePlaybackSession();
        session.SetFirst(first);
        Require(session.SetNext(second, first), "Could not set a successor for the active track.");
        Require(session.Seek(first, first.Waveform.TotalFrames - 1), "Could not seek to the last frame.");
        var before = session.Snapshot.Current;
        Require(session.SetNext(null, first), "Could not clear the successor after moving the active row.");
        Require(session.Snapshot.Current == before && !session.RequestMix(), "Clearing the successor changed the cursor or left a pending mix.");
        var block = new float[BlockSize * PlaybackFormat.Channels];
        await session.RunAsync(new InMemoryOutput(source =>
        {
            Require(source.Render(block) == 1 && source.Render(block) == 0 && session.Snapshot.Current?.Track == first,
                "A cleared successor was still played.");
        }), 0, CancellationToken.None);

        Require(session.SetNext(second, first) && session.RequestMix(), "Moving the active row back could not restore the successor.");
        Require(!session.SetNext(null, first), "Changing the queue undid an accepted transition request.");
        await session.RunAsync(new InMemoryOutput(source =>
        {
            Require(source.Render(block) == BlockSize && session.Snapshot.Current?.Track == second,
                "The restored successor was not promoted.");
            Require(!session.SetNext(first, first), "A stale request changed the new track's successor.");
        }), 0, CancellationToken.None);
    }

    private static async Task CheckClearingPlaybackSuccessor(CachedTrack first, CachedTrack second)
    {
        using var session = new QueuePlaybackSession();
        session.SetFirst(first);
        Require(session.SetNext(second, first) && session.RequestMix(), "Could not request the pending mix.");
        var before = session.Snapshot.Current;
        Require(session.SetNext(null) && !session.Snapshot.IsMixing && session.Snapshot.Current == before,
            "Clearing the successor left a pending mix or moved the current deck.");

        Require(session.SetNext(second, first) && session.RequestMix(), "Could not start the second mix.");
        var block = new float[BlockSize * PlaybackFormat.Channels];
        await session.RunAsync(new InMemoryOutput(source =>
        {
            Require(source.Render(block) == BlockSize && session.Snapshot.Incoming?.Track == second,
                "The transition did not reach the audio output.");
            Require(!session.SetNext(null), "Clearing interrupted a transition already being rendered.");
            while (source.Render(block) > 0) { }
            Require(session.Snapshot is { IsMixing: false, Current.Track: var track } && track == second,
                "The committed transition did not finish after clearing.");
        }), 0, CancellationToken.None);
    }

    /// <summary>Verifies that repeat fills the block across the loop boundary and leaves manual mixing available.</summary>
    private static async Task CheckRepeatOne(CachedTrack first, CachedTrack second)
    {
        using var session = new QueuePlaybackSession();
        session.SetFirst(first);
        Require(session.SetNext(second), "Could not arm the next track.");
        session.SetRepeatOne(true);
        Require(session.Seek(first, first.Waveform.TotalFrames - 3), "Could not place the cursor before the loop boundary.");
        var block = new float[BlockSize * PlaybackFormat.Channels];
        await session.RunAsync(new InMemoryOutput(source =>
        {
            Require(source.Render(block) == BlockSize, "Repeat left a gap in the next audio block.");
            Require(session.Snapshot.Current is { Position: BlockSize - 3 } current && current.Track == first &&
                session.Snapshot.Incoming is null, "Repeat did not take precedence over automix.");
            for (int i = 0; i < block.Length; i += 2)
            {
                Require(block[i] == .25f && block[i + 1] == 0, "A successor or silence leaked into the repeat loop.");
            }
            session.SetCrossfadeDuration(0);
            Require(session.RequestMix() && source.Render(block) == BlockSize && session.Snapshot.Current?.Track == second,
                "Repeat prevented a manual track change.");
            session.SetRepeatOne(false);
            Require(session.Seek(second, second.Waveform.TotalFrames - 1) && source.Render(block) == 1 && source.Render(block) == 0,
                "Playback did not stop at the end after repeat was disabled.");
        }), 0, CancellationToken.None);
    }

    /// <summary>Confirms that the speaker output and FFT both receive the filtered master.</summary>
    private static async Task CheckMasterEqualizer(string directory)
    {
        const int band = 4;
        const int toneFrames = Rate;
        string path = Path.Combine(directory, "tone.pcm");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            for (int frame = 0; frame < toneFrames; frame++)
            {
                short sample = (short)(1024 * Math.Sin(2 * Math.PI * 1000 * frame / Rate));
                writer.Write(sample);
                writer.Write((short)0);
            }
        }
        var tone = new CachedTrack("tone", path, new TrackWaveform([], toneFrames, Rate));
        Require(EqualizerSettings.Frequencies.SequenceEqual([60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000]),
            "The master equalizer frequencies changed.");
        var flat = EqualizerSettings.Flat;
        var boost = flat.WithGain(band, 12);
        var cut = flat.WithGain(band, -12).WithEnabled(true);
        var dry = await Measure(boost);
        var boosted = await Measure(boost.WithEnabled(true));
        var cutResult = await Measure(cut);
        var raised = await Measure(flat.WithOutputGain(6).WithEnabled(true));
        var bypassed = await Measure(flat.WithOutputGain(12));
        Require(Math.Abs(boosted.Rms / dry.Rms - Math.Pow(10, 12d / 20)) < .05 &&
            Math.Abs(cutResult.Rms / dry.Rms - Math.Pow(10, -12d / 20)) < .015, "The selected band did not change the output by the requested gain.");
        Require(boosted.Spectrum > dry.Spectrum && dry.Spectrum > cutResult.Spectrum, "The master spectrum did not follow the filtered output.");
        Require(Math.Abs(raised.Rms / dry.Rms - Math.Pow(10, 6d / 20)) < .02 && raised.Spectrum > dry.Spectrum,
            "The overall gain did not reach both the audio output and the FFT.");
        Require(bypassed.Rms == dry.Rms, "The disabled equalizer still applied overall gain to PCM.");
        Require(bypassed.Spectrum == dry.Spectrum,
            $"The disabled equalizer changed the spectrum for the same PCM window: dry={dry.Spectrum:R}, bypass={bypassed.Spectrum:R}.");

        async Task<(double Rms, float Spectrum)> Measure(EqualizerSettings settings)
        {
            using var session = new QueuePlaybackSession();
            session.SetFirst(tone);
            session.SetEqualizer(settings);
            var block = new float[BlockSize * PlaybackFormat.Channels];
            var levels = new float[PlaybackFormat.SpectrumBars];
            double rms = 0;
            await session.RunAsync(new InMemoryOutput(source =>
            {
                // The asynchronous spectrum worker publishes only the newest captured
                // window. Rendering the entire track in a tight loop makes different
                // measurements observe different FFT windows, including trailing silence.
                // Wait for each hop after the initial 2048-frame window. Stop well
                // before EOF so every profile is measured on the same full-tone block.
                long version = 0;
                const int blocks = 40;
                for (int i = 0; i < blocks; i++)
                {
                    Require(source.Render(block) == BlockSize, "The equalizer test tone ended prematurely.");
                    double sum = 0;
                    for (int sample = 0; sample < block.Length; sample += 2)
                    {
                        Require(block[sample + 1] == 0 && float.IsFinite(block[sample]), "The stereo filter linked the channels or produced non-finite output.");
                        sum += block[sample] * block[sample];
                    }
                    rms = Math.Sqrt(sum / BlockSize);
                    if (i >= 1)
                    {
                        Require(SpinWait.SpinUntil(() => session.Spectrum!.TryReadSpectrum(levels, ref version),
                            TimeSpan.FromSeconds(5)), $"No master spectrum snapshot for tone block {i + 1}.");
                    }
                }
            }), 0, CancellationToken.None);
            return (rms, levels.Max());
        }
    }

    /// <summary>An output error must not leave a published mixer after the worker exits.</summary>
    private static async Task CheckPlaybackSessionFailureCleanup(CachedTrack track)
    {
        var session = new PlaybackSession(new InMemoryOutput(_ =>
            throw new IOException("Synthetic output failure")), new DeckVolumes(), track, 0, null, 0);
        try
        {
            await session.RunAsync(CancellationToken.None);
            throw new InvalidOperationException("Expected output failure was swallowed.");
        }
        catch (IOException ex) when (ex.Message == "Synthetic output failure")
        {
            Require(session.Spectrum is null, "A failed playback run retained a stale mixer.");
        }
    }
    private static async Task CheckConcurrentQueueControl(CachedTrack track)
    {
        using var queue = new QueueSource();
        queue.SetFirst(track);
        queue.SetRepeatOne(true);
        using var start = new ManualResetEventSlim(false);
        var render = Task.Run(() =>
        {
            var block = new float[BlockSize * PlaybackFormat.Channels];
            start.Wait();
            for (int i = 0; i < 256; i++)
            {
                Require(queue.Read(block) == BlockSize, "Concurrent transport interrupted rendering.");
                for (int sample = 0; sample < block.Length; sample += 2)
                    Require(Math.Abs(block[sample] - .25f) < .0001f && Math.Abs(block[sample + 1]) < .0001f,
                        "Concurrent transport corrupted PCM.");
                Thread.Yield();
            }
        });
        var control = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 128; i++)
            {
                Require(queue.TrySeek(track, i * 37L), "Concurrent seek was lost.");
                Require(queue.TrySetSpeed(track, i % 2 == 0 ? .95 : 1.05), "Concurrent speed edit was lost.");
                Require(queue.Snapshot.Current?.Track == track, "Concurrent control changed the current deck.");
                Thread.Yield();
            }
        });
        start.Set();
        await Task.WhenAll(render, control).WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("OK: concurrent queue rendering and transport control.");
    }
}
