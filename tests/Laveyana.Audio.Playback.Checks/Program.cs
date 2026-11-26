using Laveyana.Audio.Playback;

internal static partial class Program
{
    private const int Rate = PlaybackFormat.SampleRate;
    private const int PauseFrames = Rate * 300 / 1000;
    private const int BlockSize = PlaybackFormat.BlockFrames;

    private static void CheckNativeOutputCounter()
    {
        NativeOutputCounter counter = default;
        Require(counter.Observe(100, 200) == 100 &&
            counter.Observe(0, 200) == 100 &&
            counter.Observe(50, 200) == 150 &&
            counter.Observe(500, 200) == 150 &&
            counter.Observe(510, 200) == 160,
            "Native counter reset or impossible progress corrupted the audible position.");
        NativeOutputCounter wrap = default;
        Require(wrap.Observe(0x70000000) == 0x70000000L &&
            wrap.Observe(0xE0000000) == 0xE0000000L &&
            wrap.Observe(0xFFFFFFF0) == 0xFFFFFFF0L &&
            wrap.Observe(0x10) == 0x100000010L,
            "Genuine unsigned counter wrap was lost.");
    }

    private static async Task Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--rhythm-report")
        {
            foreach (string archive in args.Skip(1)) RhythmReportReplay.Run(archive);
            return;
        }
        string directory = Directory.CreateTempSubdirectory("lavpulse-checks-").FullName;
        try
        {
            CheckNativeOutputCounter();
            CheckSystemAudioFileNames();
            CheckQueuePreservesOrderOnDirectSelection();
            CheckQueueChangesDuringPreparation();
            CheckQueueClear();
            CheckQueueViewport();
            CheckTrackListLayout();
            CheckQueueMarqueeMotion();
            await CheckManualScanControlsAsync();
            await CheckInterruptedQueueAnalysis();
            await CheckQueueAnalysisLookahead();
            await CheckCompactAnalysisPassAsync();
            CheckMp3AudioInfo();
            MusicProfileChecks.Run();
            await TrackPreparationChecks.RunAsync(directory);
            await LocalTrackStoreChecks.RunAsync(directory);
            await CheckTrackReportBundleAsync(directory);
            CheckSpectrumCaptureToggle();
            Console.WriteLine("OK: MP3 stream properties.");
            CachedTrack first = MakeTrack(directory, "first", left: 8192, right: 0);
            CachedTrack second = MakeTrack(directory, "second", left: 0, right: 8192);
            MusicProfileChecks.CheckNormalization(first, second);
            await CheckPlaybackSessionFailureCleanup(first);
            CheckMp3ArtworkSelection();
            CheckSingleInputMixer();
            CheckPcm16Conversion();
            Console.WriteLine("OK: ID3 artwork and PCM conversion and mixing.");
            CheckAudioPresentationHistory(first, second);
            await CheckAudibleQueueSnapshot(first, second);
            await CheckPauseAndResume(first, second, duringMix: false);
            await CheckPauseAndResume(first, second, duringMix: true);
            CheckShortTrack();
            await CheckStartupAndRate(first, second);
            await CheckFeedSelection(first, second);
            await CheckReplaceSuccessor(first, second);
            await CheckClearingPlaybackSuccessor(first, second);
            await CheckRepeatOne(first, second);
            await CheckConcurrentQueueControl(first);
            await CheckStressAsync(directory, first, second);
            await CheckMasterEqualizer(directory);
            SpectrumFrameClockChecks.Run();
            Console.WriteLine("OK: transport envelopes, speed, queue transitions, repeat one, and filtered master output and spectrum.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
