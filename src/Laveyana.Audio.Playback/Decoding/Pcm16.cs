namespace Laveyana.Audio.Playback;

public static class Pcm16
{
    /// <summary>Encodes normalized samples as signed 16-bit PCM in the destination span.</summary>
    public static void Encode(ReadOnlySpan<float> input, Span<short> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = input[i] <= -1f ? short.MinValue : (short)MathF.Round(input[i] * short.MaxValue);
        }
    }
}
