using Laveyana.Audio.Playback;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AVFoundation;

namespace Laveyana.LavPulse.AudioOutput;

internal sealed class PlatformAudioOutput : IAudioOutput
{
    private const int BufferCount = 3;

    /// <summary>Activates the Apple media session before starting output and releases it afterward.</summary>
    public async Task PlayAsync(IAudioRenderSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = AVAudioSession.SharedInstance();
        if (session.SetCategory(AVAudioSessionCategory.Playback) is { } categoryError)
        {
            throw new IOException(categoryError.LocalizedDescription);
        }

        if (!session.SetActive(true, out var activationError))
        {
            throw new IOException(activationError?.LocalizedDescription ?? "Не удалось активировать аудиосессию.");
        }

        try
        {
            await PlayActiveAsync(source, token).ConfigureAwait(false);
        }
        finally
        {
            session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out _);
        }
    }

    /// <summary>Streams the master through a pool of AVAudioEngine buffers.</summary>
    private static async Task PlayActiveAsync(IAudioRenderSource source, CancellationToken token)
    {
        using var engine = new AVAudioEngine();
        using var player = new AVAudioPlayerNode();
        using var format = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, PlaybackFormat.SampleRate,
            PlaybackFormat.Channels, interleaved: false);
        engine.AttachNode(player);
        engine.Connect(player, engine.MainMixerNode, format);

        var available = Channel.CreateUnbounded<AVAudioPcmBuffer>(new UnboundedChannelOptions { SingleReader = true });
        var buffers = new List<AVAudioPcmBuffer>(BufferCount);
        source.ReportOutputPosition(0);
        try
        {
            for (int i = 0; i < BufferCount; i++)
            {
                var buffer = new AVAudioPcmBuffer(format, PlaybackFormat.BlockFrames);
                buffers.Add(buffer);
                available.Writer.TryWrite(buffer);
            }

            if (!engine.StartAndReturnError(out var error))
            {
                throw new IOException(error?.LocalizedDescription ?? "Не удалось запустить аудиовыход.");
            }

            var block = new float[PlaybackFormat.BlockFrames * PlaybackFormat.Channels];
            var left = new float[PlaybackFormat.BlockFrames];
            var right = new float[PlaybackFormat.BlockFrames];
            int scheduled = 0;
            bool started = false;

            while (true)
            {
                AVAudioPcmBuffer buffer = await TakeBufferAsync(available.Reader, token).ConfigureAwait(false);
                if (started) ReportOutputPosition(player, source);
                int frames = source.Render(block);
                if ((uint)frames > PlaybackFormat.BlockFrames)
                    throw new InvalidDataException("Invalid audio render frame count.");
                IntPtr channels = buffer.FloatChannelData;
                if (frames == 0)
                {
                    if (scheduled > 0)
                    {
                        // Wait for the device to play a final silent frame before stopping the engine.
                        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        buffer.FrameLength = 1;
                        Marshal.WriteInt32(Marshal.ReadIntPtr(channels), 0);
                        Marshal.WriteInt32(Marshal.ReadIntPtr(channels, IntPtr.Size), 0);
                        player.ScheduleBuffer(buffer, AVAudioPlayerNodeCompletionCallbackType.PlayedBack,
                            _ => drained.TrySetResult());
                        if (!started) player.Play();
                        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                    }

                    break;
                }

                for (int i = 0; i < frames; i++)
                {
                    left[i] = block[i * 2];
                    right[i] = block[i * 2 + 1];
                }

                buffer.FrameLength = (uint)frames;
                Marshal.Copy(left, 0, Marshal.ReadIntPtr(channels), frames);
                Marshal.Copy(right, 0, Marshal.ReadIntPtr(channels, IntPtr.Size), frames);

                player.ScheduleBuffer(buffer, AVAudioPlayerNodeCompletionCallbackType.Rendered,
                    _ => available.Writer.TryWrite(buffer));
                if (!started && ++scheduled == BufferCount)
                {
                    player.Play();
                    started = true;
                }
            }
        }
        finally
        {
            try { player.Stop(); }
            finally
            {
                try { engine.Stop(); }
                finally
                {
                    available.Writer.TryComplete();
                    foreach (AVAudioPcmBuffer buffer in buffers)
                        buffer.Dispose();
                }
            }
        }
    }

    private static void ReportOutputPosition(AVAudioPlayerNode player, IAudioRenderSource source)
    {
        using var renderTime = player.LastRenderTime;
        if (renderTime is null) return;
        using var playerTime = player.GetPlayerTimeFromNodeTime(renderTime);
        if (playerTime is { SampleTimeValid: true, SampleRate: > 0 })
            source.ReportOutputPosition(Math.Max(0, (long)(playerTime.SampleTime * PlaybackFormat.SampleRate / playerTime.SampleRate)));
    }

    /// <summary>Waits for an available native buffer with cancellation and a device timeout.</summary>
    private static async ValueTask<AVAudioPcmBuffer> TakeBufferAsync(ChannelReader<AVAudioPcmBuffer> pool,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pool.TryRead(out var buffer))
        {
            return buffer;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await pool.ReadAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Apple audio output stopped returning buffers.");
        }
    }
}
