namespace Laveyana.Audio.Playback;

/// <summary>Filters the mixed stereo signal using a cascade of peaking filters.</summary>
internal sealed class MasterEqualizerSource(IPcmSource source) : IPcmSource
{
    private sealed record Profile(Coefficients[] Filters, double Gain);
    private static readonly Profile Bypass = new([], 1);
    private readonly int _rampFrames = Math.Max(1, source.SampleRate / 100);
    private readonly FilterBank _first = new();
    private readonly FilterBank _second = new();
    private Profile _requested = Bypass;
    private bool _firstIsActive = true;
    private int _remaining;

    public int SampleRate => source.SampleRate;

    /// <summary>Prepares filter coefficients away from the render thread and publishes them as a unit.</summary>
    public void Configure(EqualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var filters = new List<Coefficients>(EqualizerSettings.Frequencies.Count);
        if (settings.Enabled)
        {
            var frequencies = EqualizerSettings.Frequencies;
            for (int band = 0; band < frequencies.Count; band++)
            {
                double gain = settings.GainAt(band);
                double center = frequencies[band];
                if (gain == 0 || center >= SampleRate / 2d) continue;
                // The closer high-frequency bands need narrower peaks than the octave-spaced bands.
                double lower = band == 0 ? center / Math.Sqrt(frequencies[1] / center) : Math.Sqrt(center * frequencies[band - 1]);
                double upper = band == frequencies.Count - 1 ? center * Math.Sqrt(center / frequencies[band - 1])
                    : Math.Sqrt(center * frequencies[band + 1]);
                filters.Add(Coefficients.Peaking(SampleRate, center, center / (upper - lower), gain));
            }
        }
        var outputGain = settings.Enabled ? Math.Pow(10, settings.OutputGainDb / 20) : 1;
        Volatile.Write(ref _requested, filters.Count == 0 && outputGain == 1 ? Bypass : new Profile(filters.ToArray(), outputGain));
    }

    /// <summary>Starts a new output run with fresh filter state and the latest settings.</summary>
    public void Reset()
    {
        _firstIsActive = true;
        _remaining = 0;
        _first.Use(Volatile.Read(ref _requested));
        _second.Use(Bypass);
    }

    /// <summary>Processes only valid PCM frames and blends filter changes over ten milliseconds.</summary>
    public int Read(Span<float> destination)
    {
        int frames = source.Read(destination);
        if((uint)frames>(uint)(destination.Length/PlaybackFormat.Channels))
            throw new InvalidDataException("Invalid PCM frame count before equalizer.");
        if (frames == 0) return 0;

        var active = _firstIsActive ? _first : _second;
        var target = _firstIsActive ? _second : _first;
        var requested = Volatile.Read(ref _requested);
        if (_remaining == 0 && !ReferenceEquals(active.Settings, requested))
        {
            target.Use(requested);
            _remaining = _rampFrames;
        }
        int frame = 0;
        for (; frame < frames && _remaining > 0; frame++, _remaining--)
        {
            int sample = frame * PlaybackFormat.Channels;
            float left = float.IsFinite(destination[sample]) ? destination[sample] : 0;
            float right = float.IsFinite(destination[sample+1]) ? destination[sample+1] : 0;
            destination[sample]=left;
            destination[sample+1]=right;
            float blend = (float)(_rampFrames - _remaining + 1) / _rampFrames;
            active.Process(ref left, ref right);
            target.Process(ref destination[sample], ref destination[sample + 1]);
            destination[sample] = left + (destination[sample] - left) * blend;
            destination[sample + 1] = right + (destination[sample + 1] - right) * blend;
            if (_remaining == 1)
            {
                _firstIsActive = !_firstIsActive;
                active = target;
            }
        }

        if (!ReferenceEquals(active.Settings, Bypass))
        {
            for (int sample = frame * PlaybackFormat.Channels; sample < frames * PlaybackFormat.Channels; sample += PlaybackFormat.Channels)
            {
                active.Process(ref destination[sample], ref destination[sample + 1]);
            }
        }
        return frames;
    }

    private readonly record struct Coefficients(double B0, double B1, double B2, double A1, double A2)
    {
        public static Coefficients Peaking(int sampleRate, double center, double q, double gain)
        {
            double amplitude = Math.Pow(10, gain / 40);
            double angle = 2 * Math.PI * center / sampleRate;
            double alpha = Math.Sin(angle) / (2 * q);
            double mid = -2 * Math.Cos(angle);
            double a0 = 1 + alpha / amplitude;
            return new((1 + alpha * amplitude) / a0, mid / a0, (1 - alpha * amplitude) / a0,
                mid / a0, (1 - alpha / amplitude) / a0);
        }
    }

    private struct FilterState
    {
        private double _z1;
        private double _z2;

        public double Process(double input, in Coefficients filter)
        {
            double output = input * filter.B0 + _z1;
            _z1 = input * filter.B1 - filter.A1 * output + _z2;
            _z2 = input * filter.B2 - filter.A2 * output;
            return output;
        }
    }

    private sealed class FilterBank
    {
        private readonly FilterState[] _left = new FilterState[EqualizerSettings.Frequencies.Count];
        private readonly FilterState[] _right = new FilterState[EqualizerSettings.Frequencies.Count];
        public Profile Settings { get; private set; } = Bypass;

        public void Use(Profile settings)
        {
            Settings = settings;
            Array.Clear(_left);
            Array.Clear(_right);
        }

        public void Process(ref float left, ref float right)
        {
            double a = float.IsFinite(left) ? left * Settings.Gain : 0;
            double b = float.IsFinite(right) ? right * Settings.Gain : 0;
            var filters = Settings.Filters;
            for (int band = 0; band < filters.Length; band++)
            {
                a = _left[band].Process(a, in filters[band]);
                b = _right[band].Process(b, in filters[band]);
            }
            left = (float)a;
            right = (float)b;
        }
    }
}
