using System.Runtime.InteropServices;

namespace Laveyana.Audio.Playback;

public sealed partial class TrackPreparation
{
    private static bool CanAnalyzeInPlace(Stream source)
    {
        if (!source.CanSeek) return false;
        try
        {
            _ = source.Length;
            source.Position = 0;
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException)
        {
            return false;
        }
    }

    private CachedTrack Decode(Stream source, TrackFile file, string pcmPath, TrackMetadata? metadata,
        (TrackWaveform Waveform, TrackAudioInfo? Info)? analysis, CancellationToken token, bool parallelKey)
    {
        var decoded = DecodeWaveform(source, Path.GetExtension(file.Name), pcmPath, analysis?.Waveform, token, parallelKey: parallelKey);
        var waveform = decoded.Waveform;

        if (metadata is null)
        {
            source.Position = 0;
            metadata = ReadTags(file, source);
        }

        var audioInfo = TrackFile.IsMp3(file.Name)
            ? analysis?.Info ?? Mp3AudioInfoReader.Read(source, token) : decoded.Info ?? analysis?.Info;
        token.ThrowIfCancellationRequested();
        return new CachedTrack(file.Name, pcmPath, waveform, metadata, audioInfo);
    }

    private (TrackWaveform Waveform, TrackAudioInfo? Info) DecodeWaveform(Stream source,
        string extension, string? pcmPath, TrackWaveform? waveform, CancellationToken token, TrackRhythmTrace? trace = null, bool parallelKey = false)
    {
        extension = extension.ToLowerInvariant();
        bool mp3 = extension == ".mp3";
        bool vorbis = extension is ".ogg" or ".oga";
        long encodedBytes = source.Length;

        (TrackWaveform, TrackAudioInfo?) Record(IMp3SampleDecoder native, string backend)
        {
            using var decoder = new Mp3PcmSource(native);
            int rate = decoder.NativeRate, channels = decoder.NativeChannels;
            string codec = native.Codec ?? extension.TrimStart('.').ToUpperInvariant();
            int bits = native is WaveSampleDecoder wave ? wave.BitsPerSample : 0;
            var result = AnalyzeOrRecord(decoder, pcmPath, waveform, token, trace, parallelKey);
            if (trace is not null) trace.Decoder = backend;
            if (mp3) return (result, null);
            int bitrate = bits > 0 ? (int)((long)rate * channels * bits / 1000)
                : (int)Math.Clamp(Math.Round(encodedBytes * 8d * PlaybackFormat.SampleRate /
                    Math.Max(1, result.TotalFrames) / 1000), 0, int.MaxValue);
            return (result, new TrackAudioInfo(codec, rate, bitrate, vorbis || extension == ".flac",
                channels == 1 ? TrackChannelMode.Mono : TrackChannelMode.Stereo));
        }

        if (extension == ".wav")
            return Record(new WaveSampleDecoder(source), "RIFF/WAVE");

        Exception? platformError = null;
        if (_openPlatformDecoder is not null)
        {
            try
            {
                if (_openPlatformDecoder(source, token) is { } platformDecoder)
                    return Record(platformDecoder, "platform");
            }
            catch (InvalidDataException ex)
            {
                token.ThrowIfCancellationRequested();
                platformError = ex;
                System.Diagnostics.Debug.WriteLine(ex);
                if (pcmPath is not null) File.Delete(pcmPath);
            }
            source.Position = 0;
        }

        if (mp3)
        {
            using var managedDecoder = new Mp3PcmSource(source);
            var result = AnalyzeOrRecord(managedDecoder, pcmPath, waveform, token, trace, parallelKey);
            if (trace is not null) trace.Decoder = "NLayer";
            return (result, null);
        }
        if (vorbis)
            return Record(new VorbisSampleDecoder(source), "NVorbis");
        throw new InvalidDataException("Системный декодер не поддерживает этот аудиофайл.", platformError);
    }

    private static TrackWaveform AnalyzeOrRecord(Mp3PcmSource decoder, string? pcmPath, TrackWaveform? waveform,
        CancellationToken token, TrackRhythmTrace? trace = null, bool parallelKey = false)
    {
        if (pcmPath is null) return TrackWaveform.Analyze(decoder, token, trace, parallelKey);

        long frames = 0;
        using (var pcm = new FileStream(pcmPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
        {
            var recording = new RecordingSource(decoder, pcm);
            if (waveform is null) return TrackWaveform.Analyze(recording, token, trace, parallelKey);

            var buffer = new float[4096 * PlaybackFormat.Channels];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = recording.Read(buffer);
                if (read == 0) break;
                frames += read;
            }
        }
        token.ThrowIfCancellationRequested();
        if (frames == waveform.TotalFrames) return waveform;

        // A different decoder can produce a different count of padded frames for the same MP3.
        using var recorded = new CachedPcmSource(pcmPath, new DeckCursor(frames, 0));
        return TrackWaveform.Analyze(recorded, token, trace, parallelKey);
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
            if(frames==0)return 0;
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
