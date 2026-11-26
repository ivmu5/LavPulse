using System.Text.Json.Serialization;

namespace Laveyana.Audio.Playback;

public sealed record TrackKeySection(long StartFrame, long EndFrame, TrackMusicalKey? Key);
public sealed record TrackEnergyPoint(long StartFrame, long EndFrame, float Energy, float BassShare, float AttacksPerSecond);
public enum MusicSectionKind { Silence, Intro, Outro, BuildUp, Breakdown, HighEnergy, Steady }
public sealed record TrackMusicSection(long StartFrame, long EndFrame, MusicSectionKind Kind, float Energy);
/// <summary>Number of observed pulses per repeating accent cycle; not a notated time signature.</summary>
public sealed record TrackMeterSection(long StartFrame, long EndFrame, int PulsesPerBar, float Confidence, long[] Downbeats);

/// <summary>Version each result independently. Missing audio-derived features remain unknown on older caches.
/// Derived results can be rebuilt from saved waveform/rhythm without opening the audio.</summary>
public sealed record TrackMusicProfile
{
    public const int CurrentMeterVersion = 1, CurrentEnergyVersion = 1, CurrentStructureVersion = 1,
        CurrentLocalKeyVersion = 1;
    public int MeterVersion { get; init; }
    public int EnergyVersion { get; init; }
    public int StructureVersion { get; init; }
    public int LocalKeyVersion { get; init; }
    public int LoudnessVersion { get; init; }
    public TrackMeterSection[] Meter { get; init; } = [];
    public TrackEnergyPoint[] Energy { get; init; } = [];
    public TrackMusicSection[] Structure { get; init; } = [];
    public TrackKeySection[] LocalKeys { get; init; } = [];
    public TrackLoudness? Loudness { get; init; }

    [JsonIgnore]
    public bool HasCurrentDerived => MeterVersion == CurrentMeterVersion &&
        EnergyVersion == CurrentEnergyVersion && StructureVersion == CurrentStructureVersion;

    public TrackMusicalKey? KeyAt(long frame) =>
        TryGetLocalKey(frame, out var key) ? key : null;

    public bool TryGetLocalKey(long frame, out TrackMusicalKey? key)
    {
        int lo = 0, hi = LocalKeys.Length;
        while (lo < hi)
        {
            int mid = lo + (hi-lo)/2;
            if (LocalKeys[mid].EndFrame <= frame) lo = mid + 1;
            else hi = mid;
        }
        if (lo < LocalKeys.Length && LocalKeys[lo].StartFrame <= frame)
        {
            key = LocalKeys[lo].Key;
            return true;
        }
        key = null;
        return false;
    }

    public bool IsValid(long total)
    {
        bool Range(long start, long end) => start >= 0 && end > start && end <= total;
        bool Unit(float v) => float.IsFinite(v) && v >= 0 && v <= 1;
        return MeterVersion >= 0 && EnergyVersion >= 0 && StructureVersion >= 0 &&
            LocalKeyVersion >= 0 && LoudnessVersion >= 0 &&
            Meter is { Length: <= 4096 } && Energy is { Length: <= 8192 } &&
            Structure is { Length: <= 8192 } && LocalKeys is { Length: <= 8192 } &&
            Meter.All(m => m is not null && Range(m.StartFrame,m.EndFrame) && m.PulsesPerBar is 3 or 4 &&
                Unit(m.Confidence) && m.Downbeats is { Length: <= 32768 } &&
                m.Downbeats.All(f => f >= m.StartFrame && f < m.EndFrame)) &&
            Energy.All(e => e is not null && Range(e.StartFrame,e.EndFrame) && Unit(e.Energy) &&
                Unit(e.BassShare) && float.IsFinite(e.AttacksPerSecond) && e.AttacksPerSecond >= 0) &&
            Structure.All(s => s is not null && Range(s.StartFrame,s.EndFrame) && Unit(s.Energy) && Enum.IsDefined(s.Kind)) &&
            LocalKeys.All(k => k is not null && Range(k.StartFrame,k.EndFrame) && (k.Key is null || k.Key.IsValid)) &&
            Ordered(Meter.Select(m => (m.StartFrame,m.EndFrame))) &&
            Ordered(Energy.Select(e => (e.StartFrame,e.EndFrame))) &&
            Ordered(Structure.Select(s => (s.StartFrame,s.EndFrame))) &&
            Ordered(LocalKeys.Select(k => (k.StartFrame,k.EndFrame))) &&
            (Loudness is null || Loudness.IsValid(total));
    }

    private static bool Ordered(IEnumerable<(long Start,long End)> spans)
    {
        long previous = 0;
        foreach (var span in spans)
        {
            if (span.Start < previous) return false;
            previous = span.End;
        }
        return true;
    }
}

public static class TrackMusicProfileAnalyzer
{
    public static TrackMusicProfile Derive(TrackWaveform waveform, TrackMusicProfile? existing = null,
        CancellationToken token = default)
    {
        existing ??= new();
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
            Structure = BuildStructure(energy)
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
            for (int j = i; j < end; j++)
            {
                var p = w.Points[j];
                power += .55*p.Peak*p.Peak + .30*p.Low*p.Low + .15*p.Mid*p.Mid;
                bass += p.Low;
                bands += p.Low+p.Mid+p.High;
            }
            while (hit < attacks.Length && attacks[hit] < startFrame) hit++;
            int first = hit;
            while (hit < attacks.Length && attacks[hit] < endFrame) hit++;
            float density = (float)((hit-first) * (double)PlaybackFormat.SampleRate / (endFrame-startFrame));
            double amplitude = Math.Sqrt(Math.Max(0, power/(end-i)));
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
            if(sections.Count>0&&sections[^1].Kind==kind)
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
        foreach(var section in rhythm.Sections)
        {
            token.ThrowIfCancellationRequested();
            while(cursor<rhythm.BeatFrames.Length&&rhythm.BeatFrames[cursor]<section.StartFrame)cursor++;
            int first=cursor;
            while(cursor<rhythm.BeatFrames.Length&&rhythm.BeatFrames[cursor]<section.EndFrame)cursor++;
            int count=cursor-first;
            if(count<16)continue;
            var strength=new double[count];
            for(int i=0;i<count;i++)
            {
                int bin=(int)Math.Min(w.Points.Count-1,rhythm.BeatFrames[first+i]/w.FramesPerPoint);
                var p=w.Points[bin];
                strength[i]=.7*p.Low+.3*p.Mid;
            }
            double mean=strength.Average();
            if(mean<.015)continue;
            // Compare repeated accents, requiring multiple complete bars.
            (int Meter,int Phase,double Score) best=(0,0,0),second=(0,0,0);
            foreach(int meter in new[]{3,4})
            {
                if(count<meter*5)continue;
                for(int phase=0;phase<meter;phase++)
                {
                    double strong=0,weak=0;int ns=0,nw=0,positive=0;
                    for(int j=0;j<count;j++)
                    {
                        if(j%meter==phase){strong+=strength[j];ns++;if(strength[j]>mean*1.1)positive++;}
                        else{weak+=strength[j];nw++;}
                    }
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

/// <summary>Advisory mix score. Unknown features reduce evidence, not the score.</summary>
public sealed record MixCompatibility(float? Score, float Evidence, float? Tempo, float? Harmony, float? Energy);
public static class MixCompatibilityAnalyzer
{
    public static MixCompatibility Compare(TrackWaveform outgoing,long outFrame,TrackWaveform incoming,long inFrame)
    {
        double a=outgoing.Rhythm is {HasBeatGrid:true} ar?ar.BpmAt(outFrame):0;
        double b=incoming.Rhythm is {HasBeatGrid:true} br?br.BpmAt(inFrame):0;
        float? tempo=a>0&&b>0?(float)Math.Clamp(1-Math.Min(Math.Abs(Math.Log2(a/b)),
            Math.Min(Math.Abs(Math.Log2(2*a/b)),Math.Abs(Math.Log2(a/(2*b)))))*5,0,1):null;
        // Do not turn an explicitly uncertain local section into a confident
        // harmonic match by borrowing the global key.
        var ka=KeyForMix(outgoing,outFrame);
        var kb=KeyForMix(incoming,inFrame);
        float? harmony=null;
        if(ka is {IsValid:true,Confidence:>=.15f}&&kb is {IsValid:true,Confidence:>=.15f})
        {
            int na=int.Parse(ka.Camelot[..^1],System.Globalization.CultureInfo.InvariantCulture);
            int nb=int.Parse(kb.Camelot[..^1],System.Globalization.CultureInfo.InvariantCulture);
            int delta=Math.Abs(na-nb);
            harmony=na==nb?(ka.Mode==kb.Mode?1f:.9f):ka.Mode==kb.Mode&&(delta==1||delta==11)?.85f:.25f;
        }
        var ea=outgoing.MusicProfile?.Energy.FirstOrDefault(p=>p.StartFrame<=outFrame&&outFrame<p.EndFrame);
        var eb=incoming.MusicProfile?.Energy.FirstOrDefault(p=>p.StartFrame<=inFrame&&inFrame<p.EndFrame);
        // Energy is normalized per track: this is relative intensity, not equal loudness.
        float? energy=ea is not null&&eb is not null?1-Math.Abs(ea.Energy-eb.Energy):null;
        float sum=0,weight=0;
        void Add(float? score,float w){if(score is float s){sum+=s*w;weight+=w;}}
        Add(tempo,.45f);Add(harmony,.35f);Add(energy,.2f);
        return new(weight>0?sum/weight:null,weight,tempo,harmony,energy);
    }

    private static TrackMusicalKey? KeyForMix(TrackWaveform waveform,long frame)
    {
        if(waveform.MusicProfile is { } profile && profile.TryGetLocalKey(frame,out var key)) return key;
        return waveform.MusicalKey;
    }
}
