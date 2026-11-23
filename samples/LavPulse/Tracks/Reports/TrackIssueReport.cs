namespace Laveyana.LavPulse.Tracks;

/// <summary>A user-observed problem over a selected portion of a decoded track.</summary>
internal sealed record TrackIssueMark(long StartFrame, long EndFrame, string Comment, DateTimeOffset CreatedAt);

/// <summary>Persistent report for one imported MP3, identified by its SHA-256 library filename.</summary>
internal sealed record TrackIssueReport(
    string Id, string FileName, string Title, string? Artist, string Analysis,
    List<TrackIssueMark> Marks);
