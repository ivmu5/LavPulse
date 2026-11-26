using Laveyana.Audio;
using Laveyana.Audio.Playback;

internal static partial class Program
{
    // Fixed seed and bounded iterations: failures are reproducible without a device.
    private static async Task CheckStressAsync(string directory, CachedTrack first, CachedTrack second)
    {
        CheckMalformedInputs(directory, first);
        CheckRandomizedTransport(first, second);
        await CheckRepeatedOutputRunsAsync(first);
        Console.WriteLine("OK: malformed PCM/profile inputs, 512 deterministic transport edits and repeated output lifecycle.");
    }

    private static void CheckMalformedInputs(string directory, CachedTrack track)
    {
        using var queue = new QueueSource();
        queue.SetFirst(track);
        Require(queue.TrySeek(track, 123), "Could not publish test seek.");
        try { queue.Read(new float[1]); throw new Exception("Partial queue frame accepted."); }
        catch (ArgumentException) { }
        Require(queue.Read([]) == 0 && queue.Snapshot.Current?.Position == 123,
            "An empty/invalid read changed the queue position.");
        var levels = new DeckVolumes();
        try { levels.Set((Deck)99, .1f); throw new Exception("Unknown deck accepted."); }
        catch (ArgumentOutOfRangeException) { }
        Require(levels.Get(Deck.B) == .5f,"Invalid deck edited deck B.");
        var mixer = new PcmMixer([new PcmInput(new ContinuousPcm())]);
        try { mixer.Render(new float[3]); throw new Exception("Partial mixer frame accepted."); }
        catch (ArgumentException) { }

        string path = Path.Combine(directory,"truncated-stress.pcm");
        File.WriteAllBytes(path,[0,0,0,0]);
        using (var source = new CachedPcmSource(path,new DeckCursor(2,0)))
        {
            Require(source.Read(new float[2]) == 1,"First valid cache frame was lost.");
            try { source.Read(new float[2]); throw new Exception("Truncated PCM silently accepted."); }
            catch (EndOfStreamException) { }
            Require(source.LastReadFrames == 0,"Failed cache read retained stale frame count.");
            source.Dispose();
            try { source.Read(new float[2]); throw new Exception("Disposed PCM reader accepted a read."); }
            catch (ObjectDisposedException) { }
        }
        var invalid = new TrackMusicProfile { LocalKeys = null!, Energy = null! };
        Require(!invalid.TryGetLocalKey(0,out _) && invalid.EnergyAt(0) is null && !invalid.IsValid(100),
            "Malformed optional profile produced valid evidence.");
        invalid = new TrackMusicProfile { LocalKeys = [null!], Energy = [null!] };
        Require(!invalid.TryGetLocalKey(0,out _) && invalid.EnergyAt(0) is null,
            "Null profile records reached the player.");
        var ordered = new TrackMusicProfile { Energy = [new(0,10,.5f,.5f,1),new(10,20,.5f,.5f,1)] };
        Require(ordered.IsValid(20) && !ordered.IsValid(-1) &&
            !(ordered with { Energy = [new(0,10,.5f,.5f,1),new(9,20,.5f,.5f,1)] }).IsValid(20),
            "Single-pass validation accepted overlapping or negative timelines.");
        using var cancelled = new CancellationTokenSource();
        try { TrackWaveform.Analyze(new CancelAtEof(cancelled),cancelled.Token); throw new Exception("Cancelled EOF accepted."); }
        catch (OperationCanceledException) { }
    }

    private sealed class CancelAtEof(CancellationTokenSource stop) : IPcmSource
    {
        public int SampleRate => PlaybackFormat.SampleRate;
        public int Read(Span<float> destination) { stop.Cancel(); return 0; }
    }

    private static void CheckRandomizedTransport(CachedTrack first, CachedTrack second)
    {
        using var queue = new QueueSource();
        queue.SetFirst(first);
        queue.SetAutoMix(false);
        queue.SetRepeatOne(true);
        var random = new Random(81290);
        for (int i=0;i<512;i++)
        {
            var current = queue.Snapshot.Current!.Value.Track;
            if (i%16==0)
            {
                var next = ReferenceEquals(current,first) ? second : first;
                queue.SetCrossfadeDuration(0);
                Require(queue.SetNext(next,current) && queue.RequestMix(),"Stress successor could not be armed.");
                var block = new float[2];
                Require(queue.Read(block)==1 && ReferenceEquals(queue.Snapshot.Current?.Track,next),
                    "Zero-duration stress transition did not promote the successor.");
                current=next;
            }
            long target=random.NextInt64(0,current.Waveform.TotalFrames+1);
            Require(queue.TrySeek(current,target) && queue.TrySetSpeed(current,.75+random.Next(51)/100d),
                "Stress transport rejected an idle deck.");
            int count=random.Next(1,PlaybackFormat.BlockFrames*2);
            var pcm=new float[count*PlaybackFormat.Channels];
            Require(queue.Read(pcm)==count && pcm.All(float.IsFinite) &&
                pcm.All(sample=>sample>=-1 && sample<=1), "Repeat/seek stress produced invalid audio.");
            var snapshot=queue.Snapshot.Current!.Value;
            Require(ReferenceEquals(snapshot.Track,current) && snapshot.Position>=0 &&
                snapshot.Position<=current.Waveform.TotalFrames,"Stress cursor escaped its track.");
        }
    }

    private static async Task CheckRepeatedOutputRunsAsync(CachedTrack track)
    {
        using var session = new QueuePlaybackSession();
        session.SetFirst(track);
        session.SetRepeatOne(true);
        session.SetSpectrumEnabled(false);
        for(int i=0;i<16;i++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run=session.RunAsync(new WaitingOutput(entered,release),0,timeout.Token);
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                try { session.Dispose(); throw new Exception("Active session disposal accepted."); }
                catch (InvalidOperationException) { }
                try { await session.RunAsync(new InMemoryOutput(_=>{}),0,timeout.Token);
                    throw new Exception("Concurrent output run accepted."); }
                catch(InvalidOperationException) { }
                long version=0;
                try { session.Spectrum!.TryReadSpectrum(new float[1],ref version);
                    throw new Exception("Short spectrum buffer accepted."); }
                catch(ArgumentException) { }
            }
            finally
            {
                release.TrySetResult();
                await run.WaitAsync(timeout.Token);
            }
            Require(session.Spectrum is null,"Restart retained a stale mixer.");
        }
        session.Dispose();
        session.Dispose();
        try { await session.RunAsync(new InMemoryOutput(_=>{}),0,default);
            throw new Exception("Disposed session restarted."); }
        catch (ObjectDisposedException) { }
    }

    private sealed class WaitingOutput(TaskCompletionSource entered,TaskCompletionSource release) : IAudioOutput
    {
        public async Task PlayAsync(IAudioRenderSource source,CancellationToken token)
        {
            var block = new float[PlaybackFormat.BlockFrames*PlaybackFormat.Channels];
            Require(source.Render(block)==PlaybackFormat.BlockFrames,"Restart lost PCM.");
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        }
    }
}
