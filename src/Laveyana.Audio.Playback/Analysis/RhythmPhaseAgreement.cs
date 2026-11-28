namespace Laveyana.Audio.Playback;

internal readonly record struct RhythmPhaseAgreementRow(long StartFrame,long EndFrame,string Left,string Right,
    int LeftCount,int RightCount,double LeftPeriod,double RightPeriod,int Matches70,
    double OffsetCycles,double Concentration,string Status);

/// <summary>Pairwise agreement, not correctness or a calibrated posterior.
/// Uses only generated grids (never annotations). Fixed 8 s blocks, with a
/// one-to-one match and circular phase measurement at compatible tempos.</summary>
internal static class RhythmPhaseAgreement
{
    internal const int Version=2;
    internal const long BlockFrames=8L*PlaybackFormat.SampleRate;
    internal static RhythmPhaseAgreementRow[] Analyze(RhythmAuditResult result,long[] legacy,long total,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(result.Baseline.States.Length==0||total<=0)return [];
        long horizon=Math.Min(total,result.Baseline.States[^1].Frame+
            RhythmJointDecoder.Stride*(long)RhythmJointDecoder.Hop);
        var paths=new Dictionary<string,long[]>(StringComparer.Ordinal)
        {
            ["audit-baseline"]=result.Baseline.Beats.Select(b=>b.Frame).ToArray(),
            ["joint"]=result.Joint.Beats.Select(b=>b.Frame).ToArray(),
            ["legacy-confirmed"]=legacy
        };
        foreach(var path in result.EnvelopePaths)paths.Add(path.Name,path.Path.Beats.Select(b=>b.Frame).ToArray());
        (string Left,string Right)[] pairs=[
            ("audit-baseline","joint"),("audit-baseline","legacy-confirmed"),
            ("audit-baseline","envelope-decay"),("envelope-decay","legacy-confirmed"),
            ("audit-baseline","envelope-refined-decay"),("envelope-decay","envelope-refined-decay"),
            ("envelope-refined-decay","legacy-confirmed")];
        var rows=new List<RhythmPhaseAgreementRow>();
        foreach(var (left,right) in pairs)
        {
            if(!paths.TryGetValue(left,out var a)||!paths.TryGetValue(right,out var b))continue;
            int ai=0,bi=0;
            for(long start=0;start<horizon;start+=BlockFrames)
            {
                token.ThrowIfCancellationRequested();
                long end=Math.Min(horizon,start+BlockFrames);
                int firstA=ai,firstB=bi;
                while(ai<a.Length&&a[ai]<end)ai++;
                while(bi<b.Length&&b[bi]<end)bi++;
                rows.Add(Compare(start,end,left,right,a.AsSpan(firstA,ai-firstA),b.AsSpan(firstB,bi-firstB)));
            }
        }
        return rows.ToArray();
    }

    internal static RhythmPhaseAgreementRow Compare(long start,long end,string left,string right,
        ReadOnlySpan<long> a,ReadOnlySpan<long> b)
    {
        const long tolerance=PlaybackFormat.SampleRate*70L/1000;
        int matches=0,i=0,j=0;
        while(i<a.Length&&j<b.Length)
        {
            long delta=b[j]-a[i];
            if(delta< -tolerance)j++;
            else if(delta>tolerance)i++;
            else {matches++;i++;j++;}
        }
        double pa=Period(a),pb=Period(b);
        int leftCount=a.Length,rightCount=b.Length;
        RhythmPhaseAgreementRow Row(double offset,double concentration,string status)=>
            new(start,end,left,right,leftCount,rightCount,pa,pb,matches,offset,concentration,status);
        if(a.Length<4||b.Length<4||pa<=0||pb<=0)return Row(0,0,"insufficient");
        if(Math.Abs(pa-pb)>.08*Math.Max(pa,pb))return Row(0,0,"tempo_mismatch");
        var leftPhase = OverlappingPhase(a, b, pb, -1);
        var rightPhase = OverlappingPhase(b, a, pa, 1);
        if (leftPhase.Count < 3 || rightPhase.Count < 3) return Row(0, 0, "insufficient_overlap");

        // Average both directions so swapping the grids reverses the offset
        // without changing concentration. Phase is measured only inside
        // actual, locally regular intervals on the other grid.
        double real = .5 * (leftPhase.Real / leftPhase.Count + rightPhase.Real / rightPhase.Count);
        double imaginary = .5 * (leftPhase.Imaginary / leftPhase.Count + rightPhase.Imaginary / rightPhase.Count);
        double concentration = Math.Clamp(Math.Sqrt(real * real + imaginary * imaginary), 0, 1);
        double offset = Math.Atan2(imaginary, real) / Math.Tau;
        string status = concentration < .7 ? "diffuse" :
            Math.Abs(offset) >= .25 ? "phase_conflict" :
            Math.Abs(offset) <= .125 ? "near_phase" : "phase_offset";
        return Row(offset, concentration, status);
    }

    private static (int Count, double Real, double Imaginary) OverlappingPhase(
        ReadOnlySpan<long> observations, ReadOnlySpan<long> grid, double period, int sign)
    {
        int index = 0, count = 0;
        double real = 0, imaginary = 0;
        foreach (long frame in observations)
        {
            if (frame < grid[0]) continue;
            if (frame > grid[^1]) break;
            while (index+2 < grid.Length && grid[index+1] < frame) index++;
            long interval = grid[index+1] - grid[index];
            if (interval < .67 * period || interval > 1.5 * period) continue;

            double angle = sign * Math.Tau * (frame - grid[index]) / interval;
            (double sine, double cosine) = Math.SinCos(angle);
            real += cosine;
            imaginary += sine;
            count++;
        }
        return (count, real, imaginary);
    }

    private static double Period(ReadOnlySpan<long> beats)
    {
        if(beats.Length<2)return 0;
        int n=beats.Length-1;
        Span<long> gaps=n<=128?stackalloc long[n]:new long[n];
        for(int i=0;i<n;i++)gaps[i]=beats[i+1]-beats[i];
        gaps.Sort();
        return (gaps[(n-1)/2]+(double)gaps[n/2])/2;
    }
}
