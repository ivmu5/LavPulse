namespace Laveyana.Audio.Playback;

/// <summary>Continuity-constrained tempo path. Keeps the original bounded
/// neighborhood, global-jump cost, arithmetic and strict tie-breaking.</summary>
internal static class RhythmTempoPath
{
    public static int[] Select(float[,] ranking,double resolution,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int windows=ranking.GetLength(0), candidates=ranking.GetLength(1);
        if(windows==0||candidates==0)return [];
        if(candidates>short.MaxValue)throw new ArgumentOutOfRangeException(nameof(ranking));
        var backtrack=new short[windows,candidates];
        var previous=new double[candidates];
        var next=new double[candidates];
        const int neighborhood=24;
        Span<double> penalty=stackalloc double[neighborhood+1];
        for(int d=0;d<penalty.Length;d++)penalty[d]=.022*d*resolution;
        for(int c=0;c<candidates;c++)previous[c]=ranking[0,c];
        for(int w=1;w<windows;w++)
        {
            token.ThrowIfCancellationRequested();
            int global=Best(previous);
            double globalValue=previous[global]-.45;
            for(int c=0;c<candidates;c++)
            {
                int best=global;
                double value=globalValue;
                int end=Math.Min(candidates-1,c+neighborhood);
                for(int earlier=Math.Max(0,c-neighborhood);earlier<=end;earlier++)
                {
                    double score=previous[earlier]-penalty[Math.Abs(c-earlier)];
                    if(score<=value)continue;
                    best=earlier;value=score;
                }
                next[c]=value+ranking[w,c];
                backtrack[w,c]=(short)best;
            }
            (previous,next)=(next,previous);
        }
        var path=new int[windows];
        int selected=Best(previous);
        for(int w=windows-1;w>=0;w--)
        {
            path[w]=selected;
            if(w>0)selected=backtrack[w,selected];
        }
        return path;
    }

    private static int Best(double[] values)
    {
        int best=0;
        for(int i=1;i<values.Length;i++)
            if(values[i]>values[best])best=i;
        return best;
    }
}
