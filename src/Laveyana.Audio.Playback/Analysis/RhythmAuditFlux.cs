using System.Numerics;

namespace Laveyana.Audio.Playback;

/// <summary>Audit-inspired 16 kHz stereo-power flux, 100 Hz, on the native PCM clock.
/// Centered FIR decimation differs from FFmpeg and has its own feature version.</summary>
internal sealed class RhythmAuditFlux
{
    internal const int Version=1, HopFrames=480, Rate=100, MaxFrames=1_000_000;
    private const int Size=512, Decimation=3, FilterSize=63, Delay=31, Hop=160;
    private static readonly double[] Fir=BuildFir();
    private static readonly double[] Hann=Enumerable.Range(0,Size)
        .Select(i=>.5-.5*Math.Cos(Math.Tau*i/(Size-1))).ToArray();
    private static readonly Complex[] Twiddle=Enumerable.Range(0,Size/2)
        .Select(i=>Complex.FromPolarCoordinates(1,-Math.Tau*i/Size)).ToArray();
    private static readonly int[] Reverse=Enumerable.Range(0,Size).Select(value=>
    {
        int x=value,r=0;
        for(int j=1;j<Size;j<<=1){r=(r<<1)|(x&1);x>>=1;}
        return r;
    }).ToArray();
    private static readonly int[] BinBand=Enumerable.Range(0,Size/2).Select(i=>
    {
        double hz=i*16000d/Size;
        return hz<30||hz>=7900?-1:hz<100?0:hz<220?1:hz<600?2:hz<2000?3:4;
    }).ToArray();
    private readonly double[] _firL=new double[FilterSize],_firR=new double[FilterSize];
    private readonly float[] _left=new float[Size],_right=new float[Size];
    private readonly Complex[] _fft=new Complex[Size];
    private readonly double[] _previous=new double[Size/2];
    private readonly List<RhythmSpectrumFrame> _frames=[];
    private long _nativeCount,_sampleCount;
    private int _firWrite,_write;
    private bool _hasPrevious,_finished;
    internal IReadOnlyList<RhythmSpectrumFrame> Frames=>_frames;
    internal bool Truncated {get;private set;}

    internal void Add(ReadOnlySpan<float> stereo,CancellationToken token)
    {
        if(_finished)throw new InvalidOperationException("Audit flux already finished.");
        if((stereo.Length&1)!=0)throw new ArgumentException("Stereo frames required.",nameof(stereo));
        token.ThrowIfCancellationRequested();
        for(int i=0;i<stereo.Length;i+=2)
        {
            if(_frames.Count>=MaxFrames){Truncated=true;return;}
            PushNative(stereo[i],stereo[i+1],token);
        }
    }

    internal void Finish(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(_finished)return;
        _finished=true;
        long real=_nativeCount;
        if(real==0||Truncated)return;
        for(int i=0;i<Delay&&_frames.Count<MaxFrames;i++)PushNative(0,0,token);
        long lastCenter=(real-1)/HopFrames*HopFrames/Decimation;
        while(_sampleCount<lastCenter+Size/2&&_frames.Count<MaxFrames)PushSample(0,0,token);
        Truncated=lastCenter/Hop+1>_frames.Count;
    }

    private void PushNative(float left,float right,CancellationToken token)
    {
        _firL[_firWrite]=left;_firR[_firWrite]=right;
        _firWrite=(_firWrite+1)%FilterSize;
        long center=_nativeCount++-Delay;
        if(center<0||center%Decimation!=0)return;
        double l=0,r=0;
        int k=0;
        for(int i=_firWrite;i<FilterSize;i++,k++){l+=Fir[k]*_firL[i];r+=Fir[k]*_firR[i];}
        for(int i=0;i<_firWrite;i++,k++){l+=Fir[k]*_firL[i];r+=Fir[k]*_firR[i];}
        PushSample((float)l,(float)r,token);
    }

    private void PushSample(float left,float right,CancellationToken token)
    {
        _left[_write]=left;_right[_write]=right;
        _write=(_write+1)&(Size-1);_sampleCount++;
        long center=_sampleCount-Size/2;
        if(center<0||center%Hop!=0)return;
        token.ThrowIfCancellationRequested();
        for(int i=0;i<Size;i++)
        {
            int src=(_write+i)&(Size-1);
            _fft[Reverse[i]]=new Complex(_left[src]*Hann[i],_right[src]*Hann[i]);
        }
        for(int length=2;length<=Size;length<<=1)
        {
            int half=length/2,step=Size/length;
            for(int start=0;start<Size;start+=length)
                for(int j=0;j<half;j++)
                {
                    Complex even=_fft[start+j],odd=_fft[start+j+half]*Twiddle[j*step];
                    _fft[start+j]=even+odd;_fft[start+j+half]=even-odd;
                }
        }
        Span<double> flux=stackalloc double[5];Span<int> bins=stackalloc int[5];
        flux.Clear();bins.Clear();
        for(int i=1;i<Size/2;i++)
        {
            int band=BinBand[i];if(band<0)continue;
            Complex a=_fft[i],b=_fft[Size-i];
            double power=(a.Real*a.Real+a.Imaginary*a.Imaginary+b.Real*b.Real+b.Imaginary*b.Imaginary)*.5;
            double value=Math.Log(1+100*Math.Sqrt(power)/Size);
            if(_hasPrevious)flux[band]+=Math.Max(0,value-_previous[i]);
            _previous[i]=value;bins[band]++;
        }
        for(int b=0;b<5;b++)flux[b]/=Math.Max(1,bins[b]);
        _frames.Add(new(center*Decimation,(float)flux[0],(float)flux[1],(float)flux[2],(float)flux[3],(float)flux[4]));
        _hasPrevious=true;
    }

    private static double[] BuildFir()
    {
        const double cutoff=7200d/48000;
        var fir=new double[FilterSize];
        for(int i=0;i<fir.Length;i++)
        {
            int x=i-Delay;
            double sinc=x==0?2*cutoff:Math.Sin(Math.Tau*cutoff*x)/(Math.PI*x);
            fir[i]=sinc*(.54-.46*Math.Cos(Math.Tau*i/(FilterSize-1)));
        }
        double sum=fir.Sum();
        for(int i=0;i<fir.Length;i++)fir[i]/=sum;
        return fir;
    }
}
