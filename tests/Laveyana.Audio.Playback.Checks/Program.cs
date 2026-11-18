using Laveyana.Audio.Playback;

const int Rate = PlaybackFormat.SampleRate;
const int PauseFrames = Rate * 300 / 1000;
const int BlockSize = PlaybackFormat.BlockFrames;
string directory = Directory.CreateTempSubdirectory("lavpulse-checks-").FullName;
try
{
    CachedTrack first = MakeTrack(directory, "first", left: 8192, right: 0);
    CachedTrack second = MakeTrack(directory, "second", left: 0, right: 8192);
    await CheckPauseAndResume(first, second, duringMix: false);
    await CheckPauseAndResume(first, second, duringMix: true);
    CheckShortTrack();
    await CheckStartupAndRate(first, second);
    await CheckFeedSelection(first, second);
    await CheckReplaceSuccessor(first, second);
    Console.WriteLine("OK: startup/pause envelopes, resume, per-track speed, mix seek lock, queue feed selection and successor replacement.");
}
finally
{
    Directory.Delete(directory, recursive: true);
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

sealed class ShortSource : Laveyana.Audio.IPcmSource
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
        destination[..8].Fill(1);
        return 4;
    }
}
