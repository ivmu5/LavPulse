using Laveyana.Audio.Playback;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Laveyana.Audio.Maui.AudioOutput;

internal sealed class PlatformAudioOutput : IAudioOutput
{
    /// <summary>Starts the selected Windows output path through WaveOut and waits for completion.</summary>
    public async Task PlayAsync(IAudioRenderSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        using var device = new WaveOutEvent
        {
            DeviceNumber = -1,
            DesiredLatency = 90,
            NumberOfBuffers = 3
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
        device.Play();
        try
        {
            await ended.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            device.Stop();
            await ended.Task.ConfigureAwait(false);
            throw;
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
                int rendered = source.Render(_pcm.AsSpan(0, requested)) * PlaybackFormat.Channels;
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
