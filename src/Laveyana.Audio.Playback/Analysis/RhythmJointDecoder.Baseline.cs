namespace Laveyana.Audio.Playback;

internal static partial class RhythmJointDecoder
{
    private static (double[,] Baseline, double[,] Hybrid) TempoEvidence(Input input, CancellationToken token)
    {
        var scores = new double[input.Centers.Length, TempoCount];
        var hybrid = new double[input.Centers.Length, TempoCount];
        var correlations = new double[451];
        var samples = new double[1601];
        const double binHz = Rate / (double)RhythmAuditAutocorrelation.Size;
        int minBin = (int)Math.Ceiling(MinBpm / (60 * binHz));
        int maxBin = (int)Math.Floor(12 / binHz);
        var power = new double[maxBin + 1];
        var autocorrelation = new RhythmAuditAutocorrelation();
        for (int w = 0; w < input.Centers.Length; w++)
        {
            token.ThrowIfCancellationRequested();
            int length = Measure(input.Centers[w], 8 * Rate);
            double maximum = MaximumPower();
            for (int j = 0; j < TempoCount; j++)
            {
                scores[w, j] = Correlation(j, length);
                hybrid[w, j] = .5 * (.55 * scores[w, j] + .135 * Fourier(j, maximum));
            }
            length = Measure(input.Centers[w], 4 * Rate);
            maximum = MaximumPower();
            for (int j = 0; j < TempoCount; j++)
                hybrid[w, j] += .5 * (.55 * Correlation(j, length) + .135 * Fourier(j, maximum));
        }
        return (scores, hybrid);

        int Measure(int center, int radius)
        {
            int start = Math.Max(0, center - radius), end = Math.Min(input.Length, center + radius + 1);
            int length = end - start;
            Array.Clear(correlations);
            Array.Clear(power);
            var taper = input.Taper(length);
            for (int band = 0; band < 5; band++)
            {
                token.ThrowIfCancellationRequested();
                double mean = 0, energy = 0;
                for (int i = 0; i < length; i++) mean += input.Bands[band, start+i] / (double)length;
                for (int i = 0; i < length; i++)
                {
                    samples[i] = (input.Bands[band, start+i] - mean) * taper[i];
                    energy += samples[i] * samples[i];
                }
                if (energy >= 1e-8)
                    autocorrelation.Accumulate(samples.AsSpan(0, length), correlations, 1 / (energy * 5), token, power);
            }
            return length;
        }

        double MaximumPower()
        {
            double maximum = 1e-12;
            for (int k = minBin; k <= maxBin; k++) maximum = Math.Max(maximum, power[k]);
            return maximum;
        }

        double Fourier(int index, double maximum)
        {
            double bin = (MinBpm + index) / (60 * binHz);
            int lower = (int)bin;
            double value = power[lower] + (power[lower+1] - power[lower]) * (bin - lower);
            return Math.Sqrt(Math.Clamp(value / maximum, 0, 1));
        }

        double Correlation(int index, int length)
        {
            double At(double lag)
            {
                int lower = (int)lag;
                if (lower >= correlations.Length || lower >= length) return 0;
                double next = lower+1 < length && lower+1 < correlations.Length ? correlations[lower+1] : 0;
                return correlations[lower] + (next - correlations[lower]) * (lag - lower);
            }
            double period = 60d * Rate / (MinBpm + index);
            return .55 * At(period) + .30 * At(2*period) + .15 * At(3*period);
        }
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

    private static RhythmAuditPath Baseline(Input input,double[,] scores,long total,CancellationToken token,
        out double[] cycles)
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
        cycles=theta;
        return new(states,Emit(input,theta,total,token));
    }
}
