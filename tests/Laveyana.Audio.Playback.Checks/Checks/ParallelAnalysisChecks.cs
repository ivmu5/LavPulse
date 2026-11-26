using Laveyana.Audio;
using Laveyana.Audio.Playback;
using Laveyana.LavPulse.Tracks;

internal static class ParallelAnalysisChecks
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            TrackWaveform.Analyze(new ToneSource(sampleRate:44100), timeout.Token);
            throw new InvalidOperationException("A noncanonical PCM rate was accepted.");
        }
        catch (ArgumentException ex) when (ex.ParamName == "source") { }
        await Task.Run(() =>
        {
            var serial = TrackWaveform.Analyze(new ToneSource(), timeout.Token);
            var parallel = TrackWaveform.Analyze(new ToneSource(), timeout.Token, null, parallelKey: true);
            Require(serial.TotalFrames == parallel.TotalFrames &&
                serial.FramesPerPoint == parallel.FramesPerPoint && serial.Points.SequenceEqual(parallel.Points) &&
                serial.MusicalKey == parallel.MusicalKey &&
                serial.MusicProfile!.LocalKeys.SequenceEqual(parallel.MusicProfile!.LocalKeys) &&
                serial.MusicProfile.Energy.SequenceEqual(parallel.MusicProfile.Energy) &&
                serial.MusicProfile.Loudness!.IntegratedLufs == parallel.MusicProfile.Loudness!.IntegratedLufs &&
                serial.MusicProfile.Loudness.ShortTerm.SequenceEqual(parallel.MusicProfile.Loudness.ShortTerm) &&
                serial.MusicProfile.Loudness.SamplePeakDbfs == parallel.MusicProfile.Loudness.SamplePeakDbfs &&
                serial.MusicProfile.LocalKeys[^1].EndFrame == serial.TotalFrames &&
                serial.MusicProfile.LocalKeys[^1].Key is null &&
                serial.Rhythm!.BeatFrames.SequenceEqual(parallel.Rhythm!.BeatFrames) &&
                serial.Rhythm.PredictedBeatFrames.SequenceEqual(parallel.Rhythm.PredictedBeatFrames),
                "Parallel analysis changed ordered PCM evidence.");
            using var pipeline = new ParallelKeyAnalysis((_, _, _) => throw new InvalidDataException("worker fault"),
                () => null, timeout.Token);
            try
            {
                for (int i = 0; i < 32; i++) pipeline.Submit(new float[32]);
                pipeline.CompleteInput();
            }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
            try { pipeline.Join(); throw new InvalidOperationException("Worker fault was hidden."); }
            catch (InvalidDataException ex) when (ex.Message == "worker fault") { }
        }, timeout.Token).WaitAsync(timeout.Token);

        // Cancellation must release a producer blocked on a full buffer,
        // and disposal must wait for the consumer before returning pooled PCM.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var entered = new ManualResetEventSlim();
        using var full = new ManualResetEventSlim();
        using var pipeline2 = new ParallelKeyAnalysis((_, _, token) =>
        {
            entered.Set();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
        }, () => null, cancellation.Token);
        var producer = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 32; i++)
                {
                    pipeline2.Submit(new float[32]);
                    if (i == ParallelKeyAnalysis.Capacity) full.Set();
                }
                throw new InvalidOperationException("A full buffer ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)), "The key worker did not start.");
            Require(full.Wait(TimeSpan.FromSeconds(5)), "The bounded buffer did not fill.");
        }
        finally
        {
            cancellation.Cancel();
            await producer;
        }
        try { pipeline2.Join(); throw new InvalidOperationException("The consumer ignored cancellation."); }
        catch (OperationCanceledException) { }

        // Source failure must not strand the consumer waiting for more PCM.
        await Task.Run(() =>
        {
            try
            {
                TrackWaveform.Analyze(new ToneSource(fail: true), timeout.Token, null, parallelKey: true);
                throw new InvalidOperationException("Decoder fault was hidden.");
            }
            catch (IOException ex) when (ex.Message == "decoder fault") { }
        }).WaitAsync(timeout.Token);
        await CheckTrackGatesAsync(timeout.Token);
        Console.WriteLine("OK: two-lane analysis equivalence, bounded buffer, consumer and producer failures, cancellation and track identity.");
    }

    private static async Task CheckTrackGatesAsync(CancellationToken token)
    {
        var gates = new TrackOperationGates();
        var first = TrackFile.FromPath("same-track.mp3");
        var second = TrackFile.FromPath(Path.GetFullPath("same-track.mp3"));
        var other = TrackFile.FromPath("other-track.mp3");
        using (var lease = await gates.AcquireAsync(first, token))
        {
            using var independent = await gates.AcquireAsync(other, token);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            var waiting = gates.AcquireAsync(second, cancelled.Token).AsTask();
            Require(!waiting.IsCompleted, "Different TrackFile instances for the same path did not serialize.");
            cancelled.Cancel();
            try
            {
                using var unexpected = await waiting;
                throw new InvalidOperationException("Cancelled track-gate wait succeeded.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
        using (var next = await gates.AcquireAsync(second, token)) { }
        Require(gates.ActiveKeys == 0, "Track-gate entries leaked after cancellation or release.");
    }

    private sealed class ToneSource(bool fail = false, int sampleRate = 48000) : IPcmSource
    {
        private int _position;
        public int SampleRate => sampleRate;
        public int Read(Span<float> destination)
        {
            if (fail && _position >= 16384) throw new IOException("decoder fault");
            int frames = Math.Min(destination.Length / 2, 48000 * 6 - _position);
            for (int i = 0; i < frames; i++, _position++)
            {
                double t = _position / 48000d;
                float sample = (float)(.2 * Math.Sin(Math.Tau * 261.6256 * t) +
                    .15 * Math.Sin(Math.Tau * 329.6276 * t) + .15 * Math.Sin(Math.Tau * 391.9954 * t));
                destination[i * 2] = sample;
                destination[i * 2 + 1] = -sample;
            }
            return frames;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
