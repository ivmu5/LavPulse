using Laveyana.LavPulse.Controls;
using Laveyana.LavPulse.Tracks;
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
        await CheckStreamedCopyAsync(store);
        long mediaBytes = new FileInfo(first.LocalPath!).Length;
        Require(await store.ReadTrackStorageSizeAsync(first, default) == mediaBytes,
            "A track without cached data did not report its MP3 size.");
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
        Require(await reopened.ReadTrackStorageSizeAsync(tracks[0], default) ==
                tracks[0].SizeBytes + AnalysisBytes(tracks[0]) &&
            await reopened.ReadTrackStorageSizeAsync(tracks[1], default) ==
                mediaBytes + AnalysisBytes(tracks[1]) + new FileInfo(ready.Path).Length,
            "Track storage did not include exactly the cache files that exist after reopening.");

        var points = ready.Waveform.Points.ToArray();
        points[0] = points[0] with { Peak = 0.193f };
        var rescanned = new TrackWaveform(points, ready.Waveform.TotalFrames, ready.Waveform.FramesPerPoint);
        var changes = new List<string>();
        reopened.TrackStorageChanged += changes.Add;
        await reopened.SaveAnalysisAsync(tracks[1], rescanned, ready.AudioInfo, null, default, replaceAnalysis: true);
        cached = await reopened.ReadAnalysisAsync(tracks[1], default);
        Require(cached is not null && cached.Points[0] == points[0] && reopened.FindPcm(cached) == ready.Path,
            "Forced analysis did not replace the waveform while keeping compatible PCM.");

        // After PCM eviction the saved waveform remains usable when rebuilding the playback cache.
        await File.WriteAllBytesAsync(ready.Path, [1, 2]);
        Require(reopened.FindPcm(cached!) is null, "An incomplete cache was treated as playable.");
        Require(await reopened.ReadTrackStorageSizeAsync(tracks[1], default) == mediaBytes + AnalysisBytes(tracks[1]) + 2,
            "Track storage used estimated PCM size instead of its actual length.");
        reopened.EvictPcm(tracks[1], ready);
        Require(await reopened.ReadTrackStorageSizeAsync(tracks[1], default) == mediaBytes + AnalysisBytes(tracks[1]) &&
            changes.SequenceEqual([tracks[1].LocalPath, tracks[1].LocalPath]),
            "Cache changes did not update the media and analysis total or notify the library.");
        var waveform = cached!.Waveform;
        var rebuilt = await preparation.ImportAsync(tracks[1], null, (waveform, cached.AudioInfo), default);
        Require(ReferenceEquals(rebuilt.Waveform, waveform), "Rebuilding PCM recomputed a valid waveform.");
        rebuilt.Delete();

        await CheckScanOrderAsync(tracks);
        await CheckFolderLibraryAsync(directory);
        await CheckLegacyQueueAsync(directory, first);
        CheckMetadataCache();
        Console.WriteLine("OK: durable track copies, queue restore, saved waveform reuse and ordered analysis.");
        Console.WriteLine("OK: track storage sizes, including analysis and PCM cache eviction.");

        long AnalysisBytes(TrackFile file) => new FileInfo(Path.Combine(root, "analysis-v1",
            Path.GetFileNameWithoutExtension(file.LocalPath) + ".json")).Length;
    }

    private static void CheckMetadataCache()
    {
        var cache = new TrackMetadataCache();
        var first = TrackFile.FromPath("first.mp3");
        var second = TrackFile.FromPath("second.mp3");
        var thumbnail = new TrackMetadata("Title", "Artist", new byte[3 * 1024 * 1024]);
        cache.Store(first, thumbnail);
        Require(ReferenceEquals(cache.Get(first), thumbnail), "A recycled row could not reuse its metadata.");
        cache.Store(second, thumbnail);
        Require(cache.Get(first) is null && ReferenceEquals(cache.Get(second), thumbnail),
            "The library metadata cache exceeded its artwork budget.");
        cache.Invalidate(new HashSet<string> { second.LocalPath! });
        Require(cache.Get(second) is null, "A rescan did not discard the cached thumbnail.");

        var textOnly = thumbnail with { Artwork = default };
        cache.Store(first, textOnly);
        for (int i = 0; i < 96; i++) cache.Store(TrackFile.FromPath($"track-{i}.mp3"), textOnly);
        Require(cache.Get(first) is null, "Track tags without artwork bypassed the cache's entry limit.");
        Console.WriteLine("OK: bounded library metadata reuse and rescan invalidation.");
    }

    private static async Task CheckFolderLibraryAsync(string directory)
    {
        string source = Path.Combine(directory, "source-folder");
        string child = Path.Combine(source, "Disc 2");
        Directory.CreateDirectory(child);
        await File.WriteAllBytesAsync(Path.Combine(source, "b.MP3"), [2, 2]);
        await File.WriteAllBytesAsync(Path.Combine(source, "a.mp3"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "notes.txt"), [0]);
        await File.WriteAllBytesAsync(Path.Combine(child, "c.mp3"), [3, 3, 3]);

        var folder = TrackFolderReader.FromDirectory(source, "Album", TrackFile.FromPath);
        var rootListing = await folder.ReadAsync(default);
        Require(rootListing.Children.Count == 1, "The browser did not expose a nested directory.");
        var folderSizes = await TrackFolderSizes.ReadAsync(folder, rootListing, default);
        Require(rootListing.Files.Select(file => file.SizeBytes).SequenceEqual([1L, 2L]) &&
            folderSizes[folder.Id] == 6 && folderSizes[rootListing.Children.Single().Id] == 3,
            "Folder totals did not include nested MP3 sizes or included another file type.");
        var storedSizes = await TrackFolderSizes.ReadAsync(folder, rootListing, default,
            (file, _) => ValueTask.FromResult(file.SizeBytes + 10));
        Require(storedSizes[folder.Id] == 36 && storedSizes[rootListing.Children.Single().Id] == 13,
            "Folder totals did not use the provided storage size.");
        var partial = TrackFolder.FromFiles(rootListing.Name, [rootListing.Files[0] with { SizeBytes = null }]) with
        {
            Children = rootListing.Children
        };
        var partialSizes = await TrackFolderSizes.ReadAsync(folder, partial, default);
        Require(partialSizes[folder.Id] is null && partialSizes[rootListing.Children.Single().Id] == 3,
            "Unknown sizes hid the size of a readable subfolder or produced an incomplete total.");
        var selectedSubfolder = await TrackFolderReader.ReadTracksAsync(rootListing.Children.Single(), default, [folder]);
        Require(selectedSubfolder.Select(item => item.File.Name).SequenceEqual(["c.mp3"]) &&
            selectedSubfolder[0].Folders.Select(item => item.Name).SequenceEqual(["Album", "Disc 2"]),
            "Selecting a subfolder included tracks outside its subtree or lost its ancestry.");
        var recursive = await TrackFolderReader.ReadTracksAsync(folder, default);
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
        var libraryRoot = reopened.OpenLibrary(reopened.LibrarySnapshot());
        var library = await libraryRoot.ReadAsync(default);
        Require(ReferenceEquals(library, await libraryRoot.ReadAsync(default)),
            "The same catalog snapshot rebuilt its sorted list of folders.");
        var albumFolder = library.Children.Single(folder => folder.Name == "Album");
        var album = await albumFolder.ReadAsync(default);
        Require(ReferenceEquals(album, await albumFolder.ReadAsync(default)),
            "Reopening a folder rebuilt its sorted tracks.");
        var nested = await album.Children.Single().ReadAsync(default);
        Require(nested.Files.Single().LocalPath == queued[^1].LocalPath &&
            album.Files.Select(file => file.Name).SequenceEqual(["a.mp3", "b.MP3"]),
            "The saved hierarchy failed to reopen after the source directory was deleted.");
        var savedSizes = await TrackFolderSizes.ReadAsync(libraryRoot, library, default);
        Require(nested.Files.Single().SizeBytes == 3 && savedSizes[libraryRoot.Id] == 9 &&
            savedSizes[albumFolder.Id] == 6 && savedSizes[album.Children.Single().Id] == 3,
            "Saved tracks and folder totals did not recover their file sizes after restart.");
        Require(library.Children.Single(folder => folder.Name == "Disc 2").Id != album.Children.Single().Id,
            "Different tree branches cannot share expansion state.");

        Require(reopened.ForgetLibraryItem("", null) == 0, "The library root could be removed by mistake.");
        Require(reopened.ForgetLibraryItem(albumFolder.Id, album.Files[0]) == 1 &&
            reopened.ForgetLibraryItem(albumFolder.Id, album.Files[0]) == 0,
            "Deleting a library track removed the wrong membership.");
        Require(reopened.ForgetLibraryItem(albumFolder.Id, null) == 2,
            "Deleting a library folder did not include the remaining tracks in its subtree.");
        await reopened.SaveQueueAsync(reopened.Snapshot(restored.Tracks, restored.Current));

        var afterDelete = new LocalTrackStore(libraryPath);
        var queue = await afterDelete.ReadQueueAsync(default);
        var remaining = await afterDelete.OpenLibrary(afterDelete.LibrarySnapshot()).ReadAsync(default);
        Require(queue.Tracks.Single().LocalPath == queued[0].LocalPath && File.Exists(queued[0].LocalPath) &&
            remaining.Children.Single().Name == "Disc 2",
            "Deleting a library branch removed a queued copy or another branch.");
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

    private static async Task CheckStreamedCopyAsync(LocalTrackStore store)
    {
        var bytes = new byte[280_027];
        new Random(19).NextBytes(bytes);
        var streamed = TrackFile.FromStream("streamed.mp3", () => Task.FromResult<Stream>(new MemoryStream(bytes)));
        var saved = await store.CopyAsync(streamed, default);
        string expectedId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        Require(Path.GetFileNameWithoutExtension(saved.LocalPath) == expectedId &&
            (await File.ReadAllBytesAsync(saved.LocalPath!)).SequenceEqual(bytes),
            "Hashing while importing changed the media bytes or durable content ID.");

        var custom = streamed with
        {
            CopyToAsync = async (destination, token) =>
            {
                await destination.WriteAsync(bytes, token);
                destination.Position = 0;
                await destination.WriteAsync(new byte[] { (byte)~bytes[0] }, token);
            }
        };
        var customized = await store.CopyAsync(custom, default);
        bytes[0] = (byte)~bytes[0];
        Require(customized.LocalPath != saved.LocalPath &&
            (await File.ReadAllBytesAsync(customized.LocalPath!)).SequenceEqual(bytes),
            "A custom seekable import was replaced by its original stream factory.");
        File.Delete(saved.LocalPath!);
        File.Delete(customized.LocalPath!);
    }
}
