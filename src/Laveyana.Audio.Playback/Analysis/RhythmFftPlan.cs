using System.Numerics;

namespace Laveyana.Audio.Playback;

/// <summary>Shared immutable radix-2 plan; callers own their scratch arrays.
/// Different sample rates, windows and hop clocks remain separate.</summary>
internal sealed class RhythmFftPlan
{
    private readonly int _size;
    private readonly Complex[] _twiddle;
    private readonly int[] _reverse;

    internal RhythmFftPlan(int size)
    {
        if(size<2||(size&(size-1))!=0)throw new ArgumentOutOfRangeException(nameof(size));
        _size=size;
        _twiddle=Enumerable.Range(0,size/2)
            .Select(i=>Complex.FromPolarCoordinates(1,-Math.Tau*i/size)).ToArray();
        _reverse=Enumerable.Range(0,size).Select(value=>
        {
            int x=value,r=0;
            for(int j=1;j<size;j<<=1){r=(r<<1)|(x&1);x>>=1;}
            return r;
        }).ToArray();
    }

    internal int Reverse(int index)=>_reverse[index];

    /// <summary>Input is already in bit-reversed order. Preserve the exact
    /// original butterfly and arithmetic order for feature compatibility.</summary>
    internal void Transform(Complex[] buffer,CancellationToken token=default)
    {
        if(buffer.Length!=_size)throw new ArgumentException("FFT scratch size.",nameof(buffer));
        for(int length=2;length<=_size;length<<=1)
        {
            token.ThrowIfCancellationRequested();
            int half=length/2,step=_size/length;
            for(int start=0;start<_size;start+=length)
                for(int j=0;j<half;j++)
                {
                    Complex even=buffer[start+j],odd=buffer[start+j+half]*_twiddle[j*step];
                    buffer[start+j]=even+odd;buffer[start+j+half]=even-odd;
                }
        }
    }
}
