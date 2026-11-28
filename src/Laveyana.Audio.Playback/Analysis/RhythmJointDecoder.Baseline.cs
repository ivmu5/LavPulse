namespace Laveyana.Audio.Playback;

internal static partial class RhythmJointDecoder
{
    private static double[,] TempoEvidence(Input input,CancellationToken token)
    {
        var scores=new double[input.Centers.Length,TempoCount];
        var ac=new double[451];var v=new double[1601];var taper=new double[1601];
        var autocorrelation=new RhythmAuditAutocorrelation();
        for(int w=0;w<input.Centers.Length;w++)
        {
            token.ThrowIfCancellationRequested();
            int start=Math.Max(0,input.Centers[w]-800),end=Math.Min(input.Length,input.Centers[w]+801),n=end-start;
            Array.Clear(ac);
            for(int i=0;i<n;i++)taper[i]=Hann(i,n);
            for(int band=0;band<5;band++)
            {
                token.ThrowIfCancellationRequested();
                double mean=0,energy=0;
                for(int i=0;i<n;i++)mean+=input.Bands[band,start+i]/(double)n;
                for(int i=0;i<n;i++){v[i]=(input.Bands[band,start+i]-mean)*taper[i];energy+=v[i]*v[i];}
                if(energy<1e-8)continue;
                autocorrelation.Accumulate(v.AsSpan(0,n),ac,1/(energy*5),token);
            }
            double At(double lag)
            {
                int i=(int)lag;
                if(i>=ac.Length||i>=n)return 0;
                double next=i+1<n&&i+1<ac.Length?ac[i+1]:0;
                return ac[i]+(next-ac[i])*(lag-i);
            }
            for(int j=0;j<TempoCount;j++)
            {
                double p=60d*Rate/(j+MinBpm);
                scores[w,j]=.55*At(p)+.30*At(2*p)+.15*At(3*p);
            }
        }
        return scores;
    }

    private static int[] TempoPath(double[,] scores,CancellationToken token)
    {
        int n=scores.GetLength(0);
        var back=new ushort[n,TempoCount];var previous=new double[TempoCount];var current=new double[TempoCount];
        for(int j=0;j<TempoCount;j++)previous[j]=scores[0,j]-Prior[j];
        for(int w=1;w<n;w++)
        {
            token.ThrowIfCancellationRequested();
            for(int j=0;j<TempoCount;j++)
            {
                double best=double.NegativeInfinity;int index=0;
                for(int k=0;k<TempoCount;k++)
                {
                    double value=previous[k]-TempoCosts[k,j];
                    if(value>best){best=value;index=k;}
                }
                current[j]=best+scores[w,j]-Prior[j];back[w,j]=(ushort)index;
            }
            (previous,current)=(current,previous);
        }
        int winner=Array.IndexOf(previous,previous.Max());var path=new int[n];
        for(int w=n-1;w>=0;w--){path[w]=winner+MinBpm;if(w>0)winner=back[w,winner];}
        return path;
    }

    private static RhythmAuditPath Baseline(Input input,double[,] scores,long total,CancellationToken token)
    {
        int[] tempo=TempoPath(scores,token);var theta=new double[input.Length];
        for(int i=1;i<theta.Length;i++)theta[i]=theta[i-1]+tempo[Math.Min((i-1+Stride/2)/Stride,tempo.Length-1)]/(60d*Rate);
        int n=tempo.Length;var obs=new double[n][];
        for(int w=0;w<n;w++)
        {
            token.ThrowIfCancellationRequested();int center=input.Centers[w];
            obs[w]=Fold(input,Math.Max(0,center-400),Math.Min(input.Length,center+401),i=>theta[i]);
        }
        var previous=(double[])obs[0].Clone();var current=new double[PhaseCount];var back=new byte[n,PhaseCount];
        for(int w=1;w<n;w++)
        {
            token.ThrowIfCancellationRequested();
            for(int j=0;j<PhaseCount;j++)
            {
                double best=double.NegativeInfinity;int index=0;
                for(int k=0;k<PhaseCount;k++)
                {
                    double value=previous[k]-.35*Math.Abs(WrapSigned((j-k)/(double)PhaseCount));
                    if(value>best){best=value;index=k;}
                }
                current[j]=best+obs[w][j];back[w,j]=(byte)index;
            }
            (previous,current)=(current,previous);
        }
        int winner=Array.IndexOf(previous,previous.Max());var selected=new int[n];
        for(int w=n-1;w>=0;w--){selected[w]=winner;if(w>0)winner=back[w,winner];}
        var offsets=new double[n];var states=new RhythmAuditState[n];
        for(int w=0;w<n;w++)
        {
            double offset=selected[w]/(double)PhaseCount;
            offsets[w]=w==0?offset:offsets[w-1]+WrapSigned(offset-offsets[w-1]);
            states[w]=new(input.Centers[w]*(long)Hop,tempo[w],Mod(theta[input.Centers[w]]-offsets[w]),
                scores[w,tempo[w]-MinBpm],obs[w][selected[w]],0);
        }
        for(int i=0;i<theta.Length;i++)
        {
            int w=Math.Min(i/Stride,n-1),next=Math.Min(w+1,n-1);double f=(i%Stride)/(double)Stride;
            theta[i]-=offsets[w]+f*(offsets[next]-offsets[w]);
        }
        return new(states,Emit(input,theta,total,token));
    }
}
