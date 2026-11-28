using System.Numerics;

namespace Laveyana.Audio.Playback;

/// <summary>Linear (zero padded) autocorrelation of a real tapered window.
/// Fixed scratch buffers, one allocation per decoder, no circular wrap at the
/// longest period. Normalize by zero-lag energy outside this helper.</summary>
internal sealed class RhythmAuditAutocorrelation
{
    private const int Size=4096;
    private static readonly Complex[] Twiddle=Enumerable.Range(0,Size/2)
        .Select(i=>Complex.FromPolarCoordinates(1,-Math.Tau*i/Size)).ToArray();
    private static readonly int[] Reverse=Enumerable.Range(0,Size).Select(value=>
    {
        int x=value,r=0;
        for(int j=1;j<Size;j<<=1){r=(r<<1)|(x&1);x>>=1;}
        return r;
    }).ToArray();
    private readonly Complex[] _buffer=new Complex[Size];

    internal void Accumulate(ReadOnlySpan<double> samples,Span<double> destination,double scale,
        CancellationToken token)
    {
        if(samples.Length>Size/2||!double.IsFinite(scale))
            throw new ArgumentException("Autocorrelation input exceeds supported window.");
        token.ThrowIfCancellationRequested();
        Array.Clear(_buffer);
        for(int i=0;i<samples.Length;i++)_buffer[Reverse[i]]=new Complex(samples[i],0);
        Transform(token);
        // FFT(power) equals IFFT(power)*N here because the power of a real
        // signal is even. Use the same forward butterfly and no extra copy.
        for(int i=0;i<Size;i++)
        {
            Complex value=_buffer[i];
            _buffer[i]=new Complex(value.Real*value.Real+value.Imaginary*value.Imaginary,0);
        }
        for(int i=0;i<Size;i++)
        {
            int j=Reverse[i];if(j>i)(_buffer[i],_buffer[j])=(_buffer[j],_buffer[i]);
        }
        Transform(token);
        double gain=scale/Size;
        int end=Math.Min(samples.Length,destination.Length);
        for(int lag=0;lag<end;lag++)destination[lag]+=_buffer[lag].Real*gain;
    }

    private void Transform(CancellationToken token)
    {
        for(int length=2;length<=Size;length<<=1)
        {
            token.ThrowIfCancellationRequested();
            int half=length/2,step=Size/length;
            for(int start=0;start<Size;start+=length)
                for(int j=0;j<half;j++)
                {
                    Complex even=_buffer[start+j],odd=_buffer[start+j+half]*Twiddle[j*step];
                    _buffer[start+j]=even+odd;_buffer[start+j+half]=even-odd;
                }
        }
    }
}
