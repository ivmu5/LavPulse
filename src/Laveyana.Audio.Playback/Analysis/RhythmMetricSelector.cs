namespace Laveyana.Audio.Playback;

internal readonly record struct RhythmRoleScore(double Score,double AttackRecall,double BeatSupport,
    double HatAlignment,double Alternation,double Contrast,double PeriodFrames,int BeatCount,int EventCount)
{
    internal bool Valid=>Score>=0;
    internal static RhythmRoleScore Missing(int beats,int events)=>new(-1,0,0,0,0,0,0,beats,events);
}
internal readonly record struct RhythmRoleCandidate(long StartFrame,long EndFrame,int PathId,
    RhythmRoleScore Evidence,bool LocallySupported,bool Eligible);
internal readonly record struct RhythmRoleDecision(long StartFrame,long EndFrame,int SelectedPathId,
    int LocalBestPathId,double BaselineScore,double BestScore,double RunnerUpGap,
    bool Ambiguous,string Reason);
internal readonly record struct RhythmRoleBeat(RhythmAuditBeat Beat,int PathId);
internal sealed record RhythmRoleSelection(RhythmRoleEvent[] Events,RhythmRoleCandidate[] Candidates,
    RhythmRoleDecision[] Decisions,RhythmRoleBeat[] Beats)
{
    internal static RhythmRoleSelection Empty {get;}=new([],[],[],[]);
}

/// <summary>Conservative shadow selection. No annotations, no learned probabilities.
/// Alternatives require repeated local support, then compatible boundary intervals.
/// The baseline is always a feasible path, including its inactive gaps.</summary>
internal static class RhythmMetricSelector
{
    internal const int Version=1;
    internal const long BlockFrames=8L*PlaybackFormat.SampleRate;
    internal const double MinimumGain=.05,SwitchCost=.03;

    internal static RhythmRoleSelection Select(RhythmRoleEvent[] events,RhythmAuditPath baseline,
        RhythmMetricPath[] alternatives,long total,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(total<=0||baseline.States.Length==0)return RhythmRoleSelection.Empty;
        // Bound analysis to the feature trajectory, not the entire audio file
        // if its feature stream was truncated.
        long horizon=Math.Min(total,baseline.States[^1].Frame+
            RhythmJointDecoder.Stride*(long)RhythmJointDecoder.Hop);
        int windows=checked((int)((horizon-1)/BlockFrames+1));
        var paths=new[]{baseline}.Concat(alternatives.Select(p=>p.Path)).ToArray();
        int count=paths.Length;
        var scores=new RhythmRoleScore[windows,count];
        var slices=new (int Start,int End)[windows,count];
        var supported=new bool[windows,count];var eligible=new bool[windows,count];
        var cursors=new int[count];int eventCursor=0;
        for(int w=0;w<windows;w++)
        {
            token.ThrowIfCancellationRequested();
            long start=w*BlockFrames,end=Math.Min(horizon,start+BlockFrames);
            int eventStart=eventCursor;
            while(eventCursor<events.Length&&events[eventCursor].Frame<end)eventCursor++;
            var localEvents=events.AsSpan(eventStart,eventCursor-eventStart);
            for(int p=0;p<count;p++)
            {
                var beats=paths[p].Beats;
                int first=cursors[p];
                while(cursors[p]<beats.Length&&beats[cursors[p]].Frame<end)cursors[p]++;
                slices[w,p]=(first,cursors[p]);
                scores[w,p]=Evaluate(beats.AsSpan(first,cursors[p]-first),localEvents);
            }
            supported[w,0]=eligible[w,0]=true;
            for(int p=1;p<count;p++)
                supported[w,p]=scores[w,0].Valid&&scores[w,p].Valid&&
                    scores[w,p].Score>=scores[w,0].Score+MinimumGain;
        }
        for(int w=0;w<windows;w++)
            for(int p=1;p<count;p++)
                eligible[w,p]=supported[w,p]&&
                    (w>0&&supported[w-1,p]||w+1<windows&&supported[w+1,p]);

        var back=new int[windows,count];
        var previous=new double[count];var current=new double[count];
        for(int p=0;p<count;p++)previous[p]=eligible[0,p]?scores[0,p].Score:double.NegativeInfinity;
        for(int w=1;w<windows;w++)
        {
            token.ThrowIfCancellationRequested();
            Array.Fill(current,double.NegativeInfinity);
            for(int p=0;p<count;p++)
            {
                if(!eligible[w,p])continue;
                for(int q=0;q<count;q++)
                {
                    if(!double.IsFinite(previous[q]))continue;
                    if(p!=q&&!Compatible(w,q,p))continue;
                    double value=previous[q]+scores[w,p].Score-(p==q?0:SwitchCost);
                    if(value>current[p]){current[p]=value;back[w,p]=q;}
                }
            }
            (previous,current)=(current,previous);
        }
        int winner=Array.IndexOf(previous,previous.Max());
        var chosen=new int[windows];
        for(int w=windows-1;w>=0;w--)
        {
            chosen[w]=winner;
            if(w>0)winner=back[w,winner];
        }
        var diagnostics=new List<RhythmRoleCandidate>(windows*count);
        var decisions=new RhythmRoleDecision[windows];
        var output=new List<RhythmRoleBeat>();
        for(int w=0;w<windows;w++)
        {
            token.ThrowIfCancellationRequested();
            long start=w*BlockFrames,end=Math.Min(horizon,start+BlockFrames);
            int localBest=0,validCount=0;double best=-1,runner=-1;
            for(int p=0;p<count;p++)
            {
                var score=scores[w,p];
                diagnostics.Add(new(start,end,p-1,score,supported[w,p],eligible[w,p]));
                if(!score.Valid)continue;
                validCount++;
                if(score.Score>best){runner=best;best=score.Score;localBest=p;}
                else if(score.Score>runner)runner=score.Score;
            }
            int selected=chosen[w];
            double gap=validCount>=2?best-runner:0;
            bool ambiguous=validCount<2||gap<MinimumGain;
            string reason=selected!=0?"supported_continuous_alternative":
                !scores[w,0].Valid?"insufficient_baseline_evidence":
                localBest==0?"baseline_best":
                !supported[w,localBest]?"insufficient_gain":
                !eligible[w,localBest]?"isolated_evidence":"continuity_or_path_cost";
            decisions[w]=new(start,end,selected-1,localBest-1,scores[w,0].Score,best,gap,ambiguous,reason);
            var slice=slices[w,selected];
            for(int i=slice.Start;i<slice.End;i++)
            {
                var beat=paths[selected].Beats[i];
                if(output.Count>0&&beat.Frame<=output[^1].Beat.Frame)
                    throw new InvalidDataException("Nonmonotone selected beat coordinates.");
                // Source index is retained as provenance, not a global count
                // across unrelated metric paths.
                output.Add(new(beat,selected-1));
            }
        }
        return new(events,diagnostics.ToArray(),decisions,output.ToArray());

        bool Compatible(int w,int left,int right)
        {
            var a=slices[w-1,left];var b=slices[w,right];
            double p=scores[w-1,left].PeriodFrames,q=scores[w,right].PeriodFrames;
            if(a.End<=a.Start||b.End<=b.Start||p<=0||q<=0)return false;
            double gap=paths[right].Beats[b.Start].Frame-(double)paths[left].Beats[a.End-1].Frame;
            double period=(p+q)/2;
            return Math.Abs(p-q)<=.08*Math.Max(p,q)&&Math.Abs(gap-period)<=.20*period;
        }
    }

    internal static RhythmRoleScore Evaluate(ReadOnlySpan<RhythmAuditBeat> beats,
        ReadOnlySpan<RhythmRoleEvent> events)
    {
        if(beats.Length<4||events.Length<4)return RhythmRoleScore.Missing(beats.Length,events.Length);
        // An 8 s block can contain only a few dozen beats under the decoder's
        // 40–240 BPM range. Use bounded stack scratch, heap for unusual inputs.
        int n=beats.Length;
        Span<long> intervals=n<=128?stackalloc long[n-1]:new long[n-1];
        for(int i=1;i<n;i++)intervals[i-1]=beats[i].Frame-beats[i-1].Frame;
        intervals.Sort();
        double period=Math.Clamp((intervals[(n-2)/2]+(double)intervals[(n-1)/2])/2,
            PlaybackFormat.SampleRate/4d,PlaybackFormat.SampleRate*1.5);
        Span<double> support=n<=128?stackalloc double[n]:new double[n];
        Span<double> kick=n<=128?stackalloc double[n]:new double[n];
        Span<double> snare=n<=128?stackalloc double[n]:new double[n];
        support.Clear();kick.Clear();snare.Clear();
        double totalRole=0,hitRole=0,totalHat=0,hitHat=0,contrast=0;
        int nearest=0;
        foreach(var evt in events)
        {
            while(nearest+1<n&&Math.Abs(evt.Frame-beats[nearest+1].Frame)<
                Math.Abs(evt.Frame-beats[nearest].Frame))nearest++;
            double d=(evt.Frame-beats[nearest].Frame)/period;
            double on=Kernel(d),off=Kernel(Math.Abs(d)-.5);
            double role=evt.Kick+evt.Snare+.25*evt.BassTail;
            totalRole+=role;hitRole+=role*on;
            totalHat+=evt.Hat;hitHat+=evt.Hat*Math.Max(on,off);
            contrast+=role*(on-off);
            support[nearest]=Math.Max(support[nearest],on*Math.Min(1,role));
            kick[nearest]=Math.Max(kick[nearest],on*evt.Kick);
            snare[nearest]=Math.Max(snare[nearest],on*evt.Snare);
        }
        double recall=hitRole/Math.Max(1e-9,totalRole),hat=hitHat/Math.Max(1e-9,totalHat);
        contrast/=Math.Max(1e-9,totalRole);
        double precision=0,alternation=0;
        for(int i=0;i<n;i++)
        {
            precision+=support[i]/n;
            if(i+1<n)alternation+=Math.Max(kick[i]*snare[i+1],snare[i]*kick[i+1])/(n-1);
        }
        double score=.35*recall+.35*precision+.15*hat+.10*alternation+.05*contrast;
        return new(Math.Clamp(score,0,1),recall,precision,hat,alternation,contrast,period,n,events.Length);
    }
    private static double Kernel(double d)=>Math.Exp(-.5*Math.Pow(d/.12,2));
}
