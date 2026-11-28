namespace Laveyana.Audio.Playback;

/// <summary>Shape and soft acoustic-role proxies, not instrument labels or probabilities.
/// Flux measures positive spectral changes; it cannot prove a sustained bass note.</summary>
internal readonly record struct RhythmRoleEvent(long Frame,double Strength,double Sharpness,
    double TailRatio,double Low,double Middle,double High,double Kick,double Snare,double Hat,double BassTail);

internal static class RhythmEventClassifier
{
    internal const int Version=1,RefractoryHops=6;
    internal static RhythmRoleEvent[] Analyze(IReadOnlyList<RhythmSpectrumFrame> raw,
        float[,] normalized,double activityGate,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int count=raw.Count;
        if(normalized.GetLength(0)!=5||normalized.GetLength(1)!=count)
            throw new ArgumentException("Event feature dimensions.",nameof(normalized));
        if(count<3)return [];
        var pulse=new double[count];
        for(int i=0;i<count;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            for(int band=0;band<5;band++)pulse[i]+=normalized[band,i]/5d;
        }
        var peaks=new List<int>();
        for(int i=1;i<count-1;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            if(pulse[i]<.28||pulse[i]<pulse[i-1]||pulse[i]<=pulse[i+1])continue;
            double rawMean=0;
            for(int b=0;b<5;b++)rawMean+=raw[i].Band(b)/5d;
            if(rawMean<=activityGate)continue;
            if(peaks.Count>0&&i-peaks[^1]<RefractoryHops)
            {
                if(pulse[i]>pulse[peaks[^1]])peaks[^1]=i;
            }
            else peaks.Add(i);
        }
        var events=new RhythmRoleEvent[peaks.Count];
        Span<double> maximum=stackalloc double[5];
        for(int n=0;n<peaks.Count;n++)
        {
            if((n&1023)==0)token.ThrowIfCancellationRequested();
            int i=peaks[n];
            maximum.Clear();
            for(int j=Math.Max(0,i-2);j<=Math.Min(count-1,i+2);j++)
                for(int b=0;b<5;b++)maximum[b]=Math.Max(maximum[b],normalized[b,j]);
            double low=(maximum[0]+maximum[1])/2,middle=(maximum[2]+maximum[3])/2,high=maximum[4];
            int tailEnd=Math.Min(count,i+11);
            double tail=0;
            for(int j=i+3;j<tailEnd;j++)tail+=pulse[j];
            tail/=Math.Max(1,tailEnd-i-3);
            double tailRatio=tail/Math.Max(1e-6,pulse[i]);
            double sharpness=Math.Clamp(1-tailRatio,0,1);
            double kick=low*(.4+.6*sharpness)*(1-.35*high);
            double snare=middle*(.4+.6*sharpness)*(1-.45*low);
            double hat=high*(1-.5*low)*(1-.4*middle);
            double bassTail=low*(1-sharpness);
            events[n]=new(raw[i].Frame,pulse[i],sharpness,tailRatio,low,middle,high,kick,snare,hat,bassTail);
        }
        return events;
    }
}
