namespace Laveyana.Audio.Playback;

internal readonly record struct RhythmAuditState(long Frame,double Bpm,double PhaseCycles,
    double TempoEvidence,double PhaseEvidence,double Margin);
internal readonly record struct RhythmAuditBeat(long Frame,long Index,bool Observed);
internal sealed record RhythmAuditPath(RhythmAuditState[] States,RhythmAuditBeat[] Beats)
{
    internal static RhythmAuditPath Empty {get;}=new([],[]);
}
internal sealed record RhythmAuditResult(RhythmAuditPath Baseline,RhythmAuditPath Joint,RhythmAuditPath Alternative)
{
    internal static RhythmAuditResult Empty {get;}=new(RhythmAuditPath.Empty,RhythmAuditPath.Empty,RhythmAuditPath.Empty);
}

/// <summary>Two-pass audit baseline and joint beam share features but never use taps or legacy BPM.</summary>
internal static partial class RhythmJointDecoder
{
    internal const int Version=1;
    internal const int Rate=RhythmAuditFlux.Rate,Hop=RhythmAuditFlux.HopFrames,Stride=200,PhaseCount=32,MinBpm=40,MaxBpm=240,
        TempoCount=MaxBpm-MinBpm+1;
    private static readonly double[] Prior=Enumerable.Range(MinBpm,TempoCount)
        .Select(b=>.070*Math.Pow(Math.Log2(b/120d)/.65,2)).ToArray();
    private static readonly double[,] TempoCosts=BuildCosts();
    private static readonly double[] Smooth=Enumerable.Range(-5,11)
        .Select(i=>Math.Exp(-.5*Math.Pow(i/1.2,2))).ToArray();
    private static readonly double SmoothSum=Smooth.Sum();

    internal static RhythmAuditResult Analyze(IReadOnlyList<RhythmSpectrumFrame> raw,long totalFrames,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(raw.Count>RhythmAuditFlux.MaxFrames||totalFrames<=0)throw new InvalidDataException("Audit feature bounds.");
        for(int i=0;i<raw.Count;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            if(raw[i].Frame!=(long)i*Hop||raw[i].Frame>=totalFrames)
                throw new InvalidDataException("Invalid audit feature clock.");
            for(int b=0;b<5;b++)
                if(!float.IsFinite(raw[i].Band(b))||raw[i].Band(b)<0)
                    throw new InvalidDataException("Invalid audit spectral value.");
        }
        if(raw.Count<400)return RhythmAuditResult.Empty;
        var input=Prepare(raw,token);
        if(input.Activity.All(x=>x<=1e-6))return RhythmAuditResult.Empty;
        var scores=TempoEvidence(input,token);
        var baseline=Baseline(input,scores,totalFrames,token);
        var (joint,alternative)=Joint(input,scores,totalFrames,token);
        return new(baseline,joint,alternative);
    }

    private sealed record Input(float[,] Bands,double[] Low,double[] High,double[] Activity,int[] Centers,double Gate)
    {
        internal int Length=>Low.Length;
    }

    private static Input Prepare(IReadOnlyList<RhythmSpectrumFrame> raw,CancellationToken token)
    {
        int n=raw.Count;
        var bands=new float[5,n];var low=new double[n];var high=new double[n];var meanRaw=new double[n];
        var prefix=new double[n+1];
        for(int b=0;b<5;b++)
        {
            prefix[0]=0;
            for(int i=0;i<n;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                double value=raw[i].Band(b);prefix[i+1]=prefix[i]+value;meanRaw[i]+=value/5;
            }
            for(int i=0;i<n;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                int from=Math.Max(0,i-Rate),to=Math.Min(n,i+Rate+1);
                double sum=prefix[to]-prefix[from]+Math.Max(0,Rate-i)*raw[0].Band(b)+
                    Math.Max(0,i+Rate+1-n)*raw[^1].Band(b);
                double value=raw[i].Band(b),v=value/(value+Math.Max(0,sum)/(2*Rate+1)+1e-6);
                bands[b,i]=(float)v;
                if(b<2)low[i]+=v/2;else high[i]+=v/3;
            }
        }
        var activity=new double[n];var deque=new int[n];int head=0,tail=0,next=0;
        for(int i=0;i<n;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            while(next<n&&next<=i+50)
            {
                while(tail>head&&meanRaw[deque[tail-1]]<=meanRaw[next])tail--;
                deque[tail++]=next++;
            }
            while(head<tail&&deque[head]<i-50)head++;
            activity[i]=meanRaw[deque[head]];
        }
        var sorted=(double[])activity.Clone();Array.Sort(sorted);
        double q=.9*(n-1);int qi=(int)q;
        double quantile=sorted[qi]+(sorted[Math.Min(qi+1,n-1)]-sorted[qi])*(q-qi);
        return new(bands,low,high,activity,Enumerable.Range(0,(n-1)/Stride+1).Select(i=>i*Stride).ToArray(),
            Math.Max(1e-6,.015*quantile));
    }

    private static double[,] BuildCosts()
    {
        var costs=new double[TempoCount,TempoCount];
        for(int a=0;a<TempoCount;a++)for(int b=0;b<TempoCount;b++)
            costs[a,b]=Math.Min(2.5*Math.Abs(Math.Log2((a+MinBpm)/(double)(b+MinBpm))),.66);
        return costs;
    }
    private static double WrapSigned(double x)=>x-Math.Floor(x+.5);
    private static double Mod(double x)=>x-Math.Floor(x);
    private static double Hann(int i,int n)=>n<2?1:.5-.5*Math.Cos(Math.Tau*i/(n-1));

    private static double[] Fold(Input input,int start,int end,Func<int,double> cycles)
    {
        int n=end-start;
        if(n<2)return new double[PhaseCount];
        double ml=0,mh=0;
        for(int i=start;i<end;i++){ml+=input.Low[i]/n;mh+=input.High[i]/n;}
        Span<double> l=stackalloc double[PhaseCount],h=stackalloc double[PhaseCount],counts=stackalloc double[PhaseCount];
        l.Clear();h.Clear();counts.Clear();
        for(int i=start;i<end;i++)
        {
            double p=Mod(cycles(i))*PhaseCount;
            if (p>=PhaseCount) p=0; // roundoff at the circular boundary
            int k=(int)p,next=(k+1)%PhaseCount;
            double w=Hann(i-start,n),v=p-k,a=w*(1-v),b=w*v;
            counts[k]+=a;counts[next]+=b;
            l[k]+=a*(input.Low[i]-ml);l[next]+=b*(input.Low[i]-ml);
            h[k]+=a*(input.High[i]-mh);h[next]+=b*(input.High[i]-mh);
        }
        for(int i=0;i<PhaseCount;i++){l[i]/=Math.Max(counts[i],1e-6);h[i]/=Math.Max(counts[i],1e-6);}
        var result=new double[PhaseCount];
        for(int i=0;i<PhaseCount;i++)
        {
            double low=0,high=0,opposite=0;
            for(int k=-5;k<=5;k++)
            {
                int p=(i+k+PhaseCount)%PhaseCount;double weight=Smooth[k+5]/SmoothSum;
                low+=weight*l[p];high+=weight*h[p];opposite+=weight*h[(p+PhaseCount/2)%PhaseCount];
            }
            result[i]=.65*low+.35*Math.Max(high,opposite);
        }
        return result;
    }

    private static RhythmAuditBeat[] Emit(Input input,double[] cycles,long total,CancellationToken token)
    {
        var beats=new List<RhythmAuditBeat>();
        for(int i=0;i+1<cycles.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            double a=cycles[i],b=cycles[i+1];long index=(long)Math.Floor(a)+1;
            if(b<a||index>b||input.Activity[i]<=input.Gate)continue;
            long frame=(long)Math.Round((i+(index-a)/(b-a))*Hop);
            if(frame<0||frame>=total||beats.Count>0&&(frame<=beats[^1].Frame||index<=beats[^1].Index))continue;
            double peak=0;
            for(int j=Math.Max(0,i-5);j<=Math.Min(input.Length-1,i+5);j++)peak=Math.Max(peak,input.Low[j]);
            beats.Add(new(frame,index,peak>=.35));
        }
        return beats.ToArray();
    }
}
