using System.Numerics;

namespace Laveyana.Audio.Playback;

/// <summary>Normalizes decoder and custom PCM identically. Input may alias output exactly.</summary>
internal static class PcmNormalization
{
    public static float Sample(float value) => float.IsFinite(value)?Math.Clamp(value,-1f,1f):0;

    public static void Copy(ReadOnlySpan<float> input,Span<float> output)
    {
        if(output.Length<input.Length)throw new ArgumentException("PCM destination is too small.",nameof(output));
        int i=0;
        if(Vector.IsHardwareAccelerated)
        {
            int width=Vector<float>.Count;
            var max=Vector<float>.One;
            var min=-max;
            var finiteMax=new Vector<float>(float.MaxValue);
            for(;i<=input.Length-width;i+=width)
            {
                var value=new Vector<float>(input[i..]);
                value=Vector.ConditionalSelect(Vector.LessThanOrEqual(Vector.Abs(value),finiteMax),value,Vector<float>.Zero);
                Vector.Min(max,Vector.Max(min,value)).CopyTo(output[i..]);
            }
        }
        for(;i<input.Length;i++)output[i]=Sample(input[i]);
    }
}
