namespace Laveyana.Audio.Playback;

internal static partial class RhythmJointDecoder
{
    /// <summary>Local tempo search restricted to a rational family's nominal
    /// trajectory. Independent evidence may refine the tempo without changing
    /// the family's identity. Phase is decoded after integration of the residual.</summary>
    private static double[] RefineMetricTempo(double[] nominal,double[,] scores,CancellationToken token)
    {
        int n=nominal.Length;
        var seeds=new int[n][];
        var back=new int[n][];
        double[] previous=[];
        for(int w=0;w<n;w++)
        {
            token.ThrowIfCancellationRequested();
            double radius=Math.Max(1,.03*nominal[w]);
            int lo=Math.Max(MinBpm,(int)Math.Ceiling(nominal[w]-radius));
            int hi=Math.Min(MaxBpm,(int)Math.Floor(nominal[w]+radius));
            var candidates=seeds[w]=Enumerable.Range(lo,hi-lo+1).ToArray();
            var current=new double[candidates.Length];var pointers=back[w]=new int[candidates.Length];
            for(int j=0;j<candidates.Length;j++)
            {
                double residual=Math.Log2(candidates[j]/nominal[w]);
                double emission=scores[w,candidates[j]-MinBpm]-.10*Math.Pow(residual/.043,2);
                double best=0;
                if(w>0)
                {
                    best=double.NegativeInfinity;
                    for(int k=0;k<previous.Length;k++)
                    {
                        double priorResidual=Math.Log2(seeds[w-1][k]/nominal[w-1]);
                        double value=previous[k]-.8*Math.Abs(priorResidual-residual);
                        if(value>best){best=value;pointers[j]=k;}
                    }
                }
                current[j]=best+emission;
            }
            previous=current;
        }
        int winner=Array.IndexOf(previous,previous.Max());var result=new double[n];
        for(int w=n-1;w>=0;w--)
        {
            token.ThrowIfCancellationRequested();
            int bpm=seeds[w][winner],i=bpm-MinBpm;
            double delta=0;
            if(i>0&&i+1<TempoCount)
            {
                double left=scores[w,i-1],middle=scores[w,i],right=scores[w,i+1];
                double curvature=left-2*middle+right;
                if(curvature< -1e-9)
                    delta=Math.Clamp(.5*(left-right)/curvature,-.5,.5);
            }
            double radius=Math.Max(1,.03*nominal[w]);
            result[w]=Math.Clamp(bpm+delta,Math.Max(MinBpm,nominal[w]-radius),
                Math.Min(MaxBpm,nominal[w]+radius));
            if(w>0)winner=back[w][winner];
        }
        return result;
    }
}
