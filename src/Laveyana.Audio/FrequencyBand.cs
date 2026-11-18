namespace Laveyana.Audio;

/// <summary>The lower and upper frequency boundaries of one spectrum bar, in hertz.</summary>
public readonly record struct FrequencyBand(double LowerHz, double UpperHz);
