namespace Laveyana.Audio.Playback;

/// <summary>Unwraps a native unsigned 32-bit playback counter, ignoring resets
/// and deltas beyond the data that the caller has submitted.</summary>
internal struct NativeOutputCounter
{
    private uint _last;
    public long Position { get; private set; }

    public long Observe(uint raw, long submitted = long.MaxValue)
    {
        uint delta = unchecked(raw - _last);
        _last = raw;
        // A reset or backward glitch is not a 4 GiB/frame advance.
        if (delta <= int.MaxValue && submitted >= Position &&
            delta <= submitted - Position)
            Position += delta;
        return Position;
    }
}
