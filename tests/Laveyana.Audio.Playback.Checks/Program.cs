using Laveyana.Audio.Playback;

internal static partial class Program
{
    private const int Rate = PlaybackFormat.SampleRate;
    private const int PauseFrames = Rate * 300 / 1000;
    private const int BlockSize = PlaybackFormat.BlockFrames;

    private static async Task Main()
    {
        string directory = Directory.CreateTempSubdirectory("lavpulse-checks-").FullName;
        try
        {
            CheckQueuePreservesOrderOnDirectSelection();
            CheckQueueChangesDuringPreparation();
            CheckQueueViewport();
            CheckTrackListLayout();
            await CheckManualScanControlsAsync();
            await CheckInterruptedQueueAnalysis();
            await CheckQueueAnalysisLookahead();
            CheckMp3AudioInfo();
            await TrackPreparationChecks.RunAsync(directory);
            await LocalTrackStoreChecks.RunAsync(directory);
            CheckSpectrumCaptureToggle();
            Console.WriteLine("OK: MP3 stream properties.");
            CachedTrack first = MakeTrack(directory, "first", left: 8192, right: 0);
            CachedTrack second = MakeTrack(directory, "second", left: 0, right: 8192);
            await CheckPlaybackSessionFailureCleanup(first);
            CheckMp3ArtworkSelection();
            CheckSingleInputMixer();
            CheckPcm16Conversion();
            Console.WriteLine("OK: ID3 artwork and PCM conversion and mixing.");
            await CheckPauseAndResume(first, second, duringMix: false);
            await CheckPauseAndResume(first, second, duringMix: true);
            CheckShortTrack();
            await CheckStartupAndRate(first, second);
            await CheckFeedSelection(first, second);
            await CheckReplaceSuccessor(first, second);
            await CheckRepeatOne(first, second);
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
