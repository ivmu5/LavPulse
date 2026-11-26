using Laveyana.Audio.Playback;
using System.Runtime.InteropServices;
using NAudio;
using NAudio.Wave;

namespace Laveyana.LavPulse.AudioOutput;

internal sealed class PlatformAudioOutput : IAudioOutput
{
    /// <summary>Starts the selected Windows output path through WaveOut and waits for completion.</summary>
    public async Task PlayAsync(IAudioRenderSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        using var device = new WaveOutEvent
        {
            DeviceNumber = -1,
            // Give the audio writer enough queued PCM to survive Windows UI and COM spikes.
            DesiredLatency = 240,
            NumberOfBuffers = 4
        };

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is null)
            {
                ended.TrySetResult();
            }
            else
            {
                ended.TrySetException(args.Exception);
            }
        };

        device.Init(new MasterWaveProvider(source));
        source.ReportOutputPosition(0);
        device.Play();
        try
        {
            await FollowOutputPositionAsync(device, source, ended.Task, token).ConfigureAwait(false);
            await ended.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            device.Stop();
            // A broken driver may never raise PlaybackStopped. Do not wait
            // indefinitely and strand the queue/session during shutdown.
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Publishes the frames played by WaveOut without querying the device on the render thread.</summary>
    private static async Task FollowOutputPositionAsync(WaveOutEvent device, IAudioRenderSource source,
        Task ended, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        NativeOutputCounter counter = default;
        while (!ended.IsCompleted && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            if (ended.IsCompleted) break;
            try
            {
                uint position = unchecked((uint)device.GetPosition());
                source.ReportOutputPosition(counter.Observe(position) / PlaybackFormat.Pcm16FrameBytes);
            }
            catch (Exception ex) when (ex is MmException or NotSupportedException)
            {
                // A driver without a usable playback counter can still display analyzed frames.
                source.ReportOutputPosition(long.MaxValue);
                break;
            }
        }
    }

    private sealed class MasterWaveProvider(IAudioRenderSource source) : IWaveProvider
    {
        private readonly float[] _pcm = new float[PlaybackFormat.BlockFrames * PlaybackFormat.Channels];

        public WaveFormat WaveFormat { get; } = new(PlaybackFormat.SampleRate, 16, PlaybackFormat.Channels);

        /// <summary>Converts the requested part of the master to the PCM format expected by WaveOut.</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            Span<short> output = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(offset, count));
            int written = 0;
            while (output.Length - written >= PlaybackFormat.Channels)
            {
                int requested = Math.Min(_pcm.Length, output.Length - written);
                requested -= requested % PlaybackFormat.Channels;
                int frames = source.Render(_pcm.AsSpan(0, requested));
                if ((uint)frames > (uint)(requested / PlaybackFormat.Channels))
                    throw new InvalidDataException("Invalid audio render frame count.");
                int rendered = frames * PlaybackFormat.Channels;
                Pcm16.Encode(_pcm.AsSpan(0, rendered), output.Slice(written, rendered));

                written += rendered;
                if (rendered < requested)
                {
                    break;
                }
            }

            return written * sizeof(short);
        }
    }
}
