using System.Numerics;

namespace Laveyana.Audio.Playback;

public static class Pcm16
{
    private const float DecodeScale = 1f / 32768f;

    /// <summary>Encodes normalized samples as signed 16-bit PCM in the destination span.</summary>
    public static void Encode(ReadOnlySpan<float> input, Span<short> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            output[i] = input[i] <= -1f ? short.MinValue : (short)MathF.Round(input[i] * short.MaxValue);
        }
    }

    /// <summary>Converts signed 16-bit PCM to normalized samples at the original rate.</summary>
    internal static void Decode(ReadOnlySpan<short> input, Span<float> output)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int half = Vector<float>.Count;
            int stride = Vector<short>.Count;
            for (; i <= input.Length - stride; i += stride)
            {
                Vector.Widen(new Vector<short>(input[i..]), out Vector<int> low, out Vector<int> high);
                (Vector.ConvertToSingle(low) * DecodeScale).CopyTo(output[i..]);
                (Vector.ConvertToSingle(high) * DecodeScale).CopyTo(output[(i + half)..]);
            }
        }
        for (; i < input.Length; i++)
        {
            output[i] = input[i] * DecodeScale;
        }
    }
}
