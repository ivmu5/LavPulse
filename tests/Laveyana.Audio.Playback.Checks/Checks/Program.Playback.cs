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
        Require(bypassed.Rms == dry.Rms && bypassed.Spectrum == dry.Spectrum, "The disabled equalizer still applied overall gain.");

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
                for (int i = 0; i < 50; i++)
                {
                    if (source.Render(block) != BlockSize) break;
                    double sum = 0;
                    for (int sample = 0; sample < block.Length; sample += 2)
                    {
                        Require(block[sample + 1] == 0 && float.IsFinite(block[sample]), "The stereo filter linked the channels or produced non-finite output.");
                        sum += block[sample] * block[sample];
                    }
                    rms = Math.Sqrt(sum / BlockSize);
                }
                long version = 0;
                Require(SpinWait.SpinUntil(() => session.Spectrum!.TryReadSpectrum(levels, ref version), TimeSpan.FromSeconds(2)),
                    "No master spectrum snapshot was published.");
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
}
