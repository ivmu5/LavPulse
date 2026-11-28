namespace Laveyana.Audio.Playback;

internal static partial class RhythmJointDecoder
{
    // Independent, bounded phase paths on the baseline tempo trajectory.
    // This is a metrical hypothesis generator, not a joint-meter posterior.
    // A rational ratio may be musically ambiguous even with perfect audio.
    internal const int MaxMetricBeats=100_000,MaxMetricStates=50_000;
    private static readonly (int Numerator,int Denominator)[] MetricRatios =
        [(1,1),(1,2),(2,1),(2,3),(3,2),(3,4),(4,3),(1,3),(3,1)];

    private static (RhythmMetricPath[] Paths,bool BudgetReached) MetricPaths(Input input,double[,] scores,
        RhythmAuditPath baseline,double[] baselineCycles,long total,CancellationToken token,bool refined=false)
    {
        var paths=new List<RhythmMetricPath>();
        int stateCount=0,beatCount=0;
        int n=input.Centers.Length;
        var scaled=new double[input.Length];
        var cycles=new double[input.Length];
        foreach(var (numerator,denominator) in MetricRatios)
        {
            token.ThrowIfCancellationRequested();
            double ratio=numerator/(double)denominator;
            // Do not clamp an out-of-range ratio: that would silently change
            // the meaning and continuity of the metrical transformation.
            if(baseline.States.Any(s=>s.Bpm*ratio<MinBpm||s.Bpm*ratio>MaxBpm))continue;
            int lanes=Math.Max(2,denominator);
            // Conservative full-family budget. Never retain only the first
            // parity because later lanes exceeded a memory limit.
            if(stateCount+(long)lanes*n>MaxMetricStates||
                beatCount+lanes*(input.Length/(double)Rate*MaxBpm/60+4)>MaxMetricBeats)
                return(paths.ToArray(),true);
            double[] nominal=baseline.States.Select(s=>s.Bpm*ratio).ToArray();
            double[] tempos=refined?RefineMetricTempo(nominal,scores,token):nominal;
            double correction=0;
            for(int i=0;i<scaled.Length;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                if(refined&&i>0)
                {
                    int index=Math.Min((i-1+Stride/2)/Stride,n-1);
                    correction+=(tempos[index]-nominal[index])/(60d*Rate);
                }
                scaled[i]=baselineCycles[i]*ratio+correction;
            }
            var observations=new double[n][];
            for(int w=0;w<n;w++)
            {
                token.ThrowIfCancellationRequested();
                int center=input.Centers[w];
                observations[w]=Fold(input,Math.Max(0,center-400),Math.Min(input.Length,center+401),i=>scaled[i]);
            }
            var family=new List<RhythmMetricPath>(lanes);
            for(int lane=0;lane<lanes;lane++)
            {
                token.ThrowIfCancellationRequested();
                // Each lane has a disjoint search region with a guard gap.
                // Prefixes from one parity can never replace another parity.
                // +/- 20% of lane spacing allows local phase correction.
                double center=lane/(double)lanes,radius=.20/lanes;
                var bins=Enumerable.Range(0,PhaseCount)
                    .Where(p=>Math.Abs(WrapSigned(p/(double)PhaseCount-center))<=radius+1e-12)
                    .ToArray();
                var selected=SelectMetricPhase(observations,bins,token);
                var states=new RhythmAuditState[n];var offsets=new double[n];
                double tempoSum=0,phaseSum=0;int active=0;
                for(int w=0;w<n;w++)
                {
                    double offset=selected[w]/(double)PhaseCount;
                    offsets[w]=w==0?offset:offsets[w-1]+WrapSigned(offset-offsets[w-1]);
                    double bpm=tempos[w];
                    double at=bpm-MinBpm;int lower=(int)at;
                    double evidence=scores[w,lower]+(scores[w,Math.Min(lower+1,TempoCount-1)]-
                        scores[w,lower])*(at-lower);
                    double phase=observations[w][selected[w]];
                    int sample=input.Centers[w];
                    states[w]=new(sample*(long)Hop,bpm,Mod(scaled[sample]-offsets[w]),evidence,phase,0);
                    if(input.Activity[sample]>input.Gate)
                    {
                        tempoSum+=evidence;phaseSum+=phase;active++;
                    }
                }
                for(int i=0;i<cycles.Length;i++)
                {
                    if((i&4095)==0)token.ThrowIfCancellationRequested();
                    int w=Math.Min(i/Stride,n-1),next=Math.Min(w+1,n-1);
                    double f=(i%Stride)/(double)Stride;
                    cycles[i]=scaled[i]-offsets[w]-f*(offsets[next]-offsets[w]);
                }
                var path=AnnotateActivity(new(states,Emit(input,cycles,total,token)),input,token);
                family.Add(new(numerator,denominator,lane,lanes,tempoSum/Math.Max(1,active),
                    phaseSum/Math.Max(1,active),path));
            }
            int addedBeats=family.Sum(p=>p.Path.Beats.Length);
            if(beatCount+addedBeats>MaxMetricBeats)return(paths.ToArray(),true);
            paths.AddRange(family);stateCount+=lanes*n;beatCount+=addedBeats;
        }
        return(paths.ToArray(),false);
    }

    private static int[] SelectMetricPhase(double[][] observations,int[] bins,CancellationToken token)
    {
        int n=observations.Length,k=bins.Length;
        var back=new byte[n,k];
        var previous=bins.Select(p=>observations[0][p]).ToArray();
        var current=new double[k];
        for(int w=1;w<n;w++)
        {
            token.ThrowIfCancellationRequested();
            for(int j=0;j<k;j++)
            {
                double best=double.NegativeInfinity;int parent=0;
                for(int i=0;i<k;i++)
                {
                    double value=previous[i]-.35*Math.Abs(WrapSigned((bins[j]-bins[i])/(double)PhaseCount));
                    if(value>best){best=value;parent=i;}
                }
                current[j]=best+observations[w][bins[j]];
                back[w,j]=(byte)parent;
            }
            (previous,current)=(current,previous);
        }
        var selected=new int[n];int winner=Array.IndexOf(previous,previous.Max());
        for(int w=n-1;w>=0;w--)
        {
            selected[w]=bins[winner];
            if(w>0)winner=back[w,winner];
        }
        return selected;
    }
}
