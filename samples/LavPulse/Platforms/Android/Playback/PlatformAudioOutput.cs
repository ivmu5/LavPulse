using Laveyana.Audio.Playback;
using System.Diagnostics;
using Android.Media;
using PcmEncoding = Android.Media.Encoding;

namespace Laveyana.LavPulse.AudioOutput;

internal sealed class PlatformAudioOutput : IAudioOutput
{
    /// <summary>Runs the blocking Android audio writer on a dedicated thread.</summary>
    public Task PlayAsync(IAudioRenderSource source, CancellationToken token) =>
        Task.Factory.StartNew(() => Play(source, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Creates and releases the native AudioTrack configured for stereo PCM.</summary>
    private static void Play(IAudioRenderSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        const int bytesPerBlock = PlaybackFormat.BlockFrames * PlaybackFormat.Pcm16FrameBytes;
        int minimum = AudioTrack.GetMinBufferSize(PlaybackFormat.SampleRate, ChannelOut.Stereo, PcmEncoding.Pcm16bit);
        if (minimum <= 0)
        {
            throw new IOException($"Android не смог открыть стереовыход 48 кГц (код {minimum}).");
        }

        int bufferBytes = Math.Max(minimum, bytesPerBlock * 4);
        using var attributeBuilder = new AudioAttributes.Builder();
        using var attributes = attributeBuilder.SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!.Build();
        using var formatBuilder = new AudioFormat.Builder();
        using var format = formatBuilder.SetSampleRate(PlaybackFormat.SampleRate)!
            .SetEncoding(PcmEncoding.Pcm16bit)!.SetChannelMask(ChannelOut.Stereo)!.Build();
        using var builder = new AudioTrack.Builder();
        using var track = builder.SetAudioAttributes(attributes!)!.SetAudioFormat(format!)!
            .SetTransferMode(AudioTrackMode.Stream)!
            .SetBufferSizeInBytes(bufferBytes)!.Build()
            ?? throw new IOException("Android не создал аудиовыход.");

        try
        {
            if (track.State != AudioTrackState.Initialized)
            {
                throw new IOException("Android не подготовил аудиовыход.");
            }

            PlayTrack(track, source, token);
        }
        finally
        {
            track.Release();
        }
    }

    /// <summary>Primes the output, streams mixed blocks and waits for the final frames to play.</summary>
    private static void PlayTrack(AudioTrack track, IAudioRenderSource source, CancellationToken token)
    {
        using var stop = token.Register(() =>
        {
            try
            {
                track.Pause();
            }
            catch (Java.Lang.IllegalStateException)
            {
                // The device may already have stopped.
            }
        });

        source.ReportOutputPosition(0);
        var block = new float[PlaybackFormat.BlockFrames * PlaybackFormat.Channels];
        var pcm = new short[block.Length];
        NativeOutputCounter head = default;
        long written = 0;
        long played = 0;
        bool started = false;
        int primeSamples = track.BufferSizeInFrames * PlaybackFormat.Channels;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            int frames = source.Render(block);
            if ((uint)frames > PlaybackFormat.BlockFrames)
                throw new InvalidDataException("Invalid audio render frame count.");
            if (frames == 0)
            {
                break;
            }

            int samples = frames * PlaybackFormat.Channels;
            Pcm16.Encode(block.AsSpan(0, samples), pcm);
            int offset = 0;
            if (!started)
            {
                int requested = Math.Min(samples, primeSamples);
                int accepted = requested == 0 ? 0 : track.Write(pcm, 0, requested, WriteMode.NonBlocking);
                if (accepted < 0)
                {
                    throw new IOException($"Ошибка аудиовыхода Android (код {accepted}).");
                }

                offset = accepted;
                primeSamples -= accepted;
                if (primeSamples == 0 || accepted < samples)
                {
                    token.ThrowIfCancellationRequested();
                    track.Play();
                    token.ThrowIfCancellationRequested();
                    started = true;
                }
            }

            while (offset < samples)
            {
                token.ThrowIfCancellationRequested();
                int accepted = track.Write(pcm, offset, samples - offset, WriteMode.Blocking);
                token.ThrowIfCancellationRequested();
                if (accepted <= 0)
                {
                    throw new IOException($"Аудиовыход Android не принял данные (код {accepted}).");
                }

                offset += accepted;
            }

            written += frames;
            if (started)
            {
                played = head.Observe(unchecked((uint)track.PlaybackHeadPosition), written);
                source.ReportOutputPosition(played);
            }
        }

        token.ThrowIfCancellationRequested();
        if (!started && written > 0)
        {
            track.Play();
            token.ThrowIfCancellationRequested();
        }

        var drain = Stopwatch.StartNew();
        while (played < written)
        {
            token.WaitHandle.WaitOne(10);
            token.ThrowIfCancellationRequested();
            long previous = played;
            played = head.Observe(unchecked((uint)track.PlaybackHeadPosition), written);
            if (played > previous) drain.Restart();
            source.ReportOutputPosition(played);
            if (drain.Elapsed > TimeSpan.FromSeconds(5) && played < written)
            {
                throw new IOException("Аудиовыход Android перестал продвигаться.");
            }
        }
    }

 }
