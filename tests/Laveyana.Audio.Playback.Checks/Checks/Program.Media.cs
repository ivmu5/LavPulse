using Laveyana.Audio;
using Laveyana.Audio.Playback;

internal static partial class Program
{
    /// <summary>Checks that disabling FFT preserves playback timing and discards pre-toggle windows.</summary>
    private static void CheckSpectrumCaptureToggle()
    {
        var source = new ContinuousPcm();
        var mixer = new PcmMixer([new PcmInput(source)], 16, 2048);
        var block = new float[PlaybackFormat.BlockFrames * PlaybackFormat.Channels];
        var bands = new float[mixer.Spectrum.Bands.Count];
        mixer.Render(block);
        mixer.Render(block);
        Require(mixer.Spectrum.TryReadLevels(bands, out long first) && first == 2 * PlaybackFormat.BlockFrames,
            "No initial spectrum window.");

        // Leave one pending snapshot to ensure it cannot appear when visualization is restored.
        mixer.Render(block);
        mixer.Spectrum.SetEnabled(false);
        mixer.Render(block);
        mixer.Render(block);
        Require(!mixer.Spectrum.TryReadLevels(bands, out _) && block[0] == .25f,
            "Hidden spectrum altered audio or emitted a window.");

        mixer.Spectrum.SetEnabled(true);
        mixer.Render(block);
        Require(!mixer.Spectrum.TryReadLevels(bands, out _), "Re-enabled spectrum reused stale PCM.");
        mixer.Render(block);
        Require(mixer.Spectrum.TryReadLevels(bands, out long resumed) && resumed == 7 * PlaybackFormat.BlockFrames,
            "Spectrum did not resume on the absolute render clock.");
    }

    /// <summary>Verifies bitrate and mode are read from the audio frames even with misleading tag or seek-table bytes.</summary>
    private static void CheckMp3AudioInfo()
    {
        using var source = new MemoryStream();
        // The tag contains two complete-looking 128 kbps Stereo frames.
        source.Write("ID3"u8);
        source.Write([4, 0, 0x10, 0, 0, 6, 66]);
        source.Write(CreateFrame(417, 0xfb, 0x90, 0x00));
        source.Write(CreateFrame(417, 0xfb, 0x90, 0x00));
        source.Write(new byte[10]); // ID3v2.4 footer.

        byte[] seekFrame = CreateFrame(626, 0xfb, 0xb0, 0x00);
        "Xing"u8.CopyTo(seekFrame.AsSpan(36));
        source.Write(seekFrame);
        source.Write(CreateFrame(417, 0xfb, 0x90, 0x40)); // 128 kbps Joint Stereo.
        source.Write(CreateFrame(731, 0xfb, 0xc0, 0x40)); // 224 kbps Joint Stereo.
        var vbr = Mp3AudioInfoReader.Read(source, CancellationToken.None);
        Require(vbr == new TrackAudioInfo("MP3", 44100, 176, true, TrackChannelMode.JointStereo), "Invalid VBR audio properties.");

        source.SetLength(0);
        source.Write(CreateFrame(144, 0xe3, 0x28, 0xc0));
        source.Write(CreateFrame(144, 0xe3, 0x28, 0xc0));
        var mono = Mp3AudioInfoReader.Read(source, CancellationToken.None);
        Require(mono == new TrackAudioInfo("MP3", 8000, 16, false, TrackChannelMode.Mono), "Invalid low-rate mono audio properties.");

        source.SetLength(0);
        source.Write(CreateFrame(417, 0xfb, 0x00, 0x00));
        source.Write(CreateFrame(418, 0xfb, 0x02, 0x00));
        source.Write(CreateFrame(417, 0xfb, 0x00, 0x00));
        var freeFormat = Mp3AudioInfoReader.Read(source, CancellationToken.None);
        Require(freeFormat == new TrackAudioInfo("MP3", 44100, 128, false, TrackChannelMode.Stereo), "Invalid free-format MP3 properties.");

        using var manyFrames = new CountingMemoryStream();
        byte[] frame = CreateFrame(417, 0xfb, 0x90, 0x00);
        for (int i = 0; i < 1000; i++) manyFrames.Write(frame);
        manyFrames.Write(frame.AsSpan(0, 7)); // The final header has no complete frame.
        var buffered = Mp3AudioInfoReader.Read(manyFrames, CancellationToken.None);
        Require(buffered == new TrackAudioInfo("MP3", 44100, 128, false, TrackChannelMode.Stereo) &&
            manyFrames.Position == 417_004 && manyFrames.Seeks < 25,
            "MP3 properties changed across read-ahead windows or still seek frame by frame.");
        Console.WriteLine($"OK: MP3 properties across read-ahead windows ({manyFrames.Seeks} seeks for 1,000 frames).");

        static byte[] CreateFrame(int length, byte versionLayer, byte rate, byte channel)
        {
            var bytes = new byte[length];
            bytes[0] = 0xff;
            bytes[1] = versionLayer;
            bytes[2] = rate;
            bytes[3] = channel;
            return bytes;
        }
    }

    private sealed class CountingMemoryStream : MemoryStream
    {
        public int Seeks { get; private set; }
        public override long Position
        {
            get => base.Position;
            set
            {
                Seeks++;
                base.Position = value;
            }
        }
    }

    private static void CheckMp3ArtworkSelection()
    {
        foreach (byte version in new byte[] { 3, 4 })
        {
            using var body = new MemoryStream();
            WriteFrame("TIT2"u8, [1, 0xfe, 0xff, 0, (byte)'A']);
            WriteFrame("TALB"u8, [3, .. "Album title"u8]);
            WriteFrame("APIC"u8, Picture(0, 11));
            WriteFrame("APIC"u8, Picture(3, 22));
            WriteFrame("APIC"u8, Picture(0, 33));
            WriteFrame("APIC"u8, Picture(3, 44));

            using var stream = new MemoryStream();
            stream.Write("ID3"u8);
            stream.Write([version, 0, 0, 0, 0, (byte)(body.Length >> 7), (byte)(body.Length & 127)]);
            body.WriteTo(stream);
            stream.Position = 0;
            var tags = Mp3Tags.Read(stream);
            Require(tags.Title == "A" && tags.Album == "Album title" && tags.Artwork is { Length: 4 } image && image[^1] == 44,
                "ID3 must retain the album and final front cover, and decode big-endian text.");
            stream.SetLength(stream.Length - 1);
            stream.Position = 0;
            Require(Mp3Tags.Read(stream) == new Mp3Tags(), "A truncated tag published partial metadata.");

            static byte[] Picture(byte type, byte marker) =>
                [0, .. "image/jpeg"u8, 0, type, 0, 0xff, 0xd8, 0xff, marker];

            void WriteFrame(ReadOnlySpan<byte> name, ReadOnlySpan<byte> data)
            {
                body.Write(name);
                // These frame sizes fit in a single byte for both ID3 size encodings.
                body.Write([0, 0, 0, (byte)data.Length, 0, 0]);
                body.Write(data);
            }
        }
    }

    private static void CheckSingleInputMixer()
    {
        var input = new PcmInput(new ShortSource(2f));
        var mixer = new PcmMixer([input]);
        var output = new float[8 * PlaybackFormat.Channels];
        Array.Fill(output, 2f);
        mixer.Render(output);
        Require(output.AsSpan(0, 8).IndexOfAnyExcept(1f) < 0 &&
            output.AsSpan(8).IndexOfAnyExcept(0f) < 0, "A short read did not clamp the signal and clear the tail.");
        mixer.Render(output);
        Require(output.AsSpan().IndexOfAnyExcept(0f) < 0, "An exhausted source must fill the output with silence.");

        var continuous = new PcmInput(new ContinuousPcm(), .5f);
        var attenuated = new PcmMixer([continuous]);
        attenuated.Render(output);
        Require(output.AsSpan().IndexOfAnyExcept(.125f) < 0, "The direct mixer path ignored the input gain.");
        continuous.Gain = 1f;
        attenuated.Render(output);
        Require(output.AsSpan().IndexOfAnyExcept(.25f) < 0, "The direct mixer path ignored a new gain.");

        var summed = new PcmMixer([new PcmInput(new ShortSource()), new PcmInput(new ContinuousPcm())]);
        summed.Render(output);
        Require(output.AsSpan(0, 8).IndexOfAnyExcept(1f) < 0 &&
            output.AsSpan(8).IndexOfAnyExcept(.25f) < 0, "Multi-input gain, clipping or tail handling changed.");
    }

    private static void CheckPcm16Conversion()
    {
        // Cover all signed values and a non-vector-sized tail.
        var pcm = new short[ushort.MaxValue + 4];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = unchecked((short)(i + short.MinValue));
        var samples = new float[pcm.Length + 1];
        Array.Fill(samples, 2f);
        Pcm16.Decode(pcm, samples);
        for (int i = 0; i < pcm.Length; i++)
        {
            Require(samples[i] == pcm[i] / 32768f, "PCM16 conversion changed a sample.");
        }
        Require(samples[^1] == 2f, "PCM16 conversion wrote past the decoded samples.");
    }

    /// <summary>Checks the audible buffer and verifies that neither deck was reset or read past the fade.</summary>
}
