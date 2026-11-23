using System.Text.Json;

namespace Laveyana.LavPulse.Tracks;

/// <summary>
/// Persists comments independently of the movable MP3/PCM library. Does not upload data.
/// One atomic JSON replacement protects annotations if the app is terminated while saving.
/// </summary>
internal sealed class TrackReportStore(string filePath)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<TrackIssueReport>? _items;

    public async Task<TrackIssueReport[]> ReadAllAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            return _items!.Select(x => x with { Marks = [.. x.Marks] }).ToArray();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Entering manual BPM annotation enrolls the track immediately, even before a mark is saved.</summary>
    public async Task<bool> RegisterAsync(string id, string fileName, string title, string? artist,
        string analysis, CancellationToken token)
    {
        ValidateId(id);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            int index = _items!.FindIndex(report => report.Id == id);
            var previous = new List<TrackIssueReport>(_items);
            if (index >= 0)
            {
                // Existing user annotations remain intact when manual editing resumes.
                var current = _items[index];
                _items[index] = current with
                {
                    FileName = fileName, Title = title, Artist = artist, Analysis = analysis
                };
            }
            else _items.Add(new(id, fileName, title, artist, analysis, []));

            try { await PersistAsync(token).ConfigureAwait(false); }
            catch { _items = previous; throw; }
            return index < 0;
        }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(string id, string fileName, string title, string? artist,
        string analysis, TrackIssueMark mark, CancellationToken token)
    {
        ValidateId(id);
        if (mark.StartFrame < 0 || mark.EndFrame < mark.StartFrame ||
            (mark.Kind == TrackReportMarkKind.Problem && string.IsNullOrWhiteSpace(mark.Comment)) ||
            mark.Comment.Length > 3000 || mark.TimingToleranceMilliseconds is < 0 or > 1500 ||
            mark.PossibleMissingBeats is < 0 or > 3 ||
            (mark.SuggestedBpm is double bpm && (!double.IsFinite(bpm) || bpm is < 40 or > 300)))
            throw new ArgumentException("Укажи корректный диапазон и комментарий.", nameof(mark));

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            var existing = _items!.FindIndex(x => x.Id == id);
            var previous = new List<TrackIssueReport>(_items);
            if (existing >= 0)
            {
                var item = _items[existing];
                _items[existing] = item with
                {
                    FileName = fileName, Title = title, Artist = artist, Analysis = analysis,
                    Marks = [.. item.Marks, mark]
                };
            }
            else _items.Add(new(id, fileName, title, artist, analysis, [mark]));
            try { await PersistAsync(token).ConfigureAwait(false); }
            catch { _items = previous; throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            var prior = _items;
            _items = [];
            try { await PersistAsync(token).ConfigureAwait(false); }
            catch { _items = prior; throw; }
        }
        finally { _gate.Release(); }
    }

    private static void ValidateId(string id)
    {
        if (id.Length != 64 || !id.All(char.IsAsciiHexDigitUpper))
            throw new ArgumentException("Неверный идентификатор импортированного трека.", nameof(id));
    }

    private async Task EnsureLoadedAsync(CancellationToken token)
    {
        if (_items is not null) return;
        if (!File.Exists(filePath))
        {
            _items = [];
            return;
        }
        await using var source = File.OpenRead(filePath);
        _items = await JsonSerializer.DeserializeAsync(source,
            TrackReportJson.Default.ListTrackIssueReport, token).ConfigureAwait(false) ?? [];
    }

    private async Task PersistAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, _items,
                    TrackReportJson.Default.ListTrackIssueReport, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, filePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
