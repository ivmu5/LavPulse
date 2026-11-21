using System.Numerics;

namespace Laveyana.Audio.Playback;

public static class Pcm16
{
    private const float DecodeScale = 1f / 32768f;

    /// <summary>Encodes normalized samples as signed 16-bit PCM in the destination span.</summary>
    public static void Encode(ReadOnlySpan<float> input, Span<short> output)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && output.Length >= input.Length)
        {
            int half = Vector<float>.Count;
            int stride = Vector<short>.Count;
            for (; i <= input.Length - stride; i += stride)
            {
                var low = new Vector<float>(input[i..]);
                var high = new Vector<float>(input[(i + half)..]);
                // The public encoder also accepts samples outside the normalized range.
                if (!Vector.LessThanOrEqualAll(Vector.Abs(low), Vector<float>.One) ||
                    !Vector.LessThanOrEqualAll(Vector.Abs(high), Vector<float>.One))
                {
                    EncodeScalar(input.Slice(i, stride), output.Slice(i, stride));
                    continue;
                }

                Vector.Narrow(EncodeNormalized(low), EncodeNormalized(high)).CopyTo(output[i..]);
            }
        }
        EncodeScalar(input[i..], output[i..]);
    }

    private static Vector<int> EncodeNormalized(Vector<float> input)
    {
        var rounded = Vector.ConvertToInt32Native(Vector.Round(input * short.MaxValue));
        return Vector.ConditionalSelect(Vector.Equals(input, new Vector<float>(-1f)),
            new Vector<int>(short.MinValue), rounded);
    }

    private static void EncodeScalar(ReadOnlySpan<float> input, Span<short> output)
    {
        for (int i = 0; i < input.Length; i++)
            output[i] = input[i] <= -1f ? short.MinValue : (short)MathF.Round(input[i] * short.MaxValue);
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
