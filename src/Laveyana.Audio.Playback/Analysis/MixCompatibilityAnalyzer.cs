namespace Laveyana.Audio.Playback;

/// <summary>Advisory mix score. Unknown features reduce evidence, not the score.</summary>
public sealed record MixCompatibility(float? Score, float Evidence, float? Tempo, float? Harmony, float? Energy);
public static class MixCompatibilityAnalyzer
{
    public static MixCompatibility Compare(TrackWaveform outgoing,long outFrame,TrackWaveform incoming,long inFrame)
    {
        if(outFrame<0||outFrame>=outgoing.TotalFrames||inFrame<0||inFrame>=incoming.TotalFrames)
            return new(null,0,null,null,null);
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
        var ea=outgoing.MusicProfile?.EnergyAt(outFrame);
        var eb=incoming.MusicProfile?.EnergyAt(inFrame);
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
