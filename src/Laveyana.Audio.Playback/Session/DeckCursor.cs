namespace Laveyana.Audio.Playback;

internal sealed class DeckCursor(long length, long start)
{
    private SeekPoint _pending = new(ClampStart(length,start));
    private SeekPoint? _applied;
    private long _frame;

    public long Length { get; } = length;

    private static long ClampStart(long length,long start)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return Math.Clamp(start,0,length);
    }
    public SeekPoint Pending => Volatile.Read(ref _pending);

    public long Position
    {
        get
        {
            var pending = Pending;
            return ReferenceEquals(pending, Volatile.Read(ref _applied)) ? Volatile.Read(ref _frame) : pending.Frame;
        }
    }

    /// <summary>Publishes a new seek request that supersedes older pending requests.</summary>
    public void Seek(long frame) => Volatile.Write(ref _pending, new SeekPoint(Math.Clamp(frame, 0, Length)));

    /// <summary>Publishes the reader's position and the latest seek it has applied.</summary>
    public void Report(long frame, SeekPoint? applied)
    {
        Volatile.Write(ref _frame, frame);
        Volatile.Write(ref _applied, applied);
    }

    public sealed class SeekPoint(long frame)
    {
        public long Frame { get; } = frame;
    }
}
