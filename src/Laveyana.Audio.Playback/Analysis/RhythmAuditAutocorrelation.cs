using System.Numerics;

namespace Laveyana.Audio.Playback;

/// <summary>Linear (zero padded) autocorrelation of a real tapered window.
/// Fixed scratch buffers, one allocation per decoder, no circular wrap at the
/// longest period. Normalize by zero-lag energy outside this helper.</summary>
internal sealed class RhythmAuditAutocorrelation
{
    private const int Size=4096;
    private static readonly RhythmFftPlan Fft=new(Size);
    private readonly Complex[] _buffer=new Complex[Size];

    internal void Accumulate(ReadOnlySpan<double> samples,Span<double> destination,double scale,
        CancellationToken token)
    {
        if(samples.Length>Size/2||!double.IsFinite(scale))
            throw new ArgumentException("Autocorrelation input exceeds supported window.");
        token.ThrowIfCancellationRequested();
        Array.Clear(_buffer);
        for(int i=0;i<samples.Length;i++)_buffer[Fft.Reverse(i)]=new Complex(samples[i],0);
        Fft.Transform(_buffer,token);
        // FFT(power) equals IFFT(power)*N here because the power of a real
        // signal is even. Use the same forward butterfly and no extra copy.
        for(int i=0;i<Size;i++)
        {
            Complex value=_buffer[i];
            _buffer[i]=new Complex(value.Real*value.Real+value.Imaginary*value.Imaginary,0);
        }
        for(int i=0;i<Size;i++)
        {
            int j=Fft.Reverse(i);if(j>i)(_buffer[i],_buffer[j])=(_buffer[j],_buffer[i]);
        }
        Fft.Transform(_buffer,token);
        double gain=scale/Size;
        int end=Math.Min(samples.Length,destination.Length);
        for(int lag=0;lag<end;lag++)destination[lag]+=_buffer[lag].Real*gain;
    }

}
