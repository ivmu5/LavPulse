using Laveyana.Audio.Playback;

internal static partial class Program
{
    private static void CheckRhythmContinuity()
    {
        const int windows = 12, candidates = 401;
        int Index(double bpm) => (int)((bpm - 40) * 2);
        var raw = new float[windows,candidates];
        var periodicity = new float[windows,candidates];
        var phase = new float[windows,candidates];
        void Set(int w, double bpm, float a, float q)
        {
            int c = Index(bpm);
            raw[w,c] = a;
            periodicity[w,c] = q;
            phase[w,c] = .25f;
        }
        var path = Enumerable.Repeat(Index(64), windows).ToArray();
        for (int w = 0; w < windows; w++)
        {
            Set(w, 64, .18f, .12f);
            Set(w, 128, .23f, .17f);
        }
        // Both candidates initially cover every window. Stronger raw and
        // periodicity scores alone cannot satisfy the coverage-gain gate.
        RhythmTempoContinuity.Resolve(path, raw, periodicity, phase, 40, .5, default);
        Require(path.All(c => c == Index(64)),
            "Equal coverage bypassed the required support gain for a slow alias.");
        // Two unsupported old windows give the alternative 1/6 more coverage
        // (> .08), while preserving the required raw and periodicity margins.
        for (int w = 0; w < 2; w++) Set(w, 64, .18f, .10f);
        RhythmTempoContinuity.Resolve(path, raw, periodicity, phase, 40, .5, default);
        Require(path.All(c => c == Index(128)), "A supported doubled beat did not replace its slow alias.");
        Array.Fill(path, Index(64));
        for (int w = 0; w < windows; w++)
        {
            Set(w, 64, .18f, .12f);
            Set(w, 128, .25f, .04f);
        }
        RhythmTempoContinuity.Resolve(path, raw, periodicity, phase, 40, .5, default);
        Require(path.All(c => c == Index(64)), "Weak harmonic evidence overwrote a genuine slow beat.");
        Array.Clear(raw); Array.Clear(periodicity); Array.Clear(phase);
        Array.Fill(path, Index(226));
        for (int w = 0; w < windows; w++)
        {
            Set(w, 226, .40f, .10f);
            Set(w, 113, .19f, .14f);
        }
        RhythmTempoContinuity.Resolve(path, raw, periodicity, phase, 40, .5, default);
        Require(path.All(c => c == Index(113)), "A corroborated slower pulse did not replace its high subdivision.");
        Array.Fill(path, Index(226));
        for (int w = 0; w < windows; w++) Set(w, 113, .19f, .04f);
        RhythmTempoContinuity.Resolve(path, raw, periodicity, phase, 40, .5, default);
        Require(path.All(c => c == Index(226)), "An independently unsupported half tempo overwrote a fast beat.");
        var tempos = Enumerable.Repeat(100d, windows).ToArray();
        var quality = Enumerable.Repeat(.3f, windows).ToArray();
        var eligible = Enumerable.Repeat(true, windows).ToArray();
        var hits = Enumerable.Repeat(12, windows).ToArray();
        eligible[3] = eligible[6] = eligible[7] = eligible[10] = false;
        Set(3, 100, .145f, .13f);
        Set(6, 100, .145f, .13f);
        Set(7, 100, .145f, .13f);
        Set(10, 100, .145f, .02f);
        RhythmTempoContinuity.RecoverSingleWindows(tempos, quality, eligible, raw, periodicity, phase, hits, 40, .5, default);
        Require(eligible[3] && quality[3] == .145f && !eligible[6] && !eligible[7] && !eligible[10],
            "Recovery inflated confidence, chained weak windows, or crossed unsupported audio.");
        // 150 BPM, 100 Hz envelope, eight-hop offset. The old phase
        // search never tested this tempo because it was above 120 BPM.
        var energy = Enumerable.Range(0, 1000).Select(i =>
            new RhythmEnergy(i % 40 == 8 ? 1f : 0f, i % 40 == 8 ? .5f : 0f, 0)).ToArray();
        var evidence = new RhythmBandEvidence(energy, 100, default);
        var refined = evidence.ChoosePhase(0, 40, 150, 0, 1000, false);
        Require(refined.Alternate && refined.SelectedScore > refined.OriginalScore &&
            refined.Phase >= 5 && refined.Phase <= 10,
            "An independently supported fast phase offset was not selected.");
        Require(!evidence.ChoosePhase(0, 40, 150, 0, 1000, true).Alternate,
            "A strong phase anchor was overwritten.");
        CheckBassOctave();
        CheckSpectralHypotheses();
        CheckGlobalHypothesisPath();
        CheckHarmonicFamily();
        CheckJointDecoder();
        CheckAuditAutocorrelation();
        Console.WriteLine("OK: evidence-gated octave correction, isolated window recovery and local phase refinement.");
    }

    private static void CheckAuditAutocorrelation()
    {
        var ac=new RhythmAuditAutocorrelation();
        var rng=new Random(20261128);
        foreach(int count in new[]{1,17,128,801,1601})
        {
            var samples=Enumerable.Range(0,count).Select(_=>rng.NextDouble()*2-1).ToArray();
            var actual=new double[451];
            ac.Accumulate(samples,actual,.37,default);
            for(int lag=0;lag<actual.Length;lag++)
            {
                double expected=0;
                for(int i=0;i+lag<count;i++)expected+=samples[i]*samples[i+lag]*.37;
                Require(Math.Abs(expected-actual[lag])<=1e-8*Math.Max(1,Math.Abs(expected)),
                    "FFT autocorrelation differs from the linear direct reference.");
            }
        }
    }

    private static void CheckRoleSelector(RhythmAuditResult result)
    {
        var role=result.RoleSelection;
        Require(role.Events.Length>0&&role.Decisions.Length>0&&
            role.Events.Zip(role.Events.Skip(1)).All(p=>p.Second.Frame-p.First.Frame>=
                RhythmEventClassifier.RefractoryHops*(long)RhythmAuditFlux.HopFrames),
            "Role extraction lost events or refractory spacing.");
        Require(role.Beats.Zip(role.Beats.Skip(1)).All(p=>p.First.Beat.Frame<p.Second.Beat.Frame),
            "Role selector duplicated or reversed beats.");
        var aligned=Enumerable.Range(0,16).Select(i=>new RhythmAuditBeat(i*24000L,i,true)).ToArray();
        var shifted=aligned.Select(b=>b with { Frame=b.Frame+12000 }).ToArray();
        var events=aligned.Select((b,i)=>new RhythmRoleEvent(b.Frame,1,1,0,
            i%2==0?1:0,i%2==0?0:1,0,i%2==0?1:0,i%2==0?0:1,0,0)).ToArray();
        var good=RhythmMetricSelector.Evaluate(aligned,events);
        var bad=RhythmMetricSelector.Evaluate(shifted,events);
        Require(good.Score>bad.Score+RhythmMetricSelector.MinimumGain&&good.Alternation>.9,
            "Role score missed aligned alternating attacks.");
        // A clean full-track alternative can win with repeated evidence.
        // No mid-track phase jump is required in this fixture.
        var clean=Enumerable.Range(0,48).Select(i=>new RhythmAuditBeat(i*24000L,i,true)).ToArray();
        var cleanEvents=clean.Select((b,i)=>new RhythmRoleEvent(b.Frame,1,1,0,
            i%2==0?1:0,i%2==0?0:1,0,i%2==0?1:0,i%2==0?0:1,0,0)).ToArray();
        long duration=48*24000L;
        var states=Enumerable.Range(0,12).Select(i=>new RhythmAuditState(i*96000L,120,0,1,1,0)).ToArray();
        var wrong=new RhythmAuditPath(states,clean.Select(b=>b with {Frame=b.Frame+12000}).ToArray());
        var correct=new RhythmMetricPath(1,1,1,2,1,1,new(states,clean));
        var selected=RhythmMetricSelector.Select(cleanEvents,wrong,[correct],duration,default);
        Require(selected.Decisions.All(d=>d.SelectedPathId==0)&&
            selected.Beats.Select(b=>b.Beat.Frame).SequenceEqual(clean.Select(b=>b.Frame)),
            "Repeated clean role evidence could not select an independent full-track phase.");
        var none=RhythmMetricSelector.Select([],result.Baseline,result.MetricPaths,
            result.Baseline.States[^1].Frame+RhythmJointDecoder.Stride*(long)RhythmJointDecoder.Hop,default);
        Require(none.Decisions.All(d=>d.SelectedPathId==-1&&d.Ambiguous)&&
            none.Beats.Select(b=>b.Beat.Frame).SequenceEqual(result.Baseline.Beats.Select(b=>b.Frame)),
            "Selector changed baseline without evidence.");
    }

    private static void CheckSharedRhythmFft()
    {
        foreach(int size in new[]{512,2048,4096})
        {
            var plan=new RhythmFftPlan(size);
            var original=Enumerable.Range(0,size).Select(i=>new System.Numerics.Complex(
                Math.Sin(i*.031),Math.Cos(i*.071))).ToArray();
            var old=new System.Numerics.Complex[size];
            var actual=new System.Numerics.Complex[size];
            for(int i=0;i<size;i++)
            {
                int x=i,r=0;
                for(int bit=1;bit<size;bit<<=1){r=(r<<1)|(x&1);x>>=1;}
                old[r]=original[i];actual[plan.Reverse(i)]=original[i];
            }
            var twiddle=Enumerable.Range(0,size/2)
                .Select(i=>System.Numerics.Complex.FromPolarCoordinates(1,-Math.Tau*i/size)).ToArray();
            for(int length=2;length<=size;length<<=1)
            {
                int half=length/2,step=size/length;
                for(int start=0;start<size;start+=length)
                    for(int j=0;j<half;j++)
                    {
                        var even=old[start+j];var odd=old[start+j+half]*twiddle[j*step];
                        old[start+j]=even+odd;old[start+j+half]=even-odd;
                    }
            }
            plan.Transform(actual);
            Require(actual.SequenceEqual(old),"Shared FFT changed reference arithmetic.");
        }
    }

    private static void CheckCrossfitTemplates()
    {
        var beats=Enumerable.Range(0,96).Select(i=>i*24000L).ToArray();
        var events=beats.Select((frame,i)=>new RhythmRoleEvent(frame,1,i%4==3?1:0,0,
            i%4==0?1:0,i%4==1?1:0,i%4==2?1:0,0,0,0,0)).ToArray();
        var model=RhythmTemplateAnalyzer.CheckFixture(events,beats,default);
        Require(model.Prototypes.Length==8&&model.Evidence.Length==2&&
            model.Evidence.All(e=>e.Coverage>.9&&e.FourGain>.2&&e.FourGain>e.ThreeGain&&e.FourGain>e.TwoGain),
            "Cross-fit model did not recover repeated four-pulse acoustic structure.");
        Require(model.RankedBeats.SequenceEqual(beats)&&model.GuardedBeats.SequenceEqual(beats),
            "Template ranking changed coordinates of the selected grid.");
        Require(model.Evidence.All(e=>e.ConditionalFourGain>.2)&&
            model.ConditionalRankedBeats.SequenceEqual(beats)&&model.ConditionalGuardedBeats.SequenceEqual(beats),
            "Conditional recurrence lost the known four-pulse fixture.");
        Require(RhythmTemplateAnalyzer.ConditionalGain(0,0)==0&&
            RhythmTemplateAnalyzer.ConditionalGain(5,20)==0&&
            RhythmTemplateAnalyzer.ConditionalGain(20,20)>0&&RhythmTemplateAnalyzer.ConditionalGain(0,20)<0,
            "Conditional evidence did not respect uniform, unseen or concentrated counts.");
        Require(model.SoftFolds.Length==2&&model.SoftFolds.All(f=>f.Temperature>0&&
            double.IsFinite(f.Temperature)&&f.MeanTrainingEntropy>=0&&f.MeanTestEntropy>=0)&&
            model.Evidence.All(e=>e.SoftFourGain>.2)&&
            model.SoftRankedBeats.SequenceEqual(beats)&&model.SoftGuardedBeats.SequenceEqual(beats),
            "Soft recurrence lost the four-pulse fixture.");
        double[] equal=[0,0,0,0],separated=[0,1,2,3];
        RhythmTemplateAnalyzer.NormalizeMembership(equal,1e-10);
        RhythmTemplateAnalyzer.NormalizeMembership(separated,1e-10);
        Require(equal.All(w=>w==.25)&&separated[0]==1&&
            separated.Skip(1).All(w=>w==0)&&Math.Abs(separated.Sum()-1)<1e-12,
            "Soft membership failed degenerate or separated centroids.");
        Require(model.TransitionEvidence.Length==2&&model.TransitionEvidence.All(e=>
            double.IsFinite(e.Gain)&&double.IsFinite(e.Increment)&&e.TrainingPairs>0&&
            e.TestPairs>0&&e.TestPairs<e.TestEvents),
            "Transition diagnostics lost valid adjacent events.");
        Require(model.SoftBlockComparisons.Length>0&&model.SoftBlockComparisons.All(e=>
            e.CommonEvents>=8&&e.Candidate==e.Reference&&e.Difference==0),
            "Identical grids have nonzero paired block evidence.");
        Require(RhythmTemplateAnalyzer.CanUsePrevious(0,24000)&&
            !RhythmTemplateAnalyzer.CanUsePrevious(0,24001)&&
            !RhythmTemplateAnalyzer.CanUsePrevious(383999,384000)&&
            !RhythmTemplateAnalyzer.CanUsePrevious(100,100),
            "Transition context crossed a fold block, long pause or duplicate frame.");
        var repeated=RhythmTemplateAnalyzer.CheckFixture(events,beats,default);
        Require(repeated.Prototypes.SequenceEqual(model.Prototypes)&&repeated.Evidence.SequenceEqual(model.Evidence)&&
            repeated.TransitionEvidence.SequenceEqual(model.TransitionEvidence)&&
            repeated.SoftBlockComparisons.SequenceEqual(model.SoftBlockComparisons),
            "Cross-fit clustering is not deterministic.");
        Require(RhythmTemplateAnalyzer.CheckFixture(events.Take(8).ToArray(),beats,default).Evidence.Length==0,
            "Insufficient events produced a template score.");
        var constant=events.Select(e=>e with {Low=1,Middle=0,High=0,Sharpness=0}).ToArray();
        var uniform=RhythmTemplateAnalyzer.CheckFixture(constant,beats,default);
        Require(uniform.Evidence.All(e=>e.FourGain<.1&&e.ConditionalFourGain<.1&&e.SoftFourGain<.1),
            "Uniform timbre incorrectly supplied evidence of a four-pulse accent pattern.");
    }

    private static void CheckJointDecoder()
    {
        const int hop=RhythmAuditFlux.HopFrames;
        var input=Enumerable.Range(0,1200).Select(i=>
        {
            float bass=i%50==25?1:0,high=i%50==0?1:0;
            return new RhythmSpectrumFrame((long)i*hop,bass,bass,high,high,high);
        }).ToArray();
        long total=input.Length*(long)hop;
        var result=RhythmJointDecoder.Analyze(input,total,default);
        Require(result.Baseline.Beats.Length>0&&result.Joint.Beats.Length>0,
            "Joint decoder lost periodic input.");
        var hybrid = result.HybridTempogram;
        Require(hybrid.States.Length == result.Baseline.States.Length &&
            hybrid.States.All(state => state.Bpm is >= 118 and <= 122 && double.IsFinite(state.TempoEvidence)) &&
            hybrid.Beats.All(beat => beat.Frame >= 0 && beat.Frame < total) &&
            hybrid.Beats.Count(beat => Math.Abs(beat.Frame % 24000 - 12000) < 2400) >= 20,
            "Hybrid tempogram lost the periodic low-band phase or native clock.");
        Require(result.MetricPaths.Length>=2 &&
            result.MetricPaths.Where(p=>p.Numerator==1&&p.Denominator==1).Count()==2 &&
            !result.MetricBudgetReached,"Independent phase lanes disappeared.");
        var sameRate=result.MetricPaths.Where(p=>p.Numerator==1&&p.Denominator==1).ToArray();
        Require(sameRate.All(p=>p.Path.Beats.Length>0) &&
            sameRate[0].Path.States.Zip(sameRate[1].Path.States).All(pair=>
            {
                double distance=Math.Abs(pair.First.PhaseCycles-pair.Second.PhaseCycles);
                return Math.Min(distance,1-distance)>=.25;
            }),"Opposing phase lanes collapsed into the same trajectory.");
        foreach(var family in result.MetricPaths.GroupBy(p=>(p.Numerator,p.Denominator)))
        {
            Require(family.Count()==family.First().LaneCount &&
                family.Select(p=>p.Lane).Distinct().Count()==family.Count(),
                "Incomplete metrical family.");
            foreach(var lane in family)
                Require(lane.Path.States.Zip(result.Baseline.States).All(pair=>
                    Math.Abs(pair.First.Bpm-pair.Second.Bpm*lane.Numerator/lane.Denominator)<1e-8),
                    "Metric ratio was clamped or changed.");
        }
        Require(result.RefinedMetricPaths.Length==result.MetricPaths.Length &&
            !result.RefinedMetricBudgetReached,"Refined metric families lost lanes.");
        foreach(var lane in result.RefinedMetricPaths)
            Require(lane.Path.States.Zip(result.Baseline.States).All(pair=>
            {
                double nominal=pair.Second.Bpm*lane.Numerator/lane.Denominator;
                return Math.Abs(pair.First.Bpm-nominal)<=Math.Max(1,.03*nominal)+1e-8;
            }),"Refined path escaped its nominal family.");
        foreach (var path in new[]{result.Baseline,result.Joint,result.Alternative}
            .Concat(result.MetricPaths.Select(p=>p.Path))
            .Concat(result.RefinedMetricPaths.Select(p=>p.Path)))
        {
            Require(path.Beats.All(b=>b.Frame>=0&&b.Frame<total)&&path.Beats.Zip(path.Beats.Skip(1))
                .All(pair=>pair.First.Frame<pair.Second.Frame&&pair.First.Index<pair.Second.Index),
                "Joint decoder published invalid coordinates.");
            Require(path.States.All(s=>double.IsFinite(s.Bpm)&&s.Bpm>=40&&s.Bpm<=240&&
                s.PhaseCycles>=0&&s.PhaseCycles<1),"Invalid joint state.");
        }
        CheckRoleSelector(result);
        CheckCrossfitTemplates();
        CheckSharedRhythmFft();
        // The envelope is a distinct observation: its attack is deliberately
        // displaced from the low spectral flux in this fixture.
        var envelope=Enumerable.Range(0,input.Length).Select(i=>
        {
            float level=(float)Math.Exp(-(i%50)/6d);
            return new RhythmEnergy(level,level*.5f,level*.2f);
        }).ToArray();
        var withEnvelope=RhythmJointDecoder.Analyze(input,total,default,envelope,hop);
        Require(withEnvelope.Baseline.Beats.SequenceEqual(result.Baseline.Beats)&&
            withEnvelope.Joint.Beats.SequenceEqual(result.Joint.Beats),
            "Envelope phase experiment changed existing paths.");
        Require(withEnvelope.EnvelopePaths.Length==4&&withEnvelope.EnvelopePaths.All(p=>
            p.Path.Beats.Length>0&&p.Path.Beats.All(b=>b.Frame>=0&&b.Frame<total)&&
            p.Path.Beats.Zip(p.Path.Beats.Skip(1)).All(q=>q.First.Frame<q.Second.Frame&&q.First.Index<q.Second.Index)&&
            (p.Name=="envelope-refined-decay"
                ?p.Path.States.Zip(result.Baseline.States).All(q=>
                    Math.Abs(q.First.Bpm-q.Second.Bpm)<=Math.Max(1,.03*q.Second.Bpm)+1e-8)
                :p.Path.States.Select(s=>s.Bpm).SequenceEqual(result.Baseline.States.Select(s=>s.Bpm)))),
            "Envelope paths lost valid coordinates or changed nominal tempo.");
        Require(withEnvelope.EnvelopePaths[0].Path.Beats.Any(b=>b.Frame%24000<2400||b.Frame%24000>21600),
            "Envelope rise path did not find the independent attack phase.");
        Require(RhythmJointDecoder.Analyze(input,total,default,envelope,hop*2).EnvelopePaths.Length==0,
            "Envelope experiment accepted a mismatched clock.");
        var constant=Enumerable.Repeat(new RhythmEnergy(1,1,1),input.Length).ToArray();
        Require(RhythmJointDecoder.Analyze(input,total,default,constant,hop).EnvelopePaths.Length==0,
            "Constant envelope generated unsupported phase hypotheses.");
        var regularPhase=Enumerable.Range(0,16).Select(i=>24000L*i).ToArray();
        var opposingPhase=regularPhase.Select(f=>f+12000).ToArray();
        var agreement=RhythmPhaseAgreement.Compare(0,384000,"a","b",regularPhase,regularPhase);
        var conflict=RhythmPhaseAgreement.Compare(0,384000,"a","b",regularPhase,opposingPhase);
        Require(agreement.Status=="near_phase"&&agreement.Matches70==16&&agreement.Concentration>.99&&
            conflict.Status=="phase_conflict"&&conflict.Matches70==0&&
            Math.Abs(Math.Abs(conflict.OffsetCycles)-.5)<1e-8&&conflict.Concentration>.99,
            "Phase agreement lost aligned or opposing grid distinction.");
        Require(RhythmPhaseAgreement.Compare(0,384000,"a","b",regularPhase,
            regularPhase.Where((_,i)=>i%2==0).ToArray()).Status=="tempo_mismatch",
            "Phase agreement confused a half-rate grid with a phase shift.");
        Require(RhythmPhaseAgreement.Compare(0,384000,"a","b",regularPhase,[]).Status=="insufficient",
            "Missing reference became phase confidence.");
        var paused=Enumerable.Range(0,2400).Select(i=>
        {
            float hit=i>=800&&i<1600?0:i%50==25?1:0;
            return new RhythmSpectrumFrame((long)i*hop,hit,hit,hit,hit,hit);
        }).ToArray();
        var pausedResult=RhythmJointDecoder.Analyze(paused,paused.Length*(long)hop,default);
        Require(pausedResult.Joint.States.Any(s=>s.InactiveBoundaryFrame>=0)&&
            pausedResult.Joint.Beats.Any(b=>b.ActivityRegion==0)&&
            pausedResult.Joint.Beats.Any(b=>b.ActivityRegion==1)&&
            !pausedResult.Joint.Beats.Any(b=>b.Frame>=850L*hop&&b.Frame<1550L*hop),
            "Long activity gap was not distinguished from predicted beats.");
        var silence=input.Select(f=>f with {Sub=0,Bass=0,LowMid=0,Mid=0,High=0}).ToArray();
        var silentResult=RhythmJointDecoder.Analyze(silence,total,default);
        Require(silentResult.HybridTempogram.Beats.Length == 0, "Silence acquired a hybrid grid.");
        Require(silentResult.Joint.Beats.Length==0&&silentResult.MetricPaths.Length==0&&
            silentResult.RefinedMetricPaths.Length==0&&silentResult.RoleSelection.Events.Length==0,
            "Silence generated a joint or metrical grid.");
        var stereo=new float[8192*2];
        for(int i=0;i<8192;i++)
        {
            float v=.4f*MathF.Sin(MathF.Tau*100*i/PlaybackFormat.SampleRate);
            stereo[2*i]=v;stereo[2*i+1]=-v;
        }
        var split=new RhythmAuditFlux();split.Add(stereo.AsSpan(0,2000),default);
        split.Add(stereo.AsSpan(2000),default);split.Finish(default);
        var whole=new RhythmAuditFlux();whole.Add(stereo,default);whole.Finish(default);
        Require(split.Frames.SequenceEqual(whole.Frames)&&split.Frames.Count==18&&
            split.Frames[0].Frame==0&&split.Frames.Skip(1).Any(f=>f.Sub>0||f.Bass>0),
            "Audit flux chunking, clock or antiphase energy regressed.");
        var metric=RhythmReportReplay.SeriesMetrics([0,48000,240000,480000],
            [0,48000,480000],3360);
        Require(metric.F1==1,"Series metrics counted beats outside annotated runs.");
        var regular=Enumerable.Range(0,40).Select(i=>i*24000L).ToArray();
        Require(RhythmReportReplay.PhaseNull(regular,regular,3360)<.5,
            "Phase-null control did not distinguish a regular exact grid.");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        bool threw=false;
        try { RhythmJointDecoder.Analyze(input,total,cancelled.Token); }
        catch(OperationCanceledException){threw=true;}
        Require(threw,"Joint decoder ignored cancellation.");
    }

    private static void CheckHarmonicFamily()
    {
        const long duration = 576000;
        var attacks = Enumerable.Range(0,24).Select(i => (Index: i*25, Weight: 1d)).ToArray();
        TrackRhythmHypothesis H(double bpm, long step) => new(0,duration,bpm,120,.7f,.8f,.9f,.8f,.7f,
            Enumerable.Range(0,(int)(duration/step)).Select(i => i*step).ToArray());
        var correct = RhythmHarmonicFamily.Evaluate(H(120,24000),attacks,attacks,.8f);
        var slow = RhythmHarmonicFamily.Evaluate(H(60,48000),attacks,attacks,.8f);
        var fast = RhythmHarmonicFamily.Evaluate(H(240,12000),attacks,attacks,.8f);
        Require(correct.BassAttackRecall == 1 && correct.BassBeatPrecision == 1 &&
            slow.BassAttackRecall < correct.BassAttackRecall &&
            fast.BassBeatPrecision < correct.BassBeatPrecision &&
            correct.FamilyScore > Math.Max(slow.FamilyScore,fast.FamilyScore),
            "Harmonic scoring favored missing or unsupported subdivisions.");
        var legacy = new[] { correct with { IsPreferred = true }, slow, fast };
        var path = RhythmHypothesisPath.SelectFamily(legacy,default);
        Require(path.Count(h => h.IsFamilyPreferred) == 1 && path[0].IsFamilyPreferred &&
            path[0].IsPreferred && !path[1].IsPreferred && !path[2].IsPreferred,
            "Family path replaced the previous spectral path.");
    }

    private static void CheckGlobalHypothesisPath()
    {
        const long duration = 576000;
        TrackRhythmHypothesis H(int block, double bpm, float score, bool weak = false, long offset = 0)
        {
            long start = block * duration;
            long period = (long)Math.Round(60 * PlaybackFormat.SampleRate / bpm);
            var beats = new List<long>();
            for (long frame = start + offset; frame < start + duration; frame += period) beats.Add(frame);
            return new(start,start+duration,bpm,120,.8f,.8f,.9f,.8f,score,beats.ToArray())
            { AttackCoverage = .8f, IsRecoveryCandidate = weak };
        }
        var path = RhythmHypothesisPath.Select([H(0,120,.8f),H(0,60,.6f),
            H(1,120,.6f),H(1,60,.65f),H(1,120,.65f,offset:12000),
            H(2,120,.8f),H(2,60,.6f)],default);
        Require(path.Count(h=>h.IsPreferred)==3 && path.Where(h=>h.IsPreferred)
            .All(h=>h.Bpm==120 && h.CandidateFrames[0]==h.StartFrame),
            "Global path preferred an isolated octave or phase jump.");
        path = RhythmHypothesisPath.Select([H(0,120,.8f),H(1,120,.5f,true),H(2,120,.8f)],default);
        Require(path.All(h=>h.IsPreferred),"A supported isolated weak block was not recovered.");
        path = RhythmHypothesisPath.Select([H(0,120,.8f),H(1,120,.5f,true),
            H(2,120,.5f,true),H(3,120,.8f)],default);
        Require(path.Count(h=>h.IsPreferred)==2,"Weak blocks bootstrapped each other.");
        path = RhythmHypothesisPath.Select([H(0,120,.8f),H(2,60,.8f)],default);
        Require(path.Count(h=>h.IsPreferred)==2,"A missing block invalidated independent regions.");
    }

    private static void CheckSpectralHypotheses()
    {
        const int hop = RhythmSpectralFlux.Hop;
        // 150 BPM fast pulse with alternating accents, 75 BPM reference.
        var flux = Enumerable.Range(0, 600).Select(i =>
        {
            float hit = i % 20 == 4 ? (i % 40 == 4 ? 1 : .8f) : 0;
            return new RhythmSpectrumFrame(1024 + (long)i * hop, hit, hit, hit, hit, hit);
        }).ToArray();
        long total = 1024 + 600L * hop;
        var reference = new TrackBeatGrid(75, 0, .5f, []);
        var hypotheses = RhythmHypothesisAnalyzer.Analyze(flux, reference, total, default);
        Require(hypotheses.Any(h => Math.Abs(h.Bpm - 150) < 3) &&
            hypotheses.Any(h => Math.Abs(h.Bpm - 75) < 3) &&
            hypotheses.Count(h => h.IsPreferred) == 1 &&
            RhythmHypothesisAnalyzer.IsValid(hypotheses, total),
            "Independent spectral candidates lost the tempo family or their frame bounds.");
        Require(RhythmHypothesisAnalyzer.Analyze(flux.Select(f => f with
            { Sub = 0, Bass = 0, LowMid = 0, Mid = 0, High = 0 }).ToArray(),
            reference, total, default).Length == 0, "Silence generated spectral hypotheses.");
        var stream = new RhythmSpectralFlux();
        var pcm = new float[8192 * 2];
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            float value = .5f * MathF.Sin(MathF.Tau * 100 * i / PlaybackFormat.SampleRate);
            pcm[2*i] = value; pcm[2*i+1] = -value;
        }
        stream.Add(pcm.AsSpan(0, 2000), default);
        stream.Add(pcm.AsSpan(2000), default);
        Require(stream.Frames.Count == 7 && stream.Frames[0].Frame == 1024 &&
            stream.Frames.Zip(stream.Frames.Skip(1)).All(p => p.Second.Frame - p.First.Frame == hop) &&
            stream.Frames.Skip(1).Any(f => f.Bass > 0),
            "Stereo spectral stream lost anti-phase energy or hop alignment.");
    }

    private static void CheckBassOctave()
    {
        const int rate = 100, length = 3200, windows = 12, stride = 200, radius = 450;
        int Index(double bpm) => (int)((bpm - 40) * 2);
        RhythmBandEvidence Build(double alternateBass, bool subdivision = false, bool steadyBass = false)
        {
            var energy = new RhythmEnergy[length];
            if (steadyBass) Array.Fill(energy, new RhythmEnergy(.4f, 0, 0));
            for (int beat = 0; ; beat++)
            {
                int i = (int)Math.Round(11 + beat * rate * 60d / 172 + Math.Sin(beat * 3) * 2);
                if (i >= length) break;
                float bass = steadyBass ? .4f : beat % 2 == 0 ? 1f : (float)alternateBass;
                energy[i] = new(bass, .25f, .8f);
                if (subdivision && i + 17 < length) energy[i + 17] = new(1, .25f, .8f);
            }
            return new RhythmBandEvidence(energy, rate, default);
        }
        var strong = Build(.64);
        Require(strong.MeasureBassPulse(600, 1500, 172, default) > 0,
            "Alternating independent bass attacks at 172 BPM were missed.");
        Require(Build(0).MeasureBassPulse(600, 1500, 172, default) == 0,
            "High-band subdivisions doubled a genuine 86 BPM bass pulse.");
        Require(Build(.04).MeasureBassPulse(600, 1500, 172, default) == 0,
            "Weak offbeat bass subdivisions doubled the tempo.");
        Require(Build(1, subdivision: true).MeasureBassPulse(600, 1500, 172, default) == 0,
            "Dense off-grid bass attacks were treated as a confirmed fast beat.");
        Require(Build(0, steadyBass: true).MeasureBassPulse(600, 1500, 172, default) == 0,
            "Sustained bass was treated as repeated attacks.");

        var raw = new float[windows,401];
        var periodicity = new float[windows,401];
        var phase = new float[windows,401];
        for (int w = 0; w < windows; w++)
        {
            raw[w,Index(86)] = .40f; periodicity[w,Index(86)] = .25f;
            raw[w,Index(172)] = .35f; periodicity[w,Index(172)] = .20f;
            phase[w,Index(172)] = .30f;
        }
        var path = Enumerable.Repeat(Index(86), windows).ToArray();
        path[8] = Index(128);
        RhythmBassOctave.Resolve(path, raw, periodicity, phase, strong,
            stride, radius, 40, .5, default);
        Require(path[4] == Index(172) && path[8] == Index(128),
            "Bass parity did not resolve the alias or changed an unrelated tempo.");
        Array.Fill(path, Index(86));
        RhythmBassOctave.Resolve(path, raw, periodicity, phase, Build(0),
            stride, radius, 40, .5, default);
        Require(path.All(c => c == Index(86)), "A genuine slow tempo was doubled.");
        Array.Fill(path, Index(86));
        for (int w = 0; w < windows; w++)
            if (w != 5) periodicity[w,Index(172)] = 0;
        RhythmBassOctave.Resolve(path, raw, periodicity, phase, strong,
            stride, radius, 40, .5, default);
        Require(path.All(c => c == Index(86)), "An isolated fast candidate changed the tempo.");
    }
}
