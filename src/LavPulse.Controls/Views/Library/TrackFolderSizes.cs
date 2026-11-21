using System.Diagnostics;
using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

internal static class TrackFolderSizes
{
    /// <summary>Totals the tracks in each folder without decoding audio streams.</summary>
    public static async Task<IReadOnlyDictionary<string, long?>> ReadAsync(TrackFolderSource root, TrackFolder contents,
        CancellationToken token, Func<TrackFile, CancellationToken, ValueTask<long?>>? readSize = null)
    {
        var sizes = new Dictionary<string, long?>(StringComparer.Ordinal);
        await VisitAsync(root, contents).ConfigureAwait(false);
        return sizes;

        async Task<long?> VisitAsync(TrackFolderSource folder, TrackFolder? listing = null)
        {
            token.ThrowIfCancellationRequested();
            // A null entry also guards against cycles in a document provider's tree.
            if (!sizes.TryAdd(folder.Id, null)) return sizes[folder.Id];
            if (listing is null)
            {
                try { listing = await folder.ReadAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    return null;
                }
            }

            long total = 0;
            bool complete = true;
            foreach (var file in listing.Files)
            {
                token.ThrowIfCancellationRequested();
                Add(readSize is null ? file.SizeBytes : await readSize(file, token).ConfigureAwait(false));
            }
            foreach (var child in listing.Children)
                Add(await VisitAsync(child).ConfigureAwait(false));
            return sizes[folder.Id] = complete ? total : null;

            void Add(long? size)
            {
                if (size is long bytes && bytes >= 0 && bytes <= long.MaxValue - total) total += bytes;
                else complete = false;
            }
        }
    }
}
