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
    Console.WriteLine("OK: pause envelope, exact cursor on resume, paused crossfade, and end-of-track fade.");
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
    }

    var block = new float[BlockSize * PlaybackFormat.Channels];
    int rendered = 0;
    await session.RunAsync(new InMemoryOutput(source =>
    {
        Require(source.Render(block) == BlockSize, "Initial PCM block is missing.");
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
    }), CancellationToken.None);

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
        Require(block[0] > 0, "The resume produced silence.");
    }), CancellationToken.None);
    Require(session.Snapshot.Current?.Position == pausedAt + BlockSize, "Resume did not continue from the saved cursor.");
    if (duringMix)
    {
        Require(session.Snapshot.Incoming?.Position == pausedAt + BlockSize, "The paused crossfade did not resume.");
    }
}

static void CheckShortTrack()
{
    var output = new PauseFadeSource(new ShortSource());
    Span<float> block = stackalloc float[PlaybackFormat.Channels * 10];
    Require(output.RequestPause() && output.Read(block) == 4, "Could not render the short tail.");
    Require(block[0] == 1 && block[6] == 0 && output.Read(block) == 0, "The short tail did not fade to zero.");
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
