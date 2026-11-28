namespace Laveyana.Audio.Playback;

internal readonly record struct RhythmTransitionEvidence(string Name,int TestFold,int TestEvents,
    int TrainingPairs,int TestPairs,double Gain,double Increment);
internal readonly record struct RhythmSoftBlockComparison(string Candidate,string Reference,long StartFrame,
    int CommonEvents,double CandidateGain,double ReferenceGain,double Difference);

internal static partial class RhythmTemplateAnalyzer
{
    internal const long TransitionMaxGapFrames=PlaybackFormat.SampleRate/2;
    private const double TransitionPrior=8;

    // Never join a pair across an 8 s fold block, missing event position or
    // a long pause. Adjacent events are acoustic attacks, not musical beats.
    internal static bool CanUsePrevious(long previous,long current)
        =>current>previous&&current-previous<=TransitionMaxGapFrames&&
            previous/BlockFrames==current/BlockFrames;

    private static RhythmTransitionEvidence TransitionScore(string name,RhythmRoleEvent[] events,
        Position[] positions,int[] folds,int test,SoftModel model,int testing,CancellationToken token)
    {
        Span<double> single=stackalloc double[Clusters*Slots*4];
        Span<double> pair=stackalloc double[Clusters*Clusters*Slots*4];
        single.Clear();pair.Clear();
        int trainingPairs=0,testPairs=0;
        for(int i=0;i<positions.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            if(!positions[i].Valid||folds[i]==test)continue;
            var p=positions[i];int r=Modulo(p.Index,4);
            bool hasPrevious=i>0&&positions[i-1].Valid&&CanUsePrevious(events[i-1].Frame,events[i].Frame);
            if(hasPrevious)trainingPairs++;
            for(int k=0;k<Clusters;k++)
            {
                double w=model.Weights[i*Clusters+k];
                single[(k*Slots+p.Slot)*4+r]+=w;
                if(hasPrevious)
                    for(int l=0;l<Clusters;l++)
                        pair[((k*Clusters+l)*Slots+p.Slot)*4+r]+=w*model.Weights[(i-1)*Clusters+l];
            }
        }
        for(int at=0;at<single.Length;at+=4)
        {
            double total=single[at]+single[at+1]+single[at+2]+single[at+3];
            for(int r=0;r<4;r++)single[at+r]=(single[at+r]+2)/(total+8);
        }
        // Back off to the current-event model when pair data is scarce.
        for(int k=0;k<Clusters;k++)for(int l=0;l<Clusters;l++)for(int s=0;s<Slots;s++)
        {
            int at=((k*Clusters+l)*Slots+s)*4,baseAt=(k*Slots+s)*4;
            double total=pair[at]+pair[at+1]+pair[at+2]+pair[at+3];
            for(int r=0;r<4;r++)pair[at+r]=(pair[at+r]+TransitionPrior*single[baseAt+r])/(total+TransitionPrior);
        }
        double gain=0,increment=0;
        for(int i=0;i<positions.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            if(!positions[i].Valid||folds[i]!=test)continue;
            var p=positions[i];int r=Modulo(p.Index,4);
            bool hasPrevious=i>0&&positions[i-1].Valid&&CanUsePrevious(events[i-1].Frame,events[i].Frame);
            double baseProbability=0,probability=0;
            for(int k=0;k<Clusters;k++)
            {
                double w=model.Weights[i*Clusters+k];
                baseProbability+=w*single[(k*Slots+p.Slot)*4+r];
                if(hasPrevious)
                    for(int l=0;l<Clusters;l++)
                        probability+=w*model.Weights[(i-1)*Clusters+l]*pair[((k*Clusters+l)*Slots+p.Slot)*4+r];
            }
            if(hasPrevious)testPairs++;
            else probability=baseProbability;
            gain+=Math.Log(4*probability);
            increment+=Math.Log(probability/baseProbability);
        }
        return new(name,test,testing,trainingPairs,testPairs,gain/testing,increment/testing);
    }

    // Compare the existing soft winner with its reference on exactly the
    // same test events. This measures acoustic evidence, not beat accuracy.
    private static RhythmSoftBlockComparison[] CompareSoftBlocks(RhythmRoleEvent[] events,int[] folds,
        SoftModel[] models,Candidate candidate,Candidate reference,CancellationToken token)
    {
        var left=Gains(candidate);
        var right=ReferenceEquals(candidate,reference)?left:Gains(reference);
        var rows=new List<RhythmSoftBlockComparison>();
        for(int i=0;i<events.Length;)
        {
            token.ThrowIfCancellationRequested();
            long block=events[i].Frame/BlockFrames;
            int count=0;double a=0,b=0;
            while(i<events.Length&&events[i].Frame/BlockFrames==block)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                if(double.IsFinite(left[i])&&double.IsFinite(right[i]))
                {count++;a+=left[i];b+=right[i];}
                i++;
            }
            if(count>=8)rows.Add(new(candidate.Name,reference.Name,block*BlockFrames,count,a/count,b/count,(a-b)/count));
        }
        return rows.ToArray();

        double[] Gains(Candidate path)
        {
            var positions=Positions(events,path,token);
            var result=new double[events.Length];Array.Fill(result,double.NaN);
            for(int test=0;test<2;test++)
            {
                int training=0,testing=0;
                for(int i=0;i<positions.Length;i++)
                {
                    if((i&4095)==0)token.ThrowIfCancellationRequested();
                    if(!positions[i].Valid)continue;
                    if(folds[i]==test)testing++;else training++;
                }
                if(training>=MinEvents&&testing>=MinEvents)
                    SoftScore(positions,folds,test,models[test],testing,token,result);
            }
            return result;
        }
    }
}
