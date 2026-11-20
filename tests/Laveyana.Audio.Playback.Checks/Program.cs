using Laveyana.Audio;
using Laveyana.Audio.Playback;
using Laveyana.Audio.Maui.Tracks;

const int Rate = PlaybackFormat.SampleRate;
const int PauseFrames = Rate * 300 / 1000;
const int BlockSize = PlaybackFormat.BlockFrames;
string directory = Directory.CreateTempSubdirectory("lavpulse-checks-").FullName;
try
{
    CheckQueuePreservesOrderOnDirectSelection();
    await CheckQueueAnalysisLookahead();
    CheckMp3AudioInfo();
    CheckSpectrumCaptureToggle();
    Console.WriteLine("OK: MP3 stream properties.");
    CachedTrack first = MakeTrack(directory, "first", left: 8192, right: 0);
    CachedTrack second = MakeTrack(directory, "second", left: 0, right: 8192);
    await CheckPlaybackSessionFailureCleanup(first);
    CheckMp3ArtworkSelection();
    CheckSingleInputMixer();
    CheckPcm16Conversion();
    Console.WriteLine("OK: ID3 artwork and PCM conversion and mixing.");
    await CheckPauseAndResume(first, second, duringMix: false);
    await CheckPauseAndResume(first, second, duringMix: true);
    CheckShortTrack();
    await CheckStartupAndRate(first, second);
    await CheckFeedSelection(first, second);
    await CheckReplaceSuccessor(first, second);
    await CheckRepeatOne(first, second);
    await CheckMasterEqualizer(directory);
    Console.WriteLine("OK: transport envelopes, speed, queue transitions, repeat one, and filtered master output and spectrum.");
}
finally
{
    Directory.Delete(directory, recursive: true);
}

static async Task CheckQueueAnalysisLookahead()
{
    var files = Enumerable.Range(0, 15).Select(i => TrackFile.FromPath($"queue-{i}.mp3")).ToArray();
    var queue = new PlaybackQueue();
    queue.AddRange(files);
    queue.SetCurrent(files[0]);
    Require(queue.GetUpcoming(10).SequenceEqual(files[1..11]), "Lookahead must start after the current track.");

    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var prepared = new System.Collections.Concurrent.ConcurrentDictionary<TrackFile, bool>();
    var order = new List<TrackFile>();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var analysis = new QueueTrackAnalysis(prepared.ContainsKey, async file =>
    {
        order.Add(file);
        if (file == files[1])
        {
            started.SetResult();
            await release.Task.WaitAsync(stop.Token);
        }
        if (file == files[3]) throw new InvalidDataException("Damaged MP3.");
        prepared[file] = true;
    }, () => { }, stop.Token);

    var first = analysis.Update(queue.GetUpcoming(10), allowed: true)!;
    await started.Task.WaitAsync(stop.Token);
    Require(queue.TryRemove(files[2], _ => true), "Could not remove a pending track.");
    Require(analysis.Update(queue.GetUpcoming(10), allowed: false) is null, "A second worker started.");
    release.SetResult();
    await first.WaitAsync(stop.Token);
    analysis.Finish();
    Require(order.SequenceEqual(files[1..2]), "A new decode started after the background execution window expired.");

    var resumed = analysis.Update(queue.GetUpcoming(10), allowed: true)!;
    await resumed.WaitAsync(stop.Token);
    analysis.Finish();
    Require(order.SequenceEqual(new[] { files[1] }.Concat(files[3..12])),
        "Lookahead ignored a removal, skipped viable tracks after an error, or exceeded ten positions.");
    Require(analysis.Update(queue.GetUpcoming(10), allowed: true) is null, "Prepared or failed tracks were scheduled again.");

    queue.SetCurrent(files[3]);
    Require(queue.TryRemove(files[3], _ => true), "Could not remove the playing row.");
    Require(queue.GetUpcoming(10).SequenceEqual(files[4..14]), "Lookahead lost the detached playback boundary.");
    await analysis.Update(queue.GetUpcoming(10), allowed: true)!.WaitAsync(stop.Token);
    analysis.Finish();
    Require(order.TakeLast(2).SequenceEqual(files[12..14]) && !prepared.ContainsKey(files[14]),
        "Advancing the queue did not refill only the new lookahead positions.");
    Console.WriteLine("OK: rolling queue analysis, background pause and resume, and failed-track isolation.");
}

/// <summary>Switching directly to any track changes the history boundary, not the playlist order.</summary>
static void CheckQueuePreservesOrderOnDirectSelection()
{
    var files = Enumerable.Range(0, 6).Select(i =>
        TrackFile.FromPath(Path.Combine("unused", $"queue-{i}.mp3"))).ToArray();
    var queue = new PlaybackQueue();
    queue.AddRange(files);
    queue.SetCurrent(files[0]);
    Require(queue.Next == files[1], "The initial successor is incorrect.");

    // Simulate clicking a later row and completing the transition.
    queue.SetCurrent(files[4]);
    Require(queue.Tracks.SequenceEqual(files), "Selecting a later track reordered the queue.");
    Require(queue.PlayedCount == 4 && queue.Next == files[5], "History boundary moved incorrectly.");

    // Clicking a gray row plays it again, leaving every later row upcoming.
    queue.SetCurrent(files[1]);
    Require(queue.Tracks.SequenceEqual(files), "Replaying history reordered the queue.");
    Require(queue.PlayedCount == 1 && queue.Previous == files[0] && queue.Next == files[2],
        "Replaying history did not restore the proper gray/upcoming boundary.");
}

/// <summary>Checks that disabling FFT preserves playback timing and discards pre-toggle windows.</summary>
static void CheckSpectrumCaptureToggle()
{
    var source = new ContinuousPcm();
    var mixer = new PcmMixer([new PcmInput(source)], 16, 2048);
    var block = new float[PlaybackFormat.BlockFrames * PlaybackFormat.Channels];
    var bands = new float[mixer.Spectrum.Bands.Count];
    mixer.Render(block);
    mixer.Render(block);
    Require(mixer.Spectrum.TryReadLevels(bands, out long first) && first == 2 * PlaybackFormat.BlockFrames,
        "No initial spectrum window.");

    // Leave one pending snapshot to ensure it cannot appear when visualization is restored.
    mixer.Render(block);
    mixer.Spectrum.SetEnabled(false);
    mixer.Render(block);
    mixer.Render(block);
    Require(!mixer.Spectrum.TryReadLevels(bands, out _) && block[0] == .25f,
        "Hidden spectrum altered audio or emitted a window.");

    mixer.Spectrum.SetEnabled(true);
    mixer.Render(block);
    Require(!mixer.Spectrum.TryReadLevels(bands, out _), "Re-enabled spectrum reused stale PCM.");
    mixer.Render(block);
    Require(mixer.Spectrum.TryReadLevels(bands, out long resumed) && resumed == 7 * PlaybackFormat.BlockFrames,
        "Spectrum did not resume on the absolute render clock.");
}

/// <summary>Verifies bitrate and mode are read from the audio frames even with misleading tag or seek-table bytes.</summary>
static void CheckMp3AudioInfo()
{
    using var source = new MemoryStream();
    // The tag contains two complete-looking 128 kbps Stereo frames.
    source.Write("ID3"u8);
    source.Write([4, 0, 0x10, 0, 0, 6, 66]);
    source.Write(CreateFrame(417, 0xfb, 0x90, 0x00));
    source.Write(CreateFrame(417, 0xfb, 0x90, 0x00));
    source.Write(new byte[10]); // ID3v2.4 footer.

    byte[] seekFrame = CreateFrame(626, 0xfb, 0xb0, 0x00);
    "Xing"u8.CopyTo(seekFrame.AsSpan(36));
    source.Write(seekFrame);
    source.Write(CreateFrame(417, 0xfb, 0x90, 0x40)); // 128 kbps Joint Stereo.
    source.Write(CreateFrame(731, 0xfb, 0xc0, 0x40)); // 224 kbps Joint Stereo.
    var vbr = Mp3AudioInfoReader.Read(source, CancellationToken.None);
    Require(vbr == new TrackAudioInfo("MP3", 44100, 176, true, TrackChannelMode.JointStereo), "Invalid VBR audio properties.");

    source.SetLength(0);
    source.Write(CreateFrame(144, 0xe3, 0x28, 0xc0));
    source.Write(CreateFrame(144, 0xe3, 0x28, 0xc0));
    var mono = Mp3AudioInfoReader.Read(source, CancellationToken.None);
    Require(mono == new TrackAudioInfo("MP3", 8000, 16, false, TrackChannelMode.Mono), "Invalid low-rate mono audio properties.");

    source.SetLength(0);
    source.Write(CreateFrame(417, 0xfb, 0x00, 0x00));
    source.Write(CreateFrame(418, 0xfb, 0x02, 0x00));
    source.Write(CreateFrame(417, 0xfb, 0x00, 0x00));
    var freeFormat = Mp3AudioInfoReader.Read(source, CancellationToken.None);
    Require(freeFormat == new TrackAudioInfo("MP3", 44100, 128, false, TrackChannelMode.Stereo), "Invalid free-format MP3 properties.");

    static byte[] CreateFrame(int length, byte versionLayer, byte rate, byte channel)
    {
        var bytes = new byte[length];
        bytes[0] = 0xff;
        bytes[1] = versionLayer;
        bytes[2] = rate;
        bytes[3] = channel;
        return bytes;
    }
}

static void CheckMp3ArtworkSelection()
{
    foreach (byte version in new byte[] { 3, 4 })
    {
        using var body = new MemoryStream();
        WriteFrame("TIT2"u8, [1, 0xfe, 0xff, 0, (byte)'A']);
        WriteFrame("APIC"u8, Picture(0, 11));
        WriteFrame("APIC"u8, Picture(3, 22));
        WriteFrame("APIC"u8, Picture(0, 33));
        WriteFrame("APIC"u8, Picture(3, 44));

        using var stream = new MemoryStream();
        stream.Write("ID3"u8);
        stream.Write([version, 0, 0, 0, 0, (byte)(body.Length >> 7), (byte)(body.Length & 127)]);
        body.WriteTo(stream);
        stream.Position = 0;
        var tags = Mp3Tags.Read(stream);
        Require(tags.Title == "A" && tags.Artwork is { Length: 4 } image && image[^1] == 44,
            "ID3 must keep the final valid front cover and decode big-endian text.");
        stream.SetLength(stream.Length - 1);
        stream.Position = 0;
        Require(Mp3Tags.Read(stream) == new Mp3Tags(), "A truncated tag published partial metadata.");

        static byte[] Picture(byte type, byte marker) =>
            [0, .. "image/jpeg"u8, 0, type, 0, 0xff, 0xd8, 0xff, marker];

        void WriteFrame(ReadOnlySpan<byte> name, ReadOnlySpan<byte> data)
        {
            body.Write(name);
            // These frame sizes fit in a single byte for both ID3 size encodings.
            body.Write([0, 0, 0, (byte)data.Length, 0, 0]);
            body.Write(data);
        }
    }
}

static void CheckSingleInputMixer()
{
    var input = new PcmInput(new ShortSource(2f));
    var mixer = new PcmMixer([input]);
    var output = new float[8 * PlaybackFormat.Channels];
    Array.Fill(output, 2f);
    mixer.Render(output);
    Require(output.AsSpan(0, 8).IndexOfAnyExcept(1f) < 0 &&
        output.AsSpan(8).IndexOfAnyExcept(0f) < 0, "A short read did not clamp the signal and clear the tail.");
    mixer.Render(output);
    Require(output.AsSpan().IndexOfAnyExcept(0f) < 0, "An exhausted source must fill the output with silence.");

    var continuous = new PcmInput(new ContinuousPcm(), .5f);
    var attenuated = new PcmMixer([continuous]);
    attenuated.Render(output);
    Require(output.AsSpan().IndexOfAnyExcept(.125f) < 0, "The direct mixer path ignored the input gain.");
    continuous.Gain = 1f;
    attenuated.Render(output);
    Require(output.AsSpan().IndexOfAnyExcept(.25f) < 0, "The direct mixer path ignored a new gain.");

    var summed = new PcmMixer([new PcmInput(new ShortSource()), new PcmInput(new ContinuousPcm())]);
    summed.Render(output);
    Require(output.AsSpan(0, 8).IndexOfAnyExcept(1f) < 0 &&
        output.AsSpan(8).IndexOfAnyExcept(.25f) < 0, "Multi-input gain, clipping or tail handling changed.");
}

static void CheckPcm16Conversion()
{
    // Cover all signed values and a non-vector-sized tail.
    var pcm = new short[ushort.MaxValue + 4];
    for (int i = 0; i < pcm.Length; i++) pcm[i] = unchecked((short)(i + short.MinValue));
    var samples = new float[pcm.Length + 1];
    Array.Fill(samples, 2f);
    Pcm16.Decode(pcm, samples);
    for (int i = 0; i < pcm.Length; i++)
    {
        Require(samples[i] == pcm[i] / 32768f, "PCM16 conversion changed a sample.");
    }
    Require(samples[^1] == 2f, "PCM16 conversion wrote past the decoded samples.");
}

/// <summary>Checks the audible buffer and verifies that neither deck was reset or read past the fade.</summary>
static async Task CheckPauseAndResume(CachedTrack first, CachedTrack second, bool duringMix)
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

static void CheckShortTrack()
{
    var output = new TransportFadeSource(new ShortSource(), startMilliseconds: 0);
    Span<float> block = stackalloc float[PlaybackFormat.Channels * 10];
    Require(output.RequestPause(300) && output.Read(block) == 4, "Could not render the short tail.");
    Require(block[0] == 1 && block[6] == 0 && output.Read(block) == 0, "The short tail did not fade to zero.");
}

static async Task CheckStartupAndRate(CachedTrack first, CachedTrack second)
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

static async Task CheckFeedSelection(CachedTrack first, CachedTrack second)
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
static async Task CheckReplaceSuccessor(CachedTrack first, CachedTrack second)
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
static async Task CheckRepeatOne(CachedTrack first, CachedTrack second)
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
static async Task CheckMasterEqualizer(string directory)
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
static async Task CheckPlaybackSessionFailureCleanup(CachedTrack track)
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

static CachedTrack MakeTrack(string directory, string name, short left, short right)
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

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class InMemoryOutput(Action<IAudioRenderSource> render) : IAudioOutput
{
    public Task PlayAsync(IAudioRenderSource source, CancellationToken cancellationToken)
    {
        render(source);
        return Task.CompletedTask;
    }
}

sealed class ShortSource(float sample = 1f) : IPcmSource
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

file sealed class ContinuousPcm : IPcmSource
{
    public int SampleRate => PlaybackFormat.SampleRate;

    public int Read(Span<float> destination)
    {
        destination.Fill(.25f);
        return destination.Length / PlaybackFormat.Channels;
    }
}
