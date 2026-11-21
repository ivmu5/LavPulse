using System.Diagnostics;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

/// <summary>Follows sampled deck positions at display cadence with bounded prediction between audio blocks.</summary>
internal struct DeckPlayhead
{
    private const double PredictionSeconds = .070;
    private const double CorrectionSeconds = .12;
    private const long DiscontinuityFrames = PlaybackFormat.SampleRate * 3 / 10;
    private long _sample;
    private double _sampleTime;
    private double _frameTime;
    private double _position;
    private double _speed;

    public long DisplayPosition => (long)Math.Round(_position);

    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Observe(long position, double speed, double time, bool snap)
    {
        if (snap || position < _sample || position - _sample > DiscontinuityFrames)
        {
            _position = position;
            _frameTime = time;
            _sampleTime = time;
        }
        else if (position != _sample || speed != _speed)
        {
            _sampleTime = time;
        }

        _sample = position;
        _speed = speed;
    }

    public long Advance(double time, long end)
    {
        double elapsed = Math.Max(0, time - _frameTime);
        _frameTime = time;
        double framesPerSecond = PlaybackFormat.SampleRate * _speed;
        double elapsedSinceSample = Math.Clamp(time - _sampleTime, 0, PredictionSeconds);
        double target = Math.Min(end, _sample + elapsedSinceSample * framesPerSecond);
        double nominal = _position + elapsed * framesPerSecond;
        double rate = Math.Clamp(framesPerSecond + (target - nominal) / CorrectionSeconds,
            framesPerSecond * .5, framesPerSecond * 1.6);
        // New blocks can move the target slightly back. Correct the speed without stepping back.
        double limit = Math.Min(end, _sample + PredictionSeconds * framesPerSecond);
        _position = Math.Max(_position, Math.Min(limit, _position + elapsed * rate));
        return DisplayPosition;
    }
}
