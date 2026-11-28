namespace Laveyana.Audio.Playback;

internal sealed record RhythmEnvelopePath(string Name,RhythmAuditPath Path);

internal static partial class RhythmJointDecoder
{
    internal const int EnvelopePhaseVersion=2;

    /// <summary>Phase evidence from the existing filter-band mean-absolute
    /// envelope. This is a different observation of the same PCM, not an
    /// independent statistical sample or an instrument separator.
    /// Keep the native clock, tempo and activity gate of the audit baseline.
    /// No new FFT, annotation input or promotion to playback.</summary>
    private static RhythmEnvelopePath[] EnvelopePhasePaths(Input input,RhythmAuditPath baseline,
        double[] baselineCycles,IReadOnlyList<RhythmEnergy>? envelope,int envelopeHop,
        long total,CancellationToken token,RhythmMetricPath[] refinedPaths,double[,] scores)
    {
        // The current native producer emits one envelope per 480 frames.
        // Do not reinterpret old/mismatched clocks or extrapolate missing data.
        if(envelope is null||envelopeHop!=Hop||envelope.Count<input.Length)return [];
        int n=input.Length;
        var relative=new double[n];var rise=new double[n];var prefix=new double[n+1];
        var low=new double[n];var lowDecay=new double[n];var high=new double[n];
        for(int band=0;band<3;band++)
        {
            prefix[0]=0;
            for(int i=0;i<n;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                double value=band==0?envelope[i].Low:band==1?envelope[i].Mid:envelope[i].High;
                if(!double.IsFinite(value)||value<0)return [];
                relative[i]=value;
                prefix[i+1]=prefix[i]+value;
            }
            double first=relative[0],last=relative[^1];
            for(int i=0;i<n;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                double mean=LocalMean(prefix,first,last,i,n);
                relative[i]/=relative[i]+mean+1e-6;
            }
            prefix[0]=0;
            for(int i=0;i<n;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                double previous=(relative[Math.Max(0,i-1)]+relative[Math.Max(0,i-2)]+
                    relative[Math.Max(0,i-3)])/3;
                rise[i]=Math.Max(0,relative[i]-previous);
                prefix[i+1]=prefix[i]+rise[i];
            }
            for(int i=0;i<n;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                double strength=rise[i]/(rise[i]+LocalMean(prefix,rise[0],rise[^1],i,n)+1e-6);
                if(band==0)
                {
                    low[i]=strength;
                    double tail=0;
                    for(int j=6;j<=10;j++)tail+=relative[Math.Min(n-1,i+j)]/5;
                    double decay=Math.Max(0,relative[i]-tail);
                    lowDecay[i]=strength*(.5+.5*decay);
                }
                else high[i]+=strength/2;
            }
        }
        // A constant envelope provides no phase evidence at all.
        if(!low.Any(v=>v>1e-6)&&!high.Any(v=>v>1e-6))return [];
        var paths=new List<RhythmEnvelopePath>(4);
        paths.Add(new("envelope-rise",Decode(input with { Low=low,High=high })));
        paths.Add(new("envelope-decay",Decode(input with { Low=lowDecay,High=high })));
        var decayHigh=(double[])high.Clone();
        for(int i=0;i<n;i++)
        {
            if((i&4095)==0)token.ThrowIfCancellationRequested();
            low[i]=.5*(low[i]+input.Low[i]);
            high[i]=.5*(high[i]+input.High[i]);
        }
        // Preserve all three schema-12 paths before trying a fractional
        // tempo trajectory. The decay path must use the unblended high band.
        paths.Add(new("envelope-hybrid",Decode(input with { Low=low,High=high })));
        var refined=refinedPaths.FirstOrDefault(p=>p.Numerator==1&&p.Denominator==1);
        if(refined is not null&&refined.Path.States.Length==baseline.States.Length)
        {
            var tempoStates=refined.Path.States;
            var corrected=new double[n];
            double correction=0;
            for(int i=0;i<n;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                if(i>0)
                {
                    int w=Math.Min((i-1+Stride/2)/Stride,tempoStates.Length-1);
                    correction+=(tempoStates[w].Bpm-baseline.States[w].Bpm)/(60d*Rate);
                }
                corrected[i]=baselineCycles[i]+correction;
            }
            paths.Add(new("envelope-refined-decay",Decode(input with { Low=lowDecay,High=decayHigh },
                corrected,tempoStates)));
        }
        return paths.ToArray();

        RhythmAuditPath Decode(Input phaseInput,double[]? trajectory=null,RhythmAuditState[]? tempoStates=null)
        {
            var phaseTrajectory=trajectory??baselineCycles;
            int count=input.Centers.Length;
            var observations=new double[count][];
            for(int w=0;w<count;w++)
            {
                token.ThrowIfCancellationRequested();
                int center=input.Centers[w];
                observations[w]=Fold(phaseInput,Math.Max(0,center-400),Math.Min(n,center+401),i=>phaseTrajectory[i]);
            }
            int[] selected=SelectMetricPhase(observations,Enumerable.Range(0,PhaseCount).ToArray(),token);
            var offsets=new double[count];var states=new RhythmAuditState[count];
            for(int w=0;w<count;w++)
            {
                double offset=selected[w]/(double)PhaseCount;
                offsets[w]=w==0?offset:offsets[w-1]+WrapSigned(offset-offsets[w-1]);
                var state=baseline.States[w];
                if(tempoStates is not null)
                {
                    double bpm=tempoStates[w].Bpm,at=bpm-MinBpm;
                    int lower=(int)at;
                    double evidence=scores[w,lower]+(scores[w,Math.Min(lower+1,TempoCount-1)]-
                        scores[w,lower])*(at-lower);
                    state=state with { Bpm=bpm,TempoEvidence=evidence };
                }
                states[w]=state with
                {
                    PhaseCycles=Mod(phaseTrajectory[input.Centers[w]]-offsets[w]),
                    PhaseEvidence=observations[w][selected[w]],Margin=0
                };
            }
            var cycles=new double[n];
            for(int i=0;i<n;i++)
            {
                if((i&4095)==0)token.ThrowIfCancellationRequested();
                int w=Math.Min(i/Stride,count-1),next=Math.Min(w+1,count-1);
                double f=(i%Stride)/(double)Stride;
                cycles[i]=phaseTrajectory[i]-offsets[w]-f*(offsets[next]-offsets[w]);
            }
            // Retain original spectral observed flag semantics and pause gate;
            // new phase evidence is not a calibrated event probability.
            return AnnotateActivity(new(states,Emit(input,cycles,total,token)),input,token);
        }
    }

    private static double LocalMean(double[] prefix,double first,double last,int i,int n)
    {
        int from=Math.Max(0,i-Rate),to=Math.Min(n,i+Rate+1);
        double sum=prefix[to]-prefix[from]+Math.Max(0,Rate-i)*first+
            Math.Max(0,i+Rate+1-n)*last;
        return Math.Max(0,sum)/(2*Rate+1);
    }
}
