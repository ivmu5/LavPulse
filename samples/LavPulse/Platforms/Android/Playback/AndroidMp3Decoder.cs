using Android.Media;
using Laveyana.Audio.Playback;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ParcelFileDescriptor = Android.OS.ParcelFileDescriptor;
using Stream = System.IO.Stream;

namespace Laveyana.LavPulse.Tracks;

/// <summary>Decodes audio with Android's media stack and exposes the original mono or stereo rate.</summary>
internal sealed class AndroidMp3Decoder(MediaExtractor extractor, MediaCodec codec, int channels, int sampleRate,
    CancellationToken token, string mime) : IMp3SampleDecoder
{
    private static readonly TimeSpan OutputTimeout = TimeSpan.FromSeconds(2);
    private readonly MediaCodec.BufferInfo _info = new();
    private short[] _pcm = [];
    private int _offset;
    private int _available;
    private bool _inputEnded;
    private bool _outputEnded;

    public int Channels => channels;
    public int SampleRate => sampleRate;
    public string Codec => mime switch
    {
        "audio/mpeg" => "MP3",
        "audio/mp4a-latm" => "AAC",
        "audio/flac" => "FLAC",
        "audio/opus" => "Opus",
        "audio/vorbis" => "Ogg Vorbis",
        _ => mime
    };

    public static IMp3SampleDecoder? Open(Stream source, CancellationToken token)
    {
        // Non-seekable Android document streams have already been copied to an app cache file.
        if (source is not FileStream file) return null;
        token.ThrowIfCancellationRequested();
        MediaExtractor? extractor = new();
        MediaCodec? codec = null;
        try
        {
            SetFileSource(extractor, file);
            int selected = -1;
            for (int i = 0; i < extractor.TrackCount; i++)
            {
                using var candidate = extractor.GetTrackFormat(i);
                if (candidate.GetString(MediaFormat.KeyMime) is "audio/mpeg" or "audio/mp4a-latm"
                    or "audio/flac" or "audio/opus" or "audio/vorbis")
                { selected = i; break; }
            }
            if (selected < 0) return null;
            using var format = extractor.GetTrackFormat(selected);
            string mime = format.GetString(MediaFormat.KeyMime)!;
            int channels = format.GetInteger(MediaFormat.KeyChannelCount);
            int rate = format.GetInteger(MediaFormat.KeySampleRate);
            if (channels is not (1 or 2) || rate <= 0 || rate > 192_000) return null;

            extractor.SelectTrack(selected);
            codec = MediaCodec.CreateDecoderByType(mime);
            codec.Configure(format, null, null, MediaCodecConfigFlags.None);
            codec.Start();
            var decoder = new AndroidMp3Decoder(extractor, codec, channels, rate, token, mime);
            extractor = null;
            codec = null;
            return decoder;
        }
        catch (Java.Lang.Exception ex)
        {
            throw new InvalidDataException("Системный декодер Android не открыл аудиофайл.", ex);
        }
        finally
        {
            Release(codec, extractor);
        }
    }

    private static void SetFileSource(MediaExtractor extractor, FileStream file)
    {
        using var fd = ParcelFileDescriptor.FromFd(file.SafeFileHandle.DangerousGetHandle().ToInt32())
            ?? throw new InvalidDataException("Android не открыл дескриптор аудио.");
        try
        {
            using var descriptor = fd.FileDescriptor
                ?? throw new InvalidDataException("Android вернул пустой дескриптор аудио.");
            extractor.SetDataSource(descriptor, 0, file.Length);
        }
        finally
        {
            fd.Close();
        }
    }

    public int ReadSamples(float[] destination)
    {
        try
        {
            if (_offset == _available && !ReadOutput()) return 0;
            int count = Math.Min(destination.Length, _available - _offset);
            Pcm16.Decode(_pcm.AsSpan(_offset, count), destination);
            _offset += count;
            return count;
        }
        catch (Java.Lang.Exception ex)
        {
            throw new InvalidDataException("Системный декодер Android не прочитал аудиофайл.", ex);
        }
    }

    private bool ReadOutput()
    {
        long started = Stopwatch.GetTimestamp();
        while (!_outputEnded)
        {
            token.ThrowIfCancellationRequested();
            FeedInput();
            int index = codec.DequeueOutputBuffer(_info, 10_000);
            if (index == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                CheckOutputFormat();
            }
            else if (index >= 0 && ReadBuffer(index))
            {
                return true;
            }

            if (!_outputEnded && Stopwatch.GetElapsedTime(started) >= OutputTimeout)
                throw new InvalidDataException("Системный декодер Android перестал выдавать PCM.");
        }
        return false;
    }

    private void FeedInput()
    {
        while (!_inputEnded)
        {
            token.ThrowIfCancellationRequested();
            int index = codec.DequeueInputBuffer(0);
            if (index < 0) return;
            using var input = codec.GetInputBuffer(index)
                ?? throw new InvalidDataException("Декодер Android не предоставил входной буфер.");
            int bytes = extractor.ReadSampleData(input, 0);
            if (bytes < 0)
            {
                codec.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                _inputEnded = true;
                return;
            }
            codec.QueueInputBuffer(index, 0, bytes, extractor.SampleTime, MediaCodecBufferFlags.None);
            extractor.Advance();
        }
    }

    private bool ReadBuffer(int index)
    {
        try
        {
            _outputEnded = (_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
            if (_info.Size == 0 || (_info.Flags & MediaCodecBufferFlags.CodecConfig) != 0) return false;
            if (_info.Size < 0 || _info.Size % (channels * sizeof(short)) != 0)
                throw new InvalidDataException("Декодер Android вернул неполный PCM-кадр.");

            using var output = codec.GetOutputBuffer(index)
                ?? throw new InvalidDataException("Декодер Android не предоставил выходной буфер.");
            IntPtr address = output.GetDirectBufferAddress();
            if (address == IntPtr.Zero || _info.Offset < 0 || _info.Size > output.Capacity() - _info.Offset)
                throw new InvalidDataException("Декодер Android вернул неверный PCM-буфер.");

            int samples = _info.Size / sizeof(short);
            if (_pcm.Length < samples) _pcm = new short[samples];
            Marshal.Copy(address + _info.Offset, _pcm, 0, samples);
            _offset = 0;
            _available = samples;
            return true;
        }
        finally
        {
            codec.ReleaseOutputBuffer(index, false);
        }
    }

    private void CheckOutputFormat()
    {
        using var format = codec.OutputFormat;
        const string encodingKey = "pcm-encoding";
        if (format.GetInteger(MediaFormat.KeyChannelCount) != channels ||
            format.GetInteger(MediaFormat.KeySampleRate) != sampleRate ||
            (format.ContainsKey(encodingKey) && format.GetInteger(encodingKey) != (int)Android.Media.Encoding.Pcm16bit))
        {
            throw new InvalidDataException("Системный декодер Android вернул неподдерживаемый формат PCM.");
        }
    }

    public void Dispose()
    {
        _info.Dispose();
        Release(codec, extractor);
    }

    private static void Release(MediaCodec? codec, MediaExtractor? extractor)
    {
        try { codec?.Release(); }
        catch (Java.Lang.Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            codec?.Dispose();
            try { extractor?.Release(); }
            catch (Java.Lang.Exception ex) { Debug.WriteLine(ex); }
            finally { extractor?.Dispose(); }
        }
    }
}
