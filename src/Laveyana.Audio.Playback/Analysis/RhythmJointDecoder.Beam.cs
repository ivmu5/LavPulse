namespace Laveyana.Audio.Playback;

internal static partial class RhythmJointDecoder
{
    internal const int BeamWidth=16;
    private readonly record struct Node(int Bpm,int Phase,double Tempo,double Evidence,double Score,int Parent);

    private static (RhythmAuditPath Best,RhythmAuditPath Alternative) Joint(Input input,double[,] scores,
        long total,CancellationToken token)
    {
        int n=input.Centers.Length;var history=new Node[n][];
        for(int w=0;w<n;w++)
        {
            token.ThrowIfCancellationRequested();int center=input.Centers[w];
            var seeds=new List<int>();
            void Add(int bpm)
            {
                if(bpm>=MinBpm&&bpm<=MaxBpm&&seeds.Count<16&&seeds.All(v=>Math.Abs(v-bpm)>1))seeds.Add(bpm);
            }
            var peaks=Enumerable.Range(MinBpm,TempoCount).Where(b=>
                (b==MinBpm||scores[w,b-MinBpm]>=scores[w,b-MinBpm-1])&&
                (b==MaxBpm||scores[w,b-MinBpm]>=scores[w,b-MinBpm+1]))
                .OrderByDescending(b=>scores[w,b-MinBpm]-Prior[b-MinBpm]).Take(6).ToArray();
            foreach(int bpm in peaks)Add(bpm);
            if(w>0)foreach(var node in history[w-1].Take(4))Add(node.Bpm);
            foreach(int bpm in peaks.Take(2))
            {
                Add(bpm*2);Add((int)Math.Round(bpm/2d));Add((int)Math.Round(bpm*1.5));Add((int)Math.Round(bpm/1.5));
            }
            var candidates=new List<Node>(seeds.Count*PhaseCount);
            foreach(int bpm in seeds)
            {
                double frequency=bpm/(60d*Rate);
                var profile=Fold(input,Math.Max(0,center-200),Math.Min(input.Length,center+201),i=>(i-center)*frequency);
                double evidence=scores[w,bpm-MinBpm];
                for(int p=0;p<PhaseCount;p++)
                {
                    double phase=profile[(PhaseCount-p)%PhaseCount];
                    double emission=evidence-Prior[bpm-MinBpm]+1.5*phase;
                    double best=0;int parent=-1;
                    if(w>0)
                    {
                        best=double.NegativeInfinity;var previous=history[w-1];
                        for(int j=0;j<previous.Length;j++)
                        {
                            Node a=previous[j];
                            double predicted=a.Phase/(double)PhaseCount+(a.Bpm+bpm)*Stride/(120d*Rate);
                            double phaseError=Math.Abs(WrapSigned(p/(double)PhaseCount-predicted));
                            double cost=TempoCosts[a.Bpm-MinBpm,bpm-MinBpm]+.50*phaseError;
                            double value=a.Score-cost;
                            if(value>best){best=value;parent=j;}
                        }
                    }
                    candidates.Add(new(bpm,p,evidence,phase,best+emission,parent));
                }
            }
            var chosen=new List<Node>(BeamWidth);
            foreach(var node in candidates.OrderByDescending(x=>x.Score))
            {
                if(chosen.Any(a=>Math.Abs(a.Bpm-node.Bpm)<=1&&
                    Math.Abs(WrapSigned((a.Phase-node.Phase)/(double)PhaseCount))<.12))continue;
                chosen.Add(node);if(chosen.Count==BeamWidth)break;
            }
            history[w]=chosen.ToArray();
        }
        var primary=Backtrack(history,0,input,total,token);var alternative=RhythmAuditPath.Empty;
        for(int k=1;k<history[^1].Length;k++)
        {
            var other=Backtrack(history,k,input,total,token);
            int distinct=primary.States.Zip(other.States).Count(pair=>
                Math.Abs(pair.First.Bpm-pair.Second.Bpm)>.04*pair.First.Bpm||
                Math.Abs(WrapSigned(pair.First.PhaseCycles-pair.Second.PhaseCycles))>=.20);
            if(distinct>=Math.Max(1,n/4)){alternative=other;break;}
        }
        return(primary,alternative);
    }

    private static RhythmAuditPath Backtrack(Node[][] history,int winner,Input input,long total,CancellationToken token)
    {
        int n=history.Length;var states=new RhythmAuditState[n];
        for(int w=n-1;w>=0;w--)
        {
            token.ThrowIfCancellationRequested();var node=history[w][winner];
            double other=history[w].Where((_,i)=>i!=winner).Select(x=>x.Score).DefaultIfEmpty(node.Score).Max();
            states[w]=new(input.Centers[w]*(long)Hop,node.Bpm,node.Phase/(double)PhaseCount,node.Tempo,node.Evidence,node.Score-other);
            winner=node.Parent;
        }
        var cycles=new double[input.Length];cycles[0]=states[0].PhaseCycles;
        for(int w=0;w<n;w++)
        {
            token.ThrowIfCancellationRequested();
            int start=input.Centers[w],end=w+1<n?input.Centers[w+1]:input.Length-1;
            double first=states[w].Bpm/(60*Rate),last=w+1<n?states[w+1].Bpm/(60*Rate):first;
            bool jump=Math.Abs(first-last)>.08*Math.Max(first,last);
            double Advance(int delta)
            {
                double length=Math.Max(1,end-start),x=delta/length;
                return jump?first*Math.Min(delta,length/2)+last*Math.Max(0,delta-length/2):
                    delta*(first+.5*(last-first)*x);
            }
            double predicted=cycles[start]+Advance(end-start);
            double correction=w+1<n?WrapSigned(states[w+1].PhaseCycles-predicted):0;
            for(int i=start+1;i<=end;i++)cycles[i]=cycles[start]+Advance(i-start)+correction*(i-start)/Math.Max(1,end-start);
        }
        return new(states,Emit(input,cycles,total,token));
    }
}
