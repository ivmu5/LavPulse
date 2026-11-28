namespace Laveyana.Audio.Playback;

internal readonly record struct RhythmTemplatePrototype(int TestFold,int Cluster,double Low,double Middle,double High,double Sharpness,int TrainingCount);
internal readonly record struct RhythmTemplateEvidence(string Name,int TestFold,int TrainingEvents,int TestEvents,
    double Coverage,double PhaseGain,double TwoGain,double ThreeGain,double FourGain)
{
    internal double ConditionalFourGain { get; init; }
}
internal readonly record struct RhythmTemplateAccent(string Name,int TestFold,int Cluster,int BeatPosition,int PhaseSlot,int TrainingEvents);
internal sealed record RhythmTemplateResult(RhythmTemplatePrototype[] Prototypes,RhythmTemplateEvidence[] Evidence,
    RhythmTemplateAccent[] Accents,string Reference,string Ranked,string Guarded,long[] RankedBeats,long[] GuardedBeats)
{
    internal static RhythmTemplateResult Empty {get;}=new([],[],[],"","","",[],[]);
    internal string ConditionalRanked {get;init;}="";
    internal string ConditionalGuarded {get;init;}="";
    internal long[] ConditionalRankedBeats {get;init;}=[];
    internal long[] ConditionalGuardedBeats {get;init;}=[];
}

/// <summary>Track-local acoustic recurrence with cross-fitted timbre clusters.
/// A recurring pattern does not identify a musical downbeat, and the four-pulse
/// rank is an experimental hypothesis, not an estimator of probability.</summary>
internal static class RhythmTemplateAnalyzer
{
    internal const int Version=2,Clusters=4,Slots=8,Iterations=12;
    internal const long BlockFrames=8L*PlaybackFormat.SampleRate;
    private const int MinEvents=16;
    private const double MinimumCoverage=.8;
    private readonly record struct Feature(double Low,double Middle,double High,double Sharpness)
    {
        public static Feature operator +(Feature a,Feature b)=>new(a.Low+b.Low,a.Middle+b.Middle,a.High+b.High,a.Sharpness+b.Sharpness);
        public static Feature operator /(Feature a,double b)=>new(a.Low/b,a.Middle/b,a.High/b,a.Sharpness/b);
        internal double Distance(Feature b)
        {
            double l=Low-b.Low,m=Middle-b.Middle,h=High-b.High,s=Sharpness-b.Sharpness;
            return l*l+m*m+h*h+s*s;
        }
    }
    private sealed record Model(Feature[] Centers,int[] Labels,int[] Counts);
    private sealed record Candidate(string Name,long[] Frames,long[] Index,bool Rank);
    private readonly record struct Position(bool Valid,int Slot,long Index);

    internal static RhythmTemplateResult Analyze(RhythmAuditResult audit,long[] legacy,CancellationToken token)
    {
        var events=audit.RoleSelection.Events;
        var candidates=new List<Candidate>();
        void Add(string name,RhythmAuditPath path,bool rank=true)
        {
            if(path.Beats.Length<4)return;
            candidates.Add(new(name,path.Beats.Select(b=>b.Frame).ToArray(),path.Beats.Select(b=>b.Index).ToArray(),rank));
        }
        Add("audit-baseline",audit.Baseline);
        // Preserve this candidate order as part of the development protocol.
        foreach(string name in new[]{"envelope-decay","envelope-hybrid","envelope-rise"})
        {
            var path=audit.EnvelopePaths.FirstOrDefault(p=>p.Name==name);
            if(path is not null)Add(name,path.Path);
        }
        Add("joint",audit.Joint);
        if(legacy.Length>=4)candidates.Add(new("legacy",legacy,LegacyIndices(legacy),true));
        foreach(var path in audit.EnvelopePaths.Where(p=>p.Name=="envelope-refined-decay"))Add(path.Name,path.Path,false);
        return AnalyzeCore(events,candidates,token);
    }

    // Also used by deterministic generated regression fixtures.
    internal static RhythmTemplateResult CheckFixture(RhythmRoleEvent[] events,long[] beats,CancellationToken token)
        =>AnalyzeCore(events,[new("audit-baseline",beats,Enumerable.Range(0,beats.Length).Select(i=>(long)i).ToArray(),true)],token);

    private static RhythmTemplateResult AnalyzeCore(RhythmRoleEvent[] events,List<Candidate> candidates,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(events.Length<2*MinEvents||candidates.Count==0)return RhythmTemplateResult.Empty;
        int n=events.Length;
        var features=new Feature[n];var folds=new int[n];
        for(int i=0;i<n;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            var e=events[i];double sum=Math.Max(1e-9,e.Low+e.Middle+e.High);
            features[i]=new(e.Low/sum,e.Middle/sum,e.High/sum,e.Sharpness);
            folds[i]=(int)((e.Frame/BlockFrames)%2);
        }
        var models=new Model[2];var prototypes=new List<RhythmTemplatePrototype>(2*Clusters);
        for(int test=0;test<2;test++)
        {
            var train=Enumerable.Range(0,n).Where(i=>folds[i]!=test).ToArray();
            if(train.Length<MinEvents||n-train.Length<MinEvents)return RhythmTemplateResult.Empty;
            models[test]=Fit(features,train,token);
            for(int k=0;k<Clusters;k++)
            {
                var v=models[test].Centers[k];
                prototypes.Add(new(test,k,v.Low,v.Middle,v.High,v.Sharpness,models[test].Counts[k]));
            }
        }
        var evidence=new List<RhythmTemplateEvidence>();
        var accents=new List<RhythmTemplateAccent>();
        foreach(var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var positions=Positions(events,candidate,token);
            int valid=positions.Count(p=>p.Valid);
            double coverage=valid/(double)n;
            for(int test=0;test<2;test++)
            {
                var phase=new int[Clusters*Slots];
                var bars=new[]{new int[Clusters*2*Slots],new int[Clusters*3*Slots],new int[Clusters*4*Slots]};
                var labels=models[test].Labels;
                int training=0,testing=0;
                for(int i=0;i<n;i++)
                {
                    if((i&4095)==0)token.ThrowIfCancellationRequested();
                    if(!positions[i].Valid)continue;
                    if(folds[i]==test){testing++;continue;}
                    training++;var p=positions[i];int k=labels[i];
                    phase[k*Slots+p.Slot]++;
                    for(int m=2;m<=4;m++)bars[m-2][(k*m+Modulo(p.Index,m))*Slots+p.Slot]++;
                }
                if(training<MinEvents||testing<MinEvents)continue;
                var phaseTotals=new int[Clusters];
                for(int k=0;k<Clusters;k++)for(int s=0;s<Slots;s++)phaseTotals[k]+=phase[k*Slots+s];
                var gains=new double[4];
                double conditionalFour=0;
                for(int i=0;i<n;i++)
                {
                    if((i&4095)==0)token.ThrowIfCancellationRequested();
                    if(!positions[i].Valid||folds[i]!=test)continue;
                    var p=positions[i];int k=labels[i];int count=phaseTotals[k];
                    double probability=(phase[k*Slots+p.Slot]+1d)/(count+Slots);
                    gains[0]+=Math.Log(probability*Slots);
                    conditionalFour+=ConditionalGain(bars[2][(k*4+Modulo(p.Index,4))*Slots+p.Slot],
                        phase[k*Slots+p.Slot]);
                    for(int m=2;m<=4;m++)
                    {
                        double q=(bars[m-2][(k*m+Modulo(p.Index,m))*Slots+p.Slot]+1d)/(count+m*Slots);
                        gains[m-1]+=Math.Log(q*m/probability);
                    }
                }
                evidence.Add(new(candidate.Name,test,training,testing,coverage,gains[0]/testing,
                    gains[1]/testing,gains[2]/testing,gains[3]/testing)
                    {ConditionalFourGain=conditionalFour/testing});
                for(int k=0;k<Clusters;k++)for(int pos=0;pos<4;pos++)for(int s=0;s<Slots;s++)
                    accents.Add(new(candidate.Name,test,k,pos,s,bars[2][(k*4+pos)*Slots+s]));
            }
        }
        var reference=candidates.FirstOrDefault(p=>p.Name=="envelope-decay")??candidates[0];
        var ranked=reference;double best=double.NegativeInfinity;
        foreach(var candidate in candidates.Where(p=>p.Rank))
        {
            var rows=evidence.Where(e=>e.Name==candidate.Name).ToArray();
            if(rows.Length!=2||rows[0].Coverage<MinimumCoverage)continue;
            double mean=rows.Sum(e=>e.FourGain*e.TestEvents)/rows.Sum(e=>e.TestEvents);
            if(mean>best){best=mean;ranked=candidate;}
        }
        var guarded=reference;
        var baseRows=evidence.Where(e=>e.Name==reference.Name).OrderBy(e=>e.TestFold).ToArray();
        var rankedRows=evidence.Where(e=>e.Name==ranked.Name).OrderBy(e=>e.TestFold).ToArray();
        if(baseRows.Length==2&&rankedRows.Length==2&&rankedRows.Zip(baseRows).All(p=>
            p.First.FourGain>Math.Max(0,p.Second.FourGain)))
            guarded=ranked;
        // Keep the original comparison protocol intact. The regularized
        // conditional model is a separate experimental whole-path selector.
        var conditionalRanked=reference;double conditionalBest=double.NegativeInfinity;
        foreach(var candidate in candidates.Where(p=>p.Rank))
        {
            var rows=evidence.Where(e=>e.Name==candidate.Name).ToArray();
            if(rows.Length!=2||rows[0].Coverage<MinimumCoverage)continue;
            double mean=rows.Sum(e=>e.ConditionalFourGain*e.TestEvents)/rows.Sum(e=>e.TestEvents);
            if(mean>conditionalBest){conditionalBest=mean;conditionalRanked=candidate;}
        }
        var conditionalGuarded=reference;
        var conditionalRows=evidence.Where(e=>e.Name==conditionalRanked.Name).OrderBy(e=>e.TestFold).ToArray();
        if(baseRows.Length==2&&conditionalRows.Length==2&&conditionalRows.Zip(baseRows).All(p=>
            p.First.ConditionalFourGain>Math.Max(0,p.Second.ConditionalFourGain)))
            conditionalGuarded=conditionalRanked;
        return new(prototypes.ToArray(),evidence.ToArray(),accents.ToArray(),reference.Name,ranked.Name,guarded.Name,
            ranked.Frames,guarded.Frames)
        {
            ConditionalRanked=conditionalRanked.Name,ConditionalGuarded=conditionalGuarded.Name,
            ConditionalRankedBeats=conditionalRanked.Frames,ConditionalGuardedBeats=conditionalGuarded.Frames
        };
    }

    // Conditional probability of pulse position given cluster and phase slot.
    // Symmetric Dirichlet(2,2,2,2) prior gives uniform evidence when unseen;
    // unlike v1 this does not mix separately smoothed joint/marginal models.
    internal static double ConditionalGain(int cell,int phaseTotal)
        =>Math.Log(4d*(cell+2d)/(phaseTotal+8d));

    private static Model Fit(Feature[] features,int[] train,CancellationToken token)
    {
        var centers=new Feature[Clusters];Feature mean=default;
        foreach(int i in train)mean+=features[i];
        mean/=train.Length;
        int winner=train[0];double best=-1;
        foreach(int i in train)
        {
            double d=features[i].Distance(mean);
            if(d>best){best=d;winner=i;}
        }
        centers[0]=features[winner];
        for(int k=1;k<Clusters;k++)
        {
            best=-1;
            foreach(int i in train)
            {
                double d=double.PositiveInfinity;
                for(int j=0;j<k;j++)d=Math.Min(d,features[i].Distance(centers[j]));
                if(d>best){best=d;winner=i;}
            }
            centers[k]=features[winner];
        }
        var counts=new int[Clusters];var sums=new Feature[Clusters];
        for(int iteration=0;iteration<Iterations;iteration++)
        {
            token.ThrowIfCancellationRequested();
            Array.Clear(counts);Array.Clear(sums);
            foreach(int i in train)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                int k=Nearest(features[i],centers);counts[k]++;sums[k]+=features[i];
            }
            for(int k=0;k<Clusters;k++)if(counts[k]>0)centers[k]=sums[k]/counts[k];
        }
        var labels=new int[features.Length];Array.Clear(counts);
        for(int i=0;i<labels.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            labels[i]=Nearest(features[i],centers);
        }
        foreach(int i in train)counts[labels[i]]++;
        return new(centers,labels,counts);
    }

    private static int Nearest(Feature x,Feature[] centers)
    {
        int best=0;double distance=x.Distance(centers[0]);
        for(int i=1;i<centers.Length;i++)
        {
            double d=x.Distance(centers[i]);
            if(d<distance){distance=d;best=i;}
        }
        return best;
    }

    private static Position[] Positions(RhythmRoleEvent[] events,Candidate path,CancellationToken token)
    {
        var positions=new Position[events.Length];int j=0;
        for(int i=0;i<events.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            long frame=events[i].Frame;
            while(j+1<path.Frames.Length&&path.Frames[j+1]<=frame)j++;
            if(frame<path.Frames[j]||j+1>=path.Frames.Length)continue;
            long period=path.Frames[j+1]-path.Frames[j];
            if(period<PlaybackFormat.SampleRate/4||period>PlaybackFormat.SampleRate*1.5||
                path.Index[j+1]-path.Index[j]!=1)continue;
            int slot=Math.Clamp((int)Math.Floor((frame-path.Frames[j])*Slots/(double)period+.5),0,Slots);
            positions[i]=new(true,slot%Slots,path.Index[j]+(slot==Slots?1:0));
        }
        return positions;
    }

    // Legacy confirmed grids do not carry an absolute beat index. Infer gaps
    // from the median interval for diagnostics only; this can fail across
    // tempo changes and is deliberately never treated as verified bar phase.
    private static long[] LegacyIndices(long[] beats)
    {
        var gaps=new long[beats.Length-1];
        for(int i=0;i<gaps.Length;i++)gaps[i]=beats[i+1]-beats[i];
        Array.Sort(gaps);
        double median=Math.Max(1,(gaps[(gaps.Length-1)/2]+(double)gaps[gaps.Length/2])/2);
        var indices=new long[beats.Length];
        for(int i=1;i<beats.Length;i++)indices[i]=indices[i-1]+Math.Max(1,(long)Math.Round((beats[i]-beats[i-1])/median));
        return indices;
    }
    private static int Modulo(long index,int modulus)=>(int)((index%modulus+modulus)%modulus);
}
