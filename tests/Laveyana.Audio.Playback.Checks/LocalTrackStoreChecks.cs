using Laveyana.Audio.Maui.Tracks;
using Laveyana.Audio.Playback;

internal static class LocalTrackStoreChecks
{
    public static async Task RunAsync(string directory)
    {
        string root = Path.Combine(directory, "persistent-library");
        var store = new LocalTrackStore(root);
        var first = await store.CopyAsync(Source("first.mp3", 1), default);
        var second = await store.CopyAsync(Source("second.mp3", 2), default);
        var duplicate = await store.CopyAsync(Source("copy.mp3", 1), default);
        Require(first.LocalPath == duplicate.LocalPath, "Identical imports duplicated local media.");
        await store.SaveQueueAsync(store.Snapshot([second, first], first));

        var preparation = new TrackPreparation(store.PcmDirectory);
        var scanned = await preparation.AnalyzeAsync(second.LocalPath!, default);
        await store.SaveAnalysisAsync(second, scanned.Waveform, scanned.Info, null, default);
        Require(!Directory.Exists(store.PcmDirectory), "Compact analysis allocated a PCM directory.");
        var ready = await preparation.ImportAsync(first, default);
        await store.SaveAnalysisAsync(first, ready.Waveform, ready.AudioInfo, ready.Path, default);
        await store.SaveAnalysisAsync(first, scanned.Waveform, scanned.Info, null, default);

        var reopened = new LocalTrackStore(root);
        var (tracks, current) = await reopened.ReadQueueAsync(default);
        Require(tracks.Select(file => file.Name).SequenceEqual(["second.mp3", "first.mp3"]) &&
            ReferenceEquals(current, tracks[1]), "Queue order or current track changed after reopening.");
        Require(reopened.HasAnalysis(tracks[0]), "Compact analysis was not indexed after reopening.");
        var cached = await reopened.ReadAnalysisAsync(tracks[1], default);
        Require(cached is not null && reopened.FindPcm(cached) == ready.Path,
            "Completed PCM was lost or replaced by a compact scan.");

        var points = ready.Waveform.Points.ToArray();
        points[0] = points[0] with { Peak = 0.193f };
        var rescanned = new TrackWaveform(points, ready.Waveform.TotalFrames, ready.Waveform.FramesPerPoint);
        await reopened.SaveAnalysisAsync(tracks[1], rescanned, ready.AudioInfo, null, default, replaceAnalysis: true);
        cached = await reopened.ReadAnalysisAsync(tracks[1], default);
        Require(cached is not null && cached.Points[0] == points[0] && reopened.FindPcm(cached) == ready.Path,
            "Forced analysis did not replace the waveform while keeping compatible PCM.");

        // After PCM eviction the saved waveform remains usable when rebuilding the playback cache.
        await File.WriteAllBytesAsync(ready.Path, [1, 2]);
        Require(reopened.FindPcm(cached!) is null, "An incomplete cache was treated as playable.");
        ready.Delete();
        var waveform = cached!.Waveform;
        var rebuilt = await preparation.ImportAsync(tracks[1], null, (waveform, cached.AudioInfo), default);
        Require(ReferenceEquals(rebuilt.Waveform, waveform), "Rebuilding PCM recomputed a valid waveform.");
        rebuilt.Delete();

        await CheckScanOrderAsync(tracks);
        await CheckFolderLibraryAsync(directory);
        await CheckLegacyQueueAsync(directory, first);
        Console.WriteLine("OK: durable track copies, queue restore, saved waveform reuse and ordered analysis.");
    }

    private static async Task CheckFolderLibraryAsync(string directory)
    {
        string source = Path.Combine(directory, "source-folder");
        string child = Path.Combine(source, "Disc 2");
        Directory.CreateDirectory(child);
        await File.WriteAllBytesAsync(Path.Combine(source, "b.MP3"), [2]);
        await File.WriteAllBytesAsync(Path.Combine(source, "a.mp3"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "notes.txt"), [0]);
        await File.WriteAllBytesAsync(Path.Combine(child, "c.mp3"), [3]);

        var folder = TrackFolderReader.FromDirectory(source, "Album", TrackFile.FromPath);
        var rootListing = await folder.ReadAsync(default);
        Require(rootListing.Children.Count == 1, "The browser did not expose a nested directory.");
        var guarded = folder with
        {
            ReadAsync = _ => Task.FromResult(rootListing with
            {
                Children = [rootListing.Children[0] with
                {
                    ReadAsync = _ => throw new InvalidOperationException("Flat scans must leave subfolders unopened.")
                }]
            })
        };
        var direct = await TrackFolderReader.ReadTracksAsync(guarded, false, default);
        Require(direct.Select(item => item.File.Name).SequenceEqual(["a.mp3", "b.MP3"]),
            "A flat scan did not preserve direct file order.");
        var recursive = await TrackFolderReader.ReadTracksAsync(folder, true, default);
        Require(recursive.Select(item => item.File.Name).SequenceEqual(["a.mp3", "b.MP3", "c.mp3"]) &&
            recursive[^1].Folders.Select(item => item.Name).SequenceEqual(["Album", "Disc 2"]),
            "Recursive imports lost order or folder ancestry.");

        string libraryPath = Path.Combine(directory, "folder-library");
        var store = new LocalTrackStore(libraryPath);
        var queued = new List<TrackFile>();
        foreach (var item in recursive)
        {
            var local = await store.CopyAsync(item.File, default);
            Require(store.Remember(local, item.Folders) && !store.Remember(local, item.Folders),
                "Importing the same track into the same folder duplicated its membership.");
            queued.Add(local);
        }
        Require(store.Remember(queued[^1], [recursive[^1].Folders[^1]]),
            "The same media copy cannot be indexed from more than one folder path.");
        await store.SaveQueueAsync(store.Snapshot([queued[0]], queued[0]));
        Directory.Delete(source, recursive: true);

        var reopened = new LocalTrackStore(libraryPath);
        var restored = await reopened.ReadQueueAsync(default);
        Require(restored.Tracks.Length == 1, "Library-only tracks were returned to the playback queue.");
        var library = await reopened.OpenLibrary(reopened.LibrarySnapshot()).ReadAsync(default);
        var album = await library.Children.Single(folder => folder.Name == "Album").ReadAsync(default);
        var nested = await album.Children.Single().ReadAsync(default);
        Require(nested.Files.Single().LocalPath == queued[^1].LocalPath &&
            album.Files.Select(file => file.Name).SequenceEqual(["a.mp3", "b.MP3"]),
            "The saved hierarchy failed to reopen after the source directory was deleted.");
        Require(library.Children.Single(folder => folder.Name == "Disc 2").Id != album.Children.Single().Id,
            "Different tree branches cannot share expansion state.");
    }

    private static async Task CheckLegacyQueueAsync(string directory, TrackFile source)
    {
        var store = new LocalTrackStore(Path.Combine(directory, "legacy-library"));
        var local = await store.CopyAsync(source, default);
        await store.SaveQueueAsync(new([new(Path.GetFileNameWithoutExtension(local.LocalPath!), local.Name)], null));
        await store.ReadQueueAsync(default);
        var listing = await store.OpenLibrary(store.LibrarySnapshot()).ReadAsync(default);
        Require(listing.Files.Single().Name == local.Name, "The legacy queue did not migrate into the local library.");
    }

    private static async Task CheckScanOrderAsync(TrackFile[] tracks)
    {
        var order = new List<string>();
        var prepared = new HashSet<TrackFile>();
        var analyzed = new HashSet<TrackFile>();
        Task Prepare(TrackFile file)
        {
            order.Add("pcm:" + file.Name);
            prepared.Add(file);
            analyzed.Add(file);
            return Task.CompletedTask;
        }
        Task Analyze(TrackFile file)
        {
            order.Add("scan:" + file.Name);
            analyzed.Add(file);
            return Task.CompletedTask;
        }

        var lookahead = new QueueTrackAnalysis(prepared.Contains, Prepare, () => { }, default, analyzed.Contains, Analyze);
        await lookahead.Update([tracks[0]], allowed: true, tracks)!;
        lookahead.Finish();
        Require(order.SequenceEqual(["pcm:second.mp3", "scan:first.mp3"]) &&
            lookahead.Update([tracks[0]], allowed: true, tracks) is null,
            "The worker skipped queue priority or repeated completed analysis.");
    }

    private static TrackFile Source(string name, byte marker) => new(name, async (output, token) =>
    {
        using var source = TrackPreparationChecks.CreateMp3();
        await source.CopyToAsync(output, token);
        output.WriteByte(marker);
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
