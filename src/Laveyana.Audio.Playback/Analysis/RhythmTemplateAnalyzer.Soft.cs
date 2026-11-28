namespace Laveyana.Audio.Playback;

internal readonly record struct RhythmSoftFold(int TestFold,double Temperature,
    double MeanTrainingEntropy,double MeanTestEntropy,int TrainingEvents,int TestEvents);

internal static partial class RhythmTemplateAnalyzer
{
    private const double MinimumTemperature=1e-10;
    private sealed record SoftModel(double[] Weights,RhythmSoftFold Diagnostics);

    // Use the median squared distance to the nearest training centroid as
    // the temperature. No test-fold distances or human beat labels choose it.
    private static SoftModel SoftMembership(Feature[] features,int[] folds,int[] train,Model model,
        int test,CancellationToken token)
    {
        var distances=new double[train.Length];
        for(int i=0;i<train.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            int index=train[i];
            distances[i]=features[index].Distance(model.Centers[model.Labels[index]]);
        }
        Array.Sort(distances);
        double temperature=Math.Max(MinimumTemperature,
            (distances[(distances.Length-1)/2]+distances[distances.Length/2])/2);
        var weights=new double[checked(features.Length*Clusters)];
        Span<double> scratch=stackalloc double[Clusters];
        double trainingEntropy=0,testEntropy=0;int testing=0;
        for(int i=0;i<features.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            for(int k=0;k<Clusters;k++)scratch[k]=features[i].Distance(model.Centers[k]);
            NormalizeMembership(scratch,temperature);
            double entropy=0;
            for(int k=0;k<Clusters;k++)
            {
                double w=scratch[k];
                weights[i*Clusters+k]=w;
                if(w>0)entropy-=w*Math.Log(w);
            }
            if(folds[i]==test){testEntropy+=entropy;testing++;}
            else trainingEntropy+=entropy;
        }
        return new(weights,new(test,temperature,trainingEntropy/train.Length,
            testEntropy/Math.Max(1,testing),train.Length,testing));
    }

    /// <summary>Stable radial-basis membership, not a calibrated posterior
    /// of an instrument class. In-place, allocation-free for each event.</summary>
    internal static void NormalizeMembership(Span<double> distances,double temperature)
    {
        double minimum=double.PositiveInfinity;
        foreach(double d in distances)minimum=Math.Min(minimum,d);
        double sum=0;
        for(int k=0;k<distances.Length;k++)
        {
            distances[k]=Math.Exp(-(distances[k]-minimum)/Math.Max(MinimumTemperature,temperature));
            sum+=distances[k];
        }
        for(int k=0;k<distances.Length;k++)distances[k]/=sum;
    }

    private static double SoftScore(Position[] positions,int[] folds,int test,SoftModel model,
        int testing,CancellationToken token,double[]? eventGains=null)
    {
        // Fractional training counts, normalized conditional distribution
        // per cluster and phase. Use the same symmetric prior as schema 15.
        Span<double> phase=stackalloc double[Clusters*Slots];
        Span<double> cells=stackalloc double[Clusters*Slots*4];
        phase.Clear();cells.Clear();
        for(int i=0;i<positions.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            if(folds[i]==test||!positions[i].Valid)continue;
            var p=positions[i];int r=Modulo(p.Index,4);
            for(int k=0;k<Clusters;k++)
            {
                double w=model.Weights[i*Clusters+k];
                int at=k*Slots+p.Slot;
                phase[at]+=w;cells[at*4+r]+=w;
            }
        }
        // Predict the observed position by marginalizing cluster membership
        // before taking the logarithm. Never condition on a test label.
        for(int at=0;at<phase.Length;at++)
            for(int r=0;r<4;r++)
                cells[at*4+r]=(cells[at*4+r]+2)/(phase[at]+8);
        double gain=0;
        for(int i=0;i<positions.Length;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            if(folds[i]!=test||!positions[i].Valid)continue;
            var p=positions[i];int r=Modulo(p.Index,4);
            double probability=0;
            for(int k=0;k<Clusters;k++)
                probability+=model.Weights[i*Clusters+k]*cells[(k*Slots+p.Slot)*4+r];
            double value=Math.Log(4*probability);
            gain+=value;
            if(eventGains is not null)eventGains[i]=value;
        }
        return gain/testing;
    }
}
