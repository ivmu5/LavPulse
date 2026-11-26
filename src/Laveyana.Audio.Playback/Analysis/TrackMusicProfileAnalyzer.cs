namespace Laveyana.Audio.Playback;

public static class TrackMusicProfileAnalyzer
{
    public static TrackMusicProfile Derive(TrackWaveform waveform, TrackMusicProfile? existing = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        token.ThrowIfCancellationRequested();
        if(existing is null||!existing.IsValid(waveform.TotalFrames)) existing=new();
        if (existing.HasCurrentDerived) return existing;
        var energy = existing.EnergyVersion == TrackMusicProfile.CurrentEnergyVersion
            ? existing.Energy : BuildEnergy(waveform, token);
        return existing with
        {
            MeterVersion = TrackMusicProfile.CurrentMeterVersion,
            EnergyVersion = TrackMusicProfile.CurrentEnergyVersion,
            StructureVersion = TrackMusicProfile.CurrentStructureVersion,
            Energy = energy,
            Meter = existing.MeterVersion == TrackMusicProfile.CurrentMeterVersion
                ? existing.Meter : BuildMeter(waveform, token),
            Structure = existing.StructureVersion == TrackMusicProfile.CurrentStructureVersion &&
                existing.EnergyVersion == TrackMusicProfile.CurrentEnergyVersion
                ? existing.Structure : BuildStructure(energy)
        };
    }

    private static TrackEnergyPoint[] BuildEnergy(TrackWaveform w, CancellationToken token)
    {
        if (w.Points.Count == 0 || w.FramesPerPoint <= 0 || w.TotalFrames <= 0) return [];
        int stride = Math.Max(1, (int)Math.Ceiling(4d * PlaybackFormat.SampleRate / w.FramesPerPoint));
        var result = new List<TrackEnergyPoint>();
        var attacks = w.Rhythm?.TransientFrames ?? [];
        int hit = 0;
        for (int i = 0; i < w.Points.Count; i += stride)
        {
            token.ThrowIfCancellationRequested();
            int end = Math.Min(w.Points.Count, i + stride);
            long startFrame = (long)i * w.FramesPerPoint;
            long endFrame = Math.Min(w.TotalFrames, (long)end * w.FramesPerPoint);
            if (endFrame <= startFrame) break;
            double power = 0, bass = 0, bands = 0;
            long representedFrames=0;
            for (int j = i; j < end; j++)
            {
                long frames=Math.Min(w.FramesPerPoint,endFrame-(long)j*w.FramesPerPoint);
                if(frames<=0)break;
                var p = w.Points[j];
                power += (.55*p.Peak*p.Peak + .30*p.Low*p.Low + .15*p.Mid*p.Mid)*frames;
                bass += (double)p.Low*frames;
                bands += ((double)p.Low+p.Mid+p.High)*frames;
                representedFrames+=frames;
            }
            while (hit < attacks.Length && attacks[hit] < startFrame) hit++;
            int first = hit;
            while (hit < attacks.Length && attacks[hit] < endFrame) hit++;
            float density = (float)((hit-first) * (double)PlaybackFormat.SampleRate / (endFrame-startFrame));
            double amplitude = Math.Sqrt(Math.Max(0, power/Math.Max(1,representedFrames)));
            float raw = (float)Math.Clamp(amplitude * (.8 + .2*Math.Min(1,density/5)), 0, 1);
            result.Add(new(startFrame, endFrame, raw, (float)Math.Clamp(bass/Math.Max(1e-12,bands),0,1), density));
        }
        var sorted = result.Select(x=>x.Energy).Order().ToArray();
        if (sorted.Length == 0) return [];
        float reference = Math.Max(.025f, sorted[(int)((sorted.Length-1)*.9)]);
        return result.Select(e=>e with {Energy=Math.Clamp(e.Energy/reference,0,1)}).ToArray();
    }

    private static TrackMusicSection[] BuildStructure(TrackEnergyPoint[] energy)
    {
        var sections = new List<TrackMusicSection>();
        for(int i=0;i<energy.Length;i++)
        {
            var e=energy[i];
            float previous=i>0?energy[i-1].Energy:e.Energy;
            float next=i+1<energy.Length?energy[i+1].Energy:e.Energy;
            var kind=e.Energy<.035f?MusicSectionKind.Silence:
                i<Math.Max(1,energy.Length/8)&&e.Energy<.55f?MusicSectionKind.Intro:
                i>=energy.Length-Math.Max(1,energy.Length/8)&&e.Energy<.55f?MusicSectionKind.Outro:
                next-previous>.28f&&e.Energy>previous?MusicSectionKind.BuildUp:
                e.Energy<.38f?MusicSectionKind.Breakdown:
                e.Energy>.76f?MusicSectionKind.HighEnergy:MusicSectionKind.Steady;
            if(sections.Count>0&&sections[^1].Kind==kind&&sections[^1].EndFrame==e.StartFrame)
            {
                var last=sections[^1];
                double frames=e.EndFrame-e.StartFrame;
                sections[^1]=last with {EndFrame=e.EndFrame,Energy=(float)((last.Energy*(last.EndFrame-last.StartFrame)+e.Energy*frames)/(e.EndFrame-last.StartFrame))};
            }
            else sections.Add(new(e.StartFrame,e.EndFrame,kind,e.Energy));
        }
        return sections.ToArray();
    }

    private static TrackMeterSection[] BuildMeter(TrackWaveform w, CancellationToken token)
    {
        if (w.Rhythm is not { HasBeatGrid:true } rhythm || w.Points.Count == 0 ||
            w.FramesPerPoint > PlaybackFormat.SampleRate / 10 || w.FramesPerPoint <= 0) return [];
        var result=new List<TrackMeterSection>();
        int cursor=0;
        Span<double> sums=stackalloc double[4];
        Span<int> counts=stackalloc int[4], positives=stackalloc int[4];
        foreach(var section in rhythm.Sections)
        {
            token.ThrowIfCancellationRequested();
            while(cursor<rhythm.BeatFrames.Length&&rhythm.BeatFrames[cursor]<section.StartFrame)cursor++;
            int first=cursor;
            while(cursor<rhythm.BeatFrames.Length&&rhythm.BeatFrames[cursor]<section.EndFrame)cursor++;
            int count=cursor-first;
            if(count<16)continue;
            // A missing beat can rotate every later downbeat. Reject sections
            // containing such discontinuities rather than assuming a bar phase.
            double period = 60d * PlaybackFormat.SampleRate / section.Bpm;
            bool discontinuous = false;
            for (int i=first+1; i<cursor; i++)
                if (Math.Abs((rhythm.BeatFrames[i]-rhythm.BeatFrames[i-1])/period-1) > .35)
                { discontinuous=true; break; }
            if(discontinuous)continue;
            var strength=new double[count];
            for(int i=0;i<count;i++)
            {
                int bin=(int)Math.Min(w.Points.Count-1,rhythm.BeatFrames[first+i]/w.FramesPerPoint);
                double peak=0;
                for(int j=Math.Max(0,bin-1);j<=Math.Min(w.Points.Count-1,bin+1);j++)
                {
                    var p=w.Points[j];
                    peak=Math.Max(peak,.7*p.Low+.3*p.Mid);
                }
                strength[i]=peak;
            }
            double totalStrength=strength.Sum();
            double mean=totalStrength/count;
            if(mean<.015)continue;
            // Compare repeated accents, requiring multiple complete bars.
            (int Meter,int Phase,double Score) best=(0,0,0),second=(0,0,0);
            for(int meter=3;meter<=4;meter++)
            {
                if(count<meter*5)continue;
                sums.Clear(); counts.Clear(); positives.Clear();
                for(int j=0;j<count;j++)
                {
                    int phase=j%meter;
                    sums[phase]+=strength[j]; counts[phase]++;
                    if(strength[j]>mean*1.1)positives[phase]++;
                }
                for(int phase=0;phase<meter;phase++)
                {
                    double strong=sums[phase],weak=totalStrength-strong;
                    int ns=counts[phase],nw=count-ns,positive=positives[phase];
                    double score=(strong/ns-weak/nw)/Math.Max(.015,mean)*(positive/(double)ns);
                    if(score>best.Score){second=best;best=(meter,phase,score);}
                    else if(score>second.Score)second=(meter,phase,score);
                }
            }
            if(best.Score<.22||best.Score-second.Score<.10)continue;
            long[] downbeats=Enumerable.Range(0,count).Where(i=>i%best.Meter==best.Phase)
                .Select(i=>rhythm.BeatFrames[first+i]).ToArray();
            result.Add(new(section.StartFrame,section.EndFrame,best.Meter,
                (float)Math.Clamp((best.Score-second.Score)*.8,0,1),downbeats));
        }
        return result.ToArray();
    }
}
