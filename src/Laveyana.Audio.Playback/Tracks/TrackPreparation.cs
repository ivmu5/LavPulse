using System.Runtime.InteropServices;

namespace Laveyana.Audio.Playback;

/// <summary>Prepares seekable PCM and a display waveform from an MP3 stream.</summary>
public sealed class TrackPreparation(string cacheDirectory)
{
    /// <summary>Imports one track and removes temporary files if decoding fails or is cancelled.</summary>
    public async Task<CachedTrack> ImportAsync(TrackFile file, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!TrackFile.IsMp3(file.Name))
        {
            throw new InvalidDataException("Выбери файл с расширением .mp3.");
        }

        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(cacheDirectory);
        string prefix = Path.Combine(cacheDirectory, $"lavpulse-{Guid.NewGuid():N}");
        string mp3Path = prefix + ".mp3";
        string pcmPath = prefix + ".pcm";
        try
        {
            await using (var copy = new FileStream(mp3Path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await file.CopyToAsync(copy, token).ConfigureAwait(false);
            }

            CachedTrack track = await Task.Run(() =>
            {
                using var decoder = new Mp3PcmSource(mp3Path);
                using var pcm = new FileStream(pcmPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
                TrackWaveform waveform = TrackWaveform.Analyze(new RecordingSource(decoder, pcm), token);
                return new CachedTrack(file.Name, pcmPath, waveform, Mp3Tags.Read(mp3Path));
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return track;
        }
        catch
        {
            CachedTrack.DeleteCacheFile(pcmPath);
            throw;
        }
        finally
        {
            CachedTrack.DeleteCacheFile(mp3Path);
        }
    }

    /// <summary>Writes the same samples used by the waveform analysis to the PCM cache.</summary>
    private sealed class RecordingSource(Mp3PcmSource source, Stream output) : IPcmSource
    {
        private short[] _buffer = [];

        public int SampleRate => source.SampleRate;

        /// <summary>Records the decoded frames while passing them through to waveform analysis.</summary>
        public int Read(Span<float> destination)
        {
            int frames = source.Read(destination);
            int samples = frames * PcmMixer.ChannelCount;
            if (_buffer.Length < samples)
            {
                _buffer = new short[samples];
            }

            var pcm = _buffer.AsSpan(0, samples);
            Pcm16.Encode(destination[..samples], pcm);
            output.Write(MemoryMarshal.AsBytes(pcm));
            return frames;
        }
    }
}
